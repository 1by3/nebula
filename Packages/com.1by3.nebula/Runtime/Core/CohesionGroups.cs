using System.Collections.Generic;

namespace Nebula
{
    /// <summary>
    /// Which entities belong to which <see cref="NetworkIdentity.CohesionGroup"/>, in this process. A cohesion group
    /// is the game's promise that a set of entities must be simulated by one worker and move between workers as a
    /// unit: a ragdoll's bodies, a turret and its mount, the two sides of an interaction that is mid-flight. Nebula
    /// never guesses a group (see <c>docs/cohesion-hints.md</c>, D1); the game joins and leaves one.
    /// <para>
    /// The table is process-wide, like <see cref="SceneEntities"/>, and holds every initialised identity that
    /// carries a non-zero group - authoritative copies, ghosts and client replicas alike. Callers that mean "the
    /// members I own" filter by <see cref="NetworkIdentity.HasAuthority"/>; <see cref="NebulaWorker"/> does, and a
    /// member it finds that it does not own is a group it cannot move as a unit, which it reports rather than
    /// splitting silently (D4).
    /// </para>
    /// <para>Group 0 is "no group" and is never stored, so a game that uses no cohesion group pays an empty table.</para>
    /// </summary>
    public static class CohesionGroups
    {
        private static readonly Dictionary<uint, List<NetworkIdentity>> ByGroup = new Dictionary<uint, List<NetworkIdentity>>();
        private static readonly List<NetworkIdentity> Empty = new List<NetworkIdentity>();

        /// <summary>How many distinct groups have a member in this process.</summary>
        public static int GroupCount => ByGroup.Count;

        /// <summary>Every group with a member here, in no particular order.</summary>
        public static IEnumerable<uint> Groups => ByGroup.Keys;

        /// <summary>
        /// The identities in <paramref name="group"/>, in the order they joined. Empty for group 0 and for a group
        /// nothing here belongs to. The list is the live one: do not hold it across a join or a leave.
        /// </summary>
        public static IReadOnlyList<NetworkIdentity> Members(uint group)
        {
            return group != 0 && ByGroup.TryGetValue(group, out var list) ? list : Empty;
        }

        /// <summary>How many identities are in <paramref name="group"/> here (0 for "no group").</summary>
        public static int MemberCount(uint group) => Members(group).Count;

        /// <summary>
        /// Whether two entities are in the same non-zero cohesion group, so one worker always simulates both.
        /// <see cref="PhysicsIslands.SameIsland(NetworkIdentity, NetworkIdentity, System.Func{NetworkIdentity, Container})"/>
        /// asks this before it reports a joint across containers.
        /// </summary>
        public static bool Same(NetworkIdentity a, NetworkIdentity b)
        {
            return a != null && b != null && a.CohesionGroup != 0 && a.CohesionGroup == b.CohesionGroup;
        }

        internal static void Register(NetworkIdentity identity)
        {
            if (identity == null || identity.CohesionGroup == 0) return;
            if (!ByGroup.TryGetValue(identity.CohesionGroup, out var list)) ByGroup[identity.CohesionGroup] = list = new List<NetworkIdentity>(4);
            if (!list.Contains(identity)) list.Add(identity);
        }

        internal static void Unregister(NetworkIdentity identity, uint group)
        {
            if (identity == null || group == 0 || !ByGroup.TryGetValue(group, out var list)) return;
            list.Remove(identity);
            if (list.Count == 0) ByGroup.Remove(group);
        }

        /// <summary>Forget everything (a session ending, a test fixture tearing down).</summary>
        internal static void Clear() => ByGroup.Clear();
    }
}
