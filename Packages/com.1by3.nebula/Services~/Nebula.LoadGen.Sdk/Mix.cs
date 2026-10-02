using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;

namespace Nebula.LoadGen;

/// <summary>One line of a mix: a behaviour name, its weight and its options.</summary>
public sealed class MixEntry
{
    public string Name = "idle";
    public double Weight = 1;
    public BehaviourOptions Options = new();
}

/// <summary>
/// A weighted spread of behaviours over a run's clients. <see cref="Assign"/> is exact (largest remainder, so 100
/// clients of <c>wander:70,travel:20,idle:10</c> are 70, 20 and 10) and seeded (the order is a seeded shuffle), so the
/// same seed gives the same client the same behaviour every run.
/// </summary>
public sealed class BehaviourMix
{
    public List<MixEntry> Entries { get; } = new();

    /// <summary>Parse <c>name[:weight],name[:weight],...</c>.</summary>
    public static BehaviourMix Parse(string text)
    {
        var mix = new BehaviourMix();
        foreach (string part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int colon = part.LastIndexOf(':');
            var entry = new MixEntry();
            if (colon < 0) entry.Name = part;
            else
            {
                entry.Name = part.Substring(0, colon).Trim();
                if (!double.TryParse(part.AsSpan(colon + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out entry.Weight) || entry.Weight < 0)
                    throw new ArgumentException($"bad weight in mix entry '{part}'");
            }
            if (entry.Name.Length == 0) throw new ArgumentException($"mix entry '{part}' has no name");
            mix.Entries.Add(entry);
        }
        if (mix.Entries.Count == 0) throw new ArgumentException("the mix is empty");
        if (mix.Entries.Sum(e => e.Weight) <= 0) throw new ArgumentException("the mix weights add up to zero");
        return mix;
    }

    /// <summary>The entry index for each of <paramref name="count"/> clients.</summary>
    public int[] Assign(int count, int seed)
    {
        double total = Entries.Sum(e => e.Weight);
        var counts = new int[Entries.Count];
        var remainders = new double[Entries.Count];
        int given = 0;
        for (int i = 0; i < Entries.Count; i++)
        {
            double exact = count * Entries[i].Weight / total;
            counts[i] = (int)Math.Floor(exact);
            remainders[i] = exact - counts[i];
            given += counts[i];
        }
        foreach (int i in Enumerable.Range(0, Entries.Count).OrderByDescending(i => remainders[i]).ThenBy(i => i).Take(count - given)) counts[i]++;
        var result = new int[count];
        int at = 0;
        for (int i = 0; i < counts.Length; i++) for (int k = 0; k < counts[i]; k++) result[at++] = i;
        var rng = new Random(seed ^ 0x5bd1e995);
        for (int i = result.Length - 1; i > 0; i--) { int j = rng.Next(i + 1); (result[i], result[j]) = (result[j], result[i]); }
        return result;
    }

    /// <summary>The seed of a client's own random source.</summary>
    public static int ClientSeed(int seed, int clientIndex) => unchecked(seed * 7919 + clientIndex * 104729 + 12345);
}

/// <summary>A run configuration read from a JSON file (<c>--profile</c>); every field is optional and the command line overrides it.</summary>
public sealed class LoadProfile
{
    public int? Seed;
    public double? MaxMinutes;
    public int? InputHz;
    public double? MoveSpeed;
    public List<string> Plugins = new();
    public BehaviourMix? Mix;

