# Transport encryption and peer authentication (NEB-226)

Status: landed with NEB-226. User-facing page: `website/content/docs/deploy/encryption.mdx`.
Code: `Packages/com.1by3.nebula/Runtime/Transport/EncryptedTransport.cs`, `NebulaCrypto.cs`,
`TransportCertificate.cs`. Tests: `Services~/Nebula.Services.Tests/TransportEncryptionTests.cs`.
Related: `docs/../website/content/docs/deploy/*` (the VPC requirement), the auth memo behind
`Runtime/Gateway/ClientAuth.cs` (who a player is, which this does not answer), and
`Services~/Nebula.Services/WebRtc/Dtls.cs` (the browser path, which was already encrypted).

## 0. Purpose

Before this change, exactly one of Nebula's links was encrypted: the WebRTC data channel a browser client uses,
because DTLS is not optional there. A native client's UDP traffic — inputs, snapshots, RPCs, the identity token
in `Hello` — went over the wire in the clear, and the only credential anywhere in the mesh was `MeshToken`, a
shared secret. On a public internet path that means a player's ID token can be read off the wire and replayed,
and anything a player sends can be rewritten in flight.

This adds an encrypted, server-authenticated channel between a **native client and a gateway**, configurable and
off by default, and writes down what it costs.

**Non-goals.** Account systems (that is `ClientAuth`), anti-cheat, encrypting the links inside the mesh
(see D1), certificate issuance for Nebula Cloud (a separate repository), and hiding a player's IP address.

## D1. Only the client-to-gateway link is encrypted

Worker-to-worker and gateway-to-worker links stay in the clear. They carry far more traffic than a client link,
they are the mesh's hot path, and they are not reachable from the internet in any supported deployment.

**This makes a private network a hard deployment requirement, not a recommendation.** Workers must sit in one
private network (a VPC, a VLAN, a WireGuard mesh), must talk only inside it, and must be reachable only by the
gateways of the same mesh. `MeshToken` remains the credential on those links (`MeshPeerAuth`), which proves a
peer belongs to the mesh but does not protect what it says. The deployment guide states this as a requirement:
`website/content/docs/deploy/encryption.mdx` §"Workers belong on a private network", linked from the deployment
index.

**D1a.** The gateway accepts clients and workers on the same UDP socket, so the encryption layer cannot be a
property of the socket. It is a property of a *peer*: a link that opens with a key exchange is encrypted, and
every other link is passed through untouched. That is what lets a worker keep dialling the same gateway port.

## D2. An AEAD of our own over the payload, not DTLS

Two options were on the table: (a) an application-layer AEAD over the LiteNetLib payload with a key exchange of
our own, or (b) reusing the managed DTLS stack the WebRTC path already has.

