using System;
using System.Collections.Generic;
#if NEBULA_SERVICE
using Nebula.ServicePrimitives;
#else
using UnityEngine;
#endif

namespace Nebula
{
    sealed partial class NebulaGateway
    {
        private sealed class SessionRelease
        {
            public GatewaySessionRequest Request;
            public double At;
            public bool InFlight;
        }

        private readonly Dictionary<string, ClientConn> _sessionClients = new Dictionary<string, ClientConn>();
        private readonly HashSet<ClientConn> _pendingSessionClients = new HashSet<ClientConn>();
        private readonly List<ClientConn> _coordinationScratch = new List<ClientConn>();
        private readonly List<SessionRelease> _sessionReleases = new List<SessionRelease>();
        private double _nextSessionPoll;
        private bool _sessionPollInFlight;
        private IGatewaySessionControlPlane SessionCoordinator => ControlPlane as IGatewaySessionControlPlane;
        private string SessionGatewayKey => PlayerSessions.GatewayKey(GatewayId, Incarnation);

        private bool IsCurrentLink(ClientConn c) => _clientsByPeer.TryGetValue(c.PeerId, out var current) && current == c && c.DisconnectAt == 0;

        private GatewaySessionRequest SessionRequestFor(ClientConn c, string operation)
        {
            var request = new GatewaySessionRequest
            {
                Operation = operation,
                Identity = c.Identity,
                Gateway = SessionGatewayKey,
                Claim = c.CoordinationClaim,
                SessionId = c.ClientId,
                Generation = c.Generation,
                Worker = c.SpawnWorkerId,
                Container = c.SpawnContainer
            };
            if (c.PawnNetId != 0 && _entities.TryGetValue(c.PawnNetId, out var pawn) && _workersByIndex.TryGetValue(pawn.OwnerWorkerIndex, out var worker))
            {
                request.Worker = worker.WorkerId;
                request.Container = pawn.Container;
                request.ObservedPawn = true;
            }
            return request;
        }

        private void BeginCoordinatedWelcome(ClientConn c, in AuthResult auth, string issuedToken, string session)
        {
            if (SessionCoordinator == null)
            {
                Reject(c, "this control plane does not support coordinated player sessions");
                return;
            }
            c.Identity = auth.Identity ?? "";
            c.AuthPending = true;
            c.CoordinationClaim = Guid.NewGuid().ToString("N");
            c.CoordinationDeadline = _clock.Elapsed.TotalSeconds + 10;
            _sessionClients[c.CoordinationClaim] = c;
            _pendingSessionClients.Add(c);
            var request = SessionRequestFor(c, "claim");
            bool tokenReclaimed = false;
            if (!string.IsNullOrEmpty(session) && _sessions.Verify(session, out var claims, out _) && claims.Identity == c.Identity)
            {
                request.SessionId = claims.SessionId;
                request.Generation = claims.Generation;
                tokenReclaimed = true;
            }
            var authenticated = auth;
            c.RetryCoordination = () =>
            {
                if (c.CoordinationInFlight || !IsCurrentLink(c)) return;
                c.CoordinationInFlight = true;
                SessionCoordinator.SessionRequest(request, reply =>
                {
                    c.CoordinationInFlight = false;
                    if (!IsCurrentLink(c))
                    {
                        QueueSessionRelease(c, "cancel");
                        return;
                    }
                    if (reply.Status == "pending" && _clock.Elapsed.TotalSeconds < c.CoordinationDeadline)
                    {
                        c.NextCoordination = _clock.Elapsed.TotalSeconds + 0.25;
                        return;
                    }
                    c.RetryCoordination = null;
                    _pendingSessionClients.Remove(c);
                    c.AuthPending = false;
                    if (reply.Status != "granted" || reply.Session == null)
                    {
                        QueueSessionRelease(c, "cancel");
                        Reject(c, reply.Status == "pending" ? "the other session could not be disconnected; try again after it disconnects" :
                            string.IsNullOrEmpty(reply.Error) ? "player session could not be coordinated" : reply.Error);
                        return;
                    }
                    var granted = reply.Session;
                    _clientsById.Remove(c.ClientId);
                    c.Reclaimed = granted.SessionId != c.ClientId || tokenReclaimed;
                    c.ClientId = granted.SessionId;
                    c.Generation = granted.Generation;
                    c.SpawnWorkerId = granted.Worker;
                    c.SpawnContainer = granted.Container;
                    _clientsById[c.ClientId] = c;
                    foreach (var entity in _entities.Values)
                        if (entity.OwnerClientId == c.ClientId) { c.PawnNetId = entity.NetId; break; }
                    _recentlyLost.RemoveAll(kv => kv.Key == c.ClientId);
                    FinishWelcomeClient(c, authenticated, issuedToken, session);
                });
            };
            c.RetryCoordination();
        }

