using System;
using System.Collections.Generic;
#if NEBULA_SERVICE
using Nebula.ServicePrimitives;
#else
using UnityEngine;
#endif

namespace Nebula
{
    /// <summary>What a gateway remembers about one entity in one client's far tier (D111).</summary>
    internal struct FarHoldState
    {
        /// <summary>The client's short name for it in compact poses; 0 for a client older than protocol 32.</summary>
        public ushort Slot;
        /// <summary>The record's <c>Facts</c> when the client was last sent a full entry: a compact pose may follow only while they match.</summary>
        public int Facts;
        /// <summary>The position of the last full entry the client was sent: what its compact poses are offsets from.</summary>
        public double AnchorX, AnchorY, AnchorZ;
    }

    /// <summary>One client's far slots: small numbers handed out on a full entry and taken back when the entity leaves (D111).</summary>
    internal sealed class FarSlots
    {
        private readonly Stack<ushort> _free = new Stack<ushort>();
        private ushort _next = 1;

        /// <summary>A free slot, or 0 when all <see cref="FarPosesMsg.MaxSlot"/> are taken (the entity then goes as full entries).</summary>
        public ushort Take()
        {
            if (_free.Count > 0) return _free.Pop();
            if (_next > FarPosesMsg.MaxSlot) return 0;
            return _next++;
        }

        public void Give(ushort slot)
        {
            if (slot != 0) _free.Push(slot);
        }

        public void Clear()
        {
            _free.Clear();
            _next = 1;
        }
    }

    /// <summary>
    /// The gateway's half of the far relevance tier (<c>docs/interest-management.md</c> §16, NEB-388). Workers send the
    /// poses of their far entities to the gateways whose foci they reach (<see cref="FarPublisher"/>); the gateway keeps
    /// the newest of each (<see cref="FarRecord"/>), decides per client at its interest evaluation which of them are in
    /// its far tier, and relays each new pose to the clients that hold it. A far entity is not an
    /// <see cref="EntityRecord"/>: it has no container, no state stream and no observers, and it never enters a client's
    /// interest set through this path. The normal tier wins: an entity a client holds as a replica is never in its far
    /// tier, and entering the replica set takes it out silently (the client turns the marker into the replica).
    /// <para>
    /// Protocol 32: each class (<see cref="FarClass"/>, D110) has its own budget per client; a client of 32 is sent a
    /// full entry when an entity enters its far tier or its facts change, and compact poses (<see cref="FarPosesMsg"/>,
    /// D111) in between; and an entity leaving its replica set is sent as a far marker just before its despawn (D112).
    /// </para>
    /// </summary>
    public sealed partial class NebulaGateway
    {
        private sealed class FarRecord
        {
            public FarEntityEntry Entry;
            public ushort WorkerIndex;
            /// <summary>Gateway clock time of the newest entry; a record not refreshed for <see cref="FarExpirySeconds"/> is dropped.</summary>
            public double ReceivedAt;
            /// <summary>Goes up whenever anything but the pose changes (epoch, prefab, owner, group, scope, radius, rate, class).</summary>
            public int Facts;
            /// <summary>The clients that hold it in their far tier: the exact audience of each new pose.</summary>
            public readonly List<ClientConn> Holders = new List<ClientConn>();
        }

        private readonly Dictionary<ulong, FarRecord> _farRecords = new Dictionary<ulong, FarRecord>();
        private readonly List<FarEntityEntry> _farIn = new List<FarEntityEntry>();
        private readonly List<ClientConn> _farTouched = new List<ClientConn>();
        private readonly List<ulong> _farScratch = new List<ulong>();
        private readonly FarSelection _farSelection = new FarSelection();
        private readonly HashSet<ulong> _farChosen = new HashSet<ulong>();
        private readonly NetworkWriter _farWriter = new NetworkWriter(1024);
        private FarClassTable _farClassTable;
        private FarClass[] _farClassTableFrom;
        private int _farClassTableDefault = -1, _farClassTableTotal = -1;
        private long _farEntriesRelayed, _farPosesRelayed, _farBytesRelayed;

        /// <summary>Far entities this gateway holds a pose for (NEB-388).</summary>
        public int FarEntityCount => _farRecords.Count;
        /// <summary>Far-tier entries sent to clients since start (enters, updates and leaves, full and compact).</summary>
        public long FarEntriesRelayed => _farEntriesRelayed;
        /// <summary>Of <see cref="FarEntriesRelayed"/>, the compact poses (<see cref="FarPosesMsg"/>, protocol 32).</summary>
        public long FarPosesRelayed => _farPosesRelayed;
        /// <summary>Bytes of far-tier messages sent to clients since start.</summary>
        public long FarBytesRelayed => _farBytesRelayed;

