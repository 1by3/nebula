using System.Globalization;
using System.Numerics;

namespace Nebula.LoadGen;

/// <summary>
/// What one synthetic client does while it is connected. One instance is created per client, so a behaviour can
/// keep its own state in fields. <see cref="Tick"/> runs on the loadgen's single loop thread: never block in it.
/// </summary>
public interface ILoadGenBehaviour
{
    /// <summary>The client has a session and a pawn (first join, and every rejoin after a drop or <see cref="IClientContext.Leave"/>).</summary>
    void OnJoined(IClientContext client) { }

    /// <summary>
    /// Called once per input interval (<c>--input-hz</c>) while the client is connected and welcomed, just before
    /// that interval's input is encoded, so a <see cref="IClientContext.SetMove"/> made here is in the input sent
    /// right after. <paramref name="dt"/> is the seconds since the previous call. Check
    /// <see cref="IClientContext.Joined"/> before acting if the behaviour needs a pawn.
    /// </summary>
    void Tick(IClientContext client, double dt);

    /// <summary>The client lost its link, was refused, or left. Reset any per-session state.</summary>
    void OnLeft(IClientContext client) { }
}

/// <summary>A replicated entity as this client last heard about it.</summary>
public sealed class ReplicaInfo
{
    public ulong NetId;
    public ushort PrefabId;
    public ulong OwnerClientId;
    /// <summary>Pose from the spawn message, in the entity's container space. Not updated by movement.</summary>
    public Vector3 SpawnPosition;
    /// <summary>The newest replicated variables the gateway sent (spawn, then each <c>EntityVars</c>); the game decodes them.</summary>
    public byte[] Vars = Array.Empty<byte>();
    /// <summary>How many times <see cref="Vars"/> changed. Lets a behaviour wait for the next update.</summary>
    public int VarsVersion;
}

/// <summary>Everything a behaviour can do with its client, and everything it can read about it.</summary>
public interface IClientContext
{
    /// <summary>Zero-based index of this client across the whole run (<c>--id-offset</c> included).</summary>
    int Index { get; }
    string Name { get; }
    /// <summary>The behaviour name this client was assigned from the mix.</summary>
    string BehaviourName { get; }
    /// <summary>Seconds since the run started.</summary>
    double Time { get; }
    /// <summary>This client's random source, seeded from <c>--seed</c> and <see cref="Index"/>: the same run repeats.</summary>
    Random Rng { get; }

    bool Connected { get; }
    /// <summary>The gateway reports the join complete (JoinState.Joined). A pawn may replicate a moment later: check PawnNetId if the behaviour needs one.</summary>
    bool Joined { get; }
    ulong SessionId { get; }
    /// <summary>The net id of this client's pawn, 0 until it spawns.</summary>
    ulong PawnNetId { get; }
    ReplicaInfo? Pawn { get; }
    IReadOnlyCollection<ulong> Replicas { get; }
    bool TryGetReplica(ulong netId, out ReplicaInfo replica);

    /// <summary>
    /// Where the pawn is believed to be: its spawn position plus the loadgen's dead reckoning of
    /// <see cref="SetMove"/> at <c>--move-speed</c>. The loadgen cannot decode a game's pose, so this drifts from
    /// the truth when the game moves the pawn differently. A behaviour that can read the real position from
    /// <see cref="ReplicaInfo.Vars"/> corrects it with <see cref="SetEstimatedPosition"/>.
    /// </summary>
    Vector3 EstimatedPosition { get; }
    void SetEstimatedPosition(Vector3 position);

    /// <summary>
    /// The movement to send in every input from now on: <paramref name="direction"/> (x is world x, y is world z;
    /// length is clamped to 1) and whether the player is sprinting. Stays set until changed.
    /// </summary>
    void SetMove(Vector2 direction, bool sprint = false);
    /// <summary>The action bits to send in every input from now on (the game decides what a bit means).</summary>
    void SetActions(uint bits);
    /// <summary>Send exactly these bytes as the input payload instead of the encoder's. Null goes back to the encoder.</summary>
    void SetRawInput(byte[]? payload);

