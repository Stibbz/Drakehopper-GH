"""Paste this (only this) into the Python 3 script component, then export the .ghuser.

Component inputs : curve, min_elem, max_deviation, elem_len, max_angle, connect_last
Component outputs: polyline, points, info

The real code lives in segment_polyline.py in the repo, so code changes need no
re-export. Only re-export the .ghuser when the inputs/outputs change.
"""
SCRIPT = r"C:\Users\sdraak\source\repos\Drakehopper-GH\SegmentPolyline\segment_polyline.py"

with open(SCRIPT, encoding="utf-8") as f:
    exec(compile(f.read(), SCRIPT, "exec"), globals())