        /// <summary>The far classes in use (D110), resolved from the interest settings.</summary>
        internal FarClassTable FarClasses
        {
            get
            {
                if (_farClassTable == null || !ReferenceEquals(_farClassTableFrom, _interest.FarClasses) ||
                    _farClassTableDefault != _interest.FarMaxEntities || _farClassTableTotal != _interest.FarMaxTotal)
                {
                    _farClassTable = FarClassTable.From(_interest);
                    _farClassTableFrom = _interest.FarClasses;
                    _farClassTableDefault = _interest.FarMaxEntities;
                    _farClassTableTotal = _interest.FarMaxTotal;
                }
                return _farClassTable;
            }
        }

        /// <summary>Whether a client holds an entity in its far tier (tests and diagnostics); false for an unknown client.</summary>
        public bool ClientHoldsFar(ulong clientId, ulong netId) => _clientsById.TryGetValue(clientId, out var c) && c.FarHeld.ContainsKey(netId);

        /// <summary>How many entities a client holds in its far tier; -1 for an unknown client.</summary>
        public int FarTierSize(ulong clientId) => _clientsById.TryGetValue(clientId, out var c) ? c.FarHeld.Count : -1;

        /// <summary>How many entities of far class <paramref name="farClass"/> a client holds (D110); -1 for an unknown client.</summary>
        public int FarTierSize(ulong clientId, byte farClass)
        {
            if (!_clientsById.TryGetValue(clientId, out var c)) return -1;
            int n = 0;
            foreach (ulong id in c.FarHeld.Keys) if (_farRecords.TryGetValue(id, out var rec) && rec.Entry.FarClass == farClass) n++;
            return n;
        }

        /// <summary>
        /// Seconds without a pose after which a far record is dropped: three of its updates, and never less than five
        /// seconds, so a handover (the new owner starts sending at its next match) does not take the marker away.
        /// </summary>
        private static double FarExpirySeconds(in FarEntityEntry entry) => Math.Max(5.0, 3.0 / FarRelevance.ClampRate(entry.UpdateRate));

        private bool ClientTakesFar(ClientConn client) =>
            client.Welcomed && client.ProtocolVersion >= HelloMsg.FarEntitiesVersion && FarClasses.AnyBudget;

