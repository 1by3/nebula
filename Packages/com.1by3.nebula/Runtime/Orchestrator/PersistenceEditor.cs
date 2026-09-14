using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
#if NEBULA_SERVICE
using Nebula.ServicePrimitives;
#else
using UnityEngine;
#endif

namespace Nebula
{
    /// <summary>
    /// Reads and rewrites persisted records for the dashboard's Persistence tab (<c>/persistence</c>).
    /// <para>
    /// The standalone orchestrator reads field schemas exported from game prefabs and scene entities at build time.
    /// Fields use the same names as <see cref="PersistentStateCodec"/> (<c>"&lt;BehaviourTypeName&gt;.&lt;FieldName&gt;"</c>).
    /// Built-in types and exported enumerations have typed controls. Custom types, unknown fields, and a behavior's
    /// own <c>#state</c> chunk are shown and edited as hex. The legacy Unity host discovers these schemas from assets.
    /// </para><para>
    /// Loads are asynchronous (the store answers from <see cref="IPersistenceStore.Tick"/>), so the editor keeps a
    /// snapshot of every record and refreshes it at most every <see cref="SnapshotMaxAgeSeconds"/> seconds while the
    /// page polls. Every method here must run on the main thread, from the orchestrator's command pump.
    /// </para><para>
    /// <b>Caveat.</b> An edit lands in the store, not in a running worker. While an entity is alive and authoritative
    /// somewhere, its next checkpoint overwrites whatever you saved here. Edit records of entities that are not alive
    /// (the mesh is stopped, or the entity was deleted), or fields the entity never writes back.
    /// </para>
    /// </summary>
    public sealed class PersistenceEditor
    {
        /// <summary>How long a snapshot of the store is served before the next GET triggers a reload.</summary>
        public const float SnapshotMaxAgeSeconds = 2f;

        /// <summary>Largest chunk <see cref="PersistentStateCodec"/> can hold for one entry (its length is a ushort).</summary>
        private const int MaxEntryBytes = ushort.MaxValue;

        /// <summary>Written into <see cref="PersistedEntityRecord.SavedBy"/> so an edit is never mistaken for a live worker's save.</summary>
        public const string EditorSavedBy = "dashboard";

        private readonly IPersistenceStore _store;
        private readonly List<PersistedEntityRecord> _snapshot = new List<PersistedEntityRecord>();
        private readonly Dictionary<string, PersistedEntityRecord> _byKey = new Dictionary<string, PersistedEntityRecord>(StringComparer.Ordinal);
        /// <summary>Decoded schema per template GameObject: entry name -> NetworkVariable value type (null = untyped entry).</summary>
#if !NEBULA_SERVICE
        private readonly Dictionary<GameObject, Dictionary<string, Type>> _schemas = new Dictionary<GameObject, Dictionary<string, Type>>();
#endif
        private readonly StringBuilder _json = new StringBuilder(4096);

        private float _requestedAt = float.NegativeInfinity;
        private bool _loaded;

