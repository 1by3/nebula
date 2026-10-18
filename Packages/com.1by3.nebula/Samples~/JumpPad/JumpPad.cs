using System.Collections.Generic;
using Nebula;
using UnityEngine;

namespace NebulaSamples
{
    /// <summary>
    /// A pad on the ground that throws whatever stands on it into the air. It is a <b>sample</b>: put it on an entity
    /// inside the same container as the pawns. Each tick, on the worker that has authority over the pad, it looks at
    /// the predicted entities in its container and posts a <see cref="LaunchEvent"/> to the ones standing within
    /// <see cref="Radius"/> of it that it has not launched in the last <see cref="CooldownTicks"/>.
    /// <para>
    /// <c>PostServerEvent</c> works on the pawn's authority. The event applies on the next tick the pawn simulates,
    /// whether the pad or the pawn is ticked first in a worker tick. When the pawn goes first, the owner's report
    /// for that tick already carries the launch for the next one, and its replay applies it with no correction. When
    /// the pad goes first, the launch applies in the same tick it is posted and the owner is corrected once, a
    /// small step that correction smoothing hides.
    /// </para>
    /// </summary>
    public sealed class JumpPad : NetworkBehaviour
    {
        [Tooltip("Horizontal distance from the pad within which a pawn is launched, in meters.")]
        public float Radius = 1.5f;
        [Tooltip("How high above the pad a pawn still counts as standing on it, in meters.")]
        public float Height = 0.5f;
        [Tooltip("The velocity a launched pawn takes, in the pad's orientation: meters per second.")]
        public Vector3 LaunchVelocity = new Vector3(0f, 12f, 0f);
        [Tooltip("Ticks before the same pawn can be launched again.")]
        public int CooldownTicks = 30;

        private readonly Dictionary<ulong, uint> _lastLaunch = new Dictionary<ulong, uint>();

        public override void NetworkTick(uint tick, float deltaTime)
        {
            if (!HasAuthority || Identity.Container == null) return;
            var pad = transform.position;
            foreach (var other in Identity.Container.Entities)
            {
                if (other == null || other == Identity || other.Predicted == null || !other.HasAuthority) continue;
                var offset = other.transform.position - pad;
                if (offset.y < -0.01f || offset.y > Height || new Vector2(offset.x, offset.z).sqrMagnitude > Radius * Radius) continue;
                if (_lastLaunch.TryGetValue(other.NetId, out var last) && tick - last < (uint)CooldownTicks) continue;
                if (other.Predicted.PostServerEvent(new LaunchEvent { Velocity = transform.rotation * LaunchVelocity })) _lastLaunch[other.NetId] = tick;
            }
        }
    }
}
