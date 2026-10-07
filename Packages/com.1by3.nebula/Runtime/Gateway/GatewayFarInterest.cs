using System;
using System.Collections.Generic;
#if NEBULA_SERVICE
using Nebula.ServicePrimitives;
#else
using UnityEngine;
#endif

namespace Nebula
{
    /// <summary>
    /// The gateway's half of the far relevance tier (<c>docs/interest-management.md</c> §16, NEB-388). Workers send the
    /// poses of their far entities to the gateways whose foci they reach (<see cref="FarPublisher"/>); the gateway keeps
    /// the newest of each (<see cref="FarRecord"/>), decides per client at its interest evaluation which of them are in
    /// its far tier, and relays each new pose to the clients that hold it. A far entity is not an
    /// <see cref="EntityRecord"/>: it has no container, no state stream and no observers, and it never enters a client's
    /// interest set through this path. The normal tier wins: an entity a client holds as a replica is never in its far
    /// tier, and entering the replica set takes it out silently (the client turns the marker into the replica).
    /// </summary>
    public sealed partial class NebulaGateway
    {
        private sealed class FarRecord
        {
            public FarEntityEntry Entry;
            public ushort WorkerIndex;
            /// <summary>Gateway clock time of the newest entry; a record not refreshed for <see cref="FarExpirySeconds"/> is dropped.</summary>
            public double ReceivedAt;
            /// <summary>The clients that hold it in their far tier: the exact audience of each new pose.</summary>
            public readonly List<ClientConn> Holders = new List<ClientConn>();
        }

        private readonly Dictionary<ulong, FarRecord> _farRecords = new Dictionary<ulong, FarRecord>();
        private readonly List<FarEntityEntry> _farIn = new List<FarEntityEntry>();
        private readonly List<ClientConn> _farTouched = new List<ClientConn>();
        private readonly List<ulong> _farScratch = new List<ulong>();
        private readonly List<(ulong NetId, double SqrDistance)> _farCandidates = new List<(ulong, double)>();
        private readonly HashSet<ulong> _farChosen = new HashSet<ulong>();
        private readonly NetworkWriter _farWriter = new NetworkWriter(1024);
        private static readonly Comparison<(ulong NetId, double SqrDistance)> NearestFirst = (a, b) => a.SqrDistance.CompareTo(b.SqrDistance);
        private long _farEntriesRelayed;

        /// <summary>Far entities this gateway holds a pose for (NEB-388).</summary>
        public int FarEntityCount => _farRecords.Count;
        /// <summary>Far-tier entries sent to clients since start (enters, updates and leaves).</summary>
        public long FarEntriesRelayed => _farEntriesRelayed;

        /// <summary>Whether a client holds an entity in its far tier (tests and diagnostics); false for an unknown client.</summary>
        public bool ClientHoldsFar(ulong clientId, ulong netId) => _clientsById.TryGetValue(clientId, out var c) && c.FarHeld.Contains(netId);

        /// <summary>How many entities a client holds in its far tier; -1 for an unknown client.</summary>
        public int FarTierSize(ulong clientId) => _clientsById.TryGetValue(clientId, out var c) ? c.FarHeld.Count : -1;

        /// <summary>
        /// Seconds without a pose after which a far record is dropped: three of its updates, and never less than five
        /// seconds, so a handover (the new owner starts sending at its next match) does not take the marker away.
        /// </summary>
        private static double FarExpirySeconds(in FarEntityEntry entry) => Math.Max(5.0, 3.0 / FarRelevance.ClampRate(entry.UpdateRate));

        private bool ClientTakesFar(ClientConn client) =>
            client.Welcomed && client.ProtocolVersion >= HelloMsg.FarEntitiesVersion && _interest.FarMaxEntities > 0;

        // ------------------------------------------------------------------------------------------- from workers

