using System.Collections.Generic;
using NUnit.Framework;

namespace Nebula.Tests
{
    /// <summary>
    /// The game's say in interest (design §7), and the validation that keeps a client's hint an input rather
    /// than a way to ask the gateway for the whole map.
    /// </summary>
    public class InterestPolicyTests
    {
        private sealed class ExtraFocusPolicy : IInterestPolicy
        {
            public void Collect(in InterestClient client, InterestQuery query)
            {
                query.AddFocus(500, 0, 0);
                query.AddEntity(42);
            }
            public bool Authorize(in InterestClient client, in InterestEntity entity) => true;
        }

        private sealed class TeamPolicy : IInterestPolicy
        {
            public void Collect(in InterestClient client, InterestQuery query) => query.AddEntity(7);
            public bool Authorize(in InterestClient client, in InterestEntity entity) => entity.InterestGroup == client.Team;
        }

        private static InterestClient Player(double x = 0, double z = 0, byte team = 0) => new InterestClient
        {
            ClientId = 1, HasPawn = true, PawnNetId = 9, PawnX = x, PawnZ = z, Team = team,
        };

        [Test]
        public void TheDefaultPolicyFocusesOnThePawnAndTheAcceptedHint()
        {
            var query = new InterestQuery();
            query.Reset(InterestSettings.Default);
            var client = Player(10, 20);
            client.HasHint = true; client.HintX = 40; client.HintZ = 20;
            DefaultInterestPolicy.Instance.Collect(client, query);
            Assert.AreEqual(2, query.Foci.Count);
            Assert.AreEqual(10, query.Foci[0].X);
            Assert.AreEqual(9UL, query.Foci[0].SourceNetId);
            Assert.AreEqual(40, query.Foci[1].X);
        }

        [Test]
        public void APawnlessClientGetsNoFocusOfItsOwn()
        {
            var query = new InterestQuery();
            query.Reset(InterestSettings.Default);
            DefaultInterestPolicy.Instance.Collect(new InterestClient { ClientId = 2 }, query);
            Assert.AreEqual(0, query.Foci.Count, "no pawn means global entities only, not everything at a slow rate");
        }

        [Test]
        public void CombiningPoliciesUnionsFociAndAndsAuthorization()
        {
            var combined = InterestPolicies.Combine(DefaultInterestPolicy.Instance, InterestPolicies.Combine(new ExtraFocusPolicy(), new TeamPolicy()));
            var query = new InterestQuery();
            query.Reset(InterestSettings.Default);
            var client = Player(1, 2, team: 3);
            combined.Collect(client, query);
            Assert.AreEqual(2, query.Foci.Count);
            CollectionAssert.AreEqual(new ulong[] { 42, 7 }, new List<ulong>(query.Entities));
            Assert.IsTrue(combined.Authorize(client, new InterestEntity { InterestGroup = 3 }));
            Assert.IsFalse(combined.Authorize(client, new InterestEntity { InterestGroup = 4 }), "one policy refusing is enough");
        }

        [Test]
        public void CombineToleratesNulls()
        {
            Assert.AreSame(DefaultInterestPolicy.Instance, InterestPolicies.Combine(DefaultInterestPolicy.Instance, null));
            Assert.AreSame(DefaultInterestPolicy.Instance, InterestPolicies.Combine(null, DefaultInterestPolicy.Instance));
            Assert.IsNull(InterestPolicies.Combine((IInterestPolicy)null, null));
        }

        [Test]
        public void TheQueryStopsAtItsCaps()
        {
            var settings = InterestSettings.Default;
            settings.MaxFoci = 2;
            settings.MaxExplicitPerClient = 1;
            var query = new InterestQuery();
            query.Reset(settings);
            Assert.IsTrue(query.AddFocus(0, 0, 0));
            Assert.IsTrue(query.AddFocus(1, 0, 0));
            Assert.IsFalse(query.AddFocus(2, 0, 0));
            Assert.IsTrue(query.AddEntity(1));
            Assert.IsTrue(query.AddEntity(1), "the same id twice is not a second subscription");
            Assert.IsFalse(query.AddEntity(2));
            Assert.IsTrue(query.Overflowed);
        }

