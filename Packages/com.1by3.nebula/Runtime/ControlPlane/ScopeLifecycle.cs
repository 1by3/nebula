using System;
using System.Collections.Generic;

namespace Nebula
{
    /// <summary>
    /// What the retire policy is told about a scope. Everything here is derived from rows the whole mesh can see
    /// (the scope row, its lease rows, and the per-container occupancy the workers' telemetry reports), so the
    /// decision is reproducible on any orchestrator and a policy can be tested without a mesh.
    /// </summary>
    public struct ScopeRetireContext
    {
        /// <summary>The row being judged. Never null when the policy is called.</summary>
        public ScopeInfo Scope;
        /// <summary>
        /// Seconds since anything last wanted any part of this scope: the smallest age over the lease rows of its
        /// live parts (<see cref="ScopeLifecycle.CollectParts"/>). One busy part keeps the whole scope hot, which is
        /// what "all parts retire together" requires.
        /// </summary>
        public double IdleSeconds;
        /// <summary>Authoritative entities the workers last reported inside the scope's live parts, summed.</summary>
        public int Entities;
        /// <summary>Client-owned entities inside the scope's live parts (players and bots), summed.</summary>
        public int Players;
        /// <summary>The configured idle threshold, in seconds (<c>NebulaConfig.ScopeIdleRetireSeconds</c>); 0 or less turns retiring off.</summary>
        public float RetireAfterSeconds;
    }

    /// <summary>The game's answer to "should this scope go now?". See <see cref="ScopeLifecycle.ShouldRetire"/>.</summary>
    public delegate bool ScopeRetirePolicy(in ScopeRetireContext context);

    /// <summary>
    /// The scope lifecycle's pure parts: how idle age and occupancy are aggregated from a scope's parts, when a
    /// scope should retire, and whether a client may be admitted to it. The orchestrator drives the state machine
    /// with these; they hold no state of their own, so a test can drive the whole sequence without a mesh.
    /// Design of record: <c>docs/scope-lifecycle.md</c>.
    /// </summary>
    public static class ScopeLifecycle
    {
        /// <summary>
        /// How long the orchestrator waits for every part to acknowledge a retire or a restore before it gives up
        /// and finishes the step anyway. A worker that died mid-step must not leave a scope stuck in
        /// <see cref="ScopeState.Retiring"/> (nothing could ever enter it again) or in
        /// <see cref="ScopeState.Restoring"/> (nobody could ever join it).
        /// </summary>
        public const float StepTimeoutSeconds = 30f;

        /// <summary>
        /// Consulted for every <see cref="ScopeState.Active"/> scope on the orchestrator's idle sweep. Replace it to
        /// keep a scope hot for reasons Nebula cannot see — a raid whose party is still in the queue, a housing
        /// instance its owner is about to re-enter, a scope a live event is scheduled in. The default is
        /// <see cref="RetireWhenIdle"/>. It is consulted, never obeyed blindly: a scope that is not idle is never
        /// offered to it.
        /// </summary>
        public static ScopeRetirePolicy ShouldRetire = RetireWhenIdle;

        /// <summary>
        /// The default policy: retire once the scope has held no client-owned entity and no authoritative entity at
        /// all for <see cref="ScopeRetireContext.RetireAfterSeconds"/>. Pure.
        /// </summary>
        public static bool RetireWhenIdle(in ScopeRetireContext context)
        {
            if (context.RetireAfterSeconds <= 0f) return false;
            if (context.Players > 0 || context.Entities > 0) return false;
            return context.IdleSeconds >= context.RetireAfterSeconds;
        }

        /// <summary>
        /// The scope's <b>live parts</b>, into <paramref name="into"/> (cleared first): the containers its row names,
        /// then every other lease row whose <see cref="InstanceContainerInfo.ScopeKey"/> is the scope's key. For a
        /// <see cref="ScopeKind.Parts"/> scope the two are the same set. A <see cref="ScopeKind.Grid"/> scope's row
        /// names only its anchor chunk; the rest are the chunks the allocator leased on demand around the scope's
        /// pawns. Idle age, occupancy and the retire sequence are all judged over this set, so a player standing
        /// three chunks from the anchor keeps the scope alive (<c>docs/scope-lifecycle.md</c> D4).
        /// </summary>
        public static void CollectParts(IControlPlane cp, ScopeInfo scope, List<string> into)
        {
            if (into == null) throw new ArgumentNullException(nameof(into));
            into.Clear();
            if (scope == null) return;
            if (scope.ContainerIds != null) into.AddRange(scope.ContainerIds);
            var leases = cp != null ? cp.Leases : null;
            if (leases == null || string.IsNullOrEmpty(scope.ScopeKey)) return;
            for (int i = 0; i < leases.Count; i++)
            {
                var lease = leases[i];
                if (lease?.Instance == null || !string.Equals(lease.Instance.ScopeKey, scope.ScopeKey, StringComparison.Ordinal)) continue;
                if (scope.ContainerIds != null && scope.ContainerIds.Contains(lease.ContainerId)) continue;
                into.Add(lease.ContainerId);
            }
        }