        private void OnFarEntities(WorkerConn w, NetworkReader r)
        {
            FarEntitiesMsg.Read(r, _farIn);
            double now = InterestNow;
            for (int i = 0; i < _farIn.Count; i++)
            {
                var entry = _farIn[i];
                _farRecords.TryGetValue(entry.NetId, out var rec);
                if (entry.Kind == FarEntryKind.Gone)
                {
                    // Only the worker that sent the newest pose, or a newer epoch, takes it away: a late "gone" from the
                    // previous owner after a handover must not remove what the new owner is publishing.
                    if (rec == null || (rec.WorkerIndex != w.Index && entry.Epoch <= rec.Entry.Epoch)) continue;
                    DropFarRecord(rec);
                    continue;
                }
                if (rec != null && entry.Epoch < rec.Entry.Epoch) continue;
                if (rec == null) _farRecords[entry.NetId] = rec = new FarRecord();
                rec.Entry = entry;
                rec.WorkerIndex = w.Index;
                rec.ReceivedAt = now;
                for (int h = rec.Holders.Count - 1; h >= 0; h--)
                {
                    var client = rec.Holders[h];
                    // Became a replica since the last evaluation (an arrival): the marker is the replica now.
                    if (client.Visible.Contains(entry.NetId)) { client.FarHeld.Remove(entry.NetId); rec.Holders.RemoveAt(h); continue; }
                    QueueFar(client, entry);
                }
            }
            _farIn.Clear();
            FlushFar();
        }

        /// <summary>The entity despawned on its worker: whoever holds it in the far tier is told it is gone.</summary>
        private void OnFarDespawn(ushort workerIndex, ulong netId, uint epoch)
        {
            if (!_farRecords.TryGetValue(netId, out var rec)) return;
            if (rec.WorkerIndex != workerIndex && epoch < rec.Entry.Epoch) return;
            DropFarRecord(rec);
            FlushFar();
        }

        private void DropFarRecord(FarRecord rec)
        {
            _farRecords.Remove(rec.Entry.NetId);
            var gone = FarEntityEntry.GoneOf(rec.Entry.NetId, rec.Entry.Epoch);
            for (int h = 0; h < rec.Holders.Count; h++)
            {
                var client = rec.Holders[h];
                if (client.FarHeld.Remove(rec.Entry.NetId)) QueueFar(client, gone);
            }
            rec.Holders.Clear();
        }

        /// <summary>Drop far records nobody has refreshed (their worker died, or stopped matching us without a word).</summary>
        private void ExpireFarRecords(double now)
        {
            if (_farRecords.Count == 0) return;
            _farScratch.Clear();
            foreach (var kv in _farRecords) if (now - kv.Value.ReceivedAt > FarExpirySeconds(kv.Value.Entry)) _farScratch.Add(kv.Key);
            for (int i = 0; i < _farScratch.Count; i++) DropFarRecord(_farRecords[_farScratch[i]]);
            _farScratch.Clear();
            FlushFar();
        }

        // ------------------------------------------------------------------------------------------- per client

        /// <summary>
        /// Bring one client's far tier up to date, after its interest set: the far entities of its own scope within their
        /// far radius of one of its foci in the scope's own space (frame key 0, which every client with a pawn has: its
        /// own, or the enclosing focus around a pawn on a planet), measured in double, that it does not hold as replicas
        /// and that the policy authorizes, nearest first, at most <see cref="InterestSettings.FarMaxEntities"/>. An entity
        /// already held stays until it is <see cref="FarRelevance.StayMargin"/> beyond its radius.
        /// </summary>
        private void EvaluateFar(ClientConn client, in InterestClient snapshot)
        {
            if (!ClientTakesFar(client) || client.Interest == null)
            {
                if (client.FarHeld.Count > 0) { ReleaseFar(client, sendGone: client.Welcomed); FlushFar(); }
                return;
            }
            if (_farRecords.Count == 0 && client.FarHeld.Count == 0) return;
            ulong scope = InstanceOf(client);
            var foci = client.Interest.Foci;
            _farCandidates.Clear();
            foreach (var kv in _farRecords)
            {
                var rec = kv.Value;
                ref readonly var e = ref rec.Entry;
                if (client.Visible.Contains(e.NetId) || e.InstanceId != scope || e.NetId == client.PawnNetId) continue;
                double best = double.MaxValue;
                for (int f = 0; f < foci.Count; f++)
                {
                    var focus = foci[f];
                    if (focus.Space != 0) continue;
                    double d2 = e.SqrDistanceTo(focus.X, focus.Y, focus.Z);
                    if (d2 < best) best = d2;
                }
                if (best == double.MaxValue) continue;
                if (!FarRelevance.Within(best, e.Radius, client.FarHeld.Contains(e.NetId), _interest.ExitMargin)) continue;
                var entity = new InterestEntity
                {
                    NetId = e.NetId,
                    PrefabId = e.PrefabId,
                    OwnerClientId = e.OwnerClientId,
                    X = e.X, Y = e.Y, Z = e.Z,
                    RelevanceRadius = e.Radius,
                    InterestGroup = e.InterestGroup,
                    InstanceId = e.InstanceId,
                };
                if (!AuthorizeFar(snapshot, entity)) continue;
                _farCandidates.Add((e.NetId, best));
            }
            if (_farCandidates.Count > _interest.FarMaxEntities) _farCandidates.Sort(NearestFirst);
            _farChosen.Clear();
            for (int i = 0; i < _farCandidates.Count && i < _interest.FarMaxEntities; i++) _farChosen.Add(_farCandidates[i].NetId);
            _farCandidates.Clear();

            // Out first, so a client never holds more than its budget even for the length of one message.
            _farScratch.Clear();
            foreach (ulong held in client.FarHeld) if (!_farChosen.Contains(held)) _farScratch.Add(held);
            for (int i = 0; i < _farScratch.Count; i++)
            {
                ulong id = _farScratch[i];
                client.FarHeld.Remove(id);
                uint epoch = 0;
                if (_farRecords.TryGetValue(id, out var rec)) { rec.Holders.Remove(client); epoch = rec.Entry.Epoch; }
                // An entity that just became a replica is not "gone": the client turns its marker into the replica.
                if (!client.Visible.Contains(id)) QueueFar(client, FarEntityEntry.GoneOf(id, epoch));
            }
            _farScratch.Clear();
            foreach (ulong id in _farChosen)
            {
                if (!client.FarHeld.Add(id)) continue;
                var rec = _farRecords[id];
                rec.Holders.Add(client);
                QueueFar(client, rec.Entry);
            }
            _farChosen.Clear();
            FlushFar();
        }

