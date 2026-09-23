# Location contract — design

Status: design of record for protocol **v18** (breaking; NEB-220). Decisions made without asking are marked **D#**.
User-facing page: `website/content/docs/specifications/entity-location.mdx`. Conformance tests:
`Tests/EditMode/ConformanceLocationTests.cs` (`[Category("Conformance")]`, compiled into the Unity package and into
`Services~/Nebula.Services.Tests`).

## 0. Problem

Before v18 there were three partial answers to "where is this entity, durably":

- `NetworkIdentity.ContainerRef` — the per-tick wire name of the container (a dense index for a baked container,
  the carrier's net id for a carried one, the game's 64-bit id for a runtime one). The dense index moves whenever the
  baked set changes; a net id is reassigned on every mesh restart.
- `NetworkIdentity.InstanceId` — a 64-bit SHA-256 prefix of the game's instance key (`TemplateId/key`). The string
  it was made from was hashed on the worker and never kept, so no process could report *which* instance an entity is
  in, only that two entities are in the same one.
- `PersistedEntityRecord.ContainerId` / `CarrierKey` / `LocalPosition` / `LocalRotation` — the store's view, with no
  scope at all: a record restored into `rt_11` had to trust that `rt_11` still meant the same instance.

None of those is one thing that is the same on the worker, the gateway, the orchestrator, the client and in the
store, and none survives every kind of restart. Scope activation (bringing a scope's containers into being on demand,
NEB-233) and keyed world scopes (NEB-239) both need to name a place before it exists anywhere; they cannot be built
on a wire index or a net id.

## 1. The triple

An entity's **location** is three values:

| Part | Type | Meaning |
|---|---|---|
| `ScopeKey` | opaque string | Which simulation scope the entity is in. `""` is the public world. |
| `ContainerId` | string | The container's string id (`Container.ContainerId`). `""` is "no container". |
| local pose | `Vector3` + `Quaternion` | Position and rotation in the container's local frame (world frame when there is no container). |

`EntityLocation` (`Runtime/Containers/EntityLocation.cs`) is the value type: pure C#, compiled into the Unity runtime
and into `Services~`. Equality is exact (ordinal on both strings, per component on the pose), `ToString` is
`scope:container@(x, y, z)/(x, y, z, w)` in the invariant culture, and `Write`/`Read` are the wire form (§5).

**D1** The triple is a *value*, not a reference. It carries nothing that a handover, a lease change or a rebuild
changes: no authority epoch, no owning worker, no wire index, no net id of the entity itself. Those stay where they
are (`NetworkIdentity.Epoch`, `Container.OwnerWorkerId`, `ContainerRef`) and are compared separately.

**D2** Nebula never parses or interprets the scope key. The only operation on it is ordinal string equality. It is not
split on `/`, not hashed inside the contract, not ordered, not measured against another key. The hash the runtime still
uses internally for scope isolation (`InstanceId`, §3) is *derived from* the key by the worker that prepared the
instance and travels beside it; the contract does not depend on being able to invert it.

**D3** The container id in the triple is the container's string id, not its `ContainerRef`. The string id is the one
name a container has that every role already agrees on, that the lease row is keyed by, and that the store already
uses. The wire index stays the per-tick encoding of the container inside entity messages; it is not part of the
durable contract.

## 2. What each part is, per container kind

`EntityLocation.ContainerKind` classifies the id by its form alone, so a process that does not hold the container
can still tell what kind of place a triple names.