        public PersistenceEditor(IPersistenceStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        /// <summary>The store this editor was built for, so the orchestrator can notice the store being swapped.</summary>
        public IPersistenceStore Store => _store;

        // ------------------------------------------------------------------------------------------------ endpoints

        /// <summary>GET /api/persistence/records: every record, decoded.</summary>
        public OrchestratorHttpServer.Response Records()
        {
            Refresh(false);
            var sb = _json;
            sb.Clear();
            var w = new JsonWriter(sb);
            w.BeginObject();
            w.Prop("ok", true);
            w.Prop("backend", _store.Backend);
            w.Prop("connected", _store.IsConnected);
            w.Prop("loaded", _loaded);
            w.Prop("count", _snapshot.Count);
            w.Prop("generatedAt", DateTime.UtcNow.ToString("o"));
            w.Key("records");
            w.BeginArray();
            for (int i = 0; i < _snapshot.Count; i++) WriteRecord(w, _snapshot[i]);
            w.EndArray();
            w.EndObject();
            return OrchestratorHttpServer.Response.Json(200, sb.ToString());
        }

        /// <summary>
        /// POST /api/persistence/record: apply an edit and save it under the record's current epoch.
        /// Body: <c>{key, position?, rotationEuler?|rotation?, velocity?, containerId?, carrierKey?, name?, entries?}</c>.
        /// </summary>
        public OrchestratorHttpServer.Response Update(string body)
        {
            if (!PersistenceJson.TryParseObject(body, out var obj, out string parseError)) return Bad(parseError);
            string key = PersistenceJson.GetString(obj, "key");
            if (string.IsNullOrEmpty(key)) return Bad("body must contain a non-empty \"key\"");
            Refresh(true);
            if (!_byKey.TryGetValue(key, out var stored)) return OrchestratorHttpServer.Response.Error(404, $"no persisted record '{key}'");

            var record = stored.Clone();
            string error = ApplyFields(record, obj);
            if (error != null) return Bad(error);

            ulong before = stored.Version;
            _store.Save(record);
            _store.Tick();
            // The store owns Version and SavedAt; re-read so the page shows what was actually kept.
            Refresh(true);
            return Single(Written(key, record, before));
        }

        /// <summary>POST /api/persistence/delete: <c>{key}</c>.</summary>
        public OrchestratorHttpServer.Response Delete(string body)
        {
            if (!PersistenceJson.TryParseObject(body, out var obj, out string parseError)) return Bad(parseError);
            string key = PersistenceJson.GetString(obj, "key");
            if (string.IsNullOrEmpty(key)) return Bad("body must contain a non-empty \"key\"");
            Refresh(true);
            if (!_byKey.ContainsKey(key)) return OrchestratorHttpServer.Response.Error(404, $"no persisted record '{key}'");
            _store.Delete(key);
            _store.Tick();
            Refresh(true);
            return OrchestratorHttpServer.Response.Json(200, "{\"ok\":true}");
        }

        /// <summary>
        /// POST /api/persistence/duplicate: <c>{key, newKey, ...}</c>. Copies the record under a new key, optionally
        /// with the same edits <see cref="Update"/> accepts (a second crate three metres to the left).
        /// </summary>
        public OrchestratorHttpServer.Response Duplicate(string body)
        {
            if (!PersistenceJson.TryParseObject(body, out var obj, out string parseError)) return Bad(parseError);
            string key = PersistenceJson.GetString(obj, "key");
            string newKey = PersistenceJson.GetString(obj, "newKey");
            if (string.IsNullOrEmpty(key)) return Bad("body must contain a non-empty \"key\"");
            if (string.IsNullOrEmpty(newKey)) return Bad("body must contain a non-empty \"newKey\"");
            if (newKey == key) return Bad("\"newKey\" must differ from \"key\"");
            Refresh(true);
            if (!_byKey.TryGetValue(key, out var stored)) return OrchestratorHttpServer.Response.Error(404, $"no persisted record '{key}'");
            if (_byKey.ContainsKey(newKey)) return OrchestratorHttpServer.Response.Error(409, $"'{newKey}' already exists");

            var record = stored.Clone();
            record.Key = newKey;
            record.Epoch = 0;      // a copy has no authority history of its own
            record.Version = 0;
            record.Owned = false;  // there is no client to own the copy
            string error = ApplyFields(record, obj);
            if (error != null) return Bad(error);
            // A scene entity is identified by its scene id; a second record for the same object would fight over it.
            if (record.SceneId != 0) return Bad("a scene entity's record cannot be duplicated: its scene id identifies exactly one object");

            _store.Save(record);
            _store.Tick();
            Refresh(true);
            return Single(Written(newKey, record, 0));
        }

        /// <summary>
        /// What to answer a write with: the record the store now holds when the write has been observed, otherwise
        /// the one we wrote. A backend that answers from a mirror or a remote store (<see cref="RemotePersistenceStore"/>)
        /// only mirrors the new row a round trip later, and showing the caller the pre-write snapshot would read as
        /// "your edit was dropped". The store's own Version and SavedAt land with the page's next poll.
        /// </summary>
        private PersistedEntityRecord Written(string key, PersistedEntityRecord written, ulong versionBefore)
        {
            if (!_byKey.TryGetValue(key, out var saved)) return written;
            return saved.Version != versionBefore ? saved : written;
        }

        private static OrchestratorHttpServer.Response Bad(string message) => OrchestratorHttpServer.Response.Error(400, message);

        private OrchestratorHttpServer.Response Single(PersistedEntityRecord record)
        {
            var sb = _json;
            sb.Clear();
            var w = new JsonWriter(sb);
            w.BeginObject();
            w.Prop("ok", true);
            w.Key("record");
            WriteRecord(w, record);
            w.EndObject();
            return OrchestratorHttpServer.Response.Json(200, sb.ToString());
        }

        // ------------------------------------------------------------------------------------------------- snapshot

        /// <summary>
        /// Reload the snapshot when it is older than <see cref="SnapshotMaxAgeSeconds"/> (or always, when
        /// <paramref name="force"/>). Both stores answer a load from memory through their callback queue, so one
        /// <see cref="IPersistenceStore.Tick"/> hands the result back inside this call whenever the store is ready.
        /// </summary>
        public void Refresh(bool force)
        {
            float now = Time.unscaledTime;
            if (!force && now - _requestedAt < SnapshotMaxAgeSeconds) return;
            _requestedAt = now;
            _store.LoadWhere(_ => true, list =>
            {
                _snapshot.Clear();
                _byKey.Clear();
                if (list != null)
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        var r = list[i];
                        if (r == null || string.IsNullOrEmpty(r.Key)) continue;
                        _snapshot.Add(r);
                        _byKey[r.Key] = r;
                    }
                    _snapshot.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
                }
                _loaded = true;
            });
            _store.Tick();
        }

