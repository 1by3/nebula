using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Nebula
{
    public sealed partial class Portal
    {
        /// <summary>The margin, in meters, by which the validated volume is drawn in from the cross-section's edges, so a floor or a frame that touches them does not count.</summary>
        private const float ValidateSkin = 0.05f;

        private static readonly Collider[] ValidateHits = new Collider[64];

        /// <summary>
        /// Check that this side's own static colliders leave the cross-section clear for <see cref="BandDepth"/> beyond the face (<c>docs/portals.md</c> D27): the
        /// tunnel mouth a game builds so that a body straddling the face is not pushed out of the doorway by the mass behind it. Run it in the Editor (a portal
        /// validates itself when it is edited) and by hand; a link also checks both of its sides when it starts preparing. Whatever is in the way is logged.
        /// In play mode it checks the near side and, for each link whose far container this process holds, the far side; in the Editor, the near side in the
        /// open scene.
        /// </summary>
        /// <param name="blockers">When given, filled with the colliders in the way.</param>
        /// <returns>True when the cross-section is clear.</returns>
        public bool Validate(List<Collider> blockers = null)
        {
            blockers?.Clear();
            var found = new List<Collider>();
            int total = 0;
            if (Application.isPlaying)
            {
                var near = NearContainer;
                var nearScene = PortalSpaces.SceneOf(near);
                if (near != null && PortalSpaces.Usable(nearScene))
                {
                    var face = NearFace;
                    var frame = PortalSpaces.FaceFrame(face, PortalSpaces.SpaceOf(near));
                    if (frame.Valid) PortalPhysics.FindBlockers(nearScene.GetPhysicsScene(), in frame, Aperture, BandDepth, beyond: true, found);
                    total += Report("near", found, blockers);
                }
                foreach (var link in _links.Values)
                {
                    var far = link.FarEntry;
                    var farScene = PortalSpaces.SceneOf(far);
                    if (far == null || link.FarScope == 0 || !PortalSpaces.Usable(farScene)) continue;
                    var face = link.FarFace;
                    var frame = PortalSpaces.FaceFrame(face, PortalSpaces.SpaceOf(far));
                    found.Clear();
                    if (frame.Valid) PortalPhysics.FindBlockers(farScene.GetPhysicsScene(), in frame, Aperture, BandDepth, beyond: false, found);
                    total += Report("far (" + link.Party + ")", found, blockers);
                }
            }
            else
            {
                Physics.SyncTransforms();
                var frame = new PortalFaceFrame(transform.position, transform.rotation);
                PortalPhysics.FindBlockers(gameObject.scene.IsValid() ? gameObject.scene.GetPhysicsScene() : Physics.defaultPhysicsScene, in frame, Aperture, BandDepth, beyond: true, found);
                total += Report("near", found, blockers);
            }
            return total == 0;
        }

        /// <summary>The party an entity belongs to at this portal (<see cref="PartyOf"/>, or the owner's identity), for a query's asker (<c>docs/portals.md</c> D12, D32).</summary>
        internal string PartyForQuery(NetworkIdentity entity) => PartyOfEntity(entity);

        private int Report(string side, List<Collider> found, List<Collider> into)
        {
            if (found.Count == 0) return 0;
            int count = found.Count;
            into?.AddRange(found);
            var names = new StringBuilder();
            for (int i = 0; i < found.Count && i < 6; i++) names.Append(i > 0 ? ", " : "").Append(found[i].name);
            if (found.Count > 6) names.Append(", ...");
            NebulaLog.Warn($"Portal '{AuthoredId}': {found.Count} static collider(s) on the {side} side stand in the cross-section within {BandDepth:0.#} m beyond the face ({names}). " +
                           "A body straddling the face would be pushed out of the doorway; build a tunnel mouth at least that deep (docs/portals.md D27).");
            return count;
        }

#if UNITY_EDITOR
        private bool _validateQueued;

        private void OnValidate()
        {
            if (Application.isPlaying || _validateQueued || !gameObject.scene.IsValid() || !gameObject.scene.isLoaded) return;
            _validateQueued = true;
            // Once edits settle: OnValidate fires on every change in the inspector, and a physics query is not welcome in the middle of one.
            UnityEditor.EditorApplication.delayCall += () =>
            {
                _validateQueued = false;
                if (this != null && !Application.isPlaying && isActiveAndEnabled) Validate();
            };
        }
#endif

        // ------------------------------------------------------------------------------------------ meeting the aperture collider

        /// <summary>
        /// A body that touches the aperture collider and is not let through has met the aperture (<c>docs/portals.md</c> D17, D28): it bounces off the
        /// solid box, so it never reaches the plane, and the refusal is raised here, once for each tick it is in contact. Bodies with no colliders, and a
        /// mover that tunnels through, are the swept test's: they are clamped.
        /// </summary>
        private void RefuseOnContact(NebulaWorker worker, NetworkIdentity entity, PortalLink farLink, bool leaving)
        {
            var physics = worker.PortalPhysicsIfAny;
            if (physics == null) return;
            var aperture = leaving ? physics.FarApertureOf(farLink.Id) : physics.NearApertureOf(PortalId);
            if (aperture == null || !physics.Touches(entity, aperture, worker.CurrentTick)) return;
            PortalLink link;
            PortalRefusal why;
            if (leaving)
            {
                link = farLink;
                if (link.State == PortalState.Open && !ObservationOnly) return; // leaving is never checked
                why = link.State == PortalState.Open ? PortalRefusal.NotAdmitted : PortalRefusal.LinkNotOpen;
            }
            else
            {
                link = ResolveLink(entity, out why);
                if (link != null && AdmitsThrough(entity, link, out why)) return;
            }
            NebulaDiagnostics.PortalRefusals++;
            RaiseRefused(entity, link, why);
        }
    }
}
