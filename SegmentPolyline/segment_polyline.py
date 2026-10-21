"""Segment a reference polyline into straight runs of whole elements.

Each straight run is n * elem_len long (n >= min_elem). Runs start and end on the
reference curve. Every run stays within max_deviation of the curve (measured as
closest distance, i.e. perpendicular), and the kink between consecutive runs
never exceeds max_angle. Greedy: each run is as long as the rules allow.

Inputs
    curve          Curve   Reference polyline.
    min_elem       int     Minimum elements per straight run (10 -> 10 * elem_len).
    max_deviation  float   Max distance between runs and the reference curve (mm).
    elem_len       float   Optional. Element length, default 1400 mm.
    max_angle      float   Optional. Max angle between consecutive runs, default 5 deg.

Outputs
    polyline       Curve   Segmented polyline.
    points         Points  Run start/end points (polyline vertices).
    info           Text    Report: sections, max angle, max deviation, failure reason.
"""
import Rhino.Geometry as rg
import math

try: elem_len
except NameError: elem_len = None
try: max_angle
except NameError: max_angle = None

elem = elem_len if elem_len else 1400.0
ang_max = max_angle if max_angle else 5.0
SAMPLE = 25.0           # curve sampling step (mm); smaller = more accurate, slower
MAX_SEGMENTS = 2000


def dist(a, b):
    return math.sqrt((b[0]-a[0])**2 + (b[1]-a[1])**2 + (b[2]-a[2])**2)


def seg_dist(p, a, b):
    abx, aby, abz = b[0]-a[0], b[1]-a[1], b[2]-a[2]
    apx, apy, apz = p[0]-a[0], p[1]-a[1], p[2]-a[2]
    l2 = abx*abx + aby*aby + abz*abz
    t = 0.0 if l2 == 0 else max(0.0, min(1.0, (apx*abx + apy*aby + apz*abz) / l2))
    cx, cy, cz = a[0]+abx*t, a[1]+aby*t, a[2]+abz*t
    return math.sqrt((p[0]-cx)**2 + (p[1]-cy)**2 + (p[2]-cz)**2)


def unit(a, b):
    v = (b[0]-a[0], b[1]-a[1], b[2]-a[2])
    l = math.sqrt(v[0]**2 + v[1]**2 + v[2]**2)
    return (v[0]/l, v[1]/l, v[2]/l)


def angle_deg(prev_dir, a, b):
    if prev_dir is None:
        return 0.0
    v = unit(a, b)
    dot = max(-1.0, min(1.0, prev_dir[0]*v[0] + prev_dir[1]*v[1] + prev_dir[2]*v[2]))
    return math.degrees(math.acos(dot))


