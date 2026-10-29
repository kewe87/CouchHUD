// The couch browser's HUD side in headless Edge (mock API, no window, no sound): the controller's place between the HUD
// and the page stays in step with the host, the page's questions and downloads are asked in the HUD, typing into the page
// works, and the page never stays "focused" while the host steers the HUD.
//
//   node scripts/browser-ui-check.mjs [--port 5198] [--debug 9334]
//
// Env: EDGE (path to msedge.exe). Runs only headless (--headless=new --mute-audio); exit code 1 on a failed check.
import { spawn } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import http from 'node:http';

const here = dirname(fileURLToPath(import.meta.url));
const args = process.argv.slice(2);
const opt = { port: '5198', debug: '9334' };
for (let i = 0; i < args.length; i++) if (args[i].startsWith('--')) opt[args[i].slice(2)] = args[++i];

const edge = process.env.EDGE || 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe';
const server = spawn(process.execPath, [join(here, 'preview-server.js')], { env: { ...process.env, PORT: opt.port }, stdio: 'ignore' });
const profile = mkdtempSync(join(tmpdir(), 'couchhud-edge-'));
const browser = spawn(edge, [`--remote-debugging-port=${opt.debug}`, '--headless=new', '--mute-audio', '--disable-gpu', '--disable-extensions',
  '--no-first-run', `--user-data-dir=${profile}`, '--window-size=1920,1080', 'about:blank'], { stdio: 'ignore' });
let cleaned = false;
const cleanup = () => {
  if (cleaned) return;
  cleaned = true;
  server.kill();
  browser.kill();
  setTimeout(() => { try { rmSync(profile, { recursive: true, force: true }); } catch { /* Edge still holds it */ } }, 500);
};
process.on('exit', cleanup);

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const getJson = (url) => new Promise((res, rej) => http.get(url, (r) => { let d = ''; r.on('data', (c) => (d += c)); r.on('end', () => { try { res(JSON.parse(d)); } catch (e) { rej(e); } }); }).on('error', rej));

async function connect() {
  for (let i = 0; i < 80; i++) {
    try {
      const page = (await getJson(`http://127.0.0.1:${opt.debug}/json`)).find((t) => t.type === 'page');
      if (page) return page.webSocketDebuggerUrl;
    } catch { /* not up yet */ }
    await sleep(250);
  }
  throw new Error('headless Edge not reachable');
}

const ws = new WebSocket(await connect());
await new Promise((r) => (ws.onopen = r));
let id = 0;
const waiting = new Map();
ws.onmessage = (m) => { const d = JSON.parse(m.data); if (d.id && waiting.has(d.id)) { waiting.get(d.id)(d); waiting.delete(d.id); } };
const send = (method, params = {}) => new Promise((res) => { waiting.set(++id, res); ws.send(JSON.stringify({ id, method, params })); });
const evaluate = async (expression) => {
  const r = await send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
  if (r.result?.exceptionDetails) throw new Error(r.result.exceptionDetails.exception?.description ?? JSON.stringify(r.result.exceptionDetails));
  return r.result?.result?.value;
};

const CODES = { F6: 'F6', Enter: 'Enter', Escape: 'Escape', Backspace: 'Backspace' };
const VK = { F6: 117, Enter: 13, Escape: 27, Backspace: 8, ArrowLeft: 37, ArrowUp: 38, ArrowRight: 39, ArrowDown: 40 };
async function key(k) {
  const text = k.length === 1 ? k : k === 'Enter' ? '\r' : undefined;
  const base = { key: k, code: CODES[k] ?? (k.length === 1 ? `Key${k.toUpperCase()}` : k), windowsVirtualKeyCode: VK[k] ?? k.toUpperCase().charCodeAt(0) };
  await send('Input.dispatchKeyEvent', { type: text ? 'keyDown' : 'rawKeyDown', ...base, text });
  await send('Input.dispatchKeyEvent', { type: 'keyUp', ...base });
  await sleep(160);
}

const host = (name, payload) => evaluate(`window.couchhudMockBrowser(${JSON.stringify(name)}, ${JSON.stringify(payload)})`);
const calls = (name) => evaluate(`(window.__mockCalls ?? []).filter((c) => c[0] === ${JSON.stringify(name)})`);
const clearCalls = () => evaluate('window.__mockCalls = []');
const view = () => evaluate(`import('/views/browser.js').then(({ browserView: v }) => ({ focus: v.focus, open: v.isOpen }))`);
const overlay = () => evaluate(`(() => { const o = document.querySelector('#overlay'); return o && !o.hidden ? o.textContent : null; })()`);
const lastLayout = async () => (await calls('browserLayout')).at(-1)?.[1];

let failed = 0;
function check(ok, what, detail) {
  console.log(`${ok ? 'ok  ' : 'FAIL'} ${what}${ok || detail === undefined ? '' : ` -> ${JSON.stringify(detail)}`}`);
  if (!ok) failed++;
}

