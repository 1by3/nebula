using System;
using System.Collections.Generic;
using System.Reflection;
using Nebula.World;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>
    /// A door on a planet that is a carrier, with real geometry and real physics (NEB-432, <c>docs/portals.md</c> D26): the planet stands 12,000 km from its
    /// scope's origin, turned, with its ground a grid it hosts; the door is in a ground chunk 205 km from the planet's centre and is turned on the ground as
    /// well; the vault it leads to is an instance scope. The planet's frame has its own physics scene and its floating origin sits at the door.
    /// <para>
    /// The door faces <see cref="DoorTurn"/> x +z in the planet's frame. The ground is a floor 2.5 m below the face with a hill of two walls and a roof around
    /// a 4 x 5 m tunnel that starts at the face; the vault has a floor and a back wall. One worker; the test is its clock and steps the scenes it does not
    /// (the hall's and the planet's are stepped by the worker's tick).
    /// </para>
    /// </summary>
    internal sealed class FramedPortalRig : IDisposable
    {
        public const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        public const string Scope = "space/vaults";
        public const string GridKey = "planet/vaults/0";
        public const string Owner = "player-a";
        public const ulong Client = 7;
        public const float FaceHeight = 2.5f;
        public static readonly Vector3 SpaceCell = new Vector3(8192f, 8192f, 8192f);
        /// <summary>Where the planet's centre stands in the scope, absolute: well past 10^7 m.</summary>
        public static readonly Vector3 PlanetAt = new Vector3(12_000_000f, 400_000f, -7_000_000f);
        public static readonly Quaternion CarrierTurn = Quaternion.Euler(8f, 35f, -6f);
        public static readonly Vector3 PlanetBox = new Vector3(500_000f, 2_000f, 500_000f);
        public static readonly Vector3 GroundCell = new Vector3(512f, 2_000f, 512f);
        /// <summary>The door, in the planet's own coordinates: 205 km from its centre (a float is 1.5 cm apart there).</summary>
        public static readonly Vector3 Door = new Vector3(204_864f, FaceHeight, 1_088f);
        /// <summary>The door's turn on the planet's ground: it faces +x.</summary>
        public static readonly Quaternion DoorTurn = Quaternion.Euler(0f, 90f, 0f);

        public readonly ConformanceMesh Mesh;
        public readonly LocalControlPlane Plane;
        public readonly InstanceTemplate Template;
        public readonly ConformanceMesh.Gateway Gateway;
        public readonly WorldDefinition Definition;
        public readonly List<GameObject> Made = new List<GameObject>();
        public readonly ushort CratePrefab, WalkerPrefab, PawnPrefab;
        public NetworkIdentity Planet;
        public Container Chunk;
        public Portal Portal;
        public NetworkIdentity Player;
        public uint TickNumber;
        public RuntimeGrid Ground;
        private readonly SimulationMode _mode;
        private readonly ushort _planetPrefab;

        public ConformanceMesh.Worker W => Mesh[0];
        public PhysicsFrame Frame => Planet.Carried.Frame;
        public static float Dt => NetworkTime.TickInterval;
        public static ulong Private => NebulaWorker.InstanceKey("vault/" + Owner);
        public Container Hall => ContainerRegistry.GetRuntime(NebulaWorker.InstanceKey("vault/" + Owner + "/hall"));
        public Scene HallScene => Hall.gameObject.scene;
        public Scene PlanetScene => Frame.Scene;

        /// <summary>When set, the vault is leased to the second worker (a split link) once it exists.</summary>
        public bool HallOnSecondWorker;
        /// <summary>The planet's worker takes its tick before the vault's worker (two workers).</summary>
        public bool PlanetFirst;
        public ConformanceMesh.Worker Other => Mesh[1];

        public FramedPortalRig(int workers = 1)
        {
            _mode = Physics.simulationMode;
            Physics.simulationMode = SimulationMode.Script;
            Definition = ScriptableObject.CreateInstance<WorldDefinition>();
            Definition.CellSize = GroundCell;
            NebulaWorld.LoadRuntime(Definition);
            Mesh = new ConformanceMesh(workers);
            Gateway = Mesh.AddGateway("gw");
            Mesh.LinkGateway(Gateway);
            if (workers == 1) Mesh.Config.GhostBandMargin = -1f;
            Plane = new LocalControlPlane();
            Plane.Connect();
            foreach (var worker in Mesh.Workers)
            {
                PortalHarness.AttachRegistered(worker.Instance, Plane);
                Plane.RegisterWorker(worker.Id, worker.Index, "127.0.0.1", (ushort)(7000 + worker.Index));
                Plane.HeartbeatWorker(worker.Id, WorkerStatus.Ready, default);
                worker.Instance.enabled = false; // the test is the workers' clock
            }

            Template = ScriptableObject.CreateInstance<InstanceTemplate>();
            Template.TemplateId = "vault";
            Template.Parts = new[] { new InstanceTemplate.Part { Id = "hall", Bounds = new Bounds(new Vector3(0f, 2f, 0f), new Vector3(8f, 4f, 8f)) } };

            var planet = new GameObject("planet-prefab");
            planet.AddComponent<NetworkIdentity>();
            var box = planet.AddComponent<Container>();
            box.ContainerId = "planet";
            box.Size = PlanetBox;
            box.Center = Vector3.zero;
            box.OwnPhysicsFrame = true;
            box.FrameInterest = FrameInterestMode.OwnRegions;
            planet.AddComponent<NetworkTransform>();
            Made.Add(planet);
            _planetPrefab = Mesh.RegisterPrefab(planet);

            var pawn = new GameObject("pawn-prefab");
            pawn.AddComponent<NetworkIdentity>();
            Made.Add(pawn);
            PawnPrefab = Mesh.RegisterPrefab(pawn);

            var crate = new GameObject("crate-prefab");
            crate.AddComponent<NetworkIdentity>();
            crate.AddComponent<BoxCollider>().size = Vector3.one;
            var body = crate.AddComponent<Rigidbody>();
            body.mass = 1f;
            body.linearDamping = 0f;
            body.angularDamping = 0.05f;
            Made.Add(crate);
            CratePrefab = Mesh.RegisterPrefab(crate);

            var walker = new GameObject("walker-prefab");
            walker.AddComponent<NetworkIdentity>();
            var controller = walker.AddComponent<CharacterController>();
            controller.radius = 0.4f;
            controller.height = 1.8f;
            controller.center = new Vector3(0f, 0.9f, 0f);
            Made.Add(walker);
            WalkerPrefab = Mesh.RegisterPrefab(walker);

            Build();
        }

        private void Mirror()
        {
            ContainerRegistry.SyncRuntime(Plane.Leases);
            foreach (var lease in Plane.Leases)
                if (lease.WorkerId == W.Id) ContainerRegistry.ApplyLease(lease.ContainerId, W.Id, W.Index, lease.Epoch);
            var hall = Hall;
            if (HallOnSecondWorker && hall != null) ContainerRegistry.ApplyLease(hall.ContainerId, Other.Id, Other.Index, 7);
        }

        /// <summary>The scope with the planet in its chunk, its ground hosted by the planet, the door's chunk leased, the frame's origin at the door, the street and the door built.</summary>
        private void Build()
        {
            var space = new RuntimeGrid(SpaceCell, planar: false, scopeKey: Scope);
            NebulaChunks.Activate(space, NebulaRoles.Worker, true, null);
            Plane.ActivateScope(new ScopeActivationRequest
            {
                ScopeKey = Scope,
                Definition = new ChunkGridDefinition { CellSize = SpaceCell, Planar = false }.ToScopeDefinition(),
                PreferredWorkerId = W.Id,
            });
            var coord = space.CoordOfAbsolute(PlanetAt);
            var spaceInstance = new InstanceContainerInfo { InstanceId = space.InstanceId, ScopeKey = Scope, PartId = ChunkKeys.PartId(coord) };
            var spaceChunk = ContainerRegistry.RegisterRuntime(space.IdOf(coord), space.BoundsOf(coord), spaceInstance);
            ContainerRegistry.ApplyLease(spaceChunk.ContainerId, W.Id, W.Index, 1);
            Plane.EnsureRuntimeContainer(spaceChunk.ContainerId, ContainerRegistry.ToAbsolute(space.BoundsOf(coord), space.InstanceId), W.Id, spaceInstance);
            W.Act(() =>
            {
                Planet = NetworkPrefabs.Instantiate(_planetPrefab, PlanetAt + space.Frame.OriginOffset, CarrierTurn, spaceChunk.ContentRoot);
                W.Instance.SpawnServerDriven(Planet, spaceChunk);
            });
            if (Planet.Carried == null || Planet.Carried.Frame == null) throw new InvalidOperationException("the planet has no physics frame");

            Ground = RuntimeGrid.Hosted(new ChunkGridDefinition { CellSize = GroundCell, Planar = true }, Scope, GridKey, Planet.Carried.ContainerId);
            NebulaChunks.Activate(Ground, NebulaRoles.Worker, true, null);
            var groundCoord = Ground.CoordOfAbsolute(Door);
            var info = new InstanceContainerInfo { InstanceId = ScopeKeys.Hash(Scope), ScopeKey = Scope, PartId = Ground.PartIdOf(groundCoord) };
            Plane.EnsureRuntimeContainer(Ground.ContainerIdOf(groundCoord), Ground.PlacementOf(groundCoord), W.Id, info);
            Mirror();
            Chunk = ContainerRegistry.GetRuntime(Ground.IdOf(groundCoord));
            if (Chunk == null || Chunk.InnerSpace != Planet.Carried) throw new InvalidOperationException("the door's chunk is not in the planet's frame");

            // The worker keeps a frame's origin near what it simulates in it (docs/container-tree.md D19): here, at the door.
            PhysicsFrames.ShiftOrigin(Frame, Door);
            BuildStreet();
            var go = new GameObject("vault door");
            Made.Add(go);
            go.transform.SetParent(Chunk.ContentRoot, false);
            go.transform.SetPositionAndRotation(Frame.LocalToSimulation(Door), DoorTurn);
            Portal = go.AddComponent<Portal>();
            Portal.Id = "vault-door";
            Portal.FarTemplate = Template;
            Portal.Aperture = PortalAperture.Box(2f, 2.5f);
            Portal.ClockForTests = () => 100f;
            PortalHarness.Enable(Portal);
            Player = Spawn(PawnPrefab, At(0f, 1f, -6f), DoorTurn);
            Player.OwnerClientId = Client;
            Player.OwnerIdentity = Owner;
            Physics.SyncTransforms();
        }

        /// <summary>A point in the door's axes, <paramref name="y"/> above the street's floor, as a point of the planet's frame scene.</summary>
        public Vector3 At(float x, float y, float z) => Frame.LocalToSimulation(Door) + DoorTurn * new Vector3(x, y - FaceHeight, z);

        /// <summary><see cref="At"/> by the frame's floating origin, so it holds when the frame is not at its simulation pose (a client between its prediction steps).</summary>
        public Vector3 SimAt(float x, float y, float z) => Door - Frame.Origin + DoorTurn * new Vector3(x, y - FaceHeight, z);

        /// <summary>The door's axes: a direction along its +z, in the planet scene.</summary>
        public static Vector3 Forward => DoorTurn * Vector3.forward;

        /// <summary>A body's position in the door's axes, the face at z = 0 and the floor at y = 0.</summary>
        public Vector3 InDoorAxes(Vector3 scenePosition) => Quaternion.Inverse(DoorTurn) * (scenePosition - Frame.LocalToSimulation(Door)) + new Vector3(0f, FaceHeight, 0f);

        public GameObject Box(string name, Transform parent, Vector3 center, Vector3 size)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = center;
            go.AddComponent<BoxCollider>().size = size;
            Made.Add(go);
            return go;
        }

        /// <summary>The street: a floor, and a hill of two walls and a roof around a 4 x 5 m tunnel that starts at the face, as a game builds it under the door's chunk.</summary>
        private void BuildStreet()
        {
            var doorway = new GameObject("doorway");
            Made.Add(doorway);
            doorway.transform.SetParent(Chunk.ContentRoot, false);
            doorway.transform.SetPositionAndRotation(Frame.LocalToSimulation(Door), DoorTurn);
            Box("street floor", doorway.transform, new Vector3(0f, -3f, 0f), new Vector3(40f, 1f, 16f));
            Box("hill left", doorway.transform, new Vector3(-6f, 0f, 3f), new Vector3(8f, 5f, 6f));
            Box("hill right", doorway.transform, new Vector3(6f, 0f, 3f), new Vector3(8f, 5f, 6f));
            Box("hill roof", doorway.transform, new Vector3(0f, 3f, 3f), new Vector3(12f, 1f, 6f));
        }

        /// <summary>The vault's geometry, put under the hall before the link prepares it: a floor and a back wall, in the hall's scene, about the door's face there.</summary>
        public void BuildHall(PortalLink link)
        {
            var hall = Hall ?? throw new InvalidOperationException("the hall does not exist yet");
            var face = ContainerRegistry.ToFrame(link.FarFace.Origin, link.FarScope);
            var turn = link.FarFace.Rotation;
            Place(Box("hall floor", hall.transform, Vector3.zero, new Vector3(8f, 1f, 8f)), face + turn * new Vector3(0f, -3f, 4f), turn);
            Place(Box("hall wall", hall.transform, Vector3.zero, new Vector3(8f, 5f, 1f)), face + turn * new Vector3(0f, 0f, 8.5f), turn);
        }

        private static void Place(GameObject go, Vector3 position, Quaternion rotation) => go.transform.SetPositionAndRotation(position, rotation);

        /// <summary>One worker tick; the hall's and the planet's scenes are stepped by it.</summary>
        public void Step()
        {
            // Both workers step every scene of this process (the planet's frame and the vault's are process-wide), so whoever goes second steps scenes the first has
            // already settled its pairs for. The vault's worker goes first by default, as in PortalPhysicsRig; a test that spawns on the planet side sets PlanetFirst.
            bool two = Mesh.Workers.Count > 1;
            if (two && !PlanetFirst)
            {
                Other.Tick(TickNumber + 1);
                Mesh.Pump();
            }
            W.Tick(++TickNumber);
            Mirror();
            Mesh.Pump();
            if (two && PlanetFirst)
            {
                Other.Tick(TickNumber);
                Mesh.Pump();
            }
            Physics.defaultPhysicsScene.Simulate(Dt);
        }

        public void Steps(int n) { for (int i = 0; i < n; i++) Step(); }

        /// <summary>Open the door for the player's party, build the vault once it exists, and get the link open.</summary>
        public PortalLink OpenDoor()
        {
            var link = Portal.Open(Owner, new[] { Client });
            Step();
            BuildHall(link);
            for (int i = 0; i < 3; i++) Step();
            Mesh.FromGateway(Gateway, W, w => new PortalWarmMsg { LinkId = link.Id, Generation = link.Generation, ClientId = Client }.Write(w));
            Step();
            if (link.State != PortalState.Open) throw new InvalidOperationException("the door did not open: " + link);
            return link;
        }

        public NetworkIdentity Spawn(ushort prefab, Vector3 scenePosition, Quaternion sceneRotation)
        {
            NetworkIdentity e = null;
            W.Act(() =>
            {
                e = NetworkPrefabs.Instantiate(prefab, scenePosition, sceneRotation, Chunk.ContentRoot);
                e.transform.SetPositionAndRotation(scenePosition, sceneRotation);
                W.Instance.SpawnServerDriven(e, Chunk);
            });
            return e;
        }

        /// <summary>A crate of the planet's ground, at a point in the door's axes.</summary>
        public NetworkIdentity Crate(float x, float y, float z, PhysicsMaterial material = null, ulong owner = 0UL)
        {
            var crate = Spawn(CratePrefab, At(x, y, z), DoorTurn);
            if (material != null) crate.GetComponent<BoxCollider>().sharedMaterial = material;
            if (owner != 0) { crate.OwnerClientId = owner; crate.OwnerIdentity = "other-" + owner; }
            return crate;
        }

        /// <summary>A crate of the vault, at a point in the door's axes.</summary>
        public NetworkIdentity HallCrate(float x, float y, float z, PhysicsMaterial material = null)
        {
            NetworkIdentity crate = null;
            var link = FirstLink();
            var face = ContainerRegistry.ToFrame(link.FarFace.Origin, link.FarScope);
            var at = face + link.FarFace.Rotation * new Vector3(x, y - FaceHeight, z);
            var owner = HallOnSecondWorker ? Other : W;
            owner.Act(() =>
            {
                crate = NetworkPrefabs.Instantiate(CratePrefab, at, link.FarFace.Rotation, Hall.transform);
                crate.transform.SetPositionAndRotation(at, link.FarFace.Rotation);
                owner.Instance.SpawnServerDriven(crate, Hall);
            });
            if (material != null) crate.GetComponent<BoxCollider>().sharedMaterial = material;
            return crate;
        }

        public PortalLink FirstLink()
        {
            foreach (var link in Portal.Links) return link;
            return null;
        }

        public static PhysicsMaterial Slippery(float bounce = 0f) => PortalPhysicsRig.Slippery(bounce);

        public static Rigidbody BodyOf(NetworkIdentity entity) => entity.GetComponent<Rigidbody>();

        public void Dispose()
        {
            if (Portal != null) PortalHarness.Disable(Portal);
            Mesh.Dispose();
            Plane.Dispose();
            NebulaChunks.ResetForNewSession();
            foreach (var c in new List<Container>(ContainerRegistry.Runtime)) if (c != null) Object.DestroyImmediate(c.gameObject);
            ContainerRegistry.PruneRuntime(new HashSet<ulong>());
            ContainerRegistry.RuntimeBoundsInFrame = null;
            ContainerRegistry.Rebuild();
            NebulaWorld.Unload();
            PhysicsFrames.DrainPool();
            foreach (var go in Made) if (go != null) Object.DestroyImmediate(go);
            Made.Clear();
            foreach (var proxy in Object.FindObjectsByType<PortalProxy>(FindObjectsInactive.Include)) Object.DestroyImmediate(proxy.gameObject);
            Object.DestroyImmediate(Template);
            Object.DestroyImmediate(Definition);
            InstanceScenes.Reset();
            Portals.ResetForNewSession();
            for (int i = SceneManager.sceneCount - 1; i >= 0; i--)
            {
                var scene = SceneManager.GetSceneAt(i);
                if ((scene.name.StartsWith("Nebula instance") || scene.name.StartsWith("Nebula frame")) && scene.isLoaded) SceneManager.UnloadSceneAsync(scene);
            }
            Physics.simulationMode = _mode;
        }
    }
}
