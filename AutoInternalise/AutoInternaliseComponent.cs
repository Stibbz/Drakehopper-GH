using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Grasshopper.Kernel.Undo;
using Grasshopper.Kernel.Undo.Actions;
using Rhino;

namespace AutoInternalise
{
  public class AutoInternaliseInfo : GH_AssemblyInfo
  {
    public override string Name => "AutoInternalise";
    public override string Description => "Internalizes Rhino-referenced (and optionally wired) parameters in the document.";
    public override Guid Id => new Guid("3c8b85a1-4660-4ed7-b408-9ac3d5225454");
    public override string AuthorName => "";
    public override string AuthorContact => "";
  }

  public class AutoInternaliseComponent : GH_Component
  {
    private static readonly MethodInfo InternaliseWiredMethod =
      typeof(AutoInternaliseComponent).GetMethod(nameof(InternaliseWiredTyped), BindingFlags.NonPublic | BindingFlags.Static);
    private static readonly MethodInfo InternaliseReferencesMethod =
      typeof(AutoInternaliseComponent).GetMethod(nameof(InternaliseReferencesTyped), BindingFlags.NonPublic | BindingFlags.Static);

    private bool _rhino;
    private bool _all;
    private bool _pendingRhino;
    private bool _pendingAll;
    private GH_Document _hookedDoc;

    // Automatic Rhino internalization runs at most once per interval; switching the toggle on resets it.
    // The wait uses its own timer because GH_Document.ScheduleSolution fires all callbacks at the earliest
    // scheduled time, so a long delay there would be cut short by any other scheduled solution.
    private static readonly TimeSpan RhinoInterval = TimeSpan.FromMinutes(5);
    private DateTime _lastRhinoRun = DateTime.MinValue;
    private System.Windows.Forms.Timer _rhinoTimer;

    // Rhino objects that could not be loaded; they are not retried until a toggle is switched on again,
    // otherwise every solution would schedule another attempt.
    private readonly HashSet<Guid> _failedIds = new HashSet<Guid>();
    private readonly List<string> _internalized = new List<string>();
    private readonly List<string> _failed = new List<string>();

    public AutoInternaliseComponent()
      : base("Auto Internalise", "AutoInt",
             "Internalizes geometry referenced from the Rhino file (continuously) so the definition keeps working " +
             "without it, or internalizes everything that supports it (once).",
             "Params", "Util")
    { }

    public override Guid ComponentGuid => new Guid("916ac35d-0f66-4309-9cd8-25d402adccf3");
    protected override Bitmap Icon => null;
    public override GH_Exposure Exposure => GH_Exposure.primary;

    protected override void RegisterInputParams(GH_InputParamManager pManager)
    {
      pManager.AddBooleanParameter("Internalize Rhino Elements", "Rhino",
        "While true, geometry referenced from the Rhino file is internalized automatically (also when you set new references).",
        GH_ParamAccess.item, false);
      pManager.AddBooleanParameter("Internalize All", "All",
        "When this becomes true, every parameter that supports it is internalized once: Rhino references as well as " +
        "parameters with incoming wires (the wires are removed).",
        GH_ParamAccess.item, false);
    }

    protected override void RegisterOutputParams(GH_OutputParamManager pManager)
    {
      pManager.AddTextParameter("Internalized", "I",
        "What was internalized since a toggle was last switched on: parameter, Rhino object ID, type, name and layer.",
        GH_ParamAccess.list);
      pManager.AddTextParameter("Failed", "F",
        "Rhino references that could not be loaded (object not in the open Rhino file), so they are still references.",
        GH_ParamAccess.list);
    }

    public override void AddedToDocument(GH_Document document)
    {
      base.AddedToDocument(document);
      Hook(document);
    }

    public override void RemovedFromDocument(GH_Document document)
    {
      Unhook();
      base.RemovedFromDocument(document);
    }

    public override void DocumentContextChanged(GH_Document document, GH_DocumentContext context)
    {
      base.DocumentContextChanged(document, context);
      if (context == GH_DocumentContext.Close || context == GH_DocumentContext.Unloaded) Unhook();
      else if (context == GH_DocumentContext.Loaded || context == GH_DocumentContext.Open) Hook(document);
    }

    private void Hook(GH_Document doc)
    {
      if (ReferenceEquals(_hookedDoc, doc)) return;
      Unhook();
      _hookedDoc = doc;
      doc.SolutionEnd += OnSolutionEnd;
    }

