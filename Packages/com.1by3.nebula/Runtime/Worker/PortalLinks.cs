using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// The worker's half of a <see cref="Portal"/>: the state machine of its links (<c>docs/portals.md</c> D9, D10) and what it
    /// publishes of them. The worker that owns the portal's container calls <see cref="Drive"/> once a tick.
    /// </summary>
    public sealed partial class Portal
    {
        private bool _ignored, _warnedDuplicate;

        /// <summary>Whether this component lost its id to another portal in the same container (<c>docs/portals.md</c> D6): it is ignored and does nothing.</summary>
        public bool IsIgnored => _ignored;

        internal void MarkDuplicate()
        {
            _ignored = true;
            if (_warnedDuplicate) return;
            _warnedDuplicate = true;
            NebulaLog.Warn($"Portal '{name}' repeats the id '{AuthoredId}' of another portal in its container; it is ignored. A portal's id must be unique within its container.");
        }

        internal void ClearDuplicate() => _ignored = false;

        // ------------------------------------------------------------------------------------------ the tick

        /// <summary>Advance every link one step on the worker that owns the portal's container.</summary>
        internal void Drive(NebulaWorker worker)
        {
            if (_crossings.Count > 0) PruneCrossings(worker.CurrentTick);
            if (_links.Count == 0) return;
            _linkScratch.Clear();
            _linkScratch.AddRange(_links.Values);
            for (int i = 0; i < _linkScratch.Count; i++) Step(worker, _linkScratch[i]);
            _linkScratch.Clear();
        }

        /// <summary>The near container is no longer this worker's and nobody could take the links (<c>docs/portals.md</c> D44): every live link reads closed, waiting on <see cref="PortalWait.NearContainerMoving"/>.</summary>
        internal void CloseForReassignment()
        {
            foreach (var link in _links.Values)
                if (link.State != PortalState.Closed) FinishClose(link, PortalWait.NearContainerMoving);
        }

        /// <summary>
        /// The near container is being reassigned and the worker taking it was sent the links (<c>docs/portals.md</c> D44): describe every link for the message, then
        /// let this worker's copies go. Each reads closed, waiting on <see cref="PortalWait.NearContainerMoving"/>, for whoever still holds a reference; the new owner
        /// has its own.
        /// </summary>
        internal void ReleaseLinks(List<PortalHandoffLink> into)
        {
            foreach (var link in _links.Values)
            {
                into.Add(new PortalHandoffLink
                {
                    Party = link.Party,
                    Generation = link.Generation,
                    State = link.State,
                    Members = new List<ulong>(link.MemberSet),
                    Warm = new List<ulong>(link.WarmSet),
                    Observers = new List<ulong>(link.ObserverSet),
                });
                if (link.State != PortalState.Closed) FinishClose(link, PortalWait.NearContainerMoving);
            }
            _links.Clear();
            _crossings.Clear();
            _leaderOf.Clear();
        }

        /// <summary>
        /// Take the links another worker handed over with the portal's near container (<c>docs/portals.md</c> D44). An open, preparing or closing link comes back
        /// preparing, waiting on <see cref="PortalWait.NearContainerMoving"/>: the far scope, the leases, this worker's own sides of the aperture and the
        /// content are checked again here, but the members that were warm are not asked again, and the generation goes on, so the clients' acknowledgements and the
        /// network gateways' records stay valid and the link is open again as soon as this worker has looked. A party that already has a live link here keeps it.
        /// </summary>
        internal void AcceptHandoff(IReadOnlyList<PortalHandoffLink> links)
        {
            for (int i = 0; i < links.Count; i++)
            {
                var handed = links[i];
                string party = handed.Party ?? "";
                if (_links.TryGetValue(party, out var existing) && existing.State != PortalState.Closed)
                {
                    foreach (ulong member in handed.Members) AddMemberTo(existing, member);
                    continue;
                }
                var link = existing ?? new PortalLink(this, party);
                _links[party] = link;
                if (handed.Generation > link.Generation) link.Generation = handed.Generation;
                link.MemberSet.Clear(); link.WarmSet.Clear(); link.ObserverSet.Clear();
                foreach (ulong member in handed.Members) link.MemberSet.Add(member);
                foreach (ulong member in handed.Warm) if (link.MemberSet.Contains(member)) link.WarmSet.Add(member);
                foreach (ulong observer in handed.Observers) link.ObserverSet.Add(observer);
                link.Rows.Clear();
                link.NeedsActivation = true;
                link.Error = null;
                link.Progress = 0f;
                link.Dirty = true;
                if (handed.State == PortalState.Closed) { SetState(link, PortalState.Closed, PortalWait.None); continue; }
                if (handed.State == PortalState.Closing)
                {
                    link.ClosingSince = Now;
                    SetState(link, PortalState.Closing, PortalWait.BandOccupied);
                    continue;
                }
                SetState(link, PortalState.Preparing, PortalWait.NearContainerMoving);
            }
        }

        private void Step(NebulaWorker worker, PortalLink link)
        {
            if (link.State == PortalState.Closed) return;
            var near = NearContainer;
            // The near container is being reassigned: this worker no longer owns the portal, and a crash would have lost the link anyway (D44).
            if (near == null || near.OwnerWorkerId != worker.WorkerId) { FinishClose(link, PortalWait.NearContainerMoving); return; }
            if (link.State == PortalState.Closing) { StepClosing(worker, link); return; }

            if (link.State == PortalState.Open) { StepOpen(worker, link); return; }

            var wait = Evaluate(worker, link, out float progress);
            if (link.State == PortalState.Closed) return;
            byte quantized = (byte)Mathf.Clamp(Mathf.RoundToInt(progress * 255f), 0, 255);
            if (quantized != link.PublishedProgress) { link.PublishedProgress = quantized; link.Dirty = true; }
            link.Progress = quantized / 255f;
            if (wait != PortalWait.ClientWarming && link.WaitClient != 0) { link.WaitClient = 0; link.Dirty = true; }
            if (wait == PortalWait.None)
            {
                for (int i = 0; i < link.Rows.Count; i++) link.Rows[i].ReadyEpoch = link.Rows[i].Container != null ? link.Rows[i].Container.LeaseEpoch : 0;
                link.NextStandingCheck = Now + StandingCheckSeconds;
                link.Progress = 1f;
                link.PublishedProgress = 255;
                SetState(link, PortalState.Open, PortalWait.None);
            }
            else SetState(link, PortalState.Preparing, wait);
        }

        /// <summary>How often an open link looks at its far scope's state. Everything else it holds is checked every tick, but a scope lookup is a scan.</summary>
        private const float StandingCheckSeconds = 1f;

        /// <summary>
        /// An open link keeps what made it open (<c>docs/portals.md</c> D10) and costs almost nothing a tick: it does not poll content or
        /// look the scope up again. It notices a far container whose lease moved under it, which makes the commit refuse (D16) and the link
        /// prepare again, handshake included; a far scope that began retiring (D43); and a member added since, which warms without closing the
        /// link for the others (D13).
        /// </summary>
        private void StepOpen(NebulaWorker worker, PortalLink link)
        {
            if (RowsMoved(link))
            {
                var gone = worker.ControlPlane.FindScope(link.FarScopeKey);
                if (gone == null || gone.State == ScopeState.Retiring || gone.State == ScopeState.Retired) { FinishClose(link, PortalWait.FarScopeRetired); return; }
                for (int i = 0; i < link.Rows.Count; i++) { link.Rows[i].AskDone = false; link.Rows[i].AskSent = false; }
                SetState(link, PortalState.Preparing, PortalWait.FarScopeActivating);
                return;
            }
            if (Now >= link.NextStandingCheck)
            {
                link.NextStandingCheck = Now + StandingCheckSeconds;
                var scope = worker.ControlPlane.FindScope(link.FarScopeKey);
                if (scope == null || scope.State == ScopeState.Retiring || scope.State == ScopeState.Retired) { FinishClose(link, PortalWait.FarScopeRetired); return; }
            }
            ulong waiting = 0;
            foreach (ulong member in link.MemberSet)
                if (!link.WarmSet.Contains(member) && (waiting == 0 || member < waiting)) waiting = member;
            if (waiting != 0)
            {
                if (link.WaitClient != waiting) { link.WaitClient = waiting; link.Dirty = true; }
                SetState(link, PortalState.Open, PortalWait.ClientWarming);
            }
            else
            {
                if (link.WaitClient != 0) { link.WaitClient = 0; link.Dirty = true; }
                SetState(link, PortalState.Open, PortalWait.None);
            }
        }

        /// <summary>Whether a container of the link's far window lost its owning lease, or its lease moved since the link became ready (<c>docs/portals.md</c> D16).</summary>
        private static bool RowsMoved(PortalLink link)
        {
            if (link.Rows.Count == 0) return true;
            for (int i = 0; i < link.Rows.Count; i++)
            {
                var c = link.Rows[i].Container;
                if (c == null || !LeaseState.IsOwning(c.LeaseState) || c.LeaseEpoch != link.Rows[i].ReadyEpoch) return true;
            }
            return false;
        }

        private void StepClosing(NebulaWorker worker, PortalLink link)
        {
            if (Now - link.ClosingSince >= CloseTimeoutSeconds)
            {
                NebulaDiagnostics.PortalCloseOverruns++;
                NebulaLog.Warn($"Portal '{AuthoredId}': the band of the link for '{link.Party}' was still occupied after {CloseTimeoutSeconds:0} s; closing it anyway");
                FinishClose(link, PortalWait.None);
                return;
            }
            if (BandOccupied(worker, link)) { SetState(link, PortalState.Closing, PortalWait.BandOccupied); return; }
            // Held closing a moment though the band is empty (CloseHoldSeconds): observers still draw the far side while the game closes its door over it.
            if (Now - link.ClosingSince < Mathf.Min(CloseHoldSeconds, CloseTimeoutSeconds - 0.5f)) { SetState(link, PortalState.Closing, PortalWait.CloseHold); return; }
            FinishClose(link, PortalWait.None);
        }

        /// <summary>
        /// Whether a body stands in the band of either face of the link (<c>docs/portals.md</c> D3, D26): tested on its extent when it has one, on its
        /// colliders' shapes otherwise and on its root when it has neither, so a ship whose hull is in the doorway holds the link open although its root is far
        /// away. Bodies this worker simulates and bodies it holds only as proxies across the portal count.
        /// </summary>
        private bool BandOccupied(NebulaWorker worker, PortalLink link)
        {
            var near = NearFace;
            var far = link.FarFace;
            // A side that simulates in a physics frame (a planet that is a carrier) has its face, and its bodies' poses, in that frame's coordinates (D26).
            var nearSpace = PortalSpaces.SpaceOf(NearContainer);
            var farSpace = PortalSpaces.SpaceOf(link.FarEntry);
            var nearFrame = PortalSpaces.FaceFrame(near, nearSpace);
            var farFrame = PortalSpaces.FaceFrame(far, farSpace);
            var band = new PortalBand(Aperture, BandMargin, BandDepth);
            var bodies = worker.PortalPhysics.Bodies;
            uint tick = worker.CurrentTick;
            return Occupies(bodies, worker.AuthoritativeEntities, near.Scope, link.FarScope, nearFrame, farFrame, nearSpace, farSpace, band, tick) ||
                   Occupies(bodies, worker.PortalGhostEntities, near.Scope, link.FarScope, nearFrame, farFrame, nearSpace, farSpace, band, tick);
        }

        private static bool Occupies(PortalBodies bodies, IReadOnlyList<NetworkIdentity> entities, ulong nearScope, ulong farScope,
            in PortalFaceFrame nearFrame, in PortalFaceFrame farFrame, Container nearSpace, Container farSpace, in PortalBand band, uint tick)
        {
            for (int i = 0; i < entities.Count; i++)
            {
                var entity = entities[i];
                if (entity == null || !entity.IsSpawned) continue;
                ulong scope = entity.InstanceId;
                if (scope != nearScope && scope != farScope) continue;
                var frame = scope == nearScope ? nearFrame : farFrame;
                if (!frame.Valid) continue;
                var info = bodies.Get(entity, tick);
                if (bodies.Touches(entity, info, tick, frame, band, 0f, null, 0f, scope == nearScope ? nearSpace : farSpace)) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------------------------------ readiness

        /// <summary>
        /// The first condition of <c>docs/portals.md</c> D10 that does not hold yet, or <see cref="PortalWait.None"/> when they all
        /// do, with how far along the link is. A far scope that retired closes a live link.
        /// </summary>
        private PortalWait Evaluate(NebulaWorker worker, PortalLink link, out float progress)
        {
            const float Stages = 6f;
            float done = 0f;
            progress = 0f;

            if (link.NeedsActivation && !Activate(worker, link)) return PortalWait.FarScopeActivating;

            // 1. The far scope is active, and the container a crossing enters is leased in an owning state.
            var scope = worker.ControlPlane.FindScope(link.FarScopeKey);
            if (scope == null) return PortalWait.FarScopeActivating;
            bool retired = scope.State == ScopeState.Retiring || scope.State == ScopeState.Retired;
            if (retired)
            {
                // A link that has been open closes with its far scope (D43); one still preparing waits for the scope it just asked for.
                if (link.State == PortalState.Preparing) return PortalWait.FarScopeActivating;
                FinishClose(link, PortalWait.FarScopeRetired);
                return PortalWait.FarScopeRetired;
            }
            done += 0.5f;
            var entry = ResolveEntry(link);
            link.FarEntry = entry;
            if (entry == null || !LeaseState.IsOwning(entry.LeaseState)) { progress = done / Stages; return PortalWait.FarScopeActivating; }
            done += 0.5f;
            // Every container of the far window is leased in an owning state (D10 condition 1): the rows a crossing may commit into (D16).
            RefreshRows(link, entry);
            for (int i = 0; i < link.Rows.Count; i++)
            {
                var row = link.Rows[i];
                if (!LeaseState.IsOwning(row.Container.LeaseState)) { progress = done / Stages; return PortalWait.FarScopeActivating; }
                if (row.ReadyEpoch != 0 && row.Container.LeaseEpoch != row.ReadyEpoch) { row.ReadyEpoch = 0; row.AskDone = false; row.AskSent = false; }
            }

            // 2. The far content is loaded on the worker that owns each row, and the near content on this one.
            bool handshaking = false, loading = false, heldThere = false;
            float loadProgress = 0f;
            bool announced = false;
            for (int i = 0; i < link.Rows.Count; i++)
            {
                var row = link.Rows[i];
                var rowContainer = row.Container;
                if (!string.IsNullOrEmpty(rowContainer.OwnerWorkerId) && rowContainer.OwnerWorkerId != worker.WorkerId)
                {
                    if (row.AskDone) continue;
                    if (!announced) { worker.PublishAdjacencyNow(this, link); announced = true; }
                    worker.AskFarWorker(link, row);
                    handshaking = true;
                    heldThere |= row.Held; // condition 6, answered by the far worker: ready but held
                    continue;
                }
                InstanceScenes.Prepare(rowContainer);
                var content = InstanceScenes.ContentState(rowContainer);
                if (content == InstanceContentState.Failed)
                {
                    link.Error = "The far container's content failed to load";
                    if (link.WarnedError != link.Error)
                    {
                        link.WarnedError = link.Error;
                        NebulaLog.Error($"Portal '{AuthoredId}': the content of {rowContainer.ContainerId} failed to load; the link for '{link.Party}' cannot open");
                    }
                    progress = done / Stages;
                    return PortalWait.FarWorkerContent;
                }
                if (content == InstanceContentState.Loading) { loading = true; loadProgress = Mathf.Max(loadProgress, InstanceScenes.ContentProgress(rowContainer)); }
            }
            if (handshaking) { progress = done / Stages; return heldThere ? PortalWait.FarContentHeld : PortalWait.FarWorkerHandshake; }
            link.Error = null;
            if (loading) { progress = (done + loadProgress) / Stages; return PortalWait.FarWorkerContent; }
            done += 1f;
            var nearContainer = NearContainer;
            InstanceScenes.Prepare(nearContainer);
            var nearContent = InstanceScenes.ContentState(nearContainer);
            if (nearContent == InstanceContentState.Loading) { progress = (done + InstanceScenes.ContentProgress(nearContainer)) / Stages; return PortalWait.FarWorkerContent; }
            if (nearContent == InstanceContentState.Failed) { link.Error = "The near container's content failed to load"; progress = done / Stages; return PortalWait.FarWorkerContent; }
            done += 1f;

            // 3. The far scope's persisted entities are restored.
            if (scope.State == ScopeState.Restoring) { progress = done / Stages; return PortalWait.FarEntitiesRestoring; }

            // 6. The application holds none of the far window's containers this worker owns (ReadinessHolds); a far worker's own holds were in its
            // answer to the handshake above. Checked after 3, so a game can populate a scope from its restored state before it lets anyone in.
            for (int i = 0; i < link.Rows.Count; i++)
            {
                var rowContainer = link.Rows[i].Container;
                bool here = string.IsNullOrEmpty(rowContainer.OwnerWorkerId) || rowContainer.OwnerWorkerId == worker.WorkerId;
                if (here && ReadinessHolds.IsHeld(rowContainer)) { progress = done / Stages; return PortalWait.FarContentHeld; }
            }
            done += 1f;

            // 4. Both workers have built the band's throat proxies and aperture colliders (D27, D28): this worker's sides here, and the far worker's
            // in its answer to the handshake above.
            if (!worker.PortalPhysics.PrepareLink(this, link, worker.CurrentTick)) { progress = done / Stages; return PortalWait.FarWorkerContent; }
            done += 1f;

            // 5. Every member's client is warm: it holds the far window and its content is ready.
            int warm = 0;
            ulong waiting = 0;
            foreach (ulong member in link.MemberSet)
            {
                if (link.WarmSet.Contains(member)) warm++;
                else if (waiting == 0 || member < waiting) waiting = member;
            }
            if (waiting != 0)
            {
                if (link.WaitClient != waiting) { link.WaitClient = waiting; link.Dirty = true; }
                progress = (done + (float)warm / link.MemberSet.Count) / Stages;
                return PortalWait.ClientWarming;
            }
            link.WaitClient = 0;
            progress = 1f;
            return PortalWait.None;
        }

        /// <summary>Create or find the far scope (a template's instance, prepared at the near face) and name the far face. False while it cannot be done.</summary>
        private bool Activate(NebulaWorker worker, PortalLink link)
        {
            if (HasFixedFarSide)
            {
                link.FarScopeKey = _fixedScopeKey;
                link.FarScope = ScopeKeys.Hash(_fixedScopeKey);
                link.FarFace = new PortalFace(link.FarScope, "", _fixedOrigin, _fixedRotation);
                link.NeedsActivation = false;
                return true;
            }
            if (FarTemplate == null)
            {
                link.Error = "The portal has neither a template nor a fixed far side";
                WarnLinkError(link);
                return false;
            }
            if (string.IsNullOrEmpty(link.Party))
            {
                link.Error = "A template far side needs a party: its far scope is TemplateId/party";
                WarnLinkError(link);
                return false;
            }
            try
            {
                // NEB-438: through the near scope's own pose when it is a turned instance, so the far scope's pose is against the world.
                TemplateFarPose(out var origin, out var rotation);
                var parts = worker.PrepareInstance(FarTemplate, link.Party, origin, rotation);
                link.FarScopeKey = FarTemplate.TemplateId + "/" + link.Party;
                link.FarScope = NebulaWorker.InstanceKey(link.FarScopeKey);
                link.FarEntryRuntimeId = parts[0].RuntimeId;
                link.FarEntryId = ContainerRegistry.RuntimeContainerId(link.FarEntryRuntimeId);
                link.FarFace = TemplateFarFace(link.FarScope, link.FarEntryId);
                link.NeedsActivation = false;
                link.Error = null;
                return true;
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException)
            {
                // A template or key the control plane refuses must not throw out of the worker's tick: the link just does not open.
                link.Error = e.Message;
                WarnLinkError(link);
                return false;
            }
        }

        private void WarnLinkError(PortalLink link)
        {
            if (link.WarnedError == link.Error) return;
            link.WarnedError = link.Error;
            NebulaLog.Warn($"Portal '{AuthoredId}' could not open the link for '{link.Party}': {link.Error}");
        }

        /// <summary>The far container a crossing enters: a template's entry part, or the container of a fixed far scope that holds the point just inside the far face.</summary>
        private static Container ResolveEntry(PortalLink link)
        {
            if (link.FarEntryRuntimeId != 0) return ContainerRegistry.GetRuntime(link.FarEntryRuntimeId);
            var inside = link.FarFace.ToAbsolute(new Double3(0.0, 0.0, 0.25));
            var found = FindDestination(ContainerRegistry.ToFrame(inside, link.FarScope), link.FarScope, null);
            link.FarEntryId = found != null ? found.ContainerId : "";
            return found;
        }

        /// <summary>The most containers of the far window a link keeps as rows: the entry and the nearest leased ones; a window over more ground than this is not warmed beyond it.</summary>
        internal const int MaxFarRows = 16;

        private static readonly List<Container> RowScratch = new List<Container>();
        private static readonly List<Container> OverlapScratch = new List<Container>();

        /// <summary>The smallest box of the scope's frame that holds <paramref name="face"/>'s axis-aligned region <c>|x| ≤ halfWidth, |y| ≤ halfHeight, zMin ≤ z ≤ zMax</c>.</summary>
        internal static Bounds FaceBounds(PortalFace face, float halfWidth, float halfHeight, float zMin, float zMax)
        {
            var first = ContainerRegistry.ToFrame(face.ToAbsolute(new Double3(-halfWidth, -halfHeight, zMin)), face.Scope);
            var bounds = new Bounds(first, Vector3.zero);
            for (int i = 1; i < 8; i++)
            {
                var corner = new Double3((i & 1) != 0 ? halfWidth : -halfWidth, (i & 2) != 0 ? halfHeight : -halfHeight, (i & 4) != 0 ? zMax : zMin);
                bounds.Encapsulate(ContainerRegistry.ToFrame(face.ToAbsolute(corner), face.Scope));
            }
            return bounds;
        }

        /// <summary>
        /// The containers of the far scope the far window touches that are leased on their own, the entry first and then the nearest ones, up to
        /// <see cref="MaxFarRows"/> (<c>docs/portals.md</c> D10, D16, D34). Worked out while the link prepares; an open link keeps the rows it opened with, because a
        /// container that appeared since is not warm on any client.
        /// </summary>
        private void RefreshRows(PortalLink link, Container entry)
        {
            RowScratch.Clear();
            RowScratch.Add(entry);
            var face = link.FarFace;
            float margin = Math.Max(0f, WindowMargin);
            var bounds = FaceBounds(face, Aperture.HalfWidth + margin, Aperture.HalfHeight + margin, 0f, Math.Max(0f, WindowDepth));
            ContainerRegistry.Overlapping(bounds, OverlapScratch, link.FarScope);
            var at = ContainerRegistry.ToFrame(face.Origin, face.Scope);
            OverlapScratch.Sort((a, b) => (a.WorldBounds.center - at).sqrMagnitude.CompareTo((b.WorldBounds.center - at).sqrMagnitude));
            for (int i = 0; i < OverlapScratch.Count && RowScratch.Count < MaxFarRows; i++)
            {
                var c = OverlapScratch[i];
                if (c == entry || !c.IsLeased || RowScratch.Contains(c)) continue;
                RowScratch.Add(c);
            }
            OverlapScratch.Clear();
            bool same = RowScratch.Count == link.Rows.Count;
            for (int i = 0; same && i < RowScratch.Count; i++) same = link.Rows[i].Container == RowScratch[i];
            if (same) { RowScratch.Clear(); return; }
            var kept = new List<FarRow>(RowScratch.Count);
            bool added = false;
            for (int i = 0; i < RowScratch.Count; i++)
            {
                var existing = link.RowOf(RowScratch[i]);
                if (existing == null) added = true;
                kept.Add(existing ?? new FarRow { Container = RowScratch[i] });
            }
            // A row no client has been warmed for: the members hold and prepare it before the link opens again, so the opening counts anew.
            if (added && link.Rows.Count > 0 && link.MemberSet.Count > 0) { link.Generation++; link.WarmSet.Clear(); }
            link.Rows.Clear();
            link.Rows.AddRange(kept);
            link.Dirty = true;
            RowScratch.Clear();
        }

        // ------------------------------------------------------------------------------------------ what is published

        internal PortalRecord ToRecord()
        {
            var near = NearFace;
            var container = NearContainer;
            var record = new PortalRecord
            {
                PortalId = PortalId,
                Id = AuthoredId,
                NearScope = near.Scope,
                NearContainerId = near.ContainerId,
                NearContainer = ContainerRef.Of(container),
                NearOrigin = near.Origin,
                NearRotation = near.Rotation,
                FarKind = HasFixedFarSide ? PortalFarKind.Fixed : PortalFarKind.Template,
                FarScope = HasFixedFarSide ? ScopeKeys.Hash(_fixedScopeKey) : 0UL,
                FarOrigin = HasFixedFarSide ? _fixedOrigin : TemplateFarOrigin(),
                FarRotation = HasFixedFarSide ? _fixedRotation : (TurnWithFace ? Quaternion.identity : near.Rotation),
                TemplateId = FarTemplate != null ? FarTemplate.TemplateId : "",
                ApertureKind = Aperture.Kind,
                HalfWidth = Aperture.HalfWidth,
                HalfHeight = Aperture.HalfHeight,
                BandMargin = BandMargin,
                BandDepth = BandDepth,
                WindowMargin = WindowMargin,
                WindowDepth = WindowDepth,
                Flags = ObservationOnly ? PortalRecord.ObservationOnlyFlag : (byte)0,
            };
            return record;
        }

        private static List<string> RowIds(PortalLink link)
        {
            var ids = new List<string>(link.Rows.Count);
            for (int i = 0; i < link.Rows.Count; i++) ids.Add(link.Rows[i].Container.ContainerId);
            return ids;
        }

        private static List<ContainerRef> RowRefs(PortalLink link)
        {
            var refs = new List<ContainerRef>(link.Rows.Count);
            for (int i = 0; i < link.Rows.Count; i++) refs.Add(ContainerRef.Of(link.Rows[i].Container));
            return refs;
        }

        internal static PortalLinkRecord ToLinkRecord(PortalLink link)
        {
            var portal = link.Portal;
            var flags = PortalLinkFlags.None;
            if (portal.ObserveAnyone) flags |= PortalLinkFlags.ObserverAnyone;
            var record = new PortalLinkRecord
            {
                LinkId = link.Id,
                PortalId = portal.PortalId,
                PartyHash = PortalIds.PartyHash(link.Party),
                FarScope = link.FarScope,
                FarEntryId = link.FarEntry != null ? link.FarEntry.ContainerId : link.FarEntryId,
                FarEntry = link.FarEntry != null ? ContainerRef.Of(link.FarEntry) : ContainerRef.None,
                State = link.State,
                Wait = link.Wait,
                WaitClient = link.WaitClient,
                Progress = link.PublishedProgress,
                Generation = link.Generation,
                Flags = flags,
                FarRowIds = RowIds(link),
                FarRows = RowRefs(link),
                Members = new List<ulong>(link.MemberSet),
                Observers = new List<ulong>(link.ObserverSet),
            };
            return record;
        }
    }
}
