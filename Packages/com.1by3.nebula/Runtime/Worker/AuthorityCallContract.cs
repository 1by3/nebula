using System;
using System.Collections.Generic;

namespace Nebula
{
    /// <summary>
    /// Where an <c>AuthorityRpc</c> call ended up. A caller that asked for a reply
    /// (<c>NetworkBehaviour.AuthorityRpcWithReply</c>) receives
    /// exactly one of these; a fire-and-forget call is decided by the same rules and only logs a rejection on the
    /// worker that made it. See the design in <c>docs/cross-worker-calls.md</c>.
    /// </summary>
    public enum AuthorityCallOutcome : byte
    {
        /// <summary>Not decided yet. Never sent on the wire.</summary>
        Pending = 0,
        /// <summary>The worker with authority ran the method once. The handler may still have thrown or refused the claim.</summary>
        Accepted = 1,
        /// <summary>
        /// The call's entity epoch was more than <see cref="NebulaConfig.AuthorityCallMaxHops"/> authority changes
        /// behind the entity, or ahead of it: the caller's copy was not the entity the authority holds.
        /// </summary>
        RejectedStaleEpoch = 2,
        /// <summary>No worker on the call's path holds the entity: it was despawned, or never existed there.</summary>
        RejectedUnknownEntity = 3,
        /// <summary>The call was forwarded <see cref="NebulaConfig.AuthorityCallMaxHops"/> times without reaching the authority.</summary>
        RejectedHopLimit = 4,
        /// <summary>A call with this id was already applied on this worker. At-most-once held; nothing ran a second time.</summary>
        RejectedDuplicate = 5,
        /// <summary>The worker that should apply or forward the call has no connection to the entity's owner.</summary>
        RejectedUnreachable = 6,
        /// <summary>No reply arrived before the caller's deadline. The call may or may not have been applied.</summary>
        TimedOut = 7,
    }

    /// <summary>The outcome of one <c>AuthorityRpcWithReply</c> call, handed to its callback exactly once.</summary>
    public readonly struct AuthorityCallResult
    {
        /// <summary>The call id the sender minted (<see cref="AuthorityCallId"/>); 0 for a call applied locally without a wire trip.</summary>
        public ulong CallId { get; }
        /// <summary>What happened to the call.</summary>
        public AuthorityCallOutcome Outcome { get; }
        /// <summary>The entity's epoch on the worker that decided the outcome; 0 when that worker did not hold the entity.</summary>
        public uint TargetEpoch { get; }
        /// <summary>How many times the call was forwarded before it was decided.</summary>
        public byte Hops { get; }
        /// <summary>True when <see cref="Outcome"/> is <see cref="AuthorityCallOutcome.Accepted"/>.</summary>
        public bool Succeeded => Outcome == AuthorityCallOutcome.Accepted;

        public AuthorityCallResult(ulong callId, AuthorityCallOutcome outcome, uint targetEpoch, byte hops)
        {
            CallId = callId;
            Outcome = outcome;
            TargetEpoch = targetEpoch;
            Hops = hops;
        }

        public override string ToString() => $"{Outcome} (call {CallId:x}, epoch {TargetEpoch}, {Hops} hops)";
    }

    /// <summary>
    /// The identity of one cross-worker call: the sending worker's index in the top 16 bits and a per-sender
    /// sequence in the low 48, the same shape as an entity <c>NetId</c>. Forwarding keeps the id, so
    /// every worker on the path and the reply agree on which call they mean.
    /// </summary>
    public static class AuthorityCallId
    {
        /// <summary>Bits of the sequence part; the worker index sits above it.</summary>
        public const int SequenceBits = 48;
        private const ulong SequenceMask = (1UL << SequenceBits) - 1;

        /// <summary>Compose a call id from the sender's worker index and its sequence number.</summary>
        public static ulong Make(ushort workerIndex, ulong sequence) => ((ulong)workerIndex << SequenceBits) | (sequence & SequenceMask);

