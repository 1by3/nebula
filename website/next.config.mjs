import { createMDX } from 'fumadocs-mdx/next';

const withMDX = createMDX();

// The CLI installers are served from the repository on GitHub. install.nebula.1by3.co and
// windows.nebula.1by3.co proxy to them (a proxy, not a redirect: `curl -sSf … | sh` does not follow
// redirects), and the docs host exposes them at /install.sh and /install.ps1.
const scripts = 'https://raw.githubusercontent.com/1by3/nebula/main/cli/install';

/** @type {import('next').NextConfig} */
const config = {
  reactStrictMode: true,
  // PostHog's API paths end in a trailing slash; keep Next from redirecting them.
  skipTrailingSlashRedirect: true,
  async rewrites() {
    return {
      beforeFiles: [
        { source: '/', has: [{ type: 'host', value: 'install.nebula.1by3.co' }], destination: `${scripts}/install.sh` },
        { source: '/', has: [{ type: 'host', value: 'windows.nebula.1by3.co' }], destination: `${scripts}/install.ps1` },
        { source: '/install.sh', destination: `${scripts}/install.sh` },
        { source: '/install.ps1', destination: `${scripts}/install.ps1` },
      ],
      // PostHog reverse proxy, used by instrumentation-client.ts.
      afterFiles: [
        { source: '/ingest/static/:path*', destination: 'https://us-assets.i.posthog.com/static/:path*' },
        { source: '/ingest/array/:path*', destination: 'https://us-assets.i.posthog.com/array/:path*' },
        { source: '/ingest/:path*', destination: 'https://us.i.posthog.com/:path*' },
      ],
    };
  },
};

export default withMDX(config);
