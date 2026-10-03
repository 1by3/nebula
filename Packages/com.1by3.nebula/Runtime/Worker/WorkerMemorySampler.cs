using System;
using System.Diagnostics;
using UnityEngine.Profiling;

namespace Nebula
{
    /// <summary>
    /// Reads a worker's memory figures for its heartbeat and its profile log line. It runs at heartbeat rate (once
    /// every <see cref="NebulaConfig.WorkerHeartbeatSeconds"/>), keeps one <see cref="Process"/> object and refreshes
    /// it, and allocates nothing per call beyond what the OS query itself needs.
    /// </summary>
    public static class WorkerMemorySampler
    {
        private static Process _process;

        /// <summary>Fills the memory fields of <paramref name="stats"/> with the current figures.</summary>
        public static void Sample(ref WorkerStats stats)
        {
            stats.ResidentBytes = ResidentBytes();
            stats.NativeAllocatedBytes = Clamp(Profiler.GetTotalAllocatedMemoryLong());
            stats.NativeReservedBytes = Clamp(Profiler.GetTotalReservedMemoryLong());
            stats.ManagedBytes = Clamp(GC.GetTotalMemory(false));
            stats.GcCount = (uint)GC.CollectionCount(0);
        }

        private static ulong ResidentBytes()
        {
            try
            {
                if (_process == null) _process = Process.GetCurrentProcess();
                else _process.Refresh();
                return Clamp(_process.WorkingSet64);
            }
            catch { return 0; }
        }

        private static ulong Clamp(long v) => v < 0 ? 0UL : (ulong)v;

        /// <summary>Megabytes with one decimal, for the profile line.</summary>
        public static string Mb(ulong bytes) => (bytes / 1048576.0).ToString("0.0");
    }
}
