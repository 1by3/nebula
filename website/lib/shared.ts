export const appName = 'Nebula';
export const tagline = 'Dynamically meshed multiplayer servers for Unity';
export const docsRoute = '/docs';
export const docsImageRoute = '/og/docs';
export const docsContentRoute = '/llms.mdx/docs';

/** Where the sources live. The site links "view source" and "edit this page" here. */
export const repo = {
  url: 'https://gitea.c11.li/1by3/nebula',
  branch: 'main',
  /** Path of the docs site inside the repository. */
  siteDir: 'website',
};

export function sourceFileUrl(pathInRepo: string): string {
  return `${repo.url}/src/branch/${repo.branch}/${pathInRepo}`;
}

/** Hosted installer one-liners (the scripts are cli/install/install.ps1 and install.sh). */
export const installers = {
  windows: 'iwr https://windows.nebula.1by3.co -useb | iex',
  unix: 'curl -sSf https://install.nebula.1by3.co | sh',
};
