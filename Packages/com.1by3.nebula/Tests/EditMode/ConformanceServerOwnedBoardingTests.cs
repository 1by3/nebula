using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>
    /// Conformance scenario 34 (NEB-360): an entity no player owns moves between a scope and a moving container with a
    /// physics frame of its own, as a player's pawn does. A server-driven walker with a <see cref="CharacterController"/>
    /// walks from the ground onto a drifting platform, rides it through a 1 km/s flight with a turn, and walks off
    /// again; its position is continuous in the scope through both crossings and its velocity is carried into each new
    /// space. The platform can be handed to another worker on the very tick the walker boards, whichever of the two the
    /// worker's tick reaches first; and a client's interpolated copy of the walker, fed the state the worker sends, never
    /// jumps across either crossing.
    /// <para>
    /// Nothing here is specific to server-owned entities, and that is the point: container membership, frame crossings
    /// (<c>docs/container-tree.md</c> D15), handover and remote interpolation treat every authoritative entity alike.
    /// Tier B on the <see cref="ConformanceMesh"/>: frames get Editor preview scenes, and the container registry is
    /// process-wide, so of two workers' copies of one carrier only the last registered owns the box and its frame; the
    /// two-worker scenario keeps the copies' poses in step, as <see cref="ConformancePhysicsFrameTests"/> does.
    /// </para>
    /// </summary>
    [Category("Conformance")]
    public sealed class ConformanceServerOwnedBoardingTests
    {
        private const float Dt = NetworkTime.TickInterval;
        private const float WalkSpeed = 3f;
        private const float DriftSpeed = 1.5f;
        /// <summary>
        /// The platform's box: 7 m wide, 4 m high, 11 m long, its floor at y = 0 in its own coordinates. The deck under
        /// it is 8 m by 12 m, so a walker leaving through a side is past the box by the hysteresis while it still stands
        /// on the deck (<c>docs/frame-bodies.md</c> D6), and steps down onto the ground from the deck's original.
        /// </summary>
        private static readonly Vector3 Deck = new Vector3(7f, 4f, 11f);
        private static readonly Vector3 DeckFloor = new Vector3(8f, 0.2f, 12f);

        private ConformanceMesh _mesh;
        private ushort _platformPrefab, _walkerPrefab;
        private readonly List<GameObject> _objects = new List<GameObject>();

        private ConformanceMesh.Worker W1 => _mesh[0];

        [SetUp]
        public void SetUp()
        {
            PhysicsFrames.SceneFactory = () => EditorSceneManager.NewPreviewScene();
            PhysicsFrames.SceneDisposer = s => EditorSceneManager.ClosePreviewScene(s);
        }

        [TearDown]
        public void TearDown()
        {
            NebulaRuntime.IsClient = false;
            NebulaRuntime.IsServer = true;
            _mesh?.Dispose();
            _mesh = null;
            foreach (var go in _objects) if (go != null) Object.DestroyImmediate(go);
            _objects.Clear();
            ContainerRegistry.Rebuild();
            PhysicsFrames.DrainPool();
            Assert.AreEqual(0, PhysicsFrames.All.Count, "every frame was released with its container");
            PhysicsFrames.SceneFactory = null;
            PhysicsFrames.SceneDisposer = null;
        }

        // ------------------------------------------------------------------------------------ fixtures

        /// <summary>
        /// A server-driven walker as a game would write one: each tick it walks towards a point given in the scope's own
        /// space (<see cref="NetworkIdentity.FromScope"/> puts it in whatever space the walker is in), falls along its
        /// space's down, and reports what it did as <see cref="NetworkMotionState.Velocity"/>. A change of space only
        /// needs its fall picked up again from the velocity Nebula converted.
        /// </summary>
        internal sealed class Walker : NetworkBehaviour
        {
            public Func<Vector3> Goal;
            public float Speed = WalkSpeed;
            private float _fall;

            public override void NetworkTick(uint tick, float deltaTime)
            {
                if (!HasAuthority) return;
                var controller = GetComponent<CharacterController>();
                var here = transform.position;
                var walk = Vector3.zero;
                if (Goal != null)
                {
                    var to = Identity.FromScope(Goal()) - here;
                    to.y = 0f;
                    float distance = to.magnitude;
                    if (distance > 0.01f) walk = to / distance * Mathf.Min(Speed, distance / deltaTime);
                }
                _fall = controller.isGrounded ? -1f : _fall - 9.81f * deltaTime;
                controller.Move((walk + Vector3.up * _fall) * deltaTime);
                Identity.Motion.Velocity = (transform.position - here) / deltaTime;
            }

            public override void OnContainerChanged(Container previous, Container current)
            {
                // Nebula converted the velocity into the new space, the platform's motion included.
                if (HasAuthority) _fall = Mathf.Min(0f, Identity.Motion.Velocity.y);
            }
        }

        private static GameObject PlatformPrefab()
        {
            var platform = new GameObject("platform-prefab");
            platform.AddComponent<NetworkIdentity>();
            var box = platform.AddComponent<Container>();
            box.ContainerId = "platform";
            box.Size = Deck;
            box.Center = new Vector3(0f, Deck.y * 0.5f, 0f);
            box.OwnPhysicsFrame = true;
            platform.AddComponent<NetworkTransform>(); // with the Container on its root, the entity carries the box
            var deck = new GameObject("deck");
            deck.transform.SetParent(platform.transform, false);
            deck.transform.localPosition = new Vector3(0f, -0.1f, 0f);
            deck.AddComponent<BoxCollider>().size = DeckFloor;
            return platform;
        }

        private static GameObject WalkerPrefab()
        {
            var walker = new GameObject("walker-prefab");
            walker.AddComponent<NetworkIdentity>();
            walker.AddComponent<NetworkTransform>();
            var controller = walker.AddComponent<CharacterController>();
            controller.height = 1.8f;
            controller.radius = 0.4f;
            controller.center = Vector3.zero;
            controller.skinWidth = 0.02f;
            controller.minMoveDistance = 0f;
            walker.AddComponent<Walker>();
            return walker;
        }

        /// <summary>A ground collider (2 km square, its top at y = 0) and a static container over it for each worker.</summary>
        private List<Container> MeshWith(int workers)
        {
            _mesh = new ConformanceMesh(workers);
            var ground = new GameObject("ground");
            _objects.Add(ground);
            ground.transform.position = new Vector3(0f, -0.5f, 0f);
            ground.AddComponent<BoxCollider>().size = new Vector3(4000f, 1f, 4000f);
            var containers = new List<Container>();
            float width = 4000f / workers;
            for (int i = 0; i < workers; i++)
            {
                var c = _mesh.AddStaticContainer($"ground-{i}", new Vector3(-2000f + width * (i + 0.5f), -50f, 0f), new Vector3(width, 100f, 4000f));
                containers.Add(c);
            }
            for (int i = 0; i < workers; i++) _mesh.SetOwner(containers[i], _mesh[i]);
            _platformPrefab = _mesh.RegisterPrefab(PlatformPrefab());
            _walkerPrefab = _mesh.RegisterPrefab(WalkerPrefab());
            Physics.SyncTransforms();
            return containers;
        }

        private static Vector3 ScopePosition(NetworkIdentity e) => e.ToScope(e.transform.position);

        /// <summary>The walker's state as the worker streams it this tick, through the wire codec.</summary>
        private static EntityStateEntry Sent(NetworkIdentity e)
        {
            var entry = EntityStateEntry.Snapshot(e);
            entry.Fields |= TransformFields.Location | TransformFields.Reliable;
            var w = new NetworkWriter();
            entry.Write(w);
            return EntityStateEntry.Read(new NetworkReader(w.ToSegment()));
        }

        /// <summary>What a client is sent each tick, and where the platform and the walker really were.</summary>
        private sealed class Recording
        {
            public readonly List<uint> Ticks = new List<uint>();
            public readonly List<EntityStateEntry> Walker = new List<EntityStateEntry>();
            public readonly List<Vector3> PlatformPosition = new List<Vector3>();
            public readonly List<Quaternion> PlatformRotation = new List<Quaternion>();
            public readonly List<Vector3> Truth = new List<Vector3>();

            public void Add(uint tick, NetworkIdentity walker, NetworkIdentity platform)
            {
                Ticks.Add(tick);
                Walker.Add(Sent(walker));
                PlatformPosition.Add(platform.transform.position);
                PlatformRotation.Add(platform.transform.rotation);
                Truth.Add(ScopePosition(walker));
            }
        }

        /// <summary>
        /// Walk a spawned walker onto the drifting platform, fly it at 1 km/s with a turn, bring it back to a drift and
        /// walk off. Asserts the worker's side; returns the stream for the client's side.
        /// </summary>
        private Recording BoardFlyAndLeave(Container ground)
        {
            var platform = W1.SpawnServerDriven(_platformPrefab, ground, new Vector3(10f, 0f, 0f), Quaternion.identity);
            var box = platform.Carried;
            ContainerRegistry.RefreshCaches();
            var walker = W1.SpawnServerDriven(_walkerPrefab, ground, new Vector3(-4f, 0.93f, 0f), Quaternion.identity);
            var legs = walker.GetComponent<Walker>();
            Assert.IsTrue(walker.IsServerDriven, "no player owns it");
            Assert.AreEqual(0UL, walker.OwnerClientId);
            var recording = new Recording();
            var platformVelocity = new Vector3(0f, 0f, DriftSpeed);
            uint tick = 1;

            void Step()
            {
                platform.transform.position += platformVelocity * Dt;
                W1.Tick(tick);
                recording.Add(tick, walker, platform);
                tick++;
            }

            // Settle on the ground, then walk to a spot on the deck, 2 m in from its middle, while the platform drifts.
            for (int i = 0; i < 10; i++) Step();
            Assume.That(walker.Container, Is.SameAs(ground));
            var spot = new Vector3(0f, 0.9f, -2f);
            legs.Goal = () => platform.transform.TransformPoint(spot);
            int boardedAt = -1;
            for (int i = 0; i < 240 && walker.Container != box; i++)
            {
                var before = ScopePosition(walker);
                var velocityBefore = walker.Motion.Velocity;
                Step();
                if (walker.Container == box)
                {
                    boardedAt = recording.Ticks.Count - 1;
                    // The worker converted what the walker did into the platform's frame: the drift is taken off.
                    Assert.That(Vector3.Distance(velocityBefore - platformVelocity, walker.Motion.Velocity), Is.LessThan(0.3f),
                        $"velocity carried into the frame: {velocityBefore} in the scope became {walker.Motion.Velocity} on the platform");
                }
                float step = Vector3.Distance(before, ScopePosition(walker));
                Assert.That(step, Is.LessThan((WalkSpeed + DriftSpeed) * Dt + 0.02f), $"tick {tick - 1}: no jump in the scope ({step:0.000} m)");
            }
            Assert.That(boardedAt, Is.GreaterThanOrEqualTo(0), "the walker boarded");
            Assert.AreSame(box, walker.Container);
            Assert.AreSame(box.Frame.Root, walker.transform.parent, "it lives in the platform's frame");
            Assert.AreEqual(box.Frame.Scene, walker.gameObject.scene);
            Assert.AreEqual(1, W1.Instance.FrameCrossings);
            for (int i = 0; i < 90; i++) Step();
            Assert.That(Vector3.Distance(spot, walker.LocalPosition), Is.LessThan(0.1f), $"it reached the spot on the deck ({walker.LocalPosition})");

            // Take off: up to 1 km/s in a second, turning at 30 degrees a second, then cruise.
            var local = walker.LocalPosition;
            for (int i = 0; i < 120; i++)
            {
                if (i < 60) platformVelocity = new Vector3(0f, 0f, DriftSpeed) + platform.transform.forward * (1000f * (i + 1) / 60f);
                platform.transform.rotation *= Quaternion.Euler(0f, 30f * Dt, 0f);
                Step();
                Assert.AreSame(box, walker.Container, $"flight tick {i}: still aboard");
                Assert.That(Vector3.Distance(local, walker.LocalPosition), Is.LessThan(0.02f), $"flight tick {i}: standing still on the deck");
            }

            // Back to a drift over the ground, and walk off the port side to a point 6 m out.
            platformVelocity = new Vector3(0f, 0f, DriftSpeed);
            var landing = new Vector3(-30f, 0f, -200f);
            platform.transform.SetPositionAndRotation(landing, Quaternion.Euler(0f, 90f, 0f));
            for (int i = 0; i < 10; i++) Step();
            Assume.That(walker.Container, Is.SameAs(box));
            var off = platform.transform.TransformPoint(new Vector3(-Deck.x * 0.5f - 6f, 0f, 0f));
            legs.Goal = () => off;
            int leftAt = -1;
            for (int i = 0; i < 240 && walker.Container == box; i++)
            {
                var before = ScopePosition(walker);
                var velocityBefore = walker.Motion.Velocity;
                Step();
                if (walker.Container != box)
                {
                    leftAt = recording.Ticks.Count - 1;
                    var expected = Quaternion.Euler(0f, 90f, 0f) * velocityBefore + platformVelocity;
                    Assert.That(Vector3.Distance(expected, walker.Motion.Velocity), Is.LessThan(0.3f),
                        $"velocity carried out of the frame: {velocityBefore} on the platform became {walker.Motion.Velocity} in the scope");
                }
                float step = Vector3.Distance(before, ScopePosition(walker));
                Assert.That(step, Is.LessThan((WalkSpeed + DriftSpeed) * Dt + 0.02f), $"tick {tick - 1}: no jump in the scope ({step:0.000} m)");
            }
            Assert.That(leftAt, Is.GreaterThanOrEqualTo(0), "the walker left the platform");
            Assert.AreSame(ground, walker.Container);
            Assert.AreEqual(2, W1.Instance.FrameCrossings);
            for (int i = 0; i < 90; i++) Step();
            var there = ScopePosition(walker);
            Assert.That(new Vector2(there.x - off.x, there.z - off.z).magnitude, Is.LessThan(0.1f), $"it reached the point on the ground ({there})");
            Assert.That(there.y, Is.EqualTo(0.92f).Within(0.05f), "standing on the ground");
            return recording;
        }

        // ------------------------------------------------------------------------------------ one worker

        [Test]
        public void AServerOwnedWalkerBoardsAMovingPlatformRidesItAndWalksOff()
        {
            var grounds = MeshWith(1);
            BoardFlyAndLeave(grounds[0]);
        }

        [Test]
        public void AClientSeesTheWalkerCrossBothWaysWithoutAJump()
        {
            var grounds = MeshWith(1);
            var recording = BoardFlyAndLeave(grounds[0]);
            var platform = FindPlatform();
            var platformContainer = platform.Carried;

            // The client's copy of the walker, fed exactly what the worker sent, interpolating two ticks behind.
            NebulaRuntime.IsServer = false;
            NebulaRuntime.IsClient = true;
            var go = new GameObject("walker-on-a-client");
            _objects.Add(go);
            var copy = go.AddComponent<NetworkIdentity>();
            var transform = go.AddComponent<NetworkTransform>();
            transform.Interpolate = true;
            copy.Initialize();
            copy.NetId = recording.Walker[0].NetId;
            copy.Epoch = recording.Walker[0].Epoch;
            copy.InvokeSpawn();

            var ownPose = (platform.transform.position, platform.transform.rotation);
            const double Delay = 2.0;
            const int SubSteps = 4;
            int compared = 0;
            try
            {
                for (int i = 0; i < recording.Ticks.Count; i++)
                {
                    Assert.IsTrue(copy.ReceiveState(recording.Ticks[i], W1.Index, recording.Walker[i]), $"tick {recording.Ticks[i]} accepted");
                    for (int s = 0; s < SubSteps; s++)
                    {
                        double render = recording.Ticks[i] - Delay + (double)s / SubSteps;
                        int at = (int)Math.Floor(render) - (int)recording.Ticks[0];
                        if (at < 0 || at + 1 >= recording.Ticks.Count) continue;
                        float f = (float)(render - Math.Floor(render));
                        // The platform's own copy on the client is interpolated to the same render tick.
                        platform.transform.SetPositionAndRotation(
                            Vector3.Lerp(recording.PlatformPosition[at], recording.PlatformPosition[at + 1], f),
                            Quaternion.Slerp(recording.PlatformRotation[at], recording.PlatformRotation[at + 1], f));
                        platformContainer.RefreshCache();
                        copy.RemoteTick(render);
                        var shown = copy.Container != null ? copy.Container.ToWorld(copy.LocalPosition) : copy.LocalPosition;
                        var truth = Vector3.Lerp(recording.Truth[at], recording.Truth[at + 1], f);
                        // Anything but a smooth glide across a crossing (a pop to the far side of the platform, a
                        // frame's worth of the platform's motion added or lost) shows up as a distance from the truth.
                        Assert.That(Vector3.Distance(truth, shown), Is.LessThan(0.1f), $"render tick {render:0.00}: shown at {shown}, the walker was at {truth} ({(copy.Container == platformContainer ? "aboard" : "on the ground")})");
                        compared++;
                    }
                }
            }
            finally { platform.transform.SetPositionAndRotation(ownPose.position, ownPose.rotation); }
            Assert.That(compared, Is.GreaterThan(recording.Ticks.Count * 3), "every render step was compared");
        }

        private NetworkIdentity FindPlatform()
        {
            foreach (var e in W1.Instance.Entities) if (e != null && e.Carried != null) return e;
            throw new InvalidOperationException("no platform");
        }

        // ------------------------------------------------------------------------------------ a handover on the boarding tick

        [TestCase(true, TestName = "ThePlatformHandedOverOnTheBoardingTickTakesTheWalker(platform first)")]
        [TestCase(false, TestName = "ThePlatformHandedOverOnTheBoardingTickTakesTheWalker(walker first)")]
        public void ThePlatformHandedOverOnTheBoardingTickTakesTheWalker(bool platformFirst)
        {
            var grounds = MeshWith(2);
            var w2 = _mesh[1];
            var west = grounds[0];
            // The seam is x = 0. The platform drifts east across it; the walker waits on the west side, just clear of
            // the platform's stern, and steps aboard on the tick the platform's origin crosses the seam.
            // The platform leaves the west ground on the tick its origin is past the seam by the handover hysteresis.
            float start = _mesh.Config.HandoverHysteresis + 0.01f - DriftSpeed * Dt;
            NetworkIdentity platform = null, walker = null;
            void SpawnPlatform() => platform = W1.SpawnServerDriven(_platformPrefab, west, new Vector3(start, 0f, 0f), Quaternion.Euler(0f, 90f, 0f));
            void SpawnWalker() => walker = W1.SpawnServerDriven(_walkerPrefab, west, new Vector3(start - Deck.z * 0.5f - 0.05f, 0.93f, 0f), Quaternion.identity);
            if (platformFirst) { SpawnPlatform(); SpawnWalker(); }
            else { SpawnWalker(); SpawnPlatform(); }
            ContainerRegistry.RefreshCaches();
            Assume.That(walker.Container, Is.SameAs(west));
            Assume.That(platform.Container, Is.SameAs(west));
            var box = platform.Carried;

            // w2 holds no copy of the platform yet: the handover is what brings it there.
            Assume.That(w2.Find(platform.NetId), Is.Null);

            // The boarding tick: the platform's origin crosses into w2's ground, and the walker's origin into its box.
            var drift = Vector3.right * (DriftSpeed * Dt);
            platform.transform.position += drift;
            walker.transform.position += Vector3.right * 0.5f; // inside the box by more than the hysteresis
            var scopeBefore = ScopePosition(walker);
            var expectedLocal = Quaternion.Inverse(platform.transform.rotation) * (scopeBefore - platform.transform.position);
            Assume.That(platform.transform.position.x, Is.GreaterThan(_mesh.Config.HandoverHysteresis), "the platform is over w2's ground");
            Assume.That(box.Contains(scopeBefore), Is.True, "the walker is inside the platform's box");
            W1.Tick(2);
            Assert.IsFalse(platform.HasAuthority, "w1 handed the platform to w2");
            _mesh.Pump();

            // Whichever order w1 took them in, the walker ends up aboard, simulated by the platform's new owner. Walker
            // first: w1 crossed it (it still simulated the platform) and it went with the platform as its rider.
            // Platform first: w1 no longer knew the frame's pose, so it handed the walker to w2 unconverted and flagged
            // as a crossing, and w2 crosses it on its own next tick.
            var theirs = w2.Find(walker.NetId);
            Assert.IsNotNull(theirs, "w2 has the walker");
            Assert.IsTrue(theirs.HasAuthority, "w2 simulates the walker");
            Assert.IsFalse(walker.HasAuthority, "and w1 does not");
            var theirPlatform = w2.Find(platform.NetId);
            Assert.IsTrue(theirPlatform.HasAuthority);
            if (platformFirst)
            {
                Assert.AreEqual(1, W1.Instance.CrossingHandoffs, "handed to the frame's pose owner");
                Assert.AreEqual(0, W1.Instance.FrameCrossings);
                // The receiver holds it against being handed straight back, and crosses it on its next tick.
                w2.Tick(3);
                Assert.AreEqual(1, w2.Instance.FrameCrossings, "w2 crossed it at the frame's pose");
            }
            else
            {
                Assert.AreEqual(1, W1.Instance.FrameCrossings, "w1 crossed it while it still simulated the platform");
                Assert.AreEqual(0, W1.Instance.CrossingHandoffs);
            }
            Assert.AreSame(theirPlatform.Carried, theirs.Container, "aboard the platform");
            Assert.That(Vector3.Distance(expectedLocal, theirs.LocalPosition), Is.LessThan(0.05f),
                $"where it stepped aboard, in the platform's coordinates ({theirs.LocalPosition}, expected {expectedLocal})");
            Assert.That(Vector3.Distance(scopeBefore, ScopePosition(theirs)), Is.LessThan(0.05f), "no jump in the scope");

            // And it keeps walking on its new owner: across the deck, while the platform drifts on.
            theirs.GetComponent<Walker>().Goal = () => theirPlatform.transform.TransformPoint(new Vector3(0f, 0.9f, 0f));
            for (uint t = 4; t < 200; t++)
            {
                theirPlatform.transform.position += drift;
                w2.Tick(t);
                Assert.AreSame(theirPlatform.Carried, theirs.Container, $"tick {t}: aboard");
                Assert.IsTrue(theirs.HasAuthority);
            }
            Assert.That(Vector3.Distance(new Vector3(0f, 0.9f, 0f), theirs.LocalPosition), Is.LessThan(0.1f), $"it walked to the middle of the deck ({theirs.LocalPosition})");

            // Then the new owner flies the platform off at 1 km/s: the walker stays where it stands.
            var standing = theirs.LocalPosition;
            for (uint t = 200; t < 320; t++)
            {
                var velocity = theirPlatform.transform.forward * (1000f * Mathf.Min(1f, (t - 199) / 60f));
                theirPlatform.transform.position += velocity * Dt;
                w2.Tick(t);
                Assert.AreSame(theirPlatform.Carried, theirs.Container, $"flight tick {t}: aboard");
                Assert.That(Vector3.Distance(standing, theirs.LocalPosition), Is.LessThan(0.02f), $"flight tick {t}: standing still on the deck");
            }
            Assert.That(theirPlatform.transform.position.x, Is.GreaterThan(500f), "the platform flew half a kilometre and more");
        }
    }
}
