using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nebula
{
    internal sealed partial class PortalPhysics
    {
        /// <summary>
        /// One worker tick's portal physics (<c>docs/portals.md</c> D26 to D28), run after the entities' own simulation and before the worker steps its
        /// scenes: the rigs follow the links, every body in a band gets its proxies posed through the link and its aperture collider pairs settled, and
        /// pairs of bodies mirrored both ways are given one scene to collide in. Nothing here allocates once the proxies a body needs are pooled.
        /// </summary>
        public void Step(uint tick)
        {
            if (!Enabled || _worker == null) return;
            try { StepCore(tick); }
            catch (Exception e)
            {
                // A scene unloaded under a proxy, a collider destroyed by game code: this must not take the rest of the tick (and what it publishes) with it.
                if (_warned.Add("step-failed")) NebulaLog.Error($"portal physics failed on tick {tick}; it will try again next tick: {e}");
            }
        }

        private void StepCore(uint tick)
        {
            _tick = tick;
            SyncWorkerRigs();
            if (_rigs.Count == 0) { NebulaDiagnostics.PortalProxies = LiveProxies; return; }
            BuildScopeIndex();
            if (_byScope.Count > 0)
            {
                var own = _worker.AuthoritativeEntities;
                for (int i = 0; i < own.Count; i++)
                {
                    var e = own[i];
                    if (e != null && e.IsSpawned) UpdateSource(e);
                }
                var held = _worker.PortalGhostEntities;
                for (int i = 0; i < held.Count; i++)
                {
                    var e = held[i];
                    if (e != null && e.IsSpawned) UpdateSource(e);
                }
            }
            foreach (var rig in _rigs.Values)
            {
                SweepRig(rig);
                if (rig.IsLive && rig.Pairs.Count + rig.Tracked.Count > 1) ResolvePairs(rig);
            }
            if (tick % 120 == 0) Bodies.Prune(tick, 600);
            NebulaDiagnostics.PortalProxies = LiveProxies;
        }

        /// <summary>The rigs follow the portals this worker owns: a rig per link, an aperture collider per portal, gone when the link or the portal is.</summary>
        private void SyncWorkerRigs()
        {
            _apertureSeen.Clear();
            var all = Portals.All;
            for (int i = 0; i < all.Count; i++)
            {
                var portal = all[i];
                if (portal == null || portal.IsIgnored) continue;
                var near = portal.NearContainer;
                if (near == null || near.OwnerWorkerId != _worker.WorkerId) continue;
                var nearFace = portal.NearFaceOf(_worker);
                if (HostableSide(near))
                {
                    EnsureNearAperture(portal, near, nearFace);
                    _apertureSeen.Add(portal.PortalId);
                }
                foreach (var link in portal.LinkValues) SyncLinkRig(portal, link, near, nearFace);
            }
            // The adjacency rigs (a link another worker drives, whose far side this worker holds) are kept by the worker's adjacency table.
            SyncAdjacencyRigs();
            _gone.Clear();
            foreach (var kv in _rigs) if (kv.Value.Stamp != _tick) _gone.Add(kv.Key);
            for (int i = 0; i < _gone.Count; i++)
            {
                TearDown(_rigs[_gone[i]], destroyApertures: true);
                _rigs.Remove(_gone[i]);
            }
            _gone.Clear();
            _apertureGone.Clear();
            foreach (var kv in _nearApertures) if (!_apertureSeen.Contains(kv.Key)) _apertureGone.Add(kv.Key);
            for (int i = 0; i < _apertureGone.Count; i++)
            {
                _nearApertures[_apertureGone[i]].Destroy();
                _nearApertures.Remove(_apertureGone[i]);
            }
            _apertureGone.Clear();
        }

        /// <summary>Follow one link of a portal this worker owns: its geometry, state and the sides this worker holds.</summary>
        private void SyncLinkRig(Portal portal, PortalLink link, Container near, PortalFace nearFace)
        {
            if (link.FarScope == 0) return;
            bool nearUsable = HostableSide(near);
            if (nearUsable) { EnsureNearAperture(portal, near, nearFace); _apertureSeen.Add(portal.PortalId); }
            var rig = RigFor(link.Id);
            rig.Stamp = _tick;
            rig.Portal = portal;
            rig.Link = link;
            rig.PortalId = portal.PortalId;
            if (rig.PartyHash == 0) rig.PartyHash = PortalIds.PartyHash(link.Party);
            rig.Generation = link.Generation;
            rig.NearScope = nearFace.Scope;
            rig.FarScope = link.FarScope;
            rig.NearFace = nearFace;
            rig.FarFace = link.FarFace;
            rig.Aperture = portal.Aperture;
            rig.BandMargin = portal.BandMargin;
            rig.BandDepth = Portal.BandDepth;
            rig.State = link.State;
            rig.ObservationOnly = portal.ObservationOnly;
            rig.HostsNear = nearUsable;
            var far = link.FarEntry;
            rig.HostsFar = far != null && far.OwnerWorkerId == _worker.WorkerId && HostableSide(far);
            rig.NearSpace = PortalSpaces.SpaceOf(near);
            rig.FarSpace = PortalSpaces.SpaceOf(far);
            if (rig.HostsNear) rig.NearScene = PortalSpaces.SceneOf(near);
            if (rig.HostsFar) rig.FarScene = PortalSpaces.SceneOf(far);
            rig.NearAperture = nearUsable && _nearApertures.TryGetValue(portal.PortalId, out var aperture) ? aperture : null;
            Bind(rig);
            if (rig.HostsFar) EnsureFarAperture(rig);
            rig.NearOwner = _worker.WorkerId;
            rig.FarOwners.Clear();
            if (_worker.PortalAdjacencyTable.TryGet(link.Id, out var edge)) AddOwners(rig.FarOwners, edge.Far);
        }

        /// <summary>The workers other than this one that own any of these containers.</summary>
        private void AddOwners(List<string> owners, List<ContainerRef> containers)
        {
            for (int i = 0; i < containers.Count; i++)
            {
                var container = containers[i].Resolve();
                string owner = container != null ? container.OwnerWorkerId : "";
                if (!string.IsNullOrEmpty(owner) && owner != _worker.WorkerId && !owners.Contains(owner)) owners.Add(owner);
            }
        }

        /// <summary>
        /// Whether a side's physics can be built here: the scene its bodies simulate in is loaded. That is the container's own scene, or the scene of the physics frame
        /// it simulates in (a planet that is a carrier), which a process that does not hold the frame does not have (<c>docs/portals.md</c> D26).
        /// </summary>
        private static bool HostableSide(Container container) => container != null && Usable(PortalSpaces.SceneOf(container));

        private void BuildScopeIndex()
        {
            foreach (var list in _byScope.Values) { list.Clear(); _spareLists.Add(list); }
            _byScope.Clear();
            foreach (var rig in _rigs.Values)
            {
                if (!rig.IsLive || rig.NearScope == rig.FarScope) continue;
                AddToScope(rig.NearScope, rig);
                AddToScope(rig.FarScope, rig);
            }
        }

        private void AddToScope(ulong scope, LinkRig rig)
        {
            if (!_byScope.TryGetValue(scope, out var list))
            {
                list = _spareLists.Count > 0 ? _spareLists[_spareLists.Count - 1] : new List<LinkRig>(2);
                if (_spareLists.Count > 0) _spareLists.RemoveAt(_spareLists.Count - 1);
                _byScope.Add(scope, list);
            }
            list.Add(rig);
        }

        // ------------------------------------------------------------------------------------------ bodies in bands

        private void UpdateSource(NetworkIdentity e)
        {
            if (!_byScope.TryGetValue(e.InstanceId, out var rigs)) return;
            for (int i = 0; i < rigs.Count; i++) UpdateSourceIn(rigs[i], e);
        }

        private void UpdateSourceIn(LinkRig rig, NetworkIdentity e)
        {
            bool entityNear = e.InstanceId == rig.NearScope;
            bool hostsDest = entityNear ? rig.HostsFar : rig.HostsNear;
            bool hostsOwn = entityNear ? rig.HostsNear : rig.HostsFar;
            if (!hostsDest && !hostsOwn) return;
            bool existing = rig.Tracked.TryGetValue(e.NetId, out var tracked);
            // Only a body already in the band finishes while a link is closing (docs/portals.md D9); new ones wait for the link to open.
            if (!existing && rig.State != PortalState.Open) return;
            var frame = entityNear ? rig.NearFrame : rig.FarFrame;
            var sideSpace = entityNear ? rig.NearSpace : rig.FarSpace;
            if (!frame.Valid) return; // the side's physics frame is not here (yet)
            // Most of what a scope holds is nowhere near a face: it is not even described.
            if (!existing && !Bodies.MayTouch(e, in frame, in rig.Band, 1f, sideSpace)) return;
            var info = Bodies.Get(e, _tick);
            float travel = info.Body != null ? info.Body.linearVelocity.magnitude * NetworkTime.TickInterval : 0f;
            float grow = (existing ? BandHysteresis : 0f) + travel;
            bool touches = Bodies.Touches(e, info, _tick, in frame, in rig.Band, grow, _eligible, grow, sideSpace);
            if (!touches)
            {
                if (existing) { ReleaseTracked(rig, tracked); rig.Tracked.Remove(e.NetId); }
                return;
            }
            if (!existing)
            {
                tracked = NewTracked(e, entityNear);
                rig.Tracked.Add(e.NetId, tracked);
            }
            tracked.Stamp = _tick;
            tracked.Info = info;
            SyncParts(rig, tracked, e, info, hostsDest);
            if (hostsOwn) UpdateAperture(rig, tracked, e, hostsOwn); else ClearIgnores(tracked);
        }

        /// <summary>Drop what a body no longer in a band had: its proxies back to the pool, its aperture pairs undone.</summary>
        private void ReleaseTracked(LinkRig rig, Tracked tracked)
        {
            ClearIgnores(tracked);
            for (int i = 0; i < tracked.Parts.Count; i++) { ReleaseDynamic(rig, tracked.Parts[i].Proxy); RecyclePart(tracked.Parts[i]); }
            tracked.Parts.Clear();
            UndoPairsOf(rig, tracked.Entity != null ? tracked.Entity.NetId : 0UL, tracked.EntityNear);
            RecycleTracked(tracked);
        }

        /// <summary>After a tick's updates: bodies not seen this tick left the scope, were despawned, or moved out of reach.</summary>
        private void SweepRig(LinkRig rig)
        {
            if (!rig.IsLive)
            {
                if (rig.Tracked.Count > 0 || rig.Pairs.Count > 0) DropDynamic(rig);
                if (rig.State == PortalState.Closed && (rig.NearThroat.Count > 0 || rig.FarThroat.Count > 0)) DropThroat(rig);
                return;
            }
            SetThroatActive(rig, true);
            // Each side's copies stand in that side's scene, about its own face: only a move of that face matters to them.
            if (rig.BuiltGeneration == rig.Generation &&
                ((rig.NearThroat.Count > 0 && !rig.NearFrame.SameAs(rig.ThroatNearFrame)) || (rig.FarThroat.Count > 0 && !rig.FarFrame.SameAs(rig.ThroatFarFrame))))
            {
                // A scope's origin moved under the copies (docs/scope-frames.md), or a physics frame's floating origin did (docs/portals.md D26): they are where
                // the old frame had them, so they are made again.
                DropThroat(rig);
                Physics.SyncTransforms();
                BuildThroat(rig);
                rig.BuiltGeneration = rig.Generation;
                SetThroatActive(rig, true);
            }
            if (rig.Tracked.Count == 0) return;
            _gone.Clear();
            foreach (var kv in rig.Tracked)
            {
                var tracked = kv.Value;
                bool stale = tracked.Stamp != _tick || tracked.Entity == null || !tracked.Entity.IsSpawned;
                if (stale) _gone.Add(kv.Key);
            }
            for (int i = 0; i < _gone.Count; i++)
            {
                if (rig.Tracked.TryGetValue(_gone[i], out var tracked)) ReleaseTracked(rig, tracked);
                rig.Tracked.Remove(_gone[i]);
            }
            _gone.Clear();
        }

        // ------------------------------------------------------------------------------------------ proxies, posed

        private void SyncParts(LinkRig rig, Tracked tracked, NetworkIdentity e, PortalBodies.Info info, bool hostsDest)
        {
            if (!hostsDest)
            {
                for (int i = 0; i < tracked.Parts.Count; i++) { ReleaseDynamic(rig, tracked.Parts[i].Proxy); RecyclePart(tracked.Parts[i]); }
                tracked.Parts.Clear();
                return;
            }
            var destScene = tracked.EntityNear ? rig.FarScene : rig.NearScene;
            // Parts whose collider no longer touches the band, or is gone.
            for (int i = tracked.Parts.Count - 1; i >= 0; i--)
            {
                var part = tracked.Parts[i];
                int index = part.Source != null ? Array.IndexOf(info.Colliders, part.Source, 0, info.Count) : -1;
                if (index >= 0 && _eligible.Contains(index)) continue;
                ReleaseDynamic(rig, part.Proxy);
                RecyclePart(part);
                tracked.Parts.RemoveAt(i);
            }
            // Colliders that came into the band.
            for (int n = 0; n < _eligible.Count; n++)
            {
                int index = _eligible[n];
                var collider = info.Colliders[index];
                bool has = false;
                for (int p = 0; p < tracked.Parts.Count; p++) if (tracked.Parts[p].Source == collider) { has = true; break; }
                if (has) continue;
                if (rig.Portal != null && rig.Portal.ProxyTotal >= rig.Portal.MaxProxiesPerPortal)
                {
                    NebulaDiagnostics.PortalProxiesDropped++;
                    WarnOnce("cap:" + rig.PortalId, $"Portal '{rig.Portal.AuthoredId}' reached MaxProxiesPerPortal ({rig.Portal.MaxProxiesPerPortal}): bodies in its band beyond that get no proxy (docs/portals.md D26)");
                    continue;
                }
                var proxy = Acquire(destScene, info.Keys[index], collider, rig, e, dynamic: true);
                tracked.Parts.Add(NewPart(collider, proxy));
            }
            // Pose each one, but only when its source moved since the last time it was posed: a proxy re-posed every tick keeps a body resting on it
            // awake (docs/portals.md D26, §18).
            var turn = tracked.EntityNear ? rig.TurnNearToFar : rig.TurnFarToNear;
            var from = tracked.EntityNear ? rig.NearFrame : rig.FarFrame;
            var to = tracked.EntityNear ? rig.FarFrame : rig.NearFrame;
            var sideSpace = tracked.EntityNear ? rig.NearSpace : rig.FarSpace;
            if (!from.Valid || !to.Valid) return;
            bool asleep = info.Body != null && info.Body.IsSleeping();
            for (int p = 0; p < tracked.Parts.Count; p++)
            {
                var part = tracked.Parts[p];
                if (asleep && part.HasLast && part.PosedAbout.SameAs(to)) continue; // a sleeping body is not looked at again, unless the face its proxy stands about moved
                int index = Array.IndexOf(info.Colliders, part.Source, 0, info.Count);
                if (index < 0) continue;
                PortalBodies.PoseOf(e, part.Source, info.Centers[index], info.Upright[index], _isClient && !e.IsLocalPlayer, sideSpace, out var position, out var rotation);
                _posedThisStep = true;
                // The face the proxy's scene is posed about moved (a framed side's floating origin shifted, or its frame moved): the proxy goes there at once, not by a move.
                bool aboutMoved = part.HasLast && !part.PosedAbout.SameAs(to);
                if (part.HasLast && !aboutMoved && Near(part.LastPosition, position) && Near(part.LastRotation, rotation)) continue;
                part.LastPosition = position;
                part.LastRotation = rotation;
                part.HasLast = true;
                part.PosedAbout = to;
                if (aboutMoved) part.Proxy.Posed = false;
                part.Proxy.Place(to.Origin + turn * (position - from.Origin), turn * rotation, Stepped);
            }
        }

        private readonly Stack<Tracked> _trackedFree = new Stack<Tracked>();
        private readonly Stack<Part> _partFree = new Stack<Part>();

        private Tracked NewTracked(NetworkIdentity entity, bool near)
        {
            var tracked = _trackedFree.Count > 0 ? _trackedFree.Pop() : new Tracked();
            tracked.Entity = entity;
            tracked.EntityNear = near;
            tracked.Stamp = 0;
            tracked.NextAdmitCheck = 0;
            tracked.Admitted = false;
            tracked.Why = PortalRefusal.None;
            tracked.Info = null;
            return tracked;
        }

        private void RecycleTracked(Tracked tracked)
        {
            tracked.Entity = null;
            tracked.Info = null;
            tracked.Parts.Clear();
            tracked.Ignored.Clear();
            tracked.IgnoredAperture = null;
            _trackedFree.Push(tracked);
        }

        private Part NewPart(Collider source, ProxyBody proxy)
        {
            var part = _partFree.Count > 0 ? _partFree.Pop() : new Part();
            part.Source = source;
            part.Proxy = proxy;
            part.HasLast = false;
            return part;
        }

        private void RecyclePart(Part part)
        {
            part.Source = null;
            part.Proxy = null;
            _partFree.Push(part);
        }

        private static bool Near(Vector3 a, Vector3 b) =>
            Mathf.Abs(a.x - b.x) < 1e-4f && Mathf.Abs(a.y - b.y) < 1e-4f && Mathf.Abs(a.z - b.z) < 1e-4f;

        private static bool Near(Quaternion a, Quaternion b) =>
            Mathf.Abs(a.x - b.x) < 2e-5f && Mathf.Abs(a.y - b.y) < 2e-5f && Mathf.Abs(a.z - b.z) < 2e-5f && Mathf.Abs(a.w - b.w) < 2e-5f;

        // ------------------------------------------------------------------------------------------ the aperture, per body

        private void ClearIgnores(Tracked tracked)
        {
            if (tracked.Ignored.Count == 0) return;
            if (tracked.IgnoredAperture != null)
                for (int i = 0; i < tracked.Ignored.Count; i++) IgnoreCollision(tracked.Ignored[i], tracked.IgnoredAperture, false);
            tracked.Ignored.Clear();
            tracked.IgnoredAperture = null;
        }

        /// <summary>
        /// Settle which of a body's colliders ignore the aperture collider of its side (<c>docs/portals.md</c> D28): an admitted body's, while the link is open;
        /// everything else's stays solid and meets the aperture. Looked at again every few ticks, not every one: admission is the game's hook.
        /// </summary>
        private void UpdateAperture(LinkRig rig, Tracked tracked, NetworkIdentity e, bool hostsOwn)
        {
            var aperture = tracked.EntityNear ? rig.NearAperture : rig.FarAperture;
            if (aperture == null || aperture.Collider == null) { ClearIgnores(tracked); return; }
            if (rig.State == PortalState.Open && _tick >= tracked.NextAdmitCheck)
            {
                tracked.NextAdmitCheck = _tick + AdmitCheckTicks;
                tracked.Admitted = Admitted(rig, tracked, e, out tracked.Why);
            }
            if (!tracked.Admitted)
            {
                ClearIgnores(tracked);
                return;
            }
            if (tracked.IgnoredAperture != null && tracked.IgnoredAperture != aperture.Collider) ClearIgnores(tracked);
            tracked.IgnoredAperture = aperture.Collider;
            var info = tracked.Info;
            for (int i = tracked.Ignored.Count - 1; i >= 0; i--)
            {
                var c = tracked.Ignored[i];
                int index = c != null ? Array.IndexOf(info.Colliders, c, 0, info.Count) : -1;
                if (index >= 0 && _eligible.Contains(index)) continue;
                IgnoreCollision(c, aperture.Collider, false);
                tracked.Ignored.RemoveAt(i);
            }
            for (int n = 0; n < _eligible.Count; n++)
            {
                var c = info.Colliders[_eligible[n]];
                // Unity forgets an ignored pair when either collider is deactivated (the aperture back in its pool between a link's close and
                // its reopening, a body's collider toggled): the list says what was asked for, not what holds, so the pair itself is checked.
                if (tracked.Ignored.Contains(c))
                {
                    if (c != null && !Physics.GetIgnoreCollision(c, aperture.Collider)) IgnoreCollision(c, aperture.Collider, true);
                    continue;
                }
                IgnoreCollision(c, aperture.Collider, true);
                tracked.Ignored.Add(c);
            }
        }

        /// <summary>Whether this body passes the aperture of its side: its party's link is open and it is admitted (D12); anything on the far side is leaving, which is never checked.</summary>
        private bool Admitted(LinkRig rig, Tracked tracked, NetworkIdentity e, out PortalRefusal why)
        {
            why = PortalRefusal.None;
            if (rig.ObservationOnly) { why = PortalRefusal.NotAdmitted; return false; }
            if (rig.State != PortalState.Open) { why = PortalRefusal.LinkNotOpen; return false; }
            if (rig.Portal == null) return tracked.EntityNear ? _isClient && rig.ClientMember : true; // no portal here: a client's pawn is let through as a member; whatever stands on the far side is leaving
            return tracked.EntityNear ? rig.Portal.AdmitsThrough(e, rig.Link, out why) : true;
        }
    }
}
