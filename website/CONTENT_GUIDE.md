# Nebula content guide

Use this guide for every user-facing documentation change. It applies to the documentation pages, landing page, CLI help, generated API reference, dashboard copy, and published images.

Nebula's readers are game developers who need to install, evaluate, integrate, operate, or debug the software. Help them complete those tasks. Do not assume they know Nebula's architecture or internal vocabulary.

Follow the [Mailchimp voice and tone guide](https://styleguide.mailchimp.com/voice-and-tone/): be plainspoken, helpful, and clear. Prefer useful information over personality or promotion.

## Write for the reader's task

State what the reader can do, then tell them how to do it.

- Start a page with its purpose or the first action.
- Phrase procedures as instructions.
- Put prerequisites before the steps that require them.
- Explain the result of a command or choice when it is not obvious.
- Separate concepts, procedures, reference material, and troubleshooting when mixing them would make a page harder to scan.

Prefer:

> To start a local mesh, run `nebula start`.

Avoid:

> Starting your powerful Nebula mesh is only one simple command away—`nebula start`.

## Use plain English

- Use active voice and direct verbs.
- Address the reader as “you” when it makes an instruction clearer.
- Keep sentences focused on one idea.
- Keep paragraphs short. Split a paragraph when it changes topic or exceeds about four sentences.
- Use a list or table only when it makes repeated information easier to compare.
- Use American English in prose. Keep API identifiers exactly as written in code.
- Use humor sparingly. Never let it obscure instructions, errors, limitations, or security guidance.

Avoid marketing claims, filler, idioms, and internal shorthand. Words such as “powerful,” “seamless,” “effortless,” “magic,” “hot path,” “front door,” and “pre-warm” rarely help the reader. Describe the behavior instead.

## Define terms before using them

Define a Nebula-specific term the first time it appears on a page. Link to the fuller concept page when that would help.

Use these meanings consistently:

| Term | Meaning |
| --- | --- |
| **mesh** | The group of Nebula clients, workers, gateway, orchestrator, and control-plane service used for one running game world. |
| **container** | A box-shaped area of the game world assigned to a worker. |
| **worker** | A headless Unity process that simulates entities in its assigned containers. |
| **authority** | Responsibility for simulating an entity and producing its state. |
| **authoritative worker** | The worker that currently has authority over an entity. |
| **ghost** | A non-authoritative copy of an entity on another worker. |
| **handover** | The transfer of entity authority and state from one worker to another. |
| **gateway** | The process that accepts client connections and routes input and replicated state. |
| **orchestrator** | The process that maintains the worker count and assigns containers. |
| **control plane** | The document the orchestrator hosts that stores process registrations, container assignments, and shared settings. It does not carry per-tick entity state. |
| **lease** | A control-plane record that assigns a container to a worker. |
| **epoch** | A number that increases when authority or a container assignment changes. Nebula uses it to reject older messages. |
| **tick** | One fixed simulation step. Nebula runs at 60 ticks per second. |

Expand an acronym on first use, for example “round-trip time (RTT)” or “remote procedure call (RPC).” Do not use another product's acronym as the only explanation of a Nebula feature.

Use `behavior` in prose. Use `NetworkBehaviour` and other British-spelled identifiers exactly as the API defines them.

## Describe current behavior

Treat the current implementation and generated API reference as the source of truth. Before documenting a feature:

1. Check the relevant guide and generated reference page.
2. Check the current public API, configuration fields, and CLI help.
3. Check the implementation when behavior or limits remain unclear.
4. Update the source comment or CLI metadata when generated documentation is wrong.

Document what Nebula does now. Do not present a design idea, roadmap item, or old demo behavior as an available feature. Label a limitation directly instead of implying that the software handles it.

Important current boundaries include:

- An entity persists only if it carries a `PersistentEntity` component: Nebula checkpoints those entities and restores them when a worker gains the lease of their container. The transient state of every other entity is still lost when a worker fails, as is any state on a persistent entity that is neither a `[Persist]` NetworkVariable nor written by `WritePersistentState`.
- A client reconnects by itself with its session token, through any gateway of the mesh, and keeps its pawn when it returns within `SessionReclaimSeconds`. Session coordination can reclaim a failed gateway's sessions after its control-plane heartbeat becomes stale. Recovery time depends on the configured heartbeat timeout and on the reconnect reaching a live gateway; the single-digit-second measurements with default settings are not a deployment guarantee (see `docs/gateway-fleet-audit.md` and `docs/scale-suite.md`). Nebula does not provide a load balancer or redirect retries to a live gateway. The orchestrator starts only its own gateway and does not restart it.
- A native client's UDP link is encrypted only when both sides turn it on: the gateway answers a key exchange by default (`EncryptClients`), the client asks for one only when `ClientEncryption` is set, and the gateway refuses plaintext clients only when `RequireEncryption` is set. The client checks the gateway's certificate against a pinned fingerprint, or accepts any certificate when none is pinned; Nebula does not validate certificate chains or hostnames. WebRTC encrypts a web client's link with DTLS. Gateway-to-worker and worker-to-worker links are never encrypted, so those processes must run on a private network. The gateway identifies players by an anonymous or OpenID Connect token; gateways and workers authenticate each other only when a mesh token is set.
- The gateway checks instance visibility and the game's interest policy, then sends only entities in each client's interest set. Distance, explicit subscriptions, and always-relevant entities determine that set; distance also reduces transform update frequency within it. Private occupants can receive a bounded observation-only view of the public world.
- Bots and server-driven entities require game-supplied behavior.
- The CLI deploys to two targets: Nebula Cloud (`--target cloud`) and the reader's own Hetzner Cloud project (`--target hetzner`). Do not describe Nebula Cloud prices, the hosting provider behind it, or its internal services; document only what the CLI does.

Recheck these statements against the code before repeating them. Change this guide when the implementation changes.

Keep task-oriented guides focused on the current workflow and API. Include release history, version comparisons, or migration instructions when they help readers understand a change or take a required action. Changelogs and feature posts may describe earlier behavior. State compatibility requirements clearly, such as running every mesh process with the same wire protocol. Generated API reference remains complete, including members marked `[Obsolete]`.

## Keep examples public and self-contained

Use generic names that explain the role of an example, such as:

- `PlayerInput`
- `PlayerController`
- `ExampleGameMode`
- `Projectile`
- `interior#…`
- `my-game`

Nebula's internal test game uses licensed third-party art and code, and its repository is private. Its source and assets cannot be distributed, but screenshots, short videos, and performance numbers from it are fine to publish as examples of what Nebula can do. Images may show names such as ShooterGame, Corporation, Starhopper, player and NPC names, and dashboard data.

In prose, call it "Nebula's internal test game." Present it as a showcase of Nebula, not as something the reader can get. Do not tell readers to clone, open, build, or run it. Do not link to its repository. Do not show its code or class names as things the reader can use or copy, and do not describe its scripts as if they ship with Nebula. Code examples keep using the generic names above.

Acceptable caption:

> A ship with its own physics frame in Nebula's internal test game.

Unacceptable caption:

> Open the ShooterGame project and press Play.

Make code examples internally consistent:

- Define every non-obvious type, field, constant, and message ID used by the example, or link to where it is defined.
- Use the same names in prose and code.
- Show the required component, attribute, or registration step.
- Do not imply that sample bot, NPC, spawning, or game-mode behavior comes with Nebula.
- Use placeholders such as `<address>` only when the reader must replace them. Explain what value belongs there.

Never publish credentials, account details, tokens, internal hostnames or IP addresses, or other infrastructure details, in text or in images.

Audit screenshots and other images visually. A text search cannot find text rendered inside an image. Reject a capture that shows credentials, account details, or internal infrastructure, or anything that implies the reader can get the game, such as a repository URL, a file path, or setup instructions. A private project name, map label, or entity name in an image is not by itself a reason to reject it.

The three dashboard screenshots under `website/public/screenshots/` (`dashboard-overview.png`, `dashboard-world-map.png`, and `dashboard-world-map-container.png`) were deliberately taken off the landing page. Do not restore them during a content audit unless the maintainer asks for it.

## Make commands actionable

Introduce a command with the action it performs:

> To show the current workers and container assignments, run:
>
> ```bash
> nebula status
> ```

Use `bash` for cross-platform shell commands and `powershell` for PowerShell-specific commands. Put one command on each line unless the commands must form one pipeline.

Say where to run a command when location matters. Describe destructive effects before commands such as `nebula destroy` or options such as `--reset-control-plane`.

## Structure pages for scanning

- Give every MDX page a specific `title` and `description` in frontmatter.
- Use an imperative title for a task page, such as “Deploy to Hetzner Cloud.”
- Use descriptive headings that make sense outside the page's table of contents.
- Put the most common path first. Move edge cases and implementation detail later.
- Use callouts for security risks, data loss, irreversible operations, and easy-to-miss constraints—not ordinary tips.
- Use absolute documentation paths, such as `/docs/guides/rpcs`.
- Use meaningful link text. Prefer `[Prediction](/docs/guides/prediction)` over `[/docs/guides/prediction](...)` or “click here.”
- Escape `<`, `>`, `{`, and `}` in MDX prose or wrap the value in backticks.

Keep reference material complete but concise. A guide should explain how to choose and use an API. The generated reference should document its exact signatures, fields, and behavior.

## Update generated documentation at its source

Do not edit these directories by hand:

- `website/content/docs/cli/`
- `website/content/docs/reference/`

To change CLI documentation, edit command summaries, details, options, or examples under `cli/Nebula.Cli`, or update `website/scripts/gen-cli.mjs` when the shared page structure must change.

To change API documentation, edit public XML comments under `Packages/com.1by3.nebula`, or update `website/tools/ApiGen` when the shared reference format must change.

From `website/`, regenerate both references with:

```bash
npm run gen
```

Commit the generated output with its source changes.

## Review checklist

Before finishing a documentation change, check that:

- The page addresses a game developer's task or question.
- The first paragraph states the purpose or first action.
- Every Nebula-specific term and acronym is defined before use.
- Instructions use direct, active language.
- Paragraphs and sentences are short enough to scan.
- Examples use generic public names and define their dependencies.
- Claims match the current API and implementation.
- Limitations, security concerns, and destructive effects are explicit.
- No text implies that readers can access, build, or run the internal test game or its code.
- Code examples use generic names, not test-game class or script names.
- No text or image exposes credentials, account details, or internal infrastructure.
- Generated pages were changed at their source and regenerated.
- Internal links point to an existing route and heading.

## Verify the site

From `website/`, run:

```bash
npm run gen
npm run build
```

Search the repository for test-game leakage into code-facing text: ShooterGame class names, script paths, repository URLs, and wording that tells the reader to use the game. Include source comments and CLI metadata because they feed generated pages, and code-facing text there must stay generic. A mention of the game's name in an image caption or showcase prose is not a failure.

Inspect changed screenshots directly. Confirm that every added page has non-empty frontmatter and that internal `/docs/...` links resolve.

If a verification command fails for an unrelated repository issue, report the exact failure. Do not claim that the documentation passed that check.
