using Nebula;
using UnityEngine;

namespace NebulaSamples
{
    /// <summary>
    /// A reference lag-compensated hitscan validator, in the smallest form that is still honest. It is a
    /// <b>sample</b>, not part of Nebula: deciding what a shot may claim and applying damage are game decisions
    /// (see <c>docs/state-history.md</c> §6). Copy it into your game and change all of it.
    /// <para>
    /// The shape: the shooter's client sends the tick it was rendering with its shot. The worker that has authority
    /// over the shooter validates that tick against the window it can answer for, then asks Nebula to cast the ray
    /// as the world stood at that tick: <see cref="PortalQueries.Raycast"/> poses every entity the worker holds at
    /// <see cref="NetworkIdentity.StateAt"/> (<see cref="HistoryRewind"/>), runs the cast in the shooter's scope, and
    /// carries it on through any open portal in its way, into the room behind a door, for the length that is left. The
    /// sample asks for no scope filter of its own: which scope a victim stands in, and whether it crossed a portal since
    /// the claimed tick, is the query's to know.
    /// </para>
    /// </summary>
    public sealed class LagCompensatedHitscan : NetworkBehaviour
    {
        [Tooltip("Metres the shot may reach, summed over every scope it goes through.")]
        public float Range = 120f;
        [Tooltip("How far into the past a claimed tick may point, on top of what the window can answer.")]
        public int MaxClaimTicks = 24;
        [Tooltip("Damage a hit does.")]
        public int Damage = 10;

        /// <summary>
        /// The worker this behaviour runs on. A real game hands it over once from
        /// <see cref="NebulaGameMode.OnWorkerStarted"/> and keeps it; the sample finds it so it stands alone.
        /// </summary>
        public NebulaWorker Worker;

        // One options object, kept: a query takes it by reference and allocates nothing itself. The shooter's own scope is rewound
        // too, so the whole shot is against the same moment; the far leg of a shot through a portal always is.
        private readonly PortalQueryOptions _options = new PortalQueryOptions { RewindNear = true };

        private void Awake()
        {
            if (Worker == null) Worker = FindAnyObjectByType<NebulaWorker>();
        }

        /// <summary>
        /// Called by the shooter's client. <paramref name="aimTick"/> is the tick the client was rendering, which is
        /// <c>NetworkTime.RenderTick</c>.
        /// </summary>
        public void Fire(Vector3 origin, Vector3 direction, uint aimTick) =>
            ServerRpc(RpcFire, origin, direction, aimTick);

        [ServerRpc]
        private void RpcFire(Vector3 origin, Vector3 direction, uint aimTick)
        {
            // Your game mode already has the worker (OnWorkerStarted); this sample keeps it simple.
            var worker = Worker;
            if (worker == null) return;

            // 1. Is the claim one we could possibly check? Nebula never decides this; a window that cannot answer
            //    and a tick further back than the game allows are different refusals, and both are the game's.
            uint now = worker.CurrentTick;
            if (aimTick > now || now - aimTick > (uint)MaxClaimTicks) return;

            // 2. Cast the ray at that tick, from the scope the shooter is in. The asker is the shooter: its own colliders are
            //    skipped, and its party (by default its owner) chooses which of a door's links the shot goes through.
            _options.Tick = aimTick;
            if (!worker.PortalQueries.Raycast(PortalAsker.Of(Identity), Identity.InstanceId, origin, direction, Range, out var hit, _options)) return;

            // 3. What came back is not always a victim. A closed door is a hit on its plane, and a shot into a room that
            //    another worker holds ends at the edge of what this one knows (a game may forward it, or call it a miss).
            if (hit.IsAperture || hit.IsTruncated || hit.Entity == null) return;

            // 4. Apply the result on whoever has authority over the victim. Locally when that is us, through the
            //    cross-worker call contract when the victim is a ghost here (docs/cross-worker-calls.md): the call
            //    is fenced by the victim's epoch and applied at most once, even if it hands over in flight.
            var health = hit.Entity.GetComponent<SampleHealth>();
            if (health != null) health.ApplyDamage(Damage);
        }
    }

    /// <summary>The victim side of the sample: a health pool with a stance in the recorded history.</summary>
    public sealed class SampleHealth : NetworkBehaviour
    {
        public NetworkVariable<int> Health = new NetworkVariable<int>(100);

        /// <summary>
        /// Marked, so a validator can ask what stance the victim was in at the claimed tick - a crouching player is
        /// a smaller target. Only marked variables are snapshotted; see <c>docs/state-history.md</c> §5.
        /// </summary>
        [SyncHistory] public NetworkVariable<byte> Stance = new NetworkVariable<byte>();

        /// <summary>
        /// The stance this victim had at <paramref name="tick"/>, read from the history rather than the present. False when the
        /// history cannot answer for the tick.
        /// </summary>
        public bool TryGetStanceAt(uint tick, out byte stance)
        {
            stance = 0;
            var state = Identity.StateAt(tick);
            return state.Available && state.TryGetValue(Stance, out stance);
        }

        /// <summary>
        /// Runs on whoever has authority over this entity. Called in-process when the shooter's worker is also the
        /// victim's, and forwarded over the worker link (fenced, at most once) when it is not.
        /// </summary>
        public void ApplyDamage(int amount) => AuthorityRpc(RpcApplyDamage, amount);

        [AuthorityRpc]
        private void RpcApplyDamage(int amount)
        {
            if (!HasAuthority) return;
            Health.Value = Mathf.Max(0, Health.Value - amount);
        }
    }
}
