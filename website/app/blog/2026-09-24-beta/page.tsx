import type { Metadata } from 'next';
import Link from 'next/link';
import { ArrowLeft, ExternalLink } from 'lucide-react';

const title = 'Nebula 0.1.0-beta.0';
const description =
  'The first beta lets you play without a build, keep a behavior’s sync state private, and save changes to procedurally placed objects.';

export const metadata: Metadata = {
  title,
  description,
};

export default function BetaReleasePage() {
  return (
    <main className="mx-auto w-full max-w-3xl px-6 py-12 sm:py-16">
      <Link
        href="/blog"
        className="inline-flex items-center gap-1 text-sm font-medium text-fd-muted-foreground hover:text-fd-foreground"
      >
        <ArrowLeft className="size-4" /> Back to the blog
      </Link>

      <article className="mt-10">
        <header className="border-b border-fd-border pb-8">
          <p className="text-sm font-medium text-fd-primary">Release</p>
          <h1 className="mt-2 text-4xl font-bold tracking-tight sm:text-5xl">
            {title}
          </h1>
          <time
            dateTime="2026-09-24"
            className="mt-4 block text-sm text-fd-muted-foreground"
          >
            September 24, 2026
          </time>
          <p className="mt-6 text-xl leading-8 text-fd-muted-foreground">
            Nebula&apos;s first beta follows alpha.32. You can now test a
            multiplayer change by pressing Play, without building a player. A
            behavior can send its state only to the players who should see it,
            and a procedural world can remember which rocks were mined.
          </p>
        </header>

        <div className="space-y-12 py-10 text-base leading-7">
          <section>
            <h2 className="text-2xl font-semibold">Play without a build</h2>
            <p className="mt-4 text-fd-muted-foreground">
              Set <code>NebulaConfig.EditorRunMode</code> to{' '}
              <code>MultiplayerPlayMode</code> and enable one virtual player in
              Unity&apos;s Multiplayer Play Mode. When you press Play, the
              virtual player runs the server in its own Editor process: the
              orchestrator, the gateway, and one worker. The main Editor joins
              it as a client.
            </p>
            <p className="mt-4 text-fd-muted-foreground">
              The server keeps persistent entities in a save file in your
              project&apos;s <code>Library</code> folder from one Play to the
              next. <strong>Nebula &gt; Dev Loop &gt; Reset Dev Saves</strong>{' '}
              starts over. The dev loop uses its own ports, so it can run next
              to a mesh started with <code>nebula start</code>, and the client
              joins only the server its own session started.
            </p>
            <p className="mt-4 text-fd-muted-foreground">
              One worker cannot test handovers between workers. Use{' '}
              <code>nebula start</code> for those.
            </p>
            <GuideLink href="/docs/getting-started/running-locally#iterate-without-building-multiplayer-play-mode">
              Iterate without building
            </GuideLink>
          </section>

          <section>
            <h2 className="text-2xl font-semibold">Keep sync state private</h2>
            <p className="mt-4 text-fd-muted-foreground">
              A <code>NetworkBehaviour</code> can now choose which clients
              receive its sync state. Override <code>SyncAudience</code> with{' '}
              <code>Owner</code> for state that only its player should see,
              such as an inventory, <code>WorkersOnly</code> for state no
              client needs, or <code>Custom</code> for a rule of your own, such
              as teammates only. <code>Everyone</code> remains the default.
            </p>
            <p className="mt-4 text-fd-muted-foreground">
              The gateway filters every message it sends, including the state a
              late joiner starts from, so a client outside the audience never
              receives the bytes. <code>NetworkVariable</code>s are not
              filtered: every client that holds the entity still receives them.
            </p>
            <GuideLink href="/docs/guides/sync-audiences">Keep sync state private</GuideLink>
          </section>

          <section>
            <h2 className="text-2xl font-semibold">Save changes to procedural objects</h2>
            <p className="mt-4 text-fd-muted-foreground">
              A world that places trees, rocks, or resource nodes from a
              chunk&apos;s seed can now record what players changed without
              making each object an entity. <code>ChunkState</code> reads an
              object&apos;s entry on any process, and a worker writes through{' '}
              <code>NebulaWorker.ChunkStates</code>. <code>CompareAndSet</code>{' '}
              runs on the worker that holds the chunk, so two players cannot
              both take the last unit of a node.
            </p>
            <p className="mt-4 text-fd-muted-foreground">
              An entry can expire, which lets a node grow back. A chunk that no
              one changed saves and sends nothing.
            </p>
            <GuideLink href="/docs/guides/procedural-object-state">
              Save procedural object state
            </GuideLink>
          </section>

          <section>
            <h2 className="text-2xl font-semibold">Choose a worker&apos;s port with PORT</h2>
            <p className="mt-4 text-fd-muted-foreground">
              A worker now reads its listen port from <code>-nebula-port</code>,
              then the <code>PORT</code> environment variable, then{' '}
              <code>WorkerBasePort</code> plus its index. A host that runs one
              worker per machine can set the port without a command-line
              switch.
            </p>
            <GuideLink href="/docs/guides/configuration">Configure Nebula</GuideLink>
          </section>

          <section>
            <h2 className="text-2xl font-semibold">Fixes</h2>
            <ul className="mt-4 list-disc space-y-3 pl-6 text-fd-muted-foreground">
              <li>
                A pawn brought back with <code>Apply</code> kept an old epoch,
                so every save of it was refused for the rest of the session. It
                now continues its record&apos;s epoch, and the stores log a
                warning when they refuse a save.
              </li>
              <li>
                A gateway or worker whose port was taken threw an exception on
                every frame. It now logs one error that names the port and
                stops.
              </li>
              <li>
                In a single-process run, the orchestrator could erase the
                scopes its worker had just activated. It now resets the control
                plane before the worker starts.
              </li>
            </ul>
          </section>

          <section className="rounded-xl border border-fd-border bg-fd-card p-6">
            <h2 className="text-2xl font-semibold">Upgrade to the beta</h2>
            <p className="mt-3 text-fd-muted-foreground">
              0.1.0-beta.0 uses wire protocol 20. A gateway still admits
              clients of protocol 19, so players on an alpha.31 or alpha.32
              build can keep playing, but they are not told when they leave a
              sync audience. Gateways and workers of one mesh must run the same
              build. Nebula now reserves network prefab ids from{' '}
              <code>0xFF00</code> for its own prefabs, so move any of your
              prefabs that use them.
            </p>
            <a
              href="https://github.com/1by3/nebula/releases/tag/v0.1.0-beta.0"
              className="mt-5 inline-flex items-center gap-1 font-medium text-fd-primary"
            >
              0.1.0-beta.0 release <ExternalLink className="size-3.5" />
            </a>
          </section>
        </div>
      </article>
    </main>
  );
}

function GuideLink({ href, children }: { href: string; children: React.ReactNode }) {
  return (
    <Link
      href={href}
      className="mt-4 inline-flex font-medium text-fd-primary hover:underline"
    >
      {children}
    </Link>
  );
}
