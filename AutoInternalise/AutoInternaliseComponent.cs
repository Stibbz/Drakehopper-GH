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
    public override string Description => "Internalises all persistent parameters in the document.";
    public override Guid Id => new Guid("3c8b85a1-4660-4ed7-b408-9ac3d5225454");
    public override string AuthorName => "";
    public override string AuthorContact => "";
  }

  public class AutoInternaliseComponent : GH_Component
  {
    private static readonly MethodInfo InternaliseMethod =
      typeof(AutoInternaliseComponent).GetMethod(nameof(InternaliseTyped), BindingFlags.NonPublic | BindingFlags.Static);

    private int _lastCount;
    private bool _pending;

    public AutoInternaliseComponent()
      : base("Auto Internalise", "AutoInt",
             "When Run is true, internalises the data of every parameter in the document that supports it " +
             "(floating params and component inputs) and removes their wires.",
             "Params", "Util")
    { }

    public override Guid ComponentGuid => new Guid("916ac35d-0f66-4309-9cd8-25d402adccf3");
    protected override Bitmap Icon => null;
    public override GH_Exposure Exposure => GH_Exposure.primary;

    protected override void RegisterInputParams(GH_InputParamManager pManager)
    {
      pManager.AddBooleanParameter("Run", "R", "Set to true to internalise all parameters.", GH_ParamAccess.item, false);
    }

    protected override void RegisterOutputParams(GH_OutputParamManager pManager)
    {
      pManager.AddIntegerParameter("Count", "N", "Number of parameters internalised on the last run.", GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
      bool run = false;
      DA.GetData(0, ref run);
      DA.SetData(0, _lastCount);

      if (!run || _pending) return;

      var doc = OnPingDocument();
      if (doc == null) return;

      // The document must not be modified during a solution, so defer the work.
      _pending = true;
      doc.ScheduleSolution(5, d =>
      {
        _pending = false;
        _lastCount = InternaliseAll(d);
        ExpireSolution(false);
      });
    }

    private int InternaliseAll(GH_Document doc)
    {
      var targets = new List<IGH_Param>();
      foreach (var obj in doc.Objects)
      {
        if (ReferenceEquals(obj, this)) continue;

        if (obj is IGH_Param param)
          targets.Add(param);
        else if (obj is IGH_Component comp)
          targets.AddRange(comp.Params.Input);
      }

      var undo = new GH_UndoRecord("Auto internalise");
      int count = 0;

      foreach (var param in targets)
      {
        if (param.SourceCount == 0) continue;
        var itemType = PersistentItemType(param.GetType());
        if (itemType == null) continue;

        // Snapshot the whole object (data + wires) so Ctrl+Z restores it.
        IGH_DocumentObject owner = param.Attributes?.GetTopLevel?.DocObject ?? param;
        undo.AddAction(new GH_GenericObjectAction(owner));

        InternaliseMethod.MakeGenericMethod(itemType).Invoke(null, new object[] { param });
        count++;
      }

      if (count > 0)
        doc.UndoUtil.RecordEvent(undo);

      return count;
    }

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

    private static void InternaliseTyped<T>(GH_PersistentParam<T> param) where T : class, IGH_Goo
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
