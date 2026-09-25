using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>
    /// Sync audiences on the worker and the client (<c>docs/sync-audience.md</c>): which chunks carry which audience,
    /// how a Custom audience is evaluated, what a handover and a reclaimed session do to it, what a gateway is sent,
    /// and how a client drops its copy when it leaves an audience. The gateway's filtering itself runs against a real
    /// gateway in the service tier (<c>Nebula.Services.Tests/ConformanceSyncAudienceTests.cs</c>).
    /// </summary>
    public class SyncAudienceTests
    {
        /// <summary>A sync behaviour holding one int, counting what it is asked to do.</summary>
        internal abstract class Probe : NetworkBehaviour
        {
            public int Value;
            public int Reads, FullReads, Cleared;
            public override bool HasSyncState => true;
            public void Set(int value) { Value = value; MarkSyncDirty(); }
            public override void WriteSyncState(NetworkWriter writer, bool full) => writer.WriteInt(Value);
            public override void ReadSyncState(NetworkReader reader, uint tick, bool full)
            {
                Value = reader.ReadInt();
                Reads++;
                if (full) FullReads++;
            }
            public override void OnSyncStateCleared() { Cleared++; Value = 0; }
        }

        internal sealed class EveryoneProbe : Probe { }
        internal sealed class OwnerProbe : Probe { public override SyncAudience SyncAudience => SyncAudience.Owner; }
        internal sealed class WorkersProbe : Probe { public override SyncAudience SyncAudience => SyncAudience.WorkersOnly; }
        internal sealed class CustomProbe : Probe
        {
            /// <summary>The rule every CustomProbe answers with; set per test.</summary>
            public static Func<ulong, NetworkIdentity, bool> Rule = (_, _) => false;
            public static uint Refresh = 30;
            public int Asked;
            public override SyncAudience SyncAudience => SyncAudience.Custom;
            public override uint SyncAudienceRefreshTicks => Refresh;
            protected override bool IsInSyncAudience(ulong clientId, NetworkIdentity pawn) { Asked++; return Rule(clientId, pawn); }
            public void Changed() => MarkSyncAudienceDirty();
        }

        private readonly List<GameObject> _objects = new List<GameObject>();

        [SetUp]
        public void SetUp()
        {
            NebulaRuntime.Reset();
            NebulaRuntime.IsServer = true;
            CustomProbe.Rule = (_, _) => false;
            CustomProbe.Refresh = 30;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _objects) if (go != null) Object.DestroyImmediate(go);
            _objects.Clear();
            NebulaRuntime.Reset();
        }

        /// <summary>An initialised entity with one probe of each kind asked for, in that order (behaviour index = position).</summary>
        private NetworkIdentity Make(string name, bool authority, params Type[] probes)
        {
            var go = new GameObject(name);
            _objects.Add(go);
            var id = go.AddComponent<NetworkIdentity>();
            foreach (var t in probes) go.AddComponent(t);
            id.Initialize();
            id.IsSpawned = true;
            // Through SetAuthority, as a spawn or a handover does: it opens every stream with a keyframe.
            if (authority) id.SetAuthority(true);
            return id;
        }

        private static List<(byte index, SyncStateCodec.ChunkFlags flags, int value)> Chunks(byte[] envelope)
        {
            var list = new List<(byte, SyncStateCodec.ChunkFlags, int)>();
            if (envelope == null || envelope.Length == 0) return list;
            SyncStateCodec.ReadEnvelope(new NetworkReader(envelope), (index, flags, chunk) =>
                list.Add((index, flags, chunk.Count >= 4 ? new NetworkReader(chunk).ReadInt() : 0)));
            return list;
        }

        private static byte[] Write(NetworkIdentity e, uint tick, bool forGateway)
        {
            var w = new NetworkWriter(256);
            return e.WriteSyncState(w, tick, Delivery.ReliableOrdered, forGateway) > 0 ? w.ToArray() : null;
        }

        // ------------------------------------------------------------------------------------------- chunks and wire

        [Test]
        public void AnEveryoneBehaviourWritesExactlyWhatItDidBeforeAudiences()
        {
            var e = Make("plain", true, typeof(EveryoneProbe));
            e.SetAuthority(true);
            Assert.IsFalse(e.HasRestrictedAudience);
            Assert.IsFalse(e.HasCustomAudience);
            e.GetComponent<EveryoneProbe>().Set(7);
            var chunks = Chunks(Write(e, 1, forGateway: true));
            Assert.AreEqual(1, chunks.Count);
            Assert.AreEqual(SyncStateCodec.ChunkFlags.Full, chunks[0].flags, "no audience bits: the byte is what it always was");

            var spawn = EntitySpawnMsg.From(e, new NetworkWriter(), forGateway: true);
            Assert.AreEqual(0u, spawn.AudienceGeneration);
            Assert.IsNull(spawn.Audience);
            var withAudience = spawn;
            withAudience.AudienceGeneration = 5;
            var a = new NetworkWriter(); spawn.Write(a, MsgId.EntitySpawn);
            var b = new NetworkWriter(); withAudience.Write(b, MsgId.EntitySpawn);
            Assert.AreEqual(a.Length + 4 + 2, b.Length, "the audience section is only written when there is one");
        }

        [Test]
        public void EachChunkCarriesItsAudienceAndAGatewayIsNeverWrittenAWorkersOnlyChunk()
        {
            var e = Make("mixed", true, typeof(EveryoneProbe), typeof(OwnerProbe), typeof(WorkersProbe), typeof(CustomProbe));
            e.SetAuthority(true);
            Assert.IsTrue(e.HasRestrictedAudience);
            Assert.IsTrue(e.HasCustomAudience);

            var toWorkers = Chunks(Write(e, 1, forGateway: false));
            Assert.AreEqual(4, toWorkers.Count, "a ghost worker is written every behaviour");
            Assert.AreEqual(SyncAudience.Everyone, SyncStateCodec.AudienceOf(toWorkers[0].flags));
            Assert.AreEqual(SyncAudience.Owner, SyncStateCodec.AudienceOf(toWorkers[1].flags));
            Assert.AreEqual(SyncAudience.WorkersOnly, SyncStateCodec.AudienceOf(toWorkers[2].flags));
            Assert.AreEqual(SyncAudience.Custom, SyncStateCodec.AudienceOf(toWorkers[3].flags));
            foreach (var c in toWorkers) Assert.IsTrue((c.flags & SyncStateCodec.ChunkFlags.Full) != 0, "the first write after gaining authority is a keyframe");

            var toGateway = Chunks(Write(e, 1, forGateway: true));
            Assert.AreEqual(3, toGateway.Count);
            Assert.IsFalse(toGateway.Exists(c => c.index == 2), "the WorkersOnly behaviour never reaches a gateway");

            var snapshot = new NetworkWriter();
            e.WriteSyncSnapshot(snapshot, forGateway: true);
            Assert.IsFalse(Chunks(snapshot.ToArray()).Exists(c => c.index == 2), "nor does its spawn snapshot");
            var ghostSnapshot = new NetworkWriter();
            e.WriteSyncSnapshot(ghostSnapshot, forGateway: false);
            Assert.IsTrue(Chunks(ghostSnapshot.ToArray()).Exists(c => c.index == 2 && SyncStateCodec.AudienceOf(c.flags) == SyncAudience.WorkersOnly));
        }

        [Test]
        public void AudienceStateRoundTripsInASpawnAHandoverAndASyncMessage()
        {
            var sets = new NetworkWriter();
            SyncAudienceCodec.Write(sets, new[] { new KeyValuePair<byte, ulong[]>(3, new ulong[] { 11, 42 }) });
            var spawn = new EntitySpawnMsg { NetId = 9, Epoch = 2, Vars = Array.Empty<byte>(), State = Array.Empty<byte>(), OwnerIdentity = "", AudienceGeneration = 6, Audience = sets.ToArray() };

            var w = new NetworkWriter();
            spawn.Write(w, MsgId.EntitySpawn);
            var r = new NetworkReader(w.ToArray()); r.ReadByte();
            var back = EntitySpawnMsg.Read(r);
            Assert.AreEqual(6u, back.AudienceGeneration);
            CollectionAssert.AreEqual(new ulong[] { 11, 42 }, SyncAudienceCodec.Read(back.Audience)[3]);

            // Embedded in a handover, the section is always there, so the fields after it still line up.
            var plain = spawn; plain.AudienceGeneration = 0; plain.Audience = null;
            foreach (var entity in new[] { spawn, plain })
            {
                var transfer = new AuthorityTransferMsg { Entity = entity, NewEpoch = 3, GhostWorkers = new[] { "w1" }, SessionGateway = "" };
                var tw = new NetworkWriter(); transfer.Write(tw);
                var tr = new NetworkReader(tw.ToArray()); tr.ReadByte();
                var got = AuthorityTransferMsg.Read(tr);
                Assert.AreEqual(3u, got.NewEpoch);
                Assert.AreEqual(entity.AudienceGeneration, got.Entity.AudienceGeneration);
                CollectionAssert.AreEqual(new[] { "w1" }, got.GhostWorkers);
            }

            Assert.AreEqual(0u, spawn.ForClient().AudienceGeneration, "a client is never sent the generation");
            Assert.IsNull(spawn.ForClient().Audience, "nor the member sets");

            var sync = new EntitySyncMsg { NetId = 9, Epoch = 2, Tick = 5, Chunks = new byte[] { 0 }, AudienceGeneration = 6 };
            var sw = new NetworkWriter(); sync.Write(sw, MsgId.EntityState);
            var sr = new NetworkReader(sw.ToArray()); sr.ReadByte();
            Assert.AreEqual(6u, EntitySyncMsg.Read(sr).AudienceGeneration);
            sync.AudienceGeneration = 0;
            var plainWriter = new NetworkWriter(); sync.Write(plainWriter, MsgId.EntityState);
            Assert.AreEqual(sw.Length - 4, plainWriter.Length, "no generation, no bytes");
        }

        // ------------------------------------------------------------------------------------------- Custom evaluation

        [Test]
        public void ACustomAudienceIsEvaluatedWhenDueAndOnlyAChangeRaisesTheGeneration()
        {
            var pawnA = Make("pawn-a", true);
            var pawnB = Make("pawn-b", true);
            var candidates = new Dictionary<ulong, NetworkIdentity> { [11] = pawnA, [12] = pawnB };
            var scratch = new List<ulong>();
            var e = Make("crate", true, typeof(CustomProbe));
            e.SetAuthority(true);
            var probe = e.GetComponent<CustomProbe>();
            var allowed = new HashSet<ulong> { 11 };
            CustomProbe.Rule = (id, pawn) => allowed.Contains(id) && pawn != null;

            Assert.IsTrue(e.EvaluateSyncAudiences(100, candidates, scratch), "evaluated at once after gaining authority");
            CollectionAssert.AreEqual(new ulong[] { 11 }, probe.AudienceMembers);
            Assert.AreEqual(1u, e.SyncAudienceGeneration);
            Assert.IsTrue(e.AudienceChangedThisTick);
            Assert.AreEqual(2, probe.Asked, "one question per pawn this worker holds");
            Write(e, 101, forGateway: true);
            e.ClearDirty();
            Assert.IsFalse(e.AudienceChangedThisTick);

            allowed.Clear(); allowed.Add(12);
            Assert.IsFalse(e.EvaluateSyncAudiences(101, candidates, scratch), "not due yet, and not marked");
            probe.Changed();
            Assert.IsTrue(e.EvaluateSyncAudiences(102, candidates, scratch), "marked dirty: evaluated at the next tick");
            CollectionAssert.AreEqual(new ulong[] { 12 }, probe.AudienceMembers);
            Assert.AreEqual(2u, e.SyncAudienceGeneration);
            var chunks = Chunks(Write(e, 103, forGateway: true));
            Assert.AreEqual(1, chunks.Count, "the changed behaviour writes this tick though its value did not change");
            Assert.IsTrue((chunks[0].flags & SyncStateCodec.ChunkFlags.Full) != 0, "and writes a keyframe, so the new member starts from current state");
            e.ClearDirty();
            Assert.IsNull(Write(e, 104, forGateway: true), "once written, it is quiet again");

            int asked = probe.Asked;
            Assert.IsFalse(e.EvaluateSyncAudiences(102 + 29, candidates, scratch), "the refresh has not run out");
            Assert.AreEqual(asked, probe.Asked);
            Assert.IsFalse(e.EvaluateSyncAudiences(102 + 30, candidates, scratch), "the refresh re-asks; the same answer changes nothing");
            Assert.Greater(probe.Asked, asked);
            Assert.AreEqual(2u, e.SyncAudienceGeneration);

            allowed.Add(11);
            Assert.IsTrue(e.EvaluateSyncAudiences(102 + 60, candidates, scratch), "the next refresh picks up a change nobody marked");
            CollectionAssert.AreEqual(new ulong[] { 11, 12 }, probe.AudienceMembers, "members are kept sorted");
        }

        [Test]
        public void ARefreshOfZeroEvaluatesOnlyWhenMarked()
        {
            CustomProbe.Refresh = 0;
            var candidates = new Dictionary<ulong, NetworkIdentity> { [11] = Make("pawn", true) };
            var e = Make("crate", true, typeof(CustomProbe));
            e.SetAuthority(true);
            var probe = e.GetComponent<CustomProbe>();
            CustomProbe.Rule = (_, _) => true;
            Assert.IsTrue(e.EvaluateSyncAudiences(1, candidates, new List<ulong>()));
            CustomProbe.Rule = (_, _) => false;
            Assert.IsFalse(e.EvaluateSyncAudiences(10_000, candidates, new List<ulong>()), "no periodic refresh");
            probe.Changed();
            Assert.IsTrue(e.EvaluateSyncAudiences(10_001, candidates, new List<ulong>()));
            Assert.IsEmpty(probe.AudienceMembers);
        }

        [Test]
        public void AnAudienceIsCappedAndKeepsTheLowestSessionIds()
        {
            var candidates = new Dictionary<ulong, NetworkIdentity>();
            var pawn = Make("pawn", true);
            for (ulong id = 1000; id > 700; id--) candidates[id] = pawn;
            var e = Make("crate", true, typeof(CustomProbe));
            e.SetAuthority(true);
            CustomProbe.Rule = (_, _) => true;
            UnityEngine.TestTools.LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("more than the 256"));
            e.EvaluateSyncAudiences(1, candidates, new List<ulong>());
            var members = e.GetComponent<CustomProbe>().AudienceMembers;
            Assert.AreEqual(SyncAudienceCodec.MaxMembers, members.Length);
            Assert.AreEqual(701ul, members[0]);
            Assert.AreEqual(956ul, members[members.Length - 1]);
        }

        [Test]
        public void AReclaimedOwnerForcesAKeyframeOfWhatItIsInTheAudienceOf()
        {
            var e = Make("pawn", true, typeof(EveryoneProbe), typeof(OwnerProbe), typeof(CustomProbe));
            e.OwnerClientId = 11;
            e.SetAuthority(true);
            e.GetComponent<CustomProbe>().AudienceMembers = new ulong[] { 11 };
            Write(e, 1, forGateway: true);
            e.ClearDirty();
            Assert.IsNull(Write(e, 2, forGateway: true), "quiet between keyframes");

            e.ForceAudienceKeyframes(12);
            Assert.IsNull(Write(e, 3, forGateway: true), "another client coming back changes nothing here");
            e.ForceAudienceKeyframes(11);
            var chunks = Chunks(Write(e, 4, forGateway: true));
            CollectionAssert.AreEquivalent(new byte[] { 1, 2 }, chunks.ConvertAll(c => c.index), "the Owner and the Custom behaviour it is in");
            foreach (var c in chunks) Assert.IsTrue((c.flags & SyncStateCodec.ChunkFlags.Full) != 0);
        }

        // ------------------------------------------------------------------------------------------- the client

        [Test]
        public void AClientDropsItsCopyWhenToldItLeftAndIgnoresChunksSentBeforeThat()
        {
            NebulaRuntime.IsServer = false;
            NebulaRuntime.IsClient = true;
            var e = Make("remote", false, typeof(OwnerProbe));
            var probe = e.GetComponent<OwnerProbe>();

            byte[] Envelope(SyncStateCodec.ChunkFlags flags, int? value)
            {
                var w = new NetworkWriter();
                int at = SyncStateCodec.BeginEnvelope(w);
                var payload = new NetworkWriter();
                if (value.HasValue) payload.WriteInt(value.Value);
                SyncStateCodec.WriteRawChunk(w, 0, flags, payload.ToSegment());
                SyncStateCodec.EndEnvelope(w, at, 1);
                return w.ToArray();
            }
            var owner = SyncStateCodec.FlagsOf(SyncAudience.Owner);

            e.ReadSyncState(new NetworkReader(Envelope(SyncStateCodec.ChunkFlags.Full | owner, 5)), 10, null, reliable: true);
            Assert.AreEqual(5, probe.Value);

            e.ReadSyncState(new NetworkReader(Envelope(SyncStateCodec.ChunkFlags.Cleared | owner, null)), 12, null, reliable: true);
            Assert.AreEqual(1, probe.Cleared, "OnSyncStateCleared, and not ReadSyncState");
            Assert.AreEqual(0, probe.Value);
            int reads = probe.Reads;

            e.ReadSyncState(new NetworkReader(Envelope(owner, 6)), 11, null, reliable: false);
            e.ReadSyncState(new NetworkReader(Envelope(SyncStateCodec.ChunkFlags.Full | owner, 7)), 12, null, reliable: false);
            Assert.AreEqual(reads, probe.Reads, "sequenced chunks from before the notice overtook it and are dropped");
            Assert.AreEqual(0, probe.Value);

            e.ReadSyncState(new NetworkReader(Envelope(SyncStateCodec.ChunkFlags.Full | owner, 8)), 12, null, reliable: true);
            Assert.AreEqual(8, probe.Value, "a keyframe on the reliable stream is a rejoin");
            e.ReadSyncState(new NetworkReader(Envelope(owner, 9)), 11, null, reliable: false);
            Assert.AreEqual(9, probe.Value, "after which the stream is read as usual");
        }

        [Test]
        public void TheRootTransformAlwaysReplicatesToEveryone()
        {
            var go = new GameObject("root-owner");
            _objects.Add(go);
            var id = go.AddComponent<NetworkIdentity>();
            go.AddComponent<OwnerOnlyTransform>();
            UnityEngine.TestTools.LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("root NetworkTransform replicates to everyone"));
            id.Initialize();
            Assert.IsFalse(id.HasRestrictedAudience);
        }

        internal sealed class OwnerOnlyTransform : NetworkTransform
        {
            public override SyncAudience SyncAudience => SyncAudience.Owner;
        }
    }

    /// <summary>
    /// Conformance scenario 27 (<c>docs/conformance-suite.md</c>, <c>docs/sync-audience.md</c>): sync audiences across
    /// real workers. Every audience reaches a neighbouring worker's ghost, WorkersOnly included; a gateway is sent the
    /// Custom member sets before the chunks they decide and never a WorkersOnly chunk; the audience generation and the
    /// member sets travel with a handover; and a session that comes back gets fresh keyframes of what it may see.
    /// </summary>
    [Category("Conformance")]
    public sealed class ConformanceSyncAudienceTests
    {
        private ConformanceMesh _mesh;
        private Container _yard;
        private ushort _crate, _pawn;

        [SetUp]
        public void SetUp()
        {
            SyncAudienceTests.CustomProbe.Rule = (_, _) => false;
            SyncAudienceTests.CustomProbe.Refresh = 30;
            _mesh = new ConformanceMesh(2);
            _yard = _mesh.AddStaticContainer("audience-yard", Vector3.zero, new Vector3(200, 60, 200));
            _mesh.SetOwner(_yard, _mesh[0]);

            var crate = new GameObject("audience-crate");
            var id = crate.AddComponent<NetworkIdentity>();
            id.AlwaysRelevant = true;
            crate.AddComponent<SyncAudienceTests.EveryoneProbe>();
            crate.AddComponent<SyncAudienceTests.OwnerProbe>();
            crate.AddComponent<SyncAudienceTests.WorkersProbe>();
            crate.AddComponent<SyncAudienceTests.CustomProbe>();
            _crate = _mesh.RegisterPrefab(crate);

            var pawn = new GameObject("audience-pawn");
            pawn.AddComponent<NetworkIdentity>().AlwaysRelevant = true;
            pawn.AddComponent<SyncAudienceTests.OwnerProbe>();
            _pawn = _mesh.RegisterPrefab(pawn);
        }

        [TearDown]
        public void TearDown()
        {
            _mesh.Dispose();
            SyncAudienceTests.CustomProbe.Rule = (_, _) => false;
        }

        private NetworkIdentity SpawnOwned(ConformanceMesh.Worker worker, ushort prefab, ulong owner, Vector3 at)
        {
            NetworkIdentity identity = null;
            worker.Act(() =>
            {
                identity = NetworkPrefabs.Instantiate(prefab, at, Quaternion.identity, _yard.transform);
                worker.Instance.Spawn(identity, _yard, owner);
            });
            return identity;
        }

        private static void SetAll(NetworkIdentity e, int value)
        {
            foreach (var p in e.GetComponents<SyncAudienceTests.Probe>()) p.Set(value);
        }

        private static List<(byte index, SyncStateCodec.ChunkFlags flags)> ChunksOf(byte[] envelope)
        {
            var list = new List<(byte, SyncStateCodec.ChunkFlags)>();
            if (envelope == null || envelope.Length == 0) return list;
            SyncStateCodec.ReadEnvelope(new NetworkReader(envelope), (index, flags, _) => list.Add((index, flags)));
            return list;
        }

        [Test]
        public void EveryAudienceReachesTheGhostOnAnotherWorker()
        {
            var w1 = _mesh[0];
            var w2 = _mesh[1];
            var sent = w1.SpawnServerDriven(_crate, _yard, new Vector3(5, 0.5f, 5), Quaternion.identity);
            SetAll(sent, 3);
            ulong netId = sent.NetId;

            // The handover carries every behaviour, WorkersOnly included; the sender keeps a ghost.
            w1.Transfer(sent, w2);
            _mesh.Pump();
            var authority = w2.Find(netId);
            Assert.IsTrue(authority.HasAuthority);
            foreach (var p in authority.GetComponents<SyncAudienceTests.Probe>()) Assert.AreEqual(3, p.Value, $"{p.GetType().Name} arrived with the handover");

            SetAll(authority, 4);
            w2.PublishTick(2);
            _mesh.Pump();
            var streamed = _mesh.DeliveredOf(MsgId.GhostSyncState, to: "w1");
            Assert.IsNotEmpty(streamed, "w2 streams the entity's sync state to the ghost w1 kept");
            var indices = new HashSet<byte>();
            foreach (var m in streamed) foreach (var c in ChunksOf(m.Read(EntitySyncMsg.Read).Chunks)) indices.Add(c.index);
            CollectionAssert.AreEquivalent(new byte[] { 0, 1, 2, 3 }, indices, "a worker is sent every audience");

            var ghost = w1.Find(netId);
            Assert.IsFalse(ghost.HasAuthority);
            foreach (var p in ghost.GetComponents<SyncAudienceTests.Probe>()) Assert.AreEqual(4, p.Value, $"the ghost's {p.GetType().Name} follows the stream");
        }

        [Test]
        public void AGatewayGetsTheMemberSetsBeforeTheChunksAndNoWorkersOnlyChunk()
        {
            var w1 = _mesh[0];
            var gw = _mesh.AddGateway("gw");
            _mesh.LinkGateway(gw);
            SpawnOwned(w1, _pawn, 11, new Vector3(1, 0.5f, 1));
            SpawnOwned(w1, _pawn, 12, new Vector3(2, 0.5f, 1));
            var allowed = new HashSet<ulong> { 11 };
            SyncAudienceTests.CustomProbe.Rule = (id, _) => allowed.Contains(id);
            var crate = w1.SpawnServerDriven(_crate, _yard, new Vector3(3, 0.5f, 3), Quaternion.identity);
            SetAll(crate, 1);

            w1.Tick(1);
            _mesh.Pump();
            var toGateway = _mesh.Delivered.FindAll(m => m.To == "gw");
            var spawn = toGateway.Find(m => m.Id == MsgId.EntitySpawn && m.Read(EntitySpawnMsg.Read).NetId == crate.NetId);
            Assert.IsNotNull(spawn.Bytes, "the always-relevant crate is announced to the gateway");
            var spawnChunks = ChunksOf(spawn.Read(EntitySpawnMsg.Read).State);
            Assert.IsFalse(spawnChunks.Exists(c => c.index == 2), "the spawn carries no WorkersOnly chunk");

            int audienceAt = toGateway.FindIndex(m => m.Id == MsgId.SyncAudience);
            int customAt = toGateway.FindIndex(m => m.Id == MsgId.EntityState && m.Read(EntitySyncMsg.Read).NetId == crate.NetId &&
                                                     ChunksOf(m.Read(EntitySyncMsg.Read).Chunks).Exists(c => c.index == 3));
            Assert.GreaterOrEqual(audienceAt, 0, "the member sets were sent");
            Assert.Greater(customAt, audienceAt, "and before the Custom chunk they decide");
            var audience = toGateway[audienceAt].Read(SyncAudienceMsg.Read);
            Assert.AreEqual(1u, audience.Generation);
            CollectionAssert.AreEqual(new ulong[] { 11 }, SyncAudienceCodec.Read(audience.Sets)[3]);
            Assert.AreEqual(1u, toGateway[customAt].Read(EntitySyncMsg.Read).AudienceGeneration, "chunks are stamped with the generation");
            foreach (var m in toGateway)
                if (m.Id == MsgId.EntityState && m.Read(EntitySyncMsg.Read).NetId == crate.NetId)
                    Assert.IsFalse(ChunksOf(m.Read(EntitySyncMsg.Read).Chunks).Exists(c => c.index == 2), "no WorkersOnly chunk, ever");

            // The rule's answer changes: new sets, a new generation, and a keyframe for the new member.
            allowed.Clear(); allowed.Add(12);
            crate.GetComponent<SyncAudienceTests.CustomProbe>().Changed();
            _mesh.Delivered.Clear();
            w1.Tick(2);
            _mesh.Pump();
            toGateway = _mesh.Delivered.FindAll(m => m.To == "gw");
            audience = toGateway.Find(m => m.Id == MsgId.SyncAudience).Read(SyncAudienceMsg.Read);
            Assert.AreEqual(2u, audience.Generation);
            CollectionAssert.AreEqual(new ulong[] { 12 }, SyncAudienceCodec.Read(audience.Sets)[3]);
            var keyframe = toGateway.Find(m => m.Id == MsgId.EntityState && m.Read(EntitySyncMsg.Read).NetId == crate.NetId);
            Assert.IsTrue(ChunksOf(keyframe.Read(EntitySyncMsg.Read).Chunks).Exists(c => c.index == 3 && (c.flags & SyncStateCodec.ChunkFlags.Full) != 0),
                "the Custom behaviour writes a keyframe with its new sets");

            // Nothing changed: nothing more is said.
            _mesh.Delivered.Clear();
            w1.Tick(3);
            _mesh.Pump();
            Assert.IsFalse(_mesh.Delivered.Exists(m => m.Id == MsgId.SyncAudience));
        }

        [Test]
        public void TheGenerationAndTheMemberSetsTravelWithAHandover()
        {
            var w1 = _mesh[0];
            var w2 = _mesh[1];
            SpawnOwned(w1, _pawn, 11, new Vector3(1, 0.5f, 1));
            SyncAudienceTests.CustomProbe.Rule = (id, _) => id == 11;
            var crate = w1.SpawnServerDriven(_crate, _yard, new Vector3(3, 0.5f, 3), Quaternion.identity);
            w1.Tick(1);
            Assert.AreEqual(1u, crate.SyncAudienceGeneration);
            ulong netId = crate.NetId;

            w1.Transfer(crate, w2);
            _mesh.Pump();
            var landed = w2.Find(netId);
            Assert.AreEqual(1u, landed.SyncAudienceGeneration, "the generation never goes back");
            CollectionAssert.AreEqual(new ulong[] { 11 }, landed.GetComponent<SyncAudienceTests.CustomProbe>().AudienceMembers,
                "the new authority starts from its predecessor's answer");

            // w2 asks its own pawns. It holds the pawn of 11 only if it holds a copy of it; here it holds none, so the
            // audience is empty from now on: a client whose pawn a worker does not hold is not in its audiences.
            _mesh.SetOwner(_yard, w2);
            w2.Tick(2);
            Assert.AreEqual(2u, landed.SyncAudienceGeneration);
            Assert.IsEmpty(landed.GetComponent<SyncAudienceTests.CustomProbe>().AudienceMembers);
        }

        [Test]
        public void AReturningSessionGetsAFreshKeyframeOfItsOwnerState()
        {
            var w1 = _mesh[0];
            var gw = _mesh.AddGateway("gw");
            _mesh.LinkGateway(gw);
            var pawn = SpawnOwned(w1, _pawn, 11, new Vector3(1, 0.5f, 1));
            pawn.GetComponent<SyncAudienceTests.OwnerProbe>().Set(5);
            w1.Tick(1);
            w1.Tick(2);
            _mesh.Pump();
            _mesh.Delivered.Clear();
            w1.Tick(3);
            _mesh.Pump();
            Assert.IsFalse(_mesh.Delivered.Exists(m => m.Id == MsgId.EntityState && m.Read(EntitySyncMsg.Read).NetId == pawn.NetId),
                "a reliable behaviour that did not change is quiet");

            // The session claims its pawn again (a reconnection, or a move to another gateway).
            _mesh.FromGateway(gw, w1, w => new SpawnPlayerMsg { ClientId = 11, Name = "ann", Identity = "", Generation = 2, Container = ContainerRef.None }.Write(w));
            _mesh.Delivered.Clear();
            w1.Tick(4);
            _mesh.Pump();
            var state = _mesh.Delivered.Find(m => m.To == "gw" && m.Id == MsgId.EntityState && m.Read(EntitySyncMsg.Read).NetId == pawn.NetId);
            Assert.IsNotNull(state.Bytes, "the Owner behaviour writes again");
            var chunks = ChunksOf(state.Read(EntitySyncMsg.Read).Chunks);
            Assert.IsTrue(chunks.Exists(c => SyncStateCodec.AudienceOf(c.flags) == SyncAudience.Owner && (c.flags & SyncStateCodec.ChunkFlags.Full) != 0),
                "a keyframe, so the new connection starts from current state");
        }
    }
}
