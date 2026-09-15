using System;
using System.Collections.Generic;

namespace Nebula
{
    /// <summary>What the scaler decided this pass.</summary>
    public enum ScaleAction
    {
        /// <summary>Nothing to do, or a condition is still being held.</summary>
        None,
        /// <summary>Add one worker.</summary>
        Grow,
        /// <summary>Retire one worker (<see cref="ScaleDecision.RetireWorkerId"/>).</summary>
        Shrink,
    }

    /// <summary>The two lines and the two clocks the scaler works to; a copy of the matching <see cref="NebulaConfig"/> fields.</summary>
    public struct ScaleSettings
    {
        /// <summary>The busiest worker above this fraction of its tick budget, held, means grow.</summary>
        public float ScaleOutUtilization;
        /// <summary>The mesh's mean below this, held, means shrink.</summary>
        public float ScaleInUtilization;
        /// <summary>How long a condition must hold before anything happens.</summary>
        public float HoldSeconds;
        /// <summary>The smallest drop in the predicted peak that makes an extra worker worth launching.</summary>
        public float MinGain;
        public int MinWorkers;
        public int MaxWorkers;

        public static ScaleSettings Default => new ScaleSettings
        {
            ScaleOutUtilization = 0.7f,
            ScaleInUtilization = 0.3f,
            HoldSeconds = 30f,
            MinGain = 0.1f,
            MinWorkers = 1,
            MaxWorkers = 32,
        };
    }

    /// <summary>The scaler's answer, and everything the dashboard needs to explain it.</summary>
    public struct ScaleDecision
    {
        public ScaleAction Action;
        /// <summary>The busiest worker's measured p90 utilization.</summary>
        public float Peak;
        /// <summary>Mean measured p90 utilization across the eligible workers.</summary>
        public float Mean;
        /// <summary>How long the current condition has held, and for how long it must, in seconds. Both 0 when nothing is held.</summary>
        public float HeldSeconds, HoldSeconds;
        /// <summary>The container that makes growing pointless (all the load and no way to split it), or "".</summary>
        public string BlockedBy;
        /// <summary>The worker that should retire when <see cref="Action"/> is <see cref="ScaleAction.Shrink"/>.</summary>
        public string RetireWorkerId;
        /// <summary>One line for the dashboard and the event log.</summary>
        public string Reason;
    }

    /// <summary>
    /// Decides when the mesh should gain or lose a worker, by asking the assignment policy what it would do rather
    /// than dividing an abstract cost by a worker count. Grow when the busiest worker has been over
    /// <see cref="ScaleSettings.ScaleOutUtilization"/> for the hold <i>and</i> a dry run at N+1 workers actually
    /// lowers the predicted peak by <see cref="ScaleSettings.MinGain"/>; shrink when the mesh's mean has been under
    /// <see cref="ScaleSettings.ScaleInUtilization"/> for the hold <i>and</i> a dry run at N-1 keeps everyone under
    /// the scale-out line. One change per hold: both clocks reset after a decision.
    /// <para>
    /// Pure except for the two hold clocks it keeps: it is fed (utilization, input, policy, settings, now) and
    /// returns a decision, so a whole history can be replayed in a unit test.
    /// </para>
    /// </summary>
    public sealed class WorkerScaler
    {
        /// <summary>
        /// May this worker be retired? The orchestrator installs a predicate that refuses a worker holding a
        /// <see cref="ContainerHint.Dedicated"/> container, so a reserved hub is never the machine that goes away.
        /// Default: anyone may go.
        /// </summary>
        public Func<string, bool> CanRetire = _ => true;

        /// <summary>
        /// Has this worker reported enough heartbeats to be judged? A worker launched moments ago has an empty
        /// window and reads as 0 % busy, which would drag the mesh's mean under the scale-in line and make the
        /// brand new machine the cheapest one to retire. The orchestrator wires this to
        /// <see cref="WorkerLoadTracker.IsWarm"/>; a cold worker is left out of the peak, the mean and the retiree
        /// choice, and holds the shrink branch until its window fills. Default: everyone counts.
        /// </summary>
        public Func<string, bool> IsWarm = _ => true;

        /// <summary>One managed worker as the settle check sees it (<see cref="IsInFlight"/>).</summary>
        public struct MeshMember
        {
            /// <summary>Draining its containers on the way out.</summary>
            public bool Retiring;
            /// <summary>In the host's idle pool: still in the managed list, but not one of the workers the mesh is running.</summary>
            public bool Parked;
        }

