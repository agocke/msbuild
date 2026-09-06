# Hardened static graph validation roadmap

This document describes an incremental implementation of the
[hardened static graph specification](hardened-static-graph.md).

The implementation goal is validation. Hardened mode proves that a build uses
only constructs from which a deterministic, cacheable execution graph could be
constructed. It does not introduce a second scheduler, a custom task workflow,
or task-result caching.

Normal MSBuild remains responsible for target ordering, batching, task
execution, output gathering, failure handling, and logging.

## Principles

- Existing behavior is unchanged unless hardened validation is explicitly
  enabled.
- Every hardened diagnostic is an error. Hardened mode is opt-in, so no
  ChangeWave or warning transition is required.
- Invalid and not-yet-supported constructs fail explicitly. Validation never
  falls back to ordinary behavior for part of a hardened build.
- Validation runs before non-Pure task execution so an invalid build does not
  perform declared side effects before failing.
- The validator reuses the evaluated project, target definitions, expression
  expansion rules, target-ordering rules, task resolution, and batching model
  already owned by MSBuild.
- The validator tracks only facts needed to accept or reject the build and to
  explain an error. It does not construct an executable workflow.
- Properties, item lists, and metadata are classified as static or deferred.
  Origins are retained only to produce useful dependency-chain diagnostics.
- Task annotations are trusted. The engine validates annotation binding and
  declared MSBuild expressions, not task implementation behavior.
- Hash encoding, result lookup, storage, transfer, replay, and eviction are
  outside this roadmap.

## Validation boundary

Hardened validation covers two existing MSBuild phases.

### Evaluation validation

Evaluation validation enforces the graph-construction restrictions that are
visible while MSBuild evaluates the project:

- exact imports after property expansion;
- the graph-construction property-function allowlist;
- declared environment-variable reads;
- workspace-bounded built-in globs;
- supported SDK and toolset resolution;
- recorded project and import inputs.

These checks belong in the existing evaluator and expander paths because a
post-evaluation pass cannot reliably reconstruct which functions, environment
reads, missing-path queries, or glob enumerations affected evaluation.

### Target validation

Target validation walks the targets selected by the ordinary MSBuild target
scheduler before non-Pure tasks execute. It checks:

- the graph-construction property-function allowlist in every target-body
  expression, including `PropertyGroup` values and conditions;
- statically resolvable target edges and batching;
- supported target-body constructs;
- task classification;
- static and deferred property, item-list, and metadata flow;
- static-context requirements;
- Declared-IO path expressions and declared read-set containment;
- cross-project routing through `MSBuild` task invocations.

The walk may maintain property and item availability state, but its output is
only success or resource-backed MSBuild errors. It does not produce task
invocation nodes or an alternate execution plan.

## Soundness proposition

The target validator exists to prove this proposition:

> Before any deferred execution runs, every possible Declared-IO task
> invocation has a finite, statically enumerable input and output footprint.

Task annotations supply trusted premises: the resolved implementation is bound
to its manifest, every external read and write is declared, and each declaration
is a finite expression over task parameters. The validator must establish the
remaining lemmas:

1. The complete requested project and target closure is validated before any
   non-Pure task in that closure executes.
2. Every invocation, target bucket, task bucket, condition outcome, and failure
   path that ordinary MSBuild can execute is represented.
3. Property, item, and metadata state at each program point conservatively
   contains every feasible ordinary execution state.
4. Batch keys, bucket order, bucket-local views, sibling isolation, and scope
   merge timing match ordinary MSBuild.
5. Task routing, output conditions, output `TaskParameter` values, and expanded
   `PropertyName` and `ItemName` destinations are static for each invocation.
6. Declared read and write expressions have static structure. Deferred
   parameter values are permitted only when they resolve to concrete paths
   before the invocation becomes runnable.
7. No value classified as Static depends on an unrecorded ambient observation
   such as a target-time glob, file timestamp, current directory, environment,
   clock, network, or file-content read.

Rejecting a legal build is a completeness limitation. Accepting a build while
omitting a feasible invocation, input, output, or path is a soundness failure.
Precision improvements that prune false conditions therefore depend on the
state- and bucket-equivalence lemmas above.

Before allocation measurement or diagnostic-inventory pinning, differential
gates must cover:

- target-level buckets and per-bucket returns;
- ordinary batchable-expression discovery order for properties and tasks;
- sibling-bucket isolation and ordered scope merging;
- false `PropertyGroup`, `ItemGroup`, item, task, and output conditions;
- dynamic task-output destinations;
- `OnError` state at every feasible failure prefix;
- target-time globs and file-time metadata;
- cross-project validation-before-execution and worker-node flag propagation.

Declared-IO manifest binding, finite `Reads` and `Writes`, ready-time path
resolution, read-set containment, and output-overlap checks remain required
premises before the proposition can be considered established.

## First implementation slice

### Purpose

The first slice proves the central validator: invalid target constructs and
invalid static/deferred data flow are rejected with actionable errors.

It is invoked over an already evaluated `ProjectInstance` and the requested
targets. Task classifications are supplied directly until sidecar discovery
is implemented.

### Supported validation

The first slice supports:

- one project configuration;
- requested targets and their statically resolved `DependsOnTargets`,
  `BeforeTargets`, and `AfterTargets` closure;
