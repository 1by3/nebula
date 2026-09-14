using System;
using System.Collections.Generic;
using System.Text;
#if NEBULA_SERVICE
using Nebula.ServicePrimitives;
#else
using UnityEngine;
#endif

namespace Nebula
{
    /// <summary>
    /// <see cref="PersistedEntityRecord"/> as JSON, the wire format between <see cref="RemotePersistenceStore"/>
    /// (workers) and <see cref="PersistenceHost"/> (the orchestrator). The state blob travels base64-encoded;
    /// vectors are <c>[x, y, z]</c>, the rotation <c>[x, y, z, w]</c>, times Unix milliseconds.
    /// </summary>
    public static class PersistedRecordJson
    {
        public static void Write(JsonWriter w, PersistedEntityRecord r)
        {
            w.BeginObject();
            w.Prop("key", r.Key ?? "");
            w.Prop("prefabId", (int)r.PrefabId);
            w.Prop("prefabName", r.PrefabName ?? "");
            w.Prop("sceneId", r.SceneId);
            w.Prop("containerId", r.ContainerId ?? "");
            w.Prop("carrierKey", r.CarrierKey ?? "");
            w.Key("position"); ControlPlaneJson.Vec(w, r.LocalPosition);
            w.Key("rotation");
            w.BeginArray();
            ControlPlaneJson.Num(w, r.LocalRotation.x); ControlPlaneJson.Num(w, r.LocalRotation.y); ControlPlaneJson.Num(w, r.LocalRotation.z); ControlPlaneJson.Num(w, r.LocalRotation.w);
            w.EndArray();
            w.Key("velocity"); ControlPlaneJson.Vec(w, r.Velocity);
            w.Prop("epoch", r.Epoch);
            w.Prop("serverDriven", r.ServerDriven);
            w.Prop("owned", r.Owned);
            w.Prop("name", r.Name ?? "");
            w.Prop("state", r.State != null && r.State.Length > 0 ? Convert.ToBase64String(r.State) : "");
            w.Prop("version", r.Version);
            w.Prop("savedAt", ControlPlaneJson.ToUnixMs(r.SavedAt));
            w.Prop("savedBy", r.SavedBy ?? "");
            w.EndObject();
        }

        /// <summary><c>{"records":[...]}</c></summary>
        public static string WriteList(IReadOnlyList<PersistedEntityRecord> records)
        {
            var sb = new StringBuilder(256 + (records != null ? records.Count * 512 : 0));
            var w = new JsonWriter(sb);
            w.BeginObject();
            w.Key("records");
            w.BeginArray();
            if (records != null) for (int i = 0; i < records.Count; i++) Write(w, records[i]);
            w.EndArray();
            w.EndObject();
            return sb.ToString();
        }

        /// <summary><c>{"record":{...}}</c>, or <c>{"record":null}</c>.</summary>
        public static string WriteOne(PersistedEntityRecord record)
        {
            var sb = new StringBuilder(512);
            var w = new JsonWriter(sb);
            w.BeginObject();
            w.Key("record");
            if (record == null) w.Raw("null");
            else Write(w, record);
            w.EndObject();
            return sb.ToString();
        }

        public static PersistedEntityRecord Parse(Dictionary<string, object> o)
        {
            var r = new PersistedEntityRecord
            {
                Key = ControlPlaneJson.Str(o, "key"),
                PrefabId = (ushort)ControlPlaneJson.Num(o, "prefabId"),
                PrefabName = ControlPlaneJson.Str(o, "prefabName"),
                SceneId = (uint)ControlPlaneJson.Num(o, "sceneId"),
                ContainerId = ControlPlaneJson.Str(o, "containerId"),
                CarrierKey = ControlPlaneJson.Str(o, "carrierKey"),
                LocalPosition = ControlPlaneJson.Vec(o, "position"),
                Velocity = ControlPlaneJson.Vec(o, "velocity"),
                Epoch = (uint)ControlPlaneJson.Num(o, "epoch"),
                ServerDriven = ControlPlaneJson.Bool(o, "serverDriven"),
                Owned = ControlPlaneJson.Bool(o, "owned"),
                Name = ControlPlaneJson.Str(o, "name"),
                Version = (ulong)ControlPlaneJson.Num(o, "version"),
                SavedAt = ControlPlaneJson.FromUnixMs(ControlPlaneJson.Num(o, "savedAt")),
                SavedBy = ControlPlaneJson.Str(o, "savedBy"),
            };
            if (o.TryGetValue("rotation", out var rot) && PersistenceJson.TryNumbers(rot, 4, out var q)) r.LocalRotation = new Quaternion((float)q[0], (float)q[1], (float)q[2], (float)q[3]);
            else r.LocalRotation = Quaternion.identity;
            string state = ControlPlaneJson.Str(o, "state");
            r.State = state.Length > 0 ? Convert.FromBase64String(state) : Array.Empty<byte>();
            return r;
        }

        /// <summary>The records under <paramref name="key"/> in a parsed body (an empty list when absent).</summary>
        public static List<PersistedEntityRecord> ParseList(Dictionary<string, object> root, string key = "records")
        {
            var result = new List<PersistedEntityRecord>();
            if (root != null && root.TryGetValue(key, out var v) && v is List<object> list)
            {
                foreach (var item in list) if (item is Dictionary<string, object> o) result.Add(Parse(o));
            }
            return result;
        }

        /// <summary>The record under <paramref name="key"/> in a parsed body, or null.</summary>
        public static PersistedEntityRecord ParseOne(Dictionary<string, object> root, string key = "record")
        {
            return root != null && root.TryGetValue(key, out var v) && v is Dictionary<string, object> o ? Parse(o) : null;
        }

        /// <summary>Read a <see cref="WriteList"/> document. Throws <see cref="FormatException"/> on malformed input.</summary>
        public static List<PersistedEntityRecord> ParseList(string json)
        {
            if (!PersistenceJson.TryParseObject(json, out var root, out string error)) throw new FormatException(error);
            return ParseList(root);
        }

        /// <summary>Read a <see cref="WriteOne"/> document. Throws <see cref="FormatException"/> on malformed input.</summary>
        public static PersistedEntityRecord ParseOne(string json)
        {
            if (!PersistenceJson.TryParseObject(json, out var root, out string error)) throw new FormatException(error);
            return ParseOne(root);
        }
    }
}
