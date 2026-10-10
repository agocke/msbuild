# Evaluation performance investigations

The measurement record behind the
[classic restore performance plan](restore-performance-plan.md). The plan
states conclusions and next steps; this page keeps the methods and data.
Experiments that were removed are in
[evaluation performance dead ends](evaluation-performance-dead-ends.md).

The workload is always a clean, package-cached static-graph restore of
`dotnet/runtime` with SDK `11.0.100-rc.1.26420.103`. Numbers come from two
machines and several workload epochs. **Do not compare absolute numbers
across sections.** Each section names its machine and its controls.

- [Instruction-level accounting](#instruction-level-accounting) (October
  2026, 8-core Ryzen 7 1800X): the current baseline.
- [Cost-model accounting](#cost-model-accounting) (16-core Xeon Platinum
  8370C, Release engine).
- [16-core configuration ladder](#16-core-configuration-ladder): independent
  hosting controls and supplemental CPU-service accounting.
- [Earlier profiling](#earlier-profiling).
- [Specialization experiments](#specialization-experiments): opt-in
  specializations, measured with Debug engine assemblies.

## Instruction-level accounting

### Harness and tools

The scripts are in [`scripts/restore-perf`](../../../scripts/restore-perf/README.md), parameterized for
any machine; this section summarizes them.

- **Machine:** 8 cores/16 threads (Zen 1), Fedora, with
  `kernel.perf_event_paranoid=1` so kernel instructions are counted.
- **Workload:** a local clone of `dotnet/runtime` at `3b5416d` restores
  through Arcade's `Build.proj`. `artifacts/obj` is deleted before each run
  and the isolated package cache is kept. MSBuild Server, node reuse, the CLI
  telemetry logger, and the outer binlog are off. Every run must produce 639
  `project.assets.json` files.
- **Engines:** each candidate is a frozen copy of `artifacts/bin/bootstrap/core`.
  Both repositories pin the same SDK, so nothing floats.
- **Units:** instructions retired (user and kernel) are the primary unit;
  wall time is for macro results.

Tools:

- `MSBUILDCOSTLEDGER=<prefix>` writes `<prefix>.<pid>.tsv` from every process
  that loads the engine. It records per-scope counts plus inclusive and
  exclusive instructions, cycles, task clock, wall time, and allocated bytes,
  and adds per-thread totals with unscoped work. `MSBUILDCOSTLEDGERLEVEL=2`
  adds the fine-grained scopes. Each scope boundary costs one `read()` of a
  perf_event group: ~3.0 k instructions inside the scope and ~3.4 k charged to
  its parent, as calibrated in the file header. Request continuations that
  resume on another request-builder thread carry their open frames. Any other
  thread hop is reported as `migrated`.
- `MSBUILDEVALUATIONTRACE=<dir>` records, per evaluation, every condition
  evaluation with its result, every applied property element (name,
  unevaluated value, evaluated value), pass markers, and the final property
  table.
- **Method attribution:** use `perf record -e instructions -c 1000003
  --call-graph fp` with `DOTNET_PerfMapEnabled=1` and
  `DOTNET_EnableWriteXorExecute=0`. With W^X on, JIT code runs from a memfd
  mapping and perf never consults the perf map. `libcoreclr.so` is stripped;
  fetch its debug file from the symbol server by build ID and place it as
  `debug` in the perf build-id cache. Plain symbol-table lookups misattribute
  this LTO-built runtime.

### Runtime configuration and hosting

Interleaved runs, three per cell (mean ± standard deviation):

| Engine assemblies | Dynamic PGO | Wall | Instructions | CPU s |
| --- | --- | ---: | ---: | ---: |
| IL (bootstrap as built) | on | 32.86 ± 0.58 s | 577.9 G | 213.2 |
| IL | off | 24.25 ± 0.16 s | 337.1 G | 144.9 |
| ReadyToRun (crossgen2, as shipped) | on | 33.41 ± 0.44 s | 642.3 G | 225.3 |
| ReadyToRun | off | 23.21 ± 0.27 s | 327.1 G | 142.3 |
| ReadyToRun | off in `NuGet.Build.Tasks.Console` only | 23.06 s (22.7-23.4) | ~300 G user | 140.9 |

The stock SDK takes 34.5 s. With PGO on, `JIT_CountProfile32` and
`JIT_ClassProfile32` are ~23% of all instructions, and their thread-local
lookups (`__tls_get_addr`) another ~6%. The whole PGO cost is in the NuGet
console, which starts fresh for each restore; disabling PGO through that
process's runtime config recovers it. Precompiling the bootstrap with
ReadyToRun changes results by about one second, so IL bootstrap
measurements are representative once PGO is controlled.

`dotnet/runtime` sets `GenerateRestoreUseStaticGraphEvaluationBinlog`, so
the NuGet console writes a binlog. ReadyToRun SDK, three runs per cell:

| Console binlog | PGO | Wall | Instructions | CPU s |
| --- | --- | ---: | ---: | ---: |
| on | on | 33.73 ± 0.86 s | 625.6 G | 224.4 |
| off | on | 27.75 ± 0.17 s | 825.1 G | 280.4 |
| on | off | 22.68 ± 0.56 s | 325.6 G | 140.5 |
| off | off | 16.64 ± 0.13 s | 289.5 G | 128.2 |

The binlog costs ~6 s of wall time but only ~36 G instructions, so its writer
is on a serial path. With PGO on, removing the binlog *raises* instructions,
probably because more threads contend on the shared profiling counters.
Instruction counts with PGO on are not a reliable unit.

### Native AOT console

A Native AOT host executable links `NuGet.Build.Tasks.Console`, MSBuild, the
NuGet libraries, the restore tasks, and both SDK resolvers. It registers the
ten tasks restore runs (`Task.RegisterTask<T>`) and the NuGet and workload
SDK resolvers (`SdkResolver.Register`), then forwards to the console's
`Program.Main`. Restore launches it with
`-p:NuGetConsoleProcessFileName=<exe>`, so no NuGet or SDK change is needed.
It must live in `sdk/<version>/` because the workload resolver derives the SDK
version from its own directory.

It uses the closed-world feature switches from `src/aot-validation`, with
one exception: `RestrictPropertyFunctionReceivers` must stay `false`.
`dotnet/runtime` dots into `Match.Groups` in `eng/Versions.props` and into
`DirectoryInfo.Name` in its coreclr `Directory.Build.targets`, and the
restricted receiver set rejects both. Keeping JIT semantics requires rooting
the reflected framework surface (`System.IO`, regex, and common value types).
Server GC must be linked explicitly (`ServerGarbageCollection=true`), because
Native AOT picks the GC flavor at link time; with the workstation GC the
restore took ~37 s on allocation-lock contention. One engine bug was fixed:
`ToolLocationHelper.GetDefaultSDKDiskRoots` added the empty AOT
`FrameworkBasePath` as a disk root and threw.

All 639 assets match the JIT console except for the embedded SDK directory.
Binlog and PGO off, three interleaved pairs:

| Console | Wall | Instructions | Cycles | CPU s |
| --- | ---: | ---: | ---: | ---: |
| JIT (ReadyToRun + tiering) | 16.85 ± 0.29 s | 285.9 G | 448.2 G | 127.5 |
| Native AOT (ILC) | 13.83 ± 0.26 s | 274.4 G | 360.6 G | 101.8 |

The gain is code quality: 4% fewer instructions, 20% fewer cycles (IPC 0.64
to 0.76), and no JIT thread.

### Accounting on the Native AOT console

Perf, filtered to the console process, binlog off: 264 G instructions.

| Subsystem | Instructions | Share | Largest leaf kinds |
| --- | ---: | ---: | --- |
| Item evaluation | 56.9 G | 21.6% | hash/lookup 17%, path/fs 11%, string scan 10% |
| Property evaluation | 40.0 G | 15.1% | hash/lookup 17%, kernel 13%, path/fs 9% |
| Server/background GC threads | 40.0 G | 15.1% | |
| Condition evaluation | 36.2 G | 13.7% | hash/lookup 20%, string scan 9% |
| NuGet's own code | 27.1 G | 10.3% | LINQ 12%, kernel 10% |
| File globbing (`FileMatcher`) | 16.3 G | 6.2% | regex interpreter 83% |
| Other evaluation | 13.7 G | 5.2% | cast/dispatch 17%, XML 11% |
| Imports, graph, execution, logging | ~34 G | ~13% | |

Evaluation is ~179 G, or ~109 M instructions per configuration, the same as
under the JIT. Subsystems come from the innermost recognizable stack frame;
operation kinds come from the leaf frames.

The level-2 ledger gives unit counts in the same image. Its probes inflate
evaluation from 170.8 to 266.1 G (~13.5 M scopes), so the prices below
subtract the calibrated probe cost and are cross-checked against level 1:

| Unit | Count | Instructions per unit (approximate) |
| --- | ---: | ---: |
| Condition evaluation | 3.78 M | ~11-12 k |
| Item operation application, excluding nested scopes | 841 k | ~27-30 k |
| Item spec construction | 844 k | ~11 k |
| `UsingTask` registration | 208 k | ~21 k |
| Metadata analysis | 839 k | ~5.4 k |
| Task parameter binding, per task | 16 k | ~123 k |
| Intrinsic task | 29 k | ~76 k |

The profile-based prices used in the plan are 42.4 G over 3.99 M traced
conditions (~10.6 k each) and 43.9 G over 2.41 M property elements,
excluding nested conditions (~18.2 k each).

### Condition and property-element breakdown

Top-down over the AOT profile under condition evaluation (42.4 G):

| Condition evaluation | Share |
| --- | ---: |
| General expansion machinery (scan for `$(`, find closing parentheses, build strings) | 35% |
| Expression-node logic | 15% |
| Property lookup | 7%, plus 5% other hashing |
| Unix path heuristic on operands (`MaybeAdjustFilePath`) | 6% |
| Kernel, mostly `Exists` | 5% |
| Numeric parse attempts on equality operands | 5% |
| Cast/dispatch, write barriers, allocation | 9% |
| Parse-cache lookup (full-string hash), pool lock, `Type.Name` reflection | 7% |

Property elements spend 16% in AOT reflection, 90% of it `Type.InvokeMember`
from late-bound property functions. The
`MSBuildLogPropertyFunctionsRequiringReflection` log shows 37,895 late-bound
calls per restore, ~23 per configuration, each ~137 k instructions under AOT:

| Function | Calls |
| --- | ---: |
| `System.IO.File.ReadAllText` | 6,578 |
| `Regex.IsMatch`, `Regex.Match` (two overloads) | 6,604 |
| `Version.Build`, `Version.Revision` | 3,384 |
| `Match.Groups`, `GroupCollection[string]`, `Group.Value` | 9,876 |
| `String.Compare` | 1,646 |
| `RuntimeInformation.RuntimeIdentifier`, `.ProcessArchitecture`, `Architecture.ToString` | 4,932 |
| `File.Exists` | 1,644 |
| `ToolLocationHelper.GetPlatformSDKDisplayName`, `GetPlatformSDKLocation`, `GetPathToStandardLibraries` | 3,071 |
| `Directory.GetParent`, `DirectoryInfo.Name` | 160 |

This branch had also made every default-path property element build two
observation strings for the module read tracker, even when it was not
recording. That is fixed.

### Executable floor for conditions and property elements

The trace of one restore contains 3.99 M condition evaluations over 2,752
distinct texts, and 2.41 M property elements over 1,926 distinct values:
54% literal, 32% plain `$(P)`, 13% with functions. A reference
implementation compiled with ILC replays it:

- **Preparation:** each distinct condition is parsed into a tree, and each
  value into literal and `$(name)` segments, with names resolved to integer
  ids. This costs 112 M instructions in total, ~17 per operation amortized.
- **Replay:** each configuration starts from its initial properties (final
  values of names no element writes), applies elements in order, and
  evaluates each condition against that state.
- **Validation:** 65 of 2.59 M checked conditions disagree with the recorded
  result, all from engine-set properties the trace does not reconstruct.
- **Coverage:** literal, property, metadata, and `and`/`or` shapes, which are
  86% of conditions and 86% of property elements. Elements with functions or
  item references are applied with their recorded values. Metadata
  references read a synthetic per-item table.

| Instructions per unit | MSBuild (AOT, all shapes) | Floor, dictionary state | Floor, slot state |
| --- | ---: | ---: | ---: |
| Condition | ~10.6 k | 782 | 500 |
| `'$(P)' op 'literal'` | | 714 | 391 |
| `and`/`or` of property comparisons | | 1,514 | 921 |
| Property element | ~18.2 k | 929 | 231 |
| Initial state per configuration | | 54 k | 5.9 k |

The dictionary variant uses a case-insensitive `Dictionary<string, string>`,
comparable to `PropertyDictionary`. The slot variant indexes values by
statically resolved name id. The MSBuild figures include the function and
`Exists` shapes the floor skips, so ratios for simple shapes are somewhat
lower. The floor is untuned: for example, it classifies operands as
boolean or numeric at run time even when one side is a static literal.

### Amplification and invariance

Across the 1,646 traced configurations:

| | Executions per distinct text | Same outcome in every configuration |
| --- | ---: | ---: |
| Conditions (text and pass) | 1,393 | 61.8% of executions |
| Property elements | 1,251 | 88.5% |
| Property elements with `$(...)` or `@(...)` references | 917 | 74.9%; 82.7% at most three values |

An identical outcome does not prove independence from incoming state, so the
live-in census keys on reads. In the properties and imports pass, each import
subtree, property group, and choose block is keyed by its source and its
live-ins: the properties it reads before writing them, with value hashes. Each
item operation is keyed by site, kind, item type, the list it is applied to,
the properties and other item lists it reads, and the evaluation directory.
Groups with equal keys must produce equal effects, which validates the key.
Work is weighted at the AOT per-unit costs (`analysis/census.py`,
`analysis/census_items.py`).

| | Groups | Inconsistent groups | Attachable work |
| --- | ---: | ---: | ---: |
| Import subtrees only (248,247 executions of 475 files) | 51,512 | 9 (1.9% of work) | 55% greedy, 59% nested bound |
| Import subtrees and blocks (+968,667 block executions) | 115,489 | 10 (2.7%) | 79% greedy, **86%** nested bound |
| Item operations (842,193 applications at 20,478 sites) | 326,392 | 3,642 (1.5%) | **60%** |

"Greedy" attaches the outermost repeating subtree; the nested bound keys every
level. At file granularity, files split three ways. Some are invariant across
the workload: Arcade's `DefaultVersions.props` and `eng/Versions.props` each
have one live-in vector. The props chain (`Sdk.props`, `Microsoft.Common.props`,
`Directory.Build.props`) has about one vector per project file, independent of
target framework. The SDK `.targets` chain is unique per configuration as a whole
(`Sdk.targets`: 1,637 vectors for 1,646 executions, 747 live-ins), but most of its
blocks repeat. One item site, the `ProjectToBuild` `Update` in `eng/Subsets.props`
with six metadata conditions per item, is 18.8% of item-operation work.

The census assumes attaching an effect and checking a key are cheap. Item keys
need an O(1) identity for the list an operation applies to, not the census's
O(n) hash. Item operation construction (~30 G) is not covered.

### Property-group effect attachment prototype

`MSBUILDATTACHPROPERTYGROUPS=1` (experimental, off by default) records each
eligible property group's effect once per distinct key: its element, the
global-property status of its properties, and its live-in values. Later
evaluations with an equal key replay the recorded writes through the same
`SetProperty` calls, so property objects, reassignment logging, and write
notifications are unchanged. A group is eligible when it uses only `$(Name)`
references and simple conditions, contains no backslash, and reads no value
containing a backslash. Backslashes make the Unix path heuristic consult the
filesystem; without them the heuristic cannot change a value. The evaluator
falls back whenever BuildCheck, property-read tracking, uninitialized-property
warnings, design-time evaluation, the profiler, compiled modules, or tracing
is active.

| Per restore (1,643 configurations) | Count |
| --- | ---: |
| Property groups attached | ~569,000 |
| Writes replayed | ~804,000 |
| Misses (first evaluation of a key) | ~26,700 |
| Ineligible groups | 348,726 (37%) |

`MSBUILDATTACHPROPERTYGROUPSVERIFY=1` evaluates every hit normally and compares:
**0 mismatches in 569,111 hits**. Assets are identical to attachment off.

| Attachment | Properties and imports pass | Evaluation total | Restore wall (3 pairs) |
| --- | ---: | ---: | ---: |
| Off | 69.9 G | 166.5 G | 17.18 ± 0.18 s |
| On | 64.4 G (-7.9%) | 162.6 G | 17.13 ± 0.23 s |

Instructions are user mode (ledger, one run each). Attachment works and saves
~9.7 k instructions per attached group, but the eligible groups are the
pass's cheap ones. Classifying the trace's group work with the same rules
(census weights): eligible 53%, property functions 24%, backslashes 11.5%,
instance functions and other `$()` forms 9%, condition functions 1%. Census
weights use average per-element costs, which the function-calling elements
dominate, so they overstate the eligible share. The remaining saving needs
eligibility for property functions (pure functions as part of the key, and
filesystem-dependent ones with their observations as inputs) and for
backslash paths (the heuristic's directory probes as inputs).

### Widened property-group reuse: local implementation and economics

The property-group prototype now permits a bounded set of pure property
functions in values, including string, regex-match/group, version, arithmetic,
lexical path, and selected platform functions. Unsupported or nondeterministic
functions invalidate the recorded variant. Conditions remain conservative.
One-argument `File.ReadAllText`, `File.Exists`, `Directory.Exists`, and Unix
path-adjustment results are observed again before replay; changed observations
miss and normal evaluation replaces the variant. Read-only culture identity
is part of the key, and observation-bearing variants also require the same
effective working directory. Mutable cultures and active read-side diagnostics
fall back. The feature remains off by default and still calls `SetProperty`
for every recorded operation: this is interpretation reuse, not shared-state
attachment or item reuse.

The local controls use the same 639-asset runtime commit and Xeon/WSL2 as the
configuration ladder. The original prototype was built separately from
`2b126d82f`; the widened engine was frozen from the working tree. Only
`Microsoft.Build.dll` differs between matching Release/ReadyToRun snapshots.
Console PGO and binlogs are off. Snapshot hashes and source-file hashes are
recorded in the session's `property-group-reuse-validation/sdk-manifest.json`.
The original and widened smoke captures attach 569,311 and 697,388 groups,
respectively, replaying 805,276 and 1,095,836 operations. Widened verification
finds **zero mismatches in 697,358 hits** against ordinary evaluation.

Four balanced, unprobed repetitions per cell:

| Engine and attachment | Mean restore wall s | Mean process-tree CPU s |
| --- | ---: | ---: |
| Original, off | 15.375 | 141.093 |
| Original, on | 15.766 | 141.240 |
| Widened, on | 15.859 | 139.848 |
| Widened, off | 15.871 | 144.423 |

There is **no demonstrated macro win**. Original-on to widened-on paired
wall savings are -0.149, -0.211, -0.208, and +0.197 s; mean -0.093 s.
The widened-off cell includes a 17.095 s / 158.42 CPU s run, so its mean
must not be used to claim an attachment win. All captures match 639 assets.

Two balanced pairs of separate level-1 task-clock ledgers measure
properties/imports at **38.170 -> 35.150 CPU s**, saving 1.400 and 4.640 s
in the pairs (mean 7.9%). Total evaluation is 79.566 -> 76.073 CPU s.
Selected console ledgers have equal scope counts, 1,643 evaluations, and
zero migration, abandonment, multiplexing, or depth overflow. Invalid outer
Arcade frames are excluded. However, separate phase-hook captures do **not**
replicate the properties-pass result in both pairs: their same-capture
properties CPU changes are 40.597 -> 36.917 and 39.990 -> 43.404 s.
The slower widened run also increases the other evaluation phases.
Graph elapsed in those captures averages 5.751 -> 6.201 s, and extraction
6.996 -> 7.033 s. The two-pair ledger result is evidence of a local benefit,
not a stable prediction of graph elapsed or proof of its cause.

To separate the reuse economics from graph parallelism, two more balanced
pairs run the **same widened engine off/on** with the child process pinned
to one logical CPU and `DOTNET_PROCESSOR_COUNT=1`. This changes graph and
runtime/GC concurrency and is a diagnostic control, not a production timing:

| Single-CPU diagnostic | Attachment off | On |
| --- | ---: | ---: |
| Properties/imports thread CPU s | 22.606 | 20.580 |
| Total evaluation thread CPU s | 47.770 | 46.430 |
| Graph elapsed s | 53.323 | 51.900 |
| Graph allocated bytes, decimal GB | 9.002 | 8.580 |
| Graph GC pause s | 5.349 | 5.329 |

The properties pass improves in both pairs by 2.100 and 1.952 CPU s
(9.0% mean), but total evaluation improves only 2.8%. One pair's graph
elapsed is slightly worse, the other substantially better. Item-pass CPU
increases in both pairs; the cause has not been established. Allocation
reduction does not buy a corresponding GC-pause reduction. Thus parallelism
alone does not explain the small saving: the current implementation removes
only a modest part of evaluation, even in the serial control.

Three diagnostic user-CPU profiles, one each for widened-off, original-on,
and widened-on, use software `cpu-clock` sampling at 999 Hz with ReadyToRun
off, console PGO off, and JIT symbols. They are attribution captures, **not**
additional macro timings. Visible stacks give this disjoint decomposition
inside `EvaluatePropertyGroupElement`:

| Approximate sampled CPU s | Widened off | Original on | Widened on |
| --- | ---: | ---: | ---: |
| Remaining ordinary property-element evaluation | 25.45 | 19.69 | 16.23 |
| Live-in lookup and observation validation | 0 | 0.89 | 1.62 |
| Cache publication | 0 | 0.76 | 1.19 |
| Entry lookup and global-override mask | 0 | 0.70 | 0.87 |
| Recorded-operation replay | 0 | 1.13 | 1.41 |
| Other group work | 2.56 | 1.78 | 1.61 |
| Total group subtree | 28.02 | 24.95 | 22.93 |

These are statistical stack attributions, not exact or matched per-block
timers; inlining, unresolved frames, JIT, and scheduling limit comparison.
Nested inclusive method totals must not be added to this table. Nevertheless,
they reject the explanation that replay alone consumes all the potential:
replay is only about 6% of the widened group subtree. Lookup, publication,
and entry/global checks cost more together. Ordinary evaluation still
accounts for about **71%** of that subtree, despite the high group hit count.
The roughly 1.6 recorded operations per hit are not a large import-subtree
attachment. The entire item pass remains outside this cache.

Untouched costs also fluctuate: the profiled item pass is 38.480 CPU s off
versus 45.607 s widened-on; sampled globbing grows from about 5.6 to 10.9
user CPU s, and GC-thread samples from 17.8 to 20.0 s. These differences
explain why a reduction in property work need not reduce whole-process CPU,
but do not prove an optimization-induced slowdown. The remaining gap needs
cost-weighted hit/fallback accounting by source block and a same-capture
graph timeline, including item/glob cache activity, GC, JIT, and scheduling.
The census's 86% is still a potential for cheap file/block effect attachment,
not the coverage or predicted saving of the current property-group cache.

Raw local evidence is in the session's `configuration-ladder-xeon/results/`
under `reuse-smoke-v1`, `reuse-matrix-v1`, `reuse-service-v1`,
`reuse-economics-serial-v1`, and `reuse-economics-profiles-v1`;
`cost-attribution.json` retains the sampled decomposition and top leaves.
Hardware instruction/cycle counters are unsupported. Do not convert these
CPU seconds to instruction counts or add them to the historical AOT results.

### Default-path read-tracker fixes

Two allocations this branch had added to every property element and every
property lookup were removed. Same snapshot with only `Microsoft.Build.dll`
swapped, binlog and PGO off, three interleaved pairs:

| Engine | Wall | Instructions | CPU s |
| --- | ---: | ---: | ---: |
| Before the fixes (`28d5b400f`) | 17.20 ± 0.01 s | 297.4 G | 125.4 |
| After (`5d9fdf2bb`, also with the inactive trace hooks) | 16.91 ± 0.13 s | 300.8 G | 124.0 |

The instruction difference is noise: user-mode instructions varied by up to
10 G (~3.5%) between runs of one configuration, from GC and spin-waiting that
depend on scheduling. The fixes do not explain the default path's extra CPU.

### JIT console profile

The same classification over the JIT console's PGO-off profile (338 G) adds
two things to the AOT picture. The console binlog writer was 8.8%, 39% of it
hash/lookup. The tiered-JIT thread was 4.9%. Hash/lookup is again the largest
identifiable kind (16.6%), followed by GC, allocation, and write barriers
(12.7%).

## Cost-model accounting

16-core Xeon Platinum 8370C (32 logical CPUs), Release engine `8b41df437`, all
three evaluation and action-graph opt-ins on, asynchronous logging, and child
binlog and meter listeners off unless stated. Two workload epochs exist
(1,655 and then 1,654 configurations), and their numbers must not be mixed.

### Workload counts and coarse evaluation ledger

| Workload unit | Count |
| --- | ---: |
| Physical project files | 637 |
| Configured evaluations / project builds | 1,655 |
| Executed targets | 28,178 |
| Executable task invocations | 16,204 |
| Final property entries | 2,301,180 |
| Final item entries | 1,586,466 |
| UsingTask registrations | 212,086 |
| Intrinsic-task scopes | 29,614 |

Coarse graph CPU (`CLOCK_THREAD_CPUTIME_ID`), mean of two runs; graph elapsed
time was 7.95 s:

| Graph account | CPU s |
| --- | ---: |
| Initial properties | 0.715 |
| Properties and imports | 60.000 |
| Item definitions | 0.283 |
| Items, including lazy application | 52.785 |
| UsingTasks | 5.657 |
| Targets | 5.922 |
| Other synchronous evaluation | 0.169 |
| **Evaluation threads** | **125.530** |
| Graph process CPU outside evaluation scopes | 20.317 |
| **Graph process total** | **145.847** |

Small metadata primitives on 512 two-entry `TaskItem`s, median CPU per
operation: a named read costs 18.1 ns; read plus three-part concatenation,
44.2 ns; a changed write, 99.5 ns; and read, concatenation, and write
together, 121.2 ns.

### Binding and execution ledger

Second epoch: 1,654 configurations and 16,189 task bodies. Child-process
intervals (clean Release, mean of two):

| Interval | Elapsed s | Process CPU s |
| --- | ---: | ---: |
| Graph construction and evaluation | 8.447 | 150.773 |
| Post-graph extraction build session | 9.250 | 17.698 |
| Build stop to process exit | 0.881 | 9.643 |

The build session alone exceeds the 5-6 s goal. All project, target,
intrinsic, and task scopes ran on **one thread**. Project-root CPU account:

| Account | CPU s |
| --- | ---: |
| Project work outside selected children | 0.177 |
| Target traversal outside bodies | 1.058 |
| Target bodies outside selected children | 1.541 |
| Intrinsic groups | 0.619 |
| Project logging and state-view creation | 1.458 |
| Task service and resolution, including 0.475 s of bodies | 2.094 |
| **Project roots** | **6.947** |

Another 10.3 CPU s of the build session falls outside these scopes, on other
threads and in scheduling.

Source and residual counts from the compiled path: 1,087 source modules,
391,187 item-operation bindings at 9,146 sites, 1,942,327 residual property
assignments at 4,336 sites, 1,746,839 metadata decisions, and 922,341
explicit metadata values. Of the residual assignments, 43% were literals and
35% were plain `$(P)`. The Unix path heuristic made 2.55 M probe requests,
1.13 M of them from residual property programs.

### Request activation and property views

Every configuration creates one base `Lookup`. Both the logging factory and
`BaseLookup` read `ProjectInstance.PropertiesToBuildWith`, which converts
compact properties into `ProjectPropertyInstance` objects; logging goes first.
The default logging property view eagerly captures filtered, unescaped
entries at event creation. Item views are deferred copy-on-read wrappers.

| Boundary | CPU s | Allocated MB |
| --- | ---: | ---: |
| Request-thread roots, all execution | 7.106 | 892.9 |
| Base lookup construction | 0.0047 | 0.27 |
| Logging factory and state views, inclusive | 1.441 | 323.4 |
| Compact-to-object conversion inside logging (2,034,153 entries) | **1.035** | **276.7** |
| Eager filtered logging property snapshot | 0.361 | 44.8 |

Conversion averages 509 ns and 136 bytes per entry. After activation,
execution's named reads touch at most 72,705 distinct names summed over
configurations (at most 75 in one configuration), at most 3.6% of the
converted entries. Bulk enumeration by loggers and public APIs is outside
that count.

The resulting frame design keeps compact base state owned by the
configuration, with request and batch overlays that preserve `Lookup`
shadowing and merge order. It builds the logging snapshot directly from
compact state at the existing capture point, and materializes public
property objects only on demand. A prototype is committed behind effect
batching. The following cohort prices its macro and execution throughput,
but not its individual residual operations.

### Compact properties through execution: implementation and paired results

This is a separate 760-asset cohort on the 16-core Xeon Platinum 8370C,
not the 639-asset Ryzen/Native AOT workload above.

The bounded implementation extends the existing compiled-module opt-in;
it does not add evaluated-project caching or another interpreter. The
configuration's `BaseLookup` retains its `ProjectInstance` and uses the
existing `IPropertyValueProvider` path for compact scalar reads. Scope
objects still shadow base values, clones share the same outer state, and
only final scope merges publish writes. A merged write removes the old
compact entry before importing the property object, retaining object
identity and subsequent mutation behavior.

Default initial-property logging now eagerly snapshots unescaped values
from both object and compact state without first converting every compact
entry to a `ProjectPropertyInstance`. Environment-derived object entries
retain the existing filter. The all-environment path still fully
materializes properties and uses deferred object reads; selected forwarding
materializes only the requested objects and retains its existing deferred
enumeration. Item views, global properties, event capture points, and the
binary-log format are unchanged. Public object demand and bulk
properties/deep-copy/translation boundaries still materialize as required.
Ordinary object-only projects retain their existing lookup and snapshot
behavior.

#### Same-input optimized comparison

Both bundles are optimized Release builds: their `DebuggableAttribute`
contains `IgnoreSymbolStoreSequencePoints`, not `DisableOptimizations`.
This is a product-change comparison, not a Debug-to-Release improvement.
The preserved ancestor was built from `8b41df437`; the candidate from
`c39c6b90e`. That commit includes the five production-file changes as well
as the preceding documentation and regression tests. Informational-version
and daily build stamps changed in the other assemblies; their production
source did not change.

The runtime checkout remains clean at
`ed3bc08392c14faed28bbe367681b0c1bc698c43`, using the same SDK, command,
opt-ins, package cache, and fresh 760-asset reference as the activation
investigation. Each invocation uses a fresh process with `/nr:false`.
The macro cohort has one warmup per bundle followed by four pairs with
alternating order. It has no startup hook, fine probes, or child binlogs;
the same outer binlog is enabled on both sides. Warmups are excluded:

| Pair | Order | Ancestor elapsed | Candidate elapsed | Saving |
|---|---|---:|---:|---:|
| 1 | ancestor, candidate | 18.21 s | 17.42 s | 0.79 s |
| 2 | candidate, ancestor | 18.59 s | 17.36 s | 1.23 s |
| 3 | ancestor, candidate | 17.78 s | 17.17 s | 0.61 s |
| 4 | candidate, ancestor | 17.49 s | 16.92 s | 0.57 s |

Mean outer restore time falls from **18.018 to 17.218 seconds**, a
**0.800-second / 4.44%** saving, positive in every pair. The paired
savings' sample standard deviation is 0.302 seconds. Total process-tree
CPU falls from 173.288 to 167.638 seconds on average, but its paired
savings' standard deviation is 7.489 seconds; this does not establish a
precise CPU price for the new bridge.

A separate two-pair cohort enables only the existing coarse phase hook
to distinguish graph evaluation from restore extraction execution:

| Extraction build-session metric | Ancestor mean | Candidate mean | Difference |
|---|---:|---:|---:|
| Elapsed | 8.215 s | 7.772 s | **0.443 s / 5.40% less** |
| Process CPU | 16.210 s | 13.883 s | 2.327 s / 14.36% less |
| Allocated bytes | 977.862 MB | 710.588 MB | 267.274 MB / 27.33% less |
| GC pause | 0.157 s | 0.081 s | 0.076 s less |

The execution elapsed savings repeat at **0.452 and 0.434 seconds**
(sample standard deviation 0.013 seconds). Execution CPU savings are
less stable, at 3.250 and 1.405 seconds. Allocation savings are similarly
repeatable at 266.865 and 267.683 MB; these are a secondary benefit, not
the throughput acceptance criterion. Process CPU includes runtime/GC
work and cannot be equated to the previously measured 1.035-second
request-thread conversion charge. GC pause is not an additive explanation
of the elapsed saving.

Graph elapsed savings are 0.964 and **-0.011** seconds; the mean
0.477-second change is not repeatable enough to credit as a graph
improvement. Graph allocations increase by 5.187 MB on average (0.083%).
Do not add the macro and phase-cohort savings or compare these absolute
times with the earlier 20.40-second clean mean: even the unchanged
ancestor is faster in this cohort. The gain is established by the
same-cohort balanced comparison.

All **14 captures**, including warmups, match the fresh reference's 760
asset hashes exactly. The four phase captures each contain 1,654
evaluations, one complete graph, and balanced evaluation boundaries.
Original SDK hashes were verified, and the isolated SDK's original
assemblies were restored after each cohort. Evidence is session-only in
`compact-execution-macro-results/`, `compact-execution-phases-results/`,
the two preserved bundles, and `compact-execution-measure.py`.

#### Compatibility evidence on the measured candidate and remaining account

Seven focused cases cover compact scalar/object demand, empty/missing and
substring reads, case-insensitive names, batch shadowing and staged
merges, stable mutable objects, eager filtered snapshots, deferred
all-environment views, and binary-log round trips with full/selected
forwarding switches both in-process and out-of-process. In-process logging
retains all initial properties regardless of those remote-only switches;
remote nodes require explicit forwarding configuration.

For this candidate, the Release engine-test build and whole-repository
`./build.sh -c Release -v quiet` both complete with zero warnings/errors.
The direct MTP test executable with class filters `*Lookup_Tests`,
`*BuildManager_Logging_Tests`, `*ModuleEvaluationSharingMetrics_Tests`,
and `*ProjectInstance_Internal_Tests` reports **153 passed, zero failed,
one platform skip**, including all seven focused cases. Existing forwarding
method filters report **five passed, zero failed, two existing skips**.
The Release bootstrap activates SDK `11.0.100-rc.1.26420.103`, builds
`src/Samples/Dependency/Dependency.csproj`, and runs `dotnet msbuild
--help`; its engine hash matches the candidate. These are scoped checks,
not a claim that the previously nongreen full suite now passes.

Retain this foundation for its measured execution-throughput improvement.
It removes an unnecessary representation transition while preserving
required observations, rather than merely delaying that transition to
lookup activation. Remaining logging snapshot, compact hit/miss/overlay
search, object-demand, write/merge, and compatibility-barrier costs are
not separately priced by this coarse candidate cohort. In particular,
object fallback can repeat scope-table searches. Those residuals and the
remaining target/task/runtime account still need bottom-up specification;
the result does not by itself bring restore close to the 5-6-second goal.

## 16-core configuration ladder

This is a new **639-asset** workload on the Xeon, not the historical
760-asset compact-execution comparison. It independently checks the hosting
ladder measured on the Ryzen; it is not a before/after engine comparison.

### Controls and output equivalence

- **Machine:** Intel Xeon Platinum 8370C, 2.80 GHz, 16 cores/32 logical CPUs,
  Linux WSL2 `6.6.87.2-microsoft-standard-WSL2`. Hardware instruction/cycle
  counters are unsupported. `PERF_EVENTS=task-clock` works; instructions,
  cycles, and IPC are unavailable, not zero.
- **Workload:** isolated `dotnet/runtime` at
  `3b5416d32b67c2cb63f69a91975620de8ea0c13d`, SDK
  `11.0.100-rc.1.26420.103`, Arcade toolset `11.0.0-beta.26458.117`.
  Each restore starts with fresh `artifacts/obj` and a retained package
  cache. The workload's `Configuration=Debug` does not select Debug engine
  assemblies.
- **Engine:** optimized Release/ReadyToRun snapshots of source
  `687ceca344aa4bdedee0e1b56cbabafa0239eefd`. The six managed assembly hashes
  match across controls. Engine opt-ins, startup hooks, and ledger probes
  are unset for the macro ladder; server, node reuse, CLI telemetry logger,
  and outer binlog are off.
- **PGO:** explicitly on/off only in
  `NuGet.Build.Tasks.Console.runtimeconfig.json`; outer-process tiering is
  unchanged. The console binlog is controlled separately by
  `GenerateRestoreUseStaticGraphEvaluationBinlog`, and actual file presence
  is checked after each run.
- **AOT:** linked from the same engine, with server GC, static task/resolver
  registrations, and `RestrictPropertyFunctionReceivers=false`. Its
  closed-world switches make this an experimental host for this workload,
  not evidence of general-purpose AOT compatibility.

The upstream scripts run five compatibility/warmup captures, excluded from
the summary, then four repetitions per cell, reversing configuration order
on alternate repetitions. Wall time covers the outer restore command;
CPU is process-tree user plus system time from `/usr/bin/time`. MSBuild
output stays on the terminal.

All **25 captures** succeed and produce the same 639 assets after replacing
only the benchmark SDK snapshot directory prefix in their content. Raw and
normalized hashes are retained; unexplained differences fail comparison.
Original SDK/workload contents and frozen snapshot hashes are preserved.

### Macro results

Means +/- sample standard deviation, four runs per cell:

| Console binlog | Console PGO | Console | Wall s | Process-tree CPU s |
| --- | --- | --- | ---: | ---: |
| on | on | JIT/R2R | 43.622 +/- 0.473 | 174.960 +/- 2.940 |
| off | on | JIT/R2R | 22.688 +/- 0.206 | 283.618 +/- 4.808 |
| on | off | JIT/R2R | 40.568 +/- 0.716 | 141.110 +/- 1.864 |
| off | off | JIT/R2R | 15.643 +/- 0.474 | 146.520 +/- 8.430 |
| off | off | Native AOT | 13.953 +/- 0.305 | 104.630 +/- 1.967 |

Paired wall savings, pairing within repetition:

| Change | Saving s |
| --- | ---: |
| Binlog off, PGO on | 20.934 +/- 0.459 |
| PGO off, binlog on | 3.054 +/- 0.786 |
| Binlog off, PGO off | 24.925 +/- 1.162 |
| PGO off, binlog off | 7.045 +/- 0.658 |
| AOT versus JIT, both off | 1.690 +/- 0.681 |

The PGO and AOT directions replicate, **not the Ryzen's ~10 s and ~3 s
magnitudes**. With binlogs off, PGO removal also saves 137.098 CPU s;
AOT saves another 41.890 CPU s. AOT wall savings are positive in every
pair: 0.676, 1.933, 2.019, and 2.132 s.

The configuration effects are not additive. Removing the binlog with PGO
on saves 20.934 wall s but **adds 108.658 CPU s**. Even with PGO off,
binlog removal saves 24.925 wall s with no mean CPU saving. This shows a
large elapsed-time dependency on logging configuration, but these timings
do not prove a contention, throttling, or writer mechanism. Do not convert
the CPU interaction into an instruction/IPC claim or extrapolate the
binlog saving to other machines.

### Supplemental service account

Six separate diagnostic captures follow the macro ladder: two JIT coarse
phase captures and two level-1 console ledgers per host, in JIT/AOT/AOT/JIT
order. All six succeed, match the same 639 normalized assets, and omit the
console binlog. These probes are **not** present in the macro timings.

JIT phase means, two runs:

| Phase | Elapsed s | Process CPU s |
| --- | ---: | ---: |
| Graph construction/evaluation | 5.504 | 105.664 |
| Extraction build | 6.394 | 10.628 |

Graph elapsed samples are 5.639 and 5.369 s; extraction samples are 6.034
and 6.753 s. Each graph is complete with 1,643 evaluations and balanced
boundaries. These are diagnostic observations, not a new paired engine
saving.

Console ledger CPU-service means, two captures per host:

| Scope | Count | JIT CPU s | AOT CPU s |
| --- | ---: | ---: | ---: |
| Total evaluation, inclusive | 1,643 | 80.478 | 57.837 |
| Properties/imports | 1,643 | 39.915 | 28.711 |
| Item-operation construction | 1,643 | 15.137 | 10.569 |
| Lazy item application | 1,643 | 20.728 | 14.847 |
| Project build, inclusive | 1,643 | 5.359 | 3.866 |
| Project-build framework, exclusive | 1,643 | 0.416 | 0.332 |
| Target builder, exclusive | 1,643 | 0.587 | 0.437 |
| Target, exclusive | 27,959 | 0.464 | 0.359 |
| Task builder, exclusive | 68,489 | 1.202 | 0.953 |
| Task, exclusive | 16,058 | 0.613 | 0.378 |
| Task resolution | 16,058 | 0.285 | 0.026 |
| Task initialization | 16,058 | 0.137 | 0.043 |
| Task parameters | 16,058 | 0.764 | 0.592 |
| Task outputs | 16,058 | 0.214 | 0.148 |
| Intrinsic task | 29,343 | 0.676 | 0.597 |

The exclusive execution rows reconcile to inclusive project-build service.
Do not add the inclusive parents to their children. No `TaskBody` row is
reported: the task-exclusive row is not a separately measured body price,
and the absent scope does not mean bodies cost zero. Build-session CPU
belongs to orchestration and is not the parent of request-thread service.
The request ledger is not complete extraction process accounting; phase
process CPU also includes runtime/background and other uncovered work.

All four console ledgers have the same counts and no migrated/abandoned
frames, multiplexed reads, or depth overflows. Each records one
request-builder thread. Companion outer Arcade ledgers contain
migrated/abandoned frames and are retained with explicit exclusions, not
included in the console totals. Task-clock-only headers cannot calibrate
instruction/cycle probe cost. JIT ledger wall times are 15.966 and 16.170 s;
AOT ledger times are 15.224 and 15.057 s, above their unprobed macro means.
Use the ledgers to locate service, not to replace macro results.

Evidence is session-only under `configuration-ladder-xeon/results/`:
`ladder-smoke/runs.json`, `ladder-v1/analysis.json`, and
`ladder-service-v1/{runs,analysis}.json`, with raw per-run reports and
frozen SDKs. The drivers are `configuration-ladder-driver.py` and
`configuration-ladder-service.py`. Completed captures were read on resume,
not rerun or overwritten.

## Earlier profiling

- **Debug assemblies:** experiments before September 2026 used Debug engine
  assemblies with JIT optimization disabled. On the same commit, Release
  restored in 17.738 s and Debug in 19.460 s (six balanced pairs; paired
  saving 1.722 s, 95% CI [1.357, 2.086]). This corrects the measurements, not
  the product: shipped MSBuild is Release.
- **Sampled profiles** (`perf` at 99 Hz with `cpu-clock:u`, and EventPipe
  thread-time sampling): graph construction used ~105 aggregate user-CPU
  seconds in 7.2 s of elapsed time. About 38% of perf leaves were unresolved
  native frames, and EventPipe sampling put ~48% of samples in `PollGC`, so
  neither ranks hotspots reliably. The repeated caller chains were the Unix
  path heuristic (`MaybeAdjustFilePath` → `DirectoryExists` → `stat`) and
  environment reads behind feature-switch checks.
- **Data model:** 1,587,184 published items averaged 1.35 direct metadata
  entries (93% had at most four). All 353,306 evaluation-time item copies with
  metadata shared their source's backing. Sampled allocation during graph
  construction was ~6 GB; compact-property `Set`/`Apply` (430 MiB) and
  ordered item collections (209 MiB) were the largest families.

## Specialization experiments

These are opt-in specializations under compiled module effect batching,
measured with Debug engine assemblies on the 16-core machine. Each removed
most of its local cost, and none showed a statistically established
restore-wall change.

| Specialization | Local effect | Paired restore wall saving (95% CI) |
| --- | --- | --- |
| Fixed imports (`$(MSBuildThisFileDirectory)` + literal) | Argument preparation -88.9% (5.17 s to 0.57 s summed) | -0.37 s [-2.92, 2.17] |
| Source-bound `Exists` arguments | Preparation -72.4%, allocation -79.4% | -0.99 s [-2.29, 0.32] |
| No-slash early exit in `MaybeAdjustFilePath` (default-on) | `ProjectToBuild` expansion allocation -147 MiB (50%) | +0.30 s [-1.38, 1.97] |
| Residual programs for repeated metadata expressions | Expansion CPU -28.7% | +0.10 s [-0.19, 0.38] |

A negative saving means the candidate was slower. All runs matched the
control's assets.
