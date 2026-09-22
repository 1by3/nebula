using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nebula.Tests
{
    /// <summary>
    /// Conformance scenario 8 (see <c>docs/conformance-suite.md</c>): a server-driven entity with custom handover
    /// state, NetworkVariables and a sync-channel behaviour crosses workers and keeps every field. The handover runs
    /// through two real workers on the <see cref="ConformanceMesh"/>: the sender's own <c>TransferAuthority</c>
    /// builds the <see cref="AuthorityTransferMsg"/>, the bytes it hands its transport are parsed by the receiver's
    /// own <c>Dispatch</c>, and <c>OnAuthorityTransfer</c> applies them. Nothing in between is a test double.
    /// </summary>
    [Category("Conformance")]
    public sealed class ConformanceHandoverStateTests
    {
        /// <summary>Game state of three kinds: replicated variables, handover-only state, and authority bookkeeping.</summary>
        private sealed class Cargo : NetworkBehaviour
        {
            public NetworkVariable<int> Fuel = new NetworkVariable<int>();
            public NetworkVariable<string> Label = new NetworkVariable<string>();

            /// <summary>Handover-only: never in the per-tick stream, only in <see cref="WriteHandoverState"/>.</summary>
            public float Heading;
            public uint RngState;

            public int GainedAuthority;
            public int LostAuthority;
            /// <summary>What <see cref="Heading"/> was when authority arrived: the documented order is state first, then the hook.</summary>
            public float HeadingAtGain = float.NaN;

            public override void WriteHandoverState(NetworkWriter writer)
            {
                writer.WriteFloat(Heading);
                writer.WriteUInt(RngState);
            }

            public override void ReadHandoverState(NetworkReader reader)
            {
                Heading = reader.ReadFloat();
                RngState = reader.ReadUInt();
            }

            public override void OnGainedAuthority()
            {
                GainedAuthority++;
                HeadingAtGain = Heading;
            }

            public override void OnLostAuthority() => LostAuthority++;
        }

        private ConformanceMesh _mesh;
        private Container _outdoor;
        private ushort _prefabId;

        [SetUp]
        public void SetUp()
        {
            _mesh = new ConformanceMesh(2);
            _outdoor = _mesh.AddStaticContainer("conformance-outdoor", Vector3.zero, new Vector3(400, 60, 400));

            // The prefab: root identity + Cargo, and a child NetworkTransform riding the sync channel.
            var prefab = new GameObject("cargo-prefab");
            prefab.AddComponent<NetworkIdentity>();
            prefab.AddComponent<Cargo>();
            var turret = new GameObject("turret");
            turret.transform.SetParent(prefab.transform, false);
            var nt = turret.AddComponent<NetworkTransform>();
            nt.UseUnreliableDeltas = false;
            nt.SyncScaleX = nt.SyncScaleY = nt.SyncScaleZ = true;
            nt.Interpolate = false;
            _prefabId = _mesh.RegisterPrefab(prefab);
        }

        [TearDown]
        public void TearDown() => _mesh.Dispose();

        private static readonly Vector3 Position = new Vector3(12f, 0.5f, -7f);
        private static readonly Quaternion Rotation = Quaternion.Euler(0f, 90f, 0f);
        private static readonly Vector3 Velocity = new Vector3(1.5f, 0f, -2f);
        private static readonly Vector3 TurretPosition = new Vector3(0f, 1.2f, 0.3f);
        private static readonly Quaternion TurretRotation = Quaternion.Euler(10f, 45f, 0f);
        private static readonly Vector3 TurretScale = new Vector3(1f, 2f, 1.5f);

        /// <summary>Spawn the cargo on <paramref name="owner"/> and give every kind of state a distinctive value.</summary>
        private NetworkIdentity SpawnLoaded(ConformanceMesh.Worker owner)
        {
            var e = owner.SpawnServerDriven(_prefabId, _outdoor, Position, Rotation);
            Assume.That(e.IsServerDriven, Is.True, "SpawnServerDriven marks the entity");
            Assume.That(e.Epoch, Is.EqualTo(1u));
            var cargo = e.GetComponent<Cargo>();
            cargo.Fuel.Value = 42;
            cargo.Label.Value = "alpha";
            cargo.Heading = 1.25f;
            cargo.RngState = 0xDEADBEEF;
            e.Motion.Velocity = Velocity;
            var turret = e.transform.Find("turret");
            turret.localPosition = TurretPosition;
            turret.localRotation = TurretRotation;
            turret.localScale = TurretScale;
            return e;
        }

        private static void AssertLoaded(NetworkIdentity e, string side)
        {
            var cargo = e.GetComponent<Cargo>();
            Assert.AreEqual(42, cargo.Fuel.Value, $"{side}: NetworkVariable<int>");
            Assert.AreEqual("alpha", cargo.Label.Value, $"{side}: NetworkVariable<string>");
            Assert.AreEqual(1.25f, cargo.Heading, 1e-6f, $"{side}: handover state (float)");
            Assert.AreEqual(0xDEADBEEFu, cargo.RngState, $"{side}: handover state (uint)");
            Assert.AreEqual(0f, Vector3.Distance(Position, e.LocalPosition), 1e-3f, $"{side}: local position");
            Assert.AreEqual(0f, Quaternion.Angle(Rotation, e.LocalRotation), 0.01f, $"{side}: local rotation");
            Assert.AreEqual(0f, Vector3.Distance(Velocity, e.Motion.Velocity), 1e-4f, $"{side}: velocity");
            var turret = e.transform.Find("turret");
            Assert.AreEqual(0f, Vector3.Distance(TurretPosition, turret.localPosition), 1e-3f, $"{side}: sync-channel child position");
            Assert.AreEqual(0f, Quaternion.Angle(TurretRotation, turret.localRotation), 0.05f, $"{side}: sync-channel child rotation");
            Assert.AreEqual(0f, Vector3.Distance(TurretScale, turret.localScale), 1e-3f, $"{side}: sync-channel child scale");
            Assert.IsTrue(e.IsServerDriven, $"{side}: IsServerDriven");
        }

        [Test]
        public void AServerDrivenEntityCrossesWorkersWithEveryFieldIntact()
        {
            var w1 = _mesh[0];
            var w2 = _mesh[1];
            var sent = SpawnLoaded(w1);
            ulong netId = sent.NetId;
            Assume.That(netId, Is.Not.EqualTo(0UL));

            // 1. The sender's own TransferAuthority builds the message; the receiver's own Dispatch applies it.
            w1.Transfer(sent, w2);
            _mesh.Pump();

            // 2. What went over the wire is the production message, parsed with the production reader.
            var transfers = _mesh.DeliveredOf(MsgId.AuthorityTransfer, to: "w2");
            Assert.AreEqual(1, transfers.Count, "exactly one authority transfer reached w2");
            var msg = transfers[0].Read(AuthorityTransferMsg.Read);
            Assert.AreEqual(netId, msg.Entity.NetId);
            Assert.AreEqual(2u, msg.NewEpoch, "the epoch is bumped by exactly one");
            Assert.AreEqual(2u, msg.Entity.Epoch, "and the spawn body carries the new epoch");
            Assert.AreEqual(EntityFlags.ServerDriven, msg.Entity.Flags & EntityFlags.ServerDriven, "the server-driven flag travels");
            Assert.AreEqual(w2.Index, msg.Entity.OwnerWorkerIndex);
            Assert.IsNotNull(msg.HandoverState);
            // Blob layout (NetworkIdentity.WriteHandoverState): one count byte, then a u16 length and payload per
            // behaviour, in behaviour order. Cargo is behaviour 0 (root before child) and wrote exactly 8 bytes; the
            // NetworkTransform's own chunk follows and is its business.
            var blob = new NetworkReader(msg.HandoverState);
            Assert.AreEqual(sent.Behaviours.Length, blob.ReadByte(), "one chunk per behaviour");
            Assert.AreEqual(sizeof(float) + sizeof(uint), blob.ReadUShort(), "Cargo's chunk is exactly what it wrote");
            Assert.AreEqual(1.25f, blob.ReadFloat(), 1e-6f, "Cargo's heading, as written by WriteHandoverState");
            Assert.AreEqual(0xDEADBEEFu, blob.ReadUInt(), "Cargo's RNG state");
            Assert.IsTrue(msg.Entity.Vars.Length > 0, "NetworkVariables ride in the spawn body");
            Assert.IsTrue(msg.Entity.State.Length > 0, "the sync-channel keyframe rides in the spawn body");
            CollectionAssert.Contains(msg.GhostWorkers, "w1", "the old owner asks to be kept as a ghost");

            // 3. The receiver holds the entity with every field, as the authority, at the new epoch.
            var received = w2.Find(netId);
            Assert.IsNotNull(received, "w2 instantiated the entity from the ghost spawn that preceded the transfer");
            Assert.IsTrue(received.HasAuthority, "w2 is the authority now");
            Assert.AreEqual(2u, received.Epoch);
            Assert.AreEqual(w2.Index, received.OwnerWorkerIndex);
            AssertLoaded(received, "receiver");
            Assert.AreSame(_outdoor, received.Container, "same container, resolved by reference on the receiver");
            var cargoIn = received.GetComponent<Cargo>();
            Assert.AreEqual(1, cargoIn.GainedAuthority, "OnGainedAuthority fired once on the receiver");
            Assert.AreEqual(0, cargoIn.LostAuthority);
            Assert.AreEqual(1.25f, cargoIn.HeadingAtGain, 1e-6f, "handover state was applied before OnGainedAuthority");
            Assert.AreEqual(1, w2.Instance.HandoversIn);
            Assert.AreEqual(1, w2.Instance.AuthoritativeCount);

            // 4. The sender became a ghost at the new epoch and kept the replicated fields it still needs to display.
            Assert.IsFalse(sent.HasAuthority, "w1 is a ghost now");
            Assert.AreEqual(2u, sent.Epoch, "the ghost holds the new epoch, so it rejects anything older");
            Assert.AreEqual(w2.Index, sent.OwnerWorkerIndex);
            Assert.IsTrue(sent.IsServerDriven, "the ghost still knows the entity is server-driven");
            var cargoOut = sent.GetComponent<Cargo>();
            Assert.AreEqual(1, cargoOut.LostAuthority, "OnLostAuthority fired once on the sender");
            Assert.AreEqual(1, w1.Instance.HandoversOut);
            Assert.AreEqual(0, w1.Instance.AuthoritativeCount);
            Assert.AreEqual(1, w1.Instance.EntityCount, "the object stays as a ghost");

            // 5. The new owner re-ghosts to the old owner, so w1's copy keeps being driven.
            Assert.AreEqual(1, _mesh.DeliveredOf(MsgId.GhostSpawn, to: "w1").Count, "w2 opened its ghost stream to w1");
            Assert.AreEqual(1, w2.Instance.GhostsSent);
        }

        [Test]
        public void AHandoverBackKeepsTheFieldsAndBumpsTheEpochAgain()
        {
            var w1 = _mesh[0];
            var w2 = _mesh[1];
            var origin = SpawnLoaded(w1);
            ulong netId = origin.NetId;
            w1.Transfer(origin, w2);
            _mesh.Pump();
            var onW2 = w2.Find(netId);
            Assume.That(onW2.HasAuthority, Is.True);

            // w2 changes what it owns, then hands the entity back.
            var cargo = onW2.GetComponent<Cargo>();
            cargo.Fuel.Value = 7;
            cargo.Heading = -0.5f;
            cargo.RngState = 99;
            w2.Transfer(onW2, w1);
            _mesh.Pump();

            var back = w1.Find(netId);
            Assert.AreSame(origin, back, "the original object is reused: it was kept as a ghost, not re-instantiated");
            Assert.IsTrue(back.HasAuthority);
            Assert.AreEqual(3u, back.Epoch, "one bump per handover");
            Assert.IsTrue(back.IsServerDriven);
            var cargoBack = back.GetComponent<Cargo>();
            Assert.AreEqual(7, cargoBack.Fuel.Value, "the receiver's value, not the sender's stale one");
            Assert.AreEqual("alpha", cargoBack.Label.Value);
            Assert.AreEqual(-0.5f, cargoBack.Heading, 1e-6f);
            Assert.AreEqual(99u, cargoBack.RngState);
            Assert.AreEqual(1, cargoBack.LostAuthority);
            Assert.AreEqual(2, cargoBack.GainedAuthority, "once at spawn, once on the way back");
            Assert.AreEqual(-0.5f, cargoBack.HeadingAtGain, 1e-6f, "the handover state was in place before the hook ran");
            Assert.IsFalse(onW2.HasAuthority);
            Assert.AreEqual(3u, onW2.Epoch);
            Assert.AreEqual(1, w1.Instance.AuthoritativeCount);
            Assert.AreEqual(0, w2.Instance.AuthoritativeCount);
            Assert.AreEqual(1, w1.Instance.HandoversIn);
            Assert.AreEqual(1, w2.Instance.HandoversOut);
        }

        [Test]
        public void AReplayedTransferAtTheSameEpochIsIgnored()
        {
            var w1 = _mesh[0];
            var w2 = _mesh[1];
            var e = SpawnLoaded(w1);
            ulong netId = e.NetId;
            w1.Transfer(e, w2);
            _mesh.Pump();
            var transfer = _mesh.DeliveredOf(MsgId.AuthorityTransfer, to: "w2")[0];
            var received = w2.Find(netId);
            received.GetComponent<Cargo>().Fuel.Value = 1000;

            LogAssert.Expect(LogType.Warning, new Regex("stale authority transfer"));
            _mesh.Replay(transfer);

            Assert.AreEqual(2u, received.Epoch, "the epoch did not move");
            Assert.AreEqual(1, w2.Instance.HandoversIn, "the duplicate was not counted as a handover");
            Assert.AreEqual(1000, received.GetComponent<Cargo>().Fuel.Value, "the replay did not roll the state back");
            Assert.AreEqual(1, received.GetComponent<Cargo>().GainedAuthority);
        }
    }
}
