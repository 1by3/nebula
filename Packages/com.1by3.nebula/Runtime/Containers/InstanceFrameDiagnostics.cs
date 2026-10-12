using System.Collections.Generic;
using Nebula.World;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// TEMPORARY (HVR-674 / NEB-426): log lines tagged [HVR-674] for an entity in an instance scope, on a worker and on a
    /// client: at a crossing and once a second while it is inside. Remove once the far-instance Play test passes.
    /// </summary>
    internal static class InstanceFrameDiagnostics
    {
        private static readonly Dictionary<ulong, float> NextLine = new Dictionary<ulong, float>();
        private static readonly Dictionary<ulong, ulong> LastScope = new Dictionary<ulong, ulong>();

        internal static void Tick(string role, NetworkIdentity e)
        {
            if (e == null) return;
            ulong scope = e.InstanceId;
            LastScope.TryGetValue(e.NetId, out var last);
            bool crossed = LastScope.ContainsKey(e.NetId) && last != scope;
            LastScope[e.NetId] = scope;
            bool instance = scope != 0 && !NebulaChunks.HasGridFor(scope);
            if (!crossed && !instance) return;
            float now = Time.unscaledTime;
            if (!crossed && NextLine.TryGetValue(e.NetId, out var next) && now < next) return;
            NextLine[e.NetId] = now + 1f;
            Log(role, crossed ? $"crossed {last:x}->{scope:x}" : "inside", e, null);
        }

        internal static void Log(string role, string what, NetworkIdentity e, InstanceBoundary boundary)
        {
            if (e == null) return;
            ulong scope = e.InstanceId;
            var frame = ScopeFrames.Of(scope);
            frame.OriginOffsetPrecise(out double ox, out double oy, out double oz);
            var absolute = ContainerRegistry.ToAbsolutePrecise(e.ScopePositionPrecise, scope);
            string origin = "";
            if (boundary != null)
            {
                var o = boundary.InstanceOrigin;
                origin = $" instanceOrigin={o} local={(absolute - o).ToVector3():F3}";
            }
            var c = e.Container;
            NebulaLog.Info($"[HVR-674] {role} {what} entity={e.NetId} scope={scope:x} frame={(frame.IsPublic ? "public" : frame.InstanceId.ToString("x"))}" +
                $"{(ScopeFrames.IsShared(scope) ? " shared" : frame.IsPinned ? " pinned" : "")} frameOffset=({ox:F3},{oy:F3},{oz:F3}) abs={absolute} unity={e.transform.position:F3}" +
                $" container={(c != null ? c.ContainerId : "none")} containerUnity={(c != null ? c.transform.position.ToString("F3") : "-")} space={(e.Space != null ? e.Space.ContainerId : "scope")}{origin}");
        }
    }
}
