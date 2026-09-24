using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// A client of the Multiplayer Play Mode dev loop joins only the server its own session started, on ports of its
    /// own, and refuses with the server's reason when that server could not bind (NEB-320).
    /// </summary>
    public sealed class EditorDevSessionTests
    {
        private static EditorRunPlan Plan(bool isMainEditor) =>
            EditorRunPlan.Resolve(NebulaEditorRunMode.MultiplayerPlayMode, true, true, isMainEditor, null, false);

        [Test]
        public void BothSidesMoveOffTheUsualPortsTogether()
        {
            var server = ScriptableObject.CreateInstance<NebulaConfig>();
            var client = ScriptableObject.CreateInstance<NebulaConfig>();
            try
            {
                Plan(isMainEditor: false).ApplyTo(server, _ => false, "C:/Dev/game");
                Plan(isMainEditor: true).ApplyTo(client, _ => false, "C:/Dev/game");
                Assert.AreEqual(7050, server.GatewayPort);
                Assert.AreEqual(server.GatewayPort, client.GatewayPort, "the client looks where the server listens");
                Assert.AreEqual(7150, server.WorkerBasePort);
                Assert.AreEqual(7130, server.DashboardPort);
                Assert.AreEqual(7100, client.WorkerBasePort, "a client runs no worker");
            }
            finally
            {
                Object.DestroyImmediate(server);
                Object.DestroyImmediate(client);
            }
        }

        [Test]
        public void AnOffsetOfZeroAndExplicitPortsKeepTheConfiguredPorts()
        {
            var config = ScriptableObject.CreateInstance<NebulaConfig>();
            try
            {
                config.EditorPortOffset = 0;
                config.DashboardPort = 0;
                Plan(isMainEditor: false).ApplyTo(config, _ => false, "C:/Dev/game");
                Assert.AreEqual(7000, config.GatewayPort);
                Assert.AreEqual(0, config.DashboardPort, "a disabled dashboard stays disabled");

                config.EditorPortOffset = 50;
                config.GatewayPort = 7600;
                Plan(isMainEditor: true).ApplyTo(config, key => key == "nebula-gateway", "C:/Dev/game");
                Assert.AreEqual(7600, config.GatewayPort, "-nebula-gateway wins");
            }
            finally { Object.DestroyImmediate(config); }
        }

        [Test]
        public void AClientWaitsForItsOwnServer()
        {
            Assert.AreEqual(EditorDevSession.Verdict.Waiting, EditorDevSession.Evaluate(null, _ => true));
            var listening = new EditorDevSession.Record { state = EditorDevSession.ListeningState, port = 7050, pid = 42 };
            Assert.AreEqual(EditorDevSession.Verdict.Ready, EditorDevSession.Evaluate(listening, pid => pid == 42));
            Assert.AreEqual(EditorDevSession.Verdict.Waiting, EditorDevSession.Evaluate(listening, _ => false), "a record whose server is gone is stale");
        }

        [Test]
        public void AServerThatCouldNotBindIsReportedNotReplaced()
        {
            var failed = new EditorDevSession.Record { state = EditorDevSession.FailedState, port = 7050, pid = 42, error = NebulaGateway.BindFailure(7050) };
            Assert.AreEqual(EditorDevSession.Verdict.Failed, EditorDevSession.Evaluate(failed, _ => true));
            string message = EditorDevSession.FailureMessage(failed);
            StringAssert.Contains("udp/7050", message);
            StringAssert.Contains("nebula stop", message);
            StringAssert.Contains("EditorPortOffset", message);
        }

        [Test]
        public void TheRecordRoundTripsAndOnlyItsWriterRemovesIt()
        {
            string root = Path.Combine(Path.GetTempPath(), "nebula-dev-session-" + System.Guid.NewGuid().ToString("N"));
            string path = EditorDevSession.PathFor(root);
            try
            {
                int pid = EditorDevSession.CurrentPid;
                EditorDevSession.Write(path, new EditorDevSession.Record { state = EditorDevSession.ListeningState, port = 7050, pid = pid, incarnation = "0000abcd" });
                var read = EditorDevSession.Read(path);
                Assert.AreEqual(7050, read.port);
                Assert.AreEqual("0000abcd", read.incarnation);
                Assert.AreEqual(EditorDevSession.Verdict.Ready, EditorDevSession.Evaluate(read, EditorDevSession.IsAlive));

                EditorDevSession.Remove(path, pid + 1);
                Assert.IsTrue(File.Exists(path), "another process's record is left alone");
                EditorDevSession.Remove(path, pid);
                Assert.IsFalse(File.Exists(path));
                Assert.IsNull(EditorDevSession.Read(path));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }
    }
}
