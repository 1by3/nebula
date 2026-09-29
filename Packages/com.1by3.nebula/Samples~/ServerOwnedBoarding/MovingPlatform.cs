using Nebula;
using UnityEngine;

namespace NebulaSamples
{
    /// <summary>
    /// A platform that carries whatever stands on it: a <see cref="Container"/> on the entity's root with
    /// <see cref="Container.OwnPhysicsFrame"/> on, moved by its authority each tick. It is a <b>sample</b>: a game moves
    /// its carriers however it likes (a flight model, a path, a physics body). What matters is the prefab's shape:
    /// <list type="bullet">
    /// <item>a <see cref="NetworkIdentity"/>, a <see cref="NetworkTransform"/> and a <see cref="Container"/> on the same
    /// root object, so the entity carries the box;</item>
    /// <item><see cref="Container.OwnPhysicsFrame"/> on, so what is aboard simulates in a scene where the platform
    /// stands still, whatever the platform does;</item>
    /// <item>a box that reaches from the deck to above a standing character's origin, so a character entering it is
    /// inside by more than <see cref="NebulaConfig.HandoverHysteresis"/>;</item>
    /// <item>colliders for the deck under the root, which the frame copies inside.</item>
    /// </list>
    /// </summary>
    [RequireComponent(typeof(Container))]
    public sealed class MovingPlatform : NetworkBehaviour
    {
        [Tooltip("Velocity in the space the platform is in, metres per second.")]
        public Vector3 Velocity;
        [Tooltip("Turn rate about the platform's own up, degrees per second.")]
        public float TurnRate;

        public override void NetworkTick(uint tick, float deltaTime)
        {
            // The worker ticks a carrier before what it carries, so everything aboard sees this tick's pose.
            if (!HasAuthority) return;
            transform.position += Velocity * deltaTime;
            if (TurnRate != 0f) transform.rotation = Quaternion.AngleAxis(TurnRate * deltaTime, transform.up) * transform.rotation;
            Identity.Motion.Velocity = Velocity;
        }

        private void Reset()
        {
            var box = GetComponent<Container>();
            if (box != null) box.OwnPhysicsFrame = true;
        }
    }
}
