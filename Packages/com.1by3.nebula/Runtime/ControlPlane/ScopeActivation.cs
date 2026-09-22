using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
#if NEBULA_SERVICE
using Nebula.ServicePrimitives;
#else
using UnityEngine;
#endif

namespace Nebula
{
    /// <summary>
    /// How a scope key becomes the ids every role agrees on. The two derivations here are the ones
    /// <see cref="NebulaWorker.PrepareInstance"/> has always used, lifted out of the worker so a gateway, an
    /// orchestrator, a matchmaking service or a test can name a scope's containers without a worker and without
    /// Unity. Design of record: <c>docs/scope-activation.md</c>.
    /// </summary>
    public static class ScopeKeys
    {
        /// <summary>The prefix of a runtime container's id (<see cref="ContainerRegistry.RuntimeContainerId"/>).</summary>
        public const string RuntimeIdPrefix = "rt_";

        /// <summary>
        /// The 64-bit isolation id of a scope key: the first eight bytes of its SHA-256, big-endian, never zero
        /// (zero is the public world). This is <see cref="NebulaWorker.InstanceKey"/>, which now calls it.
        /// </summary>
        public static ulong Hash(string key)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentException("A scope key is required", nameof(key));
            using (var hash = SHA256.Create())
            {
                var bytes = hash.ComputeHash(Encoding.UTF8.GetBytes(key));
                ulong id = 0;
                for (int i = 0; i < 8; i++) id = (id << 8) | bytes[i];
                return id == 0 ? 1UL : id;
            }
        }

        /// <summary>
        /// The runtime container id of one part of a scope: <c>rt_</c> and <see cref="Hash"/> of
        /// <c>scopeKey + "/" + partId</c>. The same key and part id produce the same id on every process and after
        /// every restart, which is what makes activation idempotent (<c>docs/scope-activation.md</c> D3).
        /// </summary>
        public static string ContainerId(string scopeKey, string partId)
        {
            if (string.IsNullOrEmpty(partId)) throw new ArgumentException("A part id is required", nameof(partId));
            return RuntimeIdPrefix + Hash(scopeKey + "/" + partId).ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>The states a <see cref="ScopeInfo"/> row can be in. NEB-240 adds retirement states here.</summary>
    public static class ScopeState
    {
        /// <summary>The scope's lease rows exist. It says nothing about who owns them or what is loaded (<see cref="ControlPlaneExtensions.IsScopeReady"/>).</summary>
        public const string Active = "active";
    }

    /// <summary>The shapes a <see cref="ScopeDefinition"/> can take. A reader that does not know a kind must not activate it.</summary>
    public static class ScopeKind
    {
        /// <summary>An explicit list of parts, each one runtime container. The only kind Nebula itself activates today.</summary>
        public const string Parts = "parts";
    }

    /// <summary>One runtime container of a scope: a stable part id and an axis-aligned box in absolute world coordinates.</summary>
    [Serializable]
    public sealed class ScopePart
    {
        /// <summary>Non-empty and unique within the definition. It is hashed with the scope key into the container id, so changing it renames the place.</summary>
        public string PartId = "";
        /// <summary>Box centre, in the same absolute frame as <see cref="LeaseInfo.Bounds"/> (see <c>docs/scope-activation.md</c> D4).</summary>
        public Vector3 Center;
        /// <summary>Box size; every component must be positive.</summary>
        public Vector3 Size;
        /// <summary>Resources path of this part's static geometry, or empty. Loaded by the worker that owns the lease, through the game's existing instance-content path.</summary>
        public string ContentResource = "";
    }

    /// <summary>
    /// What a scope is made of: the containers to bring into being and how their occupants see the public world.
    /// A definition is compared by value (its canonical JSON), so activating the same key twice with the same
    /// definition is a no-op and activating it with a different one is refused rather than silently merged.
    /// <para>
    /// <see cref="Kind"/> and <see cref="Payload"/> are the extension seam: scoped chunk grids (NEB-239) add a kind
    /// of their own and put the grid definition in the payload. Nebula carries the payload verbatim and never
    /// parses it.
    /// </para>
    /// </summary>
    [Serializable]
    public sealed class ScopeDefinition
    {
        public string Kind = ScopeKind.Parts;
        public List<ScopePart> Parts = new List<ScopePart>();
        /// <summary>Occupants may receive public entities inside <see cref="ObservationCenter"/>/<see cref="ObservationSize"/>.</summary>
        public bool ObservePublic;
        /// <summary>The outward view box, in absolute world coordinates.</summary>
        public Vector3 ObservationCenter, ObservationSize;
        /// <summary>An opaque blob the game or a later milestone attaches to the definition. Never parsed by Nebula.</summary>
        public string Payload = "";

        /// <summary>The reason this definition cannot be activated, or null when it is well formed.</summary>
        public string Validate()
        {
            if (string.IsNullOrEmpty(Kind)) return "a scope definition needs a kind";
            if (Kind != ScopeKind.Parts) return $"unknown scope definition kind '{Kind}'";
            if (Parts == null || Parts.Count == 0) return "a scope definition needs at least one part";
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var part in Parts)
            {
                if (part == null || string.IsNullOrEmpty(part.PartId)) return "every scope part needs an id";
                if (!seen.Add(part.PartId)) return $"duplicate scope part id '{part.PartId}'";
                if (part.Size.x <= 0 || part.Size.y <= 0 || part.Size.z <= 0) return $"scope part '{part.PartId}' needs a positive size";
            }
            return null;
        }
    }

