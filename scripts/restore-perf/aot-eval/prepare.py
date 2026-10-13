#!/usr/bin/env python3
"""Prepare a benchmark-only overlay; never edit engine sources in the caller's checkout."""
import json
from pathlib import Path
import subprocess
import sys


def main():
    source, work = (Path(arg).resolve() for arg in sys.argv[1:])
    if work.exists():
        raise SystemExit(f"Refusing to reuse {work}; choose a fresh work directory.")
    work.mkdir(parents=True)
    tree = work / "worktree"
    subprocess.run(
        ["git", "-C", str(source), "worktree", "add", "--detach", str(tree), "HEAD"],
        check=True,
    )
    # Include the current tracked changes, not just HEAD, in the experimental engine.
    patch = subprocess.check_output(["git", "-C", str(source), "diff", "HEAD", "--binary"])
    if patch:
        subprocess.run(["git", "-C", str(tree), "apply", "--binary", "-"], input=patch, check=True)

    original = tree / "src/Build/Instance/ProjectInstance.cs"
    text = original.read_text(encoding="utf-8-sig")
    anchor = """        public static ProjectInstance FromFile(string file, ProjectOptions options)
        {
"""
    if text.count(anchor) != 1:
        raise SystemExit("ProjectInstance.FromFile changed; review the benchmark hook before building.")
    text = text.replace(
        anchor,
        anchor
        + """            if (RestoreBenchmarkEvaluation.TryLoad(file, options, out ProjectInstance evaluated))
            {
                return evaluated;
            }

""",
        1,
    )
    (work / "ProjectInstance.cs").write_text(text)
    item = (tree / "src/Build/Instance/ProjectItemInstance.cs").read_text(encoding="utf-8-sig")
    anchor = "                translator.Translate(ref _definingFileEscaped);\n"
    if item.count(anchor) != 1:
        raise SystemExit("TaskItem translation changed; review the benchmark wire overlay before building.")
    # Node execution normally supplies the working directory. Graph construction needs it earlier.
    item = item.replace(anchor, anchor + "                translator.Translate(ref _projectDirectory);\n", 1)
    (work / "ProjectItemInstance.cs").write_text(item)
    (work / "source.json").write_text(json.dumps({
        "head": subprocess.check_output(
            ["git", "-C", str(source), "rev-parse", "HEAD"], text=True
        ).strip(),
        "working_tree_patch": bool(patch),
    }, indent=2) + "\n")


if __name__ == "__main__":
    main()
