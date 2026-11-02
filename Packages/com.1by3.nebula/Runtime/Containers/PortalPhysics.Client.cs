using System.Collections.Generic;
using UnityEngine;

namespace Nebula
{
    internal sealed partial class PortalPhysics
    {
        /// <summary>
        /// A client's portal physics for its pawn's scope (<c>docs/portals.md</c> D25, D27, D28): the aperture colliders of the faces the pawn can stand at, the
        /// other side's static geometry in the throat, and a proxy in the pawn's scene for every replica of the other scope in the band, posed from the replica's
        /// latest authoritative state, the same tick the worker posed its own proxy from, never from the interpolated pose it is drawn at. Prediction at the face
        /// then sees what the worker sees. Run it before the pawn predicts a tick.
        /// </summary>
        /// <param name="pawn">The local pawn.</param>
        /// <param name="replicas">Every entity the client holds.</param>
        /// <param name="tick">The tick being predicted.</param>
        /// <param name="holdFrame">Leave the pawn's frame at its simulation pose for the prediction step that follows
        /// (<see cref="PhysicsFrames.EndSimulation(bool)"/>, NEB-445); the caller releases it.</param>
        public void StepClient(NetworkIdentity pawn, IEnumerable<NetworkIdentity> replicas, uint tick, bool holdFrame = false)
        {
            if (!Enabled || !_isClient) return;
            try { StepClientCore(pawn, replicas, tick, holdFrame); }
            catch (System.Exception e)
            {
                if (_warned.Add("step-failed")) NebulaLog.Error($"portal physics failed on tick {tick}; it will try again next frame: {e}");
            }
        }

        private void StepClientCore(NetworkIdentity pawn, IEnumerable<NetworkIdentity> replicas, uint tick, bool holdFrame)
        {
            _tick = tick;
            if (pawn == null || !pawn.IsSpawned || pawn.Container == null || (Portals.Known.Count == 0 && _rigs.Count == 0 && _nearApertures.Count == 0))
            {
                if (_rigs.Count > 0 || _nearApertures.Count > 0) Clear();
                return;
            }
            // A pawn inside a physics frame predicts in the frame's simulation space (docs/container-tree.md D11): the aperture collider, the throat and the proxies
            // of its side stand in the frame's scene in those coordinates, so the frame goes to its simulation pose, about an origin near the pawn, while they are
            // worked out, exactly as it does for the pawn's own prediction step. The replicas of the other scope are drawn, so they are read in render space.
            var space = pawn.Space;
            bool simulating = space != null && PhysicsFrames.RendersFrames && space.Frame != null;
            if (simulating) PhysicsFrames.BeginSimulation(space, pawn.transform);
            try
            {
                ulong scope = pawn.InstanceId;
                SyncClientRigs(scope, pawn);
                if (_rigs.Count > 0)
                {
                    BuildScopeIndex();
                    UpdateSource(pawn);
                    foreach (var e in replicas)
                    {
                        if (e == null || e == pawn || !e.IsSpawned || e.Container == null || e.InstanceId == scope) continue;
                        UpdateSource(e);
                    }
                    foreach (var rig in _rigs.Values) SweepRig(rig);
                    if (tick % BodiesPruneEveryTicks == 0) Bodies.Prune(tick, BodiesPruneAgeTicks);
                }
                if (_posedThisStep)
                {
                    _posedThisStep = false;
                    Physics.SyncTransforms();
                }
                NebulaDiagnostics.PortalProxies = LiveProxies;
            }
            finally
            {
                if (simulating) PhysicsFrames.EndSimulation(holdFrame);
            }
        }

        private bool _posedThisStep;

        /// <summary>Drop everything this client built (the pawn left, the session ended).</summary>
        public void Clear()
        {
            foreach (var rig in _rigs.Values) TearDown(rig, destroyApertures: true);
            _rigs.Clear();
            foreach (var aperture in _nearApertures.Values) aperture.Destroy();
            _nearApertures.Clear();
            _byScope.Clear();
            Bodies.Clear();
        }