    private void Unhook()
    {
      StopRhinoTimer();
      if (_hookedDoc != null) _hookedDoc.SolutionEnd -= OnSolutionEnd;
      _hookedDoc = null;
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
      bool rhino = false, all = false;
      DA.GetData(0, ref rhino);
      DA.GetData(1, ref all);

      bool rhinoRisingEdge = rhino && !_rhino;
      bool allRisingEdge = all && !_all;
      _rhino = rhino;
      _all = all;

      if (rhinoRisingEdge || allRisingEdge)
      {
        _failedIds.Clear();
        _internalized.Clear();
        _failed.Clear();
      }
      if (rhinoRisingEdge)
        _lastRhinoRun = DateTime.MinValue;
      if (rhinoRisingEdge || !rhino)
        StopRhinoTimer();
      if (!rhino)
        Message = null;

      DA.SetDataList(0, _internalized);
      DA.SetDataList(1, _failed);
      if (_failed.Count > 0)
        AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
          $"{_failed.Count} Rhino reference(s) could not be loaded and were left as references. See the F output.");

      if (allRisingEdge)
        ScheduleAll(OnPingDocument());
    }

    // Catches references set after the toggle was switched on (e.g. "Set one curve").
    private void OnSolutionEnd(object sender, GH_SolutionEventArgs e)
    {
      if (!_rhino || _pendingRhino || Locked || (_rhinoTimer?.Enabled ?? false)) return;
      if (e.Document == null || FindReferencedParams(e.Document).Count == 0) return;

      var wait = _lastRhinoRun + RhinoInterval - DateTime.Now;
      if (wait <= TimeSpan.Zero)
      {
        ScheduleRhino(e.Document);
        return;
      }

      if (_rhinoTimer == null)
      {
        _rhinoTimer = new System.Windows.Forms.Timer();
        _rhinoTimer.Tick += (s, a) =>
        {
          StopRhinoTimer();
          if (_rhino && _hookedDoc != null) ScheduleRhino(_hookedDoc);
        };
      }
      _rhinoTimer.Interval = Math.Max(1, (int)Math.Ceiling(wait.TotalMilliseconds));
      _rhinoTimer.Start();

      Message = $"Next: {DateTime.Now + wait:HH:mm:ss}";
      Grasshopper.Instances.InvalidateCanvas();
    }

    private void StopRhinoTimer()
    {
      _rhinoTimer?.Stop();
    }

    // The document must not be modified during a solution, so the work is deferred to a scheduled solution.
    private void ScheduleRhino(GH_Document doc)
    {
      if (doc == null || _pendingRhino) return;
      _pendingRhino = true;
      doc.ScheduleSolution(5, d =>
      {
        _pendingRhino = false;
        // The toggle may have been switched off, or the component removed, while waiting.
        if (!_rhino || !ReferenceEquals(d, _hookedDoc)) return;

        Internalise(d, includeWired: false);
        _lastRhinoRun = DateTime.Now;
        Message = null;
        ExpireSolution(false);
      });
    }

    private void ScheduleAll(GH_Document doc)
    {
      if (doc == null || _pendingAll) return;
      _pendingAll = true;
      doc.ScheduleSolution(5, d =>
      {
        _pendingAll = false;
        if (!ReferenceEquals(d, _hookedDoc)) return;

        Internalise(d, includeWired: true);
        ExpireSolution(false);
      });
    }

    private void Internalise(GH_Document doc, bool includeWired)
    {
      var undo = new GH_UndoRecord("Auto internalize");

      foreach (var (param, itemType) in FindReferencedParams(doc))
      {
        undo.AddAction(new GH_GenericObjectAction(TopLevel(param)));
        InternaliseReferencesMethod.MakeGenericMethod(itemType)
          .Invoke(null, new object[] { param, ParamLabel(param), _failedIds, _internalized, _failed });
      }

      if (includeWired)
      {
        foreach (var param in AllParams(doc))
        {
          if (param.SourceCount == 0) continue;
          var itemType = PersistentItemType(param.GetType());
          if (itemType == null) continue;

          undo.AddAction(new GH_GenericObjectAction(TopLevel(param)));
          InternaliseWiredMethod.MakeGenericMethod(itemType)
            .Invoke(null, new object[] { param, ParamLabel(param), _failedIds, _internalized, _failed });
        }
      }

      if (undo.ActionCount > 0)
        doc.UndoUtil.RecordEvent(undo);
    }

    private IEnumerable<IGH_Param> AllParams(GH_Document doc)
    {
      foreach (var obj in doc.Objects)
      {
        if (ReferenceEquals(obj, this)) continue;

        if (obj is IGH_Param param)
          yield return param;
        else if (obj is IGH_Component comp)
          foreach (var input in comp.Params.Input)
            yield return input;
      }
    }