        // ------------------------------------------------------------------------------------------------- decoding

        /// <summary>The prefab asset (or resident scene object) a record was written from, null when this build cannot find it.</summary>
#if NEBULA_SERVICE
        private static string ResolveTemplate(PersistedEntityRecord record) => ServiceManifest.TemplateOf(record);
        private Dictionary<string, Type> SchemaOf(string template) => ServiceManifest.SchemaOf(template);
#else
        private static GameObject ResolveTemplate(PersistedEntityRecord record)
        {
            if (record.SceneId != 0)
            {
                var scene = SceneEntities.Find(record.SceneId);
                return scene != null ? scene.gameObject : null;
            }
            var prefab = NetworkPrefabs.Get(record.PrefabId);
            if (prefab != null && (string.IsNullOrEmpty(record.PrefabName) || prefab.name == record.PrefabName)) return prefab;
            // The prefab list was reordered since the save: fall back to the name, like the restore path does.
            if (!string.IsNullOrEmpty(record.PrefabName))
            {
                for (int i = 0; i < NetworkPrefabs.Count; i++)
                {
                    var p = NetworkPrefabs.Get((ushort)i);
                    if (p != null && p.name == record.PrefabName) return p;
                }
            }
            return prefab;
        }

        /// <summary>
        /// Entry name -> value type for one template, mirroring <see cref="NetworkIdentity.Initialize"/>'s discovery
        /// on the asset instead of a live entity. A null type means "known entry, no editor" (a behaviour's own
        /// <c>#state</c> chunk, or a NetworkVariable subclass that is not <see cref="NetworkVariable{T}"/>).
        /// </summary>
        private Dictionary<string, Type> SchemaOf(GameObject template)
        {
            if (template == null) return null;
            if (_schemas.TryGetValue(template, out var cached)) return cached;

            var map = new Dictionary<string, Type>(StringComparer.Ordinal);
            var behaviours = template.GetComponentsInChildren<NetworkBehaviour>(true);
            for (int i = 0; i < behaviours.Length; i++)
            {
                var b = behaviours[i];
                if (b == null) continue;
                string typeName = b.GetType().Name;
                map[typeName + PersistentStateCodec.BehaviourStateSuffix] = null;
                for (var t = b.GetType(); t != null && t != typeof(NetworkBehaviour); t = t.BaseType)
                {
                    var fields = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    for (int f = 0; f < fields.Length; f++)
                    {
                        var field = fields[f];
                        if (!typeof(NetworkVariableBase).IsAssignableFrom(field.FieldType)) continue;
                        Type value = null;
                        var ft = field.FieldType;
                        if (ft.IsGenericType && ft.GetGenericTypeDefinition() == typeof(NetworkVariable<>)) value = ft.GetGenericArguments()[0];
                        map[typeName + "." + field.Name] = value;
                    }
                }
            }
            _schemas[template] = map;
            return map;
        }

        /// <summary>One entry of a state blob as it lies in the bytes.</summary>
#endif
        private struct RawEntry
        {
            public string Name;
            public byte[] Bytes;
        }

        /// <summary>
        /// Split a blob into its named chunks. Returns false when the header is not something this build reads, in
        /// which case the whole blob is shown as one hex value instead of being taken apart.
        /// </summary>
        private static bool TrySplit(byte[] state, List<RawEntry> into)
        {
            into.Clear();
            if (state == null || state.Length == 0) return true;
            try
            {
                var reader = new NetworkReader(new ArraySegment<byte>(state));
                byte version = reader.ReadByte();
                if (version > PersistentStateCodec.Version) return false;
                int count = reader.ReadUShort();
                for (int i = 0; i < count; i++)
                {
                    string name = reader.ReadString();
                    var chunk = reader.ReadSegment(reader.ReadUShort());
                    var bytes = new byte[chunk.Count];
                    Array.Copy(chunk.Array, chunk.Offset, bytes, 0, chunk.Count);
                    into.Add(new RawEntry { Name = name, Bytes = bytes });
                }
                return true;
            }
            catch (Exception)
            {
                into.Clear();
                return false;
            }
        }

