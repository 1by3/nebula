using System;

namespace Nebula
{
    /// <summary>
    /// A game's own code running inside the <b>standalone</b> gateway (the <c>nebula-gateway</c> executable that
    /// <c>nebula start</c> and a deployed mesh run). That process is built from Nebula's sources and holds no
    /// game assemblies, so without an extension a game cannot install an <see cref="IInterestPolicy"/>, tag a
    /// client with its team, or feed a fog-of-war change to the thing that decides what each client is sent.
    /// An extension is one class library the gateway is <i>told</i> to load — never discovered — and it is the
    /// supported place for all of that.
    /// <para>
    /// Build it as a plain class library that references the gateway's <c>Nebula.Services.dll</c> (reference it,
    /// do not copy it: the gateway process already has it loaded, and a second copy would be a second set of
    /// types). Put the resulting dll next to the gateway executable — that folder is what <c>nebula build</c>
    /// produces, what the deploy tarball packs and what every VM of a fleet unpacks — and name it in
    /// <c>NebulaConfig.GatewayExtension</c> so every gateway started from that configuration loads the same one.
    /// </para>
    /// <para>
    /// <b>Threading.</b> <see cref="Initialize"/>, <see cref="Tick"/>, <see cref="Shutdown"/>, the context's
    /// client events and the interest policy all run on the gateway loop, which also carries every packet the
    /// gateway relays: a callback that blocks is latency every player on this gateway feels. Do slow work on
    /// your own thread and hand the result back with <see cref="IGatewayExtensionContext.Post"/>.
    /// </para>
    /// <para>
    /// <b>Failure.</b> A throw from <see cref="Initialize"/> stops the gateway starting, deliberately: a policy
    /// is a security filter, and a gateway that ran without the one it was configured with would be showing
    /// players things the game meant to hide. After start-up every call into an extension is isolated — the
    /// exception is logged and counted in <c>GatewayStats.ExtensionErrors</c> instead of taking the gateway
    /// down — and an exception out of the policy is treated as a refusal, never as permission.
    /// </para>
    /// </summary>
    public interface IGatewayExtension
    {
        /// <summary>
        /// Called once, after the gateway is initialized and before its first tick, so a policy installed here
        /// is in force before any client has been evaluated. Throwing fails the gateway's start-up.
        /// </summary>
        void Initialize(IGatewayExtensionContext context);

        /// <summary>
        /// Once per gateway loop (about 240 Hz: the loop runs several times per simulation tick so a relayed
        /// packet is not held). <paramref name="now"/> is seconds since the process started, from the same
        /// clock throughout. Must not block; must be cheap enough to run at that rate.
        /// </summary>
        void Tick(double now);

        /// <summary>Called once as the gateway shuts down. Stop your threads here.</summary>
        void Shutdown();
    }

    /// <summary>
    /// What an <see cref="IGatewayExtension"/> is given: the gateway's server-side surface, narrowed to what a
    /// game extension needs. Everything on it must be called from the gateway loop (from
    /// <see cref="IGatewayExtension.Initialize"/>, <see cref="IGatewayExtension.Tick"/> or a
    /// <see cref="ClientJoined"/>/<see cref="ClientLeft"/> handler) except <see cref="Post"/> and the logging
    /// methods, which are safe from any thread.
    /// </summary>
    public interface IGatewayExtensionContext
    {
        /// <summary>This gateway's mesh-wide id (<c>gw1</c>, <c>-nebula-gateway-id</c>). Every gateway of a fleet runs its own copy of the extension.</summary>
        string GatewayId { get; }

        /// <summary>The configuration this gateway was started with, as exported from the game's NebulaConfig and overridden on the command line. Read-only in practice: changing a field here changes nothing that has already been read.</summary>
        NebulaConfig Config { get; }

        /// <summary>
        /// One extension option: <c>-nebula-ext-&lt;key&gt;</c> on the command line if present, else the key in
        /// <c>NebulaConfig.GatewayExtensionOptions</c> (<c>key=value;key=value</c>, which travels in the
        /// exported manifest and so reaches every gateway of a fleet), else <paramref name="fallback"/>.
        /// </summary>
        string Option(string key, string fallback = null);

        /// <summary>
        /// Install the game's interest policy. Call it from <see cref="IGatewayExtension.Initialize"/>: before
        /// the first tick, no client has been evaluated yet, which is the only moment at which a security
        /// filter has seen everything it will be asked about. Installing one later is allowed — every client is
        /// marked dirty and re-evaluated, and anything the new policy refuses is taken away at once — but the
        /// clients already connected were told about the world under the old rules.
        /// <para>
        /// The policy is wrapped before it is installed: a throw from <c>Collect</c> is logged and the client
        /// evaluated with whatever foci it had added, and a throw from <c>Authorize</c> <b>denies</b> the
        /// entity. Passing null restores <see cref="DefaultInterestPolicy"/>.
        /// </para>
        /// </summary>
        void SetInterestPolicy(IInterestPolicy policy);

