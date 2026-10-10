#!/usr/bin/env python3
"""Summarize matrix.sh results: mean and standard deviation per configuration.

usage: summarize.py <results-dir>   (directories named <label>-<rep> with result.txt and perf-stat.csv)
"""
import collections
import glob
import os
import statistics as st
import sys

rows = collections.defaultdict(list)
kernel_missing = False
for stat in sorted(glob.glob(os.path.join(sys.argv[1], "*", "perf-stat.csv"))):
    run = os.path.dirname(stat)
    label = os.path.basename(run).rsplit("-", 1)[0]
    values = {}
    for line in open(stat):
        parts = line.strip().split(",")
        if len(parts) > 3 and not line.startswith("#"):
            try:
                values[parts[2]] = float(parts[0])
            except ValueError:
                pass
    if "<not supported>,,instructions:k" in open(stat).read():
        kernel_missing = True
    result = open(os.path.join(run, "result.txt")).read()
    wall = float(result.split("wall=")[1].split()[0])
    instruction_values = [v for k, v in values.items() if k.startswith("instructions")]
    cycle_values = [v for k, v in values.items() if k.startswith("cycles")]
    instructions = sum(instruction_values) / 1e9 if instruction_values else None
    cycles = sum(cycle_values) / 1e9 if cycle_values else None
    fields = dict(field.split("=", 1) for field in result.split())
    if fields["exit"] != "0":
        raise SystemExit(f"Restore failed: {run}")
    if "user" in fields and "system" in fields:
        cpu = float(fields["user"]) + float(fields["system"])
    elif "user" not in fields and "system" not in fields:
        task_times = [v for k, v in values.items() if k.startswith("task-clock")]
        if len(task_times) != 1:
            raise SystemExit(f"No CPU timing available: {run}")
        cpu = task_times[0] / 1000
    else:
        raise SystemExit(f"Incomplete CPU timing: {run}")
    rows[label].append((wall, instructions, cycles, cpu))

hardware = any(x[1] is not None for r in rows.values() for x in r)


def sd(xs):
    return st.stdev(xs) if len(xs) > 1 else 0.0

if not hardware:
    print("no hardware counters: instructions, cycles, and IPC are unavailable")
print(f"{'configuration':24} {'wall s':>15} {'instructions':>13} {'cycles':>9} {'CPU s':>15} {'IPC':>5}  runs")
for label, r in rows.items():
    w = [x[0] for x in r]
    t = [x[3] for x in r]
    i, c = (
        st.mean(x[k] for x in r) if all(x[k] is not None for x in r) else None
        for k in (1, 2)
    )
    instructions = f"{i:11.1f} G" if i is not None else f"{'N/A':>13}"
    cycles = f"{c:7.1f} G" if c is not None else f"{'N/A':>9}"
    ipc = f"{i / c:5.2f}" if i is not None and c else f"{'N/A':>5}"
    print(f"{label:24} {st.mean(w):7.2f} ± {sd(w):5.2f} {instructions} {cycles} {st.mean(t):7.1f} ± {sd(t):5.1f} {ipc}  {len(r)}")
if hardware and kernel_missing:
    print("WARNING: kernel instructions were not counted (kernel.perf_event_paranoid > 1); totals are user mode only")
