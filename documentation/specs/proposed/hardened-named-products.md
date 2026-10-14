# Named products for hardened builds

## Status and scope

This is a proposed extension to the
[hardened static graph](hardened-static-graph.md), not implemented behavior.
The item and switch names below specify a proposed authoring contract.

The goal is to request logical build products and execute only their required
producer closure. Users should not need to know intermediate output paths.
Products may contain multiple artifacts, and one target may describe many
independently requestable products.

This proposal extends the standalone action graph, initially emitted as
`build.ninja` plus per-action invocation manifests. MSBuild constructs and
validates the graph; Ninja launches independent task hosts after MSBuild exits.
Product discovery and validation are the first product-specific slice, with
demand selection and cache integration as subsequent stages. Ordinary target
execution remains unchanged, and products are not a prerequisite for the
initial standalone prototype.

## Model

| Concept | Meaning |
| --- | --- |
| Product | A named export from a configured project, containing artifact references. |
| Artifact | A source or generated file with a known path and, for generated files, producer identity. Its contents need not exist during construction. |
| Action | A concrete file-I/O task invocation, including its target and task batching context, executed by an independent host. This is the indivisible execution unit. |
| Target | Ordinary MSBuild code used to construct actions and values; not automatically a product or a cache boundary. |
| Product request | A configured project and product name, resolved to a set of demanded artifacts. |

A product's identity is its owning project configuration plus its logical
name. Existing project-reference and global-property rules determine the
configuration, including inner-build selection. Artifact and action identities
retain that configuration and provenance through item transforms and
cross-project returns.

Products are not filesystem paths. Renaming an intermediate directory need not
rename a product, although changed paths can still change action cache keys.
This proposal does not make actions relocatable across checkouts.

## Product catalog

Use ordinary MSBuild items, not new XML elements or target attributes.
Initially, catalog membership and metadata must be known after project
evaluation. Later support for catalogs computed by target partial evaluation
must define a discovery entry point without executing file-I/O actions.
Zero-I/O Declared-IO invocations may compute construction-time values; they
are not a separate Pure task class.

### Local products

```xml
<ItemGroup>
  <BuildProduct Include="GeneratedSource/B">
    <Targets>GenerateSources</Targets>
    <Artifacts>$(IntermediateOutputPath)B.g.cs</Artifacts>
  </BuildProduct>
</ItemGroup>
```

`Include` is the logical product name. `Targets` identifies the ordinary
target entry points whose construction describes its producers. `Artifacts`
is an MSBuild list of exact file paths, relative to the owning project.
Product names use MSBuild name comparison and cannot contain the request
separators `;` or `::`. Duplicate definitions are errors, not implicit merges.
Local products must name at least one artifact and one construction target.

The target closure includes ordinary dependencies, before/after targets,
batching, and cross-project calls. Listing `GenerateSources` does not demand
every artifact produced by that target. The product's files select the
required actions after the closure has been constructed and validated.

Catalogs can be generated from item lists using ordinary transforms:

```xml
<ItemGroup>
  <Schema Include="A.schema;B.schema;C.schema">
    <GeneratedPath>$(IntermediateOutputPath)%(Filename).g.cs</GeneratedPath>
  </Schema>
  <BuildProduct Include="@(Schema->'GeneratedSource/%(Filename)')">
    <Targets>GenerateSources</Targets>
    <Artifacts>%(GeneratedPath)</Artifacts>
  </BuildProduct>
</ItemGroup>
```

The transform preserves each schema item's custom metadata. It defines three
product instances without requiring three XML targets.

Products select artifacts; they do not duplicate the task's declared-I/O
contract. Declared inputs and outputs still determine action dependencies.
A catalog entry alone neither proves that a file exists nor authorizes a
producer to write it.

### Named collections

A product may instead aggregate other products from the same catalog:

```xml
<ItemGroup>
  <BuildProduct Include="GeneratedSources">
    <Products>GeneratedSource/A;GeneratedSource/B;GeneratedSource/C</Products>
  </BuildProduct>
</ItemGroup>
```

Aggregation unions artifact demands; it does not combine producers into one
action. An empty aggregate is permitted. Cycles and missing members are
errors. A caller can request the collection or one of its individually named
members. An artifact within a multi-artifact product is independently
requestable only if the catalog also exports a product for it.

