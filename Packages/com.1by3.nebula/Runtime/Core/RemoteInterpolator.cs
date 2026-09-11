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
            public bool Valid;
        }

        private const int Capacity = 64;
        private readonly PoseSample[] _ring = new PoseSample[Capacity];
        private uint _latestTick;
        private uint _previousTick;
        private bool _any;

        public uint LatestTick => _latestTick;
        public bool HasSamples => _any;
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
        public void Push(uint tick, Container container, Vector3 localPosition, Quaternion localRotation, Vector3 velocity)
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
            }
            _any = true;
            ref var s = ref _ring[tick % Capacity];
            s.Tick = tick;
            s.Container = container;
            s.Position = localPosition;
            s.Rotation = localRotation;
            s.Velocity = velocity;
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
        {
            container = LatestContainer;
            localPosition = LatestLocalPosition;
            localRotation = LatestLocalRotation;
            if (!_any) return false;

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
                    var world = before.Container != null ? before.Container.ToWorld(bp) : bp;
                    var worldRot = before.Container != null ? before.Container.Rotation * br : br;
                    bp = container != null ? container.ToLocal(world) : world;
                    br = container != null ? container.InverseRotation * worldRot : worldRot;
                }
                localPosition = Vector3.Lerp(bp, after.Position, f);
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
    }
}