    /// <summary>
    /// A shared simulation scope the mesh has been asked to bring into being: the opaque key that names it, the
    /// definition it was activated from, and the containers that now exist for it. One row per key, mirrored to
    /// every worker, gateway and orchestrator in the control-plane document.
    /// </summary>
    public sealed class ScopeInfo
    {
        /// <summary>The opaque key. Never empty (the public world is <see cref="EntityLocation.PublicScope"/> and is never activated).</summary>
        public string ScopeKey = "";
        /// <summary>The scope's 64-bit isolation id (<see cref="ScopeKeys.Hash"/>), the same value entities carry in <see cref="NetworkIdentity.InstanceId"/>.</summary>
        public ulong InstanceId;
        /// <summary>See <see cref="ScopeState"/>.</summary>
        public string State = ScopeState.Active;
        /// <summary>The scope's container ids, in definition order (<see cref="ScopeKeys.ContainerId"/>).</summary>
        public List<string> ContainerIds = new List<string>();
        /// <summary>The definition the first activation won with. A later activation with a different one is refused.</summary>
        public ScopeDefinition Definition = new ScopeDefinition();
        /// <summary>When the row was created, and when it was last activated. NEB-240 aggregates idle age over these rows.</summary>
        public DateTime CreatedAt, UpdatedAt;
        /// <summary>Who last asked for it, for logs and the dashboard. Free-form; Nebula attaches no meaning to it.</summary>
        public string Requester = "";
    }

    /// <summary>What a caller asks for when it wants a scope to exist.</summary>
    public sealed class ScopeActivationRequest
    {
        /// <summary>The opaque key. Two callers passing the same key get the same scope; a game-supplied unique key is a private copy.</summary>
        public string ScopeKey = "";
        /// <summary>What to bring into being. Required on the first activation; on a later one it must match the stored definition.</summary>
        public ScopeDefinition Definition = new ScopeDefinition();
        /// <summary>
        /// Create the lease rows already assigned to this worker, so the worker that asked owns them from the first
        /// change anyone sees (this is what <see cref="NebulaWorker.PrepareInstance"/> has always done). Empty when
        /// the caller is not a worker: the rows are born orphaned and the orchestrator deals them out.
        /// </summary>
        public string PreferredWorkerId = "";
        /// <summary>Free-form caller name, kept on the row for logs.</summary>
        public string Requester = "";
    }

