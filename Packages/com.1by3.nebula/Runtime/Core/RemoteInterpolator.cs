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
                LatestScale = scale ?? transform.localScale;
            }
            _any = true;
            ref var s = ref _ring[tick % Capacity];
            s.Tick = tick;
            s.Container = container;
            s.Position = localPosition;
            s.Rotation = localRotation;
            s.Velocity = velocity;
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

        /// <summary>Interpolated world pose at <paramref name="renderTick"/> (through each sample's container as it stands now).</summary>
        public bool Sample(double renderTick, out Vector3 position, out Quaternion rotation)
        {
            bool ok = Sample(renderTick, out var container, out var lp, out var lr);
            position = container != null ? container.ToWorld(lp) : lp;
            rotation = container != null ? container.Rotation * lr : lr;
            return ok;
        }

        // ---- a container's pose at a past tick (NEB-392)

        /// <summary>How many carriers deep a pose at a past tick is looked up (a crate in a ship in a turning planet is two).</summary>
        private const int CarryDepth = 4;

        /// <summary>
        /// The carrier whose motion <paramref name="container"/> follows rigidly in render space: the nearest moving
        /// carrier at or above it whose copy here is interpolated (its buffer knows where it was at a past tick). None for
        /// a container that does not move, or one in a frame posed for simulation, where nothing follows its carrier.
        /// </summary>
        private static NetworkIdentity MovingCarrierOf(Container container)
        {
            int hops = 0;
            for (var c = container; c != null && hops <= ContainerRegistry.ChainBound; c = c.Parent, hops++)
            {
                if (c.Frame != null && PhysicsFrames.InSimulationPose(c)) return null;
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
        private static void CarryBack(Container container, uint tick, ref Vector3 position, ref Quaternion rotation, int depth)
        {
            if (depth >= CarryDepth) return;
            var carrier = MovingCarrierOf(container);
            // Only a tick the carrier's buffer has reached: past its newest sample it would be extrapolated.
            if (carrier == null || tick > carrier.Interpolator.LatestTick) return;
            CarrierPoseAt(carrier, tick, depth, out var thenPosition, out var thenRotation);
            var t = carrier.transform;
            var turn = thenRotation * Quaternion.Inverse(t.rotation);
            position = thenPosition + turn * (position - t.position);
            rotation = turn * rotation;
        }

        /// <summary>The inverse of <see cref="CarryBack"/>: a world pose fixed in <paramref name="container"/> as it stood at <paramref name="tick"/>, where it is now.</summary>
        private static void CarryForward(Container container, uint tick, ref Vector3 position, ref Quaternion rotation, int depth)
        {
            if (depth >= CarryDepth) return;
            var carrier = MovingCarrierOf(container);
            // Only a tick the carrier's buffer has reached: past its newest sample it would be extrapolated.
            if (carrier == null || tick > carrier.Interpolator.LatestTick) return;
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
