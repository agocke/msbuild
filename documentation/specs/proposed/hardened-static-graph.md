# Hardened static graph

## Status

This document proposes an opt-in MSBuild execution mode that partially
evaluates project evaluation and target bodies, constructs an execution graph,
and records enough information for task invocation results to be cached by a
separate system.

The design is intentionally stricter than the existing
[static graph](../static-graph.md). Existing MSBuild behavior is unchanged when
the mode is disabled.

## Goals

The hardened static graph should:

- determine build topology before executing non-Pure task invocations;
- make graph construction deterministic from explicit graph inputs;
- express build work as task invocations with declared inputs and outputs;
- make task invocations independently cacheable;
- preserve MSBuild ordering and data-flow semantics within the supported
  language subset;
- report constructs that require execution to determine graph topology;
- make restore and other discovery work explicit rather than hiding it in
  evaluation or build execution.

The hardened mode does not attempt to prove task annotations correct. Task
classification and declared I/O are trusted assertions. This is the same trust
boundary as any user-authored incrementality declaration: the engine validates
the declaration where it can, but it does not monitor task implementation
behavior.

## Terminology

**Graph inputs** are the files, environment values, global properties, SDK
resolution results, glob results, and other engine reads used during graph
construction. Their paths, values, and content hashes are recorded.

**Execution graph construction** is deterministic partial evaluation of
MSBuild evaluation and target bodies against the graph inputs. It produces
project and target edges, shared versioned property and item state, control
flow, and task invocations. This document uses **graph construction** as a
short form.

A **deferred task invocation** is an invocation waiting for property or item
values from earlier task invocations. Its task, parameter expressions,
declared-I/O expressions, outputs, and predecessor edges are already known.

A **ready task invocation** has received all values required from earlier task
invocations. Its declared inputs and outputs can be calculated and it can
execute.

A **workspace** is the declared root within which projects may read source
files and evaluate globs. Its contents form the conservative hashed input
superset shared by projects in that workspace.

A **task invocation result** includes the task status, output properties and
items, and declared output files and directories.

Reuse of graph-construction results is **partial-evaluation reuse**.

Actual result caching is outside the scope of this specification. The
execution graph is designed to make a later caching layer possible, but this
design does not define result lookup, storage, eviction, transfer, or replay.

## The law

1. Graph construction is pure with respect to its graph inputs.
2. Graph construction MUST NOT perform ambient or user-programmed I/O.
3. Engine-managed reads required to construct the graph MAY occur, but every
   result that can affect construction MUST be recorded as a graph input.
4. All other I/O occurs in task invocations.
5. Every Declared-IO task invocation declares its inputs and outputs.
6. The execution graph records the task assembly, manifest, parameters,
   declared inputs, and declared outputs needed to determine whether two task
   invocations are equivalent.
7. Fetch is a build in its own right. It has graph construction and execution,
   and this law applies at both levels.
8. Fetch completes and writes its outputs before a fresh build graph
   construction begins.

The engine enforces the structure and declarations visible to it. It trusts
task annotations and does not attempt to prove that task code avoided
undeclared state.

## Build phases

A hardened build has three phases:

1. **Evaluation** reads projects and imports and produces the initial
   properties, items, and target definitions.
2. **Target graph construction** partially evaluates requested target bodies.
   It executes Pure task invocations and records non-Pure task invocations,
   their dependencies, and their declared I/O.
3. **Execution** runs the recorded non-Pure task invocations.

Evaluation and target graph construction together are **execution graph
construction**. Execution graph construction completes before execution
begins. A non-Pure task invocation is never run merely to continue constructing
the graph.

Fetch uses the same phases independently:

```text
Fetch evaluation
-> Fetch target graph construction
-> Fetch execution
-> Write fetch outputs
-> Build evaluation
-> Build target graph construction
-> Build execution
```

## G - Graph construction

Graph construction extends from ordinary project evaluation through target
bodies. It partially evaluates built-in MSBuild operations and Pure tasks, and
emits task invocations for non-Pure tasks.

### G1. Imports

After property substitution, every `Import` path MUST resolve to an exact
path. Wildcard imports are not permitted.

An imported file's content hash is recorded as a graph input. Any engine
resolution result that selected the path is also recorded.

Hardened mode MAY define fixed engine-owned import slots for fetch output and
resolved SDK imports. Ambient import search paths and machine-wide extension
points are not consulted unless explicitly represented as graph inputs.

