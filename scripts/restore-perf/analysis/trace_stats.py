#!/usr/bin/env python3
"""Characterize an MSBUILDEVALUATIONTRACE directory: condition and property-element shapes and reuse.

usage: trace_stats.py <trace-dir>
"""
import glob, os, re, sys
from collections import Counter
cond = Counter(); shapes = Counter(); pshapes = Counter(); pass_c = Counter(); punique = Counter()
prop_re = re.compile(r"\$\(")
def shape(s):
    k = []
    if "$(" in s: k.append("P")
    if "@(" in s: k.append("I")
    if "%(" in s: k.append("M")
    if "::" in s or re.search(r"\$\([A-Za-z_][\w]*\.", s): k.append("F")
    if re.search(r"(?i)\bexists\s*\(|hastrailingslash\s*\(", s): k.append("X")
    if re.search(r"(?i)\b(and|or)\b", s): k.append("B")
    return "".join(k) or "lit"
cur = None
for f in glob.glob(os.path.join(sys.argv[1], "trace.*.tsv")):
    for line in open(f, encoding="utf-8"):
        k = line[0]
        if k == "S": cur = line[2]
        elif k == "C":
            _, r, c = line.rstrip("\n").split("\t", 2)
            cond[c] += 1; shapes[shape(c)] += 1; pass_c[cur] += 1
        elif k == "P":
            parts = line.rstrip("\n").split("\t")
            v = parts[2]; pshapes[shape(v)] += 1; punique[v] += 1
n = sum(cond.values())
print(f"conditions: {n}, unique strings: {len(cond)}, top-100 cover {sum(v for _,v in cond.most_common(100))/n*100:.1f}%")
print("by pass:", dict(pass_c))
print("condition shapes:", [(s, f"{v/n*100:.1f}%") for s, v in shapes.most_common(12)])
pn = sum(pshapes.values())
print(f"property elements: {pn}, unique unevaluated values: {len(punique)}")
print("property value shapes:", [(s, f"{v/pn*100:.1f}%") for s, v in pshapes.most_common(12)])
print("top conditions:")
for c, v in cond.most_common(15): print(f"  {v:7d}  {c[:150]}")
