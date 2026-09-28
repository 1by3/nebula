using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Nebula;
using NUnit.Framework;

namespace Nebula.Services.Tests;

/// <summary>
/// The reason a real LiteNetLib link reports when it closes, over loopback (NEB-351): the server closing it, this
/// side closing it, nobody answering, and an encrypted handshake nobody completes.
/// </summary>
public class TransportDisconnectReasonTests
{
    private static int FreePort()
    {
        using var reserve = new UdpClient(0);
        int port = ((IPEndPoint)reserve.Client.LocalEndPoint!).Port;
        reserve.Close();
        return port;
    }

    /// <summary>Poll both ends until <paramref name="done"/> holds or the time runs out.</summary>
    private static void Pump(ITransport? server, ITransport client, List<TransportEvent> serverEvents, List<TransportEvent> clientEvents, Func<bool> done, double seconds = 10)
    {
        var clock = Stopwatch.StartNew();
        while (!done() && clock.Elapsed.TotalSeconds < seconds)
        {
            server?.Poll(e => serverEvents.Add(new TransportEvent(e.Type, e.PeerId, default, e.Reason)));
            client.Poll(e => clientEvents.Add(new TransportEvent(e.Type, e.PeerId, default, e.Reason)));
            Thread.Sleep(5);
        }
    }

    private static TransportEvent? Find(List<TransportEvent> events, TransportEvent.Kind kind)
    {
        foreach (var e in events) if (e.Type == kind) return e;
        return null;
    }

    [Test]
    public void AServerThatClosesTheLinkIsReportedAsClosedByTheRemote()
    {
        using var server = new LiteNetTransport("test-server");
        server.Listen(0);
        using var client = new LiteNetTransport("test-client");
        client.StartClient();
        var serverEvents = new List<TransportEvent>();
        var clientEvents = new List<TransportEvent>();
        int peer = client.Connect("127.0.0.1", server.LocalPort);
        Pump(server, client, serverEvents, clientEvents, () => Find(serverEvents, TransportEvent.Kind.Connected) != null && Find(clientEvents, TransportEvent.Kind.Connected) != null);
        var accepted = Find(serverEvents, TransportEvent.Kind.Connected);
        Assert.That(accepted, Is.Not.Null, "the link came up");

        server.Disconnect(accepted!.Value.PeerId);
        Pump(server, client, serverEvents, clientEvents, () => Find(clientEvents, TransportEvent.Kind.Disconnected) != null && Find(serverEvents, TransportEvent.Kind.Disconnected) != null);

        var closed = Find(clientEvents, TransportEvent.Kind.Disconnected);
        Assert.That(closed, Is.Not.Null);
        Assert.That(closed!.Value.PeerId, Is.EqualTo(peer));
        Assert.That(closed.Value.Reason, Is.EqualTo(TransportDisconnectReason.ClosedByRemote));
        Assert.That(Find(serverEvents, TransportEvent.Kind.Disconnected)!.Value.Reason, Is.EqualTo(TransportDisconnectReason.LocalRequest));
    }

    [Test]
    public void AHostThatNeverAnswersIsReportedAsUnreachable()
    {
        using var client = new LiteNetTransport("test-client");
        client.StartClient();
        var clientEvents = new List<TransportEvent>();
        client.Connect("127.0.0.1", FreePort());
        // LiteNetLib gives up after its connect attempts (about 5 s), or at once when the OS reports the port closed.
        Pump(null, client, new List<TransportEvent>(), clientEvents, () => Find(clientEvents, TransportEvent.Kind.Disconnected) != null, seconds: 15);

        var closed = Find(clientEvents, TransportEvent.Kind.Disconnected);
        Assert.That(closed, Is.Not.Null, "the attempt ended");
        Assert.That(closed!.Value.Reason, Is.EqualTo(TransportDisconnectReason.HostUnreachable));
        Assert.That(DisconnectInfo.FromTransport(closed.Value.Reason, wasConnected: false), Is.EqualTo(DisconnectReason.HostUnreachable));
    }

    [Test]
    public void ALinkThatStopsAnsweringTimesOutAfterTheConfiguredTimeout()
    {
        using var server = new LiteNetTransport("test-server", disconnectTimeoutMs: 600, pingIntervalMs: 100);
        server.Listen(0);
        using var client = new LiteNetTransport("test-client", disconnectTimeoutMs: 600, pingIntervalMs: 100);
        client.StartClient();
        var serverEvents = new List<TransportEvent>();
        var clientEvents = new List<TransportEvent>();
        client.Connect("127.0.0.1", server.LocalPort);
        Pump(server, client, serverEvents, clientEvents, () => Find(clientEvents, TransportEvent.Kind.Connected) != null && Find(serverEvents, TransportEvent.Kind.Connected) != null);
        Assert.That(Find(clientEvents, TransportEvent.Kind.Connected), Is.Not.Null, "the link came up");

        // The server vanishes without a word, as a crashed process would: no disconnect message is sent.
        var net = (LiteNetLib.NetManager)typeof(LiteNetTransport).GetField("_net", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(server)!;
        net.Stop(sendDisconnectMessages: false);
        var clock = Stopwatch.StartNew();
        Pump(null, client, serverEvents, clientEvents, () => Find(clientEvents, TransportEvent.Kind.Disconnected) != null);

        var closed = Find(clientEvents, TransportEvent.Kind.Disconnected);
        Assert.That(closed, Is.Not.Null);
        Assert.That(closed!.Value.Reason, Is.EqualTo(TransportDisconnectReason.Timeout));
        Assert.That(clock.Elapsed.TotalSeconds, Is.LessThan(4), "the 600 ms timeout applies, not the 8 s default");
    }

    [Test]
    public void AnEncryptedHandshakeNobodyCompletesIsASecurityFailure()
    {
        using var server = new LiteNetTransport("test-server");
        server.Listen(0);
        using var client = EncryptedTransport.ForClient(new LiteNetTransport("test-client"), new ClientEncryption { TimeoutSeconds = 0.5f });
        client.StartClient();
        var serverEvents = new List<TransportEvent>();
        var clientEvents = new List<TransportEvent>();
        int peer = client.Connect("127.0.0.1", server.LocalPort);
        // A plaintext server accepts the link and ignores the key exchange, so the client gives up on it.
        Pump(server, client, serverEvents, clientEvents, () => Find(clientEvents, TransportEvent.Kind.Disconnected) != null);

        var closed = Find(clientEvents, TransportEvent.Kind.Disconnected);
        Assert.That(closed, Is.Not.Null);
        Assert.That(closed!.Value.PeerId, Is.EqualTo(peer));
        Assert.That(closed.Value.Reason, Is.EqualTo(TransportDisconnectReason.SecurityFailure));
    }
}
