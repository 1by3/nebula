# Protocol versions — the table

Which wire protocol each released package version speaks, and the rule for which of them may talk to each other.
The policy itself, and why it is shaped this way, is `docs/compatibility-policy.md`.

## The rule, in one paragraph

A **client** may talk to a **gateway** that accepts its protocol: the accepted range is
`HelloMsg.MinProtocolVersion`..`HelloMsg.ProtocolVersion`, currently 20..21; protocols 18 and 19 are not supported. Anything outside that is refused
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
| unreleased | 21 | replicated maps (NEB-335, `docs/replicated-collections.md` D12); **additive**, and the window moves to 20..21 |

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

## Bumping the protocol

1. Raise `HelloMsg.ProtocolVersion` to the new number and set `HelloMsg.MinProtocolVersion` to the previous one.
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
