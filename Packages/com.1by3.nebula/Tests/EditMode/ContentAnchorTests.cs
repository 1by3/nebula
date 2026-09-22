using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// The client's content anchor (design <c>docs/interest-management.md</c> D63): which transform decides the
    /// cells a client keeps loaded and where its floating origin sits. The pawn is the default and a strategy
    /// camera is the reason there is a choice at all, so what is pinned here is the selection itself — the
    /// default, the override, giving it back, and what happens when the object the game handed over is
    /// destroyed under it. <c>NebulaWorldStreaming</c> and <c>NebulaChunkedWorld</c> both read
    /// <see cref="NebulaClient.ActiveContentAnchor"/> and nothing else, so this is the whole decision.
    /// <para>
    /// The pawn half of the fallback is not exercised here: <c>LocalPlayer</c> is set by the gateway's spawn
    /// and there is no way to fake one without a connected mesh. It is covered end to end by the play-mode and
    /// service tests instead.
    /// </para>
    /// </summary>
    public sealed class ContentAnchorTests
    {
        private GameObject host;
        private NebulaClient client;
        private GameObject camera;

        [SetUp]
        public void SetUp()
        {
            host = new GameObject("client");
            client = host.AddComponent<NebulaClient>();
            camera = new GameObject("strategy camera");
            camera.transform.position = new Vector3(1200f, 40f, -300f);
        }

        [TearDown]
        public void TearDown()
        {
            if (camera != null) Object.DestroyImmediate(camera);
            Object.DestroyImmediate(host);
        }

        [Test]
        public void ByDefaultThereIsNoOverrideAndTheAnchorIsThePawn()
        {
            Assert.IsNull(client.ContentAnchor, "a game that has never asked for one has none");
            // No pawn yet either, so there is nothing to anchor to and the streamer falls back to the origin
            // cell. That is the same thing every existing project already did.
            Assert.IsNull(client.ActiveContentAnchor);
        }

        [Test]
        public void AnAnchorTakesPrecedenceOverThePawnAndGivingItBackRestoresIt()
        {
            client.SetContentAnchor(camera.transform);
            Assert.AreSame(camera.transform, client.ContentAnchor);
            Assert.AreSame(camera.transform, client.ActiveContentAnchor, "content follows the camera, not the pawn");

            client.SetContentAnchor(null);
            Assert.IsNull(client.ContentAnchor, "null is how a game gives the pawn its job back");
            Assert.IsNull(client.ActiveContentAnchor);
        }

        [Test]
        public void ADestroyedAnchorIsNotFollowedIntoTheVoid()
        {
            client.SetContentAnchor(camera.transform);
            Object.DestroyImmediate(camera);
            camera = null;
            // A camera destroyed on a scene change must not leave the client anchored to a dead transform: the
            // cells it kept loaded would never be released and the origin would never shift again.
            Assert.IsNull(client.ActiveContentAnchor);
        }

        [Test]
        public void TheAnchorIsAPlaceAndNotTheFocusHint()
        {
            // They are set independently on purpose (D63): the hint is a request to the server about what to be
            // sent, the anchor a local decision about what to keep in memory. Setting one must not move the
            // other, or a game that pings a minimap would quietly stream the ground under the ping.
            client.FocusHint = new Vector3(500f, 0f, 500f);
            Assert.IsNull(client.ContentAnchor, "a focus hint does not re-anchor content");

            client.SetContentAnchor(camera.transform);
            Assert.AreEqual(new Vector3(500f, 0f, 500f), client.FocusHint, "and an anchor does not move the hint");
        }
    }
}
