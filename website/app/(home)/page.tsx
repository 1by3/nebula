import Image from "next/image";
import Link from "next/link";
import {
  ArrowRight,
  Boxes,
  Layers3,
  Radar,
  TerminalSquare,
  Gauge,
  Globe,
} from "lucide-react";
import { DynamicCodeBlock } from "fumadocs-ui/components/dynamic-codeblock";
import { installers, tagline } from "@/lib/shared";

const sample = `public sealed class PlayerController : PredictedBehaviour<PlayerInput>
{
    private const float Damage = 10f;
    public NetworkVariable<float> Health = new NetworkVariable<float>(100f);

    // Read local input once per tick on the owning client.
    protected override PlayerInput GatherInput() { /* ... */ }

    // Move the player on the worker and the predicting client.
    protected override void Simulate(uint tick, in PlayerInput input, float dt)
    {
        Move(input, dt);
        if (input.Fire && HasAuthority && TraceShot(out var victim))
        {
            // Run TakeDamage on the worker that controls the victim.
            victim.AuthorityRpc(victim.TakeDamage, Damage);
        }
    }

    [AuthorityRpc] void TakeDamage(float amount) => Health.Value -= amount;
}`;

const pillars = [
  {
    icon: Layers3,
    title: "Divide the world into containers",
    body: "Create box-shaped containers for rooms, ships, or sections of a level. Nebula assigns each container to a worker at runtime.",
  },
  {
    icon: Radar,
    title: "Transfer entities between workers",
    body: "Nebula creates a non-authoritative copy on the next worker before an entity crosses a boundary. It then transfers the current state and any pending player input.",
  },
  {
    icon: Boxes,
    title: "Build networked gameplay",
    body: "Use NetworkBehaviour, NetworkVariable, remote calls, NetworkTransform, NetworkAnimator, and NetworkRigidbody. Check HasAuthority and IsGhost when worker ownership matters.",
  },
  {
    icon: Gauge,
    title: "Add and remove workers",
    body: "Change the worker count while the game runs. The orchestrator moves containers away from a worker before it stops that worker.",
  },
  {
    icon: TerminalSquare,
    title: "Use one command-line tool",
    body: "Run nebula init to add the package, nebula start to run locally, and nebula deploy to deploy the build to configured virtual machines.",
  },
  {
    icon: Globe,
    title: "Stream partitioned worlds",
    body: "Split a large world into cell scenes. Workers and clients load the cells they need, and Nebula shifts the Unity origin as the player moves.",
  },
];

