'use strict';

// Serves ui/ with the mock API, for UI work without Windows.
//
// CATALOG_SKINS=<folder>: also serves the skins in that folder (one sub folder per skin, with skin.json and skin.css) the way the host
// serves installed skins: as "user:<folder>" skins, the stylesheet cleaned of external addresses like SkinCss.Sanitize does.
// It is how a skin from the catalog is looked at before it is published (scripts/ui-shot.mjs, step skin:user:<folder>).
const http = require('http');
const fs = require('fs');
const path = require('path');

const root = path.join(__dirname, '..', 'ui');
const port = Number(process.env.PORT) || 5173;
const skinRoot = process.env.CATALOG_SKINS ? path.resolve(process.env.CATALOG_SKINS) : null;
const types = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.svg': 'image/svg+xml', '.png': 'image/png', '.json': 'application/json' };

// The same pattern as SkinCss.External in Core.
const EXTERNAL = /(?<![A-Za-z0-9_\-])(?:https?:)?\/\/(?!(?:local|art|app)\.couchhud(?:[\/:"')\s]|$)|www\.w3\.org\/)[^\s"')]+/gi;
const sanitize = (css) => css.replace(EXTERNAL, 'about:blank');

function serveSkins(urlPath, res) {
  const rest = urlPath.slice('/__catalog-skins/'.length);
  if (!skinRoot) return res.writeHead(404).end('not found');
  if (rest === 'index.json') {
    const skins = fs.readdirSync(skinRoot, { withFileTypes: true })
      .filter((d) => d.isDirectory() && fs.existsSync(path.join(skinRoot, d.name, 'skin.json')) && fs.existsSync(path.join(skinRoot, d.name, 'skin.css')))
      .map((d) => ({ folder: d.name, manifest: JSON.parse(fs.readFileSync(path.join(skinRoot, d.name, 'skin.json'), 'utf8')) }));
    return res.writeHead(200, { 'Content-Type': 'application/json' }).end(JSON.stringify(skins));
  }
  const file = path.join(skinRoot, rest);
  if (!file.startsWith(skinRoot + path.sep) || !fs.existsSync(file)) return res.writeHead(404).end('not found');
  const data = fs.readFileSync(file);
  if (path.basename(file) === 'skin.css') {
    const text = data.toString('utf8');
    const clean = sanitize(text);
    if (clean !== text) console.log(`sanitizer changed ${rest}`);
    return res.writeHead(200, { 'Content-Type': 'text/css' }).end(clean);
  }
  return res.writeHead(200, { 'Content-Type': types[path.extname(file)] || 'application/octet-stream' }).end(data);
}

http.createServer((req, res) => {
  const urlPath = decodeURIComponent(new URL(req.url, 'http://x').pathname);
  if (urlPath.startsWith('/__catalog-skins/')) return serveSkins(urlPath, res);
  const file = path.join(root, urlPath === '/' ? 'index.html' : urlPath);
  if (!file.startsWith(root)) {
    res.writeHead(403).end();
    return;
  }
  fs.readFile(file, (err, data) => {
    if (err) {
      res.writeHead(404).end('not found');
      return;
    }
    res.writeHead(200, { 'Content-Type': types[path.extname(file)] || 'application/octet-stream' }).end(data);
  });
}).listen(port, () => console.log(`CouchHud preview: http://localhost:${port}`));
