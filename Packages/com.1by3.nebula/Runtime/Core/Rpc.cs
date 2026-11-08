using System;
using System.Collections.Generic;
using System.Reflection;

namespace Nebula
{
    /// <summary>Runs on every client that knows the entity (or only its owner when sent with OwnerRpc).</summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class ClientRpcAttribute : Attribute
    {
        /// <summary>
        /// Metres. When set, the gateway delivers a broadcast of this RPC only to clients whose pawn is within this
        /// distance of the entity (a tracer, a footstep, a muzzle flash: nobody across the map needs it). 0 (the
        /// default) reaches every client. Targeted sends (OwnerRpc) are never filtered.
        /// </summary>
        public float Radius;

        /// <summary>
        /// How the call travels from the worker to the client (through the gateway), for a broadcast and for
        /// <c>OwnerRpc</c> alike. <see cref="Nebula.Delivery.ReliableOrdered"/> (the default) arrives exactly once, in
        /// order with the other reliable traffic. <see cref="Nebula.Delivery.Sequenced"/> is for a message only useful
        /// if it is fast, such as a provisional hit marker: it is not retransmitted when its packet is lost, and one
        /// overtaken by a newer sequenced packet is dropped, so a lost packet never holds it up. A sequenced call may
        /// therefore never arrive, and its order relative to reliable RPCs, spawns and state is not guaranteed: it can
        /// arrive before or after a reliable message that was sent earlier or later. A call whose arguments do not fit
        /// one small packet (about 400 bytes with the header) is sent reliable instead. Nebula has no separate
        /// unordered-unreliable delivery class; sequenced is the fastest one.
        /// </summary>
        public Delivery Delivery = Delivery.ReliableOrdered;
    }

    /// <summary>Sent by the owning client, runs on whichever worker currently has authority over the entity.</summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class ServerRpcAttribute : Attribute { }

    /// <summary>
    /// Runs on the worker that has authority over the entity. If the caller has authority, Nebula runs the method
    /// directly. If the caller holds only a ghost, Nebula sends the call to the authoritative worker. Use it for
    /// cross-container interactions such as damage requests.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class AuthorityRpcAttribute : Attribute { }

    /// <summary>
    /// Sent by any client to any entity in its interest set, owned or not, and run on the worker with authority over the
    /// entity: the way a player touches, sits on, takes or edits an object that is not theirs. The first parameter of the
    /// method must be a <see cref="ClientRequestContext"/>, which Nebula fills in with who sent the call; the rest are the
    /// arguments the client sent.
    /// <para>
    /// <b>Everything the client sent is untrusted.</b> Nebula only guarantees who the sender is (the gateway stamped it
    /// from the connection), that the entity was in the sender's interest set when the gateway admitted it, and that the
    /// call was within the size and rate limits. Whether the sender may do this, is near enough, owns what they say they own,
    /// and sent sane numbers is the handler's to check, every time. A handler that trusts an argument is an exploit.
    /// </para>
    /// <para>
    /// Send it with <c>ClientRequest</c> or <c>ClientRequestWithReply</c> from a <see cref="NetworkBehaviour"/> on the client. See
    /// the Client requests guide.
    /// </para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class ClientRequestRpcAttribute : Attribute { }

    internal enum RpcKind : byte { Client, Server, Authority, ClientRequest }

    internal sealed class RpcMethod
    {
        public MethodInfo Method;
        public Type[] ParameterTypes;
        /// <summary>ClientRequest only: the method's first parameter is a <see cref="ClientRequestContext"/>, which is not on the wire and is not in <see cref="ParameterTypes"/>.</summary>
        public bool TakesContext;
        public RpcKind Kind;
        public uint Hash;
        public string Name;
        /// <summary>ClientRpc only: <see cref="ClientRpcAttribute.Radius"/>.</summary>
        public float Radius;
        /// <summary>ClientRpc only: <see cref="ClientRpcAttribute.Delivery"/>, normalised to a class the transport has.</summary>
        public Delivery Delivery = Delivery.ReliableOrdered;
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
                    float radius = 0f;
                    var delivery = Delivery.ReliableOrdered;
                    var clientAttr = m.GetCustomAttribute<ClientRpcAttribute>();
                    if (clientAttr != null) { kind = RpcKind.Client; radius = clientAttr.Radius; delivery = clientAttr.Delivery == Delivery.Sequenced ? Delivery.Sequenced : Delivery.ReliableOrdered; }
                    else if (m.GetCustomAttribute<ServerRpcAttribute>() != null) kind = RpcKind.Server;
                    else if (m.GetCustomAttribute<AuthorityRpcAttribute>() != null) kind = RpcKind.Authority;
                    else if (m.GetCustomAttribute<ClientRequestRpcAttribute>() != null) kind = RpcKind.ClientRequest;
                    else continue;

