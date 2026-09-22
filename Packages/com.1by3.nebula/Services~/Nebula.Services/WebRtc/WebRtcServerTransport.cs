using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Org.BouncyCastle.Tls;

namespace Nebula.WebRtc
{
    /// <summary>
    /// <see cref="ITransport"/> for browser clients: WebRTC data channels, all on one UDP port. A browser posts its SDP
    /// offer to the gateway's HTTP server, which hands it to <see cref="Accept"/> and returns the answer. The browser
    /// then checks connectivity with STUN (answered here as an ICE-lite agent), runs the DTLS handshake, and opens
    /// SCTP with two channels agreed in advance: stream 0 reliable and ordered (<see cref="Delivery.ReliableOrdered"/>),
    /// stream 1 unordered with no retransmissions (<see cref="Delivery.Sequenced"/>).
    /// <para>
    /// A socket thread answers STUN and routes each DTLS datagram to its session, and every session has a thread for
    /// DTLS. The SCTP association of a session is guarded by a lock: the session thread feeds it packets, the caller's
    /// thread sends, flushes and runs its timers from <see cref="Send"/>, <see cref="Flush"/> and <see cref="Poll"/>.
    /// Events reach the caller only in <see cref="Poll"/>.
    /// </para>
    /// <para>
    /// Every message on the unreliable stream starts with a 16-bit little-endian sequence number, and each end drops a
    /// message older than the newest it has delivered: the newest-wins rule <see cref="Delivery.Sequenced"/> has over
    /// LiteNetLib. The data channel itself is unordered, so without it a late snapshot could land after a newer one.
    /// </para>
    /// </summary>
    internal sealed class WebRtcServerTransport : ITransport, ISecureTransport
    {
        public const ushort ReliableStream = 0, UnreliableStream = 1;
        public const int SequenceBytes = 2;
        private const long HandshakeTimeoutMs = 15000, DisconnectTimeoutMs = 8000;
        private const int MaxSessions = 1024;

        private sealed class Session
        {
            public int PeerId;
            public string LocalUfrag, RemoteUfrag, RemoteFingerprint;
            public byte[] PasswordKey;
            public volatile IPEndPoint Endpoint;
            public DatagramQueue Datagrams;
            public DtlsTransport Dtls;
            public SctpAssociation Sctp;
            public readonly object Gate = new object();
            public long LastReceiveMs;
            public volatile bool Connected, Closed;
            /// <summary>Unreliable stream sequence numbers: the next one to send (under Gate) and the newest delivered (session thread).</summary>
            public ushort SendSequence, ReceiveSequence;
            public bool ReceivedSequenced;
        }

        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static double Now => Clock.Elapsed.TotalSeconds;

        private readonly string _advertise;
        private readonly DtlsIdentity _identity = DtlsIdentity.Create();
        private readonly ConcurrentDictionary<int, Session> _sessions = new ConcurrentDictionary<int, Session>();
        private readonly ConcurrentDictionary<string, Session> _byUfrag = new ConcurrentDictionary<string, Session>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<IPEndPoint, Session> _byEndpoint = new ConcurrentDictionary<IPEndPoint, Session>();
        private readonly ConcurrentQueue<(TransportEvent.Kind Kind, int PeerId, ArraySegment<byte> Data)> _events = new ConcurrentQueue<(TransportEvent.Kind, int, ArraySegment<byte>)>();
        private readonly List<IPAddress> _interfaceAddresses = new List<IPAddress>();
        private Socket _socket;
        private Thread _receiver;
        private volatile bool _running;
        private int _nextPeerId;

        /// <param name="advertiseAddress">The address clients are told to use for the gateway; offered as the first candidate when it is an IPv4 literal.</param>
        public WebRtcServerTransport(string name, string advertiseAddress)
        {
            Name = name;
            _advertise = advertiseAddress;
        }

        /// <summary>A WebRTC data channel is carried by DTLS, so every link on it is encrypted (docs/transport-encryption.md §2).</summary>
        public bool IsEncrypted(int peerId) => IsConnected(peerId);

        public string SecurityError => "";

        public string Name { get; }
        public bool IsRunning => _running;
        public int LocalPort { get; private set; }
        public int SessionCount => _sessions.Count;

