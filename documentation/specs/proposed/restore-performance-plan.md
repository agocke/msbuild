# Classic MSBuild restore performance plan

This plan targets the clean, package-cached `dotnet/runtime` static-graph
restore workload **under existing MSBuild semantics**. Project files, public
object models, project-reference and target protocols, task lifecycles,
filesystem observations, diagnostics, and restore outputs remain authoritative.
The optimization specializes repeated interpretation; it does not require a
new evaluation mode, declared-I/O contract, result cache, hardened graph, or
output-oriented build query.

The historical measurements below were adapted from the separate
`build-exp` restore investigation (August 2026). They are not measurements
of this branch at its current tip. The current bottom-up model below uses
new Release counts and CPU accounting. Current branch experiments
are recorded in [evaluation performance investigations](evaluation-perf.md);
discarded approaches are in
[evaluation performance dead ends](evaluation-performance-dead-ends.md).
Neither document's measurements can be added to the historical decomposition.

## Status (October 2026)

| Phase | Current (diagnostic follow-up epoch, 20.4 s total) | Design goal |
| --- | --- | --- |
| Graph/evaluation | 8.45 s elapsed, 150.8 process-CPU s (effective parallelism ~17.8) | ~1.5 s, <=48 CPU s |
| Post-graph build session | 9.25 s elapsed, 17.7 process-CPU s, request work on one thread | ~2.5-3.0 s (execution + tasks) |
| Everything else | ~2.7 s | ~1.0-1.5 s |

The latest counter-free Release wall time (separate cohort, `8b41df437`)
is **17.7 s** against the **5-6 s** goal. Diagnostic phase times include
instrumentation and do not sum to it.