        /// <summary>
        /// <see cref="CollectParts"/> for every scope row at once, keyed by scope key, in one pass over the lease
        /// rows. For a caller that visits every scope: the orchestrator's sweep and a worker's lifecycle pass.
        /// Lists already in <paramref name="into"/> are cleared and reused.
        /// </summary>
        public static void IndexParts(IControlPlane cp, Dictionary<string, List<string>> into)
        {
            if (into == null) throw new ArgumentNullException(nameof(into));
            foreach (var list in into.Values) list.Clear();
            var scopes = cp != null ? cp.Scopes : null;
            if (scopes == null) return;
            for (int i = 0; i < scopes.Count; i++)
            {
                var scope = scopes[i];
                if (scope == null || string.IsNullOrEmpty(scope.ScopeKey)) continue;
                if (!into.TryGetValue(scope.ScopeKey, out var parts)) into[scope.ScopeKey] = parts = new List<string>();
                if (scope.ContainerIds != null) parts.AddRange(scope.ContainerIds);
            }
            var leases = cp.Leases;
            if (leases == null) return;
            for (int i = 0; i < leases.Count; i++)
            {
                var lease = leases[i];
                string key = lease?.Instance?.ScopeKey;
                if (string.IsNullOrEmpty(key) || !into.TryGetValue(key, out var parts)) continue;
                var scope = cp.FindScope(key);
                if (scope?.ContainerIds != null && scope.ContainerIds.Contains(lease.ContainerId)) continue;
                parts.Add(lease.ContainerId);
            }
        }

        /// <summary>Seconds since anything last wanted any live part of the scope (<see cref="CollectParts"/>); see the other overload.</summary>
        public static double IdleSeconds(IControlPlane cp, ScopeInfo scope)
        {
            if (cp == null || scope == null) return 0.0;
            var parts = new List<string>();
            CollectParts(cp, scope, parts);
            return IdleSeconds(cp, parts);
        }

        /// <summary>
        /// Seconds since anything last wanted any of <paramref name="parts"/>: the smallest lease-row age over them.
        /// A part with no lease row does not take part (it is already gone), and a scope with no leased part at all
        /// is 0 — there is nothing there to retire. The row's age is the mesh-wide idle clock every worker already
        /// re-stamps while it wants a box (<see cref="IControlPlane.TouchContainer"/>,
        /// <see cref="NebulaWorker.RuntimeContainerIdleSeconds"/>).
        /// </summary>
        public static double IdleSeconds(IControlPlane cp, IReadOnlyList<string> parts)
        {
            if (cp == null || parts == null || parts.Count == 0 || cp.Leases == null) return 0.0;
            // One pass over the lease rows: a grid scope can have thousands of parts, and a lookup per part would be
            // a scan of every lease row per part.
            var wanted = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < parts.Count; i++) if (parts[i] != null) wanted.Add(parts[i]);
            double smallest = double.PositiveInfinity;
            var leases = cp.Leases;
            var now = cp.Now;
            for (int i = 0; i < leases.Count; i++)
            {
                var lease = leases[i];
                if (lease == null || lease.ContainerId == null || !wanted.Contains(lease.ContainerId)) continue;
                double age = Math.Max(0.0, (now - lease.UpdatedAt).TotalSeconds);
                if (age < smallest) smallest = age;
            }
            return double.IsPositiveInfinity(smallest) ? 0.0 : smallest;
        }

        /// <summary>
        /// What the workers last reported inside the containers the scope's row names, summed over them. A grid
        /// scope's row names only its anchor: pass its live parts (<see cref="CollectParts"/>) to the other overload.
        /// </summary>
        public static void Occupancy(ScopeInfo scope, IReadOnlyDictionary<string, ContainerLoad> occupancy, out int entities, out int players) =>
            Occupancy(scope?.ContainerIds, occupancy, out entities, out players);

