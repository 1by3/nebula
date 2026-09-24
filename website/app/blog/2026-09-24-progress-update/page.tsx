import type { Metadata } from 'next';
import Link from 'next/link';
import { ArrowLeft, ExternalLink } from 'lucide-react';

const title = 'Nebula progress update: September 24, 2026';
const description =
  'Alpha.32 lets cargo ride in moving ships, fixes entities to containers, stows ships with their cargo, and fixes chunk leasing and player sessions aboard ships.';

export const metadata: Metadata = {
  title,
  description,
};

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
            Alpha.31 gave a container its own physics frame: a ship&apos;s
            interior that simulates in the ship&apos;s coordinates while the
            ship flies. Alpha.32 makes that interior useful for cargo. Loose
            crates ride through a whole flight, entities can be fixed in place,
            and a ship can be put away with its cargo aboard.
          </p>
        </header>

        <div className="space-y-12 py-10 text-base leading-7">
          <section>
            <h2 className="text-2xl font-semibold">Carry loose cargo in a moving ship</h2>
            <p className="mt-4 text-fd-muted-foreground">
              A physics frame is a separate physics scene in which a container
              stands still. A crate inside a ship&apos;s frame needs no new
              component: it simulates where the ship is at rest, so a stack of
              crates stays in order through takeoff, turns, cruise at 1 km/s,
              and landing, then falls asleep. A handover of the ship to another
              worker keeps each crate&apos;s pose, velocity, and sleep.
            </p>
            <p className="mt-4 text-fd-muted-foreground">
              Moving hull parts now carry what rests on them. On a worker, the
              frame&apos;s copy of a ramp, lift, or door becomes a kinematic
              body the first time it moves, so a crate on a rising lift rides
              it instead of being pushed out of it.
            </p>
            <GuideLink href="/docs/guides/physics-frames#carry-loose-cargo">
              Carry loose cargo
            </GuideLink>
          </section>

          <section>
            <h2 className="text-2xl font-semibold">Let cargo feel the ship move</h2>
            <p className="mt-4 text-fd-muted-foreground">
              By default, a crate in a frame does not notice the ship
              accelerating. Add <code>FrameInertia</code> to make it slide when
              the ship accelerates, brakes, or turns. <code>Scale</code> sets
              how much of the motion it feels, like inertial dampers.{' '}
              <code>MaxAcceleration</code> keeps a hard landing from launching
              it, and <code>MinAcceleration</code> lets it sleep during a
              steady cruise.
            </p>
            <GuideLink href="/docs/guides/physics-frames#let-cargo-feel-the-ship-move">
              Configure FrameInertia
            </GuideLink>
          </section>

          <section>
            <h2 className="text-2xl font-semibold">Fix an entity to a container</h2>
            <p className="mt-4 text-fd-muted-foreground">
              <code>FrameAttachment</code> holds an entity at a pose in a
              container&apos;s space: a crate strapped to a cargo grid, a turret
              on a deck, or a crate on a shelf in a building. Call{' '}
              <code>Attach</code> or <code>Detach</code> on the authoritative
              worker, or <code>RequestAttach</code> and{' '}
              <code>RequestDetach</code> from another worker.
            </p>
            <p className="mt-4 text-fd-muted-foreground">
              An attached entity stays in its container whatever its position,
              and loose crates can rest on it. The attachment survives a
              handover, a restore from persistence, and a player joining later.
              A detach leaves the entity where it is and eases apart any
              overlap with its neighbors.
            </p>
            <GuideLink href="/docs/guides/physics-frames#attach-an-entity-to-a-container">
              Attach an entity to a container
            </GuideLink>
          </section>

          <section>
            <h2 className="text-2xl font-semibold">Put a ship away with its cargo</h2>
            <p className="mt-4 text-fd-muted-foreground">
              Despawning a carrier used to set down everything inside it. To
              put a ship in a hangar and keep its cargo aboard, pass{' '}
              <code>CargoPolicy.Stow</code>:
            </p>
            <pre className="mt-4 overflow-x-auto rounded-lg border border-fd-border bg-fd-card p-4 text-sm">
              <code>worker.Despawn(ship, keepPersisted: true, CargoPolicy.Stow);</code>
            </pre>
            <p className="mt-4 text-fd-muted-foreground">
              Nebula saves each persistent rider aboard the ship, and the cargo
              comes back when the ship is restored. Players&apos; pawns and
              entities that are not persistent are still set down.
            </p>
            <GuideLink href="/docs/guides/physics-frames#stow-a-ship-with-its-cargo">
              Stow a ship with its cargo
            </GuideLink>
          </section>

          <section>
            <h2 className="text-2xl font-semibold">Fix chunk leasing and placement aboard ships</h2>
            <p className="mt-4 text-fd-muted-foreground">
              In a chunked world, workers lease the chunks around each player.
              Inside a ship&apos;s frame, a worker read the pilot&apos;s position
              in the ship&apos;s own coordinates, so the leased chunks stayed
              near the world&apos;s origin wherever the ship flew. They now
              follow the ship, and a client aboard it keeps its floating origin
              on the chunk the ship is in.
            </p>
            <p className="mt-4 text-fd-muted-foreground">
              <code>NetworkIdentity.PlaceInScope</code> and instance transfers
              placed an entity tens of meters off when the target was inside a
              ship. They now convert the pose into the ship&apos;s frame, so a
              teleport to a point inside a ship lands where it should.
            </p>
            <GuideLink href="/docs/guides/infinite-runtime-world#each-world-keeps-its-own-floating-origin">
              Read about origins in a chunked world
            </GuideLink>
          </section>

          <section>
            <h2 className="text-2xl font-semibold">Remove a disconnected player&apos;s pawn after a handover</h2>
            <p className="mt-4 text-fd-muted-foreground">
              When a player disconnects, the worker keeps their pawn for{' '}
              <code>SessionReclaimSeconds</code> so they can reconnect. If the
              pawn changed worker during that time, the new worker treated the
              player as connected and kept the pawn, and the chunk it stood in,
              forever. The countdown now travels with the pawn. The new worker
              resumes it with the time that was left and removes the pawn when
              it runs out, unless the player has reconnected.
            </p>
            <GuideLink href="/docs/guides/authentication#the-reclaim-grace">
              Read about the reclaim grace
            </GuideLink>
          </section>

          <section>
            <h2 className="text-2xl font-semibold">Fix the dashboard</h2>
            <p className="mt-4 text-fd-muted-foreground">
              An apostrophe in a tooltip stopped the orchestrator&apos;s
              dashboard from loading in alpha.31. It loads again.
            </p>
          </section>

          <section className="rounded-xl border border-fd-border bg-fd-card p-6">
            <h2 className="text-2xl font-semibold">Upgrade to alpha.32</h2>
            <p className="mt-3 text-fd-muted-foreground">
              <code>0.1.0-alpha.32</code> keeps wire protocol 19, so clients
              built with alpha.31 can still connect. The handover message
              between workers gained an optional field, and every worker of a
              mesh must run the same build, so rebuild and restart your workers
              together.
            </p>
            <div className="mt-5 flex flex-col items-start gap-3 sm:flex-row sm:gap-6">
              <Link
                href="/docs/guides/physics-frames"
                className="inline-flex font-medium text-fd-primary hover:underline"
              >
                Read the physics frames guide
              </Link>
              <a
                href="https://github.com/1by3/nebula/releases/tag/v0.1.0-alpha.32"
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
