using System;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// Tick-indexed pose buffer for copies of an entity this process does not simulate: ghosts on a worker and
    /// remote entities on a client. The consumer decides at which (fractional) tick to sample.
    /// <para>
    /// Samples are kept in the space they arrived in: container-local, tagged with the container. A pose inside a
    /// moving container (a passenger on a ship) is therefore interpolated relative to the ship, and the sampled
    /// local pose is applied under the ship's transform wherever the ship is <i>now</i>, so passengers never lag a
    /// frame behind the hull. A sample with no container is a world pose (an entity outside every container).
    /// </para>
    /// </summary>
    public sealed class RemoteInterpolator : MonoBehaviour
    {
        private struct PoseSample
        {
            public uint Tick;
            public Container Container;
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 Velocity;
            /// <summary>Container-local, radians per second.</summary>
            public Vector3 AngularVelocity;
            public bool HasAngularVelocity, HasVelocity;
            public Vector3 Scale;
            public bool Valid;
        }

        private const int Capacity = 64;
        private readonly PoseSample[] _ring = new PoseSample[Capacity];
        private uint _latestTick;
        private uint _previousTick;
        private bool _any;

        /// <summary>Process-wide sample statistics, reset by whoever reports them (NebulaClient's telemetry line).</summary>
        public static long Samples, Starved;
        public static double DepthSum, MaxOvershoot;
        public static void ResetStats() { Samples = Starved = 0; DepthSum = MaxOvershoot = 0; }

        public uint LatestTick => _latestTick;
        public bool HasSamples => _any;
        public Vector3 LatestScale { get; private set; } = Vector3.one;
        internal bool SlerpPosition;
        /// <summary>
        /// Interpolate along a cubic Hermite curve through both samples' velocities, and turn through their angular velocities,
        /// when the velocities explain the move (<see cref="NetworkTransform.VelocityAwareInterpolation"/>). Off: straight lines.
        /// </summary>
        internal bool VelocityAware;
        /// <summary>Angular velocity of the newest sample, in its container's local space, radians per second (zero when the stream does not carry one).</summary>
        public Vector3 LatestAngularVelocity { get; private set; }
        /// <summary>Whether the newest sample carried an angular velocity (protocol 30, <see cref="NetworkTransform.SyncAngularVelocity"/>).</summary>
        public bool LatestHasAngularVelocity { get; private set; }
        /// <summary>World-space velocity of the newest sample (what the authority reported).</summary>
        public Vector3 LatestVelocity { get; private set; }
        /// <summary>Container of the newest sample (null: world space).</summary>
        public Container LatestContainer { get; private set; }
        /// <summary>Position of the newest sample, in <see cref="LatestContainer"/>'s space.</summary>
        public Vector3 LatestLocalPosition { get; private set; }
        public Quaternion LatestLocalRotation { get; private set; } = Quaternion.identity;
        /// <summary>World position of the newest sample, through its container's current frame.</summary>
        public Vector3 LatestPosition => LatestContainer != null ? LatestContainer.ToWorld(LatestLocalPosition) : LatestLocalPosition;
        public Quaternion LatestRotation => LatestContainer != null ? LatestContainer.Rotation * LatestLocalRotation : LatestLocalRotation;

        /// <summary>Record a pose expressed in <paramref name="container"/>'s local space (world space when null). <paramref name="velocity"/> is world-space.</summary>
        public void Push(uint tick, Container container, Vector3 localPosition, Quaternion localRotation, Vector3 velocity, Vector3? scale = null)
            => Push(tick, container, localPosition, localRotation, velocity, Vector3.zero, false, true, scale);

        /// <summary>
        /// Record a pose with its angular velocity (<paramref name="angularVelocity"/>, container-local, radians per second, meaningful when
        /// <paramref name="hasAngularVelocity"/>). <paramref name="hasVelocity"/> false says the stream carries no velocity, so the
        /// sample's zero is not a stop.
        /// </summary>
        public void Push(uint tick, Container container, Vector3 localPosition, Quaternion localRotation, Vector3 velocity,
            Vector3 angularVelocity, bool hasAngularVelocity, bool hasVelocity, Vector3? scale = null)
        {
            if (_any && tick + Capacity <= _latestTick) return; // too old to matter
            if (!_any || tick > _latestTick)
            {
                _previousTick = _any ? _latestTick : tick;
                _latestTick = tick;
                LatestContainer = container;
                LatestLocalPosition = localPosition;
                LatestLocalRotation = localRotation;
                LatestVelocity = velocity;
                LatestAngularVelocity = hasAngularVelocity ? angularVelocity : Vector3.zero;
                LatestHasAngularVelocity = hasAngularVelocity;
                LatestScale = scale ?? transform.localScale;
            }
            _any = true;
            ref var s = ref _ring[tick % Capacity];
            s.Tick = tick;
            s.Container = container;
            s.Position = localPosition;
            s.Rotation = localRotation;
            s.Velocity = velocity;
            s.AngularVelocity = hasAngularVelocity ? angularVelocity : Vector3.zero;
            s.HasAngularVelocity = hasAngularVelocity;
            s.HasVelocity = hasVelocity;
            s.Scale = scale ?? transform.localScale;
            s.Valid = true;
        }

        /// <summary>Record a world-space pose (no container).</summary>
        public void Push(uint tick, Vector3 position, Quaternion rotation, Vector3 velocity) => Push(tick, null, position, rotation, velocity);

        /// <summary>The floating origin moved: buffered world poses follow. Container-local poses need nothing (their containers moved).</summary>
        public void Shift(Vector3 delta)
        {
            for (int i = 0; i < Capacity; i++) if (_ring[i].Valid && _ring[i].Container == null) _ring[i].Position += delta;
            if (LatestContainer == null) LatestLocalPosition += delta;
        }

        public void Clear()
        {
            for (int i = 0; i < Capacity; i++) _ring[i].Valid = false;
            _any = false;
        }

        private static ulong ScopeOf(Container container) => container != null ? container.InstanceId : 0UL;

        /// <summary>
        /// The entity went through a portal (<c>docs/portals.md</c> D18): every buffered sample recorded in the scope it left is rewritten
        /// into <paramref name="destination"/>, in the scope it entered, through the link's pose, so the buffer holds one space and a
        /// replica glides across the face. Without it a sample on the old side would be bridged into the new container through the two
        /// containers' world poses, which differ by the portal's turn: a pop of up to a metre at a turned door.
        /// </summary>
        internal void CarryThroughPortal(in PortalMap map, Container destination)
        {
            if (!_any) return;
            for (int i = 0; i < Capacity; i++)
            {
                if (!_ring[i].Valid || ScopeOf(_ring[i].Container) != map.FromScope) continue;
                Portals.CarryLocal(in map, _ring[i].Container, _ring[i].Position, _ring[i].Rotation, _ring[i].Velocity, destination,
                    out var p, out var r, out var v);
                _ring[i].Container = destination;
                _ring[i].Position = p;
                _ring[i].Rotation = r;
                _ring[i].Velocity = v;
            }
            if (ScopeOf(LatestContainer) == map.FromScope)
            {
                Portals.CarryLocal(in map, LatestContainer, LatestLocalPosition, LatestLocalRotation, LatestVelocity, destination,
                    out var p, out var r, out var v);
                LatestContainer = destination;
                LatestLocalPosition = p;
                LatestLocalRotation = r;
                LatestVelocity = v;
            }
        }

        private bool TryGet(uint tick, out PoseSample s)
        {
            s = _ring[tick % Capacity];
            return s.Valid && s.Tick == tick;
        }

        /// <summary>
        /// Interpolated pose at <paramref name="renderTick"/>, in the space of <paramref name="container"/> (the
        /// newer sample's container; null means world). Falls back to extrapolating from the newest sample by its
        /// velocity (capped) when asked for a time we have not received yet. Apply it with
        /// <see cref="NetworkIdentity.SetLocalPose"/> so an entity under a moving container lands where the
        /// container is this frame.
        /// </summary>
        public bool Sample(double renderTick, out Container container, out Vector3 localPosition, out Quaternion localRotation)
            => Sample(renderTick, 0, out container, out localPosition, out localRotation);

        /// <param name="depth">How many carriers deep this sample is asked for while bridging another entity's crossing (0: the entity itself, counted in the telemetry).</param>
        private bool Sample(double renderTick, int depth, out Container container, out Vector3 localPosition, out Quaternion localRotation)
        {
            container = LatestContainer;
            localPosition = LatestLocalPosition;
            localRotation = LatestLocalRotation;
            if (!_any) return false;

            // Telemetry (see NebulaClient), for streams arriving every tick or two only: an entity the gateway sends
            // every 12th tick, or one that stopped moving, sits past its newest sample by design. A full-rate stream
            // whose render tick runs past the newest sample is starving: the snapshot pipeline delivered late.
            // Only while the stream is current (its newest sample is within two ticks of the newest tick heard): an
            // entity that stopped moving stops being sent, and is not starving. A stalled pipeline shows up in the
            // client's own-pawn gaps and snapshot age instead.
            if (depth == 0 && _latestTick - _previousTick <= 2 && NetworkTime.LatestServerTick - _latestTick <= 2)
            {
                Samples++;
                DepthSum += _latestTick - renderTick;
                if (renderTick > _latestTick + 1)
                {
                    Starved++;
                    double over = renderTick - _latestTick;
                    if (over > MaxOvershoot) MaxOvershoot = over;
                }
            }
            if (renderTick >= _latestTick)
            {
                // Past the newest sample: extrapolate by velocity, for at most the spacing this stream arrives at
                // (an entity the gateway sends every 12th tick keeps moving between samples instead of stepping)
                // and never less than a few ticks for a full-rate stream that just lost a packet.
                float spacing = Mathf.Max(6f, _latestTick - _previousTick);
                float ahead = Mathf.Min((float)(renderTick - _latestTick), spacing);
                var v = container != null ? container.InverseRotation * LatestVelocity : LatestVelocity;
                localPosition = LatestLocalPosition + v * (ahead * NetworkTime.TickInterval);
                // And keep turning at the newest sample's angular velocity, over the same capped time.
                if (LatestHasAngularVelocity) localRotation = Turn(LatestAngularVelocity, ahead * NetworkTime.TickInterval) * LatestLocalRotation;
                return true;
            }

            uint floor = (uint)System.Math.Floor(renderTick);
            // Find the nearest valid samples on either side of renderTick.
            PoseSample before = default, after = default;
            bool hasBefore = false, hasAfter = false;
            for (int i = 0; i < Capacity; i++)
            {
                uint t = floor - (uint)i;
                if (t > floor) break; // underflow
                if (TryGet(t, out before)) { hasBefore = true; break; }
            }
            for (uint t = floor + 1; t <= _latestTick; t++)
            {
                if (TryGet(t, out after)) { hasAfter = true; break; }
            }
            if (hasBefore && hasAfter)
            {
                float span = after.Tick - before.Tick;
                float f = span > 0 ? (float)((renderTick - before.Tick) / span) : 0f;
                container = after.Container;
                var bp = before.Position;
                var br = before.Rotation;
                if (before.Container != after.Container)
                {
                    // The entity changed container between the two samples: express the older one in the newer frame.
                    // The conversion uses both containers' poses at the older sample's own tick, not where they are
                    // now: a turning planet has moved on since, and a sample converted at today's pose lands
                    // ω × r × (render delay) off, a jump at every crossing of its box (NEB-392). The older sample is
                    // put in the newer container as both stood at its tick; from there both samples are local to the
                    // newer container and follow it to where it is now. Two containers riding the same carrier (two
                    // chunks of one planet) keep converting at their relative pose, which does not change.
                    var world = before.Container != null ? before.Container.ToWorld(bp) : bp;
                    var worldRot = before.Container != null ? before.Container.Rotation * br : br;
                    if (before.Container != null) CarryBack(before.Container, before.Tick, ref world, ref worldRot, depth);
                    if (container != null)
                    {
                        CarryForward(container, before.Tick, ref world, ref worldRot, depth);
                        bp = container.ToLocal(world);
                        br = container.InverseRotation * worldRot;
                    }
                    else
                    {
                        bp = world;
                        br = worldRot;
                    }
                }
                localPosition = SlerpPosition ? Vector3.Slerp(bp, after.Position, f) : Vector3.Lerp(bp, after.Position, f);
                localRotation = Quaternion.Slerp(br, after.Rotation, f);
                // Between two samples in one container, follow their velocities instead of the straight chord: a turning
                // entity sent at 15 or 5 Hz otherwise turns a corner at every sample.
                if (VelocityAware && before.Container == after.Container && span > 0)
                {
                    float seconds = span * NetworkTime.TickInterval;
                    // Angular velocities are measured in the container's own frame, so they hold in a moving one too; velocities do not.
                    if (!SlerpPosition && before.HasVelocity && after.HasVelocity && !RidesAMovingContainer(container))
                    {
                        var inverse = container != null ? container.InverseRotation : Quaternion.identity;
                        if (TryHermite(bp, inverse * before.Velocity, after.Position, inverse * after.Velocity, seconds, f, out var curved))
                            localPosition = curved;
                    }
                    if (before.HasAngularVelocity && after.HasAngularVelocity && TryTurnBetween(br, before.AngularVelocity, after.Rotation, after.AngularVelocity, seconds, f, out var turned))
                        localRotation = turned;
                }
                return true;
            }
            if (hasAfter || hasBefore)
            {
                var only = hasAfter ? after : before;
                container = only.Container;
                localPosition = only.Position;
                localRotation = only.Rotation;
            }
            return true;
        }

        /// <summary>
        /// Whether <paramref name="container"/>, or one it sits in, moves (a dynamic container: a ship, a turning planet). Its
        /// samples' velocities are in the scope's space, not the moving container's, so they are no tangents for a curve in its
        /// local space (on a turning planet they are off by the turn times the radius): velocity-aware interpolation keeps the
        /// straight line there.
        /// </summary>
        private static bool RidesAMovingContainer(Container container)
        {
            int hops = 0;
            for (var c = container; c != null && hops <= ContainerRegistry.ChainBound; c = c.Parent, hops++)
                if (c.IsDynamic) return true;
            return false;
        }

        /// <summary>
        /// The rotation vector (axis times angle, radians) of <paramref name="delta"/>, the shorter way round. Computed from the
        /// vector part with atan2 in double: <c>Quaternion.ToAngleAxis</c> takes the angle from <c>acos(w)</c>, which loses a turn
        /// of a few hundredths of a degree in float rounding.
        /// </summary>
        internal static Vector3 RotationVector(Quaternion delta)
        {
            double x = delta.x, y = delta.y, z = delta.z, w = delta.w;
            if (w < 0) { x = -x; y = -y; z = -z; w = -w; }
            double sin = Math.Sqrt(x * x + y * y + z * z);
            if (sin < 1e-12 || double.IsNaN(sin)) return Vector3.zero;
            double angle = 2.0 * Math.Atan2(sin, w);
            double k = angle / sin;
            return new Vector3((float)(x * k), (float)(y * k), (float)(z * k));
        }

        /// <summary>
        /// The angle between two rotations in degrees, exact down to small angles. <c>Quaternion.Angle</c> returns 0 for anything
        /// under about 0.16 degrees (it treats a dot product within 1e-6 of 1 as equal), which is what hid slow turns below a
        /// <see cref="NetworkTransform.RotAngleThreshold"/> of 0.01 degrees (NEB-406).
        /// </summary>
        internal static float AngleDegrees(Quaternion a, Quaternion b) => RotationVector(a * Quaternion.Inverse(b)).magnitude * Mathf.Rad2Deg;

        /// <summary>The rotation an angular velocity of <paramref name="omega"/> radians per second turns through in <paramref name="seconds"/> (left-multiplied: in the frame the velocity is expressed in).</summary>
        internal static Quaternion Turn(Vector3 omega, float seconds)
        {
            float radians = omega.magnitude * seconds;
            if (radians < 1e-7f || float.IsNaN(radians)) return Quaternion.identity;
            return Quaternion.AngleAxis(radians * Mathf.Rad2Deg, omega / omega.magnitude);
        }

        /// <summary>
        /// The cubic Hermite point at <paramref name="f"/> between <paramref name="p0"/> and <paramref name="p1"/> with velocities
        /// <paramref name="v0"/>, <paramref name="v1"/> over <paramref name="seconds"/>. False when the velocities do not explain the
        /// chord (their mean travel is off it by more than a quarter of its length plus 5 cm: a teleport, a bounce, a stream
        /// without velocities), so the caller keeps the straight line and nothing overshoots.
        /// </summary>
        internal static bool TryHermite(Vector3 p0, Vector3 v0, Vector3 p1, Vector3 v1, float seconds, float f, out Vector3 point)
        {
            point = Vector3.zero;
            var chord = p1 - p0;
            var mean = (v0 + v1) * (0.5f * seconds);
            float allowed = chord.magnitude * 0.25f + 0.05f;
            if ((chord - mean).sqrMagnitude > allowed * allowed) return false;
            float f2 = f * f, f3 = f2 * f;
            float h00 = 2f * f3 - 3f * f2 + 1f, h10 = f3 - 2f * f2 + f, h01 = -2f * f3 + 3f * f2, h11 = f3 - f2;
            point = h00 * p0 + (h10 * seconds) * v0 + h01 * p1 + (h11 * seconds) * v1;
            return true;
        }

        /// <summary>
        /// A rotation at <paramref name="f"/> between <paramref name="q0"/> and <paramref name="q1"/> that leaves the first with
        /// angular velocity <paramref name="w0"/> and arrives at the second with <paramref name="w1"/> (both radians per second, in
        /// the frame the rotations are local to): a cubic Hermite curve on the rotation vector from <paramref name="q0"/> to
        /// <paramref name="q1"/>, which is exact at both ends and follows a steady acceleration of the turn. False when the mean
        /// angular velocity does not explain the turn (its rotation vector is off by more than a quarter of the turn plus a
        /// degree), so the caller keeps the plain slerp.
        /// </summary>
        internal static bool TryTurnBetween(Quaternion q0, Vector3 w0, Quaternion q1, Vector3 w1, float seconds, float f, out Quaternion rotation)
        {
            rotation = q1;
            var turn = RotationVector(q1 * Quaternion.Inverse(q0));
            var mean = (w0 + w1) * (0.5f * seconds);
            float allowed = turn.magnitude * 0.25f + Mathf.Deg2Rad;
            if ((turn - mean).sqrMagnitude > allowed * allowed) return false;
            float f2 = f * f, f3 = f2 * f;
            float h10 = f3 - 2f * f2 + f, h01 = -2f * f3 + 3f * f2, h11 = f3 - f2;
            var vector = h01 * turn + (h10 * seconds) * w0 + (h11 * seconds) * w1;
            rotation = Turn(vector, 1f) * q0;
            return true;
        }

        /// <summary>Interpolated world pose at <paramref name="renderTick"/> (through each sample's container as it stands now).</summary>
        public bool Sample(double renderTick, out Vector3 position, out Quaternion rotation)
        {
            bool ok = Sample(renderTick, out var container, out var lp, out var lr);
            position = container != null ? container.ToWorld(lp) : lp;
            rotation = container != null ? container.Rotation * lr : lr;
            return ok;
        }

        // ---- a container's pose at a past tick (NEB-392)

        /// <summary>How far past a carrier's newest sample a prediction's comparison extrapolates its pose (<see cref="CarryBack"/>): a tenth of a second.</summary>
        internal static uint PredictionAheadTicks => (uint)NetworkTime.SecondsToTicks(0.1);

        /// <summary>How many carriers deep a pose at a past tick is looked up (a crate in a ship in a turning planet is two).</summary>
        private const int CarryDepth = 4;

        /// <summary>
        /// The carrier whose motion <paramref name="container"/> follows rigidly in render space: the nearest moving
        /// carrier at or above it whose copy here is interpolated (its buffer knows where it was at a past tick). None for
        /// a container that does not move, or one in a frame posed for simulation, where nothing follows its carrier.
        /// </summary>
        private static NetworkIdentity MovingCarrierOf(Container container, bool forPrediction = false)
        {
            int hops = 0;
            for (var c = container; c != null && hops <= ContainerRegistry.ChainBound; c = c.Parent, hops++)
            {
                // A prediction's comparison reads positions through the frames' render poses, not the roots', and follows the
                // carrier whether or not a frame is posed for simulation right now.
                if (!forPrediction && c.Frame != null && PhysicsFrames.InSimulationPose(c)) return null;
                if (!c.IsDynamic) continue;
                // A carrier this process predicts or simulates is not drawn from its buffer: its pose at a past tick is
                // not known here, and it is converted at its current pose as before.
                var carrier = c.Carrier;
                return carrier != null && !carrier.IsLocallyPredicted && carrier.Interpolator != null && carrier.Interpolator.HasSamples ? carrier : null;
            }
            return null;
        }

        /// <summary>A carrier's world pose at <paramref name="tick"/>, from its own buffer, through its container as that stood then.</summary>
        private static void CarrierPoseAt(NetworkIdentity carrier, uint tick, int depth, out Vector3 position, out Quaternion rotation)
        {
            carrier.Interpolator.Sample(tick, depth + 1, out var container, out position, out rotation);
            if (container == null) return;
            position = container.ToWorld(position);
            rotation = container.Rotation * rotation;
            CarryBack(container, tick, ref position, ref rotation, depth + 1);
        }

        /// <summary>
        /// A world pose fixed in <paramref name="container"/> as it stands now, moved to where it was at
        /// <paramref name="tick"/>: carried back along the motion of the carrier the container rides on.
        /// </summary>
        internal static void CarryBack(Container container, uint tick, ref Vector3 position, ref Quaternion rotation, int depth, bool forPrediction = false)
        {
            if (depth >= CarryDepth) return;
            var carrier = MovingCarrierOf(container, forPrediction);
            // Only a tick the carrier's buffer has reached: past its newest sample it would be extrapolated. A prediction's
            // comparison may run a few ticks ahead of it: the owner's report of a tick can arrive before the carrier's state
            // for that tick, and the carrier's velocity carries it across them (NEB-496).
            if (carrier == null || tick > carrier.Interpolator.LatestTick + (forPrediction ? PredictionAheadTicks : 0u)) return;
            CarrierPoseAt(carrier, tick, depth, out var thenPosition, out var thenRotation);
            var t = carrier.transform;
            var turn = thenRotation * Quaternion.Inverse(t.rotation);
            position = thenPosition + turn * (position - t.position);
            rotation = turn * rotation;
        }

        /// <summary>The inverse of <see cref="CarryBack"/>: a world pose fixed in <paramref name="container"/> as it stood at <paramref name="tick"/>, where it is now.</summary>
        internal static void CarryForward(Container container, uint tick, ref Vector3 position, ref Quaternion rotation, int depth, bool forPrediction = false)
        {
            if (depth >= CarryDepth) return;
            var carrier = MovingCarrierOf(container, forPrediction);
            // Only a tick the carrier's buffer has reached: past its newest sample it would be extrapolated. A prediction's
            // comparison may run a few ticks ahead of it: the owner's report of a tick can arrive before the carrier's state
            // for that tick, and the carrier's velocity carries it across them (NEB-496).
            if (carrier == null || tick > carrier.Interpolator.LatestTick + (forPrediction ? PredictionAheadTicks : 0u)) return;
            CarrierPoseAt(carrier, tick, depth, out var thenPosition, out var thenRotation);
            var t = carrier.transform;
            var turn = t.rotation * Quaternion.Inverse(thenRotation);
            position = t.position + turn * (position - thenPosition);
            rotation = turn * rotation;
        }

        internal Vector3 SampleScale(double tick)
        {
            if (!_any || tick >= _latestTick) return LatestScale;
            PoseSample before = default, after = default;
            bool hasBefore = false, hasAfter = false;
            foreach (var s in _ring)
            {
                if (!s.Valid) continue;
                if (s.Tick <= tick && (!hasBefore || s.Tick > before.Tick)) { before = s; hasBefore = true; }
                if (s.Tick > tick && (!hasAfter || s.Tick < after.Tick)) { after = s; hasAfter = true; }
            }
            if (hasBefore && hasAfter) return Vector3.Lerp(before.Scale, after.Scale, (float)((tick - before.Tick) / (after.Tick - before.Tick)));
            return hasBefore ? before.Scale : hasAfter ? after.Scale : LatestScale;
        }
    }
}