- ordered `PropertyGroup`, `ItemGroup`, and task children;
- property and item expressions needed by the test cases;
- the graph-construction property-function allowlist in all target-body
  conditions and values;
- unbatched task invocations;
- Pure, Declared-IO, and Unaudited classifications supplied by the test;
- static and deferred property and item outputs;
- direct flow of a deferred output into a later non-Pure task parameter;
- origin tracking for deferred values;
- errors when deferred values enter static contexts;
- collection and deduplication of independent validation errors across the
  reachable target closure before execution begins;
- ordinary target `Returns`, including deferred return values;
- pure allowlisted `System.IO.Path` operations.

The first slice rejects as not yet supported:

- target and task batching;
- `CallTarget` and `MSBuild`;
- `OnError` and `ContinueOnError`;
- target `Inputs` and `Outputs`;
- sidecar manifest discovery;
- filesystem and evaluation validation;
- executing Pure or non-Pure tasks;
- graph construction and result caching.

These restrictions bound the first validator. They are not proposed final
hardened-mode semantics.

## Hello-world failure burn-down plan

### Baseline

A no-restore build of an SDK-style hello-world project with the repository
bootstrap SDK currently produces 849 unique target-validation diagnostics
across the requested `Build` closure:

| Code | Count | Meaning |
| --- | ---: | --- |
| `MSB4286` | 416 | The validator does not yet model a legal MSBuild construct. |
| `MSB4287` | 46 | A target expression observes ambient state. |
| `MSB4288` | 387 | A deferred task output reaches a static context. |

The `MSB4286` failures divide into these implementation areas:

| Construct | Count |
| --- | ---: |
| Metadata expressions and batching | 197 |
| Metadata in in-target `ItemGroup` operations | 130 |
| Target `Outputs` | 30 |
| Target `Inputs` | 24 |
| `ContinueOnError` | 17 |
| `MSBuild` task | 14 |
| `CallTarget` task | 3 |
| `OnError` | 1 |

The `MSB4287` failures are 41 `Exists` calls and five
`System.IO.Path.GetFullPath` calls. The `MSB4288` failures are not 387
independent design problems: many are cascades from treating every unannotated
task as Unaudited. High-fan-out origins include `MSBuild`,
`ResolvePackageAssets`, `AssignLinkMetadata`, `LocateRepository`,
`ResolveComReference`, `ResolveAssemblyReference`, `AssignTargetPath`,
`ResolveTargetingPackAssets`, `AssignCulture`, and `Copy`.

`ResolvePackageAssets` and `ProcessFrameworkReferences` are architectural root
causes, not merely missing task annotations. Restore currently writes
`project.assets.json`; later targets test for that file, parse it in
`ResolvePackageAssets`, and reconstruct the item groups consumed by compilation,
copying, publishing, and project-reference logic. `ProcessFrameworkReferences`
also combines configuration lookup, RID traversal, installed-pack probing, and
generation of additional restore inputs. That pipeline discovers graph inputs
after evaluation instead of importing the resolved item model directly.

This baseline covers target validation only. Evaluation restrictions, imports,
environment reads, SDK resolution, and evaluation-time globs are not yet
represented and will add a separate failure inventory.

After the metadata, expression, target-closure, target input/output, ordinary
control-flow, intrinsic routing-input, statically false task-pruning, and
target-return propagation quanta, the same pinned build produces 56
diagnostics:

| Code | Count |
| --- | ---: |
| `MSB4286` | 0 |
| `MSB4287` | 22 |
| `MSB4288` | 34 |

The blanket metadata diagnostics are gone. Static target conditions are now
evaluated against sequential target-time property assignments, so unreachable
dependencies and target bodies no longer contribute ambient-read or deferred
value cascades. Target `Inputs` and `Outputs` retain batching semantics without
timestamp skipping, and `Outputs` retains its legacy return role when
`Returns` is absent. `ContinueOnError` is validated as a static, batchable task
control expression rather than rejected by its presence. Statically named
`OnError` targets are included in the validated failure closure, and
`MSBuildLastTaskResult` is treated as task status rather than a static value.
Static `CallTarget` target lists are included in the closure using legacy
lookup isolation: called targets see the lookup from the start of the caller,
their mutations remain hidden while the caller runs, and caller mutations win
when both scopes merge at target completion. This exposes one additional
`MSBuild` invocation in the real closure while eliminating all three blanket
`CallTarget` diagnostics. The `MSBuild` intrinsic now requires static project
identities, target names, global-property transformations, removals,
tools-version selection, and the corresponding per-project metadata instead
of being rejected wholesale. Its target outputs remain deferred, and
cross-project target traversal remains part of the later project-edge work.
Task batching is validated before condition evaluation, but a task whose
condition is statically false no longer contributes parameter diagnostics,
outputs, `MSBuildLastTaskResult`, or intrinsic target edges.
`CallTarget` `TargetOutputs` now preserve the availability and origin of the
called targets' `Returns`, or legacy `Outputs` when `Returns` is absent,
instead of becoming Deferred solely because they crossed the intrinsic task.
Active dependency, `BeforeTargets`, `CallTarget`, and failure-path revisits
produce the scheduler's existing `MSB4006` circular-dependency error;
`AfterTargets` revisits retain the ordinary scheduler's cycle suppression.

### Burn-down rules

- Fix validator blind spots before changing SDK targets merely to satisfy the
  current prototype.
