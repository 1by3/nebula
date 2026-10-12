using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// Conformance scenario 70 (<c>docs/portals.md</c> D10, D16): a link readies every leased container its far window touches, not only the entry, and a
    /// crossing commits into the deepest of them that holds the mapped point. The annex below has two parts: the rear one, which a template's entry rule would
    /// pick (the first part), and the front one, which holds the doorway. Tier B on <see cref="PortalHarness"/>; the client's half of the warm handshake
    /// (every row prepared before <see cref="MsgId.PortalWarm"/>) is in <c>PortalWireTests</c> and the play-mode client tests.
    /// </summary>
    [Category("Conformance")]
    public sealed class PortalDeepestFarContainerTests
    {
        private const string Party = "crew";
        private PortalHarness _h;
        private InstanceTemplate _annex;
        private Portal _door;
        /// <summary>The container an entity was in the moment its crossing committed (the end of the tick may move it on, as the container pass does for any entity).</summary>
        private Container _committedInto;

        [SetUp]
        public void SetUp()
        {
            _h = new PortalHarness(2);
            // The first part is the entry; the doorway (the instance's origin) is in the second.
            _annex = _h.MakeTemplate("annex",
                ("rear", new Vector3(0f, 2f, 10f), new Vector3(8f, 4f, 12f)),
                ("front", new Vector3(0f, 2f, 0f), new Vector3(8f, 4f, 8f)));
            _door = _h.NewPortal("annex-door", PortalHarness.DoorAt + new Vector3(0f, 0f, 30f), null, _annex);
            _door.Aperture = PortalAperture.Box(2f, 2.5f);
            _committedInto = null;
            _door.Crossed += (entity, portal, from, to) => _committedInto = entity.Container;
        }

        [TearDown] public void TearDown() => _h.Dispose();

        private static Vector3 At(float x, float z) => new Vector3(20f + x, 1f, 50f + z);

        private Container Rear => PortalHarness.RuntimePart("annex", Party, "rear");
        private Container Front => PortalHarness.RuntimePart("annex", Party, "front");

        private PortalLink OpenAnnex()
        {
            var link = _door.Open(Party, new[] { PortalHarness.Client });
            _h.Ticks(4);
            _h.Warm(link);
            _h.Tick();
            Assert.That(link.State, Is.EqualTo(PortalState.Open), link.ToString());
            return link;
        }

        private NetworkIdentity Pawn()
        {
            var pawn = _h.W.SpawnServerDriven(_h.PawnPrefab, _h.Street, At(0f, -1f), Quaternion.identity);
            pawn.OwnerClientId = PortalHarness.Client;
            pawn.OwnerIdentity = PortalHarness.Owner;
            return pawn;
        }

        [Test]
        public void ALinkReadiesEveryLeasedContainerTheFarWindowTouchesTheEntryFirst()
        {
            var link = OpenAnnex();
            Assert.That(link.FarEntry, Is.SameAs(Rear), "a template's entry is its first part");
            Assert.That(link.Rows.Count, Is.EqualTo(2));
            Assert.That(link.Rows[0].Container, Is.SameAs(Rear));
            Assert.That(link.Rows[1].Container, Is.SameAs(Front));
            Assert.That(link.Rows[0].ReadyEpoch, Is.Not.EqualTo(0UL));

            var published = new List<PortalLinkRecord>();
            foreach (var m in _h.Mesh.DeliveredOf(MsgId.PortalLinks, "gw")) published.AddRange(m.Read(PortalLinksMsg.Read).Upserts);
            var last = published[published.Count - 1];
            Assert.That(last.FarRowIds, Is.EqualTo(new[] { Rear.ContainerId, Front.ContainerId }), "the network gateway pins both rows for a member, the client prepares both");
            Assert.That(last.FarRows, Is.EqualTo(new[] { ContainerRef.Of(Rear), ContainerRef.Of(Front) }));
            Assert.That(last.FarEntryId, Is.EqualTo(Rear.ContainerId));
        }

        [Test]
        public void ACrossingLandsInTheDeepestFarContainerThatHoldsTheMappedPointNotTheEntry()
        {
            OpenAnnex();
            var pawn = Pawn();
            _h.Ticks(2);
            pawn.transform.position = At(0f, 0.1f);
            _h.Tick();
            Assert.That(pawn.InstanceId, Is.EqualTo(NebulaWorker.InstanceKey("annex/" + Party)), "crossed");
            Assert.That(_committedInto, Is.SameAs(Front), "the front part holds the doorway; the entry rule would have committed it into the rear part and let the container pass move it on");
            Assert.That(pawn.Container, Is.SameAs(Front));
            Assert.That(Rear.Entities, Has.No.Member(pawn));
        }

        [Test]
        public void ACommitIntoTheDeepestContainerOfAnotherWorkerIsOneHandoverAndAskedForItsOwnAnswer()
        {
            _h.LeaseToSecondWorker("annex", Party, "front");
            var link = _door.Open(Party, new[] { PortalHarness.Client });
            _h.Ticks(5);
            var asked = _h.Mesh.DeliveredOf(MsgId.InstancePrepare, _h.Other.Id);
            Assert.That(asked.Count, Is.EqualTo(1), "only the row the other worker owns is asked for");
            var message = asked[0].Read(InstancePreparationMsg.Read);
            Assert.That(message.LinkId, Is.EqualTo(link.Id));
            Assert.That(message.Destination, Is.EqualTo(ContainerRef.Of(Front)));
            _h.Warm(link);
            _h.Tick();
            Assert.That(link.State, Is.EqualTo(PortalState.Open), link.ToString());
            Assert.That(NebulaDiagnostics.SplitPortalLinks, Is.EqualTo(1), "split by a window row, not the entry");

            var pawn = Pawn();
            ulong id = pawn.NetId;
            _h.TickAll(2);
            pawn.transform.position = At(0f, 0.1f);
            _h.TickAll();
            var arrived = _h.Other.Find(id);
            Assert.That(arrived.HasAuthority, Is.True, "the same tick");
            Assert.That(arrived.Container, Is.SameAs(Front));
            Assert.That(_h.W.Find(id).HasAuthority, Is.False);
        }

        [Test]
        public void ARowThatIsNotWarmIsNeverEnteredTheNearestEnclosingRowIs()
        {
            var link = OpenAnnex();
            link.Rows.RemoveAt(1); // as if the front part appeared after the link opened: no client was warmed for it
            var pawn = Pawn();
            _h.Ticks(2);
            pawn.transform.position = At(0f, 0.1f);
            _h.Tick();
            Assert.That(pawn.InstanceId, Is.EqualTo(NebulaWorker.InstanceKey("annex/" + Party)));
            Assert.That(_committedInto, Is.SameAs(Rear), "never a container the link did not ready: the entry (the container pass then moves the pawn on into the front part, as it does for anything standing in another box)");
        }

        [Test]
        public void ALeaseThatMovedUnderARowMakesTheLinkPrepareAgain()
        {
            var link = OpenAnnex();
            ContainerRegistry.ApplyLease(Front.ContainerId, _h.W.Id, _h.W.Index, 99);
            _h.Tick();
            Assert.That(link.State, Is.EqualTo(PortalState.Preparing), "the lease epoch of a row moved since the link was ready");
            Assert.That(link.Wait, Is.EqualTo(PortalWait.FarScopeActivating));
        }

        [Test]
        public void AnOpenLinkKeepsTheRowsItOpenedWithAndDoesNotLookForMore()
        {
            var link = _door.Open(Party, new[] { PortalHarness.Client });
            _h.Ticks(2);
            uint generation = link.Generation;
            _h.Warm(link);
            _h.Tick();
            if (link.State != PortalState.Open) { _h.Ticks(2); _h.Warm(link); _h.Tick(); }
            Assert.That(link.State, Is.EqualTo(PortalState.Open), link.ToString());
            Assert.That(link.Rows.Count, Is.EqualTo(2));
            Assert.That(link.Generation, Is.EqualTo(generation), "no row appeared after the first look, so the opening is the same one");
            _h.Ticks(70);
            Assert.That(link.State, Is.EqualTo(PortalState.Open), "and an open link does not look for rows again");
            Assert.That(link.Rows.Count, Is.EqualTo(2));
        }
    }
}
