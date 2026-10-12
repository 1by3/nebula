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
        private bool _ignored, _warnedDuplicate, _warnedObserve;

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

        /// <summary>The near container is no longer this worker's (<c>docs/portals.md</c> D44): every live link reads closed, waiting on <see cref="PortalWait.NearContainerMoving"/>.</summary>
        internal void CloseForReassignment()
        {
            foreach (var link in _links.Values)
                if (link.State != PortalState.Closed) FinishClose(link, PortalWait.NearContainerMoving);
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
                link.ReadyEpoch = link.FarEntry != null ? link.FarEntry.LeaseEpoch : 0;
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
            var entry = link.FarEntry;
            if (entry == null || !LeaseState.IsOwning(entry.LeaseState) || entry.LeaseEpoch != link.ReadyEpoch)
            {
                var gone = worker.ControlPlane.FindScope(link.FarScopeKey);
                if (gone == null || gone.State == ScopeState.Retiring || gone.State == ScopeState.Retired) { FinishClose(link, PortalWait.FarScopeRetired); return; }
                link.HandshakeDone = false;
                link.HandshakeSent = false;
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
            FinishClose(link, PortalWait.None);
        }

        /// <summary>Whether an entity's root stands in the band of either face of the link (<c>docs/portals.md</c> D3): <c>|z| ≤ BandDepth</c> and in the aperture grown by <see cref="BandMargin"/>.</summary>
        private bool BandOccupied(NebulaWorker worker, PortalLink link)
        {
            var near = NearFace;
            var far = link.FarFace;
            var entities = worker.AuthoritativeEntities;
            for (int i = 0; i < entities.Count; i++)
            {
                var entity = entities[i];
                if (entity == null || !entity.IsSpawned) continue;
                ulong scope = entity.InstanceId;
                PortalFace face;
                if (scope == near.Scope) face = near;
                else if (scope == link.FarScope) face = far;
                else continue;
                var local = face.ToLocal(AbsoluteOf(entity));
                if (Math.Abs(local.Z) <= BandDepth && Aperture.Contains(local.X, local.Y, BandMargin)) return true;
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
            if (link.ReadyEpoch != 0 && entry.LeaseEpoch != link.ReadyEpoch) link.HandshakeDone = false;

            // 2. The far content is loaded on the worker that owns it, and the near content on this one.
            if (!string.IsNullOrEmpty(entry.OwnerWorkerId) && entry.OwnerWorkerId != worker.WorkerId)
            {
                if (!link.HandshakeDone)
                {
                    worker.AskFarWorker(link, entry);
                    progress = done / Stages;
                    return PortalWait.FarWorkerHandshake;
                }
                done += 1f;
            }
            else
            {
                InstanceScenes.Prepare(entry);
                var content = InstanceScenes.ContentState(entry);
                if (content == InstanceContentState.Failed)
                {
                    link.Error = "The far container's content failed to load";
                    if (link.WarnedError != link.Error)
                    {
                        link.WarnedError = link.Error;
                        NebulaLog.Error($"Portal '{AuthoredId}': the content of {entry.ContainerId} failed to load; the link for '{link.Party}' cannot open");
                    }
                    progress = done / Stages;
                    return PortalWait.FarWorkerContent;
                }
                link.Error = null;
                if (content == InstanceContentState.Loading)
                {
                    progress = (done + InstanceScenes.ContentProgress(entry)) / Stages;
                    return PortalWait.FarWorkerContent;
                }
                done += 1f;
            }
            var nearContainer = NearContainer;
            InstanceScenes.Prepare(nearContainer);
            var nearContent = InstanceScenes.ContentState(nearContainer);
            if (nearContent == InstanceContentState.Loading) { progress = (done + InstanceScenes.ContentProgress(nearContainer)) / Stages; return PortalWait.FarWorkerContent; }
            if (nearContent == InstanceContentState.Failed) { link.Error = "The near container's content failed to load"; progress = done / Stages; return PortalWait.FarWorkerContent; }
            done += 1f;

            // 3. The far scope's persisted entities are restored.
            if (scope.State == ScopeState.Restoring) { progress = done / Stages; return PortalWait.FarEntitiesRestoring; }
            done += 1f;

            // 4. Both workers have built the band's throat proxies and aperture colliders: nothing is built yet (NEB-432).
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
            if (NearScope != 0 && FarTemplate.ObservePublic && !_warnedObserve)
            {
                _warnedObserve = true;
                NebulaLog.Warn($"Portal '{AuthoredId}' stands in a scoped grid, but its template '{FarTemplate.TemplateId}' has ObservePublic on: occupants would see the public world, not this scope. Turn ObservePublic off for portals in scoped grids, and turn ObserveHost on to keep this scope's ground loaded while occupants are inside.");
            }
            try
            {
                var near = NearFace;
                var parts = worker.PrepareInstance(FarTemplate, link.Party, near.Origin, PoseRotation);
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
                FarOrigin = HasFixedFarSide ? _fixedOrigin : Double3.Zero,
                FarRotation = HasFixedFarSide ? _fixedRotation : Quaternion.identity,
                TemplateId = FarTemplate != null ? FarTemplate.TemplateId : "",
                ApertureKind = Aperture.Kind,
                HalfWidth = Aperture.HalfWidth,
                HalfHeight = Aperture.HalfHeight,
                BandMargin = BandMargin,
                BandDepth = BandDepth,
                WindowMargin = WindowMargin,
                WindowDepth = WindowDepth,
            };
            return record;
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
                Members = new List<ulong>(link.MemberSet),
                Observers = new List<ulong>(link.ObserverSet),
            };
            return record;
        }
    }
}
