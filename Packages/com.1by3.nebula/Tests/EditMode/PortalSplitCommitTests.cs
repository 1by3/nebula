using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// Conformance scenario 69 (<c>docs/portals.md</c> D35, D36): the far scope is forced onto the other worker, and entities cross into a room the
    /// near worker does not own. The link's readiness includes the far worker's answer over the worker link; a crossing commits into the far container on the
    /// tick the root passes, the ordinary handover sends authority to the far worker on the same tick with nothing held at the face and no correction of more
    /// than 0.1 m; a carrier whose riders another worker simulates is refused whole with a reason and never half-moved; every wait is reported while it lasts.
    /// Tier B: two real workers on the in-process mesh (<see cref="PortalHarness"/>), no physics scene (the physics half is
    /// <c>PortalSplitCommitPlayTests</c>, in play mode).
    /// </summary>
    [Category("Conformance")]
    public sealed class PortalSplitCommitTests
    {
        private PortalHarness _h;
        private readonly List<(NetworkIdentity entity, PortalLink link, PortalRefusal why)> _refused = new List<(NetworkIdentity, PortalLink, PortalRefusal)>();
        private ushort _shipPrefab;

        [SetUp]
        public void SetUp()
        {
            _h = new PortalHarness(2) { HallOnSecondWorker = true };
            _refused.Clear();
            _h.Door.Refused += (entity, link, why) => _refused.Add((entity, link, why));
            var ship = new GameObject("ship-prefab");
            ship.AddComponent<NetworkIdentity>();
            var hold = ship.AddComponent<Container>();
            hold.ContainerId = "hold";
            hold.Size = new Vector3(3f, 2f, 3f);
            hold.Center = new Vector3(0f, 1f, 0f);
            ship.AddComponent<NetworkTransform>();
            _shipPrefab = _h.Mesh.RegisterPrefab(ship);
        }

        [TearDown] public void TearDown() => _h.Dispose();

        private System.Collections.IDictionary Transfers =>
            (System.Collections.IDictionary)typeof(NebulaWorker).GetField("_instanceTransfers", PortalHarness.Hidden).GetValue(_h.W.Instance);

        /// <summary>The copy of an entity that is authoritative, wherever the mesh holds it.</summary>
        private NetworkIdentity Authority(ulong netId)
        {
            var near = _h.W.Find(netId);
            return near != null && near.HasAuthority ? near : _h.Other.Find(netId);
        }

        private void Peer(bool connected)
        {
            var peer = _h.W.PeersById[_h.Other.Id];
            peer.GetType().GetField("HelloReceived").SetValue(peer, connected);
        }

        // ------------------------------------------------------------------------------------------ the crossing

        [Test]
        public void APawnAndAServerOwnedCrateCrossIntoTheOtherWorkersRoomEachThroughOneHandoverWithNothingHeld()
        {
            var link = _h.OpenDoor();
            Assert.That(NebulaDiagnostics.SplitPortalLinks, Is.EqualTo(1), "the hall is the other worker's");
            var pawn = _h.Player;
            var crate = _h.W.SpawnServerDriven(_h.PawnPrefab, _h.Street, PortalHarness.At(1.2f, -1f), Quaternion.identity);
            pawn.transform.position = PortalHarness.At(-1.2f, -1f);
            _h.TickAll(2);
            ulong pawnId = pawn.NetId, crateId = crate.NetId;
            Assert.That(crate.OwnerClientId, Is.EqualTo(0UL), "a crate with no owner");
            int transfers = _h.Mesh.DeliveredOf(MsgId.AuthorityTransfer, _h.Other.Id).Count;
            int crossings = NebulaDiagnostics.CrossWorkerPortalCrossings;
            int refusals = NebulaDiagnostics.PortalRefusals;
            var crossedAt = new Dictionary<ulong, int>();
            float worst = 0f;
            Vector3 lastSeen = default;

            for (int i = 0; i < 24; i++)
            {
                float z = -1f + 0.1f * i; // 1 m/s... a metre every ten ticks; the plane is at z = 0
                var pawnAt = PortalHarness.At(-1.2f, z);
                var crateAt = PortalHarness.At(1.2f, z);
                Authority(pawnId).transform.position = pawnAt;
                Authority(crateId).transform.position = crateAt;
                _h.TickAll();
                foreach (var (id, expected) in new[] { (pawnId, pawnAt), (crateId, crateAt) })
                {
                    var now = Authority(id);
                    worst = Mathf.Max(worst, Vector3.Distance(now.transform.position, expected));
                    if (now.InstanceId == PortalHarness.Private && !crossedAt.ContainsKey(id))
                    {
                        crossedAt[id] = i;
                        Assert.That(now.HasAuthority, Is.True);
                        Assert.That(ReferenceEquals(now, _h.Other.Find(id)), Is.True, "the same tick: authority already followed the container to the worker that owns the hall");
                        Assert.That(now.Container, Is.SameAs(_h.Hall));
                        lastSeen = now.transform.position;
                    }
                }
            }

            Assert.That(crossedAt.Count, Is.EqualTo(2), "both crossed");
            Assert.That(crossedAt[pawnId], Is.EqualTo(11), "on the tick the root passed the plane (z = 0.1 m), not before and not after");
            Assert.That(crossedAt[crateId], Is.EqualTo(11));
            Assert.That(worst, Is.LessThanOrEqualTo(0.1f), "nothing was put back, snapped or held: the pose after every tick is the one it was driven to");
            Assert.That(_h.Mesh.DeliveredOf(MsgId.AuthorityTransfer, _h.Other.Id).Count - transfers, Is.EqualTo(2), "one handover for each of the two");
            Assert.That(NebulaDiagnostics.CrossWorkerPortalCrossings - crossings, Is.EqualTo(2));
            Assert.That(NebulaDiagnostics.PortalRefusals, Is.EqualTo(refusals));
            Assert.That(_refused, Is.Empty);
            Assert.That(Transfers.Count, Is.EqualTo(0), "no per-entity preparation, so nothing waited at the face");
            Assert.That(_h.W.Find(pawnId).HasAuthority, Is.False, "the near worker no longer simulates either");
            Assert.That(link.State, Is.EqualTo(PortalState.Open));
        }

        [Test]
        public void TheFarWorkerHoldsTheStateTheNearWorkerHandedOverAtTheCrossingTick()
        {
            _h.OpenDoor();
            var crate = _h.W.SpawnServerDriven(_h.PawnPrefab, _h.Street, PortalHarness.At(0.5f, -0.4f), Quaternion.Euler(0f, 30f, 0f));
            ulong id = crate.NetId;
            uint epoch = crate.Epoch;
            _h.TickAll(2);
            crate.transform.position = PortalHarness.At(0.5f, 0.1f);
            _h.TickAll();
            var arrived = _h.Other.Find(id);
            Assert.That(arrived.HasAuthority, Is.True);
            Assert.That(arrived.InstanceId, Is.EqualTo(PortalHarness.Private));
            Assert.That(Vector3.Distance(arrived.transform.position, PortalHarness.At(0.5f, 0.1f)), Is.LessThan(0.01f), "at the pose it crossed at");
            Assert.That(Quaternion.Angle(arrived.transform.rotation, Quaternion.Euler(0f, 30f, 0f)), Is.LessThan(0.1f));
            Assert.That(arrived.Epoch, Is.GreaterThan(epoch), "the crossing bumped the epoch and the handover carried it");
        }

        // ------------------------------------------------------------------------------------------ every wait has a reason

        [Test]
        public void WhileTheFarSideWaitsOnAnotherWorkerTheReasonIsReportedAndAnEntityMeetingTheFaceIsToldWhy()
        {
            _h.Door.PartyOf = entity => PortalHarness.Owner; // a crate with no owner belongs to the owner's party here
            Peer(false);
            var link = _h.Door.Open(PortalHarness.Owner, new[] { PortalHarness.Client });
            _h.Ticks(3);
            Assert.That(link.State, Is.EqualTo(PortalState.Preparing));
            Assert.That(link.Wait, Is.EqualTo(PortalWait.FarWorkerHandshake), "the hall is leased to a worker that has not answered");
            var rows = new List<NebulaWorker.PortalBinding>();
            _h.W.Instance.CopyPortalBindings(rows);
            Assert.That(rows[0].Wait, Is.EqualTo(PortalWait.FarWorkerHandshake), "and the telemetry row says so, for the dashboard");
            Assert.That(rows[0].Split, Is.True);

            var crate = _h.W.SpawnServerDriven(_h.PawnPrefab, _h.Street, PortalHarness.At(0f, -0.3f), Quaternion.identity);
            _h.Tick();
            crate.transform.position = PortalHarness.At(0f, 0.2f);
            int refusals = NebulaDiagnostics.PortalRefusals;
            _h.Tick();
            Assert.That(crate.InstanceId, Is.EqualTo(0UL), "not through a link that is not open");
            Assert.That(_refused.Count, Is.EqualTo(1));
            Assert.That(_refused[0].link, Is.SameAs(link), "the refusal names the link, whose Wait is the reason");
            Assert.That(_refused[0].why, Is.EqualTo(PortalRefusal.LinkNotOpen));
            Assert.That(NebulaDiagnostics.PortalRefusals, Is.EqualTo(refusals + 1));

            Peer(true); // the far worker is reachable: it answers, the member warms, the link opens
            _h.Ticks(3);
            Assert.That(link.Wait, Is.EqualTo(PortalWait.ClientWarming));
            _h.Warm(link);
            _h.Tick();
            Assert.That(link.State, Is.EqualTo(PortalState.Open));
        }

        // ------------------------------------------------------------------------------------------ a carrier with riders split across workers

        [Test]
        public void ACarrierWhoseRidersAnotherWorkerSimulatesIsRefusedWholeAndNothingIsHalfMoved()
        {
            _h.OpenDoor();
            var ship = _h.W.SpawnServerDriven(_shipPrefab, _h.Street, PortalHarness.At(0f, -3f), Quaternion.identity);
            var near = _h.W.SpawnServerDriven(_h.PawnPrefab, ship.Carried, ship.transform.position + new Vector3(0.5f, 0.2f, 0.5f), Quaternion.identity);
            var far = _h.W.SpawnServerDriven(_h.PawnPrefab, ship.Carried, ship.transform.position + new Vector3(-0.5f, 0.2f, -0.5f), Quaternion.identity);
            _h.TickAll(2);
            far.HasAuthority = false; // this worker holds the second rider only as a ghost: another worker simulates it
            int splits = NebulaDiagnostics.PortalSplitRefusals;
            int refusals = NebulaDiagnostics.PortalRefusals;

            ship.transform.position = PortalHarness.At(0f, 1f);
            _h.Tick();

            Assert.That(ship.Container, Is.SameAs(_h.Street), "the carrier stays");
            Assert.That(near.Container, Is.SameAs(ship.Carried), "and so does the rider this worker does simulate: nobody is half-moved");
            Assert.That(far.Container, Is.SameAs(ship.Carried));
            Assert.That(ship.InstanceId, Is.EqualTo(0UL));
            Assert.That(_refused.Count, Is.EqualTo(1));
            Assert.That(_refused[0].entity, Is.SameAs(ship));
            Assert.That(_refused[0].why, Is.EqualTo(PortalRefusal.SplitAboard));
            Assert.That(NebulaDiagnostics.PortalSplitRefusals, Is.EqualTo(splits + 1));
            Assert.That(NebulaDiagnostics.PortalRefusals, Is.EqualTo(refusals + 1), "a split refusal is a refusal");

            // Once the rider is this worker's again the whole crew crosses.
            far.HasAuthority = true;
            ship.transform.position = PortalHarness.At(0f, -0.5f);
            _h.Tick();
            ship.transform.position = PortalHarness.At(0f, 1f);
            _h.Tick();
            Assert.That(ship.InstanceId, Is.EqualTo(PortalHarness.Private));
            Assert.That(near.InstanceId, Is.EqualTo(PortalHarness.Private));
            Assert.That(far.InstanceId, Is.EqualTo(PortalHarness.Private));
        }

        [Test]
        public void ACarrierWhoseInteriorIsLeasedToAnotherWorkerIsRefusedToo()
        {
            _h.OpenDoor();
            var ship = _h.W.SpawnServerDriven(_shipPrefab, _h.Street, PortalHarness.At(0f, -3f), Quaternion.identity);
            _h.TickAll(2);
            ContainerRegistry.ApplyLease(ship.Carried.ContainerId, _h.Other.Id, _h.Other.Index, 9, LeaseState.Pinned);
            Assume.That(ship.Carried.IsPinned, Is.True);

            ship.transform.position = PortalHarness.At(0f, 1f);
            _h.Tick();

            Assert.That(ship.Container, Is.SameAs(_h.Street));
            Assert.That(_refused.Count, Is.EqualTo(1));
            Assert.That(_refused[0].why, Is.EqualTo(PortalRefusal.SplitAboard));
        }
    }
}
