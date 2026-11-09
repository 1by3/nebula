using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// Conformance scenario: one prefab carries every optional feature from spawn and a feature is switched on and off
    /// while the entity lives, without respawning it (the "one object, features come and go" model that a game with
    /// user-built props needs). Per feature: a carried container (<see cref="NetworkIdentity.SetCarriedActive"/>), a
    /// physical body (<see cref="NetworkRigidbody.Simulate"/>), a driver's seat (<see cref="NetworkIdentity.SetDriver"/>),
    /// and what a disabled sync component still does. Each is checked on the authority, on a ghost on the
    /// neighbouring worker, across a handover (with and without a ghost already there) and across a persistence
    /// restore.
    /// <para>
    /// Tier B on the <see cref="ConformanceMesh"/> with a registry per worker: two real <see cref="NebulaWorker"/>s, a
    /// real <see cref="NebulaPersistence"/> each over one shared memory store. There is no real client here; a client
    /// applies a spawn snapshot and a variable change through the same <see cref="NetworkBehaviour"/> callbacks as a
    /// ghost does, which is what <see cref="PropToggles"/> reacts to.
    /// </para>
    /// </summary>
    [Category("Conformance")]
    public sealed class ConformanceRuntimeToggleTests
    {
        /// <summary>
        /// What a game writes once per prefab: replicated, persisted feature flags, applied to the optional components
        /// on every process that holds the entity (spawn snapshot, handover and restore all arrive through
        /// <see cref="OnNetworkSpawn"/>; a change arrives through <c>OnValueChanged</c>).
        /// </summary>
        public sealed class PropToggles : NetworkBehaviour
        {
            [Persist] public NetworkVariable<bool> Interior = new NetworkVariable<bool>();
            [Persist] public NetworkVariable<bool> Physical = new NetworkVariable<bool>();
            private bool _wired;

            public override void OnNetworkSpawn()
            {
                if (!_wired)
                {
                    _wired = true;
                    Interior.OnValueChanged += (_, on) => Identity.SetCarriedActive(on);
                    Physical.OnValueChanged += (_, on) => ApplyPhysical(on);
                }
                Identity.SetCarriedActive(Interior.Value);
                ApplyPhysical(Physical.Value);
            }

            private void ApplyPhysical(bool on)
            {
                var body = GetComponent<NetworkRigidbody>();
                if (body != null) body.Simulate = on;
            }
        }

        private ConformanceMesh _mesh;
        private LocalPersistenceStore _store;
        private Container _yard;
        private ushort _propPrefab, _framedPrefab, _rigPrefab, _seatPrefab, _crateId;

        private ConformanceMesh.Worker W1 => _mesh[0];
        private ConformanceMesh.Worker W2 => _mesh[1];

        [SetUp]
        public void SetUp()
        {
            PhysicsFrames.SceneFactory = () => EditorSceneManager.NewPreviewScene();
            PhysicsFrames.SceneDisposer = s => EditorSceneManager.ClosePreviewScene(s);
            _mesh = new ConformanceMesh(2, perWorkerRegistry: true);
            _yard = _mesh.AddStaticContainer("yard", Vector3.zero, new Vector3(64f, 40f, 64f));                // x in [-32, 32]
            var dock = _mesh.AddStaticContainer("dock", new Vector3(64f, 0f, 0f), new Vector3(64f, 40f, 64f)); // x in [32, 96]
            _mesh.SetOwner(_yard, W1);
            _mesh.SetOwner(dock, W2);

            // The Prop: every optional component is on it from the start, switched off.
            var prop = new GameObject("prop-prefab");
            prop.AddComponent<NetworkIdentity>();
            var nt = prop.AddComponent<NetworkTransform>();
            nt.Interpolate = false;
            var body = prop.AddComponent<NetworkRigidbody>(); // brings its Rigidbody
            body.Body.useGravity = false;
            prop.AddComponent<PropToggles>();
            prop.AddComponent<PersistentEntity>();
            var box = prop.AddComponent<Container>();
            box.ContainerId = "interior";
            box.Size = new Vector3(8f, 4f, 8f);
            box.Center = new Vector3(0f, 2f, 0f);
            box.enabled = false;
            _propPrefab = _mesh.RegisterPrefab(prop);

            // The same prop with an interior of its own physics frame, and a hull collider to clone into it.
            var framed = Object.Instantiate(prop);
            framed.name = "framed-prop-prefab";
            framed.AddComponent<BoxCollider>().size = new Vector3(8f, 0.5f, 8f);
            var framedBox = framed.GetComponent<Container>();
            framedBox.OwnPhysicsFrame = true;
            framedBox.enabled = false;
            _framedPrefab = _mesh.RegisterPrefab(framed);

            // A rig: a child pose that rides the sync channel.
            var rig = new GameObject("rig-prefab");
            rig.AddComponent<NetworkIdentity>().AlwaysRelevant = true;
            rig.AddComponent<NetworkTransform>().Interpolate = false;
            var arm = new GameObject("arm");
            arm.transform.SetParent(rig.transform, false);
            var armPose = arm.AddComponent<NetworkTransform>();
            armPose.Interpolate = false;
            armPose.UseUnreliableDeltas = false;
            _rigPrefab = _mesh.RegisterPrefab(rig);

            // The seat: a predicted behaviour that does nothing until someone drives.
            var seat = new GameObject("seat-prefab");
            seat.AddComponent<NetworkIdentity>().AlwaysRelevant = true;
            seat.AddComponent<TestVehicle>(); // PredictedBehaviour<DriveInput>, brings its NetworkTransform
            seat.AddComponent<PersistentEntity>();
            _seatPrefab = _mesh.RegisterPrefab(seat);

            var crate = new GameObject("crate-prefab");
            crate.AddComponent<NetworkIdentity>();
            crate.AddComponent<PersistentEntity>();
            _crateId = _mesh.RegisterPrefab(crate);

            _store = new LocalPersistenceStore();
            _store.Connect();
            foreach (var w in _mesh.Workers)
            {
                var persistence = new NebulaPersistence(w.Instance, _mesh.Config, _store);
                typeof(NebulaWorker).GetProperty(nameof(NebulaWorker.Persistence)).SetValue(w.Instance, persistence);
            }
        }

        [TearDown]
        public void TearDown()
        {
            _mesh.Dispose();
            _store?.Dispose();
            _store = null;
            ContainerRegistry.Rebuild();
            PhysicsFrames.DrainPool();
            PhysicsFrames.SceneFactory = null;
            PhysicsFrames.SceneDisposer = null;
        }

        // ---------------------------------------------------------------------------------------- helpers

        private static readonly Vector3 NearSeam = new Vector3(30f, 0f, 0f);   // inside the ghost band of the dock
        private static readonly Vector3 FarFromSeam = new Vector3(-20f, 0f, 0f);

        private NetworkIdentity SpawnProp(Vector3 at) => W1.SpawnServerDriven(_propPrefab, _yard, at, Quaternion.identity);

        /// <summary>Spawn a prop on w1 near the seam and let w1's ghost band give w2 a copy of it.</summary>
        private (NetworkIdentity prop, NetworkIdentity ghost) PropWithGhost(uint tick = 1, bool framed = false)
        {
            var prop = framed ? W1.SpawnServerDriven(_framedPrefab, _yard, NearSeam, Quaternion.identity) : SpawnProp(NearSeam);
            W1.PublishTick(tick);
            _mesh.Pump();
            var ghost = W2.Find(prop.NetId);
            Assume.That(ghost, Is.Not.Null, "w1's ghost band gave w2 a copy at the seam");
            Assume.That(ghost.HasAuthority, Is.False);
            return (prop, ghost);
        }

        private static PropToggles Toggles(NetworkIdentity e) => e.GetComponent<PropToggles>();

        private NetworkIdentity SpawnCrateInto(ConformanceMesh.Worker worker, NetworkIdentity carrier)
        {
            NetworkIdentity crate = null;
            worker.Act(() =>
            {
                crate = NetworkPrefabs.Instantiate(_crateId, carrier.transform.position + Vector3.up, Quaternion.identity, carrier.transform);
                worker.Instance.SpawnServerDriven(crate, carrier.Carried);
            });
            return crate;
        }

        /// <summary>What the worker would have saved for <paramref name="e"/>, restored on <paramref name="onto"/> after the original is gone.</summary>
        private NetworkIdentity SaveAndRestore(ConformanceMesh.Worker from, NetworkIdentity e, ConformanceMesh.Worker onto, bool keepRidersAboard = false)
        {
            PersistedEntityRecord record = null;
            from.Act(() => record = from.Instance.Persistence.BuildRecord(e));
            Assume.That(record, Is.Not.Null);
            from.Act(() => from.Instance.Despawn(e, keepPersisted: true));
            _mesh.Pump();
            NetworkIdentity restored = null;
            onto.Act(() => restored = onto.Instance.Persistence.Restore(record));
            return restored;
        }

        // ---------------------------------------------------------------------------------------- carried container

        [Test]
        public void ASwitchedOffCarriedBoxIsNotCarriedAndSwitchingItOnAndOffNeverRespawnsTheEntity()
        {
            var prop = SpawnProp(FarFromSeam);
            ulong netId = prop.NetId;
            uint epoch = prop.Epoch;
            var toggles = Toggles(prop);

            Assert.That(prop.Carried, Is.Null, "a switched-off box is not carried");
            Assert.That(ContainerRegistry.Dynamic, Is.Empty, "and not registered");
            Assert.That(ContainerRegistry.FindDynamic(netId), Is.Null);

            W1.Act(() => toggles.Interior.Value = true);
            var box = prop.Carried;
            Assert.That(box, Is.Not.Null);
            Assert.That(box.IsDynamic, Is.True);
            Assert.That(ContainerRegistry.FindDynamic(netId), Is.SameAs(box));
            Assert.That(box.ContainerId, Is.EqualTo("interior#" + netId));

            var crate = SpawnCrateInto(W1, prop);
            Assert.That(crate.Container, Is.SameAs(box), "a rider can stand in the box once it is on");

            W1.Act(() => toggles.Interior.Value = false);
            Assert.That(prop.Carried, Is.Null);
            Assert.That(box.IsDynamic, Is.False);
            Assert.That(ContainerRegistry.FindDynamic(netId), Is.Null);
            Assert.That(crate.Container, Is.SameAs(_yard), "the rider was put down in the container around the prop");

            W1.Act(() => toggles.Interior.Value = true);
            Assert.That(prop.Carried, Is.SameAs(box).And.Property("IsDynamic").True, "and it can come back");
            Assert.That(prop.NetId, Is.EqualTo(netId));
            Assert.That(prop.Epoch, Is.EqualTo(epoch));
            Assert.That(W1.Find(netId), Is.SameAs(prop), "the same object, never respawned");
        }

        [Test]
        public void AGhostOnTheNeighbouringWorkerFollowsTheFlagAndAHandoverKeepsTheBox()
        {
            var (prop, ghost) = PropWithGhost();
            ulong netId = prop.NetId;
            Assert.That(ghost.Carried, Is.Null);

            W1.Act(() => Toggles(prop).Interior.Value = true);
            Assert.That(prop.Carried, Is.Not.Null.And.Property("IsDynamic").True);

            W1.Tick(2);
            _mesh.Pump();
            Assert.That(Toggles(ghost).Interior.Value, Is.True, "the flag reached the ghost");
            Assert.That(ghost.Carried, Is.Not.Null.And.Property("IsDynamic").True, "so the ghost carries the box too");

            W1.Transfer(prop, W2);
            _mesh.Pump();
            var onW2 = W2.Find(netId);
            Assert.That(onW2.HasAuthority, Is.True);
            Assert.That(onW2.Carried, Is.Not.Null.And.Property("IsDynamic").True, "the new authority carries the box");
            Assert.That(Toggles(onW2).Interior.Value, Is.True);

            W2.Act(() => Toggles(onW2).Interior.Value = false);
            Assert.That(onW2.Carried, Is.Null, "and can switch it off");
        }

        [Test]
        public void AHandoverToAWorkerWithNoGhostYetBringsTheFlagAndTheBox()
        {
            var prop = SpawnProp(FarFromSeam);
            W1.Act(() => Toggles(prop).Interior.Value = true);
            ulong netId = prop.NetId;

            W1.Transfer(prop, W2);
            _mesh.Pump();
            var onW2 = W2.Find(netId);
            Assert.That(onW2, Is.Not.Null);
            Assert.That(onW2.HasAuthority, Is.True);
            Assert.That(Toggles(onW2).Interior.Value, Is.True);
            Assert.That(onW2.Carried, Is.Not.Null.And.Property("IsDynamic").True);
        }

        [Test]
        public void ARestoredPropCarriesItsBoxOnlyIfTheSavedFlagWasOnAndItsRidersComeBackAboard()
        {
            var prop = SpawnProp(FarFromSeam);
            W1.Act(() => Toggles(prop).Interior.Value = true);
            var crate = SpawnCrateInto(W1, prop);
            string propKey = prop.Persistent.EnsureKey();
            string crateKey = crate.Persistent.EnsureKey();
            PersistedEntityRecord crateRecord = null;
            W1.Act(() => crateRecord = W1.Instance.Persistence.BuildRecord(crate));
            Assert.That(crateRecord.CarrierKey, Is.EqualTo(propKey), "the rider's record rides the prop's");

            var restored = SaveAndRestore(W1, prop, W2);
            Assert.That(restored, Is.Not.Null);
            Assert.That(Toggles(restored).Interior.Value, Is.True, "the flag is part of the saved state");
            Assert.That(restored.Carried, Is.Not.Null.And.Property("IsDynamic").True);

            NetworkIdentity restoredCrate = null;
            W2.Act(() => restoredCrate = W2.Instance.Persistence.Restore(crateRecord));
            Assert.That(restoredCrate, Is.Not.Null);
            Assert.That(restoredCrate.Container, Is.SameAs(restored.Carried), "aboard the restored prop's box");

            // The other way: saved off, restored off.
            var plain = SpawnProp(new Vector3(-10f, 0f, 0f));
            var back = SaveAndRestore(W1, plain, W2);
            Assert.That(Toggles(back).Interior.Value, Is.False);
            Assert.That(back.Carried, Is.Null);
            W2.Act(() => Toggles(back).Interior.Value = true);
            Assert.That(back.Carried, Is.Not.Null, "a restored prop can still grow a box later");
        }

        [Test]
        public void ACarriedBoxWithItsOwnPhysicsFrameBuildsAndReleasesTheFrameWithTheSwitch()
        {
            var prop = W1.SpawnServerDriven(_framedPrefab, _yard, FarFromSeam, Quaternion.identity);
            var toggles = Toggles(prop);
            Assert.That(prop.Carried, Is.Null);

            W1.Act(() => toggles.Interior.Value = true);
            var box = prop.Carried;
            Assert.That(box.Frame, Is.Not.Null, "switching it on builds the physics frame");
            Assert.That(box.Frame.Clones.Count, Is.EqualTo(1), "from the colliders the prop has now");
            var crate = SpawnCrateInto(W1, prop);
            Assert.That(crate.Container, Is.SameAs(box));

            // A collider that arrives after the box is on (user content loaded late) is not in the frame.
            W1.Act(() => prop.gameObject.AddComponent<BoxCollider>());
            W1.Tick(2);
            Assert.That(box.Frame.Clones.Count, Is.EqualTo(1), "the frame is cloned once, when the box registers");

            W1.Act(() => toggles.Interior.Value = false);
            Assert.That(prop.Carried, Is.Null);
            Assert.That(box.Frame, Is.Null, "switching it off releases the frame");
            Assert.That(crate.Container, Is.SameAs(_yard), "and sets the rider down outside it");

            W1.Act(() => toggles.Interior.Value = true);
            Assert.That(prop.Carried.Frame, Is.Not.Null);
            Assert.That(prop.Carried.Frame.Clones.Count, Is.EqualTo(2), "a fresh frame sees both colliders");
        }

        [Test]
        public void AGhostBuildsTheSameFrameFromTheSameFlagAndTheFrameSurvivesAHandover()
        {
            var (prop, ghost) = PropWithGhost(framed: true);
            W1.Act(() => Toggles(prop).Interior.Value = true);
            W1.Tick(2);
            _mesh.Pump();
            Assert.That(Toggles(ghost).Interior.Value, Is.True);
            Assert.That(ghost.Carried?.Frame, Is.Not.Null, "the ghost builds the frame from its own copy of the prefab");

            W1.Transfer(prop, W2);
            _mesh.Pump();
            var onW2 = W2.Find(prop.NetId);
            Assert.That(onW2.HasAuthority, Is.True);
            Assert.That(onW2.Carried?.Frame, Is.Not.Null);
            W2.Act(() => Toggles(onW2).Interior.Value = false);
            Assert.That(onW2.Carried, Is.Null);
        }

        // ---------------------------------------------------------------------------------------- sync components that are switched off

        [Test]
        public void ADisabledChildNetworkTransformStillReplicatesSoDisabledIsNotOff([Values(true, false)] bool enabled)
        {
            var rig = W1.SpawnServerDriven(_rigPrefab, _yard, NearSeam, Quaternion.identity);
            W1.PublishTick(1); _mesh.Pump();
            var ghost = W2.Find(rig.NetId);
            Assume.That(ghost, Is.Not.Null);

            rig.transform.Find("arm").GetComponent<NetworkTransform>().enabled = enabled;
            rig.transform.Find("arm").localPosition = new Vector3(0f, 2f, 0f);
            W1.Tick(2);
            _mesh.Pump();

            Assert.That(ghost.transform.Find("arm").localPosition.y, Is.EqualTo(2f).Within(0.01f), "the child pose arrives whether the component is enabled or not");
        }

        // ---------------------------------------------------------------------------------------- physical body

        [Test]
        public void ThePhysicalFlagGatesTheBodyOnTheAuthorityAndTheGhostStaysKinematic()
        {
            var (prop, ghost) = PropWithGhost();
            var body = prop.GetComponent<Rigidbody>();
            var ghostBody = ghost.GetComponent<Rigidbody>();
            Assert.That(body.isKinematic, Is.True, "not physical: the authority's body is held out of the solver");
            Assert.That(ghostBody.isKinematic, Is.True);

            W1.Act(() => Toggles(prop).Physical.Value = true);
            Assert.That(body.isKinematic, Is.False, "physical: PhysX owns the body");
            W1.Tick(2);
            _mesh.Pump();
            Assert.That(ghostBody.isKinematic, Is.True, "a ghost never simulates");
            Assert.That(Toggles(ghost).Physical.Value, Is.True);

            W1.Act(() => Toggles(prop).Physical.Value = false);
            Assert.That(body.isKinematic, Is.True, "and back out of the solver");
        }

        [Test]
        public void TheBodyStateFollowsTheFlagAcrossAHandoverWithAndWithoutAGhost([Values(true, false)] bool withGhost, [Values(true, false)] bool physical)
        {
            NetworkIdentity prop;
            if (withGhost) (prop, _) = PropWithGhost();
            else prop = SpawnProp(FarFromSeam);
            W1.Act(() => Toggles(prop).Physical.Value = physical);
            ulong netId = prop.NetId;

            W1.Transfer(prop, W2);
            _mesh.Pump();
            var onW2 = W2.Find(netId);
            Assert.That(onW2.HasAuthority, Is.True);
            Assert.That(onW2.GetComponent<Rigidbody>().isKinematic, Is.EqualTo(!physical), "the new authority honours the replicated flag");
            Assert.That(prop.GetComponent<Rigidbody>().isKinematic, Is.True, "the old copy is a ghost now");

            W2.Act(() => Toggles(onW2).Physical.Value = !physical);
            Assert.That(onW2.GetComponent<Rigidbody>().isKinematic, Is.EqualTo(physical), "and can toggle it after the handover");
        }

        [Test]
        public void SimulateAloneIsPerProcessAndLostByAHandoverSoTheFlagMustBeReapplied()
        {
            var (prop, ghost) = PropWithGhost();
            // No PropToggles involved: the raw NetworkRigidbody.Simulate, set on the authority only.
            W1.Act(() => prop.GetComponent<NetworkRigidbody>().Simulate = false);
            Assert.That(prop.GetComponent<Rigidbody>().isKinematic, Is.True);
            // The ghost already ran PropToggles.OnNetworkSpawn (Physical false), so detach that gate to show the raw behaviour.
            ghost.GetComponent<NetworkRigidbody>().Simulate = true;

            W1.Transfer(prop, W2);
            _mesh.Pump();
            var onW2 = W2.Find(prop.NetId);
            Assert.That(onW2.GetComponent<NetworkRigidbody>().Simulate, Is.True, "Simulate is local to a process; a handover does not carry it");
            Assert.That(onW2.GetComponent<Rigidbody>().isKinematic, Is.False, "so a body held out of the solver here is dynamic there");
        }

        // ---------------------------------------------------------------------------------------- driver's seat

        [Test]
        public void ADriverCanBeSetAndClearedOnAnIdleSeatAndTravelsWithAHandoverButNotAPersistenceRestore()
        {
            var seat = W1.SpawnServerDriven(_seatPrefab, _yard, NearSeam, Quaternion.identity);
            Assert.That(seat.Predicted, Is.Not.Null);
            Assert.That(seat.DriverClientId, Is.EqualTo(0UL));
            W1.PublishTick(1);
            _mesh.Pump();
            var ghost = W2.Find(seat.NetId);
            Assume.That(ghost, Is.Not.Null);

            Assert.That(seat.SetDriver(7), Is.True, "an idle seat takes a driver at any time");
            Assert.That(seat.DriverClientId, Is.EqualTo(7UL));
            Assert.That(ghost.SetDriver(9), Is.False, "a ghost refuses: only the authority seats someone");

            W1.Transfer(seat, W2);
            _mesh.Pump();
            var onW2 = W2.Find(seat.NetId);
            Assert.That(onW2.HasAuthority, Is.True);
            Assert.That(onW2.DriverClientId, Is.EqualTo(7UL), "the driver rides the handover");
            Assert.That(onW2.ClearDriver(), Is.True);
            Assert.That(onW2.DriverClientId, Is.EqualTo(0UL));

            Assert.That(onW2.SetDriver(7), Is.True);
            var restored = SaveAndRestore(W2, onW2, W1);
            Assert.That(restored, Is.Not.Null);
            Assert.That(restored.Predicted, Is.Not.Null, "the seat component comes back with the prefab");
            Assert.That(restored.DriverClientId, Is.EqualTo(0UL), "a session is not persistent: the seat is empty after a restore");
            Assert.That(restored.SetDriver(7), Is.True);
        }

        // ---------------------------------------------------------------------------------------- disabled sync components

        [Test]
        public void DisablingTheRootNetworkTransformFreezesTheEntityForEveryoneElse()
        {
            var (prop, ghost) = PropWithGhost();
            W1.PublishTick(2); _mesh.Pump();
            var moved = NearSeam + new Vector3(0f, 0f, 3f);
            W1.Act(() => prop.transform.position = moved);
            W1.PublishTick(3); _mesh.Pump();
            Assume.That(ghost.transform.position.z, Is.EqualTo(3f).Within(0.05f), "enabled: the ghost follows");

            W1.Act(() => prop.GetComponent<NetworkTransform>().enabled = false);
            var moved2 = NearSeam + new Vector3(0f, 0f, 6f);
            W1.Act(() => prop.transform.position = moved2);
            W1.PublishTick(4); _mesh.Pump();
            Assert.That(ghost.transform.position.z, Is.EqualTo(3f).Within(0.05f), "disabled: no pose stream; never disable the root NetworkTransform");
        }
    }
}
