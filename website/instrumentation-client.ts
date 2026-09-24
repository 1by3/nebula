import posthog from 'posthog-js';

// Events go through the /ingest rewrite in next.config.mjs so ad blockers do not drop them.
posthog.init(process.env.NEXT_PUBLIC_POSTHOG_KEY ?? 'phc_kAjxGLGauGnk8tochQiYZWA6wRSNsvZftjL2jwQcEp7D', {
  api_host: '/ingest',
  ui_host: 'https://us.posthog.com',
  defaults: '2025-05-24',
});
