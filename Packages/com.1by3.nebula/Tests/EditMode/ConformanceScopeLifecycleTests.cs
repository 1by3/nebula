using System;
using System.Collections.Generic;
using NUnit.Framework;
#if NEBULA_SERVICE
using Nebula.ServicePrimitives;
#else
using UnityEngine;
#endif

namespace Nebula.Tests
{
    /// <summary>
    /// Conformance scenario 3 of <c>docs/conformance-suite.md</c>: <b>a scope retires when idle, restores
    /// identically with its persisted entities, and admission is refused until the restore completes</b>. Design of
    /// record: <c>docs/scope-lifecycle.md</c>.
    /// <para>
    /// This file is the tier A half: the real <see cref="LocalControlPlane"/> holding the rows and the real
    /// <see cref="ScopeLifecycle"/> making every decision, with <see cref="LocalControlPlane.Clock"/> as the clock
    /// seam so a scope can be aged by minutes without waiting. What the test stands in for is only the
    /// orchestrator's loop (<c>NebulaOrchestrator.SweepScopes</c>) and the workers' acknowledgements — the two
    /// things a real mesh would provide — so the state machine, the ordering and the admission rule are the
    /// production ones. The store half (a real <see cref="NebulaPersistence"/> checkpointing a scope's part and the
    /// records coming back identically) is tier B/C in
    /// <c>Tests/EditMode/ConformanceScopeCheckpointTests.cs</c>; the gateway's refusal on a real socket is
    /// <c>Services~/Nebula.Services.Tests/ConformanceScopeAdmissionTests.cs</c>. A real retire on a real mesh is
    /// tier D and is not built.
    /// </para>
    /// </summary>
    [Category("Conformance")]
    public class ConformanceScopeLifecycleTests
    {
        private const string Key = "interior/raid-7";
        private const string Worker = "w1";
        private static readonly string[] Parts = { "interior", "cellar" };

        private readonly List<IDisposable> _disposables = new List<IDisposable>();
        private DateTime _clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        [SetUp]
        public void SetUp()
        {
            _clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        }

        [TearDown]
        public void TearDown()
        {
            ScopeLifecycle.ShouldRetire = ScopeLifecycle.RetireWhenIdle;
            foreach (var d in _disposables) { try { d.Dispose(); } catch { } }
            _disposables.Clear();
        }

        private LocalControlPlane Plane()
        {
            var plane = new LocalControlPlane();
            plane.Clock = () => _clock;
            _disposables.Add(plane);
            plane.Connect();
            // A live worker to own the parts, so readiness means what it means in a mesh.
            plane.RegisterWorker(Worker, 0, "127.0.0.1", 7000);
            plane.HeartbeatWorker(Worker, WorkerStatus.Ready, default);
            return plane;
        }

        private static ScopeDefinition Room()
        {
            return new ScopeDefinition
            {
                Kind = ScopeKind.Parts,
                Parts =
                {
                    new ScopePart { PartId = Parts[0], Center = new Vector3(500, 0, 0), Size = new Vector3(20, 10, 20), ContentResource = "Instances/Room" },
                    new ScopePart { PartId = Parts[1], Center = new Vector3(500, -10, 0), Size = new Vector3(20, 6, 20) },
                },
            };
        }

        private void Activate(LocalControlPlane plane) =>
            plane.ActivateScope(new ScopeActivationRequest { ScopeKey = Key, Definition = Room(), PreferredWorkerId = Worker, Requester = "matchmaker" });

        private static ScopeRetireContext Context(IControlPlane plane, ScopeInfo scope, float after = 300f, int entities = 0, int players = 0, bool withEntities = false) =>
            new ScopeRetireContext
            {
                Scope = scope,
                IdleSeconds = ScopeLifecycle.IdleSeconds(plane, scope),
                Entities = entities,
                Players = players,
                RetireAfterSeconds = after,
                RetireWithEntities = withEntities,
            };

        // ------------------------------------------------------------------------------- the exit criterion