        /// <summary>
        /// An authenticated client has been welcomed and is about to be evaluated for the first time: the place
        /// to give it its team, its tags and its focus mode. Raised on the gateway loop.
        /// </summary>
        event Action<NebulaGateway.GatewayClientInfo> ClientJoined;

        /// <summary>
        /// A welcomed client is gone from this gateway (disconnected, rejected, or its session moved to another
        /// gateway, which raises its own <see cref="ClientJoined"/>). By the time it runs the client's id no
        /// longer resolves, so drop whatever you keyed on it.
        /// </summary>
        event Action<NebulaGateway.GatewayClientInfo> ClientLeft;

        /// <summary>The team byte a policy reads as <see cref="InterestClient.Team"/>. Server state: a client cannot set or see it. Changing it revokes what the new team may not see inside the call, and reveals the rest at the client's next evaluation.</summary>
        void SetClientTag(ulong clientId, byte team);
        /// <inheritdoc cref="SetClientTag"/>
        byte GetClientTag(ulong clientId);

        /// <summary>Sixty-four more server-owned bits beside <see cref="SetClientTag"/> (<see cref="InterestClient.Tags"/>): alliances, roles, fronts. Same timing as the tag: revoke now, reveal at the next evaluation.</summary>
        void SetClientTags(ulong clientId, ulong tags);
        /// <inheritdoc cref="SetClientTags"/>
        ulong GetClientTags(ulong clientId);

        /// <summary>How far this client's own focus hint may move its interest (<see cref="FocusMode"/>): a spectator or commander camera gets <see cref="FocusMode.Free"/> from here, never from the client.</summary>
        void SetClientFocusMode(ulong clientId, FocusMode mode);
        /// <inheritdoc cref="SetClientFocusMode"/>
        FocusMode GetClientFocusMode(ulong clientId);

        /// <summary>
        /// Put this client at the front of the evaluation queue: a <b>reveal</b> (a fog cell uncovered, a party
        /// joined). Dirty clients jump the rotation but are still bounded per tick, so this may take a few
        /// ticks on a busy gateway. For a change that <b>tightens</b> what may be seen use
        /// <see cref="RevalidateInterest"/>, which takes effect before this one would have run.
        /// </summary>
        void MarkInterestDirty(ulong clientId);

        /// <summary>The same for every client (a world-wide reveal). Spread over the following ticks, so it is cheap on a live gateway — and so it is not how a revocation is applied: see <see cref="RevalidateAllInterest"/>.</summary>
        void MarkAllInterestDirty();

        /// <summary>
        /// Re-authorize what this client can already see and despawn whatever the policy no longer allows,
        /// <b>before this call returns</b> and before any further traffic about those entities reaches it: a
        /// team change, a stealth roll, fog closing, an access rule revoked. Costs one
        /// <see cref="IInterestPolicy.Authorize"/> per replica the client holds and adds nothing — the client
        /// is also marked dirty, so the reveal half follows at its next evaluation.
        /// </summary>
        void RevalidateInterest(ulong clientId);

        /// <summary>
        /// <see cref="RevalidateInterest"/> for every client: a world-wide tightening (a new policy, a fog
        /// sweep that can close as well as open). Every unauthorized replica on this gateway is gone by the
        /// time it returns, at the cost of one authorization per replica currently served. Use it for those
        /// events, not per tick.
        /// </summary>
        void RevalidateAllInterest();

        /// <summary>
        /// Run <paramref name="work"/> on the gateway loop, before the next <see cref="IGatewayExtension.Tick"/>.
        /// Safe to call from any thread, and the only safe way to reach the rest of this interface from one:
        /// this is how fog of war computed on the game's own thread becomes a
        /// <see cref="SetClientTags"/> plus a <see cref="MarkInterestDirty"/> without a lock anywhere near the
        /// interest evaluation. A throw from the posted action is logged and counted, like any other extension
        /// callback. Work posted after the gateway has shut down is dropped.
        /// </summary>
        void Post(Action work);

        /// <summary>Write to the gateway's log, prefixed with the extension's name. Safe from any thread.</summary>
        void Log(string message);
        /// <inheritdoc cref="Log"/>
        void LogWarning(string message);
        /// <inheritdoc cref="Log"/>
        void LogError(string message);
    }
}
