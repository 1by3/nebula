using System;
using System.Text;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// Growable little-endian byte writer used for every Nebula message. Deliberately plain:
    /// no bit packing or quantisation yet - bandwidth is not the prototype's problem, correctness is.
    /// Reuse instances; call <see cref="Reset"/> between messages.
    /// </summary>
    public sealed class NetworkWriter
    {
        private byte[] _buffer;
        private int _position;

        public NetworkWriter(int capacity = 1024)
        {
            _buffer = new byte[Math.Max(16, capacity)];
        }

        public int Position => _position;
        public int Length => _position;
        public byte[] Buffer => _buffer;

        public void Reset() => _position = 0;

        /// <summary>Drop everything written after <paramref name="length"/> (a value from <see cref="Length"/>).</summary>
        public void Rewind(int length)
        {
            if (length < 0 || length > _position) throw new ArgumentOutOfRangeException(nameof(length));
            _position = length;
        }

        public ArraySegment<byte> ToSegment() => new ArraySegment<byte>(_buffer, 0, _position);

        public byte[] ToArray()
        {
            var result = new byte[_position];
            System.Buffer.BlockCopy(_buffer, 0, result, 0, _position);
            return result;
        }

        private void Ensure(int bytes)
        {
            int needed = _position + bytes;
            if (needed <= _buffer.Length) return;
            int size = _buffer.Length * 2;
            while (size < needed) size *= 2;
            Array.Resize(ref _buffer, size);
        }

        public void WriteByte(byte value)
        {
            Ensure(1);
            _buffer[_position++] = value;
        }

        public void WriteBool(bool value) => WriteByte(value ? (byte)1 : (byte)0);

        public void WriteSByte(sbyte value) => WriteByte((byte)value);

        public void WriteUShort(ushort value)
        {
            Ensure(2);
            _buffer[_position++] = (byte)value;
            _buffer[_position++] = (byte)(value >> 8);
        }

        public void WriteShort(short value) => WriteUShort((ushort)value);

        public void WriteUInt(uint value)
        {
            Ensure(4);
            _buffer[_position++] = (byte)value;
            _buffer[_position++] = (byte)(value >> 8);
            _buffer[_position++] = (byte)(value >> 16);
            _buffer[_position++] = (byte)(value >> 24);
        }

        public void WriteInt(int value) => WriteUInt((uint)value);

        public void WriteULong(ulong value)
        {
            WriteUInt((uint)value);
            WriteUInt((uint)(value >> 32));
        }

        public void WriteLong(long value) => WriteULong((ulong)value);

        public void WriteFloat(float value)
        {
            WriteUInt((uint)BitConverter.SingleToInt32Bits(value));
        }

        public void WriteDouble(double value)
        {
            WriteULong((ulong)BitConverter.DoubleToInt64Bits(value));
        }

        public void WriteString(string value)
        {
            if (value == null)
            {
                WriteUShort(0);
                return;
            }
            int byteCount = Encoding.UTF8.GetByteCount(value);
            if (byteCount > ushort.MaxValue - 1) throw new ArgumentException("string too long for the wire");
            WriteUShort((ushort)(byteCount + 1));
            Ensure(byteCount);
            Encoding.UTF8.GetBytes(value, 0, value.Length, _buffer, _position);
            _position += byteCount;
        }

        /// <summary>Length-prefixed byte blob (ushort length).</summary>
        public void WriteBytes(ArraySegment<byte> bytes)
        {
            if (bytes.Array == null)
            {
                WriteUShort(0);
                return;
            }
            if (bytes.Count > ushort.MaxValue) throw new ArgumentException("blob too long for the wire");
            WriteUShort((ushort)bytes.Count);
            WriteRaw(bytes);
        }

        public void WriteBytes(byte[] bytes) => WriteBytes(bytes == null ? default : new ArraySegment<byte>(bytes));

        /// <summary>Raw bytes with no length prefix.</summary>
        public void WriteRaw(ArraySegment<byte> bytes)
        {
            if (bytes.Array == null || bytes.Count == 0) return;
            Ensure(bytes.Count);
            System.Buffer.BlockCopy(bytes.Array, bytes.Offset, _buffer, _position, bytes.Count);
            _position += bytes.Count;
        }

        public void WriteVector2(Vector2 v)
        {
            WriteFloat(v.x);
            WriteFloat(v.y);
        }

        public void WriteVector3(Vector3 v)
        {
            WriteFloat(v.x);
            WriteFloat(v.y);
            WriteFloat(v.z);
        }

        public void WriteQuaternion(Quaternion q)
        {
            WriteFloat(q.x);
            WriteFloat(q.y);
            WriteFloat(q.z);
            WriteFloat(q.w);
        }

        /// <summary>IEEE half (2 bytes). ~3 significant digits: fine for positions within a few hundred metres and for unit-range values.</summary>
        public void WriteHalf(float value) => WriteUShort(Mathf.FloatToHalf(value));

        /// <summary>
        /// Smallest-three quaternion compression: 4 bytes instead of 16. The largest component is dropped (its sign is
        /// normalised away) and the other three are quantised to 10 bits each in [-1/sqrt2, 1/sqrt2]; worst-case error
        /// is about 0.1 degrees.
        /// </summary>
        public void WriteCompressedQuaternion(Quaternion q)
        {
            q.Normalize();
            int largest = 0;
            float lx = Mathf.Abs(q.x), ly = Mathf.Abs(q.y), lz = Mathf.Abs(q.z), lw = Mathf.Abs(q.w);
            float max = lx;
            if (ly > max) { max = ly; largest = 1; }
            if (lz > max) { max = lz; largest = 2; }
            if (lw > max) { max = lw; largest = 3; }
            float sign = (largest == 0 ? q.x : largest == 1 ? q.y : largest == 2 ? q.z : q.w) < 0f ? -1f : 1f;
            uint packed = (uint)largest << 30;
            int shift = 20;
            for (int i = 0; i < 4; i++)
            {
                if (i == largest) continue;
                float v = (i == 0 ? q.x : i == 1 ? q.y : i == 2 ? q.z : q.w) * sign;
                packed |= (uint)QuantizeSmallestThree(v) << shift;
                shift -= 10;
            }
            WriteUInt(packed);
        }

        private const float SmallestThreeRange = 0.70710678f; // 1/sqrt(2)

        internal static int QuantizeSmallestThree(float v)
        {
            float t = (Mathf.Clamp(v, -SmallestThreeRange, SmallestThreeRange) + SmallestThreeRange) / (2f * SmallestThreeRange);
            return Mathf.Clamp(Mathf.RoundToInt(t * 1023f), 0, 1023);
        }

        internal static float DequantizeSmallestThree(int q) => (q / 1023f) * (2f * SmallestThreeRange) - SmallestThreeRange;

        public void WriteColor(Color c)
        {
            WriteFloat(c.r);
            WriteFloat(c.g);
            WriteFloat(c.b);
            WriteFloat(c.a);
        }

        /// <summary>Reserve a ushort slot to be patched later (e.g. a count written after a loop).</summary>
        public int ReserveUShort()
        {
            int at = _position;
            WriteUShort(0);
            return at;
        }

        public void PatchUShort(int at, ushort value)
        {
            _buffer[at] = (byte)value;
            _buffer[at + 1] = (byte)(value >> 8);
        }
    }
}