        public void Listen(int port)
        {
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            // Windows reports an ICMP "port unreachable" for an earlier send as an error on the next receive; ignore those.
            if (OperatingSystem.IsWindows()) _socket.IOControl(unchecked((int)0x9800000C), new byte[] { 0 }, null);
            _socket.ReceiveBufferSize = 1 << 20;
            _socket.SendBufferSize = 1 << 20;
            _socket.Bind(new IPEndPoint(IPAddress.Any, port));
            LocalPort = ((IPEndPoint)_socket.LocalEndPoint).Port;
            CollectInterfaceAddresses();
            _running = true;
            _receiver = new Thread(ReceiveLoop) { IsBackground = true, Name = "nebula-webrtc-socket" };
            _receiver.Start();
        }

        public void StartClient() => throw new NotSupportedException("browsers connect to the gateway; the WebRTC transport never dials out");

        public int Connect(string host, int port) => throw new NotSupportedException("browsers connect to the gateway; the WebRTC transport never dials out");

        /// <summary>
        /// Start a session for a browser's SDP offer and return the answer. <paramref name="requestLocalAddress"/> is the
        /// address the offer arrived on, which the browser can evidently reach, so it is the first candidate.
        /// Throws <see cref="FormatException"/> for an unusable offer and <see cref="InvalidOperationException"/> when full.
        /// </summary>
        public string Accept(string offerSdp, IPAddress requestLocalAddress)
        {
            if (!_running) throw new InvalidOperationException("the WebRTC transport is not listening");
            if (!Sdp.TryParse(offerSdp, out var offer)) throw new FormatException("the offer has no data channel section with ICE credentials and a sha-256 fingerprint");
            if (string.Equals(offer.Setup, "passive", StringComparison.OrdinalIgnoreCase)) throw new FormatException("the offer must let the gateway be the DTLS server (a=setup:actpass or active)");
            if (_sessions.Count >= MaxSessions) throw new InvalidOperationException("the gateway has no room for another browser session");
            var s = new Session
            {
                PeerId = Interlocked.Increment(ref _nextPeerId),
                LocalUfrag = Sdp.RandomToken(8),
                RemoteUfrag = offer.IceUfrag,
                RemoteFingerprint = offer.Fingerprint,
                LastReceiveMs = Environment.TickCount64,
            };
            string password = Sdp.RandomToken(24);
            s.PasswordKey = Encoding.UTF8.GetBytes(password);
            s.Datagrams = new DatagramQueue((buffer, offset, length) => SendDatagram(s, buffer, offset, length));
            _sessions[s.PeerId] = s;
            _byUfrag[s.LocalUfrag] = s;
            new Thread(() => RunSession(s)) { IsBackground = true, Name = "nebula-webrtc-" + s.PeerId }.Start();
            return Sdp.Answer(offer, s.LocalUfrag, password, _identity.Fingerprint, Candidates(requestLocalAddress), LocalPort);
        }

        public void Disconnect(int peerId)
        {
            if (_sessions.TryGetValue(peerId, out var s)) Close(s, true);
        }

        public bool IsConnected(int peerId) => _sessions.TryGetValue(peerId, out var s) && s.Connected && !s.Closed;

        public int RoundTripMs(int peerId)
        {
            if (!_sessions.TryGetValue(peerId, out var s)) return -1;
            lock (s.Gate) return s.Sctp != null ? s.Sctp.RoundTripMs : -1;
        }

        public void Send(int peerId, Delivery delivery, ArraySegment<byte> payload)
        {
            if (!_sessions.TryGetValue(peerId, out var s) || !s.Connected || s.Closed) return;
            var data = new ReadOnlySpan<byte>(payload.Array, payload.Offset, payload.Count);
            lock (s.Gate)
            {
                if (s.Closed) return;
                if (delivery != Delivery.Sequenced)
                {
                    s.Sctp.Send(ReliableStream, false, true, SctpAssociation.PpidBinary, data);
                    return;
                }
                int length = payload.Count + SequenceBytes;
                Span<byte> framed = length <= 1500 ? stackalloc byte[length] : new byte[length];
                ushort sequence = s.SendSequence++;
                framed[0] = (byte)sequence;
                framed[1] = (byte)(sequence >> 8);
                data.CopyTo(framed.Slice(SequenceBytes));
                s.Sctp.Send(UnreliableStream, true, false, SctpAssociation.PpidBinary, framed);
            }
        }

