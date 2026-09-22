using System;
using System.Collections.Generic;
using System.Globalization;

namespace Nebula
{
    /// <summary>
    /// Serves the orchestrator's <see cref="IPersistenceStore"/> to workers over the orchestrator's HTTP server, so
    /// a worker needs no database driver of its own (<see cref="RemotePersistenceStore"/> is its client). Every
    /// request is handled on the main thread from the orchestrator's command pump, which is where the store's
    /// callbacks land: a read returns <see cref="OrchestratorHttpServer.Response.Pending"/> and completes when the
    /// store answers, usually on the next tick. A write answers the same way, once the store's
    /// <see cref="IPersistenceStore.WhenWritten"/> barrier says the backend has it, so a worker's
    /// <see cref="RemotePersistenceStore.WhenWritten"/> is a real durability barrier and not just proof of delivery.
    /// <list type="bullet">
    /// <item><c>POST /api/store/save</c> <c>{"records":[...]}</c>, <c>POST /api/store/delete</c> <c>{"keys":[...]}</c>, <c>POST /api/store/clear</c></item>
    /// <item><c>GET /api/store/record?key=</c>, <c>GET /api/store/container?id=</c>, <c>GET /api/store/carried?key=</c>, <c>GET /api/store/all</c></item>
    /// <item><c>GET /api/store/count?scope=&amp;container=</c>: how many records a scope (optionally one of its containers) holds, without reading them</item>
    /// <item><c>GET /api/store/status</c>: backend, whether it is connected, how many records it holds</item>
    /// </list>
    /// Requests carry the mesh token in <c>X-Nebula-Token</c> when the mesh has one.
    /// </summary>
    public sealed class PersistenceHost
    {
        public const string Prefix = "/api/store";

        private readonly IPersistenceStore _store;
        private readonly string _token;

        public PersistenceHost(IPersistenceStore store, string token)
        {
            _store = store;
            _token = string.IsNullOrEmpty(token) ? null : token;
        }

        public IPersistenceStore Store => _store;

        /// <summary>Main thread: handle <paramref name="req"/> when it addresses the store. False for any other request.</summary>
        public bool TryHandle(OrchestratorHttpServer.Request req, out OrchestratorHttpServer.Response response)
        {
            response = default;
            string path = req.Path.TrimEnd('/');
            if (!path.StartsWith(Prefix + "/", StringComparison.Ordinal)) return false;
            if (_token != null && req.Token != _token) { response = OrchestratorHttpServer.Response.Error(401, "missing or wrong mesh token"); return true; }
            string op = path.Substring(Prefix.Length + 1);
            if (_store == null) { response = OrchestratorHttpServer.Response.Error(409, "persistence is off"); return true; }
            switch (req.Method + " " + op)
            {
                case "GET status":
                    response = OrchestratorHttpServer.Response.Json(200, $"{{\"ok\":true,\"backend\":{JsonWriter.Quote(_store.Backend)},\"connected\":{(_store.IsConnected ? "true" : "false")},\"count\":{_store.KnownCount.ToString(CultureInfo.InvariantCulture)}}}");
                    return true;
                case "POST save":
                {
                    if (!PersistenceJson.TryParseObject(req.Body, out var body, out string error)) { response = OrchestratorHttpServer.Response.Error(400, error); return true; }
                    List<PersistedEntityRecord> records;
                    try { records = PersistedRecordJson.ParseList(body); }
                    catch (Exception e) { response = OrchestratorHttpServer.Response.Error(400, "malformed record: " + e.Message); return true; }
                    foreach (var r in records) _store.Save(r);
                    // Answer only once the backing store has the writes: the worker's RemotePersistenceStore turns a
                    // 2xx here into its own WhenWritten barrier, so acking early would promise a durability it lacks.
                    var saved = OrchestratorHttpServer.Response.Json(200, $"{{\"ok\":true,\"saved\":{records.Count.ToString(CultureInfo.InvariantCulture)}}}");
                    CompleteWhenWritten(req, saved);
                    response = OrchestratorHttpServer.Response.Pending;
                    return true;
                }
                case "POST delete":
                {
                    if (!PersistenceJson.TryParseObject(req.Body, out var body, out string error)) { response = OrchestratorHttpServer.Response.Error(400, error); return true; }
                    int n = 0;
                    if (body.TryGetValue("keys", out var v) && v is List<object> keys)
                    {
                        foreach (var k in keys) if (k is string key && key.Length > 0) { _store.Delete(key); n++; }
                    }
                    var deleted = OrchestratorHttpServer.Response.Json(200, $"{{\"ok\":true,\"deleted\":{n.ToString(CultureInfo.InvariantCulture)}}}");
                    CompleteWhenWritten(req, deleted);
                    response = OrchestratorHttpServer.Response.Pending;
                    return true;
                }
                case "POST clear":
                    _store.Clear();
                    CompleteWhenWritten(req, OrchestratorHttpServer.Response.Json(200, "{\"ok\":true}"));
                    response = OrchestratorHttpServer.Response.Pending;
                    return true;
                case "GET record":
                    _store.Load(req.GetQuery("key"), r => req.Complete(OrchestratorHttpServer.Response.Json(200, PersistedRecordJson.WriteOne(r))));
                    response = OrchestratorHttpServer.Response.Pending;
                    return true;
                case "GET container":
                    _store.LoadContainer(req.GetQuery("id"), rs => req.Complete(OrchestratorHttpServer.Response.Json(200, PersistedRecordJson.WriteList(rs))));
                    response = OrchestratorHttpServer.Response.Pending;
                    return true;
                case "GET carried":
                    _store.LoadCarried(req.GetQuery("key"), rs => req.Complete(OrchestratorHttpServer.Response.Json(200, PersistedRecordJson.WriteList(rs))));
                    response = OrchestratorHttpServer.Response.Pending;
                    return true;
                case "GET count":
                    // Only the number travels: this is the "is there anything saved for this scope?" question a
                    // worker asks on the path that brings a scope to life (docs/lifecycle-hooks.md).
                    _store.CountRecords(req.GetQuery("scope"), req.GetQuery("container"),
                        n => req.Complete(OrchestratorHttpServer.Response.Json(200, $"{{\"ok\":true,\"count\":{n.ToString(CultureInfo.InvariantCulture)}}}")));
                    response = OrchestratorHttpServer.Response.Pending;
                    return true;
                case "GET all":
                    _store.LoadWhere(r => true, rs => req.Complete(OrchestratorHttpServer.Response.Json(200, PersistedRecordJson.WriteList(rs))));
                    response = OrchestratorHttpServer.Response.Pending;
                    return true;
                default:
                    response = OrchestratorHttpServer.Response.Error(404, $"no store endpoint '{req.Method} {op}'");
                    return true;
            }
        }

        private void CompleteWhenWritten(OrchestratorHttpServer.Request request, OrchestratorHttpServer.Response response)
        {
            // The HTTP host gives up after three seconds. Do not keep its request/context alive indefinitely if the
            // store is no longer ticking; a retry will receive the durable acknowledgement once writing resumes.
            var weak = new WeakReference<OrchestratorHttpServer.Request>(request);
            _store.WhenWritten(() =>
            {
                if (weak.TryGetTarget(out var pending)) pending.Complete(response);
            });
        }
    }
}
