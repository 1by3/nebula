# Replicated collections — design

Status: design of record for **NEB-335**. Wire protocol **21**, additive for clients (the window is 19..21); two new
messages (`EntityMaps` 38, worker to gateway to client, and `GhostMaps` 50, worker to worker) and one trailing field in
`EntitySpawnMsg` (`Maps`). Decisions made without asking are marked **D#**. Builds on `NetworkVariable`
(`Runtime/Core/NetworkVariable.cs`), the gateway's entity cache (`docs/interest-management.md`), persistence
(`docs/persistence-durability.md`) and handover (`docs/conformance-suite.md` scenario 8). User-facing page:
`website/content/docs/guides/network-map.mdx`. Tests: `Tests/EditMode/NetworkMapTests.cs` (the map, the codec, the
worker and the client, conformance scenarios on the `ConformanceMesh`) and
`Services~/Nebula.Services.Tests/ConformanceNetworkMapTests.cs` (the gateway).

## 0. Problem

Nebula has no replicated list or dictionary. A game that keeps an inventory, a set of discovered waypoints or the
edits of a chunk in a `NetworkVariable` pays for the whole collection on every change: `NetworkIdentity.WriteVars`
writes every variable of the entity, and `EntityVarsMsg` carries the lot, capped at 64 KB. ChunkState does exactly
this and resends up to 48 KB when one rock is mined (`docs/chunk-state.md` D6).

A custom sync behaviour cannot do better on its own. It can send reliable deltas, but the gateway caches only chunks
flagged `Full` (`NebulaGateway.EntityRecord.StoreKeyframe`), so a client that arrives after a delta is given the
older keyframe and never the delta (`guides/physics-and-handover-state.mdx`, "What the gateway caches"). Getting a late
joiner right needs a party that knows the collection's current contents on the client's side of the mesh, and the
gateway, which has no game types, is the only one there.

What a game needs is a keyed collection where:

- the writer sends only the entries that were added, changed or removed;
- a late joiner gets one full copy, then the same increments as everyone else;
- it persists through `[Persist]` like a variable;
- it survives handover and behaves the same on ghosts as on clients.

## 1. The surface

```csharp
public sealed class Inventory : NetworkBehaviour
{
    [Persist] public NetworkMap<int, ItemStack> Slots = new NetworkMap<int, ItemStack>();

    public override void OnNetworkSpawn() => Slots.OnChanged += OnSlotChanged;
    void OnSlotChanged(NetworkMapChange<int, ItemStack> change) { /* change.Kind, Key, OldValue, NewValue */ }
}

// Authority only, like NetworkVariable.Value:
Slots[3] = stack;             // add or replace
Slots.Add(4, other);          // throws if the key exists
Slots.Remove(3);
Slots.Clear();
Slots.TrySet(5, big);         // false instead of throwing when the map would pass its size cap
Slots.SetDirty(4);            // resend an entry whose reference-typed value was changed in place

// Every copy:
Slots.Count; Slots.TryGetValue(3, out var s); Slots.ContainsKey(3); foreach (var kv in Slots) { }
```

`NetworkMap<TKey, TValue>` implements `IReadOnlyDictionary<TKey, TValue>`. Keys and values use
`NetworkSerialization`, so anything a `NetworkVariable<T>` can hold works here.

## 2. Decisions

**D1 A map is a variable with its own stream.** `NetworkMapBase` derives from `NetworkVariableBase`, so it is
discovered the same way (a field on a `NetworkBehaviour`), gets the same stable name, and takes `[Persist]` the same
way. `NetworkIdentity` keeps its maps in `Maps` (a subset of `AllVars`, in the same order) and leaves them out of
`WriteVars`/`ReadVars`. A change to a plain variable therefore never resends a map, and a change to a map never
resends the variables. `Write`/`Read` on a map (the `NetworkVariableBase` contract) are the full contents, which is
what persistence uses.

**D2 A delta carries each touched key's final state, not a log of operations.** On the authority a map remembers
which keys changed since the last send and whether it was cleared. The delta written for a tick is: the clear flag,
then for each touched key either `Set` with its current value or `Remove`. Three writes to one key in a tick send one
entry. Applying a delta to a copy that already contains it changes nothing, so a delta is idempotent over any full
copy taken after its changes. That matters because a ghost spawn, a gateway subscription or a handover can snapshot
the map in the same tick that its delta goes out: the receiver gets the snapshot and then the delta, and ends in the
same state. A full copy is the same body with the clear flag set and every entry as `Set`.

