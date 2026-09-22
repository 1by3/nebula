using System;

namespace Nebula
{
    /// <summary>
    /// Where a game says what one entity costs to simulate, beyond the category it falls into.
    /// <para>
    /// <see cref="CostWeights"/> is per category: a player, a bot, a server-driven entity, anything else. That is
    /// enough to balance a shooter and not enough for a world with ambient chickens and raid bosses in it, which
    /// are both "server-driven" and are not the same machine. The weight here is a <b>multiplier</b> on the
    /// category weight, so 1 (the default everywhere) leaves the balance exactly as it was.
    /// </para>
    /// <para>
    /// Precedence, highest first (docs/cost-telemetry.md, D3):
    /// <list type="number">
    /// <item>a weight carried in from another worker with the entity's handover state, or one set explicitly with
    /// <see cref="NetworkIdentity.SetCostWeight"/>: the mesh never overrules what was decided about this
    /// individual entity, so a boss keeps its weight when it crosses a seam;</item>
    /// <item><see cref="EntityWeight"/>, this callback, when one is installed and it returns a weight of 0 or
    /// more;</item>
    /// <item><see cref="NetworkIdentity.CostWeight"/>, the value on the prefab or the scene object.</item>
    /// </list>
    /// The weight is evaluated when an entity is spawned and when <see cref="NetworkIdentity.SetCostWeight"/> is
    /// called - never per tick - so the callback may be as expensive as a prefab lookup and no more.
    /// </para>
    /// </summary>
    public static class NebulaCost
    {
        /// <summary>The largest weight an entity may carry. A weight is clamped to [0, this]; NaN reads as 1.</summary>
        public const float MaxWeight = 1024f;

        /// <summary>
        /// Optional: asked for the multiplier of every entity whose weight is being computed, in place of the
        /// value authored on it. Return a negative number to decline and let the authored
        /// <see cref="NetworkIdentity.CostWeight"/> stand. Installed once at start-up
        /// (<c>NebulaCost.EntityWeight = id =&gt; id.GetComponent&lt;Boss&gt;() != null ? 8f : -1f;</c>); it runs on
        /// the worker's main thread, inside the spawn, and its exceptions are logged and treated as declining.
        /// </summary>
        public static Func<NetworkIdentity, float> EntityWeight;

        /// <summary>Forget the callback. Tests call this in teardown; a game has no reason to.</summary>
        public static void Reset() => EntityWeight = null;

        /// <summary>
        /// The weight <paramref name="identity"/> should carry from now on: the callback's answer when it gives
        /// one, else the authored value, clamped to [0, <see cref="MaxWeight"/>].
        /// </summary>
        public static float Evaluate(NetworkIdentity identity)
        {
            if (identity == null) return 1f;
            float authored = Clamp(identity.CostWeight);
            var callback = EntityWeight;
            if (callback == null) return authored;
            float asked;
            try { asked = callback(identity); }
            catch (Exception e)
            {
                NebulaLog.Error($"NebulaCost.EntityWeight threw for {identity}; keeping its authored weight {authored}: {e}");
                return authored;
            }
            if (float.IsNaN(asked) || asked < 0f) return authored; // declined
            return Clamp(asked);
        }

        /// <summary>A weight as the mesh will carry it: NaN reads as 1, and the range is [0, <see cref="MaxWeight"/>].</summary>
        public static float Clamp(float weight)
        {
            if (float.IsNaN(weight)) return 1f;
            if (weight < 0f) return 0f;
            return weight > MaxWeight ? MaxWeight : weight;
        }
    }
}
