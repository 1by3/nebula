using System.Text.Json;
using Nebula;
using Nebula.ServicePrimitives;
using NUnit.Framework;

namespace Nebula.ServiceTests;

/// <summary>
/// The deployment compatibility policy (NEB-228, <c>docs/compatibility-policy.md</c>): <b>a gateway admits a
/// client anywhere in its protocol window, refuses anything outside it with a code the client can act on, records
/// the negotiated version on the session, and keeps speaking to a client of the older version.</b> Tier A: the
/// real <see cref="NebulaGateway"/> on a real socket over an in-process <see cref="LocalControlPlane"/>, with a
/// <see cref="FakeWorker"/> to be spawned into.
/// <para>
/// The replay half is the part that cannot be faked: <c>Fixtures/protocol-18-handshake.json</c> holds the exact
/// bytes a client sent and a gateway answered, recorded from these fixtures, and the test pushes those bytes at a
/// gateway built from today's source. Protocol 18 is both ends of the window today
/// (<see cref="HelloMsg.MinProtocolVersion"/> == <see cref="HelloMsg.ProtocolVersion"/>), so the recording is an
/// N recording; when the protocol is bumped to 19 the same file becomes the N-1 recording and the same test
/// becomes the N-1 test, with nothing to change but the constants.
/// </para>
/// </summary>
[TestFixture]
[Category("Conformance")]
public class ConformanceProtocolCompatibilityTests
{
    private string directory = "";

    [SetUp]
    public void Setup()
    {
        directory = Path.Combine(Path.GetTempPath(), "nebula-compat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        CommandLine.Override(new Dictionary<string, string>());
        var path = Path.Combine(directory, "nebula-services.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new ServiceManifest
        {
            Containers = new() { new Container { ContainerId = "c0", Index = 0, Size = new(64, 64, 64), transform = new ContainerFrame { position = new(32, 0, 0) } } },
        }, ServiceManifest.Json));
        ServiceManifest.Load(path);
    }

    [TearDown] public void Cleanup() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    // ------------------------------------------------------------------------------------ the window itself

    /// <summary>
    /// The window is exactly one version wide, or closed. This is the test that has to be looked at when the
    /// protocol is bumped: raise <see cref="HelloMsg.ProtocolVersion"/> to the new number and set
    /// <see cref="HelloMsg.MinProtocolVersion"/> to the previous one (so gateways of the new build keep admitting
    /// clients of the build being replaced), then record a fresh fixture with
    /// <see cref="RecordTheHandshakeFixture"/>. Never widen the window past one version: the additive rule
    /// (docs/compatibility-policy.md D3) is only worth auditing across one step.
    /// </summary>
    [Test]
    public void TheWindowIsOneVersionWideAtMost()
    {
        Assert.That(HelloMsg.MinProtocolVersion, Is.LessThanOrEqualTo(HelloMsg.ProtocolVersion),
            "the minimum protocol cannot be newer than the one this build speaks");
        Assert.That(HelloMsg.ProtocolVersion - HelloMsg.MinProtocolVersion, Is.LessThanOrEqualTo(1),
            "the compatibility window is N and N-1, never wider: see docs/compatibility-policy.md D1");
        Assert.That(ProtocolCompatibility.ClientProtocolAccepted(HelloMsg.ProtocolVersion), Is.True);
        Assert.That(ProtocolCompatibility.ClientProtocolAccepted(HelloMsg.MinProtocolVersion), Is.True);
        Assert.That(ProtocolCompatibility.ClientProtocolAccepted((ushort)(HelloMsg.MinProtocolVersion - 1)), Is.False);
        Assert.That(ProtocolCompatibility.ClientProtocolAccepted((ushort)(HelloMsg.ProtocolVersion + 1)), Is.False);
    }

    /// <summary>
    /// A peer announces its own version now. Before the policy, <c>HelloMsg.Write</c> always wrote the constant,
    /// which is why no window could exist (docs/scale-suite.md D8).
    /// </summary>
    [Test]
    public void AHelloAnnouncesTheSendersOwnVersionAndItsContentVersion()
    {
        var w = new NetworkWriter();
        new HelloMsg { Role = PeerRole.Client, Id = "p", Version = 17, GameContentVersion = 42 }.Write(w);
        var r = new NetworkReader(w.ToSegment());
        Assert.That((MsgId)r.ReadByte(), Is.EqualTo(MsgId.Hello));
        var read = HelloMsg.Read(r);
        Assert.That(read.Version, Is.EqualTo(17), "a peer must be able to announce a version that is not this build's");
        Assert.That(read.GameContentVersion, Is.EqualTo(42u));

        var unset = new NetworkWriter();
        new HelloMsg { Role = PeerRole.Client, Id = "p" }.Write(unset);
        var reader = new NetworkReader(unset.ToSegment());
        reader.ReadByte();
        Assert.That(HelloMsg.Read(reader).Version, Is.EqualTo(HelloMsg.ProtocolVersion), "an unset version is this build's");
    }