- Count unique diagnostics by code, source file, line, column, and message.
  Console-summary repetition is not a second failure.
- Preserve the complete inventory, but distinguish root diagnostics from
  diagnostics blocked by an earlier unsupported construct or unknown task
  classification.
- Re-run the same pinned hello-world build after every workstream and record
  the count by code, construct, source file, target, and producing task.
- Add a focused positive and negative test before removing each diagnostic
  class.
- Do not weaken a normative restriction to reduce the count. When a construct
  is genuinely incompatible, migrate it to fetch or restructure the target.

### Workstream 1 - Make the inventory causal

The current collector reports every visible failure, but it can emit large
cascades after losing precise availability information.

1. Give each diagnostic a stable root identifier.
2. Mark later diagnostics as blocked when their availability depends on an
   unsupported expression, unknown metadata state, or unresolved task
   classification.
3. Keep blocked diagnostics in the machine-readable inventory while making the
   root count the primary burn-down metric.
4. Emit a compact end-of-validation summary grouped by code, construct, target,
   and origin task.
5. Add an optional output file for the deduplicated inventory so normal console
   error repetition is not used as the working data set.

Completion requires deterministic ordering and byte-identical inventories for
repeated builds of the same evaluated project.

### Workstream 2 - Model native metadata and batching

This is the largest direct validator gap: 327 `MSB4286` failures.

1. Reuse `BatchingEngine` and `ExpressionShredder` to identify item vectors,
   transforms, qualified and unqualified metadata, and batching buckets.
2. Track item-list membership, item identity, and each metadata value
   independently as Static, Deferred, or Blocked.
3. Support metadata assignment, `KeepMetadata`, `RemoveMetadata`,
   `MatchOnMetadata`, `MatchOnMetadataOptions`, `KeepDuplicates`, `Remove`, and
   `Update` with ordinary MSBuild evaluation order.
4. Permit metadata in task parameters and conditions when the selected
   metadata is static.
5. Report a stall only when deferred membership or metadata determines a
   condition, batch partition, item operation, target edge, or Pure-task
   parameter.

Completion requires `_CollectTargetFrameworkForTelemetry` to validate without
target-name or SDK special-casing and eliminating the blanket metadata
diagnostics from the pinned hello-world inventory. Any remaining metadata error
must identify the exact deferred item or metadata origin.

#### Bucket-sensitive value-state plan

The initial metadata slice shares MSBuild's rules for associating metadata
references with item types, but its changing state remains coarser than
ordinary batching. It stores one state for an item type and metadata name,
rather than preserving the state of each selected item and evaluating the
operation in the corresponding batch. Complete Workstream 2 using existing
MSBuild nodes and scopes; do not add a validation executor.

##### Attachment points

Use two kinds of existing node:

1. Key immutable expression descriptors by the evaluated target-body nodes:
   `ProjectTargetInstance`, `ProjectPropertyGroupTaskPropertyInstance`,
   `ProjectItemGroupTaskItemInstance`, `ProjectTaskInstance`, and
   `ProjectTaskInstanceChild`. A descriptor records referenced properties,
   item vectors, transforms, metadata, the implicit item type, and which
   references form batch keys. Cache descriptors only when hardened validation
   is active. Do not add availability fields to every evaluated node.
2. Add an optional hardened payload to each active `Lookup.Scope`. The payload
   records only values that differ from the implicit Static evaluated state:
   - property abstract value by property name, where `Static` carries the exact
     concrete string rather than only an availability bit;
   - item-list membership and identity state by item type;
   - metadata state by concrete `ProjectItemInstance` and metadata name;
   - list-level default state for deferred items that have no concrete
     `ProjectItemInstance`;
   - the `ValueOrigin` chain for every non-Static entry.

`Lookup` remains the concrete-value store and its optional scope payloads
remain the availability/origin store. Neither is sufficient by itself.
The two stores are updated through one facade: a target-time mutation may be
recorded as `Static` only when it also supplies the new concrete value to
`Lookup`. Otherwise the result is `Deferred` or `Blocked`. Consumers obtain a
concrete value only by unwrapping a `Static(value)` result, so a stale value
remaining in `Lookup` cannot be used as proof of staticness.

Do not put availability fields directly on `ProjectItemInstance` or
`ProjectPropertyInstance`. Deferred values may have no concrete instance, and
per-instance fields would add feature-off memory to every ordinary build.

`ItemBucket` remains the bucket boundary rather than becoming a second state
owner. Its existing cloned `Lookup`, entered scope, truncated item types,
metadata table, sequence number, and `Expander` already define the concrete
MSBuild batch. `Lookup.Clone`, `EnterScope`, and scope leave/merge operations
carry and merge the optional payload through those existing boundaries; do not
create a parallel scope hierarchy.

##### Required invariants

- No companion state is allocated when hardened validation is disabled.
- Staticness is constructive: every `Static` property, item identity, metadata
  value, or transformed item sequence has a concrete value or exact ordered
  witness. A bare availability transition to `Static` is invalid.
- Validation never invokes a task in order to create a value or a bucket.
- Only concrete Static membership, identities, and batch-key metadata may
  participate in bucket construction.
- Deferred membership or a deferred batch key reports a stall before
  `ItemBucket` construction.
- Deferred metadata that is only task payload remains deferred and may flow to
  Declared-IO or Unaudited tasks.
- Bucket-local property and item changes are invisible to sibling buckets
  until their scopes are merged through the ordinary `Lookup` rules.
