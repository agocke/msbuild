# Classic MSBuild restore performance plan

Make a clean, package-cached static-graph restore of `dotnet/runtime` take
**5-6 seconds** under existing MSBuild semantics. Project files, public object
models, target and task protocols, filesystem observations, diagnostics, and
restore outputs stay as they are. The approach is to specialize repeated
interpretation, not to add a new evaluation mode, declared-I/O contract, or
result cache.

Evidence for every number here is in
[evaluation performance investigations](evaluation-perf.md). Experiments that
did not work are in
[evaluation performance dead ends](evaluation-performance-dead-ends.md).

## Where we are

Measured on an 8-core/16-thread Ryzen 7 1800X with this branch's engine.
Each row is a mean of three interleaved runs that produced the same 639
`project.assets.json` files
([details](evaluation-perf.md#runtime-configuration-and-hosting)):

| Configuration | Wall | Instructions |
| --- | ---: | ---: |
| As `dotnet/runtime` runs it (console binlog on, Dynamic PGO on) | 33.7 s | 626 G |
| Console binlog off | 27.8 s | 825 G* |
| Dynamic PGO off in the NuGet console | 23.1 s | ~300 G |
| Both off | 16.6 s | 290 G |
| Both off, Native AOT NuGet console | **13.8 s** | 274 G |

\*Instruction counts with PGO on are inflated by contention on shared profiling
counters and are not comparable.

Newer `dotnet/runtime` main no longer enables the console binlog. Since this
table, skipping the Unix path probe has taken the JIT `Release` engine from
17.37 to **16.63 s** (four balanced pairs). An independent ladder on a 16-core
Xeon without hardware counters confirms the directions with different
magnitudes: PGO off saves 7.0 s and Native AOT 1.7 s there
([details](evaluation-perf.md#16-core-configuration-ladder)).

## The core problem: instructions

Turning off PGO and the binlog removed pathologies, not work. Native AOT
mainly saves cycles: 20% fewer, but only 4% fewer instructions. With
ReadyToRun, JIT is a small cost. What remains is the number of instructions
MSBuild retires: **~285 G for the restore, ~170 M per configuration**, at an
IPC of only ~0.7.

At 5.5 s, roughly 2.5-3 s of the critical path is serial and 2.5-3 s is
parallel evaluation running ~10 threads wide. At ~2.5 G instructions per
core-second, that allows **~70-80 G for the whole restore, ~45 M per
configuration: 3.5-4x fewer instructions than today**. The low IPC belongs to
the same problem. Pointer-heavy, allocation-heavy state costs instructions and
cache misses, and compact state should improve both. Restore time is the
acceptance metric; instructions per configuration is the progress metric.

The restore's timeline (JIT `Release` engine, single runs,
[details](evaluation-perf.md#restore-timeline)):

| Phase | Default | Compiled modules + batching |
| --- | ---: | ---: |
| Graph evaluation in the NuGet console, elapsed | 6.35 s (61 CPU s) | 4.94 s (44 CPU s) |
| Execution in the console (serial) | 5.4 s | 5.8 s |
| Outer MSBuild, console startup, NuGet after execution | ~4.5 s | ~4.5 s |

Evaluation runs ~10 threads wide, so an evaluation saving of X CPU seconds is
worth about X/10 seconds of wall time. That is why specializations have
removed large local costs without moving wall time. Execution and the third
row need their own instruction reductions, and the third row has not yet
been broken down.

## Where the instructions are

| Pool | Default | With batching | Status |
| --- | ---: | ---: | --- |
| Item evaluation (construction and application) | ~87 G | ~55 G | Untouched; the largest pool |
| Properties and imports | ~66 G | ~35 G | Batching exists; folding and import attachment do not |
| GC threads | ~30-40 G | ~28 G | Falls with allocations |
| Conditions | ~36 G | ~16.5 G | Prepared trees not started |
| File globbing | ~16-26 G | ~26 G | 85% regex interpreter |
| NuGet's own code | ~27 G | | LINQ-heavy; NuGet's to fix |
| Execution | ~10 G | | Compact execution foundation exists |

Default figures come from the Native AOT and `Release` ledgers. Batched
figures come from a `Release` ledger and a JIT-everything profile, so they
are approximate
([details](evaluation-perf.md#compiled-modules-with-effect-batching-in-release)).

### Evaluation

A condition costs ~10.6 k instructions in MSBuild under AOT, and a property
element ~18.2 k. A reference implementation replaying the same streams costs
782 and 929 with `Dictionary` state, and 500 and 231 with slot-indexed state
([floor](evaluation-perf.md#executable-floor-for-conditions-and-property-elements)).
The measured pieces of that gap:

- **Filesystem probes.** Every property value containing `/` called
  `Directory.Exists` through the Unix path heuristic, though only a backslash
  can change its result. That is now skipped, saving 15% of the properties
  pass
  ([details](evaluation-perf.md#measured-per-file-costs-and-the-unix-path-probe)).
- **Writing state.** ~45% of a literal element's remaining ~6.2 k goes to
  `SetProperty`: prior-value lookup, `ProjectPropertyInstance` allocation,
  and hash-set insert. Most of the rest is fixed per-element overhead
  (operation-ID lookup, XML re-reads, global-property check).
- **Late-bound property functions.** ~23 calls per configuration through
  `Type.InvokeMember`, ~137 k instructions each under AOT, mostly on inputs
  that never change.

**Much of the work repeats.** Equal live-ins (the properties a unit reads
before writing) produce equal effects in all but 2-3% of the work
([census](evaluation-perf.md#amplification-and-invariance)). Weighted by
measured per-file costs, **54% of the properties pass** sits in import
executions that repeat with identical live-ins. Files fall into two regimes:

- ~25% of the pass is in files with one or two keys across the whole restore,
  mostly `eng/*.props`.
- ~25% is in the SDK `.targets` chain, which is unique per configuration as a
  whole and reachable only by block-level reuse.

The census also finds **60% of item-operation work repeating**. Resolving and
loading imports is another ~15% of the pass.

### Execution

The 1,643 project requests run one after another on one in-proc node. On the
16-core machine a request takes ~5.6 ms against a budget of ~1.4 ms. Task
service is ~1.27 ms (92 us of non-body work per task against a ~35 us
allowance), target traversal 0.64 ms, and intrinsic groups 0.37 ms
([details](evaluation-perf.md#binding-and-execution-ledger)). The compact
execution foundation no longer converts compact properties to objects for
logging, which saved 4.4% of restore wall time in a separate 760-asset
cohort
([details](evaluation-perf.md#compact-properties-through-execution-implementation-and-paired-results)).

## Direction

**Partially evaluate the existing interpreter** (the first Futamura
projection). Project and import XML is the static input, and a
configuration's properties, items, and filesystem are the dynamic input. Each
import becomes a source-bound module whose conditions, expressions, and
item operations are prepared once. The residual program runs per
configuration. Imports stay linked dynamically, because conditions and SDK
resolution select them. The existing evaluator remains the fallback for
anything unsupported.

The measurements say three things have to go together:

1. **Compact state with batch apply.** A prepared effect is applied as one
   delta into configuration-owned compact state, not as one object write per
   element. The compiled-module path already does this for runs of literal
   properties, cutting evaluation instructions by 39%.
2. **More static folding.** Constant propagation within an import, folding
   pure functions on constant arguments, and treating file reads as declared
   inputs. In `eng/Versions.props`, removing one `File.ReadAllText` let
   batching cut the file by 71%. Its sibling `Version.Details.props` is
   constant as a whole but is still evaluated element by element
   ([details](evaluation-perf.md#evaluation-without-filesystem-reads-engversionsprops)).
3. **Attachment at import granularity.** Group-level reuse cannot amortize
   shared props and targets: its hits average 1.6 writes, and the key check
   costs as much as the work it saves. A repeated import keyed on its
   live-ins can be applied as one delta.

Execution needs its own change, a **request-owned activation frame**: prepared
target and task plans that bind to compact state, explicit logging views, and
less per-request fixed cost. Running requests in parallel would multiply that
gain but does not substitute for it.

Out of scope here: the pure-evaluation mode, Declared-I/O task-result caches,
hardened graph validation, a persistent build server, a NuGet-specific
evaluator, and caching evaluated projects or task results.

## What this branch contains

| Area | State |
| --- | --- |
| Compiled evaluation modules, typed property/condition/item programs, source-bound imports and `Exists`, residual metadata programs, constant-literal item batching | Opt-in (`MSBUILDENABLECOMPILEDMODULEEVALUATION`, `MSBUILDENABLECOMPILEDMODULEEFFECTBATCHING`); -39% evaluation instructions in `Release` |
| Compact-state request activation frame (logging snapshot and lookups without object conversion) | Opt-in through effect batching; repeatable execution-throughput saving |
| Compiled target plans and direct task actions | Opt-in (`MSBUILDENABLEACTIONGRAPHPARTIALEVALUATION`) |
| Property-group effect attachment (`MSBUILDATTACHPROPERTYGROUPS`, verify with `MSBUILDATTACHPROPERTYGROUPSVERIFY`) | Experimental opt-in; no macro win |
| Async logging notification coalescing, separator-free path fast exit, Unix path probe skipped without backslashes, empty AOT framework root in `ToolLocationHelper`, default-path read-tracker allocations | Default-on |
| Cost ledger with phase spans (`MSBUILDCOSTLEDGER`), per-file properties-pass profile (`MSBUILDIMPORTCOSTPROFILE`), evaluation trace (`MSBUILDEVALUATIONTRACE`) | Diagnostic, inactive by default |

## Next steps

1. **Make the compiled-module path the base.** Confirm its macro effect with
   balanced pairs on a ReadyToRun snapshot, then build the remaining
   evaluation work on it.
2. **Items.** Item evaluation is the largest pool left. Price construction
   and application per site with the ledger, prepare construction once per
   import (it depends almost only on XML), then attach repeated item
   operations. That needs an O(1) identity for the list an operation applies
   to.
3. **Property folding and import attachment.** Constant propagation within an
   import, with `Version.Details.props` as the test case. Then key repeated
   imports on their live-ins and apply them as single deltas, starting with
   the one- and two-key files. Validate filesystem observations against the
   shared evaluation context, not by repeating the call. Worth ~20-25 G.
4. **Contained wins.** A glob matcher that avoids the regex interpreter
   (~20 G), memoization of pure property functions, and prepared condition
   trees measured against the floor.
5. **Execution activation frame.** Prepared plans bound to compact state, to
   cut per-request work from ~3.3 ms toward 1.4 ms. Try multithreaded
   execution (`MSBUILDFORCEMULTITHREADED=1`) as a multiplier afterward.
6. **Complete the timeline.** Measure the outer MSBuild process, console
   startup, and NuGet's work after execution, which together take ~4.5 s.
7. **Hand off to NuGet and runtime owners:** `TieredPGO=false` and Native AOT
   for the static-graph console, and NuGet's own ~27 G.
8. **Confirm on the 16-core machine**, using task clock where hardware
   counters are unavailable.

## Measurement rules

- Use **instructions retired** (user and kernel) as the primary cost unit
  for per-scope and per-unit accounting, and wall time for macro results.
  Whole-restore instruction totals vary by about 3% between runs (GC and
  spin-waiting), so small macro differences need wall-time pairs. Keep
  Dynamic PGO off for accounting, and report spin-waits separately: their
  instruction counts depend on the CPU. Where hardware counters are
  unsupported, say so and use process-tree CPU plus scope task clock; do not
  infer instructions, cycles, or IPC.
- Interleave control and candidate runs in balanced pairs, with the same SDK,
  workload, package cache, and switches. Every run must produce identical
  restore assets.
- Do not add phase CPU across parallel threads and call it wall time, and do
  not mix numbers across machines or measurement epochs.
- Keep small, bounded, semantics-preserving specializations on repeatable
  phase-level savings, and measure accumulated macro throughput.
- New warnings or errors in default behavior are breaking changes. Preserve
  design-time behavior, cross-platform path handling, task assembly
  unloading, and fallback to the existing evaluator.
