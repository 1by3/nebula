using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// Conformance scenario 6 (see <c>docs/conformance-suite.md</c> §4 and <c>docs/state-history.md</c>):
    /// <c>StateAt(tick)</c> matches the recorded poses on the authority and on a ghost, within the documented bound.
    /// <para>
    /// Tier B (<see cref="ConformanceMesh"/>, two real <see cref="NebulaWorker"/>s): the ghost side must be the real
    /// replication path, because the whole claim of the item is that a ghost holder can answer for a recent tick and
    /// that its entries are tagged with the <i>owner's</i> tick. Tier A cannot do it (a <c>FakeWorker</c> implements
    /// the protocol, not <see cref="NetworkIdentity"/>); tier C cannot do it (no stream, so no ghost). The ring
    /// buffer's own contract - window, bounds, gap tolerance, monotonicity - is tier C and lives in
    /// <see cref="StateHistoryRingTests"/> below, where a mesh would only add ceremony.
    /// </para>
    /// </summary>
    [Category("Conformance")]
    public sealed class ConformanceStateHistoryTests
    {
        /// <summary>One variable in the history, one deliberately left out of it.</summary>
        private sealed class Pawn : NetworkBehaviour
        {
            [SyncHistory] public NetworkVariable<int> Stance = new NetworkVariable<int>();
            public NetworkVariable<int> Score = new NetworkVariable<int>();
        }

        private const int Window = 8;

        private ConformanceMesh _mesh;
        private Container _west, _east;
        private ushort _prefabId;

        [SetUp]
        public void SetUp()
        {
            _mesh = new ConformanceMesh(2);
            // Two touching boxes: west is w1's, east is w2's. An entity near the seam is ghosted to w2.
            _west = _mesh.AddStaticContainer("conformance-west", new Vector3(-10f, 0f, 0f), new Vector3(20f, 10f, 20f));
            _east = _mesh.AddStaticContainer("conformance-east", new Vector3(10f, 0f, 0f), new Vector3(20f, 10f, 20f));
            _mesh.SetOwner(_west, _mesh[0]);
            _mesh.SetOwner(_east, _mesh[1]);

            var prefab = new GameObject("pawn-prefab");
            prefab.AddComponent<NetworkIdentity>();
            var nt = prefab.AddComponent<NetworkTransform>();
            nt.Interpolate = false;        // a ghost snaps to the delivered pose, so nothing is time-dependent
            nt.UseUnreliableDeltas = false;
            prefab.AddComponent<Pawn>();
            _prefabId = _mesh.RegisterPrefab(prefab);

            StateHistory.WindowTicks = Window; // the mesh has no Initialize(), which is what reads the config knob
        }

        [TearDown]
        public void TearDown()
        {
            _mesh.Dispose(); // resets NebulaRuntime, which resets StateHistory.WindowTicks
        }

        /// <summary>Where the pawn stands at <paramref name="tick"/>: near the seam (x = -2), walking along z.</summary>
        private static Vector3 PositionAt(uint tick) => new Vector3(-2f, 0.5f, -4f + 0.25f * tick);

        private static Quaternion RotationAt(uint tick) => Quaternion.Euler(0f, 3f * tick, 0f);

        /// <summary>
        /// Move the pawn to where tick <paramref name="tick"/> leaves it, publish that tick from its owner and
        /// deliver the bytes. This is the authority's half of a tick plus the network, with no tick loop.
        /// </summary>
        private void RunTick(NetworkIdentity pawn, uint tick, bool deliver = true)
        {
            pawn.transform.position = PositionAt(tick);
            pawn.transform.rotation = RotationAt(tick);
            pawn.Motion.Velocity = new Vector3(0f, 0f, 0.25f);
            pawn.GetComponent<Pawn>().Stance.Value = (int)tick;
            pawn.GetComponent<Pawn>().Score.Value = (int)tick * 10;
            _mesh[0].PublishTick(tick);
            if (deliver) _mesh.Pump();
        }

        private NetworkIdentity Spawn() =>
            _mesh[0].SpawnServerDriven(_prefabId, _west, PositionAt(0), RotationAt(0));

        // ------------------------------------------------------------------ the exit criterion

        [Test]
        public void StateAtMatchesTheRecordedPosesOnTheAuthorityAndOnAGhost()
        {
            var pawn = Spawn();
            for (uint t = 1; t <= Window; t++) RunTick(pawn, t);

            var ghost = _mesh[1].Find(pawn.NetId);
            Assert.IsNotNull(ghost, "w2 holds a ghost of the pawn");
            Assert.IsFalse(ghost.HasAuthority, "and it is a ghost, not a second authority");

            for (uint t = 1; t <= Window; t++)
            {
                var authority = pawn.StateAt(t);
                Assert.IsTrue(authority.Available, $"authority has tick {t}");
                Assert.AreEqual(t, authority.Tick, "the entry is tagged with the tick it belongs to");
                Assert.IsTrue(authority.FromAuthority);
                Assert.AreEqual(0f, Vector3.Distance(PositionAt(t), authority.Position), 1e-4f, $"authority pose at {t}");
                Assert.AreEqual(0f, Quaternion.Angle(RotationAt(t), authority.Rotation), 0.05f, $"authority rotation at {t}");

                var recorded = ghost.StateAt(t);
                Assert.IsTrue(recorded.Available, $"the ghost holder has tick {t}");
                Assert.AreEqual(t, recorded.Tick, "the ghost entry carries the owner's tick, not the receiver's");
                Assert.IsFalse(recorded.FromAuthority, "and says it came from the stream");
                Assert.AreEqual(0f, Vector3.Distance(authority.Position, recorded.Position), 1e-3f, $"ghost pose at {t}");
                Assert.AreEqual(0f, Quaternion.Angle(authority.Rotation, recorded.Rotation), 0.1f, $"ghost rotation at {t}");
                Assert.AreEqual(authority.Epoch, recorded.Epoch, $"epoch at {t}");
                Assert.AreSame(_west, recorded.Container, $"container at {t}");
            }
        }

        [Test]
        public void AGhostIsExactlyOneTickBehindTheOwner()
        {
            var pawn = Spawn();
            for (uint t = 1; t <= 3; t++) RunTick(pawn, t);
            var ghost = _mesh[1].Find(pawn.NetId);

            // Tick 4 happens on the owner; its bytes have not been delivered yet. That is the moment a worker's own
            // tick 4 runs: its ghosts hold the owner's tick 3, never tick 4.
            RunTick(pawn, 4, deliver: false);
            Assert.AreEqual(4u, pawn.NewestAvailableTick, "the authority has this tick as soon as it has simulated it");
            Assert.AreEqual(3u, ghost.NewestAvailableTick, "the ghost is one tick behind: the documented staleness bound");
            Assert.IsFalse(ghost.StateAt(4).Available, "and the owner's newest tick is simply unavailable there");

            _mesh.Pump();
            Assert.AreEqual(4u, ghost.NewestAvailableTick, "delivery closes the gap");
            Assert.AreEqual(0f, Vector3.Distance(PositionAt(4), ghost.StateAt(4).Position), 1e-3f);
        }

        [Test]
        public void ATickOutsideTheWindowIsUnavailableRatherThanExtrapolated()
        {
            var pawn = Spawn();
            for (uint t = 1; t <= Window + 5; t++) RunTick(pawn, t);
            var ghost = _mesh[1].Find(pawn.NetId);

            foreach (var e in new[] { pawn, ghost })
            {
                Assert.AreEqual((uint)(Window + 5), e.NewestAvailableTick);
                Assert.AreEqual((uint)(Window + 5 - Window + 1), e.OldestAvailableTick, "the window holds exactly Window ticks");
                Assert.IsFalse(e.StateAt(e.OldestAvailableTick - 1).Available, "a tick that fell out of the ring");
                Assert.IsFalse(e.StateAt(e.NewestAvailableTick + 1).Available, "a tick that has not happened");
                Assert.IsFalse(e.StateAt(1).Available, "nothing near the edge is returned for a far-away tick");
                Assert.IsTrue(e.StateAt(e.OldestAvailableTick).Available);
                Assert.IsTrue(e.StateAt(e.NewestAvailableTick).Available);
            }
        }

        [Test]
        public void AMarkedVariableIsSnapshottedPerTickAndAnUnmarkedOneIsNot()
        {
            var pawn = Spawn();
            for (uint t = 1; t <= Window; t++) RunTick(pawn, t);
            var ghost = _mesh[1].Find(pawn.NetId);
            var authorityPawn = pawn.GetComponent<Pawn>();
            var ghostPawn = ghost.GetComponent<Pawn>();

            Assert.AreEqual(Window, authorityPawn.Stance.Value, "the live value has moved on");
            for (uint t = 1; t <= Window; t++)
            {
                Assert.IsTrue(pawn.StateAt(t).TryGetValue(authorityPawn.Stance, out int stance), $"marked variable at {t}");
                Assert.AreEqual((int)t, stance, "the value the authority held at that tick, not the value it holds now");
                Assert.IsFalse(pawn.StateAt(t).TryGetValue(authorityPawn.Score, out int _), "an unmarked variable is not snapshotted");
            }

            // The ghost snapshots what it had received by the tick it recorded; GhostVars rides the same reliable
            // link as the state, so within the bound it is the owner's value for that tick.
            Assert.IsTrue(ghost.StateAt(Window).TryGetValue(ghostPawn.Stance, out int ghostStance));
            Assert.AreEqual(Window, ghostStance, "the ghost's marked variable for the newest tick");
        }

        [Test]
        public void AWorkerAnswersForAnythingItHolds()
        {
            var pawn = Spawn();
            for (uint t = 1; t <= 4; t++) RunTick(pawn, t);

            Assert.IsTrue(_mesh[0].Instance.TryGetStateAt(pawn.NetId, 3, out var mine), "its own entity");
            Assert.IsTrue(mine.FromAuthority);
            Assert.IsTrue(_mesh[1].Instance.TryGetStateAt(pawn.NetId, 3, out var theirs), "an entity it only ghosts");
            Assert.IsFalse(theirs.FromAuthority);
            Assert.AreEqual(0f, Vector3.Distance(mine.Position, theirs.Position), 1e-3f);
            Assert.IsFalse(_mesh[1].Instance.TryGetStateAt(pawn.NetId + 999, 3, out _), "an entity it does not hold");
        }

        [Test]
        public void HistoryRecordedAsAGhostSurvivesGainingAuthority()
        {
            var pawn = Spawn();
            for (uint t = 1; t <= 4; t++) RunTick(pawn, t);
            var ghost = _mesh[1].Find(pawn.NetId);
            var before = ghost.StateAt(3);
            Assert.IsTrue(before.Available);

            _mesh[0].Transfer(pawn, _mesh[1]);
            _mesh.Pump();
            var now = _mesh[1].Find(pawn.NetId);
            Assert.IsTrue(now.HasAuthority, "w2 is the authority after the handover");

            var after = now.StateAt(3);
            Assert.IsTrue(after.Available, "the ticks it recorded while ghosting are still answerable");
            Assert.IsFalse(after.FromAuthority, "and still say they came from the stream");
            Assert.AreEqual(0f, Vector3.Distance(before.Position, after.Position), 1e-4f);
        }

        [Test]
        public void AZeroWindowRecordsNothing()
        {
            StateHistory.WindowTicks = 0;
            var pawn = Spawn();
            for (uint t = 1; t <= 4; t++) RunTick(pawn, t);
            Assert.IsNull(pawn.History, "nothing is allocated when recording is off");
            Assert.IsFalse(pawn.StateAt(3).Available);
            Assert.IsFalse(_mesh[1].Find(pawn.NetId).StateAt(3).Available);
        }
    }

    /// <summary>
    /// Conformance scenario 6, tier C: the ring buffer's own contract, driven directly. No mesh, because none of
    /// this is about what a worker sends - it is about what the ring promises whoever records into it.
    /// </summary>
    [Category("Conformance")]
    public sealed class StateHistoryRingTests
    {
        private static StateHistory Ring(int capacity) => new StateHistory(capacity, null);

        private static void Record(StateHistory h, uint tick, float x = 0f, bool authority = true) =>
            h.Record(tick, new Vector3(x, 0f, 0f), Quaternion.identity, Vector3.zero, null, 1, authority);

        [Test]
        public void AnEmptyRingAnswersNothing()
        {
            var h = Ring(8);
            Assert.IsFalse(h.HasEntries);
            Assert.IsFalse(h.TryGetStateAt(0, out _));
            Assert.IsFalse(h.TryGetStateAt(5, out _));
        }

        [Test]
        public void TheWindowIsTheCapacityAndTheOldestTickFallsOutOfIt()
        {
            var h = Ring(4);
            for (uint t = 1; t <= 4; t++) Record(h, t, t);
            Assert.AreEqual(1u, h.OldestAvailableTick);
            Assert.AreEqual(4u, h.NewestAvailableTick);

            Record(h, 5, 5);
            Assert.AreEqual(2u, h.OldestAvailableTick, "tick 1's slot was reused by tick 5");
            Assert.AreEqual(5u, h.NewestAvailableTick);
            Assert.IsFalse(h.TryGetStateAt(1, out _));
            Assert.IsTrue(h.TryGetStateAt(2, out var second));
            Assert.AreEqual(2f, second.Position.x, 1e-6f);
        }

        [Test]
        public void AnOutOfOrderOrReplayedTickIsIgnored()
        {
            var h = Ring(8);
            Record(h, 5, 5f);
            Record(h, 4, 99f);
            Record(h, 5, 99f);
            Assert.AreEqual(5u, h.NewestAvailableTick);
            Assert.IsTrue(h.TryGetStateAt(5, out var s));
            Assert.AreEqual(5f, s.Position.x, 1e-6f, "the replay did not rewrite the entry");
            Assert.IsFalse(h.TryGetStateAt(4, out _), "and the late tick was not inserted behind the newest");
        }

        [Test]
        public void AGapInsideTheWindowIsAnsweredByTheNearestRecordedTick()
        {
            var h = Ring(16);
            Record(h, 1, 1f);
            Record(h, 6, 6f);   // ticks 2..5 were never recorded (nothing moved, so nothing was streamed)
            Record(h, 7, 7f);

            Assert.IsTrue(h.TryGetStateAt(4, out var near));
            Assert.AreEqual(6u, near.Tick, "the entry says which tick it really is; nothing is interpolated");
            Assert.AreEqual(6f, near.Position.x, 1e-6f);
            Assert.IsTrue(h.TryGetStateAt(2, out var back));
            Assert.AreEqual(1u, back.Tick, "the later side is preferred, but the earlier one is used when it is nearer");
        }

        [Test]
        public void GapToleranceNeverReachesOutsideTheWindow()
        {
            var h = Ring(64);
            for (uint t = 20; t <= 24; t++) Record(h, t, t);
            Assert.IsFalse(h.TryGetStateAt(19, out _), "one before the oldest is outside the window");
            Assert.IsFalse(h.TryGetStateAt(25, out _), "one after the newest has not happened");
            Assert.IsFalse(h.TryGetStateAt(1, out _));
        }

        [Test]
        public void TheCapacityIsClampedAndTheDefaultIsPinned()
        {
            Assert.AreEqual(32, StateHistory.DefaultWindowTicks, "the documented default window");
            Assert.AreEqual(4, StateHistory.GapToleranceTicks, "the documented gap tolerance");
            Assert.AreEqual(StateHistory.MaxWindowTicks, Ring(StateHistory.MaxWindowTicks * 4).Capacity);
            Assert.AreEqual(1, Ring(0).Capacity, "a ring always holds at least one tick");
        }

        [Test]
        public void RecordingDoesNotAllocateOnceTheRingIsWarm()
        {
            var h = Ring(8);
            for (uint t = 1; t <= 8; t++) Record(h, t, t);
            long before = System.GC.GetAllocatedBytesForCurrentThread();
            for (uint t = 9; t <= 200; t++) Record(h, t, t);
            long after = System.GC.GetAllocatedBytesForCurrentThread();
            Assert.AreEqual(0, after - before, "a warm ring records a tick without allocating");
        }

        [Test]
        public void ClearForgetsEverything()
        {
            var h = Ring(8);
            for (uint t = 1; t <= 4; t++) Record(h, t, t);
            h.Clear();
            Assert.IsFalse(h.HasEntries);
            Assert.IsFalse(h.TryGetStateAt(3, out _));
        }

        [Test]
        public void AnOriginShiftMovesWorldEntries()
        {
            var h = Ring(8);
            Record(h, 1, 10f);
            h.Shift(new Vector3(-100f, 0f, 0f));
            Assert.IsTrue(h.TryGetStateAt(1, out var s));
            Assert.AreEqual(-90f, s.Position.x, 1e-4f);
        }

        [Test]
        public void EveryTickInAFullRingIsDistinct()
        {
            var h = Ring(32);
            var seen = new HashSet<uint>();
            for (uint t = 1; t <= 32; t++) Record(h, t, t);
            for (uint t = 1; t <= 32; t++)
            {
                Assert.IsTrue(h.TryGetStateAt(t, out var s), $"tick {t}");
                Assert.AreEqual(t, s.Tick);
                Assert.AreEqual((float)t, s.Position.x, 1e-6f);
                Assert.IsTrue(seen.Add(s.Tick), "no two ticks share an entry");
            }
        }
    }
}