    /// <summary>A Hello from before the content-version field ends after the scope key and means "no content version".</summary>
    [Test]
    public void AHelloWithoutTheContentVersionFieldIsReadAsZero()
    {
        var w = new NetworkWriter();
        w.WriteByte((byte)MsgId.Hello);
        w.WriteUShort(HelloMsg.ProtocolVersion);
        w.WriteByte((byte)PeerRole.Client);
        w.WriteString("player");
        w.WriteUInt(0);
        w.WriteByte(0);
        w.WriteString("");
        w.WriteString("");
        w.WriteUInt(0);
        w.WriteString("");
        var r = new NetworkReader(w.ToSegment());
        r.ReadByte();
        var hello = HelloMsg.Read(r);
        Assert.That(hello.GameContentVersion, Is.EqualTo(0u));
        Assert.That(ProtocolCompatibility.ContentVersionAccepted(hello.GameContentVersion, 0, 0), Is.True,
            "a gateway that declares no content version must not start refusing clients that never sent one");
    }

    /// <summary>The content-version window: exact by default, a range when a minimum is configured.</summary>
    [Test]
    public void TheContentVersionWindowIsExactUnlessAMinimumIsConfigured()
    {
        Assert.That(ProtocolCompatibility.ContentVersionAccepted(7, 0, 0), Is.True, "a gateway with no content version checks nothing");
        Assert.That(ProtocolCompatibility.ContentVersionAccepted(7, 7, 0), Is.True);
        Assert.That(ProtocolCompatibility.ContentVersionAccepted(6, 7, 0), Is.False, "the default is an exact match");
        Assert.That(ProtocolCompatibility.ContentVersionAccepted(8, 7, 0), Is.False, "a newer client than the server is refused too");
        Assert.That(ProtocolCompatibility.ContentVersionAccepted(5, 7, 5), Is.True);
        Assert.That(ProtocolCompatibility.ContentVersionAccepted(4, 7, 5), Is.False);
    }

    // ------------------------------------------------------------------------ the gateway's refusals

    /// <summary>
    /// A client outside the window is refused with <see cref="JoinRejectReason.ProtocolUnsupported"/> and the
    /// gateway's range, which is what lets a game say "update required" rather than "something went wrong". The
    /// refusal is a message, not a silent disconnect: before the policy the gateway just dropped the link.
    /// </summary>
    [Test]
    public void AClientOutsideTheWindowIsRefusedWithTheGatewaysRange()
    {
        using var world = new ScaleWorld(1);
        using var fleet = new Fleet(gateways: 1);
        foreach (ushort version in new[] { (ushort)(HelloMsg.MinProtocolVersion - 1), (ushort)(HelloMsg.ProtocolVersion + 1) })
        {
            var client = fleet.Connect(0, "other-build-" + version);
            client.AnnounceVersion = version;
            Assert.That(fleet.Run(() => client.Rejected != null, seconds: 15), Is.True, $"protocol {version} was not refused");
            var rejected = client.Rejected!.Value;
            Assert.That(rejected.Code, Is.EqualTo(JoinRejectReason.ProtocolUnsupported));
            Assert.That(rejected.Retry, Is.False, "retrying the same build would get the same answer");
            Assert.That(rejected.SupportedMinVersion, Is.EqualTo(HelloMsg.MinProtocolVersion));
            Assert.That(rejected.SupportedMaxVersion, Is.EqualTo(HelloMsg.ProtocolVersion));
            Assert.That(client.Welcome, Is.Null);
            Assert.That(fleet.Worker.Claims, Is.Empty, "a refused client never reaches a worker");
        }
    }

    /// <summary>
    /// The game's own content version: the gateway compares the numbers and refuses with a distinct code, and
    /// says which number it runs so the client can tell the player what to fetch. Nebula does not interpret it.
    /// </summary>
    [Test]
    public void AContentVersionOutsideTheGatewaysWindowIsRefusedWithItsOwnCode()
    {
        using var world = new ScaleWorld(1);
        using var fleet = new Fleet(gateways: 1, configure: c => { c.GameContentVersion = 7; c.MinGameContentVersion = 0; });
        fleet.Worker.SpawnIntoRequestedContainer = true;

        var stale = fleet.Connect(0, "stale-content");
        stale.ContentVersion = 6;
        Assert.That(fleet.Run(() => stale.Rejected != null, seconds: 15), Is.True);
        Assert.That(stale.Rejected!.Value.Code, Is.EqualTo(JoinRejectReason.ContentVersionMismatch));
        Assert.That(stale.Rejected!.Value.ServerContentVersion, Is.EqualTo(7u), "the client is told which content the server runs");
        Assert.That(stale.Rejected!.Value.Retry, Is.False);

        var matching = fleet.Connect(0, "matching-content");
        matching.ContentVersion = 7;
        Assert.That(fleet.Run(() => matching.Join == JoinState.Joined, seconds: 20), Is.True, "the right content version joins");
        Assert.That(matching.Rejected, Is.Null);
    }

