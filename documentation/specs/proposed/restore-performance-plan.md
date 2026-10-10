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

The NuGet console process (`NuGet.Build.Tasks.Console`) hosts MSBuild's static
graph; NuGet's own code is ~5% of its instructions. On the 16-core
Xeon used for earlier work, a separate 760-asset workload took 17.7 s
with the branch's opt-ins enabled; that is not this 639-asset epoch.

The independent **16-core Xeon/WSL2 ladder is complete**
([methods and results](evaluation-perf.md#16-core-configuration-ladder)).
Four interleaved runs per cell, ordinary engine semantics with opt-ins
unset:

| Configuration | Wall s | Process-tree CPU s |
| --- | ---: | ---: |
| Console binlog on, PGO on | 43.622 | 174.960 |
| Console binlog off | 22.688 | 283.618 |
| Console PGO off | 40.568 | 141.110 |
| Both off, JIT/R2R | 15.643 | 146.520 |
| Both off, Native AOT | 13.953 | 104.630 |

All 25 macro/warmup captures match 639 assets after normalizing only SDK
snapshot paths. Hardware instruction/cycle counters are unavailable on
this machine. With binlogs off, PGO removal saves **7.045 s** and AOT
saves another **1.690 s**, not the Ryzen's ~10 s and ~3 s. Binlog removal
saves 20.934-24.925 s depending on PGO; with PGO on it also raises CPU by
108.658 s. The configuration effects are not additive, and these timings
do not establish the contention mechanism.

Design budget for the 5-6 seconds:

| Layer | Budget |
| --- | ---: |
| Graph construction and evaluation | ~1.5 s elapsed |
| Executable-task pipeline, including bodies | <=1.0 s aggregate CPU |
| Project, target, and intrinsic work outside tasks | ~1.5-2.0 s |
| Process start, NuGet, assets, finalization | ~1.0-1.5 s |

## Where the cost comes from

### Runtime and hosting

The instruction-level explanations and ~10/~6/~3-second figures below
are from the Ryzen cohort, not universal prices. The Xeon ladder above
independently confirms the directions with different magnitudes.

- **Dynamic PGO in the NuGet console costs ~10 s.** The console starts
  fresh for every restore, and with 16 evaluation threads it spends much of
  its life in instrumented code (`JIT_CountProfile32`, `JIT_ClassProfile32`,
  and their thread-local lookups are ~29% of all instructions). Turning PGO
  off in that process alone recovers the whole cost. ReadyToRun does not help:
  hot precompiled methods are instrumented too.
- **The console binlog costs ~6 s** but only ~36 G instructions, so its writer
  sits on a serial path.
- **Native AOT saves another 3 s** with 4% fewer instructions and 20% fewer
  cycles: ILC code quality plus no JIT. It needs no NuGet or SDK change
  ([how](evaluation-perf.md#native-aot-console)).

These are NuGet or runtime configuration changes, not engine changes. They
also set the baseline for engine accounting: everything below is measured
with PGO and the binlog off, mostly in the AOT image.

### Evaluation

Evaluation retires **~109 M instructions per configuration** (1,643
configurations, ~179 G), the same under the JIT and AOT. In the AOT console
process ([details](evaluation-perf.md#accounting-on-the-native-aot-console)):

| Subsystem | Share | Largest leaf kinds |
| --- | ---: | --- |
| Item evaluation | 22% | hash/lookup, path/filesystem, string scanning |
| Property evaluation | 15% | hash/lookup, kernel, path/filesystem |
| GC threads | 15% | |
| Condition evaluation | 14% | hash/lookup, string scanning |
| NuGet's own code | 10% | LINQ |
| File globbing | 6% | 83% in the regex interpreter |

The cost is spread thin: no function exceeds 2.6%. Measured against a
reference implementation that replays the recorded condition and
property-element streams
([floor](evaluation-perf.md#executable-floor-for-conditions-and-property-elements)):

| Instructions per unit | MSBuild (AOT) | Floor, `Dictionary` state | Floor, slot state |
| --- | ---: | ---: | ---: |
| Condition | ~10.6 k | 782 | 500 |
| Property element (excluding its condition) | ~18.2 k | 929 | 231 |
| Initial state per configuration | | 54 k | 5.9 k |

The ~14-20x gap to the dictionary floor is interpretation machinery: a
condition spends 35% re-expanding operands through the general expander and
7% on the property lookup it needs. Other costs are the Unix path heuristic,
numeric parse attempts, re-hashing the condition text for the parse cache, a
lock, and per-call reflection. A property element spends 16% (under AOT) in
`Type.InvokeMember` for ~23 late-bound property-function calls per
configuration. The further 1.6-9x from dictionary to slot state is the
data-structure share.

**Most of this work is the same work repeated.** A restore evaluates
3.99 M conditions over 2,752 distinct texts and 2.41 M property elements over
1,926 distinct values. A live-in census groups every import subtree, property
group, choose block, and item operation by its inputs. Equal inputs produced
equal effects in all but 2-3% of the work. On that basis, **86% of the
properties-and-imports work and 60% of item-operation work repeat with
identical inputs**, about half of evaluation
([details](evaluation-perf.md#amplification-and-invariance)). The props chain
varies per project file but not per target framework. The SDK `.targets`
files are unique per configuration as wholes, but most of their blocks repeat.

### Execution

- **Execution is serial.** Without multithreaded mode there is one in-proc
  node, so the 1,643 project requests run one after another: ~5.6 ms each on
  the 16-core machine (9.25 s), against a budget of ~1.4 ms.
- **Per request**, covered CPU is ~4.2 ms. Task service is ~1.27 ms (budget
  0.6 ms: 92 us of non-body work per task against a ~35 us allowance). Target
  traversal is 0.64 ms, intrinsic groups 0.37 ms, and the `ProjectStarted`
  property snapshot 0.22 ms.
- **Converting compact properties into objects for logging cost 1.035 CPU
  s** on the baseline serial path, although execution reads by name at most
  3.6% of them. The compact execution foundation now avoids that conversion
  ([details](evaluation-perf.md#request-activation-and-property-views)).
- **~2.3 s of the serial path is not covered by CPU scopes**: scheduler
  waits, `MSBuild`-task round trips between requests, or uncovered work.

The compact execution change has a repeatable throughput saving in a
separate 760-asset Xeon cohort: four balanced pairs reduce mean outer
restore from **18.018 to 17.218 s (4.44%)**. A separate phase cohort reduces
extraction execution from **8.215 to 7.772 s (5.40%)**, saving 0.452 and
0.434 s in its two pairs. Execution allocations also fall by 267 MB
(27%), a secondary benefit. All 14 captures match the same 760 assets.
Graph elapsed changes are inconsistent; do not credit a graph win or add
these savings to the Ryzen/Native AOT results. Individual snapshot,
lookup, object-demand, write/merge, and compatibility-barrier costs remain
unpriced
([methods and limits](evaluation-perf.md#compact-properties-through-execution-implementation-and-paired-results)).

The newer 639-asset Xeon account, with engine opt-ins unset and console
PGO/binlog off, observes **5.504 s graph elapsed and 6.394 s extraction
elapsed** in two JIT phase captures. Separate valid console ledgers cover
5.359 CPU s of request service under JIT and 3.866 s under AOT. Their
exclusive scopes reconcile, but no task-body scope is reported and outer
Arcade migrated frames are excluded. This is not complete process
accounting or an engine before/after comparison. It reinforces that
execution remains a separate part of the 5-6-second goal; evaluation
reuse alone cannot meet it.

## Direction

**Partially evaluate the existing interpreter** (the first Futamura
projection). Project and import XML is the static input, and a
configuration's properties, items, and filesystem are the dynamic input. Each
import becomes a source-bound module whose conditions, expressions, and
item operations are prepared once. The residual program runs per
configuration. Imports stay linked dynamically, because conditions and SDK
resolution select them. The existing evaluator remains the fallback for
anything unsupported.

The evidence says this is necessary but not sufficient. Earlier control
specializations removed 70-90% of their local costs without moving wall time,
because the remaining cost is in two places:

1. **State representation.** Writing ~1,400 properties and ~960 items per
   configuration as mutable, identity-bearing objects, and converting them for
   logging, costs more than computing them. The floor's slot-versus-dictionary
   gap measures this share. The target is configuration-owned compact state
   indexed by statically resolved names, with request and batch overlays and
   compatibility views that materialize public objects only on demand.
2. **Amplification.** The same imports run in hundreds of configurations.
   Evaluating each block or item operation once per distinct *live-in vector*
   (the inputs it reads before writing) and attaching its effect is polyvariant
   partial evaluation at block granularity. Filesystem observations and other
   required effects are still replayed. This differs from the dead ends:
   per-batch replay lost to lookup overhead on small units, and root-keyed
   prefix attachment never matched. Here the keys are the block's actual
   reads, and the census shows 86% and 60% of the work repeating.

Execution needs its own change, a **request-owned activation frame**: prepared
target and task plans that bind to compact state, explicit logging views, and
less per-request fixed cost. Every microsecond per request costs 1.6 ms of
wall time.

Out of scope here: the pure-evaluation mode, Declared-I/O task-result caches,
hardened graph validation, a persistent build server, a NuGet-specific
evaluator, and caching evaluated projects or task results.

## What this branch contains

| Area | State |
| --- | --- |
| Compiled evaluation modules, typed property/condition/item programs, source-bound imports and `Exists`, residual metadata programs, constant-literal item batching | Opt-in (`MSBUILDENABLECOMPILEDMODULEEVALUATION`, `MSBUILDENABLECOMPILEDMODULEEFFECTBATCHING`) |
| Compact-state request activation frame (logging snapshot and lookups without object conversion) | Opt-in through effect batching; repeatable Xeon execution-throughput saving, residual operations not yet priced |
| Compiled target plans and direct task actions | Opt-in (`MSBUILDENABLEACTIONGRAPHPARTIALEVALUATION`) |
| Async logging notification coalescing, separator-free path fast exit | Default-on |
| Property-group effect attachment (`MSBUILDATTACHPROPERTYGROUPS`, verify with `MSBUILDATTACHPROPERTYGROUPSVERIFY`) | Experimental opt-in; widened pure-function/observation support, local CPU benefit but no demonstrated macro win |
| Cost ledger (`MSBUILDCOSTLEDGER`) and evaluation trace (`MSBUILDEVALUATIONTRACE`) | Diagnostic, inactive by default |
| Fixes: empty AOT framework root in `ToolLocationHelper`, default-path read-tracker allocations | Default-on |

## Next steps

1. **Explain and improve property-group reuse economics before widening again.**
   Pure functions and revalidated filesystem/path observations now increase
   reuse from 569 k to 697 k groups; verification finds zero mismatches.
   Local phase ledgers suggest a 7.9% incremental properties-pass CPU saving,
   but separate phase-hook pairs do not consistently replicate it, and four
   macro pairs show no wall win. A same-engine single-CPU off/on control saves
   9.0% of properties CPU but only 2.8% of total evaluation. CPU profiles show
   remaining ordinary interpretation is about 71% of the widened group
   subtree; key lookup/publication costs more than write replay. Price hits
   and fallbacks by actual block cost, not count or census-average weights,
   and reconcile graph CPU/elapsed with item/glob work, GC, JIT, and scheduling
   in the same capture
   ([details](evaluation-perf.md#widened-property-group-reuse-local-implementation-and-economics)).
   Then decide between cheaper keys, eligibility for specific expensive
   blocks, and larger shared-state attachment. Item reuse and construction
   remain unimplemented; the census's 86% is not this cache's coverage.
2. **Fast paths for the late-bound property functions** (`File.ReadAllText`,
   `Regex.Match`/`IsMatch`, `Match.Groups`, `Version.*`,
   `RuntimeInformation.*`, `File.Exists`, `ToolLocationHelper.GetPlatformSDK*`)
   in `WellKnownFunctions`.
3. **Condition evaluation against the floor.** Prepared condition trees that
   read operands from state without general expansion or re-hashing, measured
   against the floor's per-shape costs.
4. **Property state representation** with slot-indexed compact state and
   compatibility views, measured against the floor's slot variant.
5. **Execution:** reconcile extraction process CPU and elapsed with covered
   request service, separate task bodies, then price activation-frame
   residuals. Keep the historical uncovered ~2.3 s separate from the new
   639-asset account.
6. **Hand off to NuGet and runtime owners:** `TieredPGO=false` for the static
   graph console, the binlog's serial path, and the Native AOT console. File
   globbing through the regex interpreter belongs here too.
7. **Reproduce the cost-weighted census on the Xeon.** Prototype verification
   and local timing controls are complete, but an independent item/block
   census remains pending. Keep item-operation construction separate from
   the measured application pass and avoid nested file/block double
   counting. Measure lookup, required observations, and state application
   against the cost of the specific work skipped; shared-base-plus-delta
   state remains a possible lever, not an established solution to the
   current macro gap.

## Measurement rules

- Use **instructions retired** (user and kernel) as the primary cost unit
  for per-scope and per-unit accounting, and wall time for macro results.
  Whole-restore instruction totals vary by about 3% between runs (GC and
  spin-waiting), so small macro differences need wall-time pairs. Keep
  Dynamic PGO off for accounting, and report spin-waits separately: their
  instruction counts depend on the CPU. Where
  hardware counters are unsupported, report that explicitly and use
  process-tree CPU plus validated scope task clock; do not infer
  instructions, cycles, or IPC.
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
