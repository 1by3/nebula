using Nebula;
using UnityEngine;

namespace NebulaSamples
{
    /// <summary>What the player asks for each tick: a walking direction in the ground plane, in -1..1 on each axis.</summary>
    public struct WalkerInput : INetworkInput
    {
        public Vector2 Move;

        public void Serialize(NetworkWriter writer) { writer.WriteVector2(Move); }
        public void Deserialize(NetworkReader reader) { Move = reader.ReadVector2(); }
    }

    /// <summary>
    /// A pawn that walks on a flat ground at <see cref="GroundHeight"/>, falls under gravity, and can be launched by a
    /// <see cref="JumpPad"/>. It is a <b>sample</b>: the movement model is deliberately small. What it shows is how a
    /// predicted behavior takes something only the worker decides.
    /// <list type="bullet">
    /// <item>The launch arrives as a <see cref="LaunchEvent"/>, read inside <c>Simulate</c> with
    /// <c>TryGetServerEvent</c> and applied to state: the vertical speed and the push, which <c>WriteState</c> and
    /// <c>ReadState</c> carry. It is not a change to the transform from outside <c>Simulate</c>.</item>
    /// <item>So the worker applies it on tick T, and the owner's replay applies it on the same tick T. The owner is
    /// not corrected afterwards. The late start (the owner hears of the launch about half a round trip after it
    /// happened) is hidden by correction smoothing.</item>
    /// </list>
    /// </summary>
    public sealed class JumpPadWalker : PredictedBehaviour<WalkerInput>
    {
        [Tooltip("Walking speed in meters per second.")]
        public float Speed = 5f;
        [Tooltip("Downward acceleration in meters per second squared.")]
        public float Gravity = 20f;
        [Tooltip("Height of the flat ground in the container's space.")]
        public float GroundHeight;

        /// <summary>The owning client's controls; set it every frame from your input code.</summary>
        public Vector2 Controls;

        private float _verticalSpeed;
        private Vector2 _push;

        public float VerticalSpeed => _verticalSpeed;

        protected override WalkerInput GatherInput() => new WalkerInput { Move = Vector2.ClampMagnitude(Controls, 1f) };

        protected override void Simulate(uint tick, in WalkerInput input, float deltaTime)
        {
            if (TryGetServerEvent<LaunchEvent>(out var launch))
            {
                _verticalSpeed = launch.Velocity.y;
                _push = new Vector2(launch.Velocity.x, launch.Velocity.z);
            }
            var position = Identity.LocalPosition;
            _verticalSpeed -= Gravity * deltaTime;
            var velocity = new Vector3(input.Move.x * Speed + _push.x, _verticalSpeed, input.Move.y * Speed + _push.y);
            position += velocity * deltaTime;
            if (position.y <= GroundHeight && _verticalSpeed <= 0f)
            {
                // Landed: the push ends with the flight.
                position.y = GroundHeight;
                _verticalSpeed = 0f;
                _push = Vector2.zero;
            }
            Identity.SetLocalPose(Identity.Container, position, Identity.LocalRotation);
            Identity.Motion.Velocity = Identity.Container != null ? Identity.Container.Rotation * velocity : velocity;
        }

        // Everything Simulate carries from one tick to the next, so the owner's replay and the next worker start from it.
        protected override void WriteState(NetworkWriter writer)
        {
            base.WriteState(writer);
            writer.WriteFloat(_verticalSpeed);
            writer.WriteVector2(_push);
        }

        protected override void ReadState(NetworkReader reader)
        {
            base.ReadState(reader);
            _verticalSpeed = reader.ReadFloat();
            _push = reader.ReadVector2();
        }
    }
}
