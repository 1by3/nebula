using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>
    /// NEB-431 (<c>docs/portals.md</c> D20, §18): a client that is sent an entity whose container it cannot resolve holds the update, whatever the
    /// container's kind, instead of placing the entity in the public world. A crate that crossed out of an instance landed in scope 0 about 1.2 km off
    /// for a client inside, because <c>OnEntitySpawn</c> held only for carrier and runtime containers and otherwise called <c>SetContainer(null)</c>, and the
    /// reconcile path had the same fallback. Both paths, and a state entry, hold now; only "no container" means the world root.
    /// </summary>
    [Category("Conformance")]
    public sealed class ClientHoldsUnresolvedContainerTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private const ulong Me = 42;
        /// <summary>A baked container index this process has no container for.</summary>
        private static readonly ContainerRef Missing = new ContainerRef(61);
        private readonly List<GameObject> _objects = new List<GameObject>();
        private NebulaClient _client;
        private IDictionary _entities;

        [SetUp]
        public void SetUp()
        {
            ContainerRegistry.Rebuild();
            NebulaRuntime.IsServer = false;
            NebulaRuntime.IsClient = true;
            var prefab = new GameObject("crate-prefab");
            _objects.Add(prefab);
            prefab.AddComponent<NetworkIdentity>();
            prefab.AddComponent<TestVehicle>();
            prefab.SetActive(false);
            NetworkPrefabs.Register(new List<GameObject> { prefab });
            var go = new GameObject("client");
            _objects.Add(go);
            _client = go.AddComponent<NebulaClient>();
            typeof(NebulaClient).GetProperty("Config").SetValue(_client, ScriptableObject.CreateInstance<NebulaConfig>());
            typeof(NebulaClient).GetProperty("ConnectionState").SetValue(_client, NebulaClient.State.InGame);
            typeof(NebulaClient).GetProperty("ClientId").SetValue(_client, Me);
            typeof(NebulaClient).GetField("_transport", Private).SetValue(_client, new ConformanceMesh.RecordingTransport());
            typeof(NebulaClient).GetField("_gatewayPeer", Private).SetValue(_client, 1);
            typeof(NebulaClient).GetField("_serverTickEstimate", Private).SetValue(_client, 1000.0);
            _entities = (IDictionary)typeof(NebulaClient).GetField("_entities", Private).GetValue(_client);
        }

        [TearDown]
        public void TearDown()
        {
            typeof(NebulaClient).GetField("_transport", Private).SetValue(_client, null);
            foreach (var e in _entities.Values) if (e is NetworkIdentity id && id != null) Object.DestroyImmediate(id.gameObject);
            foreach (var go in _objects) if (go != null) Object.DestroyImmediate(go);
            _objects.Clear();
            foreach (var c in new List<Container>(ContainerRegistry.Runtime)) if (c != null) Object.DestroyImmediate(c.gameObject);
            ContainerRegistry.PruneRuntime(new HashSet<ulong>());
            NetworkPrefabs.Register(new List<GameObject>());
            ContainerRegistry.Rebuild();
            NebulaRuntime.IsClient = false;
            NebulaRuntime.IsServer = true;
        }

        private EntitySpawnMsg SpawnMsg(ulong netId, ContainerRef container, Vector3 at, uint epoch = 1, ulong driver = 0) => new EntitySpawnMsg
        {
            NetId = netId, PrefabId = 0, Epoch = epoch, Container = container, LocalPosition = at, LocalRotation = Quaternion.identity, LocalScale = Vector3.one,
            Vars = System.Array.Empty<byte>(), State = System.Array.Empty<byte>(), ViewSeq = 1, DriverClientId = driver,
        };

        private void Spawn(EntitySpawnMsg msg) => typeof(NebulaClient).GetMethod("OnEntitySpawn", Private).Invoke(_client, new object[] { msg });

        [Test]
        public void ASpawnNamingAStaticContainerTheClientLacksIsHeldNotPlacedAtTheWorldRoot()
        {
            Spawn(SpawnMsg(9, Missing, new Vector3(4f, 1f, -2f)));
            Assert.That(_entities.Contains(9UL), Is.False, "no replica is made at the world root with coordinates local to a container this client does not have");
        }

        [Test]
        public void ASpawnWithNoContainerStillPlacesTheEntityInTheWorld()
        {
            Spawn(SpawnMsg(10, ContainerRef.None, new Vector3(4f, 1f, -2f)));
            Assert.That(_entities.Contains(10UL), Is.True, "only 'no container' means the world root");
            var crate = (NetworkIdentity)_entities[10UL];
            Assert.That(crate.Container, Is.Null);
        }

        [Test]
        public void ASpawnHeldForAMissingContainerBindsWhenThatContainerArrives()
        {
            // The first baked container: nothing is registered yet, so index 0 is missing.
            var late = new ContainerRef(0);
            Spawn(SpawnMsg(11, late, new Vector3(1f, 1f, 1f)));
            Assert.That(_entities.Contains(11UL), Is.False);

            // The baked container turns up (a scene loaded) and the registry is collected again. (The client subscribes in Initialize, which a test does not run.)
            var release = (System.Action)System.Delegate.CreateDelegate(typeof(System.Action), _client, typeof(NebulaClient).GetMethod("ReleaseResolvedHolds", Private));
            ContainerRegistry.Rebuilt += release;
            var go = new GameObject("late-container", typeof(Container));
            _objects.Add(go);
            var container = go.GetComponent<Container>();
            container.Size = new Vector3(10f, 10f, 10f);
            go.transform.position = new Vector3(100f, 0f, 0f);
            try { ContainerRegistry.Rebuild(); }
            finally { ContainerRegistry.Rebuilt -= release; }
            Assert.That(ContainerRegistry.Resolve(late), Is.Not.Null.And.SameAs(container), "the container is here now");

            Assert.That(_entities.Contains(11UL), Is.True, "and the held spawn binds");
            var crate = (NetworkIdentity)_entities[11UL];
            Assert.That(crate.Container, Is.SameAs(container));
            Assert.That(Vector3.Distance(crate.transform.position, new Vector3(101f, 1f, 1f)), Is.LessThan(0.01f), "at the position local to that container, not to the world root");
        }

        [Test]
        public void AKnownEntitySentIntoAMissingContainerStaysWhereItWas()
        {
            Spawn(SpawnMsg(12, ContainerRef.None, new Vector3(2f, 0f, 0f)));
            var crate = (NetworkIdentity)_entities[12UL];
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("unknown here; holding"));
            Spawn(SpawnMsg(12, Missing, new Vector3(400f, 0f, 0f), epoch: 2));
            Assert.That(crate.Container, Is.Null, "kept where it was until the container arrives");
            Assert.That(crate.Epoch, Is.EqualTo(1u), "and the newer epoch is held with the rest of the update");
        }

        [Test]
        public void AStateEntryNamingAMissingContainerIsHeldNotAppliedAtTheWorldRoot()
        {
            Spawn(SpawnMsg(13, ContainerRef.None, new Vector3(2f, 0f, 0f)));
            var crate = (NetworkIdentity)_entities[13UL];
            var entry = new EntityStateEntry
            {
                NetId = 13, Epoch = 2, Container = Missing, Fields = TransformFields.Position | TransformFields.Location,
                LocalPosition = new Vector3(400f, 0f, 0f), LocalRotation = Quaternion.identity, LocalScale = Vector3.one,
            };
            Assert.That(crate.ReceiveState(5, 1, entry), Is.False, "held");
            Assert.That(crate.Container, Is.Null);
            Assert.That(crate.Epoch, Is.EqualTo(1u));
            Assert.That(Vector3.Distance(crate.transform.position, new Vector3(2f, 0f, 0f)), Is.LessThan(0.01f), "it was not thrown 400 m off at the world root");
        }

        private void FixedUpdate() => typeof(NebulaClient).GetMethod("FixedUpdate", Private).Invoke(_client, null);

        private void OwnerState(ulong netId, uint tick, ContainerRef container, Vector3 position)
        {
            var w = new NetworkWriter(64);
            w.WriteVector3(position);
            w.WriteQuaternion(Quaternion.identity);
            w.WriteVector3(Vector3.zero);
            w.WriteFloat(0f);
            typeof(NebulaClient).GetMethod("OnOwnerState", Private).Invoke(_client, new object[]
            {
                new OwnerStateMsg { NetId = netId, Epoch = 1, Tick = tick, LastInputTick = tick, InputLead = 3, OwnerClientId = Me, Container = container, State = w.ToArray() },
            });
        }

        [Test]
        public void TheReconcilePathHoldsAnOwnerStateNamingAMissingContainerToo()
        {
            // A vehicle this client drives, in a runtime container it has; the worker reports it in a baked container this client has not got.
            var home = ContainerRegistry.RegisterRuntime(9001, new Bounds(new Vector3(50f, 0f, 0f), new Vector3(20f, 20f, 20f)));
            Spawn(SpawnMsg(14, ContainerRef.Runtime(9001), new Vector3(1f, 1f, 1f), driver: Me));
            var car = (NetworkIdentity)_entities[14UL];
            var vehicle = car.GetComponent<TestVehicle>();
            Assert.That(car.IsLocallyDriven, Is.True, "reconciled against the worker's owner state");
            Assert.That(car.Container, Is.SameAs(home));
            vehicle.Hands = () => new DriveInput { Throttle = 1f };
            for (int i = 0; i < 5; i++) FixedUpdate();
            uint predicted = (uint)typeof(NebulaClient).GetField("_predictTick", Private).GetValue(_client);
            Assert.That(predicted, Is.GreaterThan(0u));

            // The worker's report for the newest predicted tick names a container this client has not got: held, not applied at the world root.
            OwnerState(14, predicted, Missing, new Vector3(300f, 0f, 0f));
            Assert.That(car.Container, Is.SameAs(home), "not moved to the world root: the next report will do, once the container is here");
            Assert.That(vehicle.Corrections, Is.EqualTo(0), "and nothing was reconciled against coordinates of a container it does not have");

            // The same report naming the container it is in is applied, and the vehicle is corrected to it.
            OwnerState(14, predicted, ContainerRef.Runtime(9001), car.LocalPosition + Vector3.right);
            Assert.That(vehicle.Corrections, Is.EqualTo(1), "so the hold was the difference");
        }
    }
}
