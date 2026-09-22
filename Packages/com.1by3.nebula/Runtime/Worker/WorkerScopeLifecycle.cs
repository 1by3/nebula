using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// The worker's half of the scope lifecycle (<c>docs/scope-lifecycle.md</c>): it keeps the idle clock of the
    /// scope parts it owns honest, performs the retire sequence for a scope the orchestrator moved to
    /// <see cref="ScopeState.Retiring"/>, and reports a restore finished for a scope in
    /// <see cref="ScopeState.Restoring"/>. It decides nothing — the orchestrator owns the state machine and this
    /// only ever acts on the state it reads off the control-plane document.
    /// <para>
    /// The retire sequence for one part, in order: the <b>before-retire window</b> (<see cref="BeforeRetire"/>, the
    /// seam NEB-242 exposes as <c>OnBeforeRetire(container, cancelToken)</c>), a forced checkpoint of every
    /// persistent entity in the part, the store's write barrier, emptying the part, and only then the
    /// acknowledgement the orchestrator waits for before it deletes the lease rows.
    /// </para>
    /// </summary>
    public sealed class WorkerScopeLifecycle
    {
        /// <summary>Seconds between passes. The steps are slow and rare; a pass per frame would only cost.</summary>
        public const float PassSeconds = 0.5f;
        /// <summary>
        /// How long the before-retire window may run before it is cancelled and the retire carries on regardless.
        /// Kept below <see cref="ScopeLifecycle.StepTimeoutSeconds"/> so the checkpoint still happens inside the
        /// orchestrator's deadline rather than being cut off by it.
        /// </summary>
        public const float BeforeRetireTimeoutSeconds = 10f;
        /// <summary>
        /// How long the restore of a scope's parts waits for the store to answer "has this scope any records?"
        /// before it goes ahead without <c>NebulaLifecycle.OnScopeActivating</c> having been raised. The safety
        /// valve of <c>docs/lifecycle-hooks.md</c> D4: a store that never answers must not stop a scope coming back.
        /// </summary>
        public const float ActivatingTimeoutSeconds = 10f;

        /// <summary>
        /// Game work that must happen before a scope's part is checkpointed and emptied — hand out rewards, write a
        /// summary row, close a session. Awaited (with <see cref="BeforeRetireTimeoutSeconds"/> and a cancellation
        /// token) before anything is saved, once per part per retire. Defaults to the public hook set
        /// (<see cref="NebulaLifecycle.OnBeforeRetire"/>); a test may replace it to drive the sequence directly.
        /// </summary>
        internal Func<Container, CancellationToken, Task> BeforeRetire = NebulaLifecycle.RaiseBeforeRetire;

        /// <summary>What this worker is doing about one part of one scope right now.</summary>
        private enum Step { Before, Checkpoint, Wait, Done }

        private sealed class PartWork
        {
            public string ScopeKey = "";
            public string ContainerId = "";
            /// <summary>The part itself, kept so <see cref="NebulaLifecycle.OnRetired"/> can name it after the lease has gone.</summary>
            public Container Container;
            public Step Step;
            public int Saved;
            public Task Before;
            public CancellationTokenSource Cancel;
            public float Deadline;
            public bool Written;
        }

        /// <summary>What this worker has done about one scope coming to life here (<see cref="NebulaLifecycle.OnScopeActivating"/>).</summary>
        private sealed class Activation
        {
            /// <summary>The store has been asked how many records the scope holds.</summary>
            public bool Asked;
            /// <summary>The hook has been raised (or there was nothing to raise it to).</summary>
            public bool Raised;
            /// <summary>The scope's parts may restore: the hook was raised, or the store did not answer in time.</summary>
            public bool Open;
            public float Deadline;
        }

        private readonly NebulaWorker _worker;
        private readonly Dictionary<string, PartWork> _retiring = new Dictionary<string, PartWork>(StringComparer.Ordinal);
        private readonly Dictionary<string, Activation> _activating = new Dictionary<string, Activation>(StringComparer.Ordinal);
        private readonly HashSet<string> _here = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<string> _scratch = new List<string>();
        private float _next;

        public WorkerScopeLifecycle(NebulaWorker worker) => _worker = worker;

        /// <summary>Parts this worker is currently retiring; for tests and the worker's own diagnostics.</summary>
        public int RetiringParts => _retiring.Count;

        /// <summary>Called from the worker's frame pass. Never from the tick loop.</summary>
        public void Update()
        {
            float now = Time.unscaledTime;
            if (now < _next) return;
            _next = now + PassSeconds;
            Pass(now);
        }

        /// <summary>One pass over the scopes this worker has a part of. Split out so a test can drive it by hand.</summary>
        internal void Pass(float now)
        {
            var cp = _worker.ControlPlane;
            if (cp == null || !cp.IsConnected) return;
            var scopes = cp.Scopes;
            if (scopes == null || scopes.Count == 0) { CancelAll(); _activating.Clear(); return; }

            _scratch.Clear();
            _here.Clear();
            for (int i = 0; i < scopes.Count; i++)
            {
                var scope = scopes[i];
                if (scope == null || scope.ContainerIds == null) continue;
                for (int c = 0; c < scope.ContainerIds.Count; c++)
                {
                    string containerId = scope.ContainerIds[c];
                    var container = ContainerRegistry.FindById(containerId);
                    if (container == null || !container.IsOwnedBy(_worker.WorkerId)) continue;
                    // This worker holds a part of the scope, so the scope is coming to life here (or already has):
                    // raise the activation hook once, and keep the parts' restores behind it until it has been.
                    if (_here.Add(scope.ScopeKey)) NoteActivating(scope, now);
                    switch (scope.State)
                    {
                        case ScopeState.Active:
                            // The mesh-wide idle clock: the lease row's age is what every worker already re-stamps
                            // while it wants a box, and it is what the scope's idle age is the smallest of. Stamping
                            // it while the part is busy is the whole of this worker's contribution to the decision.
                            KeepHot(cp, container, now);
                            break;
                        case ScopeState.Retiring:
                            _scratch.Add(containerId);
                            StepRetire(cp, scope, container, now);
                            break;
                        case ScopeState.Restoring:
                            ReportRestore(cp, scope, containerId);
                            break;
                    }
                }
            }
            // A scope this worker holds no part of any more starts again from scratch the next time it lands here.
            if (_activating.Count > 0)
            {
                var gone = new List<string>();
                foreach (var kv in _activating) if (!_here.Contains(kv.Key)) gone.Add(kv.Key);
                for (int i = 0; i < gone.Count; i++) _activating.Remove(gone[i]);
            }
            // A part that is no longer retiring here (the scope moved on, or the lease left) drops its work and
            // cancels whatever the game was doing in the before-retire window.
            if (_retiring.Count == 0) return;
            var stale = new List<string>();
            foreach (var kv in _retiring) if (!_scratch.Contains(kv.Key)) stale.Add(kv.Key);
            for (int i = 0; i < stale.Count; i++) Drop(stale[i]);
        }

        /// <summary>
        /// A scope is coming to life on this worker: ask the store whether it holds anything under the scope's key
        /// and raise <see cref="NebulaLifecycle.OnScopeActivating"/> with the answer, once. While the answer is
        /// outstanding the scope's parts do not restore (<see cref="MayRestore"/>), which is what puts the hook
        /// before <see cref="NebulaLifecycle.OnContainerRestored"/>. Nothing listening means no query and no wait.
        /// </summary>
        private void NoteActivating(ScopeInfo scope, float now)
        {
            string key = scope.ScopeKey;
            if (string.IsNullOrEmpty(key)) return;
            if (scope.State == ScopeState.Retiring || scope.State == ScopeState.Retired)
            {
                // The scope is going, not coming: forget it, so the next activation raises the hook again.
                _activating.Remove(key);
                return;
            }
            if (!_activating.TryGetValue(key, out var state))
            {
                state = new Activation { Deadline = now + ActivatingTimeoutSeconds };
                _activating[key] = state;
            }
            if (state.Raised) return;

            if (!NebulaLifecycle.HasScopeActivating)
            {
                // Nobody is listening: there is nothing to hold a restore for, and no reason to ask the store.
                state.Raised = true;
                state.Open = true;
                return;
            }
            var store = _worker.Persistence != null ? _worker.Persistence.Store : null;
            if (store == null)
            {
                // No store on this worker: nothing can have been saved, so the answer is "no records" and it is final.
                state.Raised = true;
                state.Open = true;
                NebulaLifecycle.RaiseScopeActivating(scope, false);
                return;
            }
            if (!state.Asked && store.IsConnected)
            {
                state.Asked = true;
                store.CountRecords(key, "", count =>
                {
                    if (!_activating.TryGetValue(key, out var pending) || !ReferenceEquals(pending, state) || pending.Raised) return;
                    pending.Raised = true;
                    pending.Open = true;
                    NebulaLifecycle.RaiseScopeActivating(scope, count > 0);
                });
                return;
            }
            if (!state.Open && now >= state.Deadline)
            {
                state.Open = true;
                NebulaLog.Warn($"scope '{key}': the store did not answer whether it holds any records within {ActivatingTimeoutSeconds:0} s; restoring its parts before OnScopeActivating");
            }
        }

        /// <summary>
        /// Whether the records of <paramref name="containerId"/> may be read now. False only while the container is
        /// part of a scope whose <see cref="NebulaLifecycle.OnScopeActivating"/> has not been raised yet. The
        /// worker points <c>NebulaPersistence.RestoreGate</c> here.
        /// </summary>
        internal bool MayRestore(string containerId)
        {
            if (!NebulaLifecycle.HasScopeActivating || string.IsNullOrEmpty(containerId)) return true;
            var container = ContainerRegistry.FindById(containerId);
            string key = container != null ? container.ScopeKey : "";
            if (string.IsNullOrEmpty(key)) return true;                       // the public world has no activation
            if (_activating.TryGetValue(key, out var state)) return state.Open;
            // Not noted yet: hold only while there is a scope row this worker's next pass will act on. A container
            // carrying a key no scope row names (the row was removed) would otherwise never restore.
            var cp = _worker.ControlPlane;
            return cp == null || cp.FindScope(key) == null;
        }

        /// <summary>
        /// Re-stamp the lease row of a busy scope part, so the scope's idle age only grows while nothing is in it.
        /// Busy means: a client owns an entity here, or an entity here would lose state if the part were emptied —
        /// one that does not persist at all, or one whose latest changes are not saved yet. That is what makes the
        /// default policy's promise ("it comes back identical") true rather than hopeful; a game that is happy to
        /// discard its transient contents says so through <see cref="ScopeLifecycle.ShouldRetire"/>.
        /// </summary>
        private void KeepHot(IControlPlane cp, Container container, float now)
        {
            var lease = cp.FindLease(container.ContainerId);
            if (lease == null) return;
            // A write per pass would be a control-plane write per pass per container; the row only has to be
            // younger than the retire threshold, and the touch interval is the same one a worker uses to say it
            // still wants a box it does not own.
            if ((cp.Now - lease.UpdatedAt).TotalSeconds < NebulaWorker.RuntimeTouchSeconds) return;
            if (!IsBusy(container)) return;
            cp.TouchContainer(container.ContainerId);
        }

        /// <summary>Whether emptying this container now would lose something. Pure over the container's contents.</summary>
        public static bool IsBusy(Container container)
        {
            if (container == null) return false;
            var entities = container.Entities;
            for (int i = 0; i < entities.Count; i++)
            {
                var e = entities[i];
                if (e == null || !e.IsSpawned || !e.HasAuthority) continue;
                if (e.OwnerClientId != 0) return true;         // an interested client is standing in it
                var pe = e.Persistent;
                if (pe == null) return true;                   // transient state a retire would destroy
                if (pe.IsDirty || PersistentStateCodec.HasDirtyVars(e)) return true; // changes not yet in the store
            }
            return false;
        }

        private void StepRetire(IControlPlane cp, ScopeInfo scope, Container container, float now)
        {
            string containerId = container.ContainerId;
            if (!_retiring.TryGetValue(containerId, out var work))
            {
                work = new PartWork { ScopeKey = scope.ScopeKey, ContainerId = containerId, Container = container, Step = Step.Before, Deadline = now + BeforeRetireTimeoutSeconds };
                _retiring[containerId] = work;
                var hook = BeforeRetire;
                if (hook != null)
                {
                    work.Cancel = new CancellationTokenSource();
                    try { work.Before = hook(container, work.Cancel.Token); }
                    catch (Exception e) { NebulaLog.Error($"the before-retire hook threw for {containerId}: {e}"); work.Before = null; }
                }
            }

            if (work.Step == Step.Before)
            {
                if (work.Before != null && !work.Before.IsCompleted)
                {
                    if (now < work.Deadline) return;
                    NebulaLog.Warn($"the before-retire hook for {containerId} did not finish within {BeforeRetireTimeoutSeconds:0} s; cancelling it and retiring anyway");
                    try { work.Cancel?.Cancel(); } catch { }
                }
                else if (work.Before != null && work.Before.IsFaulted)
                {
                    NebulaLog.Error($"the before-retire hook failed for {containerId}: {work.Before.Exception?.GetBaseException().Message}");
                }
                work.Step = Step.Checkpoint;
            }

            if (work.Step == Step.Checkpoint)
            {
                // The forced checkpoint: every persistent entity in the part, whatever the schedule said. It has to
                // happen while this worker still holds the lease, which is why the orchestrator waits for the ack
                // before it deletes the row.
                var persistence = _worker.Persistence;
                work.Saved = persistence != null ? persistence.CheckpointContainer(containerId) : 0;
                work.Step = Step.Wait;
                var store = persistence != null ? persistence.Store : null;
                if (store == null) work.Written = true;
                else store.WhenWritten(() => work.Written = true); // the barrier: the saves reached the store
                return;
            }

            if (work.Step != Step.Wait) return;
            if (!work.Written)
            {
                if (now < work.Deadline + BeforeRetireTimeoutSeconds) return;
                NebulaLog.Warn($"the checkpoint of {containerId} did not reach the store in time; acknowledging the retire anyway");
            }
            // Saved and durable: empty the part, then say so. Persistent entities keep their records (they come back
            // on re-activation); anything else in the box is gone, which is why a busy part is never retired.
            _worker.EmptyContainer(container);
            cp.AckScopePart(scope.ScopeKey, containerId, ScopePhase.Checkpointed, work.Saved, _worker.WorkerId);
            work.Step = Step.Done;
            NebulaLog.Info($"scope '{scope.ScopeKey}': checkpointed {work.Saved} entities in {containerId} and emptied it");
        }

        /// <summary>
        /// Report the part's restore finished, once. The restore itself is the ordinary lease-landing path
        /// (<see cref="NebulaPersistence.ContainerRestored"/>): this only turns "the load came back" into the
        /// acknowledgement the orchestrator is waiting for before the scope admits clients again.
        /// </summary>
        private void ReportRestore(IControlPlane cp, ScopeInfo scope, string containerId)
        {
            if (scope.FindAck(containerId, ScopePhase.Restored) != null) return;
            var persistence = _worker.Persistence;
            if (persistence == null || persistence.Store == null)
            {
                // No store on this worker: there is nothing to bring back and nothing to wait for.
                cp.AckScopePart(scope.ScopeKey, containerId, ScopePhase.Restored, 0, _worker.WorkerId);
                return;
            }
            if (!persistence.IsContainerRestored(containerId)) return;
            cp.AckScopePart(scope.ScopeKey, containerId, ScopePhase.Restored, persistence.RestoredCountFor(containerId), _worker.WorkerId);
        }

        private void Drop(string containerId)
        {
            if (!_retiring.TryGetValue(containerId, out var work)) return;
            try { work.Cancel?.Cancel(); } catch { }
            work.Cancel?.Dispose();
            _retiring.Remove(containerId);
            // The part finished its retire here and the scope has moved on: the orchestrator has released the lease
            // and the mesh is done with the box, which is the moment OnRetired names (docs/lifecycle-hooks.md D6).
            // A part dropped before it finished (the retire was abandoned) was never retired, so it says nothing.
            if (work.Step == Step.Done) NebulaLifecycle.RaiseRetired(work.Container);
        }

        internal void CancelAll()
        {
            if (_retiring.Count == 0) return;
            _scratch.Clear();
            foreach (var kv in _retiring) _scratch.Add(kv.Key);
            for (int i = 0; i < _scratch.Count; i++) Drop(_scratch[i]);
            _scratch.Clear();
        }
    }
}
