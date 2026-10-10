#!/usr/bin/env python3
"""Per-import live-in census over an MSBUILDEVALUATIONTRACE directory.

usage: census.py <trace-dir> [top-n]

For every import subtree (a file plus everything it imports) and, when the trace has them, every property
group and choose block in the properties and imports pass, the
live-ins are the properties it reads before writing them, with the read value's hash. Two executions of
the same file with the same live-in vector should produce the same effect: the same sequence of applied
property elements and condition results. The census reports how often that holds, and how much of the
pass's work sits in subtrees whose effects repeat across configurations.

Work is weighted by the per-unit costs measured in the Native AOT console (instructions per condition and
per property element, excluding nested conditions). Imports, XML, and SDK resolution are not weighted.
"""
import glob
import hashlib
import os
import sys
from collections import defaultdict

BLOCK = "<block> "   # property group or choose block, keyed like an import subtree

CONDITION_COST = 10_600
PROPERTY_COST = 18_200


class Scope:
    __slots__ = ("path", "parent", "written", "livein", "conditions", "properties", "effect", "children", "key")

    def __init__(self, path, parent):
        self.path = path
        self.parent = parent
        self.written = set()
        self.livein = {}
        self.conditions = 0
        self.properties = 0
        self.effect = hashlib.blake2b(digest_size=8)
        self.children = []
        self.key = None

    @property
    def cost(self):
        return self.conditions * CONDITION_COST + self.properties * PROPERTY_COST


def unescape(s):
    if "\\" not in s:
        return s
    out, i = [], 0
    while i < len(s):
        if s[i] == "\\" and i + 1 < len(s):
            out.append({"t": "\t", "n": "\n", "r": "\r"}.get(s[i + 1], s[i + 1]))
            i += 2
        else:
            out.append(s[i])
            i += 1
    return "".join(out)


def close(scope):
    """Fold a finished subtree into its parent and fix its key and effect."""
    scope.key = (scope.path, tuple(sorted(scope.livein.items())))
    scope.effect = scope.effect.digest()
    parent = scope.parent
    if parent is None:
        return
    for name, h in scope.livein.items():
        if name not in parent.written and name not in parent.livein:
            parent.livein[name] = h
    parent.written |= scope.written
    parent.conditions += scope.conditions
    parent.properties += scope.properties
    parent.effect.update(scope.effect)
    parent.children.append(scope)


def load(trace_dir):
    roots = []
    for path in sorted(glob.glob(os.path.join(trace_dir, "trace.*.tsv"))):
        stack, current_pass = [], None
        for line in open(path, encoding="utf-8"):
            kind = line[0]
            if kind == "E":
                stack = [Scope("<project> " + unescape(line.rstrip("\n").split("\t", 1)[1]), None)]
                current_pass = None
            elif kind == "S":
                new_pass = line[2]
                if current_pass == "P" and new_pass != "P" and stack:
                    while len(stack) > 1:
                        close(stack.pop())
                    close(stack[0])
                    roots.append(stack[0])
                    stack = []
                current_pass = new_pass
            elif not stack or current_pass != "P":
                continue
            elif kind == "I":
                stack.append(Scope(unescape(line.rstrip("\n").split("\t", 1)[1]), stack[-1]))
            elif kind == "B":
                stack.append(Scope(BLOCK + unescape(line.rstrip("\n").split("\t", 1)[1]), stack[-1]))
            elif kind in "JK":
                if len(stack) > 1:
                    close(stack.pop())
            elif kind == "R":
                _, name, h = line.rstrip("\n").split("\t")
                name = name.lower()
                top = stack[-1]
                if name not in top.written and name not in top.livein:
                    top.livein[name] = h
            elif kind == "P":
                parts = line.rstrip("\n").split("\t")
                name = parts[1].lower()
                top = stack[-1]
                top.written.add(name)
                top.properties += 1
                top.effect.update(("P" + name + "=" + (parts[3] if len(parts) > 3 else "")).encode())
            elif kind == "C":
                top = stack[-1]
                top.conditions += 1
                top.effect.update(line[2:3].encode())
    return roots


def walk(scope):
    yield scope
    for child in scope.children:
        yield from walk(child)