**D3 Keys and values are opaque, length-prefixed bytes on the wire.** The gateway has no game types (the standalone
gateway has no game assemblies at all), yet it must keep each map's contents. So every entry is written as
`[keyLength:ushort][key][valueLength:ushort][value]` with the bytes `NetworkSerialization` produced, and the gateway
keys its copy by the key's bytes. `NetworkSerialization` is deterministic for every built-in type, so one key always
has one encoding; a custom serializer that is not deterministic for equal keys is a game bug, and the guide says so.
Formats (`NetworkMapCodec`, in `Runtime/Protocol`, compiled into the standalone gateway):

```
body    = [version:byte=1][flags:byte (1 = clear first)][count:int] { [op:byte (0 remove, 1 set)][keyLen:ushort][key]([valueLen:ushort][value]) }
section = [mapCount:byte] { [mapIndex:byte][bodyLength:int][body] }
```

`mapIndex` is the map's position in `NetworkIdentity.Maps`, which is the discovery order and the same on every
process that runs the build.

**D4 Two new reliable messages, sent after the tick's variables.** `EntityMaps` (38) goes from the authority to the
gateways that hold the entity, which forward it to the clients that hold it; `GhostMaps` (50) goes to the workers that
hold a ghost. Both are `EntityMapsMsg { NetId, Epoch, Maps }` with a section of the maps that changed, reliable
ordered on the same channel as spawns and variables, so they can never overtake the spawn they apply to. They are
written where `EntityVars`/`GhostVars` are written, to the same destinations, and the change set is cleared in
`NetworkIdentity.ClearDirty` with the variables. A receiver drops a message whose epoch is older than its copy.

**D5 The full copy rides the spawn.** `EntitySpawnMsg.Maps` holds a section with every map in full. It is a trailing
field: written only when non-empty in a standalone spawn (after the audience section, which is then always written),
and always written in a handover's embedded spawn. So the entity spawn a gateway gets, a ghost spawn, a handover and
the new owner's re-announcement all carry the complete maps, with no new path. The field is `int`-length-prefixed;
the maps are bounded by D11, not by the 64 KB of `WriteBytes`.

**D6 The gateway keeps the current contents.** Each `EntityRecord` holds a decoded copy of every map (`NetworkMapCache`,
entries keyed by key bytes). A spawn from the owning worker replaces it; an `EntityMaps` from the owning worker
(epoch and owner checked like `EntityVars`, held behind a held spawn like every other update) is applied to it and
forwarded. When a client is sent the entity later, its spawn carries the cache encoded as a full section (encoded
lazily, once per change, not once per client). A late joiner therefore gets one full copy, and from then on the same
increments as everyone else, in order, because the spawn and the increments share the client's reliable channel.
The gateway pays memory for each map it relays: the same bytes the clients hold.

**D7 Handover needs nothing new.** The transfer's embedded spawn carries the full maps as they are at the moment of
transfer, including changes not yet sent; the new owner reads them before it gains authority and announces the
entity to the gateways with a spawn that carries them again. Every client that already holds the entity is sent that
spawn and replaces its copy, raising a change for each key that differs (D10). A change set left behind on the old
owner is dropped: it becomes a ghost, and `ClearDirty` runs on every received spawn and every received delta. The new
owner starts with an empty change set (`ApplySpawnData` ends in `ClearDirty`).

**D8 Ghosts are clients of the stream.** A ghost reads the full maps from its ghost spawn and applies `GhostMaps`
deltas, epoch-checked, exactly as a client applies `EntityMaps`. A worker that starts ghosting mid-stream gets a
ghost spawn with the full copy and then the deltas (D2 makes a same-tick overlap harmless).

**D9 Persistence is the variable's.** A `[Persist]` map is saved under its name in `PersistentStateCodec`, as its full
body, and restored by reading that body (which replaces the contents). A persisted entry is limited to 64 KB by the
codec's `ushort` length, which D11's cap stays under. A record restored onto a live, authoritative entity marks the
map for a full resend (clear plus every entry) so gateways and ghosts converge, as `NebulaPersistence` already does
for variables.

