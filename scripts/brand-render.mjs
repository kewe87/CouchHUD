// Renders the branding from its SVG sources with headless Edge (no extra packages):
//   docs/logo.png (512), docs/banner.png, docs/social-preview.png (needs the docs/screenshot-*.en.png), assets/branding/png/icon-*.png
//   and src/CouchHud.Shell/couchhud.ico (PNG frames; 16-32 px from the simplified icon-small.svg), the setup pictures installer/wizard*.bmp.
//
//   node scripts/brand-render.mjs            Env: EDGE (path to msedge.exe)
import { spawn } from 'node:child_process';
import { mkdirSync, writeFileSync, readFileSync, mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import http from 'node:http';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const at = (...p) => join(root, ...p);
const edge = process.env.EDGE || 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe';
const port = 9341;

const ICON_SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256];
const SMALL = 32; // up to this size the simplified icon stays sharp
const jobs = [
  { src: 'docs/logo.svg', out: 'docs/logo.png', w: 512, h: 512 },
  { src: 'docs/banner.svg', out: 'docs/banner.png', w: 1520, h: 400 },
  // Opened directly (not as <img>) so it may load the screenshots it shows.
  { src: 'docs/social-preview.svg', out: 'docs/social-preview.png', w: 1280, h: 640, opaque: true, direct: true },
  { src: 'docs/logo.svg', out: 'assets/branding/png/icon-512.png', w: 512, h: 512 },
  ...ICON_SIZES.map((s) => ({ src: s <= SMALL ? 'assets/branding/icon-small.svg' : 'docs/logo.svg', out: `assets/branding/png/icon-${s}.png`, w: s, h: s }))
];

const profile = mkdtempSync(join(tmpdir(), 'couchhud-brand-'));
const browser = spawn(edge, [`--remote-debugging-port=${port}`, '--headless=new', '--mute-audio', '--disable-gpu', '--disable-extensions', '--force-color-profile=srgb',
  `--user-data-dir=${profile}`, '--hide-scrollbars', 'about:blank'], { stdio: 'ignore' });
process.on('exit', () => browser.kill());

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const getJson = (url) => new Promise((res, rej) => http.get(url, (r) => { let d = ''; r.on('data', (c) => (d += c)); r.on('end', () => { try { res(JSON.parse(d)); } catch (e) { rej(e); } }); }).on('error', rej));
let wsUrl;
for (let i = 0; i < 60 && !wsUrl; i++) {
  try { wsUrl = (await getJson(`http://127.0.0.1:${port}/json`)).find((t) => t.type === 'page')?.webSocketDebuggerUrl; } catch {}
  if (!wsUrl) await sleep(250);
}
if (!wsUrl) throw new Error('Edge not reachable');
const ws = new WebSocket(wsUrl);
await new Promise((r) => (ws.onopen = r));
let id = 0;
const waiting = new Map();
ws.onmessage = (m) => { const d = JSON.parse(m.data); if (d.id && waiting.has(d.id)) { waiting.get(d.id)(d); waiting.delete(d.id); } };
const send = (method, params = {}) => new Promise((res) => { waiting.set(++id, res); ws.send(JSON.stringify({ id, method, params })); });

await send('Page.enable');
const tmp = mkdtempSync(join(tmpdir(), 'couchhud-brand-page-'));
for (const job of jobs) {
  // The SVG is drawn as an <img> at the target size, so the browser scales it (crisp, no CSS from outside).
  const page = job.direct ? at(job.src) : join(tmp, 'page.html');
  if (!job.direct) writeFileSync(page, `<!doctype html><html><body style="margin:0;background:transparent"><img src="${pathToFileURL(at(job.src)).href}" style="display:block;width:${job.w}px;height:${job.h}px"></body></html>`);
  await send('Emulation.setDeviceMetricsOverride', { width: job.w, height: job.h, deviceScaleFactor: 1, mobile: false });
  await send('Emulation.setDefaultBackgroundColorOverride', { color: { r: 0, g: 0, b: 0, a: job.opaque ? 1 : 0 } });
  await send('Page.navigate', { url: pathToFileURL(page).href });
  await sleep(700);
  const r = await send('Page.captureScreenshot', { format: 'png', clip: { x: 0, y: 0, width: job.w, height: job.h, scale: 1 } });
  mkdirSync(dirname(at(job.out)), { recursive: true });
  writeFileSync(at(job.out), Buffer.from(r.result.data, 'base64'));
  console.log('png', job.out);
}

// ICO with PNG frames (Windows Vista and later), smallest first.
const frames = ICON_SIZES.map((s) => ({ s, data: readFileSync(at(`assets/branding/png/icon-${s}.png`)) }));
const header = Buffer.alloc(6 + frames.length * 16);
header.writeUInt16LE(0, 0); header.writeUInt16LE(1, 2); header.writeUInt16LE(frames.length, 4);
let offset = header.length;
frames.forEach((f, i) => {
  const o = 6 + i * 16;
  header[o] = f.s >= 256 ? 0 : f.s; header[o + 1] = f.s >= 256 ? 0 : f.s;
  header.writeUInt16LE(1, o + 4); header.writeUInt16LE(32, o + 6);
  header.writeUInt32LE(f.data.length, o + 8); header.writeUInt32LE(offset, o + 12);
  offset += f.data.length;
});
writeFileSync(at('src/CouchHud.Shell/couchhud.ico'), Buffer.concat([header, ...frames.map((f) => f.data)]));
console.log('ico', 'src/CouchHud.Shell/couchhud.ico', ICON_SIZES.join(', '));