        /// <summary>Is <paramref name="sequence"/> newer than <paramref name="last"/>, allowing for wraparound?</summary>
        public static bool IsNewer(ushort sequence, ushort last) => sequence != last && (ushort)(sequence - last) < 0x8000;

        public void Poll(Action<TransportEvent> handler)
        {
            double now = Now;
            long nowMs = Environment.TickCount64;
            foreach (var s in _sessions.Values)
            {
                if (s.Closed) continue;
                if (nowMs - Interlocked.Read(ref s.LastReceiveMs) > (s.Connected ? DisconnectTimeoutMs : HandshakeTimeoutMs))
                {
                    NebulaLog.Debugf($"[{Name}] browser session {s.PeerId} timed out ({(s.Connected ? "silent" : "never connected")})");
                    Close(s, true);
                    continue;
                }
                lock (s.Gate) s.Sctp?.Tick(now);
            }
            while (_events.TryDequeue(out var ev))
                handler(new TransportEvent(ev.Kind, ev.PeerId, ev.Data));
        }

        public void Flush()
        {
            double now = Now;
            foreach (var s in _sessions.Values)
            {
                if (!s.Connected || s.Closed) continue;
                lock (s.Gate)
                {
                    if (!s.Closed) s.Sctp.Flush(now);
                }
            }
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            foreach (var s in _sessions.Values) Close(s, true);
            try { _socket?.Close(); } catch { }
            _receiver?.Join(1000);
        }

        public void Dispose() => Stop();

        // ---------------------------------------------------------------------------------------- socket thread

        private void ReceiveLoop()
        {
            var buffer = new byte[2048];
            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            while (_running)
            {
                int n;
                try { n = _socket.ReceiveFrom(buffer, ref remote); }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { if (!_running) break; continue; }
                var from = (IPEndPoint)remote;
                var data = new ReadOnlySpan<byte>(buffer, 0, n);
                if (Stun.IsStun(data)) HandleStun(data, from);
                // RFC 7983: DTLS records start with 20 to 63. Only an address that passed a connectivity check reaches a session.
                else if (n > 0 && data[0] >= 20 && data[0] <= 63 && _byEndpoint.TryGetValue(from, out var s) && !s.Closed)
                {
                    Interlocked.Exchange(ref s.LastReceiveMs, Environment.TickCount64);
                    s.Datagrams.Enqueue(data.ToArray());
                }
            }
        }

        private void HandleStun(ReadOnlySpan<byte> data, IPEndPoint from)
        {
            if (!Stun.TryParseBindingRequest(data, out var request) || request.Username == null) return;
            // USERNAME is "<our ufrag>:<their ufrag>", signed with our password.
            int colon = request.Username.IndexOf(':');
            if (colon <= 0 || !_byUfrag.TryGetValue(request.Username.Substring(0, colon), out var s) || s.Closed) return;
            if (request.Username.Substring(colon + 1) != s.RemoteUfrag || !Stun.CheckIntegrity(data, request, s.PasswordKey)) return;
            var endpoint = new IPEndPoint(from.Address, from.Port);
            try { _socket.SendTo(Stun.BindingResponse(request.TransactionId, endpoint, s.PasswordKey), endpoint); }
            catch (SocketException) { return; }
            Interlocked.Exchange(ref s.LastReceiveMs, Environment.TickCount64);
            _byEndpoint[endpoint] = s;
            // The first address that checks out carries DTLS until the browser nominates a pair.
            if (s.Endpoint == null || request.UseCandidate) s.Endpoint = endpoint;
        }

        private void SendDatagram(Session s, byte[] buffer, int offset, int length)
        {
            var endpoint = s.Endpoint;
            if (endpoint == null || !_running) return;
            try { _socket.SendTo(buffer, offset, length, SocketFlags.None, endpoint); }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }

        // ---------------------------------------------------------------------------------------- session thread

