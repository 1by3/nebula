using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// The part of <see cref="NebulaWorker"/> a <see cref="WorkerQuery"/> talks to. A test can implement it with an
    /// in-memory loop between two fakes; the worker implements it over the lateral link.
    /// </summary>
    public interface IWorkerMessaging
    {
        ushort WorkerIndex { get; }
        void RegisterMessageHandler(ushort kind, NebulaWorker.WorkerMessageHandler handler);
        void UnregisterMessageHandler(ushort kind);
        bool SendToWorker(ushort workerIndex, ushort kind, Action<NetworkWriter> write, Delivery delivery = Delivery.ReliableOrdered);
    }

    /// <summary>
    /// A question fanned out to several workers over worker messages, with the bookkeeping every such question
    /// needs: a request id, a table of what is still unanswered, a deadline, and a callback when every reply is in
    /// or time is up. The pair of kinds is the game's; the request handler on the receiving side reads the payload
    /// and writes its reply, and the helper carries the id on both legs so the game never sees it.
    /// <para>
    /// A target that is not connected is skipped (it never counts as awaited). Sending to this worker's own index
    /// answers synchronously, like any worker message to oneself. A reply for an unknown or already completed
    /// request is counted in <see cref="RepliesLate"/> and dropped. Call <see cref="Update"/> once a frame to
    /// expire requests whose deadline passed; the completion callback then runs with <c>false</c>.
    /// </para>
    /// </summary>
    public sealed class WorkerQuery : IDisposable
    {
        /// <summary>Handles a request on the receiving worker: read <paramref name="request"/>, write the answer into <paramref name="reply"/>.</summary>
        public delegate void RequestHandler(string fromWorkerId, ushort fromWorkerIndex, NetworkReader request, NetworkWriter reply);

        /// <summary>Handles one reply on the asking worker.</summary>
        public delegate void ReplyHandler(string fromWorkerId, ushort fromWorkerIndex, NetworkReader reply);

        private sealed class Pending
        {
            public uint Id;
            public readonly HashSet<ushort> Awaiting = new HashSet<ushort>();
            public float Deadline;
            public ReplyHandler OnReply;
            public Action<bool> OnComplete;
            public bool FanningOut;
        }

        private readonly IWorkerMessaging _worker;
        private readonly ushort _requestKind;
        private readonly ushort _replyKind;
        private readonly RequestHandler _onRequest;
        private readonly Func<float> _clock;
        private readonly Dictionary<uint, Pending> _pending = new Dictionary<uint, Pending>();
        private readonly List<uint> _expired = new List<uint>();
        private readonly NetworkWriter _replyWriter = new NetworkWriter(1024);
        private uint _nextId = 1;
        private bool _disposed;

        /// <summary>Requests fanned out (one per <see cref="Send"/> that reached at least one worker).</summary>
        public int RequestsSent { get; private set; }
        /// <summary>Requests answered on this side.</summary>
        public int RequestsAnswered { get; private set; }
        /// <summary>Replies received in time.</summary>
        public int RepliesReceived { get; private set; }
        /// <summary>Workers that had not answered when a request expired.</summary>
        public int RepliesTimedOut { get; private set; }
        /// <summary>Replies that arrived after their request expired, or for an id never sent.</summary>
        public int RepliesLate { get; private set; }
        /// <summary>Requests still waiting for at least one worker.</summary>
        public int PendingCount => _pending.Count;

        /// <param name="worker">The worker to send and receive through.</param>
        /// <param name="requestKind">Worker message kind of the question.</param>
        /// <param name="replyKind">Worker message kind of the answer.</param>
        /// <param name="onRequest">Answers a question on this worker, or null when this worker only asks.</param>
        /// <param name="clock">Seconds, for the deadline; <see cref="Time.unscaledTime"/> by default.</param>
        public WorkerQuery(IWorkerMessaging worker, ushort requestKind, ushort replyKind, RequestHandler onRequest, Func<float> clock = null)
        {
            _worker = worker ?? throw new ArgumentNullException(nameof(worker));
            if (requestKind == replyKind) throw new ArgumentException("request and reply kinds must differ", nameof(replyKind));
            _requestKind = requestKind;
            _replyKind = replyKind;
            _onRequest = onRequest;
            _clock = clock ?? (() => Time.unscaledTime);
            if (onRequest != null) _worker.RegisterMessageHandler(requestKind, OnRequestMessage);
            _worker.RegisterMessageHandler(replyKind, OnReplyMessage);
        }

        /// <summary>
        /// Ask <paramref name="targets"/> (worker indices; duplicates and unreachable workers are skipped). Each
        /// reply invokes <paramref name="onReply"/>; <paramref name="onComplete"/> runs once, with <c>true</c> when
        /// every asked worker answered and <c>false</c> when <paramref name="timeoutSeconds"/> passed first. When
        /// nobody could be asked it runs immediately with <c>true</c>. Returns the request id (0 when nobody was
        /// asked).
        /// </summary>
        public uint Send(IEnumerable<ushort> targets, Action<NetworkWriter> write, ReplyHandler onReply, Action<bool> onComplete, float timeoutSeconds)
        {
            if (targets == null) throw new ArgumentNullException(nameof(targets));
            if (write == null) throw new ArgumentNullException(nameof(write));
            var p = new Pending { Id = _nextId++, OnReply = onReply, OnComplete = onComplete, Deadline = _clock() + timeoutSeconds, FanningOut = true };
            _pending[p.Id] = p;
            foreach (var index in targets)
            {
                if (p.Awaiting.Contains(index)) continue;
                p.Awaiting.Add(index); // before the send: a message to ourselves is answered inside it
                if (!_worker.SendToWorker(index, _requestKind, w => { w.WriteUInt(p.Id); write(w); })) p.Awaiting.Remove(index);
            }
            p.FanningOut = false;
            if (p.Awaiting.Count == 0)
            {
                _pending.Remove(p.Id);
                onComplete?.Invoke(true);
                return 0;
            }
            RequestsSent++;
            return p.Id;
        }

        /// <summary>Forget request <paramref name="id"/>; its callbacks never run. True when it was still pending.</summary>
        public bool Cancel(uint id) => _pending.Remove(id);

        /// <summary>Expire requests whose deadline passed, completing each with <c>false</c>. Call once a frame.</summary>
        public void Update()
        {
            if (_pending.Count == 0) return;
            float now = _clock();
            _expired.Clear();
            foreach (var kv in _pending) if (!kv.Value.FanningOut && now >= kv.Value.Deadline) _expired.Add(kv.Key);
            foreach (var id in _expired)
            {
                if (!_pending.TryGetValue(id, out var p)) continue; // a callback of an earlier one may have cancelled it
                _pending.Remove(id);
                RepliesTimedOut += p.Awaiting.Count;
                p.OnComplete?.Invoke(false);
            }
        }

        /// <summary>Unregister both handlers; pending requests are dropped without callbacks.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_onRequest != null) _worker.UnregisterMessageHandler(_requestKind);
            _worker.UnregisterMessageHandler(_replyKind);
            _pending.Clear();
        }

        private void OnRequestMessage(string fromWorkerId, ushort fromWorkerIndex, NetworkReader r)
        {
            uint id = r.ReadUInt();
            RequestsAnswered++;
            // The reply is built in our own buffer first: sending to ourselves would hand the worker's shared
            // writer to the handler while the request it is still reading lives in it.
            _replyWriter.Reset();
            _replyWriter.WriteUInt(id);
            _onRequest(fromWorkerId, fromWorkerIndex, r, _replyWriter);
            var payload = _replyWriter.ToSegment();
            _worker.SendToWorker(fromWorkerIndex, _replyKind, w => w.WriteRaw(payload));
        }

        private void OnReplyMessage(string fromWorkerId, ushort fromWorkerIndex, NetworkReader r)
        {
            uint id = r.ReadUInt();
            if (!_pending.TryGetValue(id, out var p) || !p.Awaiting.Remove(fromWorkerIndex))
            {
                RepliesLate++;
                return;
            }
            RepliesReceived++;
            p.OnReply?.Invoke(fromWorkerId, fromWorkerIndex, r);
            if (p.Awaiting.Count == 0 && !p.FanningOut)
            {
                _pending.Remove(id);
                p.OnComplete?.Invoke(true);
            }
        }
    }
}
