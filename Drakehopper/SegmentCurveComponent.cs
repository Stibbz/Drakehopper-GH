using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Grasshopper.Kernel;
using Rhino.Geometry;

namespace Drakehopper
{
  /// <summary>
  /// Segments a reference curve into straight runs of whole elements.
  /// Each run is n * elemLen long (n >= minElem), starts and ends on the curve, stays within maxDeviation of it,
  /// and kinks at most maxAngle against the previous run. Greedy: each run is as long as the rules allow.
  /// </summary>
  public class SegmentCurveComponent : GH_Component
  {
    private const double Sample = 25.0;   // curve sampling step (mm); smaller = more accurate, slower
    private const int MaxSegments = 2000;

    public SegmentCurveComponent()
      : base("Segment Curve", "SegCrv",
             "Splits a curve into straight runs of whole elements. Each run holds at least Min elements, " +
             "starts and ends on the curve and stays within Dev of it; the angle between consecutive runs never " +
             "exceeds Ang. Each run is made as long as the rules allow.",
             "Drakehopper", "Util")
    { }

    public override Guid ComponentGuid => new Guid("c2f4a7d1-3b8e-4d65-9f0a-71e5b6c8d934");
    protected override Bitmap Icon => null;
    public override GH_Exposure Exposure => GH_Exposure.primary;

    protected override void RegisterInputParams(GH_InputParamManager pManager)
    {
      pManager.AddCurveParameter("Curve", "C", "Reference curve or polyline.", GH_ParamAccess.item);
      pManager.AddIntegerParameter("Min elements", "Min", "Minimum elements per straight run (10 = 10 x element length).", GH_ParamAccess.item);
      pManager.AddNumberParameter("Max deviation", "Dev", "Maximum distance between runs and the reference curve, in mm.", GH_ParamAccess.item);
      pManager.AddNumberParameter("Element length", "Len", "Element length in mm. Default 1400.", GH_ParamAccess.item, 1400.0);
      pManager.AddNumberParameter("Max angle", "Ang", "Maximum angle between consecutive runs, in degrees. Default 5.", GH_ParamAccess.item, 5.0);
      pManager.AddBooleanParameter("Connect last", "Last",
        "Draw the final leftover part (shorter than Min elements) as one run of round(rest / element length) elements, " +
        "if it keeps Dev and Ang. Default true.", GH_ParamAccess.item, true);
    }

    protected override void RegisterOutputParams(GH_OutputParamManager pManager)
    {
      pManager.AddCurveParameter("Polyline", "PL", "Segmented polyline.", GH_ParamAccess.item);
      pManager.AddPointParameter("Points", "Pts", "Start and end points of each run (polyline vertices).", GH_ParamAccess.list);
      pManager.AddTextParameter("Info", "I", "Report: sections, max angle, max deviation, failure reason.", GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
      Curve crv = null;
      int minElem = 0;
      double maxDev = 0, elemLen = 1400.0, maxAngle = 5.0;
      bool connectLast = true;
      DA.GetData(0, ref crv);
      if (!DA.GetData(1, ref minElem)) return;
      if (!DA.GetData(2, ref maxDev)) return;
      DA.GetData(3, ref elemLen);
      DA.GetData(4, ref maxAngle);
      DA.GetData(5, ref connectLast);
      if (elemLen <= 0) elemLen = 1400.0;
      if (maxAngle <= 0) maxAngle = 5.0;

      var points = new List<Point3d>();
      string info = Run(crv, minElem, maxDev, elemLen, maxAngle, connectLast, points);

      if (points.Count >= 2)
      {
        DA.SetData(0, new PolylineCurve(points));
        DA.SetDataList(1, points);
      }
      DA.SetData(2, info);
      if (info.StartsWith("RESULT: FAILED"))
        AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, info.Split('\n').Skip(1).FirstOrDefault() ?? "Failed");
    }

    private enum Status { Ok, Short, Dev, Ang }