        /// <summary>The worker that minted <paramref name="callId"/>: where a reply goes.</summary>
        public static ushort WorkerIndexOf(ulong callId) => (ushort)(callId >> SequenceBits);

        /// <summary>The sequence part of <paramref name="callId"/>.</summary>
        public static ulong SequenceOf(ulong callId) => callId & SequenceMask;

        /// <summary>
        /// Where a sender's sequence starts for one run of its process: the low 16 bits of its incarnation, shifted
        /// above 32 bits of room. A worker that restarts with the same index therefore mints ids a previous run
        /// never used, and a receiver's ledger cannot mistake its first calls for duplicates of the old run's.
        /// </summary>
        public static ulong InitialSequence(uint incarnation) => ((ulong)(incarnation & 0xFFFF)) << 32;
    }

    /// <summary>
    /// The call ids a worker has applied, so a call that arrives twice runs once (design D5). Bounded two ways: at
    /// most <see cref="Capacity"/> ids are remembered, oldest forgotten first, and an id older than
    /// <see cref="TtlTicks"/> ticks is forgotten on the next <see cref="Expire"/>. A duplicate of a forgotten call
    /// would be applied again; the bounds say how long "at most once" is enforced for.
    /// </summary>
    public sealed class AuthorityCallLedger
    {
        /// <summary>Ids remembered per worker by default: 4096, a few seconds of busy cross-worker traffic.</summary>
        public const int DefaultCapacity = 4096;
        /// <summary>Ticks an id is remembered by default: 600, ten seconds at 60 ticks per second.</summary>
        public const uint DefaultTtlTicks = 600;

        private readonly Dictionary<ulong, uint> _appliedAt;
        private readonly Queue<ulong> _order;

        /// <summary>The most ids remembered at once.</summary>
        public int Capacity { get; }
        /// <summary>How many ticks an id stays remembered.</summary>
        public uint TtlTicks { get; }
        /// <summary>Ids currently remembered.</summary>
        public int Count => _appliedAt.Count;
        /// <summary>Calls refused as duplicates since the ledger was created.</summary>
        public long Duplicates { get; private set; }
        /// <summary>Ids forgotten because the ledger was full, since it was created.</summary>
        public long Evictions { get; private set; }

        public AuthorityCallLedger(int capacity = DefaultCapacity, uint ttlTicks = DefaultTtlTicks)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            Capacity = capacity;
            TtlTicks = ttlTicks;
            _appliedAt = new Dictionary<ulong, uint>(capacity);
            _order = new Queue<ulong>(capacity);
        }

        /// <summary>
        /// Record that <paramref name="callId"/> is being applied at <paramref name="tick"/>. False when the id is
        /// already remembered: the caller must not apply it. Recording past <see cref="Capacity"/> forgets the
        /// oldest id.
        /// </summary>
        public bool TryRecord(ulong callId, uint tick)
        {
            if (_appliedAt.ContainsKey(callId))
            {
                Duplicates++;
                return false;
            }
            while (_appliedAt.Count >= Capacity)
            {
                _appliedAt.Remove(_order.Dequeue());
                Evictions++;
            }
            _appliedAt[callId] = tick;
            _order.Enqueue(callId);
            return true;
        }

        /// <summary>Whether <paramref name="callId"/> is remembered as applied.</summary>
        public bool Contains(ulong callId) => _appliedAt.ContainsKey(callId);

        /// <summary>Forget every id recorded more than <see cref="TtlTicks"/> ticks before <paramref name="tick"/>. Call once a tick.</summary>
        public void Expire(uint tick)
        {
            while (_order.Count > 0)
            {
                ulong oldest = _order.Peek();
                if (!_appliedAt.TryGetValue(oldest, out uint at)) { _order.Dequeue(); continue; }
                if (tick - at <= TtlTicks) return;
                _order.Dequeue();
                _appliedAt.Remove(oldest);
            }
        }

