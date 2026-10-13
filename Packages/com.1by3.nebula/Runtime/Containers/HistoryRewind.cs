using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Nebula
{
    /// <summary>
    /// Marks a collider <see cref="HistoryRewind"/> stood in a scene for the length of a query: an entity that is in another scope now but was in this one at the rewound
    /// tick (it crossed a portal since), its shape posed where it was. A portal query reports the entity and collider it stands for, never this.
    /// </summary>
    internal sealed class RewindShadow : MonoBehaviour
    {
        public NetworkIdentity Entity;
        public Collider Source;
    }

    /// <summary>
    /// A scene put back as it was at a past server tick, for as long as it is held (<c>docs/portals.md</c> D33; the history is <c>docs/state-history.md</c>). Dispose it to
    /// put everything back. Make it for the scope a query is about to look at, run any physics query in that scope's scene (<c>Physics</c> or a
    /// <see cref="PortalQueries"/> call), and dispose it in a <c>finally</c> or a <c>using</c>.
    /// <para>
    /// For each entity of the scope that keeps history and that has an entry for the tick: its colliders move to the pose it had (<see cref="StateHistory"/>), and are moved
    /// back afterwards; an entity with no entry (no history, too young, outside the window) stays where it is. Because the history ring is rewritten when an entity
    /// crosses a portal (D18), every entry is in the space of the scope the entity is in now, so the helper also looks at the faces of the scope: an entity of this scope
    /// whose pose at the tick lay on the other side of a face, within the distance a body is followed to a face (<see cref="Portal.ApproachDistance"/>), was in the other
    /// scope then and its colliders are switched off; an entity of the other scope whose pose at the tick, carried through the link, lay on this side was here, and a
    /// shape of it is stood in this scope's scene for the length of the query. Nothing is replicated and nothing the game owns is changed for good.
    /// </para>
    /// Not for use from another thread, and not while the scene steps.
    /// </summary>
    public struct HistoryRewind : IDisposable
    {
        internal RewindSession Session;

        /// <summary>
        /// Rewind <paramref name="scope"/>'s scene on <paramref name="worker"/> to <paramref name="tick"/>. Everything the scope holds is looked at; give a
        /// <paramref name="area"/> to look only at what could be inside a box (in the scene's own coordinates), which is what a single cast needs.
        /// </summary>
        /// <param name="worker">The worker that keeps the history.</param>
        /// <param name="scope">The scope whose scene is to be rewound (0 is the public world).</param>
        /// <param name="tick">The server tick to look at.</param>
        /// <param name="area">Only entities that were or are in this box (grown by their own reach) are moved; null for all of them.</param>
        /// <param name="exclude">An entity to leave alone: the asker, whose own colliders a query skips anyway.</param>
        /// <param name="space">The physics frame the scene is the simulation space of, or null for the scope's own scene.</param>
        public static HistoryRewind Begin(NebulaWorker worker, ulong scope, uint tick, Bounds? area = null, NetworkIdentity exclude = null, Container space = null)
        {
            if (worker == null) throw new ArgumentNullException(nameof(worker));
            return new HistoryRewind { Session = worker.PortalQueries.Rewinder.Begin(scope, space, tick, exclude, area) };
        }

        /// <summary>How many entities were moved to their pose at the tick.</summary>
        public int Posed => Session != null ? Session.PosedCount : 0;
        /// <summary>How many entities that were in another scope at the tick were switched off for the length of the rewind.</summary>
        public int Hidden => Session != null ? Session.HiddenCount : 0;
        /// <summary>How many entities of another scope were stood in this one, as shapes, because they were here at the tick.</summary>
        public int Shadowed => Session != null ? Session.ShadowedCount : 0;

        /// <summary>Put everything back.</summary>
        public void Dispose()
        {
            var session = Session;
            Session = null;
            session?.End();
        }
    }

    /// <summary>The state of one rewind, pooled: what to put back.</summary>
    internal sealed class RewindSession
    {
        private readonly HistoryRewinder _owner;
        public readonly List<(Transform transform, Vector3 position, Quaternion rotation)> Moved = new List<(Transform, Vector3, Quaternion)>();
        public readonly List<Collider> Switched = new List<Collider>();
        public readonly List<HistoryRewinder.Shadow> Shadows = new List<HistoryRewinder.Shadow>();
        public int PosedCount, HiddenCount, ShadowedCount;
        public bool Active;

        public RewindSession(HistoryRewinder owner) { _owner = owner; }

        public void End()
        {
            if (!Active) return;
            Active = false;
            for (int i = Moved.Count - 1; i >= 0; i--)
            {
                var m = Moved[i];
                if (m.transform != null) m.transform.SetPositionAndRotation(m.position, m.rotation);
            }
            for (int i = 0; i < Switched.Count; i++) if (Switched[i] != null) Switched[i].enabled = true;
            bool any = Moved.Count > 0 || Shadows.Count > 0 || Switched.Count > 0;
            _owner.Release(this);
            Moved.Clear();
            Switched.Clear();
            if (any) Physics.SyncTransforms();
        }
    }

    /// <summary>
    /// Poses a scene's entities at a past tick for a query and puts them back (<see cref="HistoryRewind"/>). One per process, owned by its
    /// <see cref="PortalQueries"/>; it allocates nothing once its lists and the shadow shapes it has built are pooled.
    /// </summary>
    internal sealed class HistoryRewinder
    {
        /// <summary>A collider stood in a scene for a query, pooled by shape.</summary>
        internal sealed class Shadow
        {
            public GameObject Go;
            public Transform T;
            public Collider Collider;
            public RewindShadow Mark;
            public PortalShapeKey Key;
            public Scene Scene;
        }

        /// <summary>A side of a link on the scope being rewound.</summary>
        private struct Side
        {
            public LinkRig Rig;
            public bool ThisIsNear;
            public PortalFaceFrame This, Other;
            public Quaternion TurnOtherToThis;
            public ulong OtherScope;
            public Container OtherSpace;
            public Scene ThisScene;
            public bool CanStand;
            public float HalfWidth, HalfHeight;
        }

        private readonly PortalPhysics _physics;
        private readonly NebulaWorker _worker;
        private readonly Stack<RewindSession> _free = new Stack<RewindSession>();
        private readonly PortalShapePool<Shadow> _pool = new PortalShapePool<Shadow>();
        private readonly List<Side> _sides = new List<Side>(4);

        // What the pool builds with when it has no free shape: kept in fields so that asking for one allocates no closure.
        private readonly Func<Shadow> _make;
        private Scene _makeScene;
        private PortalShapeKey _makeKey;
        private Collider _makeSource;

        public HistoryRewinder(PortalPhysics physics, NebulaWorker worker)
        {
            _physics = physics;
            _worker = worker;
            _make = () => Build(_makeScene, _makeKey, _makeSource);
        }

        /// <summary>Shapes built and reused, for the tests that pin the allocations.</summary>
        public int ShadowsBuilt => _pool.Built;
        public int ShadowsReused => _pool.Reused;

        public RewindSession Begin(ulong scope, Container space, uint tick, NetworkIdentity exclude, Bounds? area)
        {
            var session = _free.Count > 0 ? _free.Pop() : new RewindSession(this);
            session.Active = true;
            session.PosedCount = session.HiddenCount = session.ShadowedCount = 0;
            if (_worker == null || tick == 0) return session;
            CollectSides(scope, space);
            uint now = _worker.CurrentTick;
            bool hasArea = area.HasValue;
            var box = area ?? default;
            foreach (var e in _worker.EntityValues)
            {
                if (e == null || e == exclude || !e.IsSpawned) continue;
                ulong where = e.InstanceId;
                bool native = where == scope;
                if (native ? e.Space != space : !LinkedTo(where, e.Space)) continue;
                if (native && e.IsPortalGhost) continue; // held only as a proxy of itself: no body in its own scope's scene here
                if (!e.TryGetStateAt(tick, out var state)) continue;
                var info = _physics.Bodies.Get(e, now);
                if (info.Count == 0) continue;
                if (native) RewindNative(session, e, state, info, hasArea, box);
                else RewindForeign(session, e, state, info, hasArea, box);
            }
            if (session.Moved.Count > 0 || session.Switched.Count > 0 || session.Shadows.Count > 0) Physics.SyncTransforms();
            return session;
        }

        internal void Release(RewindSession session)
        {
            for (int i = 0; i < session.Shadows.Count; i++)
            {
                var shadow = session.Shadows[i];
                if (shadow.Go == null) continue;
                shadow.Mark.Entity = null;
                shadow.Mark.Source = null;
                shadow.Go.SetActive(false);
                _pool.Release(shadow.Scene.handle.GetRawData(), shadow.Key, shadow);
            }
            session.Shadows.Clear();
            _free.Push(session);
        }

        private void CollectSides(ulong scope, Container space)
        {
            _sides.Clear();
            foreach (var rig in _physics.RigValues)
            {
                if (rig.NearScope == rig.FarScope) continue;
                bool near = rig.NearScope == scope && rig.NearSpace == space;
                bool far = rig.FarScope == scope && rig.FarSpace == space;
                if (!near && !far) continue;
                var side = new Side
                {
                    Rig = rig,
                    ThisIsNear = near,
                    This = near ? rig.NearFrame : rig.FarFrame,
                    Other = near ? rig.FarFrame : rig.NearFrame,
                    TurnOtherToThis = near ? rig.TurnFarToNear : rig.TurnNearToFar,
                    OtherScope = near ? rig.FarScope : rig.NearScope,
                    OtherSpace = near ? rig.FarSpace : rig.NearSpace,
                    ThisScene = near ? rig.NearScene : rig.FarScene,
                    HalfWidth = rig.Aperture.HalfWidth,
                    HalfHeight = rig.Aperture.HalfHeight,
                };
                side.CanStand = side.This.Valid && side.Other.Valid && PortalSpaces.Usable(side.ThisScene) && (near ? (rig.HostsNear || _physics.IsClientSide) : (rig.HostsFar || _physics.IsClientSide));
                _sides.Add(side);
            }
        }

        private bool LinkedTo(ulong other, Container otherSpace)
        {
            for (int i = 0; i < _sides.Count; i++) if (_sides[i].OtherScope == other && _sides[i].OtherSpace == otherSpace) return true;
            return false;
        }

        private static bool NearArea(in Bounds area, Vector3 point, float radius) => area.SqrDistance(point) <= radius * radius;

        // ------------------------------------------------------------------------------------------ an entity of this scope

        private void RewindNative(RewindSession session, NetworkIdentity e, HistoricalState state, PortalBodies.Info info, bool hasArea, in Bounds area)
        {
            var root = e.transform;
            if (hasArea && !NearArea(area, state.Position, info.Radius) && !NearArea(area, root.position, info.Radius)) return;
            var stateSpace = state.Container != null ? state.Container.InnerSpace : null;
            if (stateSpace != e.Space) return; // recorded in another space than it stands in: not a pose of this scene
            // Was it on the other side of a face then? It crossed into this scope since, and the ring says where it would have been had it always been here.
            for (int i = 0; i < _sides.Count; i++)
            {
                var side = _sides[i];
                if (!side.This.Valid) continue;
                if (!OnOtherSide(in side, state.Position)) continue;
                for (int c = 0; c < info.Count; c++)
                {
                    var collider = info.Colliders[c];
                    if (collider == null || !collider.enabled) continue;
                    collider.enabled = false;
                    session.Switched.Add(collider);
                }
                session.HiddenCount++;
                return;
            }
            if ((root.position - state.Position).sqrMagnitude < 1e-6f && Quaternion.Angle(root.rotation, state.Rotation) < 0.05f) { session.PosedCount++; return; }
            session.Moved.Add((root, root.position, root.rotation));
            root.SetPositionAndRotation(state.Position, state.Rotation);
            session.PosedCount++;
        }

        /// <summary>A pose of this scope lies where the other scope's side of a face is: within the distance a body is followed to the face, on the far side of the plane.</summary>
        private static bool OnOtherSide(in Side side, Vector3 position)
        {
            var l = side.This.ToLocal(position);
            float reach = Portal.ApproachDistance;
            if (Mathf.Abs(l.x) > side.HalfWidth + reach || Mathf.Abs(l.y) > side.HalfHeight + reach) return false;
            return side.ThisIsNear ? (l.z > Portal.CrossDepth && l.z <= reach) : (l.z < -Portal.CrossDepth && l.z >= -reach);
        }

        /// <summary>A pose carried from the other scope into this one lies on this scope's side of the face (it was here then).</summary>
        private static bool OnThisSide(in Side side, Vector3 position)
        {
            var l = side.This.ToLocal(position);
            float reach = Portal.ApproachDistance;
            if (Mathf.Abs(l.x) > side.HalfWidth + reach || Mathf.Abs(l.y) > side.HalfHeight + reach) return false;
            return side.ThisIsNear ? (l.z <= Portal.CrossDepth && l.z >= -reach) : (l.z >= -Portal.CrossDepth && l.z <= reach);
        }

        // ------------------------------------------------------------------------------------------ an entity of the other scope

        private void RewindForeign(RewindSession session, NetworkIdentity e, HistoricalState state, PortalBodies.Info info, bool hasArea, in Bounds area)
        {
            var stateSpace = state.Container != null ? state.Container.InnerSpace : null;
            if (stateSpace != e.Space) return;
            for (int i = 0; i < _sides.Count; i++)
            {
                var side = _sides[i];
                if (!side.CanStand || side.OtherScope != e.InstanceId || side.OtherSpace != e.Space) continue;
                var carried = side.This.ToScene(side.Other.ToLocal(state.Position));
                if (hasArea && !NearArea(area, carried, info.Radius)) continue;
                if (!OnThisSide(in side, carried)) continue;
                StandShapes(session, e, state, info, in side);
                session.ShadowedCount++;
                return;
            }
        }

        private void StandShapes(RewindSession session, NetworkIdentity e, HistoricalState state, PortalBodies.Info info, in Side side)
        {
            var root = e.transform;
            var inverseRoot = Quaternion.Inverse(root.rotation);
            for (int i = 0; i < info.Count; i++)
            {
                var source = info.Colliders[i];
                if (source == null) continue;
                // Where the collider stands against the root now, carried to where the root stood then, and through the link into this scene.
                PortalBodies.PoseOf(e, source, info.Centers[i], info.Upright[i], false, e.Space, out var position, out var rotation);
                var relative = inverseRoot * (position - root.position);
                var past = state.Position + state.Rotation * relative;
                var pastRotation = info.Upright[i] ? Quaternion.identity : state.Rotation * (inverseRoot * rotation);
                var shadow = Acquire(side.ThisScene, in info.Keys[i], source);
                shadow.Mark.Entity = e;
                shadow.Mark.Source = source;
                shadow.T.SetPositionAndRotation(side.This.ToScene(side.Other.ToLocal(past)), side.TurnOtherToThis * pastRotation);
                shadow.Go.SetActive(true);
                session.Shadows.Add(shadow);
            }
        }

        private Shadow Acquire(Scene scene, in PortalShapeKey key, Collider source)
        {
            _makeScene = scene;
            _makeKey = key;
            _makeSource = source;
            var shadow = _pool.Acquire(scene.handle.GetRawData(), key, _make);
            if (shadow.Go == null) shadow = Build(scene, key, source); // its scene was unloaded under it
            _makeSource = null;
            shadow.Go.layer = source.gameObject.layer;
            return shadow;
        }

        private static Shadow Build(Scene scene, in PortalShapeKey key, Collider source)
        {
            var go = new GameObject("History rewind shape") { layer = source.gameObject.layer };
            go.SetActive(false);
            SceneManager.MoveGameObjectToScene(go, scene);
            var shadow = new Shadow { Go = go, T = go.transform, Key = key, Scene = scene };
            shadow.Mark = go.AddComponent<RewindShadow>();
            shadow.Collider = PortalShapes.AddCollider(go, key, source);
            return shadow;
        }

        /// <summary>Destroy the shapes this rewinder built (the process is shutting down).</summary>
        public void Dispose()
        {
            var drain = new List<Shadow>();
            _pool.Drain(drain);
            foreach (var shadow in drain) if (shadow.Go != null) UnityEngine.Object.Destroy(shadow.Go);
        }
    }
}
