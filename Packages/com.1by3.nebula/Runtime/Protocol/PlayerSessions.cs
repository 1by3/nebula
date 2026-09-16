using System;
using System.Collections.Generic;

namespace Nebula
{
    /// <summary>
    /// A worker's view of which gateway currently speaks for each player session, and how new that claim is. Every
    /// session (<see cref="WelcomeMsg.ClientId"/>) is claimed by a gateway through <see cref="SpawnPlayerMsg"/> with
    /// a connection generation; a later claim with a higher generation moves the session to that gateway (the client
    /// reconnected elsewhere), and anything an older gateway says about the session afterwards (a despawn, inputs)
    /// is stale and ignored. When a gateway link is lost its sessions are orphaned: kept for a grace period so the
    /// same or another gateway can reclaim them, then handed back to the caller to despawn.
    /// Plain C#, no Unity or transport types: the worker feeds it gateway keys (id + incarnation) and a clock.
    /// </summary>
    public sealed class PlayerSessions
    {
        public sealed class Session
        {
            public ulong Id;
            public ulong Generation;
            /// <summary>The gateway that speaks for the session: "<gatewayId>#<incarnation>".</summary>
            public string Gateway = "";
            public bool Orphaned;
            public double OrphanedAt;
        }

        public enum Claim
        {
            /// <summary>First time this worker hears of the session.</summary>
            New,
            /// <summary>The same gateway, same generation: a repeat (the gateway re-announcing after a link blip).</summary>
            Repeat,
            /// <summary>A newer generation: the session now lives on the claiming gateway.</summary>
            Reclaimed,
            /// <summary>An older generation than the one held: the claimant lost the client to another gateway.</summary>
            Stale,
        }

        private readonly Dictionary<ulong, Session> _sessions = new Dictionary<ulong, Session>();
        private readonly List<ulong> _scratch = new List<ulong>();

        public int Count => _sessions.Count;
        public int OrphanCount { get { int n = 0; foreach (var s in _sessions.Values) if (s.Orphaned) n++; return n; } }

        public static string GatewayKey(string gatewayId, uint incarnation) => (gatewayId ?? "") + "#" + incarnation;

        public bool TryGet(ulong id, out Session session) => _sessions.TryGetValue(id, out session);

        /// <summary>A gateway claims the session with <paramref name="generation"/>. See <see cref="Claim"/> for what happened.</summary>
        public Claim Register(ulong id, ulong generation, string gateway)
        {
            if (!_sessions.TryGetValue(id, out var s))
            {
                _sessions[id] = new Session { Id = id, Generation = generation, Gateway = gateway ?? "" };
                return Claim.New;
            }
            if (generation < s.Generation) return Claim.Stale;
            bool same = generation == s.Generation && s.Gateway == (gateway ?? "");
            s.Generation = generation;
            s.Gateway = gateway ?? "";
            s.Orphaned = false;
            return same ? Claim.Repeat : Claim.Reclaimed;
        }

        /// <summary>
        /// Seed a session this worker inherited with an entity (a handover carries the owner's session with it), so
        /// the gateway's inputs are accepted at once. Never lowers a generation already known.
        /// </summary>
        public void Adopt(ulong id, ulong generation, string gateway)
        {
            if (id == 0) return;
            if (_sessions.TryGetValue(id, out var s))
            {
                if (generation < s.Generation) return;
                s.Generation = generation; s.Gateway = gateway ?? ""; s.Orphaned = false;
                return;
            }
            _sessions[id] = new Session { Id = id, Generation = generation, Gateway = gateway ?? "" };
        }

        /// <summary>
        /// A gateway says the session's link ended. True when that is current news: the session becomes an orphan
        /// and <see cref="Expire"/> hands it back after the grace unless a claim renews it. False when a newer claim
        /// exists elsewhere, so the release is stale and must be ignored. An unknown session is treated as current.
        /// </summary>
        public bool Release(ulong id, ulong generation, double now)
        {
            if (!_sessions.TryGetValue(id, out var s))
            {
                _sessions[id] = new Session { Id = id, Generation = generation, Orphaned = true, OrphanedAt = now };
                return true;
            }
            if (generation < s.Generation) return false;
            s.Generation = generation;
            s.Orphaned = true;
            s.OrphanedAt = now;
            return true;
        }

        /// <summary>
        /// May <paramref name="gateway"/> drive the session right now (inputs, server RPCs)? True for the claiming
        /// gateway and for a session this worker has never had a claim for (it learns the gateway lazily then).
        /// </summary>
        public bool Accept(ulong id, string gateway)
        {
            if (!_sessions.TryGetValue(id, out var s))
            {
                _sessions[id] = new Session { Id = id, Generation = 0, Gateway = gateway ?? "" };
                return true;
            }
            return s.Gateway == (gateway ?? "");
        }

        public void Remove(ulong id) => _sessions.Remove(id);

        /// <summary>The gateway's link dropped: its sessions wait for a reclaim until <see cref="Expire"/> gives up on them.</summary>
        public void GatewayLost(string gateway, double now)
        {
            foreach (var s in _sessions.Values)
            {
                if (s.Gateway != gateway || s.Orphaned) continue;
                s.Orphaned = true;
                s.OrphanedAt = now;
            }
        }

        /// <summary>The same gateway (same incarnation) is back: its sessions are no longer orphans.</summary>
        public void GatewayReturned(string gateway)
        {
            foreach (var s in _sessions.Values) if (s.Gateway == gateway) s.Orphaned = false;
        }

        /// <summary>Drop every orphan older than <paramref name="graceSeconds"/> and list its id, so the caller despawns the pawn.</summary>
        public void Expire(double now, double graceSeconds, List<ulong> expired)
        {
            _scratch.Clear();
            foreach (var s in _sessions.Values) if (s.Orphaned && now - s.OrphanedAt >= graceSeconds) _scratch.Add(s.Id);
            foreach (var id in _scratch) { _sessions.Remove(id); expired.Add(id); }
        }
    }
}
