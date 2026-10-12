using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// Conformance scenario 71 (<c>docs/portals.md</c> D44): a planned reassignment of the container a portal's near face is fixed in hands the portal's links
    /// to the new owner, worker to worker, with the release. The new owner holds them waiting on <see cref="PortalWait.NearContainerMoving"/> while it looks
    /// at everything again, then opens them without asking the warm clients a second time; the generation goes on; the network gateways keep the records until
    /// the new owner replaces them. A crash, an unowned container or a new owner nobody can reach sends nothing and the links read closed. Tier B on
    /// <see cref="PortalHarness"/>: the two workers share the process-wide registries, so the portal component is one object that both of them drive; what each
    /// worker does with the links it is given is what is under test.
    /// </summary>
    [Category("Conformance")]
    public sealed class PortalHandoffTests
    {
        private PortalHarness _h;

        [SetUp] public void SetUp() => _h = new PortalHarness(2);
        [TearDown] public void TearDown() => _h.Dispose();

        private PortalLink Current()
        {
            Assert.That(_h.Door.TryGetLink(PortalHarness.Owner, out var link), Is.True);
            return link;
        }

        private void Peer(ConformanceMesh.Worker from, ConformanceMesh.Worker to, bool connected)
        {
            var peer = from.PeersById[to.Id];
            peer.GetType().GetField("HelloReceived").SetValue(peer, connected);
        }

        private List<PortalHandoffMsg> Handoffs()
        {
            var all = new List<PortalHandoffMsg>();
            foreach (var m in _h.Mesh.DeliveredOf(MsgId.PortalHandoff, _h.Other.Id)) all.Add(m.Read(PortalHandoffMsg.Read));
            return all;
        }

        private int LinkRemovalsSentToTheGateway()
        {
            int n = 0;
            foreach (var m in _h.Mesh.DeliveredOf(MsgId.PortalLinks, "gw")) n += m.Read(PortalLinksMsg.Read).Removes.Count;
            return n;
        }

        [Test]
        public void APlannedReassignmentOfTheNearContainerHandsTheLinkToTheNewOwnerWhichOpensItWithoutAskingTheClientAgain()
        {
            var old = _h.OpenDoor();
            uint generation = old.Generation;
            int removals = LinkRemovalsSentToTheGateway();

            _h.Mesh.SetOwner(_h.Street, _h.Other); // the planner moved the street
            _h.Tick();                             // the releasing worker notices and sends the links with the release

            var sent = Handoffs();
            Assert.That(sent.Count, Is.EqualTo(1), "one message, to the new owner");
            Assert.That(sent[0].PortalId, Is.EqualTo(_h.Door.PortalId));
            Assert.That(sent[0].NearContainerId, Is.EqualTo("street"));
            Assert.That(sent[0].Links.Count, Is.EqualTo(1));
            var carried = sent[0].Links[0];
            Assert.That(carried.Party, Is.EqualTo(PortalHarness.Owner));
            Assert.That(carried.State, Is.EqualTo(PortalState.Open));
            Assert.That(carried.Generation, Is.EqualTo(generation));
            Assert.That(carried.Members, Is.EqualTo(new[] { PortalHarness.Client }));
            Assert.That(carried.Warm, Is.EqualTo(new[] { PortalHarness.Client }), "the members that were warm");
            Assert.That(NebulaDiagnostics.PortalHandoffs, Is.GreaterThanOrEqualTo(1));

            Assert.That(old.State, Is.EqualTo(PortalState.Closed), "whoever still holds the old link sees it closed ...");
            Assert.That(old.Wait, Is.EqualTo(PortalWait.NearContainerMoving), "... and why");
            var moved = Current();
            Assert.That(moved, Is.Not.SameAs(old), "the new owner has a link of its own");
            Assert.That(moved.State, Is.EqualTo(PortalState.Preparing), "waiting while it looks at everything again");
            Assert.That(moved.Wait, Is.EqualTo(PortalWait.NearContainerMoving));
            Assert.That(moved.Generation, Is.EqualTo(generation), "the generation goes on, so the clients' warm acknowledgements stay valid");
            Assert.That(moved.IsWarm(PortalHarness.Client), Is.True);
            Assert.That(LinkRemovalsSentToTheGateway(), Is.EqualTo(removals), "the gateways keep the record until the new owner replaces it: no flicker");

            _h.Other.Tick(_h.TickNumber + 1);
            _h.Mesh.Pump();
            for (int i = 0; i < 4 && moved.State != PortalState.Open; i++) { _h.Other.Tick(_h.TickNumber + 2 + (uint)i); _h.Mesh.Pump(); }
            Assert.That(moved.State, Is.EqualTo(PortalState.Open), moved.ToString());
            Assert.That(moved.Wait, Is.EqualTo(PortalWait.None), "the warm members were not asked again");
            Assert.That(moved.Generation, Is.EqualTo(generation));
        }

        [Test]
        public void ANewOwnerThatHasNotYetLearnedItOwnsTheContainerHoldsTheLinksUntilItDoes()
        {
            _h.OpenDoor();
            var bytes = new NetworkWriter(128);
            new PortalHandoffMsg
            {
                PortalId = _h.Door.PortalId, NearContainerId = "street",
                Links = new List<PortalHandoffLink> { new PortalHandoffLink { Party = "someone-else", Generation = 4, State = PortalState.Open, Members = new List<ulong> { 9 }, Warm = new List<ulong> { 9 }, Observers = new List<ulong> { 9 } } },
            }.Write(bytes);
            // The street is still the first worker's as far as the second can tell: the links wait.
            _h.Other.Dispatch(_h.Other.PeersById[_h.W.Id], bytes.ToArray());
            _h.Other.Tick(_h.TickNumber + 1);
            Assert.That(_h.Door.TryGetLink("someone-else", out _), Is.False);

            _h.Mesh.SetOwner(_h.Street, _h.Other);
            _h.Other.Tick(_h.TickNumber + 2);
            Assert.That(_h.Door.TryGetLink("someone-else", out var late), Is.True, "installed as soon as the lease and the portal are here");
            Assert.That(late.Generation, Is.EqualTo(4u));
            Assert.That(late.IsMember(9), Is.True);
            Assert.That(late.IsObserver(9), Is.True);
            Assert.That(late.Wait, Is.EqualTo(PortalWait.NearContainerMoving).Or.EqualTo(PortalWait.FarScopeActivating));
        }

        [Test]
        public void ANewOwnerNobodyCanReachGetsNothingAndTheLinkReadsClosed()
        {
            var link = _h.OpenDoor();
            Peer(_h.W, _h.Other, false);
            _h.Mesh.SetOwner(_h.Street, _h.Other);
            _h.Tick();
            Assert.That(Handoffs(), Is.Empty, "nothing was sent");
            Assert.That(link.State, Is.EqualTo(PortalState.Closed));
            Assert.That(link.Wait, Is.EqualTo(PortalWait.NearContainerMoving));
        }

        [Test]
        public void AContainerWithNoOwnerAfterACrashLosesTheLinksAndTheyReadClosed()
        {
            var link = _h.OpenDoor();
            ContainerRegistry.ApplyLease("street", "", ushort.MaxValue, 2);
            _h.Tick();
            Assert.That(Handoffs(), Is.Empty);
            Assert.That(link.State, Is.EqualTo(PortalState.Closed), "on a crash the links are lost and read Closed, the safe state");
            Assert.That(link.Wait, Is.EqualTo(PortalWait.NearContainerMoving));
        }

        [Test]
        public void ALinkThatWasClosingKeepsClosingOnTheNewOwnerAndAClosedOneKeepsItsGeneration()
        {
            var link = _h.OpenDoor();
            _h.Door.Close(PortalHarness.Owner);
            Assert.That(link.State, Is.EqualTo(PortalState.Closing));
            _h.Mesh.SetOwner(_h.Street, _h.Other);
            _h.Tick();
            var moved = Current();
            Assert.That(moved.State, Is.EqualTo(PortalState.Closing), "it was on its way out and still is");
            _h.Other.Tick(_h.TickNumber + 1);
            _h.Other.Tick(_h.TickNumber + 2);
            Assert.That(moved.State, Is.EqualTo(PortalState.Closed));
            Assert.That(moved.Generation, Is.EqualTo(link.Generation));
        }
    }
}
