using System;
using System.Collections.Generic;

namespace Nebula
{
    /// <summary>
    /// Where a <c>[ClientRequestRpc]</c> call ended up. A caller that asked for a reply
    /// (<c>NetworkBehaviour.ClientRequestWithReply</c>) receives exactly one of these; a fire-and-forget request is
    /// decided by the same rules and only logs a rejection where it is decided. The rejections up to
    /// <see cref="HopLimit"/> travel on the wire; <see cref="TimedOut"/> and <see cref="Disconnected"/> are decided on
    /// the client and never sent.
    /// </summary>
    public enum ClientRequestOutcome : byte
    {
        /// <summary>Not decided yet. Never sent on the wire.</summary>
        Pending = 0,
        /// <summary>The worker with authority ran the handler once. The handler may still have refused the request on its own terms: see <see cref="ClientRequestResult.Code"/>.</summary>
        Accepted = 1,
        /// <summary>The arguments were larger than <see cref="NebulaConfig.ClientRequestMaxBytes"/>. Dropped at the gateway.</summary>
        RejectedTooLarge = 2,
        /// <summary>The gateway holds no entity with that id, or no worker holds it any more (it despawned).</summary>
        RejectedUnknownEntity = 3,
        /// <summary>The entity exists, but not in the sender's interest set: the client does not hold a replica of it. Dropped at the gateway.</summary>
        RejectedNotInInterest = 4,
        /// <summary>The sender is over <see cref="NebulaConfig.ClientRequestsPerSecond"/> (with <see cref="NebulaConfig.ClientRequestBurst"/>). Dropped at the gateway.</summary>
        RejectedClientRateLimit = 5,
        /// <summary>The entity is over <see cref="NebulaConfig.EntityRequestsPerSecond"/> (with <see cref="NebulaConfig.EntityRequestBurst"/>), counted across every sender. Dropped at the gateway.</summary>
        RejectedEntityRateLimit = 6,
        /// <summary>No connected worker could take the request: the owner of the entity is not connected to the gateway, or a worker that should forward it has no link to the new owner.</summary>
        RejectedUnreachable = 7,
        /// <summary>The behaviour index or method does not exist on the entity, or the method is not a <c>[ClientRequestRpc]</c>. Only that attribute can be reached by a client request.</summary>
        RejectedNoHandler = 8,
        /// <summary>The arguments did not decode as the method's parameters.</summary>
        RejectedBadArguments = 9,
        /// <summary>The handler threw. The exception is logged on the worker; the client is told only that.</summary>
        HandlerThrew = 10,
        /// <summary>The request was forwarded <see cref="NebulaConfig.AuthorityCallMaxHops"/> times, after the entity changed worker, without reaching its authority.</summary>
        RejectedHopLimit = 11,
        /// <summary>Decided on the client: no reply arrived before the deadline. The request may or may not have been applied.</summary>
        TimedOut = 12,
        /// <summary>Decided on the client: the connection was not up when the request was made, or went down while it waited.</summary>
        Disconnected = 13,
    }

    /// <summary>The outcome of one <c>ClientRequestWithReply</c> call, handed to its callback exactly once.</summary>
    public readonly struct ClientRequestResult
    {
        /// <summary>The call id the client minted; 0 for a call that never left the client.</summary>
        public uint CallId { get; }
        /// <summary>What happened to the request.</summary>
        public ClientRequestOutcome Outcome { get; }
        /// <summary>
        /// A number the handler chose, through <see cref="ClientRequestContext.ReplyCode"/>: its own verdict ("allowed",
        /// "too far", "not yours"). 0 unless the handler set one and <see cref="Outcome"/> is
        /// <see cref="ClientRequestOutcome.Accepted"/>. Not a place for data: the reply is two bytes of verdict, and a handler that needs
        /// to answer with more sends a <c>ClientRpc</c> or an owner call.
        /// </summary>
        public ushort Code { get; }
        /// <summary>True when <see cref="Outcome"/> is <see cref="ClientRequestOutcome.Accepted"/>.</summary>
        public bool Succeeded => Outcome == ClientRequestOutcome.Accepted;

        public ClientRequestResult(uint callId, ClientRequestOutcome outcome, ushort code = 0)
        {
            CallId = callId;
            Outcome = outcome;
            Code = code;
        }

        public override string ToString() => $"{Outcome} (request {CallId}, code {Code})";
    }

