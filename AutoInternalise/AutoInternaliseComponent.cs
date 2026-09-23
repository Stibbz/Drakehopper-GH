using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Reflection;
using Grasshopper.GUI.Canvas;
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
    public override string Description => "Internalizes Rhino-referenced, Revit and wired parameters in the document.";
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

    // Rhino and Revit auto modes share one cooldown. The wait uses its own timer because
    // GH_Document.ScheduleSolution fires all callbacks at the earliest scheduled time, so a long delay there
    // would be cut short by any other scheduled solution.
    private static readonly TimeSpan AutoInterval = TimeSpan.FromMinutes(15);

    private bool _rhino;
    private bool _revit;
    private bool _all;
    private GH_Document _hookedDoc;

    private bool _pendingAuto;
    private bool _pendingRefresh;
    private DateTime _lastAutoRun = DateTime.MinValue;
    private DateTime _nextAutoRun;
    private System.Windows.Forms.Timer _autoTimer;

    // Session state, cleared when a toggle is switched on.
    // Rhino objects that could not be loaded are not retried, otherwise every solution would schedule another attempt.
    private readonly HashSet<Guid> _failedIds = new HashSet<Guid>();
    private readonly List<string> _nodes = new List<string>();
    private readonly List<string> _details = new List<string>();
    private readonly List<string> _failed = new List<string>();
    private readonly HashSet<IGH_DocumentObject> _highlighted = new HashSet<IGH_DocumentObject>();

    // Everything internalized in the file, however it got there.
    private List<string> _fileInternalized = new List<string>();
    // Signature of each component input's default persistent data, so default values don't count as internalized.
    private readonly Dictionary<string, string> _defaultSignatures = new Dictionary<string, string>();

    private static readonly Color HighlightColor = Color.FromArgb(255, 140, 0);
    private GH_Canvas _paintCanvas;

    public AutoInternaliseComponent()
      : base("Auto Internalise", "AutoInt",
             "Rhino and Revit: while true, internalizes automatically, at most once every 15 minutes (shared). " +
             "All: internalizes everything once when switched on. Internalized nodes are outlined in orange.",
             "Params", "Util")
    { }

    public override Guid ComponentGuid => new Guid("916ac35d-0f66-4309-9cd8-25d402adccf3");
    protected override Bitmap Icon => null;
    public override GH_Exposure Exposure => GH_Exposure.primary;

    protected override void RegisterInputParams(GH_InputParamManager pManager)
    {
      pManager.AddBooleanParameter("Auto Rhino (every 15 min)", "Rhino 15m",
        "CONTINUOUS. While true, geometry referenced from the Rhino file is internalized automatically, " +
        "at most once every 15 minutes (cooldown shared with Revit). Switching it on runs immediately.",
        GH_ParamAccess.item, false);
      pManager.AddBooleanParameter("Auto Revit (every 15 min)", "Revit 15m",
        "CONTINUOUS. While true, every Rhino.Inside.Revit element parameter with incoming wires (e.g. the Elements input " +
        "of Element Geometry fed by a Graphical Element) is internalized and its wires removed, at most once every " +
        "15 minutes (cooldown shared with Rhino). Switching it on runs immediately. " +
        "Note: this stores element references (UniqueIds); the Revit model must still be open to get their geometry.",
        GH_ParamAccess.item, false);
      pManager.AddBooleanParameter("Internalize All (once)", "All once",
        "ONE-OFF. When this switches to true, every parameter that supports it is internalized once: Rhino references " +
        "as well as parameters with incoming wires (the wires are removed).",
        GH_ParamAccess.item, false);
    }

    protected override void RegisterOutputParams(GH_OutputParamManager pManager)
    {
      pManager.AddTextParameter("Internalized Nodes", "Nodes",
        "Nodes internalized by this component since a toggle was last switched on.", GH_ParamAccess.list);
      pManager.AddTextParameter("All Internalized Nodes", "All",
        "Every node in this file that holds internalized data, whether internalized manually or by this component. " +
        "Default values of component inputs and still-linked Rhino references are not counted.", GH_ParamAccess.list);
      pManager.AddTextParameter("Details", "D",
        "Per item: node, Rhino object ID, type, name and layer, or the number of items taken from wires.", GH_ParamAccess.list);
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
      StopAutoTimer();
      UnhookCanvas();
      if (_hookedDoc != null) _hookedDoc.SolutionEnd -= OnSolutionEnd;
      _hookedDoc = null;
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
      bool rhino = false, revit = false, all = false;
      DA.GetData(0, ref rhino);
      DA.GetData(1, ref revit);
      DA.GetData(2, ref all);

      bool rhinoRisingEdge = rhino && !_rhino;
      bool revitRisingEdge = revit && !_revit;
      bool allRisingEdge = all && !_all;
      _rhino = rhino;
      _revit = revit;
      _all = all;

      if (rhinoRisingEdge || revitRisingEdge || allRisingEdge)
      {
        _failedIds.Clear();
        _nodes.Clear();
        _details.Clear();
        _failed.Clear();
        _highlighted.Clear();
        Grasshopper.Instances.InvalidateCanvas();
      }

      // Switching an auto mode on runs straight away (the check happens at the end of this solution).
      if (rhinoRisingEdge || revitRisingEdge)
      {
        _lastAutoRun = DateTime.MinValue;
        StopAutoTimer();
      }
      if (!rhino && !revit)
        StopAutoTimer();
      UpdateMessage();

      var doc = OnPingDocument();
      if (doc != null)
        _fileInternalized = FindFileInternalized(doc);

      DA.SetDataList(0, _nodes);
      DA.SetDataList(1, _fileInternalized);
      DA.SetDataList(2, _details);
      DA.SetDataList(3, _failed);
      if (_failed.Count > 0)
        AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
          $"{_failed.Count} Rhino reference(s) could not be loaded and were left as references. See the F output.");

      if (allRisingEdge)
        ScheduleOnce(doc);
    }

    private void OnSolutionEnd(object sender, GH_SolutionEventArgs e)
    {
      var doc = e.Document;
      if (doc == null || Locked) return;

      RefreshFileOutput(doc);

      if (!(_rhino || _revit) || _pendingAuto || (_autoTimer?.Enabled ?? false)) return;
      if (!HasAutoWork(doc)) return;

      var wait = _lastAutoRun + AutoInterval - DateTime.Now;
      if (wait <= TimeSpan.Zero)
      {
        ScheduleAuto(doc);
        return;
      }

      if (_autoTimer == null)
      {
        _autoTimer = new System.Windows.Forms.Timer();
        _autoTimer.Tick += (s, a) =>
        {
          StopAutoTimer();
          if ((_rhino || _revit) && _hookedDoc != null) ScheduleAuto(_hookedDoc);
        };
      }
      _nextAutoRun = DateTime.Now + wait;
      _autoTimer.Interval = Math.Max(1, (int)Math.Ceiling(wait.TotalMilliseconds));
      _autoTimer.Start();
      UpdateMessage();
    }

    /// <summary>Re-solves this component when the file-wide list changed (e.g. after a manual internalize).</summary>
    private void RefreshFileOutput(GH_Document doc)
    {
      if (_pendingRefresh) return;
      var list = FindFileInternalized(doc);
      if (list.SequenceEqual(_fileInternalized)) return;

      _fileInternalized = list;
      _pendingRefresh = true;
      doc.ScheduleSolution(5, d =>
      {
        _pendingRefresh = false;
        ExpireSolution(false);
      });
    }

    private bool HasAutoWork(GH_Document doc) =>
      (_rhino && FindReferencedParams(doc).Count > 0) ||
      (_revit && FindWiredParams(doc, IsRevitElementType).Any());

    private void StopAutoTimer()
    {
      _autoTimer?.Stop();
    }

    private void UpdateMessage()
    {
      var modes = new List<string>();
      if (_rhino) modes.Add("Rhino");
      if (_revit) modes.Add("Revit");

      string text;
      if (modes.Count == 0)
        text = "Auto: off";
      else
      {
        text = $"Auto {string.Join("+", modes)} / 15 min";
        if (_autoTimer?.Enabled ?? false) text += $" · next {_nextAutoRun:HH:mm}";
        else if (_lastAutoRun != DateTime.MinValue) text += $" · last {_lastAutoRun:HH:mm}";
      }

      if (Message == text) return;
      Message = text;
      Attributes?.ExpireLayout();
      Grasshopper.Instances.RedrawCanvas();
    }

    // The document must not be modified during a solution, so the work is deferred to a scheduled solution.
    private void ScheduleAuto(GH_Document doc)
    {
      if (doc == null || _pendingAuto) return;
      _pendingAuto = true;
      doc.ScheduleSolution(5, d =>
      {
        _pendingAuto = false;
        // The toggles may have been switched off, or the component removed, while waiting.
        if (!(_rhino || _revit) || !ReferenceEquals(d, _hookedDoc)) return;

        Internalise(d, rhinoReferences: _rhino, wiredFilter: _revit ? IsRevitElementType : (Func<Type, bool>)null);
        _lastAutoRun = DateTime.Now;
        UpdateMessage();
        ExpireSolution(false);
      });
    }

    /// <summary>Internalize All: one-off run, triggered when the toggle switches on.</summary>
    private void ScheduleOnce(GH_Document doc)
    {
      if (doc == null) return;
      doc.ScheduleSolution(5, d =>
      {
        if (!ReferenceEquals(d, _hookedDoc)) return;
        Internalise(d, rhinoReferences: true, wiredFilter: _ => true);
        ExpireSolution(false);
      });
    }

    /// <summary>
    /// True for Rhino.Inside.Revit element data types. Matched by name so this plugin also loads without Rhino.Inside.Revit.
    /// </summary>
    private static bool IsRevitElementType(Type itemType)
    {
      const string ElementInterface = "RhinoInside.Revit.GH.Types.IGH_Element";
      if (itemType.FullName == ElementInterface) return true;
      foreach (var i in itemType.GetInterfaces())
        if (i.FullName == ElementInterface) return true;
      return false;
    }

    private void HookCanvas()
    {
      var canvas = Grasshopper.Instances.ActiveCanvas;
      if (canvas == null || ReferenceEquals(canvas, _paintCanvas)) return;
      UnhookCanvas();
      _paintCanvas = canvas;
      canvas.CanvasPostPaintObjects += OnCanvasPostPaintObjects;
    }

    private void UnhookCanvas()
    {
      if (_paintCanvas != null) _paintCanvas.CanvasPostPaintObjects -= OnCanvasPostPaintObjects;
      _paintCanvas = null;
    }

    private void OnCanvasPostPaintObjects(GH_Canvas sender)
    {
      if (_highlighted.Count == 0 || sender.Document == null || !ReferenceEquals(sender.Document, _hookedDoc)) return;

      var g = sender.Graphics;
      using (var pen = new Pen(HighlightColor, 3f))
      using (var fill = new SolidBrush(Color.FromArgb(40, HighlightColor)))
      {
        foreach (var obj in _highlighted)
        {
          // Skip objects that were deleted since.
          if (obj.Attributes == null || !ReferenceEquals(obj.OnPingDocument(), _hookedDoc)) continue;

          var bounds = obj.Attributes.Bounds;
          bounds.Inflate(6f, 6f);
          using (GraphicsPath path = GH_CapsuleRenderEngine.CreateRoundedRectangle(bounds, 6f))
          {
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
          }
        }
      }
    }

    /// <param name="rhinoReferences">Internalize geometry referenced from the Rhino file.</param>
    /// <param name="wiredFilter">If not null, also internalize wired parameters whose data type passes this filter.</param>
    private void Internalise(GH_Document doc, bool rhinoReferences, Func<Type, bool> wiredFilter)
    {
      var undo = new GH_UndoRecord("Auto internalize");
      var changed = new List<IGH_Param>();

      var referenced = rhinoReferences ? FindReferencedParams(doc) : new List<(IGH_Param, Type)>();
      foreach (var (param, itemType) in referenced)
      {
        undo.AddAction(new GH_GenericObjectAction(TopLevel(param)));
        int before = _details.Count;
        InternaliseReferencesMethod.MakeGenericMethod(itemType)
          .Invoke(null, new object[] { param, NodeTitle(param), _failedIds, _details, _failed });
        if (_details.Count > before) changed.Add(param);
      }

      if (wiredFilter != null)
      {
        foreach (var (param, itemType) in FindWiredParams(doc, wiredFilter).ToList())
        {
          undo.AddAction(new GH_GenericObjectAction(TopLevel(param)));
          InternaliseWiredMethod.MakeGenericMethod(itemType)
            .Invoke(null, new object[] { param, NodeTitle(param), _failedIds, _details, _failed });
          changed.Add(param);
        }
      }

      if (undo.ActionCount > 0)
        doc.UndoUtil.RecordEvent(undo);

      foreach (var param in changed)
      {
        var title = NodeTitle(param);
        if (!_nodes.Contains(title)) _nodes.Add(title);
        _highlighted.Add(TopLevel(param));
      }
      if (changed.Count > 0)
      {
        HookCanvas();
        Grasshopper.Instances.InvalidateCanvas();
      }
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

    private IEnumerable<(IGH_Param, Type)> FindWiredParams(GH_Document doc, Func<Type, bool> filter)
    {
      foreach (var param in AllParams(doc))
      {
        if (param.SourceCount == 0) continue;
        var itemType = PersistentItemType(param.GetType());
        if (itemType != null && filter(itemType))
          yield return (param, itemType);
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

        foreach (var goo in PersistentData(param).AllData(true))
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

    /// <summary>
    /// Titles of all nodes that hold internalized data: no wires, persistent data present, no Rhino links left,
    /// and for component inputs, data that differs from the component's default.
    /// </summary>
    private List<string> FindFileInternalized(GH_Document doc)
    {
      var result = new List<string>();
      foreach (var param in AllParams(doc))
      {
        if (param.SourceCount > 0 || PersistentItemType(param.GetType()) == null) continue;

        var data = PersistentData(param);
        if (data == null || data.DataCount == 0) continue;
        if (data.AllData(true).Any(g => g is IGH_GeometricGoo geo && geo.IsReferencedGeometry)) continue;

        if (TopLevel(param) is IGH_Component comp && IsDefaultInput(comp, param, data)) continue;

        result.Add(NodeTitle(param));
      }
      return result;
    }

    private bool IsDefaultInput(IGH_Component comp, IGH_Param input, IGH_Structure data)
    {
      var key = comp.ComponentGuid + "|" + input.Name;
      if (!_defaultSignatures.TryGetValue(key, out var defaultSignature))
      {
        defaultSignature = null;
        try
        {
          if (Grasshopper.Instances.ComponentServer.EmitObject(comp.ComponentGuid) is IGH_Component fresh)
          {
            var freshInput = fresh.Params.Input.FirstOrDefault(p => p.Name == input.Name);
            var freshData = freshInput != null ? PersistentData(freshInput) : null;
            if (freshData != null && freshData.DataCount > 0)
              defaultSignature = Signature(freshData);
          }
        }
        catch
        {
          // Components that can't be created on their own are treated as having no defaults.
        }
        _defaultSignatures[key] = defaultSignature;
      }
      return defaultSignature != null && Signature(data) == defaultSignature;
    }

    private static string Signature(IGH_Structure data) =>
      data.PathCount + "|" + string.Join("\u001f", data.AllData(false).Select(g => g?.ToString() ?? ""));

    private static IGH_Structure PersistentData(IGH_Param param) =>
      param.GetType().GetProperty("PersistentData")?.GetValue(param) as IGH_Structure;

    private static string Title(IGH_DocumentObject obj) =>
      !string.IsNullOrEmpty(obj.NickName) && obj.NickName != obj.Name ? $"{obj.NickName} ({obj.Name})" : obj.Name;

    /// <summary>"Curve", "Site (Curve)" or "Element Geometry > Elements".</summary>
    private static string NodeTitle(IGH_Param param)
    {
      var top = TopLevel(param);
      return ReferenceEquals(top, param) ? Title(param) : $"{Title(top)} > {param.Name}";
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
      HashSet<Guid> failedIds, List<string> details, List<string> failed) where T : class, IGH_Goo
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
          details.Add(description);
        }
        else
        {
          failedIds.Add(id);
          failed.Add(description);
        }
      }
    }

    private static void InternaliseReferencesTyped<T>(GH_PersistentParam<T> param, string label,
      HashSet<Guid> failedIds, List<string> details, List<string> failed) where T : class, IGH_Goo
    {
      DestroyReferences(param, label, failedIds, details, failed);
      param.ExpireSolution(false);
    }

    private static void InternaliseWiredTyped<T>(GH_PersistentParam<T> param, string label,
      HashSet<Guid> failedIds, List<string> details, List<string> failed) where T : class, IGH_Goo
    {
      // Deep copy of the current (volatile) data.
      var data = new GH_Structure<T>((GH_Structure<T>)param.VolatileData, false);

      param.PersistentData.Clear();
      for (int i = 0; i < data.PathCount; i++)
        param.PersistentData.AppendRange(data.Branches[i], data.Paths[i]);

      int wires = param.SourceCount;
      param.RemoveAllSources();
      details.Add($"{label}: {data.DataCount} item(s) from {wires} wire(s)");

      // Data flowing in from a referencing parameter carries the Rhino IDs along; drop them too.
      DestroyReferences(param, label, failedIds, details, failed);
      param.ExpireSolution(false);
    }
  }
}
