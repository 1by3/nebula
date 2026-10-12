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
    /// The portal messages of protocol 28 on the wire (NEB-428, <c>docs/portals.md</c> D6, D45, D46): <see cref="MsgId.Portals"/>,
    /// <see cref="MsgId.PortalLinks"/>, <see cref="MsgId.PortalWarm"/>, the trailing link id of <see cref="InstancePreparationMsg"/>, the ids
    /// portals and links are derived to, and the closed window. Pure C#, so the same tests run in <c>Nebula.Services.Tests</c>.
    /// </summary>
    [Category("Conformance")]
    public class PortalWireTests
    {
        private static NetworkReader ReaderAfterId(NetworkWriter w, MsgId expected)
        {
            var r = new NetworkReader(w.ToSegment());
            Assert.AreEqual(expected, (MsgId)r.ReadByte());
            return r;
        }

        [Test]
        public void TheMessageIdsAreTheNextFreeOnesAndTheWindowIsClosedAt28()
        {
            Assert.AreEqual(27, (int)MsgId.Portals);
            Assert.AreEqual(28, (int)MsgId.PortalLinks);
            Assert.AreEqual(29, (int)MsgId.PortalWarm);
            Assert.AreEqual(28, HelloMsg.ProtocolVersion);
            Assert.AreEqual(28, HelloMsg.MinProtocolVersion, "a client of 27 is refused: portals are not additive in meaning (D46)");
        }

        [Test]
        public void APortalRecordRoundTrips()
        {
            var record = new PortalRecord
            {
                PortalId = PortalIds.Of("c0", "door"), Id = "door", NearScope = 77, NearContainerId = "c0", NearContainer = ContainerRef.Runtime(0xABCDEF),
                NearOrigin = new Double3(50_000_000.125, -280_000.5, -60_000_000.75), NearRotation = new Quaternion(0f, 0.7071068f, 0f, 0.7071068f),
                FarKind = PortalFarKind.Fixed, FarScope = 99, FarOrigin = new Double3(100.25, 2, -3), FarRotation = new Quaternion(0.1f, 0.2f, 0.3f, 0.9f),
                TemplateId = "", ApertureKind = PortalApertureKind.Box, HalfWidth = 1.5f, HalfHeight = 2.25f,
                BandMargin = 0.5f, BandDepth = 2f, WindowMargin = 8f, WindowDepth = 48f, Flags = 3,
            };
            var w = new NetworkWriter();
            PortalsMsg.Write(w, new[] { record, record }, new ulong[] { 5, 6, 7 });
            var reader = ReaderAfterId(w, MsgId.Portals);
            var update = PortalsMsg.Read(reader);
            Assert.AreEqual(0, reader.Remaining, "nothing is left over once read");
            Assert.AreEqual(2, update.Upserts.Count);
            var read = update.Upserts[1];
            Assert.AreEqual(record.PortalId, read.PortalId);
            Assert.AreEqual("door", read.Id);
            Assert.AreEqual(77UL, read.NearScope);
            Assert.AreEqual("c0", read.NearContainerId);
            Assert.AreEqual(record.NearContainer, read.NearContainer);
            Assert.AreEqual(record.NearOrigin, read.NearOrigin, "a position 5x10^7 m out keeps its double");
            Assert.AreEqual(record.NearRotation.y, read.NearRotation.y);
            Assert.AreEqual(PortalFarKind.Fixed, read.FarKind);
            Assert.AreEqual(99UL, read.FarScope);
            Assert.AreEqual(record.FarOrigin, read.FarOrigin);
            Assert.AreEqual(0.9f, read.FarRotation.w);
            Assert.AreEqual(2.25f, read.HalfHeight);
            Assert.AreEqual(48f, read.WindowDepth);
            Assert.AreEqual((byte)3, read.Flags);
            CollectionAssert.AreEqual(new ulong[] { 5, 6, 7 }, update.Removes);
        }

        [Test]
        public void ALinkRecordRoundTripsWithItsMembersAndObservers()
        {
            var record = new PortalLinkRecord
            {
                LinkId = 11, PortalId = 12, PartyHash = PortalIds.PartyHash("crew-91"), FarScope = 13, FarEntryId = "rt_14", FarEntry = ContainerRef.Runtime(14),
                State = PortalState.Closing, Wait = PortalWait.BandOccupied, WaitClient = 15, Progress = 200, Generation = 4,
                Flags = PortalLinkFlags.Member | PortalLinkFlags.ObserverAnyone, Members = new List<ulong> { 1, 2, 3 }, Observers = new List<ulong> { 2 },
            };
            var w = new NetworkWriter();
            PortalLinksMsg.Write(w, new[] { record }, new ulong[] { 99 });
            var update = PortalLinksMsg.Read(ReaderAfterId(w, MsgId.PortalLinks));
            var read = update.Upserts[0];
            Assert.AreEqual(11UL, read.LinkId);
            Assert.AreEqual(12UL, read.PortalId);
            Assert.AreEqual(record.PartyHash, read.PartyHash, "only the party's hash travels, never the party itself");
            Assert.AreEqual(13UL, read.FarScope);
            Assert.AreEqual("rt_14", read.FarEntryId);
            Assert.AreEqual(record.FarEntry, read.FarEntry);
            Assert.AreEqual(PortalState.Closing, read.State);
            Assert.AreEqual(PortalWait.BandOccupied, read.Wait);
            Assert.AreEqual(15UL, read.WaitClient);
            Assert.AreEqual((byte)200, read.Progress);
            Assert.AreEqual(4u, read.Generation);
            Assert.AreEqual(record.Flags, read.Flags);
            Assert.IsTrue(read.IsObserver);
            CollectionAssert.AreEqual(new ulong[] { 1, 2, 3 }, read.Members);
            CollectionAssert.AreEqual(new ulong[] { 2 }, read.Observers);
            CollectionAssert.AreEqual(new ulong[] { 99 }, update.Removes);

            // To a client both lists are empty.
            record.Members = null;
            record.Observers = null;
            var toClient = new NetworkWriter();
            PortalLinksMsg.Write(toClient, new[] { record }, null);
            var seen = PortalLinksMsg.Read(ReaderAfterId(toClient, MsgId.PortalLinks)).Upserts[0];
            Assert.AreEqual(0, seen.Members.Count);
            Assert.AreEqual(0, seen.Observers.Count);
        }

        [Test]
        public void AWarmCarriesTheLinkTheGenerationAndTheClientTheGatewayStamped()
        {
            var w = new NetworkWriter();
            new PortalWarmMsg { LinkId = 123456789012345UL, Generation = 3 }.Write(w);
            var read = PortalWarmMsg.Read(ReaderAfterId(w, MsgId.PortalWarm));
            Assert.AreEqual(123456789012345UL, read.LinkId);
            Assert.AreEqual(3u, read.Generation);
            Assert.AreEqual(0UL, read.ClientId, "a client sends none; the gateway stamps it");
            var forwarded = new NetworkWriter();
            new PortalWarmMsg { LinkId = 1, Generation = 2, ClientId = 7 }.Write(forwarded);
            Assert.AreEqual(7UL, PortalWarmMsg.Read(ReaderAfterId(forwarded, MsgId.PortalWarm)).ClientId);
        }

        [Test]
        public void AnInstancePreparationCarriesATrailingLinkIdAndAnOlderBodyReadsZero()
        {
            var message = new InstancePreparationMsg
            {
                RequestId = 9, EntityId = 0, Destination = ContainerRef.Runtime(21), SourceWorker = 2, LeaseEpoch = 8, Success = true, LinkId = 0xFEEDFACEUL,
            };
            var w = new NetworkWriter();
            message.Write(w, MsgId.InstancePrepare);
            var read = InstancePreparationMsg.Read(ReaderAfterId(w, MsgId.InstancePrepare));
            Assert.AreEqual(0xFEEDFACEUL, read.LinkId);
            Assert.AreEqual(9u, read.RequestId);
            Assert.IsTrue(read.Success);

            // A body written before the link id ends after Success.
            var old = new NetworkWriter();
            old.WriteByte((byte)MsgId.InstancePrepare);
            old.WriteUInt(9); old.WriteULong(42); message.Destination.Write(old); old.WriteUShort(2); old.WriteULong(8); old.WriteBool(true);
            var oldRead = InstancePreparationMsg.Read(ReaderAfterId(old, MsgId.InstancePrepare));
            Assert.AreEqual(0UL, oldRead.LinkId, "a preparation of one entity's crossing carries no link");
            Assert.AreEqual(42UL, oldRead.EntityId);
        }

        [Test]
        public void IdsAreDerivedSoEveryProcessComputesTheSameOne()
        {
            Assert.AreEqual(ScopeKeys.Hash("street#door"), PortalIds.Of("street", "door"), "the container's id, a '#', and the authored id (D6)");
            Assert.AreNotEqual(PortalIds.Of("street", "door"), PortalIds.Of("street", "window"));
            Assert.AreNotEqual(PortalIds.Of("street", "door"), PortalIds.Of("alley", "door"));
            ulong portal = PortalIds.Of("street", "door");
            Assert.AreEqual(PortalIds.Link(portal, "a"), PortalIds.Link(portal, "a"));
            Assert.AreNotEqual(PortalIds.Link(portal, "a"), PortalIds.Link(portal, "b"));
            Assert.AreNotEqual(PortalIds.Link(portal, ""), PortalIds.Link(portal, "a"));
            Assert.AreNotEqual(PortalIds.PartyHash("a"), PortalIds.PartyHash("b"));
        }
    }
}
