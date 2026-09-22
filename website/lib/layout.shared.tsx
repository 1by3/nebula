import type { BaseLayoutProps } from 'fumadocs-ui/layouts/shared';
import { BookOpen, TerminalSquare, Braces, Cloud, Newspaper } from 'lucide-react';
import { GithubInfo } from '@/components/github-info';
import { cn } from './cn';
import { appName, repo } from './shared';

export function baseOptions(): BaseLayoutProps {
  return {
    nav: {
      title: (
        <span className="inline-flex items-center gap-2 font-semibold">
          <NebulaLogo className="size-7" />
          {appName}
        </span>
      ),
      url: '/',
    },
    links: [
      { icon: <Newspaper />, text: 'Blog', url: '/blog', active: 'nested-url' },
      { icon: <BookOpen />, text: 'Docs', url: '/docs', active: 'nested-url' },
      { icon: <TerminalSquare />, text: 'CLI', url: '/docs/cli', active: 'nested-url' },
      { icon: <Braces />, text: 'API', url: '/docs/reference', active: 'nested-url' },
      { icon: <Cloud />, text: 'Nebula Cloud', url: 'https://cloud.nebula.1by3.co', external: true },
      { type: 'custom', children: <GithubInfo owner={repo.owner} repo={repo.name} />, secondary: true },
    ],
  };
}

export function NebulaLogo({ className }: { className?: string }) {
  return <span className={cn('nebula-logo', className)} aria-hidden="true" />;
}