### G2. Conditions

`Condition` is permitted wherever the MSBuild schema permits it.

A condition evaluated during graph construction MUST be a pure expression over
static properties and static item values. Its evaluation point and batching
context MUST match ordinary MSBuild semantics.

### G3. Property and item functions

The graph-construction function allowlist excludes functions that observe the
filesystem, network, registry, clock, randomness, process state, or other
ambient state.

Excluded functions include:

- `Exists`;
- `System.IO` APIs that inspect or mutate the filesystem;
- `$(registry:...)`;
- `GetPathOfFileAbove`;
- `GetDirectoryNameOfFileAbove`;
- `Guid.NewGuid`;
- current date or time APIs;
- random-number generation.

Pure string and path-string manipulation MAY be allowed. For example,
`System.IO.Path.Combine` is pure, while `GetTempPath`, `GetRandomFileName`,
and relative `GetFullPath` observe ambient state or randomness and are not.
Allowlisting a function asserts that its result is determined entirely by its
explicit arguments.

### G4. Environment variables and global properties

Environment variables are readable during graph construction only from a
declared allowlist. Each allowed variable's value, including its absence, is recorded as a graph
input.

An undeclared environment-derived property read in a graph-construction
position is an error.

Global properties, tools-version selection, and other engine inputs that can
alter evaluation or target construction enter project identity.

### G5. Globs

Globs execute only through the built-in MSBuild glob implementation.

A hardened glob:

- is scoped to the declared workspace;
- MAY traverse out of the owning project's directory;
- MUST NOT escape the workspace;
- does not stop at nested project boundaries;
- has deterministic sorted output;
- registers its matched files and relevant directory facts as graph inputs.

The build invocation MUST establish the workspace root without discovering it
through an ambient upward search. A project may therefore glob files owned by
another project or files unrelated to any project, provided they are within
the same workspace.

The declared path and resolved path of a symlink or junction are subject to the
workspace-boundary policy. This check applies to declared graph inputs; it is not a
general filesystem-access monitor.

### G6. Target graph edges

Requested targets, initial targets, default targets, `DependsOnTargets`,
`BeforeTargets`, `AfterTargets`, static `CallTarget` targets, and cross-project
target names MUST be static.

These values need not be XML literals. They MUST be fully known before the
affected edge is constructed.

`CallTarget` with a deferred or otherwise unresolved target name is
illegal.

### G7. Target ordering

Hardened graph construction preserves ordinary MSBuild semantics for:

- case-insensitive target identity;
- last-definition-wins target overriding;
- initial, default, and explicitly requested targets;
- `DependsOnTargets`;
- `BeforeTargets` and `AfterTargets`;
- target and task batching;
- once-per-project-configuration target execution;
- target cycles and failure behavior;
- the state-scope behavior of supported intrinsic tasks.

The implementation SHOULD share the existing target-ordering machinery rather
than independently reproduce it.

### G8. Target `Inputs`, `Outputs`, and `Returns`

Target `Inputs` and `Outputs` do not perform timestamp-based skipping in
hardened mode. Task-invocation content hashing decides whether execution is
needed.

During compatibility bring-up:

- `Inputs` and `Outputs` retain their target-batching role;
- `Outputs` retains its legacy target-return role when `Returns` is absent;
- `Returns` retains its ordinary target-return role.

Hardened mode does not reproduce partial target execution based on timestamp
partitions of changed and unchanged input items.

### G9. Engine input reads

Project files, imports, SDK resolution, glob enumeration, and similar
engine-managed operations may require I/O to establish the graph input
set. These are engine input reads, not task invocations.

Every positive or negative result that can alter graph construction must be
recorded. For example, adding a previously absent import or glob match must
invalidate the relevant partial-evaluation result.

### G10. Project identity

A project node is identified by:

- its canonical project identity;
- immutable global properties;
- tools-version selection;
- resolved SDK identity;
- the hardened-mode semantics version.

Outer and inner builds are distinct project nodes.

## T - Task invocations

Task annotations are trusted semantic assertions. The engine binds an
annotation to a task assembly and validates declarations visible in the
project, but does not inspect or monitor the task implementation to prove the
assertion.

Each task element produces one task invocation per batch. The task annotation
classifies those invocations as Pure, Declared-IO, or Unaudited.

