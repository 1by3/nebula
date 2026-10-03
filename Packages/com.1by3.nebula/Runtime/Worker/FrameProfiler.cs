using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;

namespace Nebula
{
    /// <summary>One profile window of whole-frame time, in milliseconds per Unity frame (see <see cref="FrameProfiler"/>).</summary>
    public struct FrameReport
    {
        public int Frames;
        public float AvgMs, P90Ms, MaxMs;
        /// <summary>The physics step (FixedUpdate's PhysicsFixedUpdate system).</summary>
        public float PhysicsMs;
        /// <summary>The rest of the FixedUpdate phase: script FixedUpdate other than Nebula's tick, and the engine's own fixed work.</summary>
        public float FixedOtherMs;
        /// <summary>Nebula's tick (the part of FixedUpdate that is Nebula's).</summary>
        public float TickMs;
        /// <summary>The Update phase: Nebula's transport poll and every script Update.</summary>
        public float UpdateMs;
        /// <summary>The PreLateUpdate phase: every script LateUpdate and the animation systems.</summary>
        public float LateUpdateMs;
        /// <summary>What the phases above do not cover: waiting for the frame limiter, rendering, the engine's own frame work and GC.</summary>
        public float IdleMs;

        /// <summary>The split as <c>name=ms</c> pairs separated by spaces, in ms per frame; the part that adds up to <see cref="AvgMs"/>.</summary>
        public string Split()
        {
            var c = CultureInfo.InvariantCulture;
            return new StringBuilder(96)
                .Append("physics=").Append(PhysicsMs.ToString("0.00", c))
                .Append(" fixed=").Append(FixedOtherMs.ToString("0.00", c))
                .Append(" tick=").Append(TickMs.ToString("0.00", c))
                .Append(" update=").Append(UpdateMs.ToString("0.00", c))
                .Append(" late=").Append(LateUpdateMs.ToString("0.00", c))
                .Append(" idle=").Append(IdleMs.ToString("0.00", c)).ToString();
        }
    }

    /// <summary>
    /// Wall time per Unity frame on a worker, and where in the frame it goes. The profile line's tick sections say
    /// where Nebula's tick goes; everything else (the physics step, script Update and LateUpdate, the frame limiter)
    /// was lumped into one 'gap'. This inserts timing systems into the player loop at the start of the frame, at
    /// the edges of the FixedUpdate, Update and PreLateUpdate phases and around the physics step, so a window reports
    /// the frame's average, p90 and max, and the frame split into phases. Each marker is one
    /// <see cref="Stopwatch.GetTimestamp"/> read and nothing is allocated per frame; <see cref="Take"/> allocates once per window.
    /// </summary>
    public static class FrameProfiler
    {
        private struct FrameStartMarker { }
        private struct FixedBeginMarker { }
        private struct FixedEndMarker { }
        private struct PhysicsBeginMarker { }
        private struct PhysicsEndMarker { }
        private struct UpdateBeginMarker { }
        private struct UpdateEndMarker { }
        private struct LateBeginMarker { }
        private struct LateEndMarker { }

        private const int MaxSamples = 2048;
        private static readonly double MsPerStopwatchTick = 1000.0 / Stopwatch.Frequency;

        // Written by the player loop on the main thread, read by Take on the same thread: no locking.
        private static long _lastFrameStart, _fixedStart, _physicsStart, _updateStart, _lateStart;
        private static long _fixed, _physics, _update, _late, _frameTotal;
        private static readonly float[] Samples = new float[MaxSamples];
        private static readonly float[] Scratch = new float[MaxSamples];
        private static int _sampleCount, _frames;
        private static float _maxMs;

