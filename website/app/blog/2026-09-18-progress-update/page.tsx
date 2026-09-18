import type { Metadata } from 'next';
import Link from 'next/link';
import { ArrowLeft, ExternalLink } from 'lucide-react';

const title = 'Nebula progress update: September 18, 2026';
const description =
  'Five alpha releases add Nebula Cloud deploys, multiple gateways, one connection per player, a 3D world map, and confirmed saves.';

export const metadata: Metadata = {
  title,
  description,
};

const releases = [
  ['0.1.0-alpha.23', 'Multiple gateways, reconnection, and Nebula Cloud deploys'],
  ['0.1.0-alpha.24', 'A dashboard that matches Nebula Cloud'],
  ['0.1.0-alpha.25', 'A 3D world map in the dashboard'],
  ['0.1.0-alpha.26', 'One connection per player across gateways'],
  ['0.1.0-alpha.27', 'Confirmed saves and procedural world tools'],
] as const;

export default function ProgressUpdatePage() {
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
          <p className="text-sm font-medium text-fd-primary">Progress update</p>
          <h1 className="mt-2 text-4xl font-bold tracking-tight sm:text-5xl">
            {title}
          </h1>
          <time
            dateTime="2026-09-18"
            className="mt-4 block text-sm text-fd-muted-foreground"
          >
            September 18, 2026
          </time>
          <p className="mt-6 text-xl leading-8 text-fd-muted-foreground">
            This week&apos;s work lets developers deploy to Nebula Cloud from
            the command line, serve one world through several gateways, keep
            each player to one connection, inspect the world in 3D, and confirm
            that a save has reached storage.
          </p>
        </header>

        <div className="space-y-12 py-10 text-base leading-7">
          <section>
            <h2 className="text-2xl font-semibold">Deploy to Nebula Cloud</h2>
            <p className="mt-4 text-fd-muted-foreground">
              Nebula Cloud is the hosted service for Nebula. Run{' '}
              <code>nebula deploy --target cloud</code> to upload a build and
              roll it out without a cloud provider account or SSH access. Each
              upload becomes a release that you can roll back to later.
            </p>
            <p className="mt-4 text-fd-muted-foreground">
              New commands cover the rest of a deployment&apos;s life:{' '}
              <code>nebula cloud login</code>, <code>nebula deployments</code>,{' '}
              <code>nebula status --cloud</code>,{' '}
              <code>nebula logs --cloud --follow</code>,{' '}
              <code>nebula scale</code>, <code>nebula rollback</code>, and{' '}
              <code>nebula destroy</code>. If a deploy is interrupted after its
              upload, the next deploy reuses the uploaded build instead of
              sending it again. The local dashboard now also shares Nebula
              Cloud&apos;s visual style.
            </p>
            <GuideLink href="/docs/deploy/cloud">Deploy to Nebula Cloud</GuideLink>
          </section>

          <section>
            <h2 className="text-2xl font-semibold">Serve one world through several gateways</h2>
            <p className="mt-4 text-fd-muted-foreground">
              A gateway is the process that accepts player connections and
              routes game traffic. One world can now be served by any number of
              gateways behind a load balancer. Session IDs are unique across
              every gateway, and each player receives a signed session token.
              A client that loses its link reconnects on its own through any
              gateway. It keeps its character if it returns within the reclaim
              period, which is 30 seconds by default.
            </p>
            <p className="mt-4 text-fd-muted-foreground">
              Each gateway reports its load, including clients, traffic, CPU,
              and memory, on the dashboard. You can drain a gateway before
              maintenance. Its players reconnect through another gateway and
              keep their sessions. Nebula does not provide the load balancer
              for your own servers; Nebula Cloud deployments include one.
            </p>
            <p className="mt-4 text-fd-muted-foreground">
              To test gateways under load, run <code>nebula-loadgen</code>. It
              connects synthetic clients, keeps them sending input, and reports
              lost sessions, reconnects, and round-trip time (RTT). The
              orchestrator also collects recent log lines from every process at{' '}
              <code>/api/logs</code>, so you can read a failed worker&apos;s
              last output after its machine is gone.
            </p>
            <GuideLink href="/docs/guides/orchestrator-and-dashboard#run-more-than-one-gateway">
              Run more than one gateway
            </GuideLink>
            <br />
            <GuideLink href="/docs/guides/load-testing">Load test a gateway fleet</GuideLink>
          </section>

          <section>
            <h2 className="text-2xl font-semibold">Keep one connection per player</h2>
            <p className="mt-4 text-fd-muted-foreground">
              Before this change, a player who connected twice, for example
              from a restarted client or a second device, received a second
              character. Both characters then wrote to the same saved record.
            </p>
            <p className="mt-4 text-fd-muted-foreground">
              Gateways now admit one connection per player identity across all
              gateways. The new connection takes over the existing session and
              character. The earlier client receives a message that it can show
              to the player. If the previous gateway cannot confirm that it
              released the player, the new join is refused. Games that want one
              identity to control several characters, such as a set of test
              bots, can turn the rule off with{' '}
              <code>NebulaConfig.SingleSessionPerPlayer</code>.
            </p>
            <GuideLink href="/docs/guides/authentication#one-connection-per-player">
              Read about one connection per player
            </GuideLink>
          </section>

          <section>
            <h2 className="text-2xl font-semibold">Inspect the world in 3D</h2>
            <p className="mt-4 text-fd-muted-foreground">
              The dashboard&apos;s world map now draws containers, world cells,
              and entities in 3D. A container is a box-shaped area of the world
              assigned to a worker, so its height now reads as clearly as its
              footprint. You can orbit, pan, and zoom, and press{' '}
              <strong>F</strong> to zoom to a selected entity and follow it.
            </p>
            <p className="mt-4 text-fd-muted-foreground">
              The map stays responsive in large worlds. Items away from the
              cursor fade as you zoom in, crowds show a limited number of
              markers, and overlapping labels are hidden. In testing, drawing
              10,000 entities dropped from 253 ms to 30 ms per frame.
            </p>
            <GuideLink href="/docs/guides/orchestrator-and-dashboard#inspect-the-world-map">
              Inspect the world map
            </GuideLink>
          </section>

          <section>
            <h2 className="text-2xl font-semibold">Build large procedural worlds</h2>
            <p className="mt-4 text-fd-muted-foreground">
              New opt-in tools help games that create containers at run time
              instead of placing them in a scene. <code>RuntimeGrid</code>{' '}
              divides space into cells and gives each cell a stable ID and
              exact bounds. <code>RuntimeGridAllocator</code> requests the
              containers in a ring around a point, such as a player.{' '}
              <code>RuntimeGrid.ShiftOriginTo</code> moves the floating origin
              without disturbing character controllers. The floating origin is
              the point that keeps coordinates near zero in a very large world.
            </p>
            <p className="mt-4 text-fd-muted-foreground">
              Two more APIs help with saved data.{' '}
              <code>BeginSpawnPlayer</code> lets a game finish loading a
              player&apos;s data before their character enters the world.{' '}
              <code>NebulaWorker.RequestEntity</code> finds the worker that
              currently simulates a saved entity and sends it a request for
              that entity.
            </p>
            <GuideLink href="/docs/guides/spawning#wait-for-player-data-before-spawning">
              Wait for player data before spawning
            </GuideLink>
            <br />
            <GuideLink href="/docs/reference/world/runtime-grid">
              Read the RuntimeGrid reference
            </GuideLink>
          </section>

          <section>
            <h2 className="text-2xl font-semibold">Confirm that a save reached storage</h2>
            <p className="mt-4 text-fd-muted-foreground">
              <code>IPersistenceStore.WhenWritten</code> calls back once every
              earlier save and delete has reached storage. Use it before an
              action that depends on those saves, such as telling a player that
              a trade is complete.
            </p>
            <p className="mt-4 text-fd-muted-foreground">
              Previously, the orchestrator confirmed a worker&apos;s save as
              soon as the save was in memory, so a crash in the next second
              could lose a confirmed record. It now confirms a save only after
              writing it to the file or database. The local store groups nearby
              requests into one background write, so the extra safety does not
              stall the game.
            </p>
            <GuideLink href="/docs/reference/persistence/i-persistence-store#whenwritten">
              Read the WhenWritten reference
            </GuideLink>
          </section>

          <section>
            <h2 className="text-2xl font-semibold">Fix physics and deployment issues</h2>
            <ul className="mt-4 list-disc space-y-2 pl-6 text-fd-muted-foreground">
              <li>
                A <code>NetworkRigidbody</code> that gains authority now starts
                from its current position instead of snapping back to an older
                one.
              </li>
              <li>
                A position sent by a client-controlled entity just before it
                crosses into another container is now applied in the correct
                container&apos;s coordinates.
              </li>
              <li>
                A gateway registers again if a restarted orchestrator no longer
                lists it.
              </li>
              <li>
                Deployments no longer include a leftover web client build. Host
                web builds with your game and point them at the gateway.
              </li>
              <li>
                Player builds now compile every script assembly from scratch,
                which prevents a worker from running outdated game code.
              </li>
            </ul>
          </section>

          <section className="rounded-xl border border-fd-border bg-fd-card p-6">
            <h2 className="text-2xl font-semibold">Five releases shipped</h2>
            <p className="mt-3 text-fd-muted-foreground">
              Releases 0.1.0-alpha.23 through 0.1.0-alpha.27 cover the work in
              this update. The wire protocol is now version 16, so rebuild
              clients, gateways, and workers together when upgrading. Custom
              control planes must implement{' '}
              <code>IGatewaySessionControlPlane</code> while one connection per
              player is turned on.
            </p>
            <ul className="mt-5 space-y-3">
              {releases.map(([version, summary]) => (
                <li key={version} className="flex flex-col gap-1 sm:flex-row sm:gap-3">
                  <a
                    href={`https://github.com/1by3/nebula/releases/tag/v${version}`}
                    className="inline-flex shrink-0 items-center gap-1 font-medium text-fd-primary"
                  >
                    {version} <ExternalLink className="size-3.5" />
                  </a>
                  <span className="text-fd-muted-foreground">{summary}</span>
                </li>
              ))}
            </ul>
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