Input availability further classifies each invocation:

| Task class | All parameters static | Any parameter deferred |
| --- | --- | --- |
| Pure | Execute during graph construction | Stall |
| Declared-IO | Ready Declared-IO task invocation | Deferred Declared-IO task invocation |
| Unaudited | Ready Unaudited task invocation | Deferred Unaudited task invocation |

Task batching therefore requires static item membership and static batch keys.
Graph construction emits one task invocation for each statically determined
batch.

### T1. Pure

A Pure annotation asserts that the task's observable result is a deterministic
function of its parameter values.

A correctly annotated Pure task does not observe filesystem, network, clock,
environment, process execution, or other state not supplied through its
parameters.

A Pure task may execute during graph construction or ordinary execution. Its
property and item outputs are static.

The engine trusts this annotation. Undeclared observations by an incorrectly
annotated task are outside the static guarantee.

### T2. Declared-IO

A Declared-IO annotation describes every external path read or written by the
task through expressions derived from task parameter values by literal
composition.

For example:

```xml
Reads="@(Sources)"
```

may qualify, while:

```xml
Reads="$(ObjDir)/**"
```

does not qualify as a finite parameter-derived read set.

The shape of each declared-I/O expression is known during graph construction.
Its concrete value MAY depend on earlier task outputs. In that case, the task
invocation remains deferred until those outputs are available.

A Declared-IO task executes only as a task invocation in the execution graph.
Its property and item outputs are deferred.

The engine validates the declared paths after the task invocation becomes
ready. It trusts that the task does not perform undeclared I/O.

### T3. Unaudited

A task without a matching annotation is Unaudited.

An Unaudited task:

- is legal in execution;
- is emitted as a task invocation;
- produces deferred property and item outputs;
- is not independently cacheable;
- makes the containing project's result not cacheable.

The presence of an Unaudited task does not prevent independent Declared-IO
task invocations elsewhere in the graph from being cacheable.

### T4. Annotation matching

Annotations live in a sidecar manifest and bind to the task assembly's content
hash.

The manifest hash is recorded on every task invocation governed by that
manifest. Any change to the task assembly invalidates the audit because the
recorded assembly hash no longer matches.

Task dependencies do not affect classification. Their hashes MAY be included
in the task invocation description, but the engine does not require dependency
analysis or separate dependency auditing.

Task factories, host processes, CLR selection, architecture, and other
engine-controlled loading details do not become task inputs merely because
MSBuild uses them to execute the task.

If an inline task or custom factory produces an assembly to which a manifest
can be bound, that assembly is classified normally. Otherwise the task is
Unaudited.

### T5. Cacheability information

Each Declared-IO task invocation records:

- the hardened graph version;
- the task assembly content hash;
- the matching manifest hash;
- resolved property parameters;
- ordered item parameters, including relevant metadata;
- declared file and directory inputs;
- declared environment inputs;
- declared output paths and output kinds;
- dependencies on earlier task invocation results;
- the workspace inputs covered by the conservative superset.

Engine implementation details that are not declared task inputs do not enter
this description.

If absolute paths occur in parameters, their values remain part of the task
invocation description. Cross-root equivalence requires a separate
path-normalization design only if the engine intentionally removes or rewrites
those values.

### T6. Task invocation result

A task invocation result contains:

- task success or failure status;
- canonical task output parameter values;
- declared output files and directories;
- declared deletions;
- structured task diagnostics.

How a later caching system stores or replays this result is outside this
specification.

### T7. Output updates

The execution graph must define deterministic semantics when task invocations
write the same path or overlapping trees.

The initial implementation MAY reject overlapping declared outputs. This is an
implementation restriction, not a fundamental requirement: ordered task
invocations may support deterministic successive updates in a later version.

## C - The cut rule and deferred values

### C1. Cuts and deferred task invocations

A non-Pure task is a **cut**.

At a cut, graph construction emits one task invocation per batch and continues
with deferred property and item outputs. Those values may flow directly to
later task invocations.

A task invocation whose parameters depend on earlier task invocations remains
deferred. It becomes ready after those invocations complete. The invocation
can then execute.

This direct task-invocation flow is not a stall.

### C2. Static and deferred values

Every property value and item list is either **static** or **deferred**.

A static value is available during graph construction. A deferred value is
available only after an earlier task invocation executes.