        [Test]
        public void AnIdleScopeRetiresInOrderAndComesBackUnderTheSameKey()
        {
            var plane = Plane();
            Activate(plane);
            var scope = plane.FindScope(Key);
            var containers = new List<string>(scope.ContainerIds);
            Assert.That(scope.State, Is.EqualTo(ScopeState.Active));
            Assert.That(plane.Leases.Count, Is.EqualTo(2), "one lease row per part");
            Assert.That(plane.IsScopeReady(Key), Is.True);
            Assert.That(ScopeLifecycle.Admits(plane, Key, out _), Is.True, "an active scope admits clients");

            // Nothing wants it for long enough. Idle age is the age of the youngest lease row, so one busy part
            // would hold the whole scope open.
            _clock = _clock.AddSeconds(301);
            Assert.That(ScopeLifecycle.IdleSeconds(plane, scope), Is.EqualTo(301).Within(1));
            Assert.That(ScopeLifecycle.RetireWhenIdle(Context(plane, scope)), Is.True);

            // Step 1: Retiring. Admission stops here, before anything has been saved.
            plane.SetScopeState(Key, ScopeState.Retiring);
            Assert.That(ScopeLifecycle.Admits(plane, Key, out var retiringReason), Is.False);
            Assert.That(retiringReason, Is.EqualTo(JoinHoldReason.ScopeRetiring));
            Assert.That(plane.Leases.Count, Is.EqualTo(2), "the lease rows are still there: the checkpoint needs its owner");

            // Step 2: every part checkpoints and acknowledges. One part is not enough.
            plane.AckScopePart(Key, containers[0], ScopePhase.Checkpointed, 3, Worker);
            Assert.That(ScopeLifecycle.NextState(scope, 1, out _), Is.Null, "all parts retire together");
            plane.AckScopePart(Key, containers[1], ScopePhase.Checkpointed, 1, Worker);
            Assert.That(ScopeLifecycle.NextState(scope, 1, out bool timedOut), Is.EqualTo(ScopeState.Retired));
            Assert.That(timedOut, Is.False);
            Assert.That(scope.AckedCount(ScopePhase.Checkpointed), Is.EqualTo(4));

            // Step 3: the leases go, the row stays.
            foreach (var id in containers) plane.RemoveContainer(id);
            plane.SetScopeState(Key, ScopeState.Retired);
            Assert.That(plane.Leases, Is.Empty, "a retired scope holds no lease");
            Assert.That(plane.FindScope(Key), Is.SameAs(scope), "the row is kept so the key keeps its identity");
            Assert.That(scope.Acks, Is.Empty, "a state change drops the acks of the step that ended");
            Assert.That(ScopeLifecycle.Admits(plane, Key, out var retiredReason), Is.False);
            Assert.That(retiredReason, Is.EqualTo(JoinHoldReason.ScopeNotReady));

            // Re-activation: the same containers, and no admission until every part reports its restore done.
            Activate(plane);
            Assert.That(scope.State, Is.EqualTo(ScopeState.Restoring));
            Assert.That(scope.ContainerIds, Is.EqualTo(containers), "the container ids are derived from the key, so they come back the same");
            Assert.That(plane.Leases.Count, Is.EqualTo(2));
            Assert.That(ScopeLifecycle.Admits(plane, Key, out var restoringReason), Is.False, "admission is refused until the restore completes");
            Assert.That(restoringReason, Is.EqualTo(JoinHoldReason.ScopeRestoring));

            plane.AckScopePart(Key, containers[0], ScopePhase.Restored, 3, Worker);
            Assert.That(ScopeLifecycle.NextState(scope, 1, out _), Is.Null, "all parts restore before admission");
            Assert.That(ScopeLifecycle.Admits(plane, Key, out _), Is.False);
            plane.AckScopePart(Key, containers[1], ScopePhase.Restored, 1, Worker);
            Assert.That(ScopeLifecycle.NextState(scope, 1, out _), Is.EqualTo(ScopeState.Active));

            plane.SetScopeState(Key, ScopeState.Active);
            Assert.That(ScopeLifecycle.Admits(plane, Key, out _), Is.True, "and then it admits clients again");
            Assert.That(plane.IsScopeReady(Key), Is.False, "readiness is still about the leases having live owners");
        }

