using System;
using System.Collections.Generic;
using System.Reflection;

namespace Nebula
{
    /// <summary>Runs on every client that knows the entity (or only its owner when sent with OwnerRpc).</summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class ClientRpcAttribute : Attribute { }

    /// <summary>Sent by the owning client, runs on whichever worker currently has authority over the entity.</summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class ServerRpcAttribute : Attribute { }

    /// <summary>
    /// Runs on whichever worker has authority over the entity, no matter which worker calls it. If the caller is
    /// authoritative it is a direct call; if the caller only holds a ghost, it crosses the lateral link. This is
    /// the primitive behind cross-container interactions such as damage claims.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class AuthorityRpcAttribute : Attribute { }

    internal enum RpcKind : byte { Client, Server, Authority }

    internal sealed class RpcMethod
    {
        public MethodInfo Method;
        public Type[] ParameterTypes;
        public RpcKind Kind;
        public uint Hash;
        public string Name;
    }

    /// <summary>Reflection-based RPC table. A source generator can replace this later without changing gameplay code.</summary>
    internal static class RpcRegistry
    {
        private static readonly Dictionary<Type, Dictionary<uint, RpcMethod>> Tables = new Dictionary<Type, Dictionary<uint, RpcMethod>>();

        public static uint Hash(string name)
        {
            unchecked
            {
                uint h = 2166136261;
                foreach (char c in name)
                {
                    h ^= c;
                    h *= 16777619;
                }
                return h;
            }
        }

        private static Dictionary<uint, RpcMethod> TableFor(Type type)
        {
            if (Tables.TryGetValue(type, out var table)) return table;
            table = new Dictionary<uint, RpcMethod>();
            for (var t = type; t != null && t != typeof(NetworkBehaviour); t = t.BaseType)
            {
                foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    RpcKind kind;
                    if (m.GetCustomAttribute<ClientRpcAttribute>() != null) kind = RpcKind.Client;
                    else if (m.GetCustomAttribute<ServerRpcAttribute>() != null) kind = RpcKind.Server;
                    else if (m.GetCustomAttribute<AuthorityRpcAttribute>() != null) kind = RpcKind.Authority;
                    else continue;

                    var ps = m.GetParameters();
                    var types = new Type[ps.Length];
                    for (int i = 0; i < ps.Length; i++)
                    {
                        types[i] = ps[i].ParameterType;
                        if (!NetworkSerialization.CanSerialize(types[i]))
                            throw new InvalidOperationException($"RPC {t.Name}.{m.Name}: parameter '{ps[i].Name}' of type {types[i].Name} is not network-serialisable");
                    }
                    uint hash = Hash(m.Name);
                    if (table.ContainsKey(hash))
                        throw new InvalidOperationException($"RPC name collision on {t.Name}: {m.Name} (overloads are not supported)");
                    table[hash] = new RpcMethod { Method = m, ParameterTypes = types, Kind = kind, Hash = hash, Name = m.Name };
                }
            }
            Tables[type] = table;
            return table;
        }

        public static RpcMethod Lookup(Type type, uint hash)
        {
            return TableFor(type).TryGetValue(hash, out var m) ? m : null;
        }

        public static RpcMethod Require(Type type, string name)
        {
            var m = Lookup(type, Hash(name));
            if (m == null) throw new InvalidOperationException($"{type.Name}.{name} is not marked [ClientRpc]/[ServerRpc]/[AuthorityRpc]");
            return m;
        }

        public static void WriteArgs(NetworkWriter writer, RpcMethod method, object[] args)
        {
            for (int i = 0; i < method.ParameterTypes.Length; i++)
            {
                NetworkSerialization.WriteObject(writer, method.ParameterTypes[i], args[i]);
            }
        }

        public static void Invoke(NetworkBehaviour target, uint hash, NetworkReader args)
        {
            var m = Lookup(target.GetType(), hash);
            if (m == null)
            {
                NebulaLog.Warn($"Unknown RPC hash {hash} on {target.GetType().Name}");
                return;
            }
            var values = new object[m.ParameterTypes.Length];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = NetworkSerialization.ReadObject(args, m.ParameterTypes[i]);
            }
            try
            {
                m.Method.Invoke(target, values);
            }
            catch (TargetInvocationException e)
            {
                NebulaLog.Error($"RPC {target.GetType().Name}.{m.Name} threw: {e.InnerException}");
            }
        }
    }
}