    // ---------------------------------------------------------------------------- the recorded replay

    /// <summary>
    /// <b>The conformance scenario.</b> A recorded client stream is pushed, byte for byte, at a gateway built
    /// from today's source: it is welcomed, the gateway records the version it announced and echoes it back as
    /// the negotiated version, and the client goes on to join and receive its pawn. Then the recorded gateway
    /// stream is parsed with today's readers, which is the other half of the additive rule — a field removed from
    /// a message, or reordered, would throw or come back wrong here.
    /// </summary>
    [Test]
    public void ARecordedClientStreamIsAdmittedAndAnsweredByAGatewayOfThisBuild()
    {
        var fixture = ProtocolFixture.Load(ProtocolFixture.PathFor(HelloMsg.MinProtocolVersion));
        Assert.That(ProtocolCompatibility.ClientProtocolAccepted(fixture.ProtocolVersion), Is.True,
            $"the checked-in recording is protocol {fixture.ProtocolVersion}, outside this build's window " +
            $"{ProtocolCompatibility.WindowText()}; record a fresh one with RecordTheHandshakeFixture");

        using var world = new ScaleWorld(1);
        using var fleet = new Fleet(gateways: 1);
        fleet.Worker.SpawnIntoRequestedContainer = true;

        var replayed = fleet.Connect(0, "recorded");
        replayed.Replay = fixture.ClientFrames();
        Assert.That(fleet.Run(() => replayed.Welcome != null, seconds: 20), Is.True, "the recorded handshake was not welcomed");
        Assert.That(replayed.Rejected, Is.Null);
        Assert.That(replayed.Welcome!.Value.NegotiatedVersion, Is.EqualTo(fixture.ProtocolVersion),
            "the gateway must record the version it negotiated and tell the client which one it was");
        Assert.That(fleet.Run(() => replayed.Join == JoinState.Joined, seconds: 20), Is.True, "the recorded client did not reach the world");
        Assert.That(fleet.Run(() => replayed.Replicas.Count > 0, seconds: 20), Is.True, "it was sent no entities");
        Assert.That(replayed.LastError, Is.Empty, "this build sent the recorded client something it could not parse: " + replayed.LastError);

        // The recorded gateway stream, read by this build. A removed or reordered field shows up here.
        int welcomes = 0, frames = 0;
        foreach (var frame in fixture.GatewayFrames())
        {
            frames++;
            var r = new NetworkReader(new ArraySegment<byte>(frame));
            while (r.Remaining > 0)
            {
                var id = (MsgId)r.ReadByte();
                if (id == MsgId.Welcome) { welcomes++; WelcomeMsg.Read(r); }
                else if (id == MsgId.Batch) { int n = r.ReadUShort(); for (int i = 0; i < n; i++) r.ReadSegment(r.ReadUShort()); }
                else break; // the rest of the stream is content this scenario does not re-decode
            }
        }
        Assert.That(frames, Is.GreaterThan(0), "the recording has no gateway frames");
        Assert.That(welcomes, Is.EqualTo(1), "the recorded gateway stream no longer parses as one welcome");
    }

    /// <summary>
    /// Regenerate <c>Fixtures/protocol-{N}-handshake.json</c> from a live handshake on this build. Explicit: it
    /// is a tool, run once per protocol bump, not a test. Run it with
    /// <c>dotnet test --filter FullyQualifiedName~RecordTheHandshakeFixture</c> and commit what it writes.
    /// </summary>
    [Test]
    [Explicit("Regenerates the checked-in protocol fixture; run it after a protocol bump and commit the file.")]
    public void RecordTheHandshakeFixture()
    {
        using var world = new ScaleWorld(1);
        using var fleet = new Fleet(gateways: 1);
        fleet.Worker.SpawnIntoRequestedContainer = true;

        var client = fleet.Connect(0, "recorded");
        client.Record = new List<(bool, byte[])>();
        Assert.That(fleet.Run(() => client.Join == JoinState.Joined && client.Replicas.Count > 0, seconds: 30), Is.True);
        client.SendInput();
        client.SendFocusHint(new Vector3(32, 0, 0));
        fleet.RunFor(1.0);

        string path = ProtocolFixture.PathFor(HelloMsg.ProtocolVersion);
        ProtocolFixture.FromRecording(HelloMsg.ProtocolVersion, 0,
            $"A client handshake, join and snapshot stream at protocol {HelloMsg.ProtocolVersion}, recorded from " +
            "Fixtures/MeshFixtures.cs by ConformanceProtocolCompatibilityTests.RecordTheHandshakeFixture.",
            client.Record).Save(path);
        TestContext.Out.WriteLine($"wrote {path} ({client.Record.Count} frames)");
    }
}
