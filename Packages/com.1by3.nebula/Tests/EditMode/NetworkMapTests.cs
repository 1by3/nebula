using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nebula.Tests
{
    /// <summary>
    /// <see cref="NetworkMap{TKey, TValue}"/> on its own (docs/replicated-collections.md): what a delta carries, that
    /// it is idempotent over a full copy, the change events on both sides, the size cap, and the full copy as the
    /// persisted entry. The mesh scenarios are <see cref="ConformanceNetworkMapTests"/>.
    /// </summary>
    public sealed class NetworkMapTests
    {
        private static int EntriesIn(NetworkMapBase map, bool full, out bool clear)
        {
            var w = new NetworkWriter();
            if (full) map.WriteFull(w); else map.WriteDelta(w);
            var entries = new List<NetworkMapCodec.Entry>();
            NetworkMapCodec.ReadBody(new NetworkReader(w.ToArray()), out clear, entries);
            return entries.Count;
        }

        private static byte[] Delta(NetworkMapBase map)
        {
            var w = new NetworkWriter();
            map.WriteDelta(w);
            map.ClearChanges();
            return w.ToArray();
        }

        private static byte[] Full(NetworkMapBase map)
        {
            var w = new NetworkWriter();
            map.WriteFull(w);
            return w.ToArray();
        }

        private static void Apply(NetworkMapBase map, byte[] body) => map.ApplyBody(new NetworkReader(body));

        private static NetworkMap<int, string> Filled(int count)
        {
            var map = new NetworkMap<int, string>();
            for (int i = 0; i < count; i++) map[i] = "item" + i;
            map.ClearChanges();
            return map;
        }

        [Test]
        public void ADeltaCarriesOnlyTheTouchedKeysEachOnce()
        {
            var map = Filled(100);
            map[5] = "a";
            map[5] = "b";
            map[5] = "c";
            map.Remove(7);
            map[200] = "new";
            Assert.AreEqual(3, EntriesIn(map, full: false, out bool clear), "three keys touched, however often");
            Assert.IsFalse(clear);

            var copy = Filled(100);
            Apply(copy, Delta(map));
            Assert.AreEqual("c", copy[5]);
            Assert.IsFalse(copy.ContainsKey(7));
            Assert.AreEqual("new", copy[200]);
            Assert.AreEqual(100, copy.Count);
            Assert.AreEqual(0, EntriesIn(map, full: false, out _), "sent is sent");
        }

        [Test]
        public void SettingAnEqualValueSendsNothing()
        {
            var map = Filled(3);
            map[1] = "item1";
            Assert.IsFalse(map.Dirty);
            Assert.AreEqual(0, EntriesIn(map, full: false, out _));
        }

        [Test]
        public void ADeltaIsIdempotentOverACopyThatAlreadyHasIt()
        {
            var map = Filled(10);
            map[3] = "x";
            map.Remove(4);
            map.Clear();
            map[1] = "after";
            // A snapshot taken in the same tick (a ghost spawn, a gateway subscribing) already has every change.
            var snapshot = Full(map);
            var delta = Delta(map);
            var copy = new NetworkMap<int, string>();
            Apply(copy, snapshot);
            var seen = new List<NetworkMapChange<int, string>>();
            copy.OnChanged += c => seen.Add(c);
            Apply(copy, delta);
            Assert.AreEqual(1, copy.Count);
            Assert.AreEqual("after", copy[1]);
            Assert.IsEmpty(seen, "the delta changed nothing a copy with it already held");
        }

        [Test]
        public void AClearThenSetsSendsTheClearAndOnlyTheSets()
        {
            var map = Filled(50);
            map.Clear();
            map[1] = "one";
            map[2] = "two";
            Assert.AreEqual(2, EntriesIn(map, full: false, out bool clear));
            Assert.IsTrue(clear);
            var copy = Filled(50);
            Apply(copy, Delta(map));
            CollectionAssert.AreEquivalent(new[] { 1, 2 }, copy.Keys);
        }

        [Test]
        public void ChangesAreRaisedPerKeyOnTheWriterAndOnTheReceiver()
        {
            var map = Filled(2);
            var written = new List<NetworkMapChange<int, string>>();
            map.OnChanged += c => written.Add(c);
            map[0] = "changed";
            map[9] = "added";
            map.Remove(1);
            Assert.AreEqual(new[] { NetworkMapChangeKind.Updated, NetworkMapChangeKind.Added, NetworkMapChangeKind.Removed },
                written.ConvertAll(c => c.Kind).ToArray());
            Assert.AreEqual("item0", written[0].OldValue);
            Assert.AreEqual("changed", written[0].NewValue);

            var copy = Filled(2);
            var received = new List<NetworkMapChange<int, string>>();
            copy.OnChanged += c => received.Add(c);
            Apply(copy, Delta(map));
            Assert.AreEqual(3, received.Count);
            Assert.IsTrue(received.Exists(c => c.Kind == NetworkMapChangeKind.Removed && c.Key == 1 && c.OldValue == "item1"));
            Assert.IsTrue(received.Exists(c => c.Kind == NetworkMapChangeKind.Updated && c.Key == 0 && c.NewValue == "changed"));
            Assert.IsTrue(received.Exists(c => c.Kind == NetworkMapChangeKind.Added && c.Key == 9));
        }

        [Test]
        public void AFullCopyOnTopOfContentsIsDiffed()
        {
            var copy = Filled(4); // 0..3
            var authority = Filled(4);
            authority.Remove(0);
            authority[1] = "different";
            authority[7] = "new";
            authority.ClearChanges();
            var received = new List<NetworkMapChange<int, string>>();
            copy.OnChanged += c => received.Add(c);
            Apply(copy, Full(authority));
            Assert.AreEqual(3, received.Count, "keys 2 and 3 are equal and raise nothing");
            Assert.AreEqual(NetworkMapChangeKind.Removed, received[0].Kind, "removals first");
            Assert.AreEqual(0, received[0].Key);
            CollectionAssert.AreEquivalent(new[] { 1, 2, 3, 7 }, copy.Keys);
        }

        [Test]
        public void ClearRaisesARemovalForEachKey()
        {
            var map = Filled(3);
            var seen = new List<NetworkMapChange<int, string>>();
            map.OnChanged += c => seen.Add(c);
            map.Clear();
            Assert.AreEqual(3, seen.Count);
            Assert.IsTrue(seen.TrueForAll(c => c.Kind == NetworkMapChangeKind.Removed));
            Assert.AreEqual(0, map.Count);
        }

        [Test]
        public void TheSizeCapRefusesAWriteAndChangesNothing()
        {
            var map = new NetworkMap<int, string>(null, capacityBytes: 200);
            string big = new string('x', 60);
            Assert.IsTrue(map.TrySet(1, big));
            Assert.IsTrue(map.TrySet(2, big));
            Assert.IsFalse(map.TrySet(3, big), "a third entry would pass 200 bytes");
            Assert.IsFalse(map.ContainsKey(3));
            Assert.Throws<InvalidOperationException>(() => map[3] = big);
            Assert.LessOrEqual(map.EncodedBytes, 200);
            Assert.AreEqual(Full(map).Length, map.EncodedBytes, "the tracked size is the encoded size");
            map[1] = "small";
            Assert.IsTrue(map.TrySet(3, big), "room was made");
            Assert.AreEqual(NetworkMapBase.MaxEncodedBytes, new NetworkMap<int, int>().Capacity);
        }

        [Test]
        public void AddRefusesAKeyThatIsThere()
        {
            var map = Filled(1);
            Assert.Throws<ArgumentException>(() => map.Add(0, "again"));
        }

        [Test]
        public void ANewerEncodingIsRefusedAndLeavesTheMap()
        {
            var map = Filled(2);
            var body = Full(Filled(5));
            body[0] = NetworkMapCodec.Version + 1;
            Assert.Throws<FormatException>(() => Apply(map, body));
            Assert.AreEqual(2, map.Count);
        }

        [Test]
        public void TheGatewaysCacheFoldsDeltasIntoTheCurrentContents()
        {
            var map = Filled(3);
            byte[] Section(Action<NetworkWriter> body)
            {
                var w = new NetworkWriter();
                int count = NetworkMapCodec.BeginSection(w);
                int at = NetworkMapCodec.BeginMap(w, 0);
                body(w);
                NetworkMapCodec.EndMap(w, at);
                NetworkMapCodec.EndSection(w, count, 1);
                return w.ToArray();
            }
            var full = Section(w => map.WriteFull(w));
            map[0] = "zero";
            map.Remove(2);
            var delta = Section(w => map.WriteDelta(w));
            map.ClearChanges();
            var folded = NetworkMapCache.Fold(full, delta);
            var copy = new NetworkMap<int, string>();
            NetworkMapCodec.ReadSection(new ArraySegment<byte>(folded), (index, body) => copy.ApplyBody(new NetworkReader(body)));
            CollectionAssert.AreEquivalent(new[] { 0, 1 }, copy.Keys);
            Assert.AreEqual("zero", copy[0]);
        }
    }

    /// <summary>
    /// Replicated maps across real workers (conformance scenario 29, <c>docs/replicated-collections.md</c>): an entity
    /// with a map and a variable on the <see cref="ConformanceMesh"/>. A variable change never resends the map and a
    /// map change never resends the variables; a gateway is sent the full copy with the spawn and then only what
    /// changed; a handover carries changes not sent yet; a ghost gets the full copy and then increments; a persisted
    /// map round-trips and a restore onto a live entity is resent whole.
    /// </summary>
    [Category("Conformance")]
    public sealed class ConformanceNetworkMapTests
    {
        internal sealed class Stash : NetworkBehaviour
        {
            public NetworkVariable<int> Level = new NetworkVariable<int>();
            [Persist] public NetworkMap<int, string> Items = new NetworkMap<int, string>();
            public NetworkMap<string, int> Counts = new NetworkMap<string, int>();
        }

        internal sealed class Historic : NetworkBehaviour
        {
            [SyncHistory] public NetworkMap<int, int> Log = new NetworkMap<int, int>();
        }

        private ConformanceMesh _mesh;
        private Container _yard;
        private ushort _prefab;

        [SetUp]
        public void SetUp()
        {
            _mesh = new ConformanceMesh(2);
            _yard = _mesh.AddStaticContainer("map-yard", Vector3.zero, new Vector3(200, 60, 200));
            _mesh.SetOwner(_yard, _mesh[0]);
            var prefab = new GameObject("map-stash");
            prefab.AddComponent<NetworkIdentity>().AlwaysRelevant = true;
            prefab.AddComponent<Stash>();
            _prefab = _mesh.RegisterPrefab(prefab);
        }

        [TearDown]
        public void TearDown() => _mesh.Dispose();

        private NetworkIdentity Spawn(ConformanceMesh.Worker worker) =>
            worker.SpawnServerDriven(_prefab, _yard, new Vector3(3, 0.5f, 3), Quaternion.identity);

        private static int Entries(byte[] section)
        {
            int n = 0;
            NetworkMapCodec.ReadSection(new ArraySegment<byte>(section ?? Array.Empty<byte>()), (index, body) =>
            {
                var entries = new List<NetworkMapCodec.Entry>();
                NetworkMapCodec.ReadBody(new NetworkReader(body), out bool clear, entries);
                n += entries.Count;
            });
            return n;
        }

        [Test]
        public void MapsAreNotVariables()
        {
            var e = Spawn(_mesh[0]);
            var stash = e.GetComponent<Stash>();
            for (int i = 0; i < 20; i++) stash.Items[i] = "item" + i;
            stash.Level.Value = 7;
            var w = new NetworkWriter();
            e.WriteVars(w);
            Assert.AreEqual(4, w.Length, "the variable block holds the int and nothing of the maps");
            Assert.AreEqual(2, e.Maps.Length);
            Assert.AreEqual(0, stash.Items.MapIndex);
            Assert.AreEqual(1, stash.Counts.MapIndex);
        }

        [Test]
        public void AGatewayGetsTheFullCopyThenOnlyWhatChanged()
        {
            var w1 = _mesh[0];
            var gw = _mesh.AddGateway("gw");
            _mesh.LinkGateway(gw);
            var e = Spawn(w1);
            var stash = e.GetComponent<Stash>();
            for (int i = 0; i < 50; i++) stash.Items[i] = "item" + i;
            stash.Counts["ore"] = 3;
            w1.Tick(1);
            _mesh.Pump();
            // The stash was announced when it spawned, empty; what was put in it since follows as changes.
            var spawn = _mesh.Delivered.Find(m => m.To == "gw" && m.Id == MsgId.EntitySpawn && m.Read(EntitySpawnMsg.Read).NetId == e.NetId);
            Assert.IsNotNull(spawn.Bytes, "the always-relevant stash is announced");
            int first = Entries(spawn.Read(EntitySpawnMsg.Read).Maps);
            foreach (var m in _mesh.DeliveredOf(MsgId.EntityMaps, to: "gw")) first += Entries(m.Read(EntityMapsMsg.Read).Maps);
            Assert.AreEqual(51, first, "every entry of both maps reached the gateway once");

            // A gateway that starts following the stash later is sent it in full with the spawn, and no changes.
            var late = _mesh.AddGateway("late");
            _mesh.LinkGateway(late);
            _mesh.Delivered.Clear();
            w1.Tick(2);
            _mesh.Pump();
            var lateSpawn = _mesh.Delivered.Find(m => m.To == "late" && m.Id == MsgId.EntitySpawn && m.Read(EntitySpawnMsg.Read).NetId == e.NetId);
            Assert.IsNotNull(lateSpawn.Bytes, "the new gateway is announced the stash");
            Assert.AreEqual(51, Entries(lateSpawn.Read(EntitySpawnMsg.Read).Maps), "its spawn carries every entry of both maps");
            Assert.IsFalse(_mesh.Delivered.Exists(m => m.Id == MsgId.EntityMaps), "nothing changed, so no increment");

            // One entry changes: one entry travels, and no variables.
            _mesh.Delivered.Clear();
            stash.Items[7] = "seven";
            w1.Tick(3);
            _mesh.Pump();
            var maps = _mesh.Delivered.FindAll(m => m.To == "gw" && m.Id == MsgId.EntityMaps);
            Assert.AreEqual(1, maps.Count);
            var msg = maps[0].Read(EntityMapsMsg.Read);
            Assert.AreEqual(e.NetId, msg.NetId);
            Assert.AreEqual(1, Entries(msg.Maps), "only the changed entry");
            Assert.IsFalse(_mesh.Delivered.Exists(m => m.Id == MsgId.EntityVars && m.Read(EntityVarsMsg.Read).NetId == e.NetId));

            // A variable changes: the variables travel, and no map.
            _mesh.Delivered.Clear();
            stash.Level.Value = 2;
            w1.Tick(4);
            _mesh.Pump();
            Assert.IsTrue(_mesh.Delivered.Exists(m => m.To == "gw" && m.Id == MsgId.EntityVars));
            Assert.IsFalse(_mesh.Delivered.Exists(m => m.Id == MsgId.EntityMaps));

            // Nothing changes: nothing is said.
            _mesh.Delivered.Clear();
            w1.Tick(5);
            _mesh.Pump();
            Assert.IsFalse(_mesh.Delivered.Exists(m => m.Id == MsgId.EntityMaps || m.Id == MsgId.EntityVars));
        }

        [Test]
        public void AHandoverCarriesUnsentChangesAndTheGhostThenGetsIncrements()
        {
            var w1 = _mesh[0];
            var w2 = _mesh[1];
            var e = Spawn(w1);
            ulong netId = e.NetId;
            var stash = e.GetComponent<Stash>();
            for (int i = 0; i < 10; i++) stash.Items[i] = "item" + i;
            w1.Tick(1);
            _mesh.Pump();
            // Changed after the last send, then handed over before any tick sends it.
            stash.Items[3] = "unsent";
            stash.Items.Remove(4);

            w1.Transfer(e, w2);
            _mesh.Pump();
            var authority = w2.Find(netId);
            Assert.IsTrue(authority.HasAuthority);
            var landed = authority.GetComponent<Stash>();
            Assert.AreEqual("unsent", landed.Items[3], "the handover carries the change");
            Assert.IsFalse(landed.Items.ContainsKey(4));
            Assert.AreEqual(9, landed.Items.Count);
            Assert.IsFalse(authority.MapsDirty, "the new owner starts with nothing to send");

            // The old owner kept a ghost; it follows the new owner's increments.
            var ghost = w1.Find(netId);
            Assert.IsFalse(ghost.HasAuthority);
            var ghostStash = ghost.GetComponent<Stash>();
            Assert.AreEqual(9, ghostStash.Items.Count);
            var seen = new List<NetworkMapChange<int, string>>();
            ghostStash.Items.OnChanged += c => seen.Add(c);

            _mesh.Delivered.Clear();
            landed.Items[3] = "again";
            landed.Items[42] = "answer";
            landed.Counts["gems"] = 1;
            w2.PublishTick(2);
            _mesh.Pump();
            var ghostMaps = _mesh.DeliveredOf(MsgId.GhostMaps, to: "w1");
            Assert.AreEqual(1, ghostMaps.Count);
            Assert.AreEqual(3, Entries(ghostMaps[0].Read(EntityMapsMsg.Read).Maps), "three entries across two maps");
            Assert.AreEqual("again", ghostStash.Items[3]);
            Assert.AreEqual("answer", ghostStash.Items[42]);
            Assert.AreEqual(1, ghostStash.Counts["gems"]);
            Assert.AreEqual(2, seen.Count, "the ghost raised one change per key of its map");

            // A ghost may not write; the attempt is ignored.
            LogAssert.Expect(LogType.Warning, new Regex("written without authority"));
            w1.Act(() => ghostStash.Items[1] = "forbidden");
            Assert.AreEqual("item1", ghostStash.Items[1]);
        }

        [Test]
        public void AStaleGhostUpdateIsIgnored()
        {
            var w1 = _mesh[0];
            var w2 = _mesh[1];
            var e = Spawn(w1);
            ulong netId = e.NetId;
            e.GetComponent<Stash>().Items[1] = "one";
            w1.Transfer(e, w2);
            _mesh.Pump();
            var ghost = w1.Find(netId);
            // A delta stamped with the epoch before the handover: something the old owner sent that arrived late.
            var w = new NetworkWriter();
            int count = NetworkMapCodec.BeginSection(w);
            int at = NetworkMapCodec.BeginMap(w, 0);
            int body = NetworkMapCodec.BeginBody(w, clear: true);
            NetworkMapCodec.EndBody(w, body, 0);
            NetworkMapCodec.EndMap(w, at);
            NetworkMapCodec.EndSection(w, count, 1);
            var stale = new NetworkWriter();
            new EntityMapsMsg { NetId = netId, Epoch = ghost.Epoch - 1, Maps = w.ToArray() }.Write(stale, MsgId.GhostMaps);
            _mesh.Replay(new ConformanceMesh.WireMessage("w2", "w1", stale.ToArray()));
            Assert.AreEqual("one", ghost.GetComponent<Stash>().Items[1]);
        }

        [Test]
        public void APersistedMapRoundTripsAndARestoreOntoALiveEntityIsResentWhole()
        {
            var w1 = _mesh[0];
            var e = Spawn(w1);
            var stash = e.GetComponent<Stash>();
            stash.Items[1] = "one";
            stash.Items[2] = "two";
            stash.Counts["not saved"] = 5;
            Assert.IsTrue(stash.Items.PersistDirty);
            var blob = PersistentStateCodec.Write(e);
            Assert.IsFalse(stash.Items.PersistDirty);
            Assert.IsTrue(PersistentStateCodec.TryReadEntry(blob, stash.Items.Name, out _), "saved under the field's name");
            Assert.IsFalse(PersistentStateCodec.TryReadEntry(blob, stash.Counts.Name, out _), "only [Persist] maps are saved");

            var other = Spawn(w1);
            var restored = other.GetComponent<Stash>();
            restored.Items[9] = "stale";
            PersistentStateCodec.Read(blob, other);
            CollectionAssert.AreEquivalent(new[] { 1, 2 }, restored.Items.Keys);
            Assert.AreEqual("two", restored.Items[2]);

            // What NebulaPersistence does after restoring a record onto a live authority: the next send is everything.
            other.ClearDirty();
            restored.Items.MarkResendAll();
            Assert.IsTrue(other.MapsDirty);
            var w = new NetworkWriter();
            restored.Items.WriteDelta(w);
            var entries = new List<NetworkMapCodec.Entry>();
            NetworkMapCodec.ReadBody(new NetworkReader(w.ToArray()), out bool clear, entries);
            Assert.IsTrue(clear, "clear first");
            Assert.AreEqual(2, entries.Count, "then every entry");
        }

        [Test]
        public void SyncHistoryOnAMapIsRefused()
        {
            var prefab = new GameObject("map-historic");
            prefab.AddComponent<NetworkIdentity>();
            prefab.AddComponent<Historic>();
            ushort id = _mesh.RegisterPrefab(prefab);
            LogAssert.Expect(LogType.Warning, new Regex(@"\[SyncHistory\] is not supported on NetworkMap"));
            var e = _mesh[0].SpawnServerDriven(id, _yard, Vector3.one, Quaternion.identity);
            Assert.AreEqual(1, e.Maps.Length, "it still replicates");
            Assert.IsEmpty(e.HistoryVars);
        }
    }
}