        [Test]
        public void ANonFiniteFocusIsRefused()
        {
            var query = new InterestQuery();
            query.Reset(InterestSettings.Default);
            Assert.IsFalse(query.AddFocus(double.NaN, 0, 0));
            Assert.IsFalse(query.AddFocus(0, double.PositiveInfinity, 0));
            Assert.AreEqual(0, query.Foci.Count);
        }

        [Test]
        public void ANonFiniteHintIsDropped()
        {
            var filter = new FocusHintFilter();
            Assert.IsFalse(filter.TryAccept(1, 0, double.NaN, 0, 0, FocusMode.PawnClamped, true, 0, 0, 0, out _, out _, out _));
            Assert.IsFalse(filter.TryAccept(1, 1, 0, double.PositiveInfinity, 0, FocusMode.PawnClamped, true, 0, 0, 0, out _, out _, out _));
            Assert.AreEqual(2, filter.DroppedNonFinite);
            Assert.AreEqual(0, filter.Accepted);
        }

        [Test]
        public void HintsAreRateLimitedPerClient()
        {
            var filter = new FocusHintFilter();          // 5 Hz by default
            Assert.IsTrue(filter.TryAccept(1, 0.0, 1, 0, 0, FocusMode.Free, false, 0, 0, 0, out _, out _, out _));
            Assert.IsFalse(filter.TryAccept(1, 0.1, 2, 0, 0, FocusMode.Free, false, 0, 0, 0, out _, out _, out _));
            Assert.IsTrue(filter.TryAccept(2, 0.1, 3, 0, 0, FocusMode.Free, false, 0, 0, 0, out _, out _, out _), "the limit is per client");
            Assert.IsTrue(filter.TryAccept(1, 0.2, 4, 0, 0, FocusMode.Free, false, 0, 0, 0, out _, out _, out _));
            Assert.AreEqual(1, filter.DroppedRate);
            filter.Forget(1);
            Assert.IsTrue(filter.TryAccept(1, 0.21, 5, 0, 0, FocusMode.Free, false, 0, 0, 0, out _, out _, out _));
        }

        [Test]
        public void TheRateLimitCanBeAskedWithoutSpendingIt()
        {
            var filter = new FocusHintFilter();
            Assert.IsFalse(filter.Throttled(1, 0.0), "asking is not accepting");
            Assert.IsTrue(filter.TryAccept(1, 0.0, 1, 0, 0, FocusMode.Free, false, 0, 0, 0, out _, out _, out _));
            Assert.IsTrue(filter.Throttled(1, 0.1));
            Assert.IsFalse(filter.Throttled(1, 0.2));
        }

        [Test]
        public void AFarHintIsClampedToTheDistanceAllowedFromThePawn()
        {
            var filter = new FocusHintFilter();
            filter.Settings.HintMaxDistance = 60;
            Assert.IsTrue(filter.TryAccept(1, 0, 1000, 0, 0, FocusMode.PawnClamped, true, 100, 0, 0, out double x, out double y, out double z));
            Assert.AreEqual(160, x, 1e-6);
            Assert.AreEqual(0, y, 1e-6);
            Assert.AreEqual(0, z, 1e-6);
            Assert.AreEqual(1, filter.Clamped);
        }

        [Test]
        public void ANearHintIsAcceptedAsSent()
        {
            var filter = new FocusHintFilter();
            Assert.IsTrue(filter.TryAccept(1, 0, 110, 0, 20, FocusMode.PawnClamped, true, 100, 0, 0, out double x, out _, out double z));
            Assert.AreEqual(110, x, 1e-6);
            Assert.AreEqual(20, z, 1e-6);
            Assert.AreEqual(0, filter.Clamped);
        }

        [Test]
        public void AServerAuthorizedFreeFocusSkipsTheClamp()
        {
            var filter = new FocusHintFilter();
            Assert.IsTrue(filter.TryAccept(1, 0, 5000, 0, 0, FocusMode.Free, true, 0, 0, 0, out double x, out _, out _));
            Assert.AreEqual(5000, x, 1e-6);
            Assert.AreEqual(0, filter.Clamped);
        }

