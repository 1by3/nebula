using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Nebula
{
    /// <summary>One face a query can meet in a scope: where it stands in the scope's scene, its opening, and the link a cast through it would take (<c>docs/portals.md</c> D32).</summary>
    internal struct QFace
    {
        public PortalFaceFrame Frame;
        public PortalAperture Aperture;
        /// <summary>The way through is the face's +z (the near side's face); false for the far side's face, whose way through is -z.</summary>
        public bool Entering;
        /// <summary>The link a cast through this face would take for the asker; null when there is none.</summary>
        public LinkRig Rig;
        /// <summary>The asker's link is open and both sides' frames are known: a cast may continue.</summary>
        public bool Open;
        public ulong PortalId;
        public Collider ApertureCollider;
    }

    internal sealed partial class PortalPhysics
    {
        private HistoryRewinder _rewinder;

        /// <summary>The rewinder queries and <see cref="HistoryRewind"/> use; made when first asked for.</summary>
        internal HistoryRewinder Rewinder => _rewinder ??= new HistoryRewinder(this, _worker);

        /// <summary>Whether this is a client's physics (it is not the simulating process of what it holds).</summary>
        internal bool IsClientSide => _isClient;

        /// <summary>The link rigs this process holds, for queries and the history rewind.</summary>
        internal Dictionary<ulong, LinkRig>.ValueCollection RigValues => _rigs.Values;

        /// <summary>
        /// The faces of the scope (and physics frame) a cast starts in, for an asker (<c>docs/portals.md</c> D12, D32): every portal whose near face stands there, with the
        /// link the asker's party would take (null when none is chosen), and every link whose far face stands there. A face is returned whether or not its link is open: a
        /// closed one is a hit on the plane.
        /// </summary>
        public void GatherFaces(ulong scope, Container space, in PortalAsker asker, List<QFace> into)
        {
            into.Clear();
            foreach (var kv in _nearApertures)
            {
                var ap = kv.Value;
                if (ap.Go == null || ap.ApertureScope != scope || ap.ApertureSpace != space) continue;
                var rig = ChooseRig(kv.Key, scope, in asker);
                into.Add(new QFace
                {
                    Frame = new PortalFaceFrame(ap.T.position, ap.T.rotation),
                    Aperture = ap.ApertureShape,
                    Entering = true,
                    Rig = rig,
                    Open = rig != null && rig.State == PortalState.Open && rig.FarFrame.Valid && !rig.ObservationOnly,
                    PortalId = kv.Key,
                    ApertureCollider = ap.Collider,
                });
            }
            foreach (var rig in _rigs.Values)
            {
                if (rig.FarScope != scope || rig.FarSpace != space || rig.NearScope == rig.FarScope || !rig.FarFrame.Valid) continue;
                if (!rig.HostsFar && !(_isClient && Usable(rig.FarScene))) continue;
                var ap = rig.FarAperture;
                into.Add(new QFace
                {
                    Frame = rig.FarFrame,
                    Aperture = rig.Aperture,
                    Entering = false,
                    Rig = rig,
                    Open = rig.State == PortalState.Open && rig.NearFrame.Valid && !rig.ObservationOnly,
                    PortalId = rig.PortalId,
                    ApertureCollider = ap != null && ap.Go != null ? ap.Collider : null,
                });
            }
        }

        /// <summary>
        /// The link of a portal the asker's cast takes at its near face (<c>docs/portals.md</c> D12): the named party's; otherwise the party of the asking entity
        /// (<see cref="Portal.PartyOf"/>, on the worker that drives the portal), the link the client is a member of on a client; otherwise the face's one open link.
        /// </summary>
        private LinkRig ChooseRig(ulong portalId, ulong scope, in PortalAsker asker)
        {
            LinkRig only = null, member = null, named = null, byEntity = null;
            int open = 0;
            Portal portal = null;
            foreach (var rig in _rigs.Values)
            {
                if (rig.PortalId != portalId || rig.NearScope != scope) continue;
                portal ??= rig.Portal;
                if (rig.State == PortalState.Open) { only = rig; open++; }
                if (rig.ClientMember) member = rig;
                if (asker.HasParty && rig.PartyHash == PortalQueries.HashOf(asker.Party)) named = rig;
            }
            if (asker.HasParty) return named;
            if (asker.Entity != null && portal != null)
            {
                string party = portal.PartyForQuery(asker.Entity);
                if (party != null)
                {
                    ulong hash = PortalQueries.HashOf(party);
                    foreach (var rig in _rigs.Values)
                        if (rig.PortalId == portalId && rig.NearScope == scope && rig.PartyHash == hash) { byEntity = rig; break; }
                    if (byEntity != null) return byEntity;
                }
            }
            if (_isClient && member != null) return member;
            return open == 1 ? only : null;
        }

        /// <summary>
        /// The scene on the other side of a face whose link is open, when this process holds it: the far scene for a near face, the near scene for a far face. False
        /// when the other side is on another worker (only the band is here), or has no scene here yet.
        /// </summary>
        public bool OtherSide(LinkRig rig, bool entering, out Scene scene, out Container space)
        {
            if (entering)
            {
                scene = rig.FarScene;
                space = rig.FarSpace;
                return (_isClient || rig.HostsFar) && Usable(scene);
            }
            scene = rig.NearScene;
            space = rig.NearSpace;
            return (_isClient || rig.HostsNear) && Usable(scene);
        }
    }
}
