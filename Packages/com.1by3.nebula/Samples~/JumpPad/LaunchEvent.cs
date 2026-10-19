using Nebula;
using UnityEngine;

namespace NebulaSamples
{
    /// <summary>
    /// What a <see cref="JumpPad"/> tells the pawn it launches: the velocity to take, in meters per second. It is a
    /// server event (<see cref="PredictedBehaviourBase.PostServerEvent{TEvent}"/>): the worker decides it, and
    /// <see cref="JumpPadWalker"/> applies it inside <c>Simulate</c>, on a tick that the worker and the owner's replay agree on.
    /// </summary>
    public struct LaunchEvent : INetworkSerializable
    {
        public Vector3 Velocity;

        public void Serialize(NetworkWriter writer) { writer.WriteVector3(Velocity); }
        public void Deserialize(NetworkReader reader) { Velocity = reader.ReadVector3(); }
    }
}
