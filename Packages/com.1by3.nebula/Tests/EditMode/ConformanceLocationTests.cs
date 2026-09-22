using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
#if NEBULA_SERVICE
using Nebula.ServicePrimitives;
#else
using UnityEngine;
#endif

namespace Nebula.Tests
{
    /// <summary>
    /// Conformance tests for the location contract (<c>docs/location-contract.md</c>): the triple
    /// (<see cref="EntityLocation.ScopeKey"/>, <see cref="EntityLocation.ContainerId"/>, local pose) names where an
    /// entity durably is, independent of which worker holds it. Exercised on the production types: the
    /// <see cref="ContainerRegistry"/> of this build (the Unity one that workers and clients run, or the service one
    /// that the standalone gateway and orchestrator run; the same source compiles into both), the real
    /// <see cref="LocalPersistenceStore"/> and <see cref="PersistedRecordJson"/>, and the real wire writer.
    /// <para>
    /// The registry is a process-wide singleton, so "two independent processes" is two independent populations of
    /// it in sequence, from fresh objects and in a different order: the second one gives the same container a
    /// different wire index, which is exactly what the contract must not depend on.
    /// </para>
    /// </summary>
    [Category("Conformance")]
    public class ConformanceLocationTests
    {
        private const string Scope = "interior/alice";
        private const ulong ScopeId = 0x5A17C0DE00000001UL;
        private const ulong PrivateRuntimeId = 11;
        private const ulong PublicRuntimeId = 22;
        private static readonly Vector3 Pose = new Vector3(1, 2, 3);
        private static readonly Quaternion Turn = new Quaternion(0, 0.70710677f, 0, 0.70710677f);

        /// <summary>The fixed triple every pinning test uses: a pose inside the private runtime container.</summary>
        private static EntityLocation Fixed => new EntityLocation(Scope, "rt_11", Pose, Quaternion.identity);

        private string _tempDir;

