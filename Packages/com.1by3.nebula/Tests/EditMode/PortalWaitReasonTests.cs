using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// Conformance scenario 72 (<c>docs/portals.md</c> D36): every wait has a reason, and each reason is reported in its situation. One situation for each
    /// <see cref="PortalWait"/> value drives a real link to it, and the same reason is what the worker's telemetry row for the link says, for the dashboard
    /// (<c>docs/portals.md</c> D36); a value nobody can reach fails the test, so a wait added later cannot go unreported. Tier B on <see cref="PortalHarness"/>.
    /// </summary>
    [Category("Conformance")]
    public sealed class PortalWaitReasonTests
    {
        private sealed class SlowContent : MonoBehaviour, IInstanceContentLoader
        {
            public InstanceContentState Current = InstanceContentState.Loading;
            public InstanceContentState State => Current;
            public float Progress => 0.5f;
        }

        private static void Peer(PortalHarness h, bool connected)
        {
            var peer = h.W.PeersById[h.Other.Id];
            peer.GetType().GetField("HelloReceived").SetValue(peer, connected);
        }

        /// <summary>Drive a link into the situation and return the wait it reports and the wait its telemetry row reports.</summary>
        private static (PortalWait link, PortalWait row) Reach(PortalWait wait)
        {
            using var h = new PortalHarness(2);
            PortalLink link;
            switch (wait)
            {
                case PortalWait.FarScopeActivating:
                    link = h.Door.Open(PortalHarness.Owner, new[] { PortalHarness.Client });
                    break;
                case PortalWait.FarWorkerContent:
                {
                    link = h.Door.Open(PortalHarness.Owner, new[] { PortalHarness.Client });
                    h.Tick();
                    var loader = new GameObject("slow").AddComponent<SlowContent>();
                    h.Objects.Add(loader.gameObject);
                    loader.transform.SetParent(h.Hall.transform, false);
                    h.Tick();
                    break;
                }
                case PortalWait.FarEntitiesRestoring:
                    link = h.Door.Open(PortalHarness.Owner, new[] { PortalHarness.Client });
                    h.Tick();
                    h.Plane.FindScope("vault/" + PortalHarness.Owner).State = ScopeState.Restoring;
                    h.Tick();
                    break;
                case PortalWait.ClientWarming:
                    link = h.Door.Open(PortalHarness.Owner, new[] { PortalHarness.Client });
                    h.Ticks(4);
                    break;
                case PortalWait.FarWorkerHandshake:
                    h.HallOnSecondWorker = true;
                    Peer(h, false);
                    link = h.Door.Open(PortalHarness.Owner, new[] { PortalHarness.Client });
                    h.Ticks(3);
                    break;
                case PortalWait.FarContentHeld:
                    link = h.Door.Open(PortalHarness.Owner, new[] { PortalHarness.Client });
                    h.Tick();
                    ReadinessHolds.Hold(h.Hall, "populating");
                    h.Ticks(2);
                    break;
                case PortalWait.BandOccupied:
                    link = h.OpenDoor();
                    h.Player.transform.position = PortalHarness.At(0f, -1f);
                    h.Door.Close(PortalHarness.Owner);
                    h.Ticks(2);
                    break;
                case PortalWait.CloseHold:
                    h.Door.CloseHoldSeconds = 1f;
                    link = h.OpenDoor();
                    h.Player.transform.position = PortalHarness.At(0f, -6f); // out of the band
                    h.Door.Close(PortalHarness.Owner);
                    h.Ticks(2);
                    break;
                case PortalWait.FarScopeRetired:
                    link = h.OpenDoor();
                    h.Plane.FindScope("vault/" + PortalHarness.Owner).State = ScopeState.Retiring;
                    h.Now += 2f;
                    h.Tick();
                    // A closed link is not in the telemetry rows: the reason is on the link and on the record the gateway and the clients are sent.
                    return (link.Wait, link.Wait);
                case PortalWait.NearContainerMoving:
                {
                    link = h.OpenDoor();
                    h.Mesh.SetOwner(h.Street, h.Other);
                    h.Tick();
                    h.Door.TryGetLink(PortalHarness.Owner, out var moved);
                    // The new owner's link waits on it until its first look; the old copy reads closed with it.
                    Assert.That(link.Wait, Is.EqualTo(PortalWait.NearContainerMoving));
                    return (moved.Wait, moved.Wait);
                }
                default:
                    throw new ArgumentException("no situation for " + wait);
            }
            var rows = new List<NebulaWorker.PortalBinding>();
            h.W.Instance.CopyPortalBindings(rows);
            Assert.That(rows.Count, Is.EqualTo(1), "a link that is not closed has a row");
            return (link.Wait, rows[0].Wait);
        }

        [Test]
        public void EveryWaitReasonIsReportedInItsSituationOnTheLinkAndInTheTelemetryRow()
        {
            var seen = new HashSet<PortalWait>();
            foreach (PortalWait wait in Enum.GetValues(typeof(PortalWait)))
            {
                if (wait == PortalWait.None) continue;
                var (onLink, inRow) = Reach(wait);
                Assert.That(onLink, Is.EqualTo(wait), "the link says why in this situation: " + wait);
                Assert.That(inRow, Is.EqualTo(wait), "and so does the telemetry row: " + wait);
                seen.Add(wait);
            }
            Assert.That(seen.Count, Is.EqualTo(Enum.GetValues(typeof(PortalWait)).Length - 1));
        }

        [Test]
        public void AnOpenLinkWaitsOnNothing()
        {
            using var h = new PortalHarness(1);
            var link = h.OpenDoor();
            Assert.That(link.Wait, Is.EqualTo(PortalWait.None));
            var rows = new List<NebulaWorker.PortalBinding>();
            h.W.Instance.CopyPortalBindings(rows);
            Assert.That(rows[0].Wait, Is.EqualTo(PortalWait.None));
            Assert.That(rows[0].State, Is.EqualTo(PortalState.Open));
        }
    }
}