        /// <summary>The game's say over a far entity: the same policy that authorizes the normal tier. A throwing policy denies.</summary>
        private bool AuthorizeFar(in InterestClient client, in InterestEntity entity)
        {
            try { return _policy.Authorize(client, entity); }
            catch (Exception ex)
            {
                NebulaLog.Error($"interest policy threw authorizing far entity #{entity.NetId}: {ex.Message}");
                return false;
            }
        }

        /// <summary>The client became a replica holder of an entity it held far: the replica replaces the marker, with no "gone".</summary>
        private void PromoteFromFar(ClientConn client, ulong netId)
        {
            if (!client.FarHeld.Remove(netId)) return;
            if (_farRecords.TryGetValue(netId, out var rec)) rec.Holders.Remove(client);
        }

        /// <summary>Let go of a client's whole far tier (it left, or may not have one).</summary>
        private void ReleaseFar(ClientConn client, bool sendGone)
        {
            foreach (ulong id in client.FarHeld)
            {
                uint epoch = 0;
                if (_farRecords.TryGetValue(id, out var rec)) { rec.Holders.Remove(client); epoch = rec.Entry.Epoch; }
                if (sendGone) QueueFar(client, FarEntityEntry.GoneOf(id, epoch));
            }
            client.FarHeld.Clear();
            if (!sendGone) client.FarOut.Clear();
        }

        // ------------------------------------------------------------------------------------------- sending

        private void QueueFar(ClientConn client, in FarEntityEntry entry)
        {
            if (client.FarOut.Count == 0) _farTouched.Add(client);
            client.FarOut.Add(entry);
        }

        /// <summary>Send every client its queued far entries, on the reliable batch (they are rare: one per entity per second).</summary>
        private void FlushFar()
        {
            for (int c = 0; c < _farTouched.Count; c++)
            {
                var client = _farTouched[c];
                var list = client.FarOut;
                if (list.Count == 0) continue;
                if (client.Welcomed && client.ProtocolVersion >= HelloMsg.FarEntitiesVersion && _clientsById.TryGetValue(client.ClientId, out var live) && live == client)
                {
                    for (int from = 0; from < list.Count; from += FarEntitiesMsg.MaxEntries)
                    {
                        int count = Math.Min(FarEntitiesMsg.MaxEntries, list.Count - from);
                        _farWriter.Reset();
                        FarEntitiesMsg.Write(_farWriter, list, from, count);
                        AppendReliable(client, _farWriter.ToSegment());
                        _farEntriesRelayed += count;
                    }
                }
                list.Clear();
            }
            _farTouched.Clear();
        }
    }
}
