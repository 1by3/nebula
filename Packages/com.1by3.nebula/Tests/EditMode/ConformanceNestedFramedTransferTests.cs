using System.Collections.Generic;
using Nebula.World;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// Conformance scenario 78 (NEB-497, <c>docs/scope-activation.md</c> D19, <c>docs/container-tree.md</c> D26): a group
    /// committed into another scope with a translation (a jump between star systems), in which a carrier with its own
    /// physics frame (A) carries a carrier with its own frame (B), with a rider in B and a rider standing directly in A.
    /// <list type="bullet">
    /// <item>On the far side every member keeps its container, frame and seat, A arrives where the translation puts it,
    /// velocities are unchanged, the history A recorded in the old scope follows it into the new one, and neither frame
    /// reads the jump as motion on the ticks after it. With two workers the destination is the other worker's, which
    /// takes the whole group, carrier first, on the next tick.</item>
    /// <item>A group whose carriers hold something another worker simulates (a rider handed to it, an interior leased to
    /// it) is refused with a reason, and nothing moves; gathered on one worker, it commits.</item>
    /// </list>
    /// <para>
    /// Tier B on the <see cref="ConformanceMesh"/> with a <see cref="LocalControlPlane"/> holding both scopes, as
    /// <see cref="ConformanceCrossScopeCrewTests"/> does; frames are Editor preview scenes.
    /// </para>
    /// </summary>
    [Category("Conformance")]
    public sealed class ConformanceNestedFramedTransferTests
    {
        private const string Alpha = "system/alpha";
        private const string Beta = "system/beta";
        private const float Dt = NetworkTime.TickInterval;
        private const float Cruise = 200f;
        private const float TurnDegreesPerSecond = 6f;
        private static readonly Vector3 CellSize = new Vector3(8192f, 8192f, 8192f);
        private static readonly Vector3 ShipAt = new Vector3(5f, 0f, 20f);
        private static readonly Quaternion ShipTurn = Quaternion.Euler(0f, 30f, 0f);
        private static readonly Vector3 Seat = new Vector3(1f, 1f, 4f);
        private static readonly Vector3 DeckhandAt = new Vector3(-8f, 1f, -10f);
        /// <summary>From the old scope's own space to the new one's.</summary>
        private static readonly Vector3 Jump = new Vector3(300f, 40f, -500f);

        private ConformanceMesh _mesh;
        private LocalControlPlane _plane;
        private RuntimeGrid _alpha, _beta;
        private Container _from, _to;
        private ushort _carrierPrefab, _shipPrefab, _crewPrefab;
        private int _historyWindow;

        private ConformanceMesh.Worker W1 => _mesh[0];

        [SetUp]
        public void SetUp()
        {
            PhysicsFrames.SceneFactory = () => EditorSceneManager.NewPreviewScene();
            PhysicsFrames.SceneDisposer = s => EditorSceneManager.ClosePreviewScene(s);
            _historyWindow = StateHistory.WindowTicks;
        }

        [TearDown]
        public void TearDown()
        {
            StateHistory.WindowTicks = _historyWindow;
            ContainerRegistry.WorkerIdByIndex = index => NebulaRuntime.IsServer && index == NebulaRuntime.LocalWorkerIndex ? NebulaRuntime.LocalWorkerId : "";
            _mesh?.Dispose();
            _mesh = null;
            _plane?.Dispose();
            _plane = null;
            foreach (var c in new List<Container>(ContainerRegistry.Runtime)) if (c != null) Object.DestroyImmediate(c.gameObject);
            ContainerRegistry.PruneRuntime(new HashSet<ulong>());
            ContainerRegistry.Rebuild();
            PhysicsFrames.DrainPool();
            PhysicsFrames.SceneFactory = null;
            PhysicsFrames.SceneDisposer = null;
        }

        // ------------------------------------------------------------------------------------ fixtures

        /// <summary>Two scopes on one control plane every worker is attached to; the old scope's chunk is w1's, the new one's the last worker's.</summary>
        private void Build(int workers, bool perWorkerRegistry)
        {
            _mesh = new ConformanceMesh(workers, perWorkerRegistry);
            StateHistory.WindowTicks = 16; // after the mesh, which resets the runtime's settings
            ContainerRegistry.WorkerIdByIndex = index => index >= 1 && index <= workers ? $"w{index}" : "";
            _alpha = new RuntimeGrid(CellSize, planar: false, scopeKey: Alpha);
            _beta = new RuntimeGrid(CellSize, planar: false, scopeKey: Beta);
            _plane = new LocalControlPlane();
            _plane.Connect();
            foreach (var w in _mesh.Workers)
            {
                AttachRegistered(w.Instance, _plane);
                _plane.RegisterWorker(w.Id, w.Index, "127.0.0.1", (ushort)(7000 + w.Index));
                _plane.HeartbeatWorker(w.Id, WorkerStatus.Ready, default);
            }
            foreach (var (key, grid) in new[] { (Alpha, _alpha), (Beta, _beta) })
                _plane.ActivateScope(new ScopeActivationRequest
                {
                    ScopeKey = key,
                    Definition = new ChunkGridDefinition { CellSize = grid.CellSize, Planar = false }.ToScopeDefinition(),
                    PreferredWorkerId = W1.Id,
                });
            _from = Chunk(_alpha, W1);
            _to = Chunk(_beta, _mesh[workers - 1]);

            var carrier = new GameObject("carrier-prefab");
            carrier.AddComponent<NetworkIdentity>();
            var hull = carrier.AddComponent<Container>();
            hull.ContainerId = "carrier";
            hull.Size = new Vector3(40f, 16f, 100f);
            hull.Center = new Vector3(0f, 6f, 0f);
            hull.OwnPhysicsFrame = true;
            carrier.AddComponent<NetworkTransform>();
            _carrierPrefab = _mesh.RegisterPrefab(carrier);
            var ship = new GameObject("ship-prefab");
            ship.AddComponent<NetworkIdentity>();
            var box = ship.AddComponent<Container>();
            box.ContainerId = "ship";
            box.Size = new Vector3(10f, 6f, 20f);
            box.Center = new Vector3(0f, 3f, 0f);
            box.OwnPhysicsFrame = true;
            ship.AddComponent<NetworkTransform>();
            _shipPrefab = _mesh.RegisterPrefab(ship);
            var crew = new GameObject("crew-prefab");
            crew.AddComponent<NetworkIdentity>();
            crew.AddComponent<NetworkTransform>();
            _crewPrefab = _mesh.RegisterPrefab(crew);
        }

        /// <summary>Point the worker at <paramref name="plane"/> as if it had registered there (see <c>ConformanceCrossScopeCrewTests</c>).</summary>
        private static void AttachRegistered(NebulaWorker worker, LocalControlPlane plane)
        {
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            typeof(NebulaWorker).GetField("<ControlPlane>k__BackingField", flags).SetValue(worker, plane);
            var registration = (WorkerRegistration)typeof(NebulaWorker).GetField("_registration", flags).GetValue(worker);
            typeof(WorkerRegistration).GetField("<IsRegistered>k__BackingField", flags).SetValue(registration, true);
            typeof(WorkerRegistration).GetField("_reconciledDocument", flags).SetValue(registration, plane.DocumentId ?? "");
        }

        private static Container Chunk(RuntimeGrid grid, ConformanceMesh.Worker owner)
        {
            var container = ContainerRegistry.RegisterRuntime(grid.IdOf(Vector3Int.zero), grid.BoundsOf(Vector3Int.zero), new InstanceContainerInfo
            {
                InstanceId = grid.InstanceId,
                ScopeKey = grid.ScopeKey,
                PartId = ChunkKeys.PartId(Vector3Int.zero),
            });
            ContainerRegistry.ApplyLease(container.ContainerId, owner.Id, owner.Index, 1);
            return container;
        }

        private sealed class Group
        {
            public NetworkIdentity Carrier, Ship, Crew, Deckhand;
            public IEnumerable<NetworkIdentity> All => new[] { Carrier, Ship, Crew, Deckhand };
        }

        /// <summary>A with B landed on it, a rider in B's seat and a deckhand standing in A, cruising and turning in the old scope.</summary>
        private Group Crewed(ref uint tick)
        {
            var g = new Group();
            g.Carrier = W1.SpawnServerDriven(_carrierPrefab, _from, new Vector3(100f, 0f, 200f), Quaternion.Euler(0f, 20f, 0f));
            g.Ship = ConformanceFrameBodiesTests.SpawnIn(W1, _shipPrefab, g.Carrier.Carried, ShipAt, ShipTurn);
            g.Crew = ConformanceFrameBodiesTests.SpawnIn(W1, _crewPrefab, g.Ship.Carried, Seat, Quaternion.identity);
            g.Deckhand = ConformanceFrameBodiesTests.SpawnIn(W1, _crewPrefab, g.Carrier.Carried, DeckhandAt, Quaternion.identity);
            Assume.That(g.Ship.Carried.Frame, Is.Not.Null, "B has its frame");
            for (int i = 0; i < 40; i++, tick++) { Move(g.Carrier); TickAll(tick); }
            Assume.That(g.Carrier.Container, Is.SameAs(_from));
            Assume.That(g.Ship.Container, Is.SameAs(g.Carrier.Carried));
            Assume.That(g.Crew.Container, Is.SameAs(g.Ship.Carried));
            return g;
        }

        private static void Move(NetworkIdentity carrier)
        {
            var t = carrier.transform;
            t.rotation = Quaternion.AngleAxis(TurnDegreesPerSecond * Dt, Vector3.up) * t.rotation;
            carrier.Motion.Velocity = t.forward * Cruise;
            t.position += carrier.Motion.Velocity * Dt;
        }

        private void TickAll(uint tick)
        {
            foreach (var w in _mesh.Workers) w.Tick(tick);
            _mesh.Pump();
        }

        private NetworkIdentity Authoritative(ulong netId, out ConformanceMesh.Worker owner)
        {
            foreach (var w in _mesh.Workers)
            {
                var e = w.Find(netId);
                if (e != null && e.HasAuthority) { owner = w; return e; }
            }
            owner = null;
            return null;
        }

        /// <summary>Prepare every member into the new scope's chunk on w1, and wait (pumping) until each is ready.</summary>
        private List<InstanceTransfer> Prepare(IEnumerable<NetworkIdentity> members)
        {
            var group = new List<InstanceTransfer>();
            W1.Act(() => { foreach (var m in members) group.Add(W1.Instance.PrepareTransfer(m, _to)); });
            _mesh.Pump();
            foreach (var t in group) Assert.IsTrue(t.Ready, "every member is ready: " + t.Error);
            return group;
        }

        private bool Commit(List<InstanceTransfer> group)
        {
            bool committed = false;
            W1.Act(() => committed = W1.Instance.TryCommitTransfers(group, Double3.From(Jump)));
            return committed;
        }

        // ------------------------------------------------------------------------------------ the jump

        [TestCase(1, TestName = "AFramedCarrierCarryingAFramedShipJumpsScopeWithEveryoneSeated(one worker)")]
        [TestCase(2, TestName = "AFramedCarrierCarryingAFramedShipJumpsScopeWithEveryoneSeated(two workers)")]
        public void AFramedCarrierCarryingAFramedShipJumpsScopeWithEveryoneSeated(int workers)
        {
            Build(workers, perWorkerRegistry: true);
            uint tick = 1;
            var g = Crewed(ref tick);
            uint last = tick - 1;
            var aScope = g.Carrier.ScopePositionPrecise;
            var aRotation = g.Carrier.transform.rotation;
            var aVelocity = g.Carrier.Motion.Velocity;
            var aUnity = g.Carrier.transform.position;
            Assume.That(g.Carrier.StateAt(last).Available, Is.True, "A has history");
            var aHistory = g.Carrier.StateAt(last).Position;
            var crewHistory = g.Crew.StateAt(last).Position;
            var frameA = g.Carrier.Carried.Frame;
            var frameB = g.Ship.Carried.Frame;
            Assume.That(frameA.State.Velocity.magnitude, Is.EqualTo(Cruise).Within(1f));

            var group = Prepare(g.All);
            Assert.IsTrue(Commit(group), "the group commits");
            Assert.IsTrue(group.TrueForAll(t => t.Finished), "every member's crossing is recorded");

            Assert.AreSame(_to, g.Carrier.Container, "A is in the new scope's chunk");
            Assert.AreEqual(_beta.InstanceId, g.Carrier.InstanceId);
            foreach (var m in g.All) Assert.AreEqual(Beta, m.ScopeKey, $"{m} is in the new scope");
            Assert.AreSame(g.Carrier.Carried, g.Ship.Container, "B is still in A");
            Assert.AreSame(g.Ship.Carried, g.Crew.Container, "the rider is still in B");
            Assert.AreSame(g.Carrier.Carried, g.Deckhand.Container, "the deckhand is still in A");
            Assert.AreSame(frameA.Root, g.Ship.transform.parent, "B still hangs under A's frame root");
            Assert.AreSame(frameB.Root, g.Crew.transform.parent, "the rider under B's");
            Assert.That(Vector3.Distance(ShipAt, g.Ship.LocalPosition), Is.LessThan(1e-4f), "B in its place in A");
            Assert.That(Quaternion.Angle(ShipTurn, g.Ship.LocalRotation), Is.LessThan(0.01f));
            Assert.That(Vector3.Distance(Seat, g.Crew.LocalPosition), Is.LessThan(1e-4f), "the rider in its seat");
            Assert.That(Vector3.Distance(DeckhandAt, g.Deckhand.LocalPosition), Is.LessThan(1e-4f), "the deckhand where it stood");
            var moved = g.Carrier.ScopePositionPrecise - (aScope + Double3.From(Jump));
            Assert.That(new Vector3((float)moved.X, (float)moved.Y, (float)moved.Z).magnitude, Is.LessThan(1e-3f), "A where the jump puts it");
            Assert.That(Quaternion.Angle(aRotation, g.Carrier.transform.rotation), Is.LessThan(0.01f), "facing as it did");
            Assert.AreEqual(aVelocity, g.Carrier.Motion.Velocity, "with its velocity");

            // What A recorded in the old scope follows it: the last tick reads where A was, moved by the jump, in the new chunk.
            var delta = g.Carrier.transform.position - aUnity;
            var carried = g.Carrier.StateAt(last);
            Assert.IsTrue(carried.Available, "A's history is still there");
            Assert.That(Vector3.Distance(aHistory + delta, carried.Position), Is.LessThan(1e-3f), $"A's history moved with it ({carried.Position}, expected {aHistory + delta})");
            Assert.AreSame(_to, carried.Container, "and names the container it is in now");
            Assert.That(Vector3.Distance(crewHistory, g.Crew.StateAt(last).Position), Is.LessThan(1e-4f), "the rider's history, in B's frame, is untouched");

            // The ticks after the jump: A cruises on in the new scope (on w2 with two workers, which takes the group).
            ulong[] ids = { g.Carrier.NetId, g.Ship.NetId, g.Crew.NetId, g.Deckhand.NetId };
            float worstA = 0f, worstB = 0f, worstBSpin = 0f;
            for (int i = 0; i < 30; i++, tick++)
            {
                var a = Authoritative(ids[0], out var owner);
                Move(a);
                TickAll(tick);
                a = Authoritative(ids[0], out owner);
                var b = Authoritative(ids[1], out _);
                var c = Authoritative(ids[2], out _);
                var d = Authoritative(ids[3], out _);
                Assert.IsNotNull(a, $"t{tick}: somebody simulates A");
                foreach (var id in ids) Assert.AreSame(owner, Authoritative(id, out var o) != null ? o : null, $"t{tick}: the group is simulated together");
                Assert.AreSame(a.Carried, b.Container, $"t{tick}: B in A");
                Assert.AreSame(b.Carried, c.Container, $"t{tick}: the rider in B");
                Assert.AreSame(a.Carried, d.Container, $"t{tick}: the deckhand in A");
                Assert.That(Vector3.Distance(Seat, c.LocalPosition), Is.LessThan(1e-4f), $"t{tick}: the rider in its seat");
                Assert.That(Vector3.Distance(ShipAt, b.LocalPosition), Is.LessThan(1e-4f), $"t{tick}: B in its place");
                var sa = a.Carried.Frame.State;
                var sb = b.Carried.Frame.State;
                if (sa.HasRates) worstA = Mathf.Max(worstA, Mathf.Abs(sa.Velocity.magnitude - Cruise));
                if (sb.HasRates) { worstB = Mathf.Max(worstB, sb.Velocity.magnitude); worstBSpin = Mathf.Max(worstBSpin, sb.AngularVelocity.magnitude); }
            }
            TestContext.WriteLine($"after the jump: A's frame speed off by up to {worstA:0.000} m/s, B's frame reads up to {worstB:0.000} m/s and {worstBSpin:0.0000} rad/s");
            Assert.That(worstA, Is.LessThan(1f), "A's frame reads its cruise, not the jump");
            Assert.That(worstB, Is.LessThan(0.05f), "B's frame reads no motion in A");
            Assert.That(worstBSpin, Is.LessThan(0.005f), "and no spin");
            var final = Authoritative(ids[0], out var finalOwner);
            Assert.AreSame(_mesh[workers - 1], finalOwner, "simulated by the new chunk's owner");
            Assert.AreSame(_to, final.Container);
        }

        // ------------------------------------------------------------------------------------ never split

        /// <summary>
        /// The rider in B is handed to w2 (so w1 cannot prepare it): the group A, B and the deckhand is refused, with a
        /// reason naming the rider, and nothing moves. B's interior leased to w2 is refused the same way. Once the rider is
        /// back on w1 and the interior with it, the whole group, prepared again, commits.
        /// </summary>
        [Test]
        public void AGroupWhoseCarrierHoldsAnotherWorkersRiderIsRefusedNotSplit()
        {
            Build(2, perWorkerRegistry: false);
            var w2 = _mesh[1];
            uint tick = 1;
            var g = Crewed(ref tick);
            var aScope = g.Carrier.ScopePositionPrecise;

            W1.Transfer(g.Crew, w2);
            _mesh.Pump();
            var theirs = w2.Find(g.Crew.NetId);
            Assume.That(theirs != null && theirs.HasAuthority, Is.True, "w2 simulates the rider in B");
            var partial = Prepare(new[] { g.Carrier, g.Ship, g.Deckhand });
            Assert.IsFalse(Commit(partial), "refused");
            var refused = partial.Find(t => t.Error != null);
            Assert.IsNotNull(refused, "a member says why");
            StringAssert.Contains(g.Crew.NetId.ToString(), refused.Error, "naming the rider another worker simulates: " + refused.Error);
            AssertNothingMoved(g, aScope);

            // Gathered: the rider comes back to w1. B's interior leased to w2 is refused just the same.
            w2.Transfer(theirs, W1);
            _mesh.Pump();
            var crew = W1.Find(g.Crew.NetId);
            Assume.That(crew.HasAuthority, Is.True);
            ContainerRegistry.ApplyLease(g.Ship.Carried.ContainerId, w2.Id, w2.Index, 1, LeaseState.Pinned);
            Assume.That(g.Ship.Carried.OwnerWorkerId, Is.EqualTo(w2.Id));
            var leased = Prepare(new[] { g.Carrier, g.Ship, crew, g.Deckhand });
            Assert.IsFalse(Commit(leased), "refused while B's interior is another worker's");
            refused = leased.Find(t => t.Error != null);
            Assert.IsNotNull(refused);
            StringAssert.Contains(g.Ship.Carried.ContainerId, refused.Error, "naming the interior: " + refused.Error);
            AssertNothingMoved(g, aScope);

            ContainerRegistry.ApplyLease(g.Ship.Carried.ContainerId, W1.Id, W1.Index, 2, LeaseState.Pinned);
            var whole = Prepare(new[] { g.Carrier, g.Ship, crew, g.Deckhand });
            Assert.IsTrue(Commit(whole), "gathered on one worker, the group commits");
            Assert.AreSame(_to, g.Carrier.Container);
            Assert.AreSame(g.Ship.Carried, crew.Container, "the rider in its seat, in the new scope");
            Assert.AreEqual(Beta, crew.ScopeKey);
        }

        private void AssertNothingMoved(Group g, Double3 aScope)
        {
            Assert.AreSame(_from, g.Carrier.Container, "A stays in the old scope");
            Assert.AreEqual(Alpha, g.Carrier.ScopeKey);
            var d = g.Carrier.ScopePositionPrecise - aScope;
            Assert.That(new Vector3((float)d.X, (float)d.Y, (float)d.Z).magnitude, Is.LessThan(1e-4f), "where it was");
            Assert.AreSame(g.Carrier.Carried, g.Ship.Container);
            Assert.AreSame(g.Carrier.Carried, g.Deckhand.Container);
        }
    }
}