await send('Page.enable');
await send('Emulation.setDeviceMetricsOverride', { width: 1920, height: 1080, deviceScaleFactor: 1, mobile: false });
await send('Page.navigate', { url: `http://127.0.0.1:${opt.port}/?lang=en` });
await sleep(3500);
await evaluate('document.querySelector("#boot")?.classList.add("done")');

try {
  // 0. Menus: an ungrouped menu renders as before (no headings, a detail on every entry, plain list); the grouped power menu in
  // tiles has its headings outside the entry indices and no label wider than its tile.
  const plain = await evaluate(`(async () => {
    const { openOverlay, closeOverlay } = await import('/components/overlay.js');
    openOverlay({ title: 'T', entries: [{ label: 'a', detail: 'da', run: () => {} }, { label: 'b', detail: 'db', run: () => {} }, { label: 'c', detail: 'dc', run: () => {} }], index: 1 });
    const ul = document.querySelector('#overlay .menu');
    const r = { cls: [...ul.classList].sort(), items: ul.children.length, details: ul.querySelectorAll('li small').length, headings: ul.querySelectorAll('.menu-group').length,
      below: !!document.querySelector('#overlay .menu-detail'), indexes: [...ul.children].map((li) => li.dataset.index).join(','), focused: ul.querySelector('li.focused')?.dataset.index };
    closeOverlay();
    return r;
  })()`);
  check(plain.cls.join() === 'menu' && plain.items === 3 && plain.details === 3 && plain.headings === 0 && !plain.below && plain.indexes === '0,1,2' && plain.focused === '1',
    'ungrouped menu renders unchanged', plain);
  const tiles = await evaluate(`(async () => {
    const { state } = await import('/core/state.js');
    state.info.isShell = true;
    const { powerMenu } = await import('/core/power.js');
    const { openOverlay, closeOverlay } = await import('/components/overlay.js');
    openOverlay(powerMenu());
    const ul = document.querySelector('#overlay .menu');
    const fits = [...ul.querySelectorAll('.menu-label')].every((l) => l.scrollWidth <= l.parentElement.clientWidth + 1);
    const r = { tiles: ul.classList.contains('tiles'), headings: [...ul.querySelectorAll('.menu-group')].map((h) => h.dataset.index ?? '-').join(','),
      entries: ul.querySelectorAll('li[data-index]').length, focused: state.overlay.entries[state.overlay.index].id, details: ul.querySelectorAll('li small').length,
      below: document.querySelector('#overlay .menu-detail')?.textContent ?? null, fits };
    closeOverlay();
    state.info.isShell = false;
    return r;
  })()`);
  check(tiles.tiles && tiles.headings === '-,-' && tiles.entries === 7 && tiles.focused === 'standby' && tiles.details === 0 && !!tiles.below && tiles.fits,
    'power menu: tiles, two headings outside the indices, detail under the tiles, labels fit', tiles);

  // 1. Opening a site: the browser tab comes up with the page in front and the controller on it.
  await clearCalls();
  await evaluate(`import('/core/api.js').then(({ api }) => api.openBrowser('example.org'))`);
  await sleep(500);
  let v = await view();
  check(v.open && v.focus === 'page', 'opened site: page has the controller', v);
  check(/^\d+,\d+,\d+,\d+$/.test(await lastLayout() ?? ''), 'page is placed over the placeholder', await lastLayout());
  check((await calls('browserFocus')).at(-1)?.[1] === true, 'host is asked for the page focus', await calls('browserFocus'));

  // 2. The host moves the controller to the bar (View on the page): the HUD follows without asking back.
  await clearCalls();
  await host('browserui', 'bar');
  await sleep(300);
  v = await view();
  check(v.focus === 'bar', 'host "bar": HUD shows the bar', v);
  check((await calls('browserFocus')).length === 0, 'no focus ping-pong after a host report', await calls('browserFocus'));

  // 3. Down in the bar gives the page the controller again.
  await key('ArrowDown');
  await sleep(300);
  v = await view();
  check(v.focus === 'page' && (await calls('browserFocus')).at(-1)?.[1] === true, 'down in the bar: back to the page', v);

  // 4. The host refuses the page (not on screen yet): the HUD must not stay "page" while the host steers the HUD.
  await host('browserui', 'bar');
  await host('refuseFocus', true);
  await sleep(200);
  await key('ArrowDown');
  await sleep(400);
  v = await view();
  check(v.focus === 'bar', 'refused page focus: HUD back on the bar', v);
  await host('refuseFocus', false);
  await key('ArrowDown');
  await sleep(400);
  check((await view()).focus === 'page', 'asking again after a refusal works', await view());

  // 5. The host gives the page the controller by itself (full screen): the HUD follows.
  await host('browserui', 'bar');
  await sleep(200);
  await host('browserui', 'page');
  await sleep(200);
  check((await view()).focus === 'page', 'host "page": HUD follows', await view());

  // 6. A page's confirm(): asked in the HUD with the site's name, the page steps aside, the answer goes back.
  await clearCalls();
  await host('browserdialog', { id: 7, kind: 'confirm', message: 'Leave this page?', host: 'example.org' });
  await sleep(400);
  const text = await overlay();
  check(!!text && text.includes('example.org') && text.includes('Leave this page?'), 'confirm shown in the HUD with the site', text);
  check(await lastLayout() === 'hidden', 'page steps aside while the HUD asks', await lastLayout());
  check(await evaluate(`document.querySelector('#overlay').classList.contains('web')`), 'a page question is marked as coming from the web');
  await key('Enter');
  await sleep(300);
  check(JSON.stringify((await calls('browserDialogAnswer')).at(-1)) === JSON.stringify(['browserDialogAnswer', 7, true, null]), 'confirm answered yes', await calls('browserDialogAnswer'));
  check(await overlay() === null, 'dialog gone after the answer', await overlay());

  // 7. An alert the host closes by itself (time-out, page gone): the HUD closes it too.
  await host('browserdialog', { id: 8, kind: 'alert', message: 'Hi', host: 'example.org', embedded: true });
  await sleep(300);
  check((await overlay())?.includes('example.org (embedded)'), 'alert of an embedded frame names the page, marked as embedded', await overlay());
  await host('browserdialog', { id: 8, closed: true });
  await sleep(300);
  check(await overlay() === null, 'alert closed by the host', await overlay());

  // 8. A download: the question defaults to "no".
  await clearCalls();
  await host('browserdialog', { id: 9, kind: 'download', message: 'holiday-photos.zip', extension: 'zip', host: 'example.org', bytes: 1048576 });
  await sleep(400);
  const dl = await overlay();
  check(!!dl && dl.includes('holiday-photos.zip') && dl.includes('1 MB') && dl.includes('File type: ZIP'), 'download asked with name, size and type', dl);
  await key('Enter');
  await sleep(300);
  check(JSON.stringify((await calls('browserDialogAnswer')).at(-1)) === JSON.stringify(['browserDialogAnswer', 9, false, null]), 'download: Enter on the default is "no"', await calls('browserDialogAnswer'));

  // 9. A prompt(): the on-screen keyboard with the page's default text.
  await clearCalls();
  await host('browserdialog', { id: 10, kind: 'prompt', message: 'Name?', defaultText: 'ab', host: 'example.org' });
  await sleep(500);
  check(await evaluate(`!!document.querySelector('.osk.web')`), 'a page prompt has the web frame on the keyboard');
  await key('c');
  await key('Enter');
  await sleep(400);
  check(JSON.stringify((await calls('browserDialogAnswer')).at(-1)) === JSON.stringify(['browserDialogAnswer', 10, true, 'abc']), 'prompt answered with the typed text', await calls('browserDialogAnswer'));

  // 10. X on the page: the keyboard types into the page; the controller is back on the page afterwards.
  await host('browserui', 'page');
  await clearCalls();
  await host('browserui', { type: 'typing', secret: false });
  await sleep(500);
  for (const ch of 'hello') await key(ch);
  await key('Enter');
  await sleep(700);
  check(JSON.stringify((await calls('browserType')).at(-1)) === JSON.stringify(['browserType', 'hello', false]), 'typed text goes into the page', await calls('browserType'));
  check((await view()).focus === 'page', 'after typing the page has the controller again', await view());

  // 11. Typing cancelled with B: still back on the page, nothing typed.
  await clearCalls();
  await host('browserui', { type: 'typing', secret: true });
  await sleep(500);
  await key('Escape');
  await sleep(500);
  check((await calls('browserType')).length === 0, 'cancelled typing sends nothing');
  check((await view()).focus === 'page', 'after cancelled typing the page has the controller again', await view());

  // 12. Unknown or malformed host messages change nothing.
  await host('browserdialog', { id: 11, kind: 'file' });
  await host('browserdialog', null);
  await host('browserui', { type: 'other' });
  await sleep(300);
  check(await overlay() === null && (await view()).focus === 'page', 'unknown messages are ignored', await view());

  // 12b. A mouse click beside the page: the keyboard is in the HUD now, the bar shows it; Enter asks for the page again; F6 toggles.
  await host('browserui', 'page');
  await sleep(200);
  await clearCalls();
  await evaluate(`document.body.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true }))`);
  await sleep(300);
  check((await view()).focus === 'bar' && (await calls('browserFocus')).at(-1)?.[1] === false, 'click beside the page: visible focus on the bar', await view());
  await host('browserui', 'page');
  await sleep(200);
  await clearCalls();
  await key('Enter');
  await sleep(300);
  check((await calls('browserFocus')).at(-1)?.[1] === true && (await view()).focus === 'page', 'Enter while the HUD has the keys: the page is asked for again', await calls('browserFocus'));
  await clearCalls();
  await key('F6');
  await sleep(300);
  check((await view()).focus === 'bar', 'F6 in the HUD goes to the bar', await view());
  await key('F6');
  await sleep(300);
  check((await view()).focus === 'page', 'F6 in the bar goes back to the page', await view());

  // 12c. A page in full screen: the HUD's strip above it says it is a website.
  await host('browser', { open: true, url: 'https://example.org/', title: 'Example', canGoBack: true, fullScreen: true });
  await sleep(300);
  const badge = await evaluate(`document.querySelector('#web-fullscreen-badge')?.textContent ?? null`);
  check(!!badge && badge.includes('example.org'), 'full screen: the website badge is shown', badge);
  await host('browser', { open: true, url: 'https://other.example/', title: 'Other', canGoBack: true, fullScreen: true });
  await sleep(300);
  const moved = await evaluate(`document.querySelector('#web-fullscreen-badge')?.textContent ?? null`);
  check(!!moved && moved.includes('other.example') && !moved.includes('example.org'), 'full screen: the badge follows a redirect to another site', moved);
  await host('browser', { open: true, url: 'https://example.org/', title: 'Example', canGoBack: true, fullScreen: false });
  await sleep(300);
  check(await evaluate(`!document.querySelector('#web-fullscreen-badge')`), 'out of full screen: the badge is gone');

  // 12d. The user went home (guide): a question of the page is not shown over the HUD, it is answered "no".
  await evaluate(`import('/core/nav.js').then(({ switchTab }) => switchTab(0))`);
  await sleep(400);
  await clearCalls();
  await host('browserdialog', { id: 21, kind: 'confirm', message: 'Enter your code', host: 'example.org' });
  await sleep(500);
  check(await overlay() === null && JSON.stringify((await calls('browserDialogAnswer')).at(-1)) === JSON.stringify(['browserDialogAnswer', 21, false, null]),
    'a page question after leaving is refused unseen', await calls('browserDialogAnswer'));
  // A question the host ended while it waited for the screen is never shown.
  await host('browserui', 'show');
  await sleep(400);
  await clearCalls();
  await evaluate(`import('/components/overlay.js').then(({ openOverlay }) => { openOverlay({ title: 'busy', tag: 'busy', entries: [{ label: 'x', run: () => {} }] }); })`);
  await sleep(200);
  await host('browserdialog', { id: 22, kind: 'alert', message: 'late', host: 'example.org' });
  await host('browserdialog', { id: 22, closed: true });
  await evaluate(`import('/components/overlay.js').then(({ closeTagged }) => closeTagged('busy'))`);
  await sleep(600);
  check(await overlay() === null && (await calls('browserDialogAnswer')).length === 0, 'a question ended while waiting is not shown', await overlay());
  await host('browserui', 'page');
  await sleep(200);

  // 13. Closing the browser: the HUD leaves the page.
  await evaluate(`import('/core/api.js').then(({ api }) => api.browserCommand('close'))`);
  await sleep(400);
  v = await view();
  check(!v.open && v.focus === 'bar' && await lastLayout() === 'hidden', 'closed browser: no page, no page focus', v);

  // 14. The guard script of every page (from BrowserGuard.cs) in a blank document: no picture-in-picture of either kind.
  const source = readFileSync(join(here, '..', 'src', 'CouchHud.Core', 'Guard', 'BrowserGuard.cs'), 'utf8');
  const guard = /public const string Script = "{3}\r?\n([\s\S]*?)\r?\n\s*"{3};/.exec(source)?.[1];
  check(!!guard, 'guard script found in BrowserGuard.cs');
  await send('Page.navigate', { url: 'about:blank' });
  await sleep(500);
  await evaluate(guard);
  const pip = await evaluate(`(async () => {
    const v = document.createElement('video');
    let refused = false;
    try { await v.requestPictureInPicture(); } catch (e) { refused = e.name === 'NotAllowedError'; }
    v.disablePictureInPicture = false;
    return { refused, disabled: v.disablePictureInPicture === true, docPip: window.documentPictureInPicture === undefined, enabled: document.pictureInPictureEnabled };
  })()`);
  check(pip?.refused && pip.disabled && pip.docPip && pip.enabled === false, 'guard: picture-in-picture and Document PiP refused', pip);
} catch (e) {
  console.log('FAIL', e.message);
  failed++;
} finally {
  console.log(failed ? `browser ui: ${failed} failed` : 'browser ui: ok');
  cleanup();
  setTimeout(() => process.exit(failed ? 1 : 0), 700);
}
