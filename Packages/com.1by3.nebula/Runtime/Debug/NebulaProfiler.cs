using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
        /// <summary>Length of the last Begin/End span, so an enclosing section can take a nested one out of its own total.</summary>
        internal long LastSpan;
        private long _start;

        internal ProfileSection(string name) { Name = name; }

        public void Begin() { _start = Stopwatch.GetTimestamp(); }

        public void End()
        {
            LastSpan = Stopwatch.GetTimestamp() - _start;
            Elapsed += LastSpan;
            Calls++;
        }
    }

    /// <summary>
    /// Where a worker's tick goes. Nebula times each phase of its own tick; game code may add sections of its own
    /// (<c>static readonly ProfileSection Move = NebulaProfiler.Section("npc.move")</c>) and they appear in the
    /// same report: one <c>[nebula] profile</c> log line per <see cref="NebulaWorker"/> every few seconds, with
    /// every section's cost expressed in milliseconds per simulated tick so the columns add up to the tick time.
    /// <see cref="Section"/> is the public API for that: it returns the same <see cref="ProfileSection"/> for a name
    /// however often it is called, so keep it in a static readonly field and call Begin/End around the work. A
    /// section also travels in the worker's heartbeat (<c>WorkerInfo.ProfileSections</c>, <c>/api/state</c>).
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
        public static string ReportAndReset(int ticks) => ReportAndReset(ticks, out _);

        /// <summary>
        /// As <see cref="ReportAndReset(int)"/>, and <paramref name="compact"/> carries the same figures as
        /// <c>name=ms</c> pairs separated by spaces (no call counts), the form the heartbeat sends to the orchestrator.
        /// </summary>
        public static string ReportAndReset(int ticks, out string compact)
        {
            var sb = new StringBuilder();
            var wire = new StringBuilder();
            lock (Sections)
            {
                foreach (var s in Sections)
                {
                    if (s.Calls == 0) continue;
                    double ms = s.Elapsed * MsPerStopwatchTick / System.Math.Max(1, ticks);
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append(s.Name).Append('=').Append(ms.ToString("0.00"));
                    if (s.Calls != ticks) sb.Append('(').Append(s.Calls).Append(')');
                    if (wire.Length > 0) wire.Append(' ');
                    wire.Append(s.Name).Append('=').Append(ms.ToString("0.00", CultureInfo.InvariantCulture));
                    s.Elapsed = 0;
                    s.Calls = 0;
                }
            }
            compact = wire.ToString();
            return sb.ToString();
        }
    }
}