        /// <summary>How the page renders and edits a value.</summary>
        private static string KindOf(Type t)
        {
            if (t == null) return "raw";
            if (t.IsEnum) return "enum";
            if (t == typeof(bool)) return "bool";
            if (t == typeof(byte) || t == typeof(sbyte) || t == typeof(short) || t == typeof(ushort) ||
                t == typeof(int) || t == typeof(uint) || t == typeof(long) || t == typeof(ulong)) return "int";
            if (t == typeof(float) || t == typeof(double)) return "float";
            if (t == typeof(string)) return "string";
            if (t == typeof(Vector2)) return "vector2";
            if (t == typeof(Vector3)) return "vector3";
            if (t == typeof(Quaternion)) return "quaternion";
            if (t == typeof(Color)) return "color";
            return "readonly";
        }

        private void WriteRecord(JsonWriter w, PersistedEntityRecord r)
        {
            var template = ResolveTemplate(r);
            var schema = SchemaOf(template);

            w.BeginObject();
            w.Prop("key", r.Key);
            w.Prop("prefabId", (int)r.PrefabId);
            w.Prop("prefabName", r.PrefabName ?? "");
            w.Prop("sceneId", r.SceneId);
            w.Prop("containerId", r.ContainerId ?? "");
            w.Prop("carrierKey", r.CarrierKey ?? "");
            w.Prop("name", r.Name ?? "");
            w.Prop("epoch", r.Epoch);
            w.Prop("version", r.Version);
            w.Prop("savedAt", r.SavedAt == default ? "" : r.SavedAt.ToUniversalTime().ToString("o"));
            w.Prop("savedBy", r.SavedBy ?? "");
            w.Prop("serverDriven", r.ServerDriven);
            w.Prop("owned", r.Owned);
            w.Prop("scene", r.SceneId != 0);
            w.Prop("resolved", template != null);
#if NEBULA_SERVICE
            w.Prop("template", ServiceManifest.TemplateDisplayName(template));
#else
            w.Prop("template", template != null ? template.name : "" );
#endif
            w.Prop("stateBytes", r.State != null ? r.State.Length : 0);
            WriteVector(w, "position", r.LocalPosition.x, r.LocalPosition.y, r.LocalPosition.z);
            w.Key("rotation");
            w.BeginArray();
            w.Value(r.LocalRotation.x); w.Value(r.LocalRotation.y); w.Value(r.LocalRotation.z); w.Value(r.LocalRotation.w);
            w.EndArray();
            var euler = r.LocalRotation.eulerAngles;
            WriteVector(w, "rotationEuler", euler.x, euler.y, euler.z);
            WriteVector(w, "velocity", r.Velocity.x, r.Velocity.y, r.Velocity.z);

            w.Key("entries");
            w.BeginArray();
            var entries = new List<RawEntry>();
            if (!TrySplit(r.State, entries))
            {
                w.BeginObject();
                w.Prop("name", "(whole blob)");
                w.Prop("kind", "raw");
                w.Prop("typeName", "");
                w.Prop("note", "the blob header is not readable by this build");
                w.Prop("hex", ToHex(r.State));
                w.EndObject();
            }
            else
            {
                for (int i = 0; i < entries.Count; i++) WriteEntry(w, entries[i], schema);
            }
            w.EndArray();
            w.EndObject();
        }

        private static void WriteVector(JsonWriter w, string name, float x, float y, float z)
        {
            w.Key(name);
            w.BeginArray();
            w.Value(x); w.Value(y); w.Value(z);
            w.EndArray();
        }

        private static void WriteEntry(JsonWriter w, RawEntry entry, Dictionary<string, Type> schema)
        {
            Type type = null;
            bool known = schema != null && schema.TryGetValue(entry.Name, out type);
            string kind = known ? KindOf(type) : "raw";
            object value = null;
            string note = null;
            if (type != null)
            {
                try
                {
                    var reader = new NetworkReader(new ArraySegment<byte>(entry.Bytes));
                    value = NetworkSerialization.ReadObject(reader, type);
                }
                catch (Exception e)
                {
                    kind = "raw";
                    note = "decode failed: " + e.Message;
                }
            }
            else if (!known)
            {
                note = "no such variable in this build";
            }
            else if (entry.Name.EndsWith(PersistentStateCodec.BehaviourStateSuffix, StringComparison.Ordinal))
            {
                note = "behaviour state, only the behaviour knows its layout";
            }

            w.BeginObject();
            w.Prop("name", entry.Name);
            w.Prop("kind", kind);
            w.Prop("typeName", type != null ? type.Name : "");
            if (note != null) w.Prop("note", note);
            w.Key("value");
            WriteValue(w, kind, type, value);
            if (kind == "enum" && type != null)
            {
                w.Key("enumNames");
                w.BeginArray();
                foreach (string n in Enum.GetNames(type)) w.Value(n);
                w.EndArray();
            }
            w.Prop("hex", ToHex(entry.Bytes));
            w.EndObject();
        }