export default function HomePage() {
  return (
    <main className="flex flex-col">
      {/* Hero */}
      <section className="relative overflow-hidden">
        <div className="nebula-grid absolute inset-0 -z-10" />
        <div className="mx-auto max-w-5xl px-6 pt-20 pb-16 text-center">
          <h1 className="text-4xl font-bold tracking-tight sm:text-6xl">
            {tagline}
          </h1>
          <p className="mx-auto mt-6 max-w-2xl text-lg text-fd-muted-foreground">
            Divide a Unity world into containers and run those containers on
            multiple dedicated servers. Nebula routes clients through one
            gateway and transfers entities when they move between workers.
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
            <InstallBox
              label="Windows (PowerShell)"
              command={installers.windows}
            />
            <InstallBox label="macOS / Linux" command={installers.unix} />
          </div>
        </div>
      </section>

      {/* Dashboard */}
      <section className="mx-auto w-full max-w-6xl px-6 py-12">
        <div className="mx-auto max-w-3xl text-center">
          <h2 className="text-2xl font-semibold">Inspect a running mesh</h2>
          <p className="mt-3 text-fd-muted-foreground">
            Open the Nebula Dashboard to see which worker controls each
            container and entity. Use it to change the worker count, drain or
            stop a worker, edit shared settings, and assign a carried container
            such as a ship interior to its own worker.
          </p>
        </div>
        <Screenshot
          className="mt-8"
          src="/screenshots/dashboard-world-map.png"
          url="localhost:7080/map"
          alt="The Nebula Dashboard world map with containers colored by worker, entity markers, and a selected vehicle whose interior is assigned to another worker."
          caption="World map: inspect containers and entities by worker, including a carried container assigned to a separate worker."
        />
        <div className="mt-6 grid gap-6 md:grid-cols-2">
          <Screenshot
            src="/screenshots/dashboard-overview.png"
            url="localhost:7080"
            alt="The Nebula Dashboard overview with mesh totals, four worker cards, assigned containers, entity counts, tick times, shared settings, and container tables."
            caption="Overview: inspect workers, container assignments, entity counts, tick time, and heartbeat status."
          />
          <Screenshot
            src="/screenshots/dashboard-world-map-container.png"
            url="localhost:7080/map"
            alt="The world map with a container selected, showing its assigned worker, resident entities, and ghost copies on nearby workers."
            caption="Container details: inspect its assigned worker, resident entities, and non-authoritative copies on nearby workers."
          />
        </div>
        <div className="mt-6 text-center">
          <Link
            href="/docs/guides/orchestrator-and-dashboard#world-map"
            className="inline-flex items-center gap-1 text-sm font-medium text-fd-primary"
          >
            Orchestrator and dashboard guide <ArrowRight className="size-4" />
          </Link>
        </div>
      </section>

      {/* Topology */}
      <section className="mx-auto w-full max-w-5xl px-6 py-12">
        <div className="grid gap-8 md:grid-cols-2 md:items-center">
          <div>
            <h2 className="text-2xl font-semibold">
              Run each role from the same build
            </h2>
            <p className="mt-3 text-fd-muted-foreground">
              Start the Unity player with a different{" "}
              <code className="text-fd-foreground">-nebula-role</code> for each
              process. Workers exchange entity copies and transfers directly
              over UDP. Clients connect to the gateway. The orchestrator
              registers workers, assigns containers, and keeps the world in
              SQLite or PostgreSQL.
            </p>
            <ul className="mt-4 space-y-2 text-sm text-fd-muted-foreground">
              <li>
                <b className="text-fd-foreground">Worker</b>: simulates assigned
                containers in a headless Unity process at 60 ticks per second.
              </li>
              <li>
                <b className="text-fd-foreground">Gateway</b>: routes player
                input and sends worker snapshots to clients.
              </li>
              <li>
                <b className="text-fd-foreground">Orchestrator</b>: maintains
                the requested worker count, assigns containers, and serves the
                dashboard.
              </li>
              <li>
                <b className="text-fd-foreground">Control plane</b>: stores
                registrations, assignments, heartbeats, and shared settings,
                hosted by the orchestrator.
              </li>
              <li>
                <b className="text-fd-foreground">Client</b>: predicts the local
                player and corrects that prediction from worker snapshots.
              </li>
            </ul>
          </div>
          <Topology />
        </div>
      </section>

      {/* Pillars */}
      <section className="mx-auto w-full max-w-5xl px-6 py-12">
        <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
          {pillars.map(({ icon: Icon, title, body }) => (
            <div
              key={title}
              className="rounded-xl border border-fd-border bg-fd-card p-5"
            >
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
            <h2 className="text-2xl font-semibold">
              Write predicted gameplay code
            </h2>
            <p className="mt-3 text-fd-muted-foreground">
              Define a serializable input struct and implement{" "}
              <code className="text-fd-foreground">Simulate</code>. The worker
              runs the method with client input. The client runs it ahead of the
              worker and repeats it after a correction. Nebula transfers pending
              input when the player moves to another worker.
            </p>
            <p className="mt-3 text-fd-muted-foreground">
              Use <code className="text-fd-foreground">AuthorityRpc</code> to
              run a method on the worker that controls the target entity. Nebula
              sends the call to another worker when the local process only has a
              ghost of the target.
            </p>
            <Link
              href="/docs/guides/prediction"
              className="mt-4 inline-flex items-center gap-1 text-sm font-medium text-fd-primary"
            >
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
          <Cta
            href="/docs/getting-started/tutorial"
            title="Follow the tutorial"
            body="Add Nebula to a Unity project and run a four-container mesh."
          />
          <Cta
            href="/docs/cli"
            title="Use the CLI"
            body="Find the syntax and options for every nebula command."
          />
          <Cta
            href="/docs/reference"
            title="Use the C# API"
            body="Find signatures and descriptions for Nebula's public types."
          />
        </div>
      </section>
    </main>
  );
}

function InstallBox({ label, command }: { label: string; command: string }) {
  return (
    <div className="rounded-lg border border-fd-border bg-fd-card p-3">
      <div className="mb-1 text-xs text-fd-muted-foreground">{label}</div>
      <code className="block overflow-x-auto whitespace-nowrap text-xs sm:text-sm">
        {command}
      </code>
    </div>
  );
}

/** A dashboard capture in a minimal browser frame. */
function Screenshot({
  src,
  url,
  alt,
  caption,
  className,
}: {
  src: string;
  url: string;
  alt: string;
  caption: string;
  className?: string;
}) {
  return (
    <figure
      className={`overflow-hidden rounded-xl border border-fd-border bg-fd-card shadow-lg ${className ?? ""}`}
    >
      <div className="flex items-center gap-1.5 border-b border-fd-border px-3 py-2">
        <span className="size-2.5 rounded-full bg-fd-muted-foreground/30" />
        <span className="size-2.5 rounded-full bg-fd-muted-foreground/30" />
        <span className="size-2.5 rounded-full bg-fd-muted-foreground/30" />
        <span className="ml-2 truncate font-mono text-xs text-fd-muted-foreground">
          {url}
        </span>
      </div>
      <a href={src} target="_blank" rel="noreferrer" className="block">
        <Image
          src={src}
          alt={alt}
          width={1920}
          height={1080}
          sizes="(min-width: 1152px) 1104px, 100vw"
          className="block h-auto w-full"
        />
      </a>
      <figcaption className="border-t border-fd-border px-4 py-3 text-sm text-fd-muted-foreground">
        {caption}
      </figcaption>
    </figure>
  );
}

function Cta({
  href,
  title,
  body,
}: {
  href: string;
  title: string;
  body: string;
}) {
  return (
    <Link
      href={href}
      className="group rounded-xl border border-fd-border bg-fd-card p-5 transition hover:border-fd-primary/60"
    >
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
  const box = "fill-fd-card stroke-fd-border";
  const text = "fill-fd-foreground text-[11px]";
  const muted = "fill-fd-muted-foreground text-[10px]";
  const line = "stroke-fd-muted-foreground";
  return (
    <svg
      viewBox="0 0 420 300"
      className="w-full rounded-xl border border-fd-border bg-fd-background p-2"
      aria-label="Nebula topology"
    >
      {/* control plane */}
      <rect x="20" y="16" width="380" height="40" rx="8" className={box} />
      <text x="210" y="34" textAnchor="middle" className={text}>
        Control plane (hosted by the orchestrator)
      </text>
      <text x="210" y="48" textAnchor="middle" className={muted}>
        workers · container assignments · gateways · settings
      </text>

      {/* orchestrator, gateway, workers */}
      {[
        { x: 20, label: "Orchestrator", sub: "dashboard :7080" },
        { x: 120, label: "Gateway", sub: "udp :7000" },
        { x: 220, label: "Worker w1", sub: "NE · udp :7101", hot: true },
        { x: 320, label: "Worker w2", sub: "SW · udp :7102", hot: true },
      ].map(({ x, label, sub, hot }) => (
        <g key={label}>
          <line
            x1={x + 40}
            y1="56"
            x2={x + 40}
            y2="110"
            className={line}
            strokeDasharray="3 3"
          />
          <rect
            x={x}
            y="110"
            width="80"
            height="48"
            rx="8"
            className={hot ? "fill-fd-primary/10 stroke-fd-primary" : box}
          />
          <text x={x + 40} y="130" textAnchor="middle" className={text}>
            {label}
          </text>
          <text x={x + 40} y="146" textAnchor="middle" className={muted}>
            {sub}
          </text>
        </g>
      ))}
      {/* direct worker connection */}
      <line
        x1="300"
        y1="134"
        x2="320"
        y2="134"
        className="stroke-fd-primary"
        strokeWidth="2"
      />
      <text x="310" y="176" textAnchor="middle" className={muted}>
        worker connection: ghost copies + handovers
      </text>
      {/* gateway to workers */}
      <path d="M200 134 H220" className={line} />
      <path
        d="M200 126 C 230 96, 300 96, 320 126"
        className={line}
        fill="none"
      />

      {/* containers */}
      <g transform="translate(20 200)">
        {[
          { x: 0, c: "fill-fd-primary/30", l: "NE · w1" },
          { x: 60, c: "fill-fd-primary/30", l: "NW · w1" },
          { x: 120, c: "fill-fd-primary/70", l: "SE · w2" },
          { x: 180, c: "fill-fd-primary/70", l: "SW · w2" },
        ].map(({ x, c, l }) => (
          <g key={l}>
            <rect
              x={x}
              y="0"
              width="56"
              height="56"
              rx="6"
              className={`${c} stroke-fd-border`}
            />
            <text x={x + 28} y="32" textAnchor="middle" className={text}>
              {l}
            </text>
          </g>
        ))}
        <text x="120" y="76" textAnchor="middle" className={muted}>
          containers assigned to workers; entities cross with a handover
        </text>
      </g>

      {/* client */}
      <rect x="300" y="210" width="100" height="44" rx="8" className={box} />
      <text x="350" y="228" textAnchor="middle" className={text}>
        Client
      </text>
      <text x="350" y="244" textAnchor="middle" className={muted}>
        sends input, receives state
      </text>
      <path d="M350 210 V 170 H 160 V 158" className={line} fill="none" />
    </svg>
  );
}
