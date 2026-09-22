using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Nebula;
using Nebula.ServicePrimitives;

namespace Nebula.RestoreDrill;

/// <summary>
/// The verification half of the backup-and-restore drill (<c>Tools/restore-drill.ps1</c>,
/// <c>docs/control-plane-availability.md</c> D7). The script does the backup and the restore with whatever the
/// engine offers; this tool is what says whether the restored database is the one that was backed up, and it
/// answers that question <b>through Nebula's own store code</b> — <see cref="SqlPersistenceStore"/> and
/// <see cref="SqlControlPlaneStorage"/>, the same classes a worker and an orchestrator read through.
/// <para>
/// That is the whole point of not writing the check in SQL. A drill that compares tables with hand-written
/// queries proves the bytes came back; a drill that reads every record the way the mesh reads it proves the
/// <i>mesh</i> can come back — it goes through the same column list, the same epoch rule and the same decoder,
/// so a schema migration that the store cannot read is a failed drill and not a green one.
/// </para>
/// <para>Verbs: <c>seed</c>, <c>snapshot</c>, <c>wipe</c>, <c>compare</c>, <c>backup</c>, <c>restore</c>,
/// <c>export</c>, <c>import</c>. Exit code 0 means the verb succeeded (for <c>compare</c>: the two snapshots
/// match); anything else is a failure with a reason on stderr.</para>
/// </summary>
public static class Program
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static int Main(string[] args)
    {
        if (args.Length == 0) { Usage(); return 2; }
        string verb = args[0];
        var options = Parse(args.Skip(1));
        try
        {
            return verb switch
            {
                "seed" => Seed(options),
                "snapshot" => Snapshot(options),
                "wipe" => Wipe(options),
                "compare" => Compare(options),
                "backup" => Backup(options),
                "restore" => Restore(options),
                "export" => Export(options),
                "import" => Import(options),
                _ => Fail($"unknown verb '{verb}'"),
            };
        }
        catch (Exception e)
        {
            return Fail(e.Message);
        }
    }

    private static void Usage()
    {
        Console.Error.WriteLine("""
            nebula-restore-drill <verb> [options]

              seed     --db <url> [--entities N] [--containers N] [--seed N] [--scope KEY]
              snapshot --db <url> --out <file.json>
              wipe     --db <url>
              compare  --before <file.json> --after <file.json> [--out <file.json>]
              backup   --db <url> --out <file>          (sqlite only: an online VACUUM INTO copy)
              restore  --db <url> --in <file>           (sqlite only: the copy back into place)
              export   --db <url> --out <file.json>     (engine-independent: every record plus the document)
              import   --db <url> --in <file.json>      (the inverse, through the store)

            <url> is a Nebula database URL: sqlite:<path> or postgres://user:pass@host:port/db
            """);
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine("restore-drill: " + message);
        return 1;
    }

    private static Dictionary<string, string> Parse(IEnumerable<string> args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? key = null;
        foreach (string arg in args)
        {
            if (arg.StartsWith("--", StringComparison.Ordinal)) { key = arg.Substring(2); options[key] = "true"; }
            else if (key != null) { options[key] = arg; key = null; }
        }
        return options;
    }

    private static string Required(Dictionary<string, string> o, string name) =>
        o.TryGetValue(name, out var v) && v != "true" ? v : throw new ArgumentException($"--{name} is required");

    private static int Int(Dictionary<string, string> o, string name, int fallback) =>
        o.TryGetValue(name, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : fallback;

    private static NebulaDatabase OpenDatabase(Dictionary<string, string> o) =>
        NebulaDatabase.Open(DatabaseUrl.Parse(Required(o, "db"), ""));

    /// <summary>Drive a store's callbacks until <paramref name="done"/> or the time is up. The store's reads land on this thread.</summary>
    private static void Pump(SqlPersistenceStore store, Func<bool> done, double seconds = 120)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!done() && clock.Elapsed.TotalSeconds < seconds) { store.Tick(); Thread.Sleep(5); }
        store.Tick();
        if (!done()) throw new TimeoutException($"the store did not answer within {seconds:0} s");
    }

    // ------------------------------------------------------------------------------------------------- seed

    /// <summary>
    /// Write a known world into the entity store and a known topology into the control-plane document. Both are
    /// derived from <c>--seed</c>, so two runs of the drill on two machines seed byte-identical content and a
    /// snapshot digest can be compared against a checked-in one.
    /// </summary>
    private static int Seed(Dictionary<string, string> o)
    {
        int entities = Int(o, "entities", 250), containers = Int(o, "containers", 8), seed = Int(o, "seed", 1234);
        string scope = o.TryGetValue("scope", out var s) && s != "true" ? s : "";
        using var db = OpenDatabase(o);
        using var store = new SqlPersistenceStore(db);
        store.Connect();
        var random = new Random(seed);
        for (int i = 0; i < entities; i++)
        {
            var state = new byte[16];
            random.NextBytes(state);
            store.Save(new PersistedEntityRecord
            {
                Key = $"drill/{seed}/{i:D6}",
                PrefabId = (ushort)(i % 7),
                PrefabName = "DrillProp" + (i % 7),
                ScopeKey = scope,
                ContainerId = "c" + (i % containers),
                LocalPosition = new Vector3((float)random.NextDouble() * 64f, 0, (float)random.NextDouble() * 64f),
                LocalRotation = Quaternion.identity,
                Velocity = Vector3.zero,
                Epoch = (uint)(1 + i % 3),
                Name = "drill-" + i,
                State = state,
                SavedBy = "restore-drill",
            });
        }
        bool written = false;
        store.WhenWritten(() => written = true);
        Pump(store, () => written);

        var plane = new LocalControlPlane();
        plane.Connect();
        for (int i = 0; i < containers; i++)
        {
            plane.EnsureContainer("c" + i);
            plane.AssignContainer("c" + i, "w" + (1 + i % 2));
        }
        for (int i = 0; i < 2; i++) plane.RegisterWorker("w" + (i + 1), (uint)(i + 1), "127.0.0.1", (ushort)(7101 + i));
        plane.SetSetting("nebula.restore-drill.seed", seed.ToString(CultureInfo.InvariantCulture));
        new SqlControlPlaneStorage(db).Save(plane.ToJson());

        Console.WriteLine($"seeded {entities} record(s) in {containers} container(s) and a {containers}-lease control plane into {db.Display}");
        return 0;
    }

    // --------------------------------------------------------------------------------------------- snapshot

    /// <summary>
    /// What the store holds, reduced to something two runs can be compared by: a digest per record (every field,
    /// including the opaque state blob) and a digest of the control-plane document's durable parts. Read through
    /// <see cref="SqlPersistenceStore.LoadWhere"/>, the offline read a tool is meant to use.
    /// </summary>
    private static int Snapshot(Dictionary<string, string> o)
    {
        string output = Required(o, "out");
        using var db = OpenDatabase(o);
        using var store = new SqlPersistenceStore(db);
        store.Connect();
        IReadOnlyList<PersistedEntityRecord>? records = null;
        store.LoadWhere(_ => true, list => records = list);
        Pump(store, () => records != null);

        var digests = records!.Select(r => new { key = r.Key, digest = Digest(r) }).OrderBy(r => r.key, StringComparer.Ordinal).ToList();
        string document = new SqlControlPlaneStorage(db).Load() ?? "";
        var plane = document.Length > 0 ? ControlPlaneJson.Parse(document) : null;

        var snapshot = new
        {
            database = db.Display,
            backend = db.Provider,
            takenAt = DateTime.UtcNow.ToString("O"),
            records = digests.Count,
            recordsDigest = Sha(string.Join("\n", digests.Select(d => d.key + " " + d.digest))),
            controlPlane = new
            {
                present = plane != null,
                workers = plane?.Workers.Count ?? 0,
                leases = plane?.Leases.Count ?? 0,
                settings = plane?.Settings.Count ?? 0,
                // The clock is refreshed on every read, so it is deliberately not part of the digest: it would
                // make every snapshot differ and say nothing about whether the restore worked.
                digest = plane == null ? "" : Sha(string.Join("\n",
                    plane.Leases.OrderBy(l => l.ContainerId, StringComparer.Ordinal)
                         .Select(l => $"{l.ContainerId}|{l.WorkerId}|{l.State}|{l.Epoch}")
                         .Concat(plane.Workers.OrderBy(w => w.WorkerId, StringComparer.Ordinal).Select(w => $"w:{w.WorkerId}|{w.WorkerIndex}|{w.Address}:{w.Port}"))
                         .Concat(plane.Settings.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"s:{kv.Key}={kv.Value}")))),
            },
            entries = digests,
        };
        Write(output, JsonSerializer.Serialize(snapshot, Json));
        Console.WriteLine($"snapshot: {digests.Count} record(s), {plane?.Leases.Count ?? 0} lease(s) -> {output}");
        return 0;
    }

    private static string Digest(PersistedEntityRecord r) => Sha(string.Join("|",
        r.Key, r.PrefabId, r.PrefabName, r.SceneId, r.ScopeKey, r.ContainerId, r.CarrierKey,
        F(r.LocalPosition.x), F(r.LocalPosition.y), F(r.LocalPosition.z),
        F(r.LocalRotation.x), F(r.LocalRotation.y), F(r.LocalRotation.z), F(r.LocalRotation.w),
        F(r.Velocity.x), F(r.Velocity.y), F(r.Velocity.z),
        r.Epoch, r.ServerDriven, r.Owned, r.Name, r.Version, r.SavedBy,
        Convert.ToHexString(r.State ?? Array.Empty<byte>())));

    private static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static string Sha(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    // ------------------------------------------------------------------------------------------------- wipe

    /// <summary>
    /// Empty everything durable: the entity table, the control-plane document, the gateway session claims and the
    /// scope claims. A drill whose wipe left anything behind would pass on the leftovers, so the script takes a
    /// snapshot of the wiped database too and insists it is empty before it restores anything.
    /// </summary>
    private static int Wipe(Dictionary<string, string> o)
    {
        using var db = OpenDatabase(o);
        using var store = new SqlPersistenceStore(db);
        store.Connect();
        store.Clear();
        bool written = false;
        store.WhenWritten(() => written = true);
        Pump(store, () => written);
        var control = new SqlControlPlaneStorage(db);
        control.Save("{}");
        ((IGatewaySessionStore)control).ClearSessions();
        ((IScopeStore)control).ClearScopes();
        Console.WriteLine($"wiped {db.Display}: entities, control-plane document, session claims and scope claims");
        return 0;
    }

    // ---------------------------------------------------------------------------------------------- compare

    private sealed record Entry(string key, string digest);

    private static int Compare(Dictionary<string, string> o)
    {
        var before = JsonDocument.Parse(File.ReadAllText(Required(o, "before"))).RootElement;
        var after = JsonDocument.Parse(File.ReadAllText(Required(o, "after"))).RootElement;
        var a = Entries(before);
        var b = Entries(after);

        var missing = a.Keys.Where(k => !b.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var extra = b.Keys.Where(k => !a.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var changed = a.Where(kv => b.TryGetValue(kv.Key, out var d) && d != kv.Value).Select(kv => kv.Key)
            .OrderBy(k => k, StringComparer.Ordinal).ToList();
        string planeBefore = before.GetProperty("controlPlane").GetProperty("digest").GetString() ?? "";
        string planeAfter = after.GetProperty("controlPlane").GetProperty("digest").GetString() ?? "";
        bool ok = missing.Count == 0 && extra.Count == 0 && changed.Count == 0 && planeBefore == planeAfter;

        var result = new
        {
            ok,
            recordsBefore = a.Count,
            recordsAfter = b.Count,
            missing = missing.Count,
            extra = extra.Count,
            changed = changed.Count,
            controlPlaneMatches = planeBefore == planeAfter,
            firstMissing = missing.Take(5),
            firstExtra = extra.Take(5),
            firstChanged = changed.Take(5),
        };
        string json = JsonSerializer.Serialize(result, Json);
        if (o.TryGetValue("out", out var output) && output != "true") Write(output, json);
        Console.WriteLine(json);
        if (ok) return 0;
        return Fail($"the restored store does not match the backup: {missing.Count} missing, {extra.Count} extra, " +
                    $"{changed.Count} changed, control plane {(planeBefore == planeAfter ? "matches" : "differs")}");
    }

    private static Dictionary<string, string> Entries(JsonElement snapshot) =>
        snapshot.GetProperty("entries").EnumerateArray()
            .ToDictionary(e => e.GetProperty("key").GetString()!, e => e.GetProperty("digest").GetString()!, StringComparer.Ordinal);

    // ------------------------------------------------------------- engine-level backup and restore (sqlite)

    /// <summary>
    /// SQLite's own online backup: <c>VACUUM INTO</c> writes a consistent copy of the whole database from the
    /// connection Nebula already opens, with the write-ahead log folded in, while the mesh keeps running. A file
    /// copy would not be safe with WAL on, and a <c>sqlite3</c> CLI is not something a studio has to install.
    /// </summary>
    private static int Backup(Dictionary<string, string> o)
    {
        using var db = OpenDatabase(o);
        if (db.Provider != "sqlite") return Fail("backup is sqlite-only; use pg_dump, or the engine-independent `export` verb");
        string output = Path.GetFullPath(Required(o, "out"));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        if (File.Exists(output)) File.Delete(output);
        using (var c = db.Open())
            NebulaDatabase.Execute(c, "VACUUM INTO " + Quote(output));
        Console.WriteLine($"backup: {db.Display} -> {output} ({new FileInfo(output).Length} bytes)");
        return 0;
    }

    private static int Restore(Dictionary<string, string> o)
    {
        var url = DatabaseUrl.Parse(Required(o, "db"), "");
        if (url.Scheme != "sqlite") return Fail("restore is sqlite-only; use pg_restore, or the engine-independent `import` verb");
        string input = Path.GetFullPath(Required(o, "in"));
        if (!File.Exists(input)) return Fail($"no backup at {input}");
        string target = Path.GetFullPath(url.Target);
        // Every pooled connection has to go before the file is replaced, or the copy lands under a handle the
        // next open would still be reading through.
        SqliteConnection.ClearAllPools();
        foreach (string sidecar in new[] { target + "-wal", target + "-shm" }) if (File.Exists(sidecar)) File.Delete(sidecar);
        File.Copy(input, target, true);
        Console.WriteLine($"restore: {input} -> sqlite:{target}");
        return 0;
    }

    private static string Quote(string path) => "'" + path.Replace("'", "''") + "'";

    // ------------------------------------------------- engine-independent backup and restore (any backend)

    /// <summary>
    /// Every record and the control-plane document as one JSON file, read through the store. Slower than the
    /// engine's own dump and not a substitute for it in production — but it is the only backup that is portable
    /// between SQLite and PostgreSQL, and it is what the drill falls back to when <c>pg_dump</c> is not on the
    /// machine.
    /// </summary>
    private static int Export(Dictionary<string, string> o)
    {
        string output = Required(o, "out");
        using var db = OpenDatabase(o);
        using var store = new SqlPersistenceStore(db);
        store.Connect();
        IReadOnlyList<PersistedEntityRecord>? records = null;
        store.LoadWhere(_ => true, list => records = list);
        Pump(store, () => records != null);
        var dump = new
        {
            document = new SqlControlPlaneStorage(db).Load() ?? "",
            records = records!.Select(r => new
            {
                r.Key, r.PrefabId, r.PrefabName, r.SceneId, r.ScopeKey, r.ContainerId, r.CarrierKey,
                px = r.LocalPosition.x, py = r.LocalPosition.y, pz = r.LocalPosition.z,
                rx = r.LocalRotation.x, ry = r.LocalRotation.y, rz = r.LocalRotation.z, rw = r.LocalRotation.w,
                vx = r.Velocity.x, vy = r.Velocity.y, vz = r.Velocity.z,
                r.Epoch, r.ServerDriven, r.Owned, r.Name, r.SavedBy,
                state = Convert.ToBase64String(r.State ?? Array.Empty<byte>()),
            }).OrderBy(r => r.Key, StringComparer.Ordinal).ToList(),
        };
        Write(output, JsonSerializer.Serialize(dump, Json));
        Console.WriteLine($"export: {dump.records.Count} record(s) -> {output}");
        return 0;
    }

    private static int Import(Dictionary<string, string> o)
    {
        string input = Required(o, "in");
        var dump = JsonDocument.Parse(File.ReadAllText(input)).RootElement;
        using var db = OpenDatabase(o);
        using var store = new SqlPersistenceStore(db);
        store.Connect();
        int n = 0;
        foreach (var r in dump.GetProperty("records").EnumerateArray())
        {
            store.Save(new PersistedEntityRecord
            {
                Key = r.GetProperty("Key").GetString() ?? "",
                PrefabId = (ushort)r.GetProperty("PrefabId").GetUInt32(),
                PrefabName = r.GetProperty("PrefabName").GetString() ?? "",
                SceneId = r.GetProperty("SceneId").GetUInt32(),
                ScopeKey = r.GetProperty("ScopeKey").GetString() ?? "",
                ContainerId = r.GetProperty("ContainerId").GetString() ?? "",
                CarrierKey = r.GetProperty("CarrierKey").GetString() ?? "",
                LocalPosition = new Vector3(r.GetProperty("px").GetSingle(), r.GetProperty("py").GetSingle(), r.GetProperty("pz").GetSingle()),
                LocalRotation = new Quaternion(r.GetProperty("rx").GetSingle(), r.GetProperty("ry").GetSingle(), r.GetProperty("rz").GetSingle(), r.GetProperty("rw").GetSingle()),
                Velocity = new Vector3(r.GetProperty("vx").GetSingle(), r.GetProperty("vy").GetSingle(), r.GetProperty("vz").GetSingle()),
                Epoch = r.GetProperty("Epoch").GetUInt32(),
                ServerDriven = r.GetProperty("ServerDriven").GetBoolean(),
                Owned = r.GetProperty("Owned").GetBoolean(),
                Name = r.GetProperty("Name").GetString() ?? "",
                SavedBy = r.GetProperty("SavedBy").GetString() ?? "",
                State = Convert.FromBase64String(r.GetProperty("state").GetString() ?? ""),
            });
            n++;
        }
        bool written = false;
        store.WhenWritten(() => written = true);
        Pump(store, () => written);
        string document = dump.GetProperty("document").GetString() ?? "";
        if (document.Length > 0) new SqlControlPlaneStorage(db).Save(document);
        Console.WriteLine($"import: {n} record(s) from {input} -> {db.Display}");
        return 0;
    }

    private static void Write(string path, string content)
    {
        string full = Path.GetFullPath(path);
        string? dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(full, content);
    }
}
