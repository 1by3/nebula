using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nebula
{
    internal sealed partial class PortalPhysics
    {
        /// <summary>How near two bodies' boxes must be, in meters, for the pair to count as touching.</summary>
        private const float PairReach = 0.05f;

        private static float SpeedOf(Tracked tracked) =>
            tracked.Info != null && tracked.Info.Body != null ? tracked.Info.Body.linearVelocity.magnitude : tracked.Entity.Motion.Velocity.magnitude;

        /// <summary>
        /// Give every pair of touching bodies, one native to each side, one scene to collide in (<c>docs/portals.md</c> D26, <see cref="PortalPairRule"/>):
        /// in the scene where the pair collides nothing changes; in the other, each body's colliders ignore the other's proxies, so a body that lands on
        /// another stops on its copy and the one under it is not pushed out by the copy of the one above.
        /// </summary>
        private void ResolvePairs(LinkRig rig)
        {
            if (!rig.HostsNear || !rig.HostsFar) return;
            if (rig.Tracked.Count > 1)
            {
                foreach (var n in rig.Tracked.Values)
                {
                    if (!n.EntityNear || n.Parts.Count == 0 || n.Entity == null) continue;
                    foreach (var f in rig.Tracked.Values)
                    {
                        if (f.EntityNear || f.Parts.Count == 0 || f.Entity == null) continue;
                        var key = (n.Entity.NetId, f.Entity.NetId);
                        bool touching = Touching(n, f);
                        rig.Pairs.TryGetValue(key, out var pair);
                        if (!touching) continue;
                        if (pair == null)
                        {
                            pair = new PairState { InNear = PortalPairRule.ResolvesNear(false, false, SpeedOf(n), SpeedOf(f), key.Item1, key.Item2) };
                            rig.Pairs.Add(key, pair);
                        }
                        pair.Stamp = _tick;
                        ApplyPair(pair, n, f);
                    }
                }
            }
            if (rig.Pairs.Count == 0) return;
            _pairGone.Clear();
            foreach (var kv in rig.Pairs) if (kv.Value.Stamp != _tick) _pairGone.Add(kv.Key);
            for (int i = 0; i < _pairGone.Count; i++)
            {
                UndoPair(rig.Pairs[_pairGone[i]]);
                rig.Pairs.Remove(_pairGone[i]);
            }
            _pairGone.Clear();
        }

        private readonly List<(ulong, ulong)> _pairGone = new List<(ulong, ulong)>();

        /// <summary>Whether the near body's colliders touch the far body's proxies in the near scene, or the far body's touch the near body's proxies in the far one.</summary>
        private static bool Touching(Tracked n, Tracked f)
        {
            var nearInfo = n.Info;
            var farInfo = f.Info;
            if (nearInfo == null || farInfo == null) return false;
            for (int i = 0; i < nearInfo.Count; i++)
            {
                var real = nearInfo.Colliders[i];
                if (real == null) continue;
                var box = real.bounds;
                box.Expand(PairReach);
                for (int p = 0; p < f.Parts.Count; p++)
                {
                    var proxy = f.Parts[p].Proxy;
                    if (proxy.Collider != null && proxy.Go.activeInHierarchy && box.Intersects(proxy.Collider.bounds)) return true;
                }
            }
            for (int i = 0; i < farInfo.Count; i++)
            {
                var real = farInfo.Colliders[i];
                if (real == null) continue;
                var box = real.bounds;
                box.Expand(PairReach);
                for (int p = 0; p < n.Parts.Count; p++)
                {
                    var proxy = n.Parts[p].Proxy;
                    if (proxy.Collider != null && proxy.Go.activeInHierarchy && box.Intersects(proxy.Collider.bounds)) return true;
                }
            }
            return false;
        }

        /// <summary>Make the other scene ignore the pair. The pairs to ignore are recomputed only when the colliders involved changed.</summary>
        private void ApplyPair(PairState pair, Tracked n, Tracked f)
        {
            // In the near scene: the near body's colliders and the far body's proxies. In the far scene: the far body's colliders and the near body's proxies.
            var realOf = pair.InNear ? f : n;       // collides where it is native... the ignoring happens in the scene the pair does NOT collide in
            var proxiesOf = pair.InNear ? n : f;
            int signature = 17;
            for (int i = 0; i < realOf.Info.Count; i++) if (realOf.Info.Colliders[i] != null) signature = signature * 31 + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(realOf.Info.Colliders[i]);
            for (int p = 0; p < proxiesOf.Parts.Count; p++) if (proxiesOf.Parts[p].Proxy.Collider != null) signature = signature * 31 + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(proxiesOf.Parts[p].Proxy.Collider);
            if (signature == pair.Signature && pair.Ignored.Count > 0) return;
            UndoPair(pair);
            pair.Signature = signature;
            for (int i = 0; i < realOf.Info.Count; i++)
            {
                var real = realOf.Info.Colliders[i];
                if (real == null) continue;
                for (int p = 0; p < proxiesOf.Parts.Count; p++)
                {
                    var proxy = proxiesOf.Parts[p].Proxy.Collider;
                    if (proxy == null) continue;
                    IgnoreCollision(real, proxy, true);
                    pair.Ignored.Add((real, proxy));
                }
            }
        }

        private static void UndoPair(PairState pair)
        {
            for (int i = 0; i < pair.Ignored.Count; i++) IgnoreCollision(pair.Ignored[i].a, pair.Ignored[i].b, false);
            pair.Ignored.Clear();
            pair.Signature = 0;
        }

        /// <summary>A proxy is going back to the pool: undo the pair ignores that name its collider first.</summary>
        private void UndoPairIgnoresInvolving(LinkRig rig, Collider collider)
        {
            if (rig.Pairs.Count == 0 || collider == null) return;
            foreach (var pair in rig.Pairs.Values)
            {
                bool involved = false;
                for (int i = 0; i < pair.Ignored.Count; i++) if (pair.Ignored[i].b == collider || pair.Ignored[i].a == collider) { involved = true; break; }
                if (involved) UndoPair(pair);
            }
        }

        /// <summary>A body left the band or the scope: forget every pair it was in.</summary>
        private void UndoPairsOf(LinkRig rig, ulong netId, bool entityNear)
        {
            if (rig.Pairs.Count == 0 || netId == 0) return;
            _pairGone.Clear();
            foreach (var kv in rig.Pairs)
                if ((entityNear ? kv.Key.near : kv.Key.far) == netId) _pairGone.Add(kv.Key);
            for (int i = 0; i < _pairGone.Count; i++)
            {
                UndoPair(rig.Pairs[_pairGone[i]]);
                rig.Pairs.Remove(_pairGone[i]);
            }
            _pairGone.Clear();
        }
    }
}