**(a) was chosen.** The DTLS in `Services~/Nebula.Services/WebRtc/Dtls.cs` is BouncyCastle's, and BouncyCastle
is a services-only dependency. A Unity client would need a DTLS *client* — either BouncyCastle shipped inside
the player (a large dependency for a game engine package, and a licensing and IL2CPP-stripping surface), or a
second implementation. Option (a) needs about 700 lines of primitives with public test vectors, and it lets the
handshake be one round trip with no fragmentation, no retransmission timer of its own (the handshake rides
LiteNetLib's reliable channel) and no cipher negotiation to get wrong.

The cost of (a) is the usual one: this is a hand-rolled protocol, and it is not the one the world's cryptographers
have spent twenty years attacking. §7 is honest about what that means.

**D2a. The primitives.** Unity's Mono profile has no `AesGcm`, no `ChaCha20Poly1305` and no X25519, so
`NebulaCrypto.cs` implements X25519 (RFC 7748) over `BigInteger` and ChaCha20-Poly1305 (RFC 8439) in 32-bit
managed arithmetic. SHA-256, HMAC-SHA256, RSA sign/verify and the CSPRNG come from the platform; `ClientAuth`
already depends on all four, so no new platform surface is introduced.

**D2b. The services use the platform AEAD.** On .NET, `System.Security.Cryptography.ChaCha20Poly1305` exists
and is hardware-accelerated, so the standalone gateway takes it and falls back to the managed code only where
the type is absent (`#if NEBULA_SERVICE` plus `IsSupported`). That is a 10x difference per packet on the side
that scales (§6). Both paths are checked against the same RFC 8439 vector by the same test.

## D3. Server authentication is a certificate, pinned by its public key

The client authenticates the **gateway**; the gateway does not authenticate the client at this layer — a player's
identity is a token inside `Hello` and is now carried encrypted, which was the point.

The gateway holds an RSA certificate and signs the handshake transcript with it. The client checks the
signature against the certificate the gateway presented, and checks the certificate against
`GatewayFingerprint`: the SHA-256 of its SubjectPublicKeyInfo, 64 hex characters, which the gateway prints on
startup. The fingerprint is of the **key**, not of the certificate, so re-issuing a certificate for the same key
does not invalidate what players shipped.

**D3a. Configuration surface (OSS).** `EncryptionCertPath` + `EncryptionKeyPath`, or `EncryptionCertPem` +
`EncryptionKeyPem` (also `NEBULA_ENCRYPTION_CERT` / `NEBULA_ENCRYPTION_KEY` for the standalone gateway). With
none of them set, the gateway generates a self-signed certificate on first run and keeps it in
`EncryptionSelfSignedPath` (`nebula-transport.pem` beside the gateway by default), so the fingerprint survives
restarts and the `nebula` CLI's local mesh needs no configuration at all. `TransportCertificate.cs` reads and
writes the PEM and DER itself, because Unity's profile has neither `CertificateRequest` nor the PKCS#8 import
helpers; it goes through `RSAParameters`, which Unity has had forever.

**D3b. No CA chain validation, deliberately.** An empty `GatewayFingerprint` encrypts the link but does not
authenticate the gateway, and the client says so in its log. Validating a CA chain and a hostname needs a
hostname to validate, and a Nebula client is usually handed an address by the control plane rather than a name
it chose; the mechanism the issue asked for — pinning — covers the self-signed case and a private-CA case
equally. Chain validation is a candidate for a later pass, not a gap this one left by accident.

## D4. The handshake sits below `Hello`

The key exchange is a transport-level frame, not a protocol message. Nothing in `Runtime/Protocol/Messages.cs`
changed except one new `JoinRejectReason`; the protocol version stays **18**. `Hello` and everything after it is
simply carried encrypted, which is also why the change does not collide with NEB-228's work on `HelloMsg`.

Frames are tagged with a first byte of `0xE0`–`0xE4`, above every `MsgId` (the highest is 49), so a gateway with
encryption off ignores a key exchange rather than mis-parsing it, and the client times out with a message that
says exactly that.

```
ClientHello  0xE0 | ver | X25519 client public (32) | client random (32)
ServerHello  0xE1 | ver | X25519 server public (32) | server random (32) | u16 cert DER | u16 signature
Finished     0xE2 | HMAC-SHA256(finished key, "client finished" || SHA-256(transcript))
Sequenced    0xE3 | seq u32 LE | ChaCha20-Poly1305(payload), AAD = the 5 header bytes
Reliable     0xE4 | seq u32 LE | ChaCha20-Poly1305(payload), AAD = the 5 header bytes
```

Keys come from `HKDF-SHA256(salt = client random || server random, ikm = X25519 shared secret,
info = "nebula-transport-v1")` split into a client→server key, a server→client key, a 4-byte nonce salt per
direction and the finished key. The nonce is `salt (4) || delivery domain (1) || 0 (3) || seq (4)`, with domain
0 for sequenced and 1 for reliable traffic. The sequence is per direction, delivery domain, and link, so a key
and nonce pair is never reused. A domain that reaches `2^32 - 16` packets drops the link
rather than wrapped.

**D4a. Replay.** Each delivery domain has its own highest sequence and 64-bit replay window. A reliable packet
waiting for retransmission cannot fall out of its window while sequenced snapshots arrive. Reliable packets
arrive in order within their own channel. A repeat inside a window, or anything below it, is dropped before
the AEAD runs. The delivery tag is authenticated with the header and selects a distinct nonce domain, so changing
it cannot move a packet between replay windows. The handshake wire version is 2; peers with a different
encryption wire version refuse the handshake. Gateway state batches reserve the 21-byte encryption envelope
within the minimum UDP packet budget.

**D4b. One round trip.** The client sends `ClientHello` when the LiteNetLib link comes up and holds the
`Connected` event back from the layers above until it has the `ServerHello`; it then sends `Finished` and its
`Hello` in the same tick. So the first game packet costs one extra round trip on connect and nothing after that.

## D5. "Require encryption" is a gateway flag with a typed refusal

`RequireEncryption` (`-nebula-require-encryption`) makes the gateway refuse a client whose link is plaintext,
with `JoinRejectedMsg { Code = JoinRejectReason.EncryptionRequired, Retry = false }` and a reason fit to show a
player. The check is three lines in `DispatchClient`, after the client record exists so the refusal can be sent,
and it asks the transport (`ISecureTransport`) rather than the message: a browser client over WebRTC is
encrypted by DTLS and passes the same check.

`EncryptClients` (default **on**) only decides whether the gateway will *answer* a key exchange; a gateway that
answers one still accepts plaintext clients until `RequireEncryption` is set. `ClientEncryption`
(`-nebula-encrypt`, default **off**) decides whether a client asks. Defaults are chosen so an existing project
sees no change and a local `nebula` mesh keeps working with zero configuration, and so turning encryption on is
a two-step rollout: ship clients that encrypt, then set `RequireEncryption`.

## D6. Measured on this machine

Windows 11, .NET 10, Release-equivalent JIT inside `dotnet test` (Debug build of the test assembly), single
thread, from `TransportEncryptionTests.OverheadIsMeasuredAndPrinted`:

| | Measurement |
|---|---|
| Per-packet overhead | **21 bytes** (1 tag + 4 sequence + 16 Poly1305 tag), whatever the payload |
| 34 B payload | 55 B frame (+61.8 %) |
| 130 B payload | 151 B frame (+16.2 %) |
| 514 B payload | 535 B frame (+4.1 %) |
| 1202 B payload | 1223 B frame (+1.7 %) |
| Handshake | **1102 bytes in 3 frames, 1 round trip** before the first game packet (the certificate is most of it: a 2048-bit RSA certificate and a 256-byte signature) |
| AEAD, platform (services) | seal **0.82 µs**, open **0.84 µs** per 200 B packet — 0.82 / 0.84 ms per 1000 packets |
| AEAD, managed (Unity build) | seal **8.15 µs**, open **8.28 µs** per 200 B packet — 8.15 / 8.28 ms per 1000 packets |
| X25519 scalar multiplication | **1.18 ms**, two per connection (one on each side) |
| RSA-2048 sign / verify | **0.37 ms** (gateway, once per connection) / **0.02 ms** (client) |

Read that as: a gateway relaying 20 000 client packets a second spends about **1.7 % of one core** on
encryption, and a connecting client costs it about 1.5 ms of key agreement and signing. A Unity client sending
and receiving 60 packets a second spends about **0.5 ms a second**, a twentieth of one 16.7 ms frame's budget
spread over a second. The bandwidth cost is the number to watch: 21 bytes on a 34-byte input is a real tax on
the smallest packets, and a mesh already batches its small reliable messages (`MsgId.Batch`), which now pays
for itself twice over.

## D7. What was verified, and what was not

Verified, by `dotnet test --filter "FullyQualifiedName~TransportEncryptionTests"` (9 tests, all passing):

- ChaCha20-Poly1305 against RFC 8439 §2.8.2, both the managed and the platform implementation, plus every
  single-bit change in ciphertext, tag and associated data being refused.
- X25519 against RFC 7748 §5.2 and §6.1, and an all-zero key share being rejected.
- The self-signed certificate: parsed back by the platform's own X.509 reader, signature verified and a wrong
  transcript refused, the PEM round trip preserving the fingerprint, and an existing store being reused so the
  fingerprint is stable across restarts.
- A real `NebulaGateway` with `RequireEncryption`: an encrypted client that pinned the printed fingerprint joins
  and its link reports as encrypted; a plaintext client is refused with `EncryptionRequired` and `Retry = false`.
- A gateway that offers but does not require encryption takes both kinds of client.
- A client that pinned another certificate refuses the gateway before sending `Hello`.
- A packet altered in flight is dropped and never reaches the game, a verbatim replay is delivered exactly once,
  and the link keeps working afterwards.

Also run: the full services suite,
`dotnet test --filter "TestCategory!=Soak&FullyQualifiedName!~AcmeTests"` — 475 passed, 0 failed; and the Unity
compile check `Tools/typecheck.ps1`, with every `Nebula.*` assembly clean.

**Not verified:**

- **Nothing ran in a Unity player.** The managed primitives compile for `netstandard2.1` and were measured on
  .NET; IL2CPP and WebGL have not executed them. The WebGL client does not use this path at all (it is DTLS),
  so the exposure is an IL2CPP desktop or console build.
- **No adversarial review, and no constant-time claim.** `BigInteger` arithmetic is not constant-time and the
  managed AEAD has not been analysed for timing. The mitigating argument is that every X25519 key pair is used
  for one connection and discarded, and the AEAD's secret-dependent work is table-free; that argument has not
  been tested, and a deployment that needs a reviewed stack should terminate players on a TLS load balancer
  instead.
- **No downgrade protection worth the name.** An attacker who can drop packets can stop a client from
  completing the handshake, and a client with `ClientEncryption` off is not protected by anything. That is what
  `RequireEncryption` is for: it makes the *gateway* refuse, and the gateway is the party whose configuration
  the operator controls.
- **No long-lived-link rekeying.** A link is dropped at `2^32 - 16` packets rather than rekeyed. At 60 packets
  a second that is over two years.
- **No measurement at fleet scale.** The CPU figures are microbenchmarks of one packet, not a gateway under a
  scale-suite run with encrypted clients.
