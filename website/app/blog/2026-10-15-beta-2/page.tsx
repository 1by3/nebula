import type { Metadata } from 'next';
import Link from 'next/link';
import { ArrowLeft, ExternalLink } from 'lucide-react';

const title = 'Nebula 0.1.0-beta.2';
const description =
  'The third beta adds portals between scopes, physics and queries that continue through them, tools for planet-sized worlds, driver prediction for server-owned vehicles, and release and IL2CPP server builds.';

export const metadata: Metadata = {
  title,
  description,
};

export default function BetaTwoReleasePage() {
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
            dateTime="2026-10-15"
            className="mt-4 block text-sm text-fd-muted-foreground"
          >
            October 15, 2026
          </time>
          <p className="mt-6 text-xl leading-8 text-fd-muted-foreground">
            This release follows beta.1. A <code>Portal</code> now connects two
            scopes, and bodies, shots, and cameras work across it. A world can
            be as large as a planet without losing precision, and a player can
            drive a server-owned vehicle with no input delay. A Linux server
            can be a release build or use IL2CPP. This release replaces{' '}
            <code>InstanceBoundary</code>, so projects that use it need a
            port, and clients built for beta.1 must be rebuilt.
          </p>
        </header>

        <div className="space-y-12 py-10 text-base leading-7">
          <section>
            <h2 className="text-2xl font-semibold">Connect two scopes with a portal</h2>
            <p className="mt-4 text-fd-muted-foreground">
              A scope is a separate space in your world, such as an apartment
              interior or a dungeon. Until now, <code>InstanceBoundary</code>{' '}
              was the only way between a scope and the world. It prepared a
              crossing for each player who came near, held the player at the
              door until the destination answered, and loaded the whole
              interior inside the worker&apos;s tick.
            </p>
            <p className="mt-4 text-fd-muted-foreground">
              <code>Portal</code> replaces it. A portal is a component at a
              face, with an opening called the aperture and a far side that is
              either a template (one far scope per party) or a fixed scope. Your
              game calls <code>Open(party, members)</code> and{' '}
              <code>Close(party)</code>. Each party has a link whose state is
              Closed, Preparing, Open, or Closing, and whose <code>Wait</code>{' '}
              value always says why the link is not open yet.
            </p>
            <ul className="mt-4 list-disc space-y-3 pl-6 text-fd-muted-foreground">
              <li>
                Any entity that is spawned and not pinned crosses an open link
                on its own: a player, a crate, a projectile, an NPC, or a
                vehicle with its riders. A fast body is tested on the step to
                come, so a projectile never simulates a step behind the face.
              </li>
              <li>
                <code>CanEnter</code> and the new <code>CanLeave</code> let
                your game decide what may cross in each direction.
              </li>
              <li>
                The interior&apos;s content loads asynchronously, and the link
                reports <code>FarWorkerContent</code> while it waits.
              </li>
              <li>
                Linked scopes stay on one worker, and a crossing between
                workers is one handover.
              </li>
              <li>
                A link can hold for <code>CloseHoldSeconds</code> so that a
                door closes over a far side that is still drawn.
              </li>
            </ul>
            <GuideLink href="/docs/guides/instancing">
              Create private instances
            </GuideLink>
          </section>

          <section>
            <h2 className="text-2xl font-semibold">Collide, cast, and look through a portal</h2>
            <p className="mt-4 text-fd-muted-foreground">
              Each scope is its own physics scene, so a shot at a door used to
              hit the door or nothing. Across an open portal:
            </p>
            <ul className="mt-4 list-disc space-y-3 pl-6 text-fd-muted-foreground">
              <li>
                A body whose extent touches the opening gets a kinematic proxy
                in the other scope, so it collides with what is on the other
                side before it crosses.
              </li>
              <li>
                <code>PortalQueries</code> offers <code>Raycast</code>,
                sweeps, and overlaps that continue through an open link.{' '}
                <code>Raycast</code> can also report where the cast went
                through (<code>PortalCrossing</code>). Lag-compensated rewind
                covers the far scope.
              </li>
              <li>
                A client that may look through a portal draws the other side in
                its own pose, and several scopes can be drawn at once. Each
                drawn scope can have its own rendering layer.
              </li>
              <li>
                <code>Portals.Map</code>, <code>Portals.TryMap</code>, and{' '}
                <code>Portals.Turn</code> carry a pose or a direction between
                two scopes, in double precision.
              </li>
            </ul>
            <p className="mt-4 text-fd-muted-foreground">
              A portal can stand in a chunk of a planet, and a body can cross
              into a scope whose container simulates in a physics frame.
            </p>
          </section>

          <section>
            <h2 className="text-2xl font-semibold">Build planet-sized worlds</h2>
            <p className="mt-4 text-fd-muted-foreground">
              A physics frame is a moving or turning space of its own, such as
              a ship or a planet. Several changes make large frames work at
              distances where a 32-bit float is meters apart.
            </p>
            <ul className="mt-4 list-disc space-y-3 pl-6 text-fd-muted-foreground">
              <li>
                A scope can host chunk grids in a container, with{' '}
                <code>NebulaChunkedWorld.ActivateHostedGrid</code>. A planet
                that is a carrier can stream its ground in chunks around
                players, and a game with a different shape, such as a cube
                sphere, supplies its own <code>IChunkGridGeometry</code>.
              </li>
              <li>
                You can place an entity, or commit a transfer, at a pose in a
                frame&apos;s own coordinates, composed in double. An entity
                placed on a pad of a planet 300,000 km out lands within a
                millimeter of it.
              </li>
              <li>
                A client draws a large frame about a render origin near the
                camera, so ground 200 km from a planet&apos;s center no longer
                shakes as the planet turns, and a client predicts far out
                about the same origin its worker uses.
              </li>
              <li>
                A far relevance tier lets a large or important entity be seen
                as a marker far beyond the interest radius: a ship 50 km away
                in orbit can be a dot the player sees and targets. Set{' '}
                <code>FarRelevanceRadius</code> on the entity and read{' '}
                <code>NebulaClient.FarEntities</code>.
              </li>
              <li>
                <code>Container.FrameApproachMargin</code> lets a client
                outside a planet&apos;s box see what stands on it. It is off by
                default and set per container.
              </li>
              <li>
                A container with a very large carried box no longer stalls the
                worker&apos;s broad phase, which a planet-sized frame did.
              </li>
            </ul>
            <GuideLink href="/docs/guides/physics-frames">
              Physics frames
            </GuideLink>
            <br />
            <GuideLink href="/docs/guides/interest-management">
              Interest management
            </GuideLink>
          </section>

          <section>
            <h2 className="text-2xl font-semibold">Drive a server-owned vehicle with no input delay</h2>
            <p className="mt-4 text-fd-muted-foreground">
              A boat or a car that no player owns used to answer the wheel
              after a round trip plus the interpolation delay: 140 ms on a
              30 ms link and 273 ms on a 150 ms link in our measurements, and a
              driver steering by what they saw overshot the heading they
              wanted.
            </p>
            <p className="mt-4 text-fd-muted-foreground">
              Call <code>NetworkIdentity.SetDriver(clientId)</code> to give the
              wheel to a client. That client predicts the vehicle every tick
              and is reconciled to the worker, as it is for its own pawn. A
              driver whose input stops for 30 ticks loses the wheel, and the
              driver travels with a handover. The vehicle&apos;s model must be
              deterministic and kinematic; a <code>NetworkRigidbody</code>{' '}
              vehicle is not predicted. A <strong>driven vehicle</strong>{' '}
              sample shows the code.
            </p>
            <GuideLink href="/docs/guides/prediction">
              Prediction and reconciliation
            </GuideLink>
          </section>

          <section>
            <h2 className="text-2xl font-semibold">Smooth entities that update slowly</h2>
            <p className="mt-4 text-fd-muted-foreground">
              A client drew every remote entity three ticks behind the newest
              state. An entity whose state arrived further apart than that
              (for example, one updated every six ticks, or one in a slow
              distance tier) was held still, extrapolated, and pulled back. A
              crowd 50 m away stood and jumped twice a second.
            </p>
            <p className="mt-4 text-fd-muted-foreground">
              The client now measures how far apart each entity&apos;s states
              arrive and draws that entity by its own gap later, up to{' '}
              <code>NebulaConfig.SlowUpdateMaxDelayTicks</code> (30; 0 turns it
              off). An entity sent every tick or two gets no extra delay, so
              players and nearby vehicles are drawn as before.
            </p>
          </section>

          <section>
            <h2 className="text-2xl font-semibold">Build, measure, and test servers</h2>
            <ul className="mt-4 list-disc space-y-3 pl-6 text-fd-muted-foreground">
              <li>
                <strong>Release and IL2CPP builds.</strong> A Linux server
                build was always a Mono development build.{' '}
                <code>NebulaConfig</code> now has{' '}
                <code>ServerBuildDevelopment</code> and{' '}
                <code>ServerBuildScriptingBackend</code>, and{' '}
                <code>nebula build --linux</code> and{' '}
                <code>nebula deploy</code> take <code>--release-build</code>,{' '}
                <code>--development-build</code>, <code>--il2cpp</code>, and{' '}
                <code>--mono</code> for one build. A project that changes
                nothing builds as before.
              </li>
              <li>
                <strong>Worker memory and frame time.</strong> Each worker
                reports resident, native, and managed memory in{' '}
                <code>nebula status</code>, the dashboard, and its{' '}
                <code>profile</code> log line. The profile line also splits the
                whole Unity frame into physics, <code>FixedUpdate</code>,{' '}
                <code>Update</code>, and <code>LateUpdate</code> time, and{' '}
                <code>NebulaProfiler.Section</code> times a section of your
                own.
              </li>
              <li>
                <strong>Load tests that behave like players.</strong>{' '}
                <code>nebula-loadgen</code> clients can{' '}
                <code>wander</code>, follow a <code>path</code>,{' '}
                <code>travel</code> between scopes, send RPCs, and churn,
                from a seeded, weighted mix. A game adds its own behaviors in a
                separate assembly with <code>--plugin</code>.
              </li>
              <li>
                <strong>Multiplayer Play Mode.</strong> Exactly one virtual
                player hosts the server, and every other virtual player joins
                as its own client. The server starts without anyone ticking a
                virtual player.
              </li>
              <li>
                <strong>Chunk ring margin.</strong>{' '}
                <code>ChunkedWorldServerRingMargin</code> sets how many chunks
                beyond <code>NearCells</code> a worker leases around each
                pawn. The default of 1 keeps today&apos;s ring.
              </li>
            </ul>
            <GuideLink href="/docs/guides/load-testing">
              Load testing a gateway fleet
            </GuideLink>
            <br />
            <GuideLink href="/docs/guides/configuration">
              Configure Nebula
            </GuideLink>
          </section>

          <section>
            <h2 className="text-2xl font-semibold">Things to know</h2>
            <ul className="mt-4 list-disc space-y-3 pl-6 text-fd-muted-foreground">
              <li>
                <strong>The scene render origin is on by default.</strong> When
                the camera is more than 1,024 m from Unity&apos;s origin, a
                client moves every loaded scene&apos;s root objects before
                drawing and restores them afterward. Static-batched meshes,
                world-space particles, trails, and light probes are not moved.
                A game with a large map and none of the new frame features can
                set <code>SceneRenderOrigin.Enabled</code> to false. We are
                considering making it opt-in.
              </li>
              <li>
                <strong>Slow entities and lag compensation.</strong> A client
                draws a slowly updated entity up to half a second behind, but a
                lag-compensated shot still claims the client&apos;s global
                render tick, so a hit on such an entity can be checked against
                a pose that is not the one the shooter saw. If you use
                lag compensation with <code>UpdateInterval</code> or distance
                tiers, set <code>SlowUpdateMaxDelayTicks</code> to 0 for now.
              </li>
              <li>
                <strong>The local save file is version 3.</strong> A game that
                runs on the local store and moves back to beta.1 will find the
                file unreadable, ignore it, and overwrite it on the next save.
                Keep a backup of <code>world.bin</code> before you try beta.2.
              </li>
              <li>
                <strong>IL2CPP stripping.</strong> Nebula finds RPCs, network
                variables, and serializable types by reflection, so keep your
                own assemblies with a <code>link.xml</code> beside your
                scripts. We have not yet tested a stripped IL2CPP build.
              </li>
              <li>
                Portals, instance scenes, and physics frames use 3D physics
                only.
              </li>
            </ul>
          </section>

          <section className="rounded-xl border border-fd-border bg-fd-card p-6">
            <h2 className="text-2xl font-semibold">Upgrade to beta.2</h2>
            <p className="mt-3 text-fd-muted-foreground">
              0.1.0-beta.2 uses wire protocol 28, and a gateway admits only
              clients of protocol 28. It refuses older clients, including those
              built for beta.1, with <code>ProtocolUnsupported</code>. Rebuild
              your clients against this version. Gateways and workers of one
              mesh must still run the same build.
            </p>
            <p className="mt-3 text-fd-muted-foreground">
              <code>InstanceBoundary</code>, <code>InstanceCrossingState</code>,{' '}
              <code>ObservePublic</code>, <code>PublicView</code>, and{' '}
              <code>ObserveHost</code> are removed, with no compatibility shim.
              Port each boundary to a <code>Portal</code>. A template and its
              key carry over, so saved instances are still found. The
              package changelog lists what replaces each setting.
            </p>
            <Link
              href="/docs/guides/instancing"
              className="mt-4 inline-flex font-medium text-fd-primary hover:underline"
            >
              Create private instances
            </Link>
            <br />
            <Link
              href="/docs/deploy/upgrades"
              className="mt-4 inline-flex font-medium text-fd-primary hover:underline"
            >
              Replace gateways and workers
            </Link>
            <br />
            <a
              href="https://github.com/1by3/nebula/releases/tag/v0.1.0-beta.2"
              className="mt-4 inline-flex items-center gap-1 font-medium text-fd-primary"
            >
              0.1.0-beta.2 release <ExternalLink className="size-3.5" />
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
