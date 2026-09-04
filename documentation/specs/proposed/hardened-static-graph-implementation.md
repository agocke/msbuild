# Hardened static graph implementation plan

This document describes an incremental implementation of the
[hardened static graph specification](hardened-static-graph.md).

The first implementation is an opt-in engine experiment. It proves
target-body partial evaluation, cuts, deferred task-invocation flow, and
static-context checking on a narrow non-SDK project. It does not initially
migrate restore, SDK resolution, project references, RAR, or compilation.

## Principles

- Existing behavior is unchanged unless hardened mode is explicitly enabled.
- New diagnostics are errors only in hardened mode. No new warnings are
  introduced.
- Unsupported constructs fail explicitly rather than falling back to ordinary
  execution inside a partially hardened build.
- The implementation reuses existing evaluation, target ordering, batching,
  task resolution, output gathering, and logging machinery wherever possible.
- Property and item state is shared and versioned. The implementation does not
  clone a complete `ProjectInstance`, property table, or item table at every
  task.
- Task annotations are trusted. The engine validates annotation matching and
  declared MSBuild expressions, not task implementation behavior.
- Result lookup, storage, transfer, replay, and eviction are outside the
  initial implementation.

## First implementation slice

### Purpose

The first implementation proves execution graph construction as a distinct
phase. It does not attempt to expose the feature through the command line or
execute the resulting graph.

It accepts an already evaluated `ProjectInstance` and one explicitly requested
target. It partially evaluates that target body, classifies each task
invocation, and produces an in-memory execution graph.

### Scope

The first slice supports:

- one project configuration;
- one explicitly requested target;
- an unbatched target;
- ordered `PropertyGroup`, `ItemGroup`, and task children;
- task conditions and parameter expressions;
- unbatched task invocations;
- Pure, Declared-IO, and Unaudited classification supplied directly by the
  test;
- static and deferred property and item outputs;
- a deferred output passed directly to a later non-Pure task invocation;
- a stall when a deferred property or item is used in a static context.

The first slice rejects:

- target and task batching;
- `BeforeTargets` and `AfterTargets`;
- `CallTarget` and `MSBuild`;
- `OnError` and `ContinueOnError`;
- target `Inputs`, `Outputs`, and `Returns`;
- sidecar manifest discovery;
- graph input recording and filesystem restrictions;
- executing the completed graph;
- result caching.

These restrictions bound the prototype. They are not proposed hardened-mode
semantics.

### Reuse the existing interpretation IR

The `msbuild-pure-eval` prototype already contains the source-only IR needed
for this slice:

- `CompiledExpressionProgram.cs` lowers property expressions, item vectors,
  conditions, target lists, and task parameter expressions;
- the source-program portion of `CompiledActionGraph.cs` lowers
  `ProjectTargetInstance.Children` into ordered target operations;
- `EvaluationModule.cs` provides stable source operation identities and
  reusable lowered evaluation operations.

Port only the source-derived representation and expression programs. Do not
bring over:

- `FastTaskAction`;
- task type or task-factory binding;
- direct reflective getters and setters;
- execution instrumentation;
- fallback execution;
- runtime fast-path eligibility rules.

Rename the imported concepts around task invocations rather than actions.
The source IR should remain independent of a loaded task type and reusable by
both execution graph construction and later optimized execution.

### Proposed model

Add an internal area under:

```text
src/Build/Graph/Hardened/
```

with an initial model similar to:

```text
ExecutionGraphConstructor
HardenedExecutionGraph
TargetConstructionProgram
TaskInvocationProgram
TaskInvocationNode
GraphProperty
GraphItemList
ValueAvailability
ValueOrigin
```

`GraphProperty` stores a static value or a deferred origin.

`GraphItemList` stores static or deferred membership. Metadata availability is
tracked independently for static items.

`ValueOrigin` identifies the producing task invocation and output parameter.
It is also the edge between producer and consumer invocations.

