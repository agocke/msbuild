# Evaluation performance investigations

MSBuild evaluates projects in every build scenario, and static-graph restore
evaluates many configurations of the same physical projects. This page records
opt-in experiments that specialize source-derived work while preserving
ordinary evaluation behavior. Local phase savings do not automatically
translate into process wall-time savings. See the
[classic restore performance plan](restore-performance-plan.md) for the
bottom-up cost budget, historical context, and current branch status.

The objective is unchanged results with less interpreter and state-construction
work, including imports, filesystem observations, conditions, diagnostics,
and public object-model behavior. Unsupported shapes use the ordinary path.
See [evaluation performance dead ends](evaluation-performance-dead-ends.md)
for removed experiments and their measured outcomes.

## Fixed-import specialization prototype

The compiled-module effect-batching opt-in now specializes ordinary single-file
imports of the form `$(MSBuildThisFileDirectory)` followed by a literal suffix.
Lowering binds the expression to its source file, and the first successful
condition evaluation prepares the normalized path for subsequent evaluations of
that module. This specializes argument preparation, not the evaluated result:
conditions, project loading, duplicate/circular-import handling, logging, and
imported property effects still execute through the existing evaluator.

SDK imports, dynamic suffixes, escaping, lists, wildcards, and paths requiring
the filesystem-dependent Unix backslash-adjustment heuristic fall back to the
ordinary path. Toolset import-search fallback remains unchanged. Path
normalization is deferred until after the condition succeeds to preserve error
timing. Source edits and file relocation invalidate the module and its binding.
The prototype uses the existing `MSBUILDENABLECOMPILEDMODULEEVALUATION=1` and
`MSBUILDENABLECOMPILEDMODULEEFFECTBATCHING=1` switches.

On the runtime static-graph restore workload with SDK
`11.0.100-rc.1.26420.103`, four diagnostic runs observed 77,052 eligible import
preparations per restore. The specialized runs normalized just 67 paths.
Cumulative preparation elapsed time averaged 5.173 seconds on the ordinary
path versus 0.574 seconds with specialization, an 88.9% reduction. These are
summed elapsed durations across parallel evaluators, not wall-time savings;
temporary diagnostic instrumentation was removed after measurement.

Twelve uninstrumented restores, arranged in six balanced pairs after warmup,
used the same isolated SDK path with only the engine assembly exchanged:

| Metric | Ordinary path | Specialized path |
| --- | ---: | ---: |
| Mean restore wall time | 25.090 s | 25.463 s |
| Mean user CPU time | 167.190 s | 164.588 s |
| Mean system CPU time | 16.193 s | 14.642 s |

The paired wall-time improvement was -0.373 seconds, with a 95% Student-t
confidence interval of -2.916 to 2.169 seconds. This does **not** establish an
end-to-end improvement or regression. The phase measurements demonstrate
substantial elimination of repeated interpreter machinery, but a repeatable
restore wall-time benefit remains an open measurement gate.

For bounded, semantics-preserving specializations, repeatable phase-level
savings and modest implementation/memory cost justify retention even when an
individual macro result is inconclusive. Evaluate restore wall time across
accumulated changes rather than require a large gain from each specialization.
Phase savings are not necessarily additive: parallelism, GC, and bottleneck
shifts can change how much reaches the critical path.

## Source-bound Exists specialization

The same effect-batching opt-in binds single-path `Exists` and `!Exists`
arguments based on `$(MSBuildThisFileDirectory)` plus a literal suffix.
These predicates use the existing compiled condition branches, including
short-circuiting `And`/`Or`, and apply to property, import, and metadata
conditions. Their argument strings are shared module constants rather than
expanded task items and a temporary file list on every execution.

The existence result is not cached by the specialization. Each execution uses
the same filesystem and, where the ordinary evaluator supplies it, loaded
project cache. Existing filesystem caching policy is unchanged. Bound argument
strings retain the ordinary path representation; they are not independently
canonicalized. Unix separator fixing happens before binding, just as it does
before ordinary `Exists` expansion. Dynamic suffixes, escaping, lists,
unsupported surrounding expressions, and unsaved source files fall back.
Source relocation or edits invalidate the bindings. Filesystem predicates are
not eligible for the pure prefix-state attachment prototype.

Two diagnostic restores per variant observed 119,218 ordinary `Exists` calls
before specialization, including 6,148 source-directory-based arguments.
The narrow lowering specializes 4,727 executions; 1,421 retain ordinary
argument preparation. Including those fallbacks, the measurements were:

| Argument preparation | Ordinary path | Specialized path |
| --- | ---: | ---: |
| Mean cumulative elapsed time | 430.162 ms | 118.520 ms |
| Mean allocated bytes | 20,630,844 | 4,256,364 |

This removes 72.4% of measured preparation elapsed time and 79.4% of measured
allocations, about 16.4 MB per restore. Thread-local allocation measurements
exclude filesystem probes and module lowering; bound preparation measured zero
steady-state allocations. Temporary instrumentation was removed afterward.

Twelve successful uninstrumented restores in six order-balanced pairs used the
same isolated SDK path and command as the import experiment, with only the
engine exchanged. One invocation with a mistyped switch failed before restore
and was excluded and rerun. Mean wall time was 22.230 seconds for the baseline
and 23.215 seconds for the specialization. The paired improvement was -0.985
seconds, with a 95% Student-t interval of -2.294 to 0.324 seconds. This sample
trends slower and does not establish a macro gain or rule out a regression.
The retained opt-in prototype has demonstrated local work/allocation savings,
not an end-to-end performance win.

Four follow-up restores with the existing evaluation meters enabled found
unchanged executed property-batch, property-effect, expansion, and function
counts. Exactly 4,727 condition evaluations moved from scalar to compiled
handling. This did not reveal broader property-batch activation as an
explanation for the slower macro sample. These observer-heavy, overlapping
phase timers are diagnostic evidence, not additional macro measurements.

## Avoid copying nonpath metadata expansions on Unix