        /// <summary>What the workers last reported inside <paramref name="parts"/>, summed over them. Absent parts count 0.</summary>
        public static void Occupancy(IReadOnlyList<string> parts, IReadOnlyDictionary<string, ContainerLoad> occupancy, out int entities, out int players)
        {
            entities = 0; players = 0;
            if (parts == null || occupancy == null) return;
            for (int i = 0; i < parts.Count; i++)
            {
                if (!occupancy.TryGetValue(parts[i], out var load)) continue;
                entities += load.Authoritative;
                players += load.Players + load.Bots;
            }
        }

        /// <summary>
        /// Whether a client naming <paramref name="scopeKey"/> may be placed now, and why not when it may not. A key
        /// with no row at all is <see cref="JoinHoldReason.ScopeNotReady"/>: the gateway never activates a scope, so
        /// the client waits for whoever sent it there (<c>docs/scope-activation.md</c> §5). The public world (an
        /// empty key) is always admitted here; whether there is a container to spawn into is the caller's next
        /// question.
        /// </summary>
        public static bool Admits(IControlPlane cp, string scopeKey, out JoinHoldReason reason)
        {
            reason = JoinHoldReason.None;
            if (string.IsNullOrEmpty(scopeKey)) return true;
            var scope = cp != null ? cp.FindScope(scopeKey) : null;
            if (scope == null) { reason = JoinHoldReason.ScopeNotReady; return false; }
            switch (scope.State)
            {
                case ScopeState.Retiring: reason = JoinHoldReason.ScopeRetiring; return false;
                case ScopeState.Retired: reason = JoinHoldReason.ScopeNotReady; return false;
                case ScopeState.Restoring: reason = JoinHoldReason.ScopeRestoring; return false;
                default: return true;
            }
        }

        /// <summary>
        /// The next state of a scope in a step that waits on its parts, or null when it should stay where it is.
        /// Pure, so the ordering ("every part, or the deadline") is one testable function rather than a shape of
        /// the orchestrator's loop: <see cref="ScopeState.Retiring"/> ends in <see cref="ScopeState.Retired"/> and
        /// <see cref="ScopeState.Restoring"/> in <see cref="ScopeState.Active"/>, once every container has
        /// acknowledged the step's phase or <paramref name="elapsedSeconds"/> has passed
        /// <see cref="StepTimeoutSeconds"/>.
        /// </summary>
        public static string NextState(ScopeInfo scope, double elapsedSeconds, out bool timedOut) =>
            NextState(scope, null, elapsedSeconds, out timedOut);

        /// <summary>
        /// <see cref="NextState(ScopeInfo, double, out bool)"/> for a scope whose live parts
        /// (<paramref name="parts"/>, from <see cref="CollectParts"/>) go beyond the containers its row names. A
        /// retire waits for <b>every live part</b> to acknowledge its checkpoint: each chunk a grid scope leased on
        /// demand is checkpointed and emptied like the anchor. A restore waits for the row's containers only,
        /// because re-activation recreates only those; a grid's other chunks come back through the ordinary
        /// lease-landing restore when pawns ring them again.
        /// </summary>
        public static string NextState(ScopeInfo scope, IReadOnlyList<string> parts, double elapsedSeconds, out bool timedOut)
        {
            timedOut = false;
            if (scope == null) return null;
            bool done;
            string next;
            switch (scope.State)
            {
                case ScopeState.Retiring:
                    next = ScopeState.Retired;
                    done = scope.AllAcked(ScopePhase.Checkpointed) && AllAcked(scope, parts, ScopePhase.Checkpointed);
                    break;
                case ScopeState.Restoring:
                    next = ScopeState.Active;
                    done = scope.AllAcked(ScopePhase.Restored);
                    break;
                default: return null;
            }
            if (done) return next;
            if (elapsedSeconds >= StepTimeoutSeconds) { timedOut = true; return next; }
            return null;
        }

        private static bool AllAcked(ScopeInfo scope, IReadOnlyList<string> parts, string phase)
        {
            if (parts == null || parts.Count == 0) return true;
            var acked = new HashSet<string>(StringComparer.Ordinal);
            if (scope.Acks != null)
                foreach (var a in scope.Acks) if (string.Equals(a.Phase, phase, StringComparison.Ordinal) && a.ContainerId != null) acked.Add(a.ContainerId);
            for (int i = 0; i < parts.Count; i++) if (!acked.Contains(parts[i])) return false;
            return true;
        }
    }
}