def run(crv, min_elem, max_dev):
    if crv is None:
        return [], None, "RESULT: FAILED\nCause: no curve input"
    n_min = max(1, int(min_elem))
    total = crv.GetLength()
    min_run = n_min * elem

    if min_run > total:
        return [], None, "\n".join([
            "RESULT: FAILED",
            "Cause: min_elem too large",
            "  - Minimum run: %d x %.0f = %.0f mm" % (n_min, elem, min_run),
            "  - Curve length: %.0f mm" % total,
            "Fix: lower min_elem to at most %d." % int(total // elem),
        ])

    # ---- sample the curve once ----
    ts = crv.DivideByLength(SAMPLE, True)
    if ts is None or len(ts) < 2:
        ts = crv.DivideByCount(max(2, int(total / SAMPLE)), True)
    P = [crv.PointAt(t) for t in ts]
    if P[-1].DistanceTo(crv.PointAtEnd) > 1e-6:
        P.append(crv.PointAtEnd)
    X = [(p.X, p.Y, p.Z) for p in P]
    N = len(X)
    S = [0.0]
    for i in range(1, N):
        S.append(S[-1] + dist(X[i - 1], X[i]))

    def attempt(start, i_cur, j, prev_dir, n):
        """Try a run of n elements. Returns (status, end, j, dev, ang)."""
        L = n * elem
        while j < N and dist(start, X[j]) < L:
            j += 1
        if j >= N:
            return "short", None, j, 0.0, 0.0
        d0, d1 = dist(start, X[j - 1]), dist(start, X[j])
        f = 0.0 if d1 == d0 else (L - d0) / (d1 - d0)
        a, b = X[j - 1], X[j]
        end = (a[0]+(b[0]-a[0])*f, a[1]+(b[1]-a[1])*f, a[2]+(b[2]-a[2])*f)
        dev = 0.0
        for k in range(i_cur, j + 1):
            dev = max(dev, seg_dist(X[k], start, end))
        ang = angle_deg(prev_dir, start, end)
        if dev > max_dev:
            return "dev", end, j, dev, ang
        if ang > ang_max + 1e-9:
            return "ang", end, j, dev, ang
        return "ok", end, j, dev, ang

    out_pts = [X[0]]
    i_cur, start, prev_dir = 0, X[0], None
    counts, devs, angs = [], [], []
    stop = None        # (status, dev, ang, arc position) of the failed min-length attempt

    while len(counts) < MAX_SEGMENTS:
        best = None
        n = n_min
        j = i_cur + 1
        while True:
            st, end, j, dev, ang = attempt(start, i_cur, j, prev_dir, n)
            if st == "ok":
                best = (end, j, n, dev, ang)
                n += 1
                continue
            if n == n_min:
                stop = (st, dev, ang, S[i_cur])
            if st == "ang":      # a longer run may fix the angle
                n += 1
                continue
            break
        if best is None:
            break
        end, j, n_best, dev, ang = best
        prev_dir = unit(start, end)
        out_pts.append(end)
        counts.append(n_best)
        devs.append(dev)
        if len(counts) > 1:
            angs.append(ang)
        start = end
        i_cur = j - 1
        stop = None

    # ---- failure: nothing placed ----
    if not counts:
        st, dev, ang, pos = stop if stop else ("short", 0, 0, 0)
        if st == "dev":
            cause = [
                "Cause: deviation too large",
                "  - Shortest allowed run: %.0f mm (%d elements)" % (min_run, n_min),
                "  - Deviation from curve: %.1f mm" % dev,
                "  - Limit (max_deviation): %.1f mm" % max_dev,
                "Fix: raise max_deviation to at least %.1f mm, or lower min_elem." % dev,
            ]
        elif st == "ang":
            cause = [
                "Cause: angle too large",
                "  - Angle: %.1f deg, limit (max_angle): %.1f deg" % (ang, ang_max),
                "Fix: raise max_angle or lower min_elem.",
            ]
        else:
            cause = [
                "Cause: no run of %.0f mm (%d elements) fits" % (min_run, n_min),
                "  - The curve is too short or folds back on itself",
                "Fix: lower min_elem.",
            ]
        return [], None, "\n".join(["RESULT: FAILED"] + cause)

    # ---- success (possibly partial) ----
    remaining = total - S[min(i_cur + 1, N - 1)]
    incomplete = remaining > elem * 0.5
    run_lengths = [c * elem for c in counts]
    if len(set(counts)) == 1:
        per_section = "%d each" % counts[0]
    else:
        per_section = ", ".join(str(c) for c in counts)

    lines = [
        "RESULT: %s" % ("INCOMPLETE" if incomplete else "OK"),
        "- Straight sections: %d" % len(counts),
        "- Elements: %d total (%s)" % (sum(counts), per_section),
        "- Run length: %.0f - %.0f mm" % (min(run_lengths), max(run_lengths)),
        "- Max angle: %.2f deg (limit %.1f deg)" % (max(angs) if angs else 0.0, ang_max),
        "- Max deviation: %.1f mm (limit %.1f mm)" % (max(devs), max_dev),
    ]
    if incomplete:
        lines.append("- Not segmented: %.0f mm of %.0f mm" % (remaining, total))
        if stop and stop[0] == "dev":
            lines.append("  Reason: next run of %d elements deviates %.1f mm (limit %.1f mm)" % (n_min, stop[1], max_dev))
        elif stop and stop[0] == "ang":
            lines.append("  Reason: next run of %d elements kinks %.1f deg (limit %.1f deg)" % (n_min, stop[2], ang_max))
        else:
            lines.append("  Reason: remaining curve is shorter than the minimum run (%.0f mm)" % min_run)
    pts = [rg.Point3d(p[0], p[1], p[2]) for p in out_pts]
    return pts, rg.PolylineCurve(pts), "\n".join(lines)


points, polyline, info = run(curve, min_elem, max_deviation)
