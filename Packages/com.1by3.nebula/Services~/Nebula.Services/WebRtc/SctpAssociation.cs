using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace Nebula.WebRtc
{
    /// <summary>
    /// The SCTP association (RFC 9260) under WebRTC data channels (RFC 8831), cut down to what Nebula needs: one
    /// association per DTLS session, channels agreed in advance (<c>negotiated: true</c>, so no DCEP), partial
    /// reliability (RFC 3758) for the unreliable channel, and a congestion window for the reliable one. Not
    /// thread-safe: the owner serializes every call. Packets leave through the output callback and messages arrive
    /// through the delivery callback, both on the calling thread.
    /// </summary>
    internal sealed class SctpAssociation
    {
        public const ushort Port = 5000;
        /// <summary>Largest SCTP packet sent. With DTLS (37 bytes for AES-GCM) and UDP/IPv4 (28) it fits the 1280-byte IPv6 minimum MTU.</summary>
        public const int MaxPacketSize = 1180;
        /// <summary>Largest message reassembled. Bigger ones abort the association.</summary>
        public const int MaxMessageSize = 1 << 20;
        public const uint PpidString = 51, PpidBinary = 53, PpidEmptyString = 56, PpidEmptyBinary = 57;

        public enum Status { Closed, CookieWait, CookieEchoed, Established, Aborted }

        private const byte ChunkData = 0, ChunkInit = 1, ChunkInitAck = 2, ChunkSack = 3, ChunkHeartbeat = 4, ChunkHeartbeatAck = 5,
            ChunkAbort = 6, ChunkShutdown = 7, ChunkShutdownAck = 8, ChunkCookieEcho = 10, ChunkCookieAck = 11, ChunkShutdownComplete = 14,
            ChunkReconfig = 130, ChunkForwardTsn = 192;
        private const byte FlagEnd = 1, FlagBegin = 2, FlagUnordered = 4;
        private const ushort ParamStateCookie = 7, ParamOutgoingReset = 13, ParamReconfigResponse = 16,
            ParamSupportedExtensions = 0x8008, ParamForwardTsnSupported = 0xC000;
        private const int CommonHeaderSize = 12, DataHeaderSize = 16;
        private const int MaxFragment = MaxPacketSize - CommonHeaderSize - DataHeaderSize;
        private const int ReceiveBufferBytes = 4 << 20;
        private const uint MaxTsnAhead = 65535;
        private const int MaxGapBlocks = 64;
        private const ushort StreamCount = 1024;
        /// <summary>Retransmission timeout bounds: the minimum stays above a browser's delayed SACK (200 ms); the maximum is short because a game would rather resend than stall.</summary>
        private const double InitialRto = 1.0, MinRto = 0.3, MaxRto = 1.5;
        private const int MaxHandshakeAttempts = 8;

        private sealed class OutChunk
        {
            public uint Tsn, Ppid, Message;
            public ushort Stream, Ssn;
            public byte Flags;
            public byte[] Data;
            public bool Reliable, Sent, Acked, Abandoned, Retransmit, InFlight;
            public int Transmissions, Misses;
            public double SentAt;
        }

        private sealed class Fragment
        {
            public ushort Stream, Ssn;
            public uint Ppid;
            public bool Unordered, Begin, End;
            public byte[] Data;
        }

        private sealed class InStream
        {
            public ushort NextSsn;
            public readonly Dictionary<ushort, (uint Ppid, byte[] Data)> Pending = new Dictionary<ushort, (uint, byte[])>();
        }

        private readonly Action<byte[], int> _output;
        private readonly Action<ushort, uint, byte[]> _deliver;
        private readonly byte[] _packet = new byte[2048];
        private int _packetLength;

        public Status State { get; private set; }
        /// <summary>Raised once, when the association is up (both ends can send).</summary>
        public Action Established;
        /// <summary>Raised once, when the peer aborts or shuts down, or a limit is broken.</summary>
        public Action Closed;

        private uint _localTag, _peerTag, _initialTsn;
        private byte[] _cookie;
        private bool _peerForwardTsn;
        private int _handshakeAttempts;
        private double _handshakeResendAt;

        // Receiving: the peer's TSNs.
        private uint _peerCumTsn;
        private readonly HashSet<uint> _receivedAbove = new HashSet<uint>();
        private readonly Dictionary<uint, Fragment> _fragments = new Dictionary<uint, Fragment>();
        private int _fragmentBytes;
        private readonly Dictionary<ushort, InStream> _inStreams = new Dictionary<ushort, InStream>();
        private readonly List<uint> _gapScratch = new List<uint>();
        private bool _sackPending;

        // Sending: our TSNs. _outstanding holds every TSN after _lastCumAck, contiguous, sent or not.
        private readonly List<OutChunk> _outstanding = new List<OutChunk>();
        private readonly Dictionary<ushort, ushort> _outSsn = new Dictionary<ushort, ushort>();
        private uint _nextTsn, _lastCumAck, _nextMessage;
        private uint _advancedPeerAckPoint, _forwardTsnSentPoint;
        private double _forwardTsnSentAt;
        private bool _forwardTsnPending;
        private int _flightBytes, _cwnd, _ssthresh, _partialBytesAcked;
        private uint _peerRwnd = ReceiveBufferBytes;
        private double _srtt, _rttvar, _rto = InitialRto;
        private bool _hasRtt;

        public SctpAssociation(Action<byte[], int> output, Action<ushort, uint, byte[]> deliver)
        {
            _output = output;
            _deliver = deliver;
        }

        /// <summary>TSNs handed out and not yet covered by the peer's cumulative acknowledgement (tests).</summary>
        internal int OutstandingCount => _outstanding.Count;

        /// <summary>Protocol events (acknowledgements, retransmissions, FORWARD-TSN), for tests.</summary>
        internal Action<string> Trace;

        /// <summary>The sender and receiver state in one string, for test failures.</summary>
        internal string Describe()
        {
            var b = new System.Text.StringBuilder();
            b.Append($"state={State} cwnd={_cwnd} ssthresh={_ssthresh} flight={_flightBytes} rto={_rto:0.000} peerRwnd={_peerRwnd} nextTsn={_nextTsn} lastCumAck={_lastCumAck} advancedPoint={_advancedPeerAckPoint} forwardPending={_forwardTsnPending} outstanding={_outstanding.Count} peerCum={_peerCumTsn} above={_receivedAbove.Count} fragments={_fragments.Count}");
            for (int i = 0; i < _outstanding.Count && i < 8; i++)
            {
                var c = _outstanding[i];
                b.Append($"\n  tsn={c.Tsn} stream={c.Stream} ssn={c.Ssn} flags={c.Flags} reliable={c.Reliable} sent={c.Sent} tx={c.Transmissions} acked={c.Acked} abandoned={c.Abandoned} retransmit={c.Retransmit} inFlight={c.InFlight} misses={c.Misses} sentAt={c.SentAt:0.00}");
            }
            foreach (var kv in _inStreams) b.Append($"\n  in stream {kv.Key}: next ssn {kv.Value.NextSsn}, pending {kv.Value.Pending.Count}");
            return b.ToString();
        }

        /// <summary>Smoothed round trip in milliseconds, measured from acknowledgements, or -1 before the first one.</summary>
        public int RoundTripMs => _hasRtt ? (int)Math.Round(_srtt * 1000) : -1;

        /// <summary>Open the association from this side (a browser always does; the gateway only answers). Tests use this.</summary>
        public void Connect(double now)
        {
            if (State != Status.Closed) return;
            ChooseLocalParameters();
            State = Status.CookieWait;
            _handshakeAttempts = 0;
            SendInit(now);
        }

        /// <summary>Queue a message. Reliable messages are retransmitted until acknowledged; unreliable ones are sent once and abandoned if lost.</summary>
        public bool Send(ushort stream, bool unordered, bool reliable, uint ppid, ReadOnlySpan<byte> message)
        {
            if (State != Status.Established) return false;
            if (message.Length > MaxMessageSize) return false;
            // A peer without FORWARD-TSN cannot skip an abandoned chunk, so everything to it is reliable.
            if (!_peerForwardTsn) reliable = true;
            ushort ssn = 0;
            if (!unordered)
            {
                _outSsn.TryGetValue(stream, out ssn);
                _outSsn[stream] = (ushort)(ssn + 1);
            }
            if (message.Length == 0)
            {
                // WebRTC carries an empty message as one zero byte with the "empty" payload protocol id.
                message = new byte[1];
                ppid = ppid == PpidString ? PpidEmptyString : PpidEmptyBinary;
            }
            uint messageId = _nextMessage++;
            int count = (message.Length + MaxFragment - 1) / MaxFragment;
            for (int i = 0; i < count; i++)
            {
                int offset = i * MaxFragment;
                int length = Math.Min(MaxFragment, message.Length - offset);
                _outstanding.Add(new OutChunk
                {
                    Tsn = _nextTsn++, Stream = stream, Ssn = ssn, Ppid = ppid, Message = messageId, Reliable = reliable,
                    Flags = (byte)((unordered ? FlagUnordered : 0) | (i == 0 ? FlagBegin : 0) | (i == count - 1 ? FlagEnd : 0)),
                    Data = message.Slice(offset, length).ToArray(),
                });
            }
            return true;
        }

        /// <summary>Put queued acknowledgements, FORWARD-TSNs and data on the wire, as far as the congestion window allows.</summary>
        public void Flush(double now)
        {
            if (State != Status.Established) return;
            if (_sackPending) AppendSack();
            if (_forwardTsnPending) AppendForwardTsn(now);
            bool reliableBlocked = false;
            for (int i = 0; i < _outstanding.Count; i++)
            {
                var c = _outstanding[i];
                if (c.Acked || c.Abandoned || (c.Sent && !c.Retransmit)) continue;
                if (c.Reliable)
                {
                    if (reliableBlocked) continue;
                    if (_flightBytes > 0 && (_flightBytes + c.Data.Length > _cwnd || _flightBytes + c.Data.Length > _peerRwnd))
                    {
                        reliableBlocked = true;
                        continue;
                    }
                    c.InFlight = true;
                    _flightBytes += c.Data.Length;
                }
                AppendData(c);
                c.Sent = true;
                c.Retransmit = false;
                c.Misses = 0;
                c.Transmissions++;
                c.SentAt = now;
            }
            EmitPacket();
        }

        /// <summary>Timers: handshake retries, retransmission timeouts, abandoning late unreliable chunks.</summary>
        public void Tick(double now)
        {
            if (State == Status.CookieWait || State == Status.CookieEchoed)
            {
                if (now < _handshakeResendAt) return;
                if (++_handshakeAttempts > MaxHandshakeAttempts) { Fail(); return; }
                if (State == Status.CookieWait) SendInit(now); else SendCookieEcho(now);
                return;
            }
            if (State != Status.Established) return;
            bool expired = false;
            for (int i = 0; i < _outstanding.Count; i++)
            {
                var c = _outstanding[i];
                if (!c.Sent || c.Acked || c.Abandoned || c.Retransmit || now - c.SentAt < _rto) continue;
                Trace?.Invoke($"T3 expired tsn {c.Tsn} reliable {c.Reliable} rto {_rto:0.00}");
                if (c.Reliable) expired = true;
                else Abandon(i);
            }
            if (expired)
            {
                // One timeout is one loss event: everything in flight goes again and the timer backs off once. Expiring
                // chunk by chunk would double the timeout for every chunk of the same burst.
                foreach (var c in _outstanding)
                    if (c.Reliable && c.Sent && !c.Acked && !c.Retransmit) MarkRetransmit(c);
                _ssthresh = Math.Max(_cwnd / 2, 4 * MaxPacketSize);
                _cwnd = MaxPacketSize;
                _partialBytesAcked = 0;
                _rto = Math.Min(_rto * 2, MaxRto);
            }
            UpdateAdvancedPeerAckPoint(now);
        }

        /// <summary>Tell the peer the association is gone. Nothing is sent after this.</summary>
        public void Abort()
        {
            if (State == Status.Aborted || State == Status.Closed) return;
            BeginPacket(_peerTag);
            int c = BeginChunk(ChunkAbort, 0);
            EndChunk(c);
            EmitPacket();
            State = Status.Aborted;
        }

        // ---------------------------------------------------------------------------------------- receiving

        public void HandlePacket(ReadOnlySpan<byte> packet, double now)
        {
            if (State == Status.Aborted || packet.Length < CommonHeaderSize + 4) return;
            if (BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(8)) != Crc.SctpChecksum(packet)) return;
            uint tag = BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(4));
            bool first = true;
            for (int p = CommonHeaderSize; p + 4 <= packet.Length;)
            {
                byte type = packet[p];
                byte flags = packet[p + 1];
                int length = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(p + 2));
                if (length < 4 || p + length > packet.Length) return;
                var value = packet.Slice(p + 4, length - 4);
                // INIT travels with tag 0 and alone; an ABORT with the T flag carries the peer's own tag; everything else carries ours.
                if (type == ChunkInit) { if (tag != 0 || !first) return; }
                else if (type == ChunkAbort && (flags & 1) != 0) { if (tag != _peerTag) return; }
                else if (tag != _localTag || _localTag == 0) return;
                first = false;
                switch (type)
                {
                    case ChunkData: HandleData(flags, value); _sackPending = true; break;
                    case ChunkInit: HandleInit(value); return;
                    case ChunkInitAck: HandleInitAck(value, now); return;
                    case ChunkSack: HandleSack(value, now); break;
                    case ChunkHeartbeat: SendControl(ChunkHeartbeatAck, value); break;
                    case ChunkAbort: Fail(); return;
                    case ChunkShutdown: SendControl(ChunkShutdownAck, default); break;
                    case ChunkShutdownAck: SendControl(ChunkShutdownComplete, default); Fail(); return;
                    case ChunkShutdownComplete: Fail(); return;
                    case ChunkCookieEcho: HandleCookieEcho(value); break;
                    case ChunkCookieAck: if (State == Status.CookieEchoed) Establish(); break;
                    case ChunkReconfig: HandleReconfig(value); break;
                    case ChunkForwardTsn: HandleForwardTsn(value); _sackPending = true; break;
                    default:
                        // The two high bits of an unknown chunk type say whether to skip it or stop reading the packet.
                        if ((type & 0x80) == 0) return;
                        break;
                }
                if (State == Status.Aborted) return;
                p += (length + 3) & ~3;
            }
        }

        private void HandleInit(ReadOnlySpan<byte> v)
        {
            if (v.Length < 16 || State == Status.Established) return;
            uint initiateTag = BinaryPrimitives.ReadUInt32BigEndian(v);
            if (initiateTag == 0) return;
            _peerTag = initiateTag;
            _peerRwnd = BinaryPrimitives.ReadUInt32BigEndian(v.Slice(4));
            ResetReceive(BinaryPrimitives.ReadUInt32BigEndian(v.Slice(12)));
            _peerForwardTsn = ParsePeerExtensions(v.Slice(16));
            if (_localTag == 0) ChooseLocalParameters();
            if (_cookie == null)
            {
                _cookie = new byte[16];
                RandomNumberGenerator.Fill(_cookie);
            }
            BeginPacket(_peerTag);
            int c = BeginChunk(ChunkInitAck, 0);
            WriteInitFields();
            WriteParam(ParamStateCookie, _cookie);
            WriteExtensionParams();
            EndChunk(c);
            EmitPacket();
        }

        private void HandleInitAck(ReadOnlySpan<byte> v, double now)
        {
            if (State != Status.CookieWait || v.Length < 16) return;
            _peerTag = BinaryPrimitives.ReadUInt32BigEndian(v);
            _peerRwnd = BinaryPrimitives.ReadUInt32BigEndian(v.Slice(4));
            ResetReceive(BinaryPrimitives.ReadUInt32BigEndian(v.Slice(12)));
            _peerForwardTsn = ParsePeerExtensions(v.Slice(16));
            for (int p = 16; p + 4 <= v.Length;)
            {
                ushort type = BinaryPrimitives.ReadUInt16BigEndian(v.Slice(p));
                int length = BinaryPrimitives.ReadUInt16BigEndian(v.Slice(p + 2));
                if (length < 4 || p + length > v.Length) break;
                if (type == ParamStateCookie) _cookie = v.Slice(p + 4, length - 4).ToArray();
                p += (length + 3) & ~3;
            }
            if (_cookie == null) return;
            State = Status.CookieEchoed;
            _handshakeAttempts = 0;
            SendCookieEcho(now);
        }

        private void HandleCookieEcho(ReadOnlySpan<byte> v)
        {
            if (_cookie == null || !v.SequenceEqual(_cookie)) return;
            if (State != Status.Established) Establish();
            SendControl(ChunkCookieAck, default);
        }

        private void HandleData(byte flags, ReadOnlySpan<byte> v)
        {
            if (State != Status.Established || v.Length < 12) return;
            uint tsn = BinaryPrimitives.ReadUInt32BigEndian(v);
            if (!After(tsn, _peerCumTsn) || _receivedAbove.Contains(tsn) || tsn - _peerCumTsn > MaxTsnAhead) return;
            if (tsn == _peerCumTsn + 1)
            {
                _peerCumTsn = tsn;
                while (_receivedAbove.Remove(_peerCumTsn + 1)) _peerCumTsn++;
            }
            else _receivedAbove.Add(tsn);

            var f = new Fragment
            {
                Stream = BinaryPrimitives.ReadUInt16BigEndian(v.Slice(4)),
                Ssn = BinaryPrimitives.ReadUInt16BigEndian(v.Slice(6)),
                Ppid = BinaryPrimitives.ReadUInt32BigEndian(v.Slice(8)),
                Unordered = (flags & FlagUnordered) != 0,
                Begin = (flags & FlagBegin) != 0,
                End = (flags & FlagEnd) != 0,
                Data = v.Slice(12).ToArray(),
            };
            if (f.Begin && f.End)
            {
                Complete(f.Stream, f.Ssn, f.Unordered, f.Ppid, f.Data);
                return;
            }
            _fragments[tsn] = f;
            _fragmentBytes += f.Data.Length;
            if (_fragmentBytes > ReceiveBufferBytes) { Abort(); Fail(); return; }
            Reassemble(tsn, f);
        }

        /// <summary>Fragments of one message have consecutive TSNs; when the run from Begin to End is all here, deliver it.</summary>
        private void Reassemble(uint tsn, Fragment f)
        {
            uint first = tsn;
            while (!_fragments[first].Begin)
            {
                if (!_fragments.TryGetValue(first - 1, out var previous) || previous.End || !SameMessage(previous, f)) return;
                first--;
            }
            uint last = tsn;
            while (!_fragments[last].End)
            {
                if (!_fragments.TryGetValue(last + 1, out var next) || next.Begin || !SameMessage(next, f)) return;
                last++;
            }
            int total = 0;
            for (uint t = first; ; t++) { total += _fragments[t].Data.Length; if (t == last) break; }
            var message = new byte[total];
            uint ppid = _fragments[first].Ppid;
            int offset = 0;
            for (uint t = first; ; t++)
            {
                var part = _fragments[t];
                Buffer.BlockCopy(part.Data, 0, message, offset, part.Data.Length);
                offset += part.Data.Length;
                _fragmentBytes -= part.Data.Length;
                _fragments.Remove(t);
                if (t == last) break;
            }
            if (total > MaxMessageSize) { Abort(); Fail(); return; }
            Complete(f.Stream, f.Ssn, f.Unordered, ppid, message);
        }

        private static bool SameMessage(Fragment a, Fragment b) => a.Stream == b.Stream && a.Unordered == b.Unordered && (a.Unordered || a.Ssn == b.Ssn);

        private void Complete(ushort stream, ushort ssn, bool unordered, uint ppid, byte[] data)
        {
            if (unordered) { Deliver(stream, ppid, data); return; }
            var s = InStreamOf(stream);
            if (ssn != s.NextSsn)
            {
                if (SsnAfter(ssn, s.NextSsn)) s.Pending[ssn] = (ppid, data);
                return;
            }
            Deliver(stream, ppid, data);
            s.NextSsn++;
            DeliverPending(stream, s);
        }

        private void DeliverPending(ushort stream, InStream s)
        {
            while (s.Pending.Remove(s.NextSsn, out var next))
            {
                Deliver(stream, next.Ppid, next.Data);
                s.NextSsn++;
            }
        }

        private void Deliver(ushort stream, uint ppid, byte[] data)
        {
            switch (ppid)
            {
                case PpidBinary:
                case PpidString: _deliver(stream, ppid, data); break;
                case PpidEmptyBinary:
                case PpidEmptyString: _deliver(stream, ppid, Array.Empty<byte>()); break;
                // DCEP (50) does not occur with negotiated channels; anything else is not a data channel message.
            }
        }

        private InStream InStreamOf(ushort stream)
        {
            if (!_inStreams.TryGetValue(stream, out var s)) _inStreams[stream] = s = new InStream();
            return s;
        }

        private void HandleForwardTsn(ReadOnlySpan<byte> v)
        {
            if (State != Status.Established || v.Length < 4) return;
            uint newCum = BinaryPrimitives.ReadUInt32BigEndian(v);
            Trace?.Invoke($"FORWARD-TSN in {newCum} (cum {_peerCumTsn})");
            if (!After(newCum, _peerCumTsn)) return;
            _peerCumTsn = newCum;
            _receivedAbove.RemoveWhere(t => !After(t, newCum));
            while (_receivedAbove.Remove(_peerCumTsn + 1)) _peerCumTsn++;
            if (_fragments.Count > 0)
            {
                var skipped = new List<uint>();
                foreach (var kv in _fragments) if (!After(kv.Key, newCum)) skipped.Add(kv.Key);
                foreach (uint t in skipped) { _fragmentBytes -= _fragments[t].Data.Length; _fragments.Remove(t); }
            }
            // Ordered streams named here skip every message up to and including the SSN given.
            for (int p = 4; p + 4 <= v.Length; p += 4)
            {
                ushort stream = BinaryPrimitives.ReadUInt16BigEndian(v.Slice(p));
                ushort ssn = BinaryPrimitives.ReadUInt16BigEndian(v.Slice(p + 2));
                var s = InStreamOf(stream);
                if (SsnAfter(s.NextSsn, ssn)) continue;
                for (ushort skippedSsn = s.NextSsn; ; skippedSsn++) { s.Pending.Remove(skippedSsn); if (skippedSsn == ssn) break; }
                s.NextSsn = (ushort)(ssn + 1);
                DeliverPending(stream, s);
            }
        }

        private void HandleReconfig(ReadOnlySpan<byte> v)
        {
            // A browser resets a stream when its channel closes. Answer "performed" so it does not wait on us.
            Span<byte> response = stackalloc byte[8];
            for (int p = 0; p + 4 <= v.Length;)
            {
                ushort type = BinaryPrimitives.ReadUInt16BigEndian(v.Slice(p));
                int length = BinaryPrimitives.ReadUInt16BigEndian(v.Slice(p + 2));
                if (length < 4 || p + length > v.Length) return;
                if (type == ParamOutgoingReset && length >= 16)
                {
                    uint request = BinaryPrimitives.ReadUInt32BigEndian(v.Slice(p + 4));
                    for (int s = p + 16; s + 2 <= p + length; s += 2)
                    {
                        var stream = InStreamOf(BinaryPrimitives.ReadUInt16BigEndian(v.Slice(s)));
                        stream.NextSsn = 0;
                        stream.Pending.Clear();
                    }
                    BinaryPrimitives.WriteUInt32BigEndian(response, request);
                    BinaryPrimitives.WriteUInt32BigEndian(response.Slice(4), 1); // success, performed
                    BeginPacket(_peerTag);
                    int c = BeginChunk(ChunkReconfig, 0);
                    WriteParam(ParamReconfigResponse, response);
                    EndChunk(c);
                    EmitPacket();
                }
                p += (length + 3) & ~3;
            }
        }

        private void HandleSack(ReadOnlySpan<byte> v, double now)
        {
            if (State != Status.Established || v.Length < 12) return;
            uint cum = BinaryPrimitives.ReadUInt32BigEndian(v);
            uint rwnd = BinaryPrimitives.ReadUInt32BigEndian(v.Slice(4));
            int gaps = BinaryPrimitives.ReadUInt16BigEndian(v.Slice(8));
            Trace?.Invoke($"SACK in cum {cum} gaps {gaps} (last {_lastCumAck}, next {_nextTsn})");
            if (After(_lastCumAck, cum) || After(cum, _nextTsn - 1)) return; // stale, or acknowledges what was never sent
            int bytesAcked = 0;
            bool cumAdvanced = After(cum, _lastCumAck);
            OutChunk sample = null;

            int removed = 0;
            while (removed < _outstanding.Count && !After(_outstanding[removed].Tsn, cum))
            {
                Acknowledge(_outstanding[removed], ref bytesAcked, ref sample);
                removed++;
            }
            _outstanding.RemoveRange(0, removed);
            _lastCumAck = cum;

            uint highestGapAcked = cum;
            for (int g = 0; g < gaps && 12 + 4 * (g + 1) <= v.Length; g++)
            {
                uint start = cum + BinaryPrimitives.ReadUInt16BigEndian(v.Slice(12 + 4 * g));
                uint end = cum + BinaryPrimitives.ReadUInt16BigEndian(v.Slice(14 + 4 * g));
                for (uint t = start; !After(t, end); t++)
                {
                    long index = (long)(t - cum) - 1;
                    if (index < 0 || index >= _outstanding.Count) break;
                    Acknowledge(_outstanding[(int)index], ref bytesAcked, ref sample);
                    if (After(t, highestGapAcked)) highestGapAcked = t;
                }
            }
            if (sample != null) MeasureRtt(now - sample.SentAt);

            // Chunks below the highest one the peer has seen are missing; three reports (two for unreliable) mean lost.
            bool fastRetransmit = false;
            for (int i = 0; i < _outstanding.Count && After(highestGapAcked, _outstanding[i].Tsn); i++)
            {
                var c = _outstanding[i];
                if (!c.Sent || c.Acked || c.Abandoned || c.Retransmit) continue;
                c.Misses++;
                if (c.Reliable && c.Misses >= 3) { MarkRetransmit(c); fastRetransmit = true; }
                else if (!c.Reliable && c.Misses >= 2) Abandon(i);
            }
            if (fastRetransmit)
            {
                _ssthresh = Math.Max(_cwnd / 2, 4 * MaxPacketSize);
                _cwnd = _ssthresh;
                _partialBytesAcked = 0;
            }
            else if (cumAdvanced && bytesAcked > 0)
            {
                if (_cwnd <= _ssthresh) _cwnd += Math.Min(bytesAcked, MaxPacketSize);
                else if ((_partialBytesAcked += bytesAcked) >= _cwnd)
                {
                    _partialBytesAcked -= _cwnd;
                    _cwnd += MaxPacketSize;
                }
            }
            _peerRwnd = rwnd > (uint)_flightBytes ? rwnd - (uint)_flightBytes : 0;
            // New data acknowledged ends a timeout backoff even without a clean RTT sample (as TCP does); otherwise a
            // lossy stretch where everything is a retransmission keeps the timer at its maximum long after it ends.
            if (cumAdvanced && _hasRtt) _rto = Math.Clamp(_srtt + 4 * _rttvar, MinRto, MaxRto);
            UpdateAdvancedPeerAckPoint(now);
        }

        private void Acknowledge(OutChunk c, ref int bytesAcked, ref OutChunk sample)
        {
            if (c.Acked) return;
            c.Acked = true;
            if (c.InFlight)
            {
                c.InFlight = false;
                _flightBytes -= c.Data.Length;
                bytesAcked += c.Data.Length;
            }
            // Karn: only a chunk sent exactly once (and not queued to go again) times a round trip. The newest such chunk
            // in an acknowledgement is the one least likely to have waited for an earlier acknowledgement that was lost.
            if (c.Sent && c.Transmissions == 1 && !c.Retransmit && !c.Abandoned && (sample == null || c.SentAt > sample.SentAt)) sample = c;
        }

        private void MeasureRtt(double r)
        {
            if (r < 0) return;
            if (!_hasRtt) { _srtt = r; _rttvar = r / 2; _hasRtt = true; }
            else
            {
                _rttvar = 0.75 * _rttvar + 0.25 * Math.Abs(_srtt - r);
                _srtt = 0.875 * _srtt + 0.125 * r;
            }
            _rto = Math.Clamp(_srtt + 4 * _rttvar, MinRto, MaxRto);
        }

        private void MarkRetransmit(OutChunk c)
        {
            c.Retransmit = true;
            if (!c.InFlight) return;
            c.InFlight = false;
            _flightBytes -= c.Data.Length;
        }

        /// <summary>Give up on an unreliable message: every fragment of it, since a partial message is useless to the peer.</summary>
        private void Abandon(int index)
        {
            uint message = _outstanding[index].Message;
            for (int i = index; i >= 0 && _outstanding[i].Message == message; i--) _outstanding[i].Abandoned = true;
            for (int i = index + 1; i < _outstanding.Count && _outstanding[i].Message == message; i++) _outstanding[i].Abandoned = true;
        }

        /// <summary>
        /// The peer can be told to move its cumulative TSN over the abandoned chunks right after its last acknowledgement,
        /// and over the chunks between them it already has (gap-acknowledged), so one FORWARD-TSN clears every hole it can.
        /// </summary>
        private void UpdateAdvancedPeerAckPoint(double now)
        {
            uint point = _lastCumAck;
            for (int i = 0; i < _outstanding.Count && (_outstanding[i].Abandoned || _outstanding[i].Acked); i++) point = _outstanding[i].Tsn;
            _advancedPeerAckPoint = point;
            // Repeat an unanswered FORWARD-TSN only after an RTO: resending on every acknowledgement ping-pongs with the peer.
            if (After(point, _lastCumAck) && (point != _forwardTsnSentPoint || now - _forwardTsnSentAt > _rto)) _forwardTsnPending = true;
        }

        // ---------------------------------------------------------------------------------------- sending

        private void ChooseLocalParameters()
        {
            do _localTag = (uint)RandomNumberGenerator.GetInt32(int.MaxValue); while (_localTag == 0);
            _initialTsn = (uint)RandomNumberGenerator.GetInt32(int.MaxValue);
            _nextTsn = _initialTsn;
            _lastCumAck = _initialTsn - 1;
            _advancedPeerAckPoint = _forwardTsnSentPoint = _lastCumAck;
        }

        private void ResetReceive(uint peerInitialTsn)
        {
            _peerCumTsn = peerInitialTsn - 1;
            _receivedAbove.Clear();
            _fragments.Clear();
            _fragmentBytes = 0;
            _inStreams.Clear();
        }

        private void Establish()
        {
            State = Status.Established;
            _cwnd = Math.Min(4 * MaxPacketSize, Math.Max(2 * MaxPacketSize, 4380));
            _ssthresh = (int)Math.Min(_peerRwnd, int.MaxValue);
            Established?.Invoke();
        }

        private void Fail()
        {
            if (State == Status.Aborted) return;
            State = Status.Aborted;
            Closed?.Invoke();
        }

        private void SendInit(double now)
        {
            BeginPacket(0);
            int c = BeginChunk(ChunkInit, 0);
            WriteInitFields();
            WriteExtensionParams();
            EndChunk(c);
            EmitPacket();
            _handshakeResendAt = now + InitialRto;
        }

        private void SendCookieEcho(double now)
        {
            BeginPacket(_peerTag);
            int c = BeginChunk(ChunkCookieEcho, 0);
            WriteBytes(_cookie);
            EndChunk(c);
            EmitPacket();
            _handshakeResendAt = now + InitialRto;
        }

        private void SendControl(byte type, ReadOnlySpan<byte> value)
        {
            BeginPacket(_peerTag);
            int c = BeginChunk(type, 0);
            WriteBytes(value);
            EndChunk(c);
            EmitPacket();
        }

        private void WriteInitFields()
        {
            WriteU32(_localTag);
            WriteU32((uint)ReceiveBufferBytes);
            WriteU16(StreamCount);
            WriteU16(StreamCount);
            WriteU32(_initialTsn);
        }

        private void WriteExtensionParams()
        {
            WriteParam(ParamForwardTsnSupported, default);
            WriteParam(ParamSupportedExtensions, stackalloc byte[] { ChunkReconfig, ChunkForwardTsn });
        }

        private static bool ParsePeerExtensions(ReadOnlySpan<byte> parameters)
        {
            bool forwardTsn = false;
            for (int p = 0; p + 4 <= parameters.Length;)
            {
                ushort type = BinaryPrimitives.ReadUInt16BigEndian(parameters.Slice(p));
                int length = BinaryPrimitives.ReadUInt16BigEndian(parameters.Slice(p + 2));
                if (length < 4 || p + length > parameters.Length) break;
                if (type == ParamForwardTsnSupported) forwardTsn = true;
                else if (type == ParamSupportedExtensions && parameters.Slice(p + 4, length - 4).IndexOf(ChunkForwardTsn) >= 0) forwardTsn = true;
                p += (length + 3) & ~3;
            }
            return forwardTsn;
        }

        private void AppendSack()
        {
            _gapScratch.Clear();
            foreach (uint t in _receivedAbove) _gapScratch.Add(t - _peerCumTsn);
            _gapScratch.Sort();
            int blocks = 0;
            for (int i = 0; i < _gapScratch.Count; i++) if (i == 0 || _gapScratch[i] != _gapScratch[i - 1] + 1) blocks++;
            blocks = Math.Min(blocks, MaxGapBlocks);
            EnsureRoom(16 + 4 * blocks);
            int c = BeginChunk(ChunkSack, 0);
            WriteU32(_peerCumTsn);
            WriteU32((uint)Math.Max(0, ReceiveBufferBytes - _fragmentBytes));
            WriteU16((ushort)blocks);
            WriteU16(0);
            int written = 0;
            for (int i = 0; i < _gapScratch.Count && written < blocks;)
            {
                uint start = _gapScratch[i];
                while (i + 1 < _gapScratch.Count && _gapScratch[i + 1] == _gapScratch[i] + 1) i++;
                WriteU16((ushort)start);
                WriteU16((ushort)_gapScratch[i]);
                written++;
                i++;
            }
            EndChunk(c);
            _sackPending = false;
        }

        private void AppendForwardTsn(double now)
        {
            _forwardTsnPending = false;
            if (!After(_advancedPeerAckPoint, _lastCumAck)) return;
            // Ordered abandoned messages also name their stream and highest SSN so the peer's reorder queue moves on.
            var ordered = new Dictionary<ushort, ushort>();
            for (int i = 0; i < _outstanding.Count && !After(_outstanding[i].Tsn, _advancedPeerAckPoint); i++)
            {
                var c = _outstanding[i];
                if ((c.Flags & FlagUnordered) != 0 || !c.Abandoned) continue;
                if (!ordered.TryGetValue(c.Stream, out ushort ssn) || SsnAfter(c.Ssn, ssn)) ordered[c.Stream] = c.Ssn;
            }
            Trace?.Invoke($"FORWARD-TSN out {_advancedPeerAckPoint}");
            EnsureRoom(8 + 4 * ordered.Count);
            int chunk = BeginChunk(ChunkForwardTsn, 0);
            WriteU32(_advancedPeerAckPoint);
            foreach (var kv in ordered) { WriteU16(kv.Key); WriteU16(kv.Value); }
            EndChunk(chunk);
            _forwardTsnSentPoint = _advancedPeerAckPoint;
            _forwardTsnSentAt = now;
        }

        private void AppendData(OutChunk c)
        {
            EnsureRoom(DataHeaderSize + c.Data.Length);
            int chunk = BeginChunk(ChunkData, c.Flags);
            WriteU32(c.Tsn);
            WriteU16(c.Stream);
            WriteU16(c.Ssn);
            WriteU32(c.Ppid);
            WriteBytes(c.Data);
            EndChunk(chunk);
        }

        private void EnsureRoom(int chunkBytes)
        {
            if (_packetLength > 0 && ((_packetLength + 3) & ~3) + chunkBytes > MaxPacketSize) EmitPacket();
            if (_packetLength == 0) BeginPacket(_peerTag);
        }

        private void BeginPacket(uint tag)
        {
            var b = _packet.AsSpan();
            BinaryPrimitives.WriteUInt16BigEndian(b, Port);
            BinaryPrimitives.WriteUInt16BigEndian(b.Slice(2), Port);
            BinaryPrimitives.WriteUInt32BigEndian(b.Slice(4), tag);
            BinaryPrimitives.WriteUInt32BigEndian(b.Slice(8), 0);
            _packetLength = CommonHeaderSize;
        }

        private void EmitPacket()
        {
            if (_packetLength <= CommonHeaderSize) { _packetLength = 0; return; }
            BinaryPrimitives.WriteUInt32LittleEndian(_packet.AsSpan(8), Crc.SctpChecksum(_packet.AsSpan(0, _packetLength)));
            int length = _packetLength;
            _packetLength = 0;
            _output(_packet, length);
        }

        private int BeginChunk(byte type, byte flags)
        {
            Pad();
            int start = _packetLength;
            _packet[start] = type;
            _packet[start + 1] = flags;
            _packetLength += 4;
            return start;
        }

        /// <summary>The length field excludes the chunk's trailing padding, which the next chunk (or the packet end) adds.</summary>
        private void EndChunk(int start)
        {
            BinaryPrimitives.WriteUInt16BigEndian(_packet.AsSpan(start + 2), (ushort)(_packetLength - start));
            Pad();
        }

        private void WriteParam(ushort type, ReadOnlySpan<byte> value)
        {
            Pad();
            WriteU16(type);
            WriteU16((ushort)(4 + value.Length));
            WriteBytes(value);
        }

        private void Pad()
        {
            while ((_packetLength & 3) != 0) _packet[_packetLength++] = 0;
        }

        private void WriteU16(ushort v) { BinaryPrimitives.WriteUInt16BigEndian(_packet.AsSpan(_packetLength), v); _packetLength += 2; }
        private void WriteU32(uint v) { BinaryPrimitives.WriteUInt32BigEndian(_packet.AsSpan(_packetLength), v); _packetLength += 4; }
        private void WriteBytes(ReadOnlySpan<byte> v) { v.CopyTo(_packet.AsSpan(_packetLength)); _packetLength += v.Length; }

        /// <summary>TSN order with wraparound: is <paramref name="a"/> after <paramref name="b"/>?</summary>
        private static bool After(uint a, uint b) => a != b && a - b < 0x80000000u;
        private static bool SsnAfter(ushort a, ushort b) => a != b && (ushort)(a - b) < 0x8000;
    }
}
