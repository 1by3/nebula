# Explicit capacity limits and admission reporting — design

Status: design of record for **NEB-236**, protocol **v18** (two appended `JoinRejected` fields and one new
`JoinStatus` reason value; no version bump). Decisions made without asking are marked **D#**. Builds directly on
`docs/cost-telemetry.md` (NEB-225), which gave every container a typed cost row with a dominant component and a
saturation, and on `docs/scope-lifecycle.md` (NEB-240), which gave a held join a reason byte. User-facing pages:
`website/content/docs/guides/scopes.mdx` §"When a target is full" and
`website/content/docs/guides/orchestrator-and-dashboard.mdx` §"Capacity and admission". Conformance tests:
`Services~/Nebula.Services.Tests/ConformanceCapacityAdmissionTests.cs` (`[Category("Conformance")]`, scenario 12 of
`docs/conformance-suite.md`); unit tests `Tests/EditMode/CapacityAdmissionTests.cs`, which run in both places.

## 0. Problem

A live event opens a new station and six hundred players jump to it in five minutes. The station is one
interaction domain: its boundaries are authored, and past them it cannot be split without making it a different
place. At some point one more arrival degrades the experience of everyone already inside.

Nebula could see this coming — the cost rows say exactly how close the station's worker is to its tick budget —
and had no way to say so. The gateway's vocabulary for refusing a client was "your token is bad" and "this gateway
is draining"; neither is true, and neither tells a travel service what to do. The three things the mesh must
*not* do on its own are the three things a mesh is tempted to do: silently make a second copy of the station,
silently split the domain, or silently let the tick collapse. So capacity has to become an answer the game gets,
and the decision of what to do about it has to be the game's.

## 1. The capacity signal

**D1 Capacity is derived, never separately measured.** `CapacityInfo` (`Runtime/Orchestrator/CapacityInfo.cs`) is
one `ContainerCost` row read through a threshold:

```csharp
public struct CapacityInfo
{
    public string ContainerId, ScopeKey;
    public bool  Known;          // a worker has reported cost for this target lately
    public float Saturation;     // the dominant component's share of its own budget; 1 = the whole of it
    public CostComponent Dominant;
    public bool  AtCapacity;     // Saturation >= NebulaConfig.CapacitySaturation
}
```

There is no new measurement, no new cadence and no new message from a worker. Cost telemetry already answers "how
close is this container to a budget, and which budget" (`docs/cost-telemetry.md` D7), which is the whole of the
question; a second, differently derived "fullness" number would be a second answer that could disagree with the
scaler's.

**D2 The saturation is the dominant component's, not a mean of the three.** A station can be full of simulation, of
replication or of per-client relay, and averaging them would report a station that is 100 % simulation-bound as a
third full. `Dominant` travels with the number, because the answer the game wants to show ("the docking bay is
busy") and the answer the operator wants ("split the cell" / "tighten interest" / "add gateways") come from the
same reading.

**D3 A scope is as full as its worst part.** `NebulaCapacity.OfScope` takes the maximum over the scope's
containers, and an unknown part never wins (`NebulaCapacity.Worse`). A scope is one interaction domain; taking the
mean would hide exactly the part that is about to cost everybody their tick, and letting an unreported part count
as empty would open a full station because one worker missed a telemetry post.

The public world has no scope row, so it has no scope-wide reading. Its containers are judged one at a time
(§3), which is right: the public world is many domains and a full cell is not a full world. A **grid** scope
(`ScopeKind.Grid`, NEB-239) is judged the same way: it is an unbounded procedural world whose row names only the
anchor chunk, its chunks are separate places, and "the world is full" is not a thing it can be.
`NebulaCapacity.IsPerContainer` is that distinction, and it is the only place the two kinds of scope differ here.

**D4 Unknown is not full.** `Known` is false until a worker reports the container. An unknown target is admitted,
so a mesh with no cost telemetry — an older worker, the first second after a start, a container nobody has
simulated yet — behaves exactly as it did before this item existed. `CapacitySaturation = 0` turns the signal off
the same way, everywhere, with one number.

**D5 The threshold is one mesh-wide number the orchestrator applies.** `NebulaConfig.CapacitySaturation`
(default **0.9**, `-nebula-capacity-saturation`, 0 = off) lives in both config copies, and the **orchestrator** is
what compares against it. What travels to the gateways is the resolved `AtCapacity` flag, not the threshold: two
gateways of one mesh answering differently about the same station because one was started with a different
argument is a failure mode with no upside.

## 2. Publishing it

**D6 The reading rides the lease row.** The orchestrator derives a reading per container each pass and writes it
with `IControlPlane.SetContainerCapacity(containerId, saturation, dominant, atCapacity)`; `LeaseInfo` gains
`HasCapacity`, `Saturation`, `Dominant`, `AtCapacity`, and the document gains three keys on a lease that has a
reading. A container has exactly one owning lease (`docs/cost-telemetry.md` D9), so the lease row *is* the
per-container row, and every role already mirrors it. A gateway therefore answers "is this target at capacity"
from memory, with no RPC to the orchestrator on the join path — the same way it learns a drain request or a
scope's state.

**D7 The capacity write does not stamp `UpdatedAt`.** That field is the mesh-wide idle clock the scope lifecycle
retires on (`docs/scope-lifecycle.md` D4). A reading published every pass would restamp every lease every half
second and no scope would ever be judged idle again. `LocalControlPlane.SetContainerCapacity` is the one write
that deliberately leaves the stamp alone.

**D8 Only a reading that moved is written.** The orchestrator keeps what it last published and writes again only
when `AtCapacity` flips, the dominant component changes, or the saturation moves by `CapacityPublishStep`
(0.02). The control-plane document is broadcast to every subscriber on every change; a per-pass rewrite of every
lease would be a broadcast per pass for a number that mostly does not move.

**D9 A target that stops reporting keeps its last reading.** When telemetry for a container expires, the
orchestrator publishes nothing rather than clearing the flag. Clearing would mean a worker that missed one
telemetry post opens a full station to everybody; keeping means a container that genuinely emptied is reopened one
telemetry cycle later than it could have been. The second is the cheaper mistake.

## 3. The refusal

**D10 One decision point, two ways in.** A join (`NebulaGateway.TryRequestSpawn`) and a prepared transfer
(`NebulaWorker.PrepareTransfer`) both ask `NebulaCapacity.Target(cp, scopeKey, containerId)` and both go through
the same hook. A travel service that sends a player to a station by `Hello` and one that walks an entity across a
seam get the same answer for the same reason.

**D11 Candidates with room are preferred before anybody is refused.** In the public world the gateway filters the
spawn candidates to those not at capacity and places the client in one of them; only when *every* candidate is
full is the hook consulted, with the least-bad candidate's reading. For a scope the candidates are the scope's
parts, and D3 has already decided the scope is full, so the filter cannot rescue it — which is the point: the
domain is authored and there is nowhere else inside it to go.

**D12 The refusal is typed, and it is not a retry hint.** `JoinRejectedMsg` gains `Code`
(`JoinRejectReason.None | AtCapacity | Denied`) and `Saturation` (f16). `Retry` keeps the meaning it has always
had — *this gateway* is the problem, reconnect and a load balancer will give you another one — and is **clear**
for a capacity refusal, because reconnecting on a one-second timer to a station that is full is a client that
hammers a full station. `NebulaClient` stops reconnecting, sets `JoinRejectReason` / `JoinRejectSaturation` and
raises `JoinRefused`; what happens next (a queue, another instance, a different scope) is the game's, which is
what "typed" is for. Both fields are appended and read tolerantly, so a message that ends early reads as
`None` / `0`: **D13 the protocol version is not bumped again**, 18 is already unreleased and already breaking.

**D14 A hold is a first-class answer.** `JoinHoldReason.AtCapacity` (5) is the docking queue: the gateway keeps
the client welcomed in `JoinState.Starting`, retries every few seconds and places it the moment room appears, with
no reconnect — exactly the mechanism a scope that is restoring already uses. A game that wants "docking queue:
position 43" returns `AdmissionDecision.Hold()` and shows its own queue; Nebula does not keep the queue (that is a
non-goal) and does not promise an order.

