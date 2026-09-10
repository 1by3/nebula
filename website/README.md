# Nebula docs and marketing site

A [Fumadocs](https://fumadocs.dev) (Next.js) site: the landing page, the hand-written docs, and two generated
references.

```bash
npm install
npm run gen        # regenerate the API and CLI references from the sources (needs the .NET 10 SDK)
npm run dev        # http://localhost:3000
npm run build      # production build (static pages)
```

## Layout

| Path | What |
| --- | --- |
| `app/(home)/page.tsx` | The landing page. |
| `app/docs/` | The docs layout and page route (Fumadocs). |
| `content/docs/*.mdx` | Hand-written pages: getting started, concepts, guides, deploy. `meta.json` sets the sidebar order. |
| `content/docs/cli/` | **Generated** by `npm run gen:cli` from `nebula help --json`. Do not edit. |
| `content/docs/reference/` | **Generated** by `npm run gen:api` from the C# sources. Do not edit. |
| `tools/ApiGen/` | The reference generator: a syntax-only Roslyn tool that turns XML doc comments into MDX. |
| `scripts/gen-cli.mjs` | The CLI reference generator. |
| `lib/shared.ts` | Site name, repository URL, installer one-liners. |
| `components/mdx.tsx` | The MDX components available to pages (Callout, Cards, Tabs, Steps, Accordions, TypeTable, Files). |

## Generated content

The generated folders are committed so the site builds anywhere without .NET or the CLI. After changing public
types, XML doc comments or CLI commands, run `npm run gen` and commit the result.

- `gen:api` walks `Assets/Nebula/Runtime`, `Assets/Nebula/Editor` and the SpacetimeDB module, and writes one
  page per public type, grouped by folder, with signatures, doc comments (`<see cref>` becomes a link), Unity
  `[Tooltip]` text as a fallback description, and nested types on the parent's page.
- `gen:cli` builds and runs the CLI (`NEBULA_BIN=nebula` uses an installed one instead) and writes one page per
  command from its option table, plus an index.

## Writing pages

Frontmatter needs `title` and `description`. Link with absolute paths (`/docs/guides/rpcs`); reference pages are
at `/docs/reference/<group>/<type-kebab>` and CLI pages at `/docs/cli/<command>`. In prose, escape `<` and `{`
or put them in backticks. Shell examples go in `bash` or `powershell` fences, one command per line.