    /// <summary>
    /// <code>{ "seed": 7, "maxMinutes": 30, "inputHz": 30, "moveSpeed": 5, "plugins": ["MyGame.LoadGen.dll"],
    ///   "mix": [ { "behaviour": "wander", "weight": 70, "options": { "radius": 40 } }, { "behaviour": "idle", "weight": 30 } ] }</code>
    /// </summary>
    public static LoadProfile Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = doc.RootElement;
        var p = new LoadProfile();
        if (root.ValueKind != JsonValueKind.Object) throw new ArgumentException("a profile is a JSON object");
        foreach (var prop in root.EnumerateObject())
        {
            switch (prop.Name.ToLowerInvariant())
            {
                case "seed": p.Seed = prop.Value.GetInt32(); break;
                case "maxminutes": p.MaxMinutes = prop.Value.GetDouble(); break;
                case "inputhz": p.InputHz = prop.Value.GetInt32(); break;
                case "movespeed": p.MoveSpeed = prop.Value.GetDouble(); break;
                case "plugins": foreach (var e in prop.Value.EnumerateArray()) p.Plugins.Add(e.GetString() ?? ""); break;
                case "mix":
                    p.Mix = new BehaviourMix();
                    foreach (var e in prop.Value.EnumerateArray())
                    {
                        var entry = new MixEntry();
                        foreach (var f in e.EnumerateObject())
                        {
                            switch (f.Name.ToLowerInvariant())
                            {
                                case "behaviour": case "name": entry.Name = f.Value.GetString() ?? ""; break;
                                case "weight": entry.Weight = f.Value.GetDouble(); break;
                                case "options":
                                    foreach (var opt in f.Value.EnumerateObject())
                                        entry.Options.Set(opt.Name, opt.Value.ValueKind == JsonValueKind.String ? opt.Value.GetString() ?? "" : opt.Value.ValueKind == JsonValueKind.True ? "true" : opt.Value.ValueKind == JsonValueKind.False ? "false" : opt.Value.GetRawText());
                                    break;
                                default: throw new ArgumentException("unknown mix field '" + f.Name + "'");
                            }
                        }
                        if (entry.Name.Length == 0) throw new ArgumentException("a mix entry needs a behaviour name");
                        p.Mix.Entries.Add(entry);
                    }
                    if (p.Mix.Entries.Count == 0 || p.Mix.Entries.Sum(x => x.Weight) <= 0) throw new ArgumentException("the profile's mix is empty");
                    break;
                default: throw new ArgumentException("unknown profile field '" + prop.Name + "'");
            }
        }
        return p;
    }
}

/// <summary>The behaviours, input encoder and scope-travel handler a run has: the built-ins plus every plugin's.</summary>
public sealed class BehaviourRegistry : IBehaviourRegistry
{
    private readonly Dictionary<string, Func<BehaviourOptions, ILoadGenBehaviour>> _factories = new(StringComparer.OrdinalIgnoreCase);

    public IInputEncoder InputEncoder { get; private set; } = new DefaultInputEncoder();
    public ScopeTravelHandler? ScopeTravel { get; private set; }
    public IEnumerable<string> Names => _factories.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase);

    public BehaviourRegistry() => BuiltinBehaviours.Register(this);

    public void Add(string name, Func<BehaviourOptions, ILoadGenBehaviour> factory) => _factories[name] = factory;
    public void SetInputEncoder(IInputEncoder encoder) => InputEncoder = encoder;
    public void SetScopeTravel(ScopeTravelHandler handler) => ScopeTravel = handler;
    public bool Has(string name) => _factories.ContainsKey(name);

    public ILoadGenBehaviour Create(string name, BehaviourOptions options) =>
        _factories.TryGetValue(name, out var f) ? f(options) : throw new ArgumentException($"unknown behaviour '{name}' (known: {string.Join(", ", Names)})");

    /// <summary>Load a plugin assembly by path and register every <see cref="ILoadGenPlugin"/> in it.</summary>
    public int LoadPlugin(string path)
    {
        string full = System.IO.Path.GetFullPath(path);
        if (!File.Exists(full)) throw new FileNotFoundException("plugin assembly not found", full);
        var context = new PluginContext(full);
        var assembly = context.LoadFromAssemblyPath(full);
        return Register(assembly);
    }

    /// <summary>Register every <see cref="ILoadGenPlugin"/> in an already loaded assembly.</summary>
    public int Register(Assembly assembly)
    {
        int n = 0;
        Type[] types;
        try { types = assembly.GetTypes(); }
        catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray()!; }
        foreach (var t in types)
        {
            if (t.IsAbstract || t.IsInterface || !typeof(ILoadGenPlugin).IsAssignableFrom(t)) continue;
            ((ILoadGenPlugin)Activator.CreateInstance(t)!).Register(this);
            n++;
        }
        if (n == 0) throw new InvalidOperationException($"{assembly.GetName().Name} has no public class implementing {nameof(ILoadGenPlugin)}");
        return n;
    }

    /// <summary>
    /// Resolves the plugin's own dependencies from its folder, but shares this assembly (and everything the loadgen
    /// already has) with the host: two copies of <see cref="ILoadGenBehaviour"/> would be two different types.
    /// </summary>
    private sealed class PluginContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;
        public PluginContext(string path) : base("loadgen-plugin:" + System.IO.Path.GetFileName(path), false) => _resolver = new AssemblyDependencyResolver(path);

        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == typeof(ILoadGenPlugin).Assembly.GetName().Name) return typeof(ILoadGenPlugin).Assembly;
            foreach (var loaded in Default.Assemblies) if (loaded.GetName().Name == name.Name) return loaded;
            string? p = _resolver.ResolveAssemblyToPath(name);
            return p != null ? LoadFromAssemblyPath(p) : null;
        }
    }
}