        [Test]
        public void ABusyPartKeepsTheWholeScopeHot()
        {
            var plane = Plane();
            Activate(plane);
            var scope = plane.FindScope(Key);

            _clock = _clock.AddSeconds(301);
            // One part is re-stamped, exactly as a worker does while something in it would be lost by a retire.
            plane.TouchContainer(scope.ContainerIds[1]);

            Assert.That(ScopeLifecycle.IdleSeconds(plane, scope), Is.EqualTo(0).Within(1), "idle age is the minimum over the parts");
            Assert.That(ScopeLifecycle.RetireWhenIdle(Context(plane, scope)), Is.False);
        }

        [Test]
        public void OccupancyAndTheKnobBothVetoARetire()
        {
            var plane = Plane();
            Activate(plane);
            var scope = plane.FindScope(Key);
            _clock = _clock.AddSeconds(301);

            Assert.That(ScopeLifecycle.RetireWhenIdle(Context(plane, scope, players: 1)), Is.False, "a client is in it");
            Assert.That(ScopeLifecycle.RetireWhenIdle(Context(plane, scope, entities: 4)), Is.False, "something is still simulating in it");
            Assert.That(ScopeLifecycle.RetireWhenIdle(Context(plane, scope, after: 0f)), Is.False, "ScopeIdleRetireSeconds = 0 turns retiring off");
            Assert.That(ScopeLifecycle.RetireWhenIdle(Context(plane, scope, after: 600f)), Is.False, "and the threshold is honoured");
            Assert.That(ScopeLifecycle.RetireWhenIdle(Context(plane, scope)), Is.True);
        }

        /// <summary>
        /// <c>ScopeRetireWithEntities</c>: a scope that holds saved objects and nobody playing retires once it is idle
        /// (the retire checkpoints them), while a player, the idle clock and the knob still keep it.
        /// </summary>
        [Test]
        public void RetireWithEntitiesRetiresAnIdleScopeHoldingObjectsButNeverOneWithAPlayer()
        {
            var plane = Plane();
            Activate(plane);
            var scope = plane.FindScope(Key);
            _clock = _clock.AddSeconds(301);

            Assert.That(ScopeLifecycle.RetireWhenIdle(Context(plane, scope, entities: 4)), Is.False, "off by default: entities keep the scope");
            Assert.That(ScopeLifecycle.RetireWhenIdle(Context(plane, scope, entities: 4, withEntities: true)), Is.True, "saved objects are checkpointed and come back");
            Assert.That(ScopeLifecycle.RetireWhenIdle(Context(plane, scope, entities: 5, players: 1, withEntities: true)), Is.False, "a client is in it");
            Assert.That(ScopeLifecycle.RetireWhenIdle(Context(plane, scope, entities: 4, after: 0f, withEntities: true)), Is.False, "0 still turns retiring off");

            plane.TouchContainer(scope.ContainerIds[0]);
            Assert.That(ScopeLifecycle.RetireWhenIdle(Context(plane, scope, entities: 4, withEntities: true)), Is.False,
                "a part a worker keeps hot (something in it would be lost) still vetoes the retire");
        }

        [Test]
        public void AGamePolicyCanKeepAnIdleScopeHot()
        {
            var plane = Plane();
            Activate(plane);
            var scope = plane.FindScope(Key);
            _clock = _clock.AddSeconds(301);

            ScopeLifecycle.ShouldRetire = (in ScopeRetireContext c) => !c.Scope.ScopeKey.StartsWith("interior/", StringComparison.Ordinal) && ScopeLifecycle.RetireWhenIdle(c);
            Assert.That(ScopeLifecycle.ShouldRetire(Context(plane, scope)), Is.False, "the hook is consulted, not the clock alone");
        }

