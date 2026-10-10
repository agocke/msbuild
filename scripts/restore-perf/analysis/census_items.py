#!/usr/bin/env python3
"""Item-operation census over an MSBUILDEVALUATIONTRACE directory recorded with operation records.

usage: census_items.py <trace-dir> [top-n]

Each item operation application is keyed by its source location, kind, item type, the hash of the item
list it is applied to, the evaluation directory (relative globs depend on it), the properties it reads,
and the hashes of other item lists it reads. Operations with equal keys should produce the same list:
the census checks that and reports how much item-pass work repeats with identical inputs.

Weights: 42 k instructions per operation plus 10.6 k per condition evaluated inside it, from the Native AOT
console's lazy item application cost (50.6 G over 842 k operations, including 1.44 M conditions). Operation
construction (parsing and expanding item specs, ~30 G) is not included.
"""
import glob
import os
import sys
from collections import defaultdict

OPERATION_COST = 42_000
CONDITION_COST = 10_600


def main():
    trace_dir = sys.argv[1]
    top_n = int(sys.argv[2]) if len(sys.argv) > 2 else 20
    groups = defaultdict(lambda: [0, 0, set()])   # key -> [executions, weight, effects]
    by_site = defaultdict(lambda: [0, 0, set()])  # site -> [executions, weight, keys]
    total = 0
    for path in sorted(glob.glob(os.path.join(trace_dir, "trace.*.tsv"))):
        stack = []
        for line in open(path, encoding="utf-8"):
            kind = line[0]
            if kind == "O":
                _, site, op, item_type, prior, directory = line.rstrip("\n").split("\t")
                stack.append({"key": [site, op, item_type, prior, directory], "reads": [], "conditions": 0})
            elif not stack:
                continue
            elif kind == "R":
                _, name, h = line.rstrip("\n").split("\t")
                stack[-1]["reads"].append(("R", name.lower(), h))
            elif kind == "Q":
                _, item_type, h = line.rstrip("\n").split("\t")
                stack[-1]["reads"].append(("Q", item_type.lower(), h))
            elif kind == "C":
                stack[-1]["conditions"] += 1
            elif kind == "Z":
                op = stack.pop()
                posterior = line.rstrip("\n").split("\t")[1]
                key = tuple(op["key"]) + (tuple(sorted(set(op["reads"]))),)
                weight = OPERATION_COST + op["conditions"] * CONDITION_COST
                g = groups[key]
                g[0] += 1
                g[1] += weight
                g[2].add(posterior)
                site = by_site[op["key"][0] + " " + op["key"][1]]
                site[0] += 1
                site[1] += weight
                site[2].add(key)
                total += weight
            elif kind == "X":
                stack = []

    inconsistent = {k for k, g in groups.items() if len(g[2]) > 1}
    inconsistent_weight = sum(groups[k][1] for k in inconsistent)
    executions = sum(g[0] for g in groups.values())
    # Each consistent group evaluates once; the rest of its executions could be attached.
    attachable = sum(g[1] * (g[0] - 1) / g[0] for k, g in groups.items() if k not in inconsistent)
    print(f"# {executions} operation applications in {len(groups)} input groups at {len(by_site)} sites")
    print(f"# item application work (weighted): {total / 1e9:.1f} G instructions")
    print(f"# groups with inconsistent results: {len(inconsistent)} ({inconsistent_weight / total * 100:.1f}% of work)")
    print(f"# attachable (repeat executions of consistent groups): {attachable / total * 100:.1f}% of work")
    print()
    print(f"{'site and kind':78} {'execs':>6} {'groups':>6} {'incons':>6} {'work %':>7}")
    for site, (n, w, keys) in sorted(by_site.items(), key=lambda kv: -kv[1][1])[:top_n]:
        short = site if len(site) <= 78 else "..." + site[-75:]
        print(f"{short:78} {n:6} {len(keys):6} {sum(1 for k in keys if k in inconsistent):6} {w / total * 100:6.1f}%")


if __name__ == "__main__":
    main()
