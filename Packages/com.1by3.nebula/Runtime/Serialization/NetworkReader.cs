using System;
using System.Text;
using UnityEngine;

namespace Nebula
{
    /// <summary>Counterpart of <see cref="NetworkWriter"/>. Throws on under-read so malformed packets fail loudly.</summary>
    public sealed class NetworkReader
    {
        private byte[] _buffer;
        private int _position;
        private int _end;

        public NetworkReader() { }

        public NetworkReader(ArraySegment<byte> segment)
        {
            Set(segment);
        }

        public NetworkReader(byte[] bytes)
        {
            Set(new ArraySegment<byte>(bytes));
        }

        public void Set(ArraySegment<byte> segment)
        {
            _buffer = segment.Array;
            _position = segment.Offset;
            _end = segment.Offset + segment.Count;
        }

        public int Remaining => _end - _position;
        public int Position => _position;

        /// <summary>Take the next <paramref name="length"/> bytes as a segment (no copy) and advance past them.</summary>
        public ArraySegment<byte> ReadSegment(int length)
        {
            if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
            Need(length);
            var seg = new ArraySegment<byte>(_buffer, _position, length);
            _position += length;
            return seg;
        }

        /// <summary>Advance past <paramref name="bytes"/> bytes without reading them.</summary>
        public void Skip(int bytes)
        {
            if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
            Need(bytes);
            _position += bytes;
        }

        private void Need(int bytes)
        {
            if (_position + bytes > _end)
                throw new InvalidOperationException($"NetworkReader: tried to read {bytes} bytes with {Remaining} remaining");
        }

        public byte ReadByte()
        {
            Need(1);
            return _buffer[_position++];
        }

        public bool ReadBool() => ReadByte() != 0;

        public sbyte ReadSByte() => (sbyte)ReadByte();

        public ushort ReadUShort()
        {
            Need(2);
            ushort v = (ushort)(_buffer[_position] | (_buffer[_position + 1] << 8));
            _position += 2;
            return v;
        }

        public short ReadShort() => (short)ReadUShort();

        public uint ReadUInt()
        {
            Need(4);
            uint v = (uint)(_buffer[_position] | (_buffer[_position + 1] << 8) | (_buffer[_position + 2] << 16) | (_buffer[_position + 3] << 24));
            _position += 4;
            return v;
        }

        public int ReadInt() => (int)ReadUInt();

        public ulong ReadULong()
        {
            ulong lo = ReadUInt();
            ulong hi = ReadUInt();
            return lo | (hi << 32);
        }

        public long ReadLong() => (long)ReadULong();

        public float ReadFloat() => BitConverter.Int32BitsToSingle((int)ReadUInt());

        public double ReadDouble() => BitConverter.Int64BitsToDouble((long)ReadULong());

        public string ReadString()
        {
            int len = ReadUShort();
            if (len == 0) return null;
            len -= 1;
            Need(len);
            string s = Encoding.UTF8.GetString(_buffer, _position, len);
            _position += len;
            return s;
        }

        /// <summary>Length-prefixed blob. Returns a segment into the underlying buffer - copy if you keep it.</summary>
        public ArraySegment<byte> ReadBytesSegment()
        {
            int len = ReadUShort();
            Need(len);
            var seg = new ArraySegment<byte>(_buffer, _position, len);
            _position += len;
            return seg;
        }

        public byte[] ReadBytes()
        {
            var seg = ReadBytesSegment();
            var arr = new byte[seg.Count];
            Buffer.BlockCopy(seg.Array, seg.Offset, arr, 0, seg.Count);
            return arr;
        }

        public ArraySegment<byte> ReadRemaining()
        {
            var seg = new ArraySegment<byte>(_buffer, _position, Remaining);
            _position = _end;
            return seg;
        }

        public Vector2 ReadVector2() => new Vector2(ReadFloat(), ReadFloat());

        public Vector3 ReadVector3() => new Vector3(ReadFloat(), ReadFloat(), ReadFloat());

        public Quaternion ReadQuaternion() => new Quaternion(ReadFloat(), ReadFloat(), ReadFloat(), ReadFloat());

        public float ReadHalf() => Mathf.HalfToFloat(ReadUShort());

        public Quaternion ReadCompressedQuaternion()
        {
            uint packed = ReadUInt();
            int largest = (int)(packed >> 30);
            float a = NetworkWriter.DequantizeSmallestThree((int)((packed >> 20) & 0x3FF));
            float b = NetworkWriter.DequantizeSmallestThree((int)((packed >> 10) & 0x3FF));
            float c = NetworkWriter.DequantizeSmallestThree((int)(packed & 0x3FF));
            float d = Mathf.Sqrt(Mathf.Max(0f, 1f - a * a - b * b - c * c));
            switch (largest)
            {
                case 0: return new Quaternion(d, a, b, c);
                case 1: return new Quaternion(a, d, b, c);
                case 2: return new Quaternion(a, b, d, c);
                default: return new Quaternion(a, b, c, d);
            }
        }

        public Color ReadColor() => new Color(ReadFloat(), ReadFloat(), ReadFloat(), ReadFloat());
    }
}
