using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// Far classes (<c>docs/interest-management.md</c> D110): a class table resolves budgets, rates and radii, a crowd class
    /// fills its own budget and never evicts the default class's hulls, a total cap serves classes by priority, and the
    /// config repairs nonsense. Plus the protocol-32 far entry fields and the compact pose message (D111) on the wire.
    /// </summary>
    [Category("Conformance")]
    public sealed class FarClassTests
    {
        private const byte Crowd = 3, Fauna = 4;

        private static FarClassTable Table(int defaultMax = 128, int total = 0) => new FarClassTable(defaultMax, new[]
        {
            new FarClass { Id = Crowd, Name = "crowd", MaxEntities = 32, UpdateRate = 2f, Radius = 1500f, Priority = 0, Upright = true },
            new FarClass { Id = Fauna, Name = "fauna", MaxEntities = 8, UpdateRate = 0.5f, Priority = -1, Upright = true },
        }, total);

        [Test]
        public void TheTableResolvesEachClassAndFallsBackToTheDefault()
        {
            var table = Table();
            Assert.That(table.Of(0).MaxEntities, Is.EqualTo(128), "the default class is budgeted by InterestFarMaxEntities");
            Assert.That(table.Of(Crowd).MaxEntities, Is.EqualTo(32));
            Assert.That(table.BudgetOf(77), Is.EqualTo((byte)0), "a class nobody defined counts against the default class");
            Assert.That(table.AnyBudget, Is.True);

            table.Resolve(Crowd, 0f, 1f, out float radius, out float rate);
            Assert.That(radius, Is.EqualTo(1500f), "the class's radius replaces the entity's");
            Assert.That(rate, Is.EqualTo(2f), "and its rate");
            table.Resolve(Fauna, 800f, 1f, out radius, out rate);
            Assert.That(radius, Is.EqualTo(800f), "a class without a radius keeps the entity's own");
            Assert.That(rate, Is.EqualTo(0.5f));
            table.Resolve(0, 1e9f, 5f, out radius, out rate);
            Assert.That((radius, rate), Is.EqualTo((1e9f, 5f)), "the default class changes nothing");

            Assert.That(new FarClassTable(0).AnyBudget, Is.False, "no budget anywhere is no far tier");
            Assert.That(new FarClassTable(0, new[] { new FarClass { Id = Crowd, MaxEntities = 4 } }).AnyBudget, Is.True);
        }

        [Test]
        public void AClassWithARadiusMakesAnEntityFarRelevant()
        {
            var go = new GameObject("walker");
            try
            {
                var identity = go.AddComponent<NetworkIdentity>();
                var table = Table();
                Assert.That(identity.IsFarRelevant(table), Is.False);
                identity.FarClass = Crowd;
                Assert.That(identity.IsFarRelevant(table), Is.True, "the crowd class has a radius of its own");
                identity.FarClass = Fauna;
                Assert.That(identity.IsFarRelevant(table), Is.False, "fauna has none, and the entity declares none");
                identity.FarRelevanceRadius = 600f;
                Assert.That(identity.IsFarRelevant(table), Is.True);

                identity.FarClass = Crowd;
                identity.FarRelevanceRadius = 0f;
                var entry = NebulaWorker.FarEntryOf(identity, table);
                Assert.That((entry.FarClass, entry.Radius, entry.UpdateRate), Is.EqualTo((Crowd, 1500f, 2f)), "the worker sends the class and its rate and radius");
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void ABusyCrowdNeverEvictsHulls()
        {
            var table = Table();
            var selection = new FarSelection();
            // 20 hulls 5 to 50 km out, 300 people 150 m to 1.5 km out: every person is nearer than every hull.
            for (int i = 0; i < 20; i++) selection.Add(table, 1000UL + (ulong)i, 0, Sq(5000.0 + i * 2000.0));
            for (int i = 0; i < 300; i++) selection.Add(table, 5000UL + (ulong)i, Crowd, Sq(150.0 + i * 4.5));
            var chosen = new HashSet<ulong>();
            selection.Choose(table, chosen);

            for (int i = 0; i < 20; i++) Assert.That(chosen, Does.Contain(1000UL + (ulong)i), "every hull keeps its place");
            int people = 0;
            foreach (ulong id in chosen) if (id >= 5000) people++;
            Assert.That(people, Is.EqualTo(32), "the crowd fills its own budget");
            for (int i = 0; i < 32; i++) Assert.That(chosen, Does.Contain(5000UL + (ulong)i), "with its nearest");
            Assert.That(selection.Count, Is.Zero, "the candidates are cleared for the next client");
        }

        [Test]
        public void ATotalCapServesClassesByPriority()
        {
            var table = new FarClassTable(64, new[]
            {
                new FarClass { Id = 0, MaxEntities = 64, Priority = 10 },
                new FarClass { Id = Crowd, MaxEntities = 32, Priority = 0 },
            }, maxTotal: 40);
            Assert.That(table.Ids, Is.EqualTo(new byte[] { 0, Crowd }));
            var selection = new FarSelection();
            for (int i = 0; i < 30; i++) selection.Add(table, 1000UL + (ulong)i, 0, Sq(10000.0 + i));
            for (int i = 0; i < 30; i++) selection.Add(table, 5000UL + (ulong)i, Crowd, Sq(200.0 + i));
            var chosen = new HashSet<ulong>();
            selection.Choose(table, chosen);
            Assert.That(chosen.Count, Is.EqualTo(40));
            for (int i = 0; i < 30; i++) Assert.That(chosen, Does.Contain(1000UL + (ulong)i), "the higher-priority class is served first");
            for (int i = 0; i < 10; i++) Assert.That(chosen, Does.Contain(5000UL + (ulong)i), "the crowd gets the rest, nearest first");
        }

        [Test]
        public void TheConfigRepairsFarClasses()
        {
            var config = ScriptableObject.CreateInstance<NebulaConfig>();
            try
            {
                config.InterestFarMaxTotal = -3;
                config.InterestFarClasses = new[]
                {
                    new FarClass { Id = Crowd, MaxEntities = -1, UpdateRate = 90f, Radius = -5f },
                    new FarClass { Id = Crowd, MaxEntities = 50 },
                };
                var issues = new List<ConfigIssue>();
                config.Validate(issues, 0f);
                Assert.That(issues.FindAll(i => i.Field == "InterestFarClasses").Count, Is.EqualTo(4), string.Join("\n", issues));
                Assert.That(issues.Exists(i => i.Field == "InterestFarMaxTotal"), Is.True);
                var settings = config.ToInterestSettings(0f);
                Assert.That(settings.FarClasses.Length, Is.EqualTo(1), "the duplicate is dropped");
                Assert.That(settings.FarClasses[0].MaxEntities, Is.Zero);
                Assert.That(settings.FarClasses[0].UpdateRate, Is.EqualTo(FarRelevance.MaxRate));
                Assert.That(settings.FarClasses[0].Radius, Is.Zero);
                Assert.That(settings.FarMaxTotal, Is.Zero);
            }
            finally { Object.DestroyImmediate(config); }
        }

        // ------------------------------------------------------------------------------------------- wire

        private static FarEntityEntry Walker() => new FarEntityEntry
        {
            NetId = 0x0001_0000_0000_0042, Epoch = 3, Kind = FarEntryKind.State, PrefabId = 12, Radius = 1500f, UpdateRate = 2f,
            X = 204_800.25, Y = -12.5, Z = 1_000_000.125, Rotation = Quaternion.Euler(0f, 30f, 0f), Velocity = new Vector3(1.5f, 0f, 0f),
            FarClass = Crowd, Slot = 7, Age = 0.42f, FromReplica = true,
        };

        private static FarEntityEntry RoundTrip(in FarEntityEntry entry, ushort version, out int bytes)
        {
            var w = new NetworkWriter(128);
            entry.Write(w, version);
            bytes = w.Length;
            return FarEntityEntry.Read(new NetworkReader(w.ToSegment()));
        }

        /// <summary>
        /// A class's rate is in hertz, not in ticks (<c>docs/tick-rate.md</c>): the worker feeds <see cref="FarPublisher"/> seconds,
        /// so a body keeps its updates a second on a mesh at 30 Hz, including rates that fall between two ticks.
        /// </summary>
        [TestCase(60)]
        [TestCase(30)]
        public void AFarRateIsInHertzAtAnyTickRate(int tickRate)
        {
            var settings = InterestSettings.Default;
            var grid = InterestGrid.Resolve(settings);
            ulong salt = RegionKeys.SaltOf(0UL);
            var receiver = new RegionSubscriptionReceiver();
            var msg = new InterestSubscribeMsg
            {
                Seq = 1, Grid = grid, Flags = InterestSubscribeFlags.Full | InterestSubscribeFlags.Commit,
                Add = new List<ulong>(), Remove = new List<ulong>(), FociRegions = new List<ulong> { grid.RegionOf(0, 0, 0) ^ salt }, Entities = new List<ulong>(),
                SetCount = 0, SetHash = RegionSubscription.Hash(new ulong[0]),
            };
            Assert.That(receiver.Apply(msg, out string rejection), Is.True, rejection);
            var publisher = new RegionPublisher();
            publisher.AddGateway(0, receiver);
            var far = new FarPublisher { EvalInterval = 0.25, ExitMargin = settings.ExitMargin };

            float[] rates = { 0.5f, 1f, 2f, 5f, 7f };
            var sent = new int[rates.Length];
            const int Seconds = 40;
            for (int tick = 1; tick <= Seconds * tickRate; tick++)
            {
                far.Begin(tick / (double)tickRate);
                for (int i = 0; i < rates.Length; i++)
                {
                    ulong id = (ulong)i + 1;
                    if (!far.IsDue(id)) { far.Keep(id); continue; }
                    far.Offer(new FarEntityEntry { NetId = id, Epoch = 1, Kind = FarEntryKind.State, Radius = 1000f, UpdateRate = rates[i], Rotation = Quaternion.identity }, salt, publisher, grid);
                }
                far.End();
                foreach (var o in far.Out) if (o.Entry.Kind == FarEntryKind.State) sent[(int)o.Entry.NetId - 1]++;
                far.Out.Clear();
            }
            for (int i = 0; i < rates.Length; i++)
                Assert.That(sent[i] / (double)Seconds, Is.EqualTo(rates[i]).Within(0.06), $"{rates[i]} Hz at a {tickRate} Hz tick");
        }

        [Test]
        public void AProtocol32EntryCarriesClassSlotAgeAndHandOver()
        {
            var read = RoundTrip(Walker(), HelloMsg.FarClassesVersion, out int bytes);
            Assert.That(read.FarClass, Is.EqualTo(Crowd));
            Assert.That(read.Slot, Is.EqualTo((ushort)7));
            Assert.That(read.Age, Is.EqualTo(0.42f).Within(0.001f));
            Assert.That(read.FromReplica, Is.True);
            Assert.That(read.X, Is.EqualTo(204_800.25));
            Assert.That(bytes, Is.EqualTo(FarEntityEntry.StateWireSize + 1 + 2 + 2), "class 1, slot 2, age 2 bytes");

            Assert.That(HelloMsg.FarClassesVersion, Is.EqualTo(HelloMsg.DroppedTicksVersion + 1), "far classes came after NEB-484's 31");
            Assert.That(FarEntityEntry.ClassesVersion, Is.EqualTo(HelloMsg.FarClassesVersion), "one number for one gate");
            var old = RoundTrip(Walker(), HelloMsg.DroppedTicksVersion, out int oldBytes);
            Assert.That(oldBytes, Is.EqualTo(FarEntityEntry.StateWireSize), "a client of 31 gets the protocol-26 entry, byte for byte");
            Assert.That((old.FarClass, old.Slot, old.Age, old.FromReplica), Is.EqualTo(((byte)0, (ushort)0, 0f, false)));
            Assert.That(old.Z, Is.EqualTo(1_000_000.125));
        }

        [Test]
        public void ACompactPoseIsAnOffsetInCentimetresAndFitsIn19Bytes()
        {
            var entries = new List<FarEntityEntry>
            {
                new FarEntityEntry { Kind = FarEntryKind.Pose, Slot = 7, Upright = true, X = 12.346, Y = -0.004, Z = -83_000.0, Rotation = Quaternion.Euler(0f, 123f, 0f), Velocity = new Vector3(1.5f, 0f, -2f) },
                new FarEntityEntry { Kind = FarEntryKind.Pose, Slot = FarPosesMsg.MaxSlot, Upright = false, X = 0.0, Y = 0.0, Z = 0.0, Rotation = Quaternion.Euler(10f, 20f, 30f) },
            };
            var w = new NetworkWriter(128);
            FarPosesMsg.Write(w, entries, 0, entries.Count);
            Assert.That(w.Length, Is.EqualTo(3 + 19 + 21), "an upright body 19 bytes, anything else 21");
            var r = new NetworkReader(w.ToSegment());
            Assert.That((MsgId)r.ReadByte(), Is.EqualTo(MsgId.FarPoses));
            var poses = new List<FarPose>();
            FarPosesMsg.Read(r, poses);
            Assert.That(poses.Count, Is.EqualTo(2));
            Assert.That(poses[0].Slot, Is.EqualTo((ushort)7));
            Assert.That(poses[0].Upright, Is.True);
            Assert.That(poses[0].DX, Is.EqualTo(12.35).Within(1e-9), "1 cm steps");
            Assert.That(poses[0].DY, Is.EqualTo(0.0).Within(1e-9));
            Assert.That(poses[0].DZ, Is.EqualTo(-83_000.0).Within(1e-9), "a negative offset tens of km out");
            Assert.That(Vector3.Angle(poses[0].Forward, Quaternion.Euler(0f, 123f, 0f) * Vector3.forward), Is.LessThan(1.5f), "the facing to about a degree");
            Assert.That(poses[0].Velocity.z, Is.EqualTo(-2f));
            Assert.That(poses[1].Slot, Is.EqualTo(FarPosesMsg.MaxSlot));
            Assert.That(Quaternion.Angle(poses[1].Rotation, entries[1].Rotation), Is.LessThan(0.3f));

            Assert.That(FarPosesMsg.Fits(FarPosesMsg.MaxOffset, 0, -FarPosesMsg.MaxOffset), Is.True);
            Assert.That(FarPosesMsg.Fits(FarPosesMsg.MaxOffset + 0.01, 0, 0), Is.False, "beyond 83.9 km the gateway sends a full entry");
        }

        [Test]
        public void TheFacingCodeIsGoodToADegreeEverywhere()
        {
            float worst = 0f;
            for (int yaw = 0; yaw < 360; yaw += 7)
                for (int pitch = -89; pitch <= 89; pitch += 11)
                {
                    var d = Quaternion.Euler(pitch, yaw, 0f) * Vector3.forward;
                    worst = Mathf.Max(worst, Vector3.Angle(d, FarPosesMsg.DecodeFacing(FarPosesMsg.EncodeFacing(d))));
                }
            Assert.That(worst, Is.LessThan(1.5f));
            Assert.That(Vector3.Angle(FarPosesMsg.DecodeFacing(FarPosesMsg.EncodeFacing(Vector3.zero)), Vector3.forward), Is.LessThan(0.01f), "no direction reads as forward");

            // An upright body on a tilted surface (a sphere's side) keeps its up axis.
            var tilted = Quaternion.Euler(0f, 0f, 40f);
            var up = tilted * Vector3.up;
            var turned = FarPosesMsg.UprightRotation(tilted * Quaternion.Euler(0f, 90f, 0f) * Vector3.forward, up, tilted);
            Assert.That(Vector3.Angle(turned * Vector3.up, up), Is.LessThan(0.01f));
            Assert.That(Quaternion.Angle(turned, tilted * Quaternion.Euler(0f, 90f, 0f)), Is.LessThan(0.01f));
        }

        private static double Sq(double d) => d * d;
    }
}
