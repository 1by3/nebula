import Link from 'next/link';
import { ArrowRight, Boxes, Layers3, Radar, TerminalSquare, Gauge, Globe } from 'lucide-react';
import { DynamicCodeBlock } from 'fumadocs-ui/components/dynamic-codeblock';
import { installers, tagline } from '@/lib/shared';

const sample = `public sealed class PlayerController : PredictedBehaviour<ShooterInput>
{
    public NetworkVariable<float> Health = new NetworkVariable<float>(100f);

    // Owning client: sample the keyboard once per tick.
    protected override ShooterInput GatherInput() { /* ... */ }

    // Runs on the worker, on the predicting client, and again during a replay.
    protected override void Simulate(uint tick, in ShooterInput input, float dt)
    {
        Move(input, dt);
        if (input.Fire && HasAuthority && TraceShot(out var victim))
        {
            // Runs on whichever worker owns the victim: local call, or the lateral link.
            victim.AuthorityRpc(victim.TakeDamage, Damage, NetId, PlayerName.Value);
        }
    }

    [AuthorityRpc] void TakeDamage(float amount, ulong attacker, string name) => Health.Value -= amount;
    [ClientRpc]    void RpcShotFired(Vector3 from, Vector3 to, bool hit) { /* laser bolt */ }
}`;

const pillars = [
  {
    icon: Layers3,
    title: 'Containers, not grids',
    body: 'Authority is assigned per designer-authored volume with its own local space: a room, a ship, a district. The container tree is static; which worker simulates each one is decided at runtime.',
  },
  {
    icon: Radar,
    title: 'Pre-warmed handover',
    body: 'Entities approaching a boundary are ghosted to the neighbouring worker ahead of time. The flip hands over the exact final state, the new epoch and the not-yet-simulated inputs, so the input stream never breaks.',
  },
  {
    icon: Boxes,
    title: 'The API you already know',
    body: 'NetworkBehaviour, NetworkVariable, ClientRpc, ServerRpc, NetworkTransform, NetworkAnimator, NetworkRigidbody. The meshing-specific additions fit on one line: HasAuthority, IsGhost, AuthorityRpc.',
  },
  {
    icon: Gauge,
    title: 'Disposable workers',
    body: 'Workers are stateless compute leased authority over containers. Kill one and its containers are reassigned within a second; scale from four workers to two mid-match and players see a handover, nothing more.',
  },
  {
    icon: TerminalSquare,
    title: 'One CLI, local to cloud',
    body: 'nebula init installs it into your Unity project, nebula start runs the whole mesh on your machine, nebula deploy puts the same build on real VMs with a dashboard you can watch.',
  },
  {
    icon: Globe,
    title: 'Streamed worlds',
    body: 'Partition a large world into cell scenes. Every cell is a container; workers load only the cells they own plus a ring around them, clients only what is near, and the origin shifts with the player.',
  },
];

