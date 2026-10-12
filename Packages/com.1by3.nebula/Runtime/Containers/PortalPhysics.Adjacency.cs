using System.Collections.Generic;
using UnityEngine;

namespace Nebula
{
    internal sealed partial class PortalPhysics
    {
        /// <summary>
        /// The rigs for links another worker drives, whose far side this worker holds (<c>docs/portals.md</c> D29, D30): built from what that worker told this one
        /// (<see cref="MsgId.PortalAdjacency"/>), so the far aperture collider stands in this worker's scene and the bodies in the far band are ghosted to the
        /// worker that holds the near face. Bodies ghosted here from the near side are posed through the link into this worker's scene as proxies.
        /// </summary>
        private void SyncAdjacencyRigs()
        {
            foreach (var incoming in _worker.AdjacencyIn)
            {
                var m = incoming.Msg;
                _rigs.TryGetValue(m.LinkId, out var existing);
                if (existing != null && existing.Link != null) continue; // a link this worker drives itself
                var far = OwnedFarContainer(m);
                if (far == null) continue;
                var rig = existing ?? RigFor(m.LinkId);
                rig.Stamp = _tick;
                rig.Portal = null;
                rig.Link = null;
                rig.PortalId = m.Portal.PortalId;
                rig.Generation = m.Generation;
                rig.NearScope = m.Portal.NearScope;
                rig.FarScope = m.FarScope;
                rig.NearFace = new PortalFace(m.Portal.NearScope, m.Portal.NearContainerId, m.Portal.NearOrigin, m.Portal.NearRotation);
                rig.FarFace = new PortalFace(m.FarScope, "", m.Portal.FarOrigin, m.Portal.FarRotation);
                rig.Aperture = PortalAperture.Box(m.Portal.HalfWidth, m.Portal.HalfHeight);
                rig.BandMargin = m.Portal.BandMargin;
                rig.BandDepth = m.Portal.BandDepth;
                rig.State = m.State;
                rig.ObservationOnly = m.Portal.ObservationOnly;
                rig.HostsNear = false;
                rig.NearAperture = null;
                rig.NearSpace = PortalSpaces.SpaceOf(m.Portal.NearContainer.Resolve());
                rig.FarSpace = PortalSpaces.SpaceOf(far);
                rig.HostsFar = HostableSide(far);
                if (rig.HostsFar) rig.FarScene = PortalSpaces.SceneOf(far);
                Bind(rig);
                if (rig.HostsFar) EnsureFarAperture(rig);
                rig.NearOwner = incoming.From;
                rig.FarOwners.Clear();
                AddOwners(rig.FarOwners, m.FarContainers);
            }
        }

        /// <summary>The far container this worker owns among those a link's band touches, or null.</summary>
        private Container OwnedFarContainer(PortalAdjacencyMsg m)
        {
            for (int i = 0; i < m.FarContainers.Count; i++)
            {
                var c = m.FarContainers[i].Resolve();
                if (c != null && c.OwnerWorkerId == _worker.WorkerId) return c;
            }
            return null;
        }

        /// <summary>
        /// The far worker's half of D10 condition 4: its side's aperture collider stands, built from what the driving worker told it. False when the link is
        /// not known here or its far container is not ready; true without building anything when the side cannot be built here (a container whose physics frame
        /// has no scene here, said once).
        /// </summary>
        public bool PrepareFarSide(ulong linkId, uint tick)
        {
            if (!Enabled || _worker == null) return true;
            _tick = tick;
            SyncAdjacencyRigs();
            foreach (var incoming in _worker.AdjacencyIn)
            {
                if (incoming.Msg.LinkId != linkId) continue;
                var far = OwnedFarContainer(incoming.Msg);
                if (far == null) return false;
                if (far.InnerSpace != null && !HostableSide(far))
                {
                    WarnOnce("framed-far:" + linkId, $"Link {linkId:x}: its far container {far.ContainerId} simulates in a physics frame that has no scene on this worker; it builds no aperture collider for it (docs/portals.md D28, as built)");
                    return true;
                }
                return _rigs.TryGetValue(linkId, out var rig) && rig.HostsFar && rig.FarAperture != null && rig.FarAperture.Go != null;
            }
            return false;
        }

        /// <summary>
        /// The workers that must hold a ghost of an entity this worker simulates because it is in the band of a portal face (<c>docs/portals.md</c> D30): the
        /// owners of the far containers the band touches for a body on the near side, and the owner of the near container for one on the far side. Only the
        /// bodies <see cref="Step"/> found in a band; nothing else is ghosted across a scope.
        /// </summary>
        public void GhostTargets(NetworkIdentity e, List<string> into)
        {
            if (_worker == null || !_byScope.TryGetValue(e.InstanceId, out var rigs)) return;
            for (int i = 0; i < rigs.Count; i++)
            {
                var rig = rigs[i];
                if (!rig.Tracked.ContainsKey(e.NetId)) continue;
                if (e.InstanceId == rig.NearScope) { for (int k = 0; k < rig.FarOwners.Count; k++) AddTarget(into, rig.FarOwners[k]); }
                else AddTarget(into, rig.NearOwner);
            }
        }

        private void AddTarget(List<string> into, string worker)
        {
            if (!string.IsNullOrEmpty(worker) && worker != _worker.WorkerId && !into.Contains(worker)) into.Add(worker);
        }
    }
}
