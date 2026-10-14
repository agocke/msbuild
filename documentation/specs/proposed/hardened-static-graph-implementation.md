# Standalone action-graph prototype roadmap

Implement the [action-graph contract](hardened-static-graph.md) in small,
verifiable slices. MSBuild emits concrete work; Ninja schedules independent
hosts. This roadmap describes planned work, not implemented behavior.

## Current foundation

- `HardenedTargetValidator` walks target bodies, tracks batching and
  static/deferred values, and can execute Pure tasks through a callback.
- `HardenedTaskClassificationResolver` resolves implementation annotations.
  `HardenedDeclaredIOFootprint` records paths, not executable actions.
- `RequestBuilder` currently validates and then enters ordinary `BuildTargets`.
- `TaskExecutionHost` depends on project, batching, and engine logging state.
- Existing composite SDK overlays provide a starting point for compilation.

Reuse these pieces without introducing another target interpreter. Keep
ordinary builds and existing validation behavior unchanged outside the new
opt-in graph-generation path.

## 1. Prove an engine-free task host

Start with a hand-authored invocation manifest and `WriteLinesToFile`.
Implement a reusable console host that loads an exact managed task, binds
supported parameters, reconstructs item metadata, and sets explicit working
directory/environment. Supply a small `IBuildEngine` compatibility layer for
supported diagnostics and services; reject nested builds and unsupported
hosting. Reuse small binding helpers, not the whole `TaskExecutionHost`.

**Done when:** a fresh process produces the expected file without loading
`Microsoft.Build`. False results, exceptions, logged errors, and missing
required outputs fail with useful diagnostics. Parameter checks cover item
order, metadata, escaping, and unset versus empty values.

## 2. Unify construction-time tasks under Declared-IO

Migrate participating tasks and target invocations from the separate Pure
contract to explicit `DeclaredInputs` and `DeclaredOutputs`. Empty lists permit
construction-time execution with known parameters and ordinary `<Output>`
mappings. Nonempty footprints remain unexecuted actions and cannot publish
MSBuild return values. Existing public attributes need not be removed.

**Done when:** a zero-I/O task's returned items and metadata can construct a
later invocation, while missing declarations, unavailable parameters, and
file-I/O output mappings fail before action side effects.

## 3. Emit a concrete Ninja graph

Extend the existing partial-evaluation walk to collect actions per concrete
task bucket. Capture bound parameters, configured-project identity, declared
paths, implementation/host context, and diagnostic provenance. Associate
generated inputs with producers and preserve required file/control ordering.
Expand supported `MSBuild` and `CallTarget` intrinsics during construction.

Write versioned invocation manifests and one Ninja edge per action. Include
manifests and the host/tool dependency closure as normal inputs; use order-only
edges only for control precedence. Handle Ninja escaping separately from shell
quoting, write manifests only on content changes, and emit explicit roots.
Record graph inputs, but require explicit regeneration after they change.

Add a graph-only path in `RequestBuilder` that publishes the complete bundle
and returns without ordinary `BuildTargets`. Enforce the spec's required-file,
static-value, and fail-fast subset; do not emulate unsupported effects with
stamps or fall back to ordinary execution.

**Done when:** clean-output generation emits all actions without running any
file-I/O task. Invalid construction publishes no executable partial graph.
Focused cases cover buckets, generated inputs, conflicting writes, cycles,
paths with spaces/dollars/colons, and supported cross-project routing.

## 4. Execute a small build with Ninja

Use a repository `.proj` fixture containing a zero-I/O value transformation,
a file producer, and a consuming file-transform action. Begin with conservative
ordering and `ninja -j1`.

**Done when:**

- After MSBuild exits, Ninja alone produces the expected files, with one host
  process per action and no engine loading, reevaluation, or nested builds.
- Parameters, metadata, and outputs match ordinary execution of the fixture.
- An unchanged second run is a no-op. Input, manifest, and host/tool changes
  invalidate the correct actions under Ninja's timestamp rules.
- Task failure blocks dependents; unsupported constructs fail during generation.

Then test parallel execution where modeled dependencies permit it.

## 5. Compile with the pinned SDK

Use existing SDK overlays for a single-framework C# hello-world profile:

```text
construction -> generated source -> compiler -> final output copy
```

Complete restore/fetch before fresh graph generation. Supply exact compiler,
reference, analyzer, generator, and other tool inputs. Start with a supported
compile closure, then extend toward `Build` without weakening the contract.
Add a two-project fixture with static child returns and configured-project
identity; child artifacts flow through file edges, not nested engine calls.

**Done when:** Ninja builds from clean outputs after MSBuild exits, compilation
and project-reference outputs match ordinary MSBuild, and failures identify
the originating project/task. Broaden platform and parallelism coverage as
those scenarios are admitted.

## After the prototype

[Named products](hardened-named-products.md) can add catalog validation,
producer-closure selection, and Ninja roots. Content caching and lazy artifact
materialization need separate standalone integration; Ninja timestamps are
not that cache. Generalized fetch, richer file effects and failure control,
deferred values, relocation, and persistent analysis remain separate work.
