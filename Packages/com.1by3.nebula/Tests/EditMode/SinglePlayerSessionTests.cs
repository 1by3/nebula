using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>
    /// One pawn per player on a worker (<see cref="NebulaConfig.SingleSessionPerPlayer"/>). A gateway that sees a
    /// player already in the world claims that player's session again, so a worker only ever hears two sessions for
    /// one identity when two connections were welcomed at the same moment by different gateways. The newer claim
    /// wins and the older session loses its pawn.
    /// </summary>
    public class SinglePlayerSessionTests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private GameObject host;
        private NebulaWorker worker;
        private NebulaConfig config;
        private IDictionary<ulong, string> identities;

        [SetUp] public void Setup()
        {
            host = new GameObject("single-session-test");
            worker = host.AddComponent<NebulaWorker>();
            config = ScriptableObject.CreateInstance<NebulaConfig>();
            typeof(NebulaWorker).GetProperty("Config").GetSetMethod(true).Invoke(worker, new object[] { config });
            identities = (IDictionary<ulong, string>)typeof(NebulaWorker).GetField("_playerIdentities", Flags).GetValue(worker);
        }

        [TearDown] public void Cleanup()
        {
            if (host != null) Object.DestroyImmediate(host);
            if (config != null) Object.DestroyImmediate(config);
        }

        /// <summary>The claim the worker is about to serve; false means it lost to a newer connection of the same player.</summary>
        private bool Resolve(ulong clientId, string identity, ulong generation)
        {
            identities[clientId] = identity;
            return (bool)typeof(NebulaWorker).GetMethod("ResolveDuplicatePlayer", Flags)
                .Invoke(worker, new object[] { null, clientId, identity, generation });
        }

        [Test] public void TheNewerOfTwoSessionsForOnePlayerKeepsThePlayer()
        {
            worker.Sessions.Register(1, 100, "gw1#1");
            Assert.That(Resolve(1, "player-a", 100), Is.True);

            worker.Sessions.Register(2, 200, "gw2#1");
            Assert.That(Resolve(2, "player-a", 200), Is.True, "the newer connection is served");
            Assert.That(worker.Sessions.TryGet(1, out _), Is.False, "the older session is over");
            Assert.That(identities.ContainsKey(1), Is.False);
            Assert.That(worker.Sessions.TryGet(2, out _), Is.True);
        }

        [Test] public void AnOlderClaimForAPlayerAlreadyHeldIsIgnored()
        {
            worker.Sessions.Register(2, 200, "gw2#1");
            Assert.That(Resolve(2, "player-a", 200), Is.True);

            // A claim from a gateway whose connection is older than the one holding the player: it arrived late and
            // must not take the pawn from the newer connection.
            worker.Sessions.Register(1, 100, "gw1#1");
            Assert.That(Resolve(1, "player-a", 100), Is.False);
            Assert.That(worker.Sessions.TryGet(1, out _), Is.False, "the losing claim leaves no session behind");
            Assert.That(identities.ContainsKey(1), Is.False);
            Assert.That(worker.Sessions.TryGet(2, out _), Is.True, "the connection that holds the player is untouched");
        }

        [Test] public void DifferentPlayersAndAnonymousClaimsAreLeftAlone()
        {
            worker.Sessions.Register(1, 100, "gw1#1");
            Assert.That(Resolve(1, "player-a", 100), Is.True);
            worker.Sessions.Register(2, 200, "gw1#1");
            Assert.That(Resolve(2, "player-b", 200), Is.True);
            Assert.That(worker.Sessions.TryGet(1, out _), Is.True, "another player is not a duplicate");

            // No identity to compare: nothing to deduplicate by.
            worker.Sessions.Register(3, 300, "gw1#1");
            Assert.That(Resolve(3, "", 300), Is.True);
            Assert.That(worker.Sessions.Count, Is.EqualTo(3));
        }

        [Test] public void TurningTheRuleOffLetsOnePlayerHoldSeveralSessions()
        {
            config.SingleSessionPerPlayer = false;
            worker.Sessions.Register(1, 100, "gw1#1");
            Assert.That(Resolve(1, "player-a", 100), Is.True);
            worker.Sessions.Register(2, 200, "gw2#1");
            Assert.That(Resolve(2, "player-a", 200), Is.True);
            Assert.That(worker.Sessions.TryGet(1, out _), Is.True, "a bot fleet may share one account on purpose");
            Assert.That(worker.Sessions.TryGet(2, out _), Is.True);
        }
    }
}
