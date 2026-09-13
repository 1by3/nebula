using System;
using System.Collections.Generic;

namespace Nebula
{
    /// <summary>Defines the serialization and change tracking used by every <see cref="NetworkVariable{T}"/>.</summary>
    public abstract class NetworkVariableBase
    {
        internal NetworkBehaviour Owner;
        internal int Index;
        internal bool Dirty;

        /// <summary>
        /// Discovered name of this variable, <c>"&lt;BehaviourTypeName&gt;.&lt;FieldName&gt;"</c>. Set when the entity
        /// is initialised and used to key the variable in the persistence blob (<see cref="PersistentStateCodec"/>),
        /// so a save survives fields being added, removed or reordered. Replication never sends it.
        /// </summary>
        public string Name { get; internal set; } = "";

        /// <summary>The field carries <see cref="PersistAttribute"/>: its value is part of the entity's saved state.</summary>
        public bool Persist { get; internal set; }

        /// <summary>Assigned since the last time this variable was written into a persistence blob.</summary>
        public bool PersistDirty { get; internal set; }

        public abstract void Write(NetworkWriter writer);
        public abstract void Read(NetworkReader reader);
    }

    /// <summary>
    /// A field replicated from the authoritative worker to clients and ghost workers. Declare it on a
    /// <see cref="NetworkBehaviour"/> and Nebula discovers it.
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
                if (Persist)
                {
                    // Opted into persistence: the entity is due a checkpoint sooner than its timer would have asked.
                    PersistDirty = true;
                    Owner?.Identity?.Persistent?.MarkDirty();
                }
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
