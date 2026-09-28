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
            /// <summary>
            /// The orphan is a release: the gateway said the client's link ended. Only a newer claim renews it. An
            /// orphan that is not released only lost its gateway's link to this worker, and the same gateway coming
            /// back renews it too (<see cref="GatewayReturned"/>).
            /// </summary>
            public bool Released;
            /// <summary>
            /// The session is over and waits for nobody: the player said goodbye or was kicked (<see cref="End"/>).
            /// Always a release too. <see cref="Expire"/> hands it back at once, whatever the grace.
            /// </summary>
            public bool Ended;
            public double OrphanedAt;

            /// <summary>Why the session is an orphan, or <see cref="OrphanKind.None"/> while a gateway speaks for it.</summary>
            public OrphanKind Orphan => !Orphaned ? OrphanKind.None : Ended ? OrphanKind.Ended : Released ? OrphanKind.Released : OrphanKind.GatewayLost;
        }

        /// <summary>Why a session is waiting for a reclaim. Carried in a handover (<see cref="AuthorityTransferMsg.SessionOrphan"/>).</summary>
        public enum OrphanKind : byte
        {
            /// <summary>A gateway speaks for the session.</summary>
            None = 0,
            /// <summary>The gateway said the client's link ended (<see cref="PlayerSessions.Release"/>).</summary>
            Released = 1,
            /// <summary>The gateway's link to the worker dropped (<see cref="PlayerSessions.GatewayLost"/>).</summary>
            GatewayLost = 2,
            /// <summary>
            /// The session is over: the player said goodbye or was kicked (<see cref="PlayerSessions.End"/>). The pawn
            /// is removed as soon as the worker that owns it hears of it, with no reclaim grace.
            /// </summary>
            Ended = 3,
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
        public Claim Register(ulong id, ulong generation, string gateway) => Register(id, generation, gateway, out _);

        /// <summary>
        /// A gateway claims the session with <paramref name="generation"/>. See <see cref="Claim"/> for what
        /// happened. <paramref name="previousGateway"/> is the gateway that spoke for the session until now (empty
        /// for <see cref="Claim.New"/>), so a <see cref="Claim.Reclaimed"/> can be reported to the gateway that
        /// just lost the session.
        /// </summary>
        public Claim Register(ulong id, ulong generation, string gateway, out string previousGateway)
        {
            previousGateway = "";
            if (!_sessions.TryGetValue(id, out var s))
            {
                _sessions[id] = new Session { Id = id, Generation = generation, Gateway = gateway ?? "" };
                return Claim.New;
            }
            if (generation < s.Generation) return Claim.Stale;
            bool same = generation == s.Generation && s.Gateway == (gateway ?? "");
            previousGateway = s.Gateway;
            s.Generation = generation;
            s.Gateway = gateway ?? "";
            s.Orphaned = false;
            s.Released = false;
            s.Ended = false;
            return same ? Claim.Repeat : Claim.Reclaimed;
        }

        /// <summary>
        /// Seed a session this worker inherited with an entity (a handover carries the owner's session with it), so
        /// the gateway's inputs are accepted at once. Never lowers a generation already known.
        /// </summary>
        public void Adopt(ulong id, ulong generation, string gateway) => Adopt(id, generation, gateway, OrphanKind.None, 0, 0, 0);

        /// <summary>
        /// Seed a session this worker inherited with an entity, in the state the sender had it. An orphan stays an
        /// orphan, with <paramref name="reclaimRemaining"/> seconds of its grace left: the sender measured that on
        /// its own clock, so the countdown resumes here rather than restarting, and no absolute time crosses
        /// between two processes whose clocks differ. <paramref name="now"/> and <paramref name="graceSeconds"/>
        /// are this worker's, the ones <see cref="Expire"/> is called with. Never lowers a generation already
        /// known, and a release this worker already heard for the same generation is kept: it is newer than the
        /// sender's view (the gateway followed the entity here before the handover arrived).
        /// </summary>
        public void Adopt(ulong id, ulong generation, string gateway, OrphanKind orphan, double reclaimRemaining, double now, double graceSeconds)
        {
            if (id == 0) return;
            if (!_sessions.TryGetValue(id, out var s))
            {
                s = new Session { Id = id, Generation = generation };
                _sessions[id] = s;
            }
            else if (generation < s.Generation) return;
            bool keepRelease = generation == s.Generation && s.Released;
            bool keepEnd = generation == s.Generation && s.Ended;
            s.Generation = generation;
            s.Gateway = gateway ?? "";
            if (orphan == OrphanKind.None)
            {
                if (keepRelease) return;
                s.Orphaned = false;
                s.Released = false;
                s.Ended = false;
                return;
            }
            // Backdated so that Expire finds the same time left as the sender did.
            double orphanedAt = now - Math.Max(0, graceSeconds - Math.Max(0, reclaimRemaining));
            s.OrphanedAt = s.Orphaned ? Math.Min(s.OrphanedAt, orphanedAt) : orphanedAt;
            s.Orphaned = true;
            s.Ended = keepEnd || orphan == OrphanKind.Ended;
            s.Released = keepRelease || s.Ended || orphan == OrphanKind.Released;
        }

        /// <summary>Seconds left before <see cref="Expire"/> gives up on an orphaned session; 0 once it is due, and for a session that is not an orphan.</summary>
        public static double ReclaimRemaining(Session session, double now, double graceSeconds) =>
            session == null || !session.Orphaned || session.Ended ? 0 : Math.Max(0, graceSeconds - (now - session.OrphanedAt));

        /// <summary>
        /// A gateway says the session's link ended. True when that is current news: the session becomes an orphan
        /// and <see cref="Expire"/> hands it back after the grace unless a claim renews it. False when a newer claim
        /// exists elsewhere, so the release is stale and must be ignored. An unknown session is treated as current.
        /// </summary>
        public bool Release(ulong id, ulong generation, double now)
        {
            if (!_sessions.TryGetValue(id, out var s))
            {
                _sessions[id] = new Session { Id = id, Generation = generation, Orphaned = true, Released = true, OrphanedAt = now };
                return true;
            }
            if (generation < s.Generation) return false;
            s.Generation = generation;
            s.Orphaned = true;
            s.Released = true;
            s.OrphanedAt = now;
            return true;
        }

        /// <summary>
        /// A gateway says the session is over, not merely disconnected: the player said goodbye or was kicked. Like
        /// <see cref="Release"/>, and fenced the same way, but <see cref="Expire"/> hands the session back on its next
        /// pass instead of after the grace. Returns false for a stale end (a newer claim exists elsewhere).
        /// </summary>
        public bool End(ulong id, ulong generation, double now)
        {
            if (!Release(id, generation, now)) return false;
            _sessions[id].Ended = true;
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

        /// <summary>The same gateway (same incarnation) is back: its sessions are no longer orphans, except the ones it released (their clients left).</summary>
        public void GatewayReturned(string gateway)
        {
            foreach (var s in _sessions.Values) if (s.Gateway == gateway && !s.Released) s.Orphaned = false;
        }

        /// <summary>
        /// How long, at least, an ended session is kept when the caller does not hold its pawn: long enough for a
        /// handover that was in flight when the end arrived to land, so the end is still here to remove the pawn.
        /// </summary>
        public const double EndedWithoutPawnSeconds = 10;

        /// <summary>
        /// Drop every orphan older than <paramref name="graceSeconds"/>, and every ended session, and list its id, so the
        /// caller despawns the pawn. Every ended session counts as one whose pawn the caller holds.
        /// </summary>
        public void Expire(double now, double graceSeconds, List<ulong> expired) => Expire(now, graceSeconds, expired, null);

        /// <summary>
        /// Drop every orphan older than <paramref name="graceSeconds"/> and list its id, so the caller despawns the pawn.
        /// An ended session (<see cref="End"/>) goes at once when <paramref name="holdsPawn"/> says the caller holds its
        /// pawn (null: always). One whose pawn is elsewhere, or not here yet, waits the grace, and at least
        /// <see cref="EndedWithoutPawnSeconds"/>: the pawn may be arriving in a handover, and the end must still be
        /// here to remove it when it does.
        /// </summary>
        public void Expire(double now, double graceSeconds, List<ulong> expired, Func<ulong, bool> holdsPawn)
        {
            _scratch.Clear();
            foreach (var s in _sessions.Values)
            {
                if (!s.Orphaned) continue;
                double waited = now - s.OrphanedAt;
                bool due = s.Ended
                    ? holdsPawn == null || holdsPawn(s.Id) || waited >= Math.Max(graceSeconds, EndedWithoutPawnSeconds)
                    : waited >= graceSeconds;
                if (due) _scratch.Add(s.Id);
            }
            foreach (var id in _scratch) { _sessions.Remove(id); expired.Add(id); }
        }
    }
}
