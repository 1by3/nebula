using System.Collections.Generic;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// Server-side game hooks, the equivalent of subclassing Mirror's NetworkManager. Put exactly one subclass in the
    /// game scene. Called on whichever worker the gateway picked for the player.
    /// </summary>
    public abstract class NebulaGameMode : MonoBehaviour
    {
        /// <summary>
        /// Create and spawn the player's entity inside <paramref name="container"/>. Return the spawned identity.
        /// Override this or the <see cref="PlayerInfo"/> overload (which also gets the player's identity across sessions).
        /// </summary>
        public virtual NetworkIdentity OnSpawnPlayer(NebulaWorker worker, ulong clientId, string playerName, Container container)
        {
            NebulaLog.Error($"{GetType().Name} overrides neither OnSpawnPlayer overload; client {clientId} gets no pawn");
            return null;
        }

        /// <summary>
        /// The same hook with everything the gateway knows about the player, including <see cref="PlayerInfo.Identity"/>,
        /// which is stable across sessions. Override this one to key saved characters or accounts on the identity.
        /// The default calls the four-argument overload.
        /// </summary>
        public virtual NetworkIdentity OnSpawnPlayer(NebulaWorker worker, in PlayerInfo player, Container container) => OnSpawnPlayer(worker, player.ClientId, player.Name, container);

        /// <summary>
        /// Begin creating a player, optionally waiting for game-owned storage or content loading.
        /// Call <paramref name="complete"/> on the Unity main thread with a factory that creates the pawn.
        /// Nebula invokes the factory only if this join is still current and connected, at most once.
        /// The default completes synchronously through <see cref="OnSpawnPlayer(NebulaWorker, in PlayerInfo, Container)"/>.
        /// </summary>
        public virtual void BeginSpawnPlayer(NebulaWorker worker, PlayerInfo player, Container container,
            System.Action<System.Func<NetworkIdentity>> complete)
        {
            complete(() => OnSpawnPlayer(worker, player, container));
        }

        /// <summary>The player's client went away; the worker despawns the entity after this returns.</summary>
        public virtual void OnPlayerDespawn(NebulaWorker worker, NetworkIdentity player) { }

        /// <summary>Called once the worker is listening and registered with the control plane.</summary>
        public virtual void OnWorkerStarted(NebulaWorker worker) { }

        /// <summary>
        /// Called for each authoritative entity before worker container membership queries.
        /// Procedural worlds may shift their origin here so distant regions resolve container boundaries precisely.
        /// The default does nothing. Keep this per-entity simulation callback inexpensive.
        /// </summary>
        public virtual void PrepareSpatialFrame(NetworkIdentity entity) { }
    }

    /// <summary>Who is joining, as the gateway told the worker (<see cref="NebulaGameMode.OnSpawnPlayer(NebulaWorker, in PlayerInfo, Container)"/>).</summary>
    public readonly struct PlayerInfo
    {
        /// <summary>
        /// The player's session id: unique across every gateway of the mesh, and kept across a reconnection with a
        /// session token (the player continues with the same pawn). A new session gets a new id.
        /// </summary>
        public readonly ulong ClientId;
        /// <summary>
        /// The player's display name: the verified token's <see cref="NebulaConfig.NameClaim"/> when the mesh sets one
        /// and the token has it, otherwise the name the client asked for.
        /// </summary>
        public readonly string Name;
        /// <summary>The player's identity across sessions (<see cref="PlayerIdentity"/>): from the OpenID token they presented, or the anonymous token the gateway issued them.</summary>
        public readonly string Identity;
        /// <summary>The client is a headless bot.</summary>
        public readonly bool IsBot;
        /// <summary>
        /// The claims of the player's verified sign-in token that the mesh forwards (<see cref="NebulaConfig.ForwardedClaims"/>),
        /// every value a string. Empty, never null, for anonymous players and when nothing is forwarded. The pawn
        /// carries the same claims as <see cref="NetworkIdentity.OwnerClaims"/>.
        /// </summary>
        public IReadOnlyDictionary<string, string> Claims => _claims ?? PlayerClaims.Empty;
        private readonly IReadOnlyDictionary<string, string> _claims;

        public PlayerInfo(ulong clientId, string name, string identity, bool isBot) : this(clientId, name, identity, isBot, null) { }

        /// <param name="clientId">The session id.</param>
        /// <param name="name">The display name.</param>
        /// <param name="identity">The identity across sessions.</param>
        /// <param name="isBot">Whether the client is a headless bot.</param>
        /// <param name="claims">Forwarded token claims; null for none.</param>
        public PlayerInfo(ulong clientId, string name, string identity, bool isBot, IReadOnlyDictionary<string, string> claims)
        {
            ClientId = clientId;
            Name = name ?? "";
            Identity = identity ?? "";
            IsBot = isBot;
            _claims = claims ?? PlayerClaims.Empty;
        }
    }
}
