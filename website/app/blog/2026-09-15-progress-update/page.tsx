import type { Metadata } from 'next';
import Link from 'next/link';
import { ArrowLeft, ExternalLink } from 'lucide-react';

const title = 'Nebula progress update: September 15, 2026';
const description =
  'Six alpha releases add adaptive worker scaling, browser play, player identity, and private spaces.';

export const metadata: Metadata = {
  title,
  description,
};

const releases = [
  ['0.1.0-alpha.17', 'Adaptive worker scaling and an idle pool'],
  ['0.1.0-alpha.18', 'Unity Web clients over WebRTC'],
  ['0.1.0-alpha.19', 'Deployment settings for minimum and maximum worker counts'],
  ['0.1.0-alpha.20', 'HTTPS for hosted web clients'],
  ['0.1.0-alpha.21', 'Stable player identity and authentication'],
  ['0.1.0-alpha.22', 'Private instances and prepared crossings'],
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
            dateTime="2026-09-15"
            className="mt-4 block text-sm text-fd-muted-foreground"
          >
            September 15, 2026
          </time>
          <p className="mt-6 text-xl leading-8 text-fd-muted-foreground">
            Today&apos;s work makes Nebula easier to run at changing scale,
            opens game worlds to browser players, gives returning players a
            stable identity, and adds private spaces within a shared world.
          </p>
        </header>

        <div className="space-y-12 py-10 text-base leading-7">
          <section>
            <h2 className="text-2xl font-semibold">Match capacity to demand</h2>
            <p className="mt-4 text-fd-muted-foreground">
              Nebula can now add and remove workers as simulation load changes.
              A worker is a headless Unity process that simulates part of the
              game world. Developers can set a minimum and maximum worker count
              for local and cloud deployments instead of choosing one fixed
              size.
            </p>
            <p className="mt-4 text-fd-muted-foreground">
              A quiet world can scale down to zero workers and start one when a
              player arrives. Recently stopped cloud workers can wait in an idle
              pool for reuse, which shortens later scale-out time. New balancing
              hints and dashboard information help developers see why Nebula
              changed capacity or why a busy area could not be split further.
            </p>
            <GuideLink href="/docs/guides/orchestrator-and-dashboard#autoscale">
              Read about autoscaling
            </GuideLink>
          </section>

          <section>
            <h2 className="text-2xl font-semibold">Let players join from a browser</h2>
            <p className="mt-4 text-fd-muted-foreground">
              Unity Web builds can now connect to the same gateway as desktop
              clients. The gateway is the process that accepts player
              connections and routes game traffic. Browser clients use Web Real-Time
              Communication (WebRTC), while existing clients can continue to use
              User Datagram Protocol (UDP) connections.
            </p>
            <p className="mt-4 text-fd-muted-foreground">
              The command-line tool can build the web client, include it in a
              deployment, and print the local play address. Cloud deployments
              also serve the connection over HTTPS with automatically renewed
              certificates, so a game hosted on a secure web page can connect
              without mixed-content errors.
            </p>
            <GuideLink href="/docs/guides/web-builds">Build for the web</GuideLink>
          </section>

          <section>
            <h2 className="text-2xl font-semibold">Recognize returning players</h2>
            <p className="mt-4 text-fd-muted-foreground">
              Every player can now keep a stable identity across connections.
              Games can accept accounts from an OpenID Connect provider, or let
              the gateway issue an anonymous identity for players who do not
              sign in. Saved characters and other persistent state can follow
              the identity instead of a display name or temporary connection.
            </p>
            <p className="mt-4 text-fd-muted-foreground">
              The example game now restores a saved character by player
              identity and includes a diagnostic command for checking the active
              identity during development.
            </p>
            <GuideLink href="/docs/guides/authentication">
              Set up authentication and player identity
            </GuideLink>
          </section>

          <section>
            <h2 className="text-2xl font-semibold">Add private spaces to a shared world</h2>
            <p className="mt-4 text-fd-muted-foreground">
              An instance is an isolated copy of one or more areas. Developers
              can now use instances for personal homes, party areas, training
              rooms, or separate mission copies without disconnecting players
              from the shared world.
            </p>
            <p className="mt-4 text-fd-muted-foreground">
              Nebula prepares the destination before a player crosses into it.
              Occupants can receive a bounded, observation-only view of nearby
              public activity, while public players and other private copies do
              not receive the instance&apos;s entities. Physics and gameplay
              queries stay within the intended copy. The example game includes
              a private-room scenario that exercises these boundaries.
            </p>
            <GuideLink href="/docs/guides/instancing">Create private instances</GuideLink>
          </section>

          <section>
            <h2 className="text-2xl font-semibold">Keep the examples current</h2>
            <p className="mt-4 text-fd-muted-foreground">
              The example game also received a browser build profile and
              browser-compatible graphics updates. Together with the identity
              and private-space examples, these changes provide working paths
              for testing the day&apos;s features in a game project.
            </p>
          </section>

          <section className="rounded-xl border border-fd-border bg-fd-card p-6">
            <h2 className="text-2xl font-semibold">Six releases shipped</h2>
            <p className="mt-3 text-fd-muted-foreground">
              Releases 0.1.0-alpha.17 through 0.1.0-alpha.22 cover the work in
              this update. The latest release changes the wire protocol, so
              rebuild clients, gateways, and workers together when upgrading.
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
