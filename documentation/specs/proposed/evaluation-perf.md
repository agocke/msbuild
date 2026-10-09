# Evaluation performance investigations
In the current effort to improve performance of MSBuild, we identified evaluation as one of the focus areas of this effort. Evaluation is the first step when loading or building, and it determines references, how projects are connected and what needs to be build. Because of this it runs in every MSBuild scenario, from solution load and design-time builds in Visual Studio, to up-to-date builds or full builds in VS or on the command line.

## Description
Current performance state of evaluation is mostly unkown, we have a few measures but no easy way of accessing and assessing them. As such, we are unsure which specific areas can be improve. 

## Goals and Motivation
We are trying to make evaluation phase of the build more performant, since it is almost always executed any performance gain becomes noticeable. A performant evaluation phase would decrease build times in general, in CI cases it frees up resources, and in individual cases it can increase dev-loop performance by making up-to-date and incremental builds go faster.

In this moment we are still in investigation phase, the objective is to make the markers we have in code more accessible to the team, so we can idetentify low hanging fixes, and improvement areas when testing builds within PerfStar.

Constraint - needs to work as it does today, but faster. We may be able to break some edge cases.

## Risks
One of the big risks is accidentally changing the current behaviour of evaluation. One of the constraints of improvement is that evaluation has the same behavior, with the exception of edge cases where we can sometimes change it.

## Plan
The plan for evaluation at the moment is to add more code markers during execution so we can use PerfStar to have a detailed view of how long each part of evaluation phase takes.

Larger changes to the evaluation are possible and under consideration for future iterations, like trying to cache the evaluation result in MSBuild. However we are focusing on investigation and performance gains with less work at the moment.

Rejected experiments and their measured outcomes are tracked in
[Evaluation performance dead ends](evaluation-performance-dead-ends.md).

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