`TaskInvocationNode` records:

- source location and task name;
- Pure, Declared-IO, or Unaudited classification;
- static and deferred parameter expressions;
- declared read and write expressions for Declared-IO tasks;
- output property and item destinations;
- predecessor task invocations.

The initial classifier is an in-memory table supplied by tests. Sidecar loading
is a later integration step.

### Construction algorithm

1. Create static property and item state from
   `ProjectInstance.PropertiesToBuildWith` and
   `ProjectInstance.ItemsToBuildWith`.
2. Locate the explicitly requested `ProjectTargetInstance`.
3. Reject target batching, target `Inputs`, `Outputs`, `Returns`,
   `BeforeTargets`, `AfterTargets`, and `OnError`.
4. Evaluate the target condition from static state.
5. Visit `ProjectTargetInstance.Children` in source order.
6. For a `ProjectPropertyGroupTaskInstance` or
   `ProjectItemGroupTaskInstance`, evaluate it only when every value required
   by its condition, names, membership, and transforms is static.
7. For a `ProjectTaskInstance`, compile its condition, batching inputs,
   parameters, and output mappings through the reused source IR.
8. Reject metadata batching in the first slice. The task element therefore
   produces exactly one task invocation.
9. Classify that invocation from the test-supplied table.
10. For a Pure invocation, require static parameters and invoke the
    test-supplied Pure-task evaluator. Store its property and item outputs as
    static.
11. For a Declared-IO or Unaudited invocation, add a
    `TaskInvocationNode`. Store each property and item output as deferred and
    record its `ValueOrigin`.
12. When a later non-Pure task parameter reads a deferred value, add an edge
    from the producing invocation and preserve the deferred expression.
13. When a static context reads a deferred value, stop with a diagnostic that
    includes the producer, output, intermediate assignments, consumer, and
    required static context.
14. Return the completed in-memory execution graph.

The first slice should not use `Lookup` as the sole graph state because
`Lookup` can represent only concrete `ProjectPropertyInstance` and
`ProjectItemInstance` values. The graph state may use `Lookup` for its static
base, but deferred properties, item membership, and metadata require a
separate representation.

### Engine integration boundaries

The first slice is invoked directly by unit tests. It does not change
`TargetBuilder`, `TargetEntry`, `TaskBuilder`, `BuildManager`, or
`BuildParameters`.

The next integration step should reuse two existing seams:

- `TargetBuilder.ProcessTargetStack` and `TargetEntry.GetDependencies` for
  final target ordering;
- `ITaskBuilder` for processing each task element after target batching.

Do not duplicate the final `DependsOnTargets`, `BeforeTargets`, and
`AfterTargets` scheduler. The test-only constructor may support one target,
but production integration must factor or invoke the existing ordering code.

### Tests

Add focused tests under:

```text
src/Build.UnitTests/Graph/Hardened/
```

The first test project should contain:

```xml
<Target Name="Build">
  <PropertyGroup>
    <Prefix>obj/</Prefix>
  </PropertyGroup>

  <PureConcat Left="$(Prefix)" Right="generated.cs">
    <Output TaskParameter="Result" PropertyName="OutputPath" />
  </PureConcat>

  <DeclaredGenerate OutputPath="$(OutputPath)">
    <Output TaskParameter="GeneratedFiles" ItemName="Generated" />
  </DeclaredGenerate>

  <DeclaredCompile Sources="@(Generated)" />
</Target>
```

Required tests:

1. Pure output becomes a static property.
2. Declared-IO property output becomes deferred.
3. Declared-IO item output becomes a deferred item list.
4. A deferred item list passed to a later Declared-IO task creates an
   invocation dependency.
5. A deferred property in a target or task condition produces a stall.
6. A deferred property passed to a Pure task produces a stall.
7. An Unaudited invocation marks the graph as not fully cacheable.
8. Child ordering matches `ProjectTargetInstance.Children`.
9. Unsupported batching and target constructs fail explicitly.

