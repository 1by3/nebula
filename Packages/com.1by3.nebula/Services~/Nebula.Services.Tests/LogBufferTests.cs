using System.Text.Json;
using Nebula;
using NUnit.Framework;

namespace Nebula.ServiceTests;

[TestFixture]
public class LogBufferTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Test]
    public void AcceptsBatchesAndPagesByCursor()
    {
        var logs = new LogBuffer(100);
        string error = logs.Accept("{\"lines\":[{\"at\":\"2026-09-16T10:00:00Z\",\"role\":\"worker\",\"instance\":\"w1\",\"level\":\"info\",\"message\":\"hello\"},{\"role\":\"gateway\",\"instance\":\"gw1\",\"level\":\"warn\",\"message\":\"slow\"}]}", out int accepted);
        Assert.That(error, Is.Null);
        Assert.That(accepted, Is.EqualTo(2));
        Assert.That(logs.Count, Is.EqualTo(2));

        var page = Parse(logs.Query(0, null, null, 10));
        Assert.That(page.GetProperty("lines").GetArrayLength(), Is.EqualTo(2));
        Assert.That(page.GetProperty("lines")[0].GetProperty("message").GetString(), Is.EqualTo("hello"));
        Assert.That(page.GetProperty("lines")[1].GetProperty("level").GetString(), Is.EqualTo("warn"));
        Assert.That(page.GetProperty("lines")[1].GetProperty("at").GetString(), Is.Not.Empty, "a missing timestamp is filled in");
        long next = page.GetProperty("next").GetInt64();
        Assert.That(next, Is.EqualTo(2));
        Assert.That(page.GetProperty("more").GetBoolean(), Is.False);

        Assert.That(Parse(logs.Query(next, null, null, 10)).GetProperty("lines").GetArrayLength(), Is.Zero, "nothing new after the cursor");
        logs.Append("worker", "w2", "error", "", "boom");
        var after = Parse(logs.Query(next, null, null, 10));
        Assert.That(after.GetProperty("lines").GetArrayLength(), Is.EqualTo(1));
        Assert.That(after.GetProperty("lines")[0].GetProperty("instance").GetString(), Is.EqualTo("w2"));

        var workers = Parse(logs.Query(0, "worker", null, 10));
        Assert.That(workers.GetProperty("lines").GetArrayLength(), Is.EqualTo(2));
        Assert.That(workers.GetProperty("next").GetInt64(), Is.EqualTo(3), "filtered-out lines still advance the cursor");
        var w1 = Parse(logs.Query(0, "worker", "w1", 10));
        Assert.That(w1.GetProperty("lines").GetArrayLength(), Is.EqualTo(1));

        var limited = Parse(logs.Query(0, null, null, 2));
        Assert.That(limited.GetProperty("lines").GetArrayLength(), Is.EqualTo(2));
        Assert.That(limited.GetProperty("more").GetBoolean(), Is.True);
        Assert.That(limited.GetProperty("next").GetInt64(), Is.EqualTo(2));
    }

    [Test]
    public void OldLinesFallOffAndBadBodiesAreRefused()
    {
        var logs = new LogBuffer(16);
        for (int i = 0; i < 40; i++) logs.Append("worker", "w1", "info", "", "line " + i);
        Assert.That(logs.Count, Is.EqualTo(16));
        var page = Parse(logs.Query(0, null, null, 100));
        Assert.That(page.GetProperty("lines")[0].GetProperty("message").GetString(), Is.EqualTo("line 24"));
        Assert.That(page.GetProperty("oldest").GetInt64(), Is.EqualTo(25));
        Assert.That(page.GetProperty("lines").GetArrayLength(), Is.EqualTo(16));

        Assert.That(logs.Accept("", out _), Is.Not.Null);
        Assert.That(logs.Accept("{\"nope\":1}", out _), Does.Contain("lines"));
        Assert.That(logs.Accept("not json", out _), Is.Not.Null);
        Assert.That(logs.Accept(new string('x', LogBuffer.MaxBodyChars + 1), out _), Does.Contain("larger"));
        logs.Append("gateway", "gw1", "info", "", new string('m', LogBuffer.MaxMessageChars + 50));
        var last = Parse(logs.Query(0, "gateway", null, 1)).GetProperty("lines")[0].GetProperty("message").GetString()!;
        Assert.That(last.Length, Is.EqualTo(LogBuffer.MaxMessageChars + 1), "long messages are cut");
    }
}
