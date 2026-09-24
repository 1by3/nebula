import type { Metadata } from 'next';
import Link from 'next/link';
import { ArrowLeft, ExternalLink } from 'lucide-react';

const title = 'Nebula progress update: September 24, 2026';
const description =
  'Three alpha releases add safer upgrades, worlds you activate by key, containers in a tree with their own physics, and cargo that rides in moving ships.';

export const metadata: Metadata = {
  title,
  description,
};

const releases = [
  ['0.1.0-alpha.30', 'Scoped worlds, cross-worker call and location contracts, upgrade policy, and recovery'],
  ['0.1.0-alpha.31', 'Containers in a tree, physics frames, and crewed ships crossing between worlds'],
  ['0.1.0-alpha.32', 'Cargo in moving ships, attachment, and session and chunk fixes'],
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
            dateTime="2026-09-24"
            className="mt-4 block text-sm text-fd-muted-foreground"
          >
            September 24, 2026
          </time>
          <p className="mt-6 text-xl leading-8 text-fd-muted-foreground">
            Since the interest management release, three alpha releases let
            you upgrade a running mesh, recover it after an orchestrator
            restart, and create worlds on demand. Containers can now nest and
            carry their own physics, so a ship can fly between worlds with its
            crew and cargo aboard.
          </p>
        </header>

        <div className="space-y-12 py-10 text-base leading-7">
          <section>
            <h2 className="text-2xl font-semibold">Upgrade without surprising players</h2>
            <p className="mt-4 text-fd-muted-foreground">
              A gateway, the process that accepts player connections, now
              accepts clients built for the current wire protocol and the one
              before it. A client outside that window gets a refusal that names
              the versions the server supports, instead of a silent disconnect.
              It does not retry by itself, so your game can tell the player to
              update.
            </p>
            <p className="mt-4 text-fd-muted-foreground">
              Your game can also set its own content version with{' '}
              <code>NebulaConfig.GameContentVersion</code>. The gateway refuses
              a client whose content does not match, with a separate reason
              code. The upgrade guide describes how to drain and replace one
              gateway or worker at a time while players keep their sessions.
            </p>
            <GuideLink href="/docs/deploy/upgrades">Upgrade a running mesh</GuideLink>
          </section>

          <section>
            <h2 className="text-2xl font-semibold">Recover after an orchestrator restart</h2>
            <p className="mt-4 text-fd-muted-foreground">
              The orchestrator is the process that assigns containers, the
              box-shaped areas of the world, to workers. If it came back without
              its stored state, workers kept simulating containers that the rest
              of the mesh no longer knew about. Workers now register again and
              claim the containers they still simulate. In our test, a
              replacement orchestrator with an empty database had every
              container assigned again in 2.6 seconds.
            </p>
            <p className="mt-4 text-fd-muted-foreground">
              A new restore drill backs up a database, wipes it, restores it,
              and checks the result through Nebula&apos;s own storage code, so
              you can rehearse a restore before you need one.
            </p>
            <GuideLink href="/docs/deploy/availability">Restart and restore a mesh</GuideLink>
          </section>

          <section>
            <h2 className="text-2xl font-semibold">Create worlds on demand</h2>
            <p className="mt-4 text-fd-muted-foreground">
              A scope is a separate simulated world, such as a dungeon run, a
              match, or a player&apos;s home. Any process that can reach the
              control plane can now ask for a scope by key with{' '}
              <code>ActivateScope</code>. Asking twice for the same key returns
              the same world, so a matchmaker and a travel menu can both ask
              without coordinating.
            </p>
            <p className="mt-4 text-fd-muted-foreground">
              A scope that holds nothing for <code>ScopeIdleRetireSeconds</code>{' '}
              (5 minutes by default) is retired: Nebula saves its persistent
              entities, confirms the saves, and releases its containers. The
              next request brings it back with what was saved.
            </p>
            <p className="mt-4 text-fd-muted-foreground">
              A scope can also be a whole procedural world. Each scope gets its
              own chunk grid, and each world keeps its own floating origin, so
              one worker can hold an arena and a distant continent without
              losing precision in either.
            </p>
            <GuideLink href="/docs/guides/scopes">Use simulation scopes</GuideLink>
          </section>

          <section>
            <h2 className="text-2xl font-semibold">Know what happened to a cross-worker call</h2>
            <p className="mt-4 text-fd-muted-foreground">
              An <code>AuthorityRpc</code> is a remote procedure call (RPC) to
              the worker that simulates an entity. Nebula now applies each call
              at most once, follows the entity through up to three handovers,
              and rejects a call that is too far out of date.{' '}
              <code>AuthorityRpcWithReply</code> tells the caller the outcome:
              accepted, rejected with a reason, or timed out.
            </p>
            <p className="mt-4 text-fd-muted-foreground">
              Every entity also has one <code>EntityLocation</code>: its scope,
              its container, and its pose in that container. It reads the same
              on every process and in storage.
            </p>
            <GuideLink href="/docs/guides/rpcs#what-nebula-promises-for-a-cross-worker-call">
              Read what Nebula promises for a call
            </GuideLink>
          </section>

          <section>
            <h2 className="text-2xl font-semibold">Keep related entities on one worker</h2>
            <p className="mt-4 text-fd-muted-foreground">
              Cohesion hints let your game say that a set of entities must be
              simulated by one worker and move between workers together, such
              as the parts of a vehicle. You can also stop Nebula from
              rebalancing a container for a limited time, for example during a
              boss fight.
            </p>
            <p className="mt-4 text-fd-muted-foreground">
              Nebula also checks your project for joints between entities in
              different containers. Physics cannot hold such a joint together
              across two workers, so <strong>Nebula &gt; Validate Project</strong>{' '}
              and the worker now warn about it.
            </p>
            <GuideLink href="/docs/guides/cohesion">Use cohesion hints</GuideLink>
            <br />
            <GuideLink href="/docs/concepts/distributed-physics">
              Understand distributed physics
            </GuideLink>
          </section>

          <section>
            <h2 className="text-2xl font-semibold">Nest containers and give them their own physics</h2>
            <p className="mt-4 text-fd-muted-foreground">
              Containers now form a tree. A container can sit inside another,
              such as an engine room inside a ship, and can either be simulated
              by its parent&apos;s worker or be assigned to a worker of its own.
              A container placed 10,000 km from the origin lands within a
              centimeter.
            </p>
            <p className="mt-4 text-fd-muted-foreground">
              A container can also have a physics frame: a physics scene of its
              own in which the container stands still. Players and objects
              inside a flying ship simulate in the ship&apos;s coordinates, with
              the ship&apos;s floor as down, at any speed. A planet can be a
              frame too, so its rotation does not disturb what stands on it.
            </p>
            <p className="mt-4 text-fd-muted-foreground">
              The dashboard shows the tree, which containers inherit their
              worker, and which ones have a frame.
            </p>
            <GuideLink href="/docs/guides/physics-frames">Use physics frames</GuideLink>
          </section>

          <section>
            <h2 className="text-2xl font-semibold">Fly a crewed ship into another world</h2>
            <p className="mt-4 text-fd-muted-foreground">
              A ship with players aboard can now fly from one scope into
              another, for example from a planet&apos;s world into space. The
              crew stays seated, and their clients follow the ship without
              reconnecting. Players left behind never learn where the ship
              went.
            </p>
            <GuideLink href="/docs/guides/scopes#move-a-crewed-carrier-into-another-scope">
              Move a crewed carrier into another scope
            </GuideLink>
          </section>

          <section>
            <h2 className="text-2xl font-semibold">Carry cargo in a moving ship</h2>
            <p className="mt-4 text-fd-muted-foreground">
              Loose crates in a ship&apos;s frame need no new component. A stack
              stays in order through takeoff, turns, cruise at 1 km/s, and
              landing, then falls asleep, and it keeps its state through a
              handover of the ship to another worker. Moving ramps, lifts, and
              doors carry what rests on them.
            </p>
            <ul className="mt-4 list-disc space-y-3 pl-6 text-fd-muted-foreground">
              <li>
                <code>FrameInertia</code> makes a crate slide when the ship
                accelerates, brakes, or turns. <code>Scale</code> sets how much
                of the motion it feels.
              </li>
              <li>
                <code>FrameAttachment</code> holds an entity at a pose in a
                container, such as a crate strapped to a cargo grid or a turret
                on a deck. The attachment survives handovers, restores, and
                late joins.
              </li>
              <li>
                <code>CargoPolicy.Stow</code> puts a ship away with its
                persistent cargo aboard. The cargo comes back when the ship is
                restored.
              </li>
            </ul>
            <GuideLink href="/docs/guides/physics-frames#carry-loose-cargo">
              Carry loose cargo
            </GuideLink>
          </section>

          <section>
            <h2 className="text-2xl font-semibold">Check Nebula&apos;s guarantees</h2>
            <p className="mt-4 text-fd-muted-foreground">
              The conformance suite is a set of deterministic tests for what
              Nebula promises across workers: handovers, calls, locations,
              scopes, physics frames, and sessions. It now covers 26 scenarios
              and runs real workers in one Editor process.
            </p>
            <GuideLink href="/docs/guides/conformance-suite">Run the conformance suite</GuideLink>
          </section>

          <section>
            <h2 className="text-2xl font-semibold">Fix persistence, sessions, and ships</h2>
            <p className="mt-4 text-fd-muted-foreground">
              These releases also fix problems found while testing large
              worlds:
            </p>
            <ul className="mt-4 list-disc space-y-3 pl-6 text-fd-muted-foreground">
              <li>
                A worker that missed heartbeats but kept running could leave
                two live copies of its persistent entities. Nebula now keeps at
                most one.
              </li>
              <li>
                A worker given hundreds of containers at once, or a game that
                filled the thread pool, could get a worker declared dead. Both
                are fixed.
              </li>
              <li>
                A disconnected player&apos;s pawn that changed worker was never
                removed. The reconnect countdown now travels with the pawn.
              </li>
              <li>
                Chunks around a pilot stayed near the origin wherever the ship
                flew, and a teleport into a ship landed tens of meters off.
                Both now use the ship&apos;s position correctly.
              </li>
              <li>
                A player in the middle of a jump lost that motion at a
                container boundary. Predicted state now travels with the
                handover.
              </li>
              <li>
                Two ships with overlapping interiors could end up inside each
                other and stop the worker. Nebula never places an entity inside
                a container it carries.
              </li>
            </ul>
          </section>

          <section className="rounded-xl border border-fd-border bg-fd-card p-6">
            <h2 className="text-2xl font-semibold">Three releases shipped</h2>
            <p className="mt-3 text-fd-muted-foreground">
              Alpha.30 uses wire protocol 18 and alpha.31 uses protocol 19.
              Neither can talk to the release before it, so rebuild and restart
              every client, gateway, worker, and orchestrator together when you
              upgrade from alpha.29 or alpha.30. Alpha.31 folds{' '}
              <code>DynamicContainer</code> into <code>Container</code>; run{' '}
              <strong>Nebula &gt; Migrate &gt; Remove DynamicContainer</strong>{' '}
              to update your prefabs. Alpha.32 keeps protocol 19.
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
