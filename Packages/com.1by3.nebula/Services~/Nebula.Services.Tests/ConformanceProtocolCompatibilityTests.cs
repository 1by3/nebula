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
/// The replay half is the part that cannot be faked: <c>Fixtures/protocol-28-handshake.json</c> holds the exact
/// bytes a client sent and a gateway answered, recorded from these fixtures, and the test pushes those bytes at a
/// gateway built from today's source. The window is 32..33: protocol 33 (client requests) is additive and moved the minimum to N-1, 32; protocols 29 to 32 were additive; protocol 28 (portals, <c>docs/portals.md</c> D46) closed the window, because
/// the meaning of its messages is not additive even though their bytes are, so a client of 27 is refused, and the protocol-27
/// recording proves it as the protocol-18 one proved the step from 18. This build replays the protocol-32 recording for N-1, and the protocol-33 recording is the one the next additive bump replays. The protocol-30 recording
/// proves that a client that fell out of the window is refused with the gateway's range. The step from 18
/// was not additive (it renumbered carrier behaviours), so the protocol-18 recording is kept to prove that such a client is
/// refused with the gateway's range rather than admitted.
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
    /// clients of the build being replaced; a bump that is not additive sets it to the new number, as 28 did), then record a fresh fixture with
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
    /// stream and the newly generated replies are parsed by an independent, frozen protocol-32 decoder.
    /// This covers public-container handshake, spawn and transform framing, not every game message or payload.
    /// </summary>
    [Test]
    public void ARecordedClientStreamIsAdmittedAndAnsweredByAGatewayOfThisBuild()
    {
        var fixture = ProtocolFixture.Load(ProtocolFixture.PathFor(32));
        Assert.That(ProtocolCompatibility.ClientProtocolAccepted(fixture.ProtocolVersion), Is.True,
            $"the checked-in recording is protocol {fixture.ProtocolVersion}, outside this build's window " +
            $"{ProtocolCompatibility.WindowText()}; record a fresh one with RecordTheHandshakeFixture");

        using var world = new ScaleWorld(1);
        using var fleet = new Fleet(gateways: 1);
        fleet.Worker.SpawnIntoRequestedContainer = true;

        var replayed = fleet.Connect(0, "recorded");
        replayed.Replay = fixture.ClientFrames();
        replayed.Record = new List<(bool, byte[])>();
        Assert.That(fleet.Run(() => replayed.Welcome != null, seconds: 20), Is.True, "the recorded handshake was not welcomed");
        Assert.That(replayed.Rejected, Is.Null);
        Assert.That(replayed.Welcome!.Value.NegotiatedVersion, Is.EqualTo(fixture.ProtocolVersion),
            "the gateway must record the version it negotiated and tell the client which one it was");
        Assert.That(fleet.Run(() => replayed.Join == JoinState.Joined, seconds: 20), Is.True, "the recorded client did not reach the world");
        Assert.That(fleet.Run(() => replayed.Replicas.Count > 0, seconds: 20), Is.True, "it was sent no entities");
        Assert.That(replayed.LastError, Is.Empty, "this build sent the recorded client something it could not parse: " + replayed.LastError);
        fleet.Worker.Move(1000, new Vector3(1.25f, 2.5f, 3.75f));
        fleet.OnPump = () => fleet.Worker.PublishStates();
        Assert.That(fleet.Run(() => replayed.StatesReceived > 0, seconds: 10), Is.True);

        void AssertHandshake(Protocol32GatewayDecoder decoded)
        {
            Assert.That(decoded.Welcomes, Is.EqualTo(1));
            Assert.That(decoded.Joined, Is.True);
            Assert.That(decoded.NegotiatedVersion, Is.EqualTo(32));
            Assert.That(decoded.Containers, Does.Contain("c0"));
            Assert.That(decoded.Spawns, Contains.Key(1000ul));
            Assert.That(decoded.Spawns[1000], Is.EqualTo((decoded.ClientId, decoded.Identity)),
                "the frozen reader must recover the pawn's owner and identity, not merely consume its bytes");
        }

        var recorded = new Protocol32GatewayDecoder();
        foreach (var frame in fixture.GatewayFrames()) recorded.Read(frame);
        AssertHandshake(recorded);

        var current = new Protocol32GatewayDecoder();
        foreach (var (outbound, bytes) in replayed.Record)
            if (!outbound) current.Read(bytes);
        AssertHandshake(current);
        Assert.That(current.StateEntities, Does.Contain(1000ul), "new gateway transforms must decode with the frozen protocol-32 layout");
        Assert.That(current.UninspectedMessageIds, Does.Not.Contain((byte)54).And.Not.Contain((byte)55).And.Not.Contain((byte)56), "protocol 33's client request messages are never sent to a client of 32");
        Assert.That(current.Positions[1000], Is.EqualTo((1.25f, 2.5f, 3.75f)),
            "the frozen reader must recover distinct position axes from the new gateway's snapshot");
    }

    /// <summary>
    /// Protocol 28 closed the window (<c>docs/portals.md</c> D46): the bytes of its new messages are additive, but a client of 27 would be
    /// sent replicas of scopes it believes hidden, would never draw them posed, and would predict its pawn into an aperture it does not know is
    /// solid. The recorded protocol-27 client is refused before it reaches a worker, with the range the gateway accepts.
    /// </summary>
    [Test]
    public void ARecordedProtocol27ClientIsRefusedWithTheGatewaysRange()
    {
        var fixture = ProtocolFixture.Load(ProtocolFixture.PathFor(27));
        Assert.That(fixture.ProtocolVersion, Is.EqualTo((ushort)27));
        Assert.That(ProtocolCompatibility.ClientProtocolAccepted(fixture.ProtocolVersion), Is.False);
        Assert.That(HelloMsg.MinProtocolVersion, Is.EqualTo((ushort)32), "protocol 28 closed the window; 33 moved it to N-1, 32");
        Assert.That(ProtocolCompatibility.ClientProtocolAccepted(28), Is.False, "a client of 28 is outside the window of 33 too");
        Assert.That(ProtocolCompatibility.ClientProtocolAccepted(29), Is.False, "and so is a client of 29");
        Assert.That(ProtocolCompatibility.ClientProtocolAccepted(31), Is.False, "and 31, which fell out when 33 moved the window");

        using var world = new ScaleWorld(1);
        using var fleet = new Fleet(gateways: 1);
        fleet.Worker.SpawnIntoRequestedContainer = true;
        var replayed = fleet.Connect(0, "recorded-27");
        replayed.Replay = fixture.ClientFrames();
        Assert.That(fleet.Run(() => replayed.Rejected != null, seconds: 20), Is.True, "a protocol-27 client was not refused");
        var rejected = replayed.Rejected!.Value;
        Assert.That(rejected.Code, Is.EqualTo(JoinRejectReason.ProtocolUnsupported));
        Assert.That(rejected.SupportedMinVersion, Is.EqualTo(HelloMsg.MinProtocolVersion));
        Assert.That(rejected.SupportedMaxVersion, Is.EqualTo(HelloMsg.ProtocolVersion));
        Assert.That(replayed.Welcome, Is.Null);
        Assert.That(fleet.Worker.Claims, Is.Empty, "a refused client never reaches a worker");
    }

    /// <summary>
    /// A client of 31 fell out of the window when 33 moved it (N and N-1, <c>docs/compatibility-policy.md</c> D1). Nothing it
    /// reads changed, but the additive rule is only audited across one step. The recorded protocol-31 client is refused before it
    /// reaches a worker, with the range the gateway accepts.
    /// </summary>
    [Test]
    public void ARecordedProtocol31ClientIsRefusedWithTheGatewaysRange()
    {
        var fixture = ProtocolFixture.Load(ProtocolFixture.PathFor(31));
        Assert.That(fixture.ProtocolVersion, Is.EqualTo((ushort)31));
        Assert.That(ProtocolCompatibility.ClientProtocolAccepted(fixture.ProtocolVersion), Is.False);

        using var world = new ScaleWorld(1);
        using var fleet = new Fleet(gateways: 1);
        fleet.Worker.SpawnIntoRequestedContainer = true;
        var replayed = fleet.Connect(0, "recorded-31");
        replayed.Replay = fixture.ClientFrames();
        Assert.That(fleet.Run(() => replayed.Rejected != null, seconds: 20), Is.True, "a protocol-31 client was not refused");
        var rejected = replayed.Rejected!.Value;
        Assert.That(rejected.Code, Is.EqualTo(JoinRejectReason.ProtocolUnsupported));
        Assert.That(rejected.Retry, Is.False);
        Assert.That(rejected.SupportedMinVersion, Is.EqualTo((ushort)32));
        Assert.That(rejected.SupportedMaxVersion, Is.EqualTo((ushort)33));
        Assert.That(replayed.Welcome, Is.Null);
        Assert.That(fleet.Worker.Claims, Is.Empty, "a refused client never reaches a worker");
    }

    /// <summary>
    /// Protocol 33 (client requests, <c>docs/client-requests.md</c>) is additive: three new messages and no change to any
    /// existing one. A client of 32 (N-1) is sent exactly the bytes it was: far entries with their class, slot and compact
    /// poses, NEB-484's dropped-tick range, and none of messages 54 to 56, even while a client of 33 sends a request and is
    /// answered. Every frame the older client was sent goes through the frozen protocol-32 reader, which fails on trailing
    /// bytes after a far entry, a far pose or an owner state.
    /// </summary>
    [Test]
    public void AProtocol32ClientIsSentBytesItCanRead()
    {
        using var world = new ScaleWorld(1);
        using var fleet = new Fleet(gateways: 1, configure: c =>
        {
            c.InterestFarClasses = new[] { new FarClass { Id = 3, Name = "crowd", MaxEntities = 8, UpdateRate = 4f, Radius = 2000f, Upright = true } };
        });
        fleet.Worker.SpawnIntoRequestedContainer = true;
        var old = fleet.Connect(0, "protocol-32");
        old.AnnounceVersion = 32;
        old.Record = new List<(bool, byte[])>();
        var current = fleet.Connect(0, "protocol-33");
        current.AnnounceVersion = HelloMsg.ClientRequestVersion;
        Assert.That(fleet.Run(() => old.Join == JoinState.Joined && current.Join == JoinState.Joined
            && fleet.Worker.Pawns.ContainsKey(old.Welcome!.Value.ClientId) && fleet.Worker.Pawns.ContainsKey(current.Welcome!.Value.ClientId), seconds: 20), Is.True);
        Assert.That(old.Welcome!.Value.NegotiatedVersion, Is.EqualTo((ushort)32));
        Assert.That(current.Welcome!.Value.NegotiatedVersion, Is.EqualTo(HelloMsg.ClientRequestVersion));

        const ulong Walker = 0x7_0000_0001, Leaver = 0x7_0000_0002;
        var walker = fleet.Worker.Spawn(Walker, new Vector3(300, 0, 0));
        walker.FarClass = 3;
        fleet.Worker.Spawn(Leaver, new Vector3(100, 0, 0)).FarClass = 3;
        double start = -1;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        fleet.OnPump = () =>
        {
            double t = clock.Elapsed.TotalSeconds;
            if (start < 0) start = t;
            fleet.Worker.Move(Walker, new Vector3(300f + (float)(t - start) * 1.5f, 0, 0));
            fleet.Worker.PublishStates();
            fleet.Worker.PublishFar(t);
        };
        Assert.That(fleet.Run(() => old.Far.ContainsKey(Walker) && current.Far.ContainsKey(Walker) && current.FarPosesReceived >= 3 && old.FarPosesReceived >= 3
            && old.Replicas.Contains(Leaver) && current.Replicas.Contains(Leaver), seconds: 15), Is.True,
            $"both hear the walker: old {old.FarPosesReceived} compact poses, current {current.FarPosesReceived}");

        // A client of 33 asks for something it can see and is answered; the client of 32 hears nothing of it.
        current.SendClientRequest(Leaver, callId: 1);
        Assert.That(fleet.Run(() => fleet.Worker.ClientRequests.Count == 1, seconds: 10), Is.True, "the gateway routed the request to the worker");
        fleet.Worker.ReplyToClientRequest(fleet.Worker.ClientRequests[0], ClientRequestOutcome.Accepted, 7);
        Assert.That(fleet.Run(() => current.RequestReplies.Count == 1, seconds: 10), Is.True);
        Assert.That(current.RequestReplies[0].Code, Is.EqualTo((ushort)7));

        // A hand-over: the replica leaves both sets, and both hear of its marker first.
        fleet.Worker.Move(Leaver, new Vector3(300, 0, 0));
        Assert.That(fleet.Run(() => !old.Replicas.Contains(Leaver) && old.Far.ContainsKey(Leaver) && !current.Replicas.Contains(Leaver) && current.Far.ContainsKey(Leaver), seconds: 10), Is.True);
        Assert.That(old.Wire.IndexOf("far-handover " + Leaver), Is.GreaterThanOrEqualTo(0).And.LessThan(old.Wire.IndexOf("despawn " + Leaver)));

        // NEB-484 is the older client's own: it is sent the range, and it reads to the end of the message.
        ulong pawn = fleet.Worker.Pawns[old.Welcome.Value.ClientId];
        fleet.Worker.SendOwnerState(pawn, old.Welcome.Value.ClientId, 50, droppedFrom: 40, droppedTo: 44);
        Assert.That(fleet.Run(() => old.OwnerStates.Count > 0, seconds: 10), Is.True);
        Assert.That(old.OwnerStates[0].DroppedTo, Is.EqualTo(44u));

        Assert.That(old.LastError, Is.Empty, "a client of 32 was sent something it could not parse: " + old.LastError);
        Assert.That(current.LastError, Is.Empty, current.LastError);
        Assert.That(old.RequestReplies, Is.Empty, "a client of 32 is never sent a request reply");
        Assert.That(old.Far[Walker].FarClass, Is.EqualTo((byte)3), "its far entries keep their class");
        Assert.That(current.Far[Walker].X, Is.EqualTo(old.Far[Walker].X).Within(1.6), "both see it in the same place (the compact pose is 1 cm steps)");

        var frozen = new Protocol32GatewayDecoder();
        foreach (var (outbound, bytes) in old.Record!)
            if (!outbound) frozen.Read(bytes);
        Assert.That(frozen.NegotiatedVersion, Is.EqualTo(32));
        Assert.That(frozen.FarEntries.Keys, Does.Contain(Walker).And.Contain(Leaver), "its far entries read as protocol-32 entries");
        Assert.That(frozen.FarPosesRead, Is.GreaterThan(0), "and its compact poses as protocol-32 poses");
        Assert.That(frozen.DroppedRanges[pawn], Is.EqualTo((40u, 44u)), "main's trailing range reads as main wrote it");
        Assert.That(frozen.UninspectedMessageIds, Does.Not.Contain((byte)54).And.Not.Contain((byte)55).And.Not.Contain((byte)56), "no client request messages");
    }

    /// <summary>
    /// A mesh that runs at another tick rate than 60 (docs/tick-rate.md) admits only clients of <see cref="HelloMsg.TickRateVersion"/>
    /// and later, which run at the rate the welcome carries: a client of 29 would count every tick at 60 Hz. It is refused with the
    /// narrowed window; a current client is welcomed with the mesh's rate.
    /// </summary>
    [Test]
    public void AMeshAt30HzRefusesAProtocol29ClientAndWelcomesACurrentOneWithItsRate()
    {
        NetworkTime.Configure(30);
        try
        {
            using var world = new ScaleWorld(1);
            using var fleet = new Fleet(gateways: 1);
            fleet.Worker.SpawnIntoRequestedContainer = true;
            var old = fleet.Connect(0, "protocol-29-at-30hz");
            old.AnnounceVersion = 29;
            var current = fleet.Connect(0, "current-at-30hz");
            Assert.That(fleet.Run(() => old.Rejected != null && current.Welcome != null, seconds: 20), Is.True);
            Assert.That(old.Rejected!.Value.Code, Is.EqualTo(JoinRejectReason.ProtocolUnsupported));
            Assert.That(old.Rejected!.Value.SupportedMinVersion, Is.EqualTo(ProtocolCompatibility.MinClientProtocol(30)));
            Assert.That(old.Welcome, Is.Null);
            Assert.That(current.Welcome!.Value.TickRate, Is.EqualTo((byte)30));
        }
        finally { NetworkTime.Configure(NetworkTime.DefaultTickRate); }
    }

    /// <summary>
    /// Protocol 19 was not additive: removing the obsolete DynamicContainer behaviour renumbered every carrier prefab's
    /// behaviours, so a protocol-18 client would address RPCs, variables and sync state to the wrong behaviour. The
    /// recorded protocol-18 client is refused before it reaches a worker, with the range the gateway accepts.
    /// </summary>
    [Test]
    public void ARecordedProtocol18ClientIsRefusedWithTheGatewaysRange()
    {
        var fixture = ProtocolFixture.Load(ProtocolFixture.PathFor(18));
        Assert.That(fixture.ProtocolVersion, Is.EqualTo((ushort)18));
        Assert.That(ProtocolCompatibility.ClientProtocolAccepted(fixture.ProtocolVersion), Is.False);

        using var world = new ScaleWorld(1);
        using var fleet = new Fleet(gateways: 1);
        fleet.Worker.SpawnIntoRequestedContainer = true;
        var replayed = fleet.Connect(0, "recorded-18");
        replayed.Replay = fixture.ClientFrames();
        Assert.That(fleet.Run(() => replayed.Rejected != null, seconds: 20), Is.True, "a protocol-18 client was not refused");
        var rejected = replayed.Rejected!.Value;
        Assert.That(rejected.Code, Is.EqualTo(JoinRejectReason.ProtocolUnsupported));
        Assert.That(rejected.SupportedMinVersion, Is.EqualTo(HelloMsg.MinProtocolVersion));
        Assert.That(rejected.SupportedMaxVersion, Is.EqualTo(HelloMsg.ProtocolVersion));
        Assert.That(replayed.Welcome, Is.Null);
        Assert.That(fleet.Worker.Claims, Is.Empty, "a refused client never reaches a worker");
    }

    /// <summary>The oracle itself: a far entry of protocol 32 reads in full, a flag bit of a later protocol does not pass, and trailing bytes after a far entry or an owner state are not skipped.</summary>
    [Test]
    public void FrozenProtocol32DecoderRejectsWhatAClientOf32CannotRead()
    {
        var entry = new FarEntityEntry { NetId = 7, Epoch = 1, Kind = FarEntryKind.State, PrefabId = 3, Radius = 500f, UpdateRate = 2f, X = 1.5, Rotation = Quaternion.identity, FarClass = 3, Slot = 9 };
        byte[] Far()
        {
            var w = new NetworkWriter();
            FarEntitiesMsg.Write(w, new[] { entry }, 0, 1, 32);
            return w.ToSegment().ToArray();
        }
        var decoded = new Protocol32GatewayDecoder();
        decoded.Read(Far());
        Assert.That(decoded.FarEntries[7].X, Is.EqualTo(1.5), "the entry a gateway writes for a client of 32");
        Assert.That(decoded.FarSlots[7], Is.EqualTo((ushort)9));
        Assert.Throws<InvalidDataException>(() => new Protocol32GatewayDecoder().Read(Far().Concat(new byte[] { 0 }).ToArray()));
        var unknownFlag = Far();
        unknownFlag[1 + 2 + 8 + 4 + 1] |= 64; // a flag bit no client of 32 reads: id, count, net id, epoch, kind, then the flags byte
        Assert.Throws<InvalidDataException>(() => new Protocol32GatewayDecoder().Read(unknownFlag));

        var owner = new NetworkWriter();
        new OwnerStateMsg { NetId = 7, Epoch = 1, Tick = 9, LastInputTick = 9, OwnerClientId = 2, State = new byte[] { 1 }, DroppedFrom = 3, DroppedTo = 5 }.Write(owner, true, true);
        var bytes = owner.ToSegment().ToArray();
        decoded.Read(bytes);
        Assert.That(decoded.DroppedRanges[7], Is.EqualTo((3u, 5u)));
        Assert.Throws<InvalidDataException>(() => new Protocol32GatewayDecoder().Read(bytes.Concat(new byte[] { 0 }).ToArray()));
    }

    [Test]
    public void FrozenProtocol18DecoderRejectsTruncatedWelcomeAndMalformedBatchedSnapshots()
    {
        var welcome = ProtocolFixture.Load(ProtocolFixture.PathFor(18)).GatewayFrames().Single(f => f[0] == 2);
        Assert.Throws<EndOfStreamException>(() => new Protocol18GatewayDecoder().Read(welcome[..^1]));

        // Independent protocol-18 bytes: one position snapshot for entity 1000 inside a reliable batch.
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)18); writer.Write((ushort)1); writer.Write((ushort)37);
        writer.Write((byte)14); writer.Write(1u); writer.Write((ushort)1); writer.Write((ushort)1);
        writer.Write(1000ul); writer.Write(1u); writer.Write((ushort)0); writer.Write((ushort)7);
        writer.Write(1f); writer.Write(2f); writer.Write(3f);
        var valid = stream.ToArray();
        var decoded = new Protocol18GatewayDecoder();
        decoded.Read(valid);
        Assert.That(decoded.StateEntities, Does.Contain(1000ul));
        Assert.That(decoded.Positions[1000], Is.EqualTo((1f, 2f, 3f)));

        // Change the inner entry count while retaining a well-formed outer batch envelope.
        var corrupt = (byte[])valid.Clone();
        corrupt[12] = 2;
        Assert.Throws<EndOfStreamException>(() => new Protocol18GatewayDecoder().Read(corrupt));
        Assert.Throws<EndOfStreamException>(() => new Protocol18GatewayDecoder().Read(valid[..^1]));
    }

    [Test]
    public void FrozenProtocol18DecoderIgnoresSafeAdditiveFieldsAndMessageIds()
    {
        var welcome = ProtocolFixture.Load(ProtocolFixture.PathFor(18)).GatewayFrames().Single(f => f[0] == 2);
        var decoded = new Protocol18GatewayDecoder();
        decoded.Read(welcome.Concat(new byte[] { 42, 0, 0, 0 }).ToArray());
        Assert.That(decoded.Welcomes, Is.EqualTo(1));
        Assert.That(decoded.NegotiatedVersion, Is.EqualTo(18));
        decoded.Read(new byte[] { 250, 1, 2, 3 });
        decoded.Read(new byte[] { 18, 1, 0, 4, 0, 250, 1, 2, 3 });
        Assert.That(decoded.UninspectedMessageIds, Does.Contain((byte)250));
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