        [TearDown]
        public void TearDown()
        {
            ResetRegistry();
            if (_tempDir != null && Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
            _tempDir = null;
        }

        private string TempFile(string name)
        {
            _tempDir = _tempDir ?? Path.Combine(Path.GetTempPath(), "nebula-conformance-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            return Path.Combine(_tempDir, name);
        }

        // ---------------------------------------------------------------------------------------- one "process"

        /// <summary>The lease rows every role mirrors runtime containers from: one private box in <see cref="Scope"/>, one public box.</summary>
        private static LeaseInfo[] Leases() => new[]
        {
            new LeaseInfo { ContainerId = ContainerRegistry.RuntimeContainerId(PrivateRuntimeId), HasBounds = true, BoundsCenter = new Vector3(500, 0, 0), BoundsSize = new Vector3(10, 10, 10), WorkerId = "w1", Epoch = 3, State = LeaseState.Active,
                Instance = new InstanceContainerInfo { InstanceId = ScopeId, ScopeKey = Scope, ObservationSize = new Vector3(30, 30, 30) } },
            new LeaseInfo { ContainerId = ContainerRegistry.RuntimeContainerId(PublicRuntimeId), HasBounds = true, BoundsCenter = new Vector3(600, 0, 0), BoundsSize = new Vector3(10, 10, 10), WorkerId = "w2", Epoch = 5, State = LeaseState.Active },
        };

#if NEBULA_SERVICE
        private static void ResetRegistry() => ContainerRegistry.Load(new ServiceManifest());

        /// <summary>Populate the registry as a gateway or orchestrator does: from the exported manifest, then the lease rows.</summary>
        private static void Populate(params string[] staticIds)
        {
            var manifest = new ServiceManifest();
            for (int i = 0; i < staticIds.Length; i++)
                manifest.Containers.Add(new Container { ContainerId = staticIds[i], Index = (ushort)i, Size = new Vector3(20, 10, 20), transform = new ContainerFrame { position = new Vector3(i * 20, 0, 0) } });
            ContainerRegistry.Load(manifest);
            ContainerRegistry.SyncRuntime(Leases());
        }
#else
        private readonly List<GameObject> _objects = new List<GameObject>();

        private void ResetRegistry()
        {
            ContainerRegistry.Load(new Container[0], false);
            foreach (var go in _objects) if (go != null) UnityEngine.Object.DestroyImmediate(go);
            _objects.Clear();
        }

        /// <summary>Populate the registry as a worker or client does: from scene containers, then the lease rows.</summary>
        private void Populate(params string[] staticIds)
        {
            var ordered = new List<Container>();
            for (int i = 0; i < staticIds.Length; i++)
            {
                var go = new GameObject(staticIds[i]);
                go.transform.position = new Vector3(i * 20, 0, 0);
                var c = go.AddComponent<Container>();
                c.ContainerId = staticIds[i];
                c.Size = new Vector3(20, 10, 20);
                c.Center = new Vector3(0, 5, 0);
                _objects.Add(go);
                ordered.Add(c);
            }
            ContainerRegistry.Load(ordered, false);
            ContainerRegistry.SyncRuntime(Leases());
        }
#endif

        // ---------------------------------------------------------------------------------------- resolution

        [Test]
        public void TheSameTripleResolvesTheSameContainerInTwoIndependentProcesses()
        {
            // Process A: one static container, the two runtime ones.
            Populate("plaza");
            var plazaA = ContainerRegistry.FindById("plaza");
            var privateA = ContainerRegistry.FindById("rt_11");
            var publicA = ContainerRegistry.FindById("rt_22");
            Assert.That(plazaA, Is.Not.Null); Assert.That(privateA, Is.Not.Null); Assert.That(publicA, Is.Not.Null);

            var inPlaza = EntityLocation.Of(plazaA, Pose, Turn);
            var inPrivate = EntityLocation.Of(privateA, Pose, Turn);
            var inPublic = EntityLocation.Of(publicA, Pose, Turn);
            Assert.That(inPlaza, Is.EqualTo(new EntityLocation("", "plaza", Pose, Turn)), "a static container is its authored id in the public scope");
            Assert.That(inPrivate, Is.EqualTo(new EntityLocation(Scope, "rt_11", Pose, Turn)), "an instance container carries the game's scope key, not the hash");
            Assert.That(inPublic, Is.EqualTo(new EntityLocation("", "rt_22", Pose, Turn)), "a public runtime container is rt_<id> in the public scope");
            Assert.That(inPlaza.Resolve(), Is.SameAs(plazaA));
            Assert.That(inPrivate.Resolve(), Is.SameAs(privateA));
            Assert.That(inPublic.Resolve(), Is.SameAs(publicA));
            var refA = plazaA.Ref;

            // Process B: fresh objects, another static container ahead of the first, the same lease rows.
            ResetRegistry();
            Populate("annex", "plaza");
            var plazaB = inPlaza.Resolve();
            var privateB = inPrivate.Resolve();
            var publicB = inPublic.Resolve();
            Assert.That(plazaB, Is.Not.Null.And.Not.SameAs(plazaA));
            Assert.That(privateB, Is.Not.Null.And.Not.SameAs(privateA));
            Assert.That(publicB, Is.Not.Null.And.Not.SameAs(publicA));
            Assert.That(plazaB.Ref, Is.Not.EqualTo(refA), "the wire index moved between the two processes");
            Assert.That(plazaB.ContainerId, Is.EqualTo("plaza"));
            Assert.That(plazaB.ScopeKey, Is.EqualTo(""));
            Assert.That(privateB.ContainerId, Is.EqualTo("rt_11"));
            Assert.That(privateB.ScopeKey, Is.EqualTo(Scope));
            Assert.That(privateB.InstanceId, Is.EqualTo(ScopeId));
            Assert.That(EntityLocation.Of(plazaB, Pose, Turn), Is.EqualTo(inPlaza), "the triple read back on process B is byte for byte the one process A produced");
            Assert.That(EntityLocation.Of(privateB, Pose, Turn), Is.EqualTo(inPrivate));
            Assert.That(EntityLocation.Of(publicB, Pose, Turn), Is.EqualTo(inPublic));
        }

        [Test]
        public void ATripleInAnotherScopeOrWithNoContainerDoesNotResolve()
        {
            Populate("plaza");
            Assert.That(new EntityLocation("interior/bob", "rt_11", Pose, Quaternion.identity).Resolve(), Is.Null, "the private box belongs to alice's scope");
            Assert.That(new EntityLocation("", "rt_11", Pose, Quaternion.identity).Resolve(), Is.Null, "the public scope does not reach into an instance");
            Assert.That(new EntityLocation(Scope, "plaza", Pose, Quaternion.identity).Resolve(), Is.Null, "a static container is not in a private scope");
            Assert.That(new EntityLocation("", "elsewhere", Pose, Quaternion.identity).Resolve(), Is.Null, "unknown here");
            Assert.That(EntityLocation.None.Resolve(), Is.Null);
            Assert.That(new EntityLocation("", "", Pose, Quaternion.identity).HasContainer, Is.False);
        }

        [Test]
        public void ContainerKindIsReadFromTheFormOfTheId()
        {
            Assert.That(new EntityLocation("", "", default, Quaternion.identity).ContainerKind, Is.EqualTo(LocationContainerKind.None));
            Assert.That(new EntityLocation("", "plaza", default, Quaternion.identity).ContainerKind, Is.EqualTo(LocationContainerKind.Static));
            Assert.That(new EntityLocation("", "rt_11", default, Quaternion.identity).ContainerKind, Is.EqualTo(LocationContainerKind.Runtime));
            Assert.That(new EntityLocation("", "rt_18446744073709551615", default, Quaternion.identity).ContainerKind, Is.EqualTo(LocationContainerKind.Runtime));
            Assert.That(new EntityLocation("", "rt_", default, Quaternion.identity).ContainerKind, Is.EqualTo(LocationContainerKind.Static), "no number: an authored id that happens to start with rt_");
            Assert.That(new EntityLocation("", "rt_x", default, Quaternion.identity).ContainerKind, Is.EqualTo(LocationContainerKind.Static));
            Assert.That(new EntityLocation("", "ship#42", default, Quaternion.identity).ContainerKind, Is.EqualTo(LocationContainerKind.Dynamic));
        }

        // ---------------------------------------------------------------------------------------- handover

        [Test]
        public void TheTripleDoesNotChangeOnAHandover()
        {
            // The parts of a placement that a handover changes (authority epoch, owning worker, wire index) are not in
            // the triple. On a record:
            var record = new PersistedEntityRecord { Key = "crate-1", Epoch = 1, SavedBy = "w1", Location = Fixed };
            var before = record.Location;
            record.Epoch = 7;
            record.SavedBy = "w2";
            Assert.That(record.Location, Is.EqualTo(before));
            Assert.That(record.ScopeKey, Is.EqualTo(Scope));
            Assert.That(record.ContainerId, Is.EqualTo("rt_11"));
            // and on the container, whose lease moving to another worker at a higher epoch changes nothing in it either:
            Populate("plaza");
            var box = ContainerRegistry.FindById("rt_11");
            var location = EntityLocation.Of(box, Pose, Turn);
            ContainerRegistry.ApplyLease("rt_11", "w9", 9, 40, LeaseState.Active);
            Assert.That(box.OwnerWorkerId, Is.EqualTo("w9"));
            Assert.That(EntityLocation.Of(box, Pose, Turn), Is.EqualTo(location));
            Assert.That(location.Resolve(), Is.SameAs(box));
#if !NEBULA_SERVICE
            // and on a live entity: the authority flips, the epoch rises, the location does not move. The entity sits
            // in the public runtime box: entering an instance container creates its instance scene, which needs play mode.
            var publicBox = ContainerRegistry.FindById("rt_22");
            var go = new GameObject("crate");
            _objects.Add(go);
            var identity = go.AddComponent<NetworkIdentity>();
            identity.SetAuthority(true);
            identity.SetContainer(publicBox);
            identity.SetLocalPose(publicBox, Pose, Turn);
            var live = identity.Location;
            Assert.That(live.ScopeKey, Is.EqualTo(""));
            Assert.That(live.ContainerId, Is.EqualTo("rt_22"));
            Assert.That(live.Resolve(), Is.SameAs(publicBox));
            identity.Epoch += 1;
            identity.SetAuthority(false);
            ContainerRegistry.ApplyLease("rt_22", "w9", 9, 41, LeaseState.Active);
            Assert.That(identity.Location, Is.EqualTo(live));
            // A persistent record built from it carries the same triple.
            var saved = new PersistedEntityRecord { Key = "crate", Epoch = identity.Epoch, ScopeKey = identity.ScopeKey, ContainerId = publicBox.ContainerId, LocalPosition = identity.LocalPosition, LocalRotation = identity.LocalRotation };
            Assert.That(saved.Location, Is.EqualTo(live));
#endif
        }

        // ---------------------------------------------------------------------------------------- persistence

        [Test]
        public void TheTripleSurvivesAStoreRoundTrip()
        {
            string file = TempFile("entities.bin");
            var written = new PersistedEntityRecord { Key = "crate-1", PrefabName = "Crate", Epoch = 2, SavedBy = "w1", Location = Fixed, State = new byte[] { 1, 2, 3 } };
            using (var store = new LocalPersistenceStore(file))
            {
                store.Connect();
                store.Save(written);
                store.Flush();
            }
            // A different process opens the same file.
            using (var reopened = new LocalPersistenceStore(file))
            {
                reopened.Connect();
                PersistedEntityRecord loaded = null;
                reopened.Load("crate-1", r => loaded = r);
                reopened.Tick();
                Assert.That(loaded, Is.Not.Null);
                Assert.That(loaded.Location, Is.EqualTo(Fixed));
                Assert.That(loaded.ScopeKey, Is.EqualTo(Scope));
                Assert.That(loaded.CarrierKey, Is.EqualTo(""));
            }
            // And the JSON form between a worker's RemotePersistenceStore and the orchestrator's PersistenceHost.
            var json = PersistedRecordJson.WriteOne(written);
            Assert.That(json, Does.Contain("\"scopeKey\":\"interior/alice\""));
            Assert.That(PersistedRecordJson.ParseOne(json).Location, Is.EqualTo(Fixed));
            Assert.That(PersistedRecordJson.ParseList(PersistedRecordJson.WriteList(new[] { written }))[0].Location, Is.EqualTo(Fixed));
        }

        [Test]
        public void ARecordWithoutAScopeKeyReadsAsThePublicWorld()
        {
            const string legacy = "{\"record\":{\"key\":\"old-1\",\"prefabId\":0,\"prefabName\":\"Crate\",\"sceneId\":0,\"containerId\":\"plaza\",\"carrierKey\":\"\"," +
                "\"position\":[1,2,3],\"rotation\":[0,0,0,1],\"velocity\":[0,0,0],\"epoch\":1,\"serverDriven\":false,\"owned\":false,\"name\":\"\",\"state\":\"\",\"version\":1,\"savedAt\":0,\"savedBy\":\"w1\"}}";
            var record = PersistedRecordJson.ParseOne(legacy);
            Assert.That(record.ScopeKey, Is.EqualTo(""));
            Assert.That(record.Location, Is.EqualTo(new EntityLocation("", "plaza", new Vector3(1, 2, 3), Quaternion.identity)));
            Assert.That(record.Location.IsPublic, Is.True);
        }

        // ---------------------------------------------------------------------------------------- wire

        [Test]
        public void TheWireBytesOfAFixedTripleArePinned()
        {
            // string scope_key, string container_id, vector3 local_position, quaternion local_rotation, each as the wire
            // protocol encodes the primitive (u16 byte count + 1, UTF-8; f32 little-endian). Changing any of this is a
            // protocol change: bump ProtocolVersion and this expectation together.
            byte[] expected =
            {
                0x0F, 0x00, 0x69, 0x6E, 0x74, 0x65, 0x72, 0x69, 0x6F, 0x72, 0x2F, 0x61, 0x6C, 0x69, 0x63, 0x65, // "interior/alice"
                0x06, 0x00, 0x72, 0x74, 0x5F, 0x31, 0x31,                                                       // "rt_11"
                0x00, 0x00, 0x80, 0x3F, 0x00, 0x00, 0x00, 0x40, 0x00, 0x00, 0x40, 0x40,                         // (1, 2, 3)
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x80, 0x3F, // identity
            };
            var w = new NetworkWriter();
            Fixed.Write(w);
            Assert.That(w.ToArray(), Is.EqualTo(expected));
            Assert.That(EntityLocation.Read(new NetworkReader(expected)), Is.EqualTo(Fixed));

            // The public world in no container: two empty strings, then the pose.
            byte[] none = { 0x01, 0x00, 0x01, 0x00, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x00, 0x00, 0x80, 0x3F };
            w = new NetworkWriter();
            EntityLocation.None.Write(w);
            Assert.That(w.ToArray(), Is.EqualTo(none));
            Assert.That(EntityLocation.Read(new NetworkReader(none)), Is.EqualTo(EntityLocation.None));
        }

        [Test]
        public void TheLeaseRowCarriesTheScopeKeyAndAnOlderRowReadsAsUnkeyed()
        {
            var info = new InstanceContainerInfo { InstanceId = ScopeId, ContentResource = "interior", ScopeKey = Scope, ObservePublic = true, ObservationCenter = new Vector3(1, 2, 3), ObservationSize = new Vector3(4, 5, 6) };
            var copy = InstanceContainerInfo.Decode(InstanceContainerInfo.Encode(info));
            Assert.That(copy.ScopeKey, Is.EqualTo(Scope));
            Assert.That(copy.InstanceId, Is.EqualTo(ScopeId));
            Assert.That(copy.ContentResource, Is.EqualTo("interior"));

            // A row encoded before the key was recorded stops after the observation box.
            var w = new NetworkWriter();
            w.WriteULong(ScopeId); w.WriteString("interior"); w.WriteBool(true); w.WriteVector3(new Vector3(1, 2, 3)); w.WriteVector3(new Vector3(4, 5, 6));
            var old = InstanceContainerInfo.Decode(Convert.ToBase64String(w.ToArray()));
            Assert.That(old.ScopeKey, Is.EqualTo(""));
            Assert.That(old.InstanceId, Is.EqualTo(ScopeId));
        }

        // ---------------------------------------------------------------------------------------- value semantics

        [Test]
        public void EqualityIsExactAndToStringIsStable()
        {
            var a = Fixed;
            var b = new EntityLocation(Scope, "rt_11", new Vector3(1, 2, 3), new Quaternion(0, 0, 0, 1));
            Assert.That(a, Is.EqualTo(b));
            Assert.That(a == b, Is.True);
            Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()));
            Assert.That(a != new EntityLocation(Scope, "rt_11", new Vector3(1, 2, 3.0000005f), Quaternion.identity), Is.True, "exact, not approximate");
            Assert.That(a != new EntityLocation("Interior/alice", "rt_11", Pose, Quaternion.identity), Is.True, "ordinal, case matters");
            Assert.That(a != new EntityLocation(Scope, "rt_12", Pose, Quaternion.identity), Is.True);
            Assert.That(new EntityLocation(null, null, Pose, Quaternion.identity), Is.EqualTo(new EntityLocation("", "", Pose, Quaternion.identity)), "null reads as empty");
            Assert.That(default(EntityLocation).ScopeKey, Is.EqualTo(""));
            Assert.That(default(EntityLocation).ContainerId, Is.EqualTo(""));
            Assert.That(a.ToString(), Is.EqualTo("interior/alice:rt_11@(1, 2, 3)/(0, 0, 0, 1)"));
            Assert.That(EntityLocation.None.ToString(), Is.EqualTo("public:none@(0, 0, 0)/(0, 0, 0, 1)"));
            Assert.That(EntityLocation.None.IsPublic, Is.True);
            Assert.That(a.IsPublic, Is.False);
        }
    }
}
