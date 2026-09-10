using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nebula
{
    /// <summary>Implement on structs/classes you want to put in a <see cref="NetworkVariable{T}"/> or an RPC argument.</summary>
    public interface INetworkSerializable
    {
        void Serialize(NetworkWriter writer);
        void Deserialize(NetworkReader reader);
    }

    /// <summary>
    /// Type-driven (de)serialisation used by NetworkVariables and RPC arguments. Built-in support for the
    /// primitives, strings, Unity vectors/quaternions/colours, enums, byte[] and anything implementing
    /// <see cref="INetworkSerializable"/>. Register custom types with <see cref="Register{T}"/>.
    /// </summary>
    public static class NetworkSerialization
    {
        private static readonly Dictionary<Type, Action<NetworkWriter, object>> Writers = new Dictionary<Type, Action<NetworkWriter, object>>();
        private static readonly Dictionary<Type, Func<NetworkReader, object>> Readers = new Dictionary<Type, Func<NetworkReader, object>>();

        static NetworkSerialization()
        {
            Register<byte>((w, v) => w.WriteByte(v), r => r.ReadByte());
            Register<sbyte>((w, v) => w.WriteSByte(v), r => r.ReadSByte());
            Register<bool>((w, v) => w.WriteBool(v), r => r.ReadBool());
            Register<short>((w, v) => w.WriteShort(v), r => r.ReadShort());
            Register<ushort>((w, v) => w.WriteUShort(v), r => r.ReadUShort());
            Register<int>((w, v) => w.WriteInt(v), r => r.ReadInt());
            Register<uint>((w, v) => w.WriteUInt(v), r => r.ReadUInt());
            Register<long>((w, v) => w.WriteLong(v), r => r.ReadLong());
            Register<ulong>((w, v) => w.WriteULong(v), r => r.ReadULong());
            Register<float>((w, v) => w.WriteFloat(v), r => r.ReadFloat());
            Register<double>((w, v) => w.WriteDouble(v), r => r.ReadDouble());
            Register<string>((w, v) => w.WriteString(v), r => r.ReadString());
            Register<Vector2>((w, v) => w.WriteVector2(v), r => r.ReadVector2());
            Register<Vector3>((w, v) => w.WriteVector3(v), r => r.ReadVector3());
            Register<Quaternion>((w, v) => w.WriteQuaternion(v), r => r.ReadQuaternion());
            Register<Color>((w, v) => w.WriteColor(v), r => r.ReadColor());
            Register<byte[]>((w, v) => w.WriteBytes(v), r => r.ReadBytes());
        }

        public static void Register<T>(Action<NetworkWriter, T> write, Func<NetworkReader, T> read)
        {
            Writers[typeof(T)] = (w, o) => write(w, (T)o);
            Readers[typeof(T)] = r => read(r);
        }

        public static bool CanSerialize(Type type)
        {
            if (Writers.ContainsKey(type)) return true;
            if (type.IsEnum) return true;
            if (typeof(INetworkSerializable).IsAssignableFrom(type)) return true;
            return false;
        }

        public static void Write<T>(NetworkWriter writer, T value) => WriteObject(writer, typeof(T), value);

        public static T Read<T>(NetworkReader reader) => (T)ReadObject(reader, typeof(T));

        public static void WriteObject(NetworkWriter writer, Type type, object value)
        {
            if (Writers.TryGetValue(type, out var w))
            {
                w(writer, value);
                return;
            }
            if (type.IsEnum)
            {
                writer.WriteInt(Convert.ToInt32(value));
                return;
            }
            if (value is INetworkSerializable s)
            {
                s.Serialize(writer);
                return;
            }
            if (typeof(INetworkSerializable).IsAssignableFrom(type) && value == null)
            {
                throw new InvalidOperationException($"Cannot serialise a null {type.Name}");
            }
            throw new InvalidOperationException($"No network serializer registered for {type.FullName}. Implement INetworkSerializable or call NetworkSerialization.Register.");
        }

        public static object ReadObject(NetworkReader reader, Type type)
        {
            if (Readers.TryGetValue(type, out var r))
            {
                return r(reader);
            }
            if (type.IsEnum)
            {
                return Enum.ToObject(type, reader.ReadInt());
            }
            if (typeof(INetworkSerializable).IsAssignableFrom(type))
            {
                var instance = (INetworkSerializable)Activator.CreateInstance(type);
                instance.Deserialize(reader);
                return instance;
            }
            throw new InvalidOperationException($"No network serializer registered for {type.FullName}.");
        }
    }
}