    /// <summary>Parameters whose internal (persistent) data still contains loadable Rhino references.</summary>
    private List<(IGH_Param, Type)> FindReferencedParams(GH_Document doc)
    {
      var result = new List<(IGH_Param, Type)>();
      foreach (var param in AllParams(doc))
      {
        // Persistent data is only used when nothing is wired in.
        if (param.SourceCount > 0) continue;
        var itemType = PersistentItemType(param.GetType());
        if (itemType == null || !typeof(IGH_GeometricGoo).IsAssignableFrom(itemType)) continue;

        var data = (IGH_Structure)param.GetType().GetProperty("PersistentData").GetValue(param);
        foreach (var goo in data.AllData(true))
        {
          if (goo is IGH_GeometricGoo geo && geo.IsReferencedGeometry && !_failedIds.Contains(geo.ReferenceID))
          {
            result.Add((param, itemType));
            break;
          }
        }
      }
      return result;
    }

    private static string ParamLabel(IGH_Param param)
    {
      var top = param.Attributes?.GetTopLevel?.DocObject;
      return top != null && !ReferenceEquals(top, param) ? $"{top.NickName}.{param.NickName}" : param.NickName;
    }

    private static IGH_DocumentObject TopLevel(IGH_Param param) =>
      param.Attributes?.GetTopLevel?.DocObject ?? param;

    /// <summary>Returns T if the type derives from GH_PersistentParam&lt;T&gt;, otherwise null.</summary>
    private static Type PersistentItemType(Type type)
    {
      for (var t = type; t != null; t = t.BaseType)
      {
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(GH_PersistentParam<>))
          return t.GetGenericArguments()[0];
      }
      return null;
    }

    /// <summary>Describes a Rhino object as "id, type 'name' on layer".</summary>
    private static string DescribeRhinoObject(Guid id, IGH_GeometricGoo geo)
    {
      var text = $"{id} {geo.TypeName}";
      var rhinoDoc = RhinoDoc.ActiveDoc;
      var obj = rhinoDoc?.Objects.FindId(id);
      if (obj == null) return text + " (not in the open Rhino file)";

      if (!string.IsNullOrEmpty(obj.Attributes.Name))
        text += $" '{obj.Attributes.Name}'";
      var layerIndex = obj.Attributes.LayerIndex;
      if (layerIndex >= 0 && layerIndex < rhinoDoc.Layers.Count)
        text += $" on layer '{rhinoDoc.Layers[layerIndex].FullPath}'";
      return text;
    }

    /// <summary>Same as Grasshopper's own "Internalise data": load the geometry, then drop the Rhino object ID.</summary>
    private static void DestroyReferences<T>(GH_PersistentParam<T> param, string label,
      HashSet<Guid> failedIds, List<string> internalized, List<string> failed) where T : class, IGH_Goo
    {
      foreach (var goo in param.PersistentData.AllData(true))
      {
        if (!(goo is IGH_GeometricGoo geo) || !geo.IsReferencedGeometry) continue;

        var id = geo.ReferenceID;
        if (failedIds.Contains(id)) continue;

        var description = $"{label}: {DescribeRhinoObject(id, geo)}";
        if (geo.IsGeometryLoaded || geo.LoadGeometry())
        {
          geo.ReferenceID = Guid.Empty;
          internalized.Add(description);
        }
        else
        {
          failedIds.Add(id);
          failed.Add(description);
        }
      }
    }

    private static void InternaliseReferencesTyped<T>(GH_PersistentParam<T> param, string label,
      HashSet<Guid> failedIds, List<string> internalized, List<string> failed) where T : class, IGH_Goo
    {
      DestroyReferences(param, label, failedIds, internalized, failed);
      param.ExpireSolution(false);
    }

    private static void InternaliseWiredTyped<T>(GH_PersistentParam<T> param, string label,
      HashSet<Guid> failedIds, List<string> internalized, List<string> failed) where T : class, IGH_Goo
    {
      // Deep copy of the current (volatile) data.
      var data = new GH_Structure<T>((GH_Structure<T>)param.VolatileData, false);

      param.PersistentData.Clear();
      for (int i = 0; i < data.PathCount; i++)
        param.PersistentData.AppendRange(data.Branches[i], data.Paths[i]);

      int wires = param.SourceCount;
      param.RemoveAllSources();
      internalized.Add($"{label}: {data.DataCount} item(s) from {wires} wire(s)");

      // Data flowing in from a referencing parameter carries the Rhino IDs along; drop them too.
      DestroyReferences(param, label, failedIds, internalized, failed);
      param.ExpireSolution(false);
    }
  }
}
