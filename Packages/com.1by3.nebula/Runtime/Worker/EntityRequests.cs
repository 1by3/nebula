using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nebula
{
    /// <summary>Where a <see cref="EntityRequests"/> call ended up.</summary>
    public enum EntityRequestStatus
    {
        /// <summary>The worker with authority answered. <see cref="EntityRequestResult.Reply"/> holds its bytes.</summary>
        Replied,
        /// <summary>No connected worker (this one included) has authority over the key, after one retry.</summary>
        NotFound,
        /// <summary>Nobody answered before the deadline. The handler may still be holding the request open.</summary>
        TimedOut,
    }

    /// <summary>Outcome of one <see cref="EntityRequests.Request"/> call.</summary>
    public readonly struct EntityRequestResult
    {
        public EntityRequestStatus Status { get; }
        /// <summary>The reply bytes when <see cref="Status"/> is <see cref="EntityRequestStatus.Replied"/>; empty otherwise.</summary>
        public byte[] Reply { get; }
        public bool Succeeded => Status == EntityRequestStatus.Replied;

        public EntityRequestResult(EntityRequestStatus status, byte[] reply)
        {
            Status = status;
            Reply = reply ?? Array.Empty<byte>();
        }
    }

    /// <summary>
    /// Asks whichever worker currently has authority over a <see cref="PersistentEntity"/> key, without needing a
    /// local ghost of it: a container query has neighbours to fall back to, but a persistent key has none, since the
    /// entity may be nowhere near this worker's containers. Unlike <see cref="WorkerQuery"/>, the answering side's
    /// handler may hold the request open for several ticks (a database round trip, a cooldown check) before it
    /// replies, so this does not reuse WorkerQuery's synchronous request/reply plumbing; it tracks the request on the
    /// asking side until a reply, a decline from every connected worker (twice - see <see cref="Request"/>), or the
    /// deadline settles it.
    /// <para>
    /// The worker holding authority answers in-process, with no network round trip. Every other connected worker
    /// answers "not mine" immediately, so the asker does not wait out the full timeout just to learn nobody has it.
    /// </para>
    /// </summary>
    public sealed class EntityRequests : IDisposable
    {
        /// <summary>
        /// Handles a request on the worker that has authority over <paramref name="entity"/>. Call
        /// <paramref name="reply"/> once, from this tick or a later one; calls after the first are ignored.
        /// </summary>
        public delegate void Handler(NetworkIdentity entity, ArraySegment<byte> payload, Action<byte[]> reply);

        /// <summary>
        /// Worker-message kinds this feature answers on. Chosen high in the ushort range so a game's own kinds
        /// (typically small, hand-picked numbers - see the worker-messages guide) do not collide with them.
        /// </summary>
        public const ushort RequestMessageKind = 65020;
        public const ushort DeclineMessageKind = 65021;
        public const ushort ReplyMessageKind = 65022;

        /// <summary>
        /// How long to wait, after every connected worker has declined, before asking again: long enough for a
        /// handover that was already in flight to land, short enough that a genuinely absent entity does not cost
        /// much beyond one round trip. A request that lands exactly inside a handover can still occasionally
        /// conclude <see cref="EntityRequestStatus.NotFound"/> if the second round is also declined everywhere.
        /// </summary>
        public const float RetryDelaySeconds = 0.2f;

        private sealed class Pending
        {
            public uint Id;
            public string Key;
            public ushort Kind;
            public byte[] Payload;
            public Action<EntityRequestResult> OnDone;
            public float Deadline;
            public readonly HashSet<ushort> Awaiting = new HashSet<ushort>();
            public bool Retried;
            public float RetryAt;
            public bool RetrySent;
        }

        private readonly IWorkerMessaging _worker;
        private readonly Func<string, NetworkIdentity> _resolveAuthority;
        private readonly Func<IEnumerable<ushort>> _connectedTargets;
        private readonly Func<float> _clock;
        private readonly Dictionary<ushort, Handler> _handlers = new Dictionary<ushort, Handler>();
        private readonly Dictionary<uint, Pending> _pending = new Dictionary<uint, Pending>();
        private readonly List<uint> _expired = new List<uint>();
        private readonly List<uint> _retrying = new List<uint>();
        private uint _nextId = 1;
        private bool _disposed;

        /// <summary>Requests still waiting on an answer, a retry, or their deadline.</summary>
        public int PendingCount => _pending.Count;

        /// <param name="worker">The worker to send and receive through.</param>
        /// <param name="resolveAuthority">The local, live entity for a persistent key, or null when this worker holds none.</param>
        /// <param name="connectedTargets">Worker indices to ask when this worker is not the authority (excluding itself).</param>
        /// <param name="clock">Seconds, for the deadline; <see cref="Time.unscaledTime"/> by default.</param>
        public EntityRequests(IWorkerMessaging worker, Func<string, NetworkIdentity> resolveAuthority, Func<IEnumerable<ushort>> connectedTargets, Func<float> clock = null)
        {
            _worker = worker ?? throw new ArgumentNullException(nameof(worker));
            _resolveAuthority = resolveAuthority ?? throw new ArgumentNullException(nameof(resolveAuthority));
            _connectedTargets = connectedTargets ?? throw new ArgumentNullException(nameof(connectedTargets));
            _clock = clock ?? (() => Time.unscaledTime);
            _worker.RegisterMessageHandler(RequestMessageKind, OnRequestMessage);
            _worker.RegisterMessageHandler(DeclineMessageKind, OnDeclineMessage);
            _worker.RegisterMessageHandler(ReplyMessageKind, OnReplyMessage);
        }

        /// <summary>Answer requests of <paramref name="kind"/> whenever this worker has authority over the target key. One handler per kind; registering again replaces it.</summary>
        public void RegisterHandler(ushort kind, Handler handler)
        {
            if (handler == null) _handlers.Remove(kind);
            else _handlers[kind] = handler;
        }

        /// <summary>
        /// Ask whichever worker has authority over <paramref name="persistentKey"/>. Local-first: if this worker
        /// currently holds authority, the handler runs in-process with no network round trip. Otherwise every
        /// connected worker is asked; a worker without authority answers "not mine" at once, so
        /// <see cref="EntityRequestStatus.NotFound"/> does not wait out the full timeout once everyone has declined.
        /// The first declined round is retried once, after <see cref="RetryDelaySeconds"/>, before concluding
        /// NotFound - see the type doc for why. <paramref name="onDone"/> runs exactly once, on the caller's thread
        /// (the same thread every worker-message callback runs on: whatever calls <c>Update</c>).
        /// </summary>
        public uint Request(string persistentKey, ushort kind, byte[] payload, Action<EntityRequestResult> onDone, float timeoutSeconds = 5f)
        {
            if (string.IsNullOrEmpty(persistentKey)) throw new ArgumentException("persistentKey is required", nameof(persistentKey));
            if (onDone == null) throw new ArgumentNullException(nameof(onDone));
            var p = new Pending
            {
                Id = _nextId++,
                Key = persistentKey,
                Kind = kind,
                Payload = payload ?? Array.Empty<byte>(),
                OnDone = onDone,
                Deadline = _clock() + timeoutSeconds,
            };
            _pending[p.Id] = p;
            if (TryHandleLocally(p.Key, p.Kind, new ArraySegment<byte>(p.Payload), bytes => CompleteReplied(p.Id, bytes))) return p.Id;
            BroadcastRound(p);
            return p.Id;
        }

        /// <summary>Forget request <paramref name="id"/>; its callback never runs. True when it was still pending.</summary>
        public bool Cancel(uint id) => _pending.Remove(id);

        /// <summary>Settle requests whose deadline passed, and re-ask requests whose retry delay elapsed. Call once a frame.</summary>
        public void Update()
        {
            if (_pending.Count == 0) return;
            float now = _clock();
            _expired.Clear();
            _retrying.Clear();
            foreach (var kv in _pending)
            {
                var p = kv.Value;
                if (now >= p.Deadline) { _expired.Add(kv.Key); continue; }
                if (p.RetryAt > 0f && !p.RetrySent && now >= p.RetryAt) _retrying.Add(kv.Key);
            }
            foreach (var id in _expired)
            {
                if (!_pending.TryGetValue(id, out var p)) continue; // an earlier callback in this pass may have resolved it
                Conclude(p, EntityRequestStatus.TimedOut);
            }
            foreach (var id in _retrying)
            {
                if (!_pending.TryGetValue(id, out var p)) continue;
                p.RetrySent = true;
                // Ownership may have landed here since the first round.
                if (TryHandleLocally(p.Key, p.Kind, new ArraySegment<byte>(p.Payload), bytes => CompleteReplied(p.Id, bytes))) continue;
                BroadcastRound(p);
            }
        }

        /// <summary>Unregister the three message kinds; pending requests are dropped without callbacks.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _worker.UnregisterMessageHandler(RequestMessageKind);
            _worker.UnregisterMessageHandler(DeclineMessageKind);
            _worker.UnregisterMessageHandler(ReplyMessageKind);
            _pending.Clear();
        }

        /// <summary>
        /// Run the handler registered for <paramref name="kind"/> if this worker has authority over
        /// <paramref name="key"/>; false (nothing invoked) when it does not, or no handler is registered. A handler
        /// that throws before replying still counts as handled: the entity is ours, so the asker should time out
        /// rather than be told nobody has it.
        /// </summary>
        private bool TryHandleLocally(string key, ushort kind, ArraySegment<byte> payload, Action<byte[]> onReply)
        {
            var entity = _resolveAuthority(key);
            if (entity == null || !entity.HasAuthority) return false;
            if (!_handlers.TryGetValue(kind, out var handler))
            {
                NebulaLog.Warn($"entity request kind {kind} for '{key}' has no handler; treating as not found");
                return false;
            }
            bool replied = false;
            void Reply(byte[] bytes)
            {
                if (replied) return;
                replied = true;
                onReply(bytes);
            }
            try { handler(entity, payload, Reply); }
            catch (Exception ex) { NebulaLog.Error($"entity request handler {kind} threw: {ex}"); }
            return true;
        }

        private void BroadcastRound(Pending p)
        {
            p.Awaiting.Clear();
            uint id = p.Id;
            string key = p.Key;
            ushort kind = p.Kind;
            byte[] payload = p.Payload;
            foreach (var index in _connectedTargets())
            {
                if (!p.Awaiting.Add(index)) continue; // duplicate target
                if (!_worker.SendToWorker(index, RequestMessageKind, w => { w.WriteUInt(id); w.WriteString(key); w.WriteUShort(kind); w.WriteBytes(payload); }))
                    p.Awaiting.Remove(index);
            }
            if (p.Awaiting.Count == 0) ScheduleRetryOrConclude(p);
        }

        private void ScheduleRetryOrConclude(Pending p)
        {
            if (p.Retried) { Conclude(p, EntityRequestStatus.NotFound); return; }
            p.Retried = true;
            p.RetryAt = _clock() + RetryDelaySeconds;
        }

        private void Conclude(Pending p, EntityRequestStatus status)
        {
            _pending.Remove(p.Id);
            p.OnDone(new EntityRequestResult(status, null));
        }

        private void CompleteReplied(uint id, byte[] bytes)
        {
            if (!_pending.TryGetValue(id, out var p)) return; // already timed out, cancelled, or answered
            _pending.Remove(id);
            p.OnDone(new EntityRequestResult(EntityRequestStatus.Replied, bytes));
        }

        private void OnRequestMessage(string fromWorkerId, ushort fromWorkerIndex, NetworkReader r)
        {
            uint id = r.ReadUInt();
            string key = r.ReadString();
            ushort kind = r.ReadUShort();
            byte[] payload = r.ReadBytes(); // copied: the handler may still hold this after the reader's buffer is reused
            bool handled = TryHandleLocally(key, kind, new ArraySegment<byte>(payload), bytes => SendReply(fromWorkerIndex, id, bytes));
            if (!handled) _worker.SendToWorker(fromWorkerIndex, DeclineMessageKind, w => w.WriteUInt(id));
        }

        private void OnDeclineMessage(string fromWorkerId, ushort fromWorkerIndex, NetworkReader r)
        {
            uint id = r.ReadUInt();
            if (!_pending.TryGetValue(id, out var p)) return;
            if (!p.Awaiting.Remove(fromWorkerIndex)) return;
            if (p.Awaiting.Count == 0) ScheduleRetryOrConclude(p);
        }

        private void OnReplyMessage(string fromWorkerId, ushort fromWorkerIndex, NetworkReader r)
        {
            uint id = r.ReadUInt();
            byte[] bytes = r.ReadBytes();
            CompleteReplied(id, bytes);
        }

        private void SendReply(ushort toWorkerIndex, uint id, byte[] bytes) => _worker.SendToWorker(toWorkerIndex, ReplyMessageKind, w => { w.WriteUInt(id); w.WriteBytes(bytes); });
    }
}