    /// <summary>
    /// A client's token bucket: <c>burst</c> requests at once, refilled at <c>rate</c> per second. A plain struct so a
    /// gateway keeps one in every client's record and one per entity without allocating.
    /// </summary>
    public struct RequestBucket
    {
        /// <summary>Tokens left as of <see cref="Updated"/>.</summary>
        public double Tokens;
        /// <summary>The time of the last refill, in the caller's seconds.</summary>
        public double Updated;
        /// <summary>False until the first take, which starts the bucket full.</summary>
        public bool Started;

        /// <summary>
        /// Take one token at time <paramref name="now"/>. A <paramref name="rate"/> of zero or less is "no limit" and
        /// always succeeds. A <paramref name="burst"/> under 1 is treated as 1.
        /// </summary>
        public bool TryTake(double now, double rate, double burst)
        {
            if (rate <= 0) return true;
            if (burst < 1) burst = 1;
            if (!Started) { Started = true; Tokens = burst; Updated = now; }
            else if (now > Updated)
            {
                Tokens = Math.Min(burst, Tokens + (now - Updated) * rate);
                Updated = now;
            }
            if (Tokens < 1) return false;
            Tokens -= 1;
            return true;
        }

        /// <summary>Whether the bucket would be full again at <paramref name="now"/>: nothing to remember about it.</summary>
        public bool IsIdle(double now, double rate, double burst, double idleSeconds)
            => !Started || rate <= 0 || (now - Updated >= idleSeconds && Tokens + (now - Updated) * rate >= Math.Max(1, burst));
    }

    /// <summary>
    /// The gateway's admission rules for a client request, in one place and with no engine types, so the gateway, the
    /// standalone services and the tests run the same code. The rules, in the order they are checked; the first that
    /// fails decides:
    /// <list type="number">
    /// <item>The arguments are larger than <see cref="MaxBytes"/>: <see cref="ClientRequestOutcome.RejectedTooLarge"/>.</item>
    /// <item>The gateway has no such entity: <see cref="ClientRequestOutcome.RejectedUnknownEntity"/>.</item>
    /// <item>The entity is not in the sender's interest set: <see cref="ClientRequestOutcome.RejectedNotInInterest"/>.</item>
    /// <item>The sender is over its rate: <see cref="ClientRequestOutcome.RejectedClientRateLimit"/>. A request that
    /// gets this far spends one of the sender's tokens, whatever happens next, so a client cannot flood a busy
    /// entity for free.</item>
    /// <item>The entity is over its rate, across every sender: <see cref="ClientRequestOutcome.RejectedEntityRateLimit"/>.</item>
    /// </list>
    /// Size, existence and interest are checked before either rate so a request that was never going to be delivered
    /// does not spend a token or create a bucket for an entity the sender cannot see.
    /// </summary>
    public sealed class ClientRequestGate
    {
        private readonly Dictionary<ulong, RequestBucket> _entities = new Dictionary<ulong, RequestBucket>();
        private readonly List<ulong> _scratch = new List<ulong>();

        /// <summary>Largest argument payload accepted, in bytes.</summary>
        public int MaxBytes { get; }
        /// <summary>Per client: requests per second (0 or less: unlimited).</summary>
        public float ClientRate { get; }
        public int ClientBurst { get; }
        /// <summary>Per entity, across every sender: requests per second (0 or less: unlimited).</summary>
        public float EntityRate { get; }
        public int EntityBurst { get; }

        /// <summary>Requests admitted since the gate was created.</summary>
        public long Admitted { get; private set; }
        /// <summary>Requests refused since the gate was created, by outcome (index = <see cref="ClientRequestOutcome"/>).</summary>
        public long[] Refused { get; } = new long[16];
        /// <summary>Entities with a bucket right now.</summary>
        public int TrackedEntities => _entities.Count;

