using System;
using System.Globalization;

namespace Nebula
{
    /// <summary>
    /// What the game knows about a container that the orchestrator cannot measure: how expensive it really is, what
    /// it belongs with, how much a worker boundary beside it hurts, and whether it wants a machine to itself. The
    /// built-in <see cref="CostBalancedAssignmentPolicy"/> reads these; a game with needs beyond them writes its own
    /// <see cref="IAssignmentPolicy"/>.
    /// <para>
    /// A hint is set at bake time on <see cref="WorldContainerManifest.Entry"/> (the World window edits it per cell)
    /// and at runtime through <see cref="IControlPlane.SetContainerHint"/>, which stores it beside the lease so an
    /// orchestrator restart sees it again. The runtime value wins over the baked one.
    /// </para>
    /// </summary>
    [Serializable]
    public struct ContainerHint : IEquatable<ContainerHint>
    {
        /// <summary>
        /// Scales what this container is treated as costing: 1 is "as measured", 2 makes the planner give it twice
        /// the room. The manual correction for a container whose occupancy is a poor proxy for its tick time - a
        /// handful of entities running expensive AI, or a hundred static props that cost almost nothing.
        /// </summary>
        public float CostMultiplier;
        /// <summary>
        /// Containers sharing a non-empty group are dealt as one item, so an instanced dungeon's rooms land on the
        /// same worker and nobody crosses a machine boundary walking through a door. "" means no group.
        /// </summary>
        public string AffinityGroup;
        /// <summary>
        /// 0..1: how much a worker boundary next to this container hurts. Where two neighbours on the curve both
        /// carry it, the planner charges itself for cutting between them and prefers to cut somewhere else. Raise it
        /// on whatever is currently hot and contested (the cells inside a battle royale's circle).
        /// </summary>
        public float SeamCost;
        /// <summary>
        /// This container wants a worker to itself: the planner reserves one for it before dealing everything else.
        /// A city hub, a raid boss room. A worker holding one is never chosen for retirement.
        /// </summary>
        public bool Dedicated;

        /// <summary>Nothing said: cost as measured, no group, no seam charge, shared like anything else.</summary>
        public static ContainerHint Default => new ContainerHint { CostMultiplier = 1f, AffinityGroup = "", SeamCost = 0f, Dedicated = false };

        /// <summary>This hint says nothing the planner would act on, so it need not be stored or shown.</summary>
        public bool IsDefault => !Dedicated && SeamCost <= 0f && string.IsNullOrEmpty(AffinityGroup) && (CostMultiplier == 1f || CostMultiplier <= 0f);

        /// <summary>
        /// The multiplier as the planner uses it: a hint that was never initialised (a zeroed struct out of a list)
        /// means "as measured", not "free", so anything at or below zero reads as 1.
        /// </summary>
        public float EffectiveMultiplier => CostMultiplier > 0f ? CostMultiplier : 1f;

        /// <summary>The seam charge as the planner uses it, clamped into 0..1.</summary>
        public float EffectiveSeamCost => SeamCost <= 0f ? 0f : SeamCost >= 1f ? 1f : SeamCost;

        /// <summary>The group as the planner uses it ("" when unset).</summary>
        public string Group => string.IsNullOrEmpty(AffinityGroup) ? "" : AffinityGroup;

        public bool Equals(ContainerHint other)
        {
            return EffectiveMultiplier == other.EffectiveMultiplier && EffectiveSeamCost == other.EffectiveSeamCost
                && Dedicated == other.Dedicated && string.Equals(Group, other.Group, StringComparison.Ordinal);
        }

        public override bool Equals(object obj) => obj is ContainerHint h && Equals(h);

        public override int GetHashCode()
        {
            int hash = EffectiveMultiplier.GetHashCode();
            hash = hash * 31 + EffectiveSeamCost.GetHashCode();
            hash = hash * 31 + Dedicated.GetHashCode();
            hash = hash * 31 + Group.GetHashCode();
            return hash;
        }

        public static bool operator ==(ContainerHint a, ContainerHint b) => a.Equals(b);
        public static bool operator !=(ContainerHint a, ContainerHint b) => !a.Equals(b);

        /// <summary>
        /// The compact string form used on a command line and in the CLI: <c>x2,group=keep,seam=0.5,dedicated</c>.
        /// Only the parts that differ from the default are written, so a default hint is "".
        /// </summary>
        public override string ToString()
        {
            if (IsDefault) return "";
            var parts = new System.Collections.Generic.List<string>(4);
            if (EffectiveMultiplier != 1f) parts.Add("x" + EffectiveMultiplier.ToString("0.###", CultureInfo.InvariantCulture));
            if (Group != "") parts.Add("group=" + Group);
            if (EffectiveSeamCost > 0f) parts.Add("seam=" + EffectiveSeamCost.ToString("0.###", CultureInfo.InvariantCulture));
            if (Dedicated) parts.Add("dedicated");
            return string.Join(",", parts);
        }

        /// <summary>
        /// Read the form <see cref="ToString"/> writes. Unknown parts are ignored; "" is the default hint.
        /// </summary>
        public static ContainerHint Parse(string text)
        {
            var hint = Default;
            if (string.IsNullOrEmpty(text)) return hint;
            foreach (var raw in text.Split(','))
            {
                string part = raw.Trim();
                if (part.Length == 0) continue;
                if (part == "dedicated") { hint.Dedicated = true; continue; }
                if (part[0] == 'x' && float.TryParse(part.Substring(1), NumberStyles.Float, CultureInfo.InvariantCulture, out float m)) { hint.CostMultiplier = m; continue; }
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string key = part.Substring(0, eq), value = part.Substring(eq + 1);
                if (key == "group") hint.AffinityGroup = value;
                else if (key == "seam" && float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float s)) hint.SeamCost = s;
                else if (key == "cost" && float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float c)) hint.CostMultiplier = c;
                else if (key == "dedicated") hint.Dedicated = value == "true" || value == "1";
            }
            return hint;
        }
    }
}
