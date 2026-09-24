using System.Reflection;
using Nebula.World;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// The orchestrator starts from an empty control plane (<c>-nebula-reset</c>, on by default), but in a
    /// single-process run the worker shares the plane and writes to it straight after the orchestrator
    /// initializes: the game activates its scopes from <c>OnWorkerStarted</c>. The reset must not wipe those rows
    /// at the orchestrator's first tick, or every join waits for a scope that never becomes ready (NEB-316).
    /// </summary>
    public sealed class OrchestratorResetTests
    {
        private const string Scope = "instance/reset-test";

        private GameObject _go;
        private NebulaConfig _config;
        private LocalControlPlane _plane;

        [SetUp]
        public void SetUp()
        {
            _config = ScriptableObject.CreateInstance<NebulaConfig>();
            _config.UseLocalControlPlane = true;
            _config.DashboardPort = 0;
            _config.WorkerCount = 1;
            _config.AutoScale = false;
            _plane = new LocalControlPlane();
            _plane.Connect();
            _go = new GameObject("orchestrator");
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_go);
            Object.DestroyImmediate(_config);
            _plane.Dispose();
        }

        private void Activate() => _plane.ActivateScope(new ScopeActivationRequest
        {
            ScopeKey = Scope,
            Definition = new ChunkGridDefinition { CellSize = new Vector3(64f, 64f, 64f), Planar = true }.ToScopeDefinition(),
        });

        private static void Tick(NebulaOrchestrator orchestrator) =>
            typeof(NebulaOrchestrator).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(orchestrator, null);

        [Test]
        public void ScopeActivatedBeforeTheFirstTickSurvivesIt()
        {
            var orchestrator = _go.AddComponent<NebulaOrchestrator>();
            orchestrator.Initialize(_config, _plane);
            // The in-process worker initializes next and the game activates a scope from OnWorkerStarted.
            Activate();
            Assert.IsNotNull(_plane.FindScope(Scope));

            Tick(orchestrator);

            Assert.IsNotNull(_plane.FindScope(Scope), "the orchestrator's reset wiped a scope its own worker had activated");
        }

        [Test]
        public void StateFromBeforeTheOrchestratorIsStillReset()
        {
            Activate();
            string document = _plane.DocumentId;

            var orchestrator = _go.AddComponent<NebulaOrchestrator>();
            orchestrator.Initialize(_config, _plane);
            Tick(orchestrator);

            Assert.IsNull(_plane.FindScope(Scope), "a stale scope from before the orchestrator started should be reset");
            Assert.AreNotEqual(document, _plane.DocumentId);
        }
    }
}