        private static void WriteValue(JsonWriter w, string kind, Type type, object value)
        {
            switch (kind)
            {
                case "bool": w.Value(value is bool b && b); return;
                case "int": w.Value(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "0"); return;
                case "float": w.Value(Convert.ToDouble(value, CultureInfo.InvariantCulture)); return;
                case "string": w.Value((string)value ?? ""); return;
                case "enum": w.Value(Enum.IsDefined(type, value) ? Enum.GetName(type, value) : Convert.ToString(value, CultureInfo.InvariantCulture)); return;
                case "vector2":
                {
                    var v = (Vector2)value;
                    w.BeginArray(); w.Value(v.x); w.Value(v.y); w.EndArray();
                    return;
                }
                case "vector3":
                {
                    var v = (Vector3)value;
                    w.BeginArray(); w.Value(v.x); w.Value(v.y); w.Value(v.z); w.EndArray();
                    return;
                }
                case "quaternion":
                {
                    var q = (Quaternion)value;
                    w.BeginArray(); w.Value(q.x); w.Value(q.y); w.Value(q.z); w.Value(q.w); w.EndArray();
                    return;
                }
                case "color":
                {
                    var c = (Color)value;
                    w.BeginArray(); w.Value(c.r); w.Value(c.g); w.Value(c.b); w.Value(c.a); w.EndArray();
                    return;
                }
                case "readonly": w.Value(value != null ? value.ToString() : "null"); return;
                default: w.Raw("null"); return;
            }
        }

        // ------------------------------------------------------------------------------------------------- encoding

        /// <summary>Apply the body's fields to <paramref name="record"/>. Returns null on success, else the reason.</summary>
        private string ApplyFields(PersistedEntityRecord record, Dictionary<string, object> body)
        {
            if (body.TryGetValue("name", out var name)) record.Name = PersistenceJson.AsString(name) ?? "";
            if (body.TryGetValue("containerId", out var container)) record.ContainerId = PersistenceJson.AsString(container) ?? "";
            if (body.TryGetValue("carrierKey", out var carrier)) record.CarrierKey = PersistenceJson.AsString(carrier) ?? "";
            if (body.TryGetValue("serverDriven", out var driven) && driven is bool sd) record.ServerDriven = sd;
            if (body.TryGetValue("owned", out var owned) && owned is bool ow) record.Owned = ow;

            if (body.TryGetValue("position", out var pos))
            {
                if (!PersistenceJson.TryNumbers(pos, 3, out var p)) return "\"position\" must be [x, y, z]";
                record.LocalPosition = new Vector3((float)p[0], (float)p[1], (float)p[2]);
            }
            if (body.TryGetValue("rotationEuler", out var euler))
            {
                if (!PersistenceJson.TryNumbers(euler, 3, out var e)) return "\"rotationEuler\" must be [x, y, z] in degrees";
                record.LocalRotation = Quaternion.Euler((float)e[0], (float)e[1], (float)e[2]);
            }
            else if (body.TryGetValue("rotation", out var rot))
            {
                if (!PersistenceJson.TryNumbers(rot, 4, out var q)) return "\"rotation\" must be [x, y, z, w]";
                var quaternion = new Quaternion((float)q[0], (float)q[1], (float)q[2], (float)q[3]);
                float length = Mathf.Sqrt(Quaternion.Dot(quaternion, quaternion));
                if (length < 1e-4f) return "\"rotation\" must not be a zero quaternion";
                record.LocalRotation = Quaternion.Normalize(quaternion);
            }
            if (body.TryGetValue("velocity", out var vel))
            {
                if (!PersistenceJson.TryNumbers(vel, 3, out var v)) return "\"velocity\" must be [x, y, z]";
                record.Velocity = new Vector3((float)v[0], (float)v[1], (float)v[2]);
            }

            if (body.TryGetValue("entries", out var entriesValue) && entriesValue != null)
            {
                if (!(entriesValue is Dictionary<string, object> edits)) return "\"entries\" must be an object of {name: value}";
                if (edits.Count > 0)
                {
                    string error = Rewrite(record, edits);
                    if (error != null) return error;
                }
            }

            record.SavedBy = EditorSavedBy;
            return null;
        }

