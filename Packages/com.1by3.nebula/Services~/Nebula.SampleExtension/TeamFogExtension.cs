using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;

namespace Nebula.SampleExtension
{
    /// <summary>
    /// A worked example of <see cref="IGatewayExtension"/>, and the one the gateway-extension tests run: teams
    /// assigned as clients join, and a fog-of-war filter fed from outside the gateway.
    /// <para>
    /// It shows the three things a real game needs from the extension point.
    /// <list type="number">
    /// <item>A policy installed in <see cref="Initialize"/>, before any client has been evaluated, because a
    /// security filter that arrives after the first evaluation has already missed one.</item>
    /// <item>Server-owned per-client state — the team byte — set from <c>ClientJoined</c>, which runs before
    /// that client's first evaluation, and dropped again on <c>ClientLeft</c>.</item>
    /// <item>Fog of war computed <b>off</b> the gateway loop (here a watcher thread reading a file a game
    /// service writes; in a real game, whatever your fog authority is) and handed back with
    /// <c>context.Post</c>, which is the only safe way for another thread to touch gateway state.</item>
    /// </list>
    /// </para>
    /// <para>
    /// Options (<c>GatewayExtensionOptions</c> in the exported config, or <c>-nebula-ext-&lt;key&gt;</c>):
    /// <list type="bullet">
    /// <item><c>fog-file</c>: a file the game's fog service writes, one <c>&lt;team&gt;:&lt;netId&gt;,&lt;netId&gt;</c> line per team. Absent = nothing is ever revealed.</item>
    /// <item><c>events-file</c>: append a line per join and leave (an audit trail, and what the tests read).</item>
    /// <item><c>teams</c>: how many teams to deal players into (default 2).</item>
    /// <item><c>fail-authorize</c>: fault injection. Makes <see cref="Authorize"/> throw, to show what the gateway does with an extension that is broken: it counts the error, denies the entity, and keeps running.</item>
    /// </list>
    /// </para>
    /// </summary>
    public sealed class TeamFogExtension : IGatewayExtension, IInterestPolicy
    {
        private IGatewayExtensionContext _context;
        private Thread _watcher;
        private volatile bool _running;
        private string _fogFile, _eventsFile;
        private int _teams = 2;
        private bool _failAuthorize;
        private int _dealt;

        /// <summary>Client → team. Read by <see cref="Authorize"/> on the gateway loop and written only there.</summary>
        private readonly Dictionary<ulong, byte> _teamOf = new Dictionary<ulong, byte>();
        /// <summary>Team → the entities the fog service has revealed to it. Replaced wholesale on the gateway loop.</summary>
        private Dictionary<byte, HashSet<ulong>> _revealed = new Dictionary<byte, HashSet<ulong>>();

