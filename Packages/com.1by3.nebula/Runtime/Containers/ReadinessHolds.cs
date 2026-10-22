using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// The application's say in a portal link's readiness (<c>docs/portals.md</c> D10, condition 6): the worker that owns a container can hold it
    /// "not ready yet" while it does something the game needs done before anyone comes through, such as spawning the population of a level the
    /// link leads into. A link whose far window touches a held container stays <see cref="PortalState.Preparing"/> with
    /// <see cref="PortalWait.FarContentHeld"/>, whichever worker owns the portal: a far worker answers the link's preparation (D35) only once its
    /// container is released.
    /// <para>
    /// A hold is named by a reason, so independent systems can hold one container and each releases only its own. It holds a link that is
    /// preparing; a link that is already open stays open. It is local to the process that placed it: it lives on the worker that owns the
    /// container, and a container that moves to another worker is not held there (that worker's application holds it again if it should).
    /// </para>
    /// <para>
    /// <b>It never wedges a link.</b> Every hold expires <paramref name="timeoutSeconds"/> after it was placed (by default
    /// <see cref="DefaultTimeoutSeconds"/>) even if nobody releases it, with a warning: the application should release sooner, and a hold placed
    /// again restarts its clock.
    /// </para>
    /// </summary>
    public static class ReadinessHolds
    {
        /// <summary>How long a hold lasts when nobody releases it, seconds, unless <see cref="Hold"/> is given another time.</summary>
        public const float DefaultTimeoutSeconds = 30f;

        private struct Entry
        {
            public float Until;
            public float Timeout;
        }

        /// <summary>The holds of each container, by its <see cref="Container.ContainerId"/>, then by reason.</summary>
        private static readonly Dictionary<string, Dictionary<string, Entry>> Holds = new Dictionary<string, Dictionary<string, Entry>>();
        private static readonly List<string> Scratch = new List<string>();

        /// <summary>Tests: the clock holds are timed on, in place of <see cref="Time.unscaledTime"/>.</summary>
        internal static Func<float> ClockForTests;

        private static float Now => ClockForTests != null ? ClockForTests() : Time.unscaledTime;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        internal static void Reset()
        {
            Holds.Clear();
            ClockForTests = null;
        }

        /// <summary>
        /// Hold <paramref name="container"/> not ready for <paramref name="reason"/> until <see cref="Release"/> or <paramref name="timeoutSeconds"/>
        /// from now, whichever comes first. Holding it again for the same reason restarts the clock.
        /// </summary>
        public static void Hold(Container container, string reason, float timeoutSeconds = DefaultTimeoutSeconds)
        {
            if (container == null || string.IsNullOrEmpty(container.ContainerId)) return;
            if (!Holds.TryGetValue(container.ContainerId, out var byReason)) Holds[container.ContainerId] = byReason = new Dictionary<string, Entry>();
            float timeout = Mathf.Max(0f, timeoutSeconds);
            byReason[reason ?? ""] = new Entry { Until = Now + timeout, Timeout = timeout };
        }

        /// <summary>Release <paramref name="container"/>'s hold for <paramref name="reason"/>. True when there was one.</summary>
        public static bool Release(Container container, string reason) =>
            container != null && Release(container.ContainerId, reason);

        /// <summary>Release the hold for <paramref name="reason"/> of the container with id <paramref name="containerId"/>. True when there was one.</summary>
        public static bool Release(string containerId, string reason)
        {
            if (string.IsNullOrEmpty(containerId) || !Holds.TryGetValue(containerId, out var byReason)) return false;
            bool removed = byReason.Remove(reason ?? "");
            if (byReason.Count == 0) Holds.Remove(containerId);
            return removed;
        }

        /// <summary>Whether <paramref name="container"/> is held now. A hold whose time ran out is dropped here, with a warning.</summary>
        public static bool IsHeld(Container container) => container != null && IsHeld(container.ContainerId);

        /// <summary>Whether the container with id <paramref name="containerId"/> is held now (<see cref="IsHeld(Container)"/>).</summary>
        public static bool IsHeld(string containerId)
        {
            if (Holds.Count == 0 || string.IsNullOrEmpty(containerId) || !Holds.TryGetValue(containerId, out var byReason)) return false;
            float now = Now;
            Scratch.Clear();
            foreach (var pair in byReason) if (now >= pair.Value.Until) Scratch.Add(pair.Key);
            foreach (var reason in Scratch)
            {
                NebulaLog.Warn($"the readiness hold '{reason}' on {containerId} was not released within {byReason[reason].Timeout:0.#} s; it no longer holds links back");
                byReason.Remove(reason);
            }
            Scratch.Clear();
            if (byReason.Count > 0) return true;
            Holds.Remove(containerId);
            return false;
        }

        /// <summary>Whether the container with id <paramref name="containerId"/> has a hold for <paramref name="reason"/> that has not run out.</summary>
        public static bool IsHeldFor(string containerId, string reason) =>
            IsHeld(containerId) && Holds.TryGetValue(containerId, out var byReason) && byReason.ContainsKey(reason ?? "");
    }
}
