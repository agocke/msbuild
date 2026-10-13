# Compact, batch-native item engine

## Decision and scope

Build a compact item execution path. Prepared item programs operate directly
on configuration-owned ordered state, rather than constructing generic item
objects and lazy operation graphs for every activation. Keep expensive
compatibility and diagnostic views off the normal path until they are
requested.

**Batch execution comes first.** The first deliverable executes prepared item
operations natively, applying each operation's effect to compact rows as one
batch. Publication, execution integration, and broader coverage follow it.

The acceptance workload, for now, is the clean, package-cached static-graph
restore of `dotnet/runtime` measured by the
[restore plan](restore-performance-plan.md). The engine is not
restore-specific: command-line builds share the same item programs and should
benefit later, but they are not yet acceptance workloads. IDE and interactive
evaluation may see no gains or performance regressions; their correctness must
remain unchanged.

This is a proposed architecture and staged implementation plan, not an
implemented engine or a new performance result. Related evidence and strategy:

- [Classic restore performance plan](restore-performance-plan.md).
- [Evaluation measurements](evaluation-perf.md).
- [Rejected evaluation experiments](evaluation-performance-dead-ends.md).
- [Existing stop-after-pass evaluation](partial-evaluation.md), which is a
  separate feature and does not make a partial instance buildable.

### Why this direction

