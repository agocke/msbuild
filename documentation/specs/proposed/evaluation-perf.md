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
