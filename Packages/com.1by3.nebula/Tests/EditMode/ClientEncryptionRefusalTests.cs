using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    public class ClientEncryptionRefusalTests
    {
        [Test]
        public void EncryptionRefusalPreservesSavedIdentityAndStopsRetrying()
        {
            const string key = "nebula.identityToken";
            bool hadSavedToken = PlayerPrefs.HasKey(key);
            string previousToken = PlayerPrefs.GetString(key, "");
            var gameObject = new GameObject("Encryption refusal test");
            try
            {
                PlayerPrefs.SetString(key, "saved-test-identity");
                var client = gameObject.AddComponent<NebulaClient>();
                const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
                typeof(NebulaClient).GetField("_storedToken", hidden).SetValue(client, "saved-test-identity");
                typeof(NebulaClient).GetField("_presentedStoredToken", hidden).SetValue(client, true);
                typeof(NebulaClient).GetProperty("WantsConnection").SetValue(client, true);
                var writer = new NetworkWriter();
                new JoinRejectedMsg { Code = JoinRejectReason.EncryptionRequired, Reason = "encryption required", Retry = false }.Write(writer);
                typeof(NebulaClient).GetMethod("Dispatch", hidden).Invoke(client, new object[] { new NetworkReader(writer.ToSegment()) });
                Assert.That(client.WantsConnection, Is.False);
                Assert.That(client.LastError, Does.Contain("encryption required"));
                Assert.That(PlayerPrefs.GetString(key), Is.EqualTo("saved-test-identity"));
                Assert.That(typeof(NebulaClient).GetField("_storedToken", hidden).GetValue(client), Is.EqualTo("saved-test-identity"));
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
                if (hadSavedToken) PlayerPrefs.SetString(key, previousToken);
                else PlayerPrefs.DeleteKey(key);
                PlayerPrefs.Save();
            }
        }
    }
}
