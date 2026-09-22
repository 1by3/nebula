using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
#if NEBULA_SERVICE
using Nebula.ServicePrimitives;
#else
using UnityEngine;
using Object = UnityEngine.Object;
#endif

namespace Nebula.Tests
{
    /// <summary>
    /// Conformance scenario 14 (<c>docs/conformance-suite.md</c>, design <c>docs/scope-frames.md</c>), the two halves
    /// that are pure C#: <b>the scaler's dry run consolidates two low-load scopes onto one worker</b>, and the region
    /// keys that make two scopes' interest disjoint. Tier A — <see cref="CostBalancedAssignmentPolicy"/>,
    /// <see cref="AssignmentPlanner"/>, <see cref="WorkerScaler"/> and <see cref="RegionKeys"/> are all pure, so the
    /// same file runs in the Unity Editor and under <c>dotnet test</c>. The worker half of the scenario — one worker
    /// holding two scopes whose local coordinates overlap, each shifting its own origin — needs real containers and
    /// entities and is <c>ConformanceScopeFrameWorkerTests.cs</c> (tier B).
    /// <para>
    /// Containers are built for the policy directly rather than through the registry, exactly as
    /// <see cref="ConformanceCohesionTests"/> does: the policy reads the lists it is handed, and the two builds
    /// construct a <see cref="Container"/> differently.
    /// </para>
    /// </summary>
    [Category("Conformance")]
    public class ConformanceScopeFrameTests
    {
        private const float Span = 64f;

        private readonly List<Container> _containers = new List<Container>();
#if !NEBULA_SERVICE
        private readonly List<GameObject> _objects = new List<GameObject>();
#endif

        [TearDown]
        public void TearDown()
        {
            _containers.Clear();
#if !NEBULA_SERVICE
            foreach (var go in _objects) if (go != null) Object.DestroyImmediate(go);
            _objects.Clear();
#endif
        }

        /// <summary>
        /// <paramref name="scopes"/> worlds of <paramref name="perScope"/> chunks each. Every world's chunks stand on
        /// exactly the same ground — that is the whole point of per-scope frames — so the boxes repeat and only the
        /// container id and the scope blob tell them apart.
        /// </summary>
        private List<Container> Worlds(int scopes, int perScope)
        {
            _containers.Clear();
            for (int s = 0; s < scopes; s++)
            {
                string scopeKey = "world/w" + s;
                for (int i = 0; i < perScope; i++)
                {
                    var instance = new InstanceContainerInfo
                    {
                        InstanceId = ScopeKeys.Hash(scopeKey),
                        ScopeKey = scopeKey,
                        PartId = "c/" + i + "/0/0",
                    };
                    _containers.Add(Chunk(ScopeKeys.ContainerId(scopeKey, instance.PartId), (ushort)_containers.Count,
                        new Vector3((i + 0.5f) * Span, 0f, 0f), instance));
                }
            }
            return _containers;
        }

        private Container Chunk(string id, ushort index, Vector3 position, InstanceContainerInfo instance)
        {
#if NEBULA_SERVICE
            return new Container
            {
                ContainerId = id,
                Index = index,
                Size = new Vector3(Span, Span, Span),
                transform = new ContainerFrame { position = position },
                Instance = instance,
            };
#else
            var go = new GameObject(id);
            _objects.Add(go);
            go.transform.position = position;
            var c = go.AddComponent<Container>();
            c.ContainerId = id;
            c.Size = new Vector3(Span, Span, Span);
            c.Center = Vector3.zero;
            c.Instance = instance;
            return c;
#endif
        }

        private static AssignmentInput Input(IReadOnlyList<Container> containers, float perContainer) => new AssignmentInput
        {
            Baked = containers,
            Runtime = new List<Container>(),
            Eligible = new List<WorkerInfo>(),
            Leases = new List<LeaseInfo>(),
            Occupancy = new Dictionary<string, ContainerLoad>(),
            Utilization = containers.ToDictionary(c => c.ContainerId, _ => perContainer),
        };

        private static ScaleSettings Settings() => new ScaleSettings
        {
            ScaleOutUtilization = 0.7f,
            ScaleInUtilization = 0.3f,
            HoldSeconds = 30f,
            MinGain = 0.1f,
            MinWorkers = 1,
            MaxWorkers = 4,
        };

        // ------------------------------------------------------------------ the scaler's dry run (the exit criterion)

