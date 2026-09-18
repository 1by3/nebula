using System;
using System.Collections.Generic;

namespace Nebula
{
    /// <summary>
    /// The state blob of a <see cref="PersistedEntityRecord"/>: every <see cref="PersistAttribute"/> NetworkVariable
    /// and every behaviour's <see cref="NetworkBehaviour.WritePersistentState"/> chunk, each under a name.
    /// <para>
    /// Named entries rather than a positional layout, because a save outlives the build that wrote it: a variable
    /// that was removed is skipped on read, one that was added keeps its default, and a renamed behaviour simply
    /// starts fresh. Each entry is length-prefixed and read through a reader bounded to its own bytes, so a
    /// behaviour that reads too much fails inside its chunk (logged) instead of corrupting the next entry.
    /// </para>
    /// <code>
    /// [version:byte=1][count:ushort] { [name:string][length:ushort][bytes] }
    /// </code>
    /// Pure functions over a <see cref="NetworkIdentity"/>: no worker, no store, no scene.
    /// </summary>
    public static class PersistentStateCodec
    {
        /// <summary>Format version written at the head of every blob. A blob with a newer version is ignored on read.</summary>
        public const byte Version = 1;

        /// <summary>Suffix of the entry holding a behaviour's own <see cref="NetworkBehaviour.WritePersistentState"/> bytes.</summary>
        public const string BehaviourStateSuffix = "#state";

        private static readonly NetworkWriter Scratch = new NetworkWriter(512);
        private static readonly NetworkReader ChunkReader = new NetworkReader(Array.Empty<byte>());
        private static readonly Dictionary<string, NetworkVariableBase> VarsByName = new Dictionary<string, NetworkVariableBase>();
        private static readonly Dictionary<string, NetworkBehaviour> BehavioursByName = new Dictionary<string, NetworkBehaviour>();

        /// <summary>The entry name of a behaviour's own state chunk.</summary>
        public static string StateNameOf(NetworkBehaviour behaviour) => behaviour.GetType().Name + BehaviourStateSuffix;

        /// <summary>
        /// Write <paramref name="identity"/>'s persistent state. Also clears the per-variable dirty flags: what is in
        /// the blob has been captured, whatever the store then does with the record.
        /// </summary>
        public static void Write(NetworkWriter writer, NetworkIdentity identity)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            if (identity == null) throw new ArgumentNullException(nameof(identity));
            writer.WriteByte(Version);
            int countAt = writer.ReserveUShort();
            ushort count = 0;

            var behaviours = identity.Behaviours;
            for (int i = 0; i < behaviours.Length; i++)
            {
                var b = behaviours[i];
                var vars = b.Vars;
                for (int v = 0; v < vars.Length; v++)
                {
                    var variable = vars[v];
                    if (!variable.Persist) continue;
                    writer.WriteString(variable.Name);
                    int at = writer.ReserveUShort();
                    int start = writer.Length;
                    variable.Write(writer);
                    writer.PatchUShort(at, (ushort)(writer.Length - start));
                    variable.PersistDirty = false;
                    count++;
                }

                Scratch.Reset();
                try { b.WritePersistentState(Scratch); }
                catch (Exception ex)
                {
                    NebulaLog.Error($"WritePersistentState on {b.GetType().Name} of {identity.name} threw: {ex.Message}");
                    Scratch.Reset();
                }
                if (Scratch.Length == 0) continue;
                writer.WriteString(StateNameOf(b));
                writer.WriteUShort((ushort)Scratch.Length);
                writer.WriteRaw(Scratch.ToSegment());
                count++;
            }

            writer.PatchUShort(countAt, count);
        }

        /// <summary>Write <paramref name="identity"/>'s persistent state into a fresh array.</summary>
        public static byte[] Write(NetworkIdentity identity)
        {
            var writer = new NetworkWriter(256);
            Write(writer, identity);
            return writer.ToArray();
        }

        /// <summary>
        /// Apply a blob to <paramref name="identity"/>. Entries are matched by name: unknown ones are skipped and
        /// missing ones leave the entity's own defaults. Reading a variable fires its
        /// <c>OnValueChanged</c>, which is what a restored entity wants.
        /// </summary>
        public static void Read(NetworkReader reader, NetworkIdentity identity)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            if (identity == null) throw new ArgumentNullException(nameof(identity));
            if (reader.Remaining == 0) return;

