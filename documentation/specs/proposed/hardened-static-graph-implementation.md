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

Completion requires eliminating the blanket metadata diagnostics. Any
remaining metadata error must identify the exact deferred item or metadata
origin.

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
item-list membership, and metadata.

`ValueAvailability` has only `Static` and `Deferred`.

`ValueOrigin` identifies the task output or intermediate assignment that made
a value deferred. Origins form a diagnostic chain; they are not execution
dependencies.

The initial task classifier is an in-memory table supplied by tests. Sidecar
loading is a later milestone.

### Validation algorithm

1. Initialize properties and items from
   `ProjectInstance.PropertiesToBuildWith` and
   `ProjectInstance.ItemsToBuildWith` as static.
2. Locate the explicitly requested `ProjectTargetInstance`.
3. Reject unsupported target attributes and child element types at their
   source locations.
4. Visit `ProjectTargetInstance.Children` in source order.
5. Validate every function used by a target-body condition or value against
   the graph-construction property-function allowlist. This includes functions
   used in `PropertyGroup` assignments.
6. For a `PropertyGroup` or `ItemGroup`, determine the availability of every
   expression it reads and propagate that availability to its assignments.
7. Require static values for conditions, names, item membership operations,
   transforms, batching expressions, and other graph-construction positions.
8. For a task element, determine the availability of its condition,
   parameters, batching expressions, and output destinations.
9. Require every Pure-task parameter to be static.
10. Classify Pure-task outputs as values that would be static during full graph
   construction, and Declared-IO or Unaudited task outputs as deferred. The
   first slice validates availability only and does not execute the task to
   obtain a concrete output value.
11. Permit deferred parameters on Declared-IO and Unaudited task invocations
    when the parameter itself is not needed to determine graph structure.
12. When a static context reads a deferred value, report an error containing
    the producing task and output, intermediate assignments, consuming
    element, and the reason that context requires a static value.
13. Complete without producing an execution graph or changing normal MSBuild
    state.

The validator must not use `Lookup` as its sole state. `Lookup` stores concrete
`ProjectPropertyInstance` and `ProjectItemInstance` values and cannot represent
deferred availability or origin chains.

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