def main():
    trace_dir = sys.argv[1]
    top_n = int(sys.argv[2]) if len(sys.argv) > 2 else 25
    roots = load(trace_dir)

    groups = defaultdict(list)          # key -> scopes
    by_path = defaultdict(list)
    for root in roots:
        for s in walk(root):
            groups[s.key].append(s)
            by_path[s.path].append(s)

    total = sum(r.cost for r in roots)
    inconsistent = {k for k, ss in groups.items() if len({s.effect for s in ss}) > 1}
    inconsistent_cost = sum(s.cost for k in inconsistent for s in groups[k] if s.parent is not None)

    # Reuse model: walk each tree top-down and stop at the outermost subtree whose (file, live-in) group
    # repeats and has consistent effects. That subtree is evaluated once per distinct group; the rest of
    # its executions are attached. Work outside chosen subtrees is evaluated as today.
    def reusable(s):
        return s.parent is not None and len(groups[s.key]) >= 2 and s.key not in inconsistent

    chosen = defaultdict(int)  # key -> executions charged
    residual = 0

    def cover(s):
        nonlocal residual
        if reusable(s):
            chosen[s.key] += 1
            return
        own = s.cost - sum(c.cost for c in s.children)
        residual += own
        for c in s.children:
            cover(c)

    for root in roots:
        cover(root)
    once = sum(groups[k][0].cost for k in chosen)            # one evaluation per distinct group
    attached = sum(groups[k][0].cost * (n - 1) for k, n in chosen.items())

    files = [p for p in by_path if not p.startswith(BLOCK) and not p.startswith("<project>")]
    print(f"# {len(roots)} evaluations, {sum(len(by_path[p]) for p in files)} import executions of {len(files)} files")
    print(f"# properties-and-imports work: {total / 1e9:.1f} G instructions (weighted: {CONDITION_COST} per condition, "
          f"{PROPERTY_COST} per property element)")
    block_execs = sum(len(ss) for p, ss in by_path.items() if p.startswith(BLOCK))
    block_groups = sum(1 for k in groups if k[0].startswith(BLOCK))
    print(f"# blocks: {block_execs} executions in {block_groups} (block, live-in) groups")
    print(f"# (file or block, live-in) groups: {len(groups)}; with inconsistent effects: {len(inconsistent)} "
          f"({inconsistent_cost / total * 100:.1f}% of work)")
    print(f"# reuse model (outermost repeating subtree): residual {residual / total * 100:.1f}%, "
          f"evaluated once per group {once / total * 100:.1f}%, attached instead of evaluated {attached / total * 100:.1f}%")

    # Nested bound: every subtree level is keyed, so each distinct (file, live-in) group evaluates its own
    # content once and attaches its children's effects; inconsistent groups and project roots always evaluate.
    evaluated = 0
    for key, ss in groups.items():
        own = ss[0].cost - sum(c.cost for c in ss[0].children)
        if key in inconsistent or ss[0].parent is None:
            evaluated += sum(x.cost - sum(c.cost for c in x.children) for x in ss)
        else:
            evaluated += own
    print(f"# nested bound (every subtree keyed): evaluated {evaluated / total * 100:.1f}%, "
          f"attached {100 - evaluated / total * 100:.1f}%")
    print()
    print(f"{'file':70} {'execs':>6} {'vectors':>7} {'live-ins':>8} {'incons':>6} {'work %':>7}")
    rows = []
    for path, ss in by_path.items():
        if path.startswith("<project>") or path.startswith(BLOCK):
            continue
        keys = {s.key for s in ss}
        rows.append((sum(s.cost for s in ss), path, len(ss), len(keys),
                     max(len(k[1]) for k in keys), sum(1 for k in keys if k in inconsistent)))
    rows.sort(reverse=True)
    for cost, path, execs, vectors, liveins, incons in rows[:top_n]:
        short = path if len(path) <= 70 else "..." + path[-67:]
        print(f"{short:70} {execs:6} {vectors:7} {liveins:8} {incons:6} {cost / total * 100:6.1f}%")
    projects = [r for r in roots]
    project_keys = {r.key for r in projects}
    print()
    print(f"# project roots: {len(projects)} executions, {len(project_keys)} distinct (project, live-in) vectors")


if __name__ == "__main__":
    main()
