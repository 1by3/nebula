using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// Conformance scenario 48 (<c>docs/conformance-suite.md</c>, NEB-400, design <c>docs/container-tree.md</c> D22): a
    /// chunk of a planet's hosted ground dealt to a worker that holds no copy of the planet. The planet is a carrier with
    /// a physics frame and regions of its own, simulated on w1; two chunks of its ground are leased under it, one to w1
    /// and one to w2, which owns nothing else anywhere near the planet. A pawn is spawned on w1 straight into w2's chunk,
    /// as a game spawns a joining player on the planet's worker, and w1 hands it to w2 on its first tick.
    /// <para>
    /// Before the fix w2 never registered the chunk (its row names the planet's box as its parent, and the planet was
    /// never sent to w2), so the handover waited for the chunk for ever, silently, and the player was lost. Now w1 gives
    /// every worker that leases a container in the planet's box a copy of the planet; the chunk registers under it, the
    /// handover lands, and the pawn stays. The rest of the planet's ground is not sent along with it. With no copy coming
    /// (the band switched off), the wait is reported after <see cref="NebulaWorker.ContainerWaitWarnSeconds"/>.
    /// </para>
    /// <para>
    /// Tier B on a <see cref="ConformanceMesh"/> with per-worker runtime containers: each worker registers the chunk rows
    /// in its own process, so w2 holds its chunk for a parent it does not have, exactly as a worker of its own does.
    /// </para>
    /// </summary>
    [Category("Conformance")]
    public sealed class ConformanceHostedChunkDealtAwayTests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private const float Dt = NetworkTime.TickInterval;
        private static readonly Vector3 PlanetBox = new Vector3(8192f, 1200f, 8192f);
        private static readonly Vector3 PlanetAt = new Vector3(30000f, 20000f, 0f);
        private static readonly Vector3 Chunk = new Vector3(512f, 1200f, 512f);
        private const ulong TownChunk = 4801, FarChunk = 4802;
        private const ulong Player = 48;

        private ConformanceMesh _mesh;
        private LocalControlPlane _plane;
        private System.DateTime _clock;
        private Container _space;
        private ushort _planetPrefab, _pawnPrefab;
        private uint _tick;
        private readonly List<string> _warnings = new List<string>();
        private readonly List<System.Action<Container>> _registered = new List<System.Action<Container>>();

        private ConformanceMesh.Worker W1 => _mesh[0];
        private ConformanceMesh.Worker W2 => _mesh[1];

        [SetUp]
        public void SetUp()
        {
            PhysicsFrames.SceneFactory = () => EditorSceneManager.NewPreviewScene();
            PhysicsFrames.SceneDisposer = s => EditorSceneManager.ClosePreviewScene(s);
            _clock = new System.DateTime(2026, 1, 1, 0, 0, 0, System.DateTimeKind.Utc);
            _plane = new LocalControlPlane { Clock = () => _clock };
            _plane.Connect();
            _tick = 1;
            _warnings.Clear();
            Application.logMessageReceived += Capture;
        }

        [TearDown]
        public void TearDown()
        {
            Application.logMessageReceived -= Capture;
            foreach (var handler in _registered) { ContainerRegistry.DynamicRegistered -= handler; ContainerRegistry.RuntimeRegistered -= handler; }
            _registered.Clear();
            _mesh?.Dispose();
            _mesh = null;
            _plane.Dispose();
            ContainerRegistry.PruneRuntime(new HashSet<ulong>());
            ContainerRegistry.Rebuild();
            PhysicsFrames.DrainPool();
            PhysicsFrames.SceneFactory = null;
            PhysicsFrames.SceneDisposer = null;
        }

        private void Capture(string message, string stack, LogType type)
        {
            if (type == LogType.Warning) _warnings.Add(message);
        }

        /// <summary>w1 owns the space and the planet in it; the town's chunk is w1's, the far chunk w2's.</summary>
        private NetworkIdentity StartMesh(float ghostBandMargin = 4f)
        {
            _mesh = new ConformanceMesh(2, perWorkerRuntime: true);
            _mesh.Config.GhostBandMargin = ghostBandMargin;
            // What a worker's start-up subscribes (the mesh does not run it): a container that registers late lets the
            // ghosts and handovers waiting for it go ahead.
            var late = typeof(NebulaWorker).GetMethod("OnLateContainerRegistered", Flags);
            foreach (var w in _mesh.Workers)
            {
                var handler = (System.Action<Container>)System.Delegate.CreateDelegate(typeof(System.Action<Container>), w.Instance, late);
                ContainerRegistry.DynamicRegistered += handler;
                ContainerRegistry.RuntimeRegistered += handler;
                _registered.Add(handler);
            }
            foreach (var w in _mesh.Workers)
            {
                Attach(w.Instance, _plane);
                _plane.RegisterWorker(w.Id, w.Index, "127.0.0.1", (ushort)(7900 + w.Index));
                _plane.HeartbeatWorker(w.Id, WorkerStatus.Ready, default);
            }
            // Static containers before any worker acts: every worker's registry starts from them.
            _space = _mesh.AddStaticContainer("space", Vector3.zero, new Vector3(80000f, 80000f, 80000f));
            _mesh.SetOwner(_space, W1);

            var planet = new GameObject("planet-prefab");
            planet.AddComponent<NetworkIdentity>();
            var box = planet.AddComponent<Container>();
            box.ContainerId = "planet";
            box.Size = PlanetBox;
            box.Center = Vector3.zero;
            box.OwnPhysicsFrame = true;
            box.FrameInterest = FrameInterestMode.OwnRegions;
            planet.AddComponent<NetworkTransform>();
            _planetPrefab = _mesh.RegisterPrefab(planet);
            var pawn = new GameObject("pawn-prefab");
            pawn.AddComponent<NetworkIdentity>();
            _pawnPrefab = _mesh.RegisterPrefab(pawn);

            var host = W1.SpawnServerDriven(_planetPrefab, _space, PlanetAt, Quaternion.identity);
            string hostId = host.Carried.ContainerId;
            // Two columns of the planet's ground side by side, the seam at x = 0 in the planet's coordinates.
            _plane.EnsureRuntimeContainer(ContainerRegistry.RuntimeContainerId(TownChunk),
                ContainerPlacement.Child(hostId, new Vector3(-256f, 0f, 0f), Chunk, ContainerAuthority.Leased), W1.Id);
            _plane.EnsureRuntimeContainer(ContainerRegistry.RuntimeContainerId(FarChunk),
                ContainerPlacement.Child(hostId, new Vector3(256f, 0f, 0f), Chunk, ContainerAuthority.Leased), W2.Id);
            Mirror();
            return host;
        }

        private static void Attach(NebulaWorker worker, LocalControlPlane plane)
        {
            typeof(NebulaWorker).GetField("<ControlPlane>k__BackingField", Flags).SetValue(worker, plane);
            var registration = (WorkerRegistration)typeof(NebulaWorker).GetField("_registration", Flags).GetValue(worker);
            typeof(WorkerRegistration).GetField("<IsRegistered>k__BackingField", Flags).SetValue(registration, true);
            typeof(WorkerRegistration).GetField("_reconciledDocument", Flags).SetValue(registration, plane.DocumentId ?? "");
        }

        /// <summary>Each worker's registry from the control plane's rows, on its own turn, as its control-plane pass does it.</summary>
        private void Mirror()
        {
            foreach (var w in _mesh.Workers)
                w.Act(() =>
                {
                    ContainerRegistry.SyncRuntime(_plane.Leases);
                    foreach (var lease in _plane.Leases)
                    {
                        if (!lease.HasBounds || string.IsNullOrEmpty(lease.WorkerId)) continue;
                        var owner = _mesh.Get(lease.WorkerId);
                        ContainerRegistry.ApplyLease(lease.ContainerId, owner.Id, owner.Index, lease.Epoch, lease.State);
                    }
                    ContainerRegistry.NotifyLeasesChanged();
                });
        }

        private void Step(int ticks = 1)
        {
            for (int i = 0; i < ticks; i++)
            {
                _clock = _clock.AddSeconds(Dt);
                Mirror();
                foreach (var w in _mesh.Workers) w.Tick(_tick);
                _mesh.Pump();
                _tick++;
            }
        }

        /// <summary>Spawn on <paramref name="worker"/> into a chunk of the planet, at a position in the planet's coordinates.</summary>
        private NetworkIdentity SpawnOnGround(ConformanceMesh.Worker worker, ulong chunk, Vector3 local, ulong owner = 0)
        {
            NetworkIdentity e = null;
            worker.Act(() =>
            {
                var container = ContainerRegistry.GetRuntime(chunk);
                Assert.IsNotNull(container, $"{worker.Id} holds chunk {chunk}");
                e = NetworkPrefabs.Instantiate(_pawnPrefab, local, Quaternion.identity, container.ContentRoot);
                worker.Instance.SpawnServerDriven(e, container);
                e.transform.position = container.InnerSpace.Frame.LocalToSimulation(local);
            });
            if (owner != 0) e.OwnerClientId = owner;
            return e;
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

        private int PendingHandovers(ConformanceMesh.Worker w) => ((System.Collections.IDictionary)w.GetField("_pendingTransfersByCarrier")).Count;

        private bool Holds(ConformanceMesh.Worker w, ulong chunk)
        {
            bool held = false;
            w.Act(() => held = ContainerRegistry.GetRuntime(chunk) != null);
            return held;
        }

        [Test]
        public void AHostedChunkDealtToAWorkerWithNoCopyOfItsHostLoadsAndTakesAHandover()
        {
            var planet = StartMesh();
            string hostId = planet.Carried.ContainerId;
            Assert.That(Holds(W1, FarChunk), Is.True, "w1, which holds the planet, registers both chunks");
            Assert.That(Holds(W2, FarChunk), Is.False, "w2 cannot register the chunk it was dealt: its parent is the planet's box");
            string waitingFor = null;
            W2.Act(() => ContainerRegistry.TryGetHeldParent(FarChunk, out waitingFor));
            Assert.AreEqual(hostId, waitingFor, "w2 holds the chunk's row for the planet's box");

            // Something on the town's ground, far from the seam: it is no business of w2's.
            var crate = SpawnOnGround(W1, TownChunk, new Vector3(-400f, 1f, 0f));
            // A joining player is spawned on the planet's worker straight into the chunk w2 leases.
            var pawn = SpawnOnGround(W1, FarChunk, new Vector3(256f, 1f, 0f), Player);
            ulong pawnId = pawn.NetId;
            Step();

            var copy = W2.Find(planet.NetId);
            Assert.That(copy, Is.Not.Null, "w1 gave w2 a copy of the planet, whose box hosts a chunk w2 leases");
            Assert.That(copy.HasAuthority, Is.False);
            Assert.That(Holds(W2, FarChunk), Is.True, "and w2's chunk registered under it");
            var landed = Authoritative(pawnId, out var owner);
            Assert.That(landed, Is.Not.Null, "somebody simulates the pawn");
            Assert.AreEqual(W2.Id, owner.Id, "the handover into w2's chunk landed");
            Assert.AreEqual(FarChunk, landed.Container.RuntimeId, "in the chunk it was handed into");
            Assert.That(landed.Container.InnerSpace, Is.SameAs(copy.Carried), "simulated in w2's copy of the planet's frame");
            Assert.AreEqual(0, PendingHandovers(W2), "nothing waits on w2");

            // Two seconds on: the pawn stays, the planet's copy stays, and the town's crate was never sent to w2.
            Step(2 * NetworkTime.TickRate);
            landed = Authoritative(pawnId, out owner);
            Assert.That(landed, Is.Not.Null, "the pawn was not lost");
            Assert.AreEqual(W2.Id, owner.Id);
            Assert.AreEqual(FarChunk, landed.Container.RuntimeId);
            Assert.That(W2.Find(planet.NetId), Is.Not.Null, "w2 keeps its copy of the planet while it leases the chunk");
            Assert.That(W2.Find(crate.NetId), Is.Null, "the rest of the planet's ground is not sent along with the planet");
            Assert.That(_warnings.Exists(m => m.Contains("waited")), Is.False, "nothing waited long enough to be reported");
        }

        [Test]
        public void AHandoverIntoAChunkWhoseHostNeverArrivesIsReported()
        {
            // The ghost band off: no copy of the planet goes anywhere, as on a mesh whose carrier's worker cannot send it.
            var planet = StartMesh(ghostBandMargin: -1f);
            SpawnOnGround(W1, FarChunk, new Vector3(256f, 1f, 0f), Player);
            Step(3 * NetworkTime.TickRate);
            Assert.AreEqual(1, PendingHandovers(W2), "the handover waits for the chunk");
            Assert.That(_warnings.Exists(m => m.Contains("waited")), Is.False, "and is not reported before the threshold");

            Step(Mathf.CeilToInt((NebulaWorker.ContainerWaitWarnSeconds - 1f) * NetworkTime.TickRate));
            string chunkId = ContainerRegistry.RuntimeContainerId(FarChunk);
            var wait = _warnings.Find(m => m.Contains("waited") && m.Contains("holds no copy of"));
            Assert.That(wait, Is.Not.Null, $"the wait is reported, with its cause (warnings: {string.Join(" | ", _warnings)})");
            StringAssert.Contains(planet.Carried.ContainerId, wait);
            var lease = _warnings.Find(m => m.Contains($"w2 leases {chunkId} but has not been able to register it"));
            Assert.That(lease, Is.Not.Null, "and so is the lease w2 cannot register");
            int reports = _warnings.FindAll(m => m.Contains("waited")).Count;
            Step(2 * NetworkTime.TickRate);
            Assert.AreEqual(reports, _warnings.FindAll(m => m.Contains("waited")).Count, "once");
        }
    }
}
