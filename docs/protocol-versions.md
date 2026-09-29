# Protocol versions — the table

Which wire protocol each released package version speaks, and the rule for which of them may talk to each other.
The policy itself, and why it is shaped this way, is `docs/compatibility-policy.md`.

## The rule, in one paragraph

A **client** may talk to a **gateway** that accepts its protocol: the accepted range is
`HelloMsg.MinProtocolVersion`..`HelloMsg.ProtocolVersion`, currently 24..25; protocols 18 to 23 are not supported. Anything outside that is refused
with `JoinRejectReason.ProtocolUnsupported` and the gateway's range, so the client can tell "update the game"
from "this server has not been upgraded yet". A **gateway, worker and orchestrator of one mesh** must speak the
**same** protocol, exactly; a mismatch is refused with a logged reason. The game's own content version is a
separate number the client announces and the gateway compares (`NebulaConfig.GameContentVersion`); Nebula only
carries it.

## The table

Protocol numbers are read from `HelloMsg.ProtocolVersion` at each release tag (`git show
<tag>:Packages/com.1by3.nebula/Runtime/Protocol/Messages.cs`), which is how this table should be regenerated.

| Package version | Protocol | Note |
|---|---|---|
| v0.1.0-alpha.0 | 4 | |
| v0.1.0-alpha.1 – alpha.2 | 5 | |
| v0.1.0-alpha.3 | 6 | |
| v0.1.0-alpha.4 – alpha.12 | 7 | |
| v0.1.0-alpha.13 – alpha.15 | 8 | |
| v0.1.0-alpha.16 | 9 | |
| v0.1.0-alpha.17 – alpha.20 | 10 | |
| v0.1.0-alpha.21 | 11 | OIDC ID tokens and anonymous identities |
| v0.1.0-alpha.22 | 12 | |
| v0.1.0-alpha.23 – alpha.24 | 13 | |
| v0.1.0-alpha.25 | 14 | |
| v0.1.0-alpha.26 – alpha.28 | 16 | 15 was never released under a tag |
| v0.1.0-alpha.29 | 17 | interest management |
| v0.1.0-alpha.30 | 18 | scoped worlds and interaction contracts; **the floor the policy starts from** |
| v0.1.0-alpha.31 – alpha.32 | 19 | `DynamicContainer` folded into `Container` (NEB-264, `docs/container-tree.md` D21); **not additive**, so the minimum is 19 too |
| v0.1.0-beta.0 | 20 | sync audiences (NEB-321, `docs/sync-audience.md` D10); **additive**, so the minimum stays 19 |
| v0.1.0-beta.1 | 24 | relevance tiers (NEB-359, `docs/server-owned-entities.md` D5); **additive** for clients, so the window is 23..24. 21 (replicated maps, NEB-335), 22 (forwarded token claims, NEB-357) and 23 (deliberate session endings, recovering hold, owner-connected flag, NEB-354 to NEB-356; **closes the window to 23..23**, pre-1.0, see `docs/compatibility-policy.md` D1) were never released under a tag |
| unreleased | 25 | driven vehicles (NEB-361, `docs/driven-vehicles.md` §4); **additive** for clients, so the window is 24..25 |

Every step in that table was breaking, because until now there was no window to be additive inside: a gateway
required an exact match, and `HelloMsg.Write` could not even announce a version other than the one it was built
with. Nothing before 18 can be admitted by a gateway of this build, and the table records that rather than
implying compatibility that never existed.

19 is the first bump under the policy, and it is not additive either: removing the `DynamicContainer` behaviour
from every carrier prefab renumbered the carrier's other behaviours, and RPCs, variables and sync state are
addressed by that index. A protocol-18 client would talk to the wrong behaviour without noticing, so
`MinProtocolVersion` moved to 19 with `ProtocolVersion` (step 2 below, the refusal case). The protocol-18 recording
is kept, and `ConformanceProtocolCompatibilityTests.ARecordedProtocol18ClientIsRefusedWithTheGatewaysRange` replays
it to prove the refusal.

20 is the first additive bump, so the window is 19..20. What a client can see of it: audience bits in a sync
chunk's flags (a protocol-19 client reads only the `Full` bit), and a `Cleared` chunk, which is the first thing a
gateway writes differently by negotiated version: only a protocol-20 client is sent one
(`ConformanceSyncAudienceTests.AProtocol19ClientStopsReceivingButIsNotSentTheNotice`). Everything else is between
workers and gateways, which must match exactly: the new `SyncAudience` message (37) and trailing fields in the spawn
and sync bodies. The protocol-19 recording now exercises N-1; `protocol-20-handshake.json` was recorded for the next
bump.