Item-application attribution found that the compiled metadata expansion path
calls `FileUtilities.MaybeAdjustFilePath` for each result. On Unix, both its
string and memory overloads copied every nonempty, non-exempt value into a
character array, even if the value contained neither `/` nor `\`. A new
early exit returns such values unchanged before allocating. Values containing
either separator still execute the existing filesystem-dependent heuristic.
This is an internal allocation optimization, active by default; it does not
bind properties early, cache filesystem decisions, or merge metadata writes.

Two balanced diagnostic pairs used the committed `27a86bf` candidate with the
three evaluation/action-graph opt-ins enabled. Only the Framework assembly
containing the helper was exchanged. Identical operation-level probes ran in
both variants, with standard meter listeners disabled to avoid charging their
callbacks to metadata evaluation. The 777,850 `ProjectToBuild` metadata
expansions per restore measured:

| Metric | Existing helper | No-slash early exit |
| --- | ---: | ---: |
| Mean allocated bytes | 307,994,988 | 153,863,096 |
| Mean aggregate thread CPU | 4.095 s | 3.542 s |

This eliminates about **147 MiB (50.0%)** of measured expansion allocations.
Across all profiled item operations, exclusive managed allocation fell by
about 195 MiB. The aggregate CPU values are diagnostic observations across
parallel evaluation threads, not wall-time savings or a statistically
established CPU improvement. The probes themselves add overhead.

Six balanced, counter-free restore pairs after one warmup per variant gave:

| Metric | Existing helper | No-slash early exit |
| --- | ---: | ---: |
| Mean restore wall time | 25.018 s | 24.722 s |
| Mean user CPU time | 174.618 s | 169.262 s |
| Mean system CPU time | 17.688 s | 17.333 s |

The paired wall saving was 0.297 seconds, with a 95% Student-t interval of
-1.379 to 1.972 seconds: **no end-to-end gain or regression is established**.
The paired user-CPU saving was 5.357 seconds, with an interval of -2.206 to
12.920 seconds. Both macro comparisons are inconclusive. Every restore
succeeded, and all saved 760 asset path/hash pairs matched the candidate
control. The bounded change is retained for its repeatable allocation savings,
not a claimed macro win. Further operation specialization remains separate.

## Specialize repeated item metadata expressions

The existing compiled metadata-value interpreter already lowers literal,
property, and metadata references. An operation-local residual program now
folds each maximal contiguous literal/property region, including interior
regions, against the finalized item-pass property environment. Current-item
and captured qualified metadata remain dynamic. Short residual expressions
execute direct reads and `string.Concat`; longer expressions retain an
ordered builder loop.

Binding happens only after a metadata condition succeeds and after the
operation has already decorated an item. Single-part expressions retain
their existing fast path. The first binding evaluates source parts in their
original order. Materialized property references are retained as residual
read observers, preserving environment-read notifications and the last-read
location used by BuildCheck without repeating property lookup. Compact
property values bind without materializing property objects. Programs belong
to the lazy item operation, not the shared module or another configuration.
Conditions, writes, predecessor history, expansion-syntax fallback, and the
filesystem-dependent path-adjustment heuristic remain at runtime. This is
part of the existing compiled-module effect-batching opt-in.

The first eager prototype added about 53 MiB of exclusive item-operation
allocation by binding many single-use expressions. Delaying binding removed
that cost, but retaining property lookup for every residual observation did
not show repeatable expansion-CPU savings. Binding property observers
separately from their stable values produced the following two balanced
diagnostic pairs, with identical item probes and standard meters disabled.
Both variants include the committed no-slash helper; only the engine assembly
was exchanged. Each NuGet child executed 777,850 `ProjectToBuild` metadata
expansions and 1,655 Update decorations:

| Metric | Previous interpreter | Bound residual program |
| --- | ---: | ---: |
| Mean expansion aggregate thread CPU | 3.002 s | 2.140 s |
| Mean expansion allocated bytes | 153,869,796 | 154,855,360 |
| Mean Update decoration aggregate thread CPU | 13.224 s | 12.114 s |
| Mean Update decoration allocated bytes | 501,648,660 | 502,618,108 |

Expansion CPU fell in both pairs (by 0.925 and 0.797 aggregate seconds),
averaging about **0.861 seconds (28.7%)**, with about **0.94 MiB** additional
allocation for binding in that section. Across all profiled item operations,
exclusive allocation was about 2.8 MiB lower on average; this small difference
is not attributed wholly to the specializer. All four restores succeeded and
all 760 asset path/hash pairs matched.

These are instrumented, aggregate thread-CPU observations, not graph elapsed
time or a statistically established CPU or restore wall-time improvement.

Ten subsequent counter-free, order-balanced restore pairs after one warmup
per variant compared the previous interpreter plus the committed no-slash
helper with the residual-program implementation. Both variants ran from the
same isolated SDK with the same package cache, asynchronous logging, and
three evaluation/action-graph opt-ins. Neither engine contained the temporary
item probes; no meter listener, startup hook, or binary logger was enabled.
Only `Microsoft.Build.dll` changed between variants:

| Metric | Previous interpreter | Bound residual program | Paired saving (95% CI) |
| --- | ---: | ---: | ---: |
| Mean restore wall time | 19.612 s | 19.517 s | 0.095 s [-0.185, 0.375] |
| Mean user CPU time | 138.168 s | 137.549 s | 0.619 s [-2.423, 3.661] |
| Mean system CPU time | 12.829 s | 12.883 s | -0.054 s [-0.657, 0.549] |
| Mean peak RSS | 2,129,199 KiB | 2,163,379 KiB | -34,180 KiB [-95,104, 26,744] |

The mean wall saving is about **0.5%**, but its paired Student-t interval
includes zero: **no end-to-end gain or regression is established**. The
user-CPU comparison is also inconclusive. Peak RSS averaged about 33.4 MiB
higher, with an inconclusive paired interval. All 22 restores, including
warmups, succeeded, and every snapshot's 760 asset path/hash pairs matched.
These samples are a new paired cohort; their absolute times should not be
compared directly with earlier experiments to attribute additional savings.
The phase-level CPU reduction remains the demonstrated benefit.

## Data-model representation measurements

A subsequent measurement-only investigation used committed engine
`8b41df437` and the same package-cached runtime static-graph restore, isolated
SDK, asynchronous logging, and three evaluation/action-graph opt-ins.
Thread-local histograms lived in a separate diagnostic checkout; no
representation change was implemented. Two repeat distribution runs were
separate from two allocation traces of the uninstrumented committed engine.
The traces attached to the actual NuGet child while a startup hook paused it
before its main method, then released it after collection began. They enabled
the CLR `gc-verbose` profile and coarse `Microsoft-Build` evaluation events,
not the standard meter listeners or a binary logger.

Both distribution runs published **1,587,184 item instances** across 1,656
evaluations. These are per-configuration populations, not unique source items
or a simultaneous live-heap count. Direct metadata averaged 1.345 entries:
51.49% of published items had none, 93.05% had at most four, and 96.35% had at
most eight. `ProjectToBuild` contributed 134,136 published items, averaging
2.20 direct entries with a maximum of four; `Compile` contributed 53,164,
averaging 0.32 with a maximum of three. Direct counts exclude inherited
metadata and built-in modifiers. For example, each published `ProjectToBuild`
item also carried three item-definition references.

Evaluation-time factory copies and task-item clone paths were both
instrumented. Of 450,702 evaluation-time copies, 353,306 had nonempty direct
metadata; **every one shared its source's direct backing**. This supports
retaining existing copy-on-write sharing rather than treating item cloning
as a deep metadata-copy problem. Single `ProjectToBuild` metadata writes
included 134,136 new names and 644,184 changed existing values; no unchanged
write was observed on that instrumented path. Small-map mutation, rather
than eliminating redundant same-value writes, is the relevant hypothesis.

Each allocation trace covered one complete graph-construction interval,
recorded approximately 67,500 allocation ticks with stacks, and reported
zero lost events. Allocation-tick weights totaled about 7.30 GB for the
NuGet child, including about 5.98 GB during graph construction. These are
sampled allocation attributions, **not exact object counts, retained bytes,
or CPU costs**; type attribution in particular can be sensitive to sampling.
Means of the two stack-attributed samples were:

| Allocation stack family | Mean sampled allocation | Interval |
| --- | ---: | --- |
| Compact-property `Set` and `Apply` | 429.5 MiB | Graph construction |
| Ordered item collections | 209.4 MiB | Graph construction |
| Task-item `SetMetadataObject` | 149.0 MiB | Graph construction |
| Compact-property materialization | 271.8 MiB | After graph construction |

These families are not an exhaustive allocation budget. Nested stack
families must not be added again: draining compact properties, for example,
is already included in property materialization. Compact dictionary
entry-array types accounted for approximately 380.2 MiB of the graph's
sampled allocation. The current compact environment maintains separate
value and name dictionaries keyed by the same property ID.

Compact storage persisted through evaluation: the final populations
contained **2,040,476 compact properties** and 339,856 ordinary property
objects. All 1,655 evaluation-time drain requests were empty. After
evaluation, 3,310 requests through `PropertiesToBuildWith` drained the
2,040,476 compact entries; half of those requests were empty. The allocation
stacks identify project-start logging as the caller of the allocating
materialization, through `ProjectLoggingContext.CreateLoggingContext`.
This exposes a compact-to-object boundary worth studying, not proof that
materialization can simply be removed: logging payloads and subsequent
execution consumers still require their existing semantics.

For ordered items, persistent list nodes accounted for approximately
135.4 MiB of graph allocation. Builder `Add` dominated the collection
origins, at approximately 114.4 MiB, versus 21.0 MiB for indexed replacement
and 6.9 MiB for `ToImmutable`. Snapshots therefore are not demonstrated to
be expensive full copies. Any alternative needs to preserve order,
duplicates, old-snapshot isolation, and efficient incremental construction.

Expanded item-spec caching served about 85.84% of 216,526 lookups from
existing entries. Approximately 30,623 actual inserted keys averaged
115.36 characters, with a maximum of 6,079. Module-construction observations
varied slightly with concurrent work; they recorded about 1,160
constructions and approximately 71,000 string-table slots. Neither these
counters nor the clone-sharing observations establish cache lifetimes,
unique backing populations, or retained-heap efficiency.

These measurements identify bounded representation candidates, not a
public object-model rewrite or a throughput ranking. **Throughput is the
primary objective; memory efficiency is secondary.** Prioritize candidates
using CPU attribution and critical-path measurements, retaining repeatable
phase-level CPU savings and measuring accumulated restore wall time.
Duplicate compact-property dictionaries, small direct metadata maps, and
logging/execution materialization remain hypotheses until their throughput
cost is established. Allocation reductions matter to this objective when
they reduce allocation work, GC overhead, or memory stalls; fewer bytes
alone do not establish a throughput improvement.
All repeat distribution and allocation-trace restores succeeded, and each
760-entry asset path/hash snapshot matched its clean control. Diagnostic
source, scripts, histograms, and traces remain outside the production tree.

## Throughput profiling and optimized baseline

The subsequent throughput investigation found an important configuration
limitation: all four local assemblies used by the preceding experiments
were built in **Debug**, with `DebuggableAttribute.DisableOptimizations`
enabled. The shipped SDK engine did not disable JIT optimization. Existing
paired results remain observations of their recorded local configurations,
but their absolute costs and hotspot rankings must not be extrapolated to
Release. There is no new Release ancestor-versus-candidate comparison here.

Building the unchanged `8b41df437` source with
`./build.sh -c Release -v quiet` succeeded with no warnings or errors.
The Release engine, framework, task, and utility assemblies all had JIT
optimization enabled. A new counter-free comparison exchanged all four
assemblies between the **same commit's Debug and Release builds**, using
the same isolated SDK, package cache, asynchronous logging, and three
evaluation/action-graph opt-ins. After one warmup per configuration, six
order-balanced pairs gave:

| Metric | Debug | Release | Paired saving (95% CI) |
| --- | ---: | ---: | ---: |
| Mean restore wall time | 19.460 s | 17.738 s | 1.722 s [1.357, 2.086] |
| Mean user CPU time | 138.380 s | 148.242 s | -9.862 s [-17.212, -2.511] |
| Mean system CPU time | 12.418 s | 11.902 s | 0.517 s [0.182, 0.851] |

The Debug measurements were a **measurement mistake**: MSBuild is already
published with Release assets. The 8.8% local wall-time difference is a
baseline correction, **not a product improvement or an end-to-end saving
over published MSBuild**. It is not a CPU reduction either: Release used
more aggregate user CPU in this cohort.
Optimization, inlining, tiering, scheduling, and parallelism change the
cost structure; elapsed throughput and aggregate CPU must be measured
separately. All 14 restores succeeded and every 760-entry asset snapshot
matched. Subsequent throughput work should use this optimized configuration.

The initial managed-thread sampler materially perturbed the parallel
workload. Many samples landed in `PollGC`, and runtime suspensions with
reason `SuspendOther` accounted for most apparent pause time. Those pauses
must not be reported as application GC costs. That cohort was not used
for throughput ranking.

Instead, two optimized-engine profiles sampled only the verified NuGet
child's **user-mode CPU** with Linux `perf`, at 99 Hz using `cpu-clock:u`
and DWARF call stacks. Collection was process-scoped, without elevated
permissions or machine-wide tracing. A separate lightweight EventPipe
session (`dotnet-common` and `Microsoft-Build:0x4:4`) supplied graph and GC
boundaries without the managed sampler. The child was gated before its
main method; perf maps, JIT dumps, and runtime method records were preserved.
JIT injection used perf's monotonic clock. Both profiles covered complete
graph intervals, had no lost EventPipe events, and matched the assets.

Means of these diagnostic profiles were:

| Throughput observation | Mean |
| --- | ---: |
| Graph-construction elapsed time | 7.159 s |
| Sampled user CPU during graph construction | 104.949 aggregate s |
| Sampled user CPU outside graph construction | 23.146 aggregate s |
| Graph thread-pool-worker user CPU | 87.616 aggregate s |
| RequestBuilder-thread user CPU outside graph construction | 6.460 s |
| GC pauses overlapping graph construction | 1.186 s |
| Total GC pauses | 1.378 s |
| Graph GC-thread user CPU, server plus background | 11.051 aggregate s |

These diagnostic elapsed times are not the counter-free macro samples.
User-mode CPU samples exclude kernel work; thread CPU and GC pauses overlap
other work and must not be added as wall-time components. Aggregate worker
time is not a critical path. The RequestBuilder observation identifies a
substantial execution/orchestration thread to investigate, not proof that
its entire cost is serial or removable.

Around 38% of graph sample leaves remained unresolved, primarily in the
native runtime, and many other leaves were runtime stubs. DWARF unwinding
also left incomplete managed caller chains. Symbol-resolved condition
stacks accounted for about 1.74 aggregate sampled seconds, but such family
totals are incomplete attribution, not exact subsystem budgets. They
cannot establish that low-sample collection families are cheap or justify
a definitive micro-hotspot ranking.

The next throughput priorities are therefore **reducing repeated
interpretation during evaluation and locating avoidable work on the
post-graph extraction execution path**, with bounded CPU measurements at
those boundaries. Compact-property and metadata representations remain
secondary hypotheses. GC is relevant to throughput, but an allocation
change needs to reduce measured allocation/GC work or elapsed time, not
merely bytes. None of this establishes a throughput benefit from a
representation rewrite.

### Symbolized managed stacks at a lower sampling rate

Two further Release captures used `dotnet-trace collect` with
`dotnet-common,dotnet-sampled-thread-time` and
`Microsoft-Build:0x4:4`. The workload's
`DOTNET_EventPipeThreadSamplingRate=a` requested a 10 ms interval
(the runtime configuration value is hexadecimal). Both actual NuGet
children were gated before main and used the same four Release assemblies.
The traces covered the graph boundaries, reported no lost events, and
matched all 760 asset hashes.

This produced reliable **managed method names and caller chains**:
sample-weighted named-frame coverage was 100% during graph construction
and about 99.99% outside it. It does not resolve arbitrary native frames
inside interop calls. The two captures contained 12,404 and 12,369 managed
samples, plus 50,712 and 55,272 external samples. External samples were
dominated by idle thread-pool waits and were kept separate.

Sampling remained intrusive. About 47-48% of graph managed samples and
49-51% outside-graph managed samples landed in `PollGC`; these were excluded
from hotspot summaries, not reclassified as application GC. Runtime
`SuspendOther` intervals totaled approximately 1.87-1.88 seconds per
capture. Graph elapsed times were 7.726 and 7.471 seconds; restore wall
times were 19.76 and 19.59 seconds. These are diagnostic observations, not
counter-free performance measurements or causal overhead estimates.

The repeat caller chains identify more concrete throughput hypotheses:

| Boundary | Repeated symbolized caller chain |
| --- | --- |
| Unix path-adjustment heuristic | `MaybeAdjustFilePath` -> `LooksLikeUnixFilePath` -> `DirectoryExists` -> `Interop.Sys.Stat`, reached from compiled item-spec expansion and property expansion, including UsingTask registration |
| Property-function feature checks | `FeatureSwitches.EnableAllPropertyFunctions` -> `Environment.GetEnvironmentVariableCore` |
| Target-plan activation checks | `TargetEntry.GetTargetInstance` -> `CompiledTargetPlan.IsPartialEvaluationEnabled` -> `Environment.GetEnvironmentVariableCore` |

Path adjustment appeared in about 48-50% of non-`PollGC` graph managed
thread samples, and property expansion in about 56-59%; those are
overlapping caller families, **not CPU percentages or independently
removable wall time**. Metadata-condition stacks accounted for about 4%,
while observed compact-property and ordered-item families were each under
1% in that same sample population. Sampling bias and incomplete native
attribution prevent translating these shares into exact CPU budgets.

These stacks locate path-heuristic probes and repeated feature checks;
they do not rank components by budget violation. The bottom-up accounting
below is the decision framework for foundational engine improvements.
Any specialization must preserve filesystem-dependent results and the
existing ability to observe feature-switch/environment changes; simply
introducing process-wide caches would not establish semantic equivalence.
Use bounded CPU/call-count probes and optimized paired measurements to
determine the actual benefit.

## Bottom-up cost-model accounting

The next investigation rebuilt the cost model from useful-work contracts,
not managed-sample rankings. It used unchanged `8b41df437` Release assets
for counts and coarse phases, and a detached same-commit diagnostic
engine for selected exclusive CPU scopes. Both engines had JIT optimization
enabled. All opt-ins, SDK, package cache, and restore properties matched
the optimized baseline. The machine exposes 32 logical CPUs/16 physical
cores (Xeon Platinum 8370C, 2.8 GHz). The associated contracts and budget
interpretation are in
[the restore plan](restore-performance-plan.md#current-bottom-up-model-and-component-contracts).

Two census restores recorded binary logs in both the outer CLI and actual
NuGet child. A startup hook listened only to the standard event-count
meters and coarse graph/evaluation EventSource events. Replay verified
balanced start/finish events and found:

| Current workload unit | Count in each capture |
| --- | ---: |
| Physical project files | 637 |
| Configured evaluations / project builds | 1,655 |
| Executed targets | 28,178 |
| Executable task invocations / task bodies | 16,204 |
| Logged final property entries | 2,301,180 |
| Final item entries | 1,586,466 |
| Item-spec construction scopes | 388,152 |
| Metadata-analysis scopes | 382,334 |
| Lazy item-operation scopes | 384,632 |
| Update-selection / Update-decoration scopes | 27,464 |
| UsingTask registrations | 212,086 |
| Intrinsic-task scopes | 29,614 |

Final `ProjectToBuild` and `Compile` populations were 134,055 and 53,164.
Final state excludes intermediates and is not a read/write denominator.
Instrumentation categories can overlap: compiled/scalar condition
counters, for example, must not be summed as disjoint expression counts.
Module lowering attempts varied with concurrency: 1,090-1,097 in census
captures and 1,168-1,174 in finer CPU captures. These are not established
unique source/version counts.

The child binary logger materially changed the workload: census graph
times were 18.497-19.211 seconds; logger-enabled coarse controls were
18.530-18.574 seconds. Those captures supply **counts, not throughput
prices**. A separate pair disabled child binary logging and all meter
listeners, retaining only the outer CLI binary log and coarse EventSource
callbacks. Thread CPU used Linux `CLOCK_THREAD_CPUTIME_ID`; process CPU
bounded the graph interval. Means were:

| Coarse graph account | CPU seconds |
| --- | ---: |
| Initial properties | 0.715 |
| Properties and imports | 60.000 |
| Item definitions | 0.283 |
| Items, including lazy application | 52.785 |
| UsingTasks | 5.657 |
| Targets | 5.922 |
| Other synchronous evaluation work | 0.169 |
| **Evaluation-thread subtotal** | **125.530** |
| Graph process CPU not assigned to evaluation scopes | 20.317 |
| **Graph process total** | **145.847** |

The mean graph elapsed time was 7.953 seconds; outer restore elapsed
times were 18.67 and 18.37 seconds. Aggregate evaluation elapsed time
was 182.796 seconds across concurrent evaluations, **not a critical path**.
The CPU accounts reconcile, and every phase had 1,655 balanced intervals.
The residual graph account includes work on other threads, runtime/JIT,
GC, and orchestration; it is not proven removable engine overhead.
This cohort must not be combined with native user-only CPU samples or
older GC accounts into an additive total.

A detached Release engine then added thread-local stack probes around
selected existing evaluation scopes and task-service boundaries.
Disposal checked same-thread identity, nesting order, and nonnegative
exclusive CPU. Inclusive time was charged to a parent once; direct child
time was subtracted for the exclusive account. No meter listeners or
child binary logger were enabled. Two captures had zero incomplete
scopes. Their **exclusive** means were:

| Selected component | Exclusive CPU seconds |
| --- | ---: |
| Evaluation orchestration outside selected children | 0.151 |
| Initial properties | 0.718 |
| Properties/imports outside module lowering | 60.404 |
| Item definitions | 0.275 |
| Item-operation construction outside selected children | 12.073 |
| Lazy application outside selected operations | 5.423 |
| UsingTasks outside registration | 0.449 |
| Targets | 6.016 |
| Module lowering | 0.668 |
| Item-spec construction | 3.552 |
| Metadata analysis | 3.891 |
| Lazy operation outside selected children | 20.577 |
| Update selection outside nested operations | 3.162 |
| Update metadata decoration | 11.981 |
| UsingTask registration | 5.663 |
| Task scope outside selected children | 0.334 |
| Task resolution outside selected children | 0.347 |
| Task initialization, 9,584 conventional scopes | 0.087 |
| Task parameters, 9,584 conventional scopes | 0.442 |
| Task bodies, all 16,204 invocations | 0.436 |
| Task outputs, 9,584 conventional scopes | 0.288 |

The full exclusive sum was 136.936 CPU seconds, exactly matching the
sum of the selected evaluation, task, and resolution root inclusive
accounts before display rounding. Its evaluation root mean was 135.001
seconds, versus 125.530 in the coarse clean-engine cohort: observer/source
instrumentation effects remain, and these are not paired overhead estimates.
Do not subtract that difference from individual scope costs.
Registration/source helpers can also execute inside task resolution;
labels are not perfectly disjoint phase ownership. Recursive item
operations can execute during construction as well as the final lazy
pass. The exclusive ledger handles that nesting; subtracting whole
inclusive families from a presumed phase does not.

Covered task roots plus resolution used 1.934 CPU seconds, including
0.436 seconds in task bodies. This subtotal excludes preparation outside
those boundaries and includes probe costs. Fast-path constructors,
parameters, outputs, and cleanup are not individually split by the
9,584 conventional scopes; they remain within the task root. Against the
plan's provisional one-second aggregate service envelope, bodies leave
0.564 seconds (34.80 us/invocation) for non-body work, whereas covered
non-body service uses 1.498 seconds (92.45 us/invocation).
Task parameter/output scopes include required conversion and user
setters/getters; they must not all be relabelled interpreter overhead.
The service mismatch motivates pricing and reducing repeated task-site
preparation, not a promised wall-time saving.

Seven-round Release primitive fixtures used two million iterations per
round, balanced forward/reverse ordering, warmup, thread CPU, loop
controls, and result checks. They exercised 512 ordinary two-entry
`TaskItem` instances with short unescaped strings:

| Fixture | Median CPU ns/op | Observed range ns/op |
| --- | ---: | ---: |
| Loop control | 0.54 | 0.52-0.57 |
| Bound string-array slot read | 0.70 | 0.68-0.72 |
| Named metadata read | 18.13 | 17.97-19.06 |
| Read plus nonempty predicate | 17.95 | 17.90-18.04 |
| Read plus three-part concatenation | 44.23 | 39.33-48.13 |
| Changed metadata write | 99.47 | 97.32-118.87 |
| Read, concatenation, changed write | 121.15 | 118.32-122.96 |

Reported prices retain loop/control work; no tiny difference is used to
price a predicate by subtraction. Tiny read/predicate ordering differences
are measurement variation. The combined fixture is not a full expression
benchmark: inherited/built-in metadata, binding, property observers,
path heuristics, arbitrary functions, larger states/strings, and the
restore's server-GC/concurrency context remain separate/unpriced.
The first unverified measurement cohort is preserved separately; its
first-round tiering/outliers are not mixed with the final checked cohort.

All eight diagnostic restores succeeded and matched the same 760 asset
path/hash pairs. The diagnostic engine and final reference tool built
with zero warnings/errors; no production engine code changed. The
isolated SDK was restored to the clean Release bundle and original SDK
hashes were reverified. Session artifacts retain `cost-model-results/`
(census, phase/component scopes, assets, `analysis.json`),
`cost-model-tools/` (hook, replay/reference tool, checked primitive data),
`cost-model-run.py`, `cost-model-analyze.py`, and the detached diagnostic
source. These are not committed instrumentation.

## Fine-grained binding and execution accounting

The follow-up kept the same `8b41df437` clean Release bundle, runtime
commit `ed3bc08392c14faed28bbe367681b0c1bc698c43`, SDK, restore command,
opt-ins, and package-cached workload. It separated source/site/effect
counts from whole-region CPU timing and diagnostic-off controls. Child
binary logging and meter listeners remained disabled; only the outer CLI
had a binary log. No expression values were recorded.

### Input epoch and observer controls

The first new clean control retained all 760 asset paths but differed
from the historical reference on 641 hashes. The runtime worktree was
clean. The historical reference contains hashes, not asset contents, so
the cause is unestablished. Current package-audit warnings do not explain
all differences: 630 changed assets had no current log entries. That
failed comparison is preserved, not treated as an optimization result.

A fresh clean control established the new reference, including a ZIP of
the actual asset contents. All eight balanced follow-up captures matched
its 760 path/hash pairs exactly. This epoch has 1,654 evaluations and
16,189 executable task bodies, versus 1,655/16,204 in the preceding
accounting cohort. Counts, timings, and older 17.738-second macro samples
must not be mixed across these epochs.

The order was clean, diagnostic-off, census, timing, then reversed.
Each row below averages two captures:

| Observer mode | Outer restore elapsed s | Graph elapsed s |
| --- | ---: | ---: |
| Clean Release, coarse hook only | 20.400 | 8.447 |
| Diagnostic assemblies, counters/timers off | 20.620 | 8.972 |
| Source/site/effect census | 21.590 | 9.155 |
| Whole-region CPU scopes | 20.465 | 9.039 |

These are observer controls, not statistically established regressions
or savings. The diagnostic source still adds disabled branches in its
off mode. No timer/counter overhead is subtracted to invent clean
per-component prices.

The hook also captured graph, build-session, and process-exit boundaries.
It verified exactly one NuGet child with this order:
`HookReady, GraphStart, GraphStop, BuildStart, BuildStop, ProcessExit`.
Clean Release means were:

| Child process interval | Elapsed s | Process CPU s |
| --- | ---: | ---: |
| Hook ready to graph start | 0.136 | 0.187 |
| Graph construction/evaluation | 8.447 | 150.773 |
| Graph stop to build start | 0.014 | 0.014 |
| Extraction build session | 9.250 | 17.698 |
| Build stop to process exit | 0.881 | 9.643 |

Outer elapsed time outside the summed child-hook intervals was 1.671
seconds. This is an unsubdivided outer/pre-hook/shutdown complement,
not a priced startup component. Post-build work is not exclusively asset
writing. Process CPU includes runtime, logging, and other workers.
**The post-graph build session alone exceeds the entire 5-6-second goal**;
eliminating evaluation would therefore be insufficient.

### Source reuse and residual work

Both count captures agreed on source/site identities, role totals,
shapes, output-length histograms, and compiled-property function counts:

| Observed unit | Count per capture |
| --- | ---: |
| Source path/version identities | 1,087 |
| Published module objects observed by lookups | 1,087 |
| Module lookup calls | 1,688,008 |
| Unique item-operation source sites | 9,146 |
| Item-operation bindings | 391,187 |
| Residual property-write sites | 4,336 |
| Residual property assignments | 1,942,327 |
| Observed metadata source sites | 4,947 |
| Metadata-template bindings | 441,966 |
| Metadata decision attempts, including unconditional decisions | 1,746,839 |
| Conditional metadata decisions | 935,198 |
| Scalar-fallback condition decisions | 56,238 |
| Successful explicit metadata value expansions | 922,341 |

Source preparation is already shared; module lookup calls are not
recompilations. Item sites average about 42.8 bindings each. Sites are
merged by source path, version, source ID, and role, with static fields
checked for consistency. Module/version coverage is observed compiled
source, not a complete XML/import preparation census.

Residual property assignments produced 105,369,334 characters.
Their lexical shapes were 841,062 literal, 680,638 `P`, 264,474 `PF`,
129,450 `PS`, 12,555 `PFS`, 10,115 `S`, 4,032 `I`, and one `PI`.
Flags mean property syntax (`P`), metadata syntax (`M`), item-vector
syntax (`I`), static-function syntax (`F`), and slash/backslash in the
source (`S`). They are not purity/effect guarantees. Property counts
cover `ExecuteResidualPropertyProgram` assignments, not constant-block
replay, scalar fallback, initial properties, or all final state.

Metadata produced 72,702,931 characters, averaging 78.82 per explicit
value. Output-size counts provide a better primitive mixture than one
average expression:

| Output characters | Residual properties | Explicit metadata values |
| --- | ---: | ---: |
| 0 | 58,374 | 1,702 |
| 1-16 | 897,445 | 71,910 |
| 17-64 | 550,156 | 307,474 |
| 65-256 | 383,832 | 537,936 |
| 257-1,024 | 48,122 | 3,319 |
| 1,025-4,096 | 2,882 | 0 |
| More than 4,096 | 1,516 | 0 |

Of the 922,341 explicit metadata values, 915,480 (99.26%) originated at
source sites with compiled support. Successful routes included 17,770
specialized-binding completions, **738,830 specialized reuse completions
(80.10%)**, and 6,862 generic fallback completions (0.744%).
Supported source is not the same as a successful specialized route;
failed specialization attempts are not separately counted by completion
routes. Bulk constant metadata exits before per-entry decisions/values
and is excluded from those denominators.

Seventeen `PM` sites accounted for 22,986 bindings, 1,459,493 decisions,
764,813 values (82.92% of explicit values), and 734,943 specialized reuse
completions. Another value-only specializer does not address most of the
remaining region. Conditions, current-item reads, ordered writes,
binding, and representation transitions need a complete operation price.
Static source-part counts multiplied by executions are only potential
opportunities, not actual dictionary-read counts after specialization.
Inherited/built-in metadata kinds and item-vector cardinalities remain
unpriced.

### Required observation requests

Nested thread-local classifications counted requests made by the Unix
path heuristic, separately from its entry and input-character counts:

| Region | Directory plus file-or-directory probe requests |
| --- | ---: |
| Residual property program | 1,130,803 |
| Metadata binding | 92,573 |
| Metadata value | 23,726 |
| Metadata condition | 81,270 |
| Other, first/second capture | 1,222,311 / 1,222,269 |
| **Total, first/second capture** | **2,550,683 / 2,550,641** |

Both captures also recorded 296,569 function-permission checks reaching
the environment-read branch; the AppContext switch was unset on those
branches. Compiled-property function counts included 160,417
`NormalizeDirectory`, 53,226 `NormalizePath`, 6,616
`GetDirectoryNameOfFileAbove`, and 23,720 `StringReplace` calls.
Generic and condition function executions are not completely covered.

These are observation **requests**, not all filesystem operations,
kernel syscalls, their CPU prices, or proof that they can be removed.
Binding must retain filesystem/environment changes, short-circuiting,
and exception timing. A process-wide observation cache is not justified
by these counts.

### Whole-region service and execution ledger

The regional-timing pair retained same-thread/nesting checks and
nonnegative exclusive CPU checks. Both scope forests had zero incomplete
scopes and exactly conserved integer CPU nanoseconds. Selected means:

| Region | Inclusive CPU s | Exclusive CPU s |
| --- | ---: | ---: |
| Evaluation | 139.589 | 0.168 |
| Properties/imports | 64.257 | 27.094 |
| Whole property groups, 729,610 scopes | 36.570 | 36.570 |
| Module lowering, mean 1,148.5 attempts | 0.593 | 0.593 |
| Item-operation construction | 22.086 | 12.493 |
| Lazy item application | 39.605 | 5.666 |
| Item-spec construction, 396,996 scopes | 3.651 | 3.651 |
| Metadata analysis, 391,187 scopes | 4.110 | 4.110 |
| Lazy item operations | 43.572 | 20.833 |
| Update selection | 6.639 | 3.170 |
| Update metadata decoration, 27,462 scopes | 11.769 | 11.769 |
| UsingTask registration, 209,077 scopes | 5.494 | 5.494 |
| Evaluation-time target construction | 6.450 | 6.450 |

These rows are selected boundaries, not a complete additive table.
Recursive item operations also occur during construction. Only the
complete exclusive ledger is added: it totals 141.5717642 CPU seconds,
exactly equalling the evaluation root plus covered task/resolution roots.
The latter total is **1.98284635 CPU seconds**, including 0.464536 in
16,189 bodies; nested task phases must not be added again.
The same timing cohort's build session used 16.338224 process-CPU
seconds, leaving 14.355377 unassigned after covered task roots. That
complement is not automatically target-interpreter overhead.

A bounded follow-up added project, target traversal/body, intrinsic,
and project logging/state-view scopes. Its balanced order was
diagnostic-off, timing, timing, diagnostic-off. All four restores matched
the same fresh asset reference. Off controls averaged 19.735 seconds
outer elapsed (individual runs 20.69 and 18.78); timed captures averaged
20.265. This variation is not an established observer-cost estimate.

The timed build session averaged 9.091 seconds elapsed and 17.249
process-CPU seconds. All measured project/target/intrinsic/task scopes
ran on **one thread** in each capture; evaluation used 32 threads.
The project root contained the following nonoverlapping CPU account:

| Project-root account | CPU s |
| --- | ---: |
| Project work outside selected children, 1,654 scopes | 0.177 |
| Target traversal outside target bodies, 1,654 scopes | 1.058 |
| Target-body work outside selected children, 26,523 scopes | 1.541 |
| Intrinsic groups, 29,585 scopes | 0.619 |
| Project logging/state-view creation, 1,654 scopes | 1.458 |
| Executable task service plus resolution, including nested phases | 2.094 |
| **Measured project roots** | **6.947** |

The task row includes 0.475 seconds of bodies. Outside it, covered
project/target/intrinsic/logging service used 4.854 CPU seconds.
The full scope forest conserved 137.79755415 evaluation-root plus
6.94715205 project-root CPU seconds, including registration nested
inside task resolution. Preparation before `ProjectBuild`, project
teardown outside it, runtime, logging workers, and scheduling remain
outside or partly inside these boundaries as appropriate. Subtracting
project roots from the same build-process account leaves 10.302308
CPU seconds unassigned, not proven removable overhead.

`ProjectLoggingContext.CreateLoggingContext` includes property/item
view getters and event/context construction, not deferred logger
enumeration or serialization. It passes `PropertiesToBuildWith`, whose
getter calls `MaterializeCompactProperties`, before deciding which
properties/items the logger requires. This identifies a representation
transition to price; the whole 1.458 seconds is **not** attributed to
materialization. Measured scope counts are instrumentation units,
not interchangeable with logical target-event counts.

### Next foundation and remaining specification

The next execution unit to price is a **request-owned activation frame**:
source-prepared target/task actions bind against current property/item
state, with explicit logging/public-object views rather than an implicit
compact-to-object transition. Price frame activation, required view
construction, target decisions, intrinsic mutation, and task lifecycle
separately. Preserve mutable public APIs, copy-on-read event snapshots,
ordering, user callbacks, batching, and request/configuration ownership;
do not cache task results or evaluated projects.

This directly addresses a measured serialized execution boundary that
must improve independently of evaluation. The task portion retains the
provisional one-second aggregate service envelope; the other work still
needs a critical-path allocation under the 1.5-2.0-second elapsed
envelope. The ledger does not yet prove a prepared-frame design meets
either contract.

For evaluation, the corresponding bounded unit is a complete
operation-owned metadata program/frame: prepare source once, bind
scope-valid inputs once, then execute current-item conditions, reads,
and ordered writes while retaining required observations. The census
now supplies reuse and output-size weights, but metadata access-kind,
effect CPU, vector-size, and binding prices are still missing. Do not
declare metadata decorations out of spec by comparing their entire
region with the tiny read/concatenate/write fixture.

The engine and hook built with zero warnings/errors. Original SDK hashes
were verified, and the isolated SDK's four assemblies were explicitly
checked against the clean Release bundle after the cohort. No production
engine code changed. Evidence remains session-only in
`fine-cost-results-v2/`, `fine-execution-cost-results/` (each with commands,
hashes, timelines, asset contents, and checked `analysis.json`),
`fine-cost-run.py`, `fine-cost-analyze.py`, `fine-execution-analyze.py`,
the detached diagnostic source, and `cost-model-tools/fine-v2-bundle/`.

## Request activation and property-view investigation

The next investigation split the execution frame's entry costs from
required logging snapshots and compact-to-object conversion. Probes
remained in the detached Release checkout. Whole-region scopes captured
thread CPU and current-thread allocated bytes, conserving both exactly
through the existing same-thread/nesting checks. A separate census
recorded boundary counts, followed by a count-only named-property-demand
capture. No per-lookup timing was added.

### Ownership and compatibility contract

`RequestBuilder.BuildProject` holds a configuration-owned project usage
scope across execution and telemetry. `BuildRequestConfiguration.BaseLookup`
is created once per configuration, not once per task. Its root points at
the project's mutable item/property dictionaries; cloned batch lookups
share outer scopes and stage writes in inner scopes until merging.
A replacement frame must preserve those visibility and merge rules.

Both the logging factory and `BaseLookup` request
`ProjectInstance.PropertiesToBuildWith`, which drains compact properties
into `ProjectPropertyInstance` objects. Logging goes first in this
workload. Moving conversion out of logging without changing the execution
provider would only move it to lookup construction.

The default logging property view calls `PropertyDictionary.Filter`.
This eagerly captures filtered, unescaped name/value entries at event
creation. It is not the deferred `GetCopyOnReadEnumerable` path used
when all environment properties are requested. Item views are deferred
copy-on-read wrappers. A compatibility view must retain the actual
capture/read timing of each path, environment filtering, escaped versus
unescaped values, global-property event payloads, and event ordering.
Simply delaying the default property enumeration would expose later
mutations instead of the event's existing snapshot.

Public property objects remain mutable where permitted and can outlive
their lookup. Their identity and updates cannot be replaced by a frozen
value cache. Existing `ProjectInstance.GetProperty` already materializes
one compact entry on demand, while value reads can stay compact; the
execution lookup still requires the fully object-backed dictionary.

### Boundary census and service prices

Six balanced captures ran diagnostic-off, census, timing, then reversed.
All matched the same fresh 760-asset reference and saved asset contents
from the preceding investigation. Means:

| Observer mode | Outer elapsed s | Graph elapsed s | Build-session elapsed s |
| --- | ---: | ---: | ---: |
| Diagnostic-off | 20.175 | 8.634 | 8.930 |
| Activation census | 20.945 | 9.209 | 9.081 |
| Activation CPU/allocation scopes | 20.765 | 9.035 | 9.035 |

Two repeats and the census's 22.35/19.54-second outer variation do not
establish instrumentation overhead or a throughput improvement.
The census repeats exactly:

| Activation unit | Count / size |
| --- | ---: |
| Request entries; already-loaded projects | 1,654 / 1,654 |
| Base lookup creations | 1,654 |
| Compact entries before logging getter | 2,034,153; maximum 1,328/project |
| Object properties after logging getter | 2,384,682; maximum 1,561/project |
| Full, eagerly filtered property views | 1,654 |
| Deferred all-environment property views | 0 |
| Selected-property-name views | 0 |
| Deferred item-view wrappers | 1,654 |
| Global properties copied into events | 24,106; maximum 16/project |
| Compact entries remaining before base lookup | 0 |

There are 3,308 build-getter drain calls: logging drains the compact
entries, and base lookup drains an empty environment. Another 1,654
public-property drain calls have zero entries. Counting those calls as
repeated conversion would be incorrect.

The timing pair's selected prices are:

| Boundary | CPU s | Current-thread allocated MB |
| --- | ---: | ---: |
| Request-thread roots, including execution and cleanup | 7.106 | 892.891 |
| Project activation outside existing project-build scope | 0.011 | Less than 0.001 |
| Operating-environment initialization, separate roots | 0.133 | 32.630 |
| Base lookup construction, including its empty drain | 0.0047 | 0.265 |
| Project-start handling, inclusive | 1.527 | 328.878 |
| Logging factory/state-view creation, inclusive | 1.441 | 323.380 |
| Nonempty compact conversion inside logging | **1.035** | **276.660** |
| Eager filtered logging property snapshot | **0.361** | **44.828** |
| Item-view wrapper creation, not enumeration | 0.0027 | 0.066 |
| Event creation outside global-property copying | 0.0124 | 0.437 |
| Global-property event copying | 0.0129 | 1.071 |
| Result reporting/cleanup | 0.238 | 35.837 |

MB means decimal megabytes of allocated bytes, not retained memory.
Rows include parents and children and **must not all be added**.
The nonempty conversion price is the logging factory's inclusive cost
minus its exclusive remainder and selected property/item/event children;
these were its only timed children in both captures. All conversion
scopes total 1.044 CPU seconds, including empty evaluation/lookup drains.
Conversion inside logging averages **509.00 ns and 136.01 allocated
bytes per compact entry**, including draining, name/value object
construction and validation, dictionary insertion/locking, and probe
costs. It is not a primitive factory-only price.

Request execution remains on one thread. Environment initialization
uses four observed threads across its calls, and evaluation uses 34
observed threads per capture; thread counts are not simultaneous useful
concurrency. Covered executable-task service plus resolution totals
2.034 CPU seconds, including 0.465 of bodies, in this timing pair.
Conversion is a significant serialized representation charge, whereas
basic request/lookup setup is not the large missing component.

The full CPU/allocation forests have zero incomplete scopes and exact
integer conservation. Build-session process CPU averages 17.061695
seconds. Request roots account for 7.1055308 and separate environment
roots for 0.1326204, leaving **9.8235438 process-CPU seconds unassigned**.
This is not attributed wholesale to engine interpretation. The build
interval allocates approximately 978 MB process-wide and records about
0.176 seconds of GC pause; graph work allocates about 6,212 MB and
records 1.497 seconds of pause. These interval GC observations do not
price GC CPU, identify request critical-path stalls, or make pause time
an additive CPU account. Allocation is secondary to the measured
serialized service costs.

### Named-property demand after activation

A separate bounded census registered each base property dictionary at
logging-view creation, after conversion and before execution. It counted
the dictionary's named index, substring lookup, and unescaped-value
paths, covering both `Lookup` and direct `ProjectInstance` reads.
Case-insensitive distinct names were retained only in diagnostic memory;
output contains configuration IDs and numeric counts, not names/values.
Writes into the registered base table were counted separately.

The first demand cohort had no demand output: its nominal build had
not updated the diagnostic assembly. The analyzer rejected it. Its four
runs are preserved as invalid demand evidence. The corrected build used
the diagnostic project's absolute path and verified the new assembly's
demand markers before any restore. A fresh balanced off/count/count/off
cohort then matched the same asset reference in all four runs.
Off controls averaged 20.565 seconds outer elapsed; count captures
averaged 20.205. Those times do not measure an optimization.

Both valid captures have identical aggregate counts across 1,654 base
dictionaries:

| Named base-state operation | Count |
| --- | ---: |
| Properties present at activation | 2,384,682 |
| Successful named reads | 178,956 |
| Unsuccessful named reads | 279,749 |
| Distinct successfully read names, summed per configuration | **72,705** |
| Writes applied to base dictionaries | 38,887 |
| Distinct written names, summed per configuration | 26,333 |

At most 75 distinct successful names occur in any one configuration.
The summed distinct demand is 3.049% of initial base entries. It is an
**upper bound of 3.574% of initially compact entries**, since it also
includes initially object-backed properties and names created during
execution. Thus at least 96.426% of initially compact entries are not
needed by these named-read paths after activation. This is not proof
that their values are globally unused: logging and other bulk
enumeration, copying, translation, and public-object operations remain
outside that named-read denominator. Read frequency also does not
distinguish value-only demand from required object-identity demand.
Per-configuration count distributions differ between captures despite
identical aggregate counts; no exact per-site reuse claim is made.

### Bounded frame design to prototype

The evidence supports a **configuration-owned compact base property
state with request/batch overlays and explicit compatibility views**,
not merely a cheaper empty request object:

1. Keep current values in their compact base representation; prepare
   property identities with source, without freezing configuration values.
2. Preserve `Lookup` shadowing and ordered write/merge rules over request
   and batch overlays. Writes must invalidate or supersede compact
   entries; leaving the old compact value beside an object-table write
   would make direct value reads stale.
3. Build the required logging snapshot directly from base state at its
   existing capture point, preserving filtering, escaping, enumeration
   compatibility, and deferred item/all-environment behavior.
4. Materialize stable public property objects on actual object demand,
   and retain a full compatibility barrier for bulk public APIs,
   translation, and other unsupported boundaries.

The useful-work model is now:

```text
frame service =
    base activation + required environment setup
  + logging snapshot(entries, escaping/filter mixture)
  + named reads(hits, misses, scope depth)
  + demanded object materialization + ordered writes/merges
  + required public/translation barriers
```

The current 1.035-second conversion charge is an available region to
replace, **not a promised saving**. A prototype must also price its
snapshot, compact read/write bridge, remaining object demand, and
compatibility barriers; required snapshot work is not assumed free.
Basic activation already costs milliseconds, so request allocation alone
would miss the measured gap. This boundary is independent of the
remaining target/task service and unassigned process CPU, and cannot
by itself deliver the 5-6-second restore.

The diagnostic engine/hook built with zero warnings/errors. Original
SDK hashes and all four isolated clean Release assemblies were checked
after both valid cohorts. No production engine behavior changed.
Evidence is session-only in `activation-cost-results/`,
`activation-demand-results-v2/`, both checked analyzers, diagnostic source,
and `cost-model-tools/activation-bundle/`; the invalid first demand
cohort remains in `activation-demand-results/`.