        [Test]
        public void APawnlessClientsHintIsRefusedUnlessTheServerFreedIt()
        {
            var filter = new FocusHintFilter();
            // No pawn means nothing to clamp to. Accepting the raw point would hand a client that has not even
            // been given a body the pick of the world, which is the cheapest possible map-wide scrape.
            Assert.IsFalse(filter.TryAccept(1, 0, 5000, 0, 0, FocusMode.PawnClamped, false, 0, 0, 0, out double x, out _, out _));
            Assert.AreEqual(0, x, "and nothing comes back that a caller could use by accident");
            Assert.AreEqual(1, filter.DroppedUnauthorized);
            Assert.AreEqual(0, filter.Accepted);

            // The same client, once the server says it may look where it likes: a spectator, a commander view.
            Assert.IsTrue(filter.TryAccept(2, 0, 5000, 0, 0, FocusMode.Free, false, 0, 0, 0, out double free, out _, out _));
            Assert.AreEqual(5000, free, 1e-6);
        }

        [Test]
        public void AClientWhoseHintsAreDisabledIsIgnoredEvenWithAPawn()
        {
            var filter = new FocusHintFilter();
            Assert.IsFalse(filter.TryAccept(1, 0, 10, 0, 0, FocusMode.Disabled, true, 0, 0, 0, out _, out _, out _));
            Assert.AreEqual(1, filter.DroppedUnauthorized);
            Assert.IsFalse(filter.Throttled(1, 0.0), "a refused hint does not spend the client's rate budget either");
        }

        private sealed class HintPolicy : IInterestPolicy, IFocusHintPolicy
        {
            public FocusMode Grant = FocusMode.PawnClamped;
            public double? SnapX;
            public InterestClient Seen;

            public void Collect(in InterestClient client, InterestQuery query) { }
            public bool Authorize(in InterestClient client, in InterestEntity entity) => true;

            public FocusHintDecision AuthorizeFocusHint(in InterestClient client, in FocusHintDecision request)
            {
                Seen = client;
                var decision = request;
                decision.Mode = Grant;
                if (SnapX != null) decision.X = SnapX.Value;
                return decision;
            }
        }

        [Test]
        public void AFocusHintPolicySeesTheServersModeAndMayChangeItAndThePoint()
        {
            var policy = new HintPolicy { Grant = FocusMode.Free, SnapX = 250 };
            var client = Player(10, 20);
            client.FocusMode = FocusMode.PawnClamped;
            var decision = ((IFocusHintPolicy)policy).AuthorizeFocusHint(client, new FocusHintDecision { Mode = client.FocusMode, X = 900 });
            Assert.AreEqual(FocusMode.PawnClamped, policy.Seen.FocusMode, "the policy is told what the server had decided");
            Assert.AreEqual(FocusMode.Free, decision.Mode);
            Assert.AreEqual(250, decision.X, "and may move the point, not only judge it");
        }

        [Test]
        public void CombinedPoliciesChainTheirFocusHintDecisions()
        {
            var first = new HintPolicy { Grant = FocusMode.Free };
            var second = new HintPolicy { Grant = FocusMode.Disabled };
            var combined = InterestPolicies.Combine(first, second) as IFocusHintPolicy;
            Assert.IsNotNull(combined);
            var client = Player();
            Assert.AreEqual(FocusMode.Disabled, combined.AuthorizeFocusHint(client, FocusHintDecision.NearPawn(1, 2, 3)).Mode,
                "the later policy is asked about what the earlier one decided");

            // A policy that does not care about hints leaves the decision alone rather than erasing it.
            var passive = InterestPolicies.Combine(first, DefaultInterestPolicy.Instance) as IFocusHintPolicy;
            Assert.AreEqual(FocusMode.Free, passive.AuthorizeFocusHint(client, FocusHintDecision.Reject()).Mode);
        }

        [Test]
        public void FreeHintIsTheFocusModeAndNothingElse()
        {
            var client = Player();
            Assert.IsFalse(client.FreeHint);
            client.FocusMode = FocusMode.Free;
            Assert.IsTrue(client.FreeHint);
        }
    }
}
