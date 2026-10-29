// Catalog index check: every address of the project's own repositories in catalog/index.json uses one spelling.
// The host compares scheme, host and owner/repo without regard to case (CatalogUrl), but new entries must be written like this,
// so the index stays one form and a reviewer sees a different spelling as a mistake or a look-alike. Mirrors CatalogUrl.NonCanonicalReason.
import { readFileSync, readdirSync } from 'node:fs';

const CANONICAL = ['kewe87/CouchHUD-Catalog', 'kewe87/CouchHUD', 'kewe87/CaseHUD'];
const GITHUB = ['github.com', 'raw.githubusercontent.com'];

const text = readFileSync(new URL('../catalog/index.json', import.meta.url), 'utf8');
const bad = [];
for (const m of text.matchAll(/"(https?:\/\/[^"]+)"/g)) {
  const url = m[1];
  if (!url.startsWith('https://')) { bad.push(`${url}: scheme must be lower-case https://`); continue; }
  let u;
  try { u = new URL(url); } catch { bad.push(`${url}: not an address`); continue; }
  const written = url.slice(8).split('/')[0];
  if (written !== written.toLowerCase()) bad.push(`${url}: host not in lower case`);
  if (!GITHUB.includes(u.hostname)) continue;
  const [, owner, repo] = u.pathname.split('/');
  const spelled = `${owner}/${repo}`;
  const canonical = CANONICAL.find((r) => r.toLowerCase() === spelled.toLowerCase());
  if (canonical && canonical !== spelled) bad.push(`${url}: write '${canonical}', not '${spelled}'`);
}
// Plugin SDK: an entry (and a statement) may only ask for a version this build implements; a newer one is not installable here (the host
// hides it as "needs a newer CouchHUD"), so a typo such as "sdk": 40 must not reach the index.
const current = Number(/public const int Current = (\d+);/.exec(readFileSync(new URL('../src/CouchHud.Sdk/Plugins.cs', import.meta.url), 'utf8'))?.[1]);
if (!Number.isInteger(current)) bad.push('SdkVersion.Current not found in src/CouchHud.Sdk/Plugins.cs');
const checkSdk = (where, sdk) => {
  if (sdk === undefined) return;
  if (!Number.isInteger(sdk) || sdk < 1) bad.push(`${where}: "sdk" must be a whole number of at least 1`);
  else if (sdk > current) bad.push(`${where}: "sdk": ${sdk} is newer than SdkVersion.Current (${current})`);
};
for (const e of JSON.parse(text).entries ?? []) checkSdk(`index entry ${e.id}`, e.sdk);
try {
  const dir = new URL('../catalog/statements/', import.meta.url);
  for (const f of readdirSync(dir).filter((n) => n.endsWith('.json'))) checkSdk(`statement ${f}`, JSON.parse(readFileSync(new URL(f, dir), 'utf8')).sdk);
} catch { /* no statements folder */ }

if (bad.length) {
  console.error('catalog/index.json problems:\n  ' + bad.join('\n  '));
  process.exit(1);
}
console.log('catalog/index.json: addresses canonical');