- Caller, `CallTarget`, target, and task scopes preserve the same precedence as
  ordinary execution.
- Origins survive bucket partitioning, transforms, metadata filters, item
  operations, and scope merges.
- The implementation may reuse source-expression and batch-descriptor ideas
  from the interpretation prototype, but not its action graph, executor, task
  binding, or fallback workflow.

##### Implementation quanta

Each quantum is independently tested and committed.

1. **Expression descriptors**
   - Add a hardened descriptor cache keyed by immutable target-body nodes.
   - Factor dependency extraction through `ExpressionShredder` and the useful
     source-expression representation from the interpretation prototype.
   - Record qualified and unqualified metadata, transform metadata,
     metadata-sensitive item-function arguments, and implicit item types.
   - Preserve current validator behavior in this quantum.

2. **Lookup companion state**
   - Introduce the optional `HardenedLookupState` facade and sparse
     `Lookup.Scope` payload.
   - Follow `Lookup` clone, scope entry, truncation, property set, item add,
     item remove, metadata modification, and scope-leave operations without
     constructing a second scope chain.
   - Lazily materialize per-item state only for item types touched by target
     operations or batching.
   - Move the current global property/item availability overlay onto this
     scope-aware representation without changing diagnostics.

3. **Ordinary bucket construction**
   - Factor the reusable analysis and partitioning seams from
     `BatchingEngine.PrepareBatchingBuckets`; do not duplicate its association,
     comparison, ordering, or cross-product rules.
   - Validate participating item membership, identities, and metadata before
     partitioning.
   - Construct the ordinary `ItemBucket` objects when all keys are concrete
     and Static.
   - Preserve default-bucket behavior and bucket sequence ordering.

4. **Per-bucket validation**
   - Validate target, property, item, task, and output conditions using the
     bucket's existing `Expander`.
   - Resolve metadata-computed property names, item names, output
     destinations, and target return expressions in each bucket.
   - Apply abstract property, item, and metadata mutations to the bucket's
     companion scope without invoking tasks.
   - Merge bucket scopes using the ordinary `Lookup` precedence rules.

5. **Expression completion and differential gates**
   - Cover metadata named by item functions such as `WithMetadataValue`,
     `AnyHaveMetadataValue`, `HasMetadata`, and `Metadata`.
   - Compare validator bucket count, order, selected items, condition results,
     expanded destinations, and merged state against ordinary MSBuild.
   - Measure feature-off allocations and hardened-mode cost before broadening
     the inventory.

##### Completion gates

- A static `PropertyName="Result_%(Input.Kind)"` or
  `ItemName="Result_%(Input.Kind)"` produces the same destinations and order as
  ordinary MSBuild.
- A statically false per-bucket condition contributes no state changes in that
  bucket, while other buckets still apply.
- One item with deferred payload metadata does not make the same metadata on
  unrelated static items deferred.
- Deferred membership, identity, or batch-key metadata reports the exact item,
  metadata, producer, and consuming construct.
- Qualified metadata, unqualified metadata, implicit item types, empty lists,
  transforms, and cross-product batching match ordinary MSBuild.
- Target batching and task, property-group, item-group, and output batching
  use the same partitioning rules.
- Repeated validation produces identical diagnostics and origin chains.
- Ordinary builds have no new allocations attributable to hardened companion
  state and retain unchanged behavior.
- No SDK or NuGet targets are modified to satisfy these gates.

### Workstream 3 - Support ordinary control and routing

These constructs account for 35 direct `MSB4286` failures and also create
deferred cascades.

1. Validate `ContinueOnError` as a static task-control expression. Its presence
   is not itself illegal.
2. Add statically named `OnError` targets to the validated closure. Treat
   `MSBuildLastTaskResult` as execution-derived, so using it to construct later
   graph structure remains a stall.
3. Permit `CallTarget` only when its target list is static, and validate the
   called targets using its existing lookup-isolation semantics.
4. Treat `MSBuild` as the existing cross-project routing primitive rather than
   an ordinary Unaudited task. Require `Projects`, target names, global
   properties, `AdditionalProperties`, and batching inputs to be static.
5. Reuse `ProjectGraph`, `ProjectInterpretation`, and the project-reference
   protocol for child configurations; do not create a second project
   scheduler.

Completion requires the hello-world project-reference and target-framework
routing targets to validate without special-casing their names.

### Workstream 4 - Replace assets-file ingestion with imported items

This is the first architectural migration. It must precede any attempt to mark
framework or package resolution tasks Pure. `ProcessFrameworkReferences` and
`ResolvePackageAssets` are not candidates for Pure annotations in their current
forms.

1. Make restore/fetch emit fixed, exactly named `.props` and `.targets` files
   containing the complete resolved item model.
2. Import those files during the fresh post-restore evaluation. The imported
   items must cover compile assemblies, runtime assemblies, native assets,
   resources, analyzers, content, transitive project references, transitive
   framework references, package folders, app hosts, targeting packs, runtime
   packs, and package provenance.
3. Include all metadata currently reconstructed from `project.assets.json`,
   including target framework, RID, asset role, package identity and version,
   path, assembly version, file version, copy-local state, and related asset
   relationships.
4. Keep `project.assets.json` as an optional diagnostic or compatibility
   artifact, but remove it as an input to downstream hardened graph
   construction.