    /// <summary>
    /// Where the orchestrator keeps the claim on a scope key between runs. The claim is a genuine uniqueness
    /// constraint on the key (a unique column, or an exclusive file create), not a lock: two callers racing on the
    /// same key are both handed the same stored definition, whichever of them got there first, and the loser's
    /// definition is discarded. Implemented by <see cref="FileControlPlaneStorage"/> and
    /// <c>SqlControlPlaneStorage</c>; a storage that does not implement it (memory) leaves activation idempotent
    /// only for as long as the orchestrator lives.
    /// </summary>
    public interface IScopeStore
    {
        /// <summary>
        /// Store <paramref name="definition"/> under <paramref name="scopeKey"/> if the key is free, and return the
        /// definition that is stored under it now — the caller's, or the one that was already there.
        /// </summary>
        string ClaimScope(string scopeKey, string definition);
        /// <summary>Drop the claim, so the key may be activated again with a different definition.</summary>
        void ReleaseScope(string scopeKey);
        /// <summary>Drop every claim (a control-plane reset).</summary>
        void ClearScopes();
    }

    /// <summary>The scope row and the scope definition as JSON, inside the control-plane document and the activation op.</summary>
    public static class ScopeJson
    {
        public static string WriteDefinition(ScopeDefinition d)
        {
            var sb = new StringBuilder(256);
            WriteDefinition(new JsonWriter(sb), d);
            return sb.ToString();
        }

        public static void WriteDefinition(JsonWriter w, ScopeDefinition d)
        {
            d = d ?? new ScopeDefinition();
            w.BeginObject();
            w.Prop("kind", d.Kind ?? ScopeKind.Parts);
            w.Key("parts");
            w.BeginArray();
            if (d.Parts != null)
            {
                foreach (var part in d.Parts)
                {
                    if (part == null) continue;
                    w.BeginObject();
                    w.Prop("partId", part.PartId ?? "");
                    w.Key("center"); ControlPlaneJson.Vec(w, part.Center);
                    w.Key("size"); ControlPlaneJson.Vec(w, part.Size);
                    w.Prop("content", part.ContentResource ?? "");
                    w.EndObject();
                }
            }
            w.EndArray();
            w.Prop("observePublic", d.ObservePublic);
            w.Key("observationCenter"); ControlPlaneJson.Vec(w, d.ObservationCenter);
            w.Key("observationSize"); ControlPlaneJson.Vec(w, d.ObservationSize);
            w.Prop("payload", d.Payload ?? "");
            w.EndObject();
        }

        /// <summary>Read a definition written by <see cref="WriteDefinition(ScopeDefinition)"/>. An empty or malformed document reads as an empty definition.</summary>
        public static ScopeDefinition ReadDefinition(string json)
        {
            if (string.IsNullOrEmpty(json) || !PersistenceJson.TryParseObject(json, out var root, out _)) return new ScopeDefinition();
            return ReadDefinition(root);
        }

        public static ScopeDefinition ReadDefinition(Dictionary<string, object> o)
        {
            var d = new ScopeDefinition();
            if (o == null) return d;
            string kind = ControlPlaneJson.Str(o, "kind");
            if (!string.IsNullOrEmpty(kind)) d.Kind = kind;
            if (o.TryGetValue("parts", out var parts) && parts is List<object> list)
            {
                foreach (var item in list)
                {
                    if (!(item is Dictionary<string, object> p)) continue;
                    d.Parts.Add(new ScopePart
                    {
                        PartId = ControlPlaneJson.Str(p, "partId"),
                        Center = ControlPlaneJson.Vec(p, "center"),
                        Size = ControlPlaneJson.Vec(p, "size"),
                        ContentResource = ControlPlaneJson.Str(p, "content"),
                    });
                }
            }
            d.ObservePublic = ControlPlaneJson.Bool(o, "observePublic");
            d.ObservationCenter = ControlPlaneJson.Vec(o, "observationCenter");
            d.ObservationSize = ControlPlaneJson.Vec(o, "observationSize");
            d.Payload = ControlPlaneJson.Str(o, "payload");
            return d;
        }