/// <summary>
/// The input payload when no plugin supplies an encoder: 16 bytes for a game to decode or ignore. Byte 0 is the low
/// byte of the tick and byte 1 the client index (so nothing upstream can dedupe), byte 2 the flags (bit 0 sprint),
/// bytes 4-7 move x and 8-11 move z as little-endian float32, bytes 12-15 the action bits as uint32.
/// </summary>
public sealed class DefaultInputEncoder : IInputEncoder
{
    public int Encode(in InputSnapshot s, byte[] buffer)
    {
        Array.Clear(buffer, 0, 16);
        buffer[0] = (byte)(s.Tick & 0xff);
        buffer[1] = (byte)(s.ClientIndex & 0xff);
        buffer[2] = (byte)(s.Sprint ? 1 : 0);
        BitConverter.TryWriteBytes(new Span<byte>(buffer, 4, 4), s.Move.X);
        BitConverter.TryWriteBytes(new Span<byte>(buffer, 8, 4), s.Move.Y);
        BitConverter.TryWriteBytes(new Span<byte>(buffer, 12, 4), s.Actions);
        return 16;
    }
}

/// <summary>One behaviour's totals for one report interval, in a form a results collector reads.</summary>
public sealed class BehaviourReportRow
{
    public const string CsvHeader = "t,behaviour,clients,connected,joined,inputsPerSec,actionsPerSec,rpcsPerSec,travelsPerSec,actions,errors,reconnects,rejections,sessionChanges,sessionsLeft,rttP50,rttP95,replicasAvg,bytesInPerSec";

    public double T;
    public string Behaviour = "";
    public int Clients, Connected, Joined;
    public double InputsPerSec, ActionsPerSec, RpcsPerSec, TravelsPerSec;
    public long Actions, Errors;
    public int Reconnects, Rejections, SessionChanges, SessionsLeft;
    public double RttP50, RttP95, ReplicasAvg, BytesInPerSec;

    private static string N(double v, string fmt = "0.##") => v.ToString(fmt, CultureInfo.InvariantCulture);

    public string ToCsv() => string.Join(",", N(T, "0.0"), Escape(Behaviour), Clients, Connected, Joined, N(InputsPerSec), N(ActionsPerSec), N(RpcsPerSec), N(TravelsPerSec), Actions, Errors, Reconnects, Rejections, SessionChanges, SessionsLeft, N(RttP50, "0.0"), N(RttP95, "0.0"), N(ReplicasAvg, "0.0"), N(BytesInPerSec, "0"));

    /// <summary>One JSON object on one line; <c>t</c> is the seconds since the run started.</summary>
    public string ToJson()
    {
        var sb = new StringBuilder("{");
        void F(string k, string raw, bool last = false) { sb.Append('"').Append(k).Append("\":").Append(raw); if (!last) sb.Append(','); }
        F("t", N(T, "0.0"));
        F("behaviour", JsonSerializer.Serialize(Behaviour));
        F("clients", Clients.ToString(CultureInfo.InvariantCulture));
        F("connected", Connected.ToString(CultureInfo.InvariantCulture));
        F("joined", Joined.ToString(CultureInfo.InvariantCulture));
        F("inputsPerSec", N(InputsPerSec));
        F("actionsPerSec", N(ActionsPerSec));
        F("rpcsPerSec", N(RpcsPerSec));
        F("travelsPerSec", N(TravelsPerSec));
        F("actions", Actions.ToString(CultureInfo.InvariantCulture));
        F("errors", Errors.ToString(CultureInfo.InvariantCulture));
        F("reconnects", Reconnects.ToString(CultureInfo.InvariantCulture));
        F("rejections", Rejections.ToString(CultureInfo.InvariantCulture));
        F("sessionChanges", SessionChanges.ToString(CultureInfo.InvariantCulture));
        F("sessionsLeft", SessionsLeft.ToString(CultureInfo.InvariantCulture));
        F("rttP50", N(RttP50, "0.0"));
        F("rttP95", N(RttP95, "0.0"));
        F("replicasAvg", N(ReplicasAvg, "0.0"));
        F("bytesInPerSec", N(BytesInPerSec, "0"), true);
        return sb.Append('}').ToString();
    }

    private static string Escape(string s) => s.IndexOfAny(new[] { ',', '"' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
