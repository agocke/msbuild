# Hardened action graph

## Purpose and status

MSBuild becomes a graph compiler: it evaluates projects and target bodies,
writes `build.ninja` and concrete task-invocation manifests, and exits. Ninja
then launches independent task-host console processes. Execution requires
neither the MSBuild engine nor another target interpreter or custom scheduler.

This is proposed, opt-in behavior. The current hardened mode validates builds
and then executes them with the ordinary engine. Existing builds remain
unchanged unless graph generation is selected. The
[roadmap](hardened-static-graph-implementation.md) describes the implementation.

## Pipeline

1. Complete restore/fetch and any discovery that produces build graph inputs.
2. In a fresh evaluation, construct the complete requested target closure,
   executing only zero-I/O task invocations.
3. Validate and publish the Ninja graph and invocation manifests, then exit
   without ordinary target execution.
4. Run Ninja separately to execute the actions.

Illustrative commands; `-emitActionGraph` is a proposed switch:

```sh
msbuild Demo.proj --hardened-graph -target:Build -emitActionGraph:out/build.ninja
ninja -f out/build.ninja
```

Graph generation must work from clean task outputs. Recording an ordinary
build after executing its tasks does not satisfy this contract.

## One task contract

An audited task uses `[MSBuildDeclaredIOTask]` and explicitly supplies both
`DeclaredInputs` and `DeclaredOutputs`: finite, exact filesystem path lists,
separate from its operational parameters. An empty value means an empty list;
an omitted declaration is an error.

The contract asserts deterministic results from explicit parameters, declared
input contents, and fixed implementation/host context. It excludes undeclared
filesystem, network, clock, environment, or other ambient observations,
including observations made by loaded analyzers, generators, or tools.
Annotations are trusted assertions, not a sandbox or an implementation proof.

Treatment is per invocation:

| Declared filesystem footprint | Treatment with known parameters |
| --- | --- |
| Both lists empty | Execute during graph construction |
| Either list nonempty | Emit an independently executable action |
| Missing or invalid contract | Reject graph generation |

Pure is the zero-I/O case, not a separate classification or annotation.
Zero-I/O tasks may still accept values and return properties, items, and
metadata through ordinary `<Output>` mappings. Their results are available
to subsequent construction. Fixed host context includes the configured
project directory and path semantics, not an ambient current directory.

File-I/O actions cannot return MSBuild values in the prototype. A generator's
output path, for example, must be established through static items or
properties and passed to both generator and compiler. Construction knows the
path; execution supplies the contents. Only zero-I/O invocations may use
`<Output>` mappings.

## Graph construction

Reuse MSBuild's target ordering, expression expansion, batching, and
property/item scope semantics for the supported subset.

- Record graph inputs: projects/imports, global properties, declared
  environment values, pinned SDK resolution, and glob results. Imports resolve
  to exact paths; built-in globs are deterministic and workspace-bounded.
  Engine-managed reads are recorded; ambient or user-programmed I/O is not
  permitted during construction.
- Conditions, target/project routing, batch membership, all emitted parameter
  values, and item metadata must resolve during construction. Property
  functions are restricted to the hardened allowlist. File-I/O tasks must
  never run to make an unavailable construction value known.
- Expand supported `MSBuild` and `CallTarget` intrinsics into construction
  state and dependency edges, not host commands that invoke MSBuild.
  Configured projects retain their global-property and inner-build identity.
- Preserve required file and control dependencies, initially conservatively.
  Associate generated inputs with their producers, reject conflicting output
  ownership and cycles, and do not mistake stale generated files for sources.

Target `Inputs` and `Outputs` retain their batching roles, and `Returns`
retains its static value role, including legacy `Outputs` returns. Target
timestamp skipping must not omit actions from the graph. Unsupported
constructs fail with source locations and useful origin chains before graph
publication; there is no partial fallback to ordinary execution.

## Persistent graph

The graph bundle consists of `build.ninja` and versioned per-action invocation
manifests. No separate whole-graph JSON format is required.

Each manifest contains:

- invocation identity, including configured project, target, and batch;
- exact task type, assembly/annotation identity, and pinned code/tool dependencies;
- concrete typed parameters, preserving item order, metadata, and unset/empty values;
- working directory, explicit environment, and runtime/host context;
- canonical declared paths and original diagnostic locations.

