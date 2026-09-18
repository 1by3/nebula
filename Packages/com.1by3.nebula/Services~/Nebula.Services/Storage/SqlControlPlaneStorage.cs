using System;

namespace Nebula
{
    /// <summary>
    /// <see cref="IControlPlaneStorage"/> in the orchestrator's database: the document sits in the single row of
    /// <c>nebula_control_plane</c>, replaced on every save. Each call opens its own connection (pooled), so the
    /// host's background save and the persistence store's writer never share one.
    /// </summary>
    public sealed class SqlControlPlaneStorage : IControlPlaneStorage, IGatewaySessionStore
    {
        private readonly NebulaDatabase _db;

        public SqlControlPlaneStorage(NebulaDatabase db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        public string Backend => _db.Provider;

        public string Load()
        {
            using var c = _db.Open();
            using var cmd = NebulaDatabase.Command(c, "SELECT json FROM nebula_control_plane WHERE id = 1");
            var value = cmd.ExecuteScalar();
            return value is string s && s.Length > 0 ? s : null;
        }

        public void Save(string json)
        {
            using var c = _db.Open();
            NebulaDatabase.Execute(c,
                "INSERT INTO nebula_control_plane (id, json, updated_at) VALUES (1, @json, @at) ON CONFLICT (id) DO UPDATE SET json = excluded.json, updated_at = excluded.updated_at",
                ("@json", json ?? "{}"), ("@at", ControlPlaneJson.ToUnixMs(DateTime.UtcNow)));
        }

        public void Dispose() { }

        string IGatewaySessionStore.LoadSession(string identity)
        {
            using var c = _db.Open();
            using var cmd = NebulaDatabase.Command(c, "SELECT state FROM nebula_gateway_session WHERE identity = @identity", ("@identity", identity));
            return cmd.ExecuteScalar() as string;
        }

        void IGatewaySessionStore.SaveSession(string identity, string value)
        {
            using var c = _db.Open();
            if (value == null)
                NebulaDatabase.Execute(c, "DELETE FROM nebula_gateway_session WHERE identity = @identity", ("@identity", identity));
            else
                NebulaDatabase.Execute(c, "INSERT INTO nebula_gateway_session (identity, state) VALUES (@identity, @state) ON CONFLICT (identity) DO UPDATE SET state = excluded.state",
                    ("@identity", identity), ("@state", value));
        }

        void IGatewaySessionStore.ClearSessions()
        {
            using var c = _db.Open();
            NebulaDatabase.Execute(c, "DELETE FROM nebula_gateway_session");
        }
    }
}
