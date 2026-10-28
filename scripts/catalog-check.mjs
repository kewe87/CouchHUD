// Catalog index check: every address of the project's own repositories in catalog/index.json uses one spelling.
// The host compares scheme, host and owner/repo without regard to case (CatalogUrl), but new entries must be written like this,
// so the index stays one form and a reviewer sees a different spelling as a mistake or a look-alike. Mirrors CatalogUrl.NonCanonicalReason.
import { readFileSync } from 'node:fs';

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
if (bad.length) {
  console.error('catalog/index.json uses non-canonical addresses:\n  ' + bad.join('\n  '));
  process.exit(1);
}
console.log('catalog/index.json: addresses canonical');
