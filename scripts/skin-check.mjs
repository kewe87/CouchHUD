// Skin stylesheets must not hide tabs: node scripts/skin-check.mjs [dir ...]
// Each dir holds skin folders (one sub folder per skin with a skin.css), like the catalog's skins/ folder, or is a skin folder itself (a
// skin.css inside; scripts/catalog-entry.ps1 calls it so for the folder it packs). Without arguments the sibling checkout
// ../couchhud-catalog-src/skins is checked when it exists. The rule is in scripts/skin-rules.mjs; it is a lint for the catalog review, the
// runtime (ui/core/nav.js) is what enforces that tabs stay visible.
import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { tabProblems } from './skin-rules.mjs';

const here = fileURLToPath(new URL('.', import.meta.url));
const dirs = process.argv.slice(2).length ? process.argv.slice(2) : [join(here, '..', '..', 'couchhud-catalog-src', 'skins')];
const bad = [];
const files = [];
for (const dir of dirs) {
  if (!existsSync(dir)) continue;
  if (existsSync(join(dir, 'skin.css'))) {
    files.push(join(dir, 'skin.css'));
    continue;
  }
  for (const d of readdirSync(dir, { withFileTypes: true })) {
    const file = join(dir, d.name, 'skin.css');
    if (d.isDirectory() && existsSync(file)) files.push(file);
  }
}
for (const file of files) for (const p of tabProblems(readFileSync(file, 'utf8'))) bad.push(`${file}: ${p}`);
if (bad.length) {
  console.error(bad.join('\n'));
  console.error(`skins: ${bad.length} problem(s)`);
  process.exit(1);
}
// Nothing to look at is said out loud, not reported as a pass.
console.log(files.length ? `skins: tabs stay (${files.length} skins)` : 'skins: skipped (no catalog folder)');
