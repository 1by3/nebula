using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>
    /// NEB-429 (<c>docs/portals.md</c> D18): everything an entity carries in time goes through the link pose with it, at a door turned against
    /// its host. On the worker: the pose in double, the reported velocity and a body's linear and angular velocity, and the recorded
    /// history, rewritten into the destination's space and container. On a copy the process does not simulate: the container change and the
    /// pose composed through the link apply in one step, the interpolation buffer holds one space and a replica glides across the face, and
    /// the local pawn's prediction history is rewritten, so reconciling around a crossing finds no correction. Tier B, on <see cref="PortalHarness"/>.
    /// </summary>
    [Category("Conformance")]
    public sealed class PortalCarryTests
    {
        /// <summary>A door turned 90° about y and tilted 20°: nothing lines up by luck.</summary>
        private static readonly Quaternion Turn = Quaternion.Euler(0f, 90f, 0f) * Quaternion.Euler(20f, 0f, 0f);

        private struct NoInput : INetworkInput
        {
            public void Serialize(NetworkWriter w) { }
            public void Deserialize(NetworkReader r) { }
        }

        private sealed class TestPawn : PredictedBehaviour<NoInput>
        {
            protected override NoInput GatherInput() => default;
            protected override void Simulate(uint tick, in NoInput input, float deltaTime) { }
        }

        private PortalHarness _h;
        private ushort _bodyPrefab, _movingPrefab;

        [SetUp]
        public void SetUp()
        {
            _h = new PortalHarness(1);
            StateHistory.WindowTicks = 16;
            var body = new GameObject("body-prefab");
            body.AddComponent<NetworkIdentity>();
            body.AddComponent<Rigidbody>().useGravity = false;
            _bodyPrefab = _h.Mesh.RegisterPrefab(body);
            var moving = new GameObject("moving-prefab");
            moving.AddComponent<NetworkIdentity>();
            moving.AddComponent<NetworkTransform>();
            _movingPrefab = _h.Mesh.RegisterPrefab(moving);
            _h.Door.transform.rotation = Turn;
        }

        [TearDown] public void TearDown() => _h.Dispose();

        /// <summary>A point <paramref name="local"/> in the door's own axes, in the street's.</summary>
        private static Vector3 InStreet(Vector3 local) => PortalHarness.DoorAt + Turn * local;

        /// <summary>The same point in the room, whose axes are upright.</summary>
        private static Vector3 InRoom(Vector3 local) => PortalHarness.DoorAt + local;

        private static Double3 Absolute(NetworkIdentity entity) => ContainerRegistry.ToAbsolutePrecise(entity.ScopePositionPrecise, entity.InstanceId);

        private NetworkIdentity Spawn(ushort prefab, Vector3 local)
        {
            var entity = _h.W.SpawnServerDriven(prefab, _h.Street, InStreet(local), Turn);
            entity.transform.SetPositionAndRotation(InStreet(local), Turn);
            return entity;
        }

        // ------------------------------------------------------------------------------------------ the worker

        [Test]
        public void AHistoryReadAtTheCrossingTickAnswersInTheDestinationsSpace()
        {
            var link = _h.OpenDoor();
            Assume.That(Quaternion.Angle(link.Turn, Turn), Is.LessThan(0.01f));
            var crate = Spawn(_movingPrefab, new Vector3(0.5f, 1f, -1.5f));
            crate.Motion.Velocity = Turn * new Vector3(0f, 0f, 3f);
            _h.Tick();
            uint t0 = _h.TickNumber;
            crate.transform.position = InStreet(new Vector3(0.5f, 1f, -0.5f));
            _h.Tick();
            uint t1 = _h.TickNumber;
            var before = crate.StateAt(t0);
            Assume.That(before.Available && before.Container == _h.Street, "recorded in the street while it stood there");
            Assert.That((before.Position - InStreet(new Vector3(0.5f, 1f, -1.5f))).magnitude, Is.LessThan(1e-3f));

            crate.transform.position = InStreet(new Vector3(0.5f, 1f, 1f));
            _h.Tick();
            uint t2 = _h.TickNumber;
            Assert.That(crate.Container, Is.SameAs(_h.Hall), "it crossed on t2");

            var at0 = crate.StateAt(t0);
            var at1 = crate.StateAt(t1);
            var atCrossing = crate.StateAt(t2);
            Assert.That(atCrossing.Available && at1.Available && at0.Available);
            foreach (var state in new[] { at0, at1, atCrossing })
            {
                Assert.That(state.Container, Is.SameAs(_h.Hall), "filed under the container it went into, whichever side of the crossing the tick is");
                Assert.That(Quaternion.Angle(state.Rotation, Quaternion.identity), Is.LessThan(0.05f), "its rotation in the room's axes");
                Assert.That((state.Velocity - new Vector3(0f, 0f, 3f)).magnitude, Is.LessThan(1e-3f), "and its velocity");
            }
            Assert.That((at0.Position - InRoom(new Vector3(0.5f, 1f, -1.5f))).magnitude, Is.LessThan(2e-3f), "the pose of two ticks before, in the room's space");
            Assert.That((at1.Position - InRoom(new Vector3(0.5f, 1f, -0.5f))).magnitude, Is.LessThan(2e-3f));
            Assert.That((atCrossing.Position - InRoom(new Vector3(0.5f, 1f, 1f))).magnitude, Is.LessThan(2e-3f), "and the pose it crossed at");
            Assert.That((atCrossing.Position - crate.transform.position).magnitude, Is.LessThan(2e-3f), "which is where it is");

            // A later tick is recorded where it stands, in the same space.
            crate.transform.position = InRoom(new Vector3(0.5f, 1f, 1.5f));
            _h.Tick();
            var next = crate.StateAt(_h.TickNumber);
            Assert.That((next.Position - InRoom(new Vector3(0.5f, 1f, 1.5f))).magnitude, Is.LessThan(2e-3f));
        }

        [Test]
        public void ABodysVelocityAndAngularVelocityAreTurnedThroughATurnedPortalBothWays()
        {
            _h.OpenDoor();
            var crate = Spawn(_bodyPrefab, new Vector3(0.3f, 1f, -1.5f));
            var body = crate.GetComponent<Rigidbody>();
            var linear = new Vector3(0.7f, -0.2f, 4f);
            var angular = new Vector3(1f, 2f, 3f);
            body.linearVelocity = Turn * linear;
            body.angularVelocity = Turn * angular;
            crate.Motion.Velocity = body.linearVelocity;
            _h.Tick();
            crate.transform.position = InStreet(new Vector3(0.3f, 1f, 1f));
            _h.Tick();
            Assert.That(crate.Container, Is.SameAs(_h.Hall));
            Assert.That((body.linearVelocity - linear).magnitude, Is.LessThan(1e-3f), "its body's linear velocity in the room's axes: the spike's crate dove into the floor");
            Assert.That((body.angularVelocity - angular).magnitude, Is.LessThan(1e-3f), "and its angular velocity");
            Assert.That((crate.Motion.Velocity - linear).magnitude, Is.LessThan(1e-3f), "and the velocity it reports");
            Assert.That(Quaternion.Angle(crate.transform.rotation, Quaternion.identity), Is.LessThan(0.05f));

            // Out again: back into the street's axes.
            _h.Tick();
            crate.transform.position = InRoom(new Vector3(0.3f, 1f, -0.4f));
            _h.Tick();
            Assert.That(crate.Container, Is.SameAs(_h.Street));
            Assert.That((body.linearVelocity - Turn * linear).magnitude, Is.LessThan(1e-3f));
            Assert.That((body.angularVelocity - Turn * angular).magnitude, Is.LessThan(1e-3f));
            Assert.That(Quaternion.Angle(crate.transform.rotation, Turn), Is.LessThan(0.05f), "turned back");
        }

        [Test]
        public void ACarrierAndItsRidersHaveTheirVelocityAndHistoryTurnedToo()
        {
            _h.OpenDoor();
            var ship = new GameObject("ship-prefab");
            ship.AddComponent<NetworkIdentity>();
            var hold = ship.AddComponent<Container>();
            hold.ContainerId = "hold";
            hold.Size = new Vector3(3f, 2f, 3f);
            hold.Center = new Vector3(0f, 1f, 0f);
            ship.AddComponent<NetworkTransform>();
            ushort shipPrefab = _h.Mesh.RegisterPrefab(ship);
            var carrier = _h.W.SpawnServerDriven(shipPrefab, _h.Street, InStreet(new Vector3(0f, 0f, -3f)), Turn);
            var rider = _h.W.SpawnServerDriven(_h.PawnPrefab, carrier.Carried, carrier.transform.position + Turn * new Vector3(0f, 0.3f, 0f), Turn);
            carrier.Motion.Velocity = Turn * new Vector3(0f, 0f, 2f);
            rider.Motion.Velocity = Turn * new Vector3(0f, 0f, 2f);
            _h.Ticks(2);
            uint before = _h.TickNumber;
            var seat = rider.History.HasEntries ? rider.StateAt(before) : default;
            Assume.That(seat.Available);
            carrier.transform.position = InStreet(new Vector3(0f, 0f, 1f));
            _h.Tick();
            Assert.That(carrier.Container, Is.SameAs(_h.Hall));
            Assert.That(rider.InstanceId, Is.EqualTo(PortalHarness.Private));
            Assert.That((rider.Motion.Velocity - new Vector3(0f, 0f, 2f)).magnitude, Is.LessThan(1e-3f), "a seated rider's velocity is turned with its carrier's");
            Assert.That((carrier.Motion.Velocity - new Vector3(0f, 0f, 2f)).magnitude, Is.LessThan(1e-3f));
            var then = rider.StateAt(before);
            Assert.That(then.Available);
            Assert.That((then.Position - InRoom(new Vector3(0f, 0.3f, -3f))).magnitude, Is.LessThan(2e-3f), "and the rider's recorded history is in the room's space");
        }

        // ------------------------------------------------------------------------------------------ a copy that is not simulated here

        private NetworkIdentity Replica(ulong netId, uint epoch)
        {
            var go = new GameObject("replica");
            _h.Objects.Add(go);
            go.AddComponent<NetworkIdentity>();
            go.AddComponent<NetworkTransform>();
            var id = go.GetComponent<NetworkIdentity>();
            id.Initialize();
            id.NetId = netId;
            id.Epoch = epoch;
            id.HasAuthority = false;
            id.InvokeSpawn();
            id.SetContainer(_h.Street);
            return id;
        }

        [TestCase(true, TestName = "AReplicaGlidesAcrossTheFaceInOneStep_PortalKnown")]
        [TestCase(false, TestName = "AReplicaGlidesAcrossTheFaceInOneStep_OnlyTheInstancesPoseIsKnown")]
        public void AReplicaGlidesAcrossTheFaceInOneStep(bool portalKnown)
        {
            _h.OpenDoor();
            var authority = Spawn(_movingPrefab, new Vector3(0.5f, 1f, -2.5f));
            var replica = Replica(authority.NetId, authority.Epoch);
            var zs = new[] { -2.5f, -2f, -1.5f, -1f, -0.5f, 0f };
            var ticks = new List<uint>();
            foreach (float z in zs)
            {
                authority.transform.position = InStreet(new Vector3(0.5f, 1f, z));
                _h.Tick();
                Assert.That(authority.HasReplicationState, "the worker prepared this tick's state to publish");
                replica.ReceiveState(_h.TickNumber, 1, authority.ReplicationState);
                ticks.Add(_h.TickNumber);
            }
            replica.RemoteTick(ticks[ticks.Count - 2] + 0.0); // drawn in the street, a tick behind
            Assert.That(replica.Container, Is.SameAs(_h.Street));
            Assert.That((replica.transform.position - InStreet(new Vector3(0.5f, 1f, -0.5f))).magnitude, Is.LessThan(2e-3f));

            // Through the face: the worker's entity is now in the room, and the replica hears so.
            authority.transform.position = InStreet(new Vector3(0.5f, 1f, 0.5f));
            _h.Tick();
            Assert.That(authority.Container, Is.SameAs(_h.Hall));
            Assert.That(authority.HasReplicationState);
            uint crossing = _h.TickNumber;
            if (!portalKnown) PortalHarness.Disable(_h.Door); // no live link: only the pose every process learns from the instance's lease row
            var pose = new List<Vector3>();
            replica.ContainerChanged += (from, to) => pose.Add(replica.transform.position);
            replica.ReceiveState(crossing, 1, authority.ReplicationState);

            // Its buffer is in one space: drawn between the last two samples it is where the path was, whichever scope it is drawn in.
            replica.RemoteTick(ticks[ticks.Count - 1] + 0.5);
            Assert.That(replica.Container, Is.SameAs(_h.Hall), "in the room's container from the first frame it is drawn there");
            var expected = InRoom(new Vector3(0.5f, 1f, 0.25f));
            TestContext.WriteLine($"drawn at {replica.transform.position}, the path is at {expected}");
            Assert.That((replica.transform.position - expected).magnitude, Is.LessThan(0.01f), "on the path, not 0.3 to 0.9 m beside it");
            Assert.That(Quaternion.Angle(replica.transform.rotation, Quaternion.identity), Is.LessThan(0.1f), "facing as it did, in the room's axes");

            // The container change and the pose composed through the link arrived in the same step: at the moment the container changed, the
            // replica was already at the link-composed pose, not at its old numbers read in the new container.
            Assert.That(pose.Count, Is.EqualTo(1));
            Assert.That((pose[0] - InRoom(new Vector3(0.5f, 1f, -0.5f))).magnitude, Is.LessThan(2e-3f), "where it was drawn before, through the link");

            // And later frames carry on along it.
            replica.RemoteTick(crossing);
            Assert.That((replica.transform.position - InRoom(new Vector3(0.5f, 1f, 0.5f))).magnitude, Is.LessThan(0.01f));
        }

        [Test]
        public void AGhostsRecordedHistoryIsRewrittenWhenTheCrossingReachesIt()
        {
            _h.OpenDoor();
            var authority = Spawn(_movingPrefab, new Vector3(0.5f, 1f, -1.5f));
            var ghost = Replica(authority.NetId, authority.Epoch);
            _h.Tick();
            Assert.That(authority.HasReplicationState);
            ghost.ReceiveState(_h.TickNumber, 1, authority.ReplicationState);
            uint t0 = _h.TickNumber;
            var then = ghost.StateAt(t0);
            Assume.That(then.Available && then.Container == _h.Street);
            authority.transform.position = InStreet(new Vector3(0.5f, 1f, 1f));
            _h.Tick();
            Assert.That(authority.HasReplicationState);
            ghost.ReceiveState(_h.TickNumber, 1, authority.ReplicationState);
            var now = ghost.StateAt(t0);
            Assert.That(now.Container, Is.SameAs(_h.Hall));
            Assert.That((now.Position - InRoom(new Vector3(0.5f, 1f, -1.5f))).magnitude, Is.LessThan(2e-3f), "a ghost holder answers in the room's space for a tick before the crossing");
            var crossing = ghost.StateAt(_h.TickNumber);
            Assert.That((crossing.Position - InRoom(new Vector3(0.5f, 1f, 1f))).magnitude, Is.LessThan(2e-3f));
        }

        [Test]
        public void ThePawnsCorrectionAtACrossingIsUnderTenCentimetres()
        {
            var link = _h.OpenDoor();
            // The worker's pawn walks in and the client's predicted copy of it follows, tick for tick.
            var go = new GameObject("local-pawn");
            _h.Objects.Add(go);
            go.AddComponent<TestPawn>();
            var pawn = go.GetComponent<NetworkIdentity>();
            pawn.Initialize();
            pawn.NetId = _h.Player.NetId;
            pawn.Epoch = _h.Player.Epoch;
            pawn.IsLocalPlayer = true;
            pawn.InvokeSpawn();
            pawn.SetContainer(_h.Street);
            var predicted = pawn.Predicted;
            Assert.That(predicted, Is.Not.Null);
            var input = new NetworkWriter();

            float speed = 2f;
            float z = -1.6f;
            uint crossing = 0;
            var local = new Dictionary<uint, Vector3>();
            for (int i = 0; i < 4; i++)
            {
                _h.Player.transform.SetPositionAndRotation(InStreet(new Vector3(0f, 1f, z)), Turn);
                _h.Tick();
                pawn.transform.SetPositionAndRotation(InStreet(new Vector3(0f, 1f, z)), Turn);
                predicted.ClientPredictTick(_h.TickNumber, input);
                z += 0.4f;
            }
            // The tick it passed the face: the worker commits; the client has predicted three more in the street.
            _h.Player.transform.SetPositionAndRotation(InStreet(new Vector3(0f, 1f, 0.2f)), Turn);
            _h.Tick();
            crossing = _h.TickNumber;
            Assert.That(_h.Player.Container, Is.SameAs(_h.Hall));
            pawn.transform.SetPositionAndRotation(InStreet(new Vector3(0f, 1f, 0.2f)), Turn);
            predicted.ClientPredictTick(crossing, input);
            for (int i = 1; i <= 3; i++)
            {
                pawn.transform.SetPositionAndRotation(InStreet(new Vector3(0f, 1f, 0.2f + 0.5f * i)), Turn);
                predicted.ClientPredictTick(crossing + (uint)i, input);
            }
            var worker = new NetworkWriter();
            worker.WriteVector3(_h.Player.LocalPosition);
            worker.WriteQuaternion(_h.Player.LocalRotation);
            worker.WriteVector3(_h.Player.Motion.Velocity);

            // The owner state names the room's container: the client moves there first (NebulaClient.OnOwnerState), then reconciles the tick.
            pawn.SetContainer(_h.Hall);
            Assert.That(pawn.Container, Is.SameAs(_h.Hall));
            Assert.That((pawn.transform.position - InRoom(new Vector3(0f, 1f, 1.7f))).magnitude, Is.LessThan(2e-3f),
                "at the container change the pawn is at its last predicted pose carried through the link");
            predicted.ClientReconcile(crossing, new NetworkReader(worker.ToSegment()));
            Assert.That(predicted.Corrections, Is.EqualTo(0), "the prediction recorded in the street matches the worker's state in the room");
            Assert.That(predicted.MaxCorrectionMagnitude, Is.LessThanOrEqualTo(0.1f));
            Assert.That(link.State, Is.EqualTo(PortalState.Open));
        }
    }
}
