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
             "Creates a plane on a curve at a parameter. By default the plane stands upright and " +
             "perpendicular to the curve, with its Y axis along world Z. " +
             "Use Perpendicular to switch to parallel, Vertical to lay it flat, " +
             "and World Z to follow the curve's slope instead of world Z.",
             "Drakehopper", "Util")
    { }

    public override Guid ComponentGuid => new Guid("5b0e0a57-8d1c-4f0e-9a43-6a1d7c3b2e11");
    protected override Bitmap Icon => null;
    public override GH_Exposure Exposure => GH_Exposure.primary;

    protected override void RegisterInputParams(GH_InputParamManager pManager)
    {
      pManager.AddCurveParameter("Curve", "C", "Section line or curve.", GH_ParamAccess.item);
      pManager.AddNumberParameter("Parameter", "t",
        "Curve parameter (raw). Reparametrize the curve upstream if you need 0-1.", GH_ParamAccess.item, 0.0);
      pManager.AddBooleanParameter("Perpendicular", "Pe",
        "True: plane is perpendicular to the curve. False: plane is parallel to the curve.",
        GH_ParamAccess.item, true);
      pManager.AddBooleanParameter("Vertical", "Ve",
        "True: plane stands upright (vertical). False: plane lies flat (horizontal).",
        GH_ParamAccess.item, true);
      pManager.AddBooleanParameter("World Z", "Z",
        "True: use world Z as up, so upright planes stay vertical. False: follow the curve's slope, so a " +
        "perpendicular plane is exactly 90 degrees to the curve.", GH_ParamAccess.item, true);
    }

    protected override void RegisterOutputParams(GH_OutputParamManager pManager)
    {
      pManager.AddPlaneParameter("Plane", "Pl", "Plane through the curve at the parameter.", GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
      Curve crv = null;
      double t = 0;
      bool zGlobal = true, perpendicular = true, vertical = true;
      if (!DA.GetData(0, ref crv) || crv == null) return;
      DA.GetData(1, ref t);
      DA.GetData(2, ref perpendicular);
      DA.GetData(3, ref vertical);
      DA.GetData(4, ref zGlobal);

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

      Vector3d up = Vector3d.ZAxis;

      if (!zGlobal)
      {
        // Use the full 3D tangent; Y is world Z made perpendicular to it.
        Vector3d tan = crv.TangentAt(t);
        if (tan.Unitize())
        {
          Vector3d y = up - (up * tan) * tan;
          if (y.Unitize())
          {
            up = y;
            x = tan;
          }
        }
      }

      Vector3d side = Vector3d.CrossProduct(up, x);
      side.Unitize();
      // Horizontal: normal = up, X along curve (parallel) or across it (perpendicular).
      // Vertical: normal along the curve (perpendicular) or sideways (parallel).
      Plane plane;
      if (!vertical)
        plane = perpendicular ? new Plane(origin, side, -x) : new Plane(origin, x, side);
      else
        plane = perpendicular ? new Plane(origin, side, up) : new Plane(origin, x, up);

      DA.SetData(0, plane);
    }
  }
}
