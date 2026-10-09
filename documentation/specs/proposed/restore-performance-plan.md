# Classic MSBuild restore performance plan

This plan targets the clean, package-cached `dotnet/runtime` static-graph
restore workload **under existing MSBuild semantics**. Project files, public
object models, project-reference and target protocols, task lifecycles,
filesystem observations, diagnostics, and restore outputs remain authoritative.
The optimization specializes repeated interpretation; it does not require a
new evaluation mode, declared-I/O contract, result cache, hardened graph, or
output-oriented build query.

The cost model and historical measurements below were adapted from the
separate `build-exp` restore investigation (August 2026). They are not
measurements of this branch at its current tip. Current branch experiments
are recorded in [evaluation performance investigations](evaluation-perf.md);
discarded approaches are in
[evaluation performance dead ends](evaluation-performance-dead-ends.md).
Neither document's measurements can be added to the historical decomposition.

## Work backward from useful work

Top-down profiles tell us where the *current implementation* spends time and
whether a change helped. They do not tell us what a good interpreter should
cost: optimizing the biggest existing method may preserve an expensive
architecture. Start with the work the restore actually needs, set a
bottom-up budget, then use profiles and paired measurements to find and
validate changes that close the gap.

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

1. **Reduce item-application metadata work.** The opt-in item pass consumes
   roughly 67-69 aggregate CPU seconds and 4 GiB of allocations across the
   restore, marginally ahead of properties/imports in CPU. Per-item metadata
   value expansion and writes in `ProjectToBuild` Updates are measured
   allocation hotspots; `Compile` Updates spend most of their measured
   decoration cost on SDK scalar conditions with property functions.
   Investigate reusable source-derived expansion plans and metadata
   representation without replaying item-specific results or suppressing
   observable writes. Preserve item-local ordering, predecessor history,
   qualified metadata, and filesystem observations.
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