            byte version = reader.ReadByte();
            if (version > Version)
            {
                NebulaLog.Warn($"persisted state of {identity.name} is version {version}, this build reads {Version}; ignored");
                return;
            }
            int count = reader.ReadUShort();

            VarsByName.Clear();
            BehavioursByName.Clear();
            var behaviours = identity.Behaviours;
            for (int i = 0; i < behaviours.Length; i++)
            {
                var b = behaviours[i];
                BehavioursByName[StateNameOf(b)] = b;
                var vars = b.Vars;
                for (int v = 0; v < vars.Length; v++)
                {
                    var variable = vars[v];
                    if (variable.Persist && !string.IsNullOrEmpty(variable.Name)) VarsByName[variable.Name] = variable;
                }
            }

            for (int i = 0; i < count; i++)
            {
                string name = reader.ReadString();
                var chunk = reader.ReadSegment(reader.ReadUShort());
                if (VarsByName.TryGetValue(name, out var variable))
                {
                    ChunkReader.Set(chunk);
                    try { variable.Read(ChunkReader); }
                    catch (Exception ex) { NebulaLog.Error($"reading persisted '{name}' of {identity.name} threw: {ex.Message}"); }
                    variable.PersistDirty = false;
                    continue;
                }
                if (BehavioursByName.TryGetValue(name, out var behaviour))
                {
                    ChunkReader.Set(chunk);
                    try { behaviour.ReadPersistentState(ChunkReader); }
                    catch (Exception ex) { NebulaLog.Error($"ReadPersistentState on {behaviour.GetType().Name} of {identity.name} threw: {ex.Message}"); }
                    continue;
                }
                NebulaLog.Debugf($"persisted state of {identity.name}: no '{name}' in this build; skipped");
            }

            VarsByName.Clear();
            BehavioursByName.Clear();
            identity.ClearDirty();
        }

        /// <summary>Apply a blob held as bytes. A null or empty blob leaves the entity untouched.</summary>
        public static void Read(byte[] state, NetworkIdentity identity)
        {
            if (state == null || state.Length == 0) return;
            Read(new NetworkReader(new ArraySegment<byte>(state)), identity);
        }

        /// <summary>
        /// Read one named entry out of a state blob without an entity to apply it to: the bytes a behaviour wrote
        /// (name <see cref="StateNameOf"/>, i.e. <c>TypeName#state</c>) or one persisted variable, exactly as
        /// <see cref="Read(NetworkReader, NetworkIdentity)"/> would have handed them over. For a game inspecting a
        /// <see cref="PersistedEntityRecord"/> it loaded — an entity not spawned on this worker, or not spawned at
        /// all — instead of duplicating the blob layout.
        /// Returns false for a null/empty blob, a blob written by a newer build, or a name that is not present.
        /// </summary>
        public static bool TryReadEntry(byte[] state, string name, out ArraySegment<byte> entry)
        {
            entry = default;
            if (state == null || state.Length == 0 || string.IsNullOrEmpty(name)) return false;
            try
            {
                var reader = new NetworkReader(new ArraySegment<byte>(state));
                if (reader.ReadByte() > Version) return false;
                int count = reader.ReadUShort();
                for (int i = 0; i < count; i++)
                {
                    string entryName = reader.ReadString();
                    var chunk = reader.ReadSegment(reader.ReadUShort());
                    if (entryName != name) continue;
                    entry = chunk;
                    return true;
                }
            }
            catch (Exception ex) { NebulaLog.Error($"reading persisted entry '{name}' threw: {ex.Message}"); }
            return false;
        }

        /// <summary><see cref="TryReadEntry"/> for a behaviour's own state chunk, named by behaviour type name
        /// (without the <see cref="BehaviourStateSuffix"/>).</summary>
        public static bool TryReadBehaviourState(byte[] state, string behaviourTypeName, out ArraySegment<byte> entry) =>
            TryReadEntry(state, behaviourTypeName + BehaviourStateSuffix, out entry);

        /// <summary>True when any <see cref="PersistAttribute"/> variable of <paramref name="identity"/> changed since the last <see cref="Write(NetworkIdentity)"/>.</summary>
        public static bool HasDirtyVars(NetworkIdentity identity)
        {
            if (identity == null) return false;
            var vars = identity.AllVars;
            for (int i = 0; i < vars.Length; i++) if (vars[i].Persist && vars[i].PersistDirty) return true;
            return false;
        }
    }
}