        private static bool SameFacts(in FarEntityEntry a, in FarEntityEntry b) =>
            a.Epoch == b.Epoch && a.PrefabId == b.PrefabId && a.InterestGroup == b.InterestGroup && a.Radius == b.Radius &&
            a.UpdateRate == b.UpdateRate && a.OwnerClientId == b.OwnerClientId && a.InstanceId == b.InstanceId && a.FarClass == b.FarClass;

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
                else if (!SameFacts(rec.Entry, entry)) rec.Facts++;
                entry.Slot = 0;
                entry.Age = 0f;
                entry.FromReplica = false;
                rec.Entry = entry;
                rec.WorkerIndex = w.Index;
                rec.ReceivedAt = now;
                for (int h = rec.Holders.Count - 1; h >= 0; h--)
                {
                    var client = rec.Holders[h];
                    // Became a replica since the last evaluation (an arrival): the marker is the replica now.
                    if (client.Visible.Contains(entry.NetId)) { ForgetHeld(client, entry.NetId); rec.Holders.RemoveAt(h); continue; }
                    QueueFarUpdate(client, rec);
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
                if (ForgetHeld(client, rec.Entry.NetId)) QueueFar(client, gone);
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
        /// The nearest squared distance from one of a client's foci in the scope's own space (frame key 0) to a far entity of
        /// its own scope, when it is within the entity's far radius (with the tier's hysteresis when the client holds it) and
        /// the policy authorizes it; <see cref="double.NaN"/> when the client may not have it.
        /// </summary>
        private double FarCandidateDistance(ClientConn client, in InterestClient snapshot, FarRecord rec, ulong scope)
        {
            var e = rec.Entry;
            if (e.InstanceId != scope || e.NetId == client.PawnNetId) return double.NaN;
            var foci = client.Interest.Foci;
            double best = double.MaxValue;
            for (int f = 0; f < foci.Count; f++)
            {
                var focus = foci[f];
                if (focus.Space != 0) continue;
                double d2 = e.SqrDistanceTo(focus.X, focus.Y, focus.Z);
                if (d2 < best) best = d2;
            }
            if (best == double.MaxValue) return double.NaN;
            if (!FarRelevance.Within(best, e.Radius, client.FarHeld.ContainsKey(e.NetId), _interest.ExitMargin)) return double.NaN;
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
            return AuthorizeFar(snapshot, entity) ? best : double.NaN;
        }

        /// <summary>
        /// Bring one client's far tier up to date, after its interest set: the far entities of its own scope within their
        /// far radius of one of its foci in the scope's own space (frame key 0, which every client with a pawn has: its
        /// own, or the enclosing focus around a pawn on a planet), measured in double, that it does not hold as replicas
        /// and that the policy authorizes, nearest first within each far class up to the class's budget
        /// (<see cref="FarClass.MaxEntities"/>; the default class's is <see cref="InterestSettings.FarMaxEntities"/>), classes
        /// served by priority while <see cref="InterestSettings.FarMaxTotal"/> allows. An entity already held stays until it
        /// is <see cref="FarRelevance.StayMargin"/> beyond its radius.
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
            var classes = FarClasses;
            _farSelection.Clear();
            foreach (var kv in _farRecords)
            {
                var rec = kv.Value;
                if (client.Visible.Contains(rec.Entry.NetId)) continue;
                double d2 = FarCandidateDistance(client, snapshot, rec, scope);
                if (double.IsNaN(d2)) continue;
                _farSelection.Add(classes, rec.Entry.NetId, rec.Entry.FarClass, d2);
            }
            _farChosen.Clear();
            _farSelection.Choose(classes, _farChosen);

            // Out first, so a client never holds more than its budget even for the length of one message.
            _farScratch.Clear();
            foreach (ulong held in client.FarHeld.Keys) if (!_farChosen.Contains(held)) _farScratch.Add(held);
            for (int i = 0; i < _farScratch.Count; i++)
            {
                ulong id = _farScratch[i];
                ForgetHeld(client, id);
                uint epoch = 0;
                if (_farRecords.TryGetValue(id, out var rec)) { rec.Holders.Remove(client); epoch = rec.Entry.Epoch; }
                // An entity that just became a replica is not "gone": the client turns its marker into the replica.
                if (!client.Visible.Contains(id)) QueueFar(client, FarEntityEntry.GoneOf(id, epoch));
            }
            _farScratch.Clear();
            foreach (ulong id in _farChosen)
            {
                if (client.FarHeld.ContainsKey(id)) continue;
                var rec = _farRecords[id];
                rec.Holders.Add(client);
                QueueFarEnter(client, rec, fromReplica: false);
            }
            _farChosen.Clear();
            FlushFar();
        }

        /// <summary>
        /// D112: an entity is leaving a client's replica set. When the far tier will have it (its record is here, the client
        /// negotiated protocol 32, it is within its far radius and the policy allows it), the client is sent its far entry
        /// now, flagged <see cref="FarEntityEntry.FromReplica"/>, ahead of the despawn, so a client never holds neither. The
        /// budget is settled by the <see cref="EvaluateFar"/> right after; a hand-over is the nearest of its class, so it is
        /// kept unless the class is already full of nearer ones.
        /// </summary>
        private void DemoteToFar(ClientConn client, ulong netId)
        {
            if (client.ProtocolVersion < HelloMsg.FarClassesVersion || client.FarHeld.ContainsKey(netId)) return;
            if (!ClientTakesFar(client) || client.Interest == null || !_farRecords.TryGetValue(netId, out var rec)) return;
            if (FarClasses.Of(rec.Entry.FarClass).MaxEntities <= 0) return;
            if (double.IsNaN(FarCandidateDistance(client, client.Interest.Client, rec, InstanceOf(client)))) return;
            rec.Holders.Add(client);
            QueueFarEnter(client, rec, fromReplica: true);
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
            if (!ForgetHeld(client, netId)) return;
            if (_farRecords.TryGetValue(netId, out var rec)) rec.Holders.Remove(client);
        }

        /// <summary>Let go of a client's whole far tier (it left, or may not have one).</summary>
        private void ReleaseFar(ClientConn client, bool sendGone)
        {
            foreach (ulong id in client.FarHeld.Keys)
            {
                uint epoch = 0;
                if (_farRecords.TryGetValue(id, out var rec)) { rec.Holders.Remove(client); epoch = rec.Entry.Epoch; }
                if (sendGone) QueueFar(client, FarEntityEntry.GoneOf(id, epoch));
            }
            client.FarHeld.Clear();
            client.FarSlots.Clear();
            if (!sendGone) client.FarOut.Clear();
        }

