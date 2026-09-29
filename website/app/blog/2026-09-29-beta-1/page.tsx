import type { Metadata } from 'next';
import Link from 'next/link';
import { ArrowLeft, ExternalLink } from 'lucide-react';

const title = 'Nebula 0.1.0-beta.1';
const description =
  'The second beta adds tools for large crowds of server-owned characters, replicated maps, a clearer client connection lifecycle, forwarded sign-in claims, and worker environment variables.';

export const metadata: Metadata = {
  title,
  description,
};

export default function BetaOneReleasePage() {
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
            dateTime="2026-09-29"
            className="mt-4 block text-sm text-fd-muted-foreground"
          >
            September 29, 2026
          </time>
          <p className="mt-6 text-xl leading-8 text-fd-muted-foreground">
            This release follows beta.0. A worker can now simulate a large crowd
            of server-owned characters, and a client is sent far less of it. A
            client can tell why its connection ended and what it is doing about
            it. A worker can read your game&apos;s settings from environment
            variables. Clients built for beta.0 must be rebuilt.
          </p>
        </header>

        <div className="space-y-12 py-10 text-base leading-7">
          <section>
            <h2 className="text-2xl font-semibold">Run crowds of server-owned characters</h2>
            <p className="mt-4 text-fd-muted-foreground">
              Every entity used to cost its worker about 4 microseconds each
              tick, whether or not it moved or anyone saw it, and a client
              received every nearby entity at a player&apos;s update rate.
              Three new controls change that:
            </p>
            <ul className="mt-4 list-disc space-y-3 pl-6 text-fd-muted-foreground">
              <li>
                <code>NetworkIdentity.UpdateInterval</code> makes the worker
                update an entity every N ticks instead of every tick.{' '}
                <code>RelevancePriority</code> (<code>Normal</code>,{' '}
                <code>High</code>, <code>Low</code>, <code>Background</code>)
                sets how quickly the gateway slows its updates to a distant
                client.
              </li>
              <li>
                <code>InterestHighTiers</code>, <code>InterestLowTiers</code>,{' '}
                and <code>InterestBackgroundTiers</code> in{' '}
                <code>NebulaConfig</code> give each priority its own distance
                bands, so slowing a crowd no longer slows players or ships.{' '}
                <code>SyncDistanceRating</code> makes a behavior&apos;s sync
                state follow the same bands.
              </li>
              <li>
                <code>NetworkIdentity.Sleep</code> puts an entity to sleep. A
                sleeping entity keeps its authority, state, and ghosts, but its{' '}
                <code>NetworkTick</code> is not called and it sends no updates.{' '}
                <code>SleepWhenUnobserved</code> sleeps it once no gateway has
                watched it for a number of seconds.
              </li>
            </ul>
            <p className="mt-4 text-fd-muted-foreground">
              In our measurements, 1,500 sleeping entities beside 150 active
              ones cost a worker 0.43 milliseconds a tick in the Editor. A
              client among 200 active and 1,000 background walkers was sent
              about 90 kilobits per second with the crowd settings, while a
              normal player 40 meters away kept 15 updates a second. Your
              figures will differ with your entity counts and settings.
            </p>
            <p className="mt-4 text-fd-muted-foreground">
              A server-owned character that walks onto a moving ship now
              crosses into the ship&apos;s physics frame and rides it, the same
              way a player does. The new <strong>Server-owned boarding</strong>{' '}
              sample shows the code.
            </p>
            <GuideLink href="/docs/guides/server-owned-entities">
              Run crowds of server-owned entities
            </GuideLink>
          </section>

          <section>
            <h2 className="text-2xl font-semibold">Replicate a map that sends only what changed</h2>
            <p className="mt-4 text-fd-muted-foreground">
              <code>NetworkMap&lt;TKey, TValue&gt;</code> is a keyed collection
              you declare on a <code>NetworkBehaviour</code>. The authoritative
              worker writes it, every copy reads it, and each tick Nebula sends
              only the keys that changed. A client that starts holding the
              entity later receives one full copy, then the same changes as
              everyone else. Add <code>[Persist]</code> to save it.
            </p>
            <p className="mt-4 text-fd-muted-foreground">
              Chunk state uses a map now, so mining one rock sends that entry
              instead of the chunk&apos;s whole table. Its API and saved format
              are unchanged.
            </p>
            <GuideLink href="/docs/guides/network-map">Replicate a keyed collection</GuideLink>
          </section>

          <section>
            <h2 className="text-2xl font-semibold">Handle connections that end</h2>
            <p className="mt-4 text-fd-muted-foreground">
              The client now reports why a connection ended and what happens
              next:
            </p>
            <ul className="mt-4 list-disc space-y-3 pl-6 text-fd-muted-foreground">
              <li>
                <code>NebulaClient.Disconnected</code> passes a{' '}
                <code>DisconnectInfo</code> with a typed{' '}
                <code>DisconnectReason</code>, a message, and{' '}
                <code>WillRetry</code>.
              </li>
              <li>
                <code>IsReconnecting</code>, <code>ReconnectAttempt</code>, and
                the <code>Reconnecting</code>, <code>Reconnected</code>, and{' '}
                <code>ReconnectGaveUp</code> events show a reconnection as it
                happens. <code>NebulaConfig</code> sets the retry schedule and
                an optional give-up time.
              </li>
              <li>
                <code>NebulaClient.Leave()</code> ends the session at once, so
                the pawn does not stay in the world for the reclaim time.{' '}
                <code>NebulaWorker.Kick</code> and{' '}
                <code>NebulaGateway.Kick</code> remove a player with a reason
                the client can read.
              </li>
              <li>
                A gateway that stops first tells its clients, so they no longer
                wait out the 8-second transport timeout.
              </li>
              <li>
                <code>LocalPlayerLost</code> fires when the client loses its
                pawn. <code>ServerStalled</code> and <code>ServerResumed</code>{' '}
                fire when no state arrives from a worker for{' '}
                <code>ClientStallSeconds</code>.
              </li>
              <li>
                On the server, <code>OnPlayerDisconnected</code> and{' '}
                <code>OnPlayerReconnected</code> run on the worker that owns
                the pawn, and <code>IsOwnerConnected</code> replicates whether
                the player is still there.
              </li>
            </ul>
            <GuideLink href="/docs/guides/connecting-clients#find-out-why-the-connection-ended">
              Find out why the connection ended
            </GuideLink>
          </section>

          <section>
            <h2 className="text-2xl font-semibold">Gate features on a sign-in token</h2>
            <p className="mt-4 text-fd-muted-foreground">
              Set <code>NebulaConfig.ForwardedClaims</code> to a list of claim
              names, and the gateway copies them from the verified OpenID
              Connect token to the worker. Read them from{' '}
              <code>PlayerInfo.Claims</code> or{' '}
              <code>NetworkIdentity.TryGetOwnerClaim</code>. Clients are never
              sent claims. <code>NameClaim</code> names a claim to use as the
              player&apos;s name, so a client can no longer choose its own.
            </p>
            <GuideLink href="/docs/guides/authentication#forward-token-claims-to-workers">
              Forward token claims to workers
            </GuideLink>
          </section>

          <section>
            <h2 className="text-2xl font-semibold">Pass settings to workers</h2>
            <p className="mt-4 text-fd-muted-foreground">
              <code>NebulaEnv.Get</code> reads a setting from the command line,
              the process environment, or a <code>.env.nebula</code> file at
              your project root. <code>nebula env</code> manages a
              deployment&apos;s variables on Nebula Cloud or your own Hetzner
              project. A worker reads its variables once, when it starts, so
              on Nebula Cloud run <code>nebula restart-workers</code> to apply
              a change now. It replaces the workers one at a time.
            </p>
            <GuideLink href="/docs/guides/environment-variables">
              Set environment variables
            </GuideLink>
          </section>

          <section>
            <h2 className="text-2xl font-semibold">More changes</h2>
            <ul className="mt-4 list-disc space-y-3 pl-6 text-fd-muted-foreground">
              <li>
                An entity can declare an extent, so a large building or ship is
                ghosted to a neighboring worker when its walls cross a seam,
                not only its root.
              </li>
              <li>
                <code>ScopeRetireWithEntities</code> lets a scope retire while
                it holds saved objects. Its persistent entities come back with
                it.
              </li>
              <li>
                <code>NebulaBuild.BuildLinuxBatch</code> builds the Linux
                player in batch mode, for a continuous integration job.
              </li>
              <li>
                Two checkouts of one project can each run the Multiplayer Play
                Mode loop, using a port offset in{' '}
                <code>UserSettings/NebulaEditorPortOffset.txt</code>.
              </li>
            </ul>
          </section>

          <section>
            <h2 className="text-2xl font-semibold">Fixes</h2>
            <ul className="mt-4 list-disc space-y-3 pl-6 text-fd-muted-foreground">
              <li>
                After a refusal that stops retries, such as a version mismatch,
                the client stayed <code>Connected</code> and never raised{' '}
                <code>ConnectionStateChanged</code>. It now moves to{' '}
                <code>Disconnected</code>.
              </li>
              <li>
                An <code>InstanceBoundary</code> inside a scoped grid, such as
                a planet, never let anyone in. It now stands in the scope of
                the container it is parented under.
              </li>
              <li>
                In the Multiplayer Play Mode loop, a client that joined far
                from the origin received no new spawns or variable changes. It
                now receives them.
              </li>
              <li>
                A client&apos;s reduced-rate tier could freeze an entity that
                updates every few ticks. A root <code>NetworkTransform</code>{' '}
                also sends its reliable recovery entry once it has settled,
                which removes about a tenth of a crowd&apos;s traffic.
              </li>
              <li>
                The worker&apos;s verbose launch log no longer prints the mesh
                token or the player signing key.
              </li>
            </ul>
          </section>

          <section className="rounded-xl border border-fd-border bg-fd-card p-6">
            <h2 className="text-2xl font-semibold">Upgrade to beta.1</h2>
            <p className="mt-3 text-fd-muted-foreground">
              0.1.0-beta.1 uses wire protocol 24. A gateway admits clients of
              protocols 23 and 24 and refuses older ones with{' '}
              <code>ProtocolUnsupported</code>, including clients built for
              beta.0. Rebuild your clients against this version. Gateways and
              workers of one mesh must still run the same build.
            </p>
            <Link
              href="/docs/deploy/upgrades"
              className="mt-4 inline-flex font-medium text-fd-primary hover:underline"
            >
              Replace gateways and workers
            </Link>
            <br />
            <a
              href="https://github.com/1by3/nebula/releases/tag/v0.1.0-beta.1"
              className="mt-4 inline-flex items-center gap-1 font-medium text-fd-primary"
            >
              0.1.0-beta.1 release <ExternalLink className="size-3.5" />
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