| Kind | `ContainerId` | `ScopeKey` | Local pose | Stability of the id |
|---|---|---|---|---|
| **Static** (authored in a scene or baked in the world manifest) | the authored id (`"plaza"`, `"cell_3_7"`) | `""` | in the container's transform frame | as stable as the authored id (the reader is told to keep it stable once the scene is in use) |
| **Runtime** (`ContainerRegistry.RegisterRuntime`) | `rt_<ulong>` in decimal | `""` for a public runtime box; the instance key for an instance part | in the lease row's absolute box frame | as stable as the id the game derived (`RuntimeGrid.PackId`, `NebulaWorker.InstanceKey(prefix + "/" + partId)`) |
| **Dynamic** (a `Container` on an entity's root, carried by the entity) | `<label>#<carrierNetId>` | the carrier's scope key | in the carrier's frame | **one mesh run**: the carrier's net id is reassigned when the mesh restarts (§4) |
| **None** | `""` | `""` | world frame | n/a |

**D4** A dynamic container's id is honestly reported as `label#netId` and honestly documented as living only as long
as the carrier's net id. Persistence does not store that id: a record saved inside a carrier keeps `ContainerId = ""`
and names the carrier by its persistence key (`CarrierKey`), exactly as before, and `PersistedEntityRecord.Location`
for such a record has an empty container id. Inventing a durable name for a carried place (`carrier:<key>`) would be a
second addressing scheme, which is a non-goal of this issue. If a durable carried location is ever needed it is a new
kind with a new id form, not a reinterpretation of this one.

## 3. What the scope key is today

There are two scopes today:

- The **public world**: `ScopeKey == ""` (`EntityLocation.PublicScope`). Every static container, every public runtime
  container, and every entity in no container is in it. `InstanceId == 0`.
- An **instance**: `ScopeKey == TemplateId + "/" + key`, the exact string `NebulaWorker.PrepareInstance(template, key,
  origin)` builds and hashes today (`InstanceBoundary` builds it the same way). `InstanceId == InstanceKey(ScopeKey)`.

**D5** The scope key of an instance *is* the existing prefix string, unchanged, not a new identifier. Nothing a game
already does to name instances changes; games that put a run id or a party id into `key` get it back in
`ScopeKey`.

**D6** The key is carried, not recomputed. `InstanceContainerInfo` (the instance metadata on a runtime container's
lease row and in `ContainerOwnership` entries) gains `string ScopeKey`. `PrepareInstance` fills it in when it ensures
the lease; `SyncRuntime` on every role copies it onto the `Container`; `Container.ScopeKey` (following the carrier for
a dynamic container) and `NetworkIdentity.ScopeKey` read it. The gateway and orchestrator, which run the service
`Container`/`ContainerRegistry` in `ServiceManifest.cs`, get the same property from the same lease row. That is the
whole plumbing; there is no second channel.

**D7** A lease row written before v18 has no scope key. `InstanceContainerInfo.Read` tolerates the shorter blob and
reads `ScopeKey = ""`; the row stands, `PrepareInstance` accepts it (it compares hash, bounds and content as before,
and only refuses a *different non-empty* key under the same hash, which would be a collision), and every entity in
that instance reports `ScopeKey == ""` until the instance is retired and prepared again. This is a documented
degradation, not a migration: the control plane is reset far more often than it is upgraded in place, and the mesh
must be restarted for v18 anyway. `PersistedEntityRecord.ScopeKey` likewise defaults to `""` when absent (JSON,
local file version 1, a SQL row from before the column).

## 4. Byte-stability guarantees and their limits

A triple produced on one process is byte for byte the triple produced on another for the same entity in the same
place, and the same one before and after the following, **for static and runtime containers**:

| Event | Why the triple is unchanged |
|---|---|
| Handover to another worker | Epoch and owner are not in the triple; the container id and scope key come from the lease row / scene, which the handover does not touch; the pose is already container-local on the wire. |
| Worker restart | Same: the worker rebuilds its registry from the scene, manifest and lease rows and reads the same ids and keys. |
| Mesh restart (control plane kept) | Lease rows persist, so `rt_*` ids and their `InstanceContainerInfo` come back verbatim; static ids are authored. |
| Mesh restart (control plane reset) | Static ids are authored. A runtime id is whatever the game derives (`PackId`, `InstanceKey`), so the same input produces the same id; an instance prepared again from the same template and key produces the same `ScopeKey` and the same `rt_*` ids. |
| Store round trip | `ScopeKey`, `ContainerId`, position and rotation are stored as-is (JSON, the local binary file, SQL columns); floats are written as IEEE-754 singles or as doubles that round-trip singles exactly. |
| Library upgrade within a protocol major | `EntityLocation.Write` uses only the primitive encodings of §5, which are pinned by `TheWireBytesOfAFixedTripleArePinned`; changing them is a protocol-version change. |

Limits, stated rather than hidden:

- **Dynamic containers** (§2, D4): the id holds for one mesh run. Across a restart the carrier is a new net id.
- **Authored id changes**: renaming a static container in the scene is the game changing the place's name. The
  contract makes that visible (old records no longer resolve); it does not paper over it.
- **World frame poses**: a triple with no container is in the world frame of the process that produced it. In a
  floating-origin world that frame is per process; the contract does not add an origin. Put entities in containers.
- **Epoch is not in the triple.** Two triples being equal says the entity is in the same place, not that either is
  the newer one. Stale-message rejection stays with `Epoch`.
- **Pre-v18 rows and records** read as the public world (D7).

## 5. Wire form

```text
EntityLocation := string scope_key, string container_id, vector3 local_position, quaternion local_rotation
```

Each primitive is the wire protocol's: `string` is `u16 (byteCount + 1)` then UTF-8 (a zero prefix is a null string,
which the contract never writes: the key and id are never null, only empty); `vector3` is three `f32`, `quaternion`
four `f32`, all little-endian. Nothing is compressed: this is a durable value, not a per-tick one.