Pure tasks produce static properties and items. Declared-IO and Unaudited
tasks produce deferred properties and items.

Item metadata is independently static or deferred. A static item list may
therefore have known membership and identities while one of its metadata
values remains deferred.

Property assignments, item operations, transforms, and metadata assignments
preserve this distinction:

- an expression over only static values is static;
- an expression that depends on a deferred value is deferred;
- combining a deferred item list with another item list produces a deferred
  item list;
- filtering, removing, or computing item identity from deferred data produces
  a deferred item list;
- assigning deferred metadata to a static item list leaves the list itself
  static;
- a deferred property or item list cannot become static.

Storing or forwarding a deferred expression is legal. It does not require
executing its producer during graph construction.

Declared files and directories are task invocation outputs, not MSBuild
values. Their path expressions may be static or deferred. Their contents and
hashes become available with the task invocation result.

Task success, failure, or cancellation is a separate task invocation result,
not a property or item value.

### C3. Static contexts

Some MSBuild contexts must be resolved during graph construction and therefore
require static values:

- imports and SDK selection;
- target `Condition`;
- target names and target edges;
- `DependsOnTargets`, `BeforeTargets`, and `AfterTargets`;
- property names, item names, and metadata names;
- `<Output>` destination names and mapping conditions;
- the workspace boundary and glob definitions;
- the `MSBuild` intrinsic's `Projects`, `Targets`, `Properties`,
  `AdditionalProperties`, property removals, and tools-version selection.

A Pure task's parameters must be static.

Target or task batching requires:

- a static item list;
- static item identities;
- static metadata for every value used as a batch key.

Metadata not used to form the batch may remain deferred if the receiving
task invocation can receive it later.

Declared-IO and Unaudited task parameters may be static or deferred. A task
invocation with a deferred parameter remains deferred until that value is
available.

Target return values may be static or deferred. A deferred target return is a
value edge to its consumer and cannot be used in a static context.

Declared output files and directories may be consumed only through another
task invocation's declared inputs or written as requested build outputs.

### C4. Stalls

A **stall** occurs when a static context receives a deferred property or item
list.

Graph construction does not execute a non-Pure task invocation merely to make
the value available.

A stall indicates one of the following repairs:

- true discovery belongs in fetch;
- graph mutation should instead be represented as a direct task-invocation
  value or declared-file edge;
- the operation is a built-in static project-graph rule;
- the target must be restructured so its topology is determined from graph
  inputs.

Not every stall is package discovery. In particular, project-reference and
target-framework negotiation may require a built-in project-graph rule rather
than fetch.

### C5. Task status and failure control

Task success or failure is a task invocation result.

Graph construction constructs all permitted success, failure,
`ContinueOnError`, and `OnError` paths before execution. Task status may select
among those paths, but it may not synthesize new target names, project nodes,
batches, or graph mutations.

`MSBuildLastTaskResult` comes from a task invocation. It may control a
preconstructed execution path. It cannot be converted to a static property or
consumed in another static context.

### C6. Value origin

Every deferred property or item list retains a dependency chain identifying:

- the task invocation and output where the deferred value originated;
- intermediate property, item, metadata, or transform operations;
- the static context that rejected it.

A stall reports this dependency chain and the static context that rejected the
value.

## M - Cross-project builds

### M1. The `MSBuild` intrinsic

The `MSBuild` task is modeled as a built-in MSBuild operation rather than as
T1, T2, or T3.

Its project paths, requested target names, global-property additions and
removals, tools version, and project configuration identity resolve from
static properties and items.

Its target outputs are cross-project property or item-list edges. Deferred
outputs may be consumed by downstream task invocations but not by static
contexts.

### M2. Project-reference protocol

Hardened graph construction must represent ordinary project-reference
protocol behavior, including:

- distinct outer and inner builds;
- `InnerBuildProperty` and `InnerBuildPropertyValues`;
- target-framework and platform negotiation;
- global-property propagation and removal;
- requested target propagation;
- target return values.

The implementation should reuse or extend the existing `ProjectGraph`
interpretation rather than create an unrelated project identity model.

Static query targets may participate in graph construction. A query whose
result depends on a task invocation is a stall unless it can be represented as
a cross-project task-result edge without changing project topology.

## P0 - Fetch

Fetch instantiates G, T, C, and M. It writes its output before a fresh build
graph construction. The build consumes those files as ordinary source
indistinguishable from hand-written declarative files.