        public void Initialize(IGatewayExtensionContext context)
        {
            _context = context;
            _fogFile = context.Option("fog-file");
            _eventsFile = context.Option("events-file");
            _failAuthorize = string.Equals(context.Option("fail-authorize", "false"), "true", StringComparison.OrdinalIgnoreCase);
            if (int.TryParse(context.Option("teams", "2"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int teams) && teams > 0) _teams = teams;

            context.ClientJoined += OnClientJoined;
            context.ClientLeft += OnClientLeft;
            // Before the first tick: from here on every client this gateway evaluates is judged by this policy.
            context.SetInterestPolicy(this);

            if (!string.IsNullOrEmpty(_fogFile))
            {
                _running = true;
                _watcher = new Thread(WatchFog) { IsBackground = true, Name = "sample-fog" };
                _watcher.Start();
            }
            context.Log($"teams={_teams}, fog={( string.IsNullOrEmpty(_fogFile) ? "none" : _fogFile)}{(_failAuthorize ? ", fault injection on" : "")}");
        }

        /// <summary>Nothing to do per tick: this extension is event driven, and its slow work is on its own thread.</summary>
        public void Tick(double now) { }

        public void Shutdown()
        {
            _running = false;
            _watcher?.Join(500);
            _watcher = null;
        }

        // ------------------------------------------------------------------------------------------- clients

        /// <summary>
        /// Runs on the gateway loop, after the welcome and before this client's first interest evaluation, so
        /// the team set here is the team the very first thing the client is sent was chosen with. A real game
        /// would read the team from its own matchmaking by <c>client.Identity</c>; this deals players out by
        /// name so a test can predict the answer.
        /// </summary>
        private void OnClientJoined(NebulaGateway.GatewayClientInfo client)
        {
            byte team = TeamFor(client.Name);
            _teamOf[client.ClientId] = team;
            _context.SetClientTag(client.ClientId, team);
            // The 64-bit tags are the same idea with more room: here, one bit per team, which is what an
            // alliance or a shared-vision rule would grow into.
            _context.SetClientTags(client.ClientId, 1UL << (team % 64));
            Append(_eventsFile, $"join {client.ClientId} {client.Name} team={team}");
            _context.Log($"{client.Name} (#{client.ClientId}) joined team {team}");
        }

        private void OnClientLeft(NebulaGateway.GatewayClientInfo client)
        {
            _teamOf.Remove(client.ClientId);
            Append(_eventsFile, $"leave {client.ClientId} {client.Name}");
        }

        private byte TeamFor(string name)
        {
            name = name ?? "";
            if (name.StartsWith("red", StringComparison.OrdinalIgnoreCase)) return 1;
            if (name.StartsWith("blue", StringComparison.OrdinalIgnoreCase)) return 2;
            return (byte)(1 + _dealt++ % _teams);
        }

        // ------------------------------------------------------------------------------------------- policy

        /// <summary>Where the client is looking. The default answer — its pawn, plus the hint the gateway has already validated — is the right one for a shooter, so this defers to it.</summary>
        public void Collect(in InterestClient client, InterestQuery query) => DefaultInterestPolicy.Instance.Collect(client, query);

        /// <summary>
        /// The fog of war. An entity's <see cref="InterestEntity.InterestGroup"/> is this game's "whose unit is
        /// this": 0 for scenery everyone can see, otherwise the team that owns it. A unit of another team is
        /// sent only once the fog service has revealed it, which is a filter on the <i>wire</i> and not on the
        /// renderer: an enemy the client is not allowed to know about is never spawned on it at all.
        /// </summary>
        public bool Authorize(in InterestClient client, in InterestEntity entity)
        {
            if (_failAuthorize) throw new InvalidOperationException("fault injection: the sample extension's Authorize is configured to throw");
            if (entity.OwnerClientId == client.ClientId) return true;      // your own pawn and everything you own
            byte owner = entity.InterestGroup;
            if (owner == 0 || owner == client.Team) return true;            // scenery, and your own team's units
            return _revealed.TryGetValue(client.Team, out var revealed) && revealed.Contains(entity.NetId);
        }

        // ------------------------------------------------------------------------------------------- fog feed

        /// <summary>
        /// The game's fog authority, standing in for whatever yours is: it watches a file, parses it on <b>this</b>
        /// thread, and posts the finished result to the gateway loop. Nothing here touches gateway state, and the
        /// gateway loop never waits on this thread.
        /// </summary>
        private void WatchFog()
        {
            DateTime stamp = DateTime.MinValue;
            long length = -1;
            while (_running)
            {
                try
                {
                    var info = new FileInfo(_fogFile);
                    if (info.Exists && (info.LastWriteTimeUtc != stamp || info.Length != length))
                    {
                        stamp = info.LastWriteTimeUtc;
                        length = info.Length;
                        var parsed = Parse(File.ReadAllText(_fogFile));
                        _context.Post(() => Apply(parsed));
                    }
                }
                catch (IOException) { /* half-written by the game; the next pass picks it up */ }
                catch (Exception e) { _context.LogError("fog watcher: " + e.Message); }
                Thread.Sleep(25);
            }
        }

        /// <summary>
        /// Back on the gateway loop: swap the reveal sets in and apply them.
        /// <para>
        /// A fog file is replaced wholesale, so a new one can <i>close</i> fog as well as open it — the
        /// revealed set for a team may shrink, and the entities that dropped out of it are entities the
        /// clients on that team are still holding replicas of. That makes this a revocation, and revocations
        /// do not wait for a rotation: <c>RevalidateAllInterest</c> takes those replicas away inside this
        /// call, and marks every client dirty so the newly revealed ones arrive at the next evaluation.
        /// <c>MarkAllInterestDirty</c> would have been the right call for a reveal-only feed, and the wrong
        /// one here — it would leave players seeing through fog that had already closed for several ticks.
        /// </para>
        /// </summary>
        private void Apply(Dictionary<byte, HashSet<ulong>> revealed)
        {
            _revealed = revealed;
            _context.RevalidateAllInterest();
            _context.Log($"fog updated: {revealed.Count} team(s) with reveals");
        }

        private static Dictionary<byte, HashSet<ulong>> Parse(string text)
        {
            var result = new Dictionary<byte, HashSet<ulong>>();
            foreach (string line in text.Split('\n'))
            {
                string trimmed = line.Trim();
                int colon = trimmed.IndexOf(':');
                if (colon <= 0 || !byte.TryParse(trimmed.Substring(0, colon), NumberStyles.Integer, CultureInfo.InvariantCulture, out byte team)) continue;
                var ids = new HashSet<ulong>();
                foreach (string id in trimmed.Substring(colon + 1).Split(','))
                    if (ulong.TryParse(id.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong netId)) ids.Add(netId);
                result[team] = ids;
            }
            return result;
        }

        private static void Append(string file, string line)
        {
            if (string.IsNullOrEmpty(file)) return;
            try { File.AppendAllText(file, line + Environment.NewLine); } catch (IOException) { }
        }
    }
}
