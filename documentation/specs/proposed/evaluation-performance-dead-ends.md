# Evaluation performance dead ends

This file records optimization experiments that did not produce a worthwhile
wall-time improvement. They are not claims that the underlying ideas can never
work, but they should not be repeated without a materially different
architecture or workload.

The measurements below use the .NET runtime static-graph restore workload,
which evaluates 1,643 project configurations. The roughly one-second
wall-time improvement with a positive 95% confidence interval was the
**historical retention gate for these experiments**, not the current policy
for small, bounded specializations. For that policy and the existing-semantics
goal, see the [classic restore performance plan](restore-performance-plan.md).

## Property program and SSA variants

Several representations reduced cumulative property-evaluation work but did
not reduce wall time enough to retain. Reintroducing another fine-grained
property IR is unlikely to help unless it removes a larger unit of evaluation
or changes how resulting property state is represented.

## Dependency-keyed compiled property-batch replay

Caching individual compiled property batches produced 539,347 hits, but
dependency lookup and result publication cost more than the expression work it
removed. Restricting replay to batches containing at least eight properties was
also neutral. Future reuse should perform one equivalence lookup for a coarse
module prefix and attach shared state rather than replaying individual batches.

## Compact-property read materialization

Reading replay inputs through the compact property provider avoided
materializing the complete property table. In an isolated balanced benchmark,
however, wall time changed from 20.192 seconds to 20.241 seconds, with a paired
95% confidence interval for improvement of -0.275 to 0.177 seconds, so the
change was removed.

## Lazy cloning for metadata updates

Deferring item clones until a metadata assignment actually applied reduced
some update-selection work but shifted cost into metadata decoration and did
not improve wall time. The dominant metadata-dependent updates still had to
evaluate their conditions and values for each item.

## Empty and bare self-update fast paths

Skipping safe empty updates and avoiding an unnecessary dependency snapshot
for bare self-updates such as `Compile Update="@(Compile)"` was promising in
short runs. The full balanced benchmark measured 20.681 seconds versus 20.161
seconds, an improvement of 0.520 seconds with a 95% confidence interval of
-0.061 to 1.101 seconds. This did not meet the retention criterion and was
removed.
