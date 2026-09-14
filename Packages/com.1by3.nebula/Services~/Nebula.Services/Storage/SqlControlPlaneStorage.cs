using System;

namespace Nebula
{
    /// <summary>
    /// <see cref="IControlPlaneStorage"/> in the orchestrator's database: the document sits in the single row of
    /// <c>nebula_control_plane</c>, replaced on every save. Each call opens its own connection (pooled), so the
    /// host's background save and the persistence store's writer never share one.
    /// </summary>
    public sealed class SqlControlPlaneStorage : IControlPlaneStorage
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
    }
}
