using System;
using System.Collections.Generic;
using System.Drawing;
using Grasshopper.Kernel;
using Rhino.Geometry;

namespace Drakehopper
{
  /// <summary>
  /// Divide Distance with a different chord distance per step.
  /// Starts at the curve start; each point lies exactly Dist[i] (straight-line) from the previous point.
  /// A distance of 0 returns the current point (a leading 0 gives the curve start).
  /// </summary>
  public class DivideDistAlongCurveComponent : GH_Component
  {
    private const int Samples = 2000;   // scan samples per step
    private const int Bisections = 60;

    public DivideDistAlongCurveComponent()
      : base("DivideDistAlongCurve", "DivDist",
             "Like Divide Distance, but with a different chord (straight-line) distance per step. " +
             "Each point lies exactly Dist from the previous one. A distance of 0 returns the current point " +
             "(a leading 0 gives the curve start). Distances that cannot be reached are not placed.",
             "Drakehopper", "Util")
    { }

    public override Guid ComponentGuid => new Guid("7b3e91c4-5a2d-4f86-b1c0-d9e4a6728f15");
    protected override Bitmap Icon => null;
    public override GH_Exposure Exposure => GH_Exposure.primary;

    protected override void RegisterInputParams(GH_InputParamManager pManager)
    {
      pManager.AddCurveParameter("Curve", "C", "Reference curve.", GH_ParamAccess.item);
      pManager.AddNumberParameter("Distances", "Dist",
        "Chord distance from each point to the next (individual spacings, not cumulative). 0 returns the current point.",
        GH_ParamAccess.list);
    }

    protected override void RegisterOutputParams(GH_OutputParamManager pManager)
    {
      pManager.AddPointParameter("Points", "Pts", "Points on the curve.", GH_ParamAccess.list);
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
      Curve crv = null;
      var dists = new List<double>();
      if (!DA.GetData(0, ref crv) || crv == null) return;
      if (!DA.GetDataList(1, dists) || dists.Count == 0) return;

      var pts = new List<Point3d>();
      Point3d p = crv.PointAtStart;
      double tPrev = crv.Domain.T0, tEnd = crv.Domain.T1;

      for (int idx = 0; idx < dists.Count; idx++)
      {
        double d = dists[idx];
        if (d <= 0) { pts.Add(p); continue; }

        double a = tPrev, b = double.NaN;
        double step = (tEnd - tPrev) / Samples;
        for (int i = 1; i <= Samples; i++)
        {
          double tb = tPrev + i * step;
          if (crv.PointAt(tb).DistanceTo(p) - d >= 0) { b = tb; break; }
          a = tb;
        }

        if (double.IsNaN(b))
        {
          AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
            $"Distance #{idx} ({d:0.###}) runs past the end of the curve: no point is that far from the previous one. " +
            $"{dists.Count - idx} point(s) not placed.");
          break;
        }

        for (int k = 0; k < Bisections; k++)
        {
          double m = 0.5 * (a + b);
          if (crv.PointAt(m).DistanceTo(p) - d >= 0) b = m; else a = m;
        }
        tPrev = 0.5 * (a + b);
        p = crv.PointAt(tPrev);
        pts.Add(p);
      }

      DA.SetDataList(0, pts);
    }
  }
}
