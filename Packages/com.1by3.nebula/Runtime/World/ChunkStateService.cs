using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nebula.World
{
    /// <summary>What became of a change made through <see cref="ChunkStateService"/>.</summary>
    public enum ChunkStateOutcome : byte
    {
        /// <summary>The worker that holds the chunk's lease applied the change. <see cref="ChunkStateResult.State"/> is the entry now.</summary>
        Applied = 0,
        /// <summary>
        /// A compare-and-set found a different entry than expected, and changed nothing.
        /// <see cref="ChunkStateResult.State"/> is the entry the worker found, so the caller can decide again.
        /// </summary>
        Conflict = 1,
        /// <summary>
        /// Nothing changed: no worker holds the chunk's lease, or the one that does could not apply the change
        /// before the deadline (its restore of the chunk was still running, or the lease was moving).
        /// </summary>
        Unavailable = 2,
        /// <summary>
        /// No answer arrived before the deadline. The change may or may not have been applied: read the entry
        /// before trying again, or use a compare-and-set, which is safe to repeat.
        /// </summary>
        TimedOut = 3,
        /// <summary>
        /// The change was refused and nothing changed: the payload is longer than
        /// <see cref="ChunkState.MaxPayloadBytes"/>, the chunk's entries would exceed
        /// <see cref="ChunkState.MaxEncodedBytes"/>, or the container cannot hold chunk state (it is carried by an
        /// entity, or the id is empty).
        /// </summary>
        Rejected = 4,
    }

    /// <summary>The result of one change, passed to the callback of a <see cref="ChunkStateService"/> call.</summary>
    public readonly struct ChunkStateResult
    {
        /// <summary>What became of the change.</summary>
        public readonly ChunkStateOutcome Outcome;
        /// <summary>
        /// The object has an entry: after an applied change, the entry now; after a conflict or a refused change,
        /// the entry the worker found. False means untouched (or unknown, for <see cref="ChunkStateOutcome.TimedOut"/>
        /// and <see cref="ChunkStateOutcome.Unavailable"/>).
        /// </summary>
        public readonly bool HasState;
        /// <summary>The entry, when <see cref="HasState"/> is true.</summary>
        public readonly ObjectState State;

        /// <summary>Whether the change was applied.</summary>
        public bool Succeeded => Outcome == ChunkStateOutcome.Applied;

        internal ChunkStateResult(ChunkStateOutcome outcome, bool hasState, ObjectState state)
        {
            Outcome = outcome;
            HasState = hasState;
            State = hasState ? state : default;
        }

        /// <inheritdoc/>
        public override string ToString() => HasState ? $"{Outcome} ({State})" : $"{Outcome} (untouched)";
    }

    /// <summary>
    /// The write side of <see cref="ChunkState"/> on a worker, reached as <see cref="NebulaWorker.ChunkStates"/>.
    /// Every change is applied by the worker that holds the chunk's lease, one at a time, so two workers cannot
    /// both take the last unit of a resource. A worker that does not hold the lease sends the change to the one
    /// that does, found through the container's lease, and gets the result back. That works whether or not the
    /// chunk has any entries yet, and whether or not this worker holds a copy of them.
    /// <para>
    /// <see cref="CompareAndSet(string, ulong, ObjectState?, ObjectState?, Action{ChunkStateResult}, float)"/> is
    /// the safe way to consume something: it applies the change only if the entry is still what the caller last
    /// saw, and otherwise reports <see cref="ChunkStateOutcome.Conflict"/> with the entry it found. When this
    /// worker holds the lease and the chunk is ready, the change is applied at once and the callback runs before
    /// the call returns; <see cref="TryCompareAndSetLocal"/> does only that, and never sends anything.
    /// </para>
    /// <para>
    /// The chunk must be leased by some worker: a change to a chunk nobody has loaded is
    /// <see cref="ChunkStateOutcome.Unavailable"/>. A chunk that has just been leased is not ready until its saved
    /// state has been restored; changes to it wait for that, up to their deadline.
    /// </para>
    /// </summary>
    public sealed class ChunkStateService : IDisposable
    {
        /// <summary>
        /// Worker message kind of a change sent to the lease holder. High in the range, next to
        /// <see cref="EntityRequests.RequestMessageKind"/>, so it does not collide with a game's own kinds.
        /// </summary>
        public const ushort RequestMessageKind = 65010;
        /// <summary>Worker message kind of the lease holder's answer.</summary>
        public const ushort ReplyMessageKind = 65011;
        /// <summary>How long a change waits for its result by default, in seconds.</summary>
        public const float DefaultTimeoutSeconds = 5f;
        /// <summary>
        /// How long an emptied chunk's entity stays before it is despawned and its record deleted, in seconds.
        /// Long enough for the last change to reach the clients as a change rather than as the entity leaving.
        /// </summary>
        public const float EmptyLingerSeconds = 1f;
        /// <summary>How many times a change follows a lease that moved before it gives up as <see cref="ChunkStateOutcome.Unavailable"/>.</summary>
        public const int MaxAttempts = 4;
        /// <summary>Seconds before a change whose lease moved is sent again, so the new lease can reach this worker.</summary>
        public const float RetryDelaySeconds = 0.1f;

        /// <summary>On the wire only: the receiver does not hold the lease, so the sender looks it up again.</summary>
        private const byte NotOwnerCode = 250;

        private const byte FlagConditional = 1;
        private const byte FlagHasExpected = 2;
        private const byte FlagHasReplacement = 4;

        /// <summary>One change: what to compare, and what to write.</summary>
        private struct Op
        {
            public string ContainerId;
            public ulong ObjectId;
            public bool Conditional;
            public bool HasExpected;
            public ObjectState Expected;
            public bool HasReplacement;
            public ObjectState Replacement;
        }

        /// <summary>A change this worker sent to another and is waiting on.</summary>
        private sealed class Outgoing
        {
            public uint Id;
            public Op Op;
            public Action<ChunkStateResult> OnDone;
            public float Deadline;
            public int Attempts;
            /// <summary>When to look the lease up again and resend; 0 while the request is out.</summary>
            public float RetryAt;
        }

        /// <summary>A change this worker holds the lease for but cannot apply yet (the chunk is still restoring).</summary>
        private sealed class Held
        {
            public Op Op;
            public float Deadline;
            /// <summary>The caller on this worker, or null when the change came from another worker.</summary>
            public Action<ChunkStateResult> OnDone;
            public ushort FromWorkerIndex;
            public uint RequestId;
        }

        private enum Readiness { NotOwner, NotReady, Ready, Invalid }

        private readonly NebulaWorker _worker;
        private readonly Dictionary<uint, Outgoing> _outgoing = new Dictionary<uint, Outgoing>();
        private readonly List<Held> _held = new List<Held>();
        private readonly List<uint> _scratchIds = new List<uint>();
        private readonly List<Held> _scratchHeld = new List<Held>();
        private readonly List<ChunkStateEntity> _scratchCopies = new List<ChunkStateEntity>();
        private uint _nextId = 1;
        private bool _disposed;

        /// <summary>Seconds on a monotonic clock, for deadlines and the empty linger. A test replaces it.</summary>
        internal Func<float> Now = () => Time.unscaledTime;

        internal ChunkStateService(NebulaWorker worker)
        {
            _worker = worker ?? throw new ArgumentNullException(nameof(worker));
            _worker.RegisterMessageHandler(RequestMessageKind, OnRequest);
            _worker.RegisterMessageHandler(ReplyMessageKind, OnReply);
            if (worker.Config != null) ChunkState.UseInterest(worker.Config.ToInterestSettings());
        }

        /// <summary>Changes this worker is waiting on: sent to another worker, or held until a chunk it leases is ready.</summary>
        public int PendingCount => _outgoing.Count + _held.Count;

        /// <summary>Whether this worker holds the lease of the container <paramref name="containerId"/>.</summary>
        public bool HoldsLease(string containerId)
        {
            var c = ContainerRegistry.FindById(containerId);
            return c != null && c.IsOwnedBy(_worker.WorkerId);
        }

        /// <summary>
        /// Whether a change to the chunk would be applied on this worker right now: it holds the chunk's lease and
        /// the chunk's saved state has been restored.
        /// </summary>
        public bool CanApplyLocally(string containerId) => Check(containerId, out _) == Readiness.Ready;

        // ------------------------------------------------------------------------------------------ public writes

        /// <summary>Set the object's entry, whatever it was. See <see cref="CompareAndSet(string, ulong, ObjectState?, ObjectState?, Action{ChunkStateResult}, float)"/> for routing and results.</summary>
        public void Set(Container chunk, ulong objectId, ObjectState state, Action<ChunkStateResult> onDone = null, float timeoutSeconds = DefaultTimeoutSeconds) =>
            Set(chunk != null ? chunk.ContainerId : "", objectId, state, onDone, timeoutSeconds);

        /// <summary>Set the object's entry, whatever it was, in the chunk whose container id is <paramref name="containerId"/>.</summary>
        public void Set(string containerId, ulong objectId, ObjectState state, Action<ChunkStateResult> onDone = null, float timeoutSeconds = DefaultTimeoutSeconds) =>
            Submit(new Op { ContainerId = containerId, ObjectId = objectId, HasReplacement = true, Replacement = state }, onDone, timeoutSeconds);

        /// <summary>Remove the object's entry, so it is untouched again.</summary>
        public void Clear(Container chunk, ulong objectId, Action<ChunkStateResult> onDone = null, float timeoutSeconds = DefaultTimeoutSeconds) =>
            Clear(chunk != null ? chunk.ContainerId : "", objectId, onDone, timeoutSeconds);

        /// <summary>Remove the object's entry in the chunk whose container id is <paramref name="containerId"/>.</summary>
        public void Clear(string containerId, ulong objectId, Action<ChunkStateResult> onDone = null, float timeoutSeconds = DefaultTimeoutSeconds) =>
            Submit(new Op { ContainerId = containerId, ObjectId = objectId }, onDone, timeoutSeconds);

        /// <inheritdoc cref="CompareAndSet(string, ulong, ObjectState?, ObjectState?, Action{ChunkStateResult}, float)"/>
        public void CompareAndSet(Container chunk, ulong objectId, ObjectState? expected, ObjectState? replacement, Action<ChunkStateResult> onDone, float timeoutSeconds = DefaultTimeoutSeconds) =>
            CompareAndSet(chunk != null ? chunk.ContainerId : "", objectId, expected, replacement, onDone, timeoutSeconds);

        /// <summary>
        /// Replace the object's entry with <paramref name="replacement"/> only if it is still
        /// <paramref name="expected"/>. Null for <paramref name="expected"/> means "still untouched"; null for
        /// <paramref name="replacement"/> clears the entry. Entries are equal when their value, expiry time and
        /// payload are equal, and an entry whose expiry time has passed counts as untouched.
        /// <para>
        /// The change runs on the worker that holds the chunk's lease: here, at once, when that is this worker and
        /// the chunk is ready; otherwise it is sent there. <paramref name="onDone"/> runs exactly once, on this
        /// worker's main thread, with <see cref="ChunkStateOutcome.Applied"/> and the new entry, with
        /// <see cref="ChunkStateOutcome.Conflict"/> and the entry that was there, or with the reason nothing
        /// happened. A replacement whose expiry time has already passed clears the entry.
        /// </para>
        /// </summary>
        public void CompareAndSet(string containerId, ulong objectId, ObjectState? expected, ObjectState? replacement, Action<ChunkStateResult> onDone, float timeoutSeconds = DefaultTimeoutSeconds)
        {
            var op = new Op
            {
                ContainerId = containerId,
                ObjectId = objectId,
                Conditional = true,
                HasExpected = expected.HasValue,
                Expected = expected.GetValueOrDefault(),
                HasReplacement = replacement.HasValue,
                Replacement = replacement.GetValueOrDefault(),
            };
            Submit(op, onDone, timeoutSeconds);
        }

        /// <summary>
        /// The local fast path: apply a compare-and-set here and now if this worker holds the chunk's lease and the
        /// chunk is ready, and never send anything. Returns false, with nothing changed, when the change has to go
        /// to another worker or wait; use <see cref="CompareAndSet(string, ulong, ObjectState?, ObjectState?, Action{ChunkStateResult}, float)"/>
        /// for those.
        /// </summary>
        public bool TryCompareAndSetLocal(string containerId, ulong objectId, ObjectState? expected, ObjectState? replacement, out ChunkStateResult result)
        {
            var op = new Op
            {
                ContainerId = containerId,
                ObjectId = objectId,
                Conditional = true,
                HasExpected = expected.HasValue,
                Expected = expected.GetValueOrDefault(),
                HasReplacement = replacement.HasValue,
                Replacement = replacement.GetValueOrDefault(),
            };
            return TryLocal(op, out result);
        }

        /// <summary>
        /// The local fast path for an unconditional write: set the entry (or clear it, for a null
        /// <paramref name="state"/>) if this worker holds the chunk's lease and the chunk is ready. False, with
        /// nothing changed, otherwise.
        /// </summary>
        public bool TrySetLocal(string containerId, ulong objectId, ObjectState? state, out ChunkStateResult result)
        {
            var op = new Op { ContainerId = containerId, ObjectId = objectId, HasReplacement = state.HasValue, Replacement = state.GetValueOrDefault() };
            return TryLocal(op, out result);
        }

        private bool TryLocal(Op op, out ChunkStateResult result)
        {
            if (!Validate(op, out result)) return true;
            var readiness = Check(op.ContainerId, out var container);
            if (readiness == Readiness.Invalid) { result = new ChunkStateResult(ChunkStateOutcome.Rejected, false, default); return true; }
            if (readiness != Readiness.Ready) { result = default; return false; }
            result = Apply(op, container);
            return true;
        }

        // ------------------------------------------------------------------------------------------ routing

        private void Submit(Op op, Action<ChunkStateResult> onDone, float timeoutSeconds)
        {
            if (!Validate(op, out var refused)) { Complete(onDone, refused); return; }
            float deadline = Now() + Math.Max(0.01f, timeoutSeconds);
            Route(op, onDone, deadline, attempts: 0);
        }

        /// <summary>Send the change where the lease is: apply or hold it here, or ask the lease holder.</summary>
        private void Route(Op op, Action<ChunkStateResult> onDone, float deadline, int attempts)
        {
            var readiness = Check(op.ContainerId, out var container);
            switch (readiness)
            {
                case Readiness.Ready:
                    Complete(onDone, Apply(op, container));
                    return;
                case Readiness.Invalid:
                    Complete(onDone, new ChunkStateResult(ChunkStateOutcome.Rejected, false, default));
                    return;
                case Readiness.NotReady:
                    _held.Add(new Held { Op = op, Deadline = deadline, OnDone = onDone ?? (_ => { }) });
                    return;
            }

            string owner = OwnerOf(op.ContainerId, out bool leased);
            var pending = new Outgoing { Id = _nextId++, Op = op, OnDone = onDone, Deadline = deadline, Attempts = attempts };
            if (_nextId == 0) _nextId = 1;
            if (!leased)
            {
                // No row at all: nobody has this chunk loaded, so there is nobody to ask.
                Complete(onDone, new ChunkStateResult(ChunkStateOutcome.Unavailable, false, default));
                return;
            }
            _outgoing[pending.Id] = pending;
            if (string.IsNullOrEmpty(owner) || !SendRequest(owner, pending)) pending.RetryAt = Now() + RetryDelaySeconds;
        }

        private bool SendRequest(string ownerId, Outgoing pending)
        {
            var op = pending.Op;
            uint waitMs = (uint)Math.Max(0f, (pending.Deadline - Now()) * 1000f * 0.8f);
            uint id = pending.Id;
            pending.RetryAt = 0f;
            return _worker.SendToWorker(ownerId, RequestMessageKind, w =>
            {
                w.WriteUInt(id);
                w.WriteString(op.ContainerId);
                w.WriteULong(op.ObjectId);
                byte flags = 0;
                if (op.Conditional) flags |= FlagConditional;
                if (op.HasExpected) flags |= FlagHasExpected;
                if (op.HasReplacement) flags |= FlagHasReplacement;
                w.WriteByte(flags);
                if (op.HasExpected) WriteState(w, op.Expected);
                if (op.HasReplacement) WriteState(w, op.Replacement);
                w.WriteUInt(waitMs);
            });
        }

        /// <summary>The worker holding the lease of <paramref name="containerId"/>, and whether any lease exists for it.</summary>
        private string OwnerOf(string containerId, out bool leased)
        {
            var c = ContainerRegistry.FindById(containerId);
            if (c != null && !string.IsNullOrEmpty(c.OwnerWorkerId)) { leased = true; return c.OwnerWorkerId; }
            var cp = _worker.ControlPlane;
            if (cp != null && cp.IsConnected)
            {
                var lease = cp.FindLease(containerId);
                leased = lease != null;
                return lease != null ? lease.WorkerId ?? "" : "";
            }
            // No control plane to ask (an edit-mode worker): a registered container is the only evidence of a lease.
            leased = c != null;
            return "";
        }

        private void OnRequest(string fromWorkerId, ushort fromWorkerIndex, NetworkReader r)
        {
            uint id = r.ReadUInt();
            var op = new Op { ContainerId = r.ReadString(), ObjectId = r.ReadULong() };
            byte flags = r.ReadByte();
            op.Conditional = (flags & FlagConditional) != 0;
            op.HasExpected = (flags & FlagHasExpected) != 0;
            op.HasReplacement = (flags & FlagHasReplacement) != 0;
            if (op.HasExpected) op.Expected = ReadState(r);
            if (op.HasReplacement) op.Replacement = ReadState(r);
            uint waitMs = r.ReadUInt();

            if (!Validate(op, out var refused)) { SendReply(fromWorkerIndex, id, (byte)refused.Outcome, refused); return; }
            var readiness = Check(op.ContainerId, out var container);
            switch (readiness)
            {
                case Readiness.NotOwner:
                    SendReply(fromWorkerIndex, id, NotOwnerCode, default);
                    return;
                case Readiness.Invalid:
                    SendReply(fromWorkerIndex, id, (byte)ChunkStateOutcome.Rejected, default);
                    return;
                case Readiness.NotReady:
                    _held.Add(new Held { Op = op, Deadline = Now() + waitMs / 1000f, FromWorkerIndex = fromWorkerIndex, RequestId = id });
                    return;
            }
            var result = Apply(op, container);
            SendReply(fromWorkerIndex, id, (byte)result.Outcome, result);
        }

        private void SendReply(ushort toWorkerIndex, uint id, byte code, ChunkStateResult result)
        {
            bool sent = _worker.SendToWorker(toWorkerIndex, ReplyMessageKind, w =>
            {
                w.WriteUInt(id);
                w.WriteByte(code);
                w.WriteBool(result.HasState);
                if (result.HasState) WriteState(w, result.State);
            });
            if (!sent) NebulaLog.Warn($"chunk state: could not answer change {id} from worker {toWorkerIndex}: not connected; it will time out there");
        }

        private void OnReply(string fromWorkerId, ushort fromWorkerIndex, NetworkReader r)
        {
            uint id = r.ReadUInt();
            byte code = r.ReadByte();
            bool hasState = r.ReadBool();
            var state = hasState ? ReadState(r) : default;
            if (!_outgoing.TryGetValue(id, out var pending)) return; // timed out already
            if (code == NotOwnerCode)
            {
                // The lease moved while the change was on its way. Look it up again shortly, a few times at most.
                pending.Attempts++;
                if (pending.Attempts >= MaxAttempts)
                {
                    _outgoing.Remove(id);
                    Complete(pending.OnDone, new ChunkStateResult(ChunkStateOutcome.Unavailable, false, default));
                    return;
                }
                pending.RetryAt = Now() + RetryDelaySeconds;
                return;
            }
            _outgoing.Remove(id);
            var outcome = code <= (byte)ChunkStateOutcome.Rejected ? (ChunkStateOutcome)code : ChunkStateOutcome.Unavailable;
            Complete(pending.OnDone, new ChunkStateResult(outcome, hasState, state));
        }

        // ------------------------------------------------------------------------------------------ the lease holder

        /// <summary>
        /// Whether a change to the chunk can be applied here now. Waiting (<see cref="Readiness.NotReady"/>) covers
        /// every moment at which creating the chunk's entity could make a second copy of one that exists: the saved
        /// state has not been read yet, a record is held back for a handover, or the entity is here as a ghost
        /// whose authority is on its way.
        /// </summary>
        private Readiness Check(string containerId, out Container container)
        {
            container = ContainerRegistry.FindById(containerId);
            if (container == null || !container.IsOwnedBy(_worker.WorkerId)) return Readiness.NotOwner;
            if (container.IsDynamic) return Readiness.Invalid;
            if (_worker.IsFenced) return Readiness.NotReady;
            var persistence = _worker.Persistence;
            if (persistence != null)
            {
                if (!persistence.IsContainerRestored(containerId)) return Readiness.NotReady;
                if (persistence.IsAwaitingHandover(ChunkState.KeyOf(containerId))) return Readiness.NotReady;
            }
            var copies = ChunkState.CopiesOf(containerId);
            for (int i = 0; i < copies.Count; i++)
            {
                var copy = copies[i];
                if (copy == null || !IsMine(copy)) continue;
                if (!copy.HasAuthority) return Readiness.NotReady;
            }
            return Readiness.Ready;
        }

        private bool IsMine(ChunkStateEntity copy)
        {
            var identity = copy.Identity;
            return identity != null && identity.IsSpawned && _worker.Find(identity.NetId) == identity;
        }

        /// <summary>This worker's authoritative copy of the chunk's state, or null when the chunk has none.</summary>
        internal ChunkStateEntity FindAuthoritative(string containerId)
        {
            var copies = ChunkState.CopiesOf(containerId);
            for (int i = 0; i < copies.Count; i++)
            {
                var copy = copies[i];
                if (copy != null && copy.HasAuthority && IsMine(copy)) return copy;
            }
            return null;
        }

        private ChunkStateResult Apply(Op op, Container container)
        {
            long now = ChunkState.NowUnixMs;
            var entity = FindAuthoritative(op.ContainerId);
            if (entity != null) entity.PollExpiry(now);
            ObjectState current = default;
            bool has = entity != null && entity.TryGetLive(op.ObjectId, now, out current);

            if (op.Conditional)
            {
                bool matches = op.HasExpected ? has && current.Equals(op.Expected) : !has;
                if (!matches) return new ChunkStateResult(ChunkStateOutcome.Conflict, has, current);
            }

            if (!op.HasReplacement || !op.Replacement.IsLiveAt(now))
            {
                if (has) entity.Remove(op.ObjectId);
                return new ChunkStateResult(ChunkStateOutcome.Applied, false, default);
            }

            if (entity == null)
            {
                entity = Create(container, op.ObjectId, op.Replacement);
                return entity != null
                    ? new ChunkStateResult(ChunkStateOutcome.Applied, true, op.Replacement)
                    : new ChunkStateResult(ChunkStateOutcome.Unavailable, false, default);
            }
            if (!entity.Set(op.ObjectId, op.Replacement)) return new ChunkStateResult(ChunkStateOutcome.Rejected, has, current);
            entity.EmptySince = -1f;
            return new ChunkStateResult(ChunkStateOutcome.Applied, true, op.Replacement);
        }

        /// <summary>The chunk's first entry: spawn its entity, server-driven, at the centre of its container, with the entry already in it.</summary>
        private ChunkStateEntity Create(Container container, ulong objectId, ObjectState state)
        {
            var identity = NetworkPrefabs.Instantiate(ChunkStateEntity.PrefabId, container.WorldBounds.center, Quaternion.identity, container.transform);
            if (identity == null) return null;
            var entity = identity.GetComponent<ChunkStateEntity>();
            identity.Persistent.Key = ChunkState.KeyOf(container.ContainerId);
            entity.Seed(objectId, state);
            try { _worker.SpawnServerDriven(identity, container); }
            catch (Exception e)
            {
                NebulaLog.Error($"chunk state: could not spawn the state entity of {container.ContainerId}: {e}");
                if (identity != null) UnityEngine.Object.DestroyImmediate(identity.gameObject);
                return null;
            }
            return entity;
        }

        // ------------------------------------------------------------------------------------------ the frame pass

        /// <summary>
        /// Called once per frame by the worker: settles changes whose deadline passed, resends changes whose lease
        /// moved, applies held changes to chunks that became ready, expires entries, and despawns chunk entities
        /// that have been empty for <see cref="EmptyLingerSeconds"/>.
        /// </summary>
        internal void Update()
        {
            if (_disposed) return;
            float now = Now();
            PumpOutgoing(now);
            PumpHeld(now);
            PumpEntities(now);
        }

        private void PumpOutgoing(float now)
        {
            if (_outgoing.Count == 0) return;
            _scratchIds.Clear();
            foreach (var kv in _outgoing) _scratchIds.Add(kv.Key);
            for (int i = 0; i < _scratchIds.Count; i++)
            {
                if (!_outgoing.TryGetValue(_scratchIds[i], out var pending)) continue;
                if (now >= pending.Deadline)
                {
                    _outgoing.Remove(pending.Id);
                    // Never sent anywhere (no owner answered the lookup): nothing can have changed.
                    var outcome = pending.RetryAt > 0f ? ChunkStateOutcome.Unavailable : ChunkStateOutcome.TimedOut;
                    Complete(pending.OnDone, new ChunkStateResult(outcome, false, default));
                    continue;
                }
                if (pending.RetryAt <= 0f || now < pending.RetryAt) continue;
                _outgoing.Remove(pending.Id);
                Route(pending.Op, pending.OnDone, pending.Deadline, pending.Attempts);
            }
            _scratchIds.Clear();
        }

        private void PumpHeld(float now)
        {
            if (_held.Count == 0) return;
            _scratchHeld.Clear();
            _scratchHeld.AddRange(_held);
            _held.Clear();
            for (int i = 0; i < _scratchHeld.Count; i++)
            {
                var held = _scratchHeld[i];
                var readiness = Check(held.Op.ContainerId, out var container);
                if (readiness == Readiness.Ready) { Answer(held, Apply(held.Op, container)); continue; }
                if (readiness == Readiness.Invalid) { Answer(held, new ChunkStateResult(ChunkStateOutcome.Rejected, false, default)); continue; }
                if (readiness == Readiness.NotOwner)
                {
                    // The lease left this worker while the change waited: a local caller follows it, a remote one is told to.
                    if (held.OnDone != null) Route(held.Op, held.OnDone, held.Deadline, 1);
                    else SendReply(held.FromWorkerIndex, held.RequestId, NotOwnerCode, default);
                    continue;
                }
                if (now >= held.Deadline) { Answer(held, new ChunkStateResult(ChunkStateOutcome.Unavailable, false, default)); continue; }
                _held.Add(held);
            }
            _scratchHeld.Clear();
        }

        private void Answer(Held held, ChunkStateResult result)
        {
            if (held.OnDone != null) Complete(held.OnDone, result);
            else SendReply(held.FromWorkerIndex, held.RequestId, (byte)result.Outcome, result);
        }

        private void PumpEntities(float now)
        {
            var all = ChunkState.All;
            if (all.Count == 0) return;
            long nowMs = ChunkState.NowUnixMs;
            bool fenced = _worker.IsFenced;
            _scratchCopies.Clear();
            for (int i = 0; i < all.Count; i++)
                if (all[i] != null && all[i].HasAuthority && IsMine(all[i])) _scratchCopies.Add(all[i]);
            for (int i = 0; i < _scratchCopies.Count; i++)
            {
                var entity = _scratchCopies[i];
                entity.PollExpiry(nowMs);
                if (!entity.IsEmpty) { entity.EmptySince = -1f; continue; }
                if (entity.EmptySince < 0f) { entity.EmptySince = now; continue; }
                // A fenced worker deletes nothing: its containers may already be restored elsewhere (D8).
                if (fenced || now - entity.EmptySince < EmptyLingerSeconds) continue;
                _worker.Despawn(entity.Identity, keepPersisted: false);
            }
            _scratchCopies.Clear();
        }

        // ------------------------------------------------------------------------------------------ helpers

        private static bool Validate(Op op, out ChunkStateResult refused)
        {
            refused = new ChunkStateResult(ChunkStateOutcome.Rejected, false, default);
            if (string.IsNullOrEmpty(op.ContainerId)) return false;
            if (op.HasReplacement && op.Replacement.PayloadLength > ChunkState.MaxPayloadBytes) return false;
            if (op.HasExpected && op.Expected.PayloadLength > ChunkState.MaxPayloadBytes) return false;
            return true;
        }

        private static void Complete(Action<ChunkStateResult> onDone, ChunkStateResult result)
        {
            if (onDone == null) return;
            try { onDone(result); }
            catch (Exception e) { NebulaLog.Error($"chunk state callback threw: {e}"); }
        }

        private static void WriteState(NetworkWriter w, in ObjectState s)
        {
            w.WriteUInt(s.Value);
            w.WriteLong(s.ExpiresAtUnixMs);
            w.WriteByte((byte)Math.Min(s.PayloadLength, byte.MaxValue));
            if (s.PayloadLength > 0) w.WriteRaw(new ArraySegment<byte>(s.Payload, 0, Math.Min(s.PayloadLength, byte.MaxValue)));
        }

        private static ObjectState ReadState(NetworkReader r)
        {
            uint value = r.ReadUInt();
            long expires = r.ReadLong();
            int n = r.ReadByte();
            byte[] payload = null;
            if (n > 0)
            {
                var seg = r.ReadSegment(n);
                payload = new byte[n];
                Buffer.BlockCopy(seg.Array, seg.Offset, payload, 0, n);
            }
            return new ObjectState(value, expires, payload, noCopy: true);
        }

        /// <summary>Stop answering; changes still waiting are dropped without their callbacks.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _worker.UnregisterMessageHandler(RequestMessageKind);
            _worker.UnregisterMessageHandler(ReplyMessageKind);
            _outgoing.Clear();
            _held.Clear();
        }
    }
}
