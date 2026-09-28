using NUnit.Framework;

namespace Nebula.Tests
{
    /// <summary>
    /// The mapping from what a transport knows about a closed link to the reason a game sees (NEB-351). Pure C#:
    /// compiled into the package's EditMode tests and into Nebula.Services.Tests.
    /// </summary>
    public class DisconnectReasonTests
    {
        [TestCase(LiteNetLib.DisconnectReason.Timeout, TransportDisconnectReason.Timeout)]
        [TestCase(LiteNetLib.DisconnectReason.NetworkUnreachable, TransportDisconnectReason.NetworkUnreachable)]
        [TestCase(LiteNetLib.DisconnectReason.HostUnreachable, TransportDisconnectReason.HostUnreachable)]
        [TestCase(LiteNetLib.DisconnectReason.ConnectionFailed, TransportDisconnectReason.HostUnreachable)]
        [TestCase(LiteNetLib.DisconnectReason.UnknownHost, TransportDisconnectReason.HostUnreachable)]
        [TestCase(LiteNetLib.DisconnectReason.RemoteConnectionClose, TransportDisconnectReason.ClosedByRemote)]
        [TestCase(LiteNetLib.DisconnectReason.Reconnect, TransportDisconnectReason.ClosedByRemote)]
        [TestCase(LiteNetLib.DisconnectReason.PeerNotFound, TransportDisconnectReason.ClosedByRemote)]
        [TestCase(LiteNetLib.DisconnectReason.DisconnectPeerCalled, TransportDisconnectReason.LocalRequest)]
        [TestCase(LiteNetLib.DisconnectReason.ConnectionRejected, TransportDisconnectReason.Rejected)]
        [TestCase(LiteNetLib.DisconnectReason.InvalidProtocol, TransportDisconnectReason.Rejected)]
        [TestCase(LiteNetLib.DisconnectReason.PeerToPeerConnection, TransportDisconnectReason.Unknown)]
        public void LiteNetLibReasonsMapToTransportReasons(LiteNetLib.DisconnectReason liteNet, TransportDisconnectReason expected)
        {
            Assert.That(LiteNetTransport.ReasonOf(liteNet), Is.EqualTo(expected));
        }

        [Test]
        public void AFailedConnectOnADownNetworkIsTheNetwork()
        {
            Assert.That(LiteNetTransport.ReasonOf(LiteNetLib.DisconnectReason.ConnectionFailed, System.Net.Sockets.SocketError.NetworkUnreachable),
                Is.EqualTo(TransportDisconnectReason.NetworkUnreachable));
            Assert.That(LiteNetTransport.ReasonOf(LiteNetLib.DisconnectReason.HostUnreachable, System.Net.Sockets.SocketError.NetworkDown),
                Is.EqualTo(TransportDisconnectReason.NetworkUnreachable));
        }

        [TestCase(TransportDisconnectReason.Timeout, true, DisconnectReason.Timeout)]
        [TestCase(TransportDisconnectReason.Timeout, false, DisconnectReason.HostUnreachable)]
        [TestCase(TransportDisconnectReason.NetworkUnreachable, true, DisconnectReason.NetworkUnreachable)]
        [TestCase(TransportDisconnectReason.NetworkUnreachable, false, DisconnectReason.NetworkUnreachable)]
        [TestCase(TransportDisconnectReason.HostUnreachable, false, DisconnectReason.HostUnreachable)]
        [TestCase(TransportDisconnectReason.Rejected, false, DisconnectReason.HostUnreachable)]
        [TestCase(TransportDisconnectReason.ClosedByRemote, true, DisconnectReason.ClosedByServer)]
        [TestCase(TransportDisconnectReason.ClosedByRemote, false, DisconnectReason.HostUnreachable)]
        [TestCase(TransportDisconnectReason.LocalRequest, true, DisconnectReason.ClientRequested)]
        [TestCase(TransportDisconnectReason.SecurityFailure, false, DisconnectReason.EncryptionFailed)]
        [TestCase(TransportDisconnectReason.Unknown, true, DisconnectReason.Unknown)]
        public void TransportReasonsMapToClientReasons(TransportDisconnectReason transport, bool wasConnected, DisconnectReason expected)
        {
            Assert.That(DisconnectInfo.FromTransport(transport, wasConnected), Is.EqualTo(expected));
        }

        [Test]
        public void TheOldConstructorReportsAnUnknownReason()
        {
            var ev = new TransportEvent(TransportEvent.Kind.Disconnected, 3, default);
            Assert.That(ev.Reason, Is.EqualTo(TransportDisconnectReason.Unknown));
            var typed = TransportEvent.Disconnected(3, TransportDisconnectReason.Timeout);
            Assert.That(typed.Type, Is.EqualTo(TransportEvent.Kind.Disconnected));
            Assert.That(typed.PeerId, Is.EqualTo(3));
            Assert.That(typed.Reason, Is.EqualTo(TransportDisconnectReason.Timeout));
        }

        [Test]
        public void DisconnectInfoNeverCarriesANullMessage()
        {
            var info = new DisconnectInfo(DisconnectReason.Kicked, null, false);
            Assert.That(info.Message, Is.EqualTo(""));
            Assert.That(default(DisconnectInfo).Reason, Is.EqualTo(DisconnectReason.Unknown));
        }
    }
}