**D10 Changes are raised per key, on every copy.** `OnChanged` receives a `NetworkMapChange<TKey, TValue>` with a
`Kind` (`Added`, `Updated`, `Removed`), the key, and the old and new values. The authority raises it as it writes;
a receiver raises it as it applies a delta. A full copy received on top of existing contents (a handover's
re-announcement, a client re-entering) is diffed: removed keys, then added or updated keys, and nothing for a key whose
value is equal. `Clear()` raises `Removed` for each key. Writing a value equal to the current one does nothing, as
with `NetworkVariable`.

**D11 Each map is capped at 60 KB encoded.** The cap is `NetworkMapBase.MaxEncodedBytes` (61,440 bytes of body),
which keeps a `[Persist]` map inside a persisted entry and a full copy a sane size for a spawn. The authority tracks
the encoded size as it writes; a write that would pass the cap throws `InvalidOperationException` (`TrySet` returns
false) and changes nothing. Big, sparse world state should be split across several entities, as ChunkState does per
chunk.

**D12 Protocol 21, additive for clients.** What a client can see: a new message id (`EntityMaps`) and a trailing
field in the spawn. The gateway sends `EntityMaps` only to clients that negotiated 21 or later; a protocol-19 or -20
client reads a spawn with the trailing field and ignores it. Such a client cannot be running a build that declares a
`NetworkMap` anyway, so it misses nothing it could use. Workers and gateways of one mesh must match exactly, as
before.

**D13 What a map does not have.** No sync audience: a map goes to everyone who holds the entity, like a variable
(`docs/sync-audience.md` D12). No `[SyncHistory]`: a map on it is refused with a warning at discovery, because a
per-tick snapshot of a collection is not what state history is for. No write from a client: only the authority, or
code running while a handover is being received, may write, and any other write is ignored with a warning, as for a
variable.

**D14 In-place changes to reference values must be announced.** A map cannot see a mutation inside a value it holds
(a `byte[]`, a class). `SetDirty(key)` marks the key so its current value is sent; the guide recommends immutable
values.

**D15 ChunkState moves onto a map.** `ChunkStateEntity` keeps its entries in a `NetworkMap<ulong, ObjectState>`
instead of one whole-map variable, so mining a rock sends one entry rather than the chunk's 48 KB. Its public surface,
its events, its 48 KB cap, its expiry and its saved record format do not change: the record is still written by
`WritePersistentState` in `ChunkStateCodec`'s format (the map is not `[Persist]`), so records saved before this change
still load. This revises `docs/chunk-state.md` D6.

## 3. What this does not do

- A map is keyed, not ordered. A list whose order matters can be modelled as a map from a stable index or id.
- A large full copy still costs its size once per joiner, per gateway and per ghost worker; only changes are cheap.
- The gateway holds a copy of every map it relays; a mesh with many large maps pays that memory at each gateway.
- There is no per-entry interest: a client that holds the entity gets the whole map.

## 4. Tests

- `Tests/EditMode/NetworkMapTests.cs`: the codec round trip; a delta carries only touched keys, coalesced per tick;
  delta idempotence over a snapshot; change events on the authority and the receiver, including the diff of a full
  copy; the size cap; writes without authority ignored; `[Persist]` round trip through `PersistentStateCodec`;
  `WriteVars` never contains a map. Conformance scenarios on the `ConformanceMesh`: a ghost gets the full copy then
  only increments; a handover carries unsent changes and the new owner's announce carries the full copy; the gateway
  is sent a full copy at spawn and then only deltas.
- `Services~/Nebula.Services.Tests/ConformanceNetworkMapTests.cs` (a real gateway over UDP): a client gets the full copy
  in its spawn and then each delta; a late joiner gets the current contents (not the spawn-time contents) and then
  the same deltas; a stale-epoch delta is ignored; a new owner's spawn replaces the cache; a protocol-20 client is
  never sent `EntityMaps`.
- `Tests/EditMode/ChunkStateTests.cs`: unchanged expectations, now over the map.