Each action is one Ninja build edge, including all outputs of a multi-output
invocation. Declared input files, manifests, and host/tool dependencies are
normal explicit or implicit inputs. Required ordering without content
invalidation uses order-only edges, never as a substitute for consumed files.
Default roots represent the requested target closure.

Host commands receive manifest paths, not MSBuild expressions or serialized
engine objects. Ninja escaping and platform command quoting are separate
concerns. Rewrite manifests only when their contents change, use consistent
path spellings, and publish the complete bundle only after successful
construction. Generation metadata is separate from task outputs.

Ninja initially provides file/timestamp and command-change incrementality,
not content-addressed caching. Timestamp-preserving content changes are not
reliably detected. `restat` is optional and does not change this limitation.
See the [Ninja manual](https://ninja-build.org/manual.html).

Graph-input changes, including source-set changes, require explicitly rerunning
MSBuild. The prototype does not install a Ninja regeneration rule that calls
MSBuild. Ninja executes from the recorded graph working directory; hosts apply
their invocation-specific execution directories.

## Standalone task hosts

The first host, `MSBuild.ActionTaskHost`, accepts `--invocation <file.json>`.
Version 1 uses JSON values rather than command-line task expressions:

```json
{
  "version": 1,
  "taskAssembly": "/tools/Microsoft.Build.Tasks.Core.dll",
  "taskType": "Microsoft.Build.Tasks.WriteLinesToFile",
  "workingDirectory": "/workspace",
  "environment": {},
  "parameters": {
    "File": "obj/generated.txt",
    "Lines": ["first line", "second line"],
    "Overwrite": true,
    "DeclaredInputs": [],
    "DeclaredOutputs": ["obj/generated.txt"]
  }
}
```

Assembly and working-directory paths are absolute; task paths are relative
to that directory. Scalars use JSON strings, numbers, or booleans; the host
converts them to the task's declared parameter types using invariant MSBuild
conversions. Arrays are explicit JSON arrays, never semicolon-delimited text.
An item is either a literal string or
`{"itemSpec":"file.cs","metadata":{"Tag":"value"}}`. Item text and metadata
are literal, not MSBuild-escaped or expression-expanded. Custom metadata is
supported; derived item metadata is calculated by the task-item implementation.
Omitted parameters keep task defaults, empty strings/arrays remain empty,
and null values are rejected.

The environment replaces inherited task environment variables and defaults to
empty; culture is invariant. Optional `projectFile`, `lineNumber`, and
`columnNumber` supply diagnostic locations. Unknown fields, unsupported
versions/types, duplicate parameter names ignoring case, and missing required
parameters fail explicitly. This file format is experimental, not a public
serialization API.

Start with one reusable console application, launched in a fresh process for
each action. It loads a pinned managed task, binds supported parameter types,
reconstructs items and metadata, applies explicit execution context, and
executes one invocation.

The host may use `Microsoft.Build.Framework` and task utilities, but not the
`Microsoft.Build` engine. A small `IBuildEngine` compatibility implementation
provides supported diagnostics and services; nested builds and unsupported
services fail explicitly. The engine-dependent `TaskExecutionHost` is not
reused wholesale, although small binding helpers may be extracted.

False results, exceptions, logged errors, or missing required output files
produce nonzero exit codes and useful diagnostics. Checking required outputs
is not an audit for undeclared I/O. Ninja blocks dependents after failure;
already-running independent commands may finish.

## Prototype boundary and later work

Every file-I/O action must produce at least one persistent regular file, and
every declared output must exist on success. Inputs are existing external
regular files or required outputs of predecessor actions; generated inputs
need not exist during construction.

Reject Unaudited tasks, unresolved parameters, task factories, specialized
runtime/architecture hosting, `OnError`, non-default `ContinueOnError`,
execution-time status control, directory outputs, optional input absences,
optional/deleted outputs, in-place updates, and outputless file-I/O actions.
Synthetic success stamps must not hide unsupported filesystem effects.

The first milestone is a small task chain, then a pinned framework-only C#
compilation and a two-project build. Start with conservative ordering and
`ninja -j1`; allow concurrency only where dependencies establish its safety.

Content caching, generalized fetch, deferred value flow, richer failure
handling, relocation, automatic regeneration, and persistent analysis are
later work. Action identity is not a future cache key: generated input
digests become available only during execution, and consumer keys should use
consumed artifact digests rather than producer action keys.
[Named products](hardened-named-products.md) separately describes future root
selection and artifact demand.