        public ClientRequestGate(int maxBytes, float clientRate, int clientBurst, float entityRate, int entityBurst)
        {
            MaxBytes = Math.Max(1, maxBytes);
            ClientRate = clientRate;
            ClientBurst = Math.Max(1, clientBurst);
            EntityRate = entityRate;
            EntityBurst = Math.Max(1, entityBurst);
        }

        /// <summary>Build a gate from the mesh configuration.</summary>
        public static ClientRequestGate From(NebulaConfig config) => new ClientRequestGate(
            config.ClientRequestMaxBytes, config.ClientRequestsPerSecond, config.ClientRequestBurst,
            config.EntityRequestsPerSecond, config.EntityRequestBurst);

        /// <summary>
        /// Decide one request. Returns <see cref="ClientRequestOutcome.Accepted"/> when it may be routed, otherwise the
        /// reason to drop it. <paramref name="sender"/> is the sending client's bucket, kept in its record.
        /// </summary>
        public ClientRequestOutcome Admit(int argBytes, bool entityKnown, bool inInterest, ref RequestBucket sender, ulong netId, double now)
        {
            var why = Decide(argBytes, entityKnown, inInterest, ref sender, netId, now);
            if (why == ClientRequestOutcome.Accepted) Admitted++;
            else Refused[(int)why]++;
            return why;
        }

        private ClientRequestOutcome Decide(int argBytes, bool entityKnown, bool inInterest, ref RequestBucket sender, ulong netId, double now)
        {
            if (argBytes > MaxBytes) return ClientRequestOutcome.RejectedTooLarge;
            if (!entityKnown) return ClientRequestOutcome.RejectedUnknownEntity;
            if (!inInterest) return ClientRequestOutcome.RejectedNotInInterest;
            if (!sender.TryTake(now, ClientRate, ClientBurst)) return ClientRequestOutcome.RejectedClientRateLimit;
            if (EntityRate > 0)
            {
                _entities.TryGetValue(netId, out var bucket);
                bool ok = bucket.TryTake(now, EntityRate, EntityBurst);
                _entities[netId] = bucket;
                if (!ok) return ClientRequestOutcome.RejectedEntityRateLimit;
            }
            return ClientRequestOutcome.Accepted;
        }

        /// <summary>Forget the buckets of entities nobody has called for <paramref name="idleSeconds"/>. Call every few seconds.</summary>
        public void Sweep(double now, double idleSeconds = 30)
        {
            if (_entities.Count == 0) return;
            _scratch.Clear();
            foreach (var kv in _entities)
                if (kv.Value.IsIdle(now, EntityRate, EntityBurst, idleSeconds)) _scratch.Add(kv.Key);
            for (int i = 0; i < _scratch.Count; i++) _entities.Remove(_scratch[i]);
            _scratch.Clear();
        }

        /// <summary>Forget one entity's bucket (it despawned).</summary>
        public void Forget(ulong netId) => _entities.Remove(netId);
    }

    /// <summary>
    /// The rule a worker applies to a routed client request, with no engine types. The gateway sends a request to the
    /// worker it believes owns the entity; after a handover that worker holds a ghost and passes the request on, at most
    /// <c>maxHops</c> times (<see cref="NebulaConfig.AuthorityCallMaxHops"/>), exactly as an <c>AuthorityRpc</c> is.
    /// </summary>
    public static class ClientRequestRouting
    {
        /// <param name="known">This worker holds a copy of the entity (authoritative or ghost).</param>
        /// <param name="hasAuthority">This worker has authority over it.</param>
        /// <param name="canForward">There is a connected worker to pass the request to, other than the sender.</param>
        /// <param name="hops">How many workers have forwarded the request before this one.</param>
        /// <param name="maxHops">The forwards allowed.</param>
        public static AuthorityCallDecision Decide(bool known, bool hasAuthority, bool canForward, byte hops, int maxHops)
        {
            if (!known) return AuthorityCallDecision.Reject(AuthorityCallOutcome.RejectedUnknownEntity);
            if (hasAuthority) return AuthorityCallDecision.ApplyIt;
            if (hops >= Math.Max(0, maxHops)) return AuthorityCallDecision.Reject(AuthorityCallOutcome.RejectedHopLimit);
            if (!canForward) return AuthorityCallDecision.Reject(AuthorityCallOutcome.RejectedUnreachable);
            return AuthorityCallDecision.ForwardIt;
        }