A transfer has nobody to hold: there is no join to keep open, so `PrepareTransfer` reads a hold as a refusal. The
returned `InstanceTransfer` comes back `Finished`, with `Error` set, `RejectReason` and `Saturation` typed, and
nothing has been sent to the destination worker or the client's gateway.

## 4. The hook

**D15 One static hook, consulted only when it matters.** `NebulaAdmission.Decide` is an
`AdmissionPolicy` — `AdmissionDecision (in AdmissionRequest)` — a static for the same reason
`ScopeLifecycle.ShouldRetire` is one: there is one answer per process and it must not depend on which gateway
object a game happened to reach. It is asked **only** for an arrival into a target that is at capacity, unless
`NebulaAdmission.AlwaysConsult` is set. A hook on the hot path of every join is a cost every mesh pays for a
feature few need, and the default — `RejectWhenAtCapacity` — needs no hook at all.

**D16 The request carries who is arriving, not a handle to ask again.** `AdmissionRequest` has the kind
(`Join` / `Transfer`), the scope key, the container, the `CapacityInfo`, and the client's id, identity, name, bot
flag and the game's own `Team` / `Tags` (`NebulaGateway.SetClientTag`). That is enough to whitelist a staff
account or a party member from the game's own tables, and it keeps the policy a pure function of its argument, so
it is unit testable without a mesh.

**D17 A policy that throws refuses.** The exception is counted in `NebulaAdmission.PolicyErrors`, logged once per
occurrence, and read as `Reject`. This is the opposite of the scope retire policy's failure mode and for the same
reason: there, a wrong answer destroys a world, so the safe answer is "keep"; here, a wrong answer collapses the
tick for everyone already inside, so the safe answer is "refuse".

## 5. Where the numbers surface

- `GET /api/cost` — every row gains `atCapacity`, and the document gains `capacitySaturation`.
- `GET /api/state` — the same on the `cost` rows; each `scopes` row gains `capacityKnown`, `saturation`,
  `dominant` and `atCapacity` (the scope-wide reading of D3); and the document gains `capacitySaturation`.
- The dashboard's **Container cost** table gains a **Full** column, and the **Scopes** card a **Capacity** column.
  Both are read-only: admitting a player into a full station by hand from a dashboard is not an operation, it is a
  policy, and the hook is the supported way to express one.

## Configuration

| Field | Default | Meaning |
| --- | --- | --- |
| `NebulaConfig.CapacitySaturation` | 0.9 | how full a target may get before the mesh calls it at capacity; 0 turns the signal off (`-nebula-capacity-saturation`) |
| `NebulaAdmission.Decide` | `RejectWhenAtCapacity` | the game's policy |
| `NebulaAdmission.AlwaysConsult` | false | ask the policy about every arrival, not only saturated ones |

## Not in scope

The queue itself: its order, its UI, priority rules and VIP lists are the game's (non-goals of NEB-236). Nebula
says "this target is full, and it is full of *this*", offers a hold that costs the client nothing, and gets out of
the way. Splitting a hot container across workers is cohesion-aware rebalancing (NEB-235); it can relieve a
container, and it never relieves a domain the game authored as one.
