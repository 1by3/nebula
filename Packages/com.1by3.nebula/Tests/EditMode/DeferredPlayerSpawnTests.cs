using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    public class DeferredPlayerSpawnTests
    {
        private GameObject host, pawn;
        private NebulaWorker worker;
        private IDictionary pending;
        private object ticket;
        private int created;
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        [SetUp] public void Setup()
        {
            created = 0;
            host = new GameObject("deferred-spawn-test");
            worker = host.AddComponent<NebulaWorker>();
            pending = (IDictionary)typeof(NebulaWorker).GetField("_pendingPlayerSpawns", Flags).GetValue(worker);
            worker.Sessions.Register(7, 1, "gateway");
            ticket = new object(); pending[7UL] = ticket;
        }
        [TearDown] public void Cleanup()
        {
            if (host != null) Object.DestroyImmediate(host);
            if (pawn != null) Object.DestroyImmediate(pawn);
        }
        private NetworkIdentity Create()
        {
            created++;
            pawn = new GameObject("loaded-player");
            var identity = pawn.AddComponent<NetworkIdentity>();
            identity.OwnerClientId = 7;
            identity.InvokeSpawn(); // This fixture supplies an already-spawned result.
            return identity;
        }
        private void Complete(object token)
        {
            typeof(NebulaWorker).GetMethod("CompletePlayerSpawn", Flags).Invoke(worker,
                new object[] { 7UL, token, null, (Func<NetworkIdentity>)Create });
        }
        [Test] public void CurrentCompletionCreatesExactlyOnePlayer()
        {
            Assert.AreEqual(0, created);
            Complete(ticket); Complete(ticket);
            Assert.AreEqual(1, created);
        }
        [Test] public void CancelledJoinNeverInvokesPlayerFactory()
        {
            pending.Remove(7UL);
            Complete(ticket);
            Assert.AreEqual(0, created);
        }
        [Test] public void OlderJoinCannotCompleteAfterReplacement()
        {
            var replacement = new object(); pending[7UL] = replacement;
            Complete(ticket);
            Assert.AreEqual(0, created);
            Complete(replacement);
            Assert.AreEqual(1, created);
        }
        [Test] public void DisconnectedSessionCannotFinishLoading()
        {
            worker.Sessions.Release(7, 1, 0);
            Complete(ticket);
            Assert.AreEqual(0, created);
        }
    }
}