### Cross-project exports

A product may instead forward a product through an existing project reference:

```xml
<ItemGroup>
  <ProjectReference Include="tools/Generators.proj">
    <ProductReferenceName>Generators</ProductReferenceName>
  </ProjectReference>
  <BuildProduct Include="GeneratedSource/B">
    <FromProjectReference>Generators</FromProjectReference>
    <FromProduct>GeneratedSource/B</FromProduct>
  </BuildProduct>
</ItemGroup>
```

`ProductReferenceName` names a unique project-reference edge in the owning
project. The existing reference metadata and project-reference protocol select
the child configuration; product forwarding does not introduce another
global-property syntax. An unresolved or ambiguous configuration is an error.
Forwarding preserves the child artifact's ownership and producer, rather than
creating a new producer in the parent.

The three declaration forms are disjoint: local (`Targets`, `Artifacts`),
aggregate (`Products`), or forwarded (`FromProjectReference`, `FromProduct`).
Mixing forms is an error.

The same configured product requested through multiple parents is shared.
Aggregation must not collapse distinct frameworks or configurations simply
because their product names match. Two distinct actions claiming the same
physical output path are not made safe by different product names.

## Request surface

Proposed examples:

```sh
msbuild Generators.proj --hardened-graph -product:GeneratedSource/B -emitActionGraph:out/build.ninja
msbuild Repo.proj --hardened-graph -product:GeneratedSource/B -emitActionGraph:out/build.ninja
msbuild Repo.sln --hardened-graph -product:tools/Generators.proj::GeneratedSource/B -emitActionGraph:out/build.ninja
ninja -f out/build.ninja
```

`-emitActionGraph` and `-product` are proposed switches, not implemented
behavior. Generation records the selected products as Ninja roots; it does not
run their producers. Execution is a separate command without MSBuild.

`-product` accepts a list of logical product selectors. Project-qualified
selectors use project paths relative to the entry point's directory and
resolve only within the entry point's configured project graph. They do not
select artifact paths. A project-qualified request that resolves to multiple
configurations is rejected until the caller selects a configuration.

A top-level project exposes its own catalog, including forwarding and
aggregation. A solution adapter can aggregate the same named product across
its enabled project configurations, respecting solution configuration
mappings; a missing export is an error, not an implicit omission. A qualified
request selects just that project's product instead. An outer multitargeting
project similarly needs an explicit aggregate or an unambiguous inner-build
selection.

No products are automatically requested merely because a project or target was
analyzed. Conversely, if the user explicitly requests a collection containing
every solution project's default products, those are all legitimate roots.

In the first CLI version, product requests require hardened mode and cannot
be combined with `-target` or `-getTargetResult`. This avoids silently changing
the meaning of an existing command or its required effects. Without a product
request, graph generation uses the requested/default target closure as its
roots. Without graph generation, ordinary target requests work as before.

## Graph construction and selection

1. Resolve the requested catalogs, forwarding edges, and configurations.
2. Construct and validate the union of the local products' target scopes.
3. Retain concrete action identities and artifact provenance, not just
   flattened path lists.
4. Resolve exported artifacts to declared producers or recorded source inputs.
5. Select the transitive artifact and required control dependencies of the
   requested roots.
6. Emit selected Ninja roots and concrete task-host invocation manifests for
   standalone execution.

Existing `MSBuild` task calls still describe interproject computation.
Construction must resolve static child target returns without executing child
file-I/O actions. Those returned items retain ordinary identities and metadata,
plus engine-owned artifact provenance. Exporting or aggregating these items
does not demand their contents. A deferred child return needed for graph
construction remains a validation error.

Supported engine intrinsics such as `MSBuild` and `CallTarget` expand into
project/target edges during construction. They are not opaque cacheable
actions. Routing that cannot be expanded this way is rejected in product mode.

Existing `ProjectGraph` and isolated-build result routing are integration
points. Neither `ProjectReference` nor target `Returns` alone provides a
complete action graph or producer contract.

Demand flows from requested products toward their dependencies. Artifact
results and digests flow back toward consumers. These are separate operations.
The first implementation may analyze more projects and actions than it demands;
reducing analysis cost is not a prerequisite.

## Execution and cache boundaries