21 is additive for clients: a new message (`EntityMaps`, 38) that a gateway sends only to clients that negotiated
21, and a trailing field in the spawn body that a protocol-20 client does not read. Between workers it adds
`GhostMaps` (50). The window moves up by one, so `MinProtocolVersion` is 20 and a protocol-19 client is now refused;
the replay test runs the protocol-20 recording against a frozen protocol-20 decoder
(`Fixtures/Protocol20GatewayDecoder.cs`), and `protocol-21-handshake.json` was recorded for the next bump. The
sync-audience test of a protocol-19 client left with the window.

22 changes nothing a client sends or reads. `SpawnPlayer` gains a trailing claims section (gateway to worker), and
the spawn body gains a trailing owner-claims section that only workers write to each other. That section is always
present in the spawn body nested in `AuthorityTransfer`, so the handover's fields after it moved: not additive
between workers, which must match exactly anyway. The window moves up by one, so `MinProtocolVersion` is 21 and a
protocol-20 client, a client of `v0.1.0-beta.0`, is now refused although nothing it reads changed; 21 was never
released under a tag, so the next release refuses the previous release's clients. The replay test runs the
protocol-21 recording against a frozen protocol-21 decoder (`Fixtures/Protocol21GatewayDecoder.cs`, the protocol-20
one with its literals raised: 21 changed nothing that decoder reads), and `protocol-22-handshake.json` was recorded
for the next bump. The network-map test of a protocol-20 client left with the window.

23 closes the window to itself: `MinProtocolVersion` is 23 and every older client is refused with
`ProtocolUnsupported`. Before 1.0, keeping older clients working is not worth the fallbacks it costs
(`docs/compatibility-policy.md` D1), so protocol 23 adds its messages and fields without version gates: `Kicked` (9),
`Goodbye` (24), `KickPlayer` (39), a `server_shutdown` field on `GatewayDraining`, an `end_now` field on
`DespawnPlayer`, `JoinHoldReason.Recovering` (6), `EntityFlags.OwnerDisconnected` (4) and `OrphanKind.Ended` in the
handover's session section. The gates earlier releases kept for older clients (the protocol-20 `Cleared` chunk and
the protocol-21 `EntityMaps` checks) went with them. The replay test replays `protocol-23-handshake.json` through
a frozen protocol-23 decoder (`Fixtures/Protocol23GatewayDecoder.cs`, the protocol-20 reader with its version
literals raised): it proves that a change inside protocol 23 still answers a client of 23 the same way, and it is
the recording the next additive bump replays for N-1. The protocol-20, -21 and -22 recordings and decoders were
removed.

24 is additive for clients: the spawn's `interest_flags` gain the entity's relevance priority in bits 1–2, which a
protocol-23 client never reads, and nothing else a client sends or reads changed. Between workers,
`AuthorityTransfer` gains a trailing update section (the entity's `UpdateInterval`, one byte, written only when it is
above 1, then a dormancy byte and the entity's `SleepWhenUnobserved` when either is set); an extent section
written only to reach it carries source byte `0xFF`, "no extent". The window is 23..24,
so the protocol-23 recording now replays as N-1 through the frozen protocol-23 decoder, and
`protocol-24-handshake.json` was recorded for the next bump.

25 is additive for clients: a new message a client sends only while it drives something (`DriveInput`, 25), and a
trailing `driver_client_id` in the spawn body, after `owner_claims`, that a protocol-24 client never reads. A sender that
writes the driver writes the earlier trailing sections too, empty, and the entity body nested in `AuthorityTransfer`
always carries it, so the handover's later fields moved between workers, which must match exactly. The window is 24..25:
`MinProtocolVersion` is 24 and a protocol-23 client is now refused. The protocol-24 recording replays as N-1 through a
frozen protocol-24 decoder (`Fixtures/Protocol24GatewayDecoder.cs`, the protocol-23 one with its literals raised: 24 only
added flag bits in a byte it skips), the protocol-23 recording and decoder were removed, and `protocol-25-handshake.json`
was recorded for the next bump.

## Bumping the protocol

1. Raise `HelloMsg.ProtocolVersion` to the new number and set `HelloMsg.MinProtocolVersion` to the previous one
   (before 1.0 you may set it to the new one instead, closing the window; see `docs/compatibility-policy.md` D1).
   `ConformanceProtocolCompatibilityTests.TheWindowIsOneVersionWideAtMost` fails if the window is ever wider than
   one version.
2. Keep every change inside the window additive: a new message id, or a trailing optional field read with
   `Remaining > 0` and written only when the session's negotiated version is new enough. A field that moves or
   disappears is not additive, and needs the older client refused instead — which means leaving
   `MinProtocolVersion` equal to `ProtocolVersion` for that release and saying so in the changelog.
3. Record a fresh replay fixture with
   `dotnet test --filter FullyQualifiedName~RecordTheHandshakeFixture` and commit
   `Services~/Nebula.Services.Tests/Fixtures/protocol-<N>-handshake.json`. Record it with the build that speaks
   the version being recorded: the point of the file is that the bytes come from a different build from the one
   replaying them.
4. Add a row to the table above, and the window to the release's changelog entry.