        /// <summary>
        /// Is a launch or a retirement still landing, making this pass's measurement meaningless? True while
        /// anything is draining, or while the number of workers actually in service differs from the count the
        /// orchestrator is driving towards. A parked worker is <i>not</i> in service: it is in the idle pool, and
        /// counting it would leave a parking host permanently "waiting for the mesh to settle" after a shrink.
        /// The same <c>!Retiring &amp;&amp; !Parked</c> rule the launch/retire reconciler uses. Pure function.
        /// </summary>
        public static bool IsInFlight(int retiringCount, IEnumerable<MeshMember> managed, int desiredWorkers)
        {
            if (retiringCount > 0) return true;
            int inService = 0;
            if (managed != null) foreach (var m in managed) if (!m.Retiring && !m.Parked) inService++;
            return inService != desiredWorkers;
        }

        /// <summary>
        /// The fewest workers the mesh may have right now. <see cref="ScaleSettings.MinWorkers"/> at 0 (scale to
        /// zero) only applies to an idle mesh: the moment a client is connected or a gateway is holding a join,
        /// one worker is the floor, so a shrink cannot race the wake-up that the pending join triggers.
        /// </summary>
        public static int FloorWorkers(int minWorkers, bool hasDemand)
        {
            if (minWorkers < 0) minWorkers = 0;
            return hasDemand ? Math.Max(1, minWorkers) : minWorkers;
        }

        private double _outSince = -1.0, _inSince = -1.0;

        /// <summary>How long the scale-out condition has held, in seconds, or 0.</summary>
        public float HeldOut(double now) => _outSince < 0.0 ? 0f : (float)(now - _outSince);
        /// <summary>How long the scale-in condition has held, in seconds, or 0.</summary>
        public float HeldIn(double now) => _inSince < 0.0 ? 0f : (float)(now - _inSince);

        /// <summary>Forget both holds (autoscaling was turned off, or the mesh is in flux).</summary>
        public void Reset() { _outSince = _inSince = -1.0; }