An action remains indivisible. If three task buckets independently produce
`A.g.cs`, `B.g.cs`, and `C.g.cs`, requesting B demands only B's bucket and its
dependencies. If one invocation produces all three, a miss executes that entire
invocation. Filtering its task parameters would change the action, not prune
the graph.

A cache result must expose output identities separately from materialization.
Consumer keys include consumed artifact digests and relevant metadata, not the
producer action key. An upstream action can change while its exported reference
assembly remains identical, leaving downstream compilation keys unchanged.

Hits supply output descriptors without restoring every intermediate file.
Misses materialize the inputs they need and launch standalone task hosts.
Requested final files are materialized for the caller. All outputs of an
executed action remain subject to its declared-I/O contract, even when only
some are requested. A miss may therefore leave unrequested co-outputs on disk.

An absent output can be represented in an action-cache entry, but cannot
satisfy a product promising that file. Required product files missing after
successful execution or cache lookup are errors.

This does not initially promise a whole-project or whole-subgraph cache hit.
Resolving each selected action's key can still require traversing its
dependencies and looking up their results. Skipping those lookups requires a
separate, sound aggregate dependency summary. Persisting graph construction
across processes is also a separate optimization.

Ninja's initial file/timestamp incrementality does not implement this artifact
cache protocol. Content keys, lazy materialization, and output descriptors
require later standalone cache integration, not a return to engine execution.

## Safety boundary

The first queryable scope must be fully audited:

- After structural expansion of engine intrinsics, every task invocation must
  have a complete Declared-IO contract. Empty input and output lists permit
  construction-time execution with known parameters and ordinary value
  returns; nonempty footprints become actions. Unaudited tasks anywhere in
  the construction scope are rejected before demand pruning; an opaque task
  cannot be assumed unrelated to the product.
- All parameters and MSBuild values needed by the selected graph must resolve
  through graph construction. General execution-time property/item state-delta
  replay is not part of the first implementation.
- Each generated artifact must have one unambiguous producer. Overlapping
  writes, in-place updates, and directory outputs require additional modeling
  and are initially rejected in product mode.
- Catalog membership, artifact paths, project routing, and batch identities
  must not depend on executing file-I/O actions or probing generated output
  existence.
- Required control dependencies and graph-construction diagnostics cannot be
  removed merely because they produce no files. Unsupported failure/status
  dependencies, `OnError` effects, or external effects prevent query mode.

Ordinary execution order is preserved among demanded actions where it matters.
XML sibling order alone does not demand an otherwise independent action, but
any observable order-dependent behavior needs a modeled dependency or an
error. Product mode promises the selected products, not the execution trace,
diagnostics, or unrelated effects of running the entire target scope.

This requires more precise dependency selection than the initial prototype's
conservative ordering. Removing an ordering edge requires establishing that it
is unnecessary for the promised products and supported failure behavior.

Both the task cache and this proposal trust task annotations. Static validation
does not prove that arbitrary task code honors its declared contract.

## Compatibility and first milestone

Catalog declarations are ordinary items and metadata, and construction targets
remain ordinary targets. Older engines can continue running the existing build
entry points; they need not understand the catalog to execute them. The new
CLI request is opt-in and does not redefine `Build`, `Returns`, or timestamp
incrementality. SDKs must not remove legacy build wiring in favor of catalog
entries.

The first implementation milestone is catalog resolution and validation for
a two-project fixture containing multiple batched generator invocations.
It produces inspectable product-to-artifact-to-producer relationships without
changing which tasks ordinary builds execute.

The subsequent scheduling/cache milestone must demonstrate:

1. A top-level product forwarding one generated file selects the correct child
   configuration and producer bucket, not all projects or outputs.
2. A multi-output invocation still executes in full on a miss.
3. A repeat request uses cached results and materializes the requested file
   without restoring unnecessary intermediate files.
4. Changing an unrelated source does not execute an unrelated producer.
5. A producer change that preserves a consumed artifact's digest does not
   invalidate its consumer.
6. Ambiguous ownership, missing exports, deferred routing, and opaque effects
   fail before task side effects.
7. Ordinary target builds retain their existing behavior.

Remote caching, directory products, arbitrary effect requests, richer typed
providers, relocatable keys, aggregate subgraph caches, and persistent analysis
are not prerequisites for these milestones.
