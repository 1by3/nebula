using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// Lets a physics prop inside a moving physics frame feel the frame's motion: crates slide aft when the ship
    /// accelerates, outwards when it turns. A physics frame (<see cref="Container.OwnPhysicsFrame"/>) is a scene in
    /// which its container stands still, so without this component nothing inside feels the carrier move at all.
    /// <para>
    /// On the authority, every <see cref="NetworkBehaviour.NetworkTick"/>, before the frame's scene steps, the body
    /// gets the fictitious acceleration of its frame, <c>-(A + α × r + ω × (ω × r) + 2 ω × v)</c>, as
    /// <see cref="ForceMode.Acceleration"/>: <c>A</c> and <c>ω</c> are the frame's acceleration and angular velocity in
    /// its own axes (<see cref="PhysicsFrameState.LocalAcceleration"/>, <see cref="PhysicsFrameState.LocalAngularVelocity"/>),
    /// <c>α</c> the rate of change of <c>ω</c>, <c>r</c> and <c>v</c> the body's frame-local position and velocity.
    /// Each tick's value is multiplied by <see cref="Scale"/>, clamped to <see cref="MaxAcceleration"/>, then smoothed
    /// over <see cref="SmoothingTicks"/>; below <see cref="MinAcceleration"/> nothing is applied, so a crate in a
    /// cruising ship can sleep. The frame's state is sampled once per tick after the carriers moved, so the force
    /// follows the carrier one tick late.
    /// </para>
    /// <para>
    /// Outside a physics frame, on a ghost, on a client and while the body is kinematic it does nothing. The frame's
    /// own gravity is not rotated: "down" stays the container's floor. Add it next to a <see cref="NetworkRigidbody"/>.
    /// See <c>docs/frame-bodies.md</c> D4.
    /// </para>
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    [DisallowMultipleComponent]
    public sealed class FrameInertia : NetworkBehaviour
    {
        [Tooltip("How much of the frame's motion the body feels: 0 none (the default), 1 all of it. Games usually set it per ship, like an inertial damper.")]
        public float Scale = 0f;
        [Tooltip("The largest fictitious acceleration applied in one tick, in m/s² after Scale. Keeps a snap of the carrier (a teleport, a landing) from launching the cargo.")]
        public float MaxAcceleration = 20f;
        [Tooltip("Ticks the acceleration is smoothed over (an exponential moving average). 1 applies each tick's value as it is.")]
        public int SmoothingTicks = 6;
        [Tooltip("Below this acceleration, in m/s², nothing is applied, so a body at rest in a cruising ship can sleep.")]
        public float MinAcceleration = 0.05f;

        private Rigidbody _body;
        private Vector3 _smoothed;
        private Vector3 _lastOmega;
        private uint _lastStateTick;
        private PhysicsFrame _lastFrame;

        /// <summary>The acceleration applied on the last tick, in frame-local m/s² (zero when nothing was applied).</summary>
        public Vector3 Applied { get; private set; }

        private Rigidbody Body => _body != null ? _body : (_body = GetComponent<Rigidbody>());

        public override void NetworkTick(uint tick, float deltaTime)
        {
            var space = Identity != null ? Identity.Space : null;
            var frame = space != null ? space.Frame : null;
            var body = Body;
            if (frame == null || body == null || body.isKinematic || Scale == 0f || deltaTime <= 0f)
            {
                Forget();
                return;
            }
            var state = frame.State;
            if (!state.HasRates)
            {
                Forget();
                return;
            }
            var omega = state.LocalAngularVelocity;
            var alpha = Vector3.zero;
            if (_lastFrame == frame && state.Tick == _lastStateTick + 1) alpha = (omega - _lastOmega) / deltaTime;
            _lastFrame = frame;
            _lastOmega = omega;
            _lastStateTick = state.Tick;

            // A frame's root never rotates in simulation space, so the body's pose and velocity read frame-local.
            var r = frame.SimulationToLocal(body.worldCenterOfMass);
            var v = body.linearVelocity;
            var sample = -(state.LocalAcceleration + Vector3.Cross(alpha, r) + Vector3.Cross(omega, Vector3.Cross(omega, r)) + 2f * Vector3.Cross(omega, v));
            sample = Vector3.ClampMagnitude(sample * Scale, Mathf.Max(0f, MaxAcceleration));
            float k = SmoothingTicks > 1 ? 2f / (SmoothingTicks + 1) : 1f;
            _smoothed += (sample - _smoothed) * k;
            if (_smoothed.magnitude < MinAcceleration)
            {
                Applied = Vector3.zero;
                return;
            }
            body.AddForce(_smoothed, ForceMode.Acceleration);
            Applied = _smoothed;
        }

        public override void OnLostAuthority() => Forget();

        public override void OnContainerChanged(Container previous, Container current)
        {
            // Into another space: what was smoothed belonged to the frame it left.
            if ((previous != null ? previous.InnerSpace : null) != (current != null ? current.InnerSpace : null)) Forget();
        }

        private void Forget()
        {
            _smoothed = Vector3.zero;
            _lastFrame = null;
            Applied = Vector3.zero;
        }
    }
}
