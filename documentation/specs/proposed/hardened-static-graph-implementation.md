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

It is invoked directly by unit tests over an already evaluated
`ProjectInstance` and one explicitly requested target. Task classifications
are supplied by the test. The slice does not change any existing build path.

### Supported validation

The first slice supports:

- one project configuration;
- one explicitly requested, unbatched target;
- ordered `PropertyGroup`, `ItemGroup`, and task children;
- property and item expressions needed by the test cases;
- the graph-construction property-function allowlist in all target-body
  conditions and values;
- unbatched task invocations;
- Pure, Declared-IO, and Unaudited classifications supplied by the test;
- static and deferred property and item outputs;
- direct flow of a deferred output into a later non-Pure task parameter;
- origin tracking for deferred values;
- errors when deferred values enter static contexts.

The first slice rejects as not yet supported:

- target and task batching;
- `DependsOnTargets`, `BeforeTargets`, and `AfterTargets`;
- `CallTarget` and `MSBuild`;
- `OnError` and `ContinueOnError`;
- target `Inputs`, `Outputs`, and `Returns`;
- sidecar manifest discovery;
- filesystem and evaluation validation;
- executing Pure or non-Pure tasks;
- graph construction and result caching.

These restrictions bound the first validator. They are not proposed final
hardened-mode semantics.

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

Add an experimental hardened validation mode with no behavior change when it
is disabled.

Use an internal two-state mode:

```text
Off
Validate
```

`Off` is the unconditional default. Do not add an `Execute` mode: execution
continues to belong to ordinary MSBuild.

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
- Emit fixed `.props` and `.targets` containing items only.
- Start a fresh evaluation that consumes those files as ordinary source.
- Validate SDK and pack index generation inputs.
- Migrate one framework and RID through a Pure
  `ProcessFrameworkReferences` lookup.
- Perform package override pruning during graph construction.
- Supply exact assembly-reference inputs that avoid ambient RAR discovery.

NuGet owns locked resolution and asset production. The SDK owns generated
import hooks, index generation, and migration of SDK targets. MSBuild owns the
validation rules and diagnostics.

## Later design work

The following areas require separate design before they can be validated
completely:

- Pure-task concrete outputs used to determine graph topology;
- `ContinueOnError`, `MSBuildLastTaskResult`, `OnError`, and cancellation;
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