Items are the largest evaluation pool left. With compiled modules and
batching, item-operation construction and lazy application cost 15.3 G and
38.8 G, against 87.4 G for the default engine
([measurement](evaluation-perf.md#executable-floor-for-item-operations)).

An executable floor prices the gap. A reference implementation replays every
item operation of one restore on compact rows: shared metadata blocks,
programs prepared once per site, and metadata applied once per operation when
it reads no item metadata. It is validated against MSBuild's recorded rows
(6 disagreements in 790,968 checked operations). On the operations it covers,
76% of MSBuild's per-site item work, it spends **4.0 G against MSBuild's
54.6 G: 13.6x fewer instructions**:

- includes 12.5x;
- updates 16.8x;
- a literal include with literal metadata ~157x (~520 against ~82 k);
- the single costliest site, runtime's `ProjectToBuild` update, 16.9x
  (667 k against 11.2 M per evaluation).

The comparison is against the default engine. Batching already removed part
of that gap, most of it at the floor's highest-ratio sites, so the remaining
gap to the module path is smaller than 13.6x. The floor's cost is real work:
it expands, matches, evaluates conditions, and probes the filesystem where
MSBuild does. Most of the difference is representation and per-operation
overhead.

The compact engine improves a cold, unique configuration without any reuse.
Attaching repeated results (60% of item-operation work repeats with equal
live-ins) needs cheap producer-originated identities, and demand slicing needs
consumers that remain narrow. Both build on this foundation; neither is a
prerequisite.

Start with common project shapes, then provide explicit backstops. The goal
of covering nearly all ordinary projects is a design aspiration, not a
measured coverage claim. Measure coverage by item work and fallback cost,
not only by project or operation counts.

### Non-goals

- A new MSBuild language mode, changed task contracts, or weaker diagnostics.
- Replacing the editable `Project` object model or making every public API
  inexpensive.
- A new property frame as a prerequisite; retain the compact property provider.
- Cross-configuration result caches, content hashing of item lists, persistent
  build servers, or task-result caching.
- Dropping unused item types or introducing query-only build semantics.
- Replacing filesystem matching, regex engines, or arbitrary property
  functions as part of the initial item representation work.

## Current costs and integration boundary

The current compiled path shares preparation, but activation still builds
configuration-bound item specifications, factory wrappers, operations, lazy
list nodes, and results. Application can traverse tree-backed storage through
positional indexing, clone item objects for updates, analyze metadata
dependencies again, and reconstruct matched projections.

`ImmutableList<T>.ToBuilder()` is not a deep copy of the list. The issue is
the work performed through that representation, not an assumed full copy at
every operation. Metadata backing already shares across observed item copies;
the new engine must retain that benefit rather than eagerly copy it.

The existing item pass eventually calls `GetAllItemsDeferred()` and publishes
survivors through `_data.AddItem`. `IEvaluatorData<P, I, M, D>` and
`IItemFactory<S, T>` require class-based `IItem` implementations. Substituting
a struct is therefore not a collection-only change, and one adapter object
per row would preserve much of the overhead.

The proposed seam is a specialized item executor and native publication into
`ProjectInstance`. Reuse module preparation, source identities, property
binding, and supported expression lowering. Do not generalize the entire
editable evaluator to accept value types or build a second XML parser.

| Existing work | Intended replacement | Remaining obligation |
| --- | --- | --- |
| Per-activation operation and fragment graphs | Shared prepared programs with small bound activation state | Dynamic expansion and prefix binding |
| Tree-backed positional scans and repeated projections | Array-backed ordered state and direct readers | Matching and stable order |
| Item factories and clones during application | Value-row updates and shared immutable metadata | Copy isolation and destination context |
| Repeated metadata dependency analysis | Prepared metadata shape and value programs | Ordered, conditional execution |
| Final publication as individual item objects | Publish compact sequence roots | Requested API, task, log, and wire observations |

## Semantic model

An item type denotes an ordered, duplicate-preserving sequence of occurrences.
Operations evolve that sequence. An occurrence has an include, direct metadata,
an ordered definition context, and built-in/source context. Storage identity,
matching equality, and public object identity are different concepts.

The common engine must preserve the following laws:

| Guarantee | Consequence for the representation |
| --- | --- |
| Includes and copies retain duplicates; removes preserve survivor order | Use ordered sequences, not sets keyed by include |
| References, transforms, excludes, and conditions observe a logical prefix | Bind references to the correct sequence version |
| Copies isolate direct metadata and membership from later source changes | Share immutable backing, not mutable occurrence state |
| Metadata assignments and conditions see earlier writes | Execute an ordered program against a working metadata frame |
| Qualified updates capture the last matching duplicate per referenced type | Preserve source order and separate source bindings from destination writes |
| Direct metadata overrides ordered inherited definition layers | Retain layer precedence and distinguish direct from inherited values |
| Some definition values capture custom metadata early but defer built-ins | Preserve staged values; do not flatten every definition into a final string |
| Escaping and expansion stages are observable | Keep escaped values and never re-expand evaluated syntax accidentally |
| Matching policy differs from identity and varies by operation | Keep optional matching indexes separate from original include spelling |
| Built-ins depend on include origin and defining context | Retain wildcard-origin, defining-project, and directory information |
| False conditions and short-circuiting suppress expression effects | Do not hoist or pre-execute conditional work |

For example:

```xml
<A Include="a;b">
  <M>old</M>
</A>
<B Include="@(A)" />
<A Update="a">
  <M>new</M>
</A>
<A Remove="b" />
```

`A` ends with `a/new`; `B` retains `a/old` and `b/old`. The two sequences can
share immutable metadata backing, but not mutable membership or direct
metadata. Copies also acquire their appropriate destination definition and
source context; sharing values does not erase those distinctions.

The editable model has additional guarantees: live inherited definitions,
XML/metadata object identity, predecessor history, and rename behavior.
Preserve those on its existing path rather than embedding all of them in
every compact row.

## Physical representation and ownership

### Ordered value rows

Start with array-backed row chunks and an ordered sequence descriptor. Traverse
chunks sequentially; use typed integer handles where useful, not a global
interning arena for every relationship. The conceptual row is:

```csharp
internal readonly record struct OccurrenceId(int Value);

internal readonly record struct ItemRow(
    OccurrenceId Occurrence,
    string EscapedInclude,
    MetadataBlock DirectMetadata,
    ItemContext Context);
```

These names describe responsibilities, not finalized production types.
Occurrence IDs are owner-local identities, not normalized paths or public
objects. A copy creates a distinct occurrence; an update can replace its value
without changing that occurrence's identity. Item type belongs to the sequence,
not necessarily to every row.

`ItemContext` describes ordered definition layers and built-in/source context.
Share context at operation, fragment, or batch granularity where valid.
Avoid a context object per row when all rows use the same context.

Use ordinary GC references for immutable shared metadata and cold contexts.
Do not retain all historical rows in a process-global append-only arena.
Size and retention of the physical layout must be measured; the sketch does
not establish a row-size or allocation saving.

### Exclusive mutation and snapshots

Each activation has one writer. A captured sequence version freezes its
descriptor and reachable row storage. Later writes copy only storage that
would otherwise modify a captured version; metadata blocks and contexts are
immutable while shared.

The initial chunk implementation must specify ownership explicitly:

- Private chunks may be changed or compacted in place.
- Published chunks cannot be mutated through the writer.
- Append and metadata-only replacement detach only affected shared storage.
- Remove compacts survivors stably, without changing captured membership.
- Dead versions and blocks are released when their owners no longer retain
  them; avoid retaining every intermediate state until the build ends.

Snapshot and descriptor copying is not assumed to be O(1). Measure its cost
and retained bytes under repeated prefix captures. A flat vector that copies
the whole list after every capture can become quadratic; a segmented
collection with O(1) indexing does not automatically solve that problem.
Reuse an existing collection only after verifying its mutation/freeze costs.

C# does not provide a borrow checker, so ownership is enforced by narrow
internal APIs and assertions. Do not keep borrowed spans or references across
storage growth or mutation. Pool scratch buffers initially, not published
buffers that can still be referenced by snapshots.

### Metadata and indexes

Prepare metadata names and shapes once for supported programs. Common small
direct-metadata sets use compact storage with shared shape information;
larger or dynamic shapes can take a different representation behind the same
native reader. Do not allocate a dense array sized by a cache-wide name-ID
high-water mark.

A working frame exposes inherited values and earlier direct writes. Commit
the completed direct-metadata block at a safe operation boundary, rather
than constructing a persistent map for every assignment. Reads within the
operation must still see sequential assignments and the correct qualified
source captures.

Definition entries can contain evaluated escaped values or prepared deferred
built-in values. Preserve their binding stage. Bound lookup depth and avoid
unlimited metadata overlay chains.

Build matching indexes on demand and scope them to valid sequence versions.
Preserve duplicate order and invalidate or reuse indexes according to changes
in membership, includes, and positions. Metadata-only replacement must not
make an index retain obsolete row values. Never use record equality as the
language's matching policy. MSBuild name tables use
`MSBuildNameIgnoreCaseComparer`; path and `MatchOnMetadata` policies retain
their existing operation-specific behavior.

## Prepared execution and batching

Separate shared program data from activation data:

| Shared program | Bound activation |
| --- | --- |
| Source-bound operation records and locations | Property values and current definition context |
| Prepared selectors and expression/metadata shapes | Referenced sequence versions and qualified bindings |
| Conditions, dependency summaries, and eligibility | Filesystem context and required effect results |
| Literal fragments and constant metadata blocks | Sequence writer, scratch frame, and observation policy |

Start with literal and property-expanded includes, bare item references,
ordinary literal/glob selection, and ordered metadata programs supported by
existing lowering. Use existing filesystem and wildcard machinery at the
same semantic observation boundaries; do not universally expand special
lazy wildcards earlier.

Classify programs and bind common shapes outside row loops. Avoid per-row
factory wrappers, interface boxing, delegate closures, and rediscovery of
static metadata dependencies. Arbitrary property functions and dynamically
introduced syntax require an explicit supported path or backstop, not an
assumption that common lowering covers them.

Batching is not a transaction. Writes become visible at their logical
positions. Bind prefix references when encountered. Preserve dependencies,
source binding, and conditional effects.

Fuse a region only when its operations are proven safe to compose and no
required intermediate observation is crossed. Preserve throwing behavior and
effect order; purity alone does not prove an expression can be omitted.
Opaque calls must not be reordered across items or operations. Existing
constant-include and exact-update batching are prior art, not new savings
to claim again.

This path still performs full evaluation. Pure-region fusion is useful even
when every final item type is requested; demand slicing is separate future
work.

## Restore consumers

Restore reads few item types, all through public `ProjectInstance` APIs:

- **Graph construction** reads `ProjectReference` and `ProjectReferenceTargets`.
- **The NuGet console** builds `_CollectRestoreInputs` in every configuration,
  then calls `GetItems` for `PackageReference`, `PackageDownload`,
  `PackageVersion`, `FrameworkReference`, `PrunePackageReference`,
  `ProjectReference`, and `NuGetAuditSuppress`.

Promoting a whole instance on its first public read would therefore promote
every restore configuration. The restore path needs promotion per item type:
the first public read of a type materializes that type's objects, which
become authoritative for that type, while other types stay compact. Types
that no consumer reads are never materialized.

## Carry compact state through command-line execution

Evaluation-only compactness is insufficient. The CLI path must avoid
unconditional object conversion at these boundaries:

| Boundary | Required integration |
| --- | --- |
| Evaluation completion | Transfer compact sequence roots into `ProjectInstance`, without per-row `AddItem` |
| Item expressions and graph interpretation | Native include/metadata readers and deferred edge-item views |
| Target lookup, scopes, and intrinsic item groups | Compact base state plus scope-owned changes, preserving existing merge and copy semantics |
| Batching and task-parameter binding | Read the required occurrences directly; adapt only consumed task arguments |
| Task outputs | Import values with the existing escaping, ownership, and publication semantics |
| Logging and binary logs | Preserve events, IDs, order, and requested values without using a full mutable item view as the default snapshot |
| Node transfer and deep copy | Preserve existing wire/public contracts; support direct compact serialization or explicitly account for conversion |

Existing tasks continue to receive the same `ITaskItem` contracts. Boundary
adapters must retain current mutation isolation and aliasing behavior; sharing
a mutable wrapper across task arguments is not automatically safe. No task
author changes are required. Specialized built-in-task paths are optional
later work, not a prerequisite.

Builtin metadata is read at the existing point of use. Do not eagerly probe
files or freeze file-dependent values merely to fill a row. Readers retain
the original defining and wildcard context needed for those computations.

Count task adapters separately from full compatibility promotion. Avoiding
`ProjectItemInstance` objects while constructing equivalent per-row metadata
dictionaries or adapters elsewhere is not the intended result.

## Backstops and observation policy

Initially retain the existing evaluator for editable/design-time evaluation,
special observation modes, and unsupported item programs. Define eligibility
before executing affected work. If dynamic behavior cannot be handled
without replaying effects, conservatively choose the old path beforehand.

The first unsupported-program backstop can cover the entire item pass.
Local resumable fallback is a later refinement, not an initial correctness
requirement. A local fallback must carry already-computed values, prefix
bindings, and committed effects; it must not restart property functions or
filesystem work.

Do not implement fallback by catching evaluation exceptions and retrying.
Language errors still surface with the existing location, code, severity,
condition behavior, and ordering. No new default warnings or errors are
introduced by compact eligibility.

For public item APIs, the compatibility backstop promotes an item type to its
existing object representation on first public access; whole-instance
promotion remains the fallback for APIs that touch every type, such as
mutable item definitions. A promoted type's objects become authoritative,
including include and metadata changes. Do not maintain independently
writable compact and object copies of the same type. Engine-native readers
must handle promoted types correctly. See [Restore consumers](#restore-consumers)
for why per-type promotion is required rather than optional.

Keep public occurrence identity, collection behavior, immutability checks,
and deep-copy guarantees. Views finer than an item type are allowed later only with
a coherent mutation/identity protocol.

Ordinary internal CLI reads must not accidentally call promotion APIs.
Diagnostics that need history or non-surviving operations must either
capture it during execution or select a richer path up front. Final rows
alone cannot reconstruct provenance. Never replay arbitrary effects to
manufacture an on-demand view; use an existing explicit reevaluation
contract only where that API already permits it.

## Implementation plan

Each stage keeps the legacy path usable. Begin behind the existing compiled
evaluation opt-in or an internal experiment selector; do not require a new
public language option. Confirm default-enablement policy after validation.

| Stage | Deliverable | Exit evidence |
| --- | --- | --- |
| 0. Price the current path (done) | Per-site MSBuild costs (`MSBUILDITEMCOSTPROFILE`) and the [item floor](evaluation-perf.md#executable-floor-for-item-operations) | Dominant shapes, attainable per-operation cost, and validated semantics for 94% of operations |
| 1. **Batch execution** | Prepared item programs executed natively on compact rows for the shapes the floor covers: literal and property specs, plain item references, excludes, `@(Self)` and path-matched updates and removes, and ordered metadata programs applied once per operation when item-independent. Minimal storage: append-only per-type rows whose prefix snapshot is (array, count); copy-on-write only when captured rows are updated or removed. Whole-pass fallback. A measured legacy-publication bridge keeps instances usable. | Repeatable net item-pass instructions per configuration on restore, bridge and fallback included; identical restore assets |
| 2. Publish compact instances | Direct publication; native graph reads of `ProjectReference`; per-type promotion for public reads | No final object drain on restore; promoted types limited to those consumers read; mutation and identity cases agree |
| 3. Execution integration | Compact lookup, scopes, intrinsic item groups, and task binding for restore's `_CollectRestoreInputs`, then generally | The serial execution phase shrinks; costs are not shifted into binding or logging |
| 4. Broaden and harden | Coverage driven by fallback cost: wildcards, transforms, and common property functions; bounded safe fusion | Restore macro pairs, compatibility cases, and modest memory and complexity justify enablement |

A temporary legacy-publication bridge can keep an early prototype usable,
but it is not completion of the CLI design. Measure its cost explicitly.
Stages are not a commitment to shipping a large rewrite before learning:
retain independently useful, bounded changes on repeatable phase-level
savings while measuring accumulated end-to-end throughput.

Do not expand support merely to improve a project-count percentage. Rank
backstops by the work they retain. A rare shape at a costly site may matter
more than many cheap literal operations. In the restore, the largest work the
floor does not cover is one wildcard include (6.9 G), the SDK's `Compile`
update that computes `Link` with property functions (2.7 G), Roslyn's
transform includes (0.8 G), and Arcade's targeting-pack updates (~1.5 G).

Sequence versions should carry a slot for a producer-originated identity from
stage 1, so that later attachment of repeated item regions does not need a
storage change. Do not build attachment in stage 1.

## Semantic validation

Existing tests define observations, not requirements to retain old storage
mechanics. Route representative semantic fixtures through scalar, current
compiled, and compact `ProjectInstance` evaluation with explicit path
selection. Many fixtures currently construct editable `Project`; their
passing unchanged does not exercise the compact engine.

| Existing test area | Contract to carry into differential cases |
| --- | --- |
| [Item evaluation](../../../src/Build.UnitTests/Evaluation/ItemEvaluation_Tests.cs) | Prefix copies, excludes, removals, indirect syntax, escaping, lazy wildcard behavior |
| [Project items](../../../src/Build.OM.UnitTests/Definition/ProjectItem_Tests.cs) | Order/duplicates, layered metadata, sequential updates, qualified captures, source and built-in context |
| [Item definitions](../../../src/Build.OM.UnitTests/Definition/ProjectItemDefinition_Tests.cs) | Early custom binding, deferred built-ins, precedence, and no double expansion |
| [Compiled module sharing metrics](../../../src/Build.UnitTests/Evaluation/ModuleEvaluationSharingMetrics_Tests.cs) | Scalar/compiled agreement, conditional effects, short-circuiting, and introduced-syntax backstops |
| [Task items](../../../src/Build.UnitTests/Instance/TaskItem_Tests.cs) | Task-boundary escaping, metadata distinctions, mutation isolation, and serialization |
| [Item globs](../../../src/Build.UnitTests/Evaluation/ItemGlobs_Tests.cs) and [Project observations](../../../src/Build.OM.UnitTests/Definition/Project_Tests.cs) | Requested provenance/history, document order, and allowed reevaluation behavior |

Add focused cases for private versus frozen mutation, self-references,
duplicate copies, last-source qualified binding, removal stability, and
promotion followed by public mutation. Confirm that a supported CLI path
does not request full compatibility materialization; expected task/log
adapters are separate observations.

Preserve required expression and filesystem effects, not only final values.
Test fallback after a computed dynamic value without repeating the call
when local fallback is introduced. Test that false conditions still suppress
failing reads. Characterize skipped condition-sensitive
`ItemsIgnoringCondition` tests before treating their intended assertions as
current guarantees.

Use the repository's [test guidance](../../../.github/skills/running-unit-tests/SKILL.md).
For implementation validation, the core affected projects are:

```bash
dotnet test src/Build.UnitTests/Microsoft.Build.Engine.UnitTests.csproj -c Release
dotnet test src/Build.OM.UnitTests/Microsoft.Build.Engine.OM.UnitTests.csproj -c Release
```

Use focused xUnit v3 method/class filters during development, and expand to
affected backend/task/logging tests as those integrations land. The current
work targets modern .NET on Linux. Full
implementation validation includes the repository build and bootstrap sample
checks described in `AGENTS.md`; these commands are planned checks, not
results of writing this document.

## Performance validation and acceptance

Use a release/bootstrap build of the local engine, not accidentally the
installed SDK engine. Follow [bootstrap guidance](../../../.github/skills/use-bootstrap-msbuild/SKILL.md).

The acceptance workload is the restore plan's clean, package-cached
static-graph restore, measured with balanced pairs and identical assets.
Instructions per configuration is the progress metric and restore wall time
the acceptance metric. Later, the matrix extends to cold-process and
warm-process command-line builds, multi-targeting, a target-executing build,
and a no-op incremental build, with ordinary logging and a typical binary-log
invocation.

Use both small controlled shapes and representative project/solution
workloads. Keep SDK, target graph, package/cache state, output state, node
reuse, PGO, and logging comparable. Each scenario has its own baseline;
do not pool different machines, cohorts, or cache regimes.

Measure:

- Exclusive construction, application, snapshot, publication, and execution
  costs, including preparation amortization and fallback overhead.
- Whole-invocation instructions where hardware counters exist, process-tree
  CPU, and paired elapsed time. On machines without counters, use CPU/task
  clock and do not infer instructions, cycles, or IPC.
- Allocations, GC costs, peak/retained bytes, live versions, and copied
  snapshot/descriptor storage.
- Cost-weighted compact coverage, backstop reasons, full-promotion reasons,
  task adapters, and logging/transfer conversion.
- Ordered items/metadata and errors/effects in differential cases; task
  inputs/outputs, restore assets, built outputs, and incremental decisions
  for complete invocations.

Keep detailed instrumentation diagnostic-only. Do not add per-row tracing,
list hashing, or allocation-heavy counters to the normal path.

Retain repeatable, bounded phase-level savings with modest implementation
and memory cost. Judge the accumulated design on complete restore
throughput: an evaluation saving that reappears during binding, logging, or
transfer is not an end-to-end win. Separate CPU savings from parallel
critical-path savings: evaluation runs ~10 threads wide on 8 cores, so an
evaluation saving of X CPU seconds is worth about X/10 seconds of wall time,
while execution is serial. A restore result does not establish the
ordinary-build result.

Reject or revise a stage if common prefix captures cause quadratic copying,
compatibility boundaries force immediate full promotion, fallback dominates
the measured item work, preparation costs outweigh execution savings, or
obsolete state causes substantial retention. IDE regressions are acceptable
while correctness holds.

## Follow-on work

Once compact results have trustworthy ownership and consumers can retain
them, evaluate coarse attachment of repeated pure item regions. Identity
must originate from shared producers; equivalent contents cannot be assumed
from unrelated snapshot handles. Include keying, misses, publication, and
retained variants in that experiment.

Demand-rooted evaluation is a separate consumer optimization. It may avoid
unrequested pure regions but must retain required effects, errors, and prefix
dependencies. Neither follow-on is needed to justify cheaper execution of
the common full-build item program.