        private void ReserveCoordinatedSpawn(ClientConn c, WorkerConn proposed, ContainerRef container)
        {
            c.CoordinationInFlight = true;
            var request = SessionRequestFor(c, "reserve");
            request.Worker = proposed.WorkerId;
            request.Container = container;
            SessionCoordinator.SessionRequest(request, reply =>
            {
                c.CoordinationInFlight = false;
                if (!IsCurrentLink(c)) return;
                if (reply.Status != "granted" || reply.Session == null) return;
                c.SpawnWorkerId = reply.Session.Worker;
                c.SpawnContainer = reply.Session.Container;
                if (_workersById.TryGetValue(c.SpawnWorkerId, out var worker) && worker.Ready)
                {
                    SendClaim(c, worker, c.SpawnContainer);
                    return;
                }
                // The directory keeps a session's reserved worker while that worker is alive, and after a worker
                // is relaunched under the same id that can be a worker this gateway has no link to and no
                // interest reason to dial (its leases went elsewhere while it was down). Without this the claim
                // could never be sent and the client would retry into the same wall for the rest of its session.
                if (EnsureLink(c.SpawnWorkerId) != null)
                {
                    Reason(c.SpawnWorkerId, InterestLinkReason.Spawn);
                    _subscriptionsDirty = true;
                }
                c.NextSpawnAttempt = Time.unscaledTime + 0.5f;
            });
        }

        private void QueueSessionRelease(ClientConn c, string operation, double delay = 0)
        {
            if (c.CoordinationClaim.Length == 0) return;
            _sessionClients.Remove(c.CoordinationClaim);
            _pendingSessionClients.Remove(c);
            c.RetryCoordination = null;
            var request = SessionRequestFor(c, operation);
            foreach (var queued in _sessionReleases)
                if (queued.Request.Claim == request.Claim) return;
            _sessionReleases.Add(new SessionRelease { Request = request, At = _clock.Elapsed.TotalSeconds + delay });
        }

        private void ReleaseCoordinatedSession(ClientConn c) => QueueSessionRelease(c, c.Welcomed ? "release" : "cancel");

        private void TickSessionCoordination()
        {
            var coordinator = SessionCoordinator;
            if (coordinator == null) return;
            double now = _clock.Elapsed.TotalSeconds;
            for (int i = _sessionReleases.Count - 1; i >= 0; i--)
            {
                var release = _sessionReleases[i];
                if (release.InFlight || release.At > now) continue;
                release.InFlight = true;
                coordinator.SessionRequest(release.Request, reply =>
                {
                    release.InFlight = false;
                    if (reply.Status != "error") _sessionReleases.Remove(release);
                    else release.At = _clock.Elapsed.TotalSeconds + 1;
                });
            }
            // Retry only pending admission requests; ordinary connected clients make no per-player poll.
            if (_pendingSessionClients.Count > 0)
            {
                _coordinationScratch.Clear();
                foreach (var client in _pendingSessionClients)
                    if (client.NextCoordination <= now) _coordinationScratch.Add(client);
                foreach (var client in _coordinationScratch) client.RetryCoordination?.Invoke();
            }
            if (_sessionPollInFlight || now < _nextSessionPoll || _sessionClients.Count == 0) return;
            _nextSessionPoll = now + 0.25;
            _sessionPollInFlight = true;
            coordinator.SessionRequest(new GatewaySessionRequest { Operation = "poll", Gateway = SessionGatewayKey }, reply =>
            {
                _sessionPollInFlight = false;
                if (reply.Status != "ok") return; // coordinator failure never drops an established connection
                foreach (var revoke in reply.Revocations)
                {
                    if (_sessionClients.TryGetValue(revoke.Claim, out var previous))
                    {
                        // Remove the revoked link by identity, never by key. By the time a revocation is polled back
                        // the replacement connection may already hold the same session id (a takeover reuses it) or
                        // the same peer id (the transport recycles them), and evicting by key would disconnect the
                        // client we just admitted instead of the one the directory revoked.
                        if (_clientsByPeer.TryGetValue(previous.PeerId, out var byPeer) && byPeer == previous)
                            _clientsByPeer.Remove(previous.PeerId);
                        if (_clientsById.TryGetValue(previous.ClientId, out var byId) && byId == previous)
                            _clientsById.Remove(previous.ClientId);
                        ForgetClientInterest(previous);
                        EndPlayerLink(previous, ReplacedReason);
                        // CloseReplacedLinks runs first. Only then may the directory admit the replacement.
                        QueueSessionRelease(previous, "release", 0.6);
                    }
                    else
                    {
                        bool queued = false;
                        foreach (var release in _sessionReleases) if (release.Request.Claim == revoke.Claim) { queued = true; break; }
                        if (!queued)
                        {
                            revoke.Operation = "release";
                            _sessionReleases.Add(new SessionRelease { Request = revoke, At = _clock.Elapsed.TotalSeconds });
                        }
                    }
                }
            });
        }
    }
}