export default function HomePage() {
  return (
    <main className="flex flex-col">
      {/* Hero */}
      <section className="relative overflow-hidden">
        <div className="nebula-grid absolute inset-0 -z-10" />
        <div className="mx-auto max-w-5xl px-6 pt-20 pb-16 text-center">
          <p className="mb-4 inline-block rounded-full border border-fd-border bg-fd-card px-3 py-1 text-xs font-medium text-fd-muted-foreground">
            Unity 6 · SpacetimeDB control plane · MIT licensed
          </p>
          <h1 className="text-4xl font-bold tracking-tight sm:text-6xl">{tagline}</h1>
          <p className="mx-auto mt-6 max-w-2xl text-lg text-fd-muted-foreground">
            One world, many dedicated servers. Nebula leases each container of your level to a worker, ghosts entities across the seams before they cross, and hands authority over mid-firefight without the player noticing. Written against a Mirror and NGO-shaped API, run from one command-line tool.
          </p>
          <div className="mt-8 flex flex-wrap items-center justify-center gap-3">
            <Link
              href="/docs/getting-started/install"
              className="inline-flex items-center gap-2 rounded-lg bg-fd-primary px-5 py-2.5 text-sm font-semibold text-fd-primary-foreground transition hover:opacity-90"
            >
              Get started <ArrowRight className="size-4" />
            </Link>
            <Link
              href="/docs/concepts/architecture"
              className="inline-flex items-center gap-2 rounded-lg border border-fd-border bg-fd-card px-5 py-2.5 text-sm font-semibold transition hover:bg-fd-accent"
            >
              Read the architecture
            </Link>
          </div>
          <div className="mx-auto mt-10 grid max-w-4xl gap-3 text-left md:grid-cols-2">
            <InstallBox label="Windows (PowerShell)" command={installers.windows} />
            <InstallBox label="macOS / Linux" command={installers.unix} />
          </div>
        </div>
      </section>

      {/* Topology */}
      <section className="mx-auto w-full max-w-5xl px-6 py-12">
        <div className="grid gap-8 md:grid-cols-2 md:items-center">
          <div>
            <h2 className="text-2xl font-semibold">Five roles, one build</h2>
            <p className="mt-3 text-fd-muted-foreground">
              Every box below is the same Unity player started with a different <code className="text-fd-foreground">-nebula-role</code>. Workers simulate the containers they lease and peer directly over UDP for ghosts and authority transfers. The gateway is the one address clients connect to. The orchestrator deals containers to workers through a SpacetimeDB control plane that is never on the per-tick path.
            </p>
            <ul className="mt-4 space-y-2 text-sm text-fd-muted-foreground">
              <li><b className="text-fd-foreground">Worker</b>: headless Unity, 60 Hz, ticks derived from the wall clock so nobody is the tick master.</li>
              <li><b className="text-fd-foreground">Gateway</b>: routes inputs to the current owner, drops stale epochs, caches keyframes for late joiners.</li>
              <li><b className="text-fd-foreground">Orchestrator</b>: keeps N workers alive, reassigns leases, serves the dashboard and its HTTP API.</li>
              <li><b className="text-fd-foreground">Control plane</b>: leases and heartbeats in SpacetimeDB; if it goes away the mesh keeps simulating.</li>
              <li><b className="text-fd-foreground">Client</b>: predicts, reconciles, and is never a party to the handover protocol.</li>
            </ul>
          </div>
          <Topology />
        </div>
      </section>

      {/* Pillars */}
      <section className="mx-auto w-full max-w-5xl px-6 py-12">
        <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
          {pillars.map(({ icon: Icon, title, body }) => (
            <div key={title} className="rounded-xl border border-fd-border bg-fd-card p-5">
              <Icon className="size-5 text-fd-primary" />
              <h3 className="mt-3 font-semibold">{title}</h3>
              <p className="mt-2 text-sm text-fd-muted-foreground">{body}</p>
            </div>
          ))}
        </div>
      </section>

      {/* Code */}
      <section className="mx-auto w-full max-w-5xl px-6 py-12">
        <div className="grid gap-8 lg:grid-cols-5 lg:items-start">
          <div className="lg:col-span-2">
            <h2 className="text-2xl font-semibold">Gameplay code that does not know it is meshed</h2>
            <p className="mt-3 text-fd-muted-foreground">
              A predicted controller is a struct of input and a pure <code className="text-fd-foreground">Simulate</code>. The worker runs it with the client&apos;s inputs; the client runs it ahead and replays after a correction. When the pawn walks into a container owned by another worker, the buffered inputs travel with it.
            </p>
            <p className="mt-3 text-fd-muted-foreground">
              The one meshing-specific line is the <code className="text-fd-foreground">AuthorityRpc</code>: it runs on whichever worker owns the victim, as a direct call when that is you and over the lateral link when you only hold a ghost.
            </p>
            <Link href="/docs/guides/prediction" className="mt-4 inline-flex items-center gap-1 text-sm font-medium text-fd-primary">
              Prediction guide <ArrowRight className="size-4" />
            </Link>
          </div>
          <div className="lg:col-span-3 text-sm">
            <DynamicCodeBlock lang="csharp" code={sample} />
          </div>
        </div>
      </section>

      {/* CTA */}
      <section className="mx-auto w-full max-w-5xl px-6 pb-20 pt-6">
        <div className="grid gap-4 sm:grid-cols-3">
          <Cta href="/docs/getting-started/tutorial" title="Tutorial" body="From an empty Unity project to a four-container mesh you can walk across." />
          <Cta href="/docs/cli" title="CLI reference" body="Every nebula command, generated from the CLI itself." />
          <Cta href="/docs/reference" title="API reference" body="Every public type in the runtime, generated from the C# sources." />
        </div>
      </section>
    </main>
  );
}

