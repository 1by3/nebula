using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// Tick-indexed transform buffer for copies of an entity this process does not simulate: ghosts on a worker and
    /// remote entities on a client. The consumer decides at which (fractional) tick to sample.
    /// </summary>
    public sealed class RemoteInterpolator : MonoBehaviour
    {
        private struct PoseSample
        {
            public uint Tick;
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
        public Vector3 LatestVelocity { get; private set; }
        public Vector3 LatestPosition { get; private set; }
        public Quaternion LatestRotation { get; private set; } = Quaternion.identity;

        public void Push(uint tick, Vector3 position, Quaternion rotation, Vector3 velocity)
        {
            if (_any && tick + Capacity <= _latestTick) return; // too old to matter
            if (!_any || tick > _latestTick)
            {
                _previousTick = _any ? _latestTick : tick;
                _latestTick = tick;
                LatestPosition = position;
                LatestRotation = rotation;
                LatestVelocity = velocity;
            }
            _any = true;
            ref var s = ref _ring[tick % Capacity];
            s.Tick = tick;
            s.Position = position;
            s.Rotation = rotation;
            s.Velocity = velocity;
            s.Valid = true;
        }

        /// <summary>The floating origin moved: every buffered position follows.</summary>
        public void Shift(Vector3 delta)
        {
            for (int i = 0; i < Capacity; i++) if (_ring[i].Valid) _ring[i].Position += delta;
            LatestPosition += delta;
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
        /// Interpolated pose at <paramref name="renderTick"/>. Falls back to extrapolating from the newest sample by
        /// its velocity (capped) when asked for a time we have not received yet.
        /// </summary>
        public bool Sample(double renderTick, out Vector3 position, out Quaternion rotation)
        {
            position = LatestPosition;
            rotation = LatestRotation;
            if (!_any) return false;

            if (renderTick >= _latestTick)
            {
                // Past the newest sample: extrapolate by velocity, for at most the spacing this stream arrives at
                // (an entity the gateway sends every 12th tick keeps moving between samples instead of stepping)
                // and never less than a few ticks for a full-rate stream that just lost a packet.
                float spacing = Mathf.Max(6f, _latestTick - _previousTick);
                float ahead = Mathf.Min((float)(renderTick - _latestTick), spacing);
                position = LatestPosition + LatestVelocity * (ahead * NetworkTime.TickInterval);
                rotation = LatestRotation;
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
                position = Vector3.Lerp(before.Position, after.Position, f);
                rotation = Quaternion.Slerp(before.Rotation, after.Rotation, f);
                return true;
            }
            if (hasAfter)
            {
                position = after.Position;
                rotation = after.Rotation;
                return true;
            }
            if (hasBefore)
            {
                position = before.Position;
                rotation = before.Rotation;
                return true;
            }
            return true;
        }
    }
}
