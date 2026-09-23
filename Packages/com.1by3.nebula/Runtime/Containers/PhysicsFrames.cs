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
    /// reads it one replication delay late. Nebula applies no fictitious forces: whether crates slide when the ship
    /// brakes is the game's call, made from <see cref="LocalAcceleration"/> and <see cref="LocalAngularVelocity"/>.
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
        /// planet's frame is large, and physics a few thousand kilometres from Unity's origin would be imprecise.
        /// </summary>
        public Vector3 Origin { get; internal set; }

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
        internal GameObject Content;
        internal bool OwnsScene;

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
            if (_samples > 0 && dt > 0f)
            {
                var velocity = (position - _state.Position) / dt;
                (rotation * Quaternion.Inverse(_state.Rotation)).ToAngleAxis(out float degrees, out Vector3 axis);
                if (degrees > 180f) degrees -= 360f;
                var angular = float.IsNaN(axis.x) || Mathf.Approximately(degrees, 0f) ? Vector3.zero : axis * (degrees * Mathf.Deg2Rad / dt);
                _state.Acceleration = _samples > 1 ? (velocity - _lastVelocity) / dt : Vector3.zero;
                _state.Velocity = velocity;
                _state.AngularVelocity = angular;
                _lastVelocity = velocity;
                _state.HasRates = true;
            }
            _state.Position = position;
            _state.Rotation = rotation;
            _state.Tick = tick;
            _samples++;
        }

        internal void ResetMotion()
        {
            _samples = 0;
            _state = new PhysicsFrameState { Position = Owner.transform.position, Rotation = Owner.transform.rotation };
        }

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
            var root = new GameObject("Frame: " + owner.ContainerId);
            if (frame.Scene.IsValid()) SceneManager.MoveGameObjectToScene(root, frame.Scene);
            else if (owner.gameObject.scene.IsValid() && root.scene != owner.gameObject.scene) SceneManager.MoveGameObjectToScene(root, owner.gameObject.scene);
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
            foreach (var pair in frame.Clones) if (!ReferenceEquals(pair.Value, null)) CloneSources.Remove(pair.Value);
            frame.Clones.Clear();
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
            SyncContent(frame);
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
        /// </summary>
        internal static void SyncContent(PhysicsFrame frame)
        {
            if (frame.Clones.Count == 0 || frame.Owner == null) return;
            var toOwner = frame.Owner.transform.worldToLocalMatrix;
            for (int i = frame.Clones.Count - 1; i >= 0; i--)
            {
                var pair = frame.Clones[i];
                if (pair.Key == null || pair.Value == null)
                {
                    if (!ReferenceEquals(pair.Value, null)) CloneSources.Remove(pair.Value);
                    if (pair.Value != null) Destroy(pair.Value.gameObject);
                    frame.Clones.RemoveAt(i);
                    continue;
                }
                var m = toOwner * pair.Key.transform.localToWorldMatrix;
                var t = pair.Value.transform;
                var position = (Vector3)m.GetColumn(3);
                var rotation = m.rotation;
                var scale = m.lossyScale;
                if (t.localPosition != position || t.localRotation != rotation || t.localScale != scale)
                {
                    t.localPosition = position;
                    t.localRotation = rotation;
                    t.localScale = scale;
                }
                bool enabled = pair.Key.enabled && pair.Key.gameObject.activeInHierarchy;
                if (pair.Value.enabled != enabled) pair.Value.enabled = enabled;
            }
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
            frame.Root.position += delta;
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
                if ((mean - frame.Origin).magnitude <= OriginShiftThreshold) continue;
                float step = Mathf.Max(1f, OriginShiftStep);
                var snapped = new Vector3(Mathf.Round(mean.x / step) * step, Mathf.Round(mean.y / step) * step, Mathf.Round(mean.z / step) * step);
                ShiftOrigin(frame, snapped);
            }
            _points.Clear();
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
        /// Whether <paramref name="space"/>'s frame root is at the identity pose right now, so a position read from a
        /// transform inside it is frame-local: always on a worker, and on a client during a prediction step
        /// (<see cref="BeginSimulation"/>). False for the scope's own space.
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

        // ------------------------------------------------------------------------------------ client composition (D11)

        /// <summary>
        /// Client: put every frame root at its frame's world pose, outermost frames first, so the camera sees one
        /// world. Called by <see cref="NebulaClient"/> every rendered frame after remote entities were interpolated.
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

        private static void PoseForRender(PhysicsFrame frame)
        {
            if (frame.Root == null || frame.Owner == null) return;
            var t = frame.Owner.transform;
            frame.Root.SetPositionAndRotation(t.position, t.rotation);
            frame.Root.localScale = t.lossyScale;
        }

        /// <summary>
        /// Client: put <paramref name="space"/>'s frame root back at the identity pose for a prediction step, so the
        /// predicted pawn simulates exactly as its worker does (D11). Pair with <see cref="EndSimulation"/>.
        /// </summary>
        public static void BeginSimulation(Container space)
        {
            if (!RendersFrames) return;
            var frame = space != null ? space.Frame : null;
            _simulating = frame;
            if (frame == null || frame.Root == null) return;
            frame.Root.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
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