**D8** No existing message carries an `EntityLocation` in v18. The triple is *built from* fields already on the wire
(`ContainerOwnership` entries name the container by id and carry `InstanceContainerInfo`; entity messages carry the
container-local pose) rather than added beside them; `Write`/`Read` exist so the value can be put on the wire by a
game, an extension or a later milestone without another format. The v17 → v18 break is exactly one field:
`InstanceContainerInfo` gains a trailing `string scope_key` (D6), which changes `ContainerOwnership` and the stored
lease row's `instance` blob.

## 6. Resolution per role

`EntityLocation.Resolve()` is `ContainerRegistry.FindById(ContainerId)`, then a scope check: the container found must
have `Container.ScopeKey == ScopeKey` (ordinal) or the result is null. Null also when the id is empty or the container
is not known on this process yet (a dynamic container arrives with its carrier, a runtime one with its lease).

| Role | Registry | Where its ids and keys come from |
|---|---|---|
| Worker (Unity) | `Runtime/Containers/ContainerRegistry.cs` | scene `Container`s (`Rebuild`) or world manifest (`Load`), then `SyncRuntime(ControlPlane.Leases)` |
| Client (Unity) | same | scene / manifest, then `ContainerOwnership` upserts (`RegisterRuntime` with the entry's `Instance`) |
| Gateway (standalone) | `Services~/Nebula.Services/ServiceManifest.cs` | exported service manifest (`ContainerRegistry.Load(manifest)`), then `SyncRuntime(ControlPlane.Leases)` |
| Orchestrator (standalone) | same | same |
| Gateway / orchestrator (Unity) | Unity registry | same as the worker |

Both registries expose `FindById` and both `Container` types expose `ContainerId` and `ScopeKey`, so `EntityLocation`
is one source file for all five. The conformance test populates the registry of whichever build it is compiled into
twice, from fresh objects and in a different order (so the dense index moves), and checks that the same triple
resolves a container with the same id and key each time and that the triple read back from it is equal.

**D9** The scope check is strict. A triple in scope A never resolves a container in scope B even when the id matches,
and a public triple never resolves an instance container. A pre-v18 instance record (`ScopeKey == ""`, D7) therefore
does not `Resolve()` against a re-prepared instance whose row now has a key; `NebulaPersistence` does not use
`Resolve()` for restore (it keeps using `ContainerId`/`CarrierKey` directly), so restore behaviour is unchanged, and
the strictness only bites callers who ask the contract the question it exists to answer.

## 7. What the store holds

`PersistedEntityRecord` gains `ScopeKey` and a `Location` property (get: the triple; set: writes `ScopeKey`,
`ContainerId` and the pose, leaves `CarrierKey` alone). `NebulaPersistence.BuildRecord` fills `ScopeKey` from
`identity.ScopeKey`. The three storage forms:

- JSON (`PersistedRecordJson`): `"scopeKey"` property; absent → `""`.
- Local binary file (`LocalPersistenceStore`): file version 2 appends `string scopeKey` to each record; a version 1
  file reads with `""`.
- SQL (`SqlPersistenceStore`): `scope_key TEXT NOT NULL DEFAULT ''`, added with an `ALTER TABLE` whose failure is
  taken as "already there" (SQLite has no `ADD COLUMN IF NOT EXISTS`).

The container read (`LoadContainer(containerId)` then, `LoadContainers(containerIds)` since the restore-burst fix) is
keyed by id alone: a container id is unique across the mesh, so the
scope key on the record is information, not a second lookup key. **D10** No store query by scope key in this
milestone; scope activation (NEB-233) decides what it needs.

## 8. How keyed scopes plug in later

- **Scope activation (NEB-233)** gets a place to start from: a triple whose `Resolve()` is null and whose
  `ContainerKind` is `Runtime` with a non-empty `ScopeKey` is a place that exists in the store or in a message but not
  on this process yet. Activation is "make the containers of `ScopeKey` exist here", and the contract already says
  what to compare when they do. Nothing in `EntityLocation` changes.
- **Keyed world scopes (NEB-239)** are more values of `ScopeKey`. Because Nebula does not parse the key (D2), a shard
  key, a season key or a region key is just another string the game chose, carried on the same lease-row field, read
  by the same `Container.ScopeKey`. Whether such scopes have static containers in them, and how a static id is
  qualified by scope, is that issue's decision; the triple has room for it because `ContainerId` and `ScopeKey` are
  separate parts rather than one composed string.
- **What must not happen**: no role starts deriving the key (splitting `TemplateId/key`, hashing it into the
  contract, ordering keys), and no message starts carrying the hash in place of the key. `InstanceId` remains an
  optimization for isolation checks, not a name.

## 9. Non-goals (from the issue, kept)

Hierarchical addressing, human-readable naming, distance between scopes, procedural derivation of keys. The scope key
stays a flat opaque string; `ToString` is for logs, not for parsing.