// Setup pictures (Inno Setup wants 24-bit BMP, 100 % and 200 %): the side picture of the welcome and last page and the small one
// at the top right. The logo goes in as a nested <svg> (an SVG drawn as <img> loads nothing from outside).
const logo = readFileSync(at('docs/logo.svg'), 'utf8').replace(/<!--[\s\S]*?-->/g, '');
const logoAt = (x, y, size) => logo.replace(/<svg[^>]*>/, `<svg xmlns="http://www.w3.org/2000/svg" x="${x}" y="${y}" width="${size}" height="${size}" viewBox="0 0 512 512">`);
const wizardSvg = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 164 314" width="164" height="314">
  <defs>
    <linearGradient id="bg" x1="0" y1="0" x2="0.4" y2="1"><stop offset="0" stop-color="#1d1f4a"/><stop offset="0.6" stop-color="#0e1024"/><stop offset="1" stop-color="#0b0d16"/></linearGradient>
    <radialGradient id="halo" cx="0.5" cy="0.3" r="0.55"><stop offset="0" stop-color="#6a4cff" stop-opacity=".45"/><stop offset="1" stop-color="#6a4cff" stop-opacity="0"/></radialGradient>
    <linearGradient id="hud" x1="0" y1="0" x2="1" y2="0"><stop offset="0" stop-color="#3d8bff"/><stop offset="1" stop-color="#c04dff"/></linearGradient>
  </defs>
  <rect width="164" height="314" fill="url(#bg)"/>
  <rect width="164" height="314" fill="url(#halo)"/>
  ${logoAt(27, 52, 110)}
  <text x="82" y="200" text-anchor="middle" font-family="Segoe UI, Arial, sans-serif" font-weight="700" font-size="27" fill="#fff">Couch<tspan fill="url(#hud)">HUD</tspan></text>
</svg>`;
const smallSvg = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 55 55" width="55" height="55"><rect width="55" height="55" fill="#fff"/>${logoAt(2, 2, 51)}</svg>`;
const bmpJobs = [
  { svg: wizardSvg, out: 'installer/wizard-1.bmp', w: 164, h: 314 },
  { svg: wizardSvg, out: 'installer/wizard-2.bmp', w: 328, h: 628 },
  { svg: smallSvg, out: 'installer/wizard-small-1.bmp', w: 55, h: 55 },
  { svg: smallSvg, out: 'installer/wizard-small-2.bmp', w: 110, h: 110 }
];
for (const job of bmpJobs) {
  // Drawn into a canvas on a page; the pixels come back as Base64 RGBA.
  const svgUrl = 'data:image/svg+xml;base64,' + Buffer.from(job.svg).toString('base64');
  writeFileSync(join(tmp, 'bmp.html'), '<!doctype html><html><body></body></html>');
  await send('Page.navigate', { url: pathToFileURL(join(tmp, 'bmp.html')).href });
  await sleep(300);
  const r = await send('Runtime.evaluate', {
    awaitPromise: true,
    returnByValue: true,
    expression: `new Promise((res, rej) => { const img = new Image(); img.onerror = () => rej('svg'); img.onload = () => {
      const c = document.createElement('canvas'); c.width = ${job.w}; c.height = ${job.h}; const g = c.getContext('2d');
      g.fillStyle = '#fff'; g.fillRect(0, 0, c.width, c.height); g.drawImage(img, 0, 0, c.width, c.height);
      const d = g.getImageData(0, 0, c.width, c.height).data; let s = ''; for (let i = 0; i < d.length; i += 0x8000) s += String.fromCharCode(...d.subarray(i, i + 0x8000));
      res(btoa(s)); }; img.src = ${JSON.stringify(svgUrl)}; })`
  });
  if (r.result?.exceptionDetails || typeof r.result?.result?.value !== 'string') throw new Error(`bmp ${job.out}: ${JSON.stringify(r.result?.exceptionDetails ?? r.result)}`);
  const rgba = Buffer.from(r.result.result.value, 'base64');
  // 24-bit BMP, rows bottom up, each padded to 4 bytes.
  const row = Math.ceil((job.w * 3) / 4) * 4;
  const bmp = Buffer.alloc(54 + row * job.h);
  bmp.write('BM', 0); bmp.writeUInt32LE(bmp.length, 2); bmp.writeUInt32LE(54, 10);
  bmp.writeUInt32LE(40, 14); bmp.writeInt32LE(job.w, 18); bmp.writeInt32LE(job.h, 22); bmp.writeUInt16LE(1, 26); bmp.writeUInt16LE(24, 28);
  bmp.writeUInt32LE(row * job.h, 34); bmp.writeInt32LE(3780, 38); bmp.writeInt32LE(3780, 42);
  for (let y = 0; y < job.h; y++) {
    for (let x = 0; x < job.w; x++) {
      const s = (y * job.w + x) * 4;
      const d = 54 + (job.h - 1 - y) * row + x * 3;
      bmp[d] = rgba[s + 2]; bmp[d + 1] = rgba[s + 1]; bmp[d + 2] = rgba[s];
    }
  }
  writeFileSync(at(job.out), bmp);
  console.log('bmp', job.out);
}
browser.kill();
process.exit(0);