        /// <summary>Forget everything.</summary>
        public void Clear()
        {
            _appliedAt.Clear();
            _order.Clear();
        }
    }

    /// <summary>What a worker does with an incoming call.</summary>
    public enum AuthorityCallAction : byte
    {
        /// <summary>Run the method here, once.</summary>
        Apply,
        /// <summary>Send the call on, with its hop count raised by one, to the worker this one believes has authority.</summary>
        Forward,
        /// <summary>Drop the call; <see cref="AuthorityCallDecision.Outcome"/> says why, and the sender is told when it asked.</summary>
        Reject,
    }

    /// <summary>The decision for one incoming call: the action and, for a rejection, the reason.</summary>
    public readonly struct AuthorityCallDecision
    {
        public AuthorityCallAction Action { get; }
        /// <summary><see cref="AuthorityCallOutcome.Accepted"/> for an apply, <see cref="AuthorityCallOutcome.Pending"/> for a forward, the reason for a rejection.</summary>
        public AuthorityCallOutcome Outcome { get; }

        public AuthorityCallDecision(AuthorityCallAction action, AuthorityCallOutcome outcome)
        {
            Action = action;
            Outcome = outcome;
        }

        public static readonly AuthorityCallDecision ApplyIt = new AuthorityCallDecision(AuthorityCallAction.Apply, AuthorityCallOutcome.Accepted);
        public static readonly AuthorityCallDecision ForwardIt = new AuthorityCallDecision(AuthorityCallAction.Forward, AuthorityCallOutcome.Pending);
        public static AuthorityCallDecision Reject(AuthorityCallOutcome why) => new AuthorityCallDecision(AuthorityCallAction.Reject, why);

        public override string ToString() => Action == AuthorityCallAction.Reject ? $"Reject({Outcome})" : Action.ToString();
    }

    /// <summary>What the receiving worker knows about a call's target when the call arrives.</summary>
    public readonly struct AuthorityCallTarget
    {
        /// <summary>This worker holds a copy of the entity (authoritative or ghost).</summary>
        public bool Known { get; }
        /// <summary>This worker has authority over it.</summary>
        public bool HasAuthority { get; }
        /// <summary>The entity's epoch on this worker; meaningless when not <see cref="Known"/>.</summary>
        public uint Epoch { get; }
        /// <summary>This worker has a connected peer to send the call on to, other than the one it came from.</summary>
        public bool CanForward { get; }

        public AuthorityCallTarget(bool known, bool hasAuthority, uint epoch, bool canForward)
        {
            Known = known;
            HasAuthority = hasAuthority;
            Epoch = epoch;
            CanForward = canForward;
        }

        /// <summary>The entity is not here at all.</summary>
        public static readonly AuthorityCallTarget Unknown = new AuthorityCallTarget(false, false, 0, false);
        /// <summary>This worker has authority over the entity at <paramref name="epoch"/>.</summary>
        public static AuthorityCallTarget Authoritative(uint epoch) => new AuthorityCallTarget(true, true, epoch, false);
        /// <summary>This worker holds a ghost at <paramref name="epoch"/> and can (or cannot) forward to its owner.</summary>
        public static AuthorityCallTarget Ghost(uint epoch, bool canForward) => new AuthorityCallTarget(true, false, epoch, canForward);
    }

    /// <summary>
    /// The rules every worker applies to an incoming <c>AuthorityRpc</c> call, in one place and with no engine
    /// types, so the worker, the standalone services and the conformance tests run the same code. The rules, in
    /// the order they are checked:
    /// <list type="number">
    /// <item>The entity is unknown here: <see cref="AuthorityCallOutcome.RejectedUnknownEntity"/>.</item>
    /// <item>The call's epoch is ahead of the entity's, or more than <see cref="MaxHops"/> behind it:
    /// <see cref="AuthorityCallOutcome.RejectedStaleEpoch"/>. A handover bumps the epoch by one and costs one
    /// forward, so a call that reached its authority within the hop bound is never more than <see cref="MaxHops"/>
    /// behind; anything further behind came from a copy that had missed several authority changes (design D3).</item>
    /// <item>This worker has authority: apply, unless the ledger has the id, which is
    /// <see cref="AuthorityCallOutcome.RejectedDuplicate"/> (design D5).</item>
    /// <item>The call has already been forwarded <see cref="MaxHops"/> times: <see cref="AuthorityCallOutcome.RejectedHopLimit"/> (design D4).</item>
    /// <item>There is a peer to forward to: forward. Otherwise <see cref="AuthorityCallOutcome.RejectedUnreachable"/>.</item>
    /// </list>
    /// </summary>
    public sealed class AuthorityCallRouter
    {
        /// <summary>Forwards a call may take after its first send, and the epoch lag tolerated with it. See <see cref="NebulaConfig.AuthorityCallMaxHops"/>.</summary>
        public int MaxHops { get; }
        /// <summary>The applied-call ids this worker remembers.</summary>
        public AuthorityCallLedger Ledger { get; }

        /// <summary>Calls decided here by outcome, for the worker's statistics.</summary>
        public long Applied { get; private set; }
        public long Forwarded { get; private set; }
        public long Rejected { get; private set; }

        public AuthorityCallRouter(int maxHops, AuthorityCallLedger ledger = null)
        {
            MaxHops = Math.Max(0, maxHops);
            Ledger = ledger ?? new AuthorityCallLedger();
        }

        /// <summary>
        /// Decide what to do with a call and record the decision. An <see cref="AuthorityCallAction.Apply"/> has
        /// already put the call id in the <see cref="Ledger"/> when this returns; the caller runs the method
        /// exactly then.
        /// </summary>
        /// <param name="callId">The id the sender minted.</param>
        /// <param name="callEpoch">The entity epoch the sender observed when it made the call.</param>
        /// <param name="hops">How many times the call has been forwarded before reaching this worker.</param>
        /// <param name="target">What this worker knows about the entity.</param>
        /// <param name="tick">This worker's current tick, for the ledger.</param>
        public AuthorityCallDecision Decide(ulong callId, uint callEpoch, byte hops, in AuthorityCallTarget target, uint tick)
        {
            var decision = DecideOnly(callEpoch, hops, target);
            switch (decision.Action)
            {
                case AuthorityCallAction.Apply:
                    if (!Ledger.TryRecord(callId, tick))
                    {
                        Rejected++;
                        return AuthorityCallDecision.Reject(AuthorityCallOutcome.RejectedDuplicate);
                    }
                    Applied++;
                    break;
                case AuthorityCallAction.Forward: Forwarded++; break;
                default: Rejected++; break;
            }
            return decision;
        }

        /// <summary>The rules without the ledger: what <see cref="Decide"/> does before it checks for a duplicate. An apply here may still be a <see cref="AuthorityCallOutcome.RejectedDuplicate"/> there.</summary>
        public AuthorityCallDecision DecideOnly(uint callEpoch, byte hops, in AuthorityCallTarget target)
        {
            if (!target.Known) return AuthorityCallDecision.Reject(AuthorityCallOutcome.RejectedUnknownEntity);
            if (IsStale(callEpoch, target.Epoch)) return AuthorityCallDecision.Reject(AuthorityCallOutcome.RejectedStaleEpoch);
            if (target.HasAuthority) return AuthorityCallDecision.ApplyIt;
            if (hops >= MaxHops) return AuthorityCallDecision.Reject(AuthorityCallOutcome.RejectedHopLimit);
            if (!target.CanForward) return AuthorityCallDecision.Reject(AuthorityCallOutcome.RejectedUnreachable);
            return AuthorityCallDecision.ForwardIt;
        }

        /// <summary>
        /// Whether a call made at <paramref name="callEpoch"/> is too old (or too new) for an entity at
        /// <paramref name="entityEpoch"/>: ahead of the entity, or more than <see cref="MaxHops"/> behind it.
        /// </summary>
        public bool IsStale(uint callEpoch, uint entityEpoch)
        {
            if (callEpoch > entityEpoch) return true;
            return entityEpoch - callEpoch > (uint)MaxHops;
        }
    }

    /// <summary>
    /// The sender's side of the reply channel: the calls this worker sent with a reply requested and has not heard
    /// back about. Each settles exactly once, with the reply that arrives or with
    /// <see cref="AuthorityCallOutcome.TimedOut"/> when <see cref="Expire"/> passes its deadline.
    /// </summary>
    public sealed class AuthorityCallTracker
    {
        private sealed class Pending
        {
            public ulong CallId;
            public uint DeadlineTick;
            public Action<AuthorityCallResult> OnDone;
        }

        private readonly Dictionary<ulong, Pending> _pending = new Dictionary<ulong, Pending>();
        private readonly List<Pending> _expired = new List<Pending>();
        private ulong _nextSequence;

        /// <summary>The index of the worker minting ids here.</summary>
        public ushort WorkerIndex { get; }
        /// <summary>Calls awaiting a reply.</summary>
        public int PendingCount => _pending.Count;
        /// <summary>Replies that arrived for a call no longer pending (settled, timed out, or never tracked).</summary>
        public long LateReplies { get; private set; }

        /// <param name="workerIndex">This worker's index, the top bits of every id minted here.</param>
        /// <param name="initialSequence">Where the sequence starts; see <see cref="AuthorityCallId.InitialSequence"/>.</param>
        public AuthorityCallTracker(ushort workerIndex, ulong initialSequence = 0)
        {
            WorkerIndex = workerIndex;
            _nextSequence = initialSequence;
        }

        /// <summary>A fresh call id for a call this worker is about to send.</summary>
        public ulong Mint() => AuthorityCallId.Make(WorkerIndex, ++_nextSequence);

        /// <summary>Wait for the reply to <paramref name="callId"/>; <paramref name="onDone"/> runs once, at the reply or at <paramref name="deadlineTick"/>.</summary>
        public void Track(ulong callId, uint deadlineTick, Action<AuthorityCallResult> onDone)
        {
            if (onDone == null) throw new ArgumentNullException(nameof(onDone));
            _pending[callId] = new Pending { CallId = callId, DeadlineTick = deadlineTick, OnDone = onDone };
        }

        /// <summary>Settle <paramref name="callId"/> with <paramref name="result"/>. False when it was not pending (the reply is counted in <see cref="LateReplies"/>).</summary>
        public bool Complete(ulong callId, in AuthorityCallResult result)
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

        /// <summary>Whether <paramref name="callId"/> still awaits a reply.</summary>
        public bool IsPending(ulong callId) => _pending.ContainsKey(callId);

        /// <summary>Settle every call whose deadline is at or before <paramref name="tick"/> as <see cref="AuthorityCallOutcome.TimedOut"/>. Call once a tick.</summary>
        public void Expire(uint tick)
        {
            if (_pending.Count == 0) return;
            _expired.Clear();
            foreach (var kv in _pending) if (kv.Value.DeadlineTick <= tick) _expired.Add(kv.Value);
            for (int i = 0; i < _expired.Count; i++)
            {
                var p = _expired[i];
                if (!_pending.Remove(p.CallId)) continue; // a callback earlier in this pass settled it
                p.OnDone(new AuthorityCallResult(p.CallId, AuthorityCallOutcome.TimedOut, 0, 0));
            }
            _expired.Clear();
        }

        /// <summary>Drop every pending call without running its callback.</summary>
        public void Clear() => _pending.Clear();
    }
}
