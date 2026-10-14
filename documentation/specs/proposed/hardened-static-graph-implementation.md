# Standalone action-graph prototype roadmap

Implement the [action-graph contract](hardened-static-graph.md) in small,
verifiable slices. MSBuild emits concrete work; Ninja schedules independent
hosts. This roadmap describes planned work, not implemented behavior.

## Current foundation

- `HardenedTargetValidator` walks target bodies, tracks batching and
  static/deferred values, and constructs DeclaredIO invocations through a callback.
- `HardenedTaskClassificationResolver` resolves implementation annotations.
  `HardenedDeclaredIOFootprint` records paths, not executable actions.
- In hardened mode, `RequestBuilder` requires graph/host configuration,
  publishes the constructed bundle, and returns without ordinary `BuildTargets`.
  Restore is a separate ordinary invocation, not a hardened-mode exception.
- `TaskExecutionHost` depends on project, batching, and engine logging state.
- Existing composite SDK overlays provide a starting point for compilation.
- `MSBuild.ActionTaskHost` executes a hand-authored JSON invocation in a fresh
  process without the engine. It binds scalars, arrays, and items with custom
  metadata; applies explicit context; and checks required declared files.
- The construction callback now uses normal parameter binding, records nonempty
  DeclaredIO invocations, and executes zero-I/O invocations with output mappings.
  Process tests prove a generated Ninja producer/consumer chain, batching,
  unchanged regeneration, invalidation, and failure propagation.

Reuse these pieces without introducing another target interpreter. Keep
ordinary builds unchanged outside the opt-in hardened graph-generation path.

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

This first slice is implemented with process-level tests.

## 2. Unify construction-time tasks under Declared-IO

Migrate participating tasks and target invocations from the separate Pure
contract to explicit `DeclaredInputs` and `DeclaredOutputs`. Empty lists permit
construction-time execution with known parameters and ordinary `<Output>`
mappings. Nonempty footprints remain unexecuted actions and cannot publish
MSBuild return values. Retire the branch-only Pure attribute, interface, and
environment; reuse `TaskEnvironment` rather than a second context abstraction.

**Done when:** a zero-I/O task's returned items and metadata can construct a
later invocation, while missing declarations, unavailable parameters, and
file-I/O output mappings fail before action side effects.

The existing construction-time task catalog now uses DeclaredIO and ordinary
task context. Hardened mode has no validator-only execution path. The broader
SDK overlays remain experimental and require graph-compatible adaptation.

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

The first single-project Unix slice is implemented. Cross-project `MSBuild`
routing, forward producer resolution, Windows commands, full tool closures,
and graph-input inventories remain work rather than supported fallbacks.

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

The process fixture covers these behaviors with conservative action ordering.

## 5. Compile with the pinned SDK

The explicit compiler fixture is implemented in
`src/Samples/ActionGraphHelloWorld/HelloWorld.proj`. It uses the test assembly's
audited `GraphCSharpCompileTask` to invoke the real `csc.dll` with explicit
sources, references, compiler/runtime files, and one assembly output. A second
standalone action writes the runtime configuration. MSBuild emits both actions
without compiling; Ninja produces a runnable `HelloWorld.dll`.

Process tests cover `Hello, World!`, a no-op second Ninja run, recompilation
after a source edit without regenerating the graph, and a genuine compiler
error blocking the runtime-config action. The compiler adapter rejects missing
declarations for its explicit inputs/output and refuses execution with the
MSBuild engine loaded. It is a fixture, not a general Roslyn task replacement.

After a Release repository build, supply the installed .NET 11 runtime and
reference-pack versions explicitly:

```sh
source artifacts/sdk-build-env.sh
sdk_version="$(dotnet --version)"
runtime_version="<installed .NET 11 runtime version>"
reference_version="<installed .NET 11 reference-pack version>"

dotnet msbuild src/Samples/ActionGraphHelloWorld/HelloWorld.proj --hardened-graph \
  -p:MSBuildEmitActionGraph="$PWD/artifacts/hello-world/build.ninja" \
  -p:MSBuildActionTaskHost="$PWD/artifacts/bin/MSBuild.ActionTaskHost/Release/net11.0/MSBuild.ActionTaskHost.dll" \
  -p:MSBuildActionDotNetHost="$DOTNET_ROOT/dotnet" \
  -p:CompilerDirectory="$DOTNET_ROOT/sdk/$sdk_version/Roslyn/bincore" \
  -p:ReferenceAssemblyDirectory="$DOTNET_ROOT/packs/Microsoft.NETCore.App.Ref/$reference_version/ref/net11.0" \
  -p:RuntimeDirectory="$DOTNET_ROOT/shared/Microsoft.NETCore.App/$runtime_version" \
  -p:RuntimeVersion="$runtime_version"
ninja -C artifacts/hello-world -f build.ninja -j1
dotnet artifacts/hello-world/HelloWorld.dll
```

This bypasses SDK `Build` and reference-resolution targets. The neighboring
`HelloWorld.csproj` records the SDK integration trial: ordinary restore works,
and workload `IsOSPlatform` checks now use the captured global
`MSBuildGraphHostOS` rather than ambient OS queries. Process tests cover the
default, explicit overrides, immutable configuration, and ordinary-build
compatibility. The selected OS is recorded in the Ninja graph; see
[captured construction platform](hardened-static-graph.md#captured-construction-platform).

The next observed evaluation blocker is Roslyn's language-version expression
in `Microsoft.CSharp.Core.targets`: the allowlist rejects the array index in
`$(_TargetFrameworkVersionWithoutV.Split('.')[0])`. This is a pure-expression
coverage gap, not an OS-discovery requirement. The stock Roslyn task also lacks
DeclaredIO annotations. SDK `Build` is still unsupported; ambient discovery
has not been allowed as a fallback.

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

## 6. Exercise dotnet/runtime

After SDK compilation works, select one bounded managed library under
`~/code/runtime/src/libraries` and construct its compile closure. Complete
restore and prerequisite tool/reference builds beforehand. Use real source
generation and compiler actions, then expand to managed project-reference
closures. Treat native runtime/CMake integration as a separate later boundary,
not a prerequisite for the first managed-library result.

## After the prototype

[Named products](hardened-named-products.md) can add catalog validation,
producer-closure selection, and Ninja roots. Content caching and lazy artifact
materialization need separate standalone integration; Ninja timestamps are
not that cache. Generalized fetch, richer file effects and failure control,
deferred values, relocation, and persistent analysis remain separate work.