    private static double SegDist(Point3d p, Point3d a, Point3d b)
    {
      Vector3d ab = b - a, ap = p - a;
      double l2 = ab * ab;
      double t = l2 == 0 ? 0.0 : Math.Max(0.0, Math.Min(1.0, (ap * ab) / l2));
      return p.DistanceTo(a + ab * t);
    }

    private static Vector3d Unit(Point3d a, Point3d b)
    {
      Vector3d v = b - a;
      v.Unitize();
      return v;
    }

    private static double AngleDeg(Vector3d? prev, Point3d a, Point3d b)
    {
      if (!prev.HasValue) return 0.0;
      double dot = Math.Max(-1.0, Math.Min(1.0, prev.Value * Unit(a, b)));
      return Math.Acos(dot) * 180.0 / Math.PI;
    }

    private static string Run(Curve crv, int minElemIn, double maxDev, double elem, double angMax,
                              bool connectLast, List<Point3d> outPts)
    {
      if (crv == null) return "RESULT: FAILED\nCause: no curve input";
      int nMin = Math.Max(1, minElemIn);
      double total = crv.GetLength();
      double minRun = nMin * elem;

      if (minRun > total)
        return string.Join("\n",
          "RESULT: FAILED",
          "Cause: min elements too large",
          $"  - Minimum run: {nMin} x {elem:F0} = {minRun:F0} mm",
          $"  - Curve length: {total:F0} mm",
          $"Fix: lower min elements to at most {(int)Math.Floor(total / elem)}.");

      // ---- sample the curve once ----
      double[] ts = crv.DivideByLength(Sample, true);
      if (ts == null || ts.Length < 2)
        ts = crv.DivideByCount(Math.Max(2, (int)(total / Sample)), true);
      var X = ts.Select(t => crv.PointAt(t)).ToList();
      if (X[X.Count - 1].DistanceTo(crv.PointAtEnd) > 1e-6) X.Add(crv.PointAtEnd);
      int N = X.Count;
      var S = new double[N];
      for (int i = 1; i < N; i++) S[i] = S[i - 1] + X[i - 1].DistanceTo(X[i]);

      // Try a run of n elements. j is the sample index to continue scanning from.
      (Status st, Point3d end, int j, double dev, double ang) Attempt(Point3d start, int iCur, int j, Vector3d? prevDir, int n)
      {
        double L = n * elem;
        while (j < N && start.DistanceTo(X[j]) < L) j++;
        if (j >= N) return (Status.Short, Point3d.Unset, j, 0.0, 0.0);
        double d0 = start.DistanceTo(X[j - 1]), d1 = start.DistanceTo(X[j]);
        double f = d1 == d0 ? 0.0 : (L - d0) / (d1 - d0);
        Point3d a = X[j - 1], b = X[j];
        Point3d end = a + (b - a) * f;
        double dev = 0.0;
        for (int k = iCur; k <= j; k++) dev = Math.Max(dev, SegDist(X[k], start, end));
        double ang = AngleDeg(prevDir, start, end);
        if (dev > maxDev) return (Status.Dev, end, j, dev, ang);
        if (ang > angMax + 1e-9) return (Status.Ang, end, j, dev, ang);
        return (Status.Ok, end, j, dev, ang);
      }

      outPts.Add(X[0]);
      int iCurrent = 0;
      Point3d startPt = X[0];
      Vector3d? prev = null;
      var counts = new List<int>();
      var devs = new List<double>();
      var angs = new List<double>();
      (Status st, double dev, double ang, double pos)? stop = null;   // failed min-length attempt

      while (counts.Count < MaxSegments)
      {
        (Point3d end, int j, int n, double dev, double ang)? best = null;
        int nn = nMin;
        int jj = iCurrent + 1;
        while (true)
        {
          var r = Attempt(startPt, iCurrent, jj, prev, nn);
          jj = r.j;
          if (r.st == Status.Ok)
          {
            best = (r.end, r.j, nn, r.dev, r.ang);
            nn++;
            continue;
          }
          if (nn == nMin) stop = (r.st, r.dev, r.ang, S[iCurrent]);
          if (r.st == Status.Ang) { nn++; continue; }   // a longer run may fix the angle
          break;
        }
        if (!best.HasValue) break;
        var bst = best.Value;
        prev = Unit(startPt, bst.end);
        outPts.Add(bst.end);
        counts.Add(bst.n);
        devs.Add(bst.dev);
        if (counts.Count > 1) angs.Add(bst.ang);
        startPt = bst.end;
        iCurrent = bst.j - 1;
        stop = null;
      }

      // ---- final short run (fewer than min elements) ----
      if (counts.Count > 0 && connectLast)
      {
        double rem = total - S[Math.Min(iCurrent + 1, N - 1)];
        if (elem * 0.5 < rem && rem < minRun)
        {
          int nLast = Math.Max(1, (int)Math.Floor(rem / elem + 0.5));
          for (int n = Math.Min(nLast, nMin - 1); n > 0; n--)
          {
            var r = Attempt(startPt, iCurrent, iCurrent + 1, prev, n);
            if (r.st != Status.Ok) continue;
            prev = Unit(startPt, r.end);
            outPts.Add(r.end);
            counts.Add(n);
            devs.Add(r.dev);
            angs.Add(r.ang);
            startPt = r.end;
            iCurrent = r.j - 1;
            stop = null;
            break;
          }
        }
      }

      // ---- failure: nothing placed ----
      if (counts.Count == 0)
      {
        outPts.Clear();
        var s = stop ?? (Status.Short, 0.0, 0.0, 0.0);
        string[] cause;
        if (s.st == Status.Dev)
          cause = new[]
          {
            "Cause: deviation too large",
            $"  - Shortest allowed run: {minRun:F0} mm ({nMin} elements)",
            $"  - Deviation from curve: {s.dev:F1} mm",
            $"  - Limit (max deviation): {maxDev:F1} mm",
            $"Fix: raise max deviation to at least {s.dev:F1} mm, or lower min elements.",
          };
        else if (s.st == Status.Ang)
          cause = new[]
          {
            "Cause: angle too large",
            $"  - Angle: {s.ang:F1} deg, limit (max angle): {angMax:F1} deg",
            "Fix: raise max angle or lower min elements.",
          };
        else
          cause = new[]
          {
            $"Cause: no run of {minRun:F0} mm ({nMin} elements) fits",
            "  - The curve is too short or folds back on itself",
            "Fix: lower min elements.",
          };
        return string.Join("\n", new[] { "RESULT: FAILED" }.Concat(cause));
      }

      // ---- success (possibly partial) ----
      double remaining = total - S[Math.Min(iCurrent + 1, N - 1)];
      bool incomplete = remaining > elem * 0.5;
      bool failed = remaining >= minRun;   // unsegmented part is at least one minimum run
      var runLengths = counts.Select(c => c * elem).ToList();
      string perSection = counts.Distinct().Count() == 1
        ? $"{counts[0]} each"
        : string.Join(", ", counts);

      var lines = new List<string>
      {
        "RESULT: " + (failed ? "FAILED" : "OK"),
        $"- Straight sections: {counts.Count}",
        $"- Elements: {counts.Sum()} total ({perSection})",
        $"- Run length: {runLengths.Min():F0} - {runLengths.Max():F0} mm",
        $"- Max angle: {(angs.Count > 0 ? angs.Max() : 0.0):F2} deg (limit {angMax:F1} deg)",
        $"- Max deviation: {devs.Max():F1} mm (limit {maxDev:F1} mm)",
      };
      if (incomplete)
      {
        lines.Add($"- Not segmented: {remaining:F0} mm of {total:F0} mm");
        if (stop.HasValue && stop.Value.st == Status.Dev)
          lines.Add($"  Reason: next run of {nMin} elements deviates {stop.Value.dev:F1} mm (limit {maxDev:F1} mm)");
        else if (stop.HasValue && stop.Value.st == Status.Ang)
          lines.Add($"  Reason: next run of {nMin} elements kinks {stop.Value.ang:F1} deg (limit {angMax:F1} deg)");
        else
          lines.Add($"  Reason: remaining curve is shorter than the minimum run ({minRun:F0} mm)");
      }
      return string.Join("\n", lines);
    }
  }
}
