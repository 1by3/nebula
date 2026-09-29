using Nebula;
using UnityEngine;

namespace NebulaSamples
{
    /// <summary>What a driver sends each tick: throttle and steering, both from -1 to 1.</summary>
    public struct VehicleInput : INetworkInput
    {
        public float Throttle;
        public float Steer;

        public void Serialize(NetworkWriter writer)
        {
            writer.WriteFloat(Throttle);
            writer.WriteFloat(Steer);
        }

        public void Deserialize(NetworkReader reader)
        {
            Throttle = reader.ReadFloat();
            Steer = reader.ReadFloat();
        }
    }

    /// <summary>
    /// A kinematic car the driver's client predicts. It is a <b>sample</b>: a game writes its own vehicle model. What
    /// matters is its shape:
    /// <list type="bullet">
    /// <item>a <see cref="PredictedBehaviour{TInput}"/> on a <see cref="NetworkIdentity"/> no client owns, spawned with
    /// <see cref="NebulaWorker.SpawnServerDriven"/>;</item>
    /// <item><see cref="Simulate"/> is a deterministic function of the state and the input, with no physics engine in
    /// it: the driver's client runs it again for this one vehicle during a replay;</item>
    /// <item>everything <see cref="Simulate"/> carries from tick to tick (the speed) is in <see cref="WriteState"/>;</item>
    /// <item><see cref="PredictedBehaviour{TInput}.GatherServerInput"/> says what the car does with nobody at the wheel.</item>
    /// </list>
    /// The local player's input code sets <see cref="Controls"/> every frame on the client; it is read only while this
    /// client drives the car.
    /// </summary>
    public sealed class Vehicle : PredictedBehaviour<VehicleInput>
    {
        [Tooltip("Metres per second squared at full throttle.")]
        public float Acceleration = 8f;
        [Tooltip("Speed lost per second, as a fraction of the speed.")]
        public float Drag = 0.4f;
        [Tooltip("Degrees per second at full lock, reached from GripSpeed upwards.")]
        public float TurnRate = 60f;
        [Tooltip("Metres per second below which the car turns less, down to not at all when stopped.")]
        public float GripSpeed = 4f;

        /// <summary>Client: the local player's controls, set by the game's input code.</summary>
        public VehicleInput Controls;

        /// <summary>Metres per second along the car's forward axis. Part of the reconciled state.</summary>
        public float Speed { get; private set; }

        protected override VehicleInput GatherInput() => Controls;

        // Nobody at the wheel, or the driver's input has stopped: brake to a stop.
        protected override VehicleInput GatherServerInput(uint tick) => new VehicleInput { Throttle = Speed > 0.1f ? -1f : 0f };

        protected override void Simulate(uint tick, in VehicleInput input, float deltaTime)
        {
            Speed += (Mathf.Clamp(input.Throttle, -1f, 1f) * Acceleration - Drag * Speed) * deltaTime;
            float grip = Mathf.Clamp01(Mathf.Abs(Speed) / GripSpeed);
            float yaw = Mathf.Clamp(input.Steer, -1f, 1f) * TurnRate * grip * deltaTime;
            // In the space of the container the car is in: a car in a ship's hold drives relative to the hold.
            var rotation = Identity.LocalRotation * Quaternion.Euler(0f, yaw, 0f);
            var velocity = rotation * Vector3.forward * Speed;
            Identity.SetLocalPose(Identity.Container, Identity.LocalPosition + velocity * deltaTime, rotation);
            Identity.Motion.Velocity = Identity.Container != null ? Identity.Container.Rotation * velocity : velocity;
        }

        protected override void WriteState(NetworkWriter writer)
        {
            base.WriteState(writer);
            writer.WriteFloat(Speed);
        }

        protected override void ReadState(NetworkReader reader)
        {
            base.ReadState(reader);
            Speed = reader.ReadFloat();
        }
    }
}