        private void SyncClientRigs(ulong scope, NetworkIdentity pawn)
        {
            _apertureSeen.Clear();
            var pawnScene = pawn.gameObject.scene;
            var pawnSpace = pawn.Space;
            // A face the pawn is on the near side of is solid whether or not anything has opened it. Only the faces fixed in the container space the pawn simulates
            // in: a door of the street is not in the scene of the ship the pawn rides in.
            foreach (var portal in Portals.Known.Values)
            {
                if (portal.NearScope != scope || !Usable(pawnScene)) continue;
                if (PortalSpaces.SpaceOf(portal.NearContainer.Resolve()) != pawnSpace) continue;
                var face = new PortalFace(portal.NearScope, portal.NearContainerId, portal.NearOrigin, portal.NearRotation);
                EnsureNearAperture(portal.PortalId, PortalAperture.Box(portal.HalfWidth, portal.HalfHeight), pawnScene, pawnSpace, face);
                _apertureSeen.Add(portal.PortalId);
            }
            foreach (var kv in Portals.KnownLinks)
            {
                var link = kv.Value;
                if (!Portals.Known.TryGetValue(link.PortalId, out var portal)) continue;
                ulong near = portal.NearScope;
                ulong far = link.FarScope != 0 ? link.FarScope : portal.FarScope;
                if (near == far || (near != scope && far != scope)) continue;
                bool pawnNear = near == scope;
                var pawnSide = pawnNear ? portal.NearContainer.Resolve() : link.FarEntry.Resolve();
                if (PortalSpaces.SpaceOf(pawnSide) != pawnSpace) continue;
                var other = pawnNear ? link.FarEntry.Resolve() : portal.NearContainer.Resolve();
                var rig = RigFor(link.LinkId);
                rig.Stamp = _tick;
                rig.Portal = null;
                rig.Link = null;
                rig.PortalId = portal.PortalId;
                rig.PartyHash = link.PartyHash;
                rig.Generation = link.Generation;
                rig.NearScope = near;
                rig.FarScope = far;
                rig.NearFace = new PortalFace(near, portal.NearContainerId, portal.NearOrigin, portal.NearRotation);
                rig.FarFace = new PortalFace(far, link.FarEntryId, portal.FarOrigin, portal.FarRotation);
                rig.Aperture = PortalAperture.Box(portal.HalfWidth, portal.HalfHeight);
                rig.BandMargin = portal.BandMargin;
                rig.BandDepth = portal.BandDepth;
                rig.State = link.State;
                rig.ObservationOnly = portal.ObservationOnly;
                rig.ClientMember = (link.Flags & PortalLinkFlags.Member) != 0;
                rig.HostsNear = pawnNear && Usable(pawnScene);
                rig.HostsFar = !pawnNear && Usable(pawnScene);
                // The pawn's side is the pawn's own scene; the other side's is the scene its container simulates in (a frame's, when it has one), once its content is here.
                if (pawnNear) rig.NearScene = pawnScene; else rig.FarScene = pawnScene;
                rig.NearSpace = pawnNear ? pawnSpace : PortalSpaces.SpaceOf(other);
                rig.FarSpace = pawnNear ? PortalSpaces.SpaceOf(other) : pawnSpace;
                var otherScene = PortalSpaces.SceneOf(other);
                if (other != null && Usable(otherScene))
                {
                    if (pawnNear) rig.FarScene = otherScene; else rig.NearScene = otherScene;
                }
                else if (pawnNear) rig.FarScene = default; else rig.NearScene = default;
                Bind(rig);
                rig.NearAperture = pawnNear && _nearApertures.TryGetValue(portal.PortalId, out var aperture) ? aperture : null;
                if (rig.HostsFar) EnsureFarAperture(rig);
                if (rig.State == PortalState.Open && rig.BuiltGeneration != rig.Generation && Usable(rig.NearScene) && Usable(rig.FarScene))
                {
                    Physics.SyncTransforms();
                    BuildThroat(rig);
                    rig.BuiltGeneration = rig.Generation;
                }
            }
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
    }
}
