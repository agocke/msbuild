#!/usr/bin/env python3
"""Top-down instruction tree under a root frame.

usage: perf script -F comm,tid,period,ip,sym,dso | subtree.py <root-regex> [depth] [min-percent] [exclude-regex]

Each sample whose stack contains a frame matching <root-regex> is charged to the path from the
outermost matching frame toward the leaf. Frames matching [exclude-regex] (below the root) remove the
sample, so for example property-element cost can exclude nested condition evaluation. Prints inclusive
shares of the root's total, down to [depth] levels.
"""
import re
import sys
from collections import defaultdict

root = re.compile(sys.argv[1])
depth = int(sys.argv[2]) if len(sys.argv) > 2 else 4
min_pct = float(sys.argv[3]) if len(sys.argv) > 3 else 1.0
exclude = re.compile(sys.argv[4]) if len(sys.argv) > 4 and sys.argv[4] else None

GENERIC = re.compile(r"<[^<>]*>")


def short(frame):
    frame = frame.split(" (")[0]
    while GENERIC.search(frame):
        frame = GENERIC.sub("", frame)
    for prefix in ("Microsoft_Build_Framework_", "Microsoft_Build_", "S_P_CoreLib_", "Microsoft_NET_StringTools_"):
        if frame.startswith(prefix):
            frame = frame[len(prefix):]
            break
    return frame.replace("Microsoft_Build_", "").replace("System_Collections_Generic_", "")[:110]


tree = defaultdict(int)
leaves = defaultdict(int)
LEAF_MODE = __import__("os").environ.get("SUBTREE_LEAVES") == "1"
total = 0
excluded = 0
header = re.compile(r"^(\S.*?)\s+(\d+)\s+(\d+)\s*$")
period = 0
frames = []


def flush():
    global total, excluded
    if not frames:
        return
    idx = None
    for i, f in enumerate(frames):  # innermost first; keep the outermost match
        if root.search(f):
            idx = i
    if idx is None:
        return
    below = frames[:idx + 1][::-1]  # root ... leaf
    if exclude and any(exclude.search(f) for f in below[1:]):
        excluded += period
        return
    total += period
    leaves[short(below[-1])] += period
    path = ()
    for f in below[:depth + 1]:
        path = path + (short(f),)
        tree[path] += period


for line in sys.stdin:
    if not line.strip():
        flush()
        frames = []
        continue
    if not line[0].isspace():
        flush()
        frames = []
        m = header.match(line.rstrip("\n"))
        period = int(m.group(3)) if m else 0
        continue
    parts = line.strip().split(" ", 1)
    frames.append(parts[1] if len(parts) > 1 else parts[0])
flush()

print(f"# root total {total / 1e9:.2f}G e9 (instructions, or CPU seconds for cpu-clock); excluded {excluded / 1e9:.2f}G")


def show(prefix, level):
    children = sorted(((p, v) for p, v in tree.items() if len(p) == len(prefix) + 1 and p[:len(prefix)] == prefix), key=lambda x: -x[1])
    for p, v in children:
        pct = v / total * 100
        if pct < min_pct:
            continue
        print(f"{'  ' * level}{pct:5.1f}%  {p[-1]}")
        if level < depth:
            show(p, level + 1)


if LEAF_MODE:
    for name, v in sorted(leaves.items(), key=lambda x: -x[1])[:40]:
        print(f"{v / total * 100:5.1f}%  {name}")
    sys.exit(0)

roots = sorted(((p, v) for p, v in tree.items() if len(p) == 1), key=lambda x: -x[1])
for p, v in roots:
    print(f"{v / total * 100:5.1f}%  {p[0]}")
    show(p, 1)
