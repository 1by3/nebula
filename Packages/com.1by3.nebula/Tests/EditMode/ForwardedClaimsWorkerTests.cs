using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nebula.Tests
{
    /// <summary>
    /// Forwarded token claims on workers (NEB-357). The gateway tells a worker the claims of a player's verified token
    /// with the <see cref="SpawnPlayerMsg"/> that claims the session; the worker hands them to the game in
    /// <see cref="PlayerInfo.Claims"/>, puts them on every entity the player owns (<see cref="NetworkIdentity.OwnerClaims"/>),
    /// answers <see cref="NebulaWorker.GetPlayerClaims"/>, and sends them along with every ghost spawn and handover,
    /// so they survive a change of worker. A reconnection with a newer token replaces them everywhere. Two real
    /// workers on the <see cref="ConformanceMesh"/>, with its recording gateway. The gateway half (which claims are
    /// forwarded, the limits, anonymous players, the name claim) is <c>Services~/Nebula.Services.Tests/ForwardedClaimsTests.cs</c>.
    /// </summary>
    [Category("Conformance")]
    public sealed class ForwardedClaimsWorkerTests
    {
        private const ulong Client = 91;

        private static readonly IReadOnlyDictionary<string, string> Admin =
            PlayerClaims.From(new Dictionary<string, string> { ["is_admin"] = "true", ["username"] = "alice" });
        private static readonly IReadOnlyDictionary<string, string> NotAdmin =
            PlayerClaims.From(new Dictionary<string, string> { ["is_admin"] = "false", ["username"] = "alice" });

        private ConformanceMesh _mesh;
        private ConformanceMesh.Gateway _gateway;
        private Container _outdoor;
        private ushort _prefabId;
        private readonly List<GameObject> _objects = new List<GameObject>();

        /// <summary>A game mode that spawns the registered prefab and remembers what it was told about the player.</summary>
        private sealed class RecordingGameMode : NebulaGameMode
        {
            public ushort PrefabId;
            public readonly List<PlayerInfo> Joined = new List<PlayerInfo>();

            public override NetworkIdentity OnSpawnPlayer(NebulaWorker worker, in PlayerInfo player, Container container)
            {
                Joined.Add(player);
                return NetworkPrefabs.Instantiate(PrefabId, new Vector3(5f, 1f, 5f), Quaternion.identity, container.transform);
            }
        }

        [SetUp]
        public void SetUp()
        {
            _mesh = new ConformanceMesh(2);
            _gateway = _mesh.AddGateway("g1");
            _outdoor = _mesh.AddStaticContainer("claims-outdoor", Vector3.zero, new Vector3(400, 60, 400));
            var prefab = new GameObject("claims-pawn-prefab");
            prefab.AddComponent<NetworkIdentity>();
            _prefabId = _mesh.RegisterPrefab(prefab);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _objects) if (o != null) Object.DestroyImmediate(o);
            _objects.Clear();
            _mesh.Dispose();
        }

        private NetworkIdentity SpawnOwned(ConformanceMesh.Worker owner, Vector3 position)
        {
            NetworkIdentity e = null;
            owner.Act(() =>
            {
                e = NetworkPrefabs.Instantiate(_prefabId, position, Quaternion.identity, _outdoor.transform);
                owner.Instance.Spawn(e, _outdoor, Client);
            });
            return e;
        }

        private void Claim(ConformanceMesh.Worker worker, ulong generation, IReadOnlyDictionary<string, string> claims) =>
            _mesh.FromGateway(_gateway, worker, w => new SpawnPlayerMsg { ClientId = Client, Container = _outdoor.Ref, Name = "alice", Identity = "alice-identity", Generation = generation, Claims = claims }.Write(w));

        /// <summary>The player's pawn on <paramref name="owner"/>, and the session claimed with <paramref name="claims"/> at generation 1.</summary>
        private NetworkIdentity SpawnPawn(ConformanceMesh.Worker owner, IReadOnlyDictionary<string, string> claims)
        {
            var pawn = SpawnOwned(owner, new Vector3(5f, 1f, 5f));
            Claim(owner, 1, claims);
            return pawn;
        }

        [Test]
        public void TheGameModeAndThePawnGetTheClaimsTheGatewaySent()
        {
            var w1 = _mesh[0];
            var host = new GameObject("claims-game-mode");
            _objects.Add(host);
            var mode = host.AddComponent<RecordingGameMode>();
            mode.PrefabId = _prefabId;
            typeof(NebulaWorker).GetField("_gameMode", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(w1.Instance, mode);

            Claim(w1, 1, Admin);

            Assert.AreEqual(1, mode.Joined.Count, "the game was asked for a pawn");
            CollectionAssert.AreEquivalent(Admin, mode.Joined[0].Claims);
            var pawn = w1.Instance.FindPlayer(Client);
            Assert.IsNotNull(pawn);
            Assert.AreEqual("true", pawn.OwnerClaims["is_admin"]);
            Assert.IsTrue(pawn.TryGetOwnerClaim("username", out var name) && name == "alice");
            Assert.IsFalse(pawn.TryGetOwnerClaim("email", out _));
            CollectionAssert.AreEquivalent(Admin, w1.Instance.GetPlayerClaims(Client));
        }

        [Test]
        public void EverythingThePlayerOwnsCarriesTheClaimsAndNothingElseDoes()
        {
            var w1 = _mesh[0];
            var pawn = SpawnPawn(w1, Admin);
            CollectionAssert.AreEquivalent(Admin, pawn.OwnerClaims, "a claim for a pawn the worker already holds gives it the claims");

            var vehicle = SpawnOwned(w1, new Vector3(8f, 1f, 5f));
            CollectionAssert.AreEquivalent(Admin, vehicle.OwnerClaims, "an entity spawned for the player later gets them too");

            var npc = w1.SpawnServerDriven(_prefabId, _outdoor, new Vector3(12f, 1f, 5f), Quaternion.identity);
            Assert.AreEqual(0, npc.OwnerClaims.Count, "an entity no player owns has none");
            Assert.AreEqual(0, w1.Instance.GetPlayerClaims(Client + 1).Count, "an unknown player has none");
        }

        [Test]
        public void APlayerWithoutClaimsHasNone()
        {
            var w1 = _mesh[0];
            var pawn = SpawnPawn(w1, null);
            Assert.IsNotNull(pawn.OwnerClaims);
            Assert.AreEqual(0, pawn.OwnerClaims.Count);
            Assert.AreEqual(0, w1.Instance.GetPlayerClaims(Client).Count);
        }

        [Test]
        public void AGatewayIsNeverSentTheClaims()
        {
            var w1 = _mesh[0];
            var pawn = SpawnPawn(w1, Admin);
            Claim(w1, 1, Admin); // a repeat claim: the worker re-announces the pawn to the gateway
            _mesh.Pump();
            var announced = _mesh.DeliveredOf(MsgId.EntitySpawn, _gateway.Id);
            Assert.IsNotEmpty(announced);
            foreach (var message in announced)
            {
                var spawn = message.Read(EntitySpawnMsg.Read);
                Assert.AreEqual(pawn.NetId, spawn.NetId);
                Assert.IsNull(spawn.OwnerClaims, "claims are for workers; a gateway would only pass them to clients");
            }
        }

        [Test]
        public void TheClaimsTravelWithAHandoverAndItsGhostSpawn()
        {
            var w1 = _mesh[0];
            var w2 = _mesh[1];
            var pawn = SpawnPawn(w1, Admin);
            ulong netId = pawn.NetId;

            w1.Transfer(pawn, w2);
            _mesh.Pump();

            var ghostSpawn = _mesh.DeliveredOf(MsgId.GhostSpawn, w2.Id)[0].Read(EntitySpawnMsg.Read);
            CollectionAssert.AreEquivalent(Admin, ghostSpawn.OwnerClaims, "the ghost spawn carries them");
            var transfer = _mesh.DeliveredOf(MsgId.AuthorityTransfer, w2.Id)[0].Read(AuthorityTransferMsg.Read);
            CollectionAssert.AreEquivalent(Admin, transfer.Entity.OwnerClaims, "and so does the handover");

            var moved = w2.Find(netId);
            Assert.IsTrue(moved != null && moved.HasAuthority, "the second worker owns the pawn");
            CollectionAssert.AreEquivalent(Admin, moved.OwnerClaims);
            CollectionAssert.AreEquivalent(Admin, w2.Instance.GetPlayerClaims(Client), "the new worker knows the player now");
            var later = SpawnOwned(w2, new Vector3(9f, 1f, 5f));
            CollectionAssert.AreEquivalent(Admin, later.OwnerClaims, "an entity the new worker spawns for the player gets the claims");

            var ghost = w1.Find(netId);
            Assert.IsTrue(ghost != null && !ghost.HasAuthority, "the old worker keeps a ghost");
            CollectionAssert.AreEquivalent(Admin, ghost.OwnerClaims, "whose claims are the player's too");
        }

        [Test]
        public void AReconnectionWithANewerTokenReplacesTheClaimsOnThePawnAndItsGhosts()
        {
            var w1 = _mesh[0];
            var w2 = _mesh[1];
            var pawn = SpawnPawn(w1, Admin);
            ulong netId = pawn.NetId;
            w1.Transfer(pawn, w2);
            _mesh.Pump();

            // The player reconnects; the gateway claims the session again from the worker that owns the pawn, with
            // the claims of the token presented this time.
            Claim(w2, 2, NotAdmin);
            _mesh.Pump();

            CollectionAssert.AreEquivalent(NotAdmin, w2.Find(netId).OwnerClaims, "the pawn has the new claims");
            CollectionAssert.AreEquivalent(NotAdmin, w2.Instance.GetPlayerClaims(Client));
            CollectionAssert.AreEquivalent(NotAdmin, w1.Find(netId).OwnerClaims, "and the ghost on the other worker is told");
        }

        [Test]
        public void AReconnectionHeardBeforeTheHandoverArrivesIsNotUndoneByIt()
        {
            var w1 = _mesh[0];
            var w2 = _mesh[1];
            var pawn = SpawnPawn(w1, Admin);
            ulong netId = pawn.NetId;

            // The handover is in flight when the gateway, following the pawn, claims the session on the new worker
            // with a newer token. The new worker has no game mode, so it says it cannot spawn a pawn of its own.
            w1.Transfer(pawn, w2);
            LogAssert.Expect(LogType.Error, new Regex("cannot spawn player"));
            Claim(w2, 2, NotAdmin);
            _mesh.Pump();

            var moved = w2.Find(netId);
            Assert.IsTrue(moved != null && moved.HasAuthority);
            CollectionAssert.AreEquivalent(NotAdmin, moved.OwnerClaims, "the handover carried generation 1's claims; generation 2's win");
            CollectionAssert.AreEquivalent(NotAdmin, w2.Instance.GetPlayerClaims(Client));
        }
    }
}