        /// <summary>The wire outcome for a worker's rejection from <see cref="Decide"/>.</summary>
        public static ClientRequestOutcome OutcomeOf(AuthorityCallOutcome rejection)
        {
            switch (rejection)
            {
                case AuthorityCallOutcome.RejectedUnknownEntity: return ClientRequestOutcome.RejectedUnknownEntity;
                case AuthorityCallOutcome.RejectedHopLimit: return ClientRequestOutcome.RejectedHopLimit;
                default: return ClientRequestOutcome.RejectedUnreachable;
            }
        }
    }

    /// <summary>
    /// The sender's side of the reply channel: the requests a client made with a reply wanted and has not heard back
    /// about. Each settles exactly once, with the reply that arrives, with
    /// <see cref="ClientRequestOutcome.TimedOut"/> when <see cref="Expire"/> passes its deadline, or with
    /// <see cref="ClientRequestOutcome.Disconnected"/> from <see cref="FailAll"/>.
    /// </summary>
    public sealed class ClientRequestTracker
    {
        private sealed class Pending
        {
            public uint CallId;
            public double Deadline;
            public Action<ClientRequestResult> OnDone;
        }

        private readonly Dictionary<uint, Pending> _pending = new Dictionary<uint, Pending>();
        private readonly List<Pending> _scratch = new List<Pending>();
        private uint _next;

        /// <summary>Requests awaiting a reply.</summary>
        public int PendingCount => _pending.Count;
        /// <summary>Replies that arrived for a request no longer pending (settled, timed out, or never tracked).</summary>
        public long LateReplies { get; private set; }

        /// <summary>A fresh call id: never 0, and never one still pending.</summary>
        public uint Mint()
        {
            do { _next++; if (_next == 0) _next = 1; } while (_pending.ContainsKey(_next));
            return _next;
        }

        /// <summary>Wait for the reply to <paramref name="callId"/>; <paramref name="onDone"/> runs once, at the reply or at <paramref name="deadline"/>.</summary>
        public void Track(uint callId, double deadline, Action<ClientRequestResult> onDone)
        {
            if (onDone == null) throw new ArgumentNullException(nameof(onDone));
            _pending[callId] = new Pending { CallId = callId, Deadline = deadline, OnDone = onDone };
        }

        /// <summary>Settle <paramref name="callId"/>. False when it was not pending (counted in <see cref="LateReplies"/>).</summary>
        public bool Complete(uint callId, in ClientRequestResult result)
        {
            if (!_pending.TryGetValue(callId, out var p))
            {
                LateReplies++;
                return false;
            }
            _pending.Remove(callId);
            p.OnDone(result);
            return true;
        }

        public bool IsPending(uint callId) => _pending.ContainsKey(callId);

        /// <summary>Settle every request whose deadline is at or before <paramref name="now"/> as <see cref="ClientRequestOutcome.TimedOut"/>. Call once a frame.</summary>
        public void Expire(double now)
        {
            if (_pending.Count == 0) return;
            _scratch.Clear();
            foreach (var kv in _pending) if (kv.Value.Deadline <= now) _scratch.Add(kv.Value);
            for (int i = 0; i < _scratch.Count; i++)
            {
                var p = _scratch[i];
                if (!_pending.Remove(p.CallId)) continue; // a callback earlier in this pass settled it
                p.OnDone(new ClientRequestResult(p.CallId, ClientRequestOutcome.TimedOut));
            }
            _scratch.Clear();
        }

        /// <summary>Settle every pending request as <paramref name="outcome"/> (the connection went away).</summary>
        public void FailAll(ClientRequestOutcome outcome)
        {
            if (_pending.Count == 0) return;
            _scratch.Clear();
            foreach (var kv in _pending) _scratch.Add(kv.Value);
            _pending.Clear();
            for (int i = 0; i < _scratch.Count; i++) _scratch[i].OnDone(new ClientRequestResult(_scratch[i].CallId, outcome));
            _scratch.Clear();
        }
    }
}