5. Replace `ResolvePackageAssets` with pure filtering and projection over the
   imported items. It must not open an assets file or maintain a parsed-assets
   cache.
6. Move framework-pack, runtime-pack, workload, and RID discovery into fetch
   and the pinned SDK/pack index. The build-phase
   `ProcessFrameworkReferences` residual is a pure lookup over:

   ```text
   (framework, version, target platform, rid, self-contained, publish modes)
   ```

7. Remove installed-pack probing, `RuntimeGraphPath` file reads, and generation
   of new `PackageDownload` or implicit `PackageReference` items from the
   build-phase `ProcessFrameworkReferences` invocation. Required downloads are
   outputs of fetch, not discoveries made while constructing the build graph.
8. Make transitive framework references and project references ordinary
   imported items rather than outputs reconstructed by
   `ResolvePackageAssets`.
9. Remove downstream assets-file readers such as pack and project-reference
   helper tasks. They consume the imported item groups instead.

Completion requires deleting `ProjectAssetsFile` from the hardened inputs of
`ResolvePackageAssets`, eliminating its deferred-output fan-out, and making
`ProcessFrameworkReferences` valid as a Pure task without trusting filesystem
state.

### Workstream 5 - Resolve and apply task classifications

This workstream turns the 387 `MSB4288` symptoms into a smaller set of real
stalls.

1. Implement sidecar discovery and assembly-hash binding.
2. Produce a manifest for MSBuild-shipped tasks and coordinate equivalent
   manifests for SDK, NuGet, SourceLink, and analyzer tasks.
3. Classify high-fan-out tasks first, in this order:
   - pure item and property transforms such as `AssignTargetPath`,
     `AssignCulture`, `AssignLinkMetadata`, and
     `AssignProjectConfiguration`;
   - the residual framework and package projections after Workstream 4 has
     replaced assets-file ingestion;
   - Declared-IO producers such as `Copy`, resource generation, and generated
     source writers;
   - remaining discovery tasks such as `ResolveAssemblyReference`, whose
     discovery must move to fetch. Any task that still parses
     `project.assets.json` is removed under Workstream 4 rather than classified.
4. Re-run the inventory after each classification group. Do not annotate a
   task Pure merely because doing so removes downstream errors.
5. For every remaining `MSB4288`, choose one repair:
   - make the producer Pure;
   - move discovery to fetch;
   - pass the deferred value directly to a non-Pure task;
   - move item shaping inside the producing task invocation;
   - reject the project as a genuine stall.

Completion requires every remaining deferred diagnostic to begin at a trusted
classification and represent an actual graph-construction dependency.

### Workstream 6 - Remove ambient target functions

1. Replace `Exists` used for SDK, pack, or tool discovery with fetched,
   declared items.
2. Replace output-existence and timestamp conditions with normal task
   invocation plus declared inputs and outputs.
3. Replace optional-file tests with explicit item presence supplied by fetch
   when the file changes graph structure.
4. Extend the function parser to distinguish overloads. Permit
   `Path.GetFullPath(path, basePath)` when both arguments are static, while
   continuing to reject the overload that reads the process working
   directory.
5. Keep the five current `GetFullPath` sites invalid until each supplies an
   explicit base path or uses another pure path operation.

Completion requires zero `MSB4287` diagnostics in the pinned hello-world
target closure.

### Workstream 7 - Define hardened target incrementality

The 54 `Inputs` and `Outputs` diagnostics need engine semantics, not mechanical
deletion from SDK targets.

1. Preserve their ordinary target-batching expressions and validate those
   expressions using static/deferred availability.
2. In hardened mode, bypass timestamp-based target skipping in
   `TargetUpToDateChecker`; ordinary mode remains unchanged.
3. Preserve `Outputs` as the legacy return value only when `Returns` is absent.
4. Convert query and project-reference targets that use `Outputs` only for
   return routing to explicit `Returns`.
5. Record true incremental targets in a migration ledger for eventual
   task-invocation input/output hashing. Do not implement cache lookup or
   replay as part of this burn-down.

Completion requires zero raw-presence errors for `Inputs` or `Outputs` while
still rejecting deferred values that would determine target batching.

### Workstream 8 - Cross-repository migration and gates

Ownership follows the source of each target:

| Area | Primary responsibility |
| --- | --- |
| MSBuild engine | Availability model, batching, control flow, diagnostics, task-manifest binding, and hardened incrementality semantics. |
| MSBuild targets | `Microsoft.Common.CurrentVersion.targets` migrations and manifests for built-in tasks. |
| .NET SDK | Framework, package, telemetry, compilation, publish, and project-reference target migrations. |
| NuGet | Locked restore outputs, pack targets, and declarative asset items. |
| SourceLink and Roslyn tooling | Repository discovery and analyzer target migrations. |

Land engine support first, then flow it to the SDK and update targets against
that build. Gate each stage on:

1. the unique root-diagnostic count decreasing or staying constant with an
   explained reclassification;
2. no new ordinary-build behavior when hardened mode is off;
3. single- and multi-targeting hello-world builds;
4. project-reference, restore, build, publish, and design-time entry points;
5. Windows, Linux, and macOS;
6. a final zero-error hello-world target-validation inventory before enabling
   evaluation validation.

### Reuse from the interpretation prototype

The `msbuild-pure-eval` prototype contains useful source-expression lowering:

- `CompiledExpressionProgram.cs` identifies property expressions, item
  vectors, metadata expressions, conditions, target lists, task parameters,
  and output mappings;
- `EvaluationModule.cs` provides stable source-operation identities.

Port only the expression representation and dependency extraction needed to
determine which properties, item lists, and metadata an expression reads.

Do not port:

- `CompiledActionGraph`;
- `FastTaskAction`;
- task type or task-factory binding;
- reflective task parameter access;
- an execution graph or executor;
- execution instrumentation;
- fallback execution;
- runtime fast-path eligibility.

The reused representation should describe MSBuild source expressions, not an
alternate workflow.

### Proposed validator model

Add an internal area under:

```text
src/Build/Graph/Hardened/
```

with a small validation model:

```text
HardenedTargetValidator
HardenedValidationContext
HardenedTaskClassification
ValueAvailability
ValueOrigin
HardenedValidationError
```

`HardenedValidationContext` maintains the current availability of properties,
item-list membership, and metadata. Static evaluated state is implicit; the
context stores only target-time state that can differ from that default rather
than copying every evaluated property, item, and metadata value.

`ValueAvailability` has `Static`, `Deferred`, and `Blocked`. `Blocked` is a
validator-analysis state, not a third MSBuild value kind: it records that a
prior root diagnostic prevented the validator from determining availability,
so dependent expressions retain the origin chain without producing misleading
deferred-value cascades.

`ValueOrigin` identifies the task output or intermediate assignment that made
a value deferred. Origins form a diagnostic chain; they are not execution
dependencies.

The initial task classifier is an in-memory table supplied by tests. Sidecar
loading is a later milestone.

### Validation algorithm

1. Initialize properties and items from
   `ProjectInstance.PropertiesToBuildWith` and
   `ProjectInstance.ItemsToBuildWith` as static.
2. Traverse the requested target closure in ordinary dependency, `BeforeTargets`,
   body, and `AfterTargets` order. Missing requested or nested targets are
   collected as ordinary `MSB4057` diagnostics rather than escaping validation.
3. Maintain a property overlay over the evaluated `ProjectInstance` for
   concrete expansion of statically evaluable intrinsic property assignments.
   Unchanged properties and items delegate to the evaluated instance without
   copying or mutation. Availability and origin state remain in
   `HardenedValidationContext`; the concrete overlay never substitutes for it.
4. Locate each selected `ProjectTargetInstance`.
5. Reject unsupported target attributes and child element types at their
   source locations.
6. Visit `ProjectTargetInstance.Children` in source order.
7. Validate every function used by a target-body condition or value against
   the graph-construction property-function allowlist. This includes functions
   used in `PropertyGroup` assignments.
8. For a `PropertyGroup` or `ItemGroup`, determine the availability of every
   expression it reads and propagate that availability to its assignments.
9. Require static values for conditions, names, item membership operations,
   transforms, batching expressions, and other graph-construction positions.
10. For a task element, determine the availability of its condition,
   parameters, batching expressions, and output destinations.
11. Require every Pure-task parameter to be static.
12. Classify Pure-task outputs as values that would be static during full graph
   construction, and Declared-IO or Unaudited task outputs as deferred. The
   first slice validates availability only and does not execute the task to
   obtain a concrete output value.
13. Permit deferred parameters on Declared-IO and Unaudited task invocations
   when the parameter itself is not needed to determine graph structure.
14. When a static context reads a deferred value, report an error containing
   the producing task and output, intermediate assignments, consuming
   element, and the reason that context requires a static value.
15. Store the ordinary expansion result for every static target-body
   `ItemGroup` Include/Exclude operation under its operation identity and
   hierarchical target/item bucket path.
16. Complete without producing a separate execution graph or changing normal
   MSBuild state.

The validator must not use `Lookup` as its sole state. `Lookup` stores concrete
`ProjectPropertyInstance` and `ProjectItemInstance` values and cannot represent
deferred availability or origin chains.

The concrete expansion overlay is intentionally limited to values the
validator can reproduce without task execution or target-item batching. If an
expression depends on a task output or an item operation whose concrete value
has not been partially evaluated, the overlay must not fall back to the stale
evaluation-time value.

The item-operation companion plan is authoritative for hardened execution. It
stores ordered Include and Exclude expansions, final item snapshots,
`RecursiveDir`, copied metadata, and transitive source-item
provenance. `ItemGroupIntrinsicTask` materializes those snapshots through the
ordinary execution path. It never calls the glob implementation in hardened
execution, and a missing operation/bucket entry is an error rather than a
request to enumerate the filesystem.

The project directory is the implicit boundary for globs whose fixed directory
remains inside it. Upward or otherwise out-of-project traversal requires an
evaluated `<WorkspaceRoot>true</WorkspaceRoot>` marker; the directory
containing the winning property definition, normally `Directory.Build.props`,
is used as the wider boundary. Glob resolution validates both lexical paths
and link-resolved paths against the selected boundary before the result enters
the companion plan.

### Diagnostics

All errors must:

- use resource strings and an assigned `MSB4xxx` code;
- report the most specific available `IElementLocation`;
- name the project, target, task, property, item list, or metadata involved;
- state which construct is invalid;
- explain why hardened validation requires a static value or supported form;
- identify the repair direction.

The initial errors should distinguish:

- unsupported target attribute or child element;
- unsupported target or task batching;
- disallowed property function in a target-body expression;
- missing task classification;
- deferred task condition;
- deferred Pure-task parameter;
- deferred property used in a static context;
- deferred item list used in a static context;
- deferred metadata used in a static context.

No warning form is needed. These errors are reachable only through the new
opt-in mode or direct test entry point.

### Tests

Add focused tests under:

```text
src/Build.UnitTests/Graph/Hardened/
```

Required scenarios:

1. Initial evaluated properties and items are static.
2. Static property and item assignments remain static.
3. A Pure-task output is classified as static.
4. A Declared-IO property output is classified as deferred.
5. A Declared-IO item output is classified as deferred.
6. A deferred output may be passed to a later Declared-IO task parameter.
7. A deferred value supplied to a Pure task is rejected.
8. A deferred value used in a condition is rejected.
9. A deferred value used to determine item membership or batching is rejected.
10. The error contains the complete origin chain and source locations.
11. Unsupported target constructs fail rather than being ignored.
12. A disallowed property function in an in-target `PropertyGroup` value or
    condition is rejected.
13. Validation does not execute any test task or mutate the
    `ProjectInstance`.

### Completion criteria

The first slice is complete when it can reject representative invalid target
bodies, explain the deferred-value path that caused each failure, accept the
supported valid cases, and leave ordinary MSBuild execution untouched.

## Milestone 0 - Opt-in validation mode

### Goal

Add an experimental `--hardened-graph` switch with no behavior change when it
is disabled.

Use an internal two-state mode:

```text
Off
Validate
```

`Off` is the unconditional default. Do not add an `Execute` mode: execution
continues to belong to ordinary MSBuild.

The initial implementation accepts `--hardened-graph`,
`--hardened-graph:true`, and `--hardened-graph:false`. It forces a single
in-process build node so no unversioned field is added to the worker-node
protocol. Validation runs after the project is loaded and requested targets
are selected, but before `TargetBuilder` begins ordinary target execution.

The initial command-line surface may be experimental. A public
`BuildParameters` API should wait until the contract is stable.

Likely locations:

- `src/MSBuild/CommandLine/CommandLineSwitches.cs`
- `src/MSBuild/CommandLine/CommandLineSwitchesAccessor.cs`
- `src/MSBuild/XMake.cs`
- `src/Build/BackEnd/BuildManager/BuildParameters.cs`
- `src/Build/Resources/Strings.resx`
- `src/MSBuild.UnitTests/XMake_Tests.cs`

If the mode flows to worker nodes, add it to
`BuildParameters.ITranslatable.Translate`.

Tests must prove that no switch and `Off` preserve existing behavior, invalid
switch values are command-line errors, the mode reaches worker nodes, and
ordinary builds emit no hardened diagnostics.

## Milestone 1 - Evaluation restrictions

### Goal

Reject evaluation constructs that violate G1-G5 while continuing to use the
ordinary evaluator.

### Work

- Add a hardened validation context to evaluation.
- Restrict imports to one exact path after property expansion.
- Apply the graph-construction property-function allowlist.
- Reject undeclared environment reads.
- Require built-in globs to remain inside the declared workspace, including
  after resolving links.
- Reject unsupported custom SDK resolution and ambient upward searches.
- Record enough input facts for later validation of declared read-set
  containment; do not define cache keys or result hashes.

Likely locations:

- `src/Build/Evaluation/Evaluator.cs`
- `src/Build/Evaluation/Expander.Function.cs`
- `src/Build/Evaluation/Expander/WellKnownFunctions.cs`
- `src/Build/Evaluation/ConditionEvaluator.cs`
- `src/Build/Evaluation/Context/EvaluationContext.cs`
- `src/Build/Evaluation/LazyItemEvaluator.IncludeOperation.cs`
- `src/Build/BackEnd/Components/SdkResolution/`
- `src/Framework/Utilities/FileMatcher.cs`

The existing import loader and glob implementation remain responsible for
normal semantics. Hardened code validates requests before disallowed
enumeration or access occurs.

Completion requires focused tests for exact imports, rejected wildcard
imports, function allowlisting, declared and undeclared environment reads,
workspace-wide globs, workspace escapes, symlinks, and unchanged behavior when
validation is off.

## Milestone 2 - Task annotation resolution

### Goal

Resolve trusted sidecar annotations after MSBuild selects a task assembly.

### Work

- Define sidecar discovery, schema, and versioning.
- Bind the annotation to the selected task assembly content hash.
- Record the annotation manifest hash.
- Classify each invoked task as Pure, Declared-IO, or Unaudited.
- Parse declared read and write expressions without evaluating task code.
- Report malformed manifests, assembly-hash mismatches, and illegal
  declarations as resource-backed errors.

Likely locations:

- `src/Build/Instance/TaskRegistry.cs`
- `src/Build/BackEnd/TaskExecutionHost/TaskExecutionHost.cs`
- `src/Build/Instance/TaskFactories/`

This milestone does not require task implementation analysis, dependency
closure auditing, runtime or architecture identity, task-host identity,
sandboxing, or runtime access monitoring.

## Milestone 3 - Target-body validation integration

### Goal

Run static/deferred validation over all targets selected by normal MSBuild
ordering before any non-Pure task invocation executes.

### Work

- Integrate the first-slice validator with the existing target scheduling
  path.
