using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Grasshopper.Kernel.Undo;
using Grasshopper.Kernel.Undo.Actions;

namespace AutoInternalise
{
  public class AutoInternaliseInfo : GH_AssemblyInfo
  {
    public override string Name => "AutoInternalise";
    public override string Description => "Internalises Rhino-referenced (and optionally wired) parameters in the document.";
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
    private bool _pending;
    private int _referencedCount;
    private int _wiredCount;
    private int _failedCount;
    private GH_Document _hookedDoc;

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
      pManager.AddIntegerParameter("Referenced", "Ref", "Number of Rhino-referenced parameters internalised so far.", GH_ParamAccess.item);
      pManager.AddIntegerParameter("Wired", "Wir", "Number of wired parameters internalised so far.", GH_ParamAccess.item);
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
      if (_hookedDoc != null) _hookedDoc.SolutionEnd -= OnSolutionEnd;
      _hookedDoc = null;
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
      bool rhino = false, all = false;
      DA.GetData(0, ref rhino);
      DA.GetData(1, ref all);

      DA.SetData(0, _referencedCount);
      DA.SetData(1, _wiredCount);
      if (_failedCount > 0)
        AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
          $"{_failedCount} referenced object(s) could not be loaded from Rhino and were left as references.");

      _rhino = rhino;

      bool allRisingEdge = all && !_all;
      _all = all;

      if (allRisingEdge)
        Schedule(OnPingDocument(), includeWired: true);
    }

    // Catches references set after Run became true (e.g. "Set one curve").
    private void OnSolutionEnd(object sender, GH_SolutionEventArgs e)
    {
      if (!_rhino || _pending || Locked) return;
      if (e.Document != null && FindReferencedParams(e.Document).Count > 0)
        Schedule(e.Document, includeWired: false);
    }

    private void Schedule(GH_Document doc, bool includeWired)
    {
      if (doc == null || _pending) return;

      // The document must not be modified during a solution, so defer the work.
      _pending = true;
      doc.ScheduleSolution(5, d =>
      {
        _pending = false;
        Internalise(d, includeWired);
        ExpireSolution(false);
      });
    }

    private void Internalise(GH_Document doc, bool includeWired)
    {
      var undo = new GH_UndoRecord("Auto internalise");
      _failedCount = 0;

      foreach (var (param, itemType) in FindReferencedParams(doc))
      {
        undo.AddAction(new GH_GenericObjectAction(TopLevel(param)));
        var args = new object[] { param, 0 };
        InternaliseReferencesMethod.MakeGenericMethod(itemType).Invoke(null, args);
        _failedCount += (int)args[1];
        _referencedCount++;
      }

      if (includeWired)
      {
        foreach (var param in AllParams(doc))
        {
          if (param.SourceCount == 0) continue;
          var itemType = PersistentItemType(param.GetType());
          if (itemType == null) continue;

          undo.AddAction(new GH_GenericObjectAction(TopLevel(param)));
          InternaliseWiredMethod.MakeGenericMethod(itemType).Invoke(null, new object[] { param });
          _wiredCount++;
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

    /// <summary>Parameters whose internal (persistent) data contains geometry referenced from the Rhino document.</summary>
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
          if (goo is IGH_GeometricGoo geo && geo.IsReferencedGeometry)
          {
            result.Add((param, itemType));
            break;
          }
        }
      }
      return result;
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

    private static void InternaliseReferencesTyped<T>(GH_PersistentParam<T> param, out int failed) where T : class, IGH_Goo
    {
      failed = 0;
      foreach (var goo in param.PersistentData.AllData(true))
      {
        if (!(goo is IGH_GeometricGoo geo) || !geo.IsReferencedGeometry) continue;

        // Make sure the geometry itself is in memory before dropping the link to the Rhino object.
        if (!geo.IsGeometryLoaded && !geo.LoadGeometry())
        {
          failed++;
          continue;
        }
        geo.ReferenceID = Guid.Empty;
      }
      param.ExpireSolution(false);
    }

    private static void InternaliseWiredTyped<T>(GH_PersistentParam<T> param) where T : class, IGH_Goo
    {
      // Deep copy of the current (volatile) data.
      var data = new GH_Structure<T>((GH_Structure<T>)param.VolatileData, false);

      param.PersistentData.Clear();
      for (int i = 0; i < data.PathCount; i++)
        param.PersistentData.AppendRange(data.Branches[i], data.Paths[i]);

      param.RemoveAllSources();
      param.ExpireSolution(false);
    }
  }
}
