using System;
using System.Collections.Generic;

namespace Nebula
{
    /// <summary>
    /// How late a client's world-state streams arrive, for the adaptive part of the render delay
    /// (<see cref="NebulaConfig.AdaptiveInterpolationDelay"/>). Each packet's lateness is how many ticks older it is than the
    /// tick the client's clock expected to arrive at that moment; on a clean stream it hovers around zero, and a worker
    /// that runs behind, or a link that bunches packets, shows up as positive values. The render delay must cover the
    /// lateness of nearly every packet or the interpolator runs past its newest sample and extrapolates: this keeps a
    /// window of samples per stream (per worker, since one slow worker's entities are a small share of the packets) and
    /// reports the highest per-stream percentile.
    /// <para>Pure and allocation-free after warm-up, so it is tested on its own.</para>
    /// </summary>
    public sealed class StreamLateness
    {
        /// <summary>Samples kept per stream; older ones fall out of the window by age first.</summary>
        public const int Capacity = 256;

        private sealed class Stream
        {
            public readonly double[] Times = new double[Capacity];
            public readonly float[] Values = new float[Capacity];
            public int Head, Count;
            public double LastAt;
        }

        private readonly Dictionary<int, Stream> _streams = new Dictionary<int, Stream>();
        private readonly List<int> _stale = new List<int>();
        private readonly float[] _scratch = new float[Capacity];

        /// <summary>Seconds of samples each percentile is taken over. Default 2.</summary>
        public double WindowSeconds = 2.0;

        /// <summary>Record that a packet of stream <paramref name="stream"/> (a worker index) arrived <paramref name="lateTicks"/> late at <paramref name="now"/> seconds.</summary>
        public void Add(int stream, double now, float lateTicks)
        {
            if (float.IsNaN(lateTicks) || float.IsInfinity(lateTicks)) return;
            if (!_streams.TryGetValue(stream, out var s)) _streams[stream] = s = new Stream();
            s.Times[s.Head] = now;
            s.Values[s.Head] = lateTicks;
            s.Head = (s.Head + 1) % Capacity;
            if (s.Count < Capacity) s.Count++;
            s.LastAt = now;
        }

        /// <summary>Forget every stream (a reconnect).</summary>
        public void Clear() => _streams.Clear();

        /// <summary>
        /// The highest, over the streams heard within the window, of each stream's <paramref name="percentile"/>th percentile
        /// (0 to 100) of lateness in the last <see cref="WindowSeconds"/>, in ticks; 0 when nothing was heard. A stream silent
        /// for longer than the window is dropped.
        /// </summary>
        public float Percentile(double now, float percentile)
        {
            float p = Math.Max(0f, Math.Min(100f, percentile)) / 100f;
            float worst = 0f;
            _stale.Clear();
            foreach (var pair in _streams)
            {
                var s = pair.Value;
                if (now - s.LastAt > WindowSeconds) { _stale.Add(pair.Key); continue; }
                int n = 0;
                for (int i = 0; i < s.Count; i++)
                {
                    int at = (s.Head - 1 - i + Capacity) % Capacity;
                    if (now - s.Times[at] > WindowSeconds) break;
                    _scratch[n++] = s.Values[at];
                }
                if (n == 0) continue;
                Array.Sort(_scratch, 0, n);
                int index = Math.Max(0, Math.Min(n - 1, (int)Math.Ceiling(p * n) - 1));
                if (_scratch[index] > worst) worst = _scratch[index];
            }
            for (int i = 0; i < _stale.Count; i++) _streams.Remove(_stale[i]);
            return worst;
        }
    }
}
