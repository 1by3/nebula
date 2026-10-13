using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Nebula
{
    /// <summary>
    /// Raycasts, sweeps and overlaps that continue through an open portal (<c>docs/portals.md</c> D32, D33). Get it from <see cref="NebulaWorker.PortalQueries"/> or
    /// <see cref="NebulaClient.PortalQueries"/>: every process keeps its own, because what it can answer is the scenes it holds.
    /// <para>
    /// A query names the scope it starts in and who is asking (<see cref="PortalAsker"/>), and runs in that scope's physics scene, ignoring portal proxies. If the cast
    /// meets the cross-section of a link that is open for the asker's party before it hits anything, it continues in the far scope's scene from the plane point, carried
    /// through the link, for the distance that is left, and stops at the next aperture: one portal deep. A cast that meets a closed aperture hits it on the plane
    /// (<see cref="PortalHitFlags.Aperture"/>). Each <see cref="PortalHit"/> says which scope it is in, and its point in the asker's space and in that scope's.
    /// </para>
    /// <para>
    /// With a tick in the <see cref="PortalQueryOptions"/> a worker poses the far scope's entities at the pose they had then for the far leg
    /// (<see cref="HistoryRewind"/>), and the near scope's too with <see cref="PortalQueryOptions.RewindNear"/>. When the far scope is on another worker the
    /// query tests the part of the far leg that is in the band (the ghost proxies of the far bodies) and a cast that leaves the band is returned
    /// <see cref="PortalHitFlags.Truncated"/>. A query allocates nothing beyond the hit buffer the caller provides.
    /// </para>
    /// </summary>
    public sealed class PortalQueries
    {
        private static readonly Dictionary<string, ulong> Hashes = new Dictionary<string, ulong>();
        private static readonly PortalQueryOptions DefaultOptions = new PortalQueryOptions();

        /// <summary>The cached hash of a party (a query compares it with the links' and must not build a string for it each call).</summary>
        internal static ulong HashOf(string party)
        {
            if (!Hashes.TryGetValue(party, out ulong hash)) Hashes[party] = hash = PortalIds.PartyHash(party);
            return hash;
        }

        private readonly PortalPhysics _physics;
        private readonly NebulaWorker _worker;
        private readonly List<QFace> _faces = new List<QFace>(16);
        private readonly PortalHit[] _one = new PortalHit[1];
        private PortalCrossing _crossing;
        private readonly Collider[] _overlaps = new Collider[256];

        internal PortalQueries(PortalPhysics physics, NebulaWorker worker)
        {
            _physics = physics;
            _worker = worker;
        }

        internal HistoryRewinder Rewinder => _physics.Rewinder;

        // ------------------------------------------------------------------------------------------ rays

        /// <summary>
        /// The first thing a ray hits, continuing through an open portal (<c>docs/portals.md</c> D32).
        /// </summary>
        /// <param name="asker">Who is asking: its colliders are skipped and its party chooses the link.</param>
        /// <param name="scope">The scope the ray starts in (0 is the public world).</param>
        /// <param name="origin">The start, in the coordinates of the scope's physics scene (the simulation space of <see cref="PortalQueryOptions.Space"/> when that is set).</param>
        /// <param name="direction">The direction; it need not be normalised.</param>
        /// <param name="maxDistance">The length of the whole cast, in meters, summed over both legs.</param>
        /// <param name="hit">The hit: a collider, a closed aperture's plane or a <see cref="PortalHitFlags.Truncated"/> end.</param>
        /// <param name="options">Layers, rewind and frame; null for the defaults.</param>
        /// <returns>True when something was hit.</returns>
        public bool Raycast(PortalAsker asker, ulong scope, Vector3 origin, Vector3 direction, float maxDistance, out PortalHit hit, PortalQueryOptions options = null)
        {
            int n = Cast(in asker, scope, new QCast { Kind = QKind.Ray, A = origin, Rot = Quaternion.identity }, direction, maxDistance, _one, false, options);
            hit = n > 0 ? _one[0] : default;
            _one[0] = default;
            return n > 0;
        }

        /// <summary>
        /// As <see cref="Raycast(PortalAsker, ulong, Vector3, Vector3, float, out PortalHit, PortalQueryOptions)"/>, and where the cast went through an open portal: the link, the
        /// scopes, how far along the plane lay, and the ray as it continues in the far scene. A caller testing something of its own (a pose it keeps in history, a shape that is no
        /// collider) along the far leg uses it: the far ray is the straight continuation of the near one, whether or not the cast hit anything there.
        /// <paramref name="crossing"/> is <see cref="PortalCrossing.None"/> when the cast went through no portal.
        /// </summary>
        public bool Raycast(PortalAsker asker, ulong scope, Vector3 origin, Vector3 direction, float maxDistance, out PortalHit hit, out PortalCrossing crossing, PortalQueryOptions options = null)
        {
            _crossing = PortalCrossing.None;
            bool found = Raycast(asker, scope, origin, direction, maxDistance, out hit, options);
            crossing = _crossing;
            return found;
        }

        /// <summary>Every hit along a ray, nearest first, continuing through an open portal; stops at a closed aperture. At most <paramref name="hits"/>.Length are written.</summary>
        /// <returns>How many hits were written.</returns>
        public int RaycastAll(PortalAsker asker, ulong scope, Vector3 origin, Vector3 direction, float maxDistance, PortalHit[] hits, PortalQueryOptions options = null) =>
            Cast(in asker, scope, new QCast { Kind = QKind.Ray, A = origin, Rot = Quaternion.identity }, direction, maxDistance, hits, true, options);

        // ------------------------------------------------------------------------------------------ sweeps

        /// <summary>
        /// A sphere swept along a direction. The sphere's centre decides where it meets a cross-section; the sphere itself is cast in each scene
        /// (see <see cref="Raycast"/> for the parameters).
        /// </summary>
        public bool SweepSphere(PortalAsker asker, ulong scope, Vector3 center, float radius, Vector3 direction, float maxDistance, out PortalHit hit, PortalQueryOptions options = null)
        {
            int n = Cast(in asker, scope, new QCast { Kind = QKind.Sphere, A = center, Radius = radius, Rot = Quaternion.identity }, direction, maxDistance, _one, false, options);
            hit = n > 0 ? _one[0] : default;
            _one[0] = default;
            return n > 0;
        }

        /// <summary>A capsule (the two sphere centres and the radius) swept along a direction; the midpoint of its axis decides where it meets a cross-section.</summary>
        public bool SweepCapsule(PortalAsker asker, ulong scope, Vector3 point1, Vector3 point2, float radius, Vector3 direction, float maxDistance, out PortalHit hit, PortalQueryOptions options = null)
        {
            int n = Cast(in asker, scope, new QCast { Kind = QKind.Capsule, A = point1, B = point2, Radius = radius, Rot = Quaternion.identity }, direction, maxDistance, _one, false, options);
            hit = n > 0 ? _one[0] : default;
            _one[0] = default;
            return n > 0;
        }

        /// <summary>A box swept along a direction; its centre decides where it meets a cross-section. The box is turned by the link as it goes through.</summary>
        public bool SweepBox(PortalAsker asker, ulong scope, Vector3 center, Vector3 halfExtents, Quaternion orientation, Vector3 direction, float maxDistance, out PortalHit hit, PortalQueryOptions options = null)
        {
            int n = Cast(in asker, scope, new QCast { Kind = QKind.Box, A = center, Half = halfExtents, Rot = orientation }, direction, maxDistance, _one, false, options);
            hit = n > 0 ? _one[0] : default;
            _one[0] = default;
            return n > 0;
        }

        // ------------------------------------------------------------------------------------------ overlaps

        /// <summary>
        /// The colliders a sphere overlaps in its own scope, and in the far scope of every open link whose face it reaches through: the part of the sphere beyond the face,
        /// mapped through the link (<c>docs/portals.md</c> D32). A closed aperture the shape straddles is a hit on the plane. At most <paramref name="hits"/>.Length are written.
        /// </summary>
        /// <returns>How many hits were written.</returns>
        public int OverlapSphere(PortalAsker asker, ulong scope, Vector3 center, float radius, PortalHit[] hits, PortalQueryOptions options = null) =>
            Overlap(in asker, scope, new QCast { Kind = QKind.Sphere, A = center, Radius = radius, Rot = Quaternion.identity }, hits, options);

        /// <summary>As <see cref="OverlapSphere"/>, for a capsule.</summary>
        public int OverlapCapsule(PortalAsker asker, ulong scope, Vector3 point1, Vector3 point2, float radius, PortalHit[] hits, PortalQueryOptions options = null) =>
            Overlap(in asker, scope, new QCast { Kind = QKind.Capsule, A = point1, B = point2, Radius = radius, Rot = Quaternion.identity }, hits, options);

        /// <summary>As <see cref="OverlapSphere"/>, for a box.</summary>
        public int OverlapBox(PortalAsker asker, ulong scope, Vector3 center, Vector3 halfExtents, Quaternion orientation, PortalHit[] hits, PortalQueryOptions options = null) =>
            Overlap(in asker, scope, new QCast { Kind = QKind.Box, A = center, Half = halfExtents, Rot = orientation }, hits, options);

        // ------------------------------------------------------------------------------------------ the cast

        private enum QKind : byte { Ray, Sphere, Capsule, Box }

        private struct QCast
        {
            public QKind Kind;
            public Vector3 A, B, Half;
            public float Radius;
            public Quaternion Rot;

            /// <summary>The point whose path decides where the shape meets a cross-section.</summary>
            public Vector3 Reference => Kind == QKind.Capsule ? (A + B) * 0.5f : A;

            /// <summary>How far the shape reaches from its reference point.</summary>
            public float Reach => Kind == QKind.Ray ? 0.01f : Kind == QKind.Sphere ? Radius : Kind == QKind.Capsule ? (A - B).magnitude * 0.5f + Radius : Half.magnitude;

            public QCast Moved(Vector3 delta)
            {
                var c = this;
                c.A += delta;
                c.B += delta;
                return c;
            }

            public QCast Mapped(in PortalFaceFrame from, in PortalFaceFrame to, Quaternion turn)
            {
                var c = this;
                c.A = to.ToScene(from.ToLocal(A));
                c.B = to.ToScene(from.ToLocal(B));
                c.Rot = turn * Rot;
                return c;
            }
        }

        /// <summary>A map from one leg's scene back to the asker's, or into the hit scope's, about two faces (<c>docs/portals.md</c> D37).</summary>
        private struct Unfold
        {
            public bool Active;
            public PortalFaceFrame From, To;
            public Quaternion Turn;
            public Vector3 Point(Vector3 p) => Active ? To.ToScene(From.ToLocal(p)) : p;
            public Vector3 Direction(Vector3 v) => Active ? Turn * v : v;
        }

        private static bool SceneOf(ulong scope, Container space, out PhysicsScene ps)
        {
            if (space != null)
            {
                var scene = PortalSpaces.SceneOf(space);
                ps = PortalSpaces.Usable(scene) ? scene.GetPhysicsScene() : default;
            }
            else ps = InstanceScenes.PhysicsFor(scope);
            return ps.IsValid();
        }

        private static bool RawCast(PhysicsScene ps, in QCast s, Vector3 dir, float dist, int mask, QueryTriggerInteraction qti, out RaycastHit hit)
        {
            switch (s.Kind)
            {
                case QKind.Sphere: return ps.SphereCast(s.A, s.Radius, dir, out hit, dist, mask, qti);
                case QKind.Capsule: return ps.CapsuleCast(s.A, s.B, s.Radius, dir, out hit, dist, mask, qti);
                case QKind.Box: return ps.BoxCast(s.A, s.Half, dir, out hit, s.Rot, dist, mask, qti);
                default: return ps.Raycast(s.A, dir, out hit, dist, mask, qti);
            }
        }

        /// <summary>Where a shape meets a face's cross-section along a direction: the distance, when the reference point's path passes through the opening within <paramref name="maxT"/>.</summary>
        internal static bool PlaneHit(in QFace f, Vector3 reference, Vector3 dir, float maxT, out float t)
        {
            t = 0f;
            var l = f.Frame.ToLocal(reference);
            var d = f.Frame.Inverse * dir;
            float sign = f.Entering ? 1f : -1f;
            float dz = d.z * sign;
            float lz = l.z * sign;
            if (dz <= 1e-6f || lz > 0.001f) return false;
            t = Mathf.Max(0f, -lz / dz);
            if (t > maxT) return false;
            return f.Aperture.Contains(l.x + d.x * t, l.y + d.y * t);
        }

        private int Cast(in PortalAsker asker, ulong scope, QCast shape, Vector3 dir, float maxDistance, PortalHit[] buffer, bool all, PortalQueryOptions options)
        {
            var opt = options ?? DefaultOptions;
            _crossing = PortalCrossing.None;
            if (buffer == null || buffer.Length == 0 || maxDistance <= 0f || dir.sqrMagnitude < 1e-12f) return 0;
            dir.Normalize();
            int mask = opt.EffectiveMask;
            var qti = opt.Triggers;
            var space = opt.Space;
            if (!SceneOf(scope, space, out var ps)) return 0;
            int count = 0;
            float traveled = 0f, remaining = maxDistance;
            ulong currentScope = scope;
            Unfold toAsker = default;
            bool through = false;
            ulong throughLink = 0;
            for (int leg = 0; leg < 2; leg++)
            {
                _physics.GatherFaces(currentScope, space, in asker, _faces);
                var reference = shape.Reference;
                int found = -1;
                float tc = float.PositiveInfinity;
                for (int i = 0; i < _faces.Count; i++)
                {
                    var f = _faces[i];
                    if (PlaneHit(in f, reference, dir, remaining, out float t) && t < tc) { tc = t; found = i; }
                }
                float castDist = found >= 0 ? tc : remaining;
                bool rewind = opt.Tick != 0 && _worker != null && (leg > 0 || opt.RewindNear);
                RewindSession session = null;
                bool done;
                try
                {
                    if (rewind) session = _physics.Rewinder.Begin(currentScope, space, opt.Tick, asker.Entity, AreaOf(in shape, dir, castDist));
                    done = CastLeg(ps, shape, dir, castDist, mask, qti, in asker, currentScope, traveled, in toAsker, through, throughLink, rewind, buffer, ref count, all);
                }
                finally { session?.End(); }
                if (done && !all) return count;
                if (count >= buffer.Length) return count;
                if (found < 0) return count;

                // The cast reached a cross-section.
                var face = _faces[found];
                var atPlane = reference + dir * tc;
                float sign = face.Entering ? 1f : -1f;
                var rig = face.Rig;
                if (!face.Open || leg > 0)
                {
                    var flags = PortalHitFlags.Aperture | (face.Open ? PortalHitFlags.DepthLimit : PortalHitFlags.None) | (through ? PortalHitFlags.ThroughPortal : PortalHitFlags.None);
                    var normal = face.Frame.Rotation * new Vector3(0f, 0f, -sign);
                    buffer[count++] = new PortalHit(currentScope, face.ApertureCollider, null, traveled + tc, toAsker.Point(atPlane), atPlane, normal, toAsker.Direction(normal),
                        flags, rig != null ? rig.LinkId : 0UL);
                    return count;
                }

                // An open link: on into the other side, from where the shape's reference point crossed.
                shape = shape.Moved(dir * tc);
                traveled += tc;
                remaining -= tc;
                through = true;
                throughLink = rig.LinkId;
                var otherFrame = face.Entering ? rig.FarFrame : rig.NearFrame;
                var turn = face.Entering ? rig.TurnNearToFar : rig.TurnFarToNear;
                ulong otherScope = face.Entering ? rig.FarScope : rig.NearScope;
                if (otherFrame.Valid)
                {
                    var farAt = shape.Mapped(face.Frame, otherFrame, turn).Reference;
                    _crossing = new PortalCrossing(rig.LinkId, currentScope, otherScope, traveled, farAt, (turn * dir).normalized);
                }
                if (_physics.OtherSide(rig, face.Entering, out var otherScene, out var otherSpace))
                {
                    toAsker = new Unfold { Active = true, From = otherFrame, To = face.Frame, Turn = Quaternion.Inverse(turn) };
                    shape = shape.Mapped(face.Frame, otherFrame, turn);
                    dir = (turn * dir).normalized;
                    ps = otherScene.GetPhysicsScene();
                    currentScope = otherScope;
                    space = otherSpace;
                    continue;
                }
                BandLeg(ps, shape, dir, remaining, in face, otherScope, otherFrame, turn, traveled, in toAsker, in asker, opt, buffer, ref count, all);
                return count;
            }
            return count;
        }

        private static Bounds AreaOf(in QCast shape, Vector3 dir, float distance)
        {
            var start = shape.Reference;
            var area = new Bounds(start, Vector3.zero);
            area.Encapsulate(start + dir * distance);
            area.Expand(shape.Reach * 2f + 0.2f);
            return area;
        }

        /// <summary>
        /// Cast in one scene for one leg, up to <paramref name="distance"/>, skipping what is not a hit (the asker's own colliders). Writes the hits it finds to the
        /// buffer: all of them in order when asked, otherwise only the first. Returns true when it wrote the first.
        /// </summary>
        private bool CastLeg(PhysicsScene ps, QCast shape, Vector3 dir, float distance, int mask, QueryTriggerInteraction qti, in PortalAsker asker, ulong scope, float traveled,
            in Unfold toAsker, bool through, ulong throughLink, bool rewound, PortalHit[] buffer, ref int count, bool all)
        {
            float offset = 0f;
            var origin = shape;
            bool wrote = false;
            for (int guard = 0; guard < 64 && count < buffer.Length && offset < distance; guard++)
            {
                var from = origin.Moved(dir * offset);
                if (!RawCast(ps, in from, dir, distance - offset, mask, qti, out var h)) break;
                var collider = h.collider;
                float step = h.distance + 0.002f;
                if (collider == null) break;
                offset += step;
                if (!Resolve(collider, rewound, out var entity, out var reported)) continue;
                if (asker.Entity != null && entity == asker.Entity) continue;
                var flags = (through ? PortalHitFlags.ThroughPortal : PortalHitFlags.None) | (rewound ? PortalHitFlags.Rewound : PortalHitFlags.None);
                buffer[count++] = new PortalHit(scope, reported, entity, traveled + offset - 0.002f, toAsker.Point(h.point), h.point, h.normal, toAsker.Direction(h.normal), flags, through ? throughLink : 0UL);
                wrote = true;
                if (!all) return true;
            }
            return wrote && !all;
        }

        /// <summary>The entity and collider a physics hit stands for. False for a portal proxy, which is never a hit.</summary>
        private static bool Resolve(Collider collider, bool rewound, out NetworkIdentity entity, out Collider reported)
        {
            entity = null;
            reported = collider;
            if (rewound && collider.TryGetComponent<RewindShadow>(out var shadow))
            {
                entity = shadow.Entity;
                reported = shadow.Source != null ? shadow.Source : collider;
                return true;
            }
            if (collider.gameObject.layer == Portals.ProxyLayer || collider.GetComponent<PortalProxy>() != null) return false;
            entity = collider.GetComponentInParent<NetworkIdentity>();
            return true;
        }

        // ------------------------------------------------------------------------------------------ a far scope on another worker

        /// <summary>
        /// The far leg of a cast through a link whose far scope this process does not hold: only the band is here, as ghost proxies of the far bodies posed through the
        /// link in the scene the cast is already in (<c>docs/portals.md</c> D29, D33). Test those, and when the cast goes past the band without a hit, return it
        /// <see cref="PortalHitFlags.Truncated"/> at the band's edge.
        /// </summary>
        private void BandLeg(PhysicsScene ps, QCast shape, Vector3 dir, float remaining, in QFace face, ulong otherScope, in PortalFaceFrame otherFrame, Quaternion turn, float traveled,
            in Unfold toAsker, in PortalAsker asker, PortalQueryOptions opt, PortalHit[] buffer, ref int count, bool all)
        {
            var rig = face.Rig;
            float sign = face.Entering ? 1f : -1f;
            var toScope = new Unfold { Active = otherFrame.Valid, From = face.Frame, To = otherFrame, Turn = turn };
            var reference = shape.Reference;
            var l = face.Frame.ToLocal(reference);
            var d = face.Frame.Inverse * dir;
            var band = rig.Band;
            float exit = (band.Depth - l.z * sign) / Mathf.Max(d.z * sign, 1e-6f);
            if (d.x > 1e-6f) exit = Mathf.Min(exit, (band.HalfWidth - l.x) / d.x); else if (d.x < -1e-6f) exit = Mathf.Min(exit, (-band.HalfWidth - l.x) / d.x);
            if (d.y > 1e-6f) exit = Mathf.Min(exit, (band.HalfHeight - l.y) / d.y); else if (d.y < -1e-6f) exit = Mathf.Min(exit, (-band.HalfHeight - l.y) / d.y);
            exit = Mathf.Max(0f, exit);
            float length = Mathf.Min(remaining, exit);
            int mask = 1 << Portals.ProxyLayer;
            float offset = 0f;
            for (int guard = 0; guard < 64 && count < buffer.Length && offset < length; guard++)
            {
                var from = shape.Moved(dir * offset);
                if (!RawCast(ps, in from, dir, length - offset, mask, QueryTriggerInteraction.Ignore, out var h)) break;
                var collider = h.collider;
                float step = h.distance + 0.002f;
                if (collider == null) break;
                offset += step;
                if (!collider.TryGetComponent<PortalProxy>(out var proxy) || proxy.Authority == null || proxy.IsAperture || proxy.IsStatic || proxy.LinkId != rig.LinkId) continue;
                if (asker.Entity != null && proxy.Authority == asker.Entity) continue;
                var source = proxy.Source != null ? proxy.Source : collider;
                if ((opt.EffectiveMask & (1 << source.gameObject.layer)) == 0) continue;
                buffer[count++] = new PortalHit(otherScope, source, proxy.Authority, traveled + offset - 0.002f, toAsker.Point(h.point), toScope.Point(h.point), toScope.Direction(h.normal), toAsker.Direction(h.normal),
                    PortalHitFlags.ThroughPortal, rig.LinkId);
                if (!all) return;
            }
            if (remaining > exit + 0.01f && count < buffer.Length)
            {
                var edge = reference + dir * exit;
                buffer[count++] = new PortalHit(otherScope, null, null, traveled + exit, toAsker.Point(edge), toScope.Point(edge), toScope.Direction(-dir), toAsker.Direction(-dir),
                    PortalHitFlags.Truncated | PortalHitFlags.ThroughPortal, rig.LinkId);
            }
        }

        // ------------------------------------------------------------------------------------------ overlaps

        private int Overlap(in PortalAsker asker, ulong scope, QCast shape, PortalHit[] buffer, PortalQueryOptions options)
        {
            var opt = options ?? DefaultOptions;
            if (buffer == null || buffer.Length == 0) return 0;
            var space = opt.Space;
            if (!SceneOf(scope, space, out var ps)) return 0;
            int mask = opt.EffectiveMask;
            int count = 0;
            bool nearRewind = opt.Tick != 0 && _worker != null && opt.RewindNear;
            RewindSession session = null;
            try
            {
                if (nearRewind) session = _physics.Rewinder.Begin(scope, space, opt.Tick, asker.Entity, AreaOf(in shape, Vector3.forward, 0f));
                OverlapScene(ps, in shape, mask, opt.Triggers, in asker, scope, nearRewind, default, 0UL, false, buffer, ref count, null);
            }
            finally { session?.End(); }
            if (count >= buffer.Length) return count;

            _physics.GatherFaces(scope, space, in asker, _faces);
            int faces = _faces.Count;
            for (int i = 0; i < faces && count < buffer.Length; i++)
            {
                var face = _faces[i];
                if (!ReachesBeyond(in face, in shape, out bool straddles)) continue;
                if (!face.Open)
                {
                    if (!straddles) continue;
                    float sign = face.Entering ? 1f : -1f;
                    var normal = face.Frame.Rotation * new Vector3(0f, 0f, -sign);
                    var onPlane = face.Frame.ToScene(new Vector3(face.Frame.ToLocal(shape.Reference).x, face.Frame.ToLocal(shape.Reference).y, 0f));
                    buffer[count++] = new PortalHit(scope, face.ApertureCollider, null, 0f, onPlane, onPlane, normal, normal, PortalHitFlags.Aperture, face.Rig != null ? face.Rig.LinkId : 0UL);
                    continue;
                }
                var rig = face.Rig;
                var otherFrame = face.Entering ? rig.FarFrame : rig.NearFrame;
                var turn = face.Entering ? rig.TurnNearToFar : rig.TurnFarToNear;
                ulong otherScope = face.Entering ? rig.FarScope : rig.NearScope;
                var toAsker = new Unfold { Active = true, From = otherFrame, To = face.Frame, Turn = Quaternion.Inverse(turn) };
                if (_physics.OtherSide(rig, face.Entering, out var otherScene, out var otherSpace))
                {
                    var mapped = shape.Mapped(face.Frame, otherFrame, turn);
                    var farPs = otherScene.GetPhysicsScene();
                    bool farRewind = opt.Tick != 0 && _worker != null;
                    RewindSession far = null;
                    try
                    {
                        if (farRewind) far = _physics.Rewinder.Begin(otherScope, otherSpace, opt.Tick, asker.Entity, AreaOf(in mapped, Vector3.forward, 0f));
                        var prism = new Prism { Frame = otherFrame, HalfWidth = face.Aperture.HalfWidth, HalfHeight = face.Aperture.HalfHeight, Beyond = face.Entering ? 1f : -1f };
                        OverlapScene(farPs, in mapped, mask, opt.Triggers, in asker, otherScope, farRewind, toAsker, rig.LinkId, true, buffer, ref count, prism);
                    }
                    finally { far?.End(); }
                }
                else OverlapBand(ps, in shape, in face, otherScope, otherFrame, turn, in asker, opt, buffer, ref count);
            }
            return count;
        }

        /// <summary>The part of the far scene that belongs to the part of a shape beyond a face: the prism over the cross-section on the far scope's side.</summary>
        private struct Prism
        {
            public PortalFaceFrame Frame;
            public float HalfWidth, HalfHeight, Beyond;
        }

        /// <summary>Whether the shape's bounds reach beyond a face's plane within the opening, and whether they straddle the plane.</summary>
        private static bool ReachesBeyond(in QFace face, in QCast shape, out bool straddles)
        {
            straddles = false;
            BoundsInFace(in face.Frame, in shape, out var min, out var max);
            if (max.x < -face.Aperture.HalfWidth || min.x > face.Aperture.HalfWidth || max.y < -face.Aperture.HalfHeight || min.y > face.Aperture.HalfHeight) return false;
            float sign = face.Entering ? 1f : -1f;
            float beyond = sign > 0f ? max.z : -min.z;   // how far into the way through the shape reaches
            float behind = sign > 0f ? -min.z : max.z;   // and how far it is still on this side
            straddles = beyond >= 0f && behind >= 0f;
            return beyond >= 0f;
        }

        private static void BoundsInFace(in PortalFaceFrame frame, in QCast shape, out Vector3 min, out Vector3 max)
        {
            switch (shape.Kind)
            {
                case QKind.Sphere:
                {
                    var c = frame.ToLocal(shape.A);
                    min = c - Vector3.one * shape.Radius;
                    max = c + Vector3.one * shape.Radius;
                    return;
                }
                case QKind.Capsule:
                {
                    var a = frame.ToLocal(shape.A);
                    var b = frame.ToLocal(shape.B);
                    min = Vector3.Min(a, b) - Vector3.one * shape.Radius;
                    max = Vector3.Max(a, b) + Vector3.one * shape.Radius;
                    return;
                }
                default:
                {
                    min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                    max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                    for (int i = 0; i < 8; i++)
                    {
                        var corner = new Vector3((i & 1) == 0 ? -shape.Half.x : shape.Half.x, (i & 2) == 0 ? -shape.Half.y : shape.Half.y, (i & 4) == 0 ? -shape.Half.z : shape.Half.z);
                        var l = frame.ToLocal(shape.A + shape.Rot * corner);
                        min = Vector3.Min(min, l);
                        max = Vector3.Max(max, l);
                    }
                    return;
                }
            }
        }

        private void OverlapScene(PhysicsScene ps, in QCast shape, int mask, QueryTriggerInteraction qti, in PortalAsker asker, ulong scope, bool rewound, in Unfold toAsker, ulong linkId,
            bool through, PortalHit[] buffer, ref int count, Prism? prism)
        {
            int n;
            switch (shape.Kind)
            {
                case QKind.Sphere: n = ps.OverlapSphere(shape.A, shape.Radius, _overlaps, mask, qti); break;
                case QKind.Capsule: n = ps.OverlapCapsule(shape.A, shape.B, shape.Radius, _overlaps, mask, qti); break;
                default: n = ps.OverlapBox(shape.A, shape.Half, _overlaps, shape.Rot, mask, qti); break;
            }
            var reference = shape.Reference;
            for (int i = 0; i < n && count < buffer.Length; i++)
            {
                var collider = _overlaps[i];
                _overlaps[i] = null;
                if (collider == null || !Resolve(collider, rewound, out var entity, out var reported)) continue;
                if (asker.Entity != null && entity == asker.Entity) continue;
                if (prism.HasValue && !InPrism(prism.Value, collider.bounds)) continue;
                var point = ClosestPoint(collider, reference);
                var away = reference - point;
                var normal = away.sqrMagnitude > 1e-10f ? away.normalized : Vector3.up;
                var flags = (through ? PortalHitFlags.ThroughPortal : PortalHitFlags.None) | (rewound ? PortalHitFlags.Rewound : PortalHitFlags.None);
                buffer[count++] = new PortalHit(scope, reported, entity, 0f, toAsker.Point(point), point, normal, toAsker.Direction(normal), flags, linkId);
            }
        }

        private static bool InPrism(in Prism prism, Bounds bounds)
        {
            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            var c = bounds.center;
            var e = bounds.extents;
            for (int i = 0; i < 8; i++)
            {
                var corner = c + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z);
                var l = prism.Frame.ToLocal(corner);
                min = Vector3.Min(min, l);
                max = Vector3.Max(max, l);
            }
            if (max.x < -prism.HalfWidth - 0.01f || min.x > prism.HalfWidth + 0.01f || max.y < -prism.HalfHeight - 0.01f || min.y > prism.HalfHeight + 0.01f) return false;
            return prism.Beyond > 0f ? max.z >= -0.01f : min.z <= 0.01f;
        }

        private static Vector3 ClosestPoint(Collider collider, Vector3 point)
        {
            if (collider is BoxCollider || collider is SphereCollider || collider is CapsuleCollider || (collider is MeshCollider mesh && mesh.convex))
                return collider.ClosestPoint(point);
            return collider.bounds.ClosestPoint(point);
        }

        /// <summary>The overlap of a link whose far scope is on another worker: the part of the shape in the band, against the ghost proxies of the far bodies; reaching past the band is truncated.</summary>
        private void OverlapBand(PhysicsScene ps, in QCast shape, in QFace face, ulong otherScope, in PortalFaceFrame otherFrame, Quaternion turn, in PortalAsker asker, PortalQueryOptions opt,
            PortalHit[] buffer, ref int count)
        {
            var rig = face.Rig;
            var toScope = new Unfold { Active = otherFrame.Valid, From = face.Frame, To = otherFrame, Turn = turn };
            int n;
            int mask = 1 << Portals.ProxyLayer;
            switch (shape.Kind)
            {
                case QKind.Sphere: n = ps.OverlapSphere(shape.A, shape.Radius, _overlaps, mask, QueryTriggerInteraction.Ignore); break;
                case QKind.Capsule: n = ps.OverlapCapsule(shape.A, shape.B, shape.Radius, _overlaps, mask, QueryTriggerInteraction.Ignore); break;
                default: n = ps.OverlapBox(shape.A, shape.Half, _overlaps, shape.Rot, mask, QueryTriggerInteraction.Ignore); break;
            }
            var reference = shape.Reference;
            for (int i = 0; i < n && count < buffer.Length; i++)
            {
                var collider = _overlaps[i];
                _overlaps[i] = null;
                if (collider == null || !collider.TryGetComponent<PortalProxy>(out var proxy) || proxy.Authority == null || proxy.IsAperture || proxy.IsStatic || proxy.LinkId != rig.LinkId) continue;
                if (asker.Entity != null && proxy.Authority == asker.Entity) continue;
                var source = proxy.Source != null ? proxy.Source : collider;
                if ((opt.EffectiveMask & (1 << source.gameObject.layer)) == 0) continue;
                var point = ClosestPoint(collider, reference);
                var away = reference - point;
                var normal = away.sqrMagnitude > 1e-10f ? away.normalized : Vector3.up;
                buffer[count++] = new PortalHit(otherScope, source, proxy.Authority, 0f, point, toScope.Point(point), toScope.Direction(normal), normal, PortalHitFlags.ThroughPortal, rig.LinkId);
            }
            BoundsInFace(in face.Frame, in shape, out var min, out var max);
            float sign = face.Entering ? 1f : -1f;
            float depth = sign > 0f ? max.z : -min.z;
            if (depth > rig.Band.Depth && count < buffer.Length)
            {
                var edge = face.Frame.ToScene(new Vector3(Mathf.Clamp(face.Frame.ToLocal(reference).x, -rig.Band.HalfWidth, rig.Band.HalfWidth), Mathf.Clamp(face.Frame.ToLocal(reference).y, -rig.Band.HalfHeight, rig.Band.HalfHeight), sign * rig.Band.Depth));
                buffer[count++] = new PortalHit(otherScope, null, null, 0f, edge, toScope.Point(edge), Vector3.up, Vector3.up, PortalHitFlags.Truncated | PortalHitFlags.ThroughPortal, rig.LinkId);
            }
        }
    }
}
