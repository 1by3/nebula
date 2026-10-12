using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Nebula
{
    internal sealed partial class PortalPhysics
    {
        private readonly Collider[] _hits = new Collider[256];
        private readonly Dictionary<(ulong scene, PortalShapeKey key), int> _needed = new Dictionary<(ulong, PortalShapeKey), int>();

        // ------------------------------------------------------------------------------------------ aperture colliders (D28)

        private static PortalShapeKey ApertureKey(PortalAperture aperture) =>
            new PortalShapeKey(PortalShapeKind.Box, aperture.HalfWidth * 2f, aperture.HalfHeight * 2f, ApertureThickness, 0, null, true);

        private ProxyBody BuildAperture(Scene scene, in PortalShapeKey key, in PortalFaceFrame frame, ulong linkId)
        {
            var proxy = Build(scene, key, null, false);
            proxy.Marker.IsAperture = true;
            proxy.Marker.IsStatic = true;
            proxy.Marker.LinkId = linkId;
            proxy.Go.name = "Portal aperture";
            proxy.T.SetPositionAndRotation(frame.Origin, frame.Rotation);
            proxy.Go.SetActive(true);
            proxy.Posed = true;
            return proxy;
        }

        private static bool Matches(ProxyBody aperture, in PortalShapeKey key, Scene scene, in PortalFaceFrame frame) =>
            aperture != null && aperture.Go != null && aperture.Key.Equals(key) && aperture.Scene == scene &&
            aperture.T.position == frame.Origin && aperture.T.rotation == frame.Rotation;

        /// <summary>The thin box over a portal's cross-section in its near scene, solid until a body is admitted through an open link (<c>docs/portals.md</c> D28). It stands whether or not the portal has a link.</summary>
        private void EnsureNearAperture(Portal portal, Container near, PortalFace face) =>
            EnsureNearAperture(portal.PortalId, portal.Aperture, near.gameObject.scene, face);

        private void EnsureNearAperture(ulong portalId, PortalAperture aperture, Scene scene, PortalFace face)
        {
            var frame = new PortalFaceFrame(ContainerRegistry.ToFrame(face.Origin, face.Scope), face.Rotation);
            var key = ApertureKey(aperture);
            if (_nearApertures.TryGetValue(portalId, out var existing))
            {
                if (Matches(existing, key, scene, frame)) return;
                existing.Destroy();
                _nearApertures.Remove(portalId);
            }
            _nearApertures[portalId] = BuildAperture(scene, key, frame, 0UL);
        }

        private void EnsureFarAperture(LinkRig rig)
        {
            var key = ApertureKey(rig.Aperture);
            if (Matches(rig.FarAperture, key, rig.FarScene, rig.FarFrame)) return;
            rig.FarAperture?.Destroy();
            rig.FarAperture = BuildAperture(rig.FarScene, key, rig.FarFrame, rig.LinkId);
        }

        /// <summary>The near aperture collider of a portal this worker holds, or null.</summary>
        internal Collider NearApertureOf(ulong portalId) => _nearApertures.TryGetValue(portalId, out var aperture) && aperture.Go != null ? aperture.Collider : null;

        /// <summary>The aperture collider of a link's far side this worker holds, or null.</summary>
        internal Collider FarApertureOf(ulong linkId) =>
            _rigs.TryGetValue(linkId, out var rig) && rig.FarAperture != null && rig.FarAperture.Go != null ? rig.FarAperture.Collider : null;

        /// <summary>Whether any collider of <paramref name="entity"/> touches an aperture collider's box (grown a little): the entity has met the aperture.</summary>
        internal bool Touches(NetworkIdentity entity, Collider aperture, uint tick)
        {
            if (aperture == null) return false;
            var info = Bodies.Get(entity, tick);
            var box = aperture.bounds;
            box.Expand(0.25f); // 12 cm each side: a character controller stops a skin width (8 cm by default) short
            for (int i = 0; i < info.Count; i++)
            {
                var c = info.Colliders[i];
                if (c != null && box.Intersects(c.bounds)) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------------------------------ the throat (D27)

        /// <summary>Show or hide a link's static copies. They are built while it prepares and enabled when it opens.</summary>
        private void SetThroatActive(LinkRig rig, bool on)
        {
            if (rig.ThroatActive == on) return;
            rig.ThroatActive = on;
            for (int i = 0; i < rig.NearThroat.Count; i++) if (rig.NearThroat[i].Go != null) rig.NearThroat[i].Go.SetActive(on);
            for (int i = 0; i < rig.FarThroat.Count; i++) if (rig.FarThroat[i].Go != null) rig.FarThroat[i].Go.SetActive(on);
        }

        /// <summary>
        /// Build what a link needs in physics before it can open (<c>docs/portals.md</c> D10 condition 4): both sides' aperture colliders and, when this worker
        /// holds both scenes, the throat, with a pool of proxies for the bodies near the face. When another worker holds the far side, it builds that
        /// side's aperture on its own, and its answer to the preparation says so. Idempotent: it does the work once per opening of the link.
        /// </summary>
        /// <returns>False while this worker cannot build it yet.</returns>
        public bool PrepareLink(Portal portal, PortalLink link, uint tick)
        {
            if (!Enabled || _worker == null) return true;
            _tick = tick;
            var near = portal.NearContainer;
            if (near == null || near.OwnerWorkerId != _worker.WorkerId) return true;
            SyncLinkRig(portal, link, near, portal.NearFaceOf(_worker));
            if (!_rigs.TryGetValue(link.Id, out var rig)) return true;
            if (rig.BuiltGeneration == link.Generation) return true;
            if (rig.HostsNear && rig.HostsFar)
            {
                Physics.SyncTransforms();
                BuildThroat(rig);
                Prewarm(rig);
                portal.Validate();
                rig.BuiltGeneration = link.Generation;
            }
            else if (link.FarEntry != null && !string.IsNullOrEmpty(link.FarEntry.OwnerWorkerId) && link.FarEntry.OwnerWorkerId != _worker.WorkerId)
            {
                // The far side is another worker's: it builds its own, and says so in its answer to the preparation (docs/portals.md D10, D30).
                if (rig.HostsNear) portal.Validate();
                rig.BuiltGeneration = link.Generation;
            }
            return true;
        }

        /// <summary>Copy the other side's static colliders in the half of the band that is its own into this side's scene, switched off until the link opens.</summary>
        private void BuildThroat(LinkRig rig)
        {
            rig.ThroatNearOrigin = rig.NearFrame.Origin;
            rig.ThroatFarOrigin = rig.FarFrame.Origin;
            // Each hosted side takes the other side's statics, when the other side's scene is here to copy from (a worker that does not hold it builds nothing for it).
            if (rig.HostsNear && Usable(rig.FarScene))
                CopyStatics(rig, rig.FarScene, rig.FarFrame, rig.TurnFarToNear, rig.NearFrame, rig.NearScene, beyond: true, rig.NearThroat);
            if (rig.HostsFar && Usable(rig.NearScene))
                CopyStatics(rig, rig.NearScene, rig.NearFrame, rig.TurnNearToFar, rig.FarFrame, rig.FarScene, beyond: false, rig.FarThroat);
        }

        private void CopyStatics(LinkRig rig, Scene sourceScene, in PortalFaceFrame sourceFrame, Quaternion turn, in PortalFaceFrame destinationFrame,
            Scene destinationScene, bool beyond, List<ProxyBody> into)
        {
            float depth = rig.Band.Depth;
            var local = new Vector3(0f, 0f, beyond ? depth * 0.5f : -depth * 0.5f);
            var half = new Vector3(rig.Band.HalfWidth, rig.Band.HalfHeight, depth * 0.5f);
            int count = sourceScene.GetPhysicsScene().OverlapBox(sourceFrame.ToScene(local), half, _hits, sourceFrame.Rotation, ~0, QueryTriggerInteraction.Ignore);
            if (count == _hits.Length) WarnOnce("throat-full:" + rig.LinkId, $"The throat of link {rig.LinkId:x} found more than {_hits.Length} colliders in its band; the rest are left out (docs/portals.md D27)");
            for (int i = 0; i < count; i++)
            {
                var c = _hits[i];
                _hits[i] = null;
                if (c == null || c.isTrigger || !c.enabled) continue;
                if (c.GetComponent<PortalProxy>() != null || c.attachedRigidbody != null) continue; // ours, or not static
                if (c.GetComponentInParent<NetworkIdentity>() != null) continue;                      // an entity's: a body, mirrored as one
                if (!PortalShapes.TryDescribe(c, out var key, out var center, out bool upright)) continue;
                if (into.Count >= MaxThroatColliders)
                {
                    WarnOnce("throat-cap:" + rig.LinkId, $"The throat of link {rig.LinkId:x} holds {MaxThroatColliders} copies; further static colliders in its band are left out (docs/portals.md D27)");
                    break;
                }
                var t = c.transform;
                var position = t.TransformPoint(center);
                var rotation = upright ? Quaternion.identity : t.rotation;
                var proxy = Acquire(destinationScene, key, c, rig, null, dynamic: false);
                proxy.Place(destinationFrame.Origin + turn * (position - sourceFrame.Origin), turn * rotation, stepped: false);
                proxy.Go.SetActive(false);
                into.Add(proxy);
            }
        }

        /// <summary>
        /// While a link prepares, build proxies that bodies near its face will need and put them in the pool (<c>docs/portals.md</c> D26): one for each collider of
        /// each body within the window's reach of the face, of each shape, so that a body entering the band allocates nothing.
        /// </summary>
        private void Prewarm(LinkRig rig)
        {
            _needed.Clear();
            Warm(rig, _worker.AuthoritativeEntities);
            Warm(rig, _worker.PortalGhostEntities);
            foreach (var kv in _needed)
            {
                int have = _pool.FreeCount(kv.Key.scene, kv.Key.key);
                for (int i = have; i < kv.Value; i++)
                {
                    var scene = SceneByHandle(rig, kv.Key.scene);
                    if (!scene.IsValid()) break;
                    var proxy = Build(scene, kv.Key.key, _prewarmSource[(kv.Key.scene, kv.Key.key)], true);
                    _pool.Add(kv.Key.scene, kv.Key.key, proxy);
                }
            }
            _needed.Clear();
            _prewarmSource.Clear();
        }

        private readonly Dictionary<(ulong scene, PortalShapeKey key), Collider> _prewarmSource = new Dictionary<(ulong, PortalShapeKey), Collider>();

        private static Scene SceneByHandle(LinkRig rig, ulong handle) => Raw(rig.NearScene) == handle ? rig.NearScene : Raw(rig.FarScene) == handle ? rig.FarScene : default;

        private void Warm(LinkRig rig, IReadOnlyList<NetworkIdentity> entities)
        {
            float reach = Portal.ApproachDistance * 2f;
            for (int i = 0; i < entities.Count; i++)
            {
                var e = entities[i];
                if (e == null || !e.IsSpawned) continue;
                bool entityNear = e.InstanceId == rig.NearScope;
                if (!entityNear && e.InstanceId != rig.FarScope) continue;
                if (entityNear ? !rig.HostsFar : !rig.HostsNear) continue;
                var frame = entityNear ? rig.NearFrame : rig.FarFrame;
                var info = Bodies.Get(e, _tick);
                var local = frame.ToLocal(e.ToScope(e.transform.position));
                if (!rig.Band.Contains(local, info.Radius + reach)) continue;
                var scene = entityNear ? rig.FarScene : rig.NearScene;
                for (int k = 0; k < info.Count; k++)
                {
                    var key = (Raw(scene), info.Keys[k]);
                    _needed.TryGetValue(key, out int n);
                    _needed[key] = n + 1;
                    if (!_prewarmSource.ContainsKey(key)) _prewarmSource[key] = info.Colliders[k];
                }
            }
        }

        private static readonly Collider[] BlockerHits = new Collider[64];

        /// <summary>
        /// The static colliders of a scene that stand in the volume of the cross-section reaching <paramref name="depth"/> beyond the face (or short of it): what
        /// <see cref="Portal.Validate"/> reports (<c>docs/portals.md</c> D27). The volume is drawn in a little from its edges so a floor that meets them is not in the way.
        /// </summary>
        internal static void FindBlockers(PhysicsScene scene, in PortalFaceFrame frame, PortalAperture aperture, float depth, bool beyond, List<Collider> into)
        {
            const float skin = 0.05f;
            var half = new Vector3(Mathf.Max(0.01f, aperture.HalfWidth - skin), Mathf.Max(0.01f, aperture.HalfHeight - skin), Mathf.Max(0.01f, depth * 0.5f - skin));
            var center = frame.ToScene(new Vector3(0f, 0f, beyond ? depth * 0.5f : -depth * 0.5f));
            int count = scene.OverlapBox(center, half, BlockerHits, frame.Rotation, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                var c = BlockerHits[i];
                BlockerHits[i] = null;
                if (c == null || c.isTrigger || c.GetComponent<PortalProxy>() != null || c.attachedRigidbody != null) continue;
                if (c.GetComponentInParent<NetworkIdentity>() != null) continue;
                into.Add(c);
            }
        }

        // ------------------------------------------------------------------------------------------ a crossing

        /// <summary>Before members are moved through a portal: their proxies in the scene they are about to enter, and their aperture pairs, go.</summary>
        public void BeforeCommit(IReadOnlyList<NetworkIdentity> members)
        {
            if (!Enabled || _rigs.Count == 0) return;
            for (int i = 0; i < members.Count; i++) Untrack(members[i]);
        }

        /// <summary>After members have been moved: each is looked at again at once, so the scene it left holds its proxy on the tick it crossed (no tick without a collider).</summary>
        public void AfterCommit(IReadOnlyList<NetworkIdentity> members)
        {
            if (!Enabled || _rigs.Count == 0) return;
            for (int i = 0; i < members.Count; i++) UpdateSource(members[i]);
        }

        /// <summary>Forget everything built for one entity, in every link.</summary>
        public void Untrack(NetworkIdentity entity)
        {
            if (entity == null) return;
            foreach (var rig in _rigs.Values)
            {
                if (!rig.Tracked.TryGetValue(entity.NetId, out var tracked)) continue;
                ReleaseTracked(rig, tracked);
                rig.Tracked.Remove(entity.NetId);
            }
        }
    }
}
