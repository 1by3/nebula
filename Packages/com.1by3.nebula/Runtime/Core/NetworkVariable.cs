using System;
using System.Collections.Generic;

namespace Nebula
{
    public abstract class NetworkVariableBase
    {
        internal NetworkBehaviour Owner;
        internal int Index;
        internal bool Dirty;

        public abstract void Write(NetworkWriter writer);
        public abstract void Read(NetworkReader reader);
    }

    /// <summary>
    /// Replicated field, NGO-style: declare it on a <see cref="NetworkBehaviour"/> and Nebula discovers it.
    /// Only the authoritative worker may assign <see cref="Value"/>; every other copy (ghosts on neighbouring
    /// workers, clients) receives it and raises <see cref="OnValueChanged"/>.
    /// <code>
    /// public NetworkVariable&lt;float&gt; Health = new NetworkVariable&lt;float&gt;(100f);
    /// </code>
    /// </summary>
    public sealed class NetworkVariable<T> : NetworkVariableBase
    {
        private T _value;

        public NetworkVariable() { }

        public NetworkVariable(T initialValue)
        {
            _value = initialValue;
        }

        public event Action<T, T> OnValueChanged;

        public T Value
        {
            get => _value;
            set
            {
                if (Owner != null && Owner.Identity != null && Owner.Identity.IsSpawned && !Owner.HasAuthority)
                {
                    NebulaLog.Warn($"NetworkVariable on {Owner.GetType().Name} written without authority (netId {Owner.NetId}); ignored");
                    return;
                }
                if (EqualityComparer<T>.Default.Equals(_value, value)) return;
                var old = _value;
                _value = value;
                Dirty = true;
                Owner?.Identity?.MarkVarsDirty();
                OnValueChanged?.Invoke(old, value);
            }
        }

        public override void Write(NetworkWriter writer)
        {
            NetworkSerialization.Write(writer, _value);
        }

        public override void Read(NetworkReader reader)
        {
            var old = _value;
            _value = NetworkSerialization.Read<T>(reader);
            if (!EqualityComparer<T>.Default.Equals(old, _value))
            {
                OnValueChanged?.Invoke(old, _value);
            }
        }

        public override string ToString() => _value?.ToString() ?? "null";

        public static implicit operator T(NetworkVariable<T> v) => v._value;
    }
}
