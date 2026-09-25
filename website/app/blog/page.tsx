import type { Metadata } from 'next';
import Link from 'next/link';
import { ArrowRight } from 'lucide-react';

export const metadata: Metadata = {
  title: 'Blog',
  description: 'Progress updates and release notes from the Nebula team.',
};

const posts = [
  {
    href: '/blog/2026-09-24-beta',
    title: 'Nebula 0.1.0-beta.0',
    description:
      'The first beta lets you play without a build, keep a behavior’s sync state private, and save changes to procedurally placed objects.',
    date: 'September 24, 2026',
    dateTime: '2026-09-24',
  },
  {
    href: '/blog/2026-09-24-progress-update',
    title: 'Nebula progress update: September 24, 2026',
    description:
      'Three alpha releases add safer upgrades, worlds you activate by key, containers in a tree with their own physics, and cargo that rides in moving ships.',
    date: 'September 24, 2026',
    dateTime: '2026-09-24',
  },
  {
    href: '/blog/2026-09-21-interest-management',
    title: 'Interest management keeps large worlds local',
    description:
      'Nebula now sends each player, gateway, and worker only the entity traffic it needs, with policies for cameras, spectators, fog of war, and carried entities.',
    date: 'September 21, 2026',
    dateTime: '2026-09-21',
  },
  {
    href: '/blog/2026-09-18-progress-update',
    title: 'Nebula progress update: September 18, 2026',
    description:
      'Five alpha releases add Nebula Cloud deploys, multiple gateways, one connection per player, a 3D world map, and confirmed saves.',
    date: 'September 18, 2026',
    dateTime: '2026-09-18',
  },
  {
    href: '/blog/2026-09-15-progress-update',
    title: 'Nebula progress update: September 15, 2026',
    description:
      'Six alpha releases add adaptive worker scaling, browser play, player identity, and private spaces.',
    date: 'September 15, 2026',
    dateTime: '2026-09-15',
  },
];

export default function BlogPage() {
  return (
    <main className="mx-auto w-full max-w-4xl px-6 py-16 sm:py-20">
      <header className="max-w-2xl">
        <p className="text-sm font-medium text-fd-primary">Nebula blog</p>
        <h1 className="mt-2 text-4xl font-bold tracking-tight sm:text-5xl">
          Progress and releases
        </h1>
        <p className="mt-4 text-lg text-fd-muted-foreground">
          Follow changes that help game developers build, run, and operate
          distributed Unity worlds.
        </p>
      </header>

      <div className="mt-12 border-t border-fd-border">
        {posts.map((post) => (
          <article key={post.href} className="border-b border-fd-border py-8">
            <time
              dateTime={post.dateTime}
              className="text-sm text-fd-muted-foreground"
            >
              {post.date}
            </time>
            <h2 className="mt-2 text-2xl font-semibold">
              <Link href={post.href} className="hover:text-fd-primary">
                {post.title}
              </Link>
            </h2>
            <p className="mt-3 text-fd-muted-foreground">
              {post.description}
            </p>
            <Link
              href={post.href}
              className="mt-4 inline-flex items-center gap-1 text-sm font-medium text-fd-primary"
            >
              Read the update <ArrowRight className="size-4" />
            </Link>
          </article>
        ))}
      </div>
    </main>
  );
}