function InstallBox({ label, command }: { label: string; command: string }) {
  return (
    <div className="rounded-lg border border-fd-border bg-fd-card p-3">
      <div className="mb-1 text-xs text-fd-muted-foreground">{label}</div>
      <code className="block overflow-x-auto whitespace-nowrap text-xs sm:text-sm">{command}</code>
    </div>
  );
}

function Cta({ href, title, body }: { href: string; title: string; body: string }) {
  return (
    <Link href={href} className="group rounded-xl border border-fd-border bg-fd-card p-5 transition hover:border-fd-primary/60">
      <div className="flex items-center justify-between font-semibold">
        {title}
        <ArrowRight className="size-4 text-fd-muted-foreground transition group-hover:translate-x-0.5 group-hover:text-fd-primary" />
      </div>
      <p className="mt-2 text-sm text-fd-muted-foreground">{body}</p>
    </Link>
  );
}

/** The mesh topology: control plane on top, orchestrator/gateway/workers, one client. */
function Topology() {
  const box = 'fill-fd-card stroke-fd-border';
  const text = 'fill-fd-foreground text-[11px]';
  const muted = 'fill-fd-muted-foreground text-[10px]';
  const line = 'stroke-fd-muted-foreground';
  return (
    <svg viewBox="0 0 420 300" className="w-full rounded-xl border border-fd-border bg-fd-background p-2" aria-label="Nebula topology">
      {/* control plane */}
      <rect x="20" y="16" width="380" height="40" rx="8" className={box} />
      <text x="210" y="34" textAnchor="middle" className={text}>SpacetimeDB control plane</text>
      <text x="210" y="48" textAnchor="middle" className={muted}>workers · leases · gateways · settings</text>

      {/* orchestrator, gateway, workers */}
      {[
        { x: 20, label: 'Orchestrator', sub: 'dashboard :7080' },
        { x: 120, label: 'Gateway', sub: 'udp :7000' },
        { x: 220, label: 'Worker w1', sub: 'NE · udp :7101', hot: true },
        { x: 320, label: 'Worker w2', sub: 'SW · udp :7102', hot: true },
      ].map(({ x, label, sub, hot }) => (
        <g key={label}>
          <line x1={x + 40} y1="56" x2={x + 40} y2="110" className={line} strokeDasharray="3 3" />
          <rect x={x} y="110" width="80" height="48" rx="8" className={hot ? 'fill-fd-primary/10 stroke-fd-primary' : box} />
          <text x={x + 40} y="130" textAnchor="middle" className={text}>{label}</text>
          <text x={x + 40} y="146" textAnchor="middle" className={muted}>{sub}</text>
        </g>
      ))}
      {/* lateral link */}
      <line x1="300" y1="134" x2="320" y2="134" className="stroke-fd-primary" strokeWidth="2" />
      <text x="310" y="176" textAnchor="middle" className={muted}>lateral link: ghosts + authority transfers</text>
      {/* gateway to workers */}
      <path d="M200 134 H220" className={line} />
      <path d="M200 126 C 230 96, 300 96, 320 126" className={line} fill="none" />

      {/* containers */}
      <g transform="translate(20 200)">
        {[
          { x: 0, c: 'fill-fd-primary/30', l: 'NE · w1' },
          { x: 60, c: 'fill-fd-primary/30', l: 'NW · w1' },
          { x: 120, c: 'fill-fd-primary/70', l: 'SE · w2' },
          { x: 180, c: 'fill-fd-primary/70', l: 'SW · w2' },
        ].map(({ x, c, l }) => (
          <g key={l}>
            <rect x={x} y="0" width="56" height="56" rx="6" className={`${c} stroke-fd-border`} />
            <text x={x + 28} y="32" textAnchor="middle" className={text}>{l}</text>
          </g>
        ))}
        <text x="120" y="76" textAnchor="middle" className={muted}>containers, leased to workers; entities cross with a handover</text>
      </g>

      {/* client */}
      <rect x="300" y="210" width="100" height="44" rx="8" className={box} />
      <text x="350" y="228" textAnchor="middle" className={text}>Client</text>
      <text x="350" y="244" textAnchor="middle" className={muted}>inputs up, snapshots down</text>
      <path d="M350 210 V 170 H 160 V 158" className={line} fill="none" />
    </svg>
  );
}
