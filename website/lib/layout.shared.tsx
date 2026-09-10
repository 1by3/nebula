import type { BaseLayoutProps } from 'fumadocs-ui/layouts/shared';
import { BookOpen, GitBranch, TerminalSquare, Braces } from 'lucide-react';
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
      { type: 'icon', icon: <GitBranch />, text: 'Source', label: 'Source repository', url: repo.url, external: true },
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
