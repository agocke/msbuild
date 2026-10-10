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
    instructions = (values.get("instructions:u", 0) + values.get("instructions:k", 0)) / 1e9
    cycles = (values.get("cycles:u", 0) + values.get("cycles:k", 0)) / 1e9
    # perf names it task-clock or task-clock:u depending on permissions.
    task_ms = next((v for k, v in values.items() if k.startswith("task-clock")), 0)
    rows[label].append((wall, instructions, cycles, task_ms / 1000))

def sd(xs):
    return st.stdev(xs) if len(xs) > 1 else 0.0

print(f"{'configuration':24} {'wall s':>15} {'instructions':>13} {'cycles':>9} {'CPU s':>8} {'IPC':>5}  runs")
for label, r in rows.items():
    w = [x[0] for x in r]
    i, c, t = (st.mean(x[k] for x in r) for k in (1, 2, 3))
    print(f"{label:24} {st.mean(w):7.2f} ± {sd(w):5.2f} {i:11.1f} G {c:7.1f} G {t:8.1f} {i / c if c else 0:5.2f}  {len(r)}")
if kernel_missing:
    print("WARNING: kernel instructions were not counted (kernel.perf_event_paranoid > 1); totals are user mode only")