        /// <summary>
        /// One pass.
        /// </summary>
        /// <param name="now">Seconds on a monotonic clock.</param>
        /// <param name="utilization">p90 utilization per eligible worker (<see cref="WorkerLoadTracker.CopyUtilization"/>).</param>
        /// <param name="input">The same input the policy is about to run against, with <see cref="AssignmentInput.Utilization"/> filled in.</param>
        /// <param name="policy">The policy in force; it is run as a dry run, never applied.</param>
        /// <param name="desiredWorkers">The count the orchestrator is currently driving towards.</param>
        /// <param name="settings">The lines and limits.</param>
        /// <param name="inFlight">A launch or a retirement is still happening; hold everything until it settles.</param>
        public ScaleDecision Evaluate(double now, IReadOnlyDictionary<string, float> utilization, AssignmentInput input, IAssignmentPolicy policy, int desiredWorkers, in ScaleSettings settings, bool inFlight)
        {
            var d = new ScaleDecision { Action = ScaleAction.None, BlockedBy = "", RetireWorkerId = "", Reason = "idle", HoldSeconds = settings.HoldSeconds };
            int total = utilization != null ? utilization.Count : 0;
            if (total == 0 || policy == null || input == null)
            {
                Reset();
                d.Reason = total == 0 ? "no workers reporting" : "idle";
                return d;
            }
            // A worker whose window is still filling reads as idle; judging the mesh on it would retire the machine
            // that has just been launched. It counts towards the plan (it holds containers) but not towards the signal.
            int n = 0, cold = 0;
            foreach (var kv in utilization)
            {
                if (IsWarm != null && !IsWarm(kv.Key)) { cold++; continue; }
                n++;
                d.Mean += kv.Value;
                if (kv.Value > d.Peak) d.Peak = kv.Value;
            }
            if (n == 0)
            {
                Reset();
                d.Reason = $"waiting for the first samples from {cold} worker(s)";
                return d;
            }
            d.Mean /= n;
            if (inFlight)
            {
                Reset();
                d.Reason = "waiting for the mesh to settle";
                return d;
            }

            // ---- grow ------------------------------------------------------------------------------------
            if (d.Peak > settings.ScaleOutUtilization && desiredWorkers < settings.MaxWorkers)
            {
                if (_outSince < 0.0) _outSince = now;
                d.HeldSeconds = HeldOut(now);
                if (d.HeldSeconds < settings.HoldSeconds)
                {
                    d.Reason = $"holding: peak {d.Peak:0.00} for {d.HeldSeconds:0}/{settings.HoldSeconds:0} s";
                    return d;
                }
                var here = policy.Predict(input, total);
                var more = policy.Predict(input, total + 1);
                // A plan that left containers on the floor says nothing about the peak: the policy in force does not
                // place them from a dry run's empty lease list (the baked policy and runtime containers, say). Trust
                // what was measured instead of a predicted peak of 0, and say which it was.
                bool blind = here.Unassigned > 0 || more.Unassigned > 0;
                if (!blind && here.Peak <= settings.ScaleOutUtilization)
                {
                    // The load is on the wrong worker, not on too few workers - but only if the policy would in fact
                    // move something this pass. When it would not (its own balance rule is satisfied in cost units),
                    // waiting for a re-deal that never comes would hold the mesh over the line for ever.
                    var moves = policy.Compute(input);
                    if (moves != null && moves.Count > 0)
                    {
                        d.Reason = $"re-dealing: {total} worker(s) are enough once the containers move (predicted peak {here.Peak:0.00})";
                        return d;
                    }
                    Reset();
                    d.Action = ScaleAction.Grow;
                    d.Reason = $"growing to {desiredWorkers + 1}: peak {d.Peak:0.00} over {settings.ScaleOutUtilization:0.00} for {settings.HoldSeconds:0} s; the dry run predicts {here.Peak:0.00} but the policy is not re-dealing";
                    return d;
                }
                if (blind)
                {
                    Reset();
                    d.Action = ScaleAction.Grow;
                    d.Reason = $"growing to {desiredWorkers + 1}: peak {d.Peak:0.00} over {settings.ScaleOutUtilization:0.00} for {settings.HoldSeconds:0} s; the '{policy.Name}' policy left {Math.Max(here.Unassigned, more.Unassigned)} container(s) unplaced in the dry run, so the measurement decides";
                    return d;
                }
                if (here.Peak - more.Peak < settings.MinGain)
                {
                    d.BlockedBy = more.HeaviestContainer;
                    d.Reason = d.BlockedBy.Length > 0
                        ? $"blocked: {d.BlockedBy} carries {more.HeaviestUtilization:0.00} of a tick on its own and cannot be split; add a hint or split the cell"
                        : $"blocked: another worker would not lower the peak ({here.Peak:0.00} -> {more.Peak:0.00})";
                    return d;
                }
                Reset();
                d.Action = ScaleAction.Grow;
                d.Reason = $"growing to {desiredWorkers + 1}: peak {d.Peak:0.00} over {settings.ScaleOutUtilization:0.00} for {settings.HoldSeconds:0} s, predicted peak {here.Peak:0.00} -> {more.Peak:0.00}";
                return d;
            }
            _outSince = -1.0;

            // ---- shrink ----------------------------------------------------------------------------------
            if (d.Mean < settings.ScaleInUtilization && desiredWorkers > settings.MinWorkers && desiredWorkers > 0)
            {
                if (cold > 0)
                {
                    // A worker that has just joined has no window yet. Shrinking now would almost certainly retire it
                    // again (it reads as idle), so the mesh waits for its first samples instead of flapping.
                    Reset();
                    d.Reason = $"not shrinking yet: {cold} worker(s) have not filled their window";
                    return d;
                }
                if (_inSince < 0.0) _inSince = now;
                d.HeldSeconds = HeldIn(now);
                if (d.HeldSeconds < settings.HoldSeconds)
                {
                    d.Reason = $"holding: mean {d.Mean:0.00} for {d.HeldSeconds:0}/{settings.HoldSeconds:0} s";
                    return d;
                }
                int fewer = total - 1;
                var less = fewer > 0 ? policy.Predict(input, fewer) : null;
                float peakAfter = less != null ? less.Peak : 0f;
                // A dry run that could not place everything cannot vouch for the survivors either; the measured mean
                // being under the line for the whole hold is then the only evidence there is, and it says shrink.
                if (less != null && less.Unassigned > 0) peakAfter = 0f;
                if (fewer > 0 && peakAfter >= settings.ScaleOutUtilization)
                {
                    d.Reason = $"not shrinking: {fewer} worker(s) would peak at {peakAfter:0.00}";
                    return d;
                }
                string retire = PickRetiree(utilization);
                if (retire == null)
                {
                    d.Reason = "not shrinking: every worker is holding something that may not move";
                    return d;
                }
                Reset();
                d.Action = ScaleAction.Shrink;
                d.RetireWorkerId = retire;
                d.Reason = $"shrinking to {desiredWorkers - 1}: mean {d.Mean:0.00} under {settings.ScaleInUtilization:0.00} for {settings.HoldSeconds:0} s, {retire} is the cheapest to hand over";
                return d;
            }
            _inSince = -1.0;

            d.HeldSeconds = 0f;
            d.Reason = $"steady: peak {d.Peak:0.00}, mean {d.Mean:0.00}";
            return d;
        }

        /// <summary>The worker that costs least to hand over: the lowest attributed load among those that may retire.</summary>
        private string PickRetiree(IReadOnlyDictionary<string, float> utilization)
        {
            string best = null;
            float bestLoad = 0f;
            foreach (var kv in utilization)
            {
                if (CanRetire != null && !CanRetire(kv.Key)) continue;
                if (IsWarm != null && !IsWarm(kv.Key)) continue; // never the machine that has only just come up
                // Ties break on the id so the choice is stable between passes.
                if (best == null || kv.Value < bestLoad || (kv.Value == bestLoad && string.CompareOrdinal(kv.Key, best) > 0)) { best = kv.Key; bestLoad = kv.Value; }
            }
            return best;
        }
    }
}
