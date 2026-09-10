// Generates the CLI reference (content/docs/cli) from the CLI itself: `nebula help --json` describes every
// command, its options and examples straight from the command table in cli/Nebula.Cli, so the pages can never
// drift from what the binary accepts.
//
//   node scripts/gen-cli.mjs            builds and runs the CLI from ../cli/Nebula.Cli
//   NEBULA_BIN=nebula node scripts/gen-cli.mjs   uses an installed binary instead

import { execFileSync } from 'node:child_process';
import { mkdirSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const site = resolve(here, '..');
const repo = resolve(site, '..');
const outDir = join(site, 'content', 'docs', 'cli');

function run(cmd, args) {
  return execFileSync(cmd, args, { encoding: 'utf8', env: { ...process.env, NO_COLOR: '1' }, maxBuffer: 16 * 1024 * 1024 });
}

let json;
if (process.env.NEBULA_BIN) {
  json = run(process.env.NEBULA_BIN, ['help', '--json']);
} else {
  const project = join(repo, 'cli', 'Nebula.Cli');
  run('dotnet', ['build', project, '-nologo', '-v', 'q']);
  json = run('dotnet', ['run', '--no-build', '--project', project, '--', 'help', '--json']);
}
const doc = JSON.parse(json);

const escape = (s) => s.replace(/[<>{}*_\\|]/g, (c) => '\\' + c);
const code = (s) => '`' + s + '`';

function optionRows(options) {
  const lines = ['| Option | Description |', '| --- | --- |'];
  for (const o of options) {
    let flag = '--' + o.name + (o.hasValue ? ' <' + (o.valueName ?? 'value') + '>' : '');
    if (o.short) flag = '-' + o.short + ', ' + flag;
    lines.push(`| ${code(flag)} | ${escape(o.help)} |`);
  }
  return lines.join('\n');
}

rmSync(outDir, { recursive: true, force: true });
mkdirSync(outDir, { recursive: true });

const groups = [
  { title: 'Getting started', commands: ['setup', 'init'] },
  { title: 'Running locally', commands: ['build', 'start', 'stop', 'status', 'logs'] },
  { title: 'Deploying', commands: ['config', 'deploy', 'destroy'] },
  { title: 'Other', commands: ['version', 'help'] },
];
const known = new Set(groups.flatMap((g) => g.commands));
const rest = doc.commands.filter((c) => !known.has(c.name)).map((c) => c.name);
if (rest.length) groups.push({ title: 'More', commands: rest });

// One page per command.
for (const c of doc.commands) {
  const parts = [];
  parts.push('---');
  parts.push(`title: "nebula ${c.name}"`);
  parts.push(`description: "${c.summary.replace(/"/g, '\\"')}"`);
  parts.push('---');
  parts.push('');
  parts.push(escape(c.summary) + '.');
  parts.push('');
  parts.push('```bash');
  parts.push(`nebula ${c.name}${c.usage ? ' ' + c.usage : ''}`.trim());
  parts.push('```');
  parts.push('');
  if (c.aliases?.length) {
    parts.push(`Aliases: ${c.aliases.map(code).join(', ')}`);
    parts.push('');
  }
  if (c.details) {
    // Details are pre-formatted help text (aligned columns); keep them verbatim.
    parts.push('```text');
    parts.push(c.details);
    parts.push('```');
    parts.push('');
  }
  if (c.options?.length) {
    parts.push('## Options');
    parts.push('');
    parts.push(optionRows(c.options));
    parts.push('');
  }
  parts.push('## Global options');
  parts.push('');
  parts.push('These work with every command and may appear before or after it.');
  parts.push('');
  parts.push(optionRows(doc.globalOptions));
  parts.push('');
  if (c.examples?.length) {
    parts.push('## Examples');
    parts.push('');
    parts.push('```bash');
    for (const e of c.examples) parts.push(e);
    parts.push('```');
    parts.push('');
  }
  writeFileSync(join(outDir, c.name + '.mdx'), parts.join('\n'));
}

// Index page.
const index = [];
index.push('---');
index.push('title: CLI reference');
index.push(`description: Every nebula command, generated from the CLI (version ${doc.version}).`);
index.push('---');
index.push('');
index.push(
  'The `nebula` command-line tool installs Nebula into a Unity project, runs the mesh locally and deploys it. This reference is generated from the CLI itself (`nebula help --json`) by `website/scripts/gen-cli.mjs`; `nebula --help` and `nebula <command> --help` print the same information in the terminal. See [Install the CLI](/docs/getting-started/install) for how to get it.',
);
index.push('');
index.push('```bash');
index.push('nebula <command> [options]');
index.push('```');
index.push('');
for (const g of groups) {
  index.push(`## ${g.title}`);
  index.push('');
  index.push('| Command | Summary |');
  index.push('| --- | --- |');
  for (const name of g.commands) {
    const c = doc.commands.find((x) => x.name === name);
    if (!c) continue;
    index.push(`| [nebula ${c.name}](/docs/cli/${c.name}) | ${escape(c.summary)} |`);
  }
  index.push('');
}
index.push('## Global options');
index.push('');
index.push(optionRows(doc.globalOptions));
index.push('');
writeFileSync(join(outDir, 'index.mdx'), index.join('\n'));

const pages = ['index'];
for (const g of groups) {
  pages.push(`---${g.title}---`);
  for (const name of g.commands) if (doc.commands.some((c) => c.name === name)) pages.push(name);
}
writeFileSync(
  join(outDir, 'meta.json'),
  JSON.stringify({ title: 'CLI reference', description: 'Generated from nebula help --json', root: true, pages }, null, 2) + '\n',
);
console.log(`gen-cli: ${doc.commands.length} commands (nebula ${doc.version}) -> ${outDir}`);
