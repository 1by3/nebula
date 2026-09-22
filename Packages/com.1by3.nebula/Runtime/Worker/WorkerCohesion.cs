using System.Collections.Generic;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// The worker half of the cohesion hints (<c>docs/cohesion-hints.md</c>): container <b>holds</b>, and the
    /// summary of entity cohesion groups the worker reports to the orchestrator.
    /// <para>
    /// A hold is the game saying "do not rebalance this container for the next N seconds" - a boss fight, a
    /// cutscene, a door opening. It is a hint, not a lease: the orchestrator honours it while it is live and an
    /// orphaned container is placed whatever it says. Holds are reported in the telemetry document
    /// (<see cref="WorkerTelemetry"/>) as the seconds still to run, and the orchestrator turns them into a deadline
    /// on its own clock (D8), so no clock synchronisation is needed between worker and orchestrator.
    /// </para>
    /// </summary>
    public sealed partial class NebulaWorker
    {
        /// <summary>Longest a single <see cref="HoldContainer(string, float)"/> can defer rebalancing, in seconds.</summary>
        public const float MaxHoldSeconds = 120f;

        /// <summary>Container id -> the <see cref="Time.unscaledTime"/> at which its hold expires.</summary>
        private readonly Dictionary<string, float> _holds = new Dictionary<string, float>();
        private readonly List<string> _expiredHolds = new List<string>();

        /// <summary>One container this worker is asking the orchestrator not to move, and for how much longer.</summary>
        public readonly struct ContainerHold
        {
            public readonly string ContainerId;
            /// <summary>Seconds still to run when the hold was read. Always greater than zero.</summary>
            public readonly float SecondsRemaining;

            public ContainerHold(string containerId, float secondsRemaining)
            {
                ContainerId = containerId;
                SecondsRemaining = secondsRemaining;
            }
        }

        /// <summary>
        /// Ask the orchestrator to leave <paramref name="containerId"/> where it is for <paramref name="seconds"/>.
        /// Repeating the call extends the hold to the later of the two deadlines; <paramref name="seconds"/> at or
        /// below zero releases it. Clamped to <see cref="MaxHoldSeconds"/>: a hold is a pause, not a pin - the
        /// permanent form is <see cref="ContainerHint.Dedicated"/> or a pinned carried container.
        /// <para>
        /// The hold reaches the orchestrator with the next telemetry document (at most
        /// <see cref="WorkerTelemetry.IdleIntervalSeconds"/> later) and is forgotten if this worker stops reporting,
        /// so it never outlives the worker that asked for it.
        /// </para>
        /// </summary>
        public void HoldContainer(string containerId, float seconds)
        {
            if (string.IsNullOrEmpty(containerId)) return;
            if (seconds <= 0f) { _holds.Remove(containerId); return; }
            if (seconds > MaxHoldSeconds) seconds = MaxHoldSeconds;
            float until = Time.unscaledTime + seconds;
            _holds[containerId] = _holds.TryGetValue(containerId, out float current) && current > until ? current : until;
        }

        /// <summary>Hold a container object (see <see cref="HoldContainer(string, float)"/>).</summary>
        public void HoldContainer(Container container, float seconds)
        {
            if (container != null) HoldContainer(container.ContainerId, seconds);
        }

        /// <summary>Let a held container be rebalanced again before its hold expires.</summary>
        public void ReleaseHold(string containerId)
        {
            if (!string.IsNullOrEmpty(containerId)) _holds.Remove(containerId);
        }

        /// <summary>Seconds this worker's hold on a container still has to run, or 0 when it holds none.</summary>
        public float HoldRemaining(string containerId)
        {
            if (containerId == null || !_holds.TryGetValue(containerId, out float until)) return 0f;
            float left = until - Time.unscaledTime;
            return left > 0f ? left : 0f;
        }

        /// <summary>How many containers this worker is holding right now (expired holds are dropped when read).</summary>
        public int HeldContainers
        {
            get
            {
                CopyHolds(_holdScratch);
                return _holdScratch.Count;
            }
        }

        private readonly List<ContainerHold> _holdScratch = new List<ContainerHold>();

        /// <summary>
        /// Fill <paramref name="result"/> (cleared first) with the live holds and drop the expired ones. Called by
        /// <see cref="WorkerTelemetry"/> once per document.
        /// </summary>
        internal void CopyHolds(List<ContainerHold> result)
        {
            result.Clear();
            if (_holds.Count == 0) return;
            float now = Time.unscaledTime;
            _expiredHolds.Clear();
            foreach (var kv in _holds)
            {
                float left = kv.Value - now;
                if (left <= 0f) _expiredHolds.Add(kv.Key);
                else result.Add(new ContainerHold(kv.Key, left));
            }
            for (int i = 0; i < _expiredHolds.Count; i++) _holds.Remove(_expiredHolds[i]);
            _expiredHolds.Clear();
        }

        /// <summary>
        /// What one cohesion group costs this worker: which of its containers hold members of the group, and how
        /// many members it owns. The orchestrator unions the spans of every worker into the group the planner keeps
        /// on one worker (<c>docs/cohesion-hints.md</c>, D7).
        /// </summary>
        public struct CohesionSpan
        {
            public uint Group;
            public int Members;
            public List<string> Containers;
        }

        /// <summary>
        /// Fill <paramref name="result"/> (cleared first) with one row per cohesion group this worker owns members
        /// of. Entities in no container are counted but name no container, so a group that is entirely outside the
        /// container graph still shows up on the dashboard. Called once per telemetry document.
        /// </summary>
        internal void CopyCohesion(List<CohesionSpan> result)
        {
            result.Clear();
            if (CohesionGroups.GroupCount == 0) return;
            for (int i = 0; i < _authoritative.Count; i++)
            {
                var e = _authoritative[i];
                if (e == null || e.CohesionGroup == 0) continue;
                int at = -1;
                for (int j = 0; j < result.Count; j++) if (result[j].Group == e.CohesionGroup) { at = j; break; }
                if (at < 0)
                {
                    result.Add(new CohesionSpan { Group = e.CohesionGroup, Members = 0, Containers = new List<string>(2) });
                    at = result.Count - 1;
                }
                var span = result[at];
                span.Members++;
                string containerId = e.Container != null ? e.Container.ContainerId : "";
                if (containerId != "" && !span.Containers.Contains(containerId)) span.Containers.Add(containerId);
                result[at] = span;
            }
        }
    }
}
