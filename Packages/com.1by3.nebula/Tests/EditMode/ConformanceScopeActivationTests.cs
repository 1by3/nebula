using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
#if NEBULA_SERVICE
using Nebula.ServicePrimitives;
#else
using UnityEngine;
#endif

namespace Nebula.Tests
{
    /// <summary>
    /// Conformance scenario 2 of <c>docs/conformance-suite.md</c>: <b>scope activation is idempotent across two
    /// concurrent requesters, and a private key yields a distinct scope</b>. Design of record:
    /// <c>docs/scope-activation.md</c>.
    /// <para>
    /// Tier A over the production types, no Unity and no sockets: the real <see cref="LocalControlPlane"/> is the
    /// thing that decides activation, and the real <see cref="FileControlPlaneStorage"/> is the durable uniqueness
    /// constraint (<see cref="IScopeStore"/>). Tier B would add nothing — no worker code runs during an activation —
    /// and the fake-mesh leg that proves a gateway routes a client by key lives beside the gateway it needs, in
    /// <c>Services~/Nebula.Services.Tests/ConformanceScopeRoutingTests.cs</c>.
    /// </para>
    /// <para>
    /// "Concurrent requesters" is two independent callers whose requests the control plane sees in one run without
    /// either having observed the other's result; that is what a matchmaker and a travel menu look like from here.
    /// The genuinely parallel case — threads racing the storage constraint on one key — is
    /// <see cref="RacingClaimsOnOneKeyAllSeeTheSameDefinition"/>.
    /// </para>
    /// </summary>
    [Category("Conformance")]
    public class ConformanceScopeActivationTests
    {
        private const string Template = "interior";
        private const string SharedKey = Template + "/raid-7";
        private const string PrivateKey = Template + "/raid-7#alice";

        private readonly List<IDisposable> _disposables = new List<IDisposable>();
        private string _tempDir;

        [TearDown]
        public void TearDown()
        {
            foreach (var d in _disposables) { try { d.Dispose(); } catch { } }
            _disposables.Clear();
            if (_tempDir != null && Directory.Exists(_tempDir)) { try { Directory.Delete(_tempDir, true); } catch { } }
            _tempDir = null;
        }