                    var ps = m.GetParameters();
                    // A client request's first parameter is the sender, filled in by Nebula and never read from the wire.
                    bool takesContext = kind == RpcKind.ClientRequest;
                    if (takesContext && (ps.Length == 0 || ps[0].ParameterType != typeof(ClientRequestContext)))
                        throw new InvalidOperationException($"[ClientRequestRpc] {t.Name}.{m.Name}: the first parameter must be a ClientRequestContext, which carries who sent the call");
                    int skip = takesContext ? 1 : 0;
                    var types = new Type[ps.Length - skip];
                    for (int i = skip; i < ps.Length; i++)
                    {
                        types[i - skip] = ps[i].ParameterType;
                        if (!NetworkSerialization.CanSerialize(ps[i].ParameterType))
                            throw new InvalidOperationException($"RPC {t.Name}.{m.Name}: parameter '{ps[i].Name}' of type {ps[i].ParameterType.Name} is not network-serialisable");
                    }
                    uint hash = Hash(m.Name);
                    if (table.ContainsKey(hash))
                        throw new InvalidOperationException($"RPC name collision on {t.Name}: {m.Name} (overloads are not supported)");
                    table[hash] = new RpcMethod { Method = m, ParameterTypes = types, Kind = kind, TakesContext = takesContext, Hash = hash, Name = m.Name, Radius = radius, Delivery = delivery };
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
            if (m == null) throw new InvalidOperationException($"{type.Name}.{name} is not marked [ClientRpc]/[ServerRpc]/[AuthorityRpc]/[ClientRequestRpc]");
            return m;
        }

        public static void WriteArgs(NetworkWriter writer, RpcMethod method, object[] args)
        {
            for (int i = 0; i < method.ParameterTypes.Length; i++)
            {
                NetworkSerialization.WriteObject(writer, method.ParameterTypes[i], args[i]);
            }
        }

        /// <summary>
        /// Run a client request's handler. Only a <c>[ClientRequestRpc]</c> method can be reached this way: the hash comes
        /// from a client, and a client must not be able to name a <c>[ClientRpc]</c>, <c>[ServerRpc]</c> or <c>[AuthorityRpc]</c>
        /// by guessing its hash. Never throws for what a client sent; the outcome says what went wrong.
        /// </summary>
        public static ClientRequestOutcome InvokeClientRequest(NetworkBehaviour target, uint hash, NetworkReader args, ClientRequestContext context)
        {
            var m = Lookup(target.GetType(), hash);
            if (m == null || m.Kind != RpcKind.ClientRequest)
            {
                NebulaLog.Warn($"client request for {target.GetType().Name}: method {hash:x} is not a [ClientRequestRpc]; dropped");
                return ClientRequestOutcome.RejectedNoHandler;
            }
            var values = new object[m.ParameterTypes.Length + 1];
            values[0] = context;
            try
            {
                for (int i = 0; i < m.ParameterTypes.Length; i++) values[i + 1] = NetworkSerialization.ReadObject(args, m.ParameterTypes[i]);
            }
            catch (Exception e)
            {
                NebulaLog.Warn($"client request {target.GetType().Name}.{m.Name} from client {context.ClientId}: the arguments did not decode ({e.GetType().Name}: {e.Message}); dropped");
                return ClientRequestOutcome.RejectedBadArguments;
            }
            try
            {
                m.Method.Invoke(target, values);
                return ClientRequestOutcome.Accepted;
            }
            catch (TargetInvocationException e)
            {
                NebulaLog.Error($"client request {target.GetType().Name}.{m.Name} from client {context.ClientId} threw: {e.InnerException}");
                return ClientRequestOutcome.HandlerThrew;
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
            if (m.Kind == RpcKind.ClientRequest)
            {
                // Its first parameter is the sender, which only InvokeClientRequest supplies. Reaching it through ServerRpc or AuthorityRpc would skip that.
                NebulaLog.Warn($"RPC {target.GetType().Name}.{m.Name} is a [ClientRequestRpc] and cannot be called as another kind; dropped");
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