        /// <summary>
        /// Rebuild the state blob with <paramref name="edits"/> applied. Entries that are not edited keep their bytes
        /// verbatim, so a value this build cannot decode survives an edit to the one next to it. An edited name that
        /// the blob does not hold yet is appended when this build knows its type.
        /// </summary>
        private string Rewrite(PersistedEntityRecord record, Dictionary<string, object> edits)
        {
            var schema = SchemaOf(ResolveTemplate(record));
            var entries = new List<RawEntry>();
            if (!TrySplit(record.State, entries)) return "this build cannot read the record's state blob, so it cannot rewrite it";

            var used = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (!edits.TryGetValue(entry.Name, out var edited)) continue;
                used.Add(entry.Name);
                string error = Encode(entry.Name, edited, schema, out var bytes);
                if (error != null) return error;
                entry.Bytes = bytes;
                entries[i] = entry;
            }
            foreach (var kv in edits)
            {
                if (used.Contains(kv.Key)) continue;
                if (schema == null || !schema.TryGetValue(kv.Key, out var type) || type == null)
                    return $"'{kv.Key}' is not in the record and has no known type in this build";
                string error = Encode(kv.Key, kv.Value, schema, out var bytes);
                if (error != null) return error;
                entries.Add(new RawEntry { Name = kv.Key, Bytes = bytes });
            }

