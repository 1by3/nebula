using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>
    /// The street, the door and the hall of <see cref="PortalHarness"/> with real geometry and real physics, for the tests of <c>docs/portals.md</c> §6 (NEB-432).
    /// The door is 4 m wide and 5 m high, its face 2.5 m above the street's floor, facing +z at (20, 2.5, 20); the street is the default scene and the hall the
    /// private scope's own physics scene. The street has a floor and a hill (two walls and a roof) with a tunnel through it for the cross-section; the hall a floor
    /// and a back wall. Physics is stepped by the test, one step to each worker tick, the way a worker's own loop would between ticks.
    /// </summary>
    internal sealed class PortalPhysicsRig : IDisposable
    {
        public const float FaceY = 2.5f;
        public readonly PortalHarness H;
        public readonly ushort CratePrefab;
        public readonly ushort WalkerPrefab;
        public readonly List<GameObject> Made = new List<GameObject>();
        private readonly SimulationMode _mode;

        public static float Dt => NetworkTime.TickInterval;

        public PortalPhysicsRig(int workers = 1)
        {
            _mode = Physics.simulationMode;
            Physics.simulationMode = SimulationMode.Script;
            H = new PortalHarness(workers);
            // The test is the worker's clock: its own FixedUpdate loop would tick it in between, at the wall clock's pace.
            foreach (var worker in H.Mesh.Workers) worker.Instance.enabled = false;
            H.Door.transform.position = new Vector3(20f, FaceY, 20f);
            BuildStreet();
            var crate = new GameObject("crate-prefab");
            crate.AddComponent<NetworkIdentity>();
            var box = crate.AddComponent<BoxCollider>();
            box.size = Vector3.one;
            var body = crate.AddComponent<Rigidbody>();
            body.mass = 1f;
            body.linearDamping = 0f;
            body.angularDamping = 0.05f;
            CratePrefab = H.Mesh.RegisterPrefab(crate);
            var walker = new GameObject("walker-prefab");
            walker.AddComponent<NetworkIdentity>();
            var controller = walker.AddComponent<CharacterController>();
            controller.radius = 0.4f;
            controller.height = 1.8f;
            controller.center = new Vector3(0f, 0.9f, 0f);
            WalkerPrefab = H.Mesh.RegisterPrefab(walker);
        }

        /// <summary>The street: a floor, and a hill of two walls and a roof around a 4 x 5 m tunnel that starts at the face.</summary>
        private void BuildStreet()
        {
            Box("street floor", new Vector3(20f, -0.5f, 20f), new Vector3(40f, 1f, 16f));
            Box("hill left", new Vector3(14f, 2.5f, 23f), new Vector3(8f, 5f, 6f));
            Box("hill right", new Vector3(26f, 2.5f, 23f), new Vector3(8f, 5f, 6f));
            Box("hill roof", new Vector3(20f, 5.5f, 23f), new Vector3(12f, 1f, 6f));
        }

        public GameObject Box(string name, Vector3 center, Vector3 size, Transform parent = null)
        {
            var go = new GameObject(name);
            if (parent != null) go.transform.SetParent(parent, false);
            go.transform.position = center;
            go.AddComponent<BoxCollider>().size = size;
            Made.Add(go);
            return go;
        }

        /// <summary>
        /// The hall's geometry: a floor and a back wall under the hall container, put there before the link has prepared the hall, so they stand in its scene
        /// when it is made and the throat can copy them.
        /// </summary>
        public void BuildHall()
        {
            var hall = H.Hall;
            if (hall == null) throw new InvalidOperationException("the hall does not exist yet: open the door and tick once");
            Box("hall floor", new Vector3(20f, -0.5f, 24f), new Vector3(8f, 1f, 8f), hall.transform);
            Box("hall wall", new Vector3(20f, 2.5f, 28.5f), new Vector3(8f, 5f, 1f), hall.transform);
        }

        /// <summary>Open the door for the player's party, build the hall once it exists, and get the link open.</summary>
        public PortalLink OpenDoor(Action beforeWarm = null)
        {
            var link = H.Door.Open(PortalHarness.Owner, new[] { PortalHarness.Client });
            Step();
            BuildHall();
            for (int i = 0; i < 3; i++) Step();
            beforeWarm?.Invoke();
            H.Warm(link);
            Step();
            if (link.State != PortalState.Open) throw new InvalidOperationException("the door did not open: " + link);
            return link;
        }

        /// <summary>One worker tick and one physics step of the street's scene (the hall's is stepped by the tick).</summary>
        public void Step()
        {
            if (H.Mesh.Workers.Count > 1)
            {
                // The second worker, which holds the hall when the harness leases it away. Both workers step every instance scene in this process (the
                // scenes are process-wide statics), so the hall's own worker goes first and has posed its proxies and settled its aperture pairs by then.
                H.Other.Tick(H.TickNumber + 1);
                H.Mesh.Pump();
            }
            H.Tick();
            Physics.defaultPhysicsScene.Simulate(Dt);
        }

        public void Steps(int n) { for (int i = 0; i < n; i++) Step(); }

        public static PhysicsMaterial Slippery(float bounce = 0f) => new PhysicsMaterial("slippery")
        {
            dynamicFriction = 0f, staticFriction = 0f, bounciness = bounce,
            frictionCombine = PhysicsMaterialCombine.Minimum, bounceCombine = PhysicsMaterialCombine.Maximum,
        };

        /// <summary>A crate of the street at (x, y, z), one metre on a side.</summary>
        public NetworkIdentity Crate(Vector3 at, PhysicsMaterial material = null, ulong owner = 0UL)
        {
            var crate = H.W.SpawnServerDriven(CratePrefab, H.Street, at, Quaternion.identity);
            if (material != null) crate.GetComponent<BoxCollider>().sharedMaterial = material;
            if (owner != 0) { crate.OwnerClientId = owner; crate.OwnerIdentity = "other-" + owner; }
            return crate;
        }

        /// <summary>A pawn with a character controller standing on the street at (x, z) of the door, owned by a client of the harness.</summary>
        public NetworkIdentity Walker(float x, float z, ulong client)
        {
            var walker = H.W.SpawnServerDriven(WalkerPrefab, H.Street, new Vector3(20f + x, 0.02f, 20f + z), Quaternion.identity);
            walker.OwnerClientId = client;
            walker.OwnerIdentity = PortalHarness.Owner;
            return walker;
        }

        /// <summary>Walk a pawn one tick's worth along +z (or back), with a little gravity, the way a game's controller would before the worker steps.</summary>
        public static void Walk(NetworkIdentity walker, float metersPerSecond)
        {
            var controller = walker.GetComponent<CharacterController>();
            controller.Move(new Vector3(0f, -0.2f * Dt, metersPerSecond * Dt));
        }

        public static Rigidbody BodyOf(NetworkIdentity entity) => entity.GetComponent<Rigidbody>();

        /// <summary>The proxies standing for <paramref name="entity"/>, anywhere.</summary>
        public static List<PortalProxy> ProxiesOf(NetworkIdentity entity)
        {
            var result = new List<PortalProxy>();
            foreach (var proxy in Object.FindObjectsByType<PortalProxy>(FindObjectsInactive.Exclude))
                if (proxy.Authority == entity) result.Add(proxy);
            return result;
        }

        public static List<PortalProxy> Proxies(Func<PortalProxy, bool> filter = null)
        {
            var result = new List<PortalProxy>();
            foreach (var proxy in Object.FindObjectsByType<PortalProxy>(FindObjectsInactive.Exclude))
                if (filter == null || filter(proxy)) result.Add(proxy);
            return result;
        }

        public Scene HallScene => H.Hall.gameObject.scene;

        /// <summary>Whether a scene Nebula made (an instance's, a frame's) is still loaded: an unload takes frames.</summary>
        public static bool NebulaScenesLoaded()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.name.StartsWith("Nebula instance") || scene.name.StartsWith("Nebula frame")) return true;
            }
            return false;
        }

        public void Dispose()
        {
            H.Dispose();
            foreach (var go in Made) if (go != null) Object.DestroyImmediate(go);
            Made.Clear();
            foreach (var proxy in Object.FindObjectsByType<PortalProxy>(FindObjectsInactive.Include)) Object.DestroyImmediate(proxy.gameObject);
            for (int i = SceneManager.sceneCount - 1; i >= 0; i--)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.name.StartsWith("Nebula instance") && scene.isLoaded) SceneManager.UnloadSceneAsync(scene);
            }
            Physics.simulationMode = _mode;
        }
    }
}