Compiled evaluation, effect batching (which also gates the compact-state
activation frame), and action-graph partial evaluation are opt-in through
the three switches listed under
[branch contents](#what-this-branch-actually-contains). Other committed
changes are default-on: the candidate default saved 5.86 s against stock.
Open concern: the candidate *default* path used 27.5 more paired user-CPU
seconds than stock in the last all-up cohort. The default path is what
ships, so this is a likely regression on machines with fewer cores, and it
is not yet explained.

No combined Release measurement exists after the metadata specialization
and frame prototype. Run one before adding more specializers.

## Instruction-level accounting (October 2026)

### Harness

Measurements below come from an 8-core/16-thread Ryzen 7 1800X (Zen 1),
Fedora, `kernel.perf_event_paranoid=1` so kernel instructions count. A
local clone of `dotnet/runtime` at `3b5416d` runs a clean, package-cached
static-graph restore through Arcade's `Build.proj`: `artifacts/obj` is
deleted, the isolated package cache is kept, MSBuild Server and node reuse
are off, and the CLI's telemetry logger and the outer binlog are dropped.
Every run must produce 639 `project.assets.json` files. The runtime's own
build still asks the NuGet console for a binlog
(`GenerateRestoreUseStaticGraphEvaluationBinlog`), so that cost is part of
the workload. Each candidate engine is a frozen copy of
`artifacts/bin/bootstrap/core` (both repos pin SDK
`11.0.100-rc.1.26420.103`, so there is no version float).

Use instructions retired (user + kernel) as the primary unit and wall time
for macro results. Instructions are comparable across the 8- and 16-core
machines, except for spin-waits, whose instruction counts depend on the
CPU's `pause` latency.

### Tools

- `MSBUILDCOSTLEDGER=<prefix>` writes `<prefix>.<pid>.tsv` from every
  process using the engine: per-scope counts, inclusive and exclusive
  instructions, cycles, task-clock, wall, and allocated bytes, plus
  per-thread totals and unscoped work. `MSBUILDCOSTLEDGERLEVEL=2` adds the
  fine-grained evaluation and fast-task scopes. Each thread reads a
  perf_event group with one syscall per scope boundary. The file header
  reports the measured probe cost per scope (~3-4k instructions charged to
  the parent and ~3k to the scope itself), so fine-grained levels perturb
  execution scopes. Request continuations that resume on another
  request-builder thread carry their open frames across the two
  `await handles.ToTask()` points; any other thread hop is reported as
  `migrated`, not mixed in.
- Method attribution: `perf record -e instructions -c 1000003 --call-graph fp`
  with `DOTNET_PerfMapEnabled=1` **and** `DOTNET_EnableWriteXorExecute=0`.
  With W^X on, JIT code is mapped from a memfd and perf never consults the
  perf map. `libcoreclr.so` is stripped. Its debug file can be fetched
  from the symbol server by build ID and placed as `debug` in the perf
  build-id cache. Do not resolve LTO'd runtime addresses with `nm` or
  binutils `addr2line`; both misattributed the hottest range.

### Runtime configuration dominates the first-order gap

Three interleaved runs per cell (mean ± standard deviation):

| Engine assemblies | Dynamic PGO | Wall | Instructions | CPU s |
| --- | --- | ---: | ---: | ---: |
| IL (bootstrap as built) | on (default) | 32.86 ± 0.58 s | 577.9 G | 213.2 |
| IL | off | 24.25 ± 0.16 s | 337.1 G | 144.9 |
| ReadyToRun (crossgen2, as shipped) | on (default) | 33.41 ± 0.44 s | 642.3 G | 225.3 |
| ReadyToRun | off | 23.21 ± 0.27 s | 327.1 G | 142.3 |
| ReadyToRun | off in `NuGet.Build.Tasks.Console` only | 23.06 s (22.7-23.4) | ~300 G user | 140.9 |

The stock SDK measured 34.5 s with PGO on. With PGO on, ~23% of all
instructions were in `JIT_CountProfile32`/`JIT_ClassProfile32`, plus
~6% in `__tls_get_addr` from the same instrumentation path. **The entire
~10-second PGO cost is in the NuGet static-graph console process**, which
starts fresh for every restore and never benefits from the profile.
Setting `TieredPGO=false` in that project's runtime config is a NuGet
change, not an engine change, and must be confirmed on the 16-core
machine. ReadyToRun-compiling the bootstrap changes results by about one
second, so IL bootstrap measurements are representative once PGO is
controlled. Engine accounting below uses PGO off and reports PGO
separately.

### Static-graph binlog and a Native AOT console

`dotnet/runtime` sets `GenerateRestoreUseStaticGraphEvaluationBinlog`,
which makes the NuGet console write a binlog (later runtime commits turn
it off). Interleaved runs on the ReadyToRun SDK, three per cell:

| Static-graph binlog | PGO | Wall | Instructions | CPU s |
| --- | --- | ---: | ---: | ---: |
| on | on | 33.73 ± 0.86 s | 625.6 G | 224.4 |
| off | on | 27.75 ± 0.17 s | 825.1 G | 280.4 |
| on | off | 22.68 ± 0.56 s | 325.6 G | 140.5 |
| off | off | 16.64 ± 0.13 s | 289.5 G | 128.2 |

The binlog costs ~6 s of wall time but only ~36 G instructions, so its
writer is on a serial path. With PGO on, removing it *raises*
instructions, probably from more concurrent contention on the shared
profiling counters; PGO-on instruction counts are not a reliable unit.

The console process is almost entirely MSBuild: NuGet's own frames are
~5% of its instructions. A Native AOT build of it needs no NuGet or SDK
changes. A host executable links `NuGet.Build.Tasks.Console`, MSBuild, the
NuGet libraries, the restore tasks, and both SDK resolvers. It registers
the ten tasks restore runs (`Task.RegisterTask<T>`) and the resolvers
(`SdkResolver.Register`), and forwards to the console's `Program.Main`.
Restore launches it with `-p:NuGetConsoleProcessFileName=<exe>`, and it
lives in `sdk/<version>/`, where the workload resolver expects to run. It
uses the closed-world feature switches from `src/aot-validation` with
one exception: `RestrictPropertyFunctionReceivers` must stay `false`.
`dotnet/runtime`'s `eng/Versions.props` calls `Match.Groups` on a regex
match, and its coreclr `Directory.Build.targets` reads `DirectoryInfo.Name`,
both rejected by the restricted receiver set. Keeping JIT semantics
requires rooting the reflected framework surface (`System.IO`, regex, and
common value types). One engine bug surfaced and is fixed:
`ToolLocationHelper.GetDefaultSDKDiskRoots` added the empty AOT
`FrameworkBasePath` as a disk root and then threw.

All 639 assets match the JIT console except for the embedded SDK directory
path. Interleaved, binlog and PGO off, three pairs:

| Console | Wall | Instructions | Cycles | CPU s |
| --- | ---: | ---: | ---: | ---: |
| JIT (ReadyToRun + tiering) | 16.85 ± 0.29 s | 285.9 G | 448.2 G | 127.5 |
| Native AOT (ILC) | 13.83 ± 0.26 s | 274.4 G | 360.6 G | 101.8 |

The gain is code quality, not less work: ~4% fewer instructions but 20%
fewer cycles (IPC 0.64 → 0.76), plus no JIT thread. Server GC must be
linked explicitly (`ServerGarbageCollection=true`; the GC flavor is a
link-time choice in Native AOT). The workstation GC alone took this
restore to ~37 s through allocation-lock contention.

From 33.7 s, runtime configuration and hosting alone (no binlog, no
PGO in the console, Native AOT) reach 13.8 s on this 8-core machine with
no evaluator changes. Engine accounting should start from that baseline.

### Accounting on the Native AOT console

With no JIT, tiering, or PGO, the AOT console process retires 264 G
instructions (perf, process-filtered, binlog off):

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

Evaluation is ~179 G, ~109 M instructions per evaluation, the same as
under the JIT. AOT reduces cycles, not work.

The level-2 ledger supplies unit counts in the same image. Its probes
inflate evaluation from 170.8 to 266.1 G (~13.5 M scopes at ~6.4 k
instructions). The per-unit prices below subtract the calibrated probe
cost (3.0 k inside each scope, 3.4 k charged to its parent per child) and
are cross-checked against the level-1 run:

| Unit | Count | Instructions per unit (approximate) |
| --- | ---: | ---: |
| Condition evaluation | 3.78 M | ~11-12 k |
| Property element in the properties/imports pass, excluding its condition | ~1.9 M (census count, not this run) | ~29 k |
| Item operation application, excluding nested scopes | 841 k | ~27-30 k |
| Item spec construction | 844 k | ~11 k |
| `UsingTask` registration | 208 k | ~21 k |
| Metadata analysis | 839 k | ~5.4 k |
| Task parameter binding, per task | 16 k | ~123 k |
| Intrinsic task | 29 k | ~76 k |

A typical condition (`'$(Foo)' == 'true'`) needs one or two keyed reads
and a short comparison. A typical property element needs one ~50-character
expansion and one keyed write. Either should cost a few hundred
instructions even with generous hashing, so both run ~25-100x above a
plausible floor. Conditions alone are ~43 G (a quarter of evaluation).
These floors are estimates, not yet measurements. The executable-floor
benchmark should replace them, starting with conditions and property
elements, then item operations.

### Where the remaining instructions go (PGO off, JIT console)

A call-stack classifier over the R2R-equivalent IL profile assigns each
sample to the innermost recognizable subsystem and to an operation kind
from its leaf frames (338 G instructions):

| Subsystem | Instructions | Share | Notable leaf kinds |
| --- | ---: | ---: | --- |
| Item evaluation | 65.5 G | 19.4% | hash/lookup 22%, string scan 9%, regex 7% |
| Property evaluation | 38.5 G | 11.4% | hash/lookup 15%, kernel 14%, cast/dispatch 10% |
| Server/background GC threads | 37.8 G | 11.2% | |
| Condition evaluation | 34.1 G | 10.1% | hash/lookup 26% |
| Unattributed | 34.3 G | 10.2% | regex 20%, kernel 16% |
| NuGet console binlog | 29.9 G | 8.8% | hash/lookup 39% |
| Tiered JIT thread | 16.6 G | 4.9% | |
| Imports, other evaluation, NuGet, execution, graph | ~81 G | ~24% | |

Across everything, hash/lookup is the largest identifiable kind (16.6%),
followed by GC, allocation, and write barriers together (12.7%),
JIT/type loading (8.8%), kernel (8.5%), and cast/dispatch (6.3%). Leaf code
outside these categories is 22.5%. Evaluation alone is ~176 G
instructions, ~107 M per evaluation before GC and JIT threads. The flat
profile has no hotspot above 2.6%. That shape fits the hypothesis that the
cost is spread across data-structure operations (keyed lookups,
allocation, dispatch) rather than concentrated in an algorithm. Leaf-frame
classification undercounts that overhead; the next step is pricing it per
semantic unit.

## Where the gap comes from: per-unit accounting

The ledgers below count semantic units well but do not yet say where the
cycles go. Dividing measured pass CPU by those counts gives the *implied*
current price per unit. Comparing that with a generous bottom-up price
shows how much CPU is unattributed.

### Evaluation

At 150.8 CPU seconds over 1,654 evaluations, each evaluation costs about
91 ms of CPU, which is at least ~250 million cycles at the nominal 2.8 GHz.
With the opt-in pass split from the earlier cohort (properties/imports
~66 CPU s, items ~68 CPU s), the implied prices are:

| Unit | Count | Implied current price if the pass is attributed to it |
| --- | ---: | ---: |
| Residual property assignment | 1,942,327 | ~34 us (~96k cycles) |
| Metadata decision | 1,746,839 | ~39 us |
| Item-operation binding | 391,187 | ~173 us |

A deliberately generous bottom-up price for the counted work:

| Counted unit | Count | Generous price | CPU s |
| --- | ---: | ---: | ---: |
| Residual property assignments | 1.94 M | 1 us (~10x the 121 ns read/concat/write fixture) | 1.94 |
| Metadata decisions | 1.75 M | 0.1 us | 0.17 |
| Explicit metadata values (mean 79 chars) | 0.92 M | 1 us | 0.92 |
| Item-spec constructions | 0.39 M | 1 us | 0.39 |
| Path-heuristic probe requests | 2.55 M | 2 us (assumes every request is a syscall) | 5.10 |
| UsingTask registrations | 0.21 M | 0.5 us | 0.11 |
| Module lowering (shared, measured) | 1,087 | measured | 0.59 |
| **Total** | | | **~9.2** |

That is about 6% of measured evaluation CPU. Units outside the census are
missing: initial and environment properties, constant replay, scalar
fallback, import and SDK resolution, globbing, item definitions, target
registration, evaluation-result logging, GC, and JIT. Even if they doubled
the useful work, the total (~20 CPU s) would sit well inside the 48-second
ceiling. **The goal is consistent with the useful work. Roughly 130-140
CPU seconds of evaluation are not explained by any counted semantic unit
at generous prices.** Two hypotheses, not mutually exclusive:

- **A. Interpretive overhead per unit** (dispatch, re-parsing, name
  lookup, generic expansion): a 15-30x per-unit tax. Specializing control
  with respect to source (the first Futamura projection) removes it.
- **B. Per-configuration work outside the counted units**: state
  materialization and representation transitions, result snapshots and
  logging, allocation/GC, lock contention, and SMT inflation of
  thread-CPU time. Specializing control does not remove it. Changing the
  state representation or the runtime does.

The evidence so far leans toward B being large. Control specializations
removed 70-90% of their local costs (fixed imports 88.9%, `Exists` 72.4%)
without measurable macro movement. Compiled property programs and SSA
variants reduced property work without reducing wall time. Result replay
at a 96.7% hit rate was slower. 43% of residual property assignments are
pure literals (841,062) and another 35% are plain `$(P)` reads (680,638).
Their computation is trivial. What remains per configuration is the
*write into a per-configuration, identity-bearing state* and everything
downstream that copies or converts that state.

**Accounting unit.** Process-CPU seconds are a poor unit here. The machine
has 32 logical CPUs on 16 physical cores, and evaluation runs ~18-way
parallel, so SMT siblings share cores and inflate thread-CPU time per unit
of work. Spin-waiting and GC threads add CPU that is not interpretation.
Use **instructions retired and cycles** (`perf stat`) as the primary cost
unit, with CPU seconds secondary. Instructions per semantic unit are
directly comparable to other interpreters, and IPC separates instruction
overhead (hypothesis A) from memory and contention stalls (part of B).

Three measurements decide between A and B and replace generous guesses
with numbers:

1. **Instructions and cycles for the graph phase, at full parallelism and
   with one evaluator thread.** This gives instructions per evaluation and
   per counted unit, and the parallel-inflation factor.
2. **An executable floor.** Replay the recorded semantic-operation stream
   (property writes with their real shapes and output lengths, metadata
   decisions and values, probe requests) against a minimal slot-indexed
   state. The gap between this floor and the implied current price is the
   true per-unit overhead. The floor is the bottom-up model made
   measurable.
3. **A binding-time census.** For each evaluation, record where in
   evaluation order the first read occurs of an input that varies across
   configurations of the same project (global properties such as
   `TargetFramework`) and across projects (project path, directory probes).
   This says how much of the evaluation prefix is static modulo a small set
   of inputs. The root-only prefix prototype gets zero hits because the
   first divergent input is read at the root. The census says whether a
   deeper, live-in-keyed prefix would hit.

### Execution

Without multithreaded mode the build has exactly one in-proc node
(`NodeProviderInProc.cs`, `maxNodeCount = MultiThreaded ? MaxNodeCount : 1`).
The 1,654 project requests are therefore a serial critical path, and every
per-request fixed cost is multiplied by 1,654:

| Per project request | Current | Bottom-up |
| --- | ---: | ---: |
| Elapsed on the serialized path | 5.59 ms (9.25 s total) | ~1.35 ms (~2.2 s total) |
| Covered request-thread CPU | 4.20 ms | |
| Request-path elapsed not covered by CPU scopes | ~1.39 ms (~2.3 s) | |
| Tasks (9.8 per request x 61.7 us) | 1.27 ms | 0.60 ms |
| Target traversal (17 per request x ~20 us) | 0.64 ms | 0.34 ms |
| Intrinsic groups | 0.37 ms | ~0.19 ms |
| Required `ProjectStarted` snapshot | 0.22 ms | 0.22 ms |

Elapsed and covered CPU come from different diagnostic captures in the
same input epoch, so the uncovered row is approximate. Current component
rows overlap the covered-CPU row and are not additive with it.

The bottom-up total fits the 2.5-3.0-second execution envelope only if the
~2.3 seconds of uncovered request-path time almost disappears. That time
is waiting (scheduler handoffs, blocked requests, logging back-pressure,
GC pauses of ~0.18 s) or uncovered work. It is the largest single
execution item and is not yet split.

### Execution-model incompatibilities

These are places where the current model structurally prevents the
bottom-up budget, as opposed to an implementation being slower than it
needs to be:

1. **Serial in-proc execution.** With one in-proc node, the execution
   budget is a per-request latency budget (~1.5 ms per request including
   all waits), not a CPU budget. Multithreaded mode lifts the single-node
   limit. Whether the restore's tasks can run there without task-host
   isolation costs is unverified.
2. **Cross-request round trips (hypothesis).** Restore targets call the
   `MSBuild` task on the same project with per-framework global properties
   and on references. Each call is a scheduler round trip with request
   blocking, result caching, and handoff. The bottom-up budget has no line
   for these. Count them and price the round trip; they are a candidate
   explanation for the uncovered ~2.3 s.
3. **State identity at the public boundary.** Evaluation's output is
   ~1,390 properties and ~960 items per configuration, exposed as mutable,
   identity-bearing objects, and snapshotted for logging. Under the current
   representation, a literal assignment that is static across all 1,654
   configurations still costs a per-configuration object write. Shared
   base state with per-configuration deltas and lazy compatibility views
   (the activation-frame direction) is the representation change that
   removes this. Specializing control does not.
4. **Task lifecycle shape.** 92 us of covered non-body work per task
   against a ~35 us allowance. Reflection-based binding, generic parameter
   and output traversal, and per-invocation host setup must leave the
   common path. Compiled action plans cover part of this.

### Assessment of the first-Futamura direction

Specializing the interpreter with respect to project source is the right
direction for the control layer. The branch already does this as a
mixed-time specialization: source modules are static, imports are linked
dynamically because conditions and SDK resolution select them, and the
residual runs per configuration. Details that complicate a "first
projection alone" thesis:

- **Removing control overhead has not moved wall time.** Phase-local
  savings of 70-90% did not show up at macro scale, so the residual is
  dominated by something specialization of control leaves in place.
- **The residual's output is the expensive part.** Most residual
  assignments are literals or plain reads. Static values written into
  per-configuration state are dynamic only because the state is
  per-configuration. Partial evaluation handles this through partially
  static data structures, meaning the state representation has to become
  shared-base-plus-delta. That needs a compatibility view for the public
  object model and logging. This is the main engineering cost.
- **Polyvariance matters.** Configurations of one project differ in a
  handful of global properties. Specializing on (source, those values) is
  the polyvariant step, and the binding-time census decides whether it pays.
- **Observations, GC, contention, and scheduling are outside the
  projection.** The 2.55 M probe requests, ~9 GiB of evaluation
  allocation, SMT/contention inflation, and serialized execution need
  separate treatment.

In short: the first projection is necessary for the expression and
control layer but not sufficient. The evidence points at state
representation (evaluation) and the serial request path (execution) as
where the remaining factor has to come from.

## Work backward from useful work

Top-down profiles tell us where the *current implementation* spends time and
whether a change helped. They do not tell us what a good interpreter should
cost: optimizing the biggest existing method may preserve an expensive
architecture. Start with the work the restore actually needs, set a
bottom-up budget, then use profiles and paired measurements to find and
validate changes that close the gap.

### Current bottom-up model and component contracts

The engineering decision is **budget compliance**, not profiler rank.
Before adding a specializer or changing a representation, specify its
useful work, valid binding scope, required effects, and cost units. A
profile explains why a component exceeds its contract; a large sample
share or allocation total does not establish a violation.

The initial refreshed `8b41df437` Release workload has 637 physical project files,
1,655 configured evaluations/builds, 28,178 executed targets, and 16,204
executable tasks. Two census captures agree exactly on these counts and
on 2,301,180 logged final property entries and 1,586,466 final item entries.
Final-state entries are **not** counts of reads, writes, intermediate
items, or expression executions. Standard meter counts additionally
recorded 388,152 item-spec constructions, 382,334 metadata-analysis scopes,
384,632 lazy item-operation scopes, 27,464 Update-decoration scopes,
212,086 UsingTask registrations, and 29,614 intrinsic-task scopes.
These are instrumentation units, not mutually exclusive semantic
categories. For example, decoration is an operation over multiple items,
not one metadata read/concatenation/write.

The measurement machine exposes 32 logical CPUs on 16 physical cores
(Xeon Platinum 8370C, 2.8 GHz, one NUMA node). CPU here means thread or
process CPU time, including kernel time where the clock includes it;
native user-only samples are a different measure. Hardware concurrency
is an upper bound, not a guarantee of useful parallelism.

For each component, the service-cost model is:

```text
C(component) =
    source/version preparations * preparation cost
  + valid-scope bindings * binding cost
  + sum(residual executions by shape and size * execution cost)
  + required observations and user lifecycle work
  + coordination cost
```

Source preparations are keyed by source version, not physical project
alone. Bindings are configuration-, operation-, item-, or request-owned
as appropriate. Current property values cannot be frozen just because
their names can be bound. Expression sizes, output-string lengths,
item-vector cardinalities, metadata access kinds, and effects are cost
units: a single average "expression" or "item" price is insufficient.
Only exclusive costs at a given ledger level are added. Each shared
preparation is billed once and amortized over actual reuse, not omitted.

| Component | Useful-work contract | Current pricing status |
| --- | --- | --- |
| Source preparation | Lower source structure and dependency/effect summaries per source version; retain source/diagnostic identity. | Module lowering measured; follow-up observes 1,087 compiled source/version identities. XML/import preparation remains incomplete. |
| Configuration/operation binding | Bind stable inputs in their valid scope; retain ordered observations and changing-value reads. | Item construction and metadata analysis measured as boundaries, but binding and required effects are not separately priced. |
| Residual scalar/condition | Direct bound reads, supported operations, and short-circuit decisions; no repeated discovery of invariant source structure. | Small metadata primitives calibrated; follow-up supplies bounded source-shape, output-size, and observation-request counts, but access-kind/effect prices remain missing. |
| Ordered item operation | Required selection, ordered metadata decisions/writes, and snapshot/identity preservation; shared preparation is outside the per-item loop. | Exclusive selection/decoration costs measured; their full useful-work mixture is not yet priced. |
| Task lifecycle | Resolution/binding, construction, parameters, execution, outputs, cleanup, and required callbacks; source programs contain no request/assembly-context state. | Covered task-service costs and task bodies measured against a provisional aggregate CPU envelope. |
| Target/request orchestration | Required dependencies, conditions, batching, intrinsic mutations, and scheduling. | Follow-up prices whole project/target/intrinsic/logging boundaries on one execution thread; request preparation, runtime residuals, and critical-path allocation remain incomplete. |
| External observations/runtime | Filesystem, environment, arbitrary functions, logging, GC, and JIT retain their required semantics. | Explicit residual accounts; allocation/sample shares are not substituted for service prices. |

Elapsed phase budgets remain the 5-6-second design goal below, but the
service constraints are now explicit:

| Boundary | Quantitative contract | Current Release observation |
| --- | --- | --- |
| Graph/evaluation | ~1.5 s elapsed; at most 48 process-CPU seconds even at perfect utilization of all 32 CPUs. At 16 effective CPUs the envelope would instead be 24 CPU seconds. | Coarse diagnostic mean: 7.953 s elapsed and 145.847 process-CPU seconds. |
| Complete executable-task service | Provisional <=1.0 aggregate CPU second including bodies and all lifecycle work; elapsed/critical-path performance remains a separate requirement. | Covered task scopes plus resolution: 1.934 CPU seconds, including 0.436 seconds of bodies. |
| Other execution and non-engine work | Retain the ~1.5-2.0 s and ~1.0-1.5 s elapsed envelopes below; do not infer a CPU allocation without a concurrency/path model. | Complete current additive decomposition is not yet available. |

The graph ceiling is a necessary condition, not a sufficient specification.
At its current measured service cost, perfect 32-CPU utilization still
requires about 4.56 seconds. Reaching 1.5 seconds requires a different
cost structure, not just more scheduling parallelism on this machine.
The 16-CPU example is a planning sensitivity, not a measured concurrency
promise. Coarse phase diagnostics include instrumentation; they are not
counter-free macro samples.

For 16,204 tasks, the one-second service envelope gives **61.71 us per
invocation**. Measured bodies use 26.92 us, leaving at most **34.80 us**
for all non-body lifecycle work. The covered non-body subtotal is
**92.45 us**, about 1.498 CPU seconds versus a 0.564-second allowance.
This is a concrete provisional budget mismatch, not evidence that all
non-body work is interpreter overhead: constructors, setters/getters,
parameter conversion, outputs, and logging can be required useful work.
Preparation outside the selected scopes is not included, so 1.934 seconds
is not a measurement of the entire pipeline. Probe costs also remain in
the diagnostic measurements; no observer-cost subtraction is asserted.
The complete-task CPU contract deliberately does not equate aggregate
CPU time with elapsed time or promise a 0.934-second wall-time saving.

Seven-round Release reference fixtures on 512 two-entry `TaskItem`
instances price small residual operations using the existing metadata
implementation:

| Reference fixture | Median thread CPU per operation |
| --- | ---: |
| Direct named metadata read | 18.13 ns |
| Metadata read and nonempty predicate | 17.95 ns |
| Metadata read and three-part concatenation | 44.23 ns |
| Changed existing metadata write | 99.47 ns |
| Metadata read, concatenation, and changed write | 121.15 ns |

These are warm, small-state primitive calibrations, not complete MSBuild
expression costs or universal nanosecond limits. Reads/predicates overlap
and compound fixtures must not be added to their constituent operations.
The fixtures include the public metadata APIs and their ordinary
small-map/escaping work, but exclude binding, inherited/built-in metadata,
property-read observers, filesystem path heuristics, arbitrary functions,
and the workload's server-GC/concurrency context. They use short,
unescaped strings; string-length and access-kind mixtures remain unpriced.
For example, one million operations *of exactly the combined fixture's
shape* imply about 0.121 CPU seconds of primitive work, not a price for a
million arbitrary decorations. Neither final item counts nor the 27,464
decoration scopes establish that shape count.

The exclusive evaluation ledger and observer controls are in
[the accounting measurements](evaluation-perf.md#bottom-up-cost-model-accounting).
The clean coarse ledger reconciles 125.530 evaluation-thread CPU seconds
and 20.317 other graph-process CPU seconds to the 145.847-second graph
total. The latter is explicitly unassigned, not labelled "overhead" or
added again to GC/JIT sample families. Finer diagnostic scopes also
reconcile, but their evaluation mean is 135.001 CPU seconds; the extra
instrumentation changes the cost structure and cannot supply exact
counter-free per-component prices.

This revision therefore supplies current counts, phase/service envelopes,
an exclusive diagnostic ledger, and calibrated primitives. **It does not
yet supply a fully priced semantics-equivalent restore implementation.**
The follow-up below adds bounded source-site/shape/effect counts,
output-string distributions, and whole execution boundaries. Remaining
inputs include binding and required-observation prices, metadata access
kinds, vector-size distributions, uncovered execution/runtime work, and
a critical-path model.
Until those are priced, an item/metadata component can be a candidate for
investigation but cannot honestly receive a numeric pass/fail verdict.

The first reusable foundation must close a specified gap. Task-site
binding and parameter/output execution should be assessed against the
non-body service allowance, preserving user callbacks and lifecycle.
Shared scalar/condition residual programs should be assessed against a
shape-weighted primitive-plus-binding-plus-effects price. Neither is
justified solely by a hot stack. Accept modest, repeatable component
savings with bounded preparation/memory costs, then measure accumulated
macro throughput in optimized, counter-free comparisons.

### Fine-grained follow-up and next priced boundary

The [binding and execution follow-up](evaluation-perf.md#fine-grained-binding-and-execution-accounting)
uses a **new input epoch**, not a throughput comparison with the initial
cohort above. Although runtime source and clean engine hashes were
unchanged, 641 of the historical 760 asset hashes differed; their cause
cannot be established from historical hashes alone. A fresh clean
reference retains actual asset contents, and all twelve follow-up
captures match it exactly. This epoch has 1,654 evaluations and 16,189
task bodies. No optimization gain is inferred from cross-epoch times.

The clean coarse mean is 20.400 seconds outer elapsed, with:

| Boundary | Elapsed s | Process CPU s |
| --- | ---: | ---: |
| Graph/evaluation | 8.447 | 150.773 |
| Post-graph extraction build session | 9.250 | 17.698 |
| Build stop to child exit | 0.881 | 9.643 |

At 32 perfectly utilized logical CPUs, the graph CPU alone requires
about 4.71 seconds; the 1.5-second envelope permits at most 48 CPU
seconds. The build-session span independently exceeds the complete
5-6-second goal. A new evaluation cost structure and a new execution
cost structure are both necessary. These are measured phase gaps, not
proof that all their current CPU is removable interpreter work.

The source/binding census supplies concrete amortization units:
1,087 observed source/version modules, 1,688,008 module lookup calls,
and 391,187 item-operation bindings at 9,146 sites (42.8/site).
Shared module lowering takes about 0.593 diagnostic CPU seconds;
lookup calls must not be mistaken for repeated source compilation.
Residual property execution covers 1,942,327 assignments at 4,336 sites,
with lexical shapes and output-size distributions recorded separately.
Constant replay and scalar fallback are outside that property census.

Metadata has 441,966 template bindings, 1,746,839 decisions (935,198
conditional), and 922,341 successful explicit values. Of these values,
99.26% originate at compiled-supported source sites, and 80.10% already
complete through operation-local specialized reuse; generic fallback
completions account for 0.744%. Seventeen property/metadata sites supply
82.92% of explicit values. Their cost must include decisions, current
reads, and ordered writes, not just string expansion. Bulk constant
metadata is excluded from explicit-value denominators. The 72,702,931
metadata output characters and measured size histogram price a
different mixture from the tiny reference fixture.

Actual Unix heuristic probe requests total about 2.551 million; 296,569
permission checks reach an environment-read branch. Counts distinguish
binding, value, condition, property-program, and other regions. They
neither price effects nor permit their deletion or process-wide caching.
Required observations and their ordering remain part of each contract.

Whole-region timing conserves exclusive CPU exactly. A bounded execution
follow-up finds 6.947 CPU seconds inside 1,654 project-build roots, all on
one thread, split without double counting as:

| Covered execution service | CPU s |
| --- | ---: |
| Project work outside selected children | 0.177 |
| Target traversal outside bodies | 1.058 |
| Target-body work outside selected children | 1.541 |
| Intrinsic groups | 0.619 |
| Project logging/state-view creation | 1.458 |
| Task service and resolution, including 0.475 s of bodies | 2.094 |

The same diagnostic build session uses 17.249 process-CPU seconds.
Its 10.302-second CPU complement includes unpriced preparation,
runtime/workers, scheduling, and other work; it is not all target
overhead. All forests have zero incomplete scopes. Two repeats per mode
and variation in off controls prevent an exact observer-overhead
estimate. The preceding timing pair's 1.983 task CPU seconds belongs to
that pair, not an additional charge to this execution ledger.

**Price a request-owned activation frame next.** A source-prepared
target/task plan should bind to current state in its request/configuration
scope; logging and public object APIs should receive explicit compatible
views. The measured logging factory includes a getter that calls compact
property materialization before deciding what the logger needs. That is
a representation transition to price, not proof that materialization
explains the entire 1.458 seconds. Separate required snapshot/serialization
work from frame activation and avoid forcing object views unnecessarily.
Retain copy-on-read snapshots, mutable public APIs, ordering, lifecycle
callbacks, target batching, and all existing outputs.

Use the provisional one-second task CPU allowance and separately price
the other serialized execution work against its 1.5-2.0-second elapsed
envelope. A complete operation-owned metadata program/frame is the
evaluation counterpart: source preparation, scope-valid binding, then
current-item conditions/reads/ordered writes with effects preserved.
Neither frame has a complete workload-weighted useful-work price yet.
The next prototype must measure those units and remaining effects, not
claim success from lower allocations or a narrower value-expansion
hotspot. Source preparation is already shared; evaluated-project and
task-result caches are not part of this plan.

### Activation-frame investigation: compact state and compatible views

The [activation/property-view investigation](evaluation-perf.md#request-activation-and-property-view-investigation)
narrows the frame contract. All 1,654 project requests arrive already
loaded, and each configuration creates one base lookup. Logging first
converts **2,034,153 compact properties** into objects, producing
2,384,682 base entries. Lookup construction then sees no compact entries.
Both paths currently require the object dictionary, so deferring the
logging conversion alone would only relocate it.

The diagnostic prices separate useful snapshot work from representation
conversion:

| Activation/view boundary | CPU seconds |
| --- | ---: |
| Project activation outside the project-build scope | 0.011 |
| Separate operating-environment initialization | 0.133 |
| Base lookup construction, including empty drain | 0.0047 |
| Nonempty compact conversion inside logging | **1.035** |
| Required eager filtered logging property snapshot | **0.361** |
| Logging factory including conversion, snapshot, event/context | 1.441 |

These rows contain nesting and are not an additive ledger. Conversion
averages 509 ns/entry, including validation, draining, object creation,
dictionary insertion, and probes. It allocates 276.660 MB, but serialized
CPU is the primary motivation. Full CPU/current-thread-allocation forests
reconcile exactly. Basic frame entry is already small; a cheaper request
object is not the foundation that addresses this measured charge.

A separate named-demand census covers registered base-dictionary reads
through both execution lookup and direct project APIs. Both captures
agree on 178,956 hits, 279,749 misses, **72,705 distinct successful
names summed per configuration**, 38,887 base writes, and 26,333 distinct
written names. At most 75 distinct successful names occur per
configuration. The distinct read total is 3.049% of initial base entries
and an upper bound of **3.574% of initial compact entries**. It includes
object-backed and newly written names; bulk logging/public enumeration
and translation are outside that denominator. Per-configuration
distributions are not identical, so use aggregate counts, not inferred
exact per-site demand. A stale first diagnostic capture was rejected,
preserved, and replaced with verified probe-containing assemblies.

The bounded prototype should therefore carry **configuration-owned
compact base state, request/batch overlays, and explicit compatibility
views**. Preserve batch shadowing/merge order and ensure writes remove
or supersede old compact values. Produce the required logging snapshot
without converting every property to a public object, while keeping
its existing capture timing, environment filtering, escaping, global
properties, deferred item views, and event ordering. Materialize stable
mutable property objects on actual demand, retaining full barriers for
bulk object APIs and unsupported translation/other boundaries.

This replaces a representation charge; it is not a cache of evaluated
results. The model must price activation/environment work, snapshot size
and escaping, named hit/miss/scope-depth costs, actual object demand,
writes/merges, and fallback barriers. The 1.035 CPU seconds is not a
guaranteed wall saving, and the 0.361-second required snapshot is not
free. Remaining target/task service and process residuals still need
separate work.

In this timing pair, request-thread roots cover 7.106 CPU seconds and
separate environment roots 0.133 out of 17.062 build-process CPU
seconds. The **9.824-second unassigned CPU complement** remains
unpriced, not labelled interpreter overhead. Build GC pause totals about
0.176 seconds; this does not attribute GC CPU or establish an additive
critical-path account. All ten valid activation/demand captures match
the existing fresh 760-asset reference. Observer variation precludes
inferring throughput gains. These captures used diagnostic builds without
a frame implementation. A first frame prototype was committed afterward in
`c39c6b90e` (opt-in through compiled module effect batching; see the branch
table below). It has not yet been priced or macro-measured.

### Historical design inputs

The historical restore extraction corpus had about 636 physical projects,
1,642 configured project evaluations/builds, and 16,045 non-intrinsic task
invocations. Actual task bodies used about 0.59 seconds in aggregate. An
immutable graph could be represented and traversed in roughly 0.4 seconds
*after* its inputs existed; this does **not** price evaluating projects to
discover those inputs. Roughly 80% of observed file/input combinations
recurred across configurations. These observations motivate sharing
source-derived programs and specializing common operations, not reusing
evaluated results solely by physical project path: different configurations
can produce different state.

At 16,045 invocations, a one-second **complete** task-pipeline budget allows
about 62 microseconds per task. Bodies and constructors leave only about
25-31 microseconds (70,000-85,000 cycles on the 2.8 GHz benchmark machine)
for engine machinery in the historical budget. Repeated task resolution,
reflection, name lookup, generic parameter/output traversal, and cross-thread
logging handoffs cannot all remain on that common path. The approximate
two-second end-to-end bound is an aggressive architectural thought experiment,
not a measured lower limit or a release target. A 5-6-second restore is the
practical *goal*, allowing for compatibility, filesystem work, scheduling,
startup, and NuGet work:

| End-to-end layer | Design budget |
| --- | ---: |
| Graph construction and project evaluation | ~1.5 s |
| Complete executable-task pipeline, including bodies | <=1.0 s |
| Project, target, and intrinsic work outside tasks | ~1.5-2.0 s |
| Process, SDK/NuGet, assets, and finalization | ~1.0-1.5 s |
| **Total** | **~5-6 s** |

These are targets for a *new cost structure*, not savings to subtract from a
single run. The historical direct NuGet extraction process took 23.93 seconds:
8.60 seconds graph construction/evaluation, 14.07 seconds serialized
extraction build, and 1.26 seconds setup/finalization/restore work. Within
the 14.07-second build, 6.85 seconds were the complete task pipeline;
the other 7.22 seconds included build-session orchestration (1.92),
project setup/teardown (1.58), target stack/dependencies/skips (0.96),
other target-body work (1.76), and intrinsic item/property groups (1.00).
These numbers came from a different checkout, SDK, and capture than this
branch's recent ~22-25-second restores. They locate work but do not establish
current progress toward the goal or a current per-layer budget gap.

## Current restore baseline (October 2026)

The initial comparisons in this section used local Debug assemblies.
A later configuration check confirmed that those assemblies disable JIT
optimization. Their paired results describe that configuration, not
Release throughput. The latest same-commit configuration comparison at
`8b41df437` measured **17.738 seconds in Release versus 19.460 seconds in
Debug**, with a paired wall saving of 1.722 seconds (95% CI
[1.357, 2.086]) across six balanced pairs. Release used more aggregate
user CPU in that cohort. This is a configuration comparison, not a new
ancestor-versus-candidate attribution. Published MSBuild already uses
Release assets: switching the local measurements to Release corrects a
measurement mistake and delivers no product throughput improvement.
Use optimized assemblies for
subsequent throughput decisions; see
[throughput profiling and optimized baseline](evaluation-perf.md#throughput-profiling-and-optimized-baseline)
for the evidence and attribution limits.

The control is the clean common ancestor with this branch, `1eecb3d38`, not
current upstream `main`. The candidate is the clean committed `27a86bf`
tree; the uncommitted prefix-state prototype is excluded. Both engines ran
from the same isolated SDK against `dotnet/runtime` static-graph restore with
the same package cache and asynchronous logging. The "opt-in" candidate sets
compiled module evaluation, module effect batching, and action-graph partial
evaluation; "default" leaves those three switches unset. After warmup, ten
balanced, counter-free pairs per comparison gave:

| Comparison | Stock mean | Candidate mean | Paired wall saving (95% CI) |
| --- | ---: | ---: | ---: |
| Stock vs. candidate opt-in | 31.313 s | 22.623 s | 8.690 s [7.929, 9.451] |
| Stock vs. candidate default | 30.544 s | 24.684 s | 5.860 s [5.390, 6.330] |

These are separate stock cohorts, not a paired default-versus-opt-in
comparison or per-commit attribution. Opt-in saved 58.740 seconds of paired
user CPU but raised peak RSS by a mean 176,382 KiB. Candidate default saved
wall time while using 27.533 seconds *more* paired user CPU. All sampled
restores completed; the 760 `project.assets.json` path/hash pairs matched
across stock, default, and opt-in diagnostic snapshots. This establishes
output equivalence for those assets, not complete graph or diagnostic parity.
At 22.623 seconds, the opt-in path is still roughly 16.6-17.6 seconds above
the 5-6-second design goal.

Diagnostic graph-construction events measured 14.42 seconds for stock,
15.58 seconds for candidate default, and 9.85 seconds for candidate opt-in
in one run each. These runs enabled EventSource and are **not** the
counter-free wall samples. Stock has no matching evaluation/execution meters.
Two additional opt-in diagnostics using the candidate's coarse evaluation
events measured graph construction at 9.68-9.84 seconds, with 173-177
seconds of *process CPU* and 9.32 GiB of managed allocations in that
interval. In those runs, thread-local evaluation-pass accounting covered
1,655 evaluations:

| Opt-in evaluation pass | Aggregate thread CPU | Managed allocation |
| --- | ---: | ---: |
| Properties and imports | 65.6-67.2 s | 4.17-4.18 GiB |
| Items, including lazy application | 67.0-68.5 s | 3.99-4.00 GiB |
| All evaluation passes | 145.3-149.0 s | 9.06-9.07 GiB |

Pass CPU is summed over concurrent threads, **not** elapsed graph wall time.
The pass allocation counts include instrumentation and represent the work
performed in each synchronous pass, not objects still live after restore.
Corresponding candidate-default runs put the two large passes at roughly
112-117 and 103-108 seconds of aggregate thread CPU and 5.95 and 6.99 GiB
of allocations. The opt-in's graph/evaluation interval remains much larger
than the ~1.5-second design budget. Its diagnostic `BuildSession` scope
lasted roughly 11-12 seconds, but these nested/parallel measurements are
not a precise additive wall-time decomposition.

An isolated, instrumented candidate further split item applications by
operation and item type. With the standard meter listener **disabled** to
avoid charging its own counter callbacks to item conditions, two runs found
that the 1,655 `ProjectToBuild` `Update="@(ProjectToBuild)"` applications
allocated about 702 MiB; metadata decoration accounted for **652 MiB
(93%)**. Within decoration, compiled metadata conditions allocated about
27 MiB, metadata value expansion about 294 MiB, and metadata writes about
149 MiB. The remaining allocation includes diagnostic scopes and other
metadata-loop work, not an identified product hot method. `Compile` Update
decoration allocated about 114 MiB; its scalar metadata conditions in the
SDK's default-item targets are a separate cost. Earlier runs with the
standard meter listener enabled charged hundreds of additional MiB to
these same scopes, illustrating observer bias. The diagnostic-only item
probes still perturb CPU and allocation; no per-operation wall-time saving
has been established. All 760 asset hashes matched the candidate control.

The first bounded item-expansion improvement removes a Unix helper's
character-array copy for values with no slash characters. It halves measured
`ProjectToBuild` metadata-expansion allocations (about 147 MiB saved per
restore) without changing metadata write order or filesystem decisions.
Six counter-free restore pairs have an inconclusive wall result; see the
[detailed measurements](evaluation-perf.md#avoid-copying-nonpath-metadata-expansions-on-unix).

## Partially evaluate the existing interpreter

Treat MSBuild XML and the evaluated object model as the source language and
compatibility surface. Bind facts invariant across evaluations to internal
immutable programs; run the residual program against configuration-local
properties, items, filesystem, and request state:

```text
project/import source -> shared source-derived module
                      -> configuration-local evaluation state
evaluated targets     -> project-local compiled target/task plans
                      -> request-local execution state
```

This is program specialization, **not** memoization of an `Exists` result,
an import decision, a task result, or a whole evaluated project. Immutable
source programs can be reused even when every filesystem probe and dynamic
condition must run again. A shared typed expression substrate should keep
scalar strings, item identity/metadata, and Boolean short-circuiting distinct;
an untyped bytecode VM or a large language rewrite is not a prerequisite.
The existing `Expander` and evaluator remain semantic fallbacks.

Evaluation preserves the ordered passes: initial properties; properties,
imports, and `Choose`; item definitions; ordered item operations; task
registration; target declarations. Imports and `Choose` select the
configuration-specific active closure. A compiled module cannot flatten
these edges in advance. It must retain source locations, escaping,
conditioned-property and predecessor behavior, loaded-project-cache policy,
logging, duplicate/circular-import checks, SDK resolution, globs, and
contextual filesystem observations. Changed source or location invalidates
source-bound programs.

At execution time, task registration is project-local, task-type metadata
can be shared within the owning assembly-load context, and each task still
gets its own constructor, parameter setters, body, output getters, events,
and cleanup in the original order. Compiled plans replace repeated binding
and interpreter dispatch, not observable user code. Unsupported forms use
the existing path before partially executing a site; target-level plans
must similarly fall back before emitting events or changing lookup state.

## What this branch actually contains

| Area | Branch state | Evidence |
| --- | --- | --- |
| Evaluation modules and typed property/condition/item programs | Committed, opt-in, with scalar fallback and existing semantics | `fba1a007a`, `src/Build/Evaluation/EvaluationModule.cs`, `CompiledExpressionProgram.cs` |
| Standard evaluation/execution telemetry | Committed; detailed instrumentation is diagnostic only | `53a6c4838`, `EvaluationPerformanceInstrumentation.cs` |
| Task type metadata and direct task actions | Committed, with whole-site fallback for unsupported forms | `8f63c4fc7`, `51e37747f`, `src/Build/BackEnd/Components/RequestBuilder/CompiledActionGraph.cs` |
| Target plans | Committed target dependencies and ordered body actions; **not** the proposed direct handling of all pre-body decisions and up-to-date partitions | `a4b2fda75`, `CompiledActionGraph.cs`, `TargetEntry.cs` |
| Async logging notification coalescing | Committed; retains ordered events | `02aa3e5bf`, `src/Build/BackEnd/Components/Logging/LoggingService.cs` |
| Constant-literal item batching | Committed | `63c870657` |
| Source-bound fixed imports and `Exists` arguments | Committed, opt-in; results and filesystem observations remain dynamic | `3221e3cdb`, `80b4865ef`, [measurements](evaluation-perf.md) |
| Separator-free path-adjustment fast exit | Committed, default-on; filesystem-dependent paths retain the existing heuristic | `f524becbc`, [measurements](evaluation-perf.md#avoid-copying-nonpath-metadata-expansions-on-unix) |
| Operation-local metadata-expression specialization | Working implementation, existing effect-batching opt-in; folds invariant regions while retaining dynamic metadata and read effects | [phase measurements](evaluation-perf.md#specialize-repeated-item-metadata-expressions); macro impact not established |
| Compact-state request activation frame | Committed prototype, active only with compiled module effect batching: the base `Lookup` reads compact properties and supersedes them on merge; the `ProjectStarted` snapshot is built without converting compact properties to objects. Compact properties come only from module property templates, so environment filtering and enumeration order match the old materialize-then-filter path. | `c39c6b90e` (committed under a "docs" message), `Lookup.cs`, `ProjectLoggingContext.cs`, `ProjectInstance.GetPropertiesForLogging`; not yet priced or macro-measured |
| Prefix property-state attachment | **Uncommitted prototype**, with zero hits on the measured restore workload; not part of a proven branch gain | [partial evaluation](partial-evaluation.md); the local `prefix-state-attachment.md` draft is not committed |

The existing opt-ins include
`MSBUILDENABLECOMPILEDMODULEEVALUATION=1`,
`MSBUILDENABLECOMPILEDMODULEEFFECTBATCHING=1`, and
`MSBUILDENABLEACTIONGRAPHPARTIALEVALUATION=1`. Notification coalescing
matters to the acceptance workload when asynchronous logging is selected.
These switches are experimental controls, not evidence of default-on gains.

The coalescing experiment reduced logging start/finish cost from 87.939 to
7.994 microseconds per task and its measured fast-task site from 219.043 to
125.213 microseconds. These are local results from an earlier workload
generation, not additive restore savings.

The committed fixed-import specialization eliminated 88.9% of measured
argument-preparation time but its six-pair wall result was inconclusive.
The `Exists` specialization removed 72.4% of preparation time and 79.4% of
preparation allocations in its diagnostic comparison; its six-pair restore
result *trended slower* and cannot rule out a regression. Both measurements
were opt-in and made in different runs. The accumulated branch now has the source-aligned all-up measurement above,
but its stock control and experimental setup differ from the August
23.93-second breakdown and the other branch's 19.498-second candidate.
Do not sum local phase savings or project them onto restore wall time.

## Next boundaries under classic semantics

Order of work, from the [per-unit accounting](#where-the-gap-comes-from-per-unit-accounting):

1. **Run a combined Release macro comparison** (default and opt-in against
   `1eecb3d38`) and explain the default path's extra CPU.
2. **Decide hypothesis A versus B for evaluation** with instruction/cycle
   counts at full and single-thread parallelism, the executable floor,
   and the binding-time census. The results choose between more control
   specialization and a shared-base state representation, and say
   whether polyvariant prefix sharing can hit.
3. **Split the uncovered ~2.3 s on the serialized request path.** Count
   and price `MSBuild`-task round trips and scheduler waits, then decide
   whether to cut per-request latency or leave the single in-proc node.
4. **Price the activation-frame prototype** against its useful-work model.

The specialization boundaries below remain valid but come after those
decisions:

1. **Reduce item-application metadata work.** The opt-in item pass consumes
   roughly 67-69 aggregate CPU seconds and 4 GiB of allocations across the
   restore, marginally ahead of properties/imports in CPU. Per-item metadata
   value expansion and writes in `ProjectToBuild` Updates are measured
   allocation hotspots; `Compile` Updates spend most of their measured
   decoration cost on SDK scalar conditions with property functions.
   The first operation-local residual expansion program reduces measured
   `ProjectToBuild` expansion CPU by about 29% in two diagnostic pairs,
   with about 1 MiB of extra binding allocation in that section. Measure
   accumulated macro performance and investigate remaining metadata
   representation cost without replaying item-specific results or
   suppressing observable writes. Preserve item-local ordering, predecessor
   history, qualified metadata, and filesystem observations.
2. **Continue specializing properties/imports.** This pass is effectively
   tied with items (about 66 aggregate CPU seconds and 4.18 GiB of
   allocations). Separate remaining scalar-condition and property-state
   construction cost before choosing a larger source-derived segment.
   Explore coarse immutable prefix-state attachment only where live-ins,
   global-property precedence, source identity, and logging can be matched;
   the current root-only prototype removes no work in this restore. Dynamic
   imports and `Exists` results must still be observed, not memoized.
3. **Lower common target pre-body and intrinsic work.** The current target
   plans cover body actions but do not establish a direct path through every
   condition, dependency, batching, `Inputs`, `Outputs`, and up-to-date
   decision. An eligible plan may preflight those decisions and then execute
   the existing ordered actions; unsupported decisions must use the original
   `TargetEntry`/`TargetUpToDateChecker` path before effects. First prove
   semantic parity for skips, incremental partitions, events, and errors.
4. **Expand the direct task pipeline where measured.** Address the remaining
   generic in-process task shapes, output/condition/batching handling, and
   per-invocation host setup without changing task construction, observable
   setter/getter order, cancellation, logging, or factory behavior.
   Whole-site fallback remains the compatibility gate.

Avoid another fine-grained property/condition **result replay** cache: the
historical experiment had a 96.7% hit rate yet slowed restore from
29.22-29.80 to 33.13-36.23 seconds because lookup, validation, publication,
and replay outweighed the scalar work. Compact tables or dispatch alone also
did not consistently improve wall time while property objects and inserts
still had to be built. These results favor eliminating a *larger interpreter
segment* or its materialization costs over chasing hit rates.

## Acceptance and scope

Keep the source, SDK, workload, package cache, and feature switches identical
within a comparison. Use alternating or balanced control/candidate pairs;
record process wall, CPU, RSS, dispersion, and confidence intervals. Attribute
phase time separately from listener-free wall: aggregate spans across
parallel evaluators overlap and are not wall-time savings. Check project and
task counts, graph topology, diagnostics, import behavior, and normalized
restore assets. Preserve default and design-time behavior, cross-platform
path handling, task assembly unloading, and standard fallback. Retain small,
bounded semantic-preserving specializations on repeatable phase savings and
modest complexity even when a single macro test is inconclusive; investigate
credible regression signals and remeasure accumulated end-to-end performance.

This plan does **not** depend on the separate pure-evaluation mode, a
Declared-I/O task-result cache, hardened static-graph validation, a
persistent build server, a NuGet-specific evaluator, or bottom-up artifact
demand cuts. Those change contracts or belong to other workstreams.
