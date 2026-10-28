// Drives the UI preview (mock API) in headless Edge via CDP: send keys, switch
// skins, take screenshots, check text contrast. For UI work and regression checks.
//
//   node scripts/ui-shot.mjs --out DIR [--size 1920x1080] [--dpr 1] [--port 5199] STEP...
//
// Steps:  skin:deepsea   key:ArrowDown   key:Enter   keys:ArrowDown*3   wait:300
//         (skin:user:<folder> shows a skin from the folder given with --catalog, see below)
//         mouse:x,y (move the mouse: shows the pointer)  shot:name  eval:<js>       contrast:label   text:abc (type into page)
// Keys: Arrow*, Enter=A, Escape=B, q/e=LB/RB, x, y, m=Start, Home=Guide.
// Options: --query lang=en (URL query of the preview page), --catalog DIR (a folder with skin folders, served like installed skins,
//          for skins from the catalog: skin:user:<folder>).
// Env: EDGE (path to msedge.exe).
import { spawn } from 'node:child_process';
import { mkdirSync, writeFileSync, mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import http from 'node:http';

const here = dirname(fileURLToPath(import.meta.url));
const args = process.argv.slice(2);
const opt = { out: '.', size: '1920x1080', dpr: '1', port: '5199', debug: '9333' };
const steps = [];
for (let i = 0; i < args.length; i++) {
  if (args[i].startsWith('--')) opt[args[i].slice(2)] = args[++i];
  else steps.push(args[i]);
}
const [W, H] = opt.size.split('x').map(Number);
mkdirSync(opt.out, { recursive: true });

const edge = process.env.EDGE || 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe';
const server = spawn(process.execPath, [join(here, 'preview-server.js')], { env: { ...process.env, PORT: opt.port, ...(opt.catalog ? { CATALOG_SKINS: opt.catalog } : {}) }, stdio: 'ignore' });
const profile = mkdtempSync(join(tmpdir(), 'couchhud-edge-'));
const browser = spawn(edge, [`--remote-debugging-port=${opt.debug}`, '--headless=new', '--mute-audio', '--disable-gpu', '--disable-extensions', '--force-color-profile=srgb', `--user-data-dir=${profile}`,
  `--window-size=${W},${H}`, '--hide-scrollbars', 'about:blank'], { stdio: 'ignore' });
const cleanup = () => { server.kill(); browser.kill(); };
process.on('exit', cleanup);

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const getJson = (url) => new Promise((res, rej) => http.get(url, (r) => { let d = ''; r.on('data', (c) => (d += c)); r.on('end', () => { try { res(JSON.parse(d)); } catch (e) { rej(e); } }); }).on('error', rej));

async function connect() {
  for (let i = 0; i < 60; i++) {
    try {
      const targets = await getJson(`http://127.0.0.1:${opt.debug}/json`);
      const page = targets.find((t) => t.type === 'page');
      if (page) return page.webSocketDebuggerUrl;
    } catch {}
    await sleep(250);
  }
  throw new Error('Edge nicht erreichbar');
}

const ws = new WebSocket(await connect());
await new Promise((r) => (ws.onopen = r));
let id = 0;
const waiting = new Map();
ws.onmessage = (m) => { const d = JSON.parse(m.data); if (d.id && waiting.has(d.id)) { waiting.get(d.id)(d); waiting.delete(d.id); } };
const send = (method, params = {}) => new Promise((res) => { waiting.set(++id, res); ws.send(JSON.stringify({ id, method, params })); });
const evaluate = async (expression) => {
  const r = await send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
  if (r.result?.exceptionDetails) throw new Error(JSON.stringify(r.result.exceptionDetails.exception?.description ?? r.result.exceptionDetails));
  return r.result?.result?.value;
};

const CODES = { Enter: 'Enter', Escape: 'Escape', Home: 'Home', Backspace: 'Backspace', ' ': 'Space' };
const VK = { Enter: 13, Escape: 27, Home: 36, Backspace: 8, ArrowLeft: 37, ArrowUp: 38, ArrowRight: 39, ArrowDown: 40, ' ': 32 };
async function key(k) {
  const text = k.length === 1 ? k : k === 'Enter' ? '\r' : undefined;
  const base = { key: k, code: CODES[k] ?? (k.length === 1 ? `Key${k.toUpperCase()}` : k), windowsVirtualKeyCode: VK[k] ?? k.toUpperCase().charCodeAt(0) };
  await send('Input.dispatchKeyEvent', { type: text ? 'keyDown' : 'rawKeyDown', ...base, text });
  await send('Input.dispatchKeyEvent', { type: 'keyUp', ...base });
  await sleep(140);
}

const CONTRAST_JS = `(() => {
  const parse = (c) => { const m = c.match(/rgba?\\(([^)]+)\\)/); if (!m) return null; const p = m[1].split(/[ ,\\/]+/).filter(Boolean).map(Number); return { r: p[0], g: p[1], b: p[2], a: p[3] ?? 1 }; };
  const lum = ({ r, g, b }) => { const f = (v) => { v /= 255; return v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4; }; return 0.2126 * f(r) + 0.7152 * f(g) + 0.0722 * f(b); };
  const mix = (top, bottom) => ({ r: top.r * top.a + bottom.r * (1 - top.a), g: top.g * top.a + bottom.g * (1 - top.a), b: top.b * top.a + bottom.b * (1 - top.a), a: 1 });
  const backdrop = (node) => { const stack = []; for (let n = node; n; n = n.parentElement) { const cs = getComputedStyle(n); const bg = parse(cs.backgroundColor); if (cs.backgroundImage !== 'none') stack.push('image'); if (bg && bg.a > 0) { stack.push(bg); if (bg.a >= 1) break; } } let base = { r: 0, g: 0, b: 0, a: 1 }; let image = false; for (const l of stack.reverse()) { if (l === 'image') image = true; else base = mix(l, base); } return { base, image }; };
  const out = [];
  const seen = new Set();
  for (const node of document.querySelectorAll('body *')) {
    const own = [...node.childNodes].some((c) => c.nodeType === 3 && c.textContent.trim());
    if (!own) continue;
    const r = node.getBoundingClientRect();
    if (r.width < 2 || r.height < 2 || r.bottom < 0 || r.top > innerHeight) continue;
    const cs = getComputedStyle(node);
    if (cs.visibility === 'hidden' || +cs.opacity === 0) continue;
    let op = 1; for (let n = node; n; n = n.parentElement) op *= +getComputedStyle(n).opacity;
    const fg = parse(cs.color); if (!fg) continue;
    const { base, image } = backdrop(node);
    const eff = mix({ ...fg, a: fg.a * op }, base);
    const ratio = (Math.max(lum(eff), lum(base)) + 0.05) / (Math.min(lum(eff), lum(base)) + 0.05);
    const label = node.tagName.toLowerCase() + (node.className && typeof node.className === 'string' ? '.' + node.className.trim().split(/\\s+/).join('.') : '');
    const key = label + node.textContent.trim().slice(0, 20);
    if (ratio < 4.5 && !seen.has(key)) { seen.add(key); out.push({ ratio: +ratio.toFixed(2), el: label, text: node.textContent.trim().slice(0, 30), fg: cs.color, bg: 'rgb(' + [base.r, base.g, base.b].map(Math.round) + ')', image }); }
  }
  return out;
})()`;

await send('Page.enable');
await send('Emulation.setDeviceMetricsOverride', { width: W, height: H, deviceScaleFactor: +opt.dpr || 1, mobile: false });
await send('Page.navigate', { url: `http://127.0.0.1:${opt.port}/${opt.query ? `?${opt.query}` : ""}` });
await sleep(3500);
await evaluate('document.querySelector("#boot")?.classList.add("done")');

try {
  for (const step of steps) {
    const at = step.indexOf(':');
    const cmd = at < 0 ? step : step.slice(0, at);
    const arg = at < 0 ? '' : step.slice(at + 1);
    if (cmd === 'key') await key(arg);
    else if (cmd === 'keys') { const [k, n = 1] = arg.split('*'); for (let i = 0; i < +n; i++) await key(k); }
    else if (cmd === 'text') for (const ch of arg) await key(ch);
    else if (cmd === 'mouse') { const [x, y] = arg.split(',').map(Number); await send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: x - 20, y: y - 20 }); await send('Input.dispatchMouseEvent', { type: 'mouseMoved', x, y }); await sleep(100); }
    else if (cmd === 'wait') await sleep(+arg);
    else if (cmd === 'eval') console.log('eval ->', JSON.stringify(await evaluate(arg)));
    else if (cmd === 'skin') {
      await evaluate(`(async()=>{const {applySkin}=await import('/core/skins.js');const {state}=await import('/core/state.js');const {reloadSkins}=await import('/components/skin-menu.js');await reloadSkins();const skin=state.skins.find(s=>s.id==='${arg}');if(!skin)throw new Error('no skin ${arg}');applySkin(skin);})()`);
      await sleep(500);
    } else if (cmd === 'shot') {
      await sleep(450);
      const r = await send('Page.captureScreenshot', { format: 'png' });
      writeFileSync(join(opt.out, `${arg}.png`), Buffer.from(r.result.data, 'base64'));
      console.log('shot', arg);
    } else if (cmd === 'contrast') {
      await sleep(600); // let focus transitions settle
      const bad = await evaluate(CONTRAST_JS);
      console.log(`contrast ${arg}: ${bad.length} unter 4.5:1`);
      for (const b of bad) console.log('  ', JSON.stringify(b));
    } else console.log('unbekannter Schritt', step);
  }
} finally {
  cleanup();
  process.exit(0);
}
