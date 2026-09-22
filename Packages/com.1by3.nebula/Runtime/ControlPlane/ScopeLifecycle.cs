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
        /// Seconds since anything last wanted any part of this scope: the smallest age over its lease rows
        /// (<see cref="ScopeLifecycle.IdleSeconds"/>). One busy part keeps the whole scope hot, which is what
        /// "all parts retire together" requires.
        /// </summary>
        public double IdleSeconds;
        /// <summary>Authoritative entities the workers last reported inside the scope's parts, summed.</summary>
        public int Entities;
        /// <summary>Client-owned entities inside the scope's parts (players and bots), summed.</summary>
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
        /// Seconds since anything last wanted any part of the scope: the smallest lease-row age over its containers.
        /// A part with no lease row does not take part (it is already gone), and a scope with no leased part at all
        /// is 0 — there is nothing there to retire. The row's age is the mesh-wide idle clock every worker already
        /// re-stamps while it wants a box (<see cref="IControlPlane.TouchContainer"/>,
        /// <see cref="NebulaWorker.RuntimeContainerIdleSeconds"/>).
        /// </summary>
        public static double IdleSeconds(IControlPlane cp, ScopeInfo scope)
        {
            if (cp == null || scope == null || scope.ContainerIds == null) return 0.0;
            double smallest = double.PositiveInfinity;
            foreach (var id in scope.ContainerIds)
            {
                var lease = cp.FindLease(id);
                if (lease == null) continue;
                double age = Math.Max(0.0, (cp.Now - lease.UpdatedAt).TotalSeconds);
                if (age < smallest) smallest = age;
            }
            return double.IsPositiveInfinity(smallest) ? 0.0 : smallest;
        }

        /// <summary>What the workers last reported inside the scope's parts, summed over them. Absent parts count 0.</summary>
        public static void Occupancy(ScopeInfo scope, IReadOnlyDictionary<string, ContainerLoad> occupancy, out int entities, out int players)
        {
            entities = 0; players = 0;
            if (scope == null || scope.ContainerIds == null || occupancy == null) return;
            foreach (var id in scope.ContainerIds)
            {
                if (!occupancy.TryGetValue(id, out var load)) continue;
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
        public static string NextState(ScopeInfo scope, double elapsedSeconds, out bool timedOut)
        {
            timedOut = false;
            if (scope == null) return null;
            string phase, next;
            switch (scope.State)
            {
                case ScopeState.Retiring: phase = ScopePhase.Checkpointed; next = ScopeState.Retired; break;
                case ScopeState.Restoring: phase = ScopePhase.Restored; next = ScopeState.Active; break;
                default: return null;
            }
            if (scope.AllAcked(phase)) return next;
            if (elapsedSeconds >= StepTimeoutSeconds) { timedOut = true; return next; }
            return null;
        }
    }
}