        public static void WriteScope(JsonWriter w, ScopeInfo s)
        {
            w.BeginObject();
            w.Prop("scopeKey", s.ScopeKey ?? "");
            // As a decimal string, not a number: the document's reader turns every JSON number into a double, and
            // this is a full 64-bit hash that would come back off by a few hundred.
            w.Prop("instanceId", s.InstanceId.ToString(CultureInfo.InvariantCulture));
            w.Prop("state", s.State ?? ScopeState.Active);
            w.Prop("requester", s.Requester ?? "");
            w.Prop("createdAt", ControlPlaneJson.ToUnixMs(s.CreatedAt));
            w.Prop("updatedAt", ControlPlaneJson.ToUnixMs(s.UpdatedAt));
            w.Key("containers");
            w.BeginArray();
            if (s.ContainerIds != null) foreach (var id in s.ContainerIds) w.Value(id ?? "");
            w.EndArray();
            w.Key("definition");
            WriteDefinition(w, s.Definition);
            w.EndObject();
        }

        public static ScopeInfo ReadScope(Dictionary<string, object> o)
        {
            var s = new ScopeInfo
            {
                ScopeKey = ControlPlaneJson.Str(o, "scopeKey"),
                State = ControlPlaneJson.Str(o, "state"),
                Requester = ControlPlaneJson.Str(o, "requester"),
                CreatedAt = ControlPlaneJson.FromUnixMs(ControlPlaneJson.Num(o, "createdAt")),
                UpdatedAt = ControlPlaneJson.FromUnixMs(ControlPlaneJson.Num(o, "updatedAt")),
            };
            if (string.IsNullOrEmpty(s.State)) s.State = ScopeState.Active;
            // The isolation id is a pure function of the key, so a row that lost it (or never had it) is not broken.
            s.InstanceId = ulong.TryParse(ControlPlaneJson.Str(o, "instanceId"), NumberStyles.None, CultureInfo.InvariantCulture, out ulong instanceId)
                ? instanceId : ScopeKeys.Hash(s.ScopeKey);
            if (o.TryGetValue("containers", out var ids) && ids is List<object> list)
                foreach (var id in list) s.ContainerIds.Add(PersistenceJson.AsString(id) ?? "");
            s.Definition = o.TryGetValue("definition", out var def) && def is Dictionary<string, object> d
                ? ReadDefinition(d) : new ScopeDefinition();
            return s;
        }
    }

    // ---------------------------------------------------------------------------------------- the state machine

    public sealed partial class LocalControlPlane
    {
        private readonly List<ScopeInfo> _scopes = new List<ScopeInfo>();
        /// <summary>The durable claim on a scope key; null keeps activation idempotent only within this process.</summary>
        internal IScopeStore ScopeStore;

        public IReadOnlyList<ScopeInfo> Scopes => _scopes;

