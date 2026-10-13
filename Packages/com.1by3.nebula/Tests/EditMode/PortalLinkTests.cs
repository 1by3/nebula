using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nebula.Tests
{
    /// <summary>
    /// NEB-428 (<c>docs/portals.md</c> §3): the state machine of a party's <see cref="PortalLink"/>, every transition and every wait.
    /// Closed → Preparing → Open → Closing → Closed, with the reason a link is not open (<see cref="PortalWait"/>), the progress a door's UI
    /// shows, members that warm, observers the game chooses, and what the worker publishes to a network gateway. Tier B: real
    /// <see cref="NebulaWorker"/>s on the <see cref="ConformanceMesh"/>, a <see cref="LocalControlPlane"/>, and a gateway that only listens.
    /// </summary>
    [Category("Conformance")]
    public sealed class PortalLinkTests
    {
        private PortalHarness _h;

        private sealed class SlowContent : MonoBehaviour, IInstanceContentLoader
        {
            public InstanceContentState Current = InstanceContentState.Loading;
            public float Fraction;
            public InstanceContentState State => Current;
            public float Progress => Fraction;
        }

        [SetUp] public void SetUp() => _h = new PortalHarness(2);
        [TearDown] public void TearDown() => _h.Dispose();

        private PortalLink Link(string party = PortalHarness.Owner) { _h.Door.TryGetLink(party, out var link); return link; }

        private ScopeInfo FarScope() => _h.Plane.FindScope("vault/" + PortalHarness.Owner);

        // ------------------------------------------------------------------------------------------ the happy path

        [Test]
        public void AnUnopenedPortalIsClosedAndHasNoLinks()
        {
            Assert.That(_h.Door.StateOf(PortalHarness.Owner), Is.EqualTo(PortalState.Closed));
            Assert.That(_h.Door.TryGetLink(PortalHarness.Owner, out _), Is.False);
            Assert.That(_h.Door.LinkCount, Is.EqualTo(0));
            Assert.That(_h.Door.Close(PortalHarness.Owner), Is.False, "nothing to close");
        }

        [Test]
        public void ALinkPreparesThroughEachWaitAndOpensWhenEveryConditionHolds()
        {
            var link = _h.Door.Open(PortalHarness.Owner, new[] { PortalHarness.Client });
            Assert.That(link.State, Is.EqualTo(PortalState.Preparing));
            Assert.That(link.Wait, Is.EqualTo(PortalWait.FarScopeActivating));
            Assert.That(link.Generation, Is.EqualTo(1u));
            Assert.That(link.Party, Is.EqualTo(PortalHarness.Owner));
            Assert.That(link.Id, Is.EqualTo(PortalIds.Link(_h.Door.PortalId, PortalHarness.Owner)));

            _h.Tick(); // the far scope is activated; its part is not registered here yet
            Assert.That(_h.Plane.FindScope("vault/" + PortalHarness.Owner), Is.Not.Null, "opening prepares the party's instance at the face");
            Assert.That(link.Wait, Is.EqualTo(PortalWait.FarScopeActivating));
            float activating = link.Progress;

            _h.Tick(); // its entry part is here and leased: the content is ready, the client is not
            Assert.That(link.State, Is.EqualTo(PortalState.Preparing));
            Assert.That(link.Wait, Is.EqualTo(PortalWait.ClientWarming));
            Assert.That(link.WaitClient, Is.EqualTo(PortalHarness.Client), "the link says whose client it waits for");
            Assert.That(link.Progress, Is.GreaterThan(activating).And.LessThan(1f));
            Assert.That(link.FarEntry, Is.SameAs(_h.Hall));
            Assert.That(link.FarScope, Is.EqualTo(PortalHarness.Private));

            _h.Warm(link);
            _h.Tick();
            Assert.That(link.State, Is.EqualTo(PortalState.Open));
            Assert.That(link.Wait, Is.EqualTo(PortalWait.None));
            Assert.That(link.Progress, Is.EqualTo(1f));
            Assert.That(link.IsOpen, Is.True);
            Assert.That(NebulaDiagnostics.OpenPortalLinks, Is.EqualTo(1));
        }

        [Test]
        public void TheFarFaceOfATemplateIsTheNearFaceReadInTheInstancesOwnSpace()
        {
            var link = _h.OpenDoor();
            Assert.That(link.NearFace.Origin, Is.EqualTo(new Double3(20, 0, 20)));
            Assert.That(link.FarFace.Origin, Is.EqualTo(link.NearFace.Origin), "a template portal is coincident by construction");
            Assert.That(link.FarFace.Scope, Is.EqualTo(PortalHarness.Private));
            Assert.That(link.NearFace.Scope, Is.EqualTo(0UL));
            Assert.That(link.Turn, Is.EqualTo(Quaternion.identity), "an upright door turns nothing");
            var p = new Double3(23.5, 1.25, 26.75);
            Assert.That((link.ToNear(p) - p).X, Is.EqualTo(0.0).Within(1e-9));
            Assert.That(link.ToFar(link.ToNear(p)).Z, Is.EqualTo(p.Z).Within(1e-9));
        }

        // ------------------------------------------------------------------------------------------ every wait

        [Test]
        public void ContentThatIsStillLoadingHoldsTheLinkWithFarWorkerContent()
        {
            var link = _h.Door.Open(PortalHarness.Owner, new[] { PortalHarness.Client });
            _h.Tick();
            var loader = new GameObject("slow").AddComponent<SlowContent>();
            _h.Objects.Add(loader.gameObject);
            loader.transform.SetParent(_h.Hall.transform, false);
            loader.Fraction = 0.5f;
            _h.Tick();
            Assert.That(link.Wait, Is.EqualTo(PortalWait.FarWorkerContent));
            float loading = link.Progress;

            loader.Current = InstanceContentState.Ready;
            _h.Tick();
            Assert.That(link.Wait, Is.EqualTo(PortalWait.ClientWarming));
            Assert.That(link.Progress, Is.GreaterThan(loading));
        }

        [Test]
        public void ContentThatFailedKeepsTheLinkClosedToTrafficAndSaysWhy()
        {
            var link = _h.Door.Open(PortalHarness.Owner, new[] { PortalHarness.Client });
            _h.Tick();
            var loader = new GameObject("broken").AddComponent<SlowContent>();
            _h.Objects.Add(loader.gameObject);
            loader.transform.SetParent(_h.Hall.transform, false);
            loader.Current = InstanceContentState.Failed;
            LogAssert.Expect(LogType.Error, new Regex("failed to load"));
            _h.Tick();
            Assert.That(link.State, Is.EqualTo(PortalState.Preparing));
            Assert.That(link.Wait, Is.EqualTo(PortalWait.FarWorkerContent));
            Assert.That(link.Error, Does.Contain("failed to load"));
        }

        [Test]
        public void ARestoringFarScopeHoldsTheLinkWithFarEntitiesRestoring()
        {
            var link = _h.Door.Open(PortalHarness.Owner, new[] { PortalHarness.Client });
            _h.Tick();
            FarScope().State = ScopeState.Restoring;
            _h.Tick();
            Assert.That(link.Wait, Is.EqualTo(PortalWait.FarEntitiesRestoring));

            FarScope().State = ScopeState.Active;
            _h.Tick();
            Assert.That(link.Wait, Is.EqualTo(PortalWait.ClientWarming));
        }

        [Test]
        public void AFarContainerAnotherWorkerOwnsIsAskedForAndAnswersTheHandshake()
        {
            _h.HallOnSecondWorker = true;
            var link = _h.Door.Open(PortalHarness.Owner, new[] { PortalHarness.Client });
            _h.Tick();
            _h.Tick();
            Assert.That(link.Wait, Is.EqualTo(PortalWait.FarWorkerHandshake), "waiting for the far worker's answer");
            var asked = _h.Mesh.DeliveredOf(MsgId.InstancePrepare, _h.Other.Id);
            Assert.That(asked.Count, Is.EqualTo(1));
            var message = asked[0].Read(InstancePreparationMsg.Read);
            Assert.That(message.LinkId, Is.EqualTo(link.Id), "the preparation carries the link, not an entity");
            Assert.That(message.EntityId, Is.EqualTo(0UL));
            var answers = _h.Mesh.DeliveredOf(MsgId.InstanceReady, _h.W.Id);
            Assert.That(answers.Count, Is.EqualTo(1));
            Assert.That(answers[0].Read(InstancePreparationMsg.Read).LinkId, Is.EqualTo(link.Id), "and so does the answer");

            _h.Tick();
            Assert.That(link.Wait, Is.EqualTo(PortalWait.ClientWarming), "answered");
            Assert.That(NebulaDiagnostics.SplitPortalLinks, Is.EqualTo(0), "not open yet");
            _h.Warm(link);
            _h.Tick();
            Assert.That(link.State, Is.EqualTo(PortalState.Open));
            Assert.That(NebulaDiagnostics.SplitPortalLinks, Is.EqualTo(1), "an open link into another worker's container is counted");
        }

        [Test]
        public void ALinkWithoutAPartyCannotUseATemplateAndSaysSo()
        {
            LogAssert.Expect(LogType.Warning, new Regex("needs a party"));
            var link = _h.Door.Open("");
            _h.Ticks(2);
            Assert.That(link.State, Is.EqualTo(PortalState.Preparing));
            Assert.That(link.Error, Does.Contain("party"));
        }

        [Test]
        public void ALinkWithNoMembersOpensWithoutAnyClient()
        {
            var link = _h.Door.Open(PortalHarness.Owner);
            _h.Ticks(3);
            Assert.That(link.State, Is.EqualTo(PortalState.Open), "nothing to warm: a link only server-owned entities use");
        }

        // ------------------------------------------------------------------------------------------ closing

        [Test]
        public void ClosingWaitsForTheBandToEmptyThenClosesAndSaysWhatItWaitsFor()
        {
            var link = _h.OpenDoor();
            _h.Player.transform.position = PortalHarness.At(0f, -1f); // inside the band, 2 m either side of the face
            Assert.That(_h.Door.Close(PortalHarness.Owner), Is.True);
            Assert.That(link.State, Is.EqualTo(PortalState.Closing));
            Assert.That(link.Wait, Is.EqualTo(PortalWait.BandOccupied));
            _h.Ticks(3);
            Assert.That(link.State, Is.EqualTo(PortalState.Closing), "a body in the band holds it");
            Assert.That(link.Wait, Is.EqualTo(PortalWait.BandOccupied));

            _h.Player.transform.position = PortalHarness.At(0f, -6f);
            _h.Tick();
            Assert.That(link.State, Is.EqualTo(PortalState.Closed));
            Assert.That(link.Wait, Is.EqualTo(PortalWait.None));
            Assert.That(_h.Door.StateOf(PortalHarness.Owner), Is.EqualTo(PortalState.Closed));
        }

        [Test]
        public void AClosingLinkHoldsForItsCloseHoldThoughItsBandIsEmpty()
        {
            _h.Door.CloseHoldSeconds = 1.2f;
            var link = _h.OpenDoor();
            _h.Player.transform.position = PortalHarness.At(0f, -6f); // out of the band
            Assert.That(_h.Door.Close(PortalHarness.Owner), Is.True);
            _h.Tick();
            Assert.That(link.State, Is.EqualTo(PortalState.Closing), "held: a door is still closing over the aperture, and observers still draw the far side");
            Assert.That(link.Wait, Is.EqualTo(PortalWait.CloseHold), "and it says why");
            _h.Now += 1f;
            _h.Tick();
            Assert.That(link.State, Is.EqualTo(PortalState.Closing), "not yet");
            _h.Now += 0.3f;
            _h.Tick();
            Assert.That(link.State, Is.EqualTo(PortalState.Closed), "closed once the hold is over");
            Assert.That(link.Wait, Is.EqualTo(PortalWait.None));
        }

        [Test]
        public void ClosingGivesUpAfterTheTimeoutAndCountsTheOverrun()
        {
            var link = _h.OpenDoor();
            _h.Player.transform.position = PortalHarness.At(0f, -1f);
            int before = NebulaDiagnostics.PortalCloseOverruns;
            _h.Door.Close(PortalHarness.Owner);
            _h.Now += Portal.CloseTimeoutSeconds - 1f;
            _h.Tick();
            Assert.That(link.State, Is.EqualTo(PortalState.Closing));
            _h.Now += 2f;
            LogAssert.Expect(LogType.Warning, new Regex("still occupied"));
            _h.Tick();
            Assert.That(link.State, Is.EqualTo(PortalState.Closed));
            Assert.That(NebulaDiagnostics.PortalCloseOverruns, Is.EqualTo(before + 1));
        }

        [Test]
        public void ALinkThatNeverOpenedClosesAtOnce()
        {
            var link = _h.Door.Open(PortalHarness.Owner, new[] { PortalHarness.Client });
            Assert.That(_h.Door.Close(PortalHarness.Owner), Is.True);
            Assert.That(link.State, Is.EqualTo(PortalState.Closed));
            Assert.That(_h.Door.Close(PortalHarness.Owner), Is.False, "closing a closed link does nothing");
        }

        [Test]
        public void OpeningAClosingLinkOpensItAgainWithoutWarmingTheClientsTwice()
        {
            var link = _h.OpenDoor();
            _h.Player.transform.position = PortalHarness.At(0f, -1f);
            _h.Door.Close(PortalHarness.Owner);
            Assert.That(link.State, Is.EqualTo(PortalState.Closing));
            uint generation = link.Generation;

            Assert.That(_h.Door.Open(PortalHarness.Owner), Is.SameAs(link));
            Assert.That(link.State, Is.EqualTo(PortalState.Preparing));
            _h.Tick();
            Assert.That(link.State, Is.EqualTo(PortalState.Open), "readiness is standing: nothing to wait for");
            Assert.That(link.Generation, Is.EqualTo(generation));
        }

        [Test]
        public void OpeningAClosedLinkStartsAgainAndAWarmForTheOldOpeningIsStale()
        {
            var link = _h.OpenDoor();
            _h.Door.Close(PortalHarness.Owner);
            _h.Tick();
            Assert.That(link.State, Is.EqualTo(PortalState.Closed));

            _h.Door.Open(PortalHarness.Owner, new[] { PortalHarness.Client });
            Assert.That(link.Generation, Is.EqualTo(2u));
            _h.Ticks(2);
            Assert.That(link.Wait, Is.EqualTo(PortalWait.ClientWarming), "the client warms again for the new opening");
            _h.Warm(link, generation: 1);
            _h.Tick();
            Assert.That(link.State, Is.EqualTo(PortalState.Preparing), "a warm for the earlier opening counts for nothing");
            _h.Warm(link);
            _h.Tick();
            Assert.That(link.State, Is.EqualTo(PortalState.Open));
        }

        [Test]
        public void AFarScopeThatRetiresClosesAnOpenLinkWithFarScopeRetired()
        {
            var link = _h.OpenDoor();
            FarScope().State = ScopeState.Retiring;
            _h.Now += 2f;
            _h.Tick();
            Assert.That(link.State, Is.EqualTo(PortalState.Closed));
            Assert.That(link.Wait, Is.EqualTo(PortalWait.FarScopeRetired));
        }

        [Test]
        public void ANearContainerMovedToAnotherWorkerClosesTheLinkWithNearContainerMoving()
        {
            var link = _h.OpenDoor();
            _h.Mesh.SetOwner(_h.Street, _h.Other);
            _h.Tick();
            Assert.That(link.State, Is.EqualTo(PortalState.Closed));
            Assert.That(link.Wait, Is.EqualTo(PortalWait.NearContainerMoving));
        }

        // ------------------------------------------------------------------------------------------ members and observers

        [Test]
        public void AWarmFromANonMemberOrForTheWrongLinkIsIgnored()
        {
            var link = _h.Door.Open(PortalHarness.Owner, new[] { PortalHarness.Client });
            _h.Ticks(2);
            _h.Warm(link, clientId: 99);
            _h.Tick();
            Assert.That(link.State, Is.EqualTo(PortalState.Preparing));
            Assert.That(link.IsWarm(99), Is.False);
            _h.Mesh.FromGateway(_h.Gateway, _h.W, w => new PortalWarmMsg { LinkId = 12345, Generation = 1, ClientId = PortalHarness.Client }.Write(w));
            _h.Tick();
            Assert.That(link.IsWarm(PortalHarness.Client), Is.False);
        }

        [Test]
        public void AMemberAddedToAnOpenLinkWarmsWithoutClosingItForTheOthers()
        {
            var link = _h.OpenDoor();
            Assert.That(_h.Door.AddMember(PortalHarness.Owner, 8), Is.True);
            Assert.That(_h.Door.AddMember(PortalHarness.Owner, 8), Is.False, "already a member");
            _h.Tick();
            Assert.That(link.State, Is.EqualTo(PortalState.Open), "still open for the member who is warm");
            Assert.That(link.Wait, Is.EqualTo(PortalWait.ClientWarming));
            Assert.That(link.WaitClient, Is.EqualTo(8UL));

            _h.Warm(link, 8);
            _h.Tick();
            Assert.That(link.Wait, Is.EqualTo(PortalWait.None));
            Assert.That(_h.Door.RemoveMember(PortalHarness.Owner, 8), Is.True);
            Assert.That(link.IsMember(8), Is.False);
            Assert.That(link.IsWarm(8), Is.False);
        }

        [Test]
        public void RemovingTheOnlyUnwarmMemberLetsThePreparingLinkOpen()
        {
            var link = _h.Door.Open(PortalHarness.Owner, new ulong[] { PortalHarness.Client, 8 });
            _h.Ticks(2);
            _h.Warm(link);
            _h.Tick();
            Assert.That(link.Wait, Is.EqualTo(PortalWait.ClientWarming));
            Assert.That(link.WaitClient, Is.EqualTo(8UL));
            _h.Door.RemoveMember(PortalHarness.Owner, 8);
            _h.Tick();
            Assert.That(link.State, Is.EqualTo(PortalState.Open));
        }

        [Test]
        public void ObserversAreTheMembersTheGameAllows()
        {
            _h.Door.CanObserve = (client, link) => client != 8;
            var link = _h.Door.Open(PortalHarness.Owner, new ulong[] { PortalHarness.Client, 8 });
            Assert.That(link.IsObserver(PortalHarness.Client), Is.True);
            Assert.That(link.IsObserver(8), Is.False, "a member the game does not let look through");
            Assert.That(link.IsObserver(99), Is.False, "a stranger certainly does not");
            _h.Door.ObserveAnyone = true;
            Assert.That(link.IsObserver(99), Is.True, "unless the door lets anyone look");
        }

        // ------------------------------------------------------------------------------------------ ids and duplicates

        [Test]
        public void APortalsIdIsDerivedFromItsContainerAndItsAuthoredId()
        {
            Assert.That(_h.Door.NearContainer, Is.SameAs(_h.Street));
            Assert.That(_h.Door.PortalId, Is.EqualTo(PortalIds.Of("street", "door")));
            Assert.That(_h.Door.PortalId, Is.EqualTo(ScopeKeys.Hash("street#door")), "the container's id, '#', and the authored id");
            var other = _h.NewPortal("window", new Vector3(30f, 0f, 20f));
            Assert.That(other.PortalId, Is.Not.EqualTo(_h.Door.PortalId));
            Assert.That(Portals.Find(_h.Door.PortalId), Is.SameAs(_h.Door));
            _h.Door.TryGetLink("none", out _);
            Assert.That(PortalIds.Link(_h.Door.PortalId, "a"), Is.Not.EqualTo(PortalIds.Link(_h.Door.PortalId, "b")));
        }

        [Test]
        public void ASecondPortalWithTheSameIdInTheSameContainerIsIgnoredAndLogged()
        {
            var twin = _h.NewPortal("door", new Vector3(40f, 0f, 20f));
            LogAssert.Expect(LogType.Warning, new Regex("repeats the id 'door'"));
            _h.Tick();
            Assert.That(_h.Door.IsIgnored != twin.IsIgnored, "exactly one of them is");
            Assert.That(twin.IsIgnored, Is.True, "the later one");
        }

        // ------------------------------------------------------------------------------------------ what the worker publishes

        private List<PortalLinkRecord> PublishedLinks()
        {
            var all = new List<PortalLinkRecord>();
            foreach (var m in _h.Mesh.DeliveredOf(MsgId.PortalLinks, "gw")) all.AddRange(m.Read(PortalLinksMsg.Read).Upserts);
            return all;
        }

        private List<PortalRecord> PublishedPortals()
        {
            var all = new List<PortalRecord>();
            foreach (var m in _h.Mesh.DeliveredOf(MsgId.Portals, "gw")) all.AddRange(m.Read(PortalsMsg.Read).Upserts);
            return all;
        }

        [Test]
        public void TheWorkerPublishesThePortalAndEachLinkChangeToTheNetworkGateway()
        {
            _h.Tick();
            var portals = PublishedPortals();
            Assert.That(portals.Count, Is.EqualTo(1), "the portal's record goes once");
            var record = portals[0];
            Assert.That(record.PortalId, Is.EqualTo(_h.Door.PortalId));
            Assert.That(record.NearContainerId, Is.EqualTo("street"));
            Assert.That(record.NearOrigin, Is.EqualTo(new Double3(20, 0, 20)));
            Assert.That(record.FarKind, Is.EqualTo(PortalFarKind.Template));
            Assert.That(record.TemplateId, Is.EqualTo("vault"));
            Assert.That(record.HalfWidth, Is.EqualTo(2f));
            Assert.That(record.BandDepth, Is.EqualTo(Portal.BandDepth));
            Assert.That(record.WindowDepth, Is.EqualTo(48f));

            var link = _h.OpenDoor();
            var links = PublishedLinks();
            Assert.That(links.Count, Is.GreaterThanOrEqualTo(3), "preparing, a wait or two, and open");
            var states = new List<PortalState>();
            foreach (var l in links) states.Add(l.State);
            Assert.That(states[0], Is.EqualTo(PortalState.Preparing));
            Assert.That(states[states.Count - 1], Is.EqualTo(PortalState.Open));
            var last = links[links.Count - 1];
            Assert.That(last.LinkId, Is.EqualTo(link.Id));
            Assert.That(last.PortalId, Is.EqualTo(record.PortalId));
            Assert.That(last.Members, Is.EquivalentTo(new[] { PortalHarness.Client }), "the gateway is told who must be warm");
            Assert.That(last.Observers, Is.EquivalentTo(new[] { PortalHarness.Client }), "and who may look through");
            Assert.That(last.PartyHash, Is.EqualTo(PortalIds.PartyHash(PortalHarness.Owner)));
            Assert.That(last.FarScope, Is.EqualTo(PortalHarness.Private));
            Assert.That(last.FarEntryId, Is.EqualTo(PortalHarness.HallId));
            Assert.That(last.Generation, Is.EqualTo(1u));
            Assert.That(PublishedPortals().Count, Is.EqualTo(1), "an unchanged portal is not sent again");

            int before = PublishedLinks().Count;
            _h.Ticks(3);
            Assert.That(PublishedLinks().Count, Is.EqualTo(before), "and neither is an unchanged link");
        }

        [Test]
        public void AGatewayThatConnectsLaterIsToldEverything()
        {
            _h.OpenDoor();
            var late = _h.Mesh.AddGateway("late");
            _h.Mesh.LinkGateway(late);
            _h.Mesh.Pump();
            Assert.That(_h.Mesh.DeliveredOf(MsgId.Portals, "late").Count, Is.EqualTo(1));
            var links = _h.Mesh.DeliveredOf(MsgId.PortalLinks, "late");
            Assert.That(links.Count, Is.EqualTo(1));
            Assert.That(links[0].Read(PortalLinksMsg.Read).Upserts[0].State, Is.EqualTo(PortalState.Open));
        }

        [Test]
        public void ADisabledPortalIsWithdrawnWithItsLinks()
        {
            var link = _h.OpenDoor();
            PortalHarness.Disable(_h.Door);
            _h.Tick();
            var removes = new List<ulong>();
            foreach (var m in _h.Mesh.DeliveredOf(MsgId.Portals, "gw")) removes.AddRange(m.Read(PortalsMsg.Read).Removes);
            Assert.That(removes, Is.EquivalentTo(new[] { _h.Door.PortalId }));
            var linkRemoves = new List<ulong>();
            foreach (var m in _h.Mesh.DeliveredOf(MsgId.PortalLinks, "gw")) linkRemoves.AddRange(m.Read(PortalLinksMsg.Read).Removes);
            Assert.That(linkRemoves, Is.EquivalentTo(new[] { link.Id }));
        }

        // ------------------------------------------------------------------------------------------ a fixed far side

        [Test]
        public void AFixedFarSideOpensOnAScopeThatExistsAnywayAndMapsThroughTheTranslation()
        {
            // The scope exists anyway: the worker prepares an instance for "other" at (100, 1, 100); nobody opens a link into it by template.
            var parts = _h.W.Instance.PrepareInstance(_h.Template, "other", new Double3(100, 0, 100));
            _h.Ticks(2);
            var portal = _h.NewPortal("far", new Vector3(60f, 0f, 20f));
            portal.SetFixedFarSide("vault/other", new Double3(100, 2, 100), Quaternion.identity);
            var link = portal.Open("");
            _h.Ticks(3);
            Assert.That(link.State, Is.EqualTo(PortalState.Open), link.ToString());
            Assert.That(link.FarScope, Is.EqualTo(NebulaWorker.InstanceKey("vault/other")));
            Assert.That(link.FarEntry, Is.Not.Null);
            Assert.That(link.FarEntry.ContainerId, Is.EqualTo(ContainerRegistry.RuntimeContainerId(parts[0].RuntimeId)));
            var mapped = link.ToFar(new Double3(61, 0.5, 21));
            Assert.That(mapped.X, Is.EqualTo(101.0).Within(1e-9));
            Assert.That(mapped.Y, Is.EqualTo(2.5).Within(1e-9));
            Assert.That(mapped.Z, Is.EqualTo(101.0).Within(1e-9));
            Assert.That(link.ToNear(mapped).X, Is.EqualTo(61.0).Within(1e-9));
        }
    }
}
