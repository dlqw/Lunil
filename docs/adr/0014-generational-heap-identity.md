# ADR 0014: Separate stable object identity, dense heap slots, and the young generation

- Date: 2026-07-17

## Context

The logical Lua heap previously used one `List<LuaGcObject>` for ownership, collection, and
generation discovery. Removing each dead object searched that list and shifted its tail, making a
large sweep quadratic. A minor collection still whitened, scanned for finalizers, snapshotted, and
reset the complete list, so its cost grew with the retained old heap rather than the young
generation.

Deleted table keys add a related identity constraint. Lua permits `next(table, deletedKey)` while
the table's tombstone remains, but retaining the key's `LuaValue` also retained the managed object
after logical collection. A storage slot cannot serve as that identity because swap-removal moves
live objects.

## Decision

Every logical object has two deliberately separate identifiers:

- `ObjectId` is immutable, unique within its heap, and remains the stable identity used by a
  collectable-key tombstone.
- `HeapIndex` is a private mutable dense slot. Registration appends at that slot; collection
  replaces a removed slot with the last live object and updates the replacement before removing
  the list tail. A collected object has slot `-1`.

The heap maintains an exact reference-identity young set. Registration adds an object, ordinary
minor survival advances `New` to `Survival` and then `Old0`, and a major sweep promotes every
snapshotted young survivor directly to `Old0`. Full cycles continue to use the complete dense heap.
Minor cycles whiten, scan for finalizers, snapshot, sweep, and reset only the young set.

Ordinary minor marking rejects old objects. Permanent and handle roots therefore mark young roots
without walking retained old graphs. An old-to-young write records the owner in the remembered set;
remembered owners are force-traversed through their normal `Traverse` implementation, preserving
weak-value and ephemeron rules rather than marking every target strongly. A newly remembered owner
during incremental propagation or atomic work is queued immediately. Remembered old colors are
reset after the minor cycle. A completed full cycle drops remembered owners whose snapshotted young
targets were promoted, but retains owners that acquired young edges during that cycle because
post-snapshot allocations can remain young.

Promotion itself is also a generational boundary. An owner can be older than an object assigned
while both were still young, so no old-to-young barrier existed at the original write. Objects
newly promoted by a minor cycle are therefore remembered conservatively until the next major
cycle. Newly promoted owners from a major cycle are retained for one further cycle as well, which
covers children allocated after that major cycle's sweep snapshot.

Table tombstones store the deleted collectable key's `ObjectId` and clear the key `LuaValue`.
Primitive keys remain inline. Matching a collectable tombstone compares the incoming object's
stable ID; reactivation restores the actual key and clears the tombstone ID. Rehashing may discard
tombstones as before.

## Invariants

1. For every live heap object, `Objects[object.HeapIndex]` is the same reference; no two live
   objects share a slot.
2. An object is in the young set exactly while its age is `New` or `Survival`.
3. `ObjectId` never derives from or changes with `HeapIndex`.
4. Every old-to-young mutable edge passes through a heap write barrier and records its owner.
5. Old objects are not collected by a minor cycle. Young reachability through remembered strong,
   weak, ephemeron, root, handle, or pending-finalizer graphs follows the same Lua rules as a full
   cycle.
6. A tombstone never holds a managed reference to a deleted collectable key.

## Verification

- Dense-slot tests collect many interleaved objects and check every surviving index plus the `-1`
  state of collected objects.
- Minor-GC tests use a 1,024-object old graph and require a two-object young sweep candidate set,
  then verify repeated promotion and full-cycle reclamation.
- Incremental mutation, handle graphs, remembered old roots, weak-key ephemerons, young finalizer
  separation, resurrection, and old-garbage deferral have focused regression coverage.
- A managed `WeakReference` regression proves that a deleted collectable key is reclaimable while
  its table tombstone remains.
- The old-heap growth benchmark compares 4,000 and 8,000 retained old objects with the same 500
  young objects. Minor-collection temporary allocation remains flat instead of doubling.

## Consequences

- Object removal is O(1), but heap list order is explicitly unstable and must not be externally
  observed.
- Minor work and snapshot allocation scale with young and remembered objects, not total retained
  heap size.
- The remembered set may conservatively retain old owners until a major cycle; it does not retain
  deleted young targets by itself.
- Hash buckets gain one internal 64-bit tombstone identity field. Lua logical-memory accounting
  remains stable because the field replaces a retained managed reference as implementation
  metadata and the Lua 5.4 memory-count contract must not change with CLR layout details.
