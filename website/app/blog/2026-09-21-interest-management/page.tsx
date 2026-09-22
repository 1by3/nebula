import type { Metadata } from 'next';
import Link from 'next/link';
import { ArrowLeft, ExternalLink } from 'lucide-react';

const title = 'Interest management keeps large worlds local';
const description =
  'Nebula now sends each player, gateway, and worker only the entity traffic it needs, with policies for cameras, spectators, fog of war, and carried entities.';

export const metadata: Metadata = {
  title,
  description,
};

export default function InterestManagementPage() {
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
          <p className="text-sm font-medium text-fd-primary">Feature update</p>
          <h1 className="mt-2 text-4xl font-bold tracking-tight sm:text-5xl">
            {title}
          </h1>
          <time
            dateTime="2026-09-21"
            className="mt-4 block text-sm text-fd-muted-foreground"
          >
            September 21, 2026
          </time>
          <p className="mt-6 text-xl leading-8 text-fd-muted-foreground">
            Nebula now limits entity traffic to the part of the world each
            player needs. The same system narrows what gateways cache and what
            workers publish, so a larger world does not automatically become a
            larger network view for every player.
          </p>
        </header>

        <div className="space-y-12 py-10 text-base leading-7">
          <section>
            <h2 className="text-2xl font-semibold">
              Stop treating a public world as one broadcast room
            </h2>
            <p className="mt-4 text-fd-muted-foreground">
              Before this release, every client in a public instance was told
              about every entity in that instance. Distance changed how often
              an entity&apos;s transform was sent, but it did not stop the spawn,
              state, or later messages. Every gateway also connected to every
              worker and cached every entity it held.
            </p>
            <p className="mt-4 text-fd-muted-foreground">
              That model made a distant entity part of each client&apos;s network
              state even when the player could not see or interact with it.
              Adding entities or workers therefore increased work across the
              whole mesh, not just near the part of the world that changed.
            </p>
          </section>

          <section>
            <h2 className="text-2xl font-semibold">
              Reduce traffic at three levels
            </h2>
            <p className="mt-4 text-fd-muted-foreground">
              Interest management is the system that decides which entities a
              client hears about. Nebula now applies that decision throughout
              the route from worker to player:
            </p>
            <ol className="mt-4 list-decimal space-y-3 pl-6 text-fd-muted-foreground">
              <li>
                A client holds an exact <strong>interest set</strong>. An entity
                outside that set is not spawned, and its transforms, variables,
                synchronization state, and remote procedure calls (RPCs) are not
                relayed to that client.
              </li>
              <li>
                A gateway divides absolute world space into regions and
                subscribes only to the regions covered by its clients&apos; points
                of interest. It can disconnect from a worker when no client,
                owned entity, or explicit subscription still needs that worker.
              </li>
              <li>
                A worker groups its authoritative entities by region and sends
                each region only to gateways that subscribed to it. Gateways
                evict records after the last local reason to keep them is gone.
              </li>
            </ol>
            <p className="mt-4 text-fd-muted-foreground">
              The default point of interest is the player&apos;s pawn. The default
              radius is 120 meters, and the region grid uses 64-meter cells.
              Enter and exit distances differ, and a short linger period keeps
              entities from repeatedly spawning and despawning near the edge.
              Transform update rates still step down with distance inside the
              set.
            </p>
            <p className="mt-4 text-fd-muted-foreground">
              Container ownership is scoped too. A client receives incremental
              ownership rows for nearby containers, its current instance, and
              containers needed by entities already in its set instead of the
              mesh&apos;s complete lease table.
            </p>
            <GuideLink href="/docs/guides/interest-management#how-it-flows">
              Follow the interest flow
            </GuideLink>
          </section>

          <section>
            <h2 className="text-2xl font-semibold">
              Start with useful defaults, then describe your game
            </h2>
            <p className="mt-4 text-fd-muted-foreground">
              The default behavior needs no game code or world partition. A
              player hears about nearby entities, keeps its own pawn at the full
              update rate, and receives only globally relevant entities while
              it has no pawn. Interest evaluation is staggered across ticks so
              all clients do not create one periodic spike.
            </p>
            <p className="mt-4 text-fd-muted-foreground">
              Three prefab settings handle common exceptions. Use{' '}
              <code>RelevanceRadius</code> for an entity that should be visible
              from a different distance, <code>AlwaysRelevant</code> for the
              small set every client must receive, and <code>InterestGroup</code>{' '}
              as an input to game-specific filtering.
            </p>
            <p className="mt-4 text-fd-muted-foreground">
              For rules such as spectators, party members, strategy cameras, or
              fog of war, install an <code>IInterestPolicy</code>. A policy can
              add point or box-shaped foci, subscribe to a limited number of
              entities by ID, and authorize each client-entity pair. Tightening
              a rule can revoke existing replicas immediately; revealing new
              information stays on the bounded evaluation queue.
            </p>
            <p className="mt-4 text-fd-muted-foreground">
              A standalone gateway can load that game code through a configured
              gateway extension. Extension failures are isolated and counted,
              and an authorization error fails closed.
            </p>
            <GuideLink href="/docs/guides/gateway-extensions">
              Add game rules to a standalone gateway
            </GuideLink>
          </section>

          <section>
            <h2 className="text-2xl font-semibold">
              Treat camera positions as requests
            </h2>
            <p className="mt-4 text-fd-muted-foreground">
              A client can send a focus hint for a camera that looks away from
              its pawn. The gateway treats the hint as untrusted input. It
              rejects malformed coordinates, limits attempts to five per
              second by default, and applies a server-owned focus mode.
            </p>
            <p className="mt-4 text-fd-muted-foreground">
              The default mode clamps the hint to 60 meters from the pawn. A
              server can grant a free focus to a spectator or strategy camera,
              or disable hints for a client. Instance isolation still applies
              in every mode, so a wider camera cannot reveal another private
              instance.
            </p>
            <p className="mt-4 text-fd-muted-foreground">
              Hints use absolute, double-precision world coordinates so a
              floating-origin shift does not move the requested point. Clearing
              a hint is reliable and carries a generation number, which prevents
              an older in-flight hint from restoring a view the client already
              closed. The client&apos;s content anchor remains separate: the hint
              asks the server what to send, while the anchor chooses what the
              client keeps loaded.
            </p>
            <GuideLink href="/docs/guides/interest-management#client-focus-hints-are-inputs-never-authority">
              Configure client focus hints
            </GuideLink>
          </section>

          <section>
            <h2 className="text-2xl font-semibold">
              Keep carried entities with their carrier
            </h2>
            <p className="mt-4 text-fd-muted-foreground">
              An entity inside a moving container now follows its outermost, or
              root, carrier for interest. A passenger on a ship enters and
              leaves a client&apos;s set with the ship, even through nested carriers
              or a cross-worker handover. Spawns arrive carrier-first and
              removals happen contents-first, so the client always has the
              frame needed to place a passenger.
            </p>
            <p className="mt-4 text-fd-muted-foreground">
              Boarding, disembarking, a carrier arriving late, and a carrier
              being destroyed all reclassify and publish the affected subtree.
              A surviving passenger is moved into the carrier&apos;s former
              container before the carrier disappears. Carrier cycles are
              refused, and valid carrier chains have no fixed depth or subtree
              limit.
            </p>
            <GuideLink href="/docs/guides/interest-management#carried-entities">
              Read the carried-entity guarantees
            </GuideLink>
          </section>

          <section>
            <h2 className="text-2xl font-semibold">
              Stream entities and world content together
            </h2>
            <p className="mt-4 text-fd-muted-foreground">
              Every authorized focus now opens a bounded container window, not
              only the pawn. A strategy camera can therefore receive the empty
              chunks beneath it as well as nearby entities. Client focus never
              allocates a chunk; allocation remains a server decision.
            </p>
            <p className="mt-4 text-fd-muted-foreground">
              The release also adds an optional turnkey chunked world. With{' '}
              <code>NebulaConfig.ChunkedWorld</code> enabled, Nebula allocates
              and retires chunks around simulated pawns, maintains the client&apos;s
              floating origin, and raises content load and unload hooks. The
              game supplies the content for each chunk.
            </p>
            <GuideLink href="/docs/guides/infinite-runtime-world">
              Build an infinite runtime world
            </GuideLink>
          </section>

          <section>
            <h2 className="text-2xl font-semibold">
              See what the system is saving
            </h2>
            <p className="mt-4 text-fd-muted-foreground">
              The dashboard and debug overlay now report interest-set sizes,
              cached entities, subscribed regions, worker-link reasons, spawn
              and despawn rates, evaluation time, and worker-side filtered
              entries and bytes. Partition warnings identify a container with
              too many entities or expensive gateway filtering, and{' '}
              <code>InterestProbe</code> adds machine-readable client checks for
              load and soak tests.
            </p>
            <p className="mt-4 text-fd-muted-foreground">
              Interest management bounds replication and network work. It does
              not divide simulation work: every container still belongs to one
              authoritative worker. A dense area that exceeds one worker&apos;s
              budget still needs to be partitioned into more containers.
            </p>
            <GuideLink href="/docs/guides/interest-management#observability">
              Monitor interest management
            </GuideLink>
          </section>

          <section className="rounded-xl border border-fd-border bg-fd-card p-6">
            <h2 className="text-2xl font-semibold">Upgrade to alpha.29</h2>
            <p className="mt-3 text-fd-muted-foreground">
              Interest management ships in <code>0.1.0-alpha.29</code> and
              changes the wire protocol from version 16 to 17. Rebuild and
              restart every client, gateway, worker, and orchestrator together;
              mixed protocol versions do not connect.
            </p>
            <p className="mt-4 text-fd-muted-foreground">
              Audit any feature that assumed every public entity was always
              present, including full-map radar and pawn-less spectators. Mark
              the few global entities as always relevant or express the view
              through an interest policy. Custom clients must also apply full,
              upsert, and remove operations from the new incremental container
              ownership message.
            </p>
            <div className="mt-5 flex flex-col items-start gap-3 sm:flex-row sm:gap-6">
              <Link
                href="/docs/guides/interest-management"
                className="inline-flex font-medium text-fd-primary hover:underline"
              >
                Configure interest management
              </Link>
              <a
                href="https://github.com/1by3/nebula/releases/tag/v0.1.0-alpha.29"
                className="inline-flex items-center gap-1 font-medium text-fd-primary hover:underline"
              >
                Read the release notes <ExternalLink className="size-3.5" />
              </a>
            </div>
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