            var writer = new NetworkWriter(Math.Max(256, record.State != null ? record.State.Length * 2 : 256));
            writer.WriteByte(PersistentStateCodec.Version);
            writer.WriteUShort((ushort)entries.Count);
            for (int i = 0; i < entries.Count; i++)
            {
                writer.WriteString(entries[i].Name);
                writer.WriteUShort((ushort)entries[i].Bytes.Length);
                writer.WriteRaw(new ArraySegment<byte>(entries[i].Bytes));
            }
            record.State = writer.ToArray();
            return null;
        }

        /// <summary>Turn one edited JSON value into the bytes of its entry. Returns null on success, else the reason.</summary>
        private static string Encode(string name, object value, Dictionary<string, Type> schema, out byte[] bytes)
        {
            bytes = Array.Empty<byte>();
            Type type = null;
            schema?.TryGetValue(name, out type);
            string kind = KindOf(type);
            if (kind == "raw" || kind == "readonly")
            {
                // Untyped or uneditable: the page sends the replacement bytes as hex.
                string hex = PersistenceJson.AsString(value);
                if (hex == null) return $"'{name}' can only be edited as a hex string";
                if (!TryFromHex(hex, out bytes)) return $"'{name}': '{hex}' is not an even-length hex string";
                if (bytes.Length > MaxEntryBytes) return $"'{name}': {bytes.Length} bytes is more than the {MaxEntryBytes} an entry can hold";
                return null;
            }

            object typed;
            string error = Coerce(name, type, kind, value, out typed);
            if (error != null) return error;
            var writer = new NetworkWriter(64);
            try { NetworkSerialization.WriteObject(writer, type, typed); }
            catch (Exception e) { return $"'{name}': {e.Message}"; }
            if (writer.Length > MaxEntryBytes) return $"'{name}': {writer.Length} bytes is more than the {MaxEntryBytes} an entry can hold";
            bytes = writer.ToArray();
            return null;
        }

        /// <summary>JSON value -> the entry's CLR type. Numbers arrive as doubles, so integers are range-checked here.</summary>
        private static string Coerce(string name, Type type, string kind, object value, out object typed)
        {
            typed = null;
            try
            {
                switch (kind)
                {
                    case "bool":
                        if (value is bool b) { typed = b; return null; }
                        if (value is string sb2 && bool.TryParse(sb2, out bool pb)) { typed = pb; return null; }
                        return $"'{name}' expects true or false";
                    case "int":
                    {
                        // A ulong past 2^53 cannot survive a JSON number, so the page sends integers as strings too.
                        string text = value as string ?? (value is double d ? d.ToString("R", CultureInfo.InvariantCulture) : null);
                        if (text == null) return $"'{name}' expects a whole number";
                        text = text.Trim();
                        if (text.Length == 0) return $"'{name}' expects a whole number";
                        typed = Convert.ChangeType(text, type, CultureInfo.InvariantCulture);
                        return null;
                    }
                    case "float":
                    {
                        double d = value is double dd ? dd : (value is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double ps) ? ps : double.NaN);
                        if (double.IsNaN(d) && !(value is double)) return $"'{name}' expects a number";
                        typed = type == typeof(float) ? (object)(float)d : d;
                        return null;
                    }
                    case "string":
                        typed = PersistenceJson.AsString(value) ?? "";
                        return null;
                    case "enum":
                    {
                        if (value is string es)
                        {
                            if (!Enum.IsDefined(type, es))
                            {
                                // Accept the numeric form as well, so a value the build no longer names can be kept.
                                if (!long.TryParse(es, NumberStyles.Integer, CultureInfo.InvariantCulture, out long raw)) return $"'{name}': '{es}' is not a {type.Name}";
                                typed = Enum.ToObject(type, raw);
                                return null;
                            }
                            typed = Enum.Parse(type, es);
                            return null;
                        }
                        if (value is double en) { typed = Enum.ToObject(type, (long)en); return null; }
                        return $"'{name}' expects one of {type.Name}'s names";
                    }
                    case "vector2":
                    {
                        if (!PersistenceJson.TryNumbers(value, 2, out var v)) return $"'{name}' expects [x, y]";
                        typed = new Vector2((float)v[0], (float)v[1]);
                        return null;
                    }
                    case "vector3":
                    {
                        if (!PersistenceJson.TryNumbers(value, 3, out var v)) return $"'{name}' expects [x, y, z]";
                        typed = new Vector3((float)v[0], (float)v[1], (float)v[2]);
                        return null;
                    }
                    case "quaternion":
                    {
                        if (!PersistenceJson.TryNumbers(value, 4, out var v)) return $"'{name}' expects [x, y, z, w]";
                        typed = new Quaternion((float)v[0], (float)v[1], (float)v[2], (float)v[3]);
                        return null;
                    }
                    case "color":
                    {
                        if (!PersistenceJson.TryNumbers(value, 4, out var v)) return $"'{name}' expects [r, g, b, a]";
                        typed = new Color((float)v[0], (float)v[1], (float)v[2], (float)v[3]);
                        return null;
                    }
                    default:
                        return $"'{name}' cannot be edited as a value";
                }
            }
            catch (Exception e)
            {
                return $"'{name}': {e.Message}";
            }
        }

        // ------------------------------------------------------------------------------------------------------ hex

        private static readonly char[] HexDigits = "0123456789abcdef".ToCharArray();

        public static string ToHex(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return "";
            var sb = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++)
            {
                sb.Append(HexDigits[bytes[i] >> 4]).Append(HexDigits[bytes[i] & 0xF]);
            }
            return sb.ToString();
        }

        public static bool TryFromHex(string hex, out byte[] bytes)
        {
            bytes = Array.Empty<byte>();
            if (hex == null) return false;
            var clean = new StringBuilder(hex.Length);
            foreach (char c in hex) if (!char.IsWhiteSpace(c)) clean.Append(c);
            if (clean.Length % 2 != 0) return false;
            var result = new byte[clean.Length / 2];
            for (int i = 0; i < result.Length; i++)
            {
                int hi = HexValue(clean[i * 2]);
                int lo = HexValue(clean[i * 2 + 1]);
                if (hi < 0 || lo < 0) return false;
                result[i] = (byte)((hi << 4) | lo);
            }
            bytes = result;
            return true;
        }

        private static int HexValue(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }
    }

    /// <summary>
    /// Just enough JSON reading for the Persistence tab's request bodies, the counterpart of <see cref="JsonWriter"/>.
    /// The dashboard's other endpoints take flat objects and get by with
    /// <see cref="OrchestratorHttpServer.GetString"/>; an edit carries nested values, so it needs a real parser.
    /// Objects become <c>Dictionary&lt;string, object&gt;</c>, arrays <c>List&lt;object&gt;</c>, numbers
    /// <c>double</c>, and the rest <c>string</c>, <c>bool</c> or null.
    /// </summary>
    internal static class PersistenceJson
    {
        public static bool TryParseObject(string text, out Dictionary<string, object> value, out string error)
        {
            value = null;
            error = null;
            if (string.IsNullOrWhiteSpace(text)) { error = "the request body is empty"; return false; }
            int at = 0;
            try
            {
                var parsed = Parse(text, ref at);
                SkipWhitespace(text, ref at);
                if (at != text.Length) throw new FormatException($"trailing text at offset {at}");
                value = parsed as Dictionary<string, object>;
                if (value == null) { error = "the request body must be a JSON object"; return false; }
                return true;
            }
            catch (Exception e)
            {
                error = "malformed JSON: " + e.Message;
                return false;
            }
        }

        public static string GetString(Dictionary<string, object> obj, string key)
        {
            return obj != null && obj.TryGetValue(key, out var v) ? AsString(v) ?? "" : "";
        }

        public static string AsString(object value)
        {
            if (value is string s) return s;
            if (value is bool b) return b ? "true" : "false";
            if (value is double d) return d.ToString("R", CultureInfo.InvariantCulture);
            return null;
        }

        /// <summary>An array of exactly <paramref name="count"/> numbers, as the page sends vectors and colours.</summary>
        public static bool TryNumbers(object value, int count, out double[] numbers)
        {
            numbers = null;
            if (!(value is List<object> list) || list.Count != count) return false;
            var result = new double[count];
            for (int i = 0; i < count; i++)
            {
                if (list[i] is double d) result[i] = d;
                else if (list[i] is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double p)) result[i] = p;
                else return false;
            }
            numbers = result;
            return true;
        }

        private static object Parse(string s, ref int at)
        {
            SkipWhitespace(s, ref at);
            if (at >= s.Length) throw new FormatException("unexpected end of input");
            char c = s[at];
            switch (c)
            {
                case '{': return ParseObject(s, ref at);
                case '[': return ParseArray(s, ref at);
                case '"': return ParseString(s, ref at);
                case 't': Expect(s, ref at, "true"); return true;
                case 'f': Expect(s, ref at, "false"); return false;
                case 'n': Expect(s, ref at, "null"); return null;
                default: return ParseNumber(s, ref at);
            }
        }

        private static Dictionary<string, object> ParseObject(string s, ref int at)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            at++; // {
            SkipWhitespace(s, ref at);
            if (at < s.Length && s[at] == '}') { at++; return result; }
            while (true)
            {
                SkipWhitespace(s, ref at);
                if (at >= s.Length || s[at] != '"') throw new FormatException($"expected a property name at offset {at}");
                string key = ParseString(s, ref at);
                SkipWhitespace(s, ref at);
                if (at >= s.Length || s[at] != ':') throw new FormatException($"expected ':' at offset {at}");
                at++;
                result[key] = Parse(s, ref at);
                SkipWhitespace(s, ref at);
                if (at >= s.Length) throw new FormatException("unterminated object");
                if (s[at] == ',') { at++; continue; }
                if (s[at] == '}') { at++; return result; }
                throw new FormatException($"expected ',' or '}}' at offset {at}");
            }
        }

        private static List<object> ParseArray(string s, ref int at)
        {
            var result = new List<object>();
            at++; // [
            SkipWhitespace(s, ref at);
            if (at < s.Length && s[at] == ']') { at++; return result; }
            while (true)
            {
                result.Add(Parse(s, ref at));
                SkipWhitespace(s, ref at);
                if (at >= s.Length) throw new FormatException("unterminated array");
                if (s[at] == ',') { at++; continue; }
                if (s[at] == ']') { at++; return result; }
                throw new FormatException($"expected ',' or ']' at offset {at}");
            }
        }

        private static string ParseString(string s, ref int at)
        {
            at++; // opening quote
            var sb = new StringBuilder();
            while (true)
            {
                if (at >= s.Length) throw new FormatException("unterminated string");
                char c = s[at++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (at >= s.Length) throw new FormatException("unterminated escape");
                char e = s[at++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (at + 4 > s.Length) throw new FormatException("truncated \\u escape");
                        sb.Append((char)ushort.Parse(s.Substring(at, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        at += 4;
                        break;
                    default: throw new FormatException($"unknown escape '\\{e}'");
                }
            }
        }

        private static double ParseNumber(string s, ref int at)
        {
            int start = at;
            while (at < s.Length && (char.IsDigit(s[at]) || s[at] == '-' || s[at] == '+' || s[at] == '.' || s[at] == 'e' || s[at] == 'E')) at++;
            if (at == start) throw new FormatException($"unexpected character '{s[start]}' at offset {start}");
            return double.Parse(s.Substring(start, at - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private static void Expect(string s, ref int at, string literal)
        {
            if (at + literal.Length > s.Length || string.CompareOrdinal(s, at, literal, 0, literal.Length) != 0)
                throw new FormatException($"expected '{literal}' at offset {at}");
            at += literal.Length;
        }

        private static void SkipWhitespace(string s, ref int at)
        {
            while (at < s.Length && char.IsWhiteSpace(s[at])) at++;
        }
    }
}
