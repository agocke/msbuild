# Restore performance scripts

Tools behind the [classic restore performance plan](../../documentation/specs/proposed/restore-performance-plan.md)
and its [measurement record](../../documentation/specs/proposed/evaluation-perf.md). They measure a clean,
package-cached static-graph restore of `dotnet/runtime` with a locally built MSBuild. Linux only.

## Prerequisites

- `perf`, with `sudo sysctl kernel.perf_event_paranoid=1` so kernel instructions are counted. The setting resets
  on reboot unless persisted in `/etc/sysctl.d/`. At 2 the scripts still run but count user mode only, and they say so.
- `python3`, `git`, `bc`, `curl`; `clang` and `zlib` for the Native AOT host.
- ~15 GB under `RESTORE_BENCH` (default `~/restore-bench`) for the runtime clone, packages, and SDK snapshots.

## Workflow

```bash
export RESTORE_BENCH=~/restore-bench           # optional
scripts/restore-perf/setup.sh [local-runtime-clone]   # once: clone runtime at RUNTIME_COMMIT, warm packages

./build.sh -c Release                           # this repo; the bootstrap SDK must match runtime's global.json
scripts/restore-perf/snapshot-sdk.sh base       # freeze artifacts/bin/bootstrap/core as sdks/base
scripts/restore-perf/make-r2r-sdk.sh base base-r2r   # optional: ReadyToRun like the shipped SDK

# Interleaved comparison: label|sdk|environment|msbuild arguments
scripts/restore-perf/matrix.sh pgo 3 "on|base-r2r||" "off|base-r2r|DOTNET_TieredPGO=0|"
```

`setup.sh` also deletes runtime's `GenerateRestoreUseStaticGraphEvaluationBinlog` line from `eng/Build.props`, as
newer runtime main does, so restores run without the console binlog. On a clone set up earlier, rerun `setup.sh`.

`run.sh <sdk> <out>` runs one restore and writes `result.txt`, `cpu.txt`, and `assets.sha256`; build output
stays on the terminal. CPU time includes user and system time for the process tree. Pass a
wrapper such as `perf stat ... --` after the arguments. Environment passes through; `BENCH_ARGS` adds MSBuild
arguments. Runs with the default workload produce 639 assets; check equivalence with `compare-assets.sh`.
Each run also records `assets.normalized.sha256`, replacing only benchmark SDK snapshot directory paths,
so equivalence can be checked after later runs overwrite the assets. Unexplained differences fail comparison.
`matrix.sh` reverses configuration order on alternate repetitions and stops on a failed restore.
Set `PERF_EVENTS=task-clock` on machines without hardware counters; instructions and cycles report `N/A`,
not zero. CPU time remains available from `/usr/bin/time`.

## Which configuration to measure

| Purpose | Configuration |
| --- | --- |
| Wall-time comparisons | ReadyToRun snapshot (`make-r2r-sdk.sh`) with tiered JIT, as shipped, through `matrix.sh` |
| Per-unit accounting (instructions per condition, property element, task) | Native AOT console: no JIT, tiering, or PGO noise, fully symbolized |
| Attribution of the JIT path | `perf-record.sh` (JIT everything); compare shares, not totals, which include JIT work |

Keep Dynamic PGO off for accounting (`DOTNET_TieredPGO=0`): its instrumentation is ~30% of instructions in
the short-lived NuGet console and its counters make instruction counts unstable.

### Machines without hardware counters

On a VM without a virtual PMU everything still runs, measuring time instead of instructions:
`matrix.sh` reports wall and CPU time, the cost ledger records per-scope task clock, `perf-record.sh`
samples `cpu-clock` (analysis totals then read as CPU seconds), and the floor reports CPU nanoseconds.
Use such machines for wall time, parallel scaling, and the serial execution path; do per-unit
instruction accounting where hardware counters exist.

## Engine diagnostics

- `MSBUILDCOSTLEDGER=<prefix>` (and `MSBUILDCOSTLEDGERLEVEL=2`): per-scope instruction/cycle/CPU/allocation ledger
  from each process, `<prefix>.<pid>.tsv`. The file header reports the probe cost per scope.
- `MSBUILDIMPORTCOSTPROFILE=<prefix>` (with `MSBUILDCOSTLEDGER`): per-file inclusive and exclusive cost of the
  properties and imports pass, plus import resolution and loading, `<prefix>.<pid>.tsv`. Weight the live-in
  census by it with `analysis/import_costs.py <trace-dir> <imports.pid.tsv>`.
- `MSBUILDEVALUATIONTRACE=<existing dir>`: condition and property-element streams plus final property tables.
  Analyze with `analysis/trace_stats.py <dir>` and `analysis/invariance.py <dir>`, and replay against the
  reference implementation with `floor/build-and-run.sh <dir>`.

## Profiling

```bash
DOTNET_TieredPGO=0 scripts/restore-perf/perf-record.sh base out/profile
scripts/restore-perf/fetch-runtime-symbols.sh base          # once per SDK: libcoreclr symbols
perf script -i out/profile/perf.data --pid <console pid> -F comm,tid,period,ip,sym,dso > s.txt
analysis/classify.py < s.txt                                # subsystem x operation kind
analysis/subtree.py '<root frame regex>' 3 1.0 < s.txt      # top-down tree (SUBTREE_LEAVES=1: flat leaves)
analysis/kinds_under.py '<root frame regex>' < s.txt        # operation kinds under one construct
```

Find the console PID with `perf report --sort pid,comm`; its main thread is `dotnet` (JIT) or `RestoreAotHost`.
Attribution runs JIT everything (`DOTNET_ReadyToRun=0`) because perf cannot name ReadyToRun or W^X-mapped code.
Keep Dynamic PGO off for accounting.

## Native AOT console

`aot-host/build.sh <snapshot> <dest>` links the NuGet static-graph console, MSBuild, and the restore tasks and
SDK resolvers into one native executable, and installs it in a new snapshot. Run with
`BENCH_ARGS="-p:NuGetConsoleProcessFileName=$RESTORE_BENCH/sdks/<dest>/sdk/<version>/RestoreAotHost"`.
For symbols, copy `aot-host/bin/Release/*/linux-x64/publish/RestoreAotHost.dbg` into perf's build-id cache as `debug`.
The host registers exactly the tasks `dotnet/runtime`'s restore runs; another workload may need more
(an unregistered task fails with MSB4283).