        /// <summary>Take an entity out of a client's far tier and give its slot back. False when it was not held.</summary>
        private static bool ForgetHeld(ClientConn client, ulong netId)
        {
            if (!client.FarHeld.TryGetValue(netId, out var held)) return false;
            client.FarHeld.Remove(netId);
            client.FarSlots.Give(held.Slot);
            return true;
        }

        // ------------------------------------------------------------------------------------------- sending

        /// <summary>An entity enters a client's far tier: its full entry, with a slot, the age of the stored pose and the hand-over flag (32).</summary>
        private void QueueFarEnter(ClientConn client, FarRecord rec, bool fromReplica)
        {
            var held = new FarHoldState();
            var entry = rec.Entry;
            if (client.ProtocolVersion >= HelloMsg.FarClassesVersion)
            {
                held.Slot = client.FarSlots.Take();
                entry.Slot = held.Slot;
                entry.Age = (float)Math.Max(0.0, InterestNow - rec.ReceivedAt);
                entry.FromReplica = fromReplica;
            }
            SendFull(client, rec, held, entry);
        }

        /// <summary>A new pose for an entity a client holds: compact when the client and the facts allow (D111), else a full entry.</summary>
        private void QueueFarUpdate(ClientConn client, FarRecord rec)
        {
            if (!client.FarHeld.TryGetValue(rec.Entry.NetId, out var held)) return;
            var entry = rec.Entry;
            if (held.Slot != 0 && held.Facts == rec.Facts)
            {
                double dx = entry.X - held.AnchorX, dy = entry.Y - held.AnchorY, dz = entry.Z - held.AnchorZ;
                if (FarPosesMsg.Fits(dx, dy, dz))
                {
                    QueueFar(client, new FarEntityEntry
                    {
                        NetId = entry.NetId,
                        Kind = FarEntryKind.Pose,
                        Slot = held.Slot,
                        Upright = FarClasses.Of(entry.FarClass).Upright,
                        X = dx, Y = dy, Z = dz,
                        Rotation = entry.Rotation,
                        Velocity = entry.Velocity,
                    });
                    return;
                }
            }
            entry.Slot = held.Slot;
            SendFull(client, rec, held, entry);
        }

        private void SendFull(ClientConn client, FarRecord rec, FarHoldState held, in FarEntityEntry entry)
        {
            held.Facts = rec.Facts;
            held.AnchorX = entry.X;
            held.AnchorY = entry.Y;
            held.AnchorZ = entry.Z;
            client.FarHeld[entry.NetId] = held;
            QueueFar(client, entry);
        }

        private void QueueFar(ClientConn client, in FarEntityEntry entry)
        {
            if (client.FarOut.Count == 0) _farTouched.Add(client);
            client.FarOut.Add(entry);
        }

        /// <summary>
        /// Send every client its queued far entries, on the reliable batch, in order: runs of full entries go as
        /// <see cref="FarEntitiesMsg"/>s at the client's protocol, runs of compact poses as <see cref="FarPosesMsg"/>s.
        /// </summary>
        private void FlushFar()
        {
            for (int c = 0; c < _farTouched.Count; c++)
            {
                var client = _farTouched[c];
                var list = client.FarOut;
                if (list.Count == 0) continue;
                if (client.Welcomed && client.ProtocolVersion >= HelloMsg.FarEntitiesVersion && _clientsById.TryGetValue(client.ClientId, out var live) && live == client)
                {
                    int from = 0;
                    while (from < list.Count)
                    {
                        bool poses = list[from].Kind == FarEntryKind.Pose;
                        int max = poses ? FarPosesMsg.MaxEntries : FarEntitiesMsg.MaxEntries;
                        int count = 1;
                        while (from + count < list.Count && count < max && (list[from + count].Kind == FarEntryKind.Pose) == poses) count++;
                        _farWriter.Reset();
                        if (poses) FarPosesMsg.Write(_farWriter, list, from, count);
                        else FarEntitiesMsg.Write(_farWriter, list, from, count, client.ProtocolVersion);
                        AppendReliable(client, _farWriter.ToSegment());
                        _farEntriesRelayed += count;
                        if (poses) _farPosesRelayed += count;
                        _farBytesRelayed += _farWriter.Length;
                        from += count;
                    }
                }
                list.Clear();
            }
            _farTouched.Clear();
        }
    }
}