    /// <summary>Send a server RPC to an entity (0 is this client's pawn). The epoch is the one the client last saw.</summary>
    void SendServerRpc(ulong netId, byte behaviourIndex, uint methodHash, ReadOnlySpan<byte> args);
    /// <summary>As above, naming the method; the hash is the same FNV-1a of the name that <c>[ServerRpc]</c> uses.</summary>
    void SendServerRpc(ulong netId, byte behaviourIndex, string method, ReadOnlySpan<byte> args);
    /// <summary>
    /// Ask the game to move this client to another scope (an instance, a grid, a planet). Nebula has no wire
    /// message for it: a game defines how, by registering a <see cref="ScopeTravelHandler"/> in its plugin.
    /// Returns false, and counts an error, when no handler is registered.
    /// </summary>
    bool RequestScopeTravel(string target);

    /// <summary>Send a focus hint (absolute world coordinates), as a camera that looks away from the pawn does.</summary>
    void SetFocusHint(double x, double y, double z);
    void ClearFocusHint();

    /// <summary>
    /// Leave the game for good (sends <c>Goodbye</c>, which ends the session at once) and join again as a new
    /// session after <paramref name="rejoinAfterSeconds"/>; negative never rejoins.
    /// </summary>
    void Leave(double rejoinAfterSeconds);

    /// <summary>Count one action of your own in the per-behaviour stats (RPCs, scope travels and inputs are counted for you).</summary>
    void CountAction();
    /// <summary>Count an error in the per-behaviour stats; <paramref name="message"/> is shown with <c>--verbose</c>.</summary>
    void ReportError(string message);
}

/// <summary>The input a client sends this interval, handed to the <see cref="IInputEncoder"/>.</summary>
public readonly struct InputSnapshot
{
    public uint Tick { get; init; }
    public int ClientIndex { get; init; }
    public Vector2 Move { get; init; }
    public bool Sprint { get; init; }
    public uint Actions { get; init; }
}

/// <summary>Turns an <see cref="InputSnapshot"/> into the bytes a game decodes as its input.</summary>
public interface IInputEncoder
{
    /// <summary>Write the payload to <paramref name="buffer"/> (at least 256 bytes) and return its length.</summary>
    int Encode(in InputSnapshot input, byte[] buffer);
}

/// <summary>Moves a client to another scope; see <see cref="IClientContext.RequestScopeTravel"/>. Return false if the request could not be made.</summary>
public delegate bool ScopeTravelHandler(IClientContext client, string target);

/// <summary>Where a plugin registers what it adds.</summary>
public interface IBehaviourRegistry
{
    /// <summary>Add a behaviour under <paramref name="name"/> (case-insensitive). The factory runs once per client.</summary>
    void Add(string name, Func<BehaviourOptions, ILoadGenBehaviour> factory);
    /// <summary>Replace the default input encoder with the game's own.</summary>
    void SetInputEncoder(IInputEncoder encoder);
    /// <summary>Say how a client asks the game for a scope change.</summary>
    void SetScopeTravel(ScopeTravelHandler handler);
}

/// <summary>The entry point of a plugin assembly: one public class with a parameterless constructor.</summary>
public interface ILoadGenPlugin
{
    void Register(IBehaviourRegistry registry);
}

/// <summary>The options of one behaviour entry, from <c>--opt name.key=value</c> or a profile.</summary>
public sealed class BehaviourOptions
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public BehaviourOptions() { }
    public BehaviourOptions(IEnumerable<KeyValuePair<string, string>> values) { foreach (var kv in values) _values[kv.Key] = kv.Value; }

    public IReadOnlyDictionary<string, string> Values => _values;
    public void Set(string key, string value) => _values[key] = value;
    public bool Has(string key) => _values.ContainsKey(key);
    public string GetString(string key, string fallback = "") => _values.TryGetValue(key, out var v) ? v : fallback;
    public double GetDouble(string key, double fallback) =>
        _values.TryGetValue(key, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;
    public int GetInt(string key, int fallback) => (int)Math.Round(GetDouble(key, fallback));
    public bool GetBool(string key, bool fallback) =>
        _values.TryGetValue(key, out var v) ? !(v.Equals("false", StringComparison.OrdinalIgnoreCase) || v == "0" || v.Equals("no", StringComparison.OrdinalIgnoreCase)) : fallback;
    /// <summary>A list written <c>a|b|c</c>.</summary>
    public string[] GetList(string key) => _values.TryGetValue(key, out var v) && v.Length > 0 ? v.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : Array.Empty<string>();
}
