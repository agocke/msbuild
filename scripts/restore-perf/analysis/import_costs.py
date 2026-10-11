#!/usr/bin/env python3
"""Weight the live-in census by measured per-file costs.

usage: import_costs.py <trace-dir> <imports.pid.tsv>

<trace-dir> is an MSBUILDEVALUATIONTRACE directory; <imports.pid.tsv> is the console process's
MSBUILDIMPORTCOSTPROFILE output (with MSBUILDCOSTLEDGER set). For each import file, the repeat fraction is
the share of its executions that a cache keyed on (file, live-in values) could attach instead of
evaluating; it is applied to the file's measured exclusive cost (its own elements plus resolving and
loading its imports). SDK snapshot directory names are normalized so traces and profiles from different
snapshots join.
"""
import sys, re
sys.path.insert(0, __import__("os").path.dirname(__import__("os").path.abspath(__file__)))
from collections import defaultdict
import census
norm = lambda p: re.sub(r"/sdks/[^/]+/", "/sdks/X/", p)
roots = census.load(sys.argv[1])
groups = defaultdict(list); byfile = defaultdict(list)
for r in roots:
    for s in census.walk(r):
        if s.path.startswith(census.BLOCK): continue
        groups[s.key].append(s)
        byfile[norm(s.path) if s.parent else "<project>"].append(s)
incons = {k for k, ss in groups.items() if len({s.effect for s in ss}) > 1}
prof = {}
for l in open(sys.argv[2]):
    if l.startswith("#") or l.startswith("path\t"): continue
    f = l.rstrip("\n").split("\t"); prof[norm(f[0])] = (int(f[1]), int(f[2]), int(f[3]), int(f[4]))
tot = prof["<project>"][1]
saved = 0; rows = []
for path, (n, incl, excl, res) in prof.items():
    ss = byfile.get(path)
    if not ss or path == "<project>":
        rows.append((0, path, n, excl, 0, 0)); continue
    keys = defaultdict(int)
    for s in ss: keys[s.key] += 1
    evaluated = sum(c if k in incons else 1 for k, c in keys.items())
    frac = 1 - evaluated / len(ss)
    saved += excl * frac
    rows.append((excl * frac, path, n, excl, len(keys), frac))
print(f"measured properties pass {tot/1e9:.1f}G; nested file-level bound attaches {saved/tot*100:.1f}%")
rows.sort(reverse=True)
print(f"{'file':60} {'n':>5} {'excl%':>6} {'keys':>5} {'repeat':>6} {'saved%':>6}")
for sv, p, n, excl, k, fr in rows[:20]:
    q = p if len(p) <= 60 else "..." + p[-57:]
    print(f"{q:60} {n:5} {excl/tot*100:6.1f} {k:5} {fr*100:5.0f}% {sv/tot*100:6.1f}")
print("largest unsaved:")
for sv, p, n, excl, k, fr in sorted(rows, key=lambda r: -(r[3] - r[0]))[:12]:
    q = p if len(p) <= 60 else "..." + p[-57:]
    print(f"{q:60} {n:5} {excl/tot*100:6.1f} {k:5} {fr*100:5.0f}% unsaved {(excl-sv)/tot*100:5.1f}")