Fetch writes fixed, exactly named `.props` and `.targets` files. Hardened mode
does not depend on wildcard imports or `Exists`-conditioned imports to
discover fetch output.

### P0.1. Restore

Hardened restore inputs include:

- project and restore-graph source files;
- the lockfile or equivalent complete resolution record;
- an explicit project-local or explicitly supplied `NuGet.config`;
- package source policy and package content integrity information.

Ancestor and machine NuGet configuration are not consulted unless explicitly
copied into that configuration.

Resolution is locked or fails. Missing locked content may be acquired by fetch
task invocations, but unlocked dependency resolution is not a
graph-construction network query.

### P0.2. SDK and pack index generation

Index generation consumes SDK and pack identities pinned by content hash.

It flattens the framework lists, package overrides, platform manifests, RID
graph, and other supported SDK resolution data into items keyed by the
relevant configuration tuple, including:

```text
(framework, version, rid, self-contained)
```

It resolves supported SDK `Exists`-conditioned import choices into an exact,
condition-free chain of literal imports while preserving import order and
remaining non-filesystem conditions.

Custom SDK resolvers require a separately pinned resolution result and may be
out of scope for the first implementation.

### P0.3. Fetch output

Fetch outputs items describing every supported asset, including:

- exact path or content hash;
- package and source origin;
- content hash;
- assembly version;
- file version;
- target framework;
- RID;
- asset role.

Downstream hardened targets do not parse `project.assets.json`.

The generated `.props` and `.targets` files contain these declarative items.
They are not an additional discovery language.

NuGet packages containing executable `build`, `buildMultiTargeting`, or
`buildTransitive` logic are outside the initial items-only model. Supporting
them requires either admitting exact package source files as ordinary graph
inputs or defining a later normalization model.

## R - Resolution placement

### R1. Framework references

`ProcessFrameworkReferences` becomes a Pure task when all pack, workload, RID,
and configuration information it observes is present in the P0 index.

It performs lookup and RID-graph traversal over the configuration tuple. It
does not probe the filesystem or acquire packages.

### R2. Compile-time package pruning

Compile-time pruning through `PackageOverrides` occurs during graph
construction as a pure version comparison over fetched items.

Deferring compile-time pruning over-approximates the compile reference set and
can produce duplicate types.

### R3. Runtime conflict resolution

Runtime conflict resolution remains within one task invocation. It compares
assembly version and then file version against the platform manifest.

Its output layout and `ReferenceCopyLocalPaths`-equivalent values are explicit
task outputs consumed by later copy, publish, and project-reference work. They
do not alter already-constructed graph topology.

### R4. Assembly-reference resolution

`ResolveAssemblyReference` as currently implemented discovers inputs while
executing and therefore cannot be represented as a simple Declared-IO copy
task invocation.

Hardened mode must use one of these models:

- fetch or indexing produces an exact pre-resolved assembly and dependency
  closure, and residual copying is Declared-IO;
- a future content-derived-input task class permits dependency discovery from
  declared root file contents.

Ambient GAC, AssemblyFolders, and other machine search modes are not part of
the pre-resolved model unless explicitly represented in fetch inputs.

## E - Engine checks

The engine performs statically decidable checks in this order:

1. Establish the hardened-mode semantics version and supported request type.
2. Establish the project, global properties, declared environment, and
   workspace root.
3. Record and validate imports, SDK resolution, globs, and other graph-source
   queries.
4. Validate the graph-construction function allowlist.
5. Construct project and target edges using ordinary MSBuild ordering
   semantics.
6. Resolve each invoked task assembly and its bound sidecar annotation, or
   classify the task as T3.
7. Validate that a T1 annotation declares no I/O.
8. Validate that each T2 declared-I/O expression is parameter-derived.
9. Partially evaluate target bodies, execute Pure tasks, emit cuts, and
   mark properties, item lists, and metadata as static or deferred.
10. Reject deferred values in static contexts and report their dependency
    chain.
11. Make task invocations ready when earlier values become available.
12. Validate each ready T2 declared read set against the project's hashed
    superset.
13. Validate the supported declared-output update rules.
14. Emit the complete execution graph.
15. Mark task invocations and projects as cacheable or not cacheable.

These checks validate declarations and MSBuild data flow. They do not monitor
task filesystem, network, environment, or process activity.

