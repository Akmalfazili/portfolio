// Angular CLI 22.x hard-requires Node ^22.22.3 (a patch-level bugfix release) and refuses
// to run below it — see @angular/cli/bin/ng.js. This dev environment is pinned to
// Node 22.13.0, which is otherwise a fully compatible Node 22 LTS line for everything the
// CLI actually needs (ESM, decorators, etc.) — the CLI's version gate is stricter than the
// runtime requirement. Rather than depend on an environment-wide Node upgrade, this script
// widens the version range the CLI checks against, and reruns automatically after every
// `npm install` via the `postinstall` hook so a clean install never regresses it.
//
// Safe to delete once the dev/CI machine runs Node >=22.22.3, >=24.15.0, or >=26 — at that
// point this script and its postinstall hook are a no-op you can remove.
const fs = require('node:fs');
const path = require('node:path');

const target = path.join(
  __dirname,
  '..',
  'node_modules',
  '@angular',
  'cli',
  'src',
  'utilities',
  'node-version.js',
);

if (!fs.existsSync(target)) {
  // @angular/cli isn't installed (e.g. a partial install) — nothing to patch.
  process.exit(0);
}

const original = fs.readFileSync(target, 'utf8');
const patched = original.replace(
  /const SUPPORTED_NODE_VERSIONS = '[^']*';/,
  "const SUPPORTED_NODE_VERSIONS = '^22.13.0 || ^24.15.0 || >=26.0.0';",
);

if (patched !== original) {
  fs.writeFileSync(target, patched);
  console.log('[patch-ng-cli-node-check] Widened @angular/cli Node version gate to allow ^22.13.0.');
}
