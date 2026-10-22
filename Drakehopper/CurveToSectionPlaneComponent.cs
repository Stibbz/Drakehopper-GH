using System;
using System.Drawing;
using Grasshopper.Kernel;
using Rhino.Geometry;

namespace Drakehopper
{
  /// <summary>
  /// Turns a section line/curve into a vertical plane (X along the curve's horizontal tangent, Y = world Z),
  /// equivalent to HFrame -> DePlane -> Plane(O, X, Z).
  /// </summary>
  public class CurveToSectionPlaneComponent : GH_Component
  {
    public CurveToSectionPlaneComponent()
      : base("Section Plane", "SecPlane",
             "Turns a section line/curve into a vertical plane at a curve parameter. " +
             "The plane's X axis follows the curve's horizontal direction and its Y axis is world Z, " +
             "so the plane stands upright on the section line and its normal points sideways to the line.",
             "Drakehopper", "Util")
    { }

    public override Guid ComponentGuid => new Guid("5b0e0a57-8d1c-4f0e-9a43-6a1d7c3b2e11");
    protected override Bitmap Icon => null;
    public override GH_Exposure Exposure => GH_Exposure.primary;

    protected override void RegisterInputParams(GH_InputParamManager pManager)
    {
      pManager.AddCurveParameter("Curve", "C", "Section line or curve.", GH_ParamAccess.item);
      pManager.AddNumberParameter("Parameter", "t",
        "Position on the curve. Normalized (0 = start, 1 = end) by default.", GH_ParamAccess.item, 0.0);
      pManager.AddBooleanParameter("Normalized", "N",
        "True: t is 0-1 along the curve. False: t is the raw curve parameter.", GH_ParamAccess.item, true);
    }

    protected override void RegisterOutputParams(GH_OutputParamManager pManager)
    {
      pManager.AddPlaneParameter("Plane", "Pl", "Vertical plane through the curve at the parameter.", GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
      Curve crv = null;
      double t = 0;
      bool normalized = true;
      if (!DA.GetData(0, ref crv) || crv == null) return;
      DA.GetData(1, ref t);
      DA.GetData(2, ref normalized);

      if (normalized) t = crv.Domain.ParameterAt(t);

      Point3d origin = crv.PointAt(t);
      Vector3d x = crv.TangentAt(t);
      x.Z = 0;

      // Fall back to the start-end chord if the tangent is vertical or degenerate.
      if (!x.Unitize())
      {
        x = crv.PointAtEnd - crv.PointAtStart;
        x.Z = 0;
        if (!x.Unitize())
        {
          AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Curve has no horizontal direction.");
          return;
        }
      }

      DA.SetData(0, new Plane(origin, x, Vector3d.ZAxis));
    }
  }
}
