using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace Nebula
{
    /// <summary>
    /// A named stretch of code whose cost is accumulated by <see cref="NebulaProfiler"/>. Begin/End are a
    /// <see cref="Stopwatch.GetTimestamp"/> read each, cheap enough to leave in shipping workers.
    /// </summary>
    public sealed class ProfileSection
    {
        public readonly string Name;
        internal long Elapsed;
        internal int Calls;
        private long _start;

        internal ProfileSection(string name) { Name = name; }

        public void Begin() { _start = Stopwatch.GetTimestamp(); }

        public void End()
        {
            Elapsed += Stopwatch.GetTimestamp() - _start;
            Calls++;
        }
    }

    /// <summary>
    /// Where a worker's tick goes. Nebula times each phase of its own tick; game code may add sections of its own
    /// (<c>static readonly ProfileSection Move = NebulaProfiler.Section("npc.move")</c>) and they appear in the
    /// same report: one <c>[nebula] profile</c> log line per <see cref="NebulaWorker"/> every few seconds, with
    /// every section's cost expressed in milliseconds per simulated tick so the columns add up to the tick time.
    /// </summary>
    public static class NebulaProfiler
    {
        private static readonly List<ProfileSection> Sections = new List<ProfileSection>();
        private static readonly Dictionary<string, ProfileSection> ByName = new Dictionary<string, ProfileSection>();
        private static readonly double MsPerStopwatchTick = 1000.0 / Stopwatch.Frequency;

        public static ProfileSection Section(string name)
        {
            lock (Sections)
            {
                if (!ByName.TryGetValue(name, out var s))
                {
                    s = new ProfileSection(name);
                    ByName[name] = s;
                    Sections.Add(s);
                }
                return s;
            }
        }

        /// <summary>Every section as <c>name=ms/tick</c> (with its call count when it differs from the tick count), then reset.</summary>
        public static string ReportAndReset(int ticks)
        {
            var sb = new StringBuilder();
            lock (Sections)
            {
                foreach (var s in Sections)
                {
                    if (s.Calls == 0) continue;
                    double ms = s.Elapsed * MsPerStopwatchTick / System.Math.Max(1, ticks);
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append(s.Name).Append('=').Append(ms.ToString("0.00"));
                    if (s.Calls != ticks) sb.Append('(').Append(s.Calls).Append(')');
                    s.Elapsed = 0;
                    s.Calls = 0;
                }
            }
            return sb.ToString();
        }
    }
}
