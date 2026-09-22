using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nebula.ServiceTests;

/// <summary>
/// A recorded client session: the exact frames a client of one protocol version sent to a gateway, and the exact
/// frames that gateway sent back, as hexadecimal. The compatibility policy (<c>docs/compatibility-policy.md</c>)
/// says a gateway at N must admit a client at N-1 and keep sending it something it can parse; the only way to
/// prove that without keeping an old build around is to keep its bytes. <c>protocol-18-handshake.json</c> in this
/// directory is the checked-in recording.
/// <para>
/// To regenerate it after a protocol bump, run the explicit test
/// <c>ConformanceProtocolCompatibilityTests.RecordTheHandshakeFixture</c> (it is
/// <see cref="NUnit.Framework.ExplicitAttribute"/>, so it never runs by accident) and commit the file it writes.
/// Record with the build that speaks the version being recorded: the point of the file is that it was written by
/// a different build from the one replaying it.
/// </para>
/// </summary>
public sealed class ProtocolFixture
{
    /// <summary>The protocol version the recorded client announced.</summary>
    [JsonPropertyName("protocolVersion")] public ushort ProtocolVersion { get; set; }
    /// <summary>The game content version it announced (0 = none).</summary>
    [JsonPropertyName("contentVersion")] public uint ContentVersion { get; set; }
    /// <summary>What the recording is, for whoever opens the file.</summary>
    [JsonPropertyName("note")] public string Note { get; set; } = "";
    [JsonPropertyName("frames")] public List<Frame> Frames { get; set; } = new();

    public sealed class Frame
    {
        /// <summary>"c2g" (client to gateway) or "g2c".</summary>
        [JsonPropertyName("dir")] public string Dir { get; set; } = "";
        [JsonPropertyName("hex")] public string Hex { get; set; } = "";
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>The frames the recorded client sent, in order: what a replay pushes at a gateway of this build.</summary>
    public List<byte[]> ClientFrames() => Frames.Where(f => f.Dir == "c2g").Select(f => FromHex(f.Hex)).ToList();

    /// <summary>The frames the recorded gateway sent, in order: what a client of this build has to still parse.</summary>
    public List<byte[]> GatewayFrames() => Frames.Where(f => f.Dir == "g2c").Select(f => FromHex(f.Hex)).ToList();

    public static ProtocolFixture Load(string path) =>
        JsonSerializer.Deserialize<ProtocolFixture>(File.ReadAllText(path), Json)
        ?? throw new InvalidOperationException("empty protocol fixture: " + path);

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Json));

    /// <summary>Turn a recording taken from a <see cref="FakeClient"/> into a fixture.</summary>
    public static ProtocolFixture FromRecording(ushort protocolVersion, uint contentVersion, string note,
        IEnumerable<(bool Outbound, byte[] Bytes)> recorded)
    {
        var fixture = new ProtocolFixture { ProtocolVersion = protocolVersion, ContentVersion = contentVersion, Note = note };
        foreach (var (outbound, bytes) in recorded)
            fixture.Frames.Add(new Frame { Dir = outbound ? "c2g" : "g2c", Hex = ToHex(bytes) });
        return fixture;
    }

    /// <summary>
    /// The checked-in recordings in the source tree, found by walking up from the test assembly to the project
    /// file. The source file, not the copy in <c>bin</c>: the recorder writes the one that gets committed, and
    /// the replay reads the one a reviewer can see.
    /// </summary>
    public static string Directory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Nebula.Services.Tests.csproj"))) dir = dir.Parent;
        if (dir == null) throw new DirectoryNotFoundException("could not find Nebula.Services.Tests.csproj from " + AppContext.BaseDirectory);
        return Path.Combine(dir.FullName, "Fixtures");
    }

    public static string PathFor(ushort protocolVersion) =>
        Path.Combine(Directory(), $"protocol-{protocolVersion}-handshake.json");

    private static string ToHex(byte[] bytes)
    {
        var sb = new System.Text.StringBuilder(bytes.Length * 2);
        foreach (byte b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    private static byte[] FromHex(string hex)
    {
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }
}