        private string TempDir => _tempDir ?? (_tempDir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "nebula-scope-" + Guid.NewGuid().ToString("N"))).FullName);

        /// <summary>A two-part scope definition. Every requester builds its own object; they are equal by value.</summary>
        private static ScopeDefinition Room(float x = 0, string content = "Instances/Room")
        {
            return new ScopeDefinition
            {
                Kind = ScopeKind.Parts,
                ObservePublic = true,
                ObservationCenter = new Vector3(x, 0, 0),
                ObservationSize = new Vector3(60, 30, 60),
                Payload = "",
                Parts =
                {
                    new ScopePart { PartId = "interior", Center = new Vector3(x, 0, 0), Size = new Vector3(20, 10, 20), ContentResource = content },
                    new ScopePart { PartId = "cellar", Center = new Vector3(x, -10, 0), Size = new Vector3(20, 6, 20), ContentResource = "" },
                },
            };
        }

        private LocalControlPlane Plane(IScopeStore store = null)
        {
            var plane = new LocalControlPlane { ScopeStore = store };
            _disposables.Add(plane);
            plane.Connect();
            return plane;
        }

        private FileControlPlaneStorage Storage()
        {
            var storage = new FileControlPlaneStorage(Path.Combine(TempDir, "control-plane.json"));
            _disposables.Add(storage);
            return storage;
        }

        private static ScopeActivationRequest Ask(string key, ScopeDefinition definition, string requester) =>
            new ScopeActivationRequest { ScopeKey = key, Definition = definition, Requester = requester };

        // ------------------------------------------------------------------------------- the exit criterion

        [Test]
        public void ActivationIsIdempotentAcrossTwoConcurrentRequesters()
        {
            var plane = Plane(Storage());

            plane.ActivateScope(Ask(SharedKey, Room(), "matchmaker"));
            var first = plane.FindScope(SharedKey);
            Assert.That(first, Is.Not.Null, "the first activation creates the row");
            Assert.That(first.ContainerIds, Is.EquivalentTo(new[] { ScopeKeys.ContainerId(SharedKey, "interior"), ScopeKeys.ContainerId(SharedKey, "cellar") }));
            Assert.That(plane.Leases.Count, Is.EqualTo(2), "one lease row per part");
            var epochs = new List<ulong>();
            foreach (var id in first.ContainerIds) epochs.Add(plane.FindLease(id).Epoch);
            var createdAt = first.CreatedAt;

            // The second requester never saw the first one's answer: same key, its own equal definition object.
            plane.ActivateScope(Ask(SharedKey, Room(), "travel-menu"));

            var second = plane.FindScope(SharedKey);
            Assert.That(plane.Scopes.Count, Is.EqualTo(1), "the same key is one scope, however many callers ask for it");
            Assert.That(second, Is.SameAs(first), "the second caller gets the row the first one created");
            Assert.That(second.CreatedAt, Is.EqualTo(createdAt), "the row was not recreated");
            Assert.That(second.InstanceId, Is.EqualTo(ScopeKeys.Hash(SharedKey)));
            Assert.That(plane.Leases.Count, Is.EqualTo(2), "and no second set of lease rows");
            for (int i = 0; i < second.ContainerIds.Count; i++)
            {
                var lease = plane.FindLease(second.ContainerIds[i]);
                Assert.That(lease.Epoch, Is.EqualTo(epochs[i]), "an idempotent activation does not disturb the lease");
                Assert.That(lease.Instance.ScopeKey, Is.EqualTo(SharedKey), "every container of the scope carries the key (location contract D6)");
                Assert.That(lease.Instance.InstanceId, Is.EqualTo(ScopeKeys.Hash(SharedKey)));
            }
        }

        [Test]
        public void APrivateKeyYieldsADistinctScope()
        {
            var plane = Plane(Storage());
            plane.ActivateScope(Ask(SharedKey, Room(), "matchmaker"));
            // A private copy is the same call with a key the game made unique. There is no second API.
            plane.ActivateScope(Ask(PrivateKey, Room(), "matchmaker"));

            var shared = plane.FindScope(SharedKey);
            var mine = plane.FindScope(PrivateKey);
            Assert.That(plane.Scopes.Count, Is.EqualTo(2));
            Assert.That(mine.InstanceId, Is.Not.EqualTo(shared.InstanceId), "the two scopes are isolated from each other");
            Assert.That(mine.ContainerIds, Is.Not.EquivalentTo(shared.ContainerIds));
            foreach (var id in mine.ContainerIds)
                Assert.That(shared.ContainerIds, Does.Not.Contain(id), "no container is in both scopes");
            Assert.That(plane.Leases.Count, Is.EqualTo(4), "two lease rows each, not shared");
            Assert.That(plane.FindLease(mine.ContainerIds[0]).Instance.ScopeKey, Is.EqualTo(PrivateKey));
        }

        // ------------------------------------------------------------------------------- the constraint itself

        [Test]
        public void RacingClaimsOnOneKeyAllSeeTheSameDefinition()
        {
            IScopeStore store = Storage();
            const int racers = 16;
            var start = new ManualResetEventSlim(false);
            var results = new string[racers];
            var tasks = new Task[racers];
            for (int i = 0; i < racers; i++)
            {
                int index = i;
                tasks[i] = Task.Run(() =>
                {
                    start.Wait();
                    // Every racer offers a definition of its own, so "they all agree" cannot be an accident.
                    results[index] = store.ClaimScope(SharedKey, ScopeJson.WriteDefinition(Room(index)));
                });
            }
            start.Set();
            Assert.That(Task.WaitAll(tasks, TimeSpan.FromSeconds(30)), Is.True, "the claims completed");

            for (int i = 1; i < racers; i++)
                Assert.That(results[i], Is.EqualTo(results[0]), "every racer is told the same definition: the constraint is on the key, not a lock");
            Assert.That(store.ClaimScope(SharedKey, ScopeJson.WriteDefinition(Room(999))), Is.EqualTo(results[0]),
                "and a later caller is told the same one");
            store.ReleaseScope(SharedKey);
            Assert.That(store.ClaimScope(SharedKey, ScopeJson.WriteDefinition(Room(5))), Is.EqualTo(ScopeJson.WriteDefinition(Room(5))),
                "releasing the claim frees the key again");
        }

        [Test]
        public void ASecondOrchestratorRunAdoptsTheStoredDefinition()
        {
            var storage = Storage();
            var first = new ControlPlaneHost(storage, null, restore: false);
            _disposables.Add(first);
            first.ActivateScope(Ask(SharedKey, Room(content: "Instances/Room"), "matchmaker"));
            string stored = ScopeJson.WriteDefinition(first.FindScope(SharedKey).Definition);

            // A fresh host over the same storage, with no document to import: the claim is what survives.
            var second = new ControlPlaneHost(storage, null, restore: false);
            _disposables.Add(second);
            second.ActivateScope(Ask(SharedKey, Room(content: "Instances/OtherRoom"), "travel-menu"));

            var adopted = second.FindScope(SharedKey);
            Assert.That(adopted, Is.Not.Null);
            Assert.That(ScopeJson.WriteDefinition(adopted.Definition), Is.EqualTo(stored),
                "the stored claim wins; the second run does not replace the scope's content");
            Assert.That(adopted.ContainerIds, Is.EquivalentTo(first.FindScope(SharedKey).ContainerIds),
                "so both runs name the same containers");
        }

        [Test]
        public void TheSameKeyWithDifferentContentIsRefusedAndTheStoredScopeStands()
        {
            var plane = Plane(Storage());
            plane.ActivateScope(Ask(SharedKey, Room(content: "Instances/Room"), "matchmaker"));
            long before = plane.Version;

            plane.ActivateScope(Ask(SharedKey, Room(content: "Instances/Arena"), "impostor"));

            Assert.That(plane.Scopes.Count, Is.EqualTo(1));
            Assert.That(plane.FindScope(SharedKey).Definition.Parts[0].ContentResource, Is.EqualTo("Instances/Room"),
                "two different worlds never share one scope key");
            Assert.That(plane.Version, Is.EqualTo(before), "a refused activation changes nothing, so the caller's scope stays authoritative");
        }

        [Test]
        public void AMalformedDefinitionIsRefusedWithoutTouchingAnything()
        {
            var plane = Plane(Storage());
            plane.ActivateScope(Ask("", Room(), "matchmaker"));
            plane.ActivateScope(Ask(SharedKey, new ScopeDefinition(), "matchmaker"));
            plane.ActivateScope(Ask(SharedKey, new ScopeDefinition { Parts = { new ScopePart { PartId = "a", Size = new Vector3(0, 1, 1) } } }, "matchmaker"));
            plane.ActivateScope(Ask(SharedKey, new ScopeDefinition { Kind = "grid" }, "matchmaker"));
            Assert.That(plane.Scopes, Is.Empty);
            Assert.That(plane.Leases, Is.Empty);
        }

        // ------------------------------------------------------------------------------- readiness and removal

        [Test]
        public void AScopeIsReadyOnlyWhenEveryContainerHasALiveOwner()
        {
            var plane = Plane(Storage());
            plane.RegisterWorker("w1", 1, "127.0.0.1", 7100);
            plane.HeartbeatWorker("w1", WorkerStatus.Ready, new WorkerStats());
            plane.ActivateScope(Ask(SharedKey, Room(), "matchmaker"));
            var scope = plane.FindScope(SharedKey);

            Assert.That(plane.IsScopeReady(SharedKey), Is.False, "the rows exist, but nobody simulates them yet");
            plane.AssignContainer(scope.ContainerIds[0], "w1");
            Assert.That(plane.IsScopeReady(SharedKey), Is.False, "one part short is not ready");
            plane.AssignContainer(scope.ContainerIds[1], "w1");
            Assert.That(plane.IsScopeReady(SharedKey), Is.True);

            plane.UnregisterWorker("w1");
            Assert.That(plane.IsScopeReady(SharedKey), Is.False, "losing the owner takes readiness away again");
            Assert.That(plane.FindScope(SharedKey), Is.Not.Null, "but the scope itself is still there to be re-assigned");
        }

        [Test]
        public void AWorkerThatAsksForTheScopeOwnsItFromTheFirstChange()
        {
            var plane = Plane(Storage());
            plane.RegisterWorker("w1", 1, "127.0.0.1", 7100);
            plane.HeartbeatWorker("w1", WorkerStatus.Ready, new WorkerStats());
            plane.ActivateScope(new ScopeActivationRequest { ScopeKey = SharedKey, Definition = Room(), PreferredWorkerId = "w1" });
            Assert.That(plane.IsScopeReady(SharedKey), Is.True, "PrepareInstance's guarantee: no window in which the asking worker does not own the box");
            foreach (var id in plane.FindScope(SharedKey).ContainerIds)
                Assert.That(plane.FindLease(id).State, Is.EqualTo(LeaseState.Active));
        }

        [Test]
        public void RemovingAScopeTakesItsLeaseRowsAndItsClaimWithIt()
        {
            IScopeStore store = Storage();
            var plane = Plane(store);
            plane.ActivateScope(Ask(SharedKey, Room(content: "Instances/Room"), "matchmaker"));

            plane.RemoveScope(SharedKey);

            Assert.That(plane.FindScope(SharedKey), Is.Null);
            Assert.That(plane.Leases, Is.Empty);
            Assert.That(store.ClaimScope(SharedKey, ScopeJson.WriteDefinition(Room(content: "Instances/Arena"))),
                Is.EqualTo(ScopeJson.WriteDefinition(Room(content: "Instances/Arena"))), "the key is free again");
        }

        // ------------------------------------------------------------------------------- the document and the wire

        [Test]
        public void ScopeRowsSurviveTheControlPlaneDocumentUnchanged()
        {
            var plane = Plane(Storage());
            plane.ActivateScope(Ask(SharedKey, Room(1.5f), "matchmaker"));
            string document = plane.ToJson();

            var mirror = new LocalControlPlane();
            _disposables.Add(mirror);
            mirror.Import(ControlPlaneJson.Parse(document));

            var before = plane.FindScope(SharedKey);
            var after = mirror.FindScope(SharedKey);
            Assert.That(after, Is.Not.Null, "a subscriber sees the scope rows, which is how a caller polls for its answer");
            Assert.That(after.ScopeKey, Is.EqualTo(before.ScopeKey));
            Assert.That(after.InstanceId, Is.EqualTo(before.InstanceId));
            Assert.That(after.State, Is.EqualTo(ScopeState.Active));
            Assert.That(after.ContainerIds, Is.EqualTo(before.ContainerIds));
            Assert.That(ScopeJson.WriteDefinition(after.Definition), Is.EqualTo(ScopeJson.WriteDefinition(before.Definition)),
                "the definition round-trips byte for byte, so a mirror can compare it with its own");
        }

        [Test]
        public void ADocumentFromBeforeScopeActivationReadsAsNoScopes()
        {
            var plane = Plane();
            string document = plane.ToJson();
            Assert.That(document, Does.Not.Contain("\"scopes\""), "a mesh that activates nothing writes the document it always did");
            Assert.That(ControlPlaneJson.Parse(document).Scopes, Is.Empty);
        }

        [Test]
        public void TheActivationOpTravelsOverTheControlPlaneWriteBatch()
        {
            var plane = Plane(Storage());
            // What a RemoteControlPlane puts on the wire for ActivateScope, applied as the orchestrator applies it.
            string op = new ControlPlaneJson.OpWriter().Op(ControlPlaneJson.ActivateScope)
                .Arg("scopeKey", SharedKey)
                .Arg("definition", ScopeJson.WriteDefinition(Room()))
                .Arg("workerId", "")
                .Arg("requester", "matchmaker").End();
            Assert.That(ControlPlaneJson.ApplyBatch(ControlPlaneJson.WriteBatch(new[] { op }), plane), Is.Null);

            var scope = plane.FindScope(SharedKey);
            Assert.That(scope, Is.Not.Null);
            Assert.That(ScopeJson.WriteDefinition(scope.Definition), Is.EqualTo(ScopeJson.WriteDefinition(Room())));
            Assert.That(scope.ContainerIds, Is.EquivalentTo(new[] { ScopeKeys.ContainerId(SharedKey, "interior"), ScopeKeys.ContainerId(SharedKey, "cellar") }));
        }

        [Test]
        public void AJoinCarriesTheScopeKeyAndAHelloWithoutOneIsThePublicWorld()
        {
            var writer = new NetworkWriter();
            new HelloMsg { Role = PeerRole.Client, Id = "alice", Token = "t", Session = "s", ScopeKey = SharedKey }.Write(writer);
            var reader = new NetworkReader(writer.ToArray());
            reader.ReadByte();
            Assert.That(HelloMsg.Read(reader).ScopeKey, Is.EqualTo(SharedKey));

            // A Hello that ends where v18's Hello used to end: the field is absent, and that is the public world.
            var old = new NetworkWriter();
            old.WriteByte((byte)MsgId.Hello);
            old.WriteUShort(HelloMsg.ProtocolVersion);
            old.WriteByte((byte)PeerRole.Client);
            old.WriteString("alice");
            old.WriteUInt(0);
            old.WriteByte(0);
            old.WriteString("t");
            old.WriteString("s");
            old.WriteUInt(0);
            var oldReader = new NetworkReader(old.ToArray());
            oldReader.ReadByte();
            var parsed = HelloMsg.Read(oldReader);
            Assert.That(parsed.ScopeKey, Is.EqualTo(EntityLocation.PublicScope));
            Assert.That(parsed.Id, Is.EqualTo("alice"));
            Assert.That(parsed.Session, Is.EqualTo("s"));
        }

        // ------------------------------------------------------------------------------- the derivation itself

        [Test]
        public void TheContainerIdsOfAKeyArePinned()
        {
            // The ids are what makes "same key, same scope" true across processes and restarts; they are a hash of
            // the key, so a change here silently strands every stored record and lease row.
            Assert.That(ScopeKeys.Hash("interior/alice"), Is.EqualTo(ScopeKeys.Hash("interior/alice")));
            Assert.That(ScopeKeys.ContainerId("interior/alice", "room"), Is.EqualTo("rt_" + ScopeKeys.Hash("interior/alice/room")));
            Assert.That(ScopeKeys.ContainerId("interior/alice", "room"), Is.Not.EqualTo(ScopeKeys.ContainerId("interior/bob", "room")));
            Assert.That(new EntityLocation(SharedKey, ScopeKeys.ContainerId(SharedKey, "interior"), default, Quaternion.identity).ContainerKind,
                Is.EqualTo(LocationContainerKind.Runtime), "an activated scope's containers are runtime containers to the location contract");
        }
    }
}