- Reuse or factor `TargetBuilder.ProcessTargetStack` and
  `TargetEntry.GetDependencies`; do not implement a second target scheduler.
- Validate target conditions and `DependsOnTargets`.
- Add target and task batching validation using the existing batching rules.
- Support ordered `PropertyGroup` and `ItemGroup` operations.
- Apply the graph-construction property-function allowlist to every
  target-body expression, including `PropertyGroup` values and conditions.
- Validate `BeforeTargets`, `AfterTargets`, `CallTarget`, failure paths, and
  output mappings.
- Preserve origin chains across property assignment, item transforms,
  `Include`, `Remove`, and `Update`.
- Stop before non-Pure task execution if any error is found.

Likely locations:

- `src/Build/BackEnd/Components/RequestBuilder/TargetBuilder.cs`
- `src/Build/BackEnd/Components/RequestBuilder/TargetEntry.cs`
- `src/Build/BackEnd/Components/RequestBuilder/TaskBuilder.cs`
- `src/Build/BackEnd/Components/RequestBuilder/ITaskBuilder.cs`
- `src/Build/BackEnd/Components/RequestBuilder/Lookup.cs`
- `src/Build/BackEnd/Components/RequestBuilder/IntrinsicTask.cs`

Pure-task execution during graph construction is deliberately not introduced
by this roadmap. Validation may classify a Pure output as a value that would
be static, but cases that require its concrete value to resolve target topology
remain unsupported until their execution semantics are designed without
creating a parallel workflow.

## Milestone 4 - Declared-IO validation

### Goal

Validate T2 declarations without executing or monitoring the task.

### Work

- Require every declared path to be derived from task parameters by the
  allowed literal-composition expression language.
- Require destination names and declared path-expression structure to be
  static.
- Permit deferred parameter values where the declaration remains structurally
  valid and does not affect graph topology.
- Validate that the declared read set is contained in the conservative
  project input set.
- Detect overlapping declared outputs when statically decidable.
- Mark Unaudited invocations and containing project results as not cacheable
  in validation metadata; do not implement caching behavior.

The engine trusts that the task obeys its declaration. There is no sandbox,
filesystem tracker, post-execution verification, or implementation audit.

## Milestone 5 - Project and target edges

### Goal

Validate the full selected target graph and cross-project routing without
replacing `ProjectGraph` or the `MSBuild` task.

### Work

- Require statically resolvable requested targets.
- Validate `DependsOnTargets`, `BeforeTargets`, and `AfterTargets` using the
  ordinary scheduler's resolved edges.
- Reject computed `CallTarget` targets.
- Validate `MSBuild` task `Projects`, `Properties`, and
  `AdditionalProperties` as static contexts.
- Validate outer and inner builds and global-property transformations.
- Preserve project-reference protocol and target-return semantics.

Likely locations:

- `src/Build/Graph/ProjectGraph.cs`
- `src/Build/Graph/GraphBuilder.cs`
- `src/Build/Graph/ProjectInterpretation.cs`
- `src/Build/BackEnd/Components/RequestBuilder/IntrinsicTasks/MSBuild.cs`
- `src/Tasks/Microsoft.Common.CurrentVersion.targets`
- `src/Tasks/Microsoft.Common.CrossTargeting.targets`

## Milestone 6 - Fetch and SDK validation

### Goal

Validate the fetch build and consume its declarative outputs without adding a
new execution engine.

### Work

- Run fetch through ordinary MSBuild with hardened validation enabled.
- Require locked restore with explicit project-local configuration.
- Emit fixed `.props` and `.targets` containing the complete resolved item
  model; `project.assets.json` is not a downstream hardened input.
- Start a fresh evaluation that consumes those files as ordinary source.
- Validate SDK and pack index generation inputs.
- Replace `ResolvePackageAssets` parsing with pure projections over imported
  restore items.
- Migrate one framework and RID through a Pure
  `ProcessFrameworkReferences` lookup over the pinned SDK/pack index.
- Perform package override pruning during graph construction.
- Supply exact assembly-reference inputs that avoid ambient RAR discovery.

NuGet owns locked resolution and asset production. The SDK owns generated
import hooks, index generation, and migration of SDK targets. MSBuild owns the
validation rules and diagnostics.

## Later design work

The following areas require separate design before they can be validated
completely:

- Pure-task concrete outputs used to determine graph topology;
- full execution-failure semantics beyond static validation of
  `ContinueOnError`, `MSBuildLastTaskResult`, `OnError`, and cancellation;
- content-derived reads for compilers, RAR dependency traversal, and depfile
  producers;
- directory outputs, declared deletions, and stale-file removal;
- design-time build contracts;
- task-result equivalence and caching.

## Validation gates

Before broad SDK adoption, require:

- a negative test for every forbidden construct;
- a positive test for every supported construct;
- diagnostics with stable error codes, source locations, and repair guidance;
- target and task batching tests;
- `BeforeTargets` and `AfterTargets` tests;
- failure and `OnError` tests;
- multiprocessor and worker-node tests;
- multi-target project-reference tests;
- Windows, Linux, and macOS path tests;
- symlink and workspace-boundary tests;
- binlog tests proving validation errors are preserved;
- ordinary-build tests proving hardened validation is inert when disabled.

Performance tests should measure validator overhead and allocation cost, but
performance optimization follows a correct validation implementation. No
execution-graph format or custom-workflow benchmark is required.
