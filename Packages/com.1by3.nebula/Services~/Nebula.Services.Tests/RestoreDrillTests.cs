using NUnit.Framework;
using Nebula;
using Drill = Nebula.RestoreDrill.Program;

namespace Nebula.ServiceTests;

[TestFixture]
public class RestoreDrillTests
{
    private string _directory = null!;
    private string _database = null!;
    private string File(string name) => Path.Combine(_directory, name + ".json");

    [SetUp]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "nebula-restore-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _database = "sqlite:" + Path.Combine(_directory, "drill.db");
        Assert.That(Drill.Main(new[] { "seed", "--db", _database, "--entities", "2" }), Is.Zero);
        // Existing worlds have records saved more than once.
        Assert.That(Drill.Main(new[] { "seed", "--db", _database, "--entities", "2" }), Is.Zero);
    }

    [TearDown]
    public void Cleanup()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, true);
    }

    private void Snapshot(string name) => Assert.That(Drill.Main(new[] { "snapshot", "--db", _database, "--out", File(name) }), Is.Zero);
    private int Compare() => Drill.Main(new[] { "compare", "--before", File("before"), "--after", File("after") });

    [Test]
    public void PortableRestorePreservesUpdatedRecordsAndDurableClaims()
    {
        Snapshot("before");
        Assert.That(Drill.Main(new[] { "export", "--db", _database, "--out", File("backup") }), Is.Zero);
        Assert.That(Drill.Main(new[] { "wipe", "--db", _database }), Is.Zero);
        Snapshot("wiped");
        using (var snapshot = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(File("wiped"))))
        {
            Assert.That(snapshot.RootElement.GetProperty("records").GetInt32(), Is.Zero);
            Assert.That(snapshot.RootElement.GetProperty("sessions").GetInt32(), Is.Zero);
            Assert.That(snapshot.RootElement.GetProperty("scopes").GetInt32(), Is.Zero);
        }
        Assert.That(Drill.Main(new[] { "import", "--db", _database, "--in", File("backup") }), Is.Zero);
        Snapshot("after");
        Assert.That(Compare(), Is.Zero);
        using var db = NebulaDatabase.Open(DatabaseUrl.Parse(_database, ""));
        var storage = new SqlControlPlaneStorage(db);
        Assert.That(((IGatewaySessionStore)storage).LoadSession("restore-drill"), Is.EqualTo("drill-session-1234"));
        Assert.That(((IScopeStore)storage).ClaimScope("restore-drill", "unexpected replacement"), Is.EqualTo("drill-scope-1234"));
    }

    [TestCase("sessions")]
    [TestCase("scopes")]
    [TestCase("document")]
    public void ComparisonRejectsLostClaimsAndUnexaminedDocumentFields(string corruption)
    {
        Snapshot("before");
        using (var db = NebulaDatabase.Open(DatabaseUrl.Parse(_database, "")))
        {
            var storage = new SqlControlPlaneStorage(db);
            if (corruption == "sessions") ((IGatewaySessionStore)storage).ClearSessions();
            if (corruption == "scopes") ((IScopeStore)storage).ClearScopes();
            if (corruption == "document")
            {
                var document = System.Text.Json.Nodes.JsonNode.Parse(storage.Load())!.AsObject();
                document["document"] = "changed-identity";
                storage.Save(document.ToJsonString());
            }
        }
        Snapshot("after");
        Assert.That(Compare(), Is.EqualTo(1));
    }

    [Test]
    public void FailedImportRollsBackEveryDurableTable()
    {
        Snapshot("before");
        Assert.That(Drill.Main(new[] { "export", "--db", _database, "--out", File("backup") }), Is.Zero);
        var backup = System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(File("backup")))!;
        backup["records"]![0]!["Name"] = "must not survive rollback";
        var sessions = backup["claims"]!["sessions"]!.AsArray();
        sessions.Add(sessions[0]!.DeepClone()); // The duplicate fails after the entity and document inserts.
        System.IO.File.WriteAllText(File("backup"), backup.ToJsonString());
        Assert.That(Drill.Main(new[] { "import", "--db", _database, "--in", File("backup") }), Is.EqualTo(1));
        Snapshot("after");
        Assert.That(Compare(), Is.Zero);
    }
}