        /// <summary>Puts the timing systems into the player loop. Safe to call again: it does nothing once they are in.</summary>
        public static void Install()
        {
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            var phases = loop.subSystemList;
            if (phases == null) return;
            for (int i = 0; i < phases.Length; i++)
            {
                if (phases[i].subSystemList == null) continue;
                foreach (var s in phases[i].subSystemList) if (s.type == typeof(FrameStartMarker)) return;
            }
            for (int i = 0; i < phases.Length; i++)
            {
                var t = phases[i].type;
                var list = phases[i].subSystemList;
                if (list == null) continue;
                if (t == typeof(Initialization))
                    list = Insert(list, 0, typeof(FrameStartMarker), OnFrameStart);
                else if (t == typeof(FixedUpdate))
                {
                    int physics = Array.FindIndex(list, s => s.type == typeof(FixedUpdate.PhysicsFixedUpdate));
                    if (physics >= 0)
                    {
                        list = Insert(list, physics + 1, typeof(PhysicsEndMarker), OnPhysicsEnd);
                        list = Insert(list, physics, typeof(PhysicsBeginMarker), OnPhysicsBegin);
                    }
                    list = Insert(list, 0, typeof(FixedBeginMarker), OnFixedBegin);
                    list = Insert(list, list.Length, typeof(FixedEndMarker), OnFixedEnd);
                }
                else if (t == typeof(Update))
                {
                    list = Insert(list, 0, typeof(UpdateBeginMarker), OnUpdateBegin);
                    list = Insert(list, list.Length, typeof(UpdateEndMarker), OnUpdateEnd);
                }
                else if (t == typeof(PreLateUpdate))
                {
                    list = Insert(list, 0, typeof(LateBeginMarker), OnLateBegin);
                    list = Insert(list, list.Length, typeof(LateEndMarker), OnLateEnd);
                }
                else continue;
                phases[i].subSystemList = list;
            }
            loop.subSystemList = phases;
            PlayerLoop.SetPlayerLoop(loop);
        }

        private static PlayerLoopSystem[] Insert(PlayerLoopSystem[] list, int index, Type type, PlayerLoopSystem.UpdateFunction fn)
        {
            var result = new PlayerLoopSystem[list.Length + 1];
            Array.Copy(list, 0, result, 0, index);
            result[index] = new PlayerLoopSystem { type = type, updateDelegate = fn };
            Array.Copy(list, index, result, index + 1, list.Length - index);
            return result;
        }

        private static void OnFrameStart()
        {
            long now = Stopwatch.GetTimestamp();
            if (_lastFrameStart != 0)
            {
                long span = now - _lastFrameStart;
                _frameTotal += span;
                _frames++;
                float ms = (float)(span * MsPerStopwatchTick);
                if (ms > _maxMs) _maxMs = ms;
                // A window longer than the buffer keeps the newest frames for the percentile; the average and max stay exact.
                Samples[_sampleCount++ % MaxSamples] = ms;
            }
            _lastFrameStart = now;
        }

        private static void OnFixedBegin() { _fixedStart = Stopwatch.GetTimestamp(); }
        private static void OnFixedEnd() { _fixed += Stopwatch.GetTimestamp() - _fixedStart; }
        private static void OnPhysicsBegin() { _physicsStart = Stopwatch.GetTimestamp(); }
        private static void OnPhysicsEnd() { _physics += Stopwatch.GetTimestamp() - _physicsStart; }
        private static void OnUpdateBegin() { _updateStart = Stopwatch.GetTimestamp(); }
        private static void OnUpdateEnd() { _update += Stopwatch.GetTimestamp() - _updateStart; }
        private static void OnLateBegin() { _lateStart = Stopwatch.GetTimestamp(); }
        private static void OnLateEnd() { _late += Stopwatch.GetTimestamp() - _lateStart; }

        /// <summary>
        /// The window since the last call, then reset. <paramref name="tickTotalMs"/> is the time Nebula's ticks took
        /// in the window; it sits inside the FixedUpdate phase, so it is taken out of the 'fixed' share. Frames is 0
        /// when no timing systems are installed or no frame completed.
        /// </summary>
        public static FrameReport Take(double tickTotalMs)
        {
            var r = new FrameReport { Frames = _frames };
            if (_frames > 0)
            {
                double perFrame = MsPerStopwatchTick / _frames;
                r.AvgMs = (float)(_frameTotal * perFrame);
                r.MaxMs = _maxMs;
                int n = Math.Min(_sampleCount, MaxSamples);
                Array.Copy(Samples, Scratch, n);
                Array.Sort(Scratch, 0, n);
                r.P90Ms = Scratch[Math.Min(n - 1, (int)(n * 0.9))];
                r.PhysicsMs = (float)(_physics * perFrame);
                r.TickMs = (float)(tickTotalMs / _frames);
                r.FixedOtherMs = Math.Max(0f, (float)(_fixed * perFrame) - r.PhysicsMs - r.TickMs);
                r.UpdateMs = (float)(_update * perFrame);
                r.LateUpdateMs = (float)(_late * perFrame);
                r.IdleMs = Math.Max(0f, r.AvgMs - r.PhysicsMs - r.FixedOtherMs - r.TickMs - r.UpdateMs - r.LateUpdateMs);
            }
            _fixed = _physics = _update = _late = _frameTotal = 0;
            _frames = _sampleCount = 0;
            _maxMs = 0f;
            return r;
        }
    }
}
