# Prefix-state attachment for compiled evaluation

Prefix-state attachment caches the property environment produced by a
deterministic initial region of evaluation. A matching project configuration
installs that environment as an immutable base instead of replaying every
property assignment in the region.

## Model

For an evaluation prefix such as:

```text
initial and global properties
    -> shared property configuration
    -> prefix boundary
    -> configuration-specific evaluation
```

configurations with equivalent values for the prefix's live-in dependencies
produce equivalent property state at the boundary. The cache therefore maps
one dependency key to an immutable property state:

```text
configuration 1: evaluate prefix -> publish prefix state
configuration 2: compute key -> attach prefix state
configuration 3: compute key -> attach prefix state
```

Attachment does not enumerate or replay the cached property effects.

## Key

The key contains values observed before the prefix has definitely assigned
them. Values produced and consumed entirely within the prefix are not
dependencies.

Global-property override state is also part of eligibility and equivalence.
The prototype falls back to normal evaluation when a prefix assignment is
blocked by a non-overridable global property, preserving existing logging
semantics. Treat-as-local global values that affect the prefix are represented
in the key.

Module identity and version are implicit because each lowered
`EvaluationModule` owns its prefix cache.

Undefined and empty properties are distinct key values even though ordinary
property expansion frequently produces the same text for both.

## Attached representation

`CompactPropertyEnvironment` has three logical layers:

```text
mutable compact overlay
immutable shared prefix base
legacy initial/global property dictionary
```

Reads check the compact overlay and shared base before the legacy dictionary.
Writes after attachment go to the overlay. Removing or materializing a property
records a tombstone so a value in the shared base cannot reappear.

The immutable base is privately owned and exposed only through read methods.
Attaching it is O(1); enumerating its entries is deferred until an object-model
operation requires materializing the complete property table.

## Prototype boundary

The initial implementation is intentionally narrow:

- `ProjectInstance` evaluation only.
- The opt-in compiled-module effect-batching path only.
- One leading prefix in the root project module.
- No top implicit imports before the prefix.
- Only contiguous compiled property groups.
- No scalar fallback, contextual properties, opaque expansion, or compiled
  property functions.
- No non-overridable global assignment in a cached evaluation.
- No design-time conditioned-property or predecessor history.

The prefix ends at the first ineligible or non-property element. Evaluation
after the boundary proceeds normally in a mutable overlay.

This scope tests the architectural proposition without changing normal
evaluation behavior or attempting to snapshot import bookkeeping. A later
prototype can make an import-closed region eligible by adding imported-project
and duplicate-import state, stable source identities, SDK-resolution inputs,
and evaluation logging data to the attached state.