        [Test]
        public void AStepThatIsNeverAcknowledgedFinishesOnTheDeadline()
        {
            var plane = Plane();
            Activate(plane);
            var scope = plane.FindScope(Key);
            plane.SetScopeState(Key, ScopeState.Retiring);

            Assert.That(ScopeLifecycle.NextState(scope, ScopeLifecycle.StepTimeoutSeconds - 1, out _), Is.Null);
            Assert.That(ScopeLifecycle.NextState(scope, ScopeLifecycle.StepTimeoutSeconds, out bool timedOut), Is.EqualTo(ScopeState.Retired));
            Assert.That(timedOut, Is.True, "a worker that died must not leave a scope nobody can ever enter");

            plane.SetScopeState(Key, ScopeState.Restoring);
            Assert.That(ScopeLifecycle.NextState(scope, ScopeLifecycle.StepTimeoutSeconds, out timedOut), Is.EqualTo(ScopeState.Active));
            Assert.That(timedOut, Is.True);
        }

        [Test]
        public void AnAckForAStepThatIsNotRunningIsIgnored()
        {
            var plane = Plane();
            Activate(plane);
            var scope = plane.FindScope(Key);

            plane.AckScopePart(Key, scope.ContainerIds[0], ScopePhase.Checkpointed, 2, Worker);
            Assert.That(scope.Acks, Is.Empty, "the scope is active: nothing is being checkpointed");

            plane.SetScopeState(Key, ScopeState.Retiring);
            plane.AckScopePart(Key, scope.ContainerIds[0], ScopePhase.Restored, 2, Worker);
            Assert.That(scope.Acks, Is.Empty, "an ack from the previous step does not count towards this one");
            plane.AckScopePart(Key, "rt_not-ours", ScopePhase.Checkpointed, 2, Worker);
            Assert.That(scope.Acks, Is.Empty, "nor does one for a container the scope does not own");
        }

        [Test]
        public void AnActivationDuringARetireIsNotApplied()
        {
            var plane = Plane();
            Activate(plane);
            plane.SetScopeState(Key, ScopeState.Retiring);

            Activate(plane);

            Assert.That(plane.FindScope(Key).State, Is.EqualTo(ScopeState.Retiring),
                "bringing the scope back mid-retire would race the checkpoint that is writing its records");
        }

        [Test]
        public void TheLifecycleFieldsSurviveTheDocumentRoundTrip()
        {
            var plane = Plane();
            Activate(plane);
            plane.SetScopeState(Key, ScopeState.Retiring);
            var scope = plane.FindScope(Key);
            plane.AckScopePart(Key, scope.ContainerIds[0], ScopePhase.Checkpointed, 7, Worker);

            var snapshot = ControlPlaneJson.Parse(plane.ToJson());
            var mirrored = snapshot.Scopes[0];

            Assert.That(mirrored.State, Is.EqualTo(ScopeState.Retiring));
            Assert.That(mirrored.StateSince, Is.EqualTo(scope.StateSince).Within(TimeSpan.FromMilliseconds(1)));
            Assert.That(mirrored.Acks.Count, Is.EqualTo(1));
            Assert.That(mirrored.FindAck(scope.ContainerIds[0], ScopePhase.Checkpointed).Count, Is.EqualTo(7));
            Assert.That(mirrored.AllAcked(ScopePhase.Checkpointed), Is.False, "one of two parts");

            // A document from before this item has no state and no acks: it reads as an ordinary active scope.
            var old = ScopeJson.ReadScope(new Dictionary<string, object> { { "scopeKey", Key } });
            Assert.That(old.State, Is.EqualTo(ScopeState.Active));
            Assert.That(old.Acks, Is.Empty);
            Assert.That(ScopeState.AdmitsClients(old.State), Is.True);
        }

        [Test]
        public void ThePublicWorldIsNeverHeldByTheLifecycle()
        {
            var plane = Plane();
            Activate(plane);
            plane.SetScopeState(Key, ScopeState.Retiring);

            Assert.That(ScopeLifecycle.Admits(plane, EntityLocation.PublicScope, out var reason), Is.True);
            Assert.That(reason, Is.EqualTo(JoinHoldReason.None));
        }
    }
}
