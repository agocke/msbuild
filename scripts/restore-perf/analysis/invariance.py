#!/usr/bin/env python3
"""How much repeated condition and property-element work is invariant across evaluations.

usage: invariance.py <trace-dir>
"""
import glob, os, sys
from collections import defaultdict, Counter
cond = defaultdict(Counter)      # text -> Counter(result)
prop = defaultdict(Counter)      # unevaluated -> Counter(evaluated)
cond_pass = defaultdict(Counter) # pass -> Counter(kind)
cur = None
for f in glob.glob(os.path.join(sys.argv[1], "trace.*.tsv")):
    for line in open(f, encoding="utf-8"):
        k = line[0]
        if k == "S": cur = line[2]
        elif k == "C":
            _, r, c = line.rstrip("\n").split("\t", 2)
            cond[(cur, c)][r] += 1
        elif k == "P":
            p = line.rstrip("\n").split("\t")
            prop[p[2]][p[3] if len(p) > 3 else ""] += 1

def summarize(table, label, buckets):
    total = sum(sum(c.values()) for c in table.values())
    by = Counter()
    for key, c in table.items():
        n = sum(c.values()); d = len(c)
        by[next(b for b, lim in buckets if d <= lim)] += n
    print(f"{label}: {total} executions over {len(table)} distinct texts ({total/len(table):.0f} per text)")
    for b, _ in buckets:
        print(f"   {b:28} {by[b]/total*100:5.1f}%")

summarize(cond, "conditions (text, pass)", [("same result every time", 1), ("both results seen", 2)])
summarize(prop, "property elements", [("one evaluated value", 1), ("2-3 values", 3), ("4-16 values", 16), ("17-100 values", 100), ("more than 100 values", 10**9)])
# Literal values are trivially invariant; separate them out.
nonlit = {k: v for k, v in prop.items() if "$(" in k or "@(" in k}
summarize(nonlit, "property elements with references", [("one evaluated value", 1), ("2-3 values", 3), ("4-16 values", 16), ("17-100 values", 100), ("more than 100 values", 10**9)])
