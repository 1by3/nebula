import type { BaseLayoutProps } from 'fumadocs-ui/layouts/shared';
import { BookOpen, TerminalSquare, Braces } from 'lucide-react';
import { GithubInfo } from 'fumadocs-ui/components/github-info';
import { appName, repo } from './shared';

export function baseOptions(): BaseLayoutProps {
  return {
    nav: {
      title: (
        <span className="inline-flex items-center gap-2 font-semibold">
          <NebulaMark className="size-5" />
          {appName}
        </span>
      ),
      url: '/',
    },
    links: [
      { icon: <BookOpen />, text: 'Docs', url: '/docs', active: 'nested-url' },
      { icon: <TerminalSquare />, text: 'CLI', url: '/docs/cli', active: 'nested-url' },
      { icon: <Braces />, text: 'API', url: '/docs/reference', active: 'nested-url' },
      { type: 'custom', children: <GithubInfo owner={repo.owner} repo={repo.name} />, secondary: true },
    ],
  };
}

/** Four containers, one of them lit: the mesh in one glyph. */
export function NebulaMark({ className }: { className?: string }) {
  return (
    <svg viewBox="0 0 24 24" fill="none" className={className} aria-hidden>
      <rect x="2" y="2" width="9" height="9" rx="2" className="fill-fd-primary" />
      <rect x="13" y="2" width="9" height="9" rx="2" className="fill-fd-primary/40" />
      <rect x="2" y="13" width="9" height="9" rx="2" className="fill-fd-primary/40" />
      <rect x="13" y="13" width="9" height="9" rx="2" className="fill-fd-primary/70" />
    </svg>
  );
}
