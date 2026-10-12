using System.Collections.Generic;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// What a client draws and how it stands (<c>docs/portals.md</c> D40, D41), replacing <c>InstanceViews</c>. The drawn set is the pawn's scope
    /// (the view), every scope one open link away that the client may look through, the public world and the scopes with a
    /// <see cref="InstanceScenes.KeepDrawn"/> request. Each has a pose against the <b>anchor</b>, the scope whose frame the view's frame
    /// resolves to (<see cref="ScopePose"/>). While cameras draw, <see cref="Begin"/> poses the root containers of each drawn scope that is not the
    /// anchor, and the cameras when the view is not the anchor; <see cref="End"/> puts everything back exactly. The anchor's roots are never moved,
    /// so the world's static colliders stay clean and its sky and sun stay upright to the world. Drawing only: simulation, prediction and every pose
    /// a game reads stay in each scope's upright space.
    /// </summary>
    internal static class DrawnScopes
    {
        private struct Edge
        {
            public ulong Near, Far;
            public PortalMap NearToFar;
        }

        private struct Posed
        {
            public Transform Transform;
            public Vector3 LocalPosition;
            public Quaternion LocalRotation;
        }

        private static readonly List<ulong> Set = new List<ulong>();
        private static HashSet<ulong> _observed = new HashSet<ulong>();
        private static HashSet<ulong> _observedNext = new HashSet<ulong>();
        private static readonly List<ulong> Changed = new List<ulong>();
        private static readonly List<Edge> Edges = new List<Edge>();
        private static readonly List<ulong> Frontier = new List<ulong>();
        private static readonly Dictionary<ulong, PortalMap> Maps = new Dictionary<ulong, PortalMap>();
        private static readonly Dictionary<ulong, List<Transform>> RootSets = new Dictionary<ulong, List<Transform>>();
        private static readonly HashSet<Transform> Candidates = new HashSet<Transform>();
        private static readonly List<Posed> PosedList = new List<Posed>();
        private static readonly HashSet<Transform> PosedSet = new HashSet<Transform>();
        private static ulong _anchor;
        private static bool _dirty = true;
        private static bool _mapsBuilt;
        private static bool _hooked;
        private static bool _refreshing;

        /// <summary>The anchor: the scope whose frame the view's frame resolves to.</summary>
        internal static ulong Anchor
        {
            get { Ensure(); return _anchor; }
        }

        /// <summary>The drawn set, the view first.</summary>
        internal static IReadOnlyList<ulong> Drawn
        {
            get { Ensure(); return Set; }
        }

        /// <summary>Whether this client looks through an open link into the scope (it is drawn without a <see cref="InstanceScenes.KeepDrawn"/> request).</summary>
        internal static bool IsObserved(ulong scope) => _observed.Count > 0 && _observed.Contains(scope);

        /// <summary>Something that decides the drawn set changed (a link record, an instance pose): it is worked out again before it is next read.</summary>
        internal static void MarkDirty() => _dirty = true;

        /// <summary>Work the drawn set out again now and apply what changed: visibility of scopes entering or leaving it, and rendering-layer slots.</summary>
        internal static void Recompute()
        {
            _dirty = true;
            Refresh();
        }

        /// <summary>Work the set out again when something decided by a record or the view changed since it was last worked out.</summary>
        internal static void RefreshIfStale() => Ensure();

        private static void Ensure()
        {
            if (_dirty || Nebula.World.ScopeFrames.Resolve(InstanceScenes.View) != _anchor) Refresh();
        }

        private static void EnsureHooked()
        {
            if (_hooked) return;
            _hooked = true;
            ContainerRegistry.RuntimeRegistered += OnRegistryChanged;
            ContainerRegistry.RuntimeUnregistering += OnRegistryChanged;
            ContainerRegistry.DynamicRegistered += OnRegistryChanged;
            ContainerRegistry.DynamicUnregistering += OnRegistryChanged;
            ContainerRegistry.Rebuilt += OnRebuilt;
        }

        private static void OnRegistryChanged(Container c) => RootSets.Clear();
        private static void OnRebuilt() => RootSets.Clear();

        private static void Refresh()
        {
            if (_refreshing) return;
            _refreshing = true;
            try
            {
                EnsureHooked();
                _dirty = false;
                _mapsBuilt = false;
                Maps.Clear();
                ulong view = InstanceScenes.View;
                _anchor = Nebula.World.ScopeFrames.Resolve(view);

                // Scopes this client may look through: the other end of an open (or closing) link it observes, from the scope it is in.
                _observedNext.Clear();
                foreach (var link in Portals.KnownLinks.Values)
                {
                    if ((link.State != PortalState.Open && link.State != PortalState.Closing) || (link.Flags & (PortalLinkFlags.Observer | PortalLinkFlags.ObserverAnyone)) == 0) continue;
                    if (!Portals.Known.TryGetValue(link.PortalId, out var record)) continue;
                    ulong far = link.FarScope != 0 ? link.FarScope : record.FarScope;
                    if (record.NearScope == view && far != view) _observedNext.Add(far);
                    else if (far == view && record.NearScope != view) _observedNext.Add(record.NearScope);
                }
                Changed.Clear();
                foreach (ulong scope in _observedNext) if (!_observed.Contains(scope)) Changed.Add(scope);
                foreach (ulong scope in _observed) if (!_observedNext.Contains(scope)) Changed.Add(scope);
                var swap = _observed;
                _observed = _observedNext;
                _observedNext = swap;
                _observedNext.Clear();

                Set.Clear();
                Set.Add(view);
                AddOnce(0UL);
                foreach (ulong scope in _observed) AddOnce(scope);
                foreach (ulong scope in InstanceScenes.KeptScopes) AddOnce(scope);

                // Visibility follows the set: content of a scope that entered or left it is shown or hidden now.
                for (int i = 0; i < Changed.Count; i++) InstanceScenes.ApplyScope(Changed[i]);
                Changed.Clear();
                ScopeLayers.Sync(Set);
            }
            finally { _refreshing = false; }
        }

        private static void AddOnce(ulong scope)
        {
            if (!Set.Contains(scope)) Set.Add(scope);
        }

        // ------------------------------------------------------------------------------------------ poses

        /// <summary>The pose of <paramref name="scope"/> against the anchor (the identity for the anchor, and for a scope nothing relates to it).</summary>
        internal static ScopePose PoseOf(ulong scope)
        {
            Ensure();
            if (scope == _anchor) return ScopePose.Identity(scope, _anchor);
            BuildMaps();
            if (Maps.TryGetValue(scope, out var map)) return ScopePose.From(in map, _anchor);
            if (Portals.TryMap(scope, _anchor, Double3.Zero, out map)) return ScopePose.From(in map, _anchor);
            return ScopePose.Identity(scope, _anchor);
        }

        private static void BuildMaps()
        {
            if (_mapsBuilt) return;
            _mapsBuilt = true;
            Maps.Clear();
            Edges.Clear();
            foreach (var link in Portals.KnownLinks.Values)
            {
                if (link.State == PortalState.Closed || !Portals.Known.TryGetValue(link.PortalId, out var record)) continue;
                ulong far = link.FarScope != 0 ? link.FarScope : record.FarScope;
                if (far == 0 || far == record.NearScope) continue;
                // The same form as a worker's link: a point of the near scope is FarOrigin + turn (p - NearOrigin) in the far one.
                var map = new PortalMap(record.NearScope, far, record.NearOrigin, record.FarOrigin, record.FarRotation * Quaternion.Inverse(record.NearRotation), record.PortalId);
                Edges.Add(new Edge { Near = record.NearScope, Far = far, NearToFar = map });
            }
            if (Edges.Count > 0)
            {
                Maps[_anchor] = PortalMap.Identity(_anchor);
                Frontier.Clear();
                Frontier.Add(_anchor);
                for (int f = 0; f < Frontier.Count; f++)
                {
                    ulong node = Frontier[f];
                    var toAnchor = Maps[node];
                    for (int e = 0; e < Edges.Count; e++)
                    {
                        var edge = Edges[e];
                        ulong other;
                        PortalMap otherToNode;
                        if (edge.Near == node) { other = edge.Far; otherToNode = edge.NearToFar.Inverse; }
                        else if (edge.Far == node) { other = edge.Near; otherToNode = edge.NearToFar; }
                        else continue;
                        if (Maps.ContainsKey(other)) continue;
                        Maps[other] = Compose(in otherToNode, in toAnchor);
                        Frontier.Add(other);
                    }
                }
                Frontier.Clear();
                Maps.Remove(_anchor);
            }
            // A scope no link record reaches (a kept scope, an instance whose pose a lease row carried): its instance pose, when it has one.
            for (int i = 0; i < Set.Count; i++)
            {
                ulong scope = Set[i];
                if (scope == _anchor || Maps.ContainsKey(scope)) continue;
                if (Portals.TryMap(scope, _anchor, Double3.Zero, out var map)) Maps[scope] = map;
            }
        }

        /// <summary>First <paramref name="first"/>, then <paramref name="second"/>, still about the faces: <c>second.To + second.Turn (first.To - second.From)</c> is the new far origin.</summary>
        private static PortalMap Compose(in PortalMap first, in PortalMap second)
        {
            var turn = second.Turn * first.Turn;
            float m = Mathf.Sqrt(turn.x * turn.x + turn.y * turn.y + turn.z * turn.z + turn.w * turn.w);
            if (m > 1e-6f) turn = new Quaternion(turn.x / m, turn.y / m, turn.z / m, turn.w / m);
            return new PortalMap(first.FromScope, second.ToScope, first.FromOrigin, second.Point(first.ToOrigin), turn);
        }

        /// <summary>
        /// The matrix that takes a point in drawing space of <paramref name="scope"/>'s own upright space to where it is drawn
        /// (<see cref="Portals.RenderMatrixOf"/>).
        /// </summary>
        internal static Matrix4x4 RenderMatrixOf(ulong scope)
        {
            var pose = PoseOf(scope);
            if (pose.IsIdentity) return Matrix4x4.identity;
            var offset = Double3.From(SceneRenderOrigin.Offset);
            var from = Double3.From(ContainerRegistry.ToFrame(pose.FromOrigin, scope)) - offset;
            var to = Double3.From(ContainerRegistry.ToFrame(pose.ToOrigin, _anchor)) - offset;
            var translation = to - PhysicsFrames.Rotate(pose.Turn, from);
            return Matrix4x4.TRS(translation.ToVector3(), pose.Turn, Vector3.one);
        }

        // ------------------------------------------------------------------------------------------ drawing

        /// <summary>Whether anything is posed right now.</summary>
        internal static bool Active => PosedList.Count > 0;

        /// <summary>
        /// Pose what is drawn posed. <paramref name="offset"/> is how far the scene was moved back for drawing
        /// (<see cref="SceneRenderOrigin.Offset"/>): a scene position p is drawn at p - offset.
        /// </summary>
        internal static void Begin(IReadOnlyList<Camera> cameras, Vector3 offset)
        {
            if (PosedList.Count > 0) return;
            Ensure();
            if (Set.Count <= 1 && _anchor == InstanceScenes.View) return;
            BuildMaps();
            var shift = Double3.From(offset);
            for (int i = 0; i < Set.Count; i++)
            {
                ulong scope = Set[i];
                if (scope == _anchor || !Maps.TryGetValue(scope, out var map)) continue;
                var pose = ScopePose.From(in map, _anchor);
                if (pose.IsIdentity) continue;
                var from = Double3.From(ContainerRegistry.ToFrame(pose.FromOrigin, scope)) - shift;
                var to = Double3.From(ContainerRegistry.ToFrame(pose.ToOrigin, _anchor)) - shift;
                var roots = RootsOf(scope);
                for (int r = 0; r < roots.Count; r++)
                {
                    var t = roots[r];
                    if (t != null) Pose(t, from, to, pose.Turn);
                }
            }
            ulong view = InstanceScenes.View;
            if (cameras != null && view != _anchor && Maps.TryGetValue(view, out var viewMap))
            {
                var pose = ScopePose.From(in viewMap, _anchor);
                if (!pose.IsIdentity)
                {
                    var from = Double3.From(ContainerRegistry.ToFrame(pose.FromOrigin, view)) - shift;
                    var to = Double3.From(ContainerRegistry.ToFrame(pose.ToOrigin, _anchor)) - shift;
                    for (int i = 0; i < cameras.Count; i++)
                    {
                        var camera = cameras[i];
                        if (camera == null || IsUnderPosed(camera.transform)) continue;
                        Pose(camera.transform, from, to, pose.Turn);
                    }
                }
            }
        }

        /// <summary>Put back exactly everything <see cref="Begin"/> posed.</summary>
        internal static void End()
        {
            for (int i = PosedList.Count - 1; i >= 0; i--)
            {
                var posed = PosedList[i];
                if (posed.Transform == null) continue;
                posed.Transform.localPosition = posed.LocalPosition;
                posed.Transform.localRotation = posed.LocalRotation;
            }
            PosedList.Clear();
            PosedSet.Clear();
        }

        private static void Pose(Transform t, Double3 from, Double3 to, Quaternion turn)
        {
            PosedList.Add(new Posed { Transform = t, LocalPosition = t.localPosition, LocalRotation = t.localRotation });
            PosedSet.Add(t);
            var p = Double3.From(t.position);
            t.SetPositionAndRotation((to + PhysicsFrames.Rotate(turn, p - from)).ToVector3(), turn * t.rotation);
        }

        private static bool IsUnderPosed(Transform t)
        {
            int hops = 0;
            for (var p = t; p != null && hops < 256; p = p.parent, hops++) if (PosedSet.Contains(p)) return true;
            return false;
        }

        /// <summary>
        /// The root containers of a scope: its runtime containers that are not inside another container's box, and whose transforms are not
        /// under another such container's. Cached per scope, and dropped when the registry changes.
        /// </summary>
        internal static List<Transform> RootsOf(ulong scope)
        {
            EnsureHooked();
            if (RootSets.TryGetValue(scope, out var cached)) return cached;
            var roots = new List<Transform>();
            Candidates.Clear();
            foreach (var c in ContainerRegistry.Runtime)
                if (c != null && c.InstanceId == scope && c.FixedParent == null) Candidates.Add(c.transform);
            foreach (var t in Candidates)
            {
                bool nested = false;
                int hops = 0;
                for (var p = t.parent; p != null && hops < 256; p = p.parent, hops++) if (Candidates.Contains(p)) { nested = true; break; }
                if (!nested) roots.Add(t);
            }
            Candidates.Clear();
            RootSets[scope] = roots;
            return roots;
        }

        /// <summary>How many root sets are cached (tests).</summary>
        internal static int CachedRootSets => RootSets.Count;

        internal static void ResetForNewSession()
        {
            End();
            Set.Clear();
            _observed.Clear();
            _observedNext.Clear();
            Edges.Clear();
            Maps.Clear();
            RootSets.Clear();
            _anchor = 0;
            _dirty = true;
            _mapsBuilt = false;
            if (_hooked)
            {
                _hooked = false;
                ContainerRegistry.RuntimeRegistered -= OnRegistryChanged;
                ContainerRegistry.RuntimeUnregistering -= OnRegistryChanged;
                ContainerRegistry.DynamicRegistered -= OnRegistryChanged;
                ContainerRegistry.DynamicUnregistering -= OnRegistryChanged;
                ContainerRegistry.Rebuilt -= OnRebuilt;
            }
            ScopeLayers.Reset();
        }
    }
}