### Completion criteria

The first slice is complete when:

- the same project always produces the same execution graph;
- Pure invocations affect later static construction state;
- non-Pure invocations produce deferred values without executing;
- deferred values flow directly between non-Pure task invocations;
- static contexts reject deferred values with a complete dependency chain;
- no existing build path changes when the constructor is unused.

## Repository ownership

### MSBuild

MSBuild owns:

- hardened-mode selection and diagnostics;
- graph input recording;
- restricted graph-construction expansion;
- target-body partial evaluation;
- task annotation resolution;
- cuts, static and deferred values, and stall dependency chains;
- deferred task invocations and making task invocations ready;
- task invocation dependency and I/O descriptions;
- cross-project value and target edges;
- integration with task output gathering and logging.

### dotnet/sdk

The SDK eventually owns:

- fixed import hooks for generated fetch output;
- SDK and pack index generation;
- migration of SDK targets to fetch output items;
- the Pure `ProcessFrameworkReferences` path;
- compile-time package override pruning;
- declared runtime conflict and output-layout task invocations;
- exact assembly-reference inputs for the pre-resolved RAR model.

### NuGet

NuGet eventually owns:

- explicit hermetic restore configuration;
- locked resolution and package integrity;
- asset items;
- fixed generated fetch files;
- a supported replacement for downstream `project.assets.json` parsing;
- the policy for package-provided build assets.

### Roslyn

Roslyn coordination is deferred until the content-derived-input class is
designed. Compilation remains Unaudited in the first implementation.

## Milestone 0 - Opt-in mode and inert plumbing

### Goal

Introduce a mode switch that has no effect when disabled and can initially
validate only deliberately selected test projects.

### Proposed mode

Use an internal enum with three values:

```text
Off
Validate
Execute
```

`Off` is the unconditional default.

`Validate` constructs and validates the execution graph without running it.

`Execute` constructs the execution graph and runs it without result caching.

The initial command-line surface can be experimental. A public
`BuildParameters` API should wait until the contract is stable.

### Likely code locations

- `src/MSBuild/CommandLine/CommandLineSwitches.cs`
- `src/MSBuild/CommandLine/CommandLineSwitchesAccessor.cs`
- `src/MSBuild/XMake.cs`
- `src/Build/BackEnd/BuildManager/BuildParameters.cs`
- `src/Build/Resources/Strings.resx`
- `src/MSBuild.UnitTests/XMake_Tests.cs`

If the mode flows to worker nodes, add it to
`BuildParameters.ITranslatable.Translate`.

### Diagnostics

Reserve no warning behavior. All validation failures are resource-backed
errors reachable only when the mode is enabled.

Early diagnostics should distinguish:

- unsupported request type;
- unsupported graph-construction function;
- non-exact import;
- undeclared environment read;
- unsupported glob or workspace escape;
- missing or mismatched annotation;
- deferred property supplied where a static property is required;
- deferred item list supplied where a static item list is required;
- unsupported target-body construct.

Each structural error should include project, target, task or element,
location, and a repair direction.

### Tests

Add command-line tests proving:

- no switch preserves existing behavior;
- `Off` is equivalent to no switch;
- invalid values fail as command-line errors;
- the mode reaches `BuildParameters`;
- ordinary builds do not emit hardened diagnostics.

## Milestone 1 - Graph input recording

### Goal

Record the graph inputs used by ordinary evaluation before interpreting target
bodies.

### Initial supported subset

The first subset supports:

- exact project and import paths;
- the built-in SDK resolver result selected by the current toolset;
- a declared environment allowlist;
- literal item includes;
- built-in globs confined to one declared workspace;
- no wildcard imports;
- no ambient upward import searches;
- no custom SDK resolver;
- no source roots outside the workspace.

Unsupported constructs fail in hardened mode.

### Proposed internal model

Create an internal hardened-graph area:

```text
src/Build/Graph/Hardened/
    HardenedGraphMode.cs
    HardenedGraphInputRecorder.cs
    GraphInputs.cs
    GraphFileInput.cs
    GraphDirectoryInput.cs
    GraphQueryInput.cs
    DeclaredEnvironment.cs
    WorkspaceBoundary.cs
```

The names are provisional. The model should distinguish:

- content reads;
- directory enumeration;
- glob expressions and results;
- missing-path queries;
- SDK resolution results;
- declared environment values.

The recorded graph inputs must have deterministic ordering and hashing. The
implementation should avoid retaining duplicate paths and file hashes across
projects where an existing evaluation context can share them.

### Likely integration points

- `src/Build/Evaluation/Evaluator.cs`
- `src/Build/Evaluation/Expander.Function.cs`
- `src/Build/Evaluation/Expander/WellKnownFunctions.cs`
- `src/Build/Evaluation/ConditionEvaluator.cs`
- `src/Build/Evaluation/Context/EvaluationContext.cs`
- `src/Build/Evaluation/LazyItemEvaluator.IncludeOperation.cs`
- `src/Build/Evaluation/PropertyTrackingEvaluatorDataWrapper.cs`
- `src/Build/BackEnd/Components/SdkResolution/`
- `src/Framework/Utilities/FileMatcher.cs`

Use `EvaluationContext.ContextWithFileSystem` as an initial injection point for
recording file access. Recording must also happen at engine operations such as
imports, SDK resolution, and globs so graph inputs distinguish content reads
from directory and resolution facts.

Extend the existing environment-property tracking path to reject undeclared
environment reads in graph-construction positions.

### Import strategy

Do not change normal import behavior.

In hardened mode:

1. Expand the import expression using the restricted expander.
2. Require one exact resulting path.
3. Resolve the supported SDK or toolset path.
4. Record the selected path and file content hash.
5. Load the import through the existing evaluator.

Wildcard imports and upward-search helper functions fail before filesystem
enumeration.

### Glob strategy

The existing glob implementation remains responsible for matching semantics.
A hardened wrapper supplies the workspace-boundary policy and records:

- the glob expression;
- include and exclude expressions;
- the enumerated directory facts required to reproduce the result;
- the sorted match list;
- the workspace path and hash.

The first implementation requires an explicitly supplied workspace root and
rejects paths and resolved links escaping that root. A project glob may
traverse parent directories and nested project directories as long as it
remains within the workspace.

### Tests

Primary test locations:

- `src/Build.UnitTests/Evaluation/Evaluator_Tests.cs`
- `src/Build.UnitTests/Evaluation/ExpanderFunction_Tests.cs`
- `src/Build.UnitTests/Evaluation/FileMatcherCulture_Tests.cs`
- `src/Build.UnitTests/BackEnd/SdkResolverService_Tests.cs`

Test:

- exact import recording;
- imported-content changes;
- a previously missing exact import becoming present;
- allowed and disallowed environment reads;
- deterministic glob ordering;
- adding and removing a glob match;
- cross-project workspace globbing;
- workspace-boundary rejection;
- case-sensitive and case-insensitive path behavior;
- no behavior change with hardened mode off.

### Completion criteria

Milestone 1 is complete when a fully evaluated non-SDK project records stable
graph inputs, relevant changes alter their hash, and unrelated changes do not.

## Milestone 2 - Target-body partial evaluation

### Goal

Interpret a deliberately small target subset through the first non-Pure task,
emit deferred task invocations, continue with deferred outputs, and report a
stall when those outputs enter a static context.

This milestone does not execute Declared-IO task invocations.

### Existing engine behavior to reuse

Evaluation registers target bodies as ordered `ProjectTargetInstance`
children. Target scheduling and execution are implemented primarily by:

- `src/Build/BackEnd/Components/RequestBuilder/TargetBuilder.cs`
- `src/Build/BackEnd/Components/RequestBuilder/TargetEntry.cs`
- `src/Build/BackEnd/Components/RequestBuilder/TaskBuilder.cs`
- `src/Build/BackEnd/Components/RequestBuilder/Lookup.cs`

`Lookup` already provides scoped copy-on-write property and item state.
`LazyItemEvaluator` provides examples of representing ordered item operations
without eagerly copying complete item collections.

The hardened planner should factor or invoke existing ordering behavior rather
than implement a separate interpretation of `DependsOnTargets`,
`BeforeTargets`, and `AfterTargets`.

### Proposed internal model

Extend the hardened graph area with:

```text
HardenedGraphPlanBuilder.cs
GraphValue.cs
GraphScalarValue.cs
GraphItemValue.cs
GraphMetadataValue.cs
GraphValueEnvironment.cs
DeferredTaskInvocation.cs
TaskInvocationOutput.cs
ValueOrigin.cs
ValueAvailability.cs
GraphItemShape.cs
StallDetector.cs
```

The value environment should use shared versioned state:

- each property assignment creates a new property value version;
- each item include, remove, or update creates an ordered item-operation node;
- item-list shape, item identity, and each metadata value are independently
  static or deferred;
- task invocations capture only the property and item versions consumed by
  their parameter expressions;
- a cut does not clone all properties and items.

### First supported target subset

Support:

- `PropertyGroup`;
- `ItemGroup` with typed `Include`, `Remove`, and `Update` expressions;
- target `Condition`;
- `DependsOnTargets`;
- one ordinary Pure task form;
- one Declared-IO task form;
- ordinary `<Output>` mappings with static destination names;
- direct Declared-IO output to a later task invocation parameter.

Target and task batching, `BeforeTargets`, `AfterTargets`, failure paths,
`CallTarget`, and the built-in `MSBuild` operation can initially produce
explicit unsupported-construct errors. They must not silently run through the
ordinary execution path.

### Test tasks

Add test-only tasks with sidecar annotations:

```text
PureConcat
DeclaredCopy
UnauditedValue
```

`PureConcat` produces a deterministic scalar or item value.

`DeclaredCopy` has parameter-derived source and destination paths.

`UnauditedValue` verifies T3 classification and deferred property and item
outputs without requiring an annotation.

The tests trust these annotations; they do not require a task analyzer or
runtime access monitor.

### Required scenarios

The first vertical-slice project should demonstrate:

1. Property mutation before and after a Pure task.
2. Item include, remove, update, transform, and metadata propagation.
3. A Declared-IO cut that emits a deferred item output.
4. That output flowing to a later Declared-IO task invocation.
5. The later task invocation becoming ready after its input is available.
6. The same output used in a target condition and rejected as a stall.
7. A dependency-chain diagnostic from the originating task output through the
   intermediate assignment to the illegal condition.
8. An Unaudited task marking the project as not cacheable.

### Likely tests

- `src/Build.UnitTests/BackEnd/TargetBuilder_Tests.cs`
- `src/Build.UnitTests/BackEnd/TargetEntry_Tests.cs`
- `src/Build.UnitTests/BackEnd/TaskBuilder_Tests.cs`
- `src/Build.UnitTests/BackEnd/Lookup_Tests.cs`
- new hardened graph model tests under `src/Build.UnitTests/Graph/`

For the supported subset, compare target ordering and final static properties
and item lists with an ordinary MSBuild execution of the same project.

### Completion criteria

Milestone 2 is complete when the planner produces a deterministic execution
graph for the vertical-slice project, continues across cuts without executing
them, passes deferred values directly between task invocations, and rejects
deferred values in static contexts with a useful dependency chain.

## Milestone 3 - Task annotation resolution

Resolve a sidecar manifest after MSBuild has selected the task assembly. Bind
the manifest to the assembly content hash and classify the task as Pure,
Declared-IO, or Unaudited.

Likely integration points:

- `src/Build/Instance/TaskRegistry.cs`
- `src/Build/BackEnd/TaskExecutionHost/TaskExecutionHost.cs`
- `src/Build/Instance/TaskFactories/`

This milestone does not require TaskAnalyzer approval, dependency closure
analysis, task-host identity, or runtime access enforcement.

The implementation must define:

- sidecar discovery;
- manifest schema and versioning;
- assembly-hash matching;
- manifest hashing;
- diagnostic behavior for malformed or mismatched manifests;
- declared path-expression representation.

## Milestone 4 - Execution graph representation

Produce an execution graph that can drive ordinary task execution and contains
the information a future caching layer will need.

Provisional components:

```text
HardenedExecutionGraph
TaskInvocationNode
TaskInvocationDependency
TaskInvocationExecutor
```

For each task invocation:

1. Resolve the annotation.
2. Record static and deferred parameter expressions.
3. Record predecessor task invocations and result edges.
4. Record declared input and output expressions.
5. Mark the invocation as Pure, Declared-IO, or Unaudited.
6. Mark the invocation and containing project as cacheable or not cacheable.
7. Execute through the existing task path when running the graph.
8. Gather task outputs through the existing output machinery.

The first graph supports explicit file inputs and outputs, trusted test tasks,
and no overlapping declared outputs. These are implementation limits, not
additions to the task trust model.

## Later milestones

The following milestones require additional design before implementation.

### Failure and output-tree semantics

Add task failure handling, `ContinueOnError`, `MSBuildLastTaskResult`,
`OnError`, cancellation, directory file lists, declared deletions, stale-file
removal, and task diagnostic behavior.

### Project references

Model `MSBuild` as an intrinsic and integrate outer and inner builds,
global-property transformations, requested targets, and target-return value
edges with `ProjectGraph`.

Likely locations:

- `src/Build/Graph/ProjectGraph.cs`
- `src/Build/Graph/GraphBuilder.cs`
- `src/Build/Graph/ProjectInterpretation.cs`
- `src/Build/BackEnd/Components/RequestBuilder/IntrinsicTasks/MSBuild.cs`
- `src/Tasks/Microsoft.Common.CurrentVersion.targets`
- `src/Tasks/Microsoft.Common.CrossTargeting.targets`

### Fetch

Run a locked fetch build, write fixed declarative `.props` and `.targets`,
clear evaluation state, and start a fresh hardened graph construction.

The first NuGet prototype should use one explicit configuration, locked
resolution, a local package source, asset items, and no executable package
build assets.

### SDK index and resolution

Migrate one target framework and RID through:

- a pinned framework and runtime-pack index;
- a Pure `ProcessFrameworkReferences` path;
- graph-construction `PackageOverrides` pruning;
- explicit runtime conflict outputs;
- exact assembly paths that bypass ambient RAR search.

### Content-derived inputs

Define the next task class for compilers, RAR dependency traversal, depfile
producers, and similar task invocations whose read set is derived from
declared file contents.

### Design-time builds and result caching

Design-time builds require a project-system contract and separate latency
goals. Task-result caching can be designed after the execution graph and task
invocation equivalence rules stabilize.

## Validation gates

Before broad SDK adoption, require:

- execution-graph differential tests for every supported task invocation;
- target and task batching tests;
- `BeforeTargets` and `AfterTargets` ordering tests;
- failure and `OnError` tests;
- multiprocessor and worker-node tests;
- multi-target project-reference tests;
- Windows, Linux, and macOS path tests;
- symlink and directory-output tests;
- stable binlog comparison-record tests;
- execution-graph format goldens;
- graph-construction allocation benchmarks.

Benchmarks should measure:

- graph input recording;
- shared property and item version count;
- workspace hashing;
- target-body partial-evaluation latency;
- making task invocations ready;
- large execution-graph construction time and memory;
- invalidation breadth caused by the conservative hashed superset.