        [Test]
        public void TheDryRunPutsTwoLowLoadScopesOnOneWorker()
        {
            var containers = Worlds(scopes: 2, perScope: 4);
            var plan = new CostBalancedAssignmentPolicy().Predict(Input(containers, 0.05f), 1);

            Assert.AreEqual(0, plan.Unassigned, "every chunk of both worlds was placed");
            var only = plan.Containers[AssignmentPlanner.SyntheticWorker(0)];
            Assert.AreEqual(8, only.Count, "one worker carries both scopes; scope count never forces worker count");
            Assert.IsTrue(containers.Take(4).All(c => only.Contains(c.ContainerId)), "the first world is there");
            Assert.IsTrue(containers.Skip(4).All(c => only.Contains(c.ContainerId)), "and so is the second");
            Assert.AreEqual(0.4f, plan.Peak, 1e-3f, "the predicted peak is the load, not the number of worlds");
        }

        [Test]
        public void TheScalerConsolidatesTwoScopesOntoOneWorker()
        {
            var input = Input(Worlds(scopes: 2, perScope: 4), 0.05f);
            // Two machines, each a fifth busy: well under the scale-in line, and the dry run says one can hold it all.
            var utilization = new Dictionary<string, float> { ["w1"] = 0.2f, ["w2"] = 0.2f };
            var policy = new CostBalancedAssignmentPolicy();
            var scaler = new WorkerScaler();

            Assert.AreEqual(ScaleAction.None, scaler.Evaluate(0.0, utilization, input, policy, 2, Settings(), false).Action,
                "the shrink is held first, like any other");
            var decision = scaler.Evaluate(31.0, utilization, input, policy, 2, Settings(), false);

            Assert.AreEqual(ScaleAction.Shrink, decision.Action,
                "two sparsely populated scopes are one worker's worth of work, whatever their coordinates are");
            Assert.IsNotEmpty(decision.RetireWorkerId);
        }

        [Test]
        public void NothingInThePolicyKeysOnHowManyScopesThereAre()
        {
            var policy = new CostBalancedAssignmentPolicy();
            // The same total load and the same boxes, once as one world of twelve chunks and once as twelve worlds
            // of one chunk each. A rule that separated scopes by frame would need twelve workers for the second.
            var one = policy.Predict(Input(Worlds(scopes: 1, perScope: 12), 0.05f), 1);
            var many = policy.Predict(Input(Worlds(scopes: 12, perScope: 1), 0.05f), 1);

            Assert.AreEqual(one.Unassigned, many.Unassigned);
            Assert.AreEqual(one.Peak, many.Peak, 1e-4f);
            Assert.AreEqual(one.Containers[AssignmentPlanner.SyntheticWorker(0)].Count,
                many.Containers[AssignmentPlanner.SyntheticWorker(0)].Count,
                "twelve worlds cost exactly what twelve chunks of one world cost");
        }

        // ------------------------------------------------------------------ scope-salted region ids

        [Test]
        public void ThePublicWorldsRegionIdsAreExactlyWhatTheyWere()
        {
            ulong region = InterestGrid.PackRegion(3, 0, -7);
            Assert.AreEqual(0UL, RegionKeys.SaltOf(0UL), "the public world has no salt");
            Assert.AreEqual(region, RegionKeys.Salt(region, 0UL), "so an unscoped mesh puts the same bytes on the wire");
            Assert.AreEqual(region, RegionKeys.Unsalt(region, 0UL));
        }

        [Test]
        public void TwoScopesAtOneCoordinateAreTwoRegions()
        {
            ulong region = InterestGrid.PackRegion(3, 0, -7);
            ulong alpha = ScopeKeys.Hash("world/alpha"), beta = ScopeKeys.Hash("world/beta");

            Assert.AreNotEqual(RegionKeys.Salt(region, alpha), RegionKeys.Salt(region, beta),
                "the same ground in two worlds is two keys, so a subscription to one never matches the other");
            Assert.AreNotEqual(region, RegionKeys.Salt(region, alpha), "and neither of them is the public world's");
        }

        [Test]
        public void SaltingIsInvertibleSoTheRegionArithmeticStillWorks()
        {
            ulong alpha = ScopeKeys.Hash("world/alpha");
            var grid = InterestGrid.Resolve(InterestSettings.Default);
            ulong region = grid.RegionOf(200, 0, -500);
            ulong key = RegionKeys.Salt(region, alpha);

            Assert.AreEqual(region, RegionKeys.Unsalt(key, alpha), "the holder knows its own scope, so it can unpack");
            grid.BoundsOf(RegionKeys.Unsalt(key, alpha), out double minX, out _, out double minZ, out double maxX, out _, out double maxZ);
            Assert.LessOrEqual(minX, 200.0);
            Assert.GreaterOrEqual(maxX, 200.0);
            Assert.LessOrEqual(minZ, -500.0);
            Assert.GreaterOrEqual(maxZ, -500.0);
        }
    }
}