        /// <summary>
        /// Make the scope named by <paramref name="request"/> exist, or leave the one that already exists alone.
        /// Called on the orchestrator's main thread, which serialises it against every other control-plane write,
        /// so the check and the create are one step; the durable claim (<see cref="IScopeStore"/>) makes the same
        /// hold across orchestrator runs.
        /// </summary>
        public void ActivateScope(ScopeActivationRequest request)
        {
            if (request == null) return;
            string key = request.ScopeKey ?? "";
            if (key.Length == 0)
            {
                NebulaLog.Warn("control plane: a scope activation needs a key (the public world is not activated)");
                return;
            }
            var definition = request.Definition ?? new ScopeDefinition();
            string reason = definition.Validate();
            if (reason != null)
            {
                NebulaLog.Warn($"control plane: refusing to activate scope '{key}': {reason}");
                return;
            }
            var scope = this.FindScope(key);
            string wanted = ScopeJson.WriteDefinition(definition);
            if (scope == null && ScopeStore != null)
            {
                // The uniqueness constraint. Whatever comes back is what this key names from now on: our own
                // definition when the key was free, or the one a racing caller (or an earlier run) stored.
                string claimed;
                try { claimed = ScopeStore.ClaimScope(key, wanted); }
                catch (Exception e)
                {
                    NebulaLog.Warn($"control plane: could not claim scope '{key}': {e.Message}; not activating");
                    return;
                }
                if (!string.IsNullOrEmpty(claimed) && !string.Equals(claimed, wanted, StringComparison.Ordinal))
                {
                    definition = ScopeJson.ReadDefinition(claimed);
                    wanted = ScopeJson.WriteDefinition(definition);
                }
            }
            if (scope != null && !string.Equals(ScopeJson.WriteDefinition(scope.Definition), wanted, StringComparison.Ordinal))
            {
                // Same key, different content: joining them would put two different worlds in one scope. The row
                // that is already there stands and the caller keeps whatever scope it is in.
                NebulaLog.Warn($"control plane: scope '{key}' already names different content; the stored definition stands");
                return;
            }
            bool created = scope == null;
            if (created)
            {
                scope = new ScopeInfo { ScopeKey = key, InstanceId = ScopeKeys.Hash(key), State = ScopeState.Active, CreatedAt = Now };
                _scopes.Add(scope);
            }
            scope.Definition = definition;
            scope.Requester = request.Requester ?? "";
            scope.UpdatedAt = Now;
            scope.ContainerIds.Clear();
            foreach (var part in definition.Parts)
            {
                string containerId = ScopeKeys.ContainerId(key, part.PartId);
                scope.ContainerIds.Add(containerId);
                EnsureRuntimeContainer(containerId, new Bounds(part.Center, part.Size), request.PreferredWorkerId ?? "", new InstanceContainerInfo
                {
                    InstanceId = scope.InstanceId,
                    ContentResource = part.ContentResource ?? "",
                    ScopeKey = key,
                    ObservePublic = definition.ObservePublic,
                    ObservationCenter = definition.ObservationCenter,
                    ObservationSize = definition.ObservationSize,
                });
            }
            if (created) NebulaLog.Info($"control plane: scope '{key}' activated with {scope.ContainerIds.Count} container(s)");
            Touch();
        }

        /// <summary>Drop a scope row, its claim and the lease rows of its containers. The mechanism NEB-240's retirement policy drives.</summary>
        public void RemoveScope(string scopeKey)
        {
            var scope = this.FindScope(scopeKey);
            if (scope == null) return;
            foreach (var id in scope.ContainerIds) RemoveContainer(id);
            _scopes.Remove(scope);
            if (ScopeStore != null)
            {
                try { ScopeStore.ReleaseScope(scopeKey); }
                catch (Exception e) { NebulaLog.Warn($"control plane: could not release the claim on scope '{scopeKey}': {e.Message}"); }
            }
            Touch();
        }

        internal void ClearScopes()
        {
            _scopes.Clear();
            if (ScopeStore == null) return;
            try { ScopeStore.ClearScopes(); }
            catch (Exception e) { NebulaLog.Warn($"control plane: could not clear the scope claims: {e.Message}"); }
        }

        internal void ImportScopes(List<ScopeInfo> scopes)
        {
            _scopes.Clear();
            if (scopes != null) _scopes.AddRange(scopes);
        }
    }

    public sealed partial class ControlPlaneHost
    {
        public IReadOnlyList<ScopeInfo> Scopes => _plane.Scopes;
        public void ActivateScope(ScopeActivationRequest request) => _plane.ActivateScope(request);
        public void RemoveScope(string scopeKey) => _plane.RemoveScope(scopeKey);
    }

    public sealed partial class RemoteControlPlane
    {
        private readonly List<ScopeInfo> _scopes = new List<ScopeInfo>();

        public IReadOnlyList<ScopeInfo> Scopes => _scopes;

        public void ActivateScope(ScopeActivationRequest request) =>
            Enqueue(_op.Op(ControlPlaneJson.ActivateScope)
                .Arg("scopeKey", request?.ScopeKey ?? "")
                .Arg("definition", ScopeJson.WriteDefinition(request?.Definition))
                .Arg("workerId", request?.PreferredWorkerId ?? "")
                .Arg("requester", request?.Requester ?? "").End());

        public void RemoveScope(string scopeKey) => Enqueue(_op.Op(ControlPlaneJson.RemoveScope).Arg("scopeKey", scopeKey ?? "").End());
    }
}