## Execution graph output

The completed execution graph contains:

- target ordering and failure paths;
- one node for each non-Pure task invocation;
- static and deferred task parameters;
- dependencies between task invocations;
- task classification and annotation hashes;
- declared input and output expressions;
- property, item, file, and task-status result edges;
- project and cross-project build edges.

MSBuild may execute this graph using its existing task execution machinery.
Adding result lookup or storage is separate future work.

## V - Validation

Validation checks declared guarantees and tests the trusted annotation model
through perturbation.

### V1. Invisibility

Changing undeclared state must not change a documented stable comparison
record for graph construction.

Perturbations include:

- working directory;
- unrelated environment variables;
- machine NuGet configuration;
- mtimes;
- clock;
- unrelated files;
- user account;
- repository absolute path where bound parameters and path policy make the
  task invocation relocatable.

Raw binlogs are not byte-identical because they contain timestamps, paths,
event identities, and optional embedded sources. Validation compares a stable
record of properties, items, graph edges, diagnostics, and output files.

### V2. Sensitivity

Changing each declared graph input must change its recorded value or content
hash. Changing a declared task input must change the corresponding task
invocation description.

Tests include negative dependencies, such as adding a previously absent import
or glob match.

Sensitivity demonstrates input inclusion. It does not prove that every input
declared by the user is semantically necessary.

### V3. Differential behavior

For the supported subset, the same tree is built through ordinary and hardened
MSBuild. Validation compares:

- project configurations and global properties;
- target ordering;
- target return values;
- task output parameters;
- per-project `ReferencePath` closures;
- copy and publish closures;
- assemblies produced by deterministic compilation with path mapping.

### V4. Failure behavior

Differential tests cover:

- task false returns;
- task exceptions;
- `ContinueOnError`;
- `MSBuildLastTaskResult`;
- `OnError`;
- warning policy;
- cancellation;
- partial output writes;
- task failure diagnostics.

### V5. Index goldens

SDK and pack indexes are regenerated from pinned inputs, diffed, and reviewed
as deterministic goldens.

### V6. Path and concurrency matrix

Validation covers:

- Windows, Linux, and macOS path semantics;
- case-sensitive and case-insensitive filesystems;
- symlinks and junctions;
- cross-project workspace globs and workspace escapes;
- multiprocessor builds;
- in-proc and out-of-proc MSBuild nodes;
- target and task batching;
- single-target and multi-target project-reference graphs.

## Known limits

### Under-declaration is unprovable

Without filesystem, network, environment, and process tracking, a T1 or T2
annotation can be wrong.

Perturbation testing can find some incorrect annotations but cannot establish
their correctness.

A conservative workspace input set bounds part of this risk. It covers the
workspace plus any resolved reference files outside it. A future caching layer
can include that set when comparing task invocations. An escaping read,
undeclared write, network observation, or other ambient dependency can still
make a task incorrectly appear cacheable.

This is a deliberate trust boundary, not a gap to conceal through partial
enforcement.

### Content-derived reads have no task class

`Csc`, assembly dependency traversal, depfile-producing tools, and similar
operations discover reads from file contents.

They remain T3 until a content-derived-input class is defined. That future
class must distinguish graph topology discovery from discovery local to one
task invocation.

### Graph construction has a sequential spine

Target bodies mutate properties and items in order. Construction therefore
follows MSBuild's static target ordering within each project configuration.

The implementation uses shared versioned property and item state at cuts and
task invocations. It must not deep-clone all project properties and items at
each task.

This sequential spine affects construction latency, not task parallelism.

### Package build logic is not represented

The items-only fetch model does not initially support arbitrary executable
package `.props` and `.targets`.

### Design-time builds require a separate contract

Visual Studio design-time builds use different target sets, global properties,
and project-reference behavior. They may remain on the existing path until a
specific hardened design-time contract is defined.

## Open design questions

1. Whether the first version rejects all overlapping outputs or supports a
   limited ordered-update model.
2. How package-provided build logic enters hardened graph construction.
3. Whether assembly dependency resolution is fully moved to fetch or becomes
   the first content-derived-input task class.
4. Whether static query targets or existing `ProjectGraph` interpretation
   should drive initial outer/inner-build negotiation.
5. How path normalization should represent equivalent task invocations across
   workspace roots.
6. Which design-time build scenarios should eventually participate.
