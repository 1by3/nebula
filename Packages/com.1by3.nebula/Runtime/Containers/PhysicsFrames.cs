using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Nebula
{
    /// <summary>
    /// The motion of a physics frame in the space around it, once per tick, on every process that holds the frame
    /// (<c>docs/container-tree.md</c> D14): its pose, linear and angular velocity, and linear acceleration. The frame's
    /// owner computes it from the carrier it simulates; everyone else from the replicated stream, so a leased interior
    /// reads it one replication delay late. Nebula applies no fictitious forces by itself: whether crates slide when the
    /// ship brakes is the game's call, made with <see cref="FrameInertia"/> on the bodies that should, or from
    /// <see cref="LocalAcceleration"/> and <see cref="LocalAngularVelocity"/> directly.
    /// </summary>
    public struct PhysicsFrameState
    {
        /// <summary>The frame's origin in the space around it.</summary>
        public Vector3 Position;
        /// <summary>The frame's orientation in the space around it.</summary>
        public Quaternion Rotation;
        /// <summary>Linear velocity of the frame's origin, in the space around it (m/s).</summary>
        public Vector3 Velocity;
        /// <summary>Angular velocity, in the space around it (radians per second, axis times rate).</summary>
        public Vector3 AngularVelocity;
        /// <summary>Linear acceleration of the frame's origin, in the space around it (m/s²).</summary>
        public Vector3 Acceleration;
        /// <summary>The tick this state was taken at.</summary>
        public uint Tick;
        /// <summary>At least two samples have been taken, so the rates mean something.</summary>
        public bool HasRates;

        /// <summary><see cref="Acceleration"/> expressed in the frame's own axes: what an accelerometer bolted to the deck reads.</summary>
        public Vector3 LocalAcceleration => Quaternion.Inverse(Rotation) * Acceleration;
        /// <summary><see cref="AngularVelocity"/> in the frame's own axes.</summary>
        public Vector3 LocalAngularVelocity => Quaternion.Inverse(Rotation) * AngularVelocity;

        /// <summary>A frame-local point in the space around the frame.</summary>
        public Vector3 ToParent(Vector3 local) => Position + Rotation * local;
        /// <summary>A point in the space around the frame, in frame-local coordinates.</summary>
        public Vector3 ToLocal(Vector3 parent) => Quaternion.Inverse(Rotation) * (parent - Position);
        /// <summary>How fast a point fixed in the frame moves in the space around it: v + ω × r.</summary>
        public Vector3 PointVelocity(Vector3 local) => Velocity + Vector3.Cross(AngularVelocity, Rotation * local);
    }

    /// <summary>What a crossing policy says about one entity crossing a frame boundary (<c>docs/container-tree.md</c> D16).</summary>
    public enum FrameCrossing
    {
        /// <summary>Cross now.</summary>
        Allow,
        /// <summary>Do not cross: the entity stays in the container it is in. The game keeps it on its side (a hull wall).</summary>
        Veto,
        /// <summary>Not yet: ask again next tick (the airlock is cycling).</summary>
        Defer,
    }

    /// <summary>
    /// The game's say over where entities may cross a physics frame's boundary: anywhere on the hull, or only through
    /// portals. Asked on the worker that performs the crossing (the frame's pose owner, D15), once per tick while the
    /// entity wants to cross. Install one on <see cref="PhysicsFrames.CrossingPolicy"/>.
    /// </summary>
    public interface IFrameCrossingPolicy
    {
        /// <param name="entity">The entity that wants to cross.</param>
        /// <param name="frame">The frame whose boundary it crosses.</param>
        /// <param name="leaving">True when it leaves the frame for the space around it, false when it enters.</param>
        /// <param name="from">The container it is in.</param>
        /// <param name="to">The container it would be in after the crossing.</param>
        FrameCrossing Decide(NetworkIdentity entity, PhysicsFrame frame, bool leaving, Container from, Container to);
    }

    /// <summary>
    /// A container's own physics frame: a local physics scene in which the container stands still
    /// (<c>docs/container-tree.md</c> §3). Everything the container holds is parented under <see cref="Root"/>, so in
    /// simulation space (every worker, and a client while it predicts) positions inside the frame are container-local
    /// and "down" is the container's own down.
    /// </summary>
    public sealed class PhysicsFrame
    {
        /// <summary>The container that owns this frame.</summary>
        public Container Owner { get; internal set; }
        /// <summary>The frame root: every entity and fixed child container inside the frame hangs under it.</summary>
        public Transform Root { get; internal set; }
        /// <summary>The Unity scene the frame simulates in; invalid in the Editor outside play mode without a test scene factory.</summary>
        public Scene Scene { get; internal set; }
        /// <summary>The frame's physics scene: where queries about anything inside the frame belong.</summary>
        public PhysicsScene PhysicsScene => Scene.IsValid() ? Scene.GetPhysicsScene() : Physics.defaultPhysicsScene;
        /// <summary>The frame's motion this tick (D14).</summary>
        public PhysicsFrameState State => _state;
        /// <summary>The frame's pose is driven by an entity (its carrier), so only that entity's authority knows it exactly (D15).</summary>
        public bool HasPoseOwner => Owner != null && Owner.IsDynamic;

        /// <summary>
        /// The frame's floating origin (<c>docs/container-tree.md</c> D19): the frame-local point that sits at Unity's
        /// origin in simulation space. Zero until the worker moves it with <see cref="PhysicsFrames.ShiftOrigin"/>; a
        /// planet's frame is large, and physics a few thousand kilometres from Unity's origin would be imprecise. A
        /// client keeps one too, near what it predicts in the frame, by the same rule
        /// (<see cref="PhysicsFrames.BeginSimulation(Container, Transform)"/>).
        /// </summary>
        public Vector3 Origin { get; internal set; }

        /// <summary>
        /// The worker's automatic origin shifts (<see cref="PhysicsFrames.AutoShift"/>) move this frame's origin across,
        /// never up or down. Set by a planar chunk grid hosted in the frame (<c>docs/container-tree.md</c> D22), whose
        /// columns, like a planar root grid's, never shift vertically; a game may set it for a frame of its own.
        /// <see cref="PhysicsFrames.ShiftOrigin"/> called directly still moves the origin wherever it is told.
        /// </summary>
        public bool KeepOriginLevel { get; set; }

        /// <summary>
        /// Raised after this frame's origin moved: the delta every cached simulation-space position of this frame gets
        /// (pose history, interpolation buffers and game caches are moved by Nebula for its own entities).
        /// </summary>
        public event Action<Vector3> Shifted;

        internal void RaiseShifted(Vector3 delta) => Shifted?.Invoke(delta);

        /// <summary>A frame-local position (container-local) where it sits in simulation space right now.</summary>
        public Vector3 LocalToSimulation(Vector3 local) => local + RootOffset;

        /// <summary>A simulation-space position inside this frame as a frame-local (container-local) one.</summary>
        public Vector3 SimulationToLocal(Vector3 position) => position - RootOffset;

        /// <summary>Where the root is while the frame simulates: minus its origin on a worker, the render pose on a client outside prediction.</summary>
        internal Vector3 RootOffset => Root != null && PhysicsFrames.InSimulationPose(Owner) ? Root.position : Vector3.zero;

        internal PhysicsFrameState _state;
        private Vector3 _lastVelocity;
        private int _samples;
        internal readonly List<KeyValuePair<Collider, Collider>> Clones = new List<KeyValuePair<Collider, Collider>>();
        /// <summary>
        /// The copies whose source has moved since the frame was built, each with the kinematic body that now moves it
        /// (<c>docs/frame-bodies.md</c> D3). A copy that never moves is not in here and stays a static collider.
        /// </summary>
        internal readonly Dictionary<Collider, MovingClone> MovingClones = new Dictionary<Collider, MovingClone>();

        /// <summary>A copy that moves as a kinematic body, and the frame-local pose it was last sent to.</summary>
        internal struct MovingClone
        {
            public Rigidbody Body;
            public Vector3 Position;
            public Quaternion Rotation;
        }
        internal GameObject Content;
        internal bool OwnsScene;

        // ---- render pose and render origin (D11, D25)

        /// <summary>
        /// The transform <see cref="Root"/> hangs under: the frame's own top level. On a worker, and on a client while
        /// the frame simulates, it stays at the identity pose, so the root's simulation pose is exact. On a client
        /// drawing the frame it carries the frame's render pose about <see cref="RenderOrigin"/> (D25).
        /// </summary>
        internal Transform Pivot;

        /// <summary>
        /// Whether this frame may be drawn about a render origin near the camera (D25). On by default; a game turns it
        /// off for a frame it poses itself. A frame whose content stays near its own origin (a ship's) never uses one
        /// whatever this says, as long as the anchor stays within <see cref="PhysicsFrames.RenderOriginThreshold"/>.
        /// </summary>
        public bool UseRenderOrigin { get; set; } = true;

        /// <summary>
        /// The frame-local point the frame is drawn about on a rendering client (D25): zero while the render anchor is
        /// near the frame's own origin, else the anchor's frame-local position snapped to
        /// <see cref="PhysicsFrames.RenderOriginStep"/>. Always zero on a worker.
        /// </summary>
        public Vector3 RenderOrigin { get; internal set; }

        /// <summary>
        /// Where the frame is drawn: its carrier's position as <see cref="PhysicsFrames.PoseForRender()"/> last posed it
        /// (on a worker, and before the first pose, the carrier's current one). Read this rather than
        /// <c>Root.position</c>, which under a render origin is composed in float through the turned origin.
        /// </summary>
        public Vector3 RenderPosition => _posed ? _renderPosition : Owner != null ? Owner.transform.position : Vector3.zero;

        /// <summary>The frame's orientation as drawn (see <see cref="RenderPosition"/>).</summary>
        public Quaternion RenderRotation => _posed ? _renderRotation : Owner != null ? Owner.transform.rotation : Quaternion.identity;

        /// <summary>The frame's scale as drawn (see <see cref="RenderPosition"/>): the carrier's lossy scale, normally one.</summary>
        public Vector3 RenderScale => _posed ? _renderScale : Owner != null ? Owner.transform.lossyScale : Vector3.one;

        internal Vector3 _renderPosition;
        internal Quaternion _renderRotation = Quaternion.identity;
        internal Vector3 _renderScale = Vector3.one;
        internal bool _posed;

        /// <summary>
        /// A frame-local point where it is drawn: <see cref="RenderPosition"/> + <see cref="RenderRotation"/> ×
        /// (<see cref="RenderScale"/> ∘ <paramref name="local"/>), composed in double, so a point 200 km from the frame's
        /// origin but near the camera comes out within a fraction of a millimetre.
        /// </summary>
        public Vector3 LocalToRender(Vector3 local) => LocalToRenderPrecise(Double3.From(local)).ToVector3();

        /// <summary><see cref="LocalToRender(Vector3)"/> from and to double.</summary>
        public Double3 LocalToRenderPrecise(Double3 local)
        {
            var s = RenderScale;
            var turned = PhysicsFrames.Rotate(RenderRotation, new Double3(local.X * s.x, local.Y * s.y, local.Z * s.z));
            return Double3.From(RenderPosition) + turned;
        }

        /// <summary>A point of the drawn scene in frame-local coordinates, in double (<see cref="LocalToRender(Vector3)"/>'s inverse).</summary>
        public Double3 RenderToLocalPrecise(Double3 scene)
        {
            var r = RenderRotation;
            var local = PhysicsFrames.Rotate(new Quaternion(-r.x, -r.y, -r.z, r.w), scene - RenderPosition);
            var s = RenderScale;
            return new Double3(s.x != 0f ? local.X / s.x : 0.0, s.y != 0f ? local.Y / s.y : 0.0, s.z != 0f ? local.Z / s.z : 0.0);
        }

        /// <summary><see cref="RenderToLocalPrecise"/> for a float scene point.</summary>
        public Vector3 RenderToLocal(Vector3 scene) => RenderToLocalPrecise(Double3.From(scene)).ToVector3();

        /// <summary>A frame-local rotation as drawn.</summary>
        public Quaternion LocalToRender(Quaternion local) => RenderRotation * local;

        /// <summary>A drawn rotation in frame-local terms.</summary>
        public Quaternion RenderToLocal(Quaternion scene) => Quaternion.Inverse(RenderRotation) * scene;

        /// <summary>A frame-local point in the space around the frame, from the owner's current transform.</summary>
        public Vector3 ToParent(Vector3 local) => Owner.transform.TransformPoint(local);
        /// <summary>A point of the space around the frame in frame-local coordinates.</summary>
        public Vector3 FromParent(Vector3 parent) => Owner.transform.InverseTransformPoint(parent);
        public Quaternion ToParent(Quaternion local) => Owner.transform.rotation * local;
        public Quaternion FromParent(Quaternion parent) => Quaternion.Inverse(Owner.transform.rotation) * parent;

        internal void Sample(uint tick, float dt)
        {
            var t = Owner.transform;
            var position = t.position;
            var rotation = t.rotation;
            // The same pose sampled again in the same tick (two workers sharing one registry in a test, or a client
            // rendering twice within a tick while the carrier stands still) adds nothing; reading it as a sample would
            // report the frame at rest for a tick.
            if (_samples > 0 && tick == _state.Tick && position.Equals(_state.Position) && rotation.Equals(_state.Rotation)) return;
            if (_samples > 0 && dt > 0f)
            {
                var velocity = (position - _state.Position) / dt;
                _state.Acceleration = _samples > 1 ? (velocity - _lastVelocity) / dt : Vector3.zero;
                _state.Velocity = velocity;
                _state.AngularVelocity = AngularVelocity(rotation, dt);
                _lastVelocity = velocity;
                _state.HasRates = true;
            }
            PushRotation(rotation, _samples > 0 && dt > 0f ? dt : 0f);
            _state.Position = position;
            _state.Rotation = rotation;
            _state.Tick = tick;
            _samples++;
        }

        internal void ResetMotion()
        {
            _samples = 0;
            _rotationCount = 0;
            _state = new PhysicsFrameState { Position = Owner.transform.position, Rotation = Owner.transform.rotation };
        }

        // ---- angular velocity (D14, NEB-390)
        //
        // A rotation is stored in floats, a few 1e-8 apart per component. A planet turning once in a few hours turns by
        // about 2e-6 rad in a 60 Hz tick: a one-tick difference taken through Quaternion.ToAngleAxis read exactly zero
        // (its w rounds to 1), and even taken in double it carries a few per cent of rounding. So the rate is taken
        // over as many recent samples as it takes to turn by RateAngle, up to RotationHistory of them, as long as that
        // longer baseline agrees with the newest tick's own reading (a frame that just stopped or changed its spin
        // reads its newest tick, not an average over the last second). Everything is computed in double.

        /// <summary>How many past rotations a frame keeps for its angular velocity: about four seconds at 60 Hz.</summary>
        internal const int RotationHistory = 256;
        /// <summary>The angle (radians) a baseline must turn through before it is long enough: rounding stays below 0.1% of it.</summary>
        internal const double RateAngle = 1e-3;
        /// <summary>
        /// How far (radians per sample) a longer baseline may read from the newest tick's own reading and still be used:
        /// above the rounding of one tick's difference, and small enough that a frame that stopped turning reads less
        /// than 1e-4 rad/s at 60 Hz.
        /// </summary>
        internal const double RateAgreement = 2e-6;

        private readonly Quaternion[] _rotations = new Quaternion[RotationHistory];
        private readonly double[] _rotationTimes = new double[RotationHistory];
        private int _rotationHead, _rotationCount;
        private double _rotationClock;

        private void PushRotation(Quaternion rotation, float dt)
        {
            _rotationClock = _rotationCount == 0 ? 0.0 : _rotationClock + dt;
            _rotationHead = (_rotationHead + 1) % RotationHistory;
            _rotations[_rotationHead] = rotation;
            _rotationTimes[_rotationHead] = _rotationClock;
            if (_rotationCount < RotationHistory) _rotationCount++;
        }

        /// <summary>The angular velocity from the stored rotations to <paramref name="rotation"/>, taken <paramref name="dt"/> after the newest of them.</summary>
        private Vector3 AngularVelocity(Quaternion rotation, float dt)
        {
            if (_rotationCount == 0) return Vector3.zero;
            double now = _rotationClock + dt;
            // The newest tick's own reading.
            var newest = Rate(_rotations[_rotationHead], rotation, dt, out double angle);
            var best = newest;
            if (angle >= RateAngle) return ToVector(best);
            // Longer baselines, doubling, while they agree with it, until one has turned far enough.
            for (int back = 1; back < _rotationCount; back = back * 2 + 1)
            {
                int i = (_rotationHead - back + RotationHistory) % RotationHistory;
                double span = now - _rotationTimes[i];
                if (span <= 0.0) break;
                var longer = Rate(_rotations[i], rotation, span, out angle);
                if (Distance(longer, newest) * dt > RateAgreement) break;
                best = longer;
                if (angle >= RateAngle) break;
            }
            return ToVector(best);
        }

        /// <summary>The steady angular velocity (rad/s, x/y/z) that turns <paramref name="from"/> into <paramref name="to"/> in <paramref name="seconds"/>.</summary>
        internal static (double x, double y, double z) Rate(Quaternion from, Quaternion to, double seconds, out double angle)
        {
            // to * inverse(from), in double. from is a unit quaternion, so its inverse is its conjugate.
            double ax = to.x, ay = to.y, az = to.z, aw = to.w;
            double bx = -from.x, by = -from.y, bz = -from.z, bw = from.w;
            double w = aw * bw - ax * bx - ay * by - az * bz;
            double x = aw * bx + ax * bw + ay * bz - az * by;
            double y = aw * by - ax * bz + ay * bw + az * bx;
            double z = aw * bz + ax * by - ay * bx + az * bw;
            if (w < 0.0) { w = -w; x = -x; y = -y; z = -z; } // the short way round
            double s = Math.Sqrt(x * x + y * y + z * z);
            angle = 2.0 * Math.Atan2(s, w);
            if (s <= 0.0 || seconds <= 0.0) { angle = 0.0; return (0.0, 0.0, 0.0); }
            double k = angle / (s * seconds);
            return (x * k, y * k, z * k);
        }

        private static double Distance((double x, double y, double z) a, (double x, double y, double z) b)
        {
            double dx = a.x - b.x, dy = a.y - b.y, dz = a.z - b.z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static Vector3 ToVector((double x, double y, double z) v) => new Vector3((float)v.x, (float)v.y, (float)v.z);

        public override string ToString() => $"frame({(Owner != null ? Owner.ContainerId : "-")})";
    }

    /// <summary>
    /// The physics frames this process holds, one per container with <see cref="Container.OwnPhysicsFrame"/>
    /// (<c>docs/container-tree.md</c> §3): scene lifetime and pooling, interior colliders, frame state, conversion
    /// between spaces, the crossing policy, and — on a client — composing the frames into one rendered world.
    /// </summary>
    public static class PhysicsFrames
    {
        private static readonly List<PhysicsFrame> Frames = new List<PhysicsFrame>();
        private static readonly Stack<Scene> Pool = new Stack<Scene>();
        private static readonly List<PhysicsFrame> PoseOrder = new List<PhysicsFrame>();
        private static readonly Dictionary<Collider, Collider> CloneSources = new Dictionary<Collider, Collider>();
        private static PhysicsFrame _simulating;
        private static int _sceneCounter;

        /// <summary>
        /// Makes a local-physics scene outside play mode. EditMode tests install
        /// <c>EditorSceneManager.NewPreviewScene</c>, which has a physics scene of its own; nothing sets it at runtime.
        /// </summary>
        internal static Func<Scene> SceneFactory;
        /// <summary>Disposes of a scene <see cref="SceneFactory"/> made (tests: <c>EditorSceneManager.ClosePreviewScene</c>).</summary>
        internal static Action<Scene> SceneDisposer;

        /// <summary>How many emptied frame scenes are kept for reuse, so a carrier spawning and despawning does not create and unload a scene each time (D13).</summary>
        public static int PoolSize = 8;

        /// <summary>Every frame this process holds.</summary>
        public static IReadOnlyList<PhysicsFrame> All => Frames;

        /// <summary>The game's say over crossings (D16). Null allows every crossing.</summary>
        public static IFrameCrossingPolicy CrossingPolicy { get; set; }

        /// <summary>A frame was created (its container registered here). The game may add content to <see cref="PhysicsFrame.Root"/>.</summary>
        public static event Action<PhysicsFrame> Created;
        /// <summary>A frame is about to be released (its container is leaving this process).</summary>
        public static event Action<PhysicsFrame> Releasing;

        /// <summary>
        /// This process renders frames: a client, whose frame roots are posed at each frame's world pose outside
        /// its prediction step (D11). A worker never does, and converts poses itself when an entity changes space.
        /// </summary>
        public static bool RendersFrames => NebulaRuntime.IsClient && !NebulaRuntime.IsServer;

        /// <summary>
        /// Forget everything without touching any object: a new play session is starting in an Editor that did not
        /// reload the domain (see <see cref="NebulaStatics"/>).
        /// </summary>
        internal static void ResetForNewSession()
        {
            Frames.Clear();
            Pool.Clear();
            PoseOrder.Clear();
            CloneSources.Clear();
            _simulating = null;
            CrossingPolicy = null;
            RenderAnchor = null;
            ContentAnchor = null;
            Created = null;
            Releasing = null;
            SceneFactory = null;
            SceneDisposer = null;
        }

        // ------------------------------------------------------------------------------------ lifetime

        /// <summary>Give <paramref name="owner"/> its frame (idempotent). Called by the registry when a framed container registers.</summary>
        internal static PhysicsFrame Create(Container owner)
        {
            if (owner == null) return null;
            if (owner.Frame != null) return owner.Frame;
            var frame = new PhysicsFrame { Owner = owner };
            frame.Scene = TakeScene(out bool ownsScene);
            frame.OwnsScene = ownsScene;
            // The pivot is the frame's top level and the root hangs under it at the identity: on a client the pivot
            // carries the render pose about a render origin (D25) without the root ever being reparented.
            var pivot = new GameObject("Frame pivot: " + owner.ContainerId);
            if (frame.Scene.IsValid()) SceneManager.MoveGameObjectToScene(pivot, frame.Scene);
            else if (owner.gameObject.scene.IsValid() && pivot.scene != owner.gameObject.scene) SceneManager.MoveGameObjectToScene(pivot, owner.gameObject.scene);
            var root = new GameObject("Frame: " + owner.ContainerId);
            root.transform.SetParent(pivot.transform, false);
            frame.Pivot = pivot.transform;
            frame.Root = root.transform;
            owner.Frame = frame;
            BuildContent(frame);
            frame.ResetMotion();
            Frames.Add(frame);
            if (RendersFrames) PoseForRender(frame);
            try { Created?.Invoke(frame); }
            catch (Exception e) { NebulaLog.Error($"PhysicsFrames.Created threw: {e}"); }
            return frame;
        }

        /// <summary>
        /// Release <paramref name="owner"/>'s frame. Whatever the registry did not move out first (it evacuates entities
        /// and detaches child containers before calling this) is destroyed with the root; the scene goes back to the pool.
        /// </summary>
        internal static void Release(Container owner)
        {
            // The owner may already be destroyed (a carrier torn down without a despawn): its managed half still
            // holds the frame, and the frame still has to go.
            var frame = !ReferenceEquals(owner, null) ? owner.Frame : null;
            if (frame == null) return;
            Release(frame);
        }

        private static void Release(PhysicsFrame frame)
        {
            try { Releasing?.Invoke(frame); }
            catch (Exception e) { NebulaLog.Error($"PhysicsFrames.Releasing threw: {e}"); }
            if (_simulating == frame) _simulating = null;
            Frames.Remove(frame);
            var owner = frame.Owner;
            if (!ReferenceEquals(owner, null)) owner.Frame = null;
            // Nothing networked may go down with the root: an entity still under it would be destroyed without a despawn.
            if (frame.Root != null)
            {
                var home = owner != null ? owner.gameObject.scene : SceneManager.GetActiveScene();
                var stranded = frame.Root.GetComponentsInChildren<NetworkIdentity>(true);
                foreach (var e in stranded)
                {
                    if (e == null || e.transform.parent == null) continue;
                    e.transform.SetParent(null, true);
                    if (home.IsValid() && e.gameObject.scene != home) SceneManager.MoveGameObjectToScene(e.gameObject, home);
                }
                Destroy(frame.Root.gameObject);
            }
            if (frame.Pivot != null) Destroy(frame.Pivot.gameObject);
            foreach (var pair in frame.Clones) if (!ReferenceEquals(pair.Value, null)) CloneSources.Remove(pair.Value);
            frame.Clones.Clear();
            frame.MovingClones.Clear();
            if (frame.OwnsScene) ReturnScene(frame.Scene);
            frame.Scene = default;
        }

        /// <summary>Release every frame whose owner was destroyed without being unregistered (a scene torn down around it).</summary>
        private static void PruneDestroyed()
        {
            for (int i = Frames.Count - 1; i >= 0; i--)
                if (Frames[i].Owner == null) Release(Frames[i]);
        }

        private static Scene TakeScene(out bool owns)
        {
            owns = true;
            while (Pool.Count > 0)
            {
                var pooled = Pool.Pop();
                if (pooled.IsValid() && pooled.isLoaded) return pooled;
            }
            if (Application.isPlaying)
                return SceneManager.CreateScene("Nebula frame " + (++_sceneCounter), new CreateSceneParameters(LocalPhysicsMode.Physics3D));
            if (SceneFactory != null) return SceneFactory();
            // The Editor outside play mode cannot create a runtime scene: the frame lives in its owner's scene. Its
            // coordinates still behave (the root stays at the identity pose); only the physics is not isolated.
            owns = false;
            return default;
        }

        private static void ReturnScene(Scene scene)
        {
            if (!scene.IsValid()) return;
            // An emptied scene is only worth keeping while it is really empty.
            if (Pool.Count < PoolSize && scene.rootCount == 0) { Pool.Push(scene); return; }
            if (!Application.isPlaying) { SceneDisposer?.Invoke(scene); return; }
            if (scene.isLoaded) SceneManager.UnloadSceneAsync(scene);
        }

        /// <summary>Unload every pooled scene (a world unloaded, a test tearing down).</summary>
        public static void DrainPool()
        {
            while (Pool.Count > 0)
            {
                var s = Pool.Pop();
                if (!s.IsValid()) continue;
                if (!Application.isPlaying) SceneDisposer?.Invoke(s);
                else if (s.isLoaded) SceneManager.UnloadSceneAsync(s);
            }
        }

        private static void Destroy(GameObject go)
        {
            if (go == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(go);
            else UnityEngine.Object.DestroyImmediate(go);
        }

        // ------------------------------------------------------------------------------------ content (D12)

        /// <summary>
        /// The interior geometry: the container's <see cref="Container.FrameContent"/> prefab, or a static copy of
        /// the owner's colliders (every enabled non-trigger collider under the owner that does not belong to another
        /// entity), so a ship's floor and walls are in its frame with nothing authored.
        /// </summary>
        private static void BuildContent(PhysicsFrame frame)
        {
            var owner = frame.Owner;
            if (owner.FrameContent != null)
            {
                if (owner.FrameContent.GetComponentInChildren<NetworkIdentity>(true) != null)
                {
                    NebulaLog.Error($"FrameContent of {owner.ContainerId} contains a NetworkIdentity; frame content must be static geometry. Cloning the owner's colliders instead.");
                }
                else
                {
                    frame.Content = UnityEngine.Object.Instantiate(owner.FrameContent, frame.Root, false);
                    frame.Content.name = owner.FrameContent.name;
                    return;
                }
            }
            if (owner.IsRuntime) return; // a registered box has no geometry of its own
            var identity = owner.GetComponent<NetworkIdentity>();
            var colliders = owner.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
            {
                var source = colliders[i];
                if (source == null || source.isTrigger) continue;
                if (IsRider(source, identity)) continue; // another entity's collider: it moves itself
                var clone = CloneCollider(source, frame.Root);
                if (clone == null) continue;
                frame.Clones.Add(new KeyValuePair<Collider, Collider>(source, clone));
                CloneSources[clone] = source;
            }
            SyncContent(frame, placing: true);
        }

        /// <summary>
        /// Whether <paramref name="source"/> belongs to an entity of its own riding under the carrier, rather than to the
        /// carrier's geometry. A <see cref="NetworkIdentity"/> nested in the carrier's prefab (one that a
        /// <see cref="NetworkBehaviour"/> on a door or a seat pulled in) is never spawned: the carrier's identity owns
        /// those behaviours, and their colliders are the carrier's.
        /// </summary>
        private static bool IsRider(Collider source, NetworkIdentity carrier)
        {
            for (var t = source.transform; t != null; t = t.parent)
            {
                var id = t.GetComponent<NetworkIdentity>();
                if (id == null) continue;
                if (id == carrier) return false;
                if (id.IsSpawned || id.Container != null) return true;
            }
            return false;
        }

        private static Collider CloneCollider(Collider source, Transform root)
        {
            var go = new GameObject("Frame collider: " + source.name) { layer = source.gameObject.layer };
            go.transform.SetParent(root, false);
            Collider clone;
            switch (source)
            {
                case BoxCollider b: { var c = go.AddComponent<BoxCollider>(); c.center = b.center; c.size = b.size; clone = c; break; }
                case SphereCollider s: { var c = go.AddComponent<SphereCollider>(); c.center = s.center; c.radius = s.radius; clone = c; break; }
                case CapsuleCollider k: { var c = go.AddComponent<CapsuleCollider>(); c.center = k.center; c.radius = k.radius; c.height = k.height; c.direction = k.direction; clone = c; break; }
                case MeshCollider m: { var c = go.AddComponent<MeshCollider>(); c.sharedMesh = m.sharedMesh; c.convex = m.convex; c.cookingOptions = m.cookingOptions; clone = c; break; }
                default: Destroy(go); return null;
            }
            clone.sharedMaterial = source.sharedMaterial;
            return clone;
        }

        /// <summary>
        /// Keep each cloned collider where its source is relative to the owner, and enabled as it is: a ramp that
        /// lowers or a door that opens on the hull does the same inside the frame. Called once per tick.
        /// <para>
        /// On a process that steps the frame's scene (a worker), a copy whose source moves becomes a kinematic body the
        /// first time it moves, and is moved with <see cref="Rigidbody.MovePosition"/> from then on, so PhysX gives it
        /// a velocity for the step and a crate on a moving ramp rides it rather than being pushed out of an overlap
        /// (<c>docs/frame-bodies.md</c> D3). A client renders its frames posed and does not step them, so its copies
        /// are placed through their transforms.
        /// </para>
        /// </summary>
        internal static void SyncContent(PhysicsFrame frame) => SyncContent(frame, placing: false);

        /// <param name="frame">The frame whose copies follow their sources.</param>
        /// <param name="placing">The frame is being built: every copy is put in place, and none counts as moving.</param>
        private static void SyncContent(PhysicsFrame frame, bool placing)
        {
            if (frame.Clones.Count == 0 || frame.Owner == null) return;
            var owner = frame.Owner.transform;
            bool steps = !placing && frame.OwnsScene && !RendersFrames && frame.Root != null;
            for (int i = frame.Clones.Count - 1; i >= 0; i--)
            {
                var pair = frame.Clones[i];
                if (pair.Key == null || pair.Value == null)
                {
                    if (!ReferenceEquals(pair.Value, null)) { CloneSources.Remove(pair.Value); frame.MovingClones.Remove(pair.Value); }
                    if (pair.Value != null) Destroy(pair.Value.gameObject);
                    frame.Clones.RemoveAt(i);
                    continue;
                }
                var m = RelativeTo(pair.Key.transform, owner);
                var t = pair.Value.transform;
                var position = (Vector3)m.GetColumn(3);
                var rotation = m.rotation;
                var scale = m.lossyScale;
                if (t.localScale != scale) t.localScale = scale;
                if (frame.MovingClones.TryGetValue(pair.Value, out var moving) && moving.Body != null)
                {
                    // A kinematic copy's transform reaches its target when the scene steps, and reads back from the
                    // body with rounding: compare with the target instead, exactly (the pose is composed from local
                    // poses, so it does not jitter), so a slow part moves every tick and a part at rest sleeps.
                    if (!moving.Position.Equals(position) || !moving.Rotation.Equals(rotation)) MoveKinematic(frame, pair.Value, position, rotation);
                }
                else if (t.localPosition != position || t.localRotation != rotation)
                {
                    if (steps) MoveKinematic(frame, pair.Value, position, rotation);
                    else
                    {
                        t.localPosition = position;
                        t.localRotation = rotation;
                    }
                }
                bool enabled = pair.Key.enabled && pair.Key.gameObject.activeInHierarchy;
                if (pair.Value.enabled != enabled) pair.Value.enabled = enabled;
            }
        }

        /// <summary>
        /// <paramref name="source"/>'s pose relative to <paramref name="owner"/>, composed from the local poses in
        /// between. Going through world space instead would subtract two large positions for a carrier far from the
        /// origin and turn a part at rest into one that jitters by rounding error.
        /// </summary>
        private static Matrix4x4 RelativeTo(Transform source, Transform owner)
        {
            var m = Matrix4x4.identity;
            for (var t = source; t != null && t != owner; t = t.parent) m = Matrix4x4.TRS(t.localPosition, t.localRotation, t.localScale) * m;
            return m;
        }

        /// <summary>Move a copy to a frame-local pose as a kinematic body, turning it into one the first time.</summary>
        private static void MoveKinematic(PhysicsFrame frame, Collider clone, Vector3 localPosition, Quaternion localRotation)
        {
            if (!frame.MovingClones.TryGetValue(clone, out var moving) || moving.Body == null)
            {
                var body = clone.GetComponent<Rigidbody>();
                if (body == null) body = clone.gameObject.AddComponent<Rigidbody>();
                body.isKinematic = true;
                body.useGravity = false;
                body.interpolation = RigidbodyInterpolation.None;
                moving.Body = body;
            }
            moving.Position = localPosition;
            moving.Rotation = localRotation;
            frame.MovingClones[clone] = moving;
            var root = frame.Root;
            moving.Body.MovePosition(root.TransformPoint(localPosition));
            moving.Body.MoveRotation(root.rotation * localRotation);
        }

        // ------------------------------------------------------------------------------------ per tick

        /// <summary>
        /// Bring every frame's interior colliders in line with their sources: on a worker before anything simulates,
        /// on a client every rendered frame, since a door animates there too and the pawn predicts against the copies.
        /// </summary>
        internal static void SyncAllContent()
        {
            PruneDestroyed();
            for (int i = 0; i < Frames.Count; i++) SyncContent(Frames[i]);
        }

        /// <summary>Every process holding frames: sample each frame's motion (D14).</summary>
        internal static void UpdateStates(uint tick, float dt)
        {
            PruneDestroyed();
            for (int i = 0; i < Frames.Count; i++)
                if (Frames[i].Owner != null) Frames[i].Sample(tick, dt);
        }

        /// <summary>Worker: step every frame's physics scene (a frame scene is never auto-simulated).</summary>
        internal static void Simulate(float dt)
        {
            for (int i = 0; i < Frames.Count; i++)
            {
                var s = Frames[i].Scene;
                if (Frames[i].OwnsScene && s.IsValid() && s.isLoaded) s.GetPhysicsScene().Simulate(dt);
            }
        }

        // ------------------------------------------------------------------------------------ spaces

        /// <summary>
        /// A point in space <paramref name="from"/> expressed in space <paramref name="to"/>. A space is a framed
        /// container (its frame's local coordinates) or null (the scope's own space). Walks up from
        /// <paramref name="from"/> to the space both share and down into <paramref name="to"/> through each frame
        /// owner's transform, so it is exact at this instant in simulation space — which is where a worker is, always.
        /// </summary>
        public static Vector3 Convert(Vector3 point, Container from, Container to)
        {
            if (from == to) return point;
            var common = CommonSpace(from, to);
            for (var s = from; s != common && s != null; s = s.Space) point = s.transform.TransformPoint(point - OffsetOf(s));
            if (to == common) return point;
            Descend(ref point, to, common);
            return point;
        }

        /// <summary>Where a space's frame root sits in simulation space (its floating origin, D19), zero in render space.</summary>
        private static Vector3 OffsetOf(Container space) => space != null && space.Frame != null ? space.Frame.RootOffset : Vector3.zero;

        /// <summary>A rotation in space <paramref name="from"/> expressed in space <paramref name="to"/>.</summary>
        public static Quaternion Convert(Quaternion rotation, Container from, Container to)
        {
            if (from == to) return rotation;
            var common = CommonSpace(from, to);
            for (var s = from; s != common && s != null; s = s.Space) rotation = s.transform.rotation * rotation;
            if (to == common) return rotation;
            _chain.Clear();
            for (var s = to; s != common && s != null; s = s.Space) _chain.Add(s);
            for (int i = _chain.Count - 1; i >= 0; i--) rotation = Quaternion.Inverse(_chain[i].transform.rotation) * rotation;
            _chain.Clear();
            return rotation;
        }

        /// <summary>
        /// A velocity of a body at <paramref name="point"/> (in space <paramref name="from"/>) expressed in space
        /// <paramref name="to"/>: rotated, plus the motion of every frame left (its velocity and ω × r), minus the
        /// motion of every frame entered, from each frame's <see cref="PhysicsFrame.State"/>.
        /// </summary>
        public static Vector3 ConvertVelocity(Vector3 velocity, Vector3 point, Container from, Container to)
        {
            if (from == to) return velocity;
            var common = CommonSpace(from, to);
            for (var s = from; s != common && s != null; s = s.Space)
            {
                var state = s.Frame != null ? s.Frame.State : default;
                point -= OffsetOf(s);
                velocity = s.transform.rotation * velocity;
                if (s.Frame != null && state.HasRates) velocity += state.Velocity + Vector3.Cross(state.AngularVelocity, s.transform.rotation * point);
                point = s.transform.TransformPoint(point);
            }
            if (to == common) return velocity;
            _chain.Clear();
            for (var s = to; s != common && s != null; s = s.Space) _chain.Add(s);
            for (int i = _chain.Count - 1; i >= 0; i--)
            {
                var s = _chain[i];
                var state = s.Frame != null ? s.Frame.State : default;
                if (s.Frame != null && state.HasRates) velocity -= state.Velocity + Vector3.Cross(state.AngularVelocity, point - s.transform.position);
                velocity = Quaternion.Inverse(s.transform.rotation) * velocity;
                point = s.transform.InverseTransformPoint(point) + OffsetOf(s);
            }
            _chain.Clear();
            return velocity;
        }

        private static readonly List<Container> _chain = new List<Container>();

        private static void Descend(ref Vector3 point, Container to, Container common)
        {
            _chain.Clear();
            for (var s = to; s != common && s != null; s = s.Space) _chain.Add(s);
            for (int i = _chain.Count - 1; i >= 0; i--) point = _chain[i].transform.InverseTransformPoint(point) + OffsetOf(_chain[i]);
            _chain.Clear();
        }

        // ------------------------------------------------------------------------------------ floating origin (D19)

        /// <summary>
        /// How far, in metres, the entities a worker simulates in a frame may drift from the frame's origin before
        /// <see cref="AutoShift"/> moves the origin to them. Physics keeps millimetre precision well past this.
        /// </summary>
        public static float OriginShiftThreshold = 2048f;

        /// <summary>The grid a moved origin snaps to, in metres, so two workers that shift for the same crowd agree.</summary>
        public static float OriginShiftStep = 1024f;

        /// <summary>
        /// Move <paramref name="frame"/>'s floating origin to the frame-local point <paramref name="origin"/> (D19): its
        /// root, and with it everything in the frame, moves in simulation space so that point sits at Unity's origin.
        /// Frame-local coordinates do not change, so nothing on the wire does. The pose history and interpolation
        /// buffers of the entities in the frame are moved with it, and <see cref="PhysicsFrame.Shifted"/> reports the
        /// delta for anything else. Worker only: a client renders every frame at its world pose.
        /// </summary>
        public static void ShiftOrigin(PhysicsFrame frame, Vector3 origin)
        {
            if (frame == null || frame.Root == null || RendersFrames) return;
            var delta = frame.Origin - origin;
            if (delta == Vector3.zero) return;
            frame.Origin = origin;
            frame.Root.localPosition += delta;
            NetworkIdentity.ShiftFrameIn(frame.Owner, delta);
            ContainerRegistry.RefreshCaches();
            Physics.SyncTransforms();
            frame.RaiseShifted(delta);
        }

        /// <summary>
        /// Worker: keep each frame's origin near what this worker simulates in it. <paramref name="positions"/> is
        /// called for every frame and appends the frame-local positions of the entities simulated here; when their mean
        /// is more than <see cref="OriginShiftThreshold"/> from the origin, the origin moves to it, snapped to
        /// <see cref="OriginShiftStep"/>.
        /// </summary>
        internal static void AutoShift(Action<PhysicsFrame, List<Vector3>> positions)
        {
            if (RendersFrames || Frames.Count == 0 || positions == null) return;
            for (int i = 0; i < Frames.Count; i++)
            {
                var frame = Frames[i];
                _points.Clear();
                positions(frame, _points);
                if (_points.Count == 0) continue;
                var mean = Vector3.zero;
                for (int p = 0; p < _points.Count; p++) mean += _points[p];
                mean /= _points.Count;
                if (NextOrigin(frame, mean, out var snapped)) ShiftOrigin(frame, snapped);
            }
            _points.Clear();
        }

        /// <summary>
        /// Where <paramref name="frame"/>'s origin goes for what is simulated about the frame-local point
        /// <paramref name="near"/>: nowhere (false) while it is within <see cref="OriginShiftThreshold"/> of the origin,
        /// else <paramref name="near"/> snapped to <see cref="OriginShiftStep"/>, level with the origin for a frame that
        /// keeps it level. The worker's rule and the predicting client's, so both simulate about the same point.
        /// </summary>
        internal static bool NextOrigin(PhysicsFrame frame, Vector3 near, out Vector3 origin)
        {
            origin = frame.Origin;
            if (frame.KeepOriginLevel) near.y = frame.Origin.y;
            if ((near - frame.Origin).magnitude <= OriginShiftThreshold) return false;
            float step = Mathf.Max(1f, OriginShiftStep);
            origin = new Vector3(Mathf.Round(near.x / step) * step, Mathf.Round(near.y / step) * step, Mathf.Round(near.z / step) * step);
            if (frame.KeepOriginLevel) origin.y = frame.Origin.y;
            return true;
        }

        private static readonly List<Vector3> _points = new List<Vector3>();

        /// <summary>The innermost space both lie in (null: the scope's own space).</summary>
        public static Container CommonSpace(Container a, Container b)
        {
            int hops = 0;
            for (var x = a; x != null && hops <= ContainerRegistry.ChainBound; x = x.Space, hops++)
            {
                int inner = 0;
                for (var y = b; y != null && inner <= ContainerRegistry.ChainBound; y = y.Space, inner++)
                    if (x == y) return x;
            }
            return null;
        }

        /// <summary>
        /// A position given in space <paramref name="space"/> as a position in the scope's own space: what a game
        /// uses to relate something inside a frame to something outside it (a shot fired out through a window, a
        /// distance for a sound). Simulation space; on a client in render space every position is already a scope
        /// position.
        /// </summary>
        public static Vector3 ToScope(Vector3 position, Container space) => InSimulationPose(space) ? Convert(position, space, null) : position;

        /// <summary>The inverse of <see cref="ToScope"/>.</summary>
        public static Vector3 FromScope(Vector3 position, Container space) => InSimulationPose(space) ? Convert(position, null, space) : position;

        /// <summary><see cref="ToScope(Vector3, Container)"/> for a direction (rotated only).</summary>
        public static Vector3 DirectionToScope(Vector3 direction, Container space) => InSimulationPose(space) ? Convert(Quaternion.identity, space, null) * direction : direction;

        /// <summary><see cref="FromScope(Vector3, Container)"/> for a direction (rotated only).</summary>
        public static Vector3 DirectionFromScope(Vector3 direction, Container space) => InSimulationPose(space) ? Convert(Quaternion.identity, null, space) * direction : direction;

        /// <summary>
        /// The collider a frame's interior copy was made from (D12): a raycast inside a frame hits the copies, and game
        /// code that looks for components on what it hit (a seat, a door button) wants the original on the carrier.
        /// Returns <paramref name="hit"/> itself when it is not a copy.
        /// </summary>
        public static Collider SourceOf(Collider hit) => hit != null && CloneSources.TryGetValue(hit, out var source) && source != null ? source : hit;

        /// <summary>
        /// Whether <paramref name="space"/>'s frame root is at its simulation pose right now (the identity less the
        /// frame's floating origin, <see cref="PhysicsFrame.LocalToSimulation"/>): always on a worker, and on a client
        /// during a prediction step (<see cref="BeginSimulation(Container, Transform)"/>). False for the scope's own space.
        /// </summary>
        public static bool InSimulationPose(Container space) => space != null && space.Frame != null && (!RendersFrames || _simulating == space.Frame);

        /// <summary>
        /// Ask the crossing policy about one crossing (D16). An exception in the game's policy allows the crossing: a
        /// throwing hook must not trap an entity on one side for ever.
        /// </summary>
        internal static FrameCrossing Ask(NetworkIdentity entity, PhysicsFrame frame, bool leaving, Container from, Container to)
        {
            var policy = CrossingPolicy;
            if (policy == null) return FrameCrossing.Allow;
            try { return policy.Decide(entity, frame, leaving, from, to); }
            catch (Exception e)
            {
                NebulaLog.Error($"IFrameCrossingPolicy.Decide threw for {entity}: {e}");
                return FrameCrossing.Allow;
            }
        }

        // ------------------------------------------------------------------------------------ client composition (D11, D25)

        /// <summary>
        /// How far (metres) the render anchor may be from a frame's own origin, and later from its render origin,
        /// before the frame is drawn about a render origin near the anchor (D25). A ship's frame, whose content is a
        /// few hundred metres across, never needs one. <see cref="float.PositiveInfinity"/> turns render origins off.
        /// </summary>
        public static float RenderOriginThreshold = 2048f;

        /// <summary>The grid a render origin snaps to (metres), so it moves only as the anchor travels.</summary>
        public static float RenderOriginStep = 1024f;

        /// <summary>
        /// What frames are drawn about on a client (D25): a frame whose content is drawn far from its own origin is
        /// posed about a point near this transform, so what is near it is composed without rounding through the frame's
        /// turn times a far offset. Set it to the camera when the camera can be far from the content anchor; null (the
        /// default) uses the client's content anchor (<c>NebulaClient.ActiveContentAnchor</c>: the local pawn, or
        /// whatever <c>NebulaClient.SetContentAnchor</c> was given).
        /// </summary>
        public static Transform RenderAnchor { get; set; }

        /// <summary>The client's content anchor, handed over by <see cref="NebulaClient"/> before it poses the frames.</summary>
        internal static Transform ContentAnchor { get; set; }

        private static Transform ActiveRenderAnchor => RenderAnchor != null ? RenderAnchor : ContentAnchor;

        /// <summary>
        /// Client: put every frame root at its frame's world pose, outermost frames first, so the camera sees one
        /// world. Called by <see cref="NebulaClient"/> every rendered frame after remote entities were interpolated,
        /// and after every origin shift of the scope (<c>RuntimeGrid.ShiftOrigin</c>, <c>RuntimeGrid.ShiftOriginTo</c>),
        /// so what stands in a frame moves with the origin at once. A frame that is simulating (a prediction step) is
        /// left at its simulation pose. A frame drawn far from its own origin is posed about a render origin (D25).
        /// </summary>
        public static void PoseForRender()
        {
            if (!RendersFrames || Frames.Count == 0) return;
            PoseOrder.Clear();
            PoseOrder.AddRange(Frames);
            PoseOrder.Sort((a, b) => SpaceDepth(a.Owner).CompareTo(SpaceDepth(b.Owner)));
            for (int i = 0; i < PoseOrder.Count; i++)
                if (PoseOrder[i] != _simulating) PoseForRender(PoseOrder[i]);
            PoseOrder.Clear();
            // Queries made while rendering (the crosshair's interaction ray) must meet the frames where they are drawn.
            Physics.SyncTransforms();
        }

        /// <summary>
        /// Pose one frame for drawing (D11, D25). With a render origin O, the pivot goes to (T + R(S O), R, S), worked
        /// out in double, and the root hangs under it at (-O, identity): composed from the leaf up, a child's
        /// frame-local offset meets -O first, where the two cancel exactly, and only the small difference is turned.
        /// The root's world pose is still (T, R) to float precision; <see cref="PhysicsFrame.RenderPosition"/> holds it
        /// exactly. With no render origin the pivot is at (T, R, S) and the root at the identity under it.
        /// </summary>
        private static void PoseForRender(PhysicsFrame frame)
        {
            if (frame.Root == null || frame.Owner == null) return;
            var t = frame.Owner.transform;
            var position = t.position;
            var rotation = t.rotation;
            var scale = t.lossyScale;
            frame._renderPosition = position;
            frame._renderRotation = rotation;
            frame._renderScale = scale;
            frame._posed = true;
            var pivot = frame.Pivot;
            if (pivot == null || frame.Root.parent != pivot)
            {
                // A root someone else reparented: posed directly, as before render origins.
                frame.RenderOrigin = Vector3.zero;
                frame.Root.SetPositionAndRotation(position, rotation);
                frame.Root.localScale = scale;
                return;
            }
            var origin = RenderOriginOf(frame);
            frame.RenderOrigin = origin;
            if (origin == Vector3.zero) pivot.SetPositionAndRotation(position, rotation);
            else pivot.SetPositionAndRotation(frame.LocalToRenderPrecise(Double3.From(origin)).ToVector3(), rotation);
            pivot.localScale = scale;
            frame.Root.SetLocalPositionAndRotation(-origin, Quaternion.identity);
            frame.Root.localScale = Vector3.one;
        }

        /// <summary>
        /// Where <paramref name="frame"/> is drawn about (D25), its render pose already taken: zero when render origins
        /// are off for it or the anchor is within <see cref="RenderOriginThreshold"/> of the frame's own origin; else the
        /// current render origin while the anchor is within the threshold of it, and the anchor's frame-local position
        /// snapped to <see cref="RenderOriginStep"/> once it is not. No anchor, or one inside a frame that is
        /// simulating (whose position is not a drawn one), keeps the current origin.
        /// </summary>
        private static Vector3 RenderOriginOf(PhysicsFrame frame)
        {
            float threshold = RenderOriginThreshold;
            if (!frame.UseRenderOrigin || float.IsNaN(threshold) || float.IsInfinity(threshold)) return Vector3.zero;
            var anchor = ActiveRenderAnchor;
            if (anchor == null) return frame.RenderOrigin;
            Vector3 local;
            if (IsUnder(anchor, frame.Root))
                local = frame.Root.InverseTransformPoint(anchor.position); // frame-local at either pose of the root
            else if (_simulating != null && _simulating.Root != null && IsUnder(anchor, _simulating.Root))
                return frame.RenderOrigin;
            else
                local = frame.RenderToLocal(anchor.position);
            if (local.magnitude <= threshold) return Vector3.zero;
            var current = frame.RenderOrigin;
            if ((local - current).magnitude <= threshold) return current;
            float step = Mathf.Max(1f, RenderOriginStep);
            return new Vector3(Mathf.Round(local.x / step) * step, Mathf.Round(local.y / step) * step, Mathf.Round(local.z / step) * step);
        }

        private static bool IsUnder(Transform t, Transform root)
        {
            int hops = 0;
            for (var p = t != null ? t.parent : null; p != null && hops < 256; p = p.parent, hops++)
                if (p == root) return true;
            return false;
        }

        /// <summary>
        /// <paramref name="v"/> turned by the float rotation <paramref name="q"/>, in double. The quaternion is normalised
        /// in double first: a float one is a few 1e-8 off unit length, which would scale 200 km by a centimetre.
        /// </summary>
        internal static Double3 Rotate(Quaternion q, Double3 v)
        {
            double qx = q.x, qy = q.y, qz = q.z, qw = q.w;
            double n = Math.Sqrt(qx * qx + qy * qy + qz * qz + qw * qw);
            if (n > 0.0 && n != 1.0) { qx /= n; qy /= n; qz /= n; qw /= n; }
            // t = 2 (q x v); v' = v + w t + q x t
            double tx = 2.0 * (qy * v.Z - qz * v.Y);
            double ty = 2.0 * (qz * v.X - qx * v.Z);
            double tz = 2.0 * (qx * v.Y - qy * v.X);
            return new Double3(
                v.X + qw * tx + (qy * tz - qz * ty),
                v.Y + qw * ty + (qz * tx - qx * tz),
                v.Z + qw * tz + (qx * ty - qy * tx));
        }

        /// <summary>
        /// Client: put <paramref name="space"/>'s frame root at its simulation pose for a prediction step, so the
        /// predicted pawn simulates exactly as its worker does (D11): the identity less the frame's floating origin.
        /// Pair with <see cref="EndSimulation"/>. <see cref="BeginSimulation(Container, Transform)"/> first moves that
        /// origin near what is predicted.
        /// </summary>
        public static void BeginSimulation(Container space) => BeginSimulation(space, null);

        /// <summary>
        /// Client: <see cref="BeginSimulation(Container)"/> for <paramref name="predicted"/> (the pawn, or a vehicle the
        /// client drives), the frame's floating origin first following it by the worker's rule (D19,
        /// <see cref="NextOrigin"/>). A worker holds what it simulates far out in a large frame (a planet's ground, a
        /// few hundred kilometres from its centre) about an origin near it; at the frame's own coordinates floats are a
        /// centimetre or more apart, and a client predicting there drifted from its worker by centimetres every replay.
        /// The origin moves on the client's frame alone: nothing the client records is in simulation space (predicted
        /// history is container-local), so nothing is shifted and <see cref="PhysicsFrame.Shifted"/> is not raised.
        /// The frame's pivot goes to the identity first, so the root's simulation pose is set exactly, never through a
        /// turned render origin (D25).
        /// </summary>
        public static void BeginSimulation(Container space, Transform predicted)
        {
            if (!RendersFrames) return;
            var frame = space != null ? space.Frame : null;
            _simulating = frame;
            if (frame == null || frame.Root == null) return;
            // The root is at its render pose here, so this is the predicted entity's frame-local position.
            if (predicted != null && NextOrigin(frame, frame.Root.InverseTransformPoint(predicted.position), out var origin)) frame.Origin = origin;
            if (frame.Pivot != null && frame.Root.parent == frame.Pivot)
            {
                frame.Pivot.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                frame.Pivot.localScale = Vector3.one;
                frame.Root.SetLocalPositionAndRotation(-frame.Origin, Quaternion.identity);
            }
            else frame.Root.SetPositionAndRotation(-frame.Origin, Quaternion.identity);
            frame.Root.localScale = Vector3.one;
            Physics.SyncTransforms();
        }

        /// <summary>Client: the prediction step is over; the frame goes back to its render pose.</summary>
        public static void EndSimulation()
        {
            var frame = _simulating;
            _simulating = null;
            if (frame == null || !RendersFrames) return;
            PoseForRender(frame);
            Physics.SyncTransforms();
        }

        private static int SpaceDepth(Container c)
        {
            int depth = 0;
            for (var s = c != null ? c.Space : null; s != null && depth <= ContainerRegistry.ChainBound; s = s.Space) depth++;
            return depth;
        }
    }
}
