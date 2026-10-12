using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>
    /// The street-and-door scenario the portal tier-B tests share: real <see cref="NebulaWorker"/>s on the <see cref="ConformanceMesh"/> with a
    /// <see cref="LocalControlPlane"/>, one street container owned by the first worker, a <see cref="Portal"/> at (20, 0, 20) facing +z whose
    /// template far side is a one-part hall, and a pawn of client 7 standing 6 m in front of it. The owning client's half of readiness, its
    /// <see cref="MsgId.PortalWarm"/> through a gateway, is stood in for by <see cref="Warm"/>.
    /// </summary>
    internal sealed class PortalHarness : IDisposable
    {
        public const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        public const string Owner = "player-a";
        public const ulong Client = 7;
        public static readonly Vector3 DoorAt = new Vector3(20f, 0f, 20f);

        public readonly ConformanceMesh Mesh;
        public readonly LocalControlPlane Plane;
        public readonly InstanceTemplate Template;
        public readonly ConformanceMesh.Gateway Gateway;
        public readonly List<GameObject> Objects = new List<GameObject>();
        public readonly ushort PawnPrefab;
        public Container Street;
        public Portal Door;
        public NetworkIdentity Player;
        public uint TickNumber;
        public float Now = 100f;
        /// <summary>When set, the hall is leased to the second worker instead of the one that prepared it.</summary>
        public bool HallOnSecondWorker;

        public ConformanceMesh.Worker W => Mesh[0];
        public ConformanceMesh.Worker Other => Mesh[1];
        public static ulong Private => NebulaWorker.InstanceKey("vault/" + Owner);
        public static string HallId => ContainerRegistry.RuntimeContainerId(NebulaWorker.InstanceKey("vault/" + Owner + "/hall"));
        public Container Hall => ContainerRegistry.GetRuntime(NebulaWorker.InstanceKey("vault/" + Owner + "/hall"));

        public PortalHarness(int workers = 1)
        {
            Mesh = new ConformanceMesh(workers);
            Plane = new LocalControlPlane();
            Plane.Connect();
            foreach (var worker in Mesh.Workers)
            {
                AttachRegistered(worker.Instance, Plane);
                Plane.RegisterWorker(worker.Id, worker.Index, "127.0.0.1", (ushort)(7000 + worker.Index));
                Plane.HeartbeatWorker(worker.Id, WorkerStatus.Ready, default);
            }
            Gateway = Mesh.AddGateway("gw");
            Mesh.LinkGateway(Gateway);
            Template = ScriptableObject.CreateInstance<InstanceTemplate>();
            Template.TemplateId = "vault";
            Template.Parts = new[] { new InstanceTemplate.Part { Id = "hall", Bounds = new Bounds(new Vector3(0f, 2f, 0f), new Vector3(8f, 4f, 8f)) } };
            Template.ObservePublic = false;
            var pawn = new GameObject("pawn-prefab");
            pawn.AddComponent<NetworkIdentity>();
            Objects.Add(pawn);
            PawnPrefab = Mesh.RegisterPrefab(pawn);

            Street = Mesh.AddStaticContainer("street", Vector3.zero, new Vector3(400f, 50f, 400f));
            Mesh.SetOwner(Street, W);
            Door = NewPortal("door", DoorAt);
            Player = W.SpawnServerDriven(PawnPrefab, Street, At(0f, -6f), Quaternion.identity);
            Player.OwnerClientId = Client;
            Player.OwnerIdentity = Owner;
        }

        /// <summary>A portal in the street, facing +z, with the hall as its far side.</summary>
        public Portal NewPortal(string name, Vector3 at)
        {
            var go = new GameObject(name);
            Objects.Add(go);
            go.transform.position = at;
            var portal = go.AddComponent<Portal>();
            portal.Id = name;
            portal.FarTemplate = Template;
            portal.Aperture = PortalAperture.Box(2f, 2.5f);
            portal.ClockForTests = () => Now;
            Enable(portal);
            return portal;
        }

        public static void Enable(Portal portal) => typeof(Portal).GetMethod("OnEnable", Hidden).Invoke(portal, null);
        public static void Disable(Portal portal) => typeof(Portal).GetMethod("OnDisable", Hidden).Invoke(portal, null);

        /// <summary>A point a metre above the door's floor, <paramref name="x"/> and <paramref name="z"/> from its centre.</summary>
        public static Vector3 At(float x, float z) => new Vector3(20f + x, 1f, 20f + z);

        public static void AttachRegistered(NebulaWorker worker, LocalControlPlane plane)
        {
            typeof(NebulaWorker).GetField("<ControlPlane>k__BackingField", Hidden).SetValue(worker, plane);
            var registration = (WorkerRegistration)typeof(NebulaWorker).GetField("_registration", Hidden).GetValue(worker);
            typeof(WorkerRegistration).GetField("<IsRegistered>k__BackingField", Hidden).SetValue(registration, true);
            typeof(WorkerRegistration).GetField("_reconciledDocument", Hidden).SetValue(registration, plane.DocumentId ?? "");
        }

        public void Tick()
        {
            W.Tick(++TickNumber);
            ApplyLeases();
            Mesh.Pump();
        }

        public void Ticks(int n) { for (int i = 0; i < n; i++) Tick(); }

        public void ApplyLeases()
        {
            ContainerRegistry.SyncRuntime(Plane.Leases);
            foreach (var lease in Plane.Leases)
                if (lease.WorkerId == W.Id) ContainerRegistry.ApplyLease(lease.ContainerId, W.Id, W.Index, lease.Epoch);
            var hall = Hall;
            if (HallOnSecondWorker && hall != null) ContainerRegistry.ApplyLease(hall.ContainerId, Other.Id, Other.Index, 7);
        }

        /// <summary>Stand in for client <paramref name="clientId"/>'s <see cref="MsgId.PortalWarm"/>, forwarded by the network gateway.</summary>
        public void Warm(PortalLink link, ulong clientId = Client, uint? generation = null)
        {
            Mesh.FromGateway(Gateway, W, w => new PortalWarmMsg { LinkId = link.Id, Generation = generation ?? link.Generation, ClientId = clientId }.Write(w));
        }

        /// <summary>Open the door for the player's party and get it open: activated, ready, warm.</summary>
        public PortalLink OpenDoor(Portal portal = null, string party = Owner, params ulong[] members)
        {
            portal ??= Door;
            if (members.Length == 0) members = new[] { Client };
            var link = portal.Open(party, members);
            Ticks(3);
            foreach (ulong member in members) Warm(link, member);
            Tick();
            Assert.That(link.State, Is.EqualTo(PortalState.Open), "the door opened: " + link);
            return link;
        }

        public void Dispose()
        {
            foreach (var portal in Objects)
                if (portal != null && portal.GetComponent<Portal>() != null) Disable(portal.GetComponent<Portal>());
            Mesh.Dispose();
            Plane.Dispose();
            foreach (var c in new List<Container>(ContainerRegistry.Runtime)) if (c != null) Object.DestroyImmediate(c.gameObject);
            ContainerRegistry.PruneRuntime(new HashSet<ulong>());
            ContainerRegistry.Rebuild();
            foreach (var go in Objects) if (go != null) Object.DestroyImmediate(go);
            Objects.Clear();
            Object.DestroyImmediate(Template);
            InstanceScenes.Reset();
            Portals.ResetForNewSession();
        }
    }
}