        private void RunSession(Session s)
        {
            try
            {
                var dtls = new DtlsServerProtocol { VerifyRequests = false }.Accept(new DtlsServer(_identity, s.RemoteFingerprint), s.Datagrams);
                lock (s.Gate)
                {
                    if (s.Closed) { dtls.Close(); return; }
                    s.Dtls = dtls;
                    s.Sctp = new SctpAssociation((packet, length) => SendPacket(s, packet, length), (stream, ppid, message) => Deliver(s, stream, message));
                    s.Sctp.Established = () =>
                    {
                        s.Connected = true;
                        _events.Enqueue((TransportEvent.Kind.Connected, s.PeerId, default));
                    };
                    s.Sctp.Closed = () => Close(s, false);
                }
                var buffer = new byte[dtls.GetReceiveLimit()];
                while (!s.Closed && _running)
                {
                    if (!s.Datagrams.WaitForData(250)) continue;
                    // The datagram is already queued, so this returns at once; holding the lock keeps DTLS and SCTP single-threaded.
                    lock (s.Gate)
                    {
                        if (s.Closed) break;
                        int n = dtls.Receive(buffer, 0, buffer.Length, 1);
                        if (n > 0) s.Sctp.HandlePacket(new ReadOnlySpan<byte>(buffer, 0, n), Now);
                    }
                }
            }
            catch (Exception e)
            {
                if (!s.Closed && _running) NebulaLog.Debugf($"[{Name}] browser session {s.PeerId} ended: {e.GetBaseException().Message}");
            }
            finally
            {
                Close(s, true);
            }
        }

        private void Deliver(Session s, ushort stream, byte[] message)
        {
            if (stream != UnreliableStream)
            {
                _events.Enqueue((TransportEvent.Kind.Data, s.PeerId, new ArraySegment<byte>(message)));
                return;
            }
            if (message.Length < SequenceBytes) return;
            ushort sequence = (ushort)(message[0] | message[1] << 8);
            if (s.ReceivedSequenced && !IsNewer(sequence, s.ReceiveSequence)) return; // arrived after a newer one
            s.ReceivedSequenced = true;
            s.ReceiveSequence = sequence;
            _events.Enqueue((TransportEvent.Kind.Data, s.PeerId, new ArraySegment<byte>(message, SequenceBytes, message.Length - SequenceBytes)));
        }

        private void SendPacket(Session s, byte[] packet, int length)
        {
            try { s.Dtls.Send(packet, 0, length); }
            catch (Exception) { Close(s, false); }
        }

        private void Close(Session s, bool notifyPeer)
        {
            lock (s.Gate)
            {
                if (s.Closed) return;
                s.Closed = true;
                if (notifyPeer)
                {
                    try { s.Sctp?.Abort(); } catch { }
                    try { s.Dtls?.Close(); } catch { }
                }
            }
            s.Datagrams.Close();
            _sessions.TryRemove(s.PeerId, out _);
            _byUfrag.TryRemove(s.LocalUfrag, out _);
            foreach (var kv in _byEndpoint) if (kv.Value == s) _byEndpoint.TryRemove(kv.Key, out _);
            if (s.Connected) _events.Enqueue((TransportEvent.Kind.Disconnected, s.PeerId, default));
        }

        // ---------------------------------------------------------------------------------------- candidates

        private void CollectInterfaceAddresses()
        {
            _interfaceAddresses.Clear();
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    foreach (var a in nic.GetIPProperties().UnicastAddresses)
                        if (a.Address.AddressFamily == AddressFamily.InterNetwork) _interfaceAddresses.Add(a.Address);
                }
            }
            catch (NetworkInformationException e) { NebulaLog.Warn($"[{Name}] could not list network interfaces: {e.Message}"); }
        }

        private List<IPAddress> Candidates(IPAddress requestLocalAddress)
        {
            var list = new List<IPAddress>();
            void Add(IPAddress a)
            {
                if (a == null) return;
                if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
                if (a.AddressFamily != AddressFamily.InterNetwork || a.Equals(IPAddress.Any) || list.Contains(a)) return;
                list.Add(a);
            }
            Add(requestLocalAddress);
            if (IPAddress.TryParse(_advertise, out var advertised)) Add(advertised);
            foreach (var a in _interfaceAddresses) Add(a);
            Add(IPAddress.Loopback);
            return list;
        }
    }
}
