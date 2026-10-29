// The tab bar in the preview (mock API, headless Edge, sound off) with the plugin tabs (?hatab: Smart Home, Lumen) in each skin and at 1280x720
// and 1920x1080, in two states: "stress" (the downloads chip and every status icon shown) and "normal" (only the bell, weather and clock).
// Checked in every state of an LB/RB walk over all tabs: no tab is invisible, none is narrower than the minimum (unless its label is
// shorter anyway), nothing is cut while the bar scrolls (except names over the maximum), a tab that is cut shows at least 2rem or none of
// it, no tab part lies under an arrow, the focused tab is whole, and Smart Home and Lumen are reached with LB/RB. In the normal state
// Smart Home must be fully readable without scrolling in the default skin and in Stardust at 1920 (as far as the width allows).
// The focused tab is never cut or faded, no arrow shows while the bar fits, and on the setup assistant no tab is visible (another run per skin).
// A screenshot of the bar with Smart Home focused goes to --out (tabs4-<skin>-<size>.png, tabs4-normal-<skin>-<size>.png).
//
//   node scripts/tabs-check.mjs [--catalog DIR] [--out DIR] [--skins a,b]  (the catalog skins folder defaults to ../couchhud-catalog-src/skins)
//
// Needs Edge (env EDGE); not part of check.sh because it starts a browser. No app is started.
import { spawnSync } from 'node:child_process';
import { existsSync, readdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const args = process.argv.slice(2);
const opt = { catalog: join(here, '..', '..', 'couchhud-catalog-src', 'skins'), out: join(here, '..', 'tabs-shots') };
for (let i = 0; i < args.length; i += 2) opt[args[i].replace(/^--/, '')] = args[i + 1];
opt.catalog = resolve(opt.catalog);
const skins = ['default', ...(existsSync(opt.catalog) ? readdirSync(opt.catalog, { withFileTypes: true }).filter((d) => d.isDirectory()).map((d) => d.name) : [])]
  .filter((s) => !opt.skins || opt.skins.split(',').includes(s));

const SHOW_STATUS = `(() => { for (const id of ['session-status', 'parental-status', 'downloads-status', 'weather']) { const n = document.getElementById(id); if (!n) continue; n.hidden = false; if (!n.textContent.trim()) n.textContent = '99 min'; } return 1; })()`;
const NORMAL_STATUS = `(() => { const st = document.createElement('style'); st.textContent = '#session-status, #parental-status, #downloads-status { display: none !important; }'; document.head.append(st); return 1; })()`;
const CHECK = `(() => {
  const list = document.getElementById('tab-list'); const lr = list.getBoundingClientRect();
  const rem = parseFloat(getComputedStyle(document.documentElement).fontSize);
  const lis = [...list.children]; const pins = lis.filter((l) => l.classList.contains('pinned'));
  const pinLeft = pins.length ? Math.min(...pins.map((p) => p.getBoundingClientRect().left)) : lr.right;
  const st = document.getElementById('status').getBoundingClientRect();
  const arrows = [...document.querySelectorAll('.tab-more')].filter((n) => getComputedStyle(n).display !== 'none').map((n) => n.getBoundingClientRect());
  const scrolls = list.scrollWidth > list.clientWidth + 1;
  const probs = [];
  for (const l of lis) {
    const cs = getComputedStyle(l); const r = l.getBoundingClientRect(); const pinned = l.classList.contains('pinned');
    if (cs.display === 'none' || cs.visibility === 'hidden' || Number(cs.opacity) === 0 || r.width < 2) probs.push('invisible: ' + l.textContent);
    if (r.width < Math.min(l.scrollWidth, 6.5 * rem) - 1.5) probs.push('smaller than the minimum: ' + l.textContent + ' ' + Math.round(r.width));
    if (l.getAttribute('aria-label') !== l.textContent) probs.push('no full name: ' + l.textContent);
    if (scrolls && l.scrollWidth > l.clientWidth + 1.5 && r.width < 11.9 * rem) probs.push('cut while the bar scrolls: ' + l.textContent);
    if (!pinned) {
      let visL = r.left; let visR = r.right;
      const m = /inset\\(([^)]*)\\)/.exec(cs.clipPath || '');
      if (m) {
        const p = m[1].trim().split(/\\s+/);
        const right = p[1] === '100%' ? r.width : parseFloat(p[1]); const left = parseFloat(p[3] ?? p[1]);
        visL = r.left + (Number.isFinite(left) ? left : 0); visR = r.right - (Number.isFinite(right) ? right : 0);
      }
      const shown = Math.max(0, visR - visL);
      if (shown > 0.5 && shown < Math.min(2 * rem, r.width) - 1) probs.push('a stub of ' + Math.round(shown) + 'px of ' + l.textContent);
      if (shown > 0.5) for (const a of arrows) if (visL < a.right - 1 && visR > a.left + 1) probs.push('text under an arrow: ' + l.textContent);
    }
  }
  const act = list.querySelector('li.active'); let whole = true;
  if (act && ((act.style.clipPath && act.style.clipPath !== 'none') || (act.style.webkitMaskImage && act.style.webkitMaskImage !== 'none'))) probs.push('the focused tab is cut or faded: ' + act.textContent);
  if (!scrolls && arrows.length) probs.push('an arrow shows while the bar fits');
  if (act && !act.classList.contains('pinned')) { const r = act.getBoundingClientRect(); whole = r.left >= lr.left - 1 && r.right <= pinLeft + 1; }
  if (!whole) probs.push('the focused tab is not whole: ' + act.textContent);
  if (lr.right > st.left + 1) probs.push('the tab bar runs into the status area');
  const sh = lis.find((l) => l.textContent === 'Smart Home');
  return JSON.stringify({ active: act && act.textContent, probs, hints: arrows.length, scrolls, smartHomeReadable: !!sh && sh.scrollWidth <= sh.clientWidth + 1, widths: lis.map((l) => l.textContent + ':' + Math.round(l.getBoundingClientRect().width)), lefts: lis.map((l) => l.getBoundingClientRect().left) });
})()`.replace(/\s*\n\s*/g, ' ');

const WIZARD = `(() => { const l = document.getElementById('tab-list'); return JSON.stringify({ view: document.body.dataset.view, shown: [...l.children].filter((x) => getComputedStyle(x).visibility !== 'hidden').length, arrows: [...document.querySelectorAll('.tab-more')].filter((n) => getComputedStyle(n).visibility !== 'hidden' && getComputedStyle(n).display !== 'none').length }); })()`;

let failed = 0;
let n = 0;
for (const mode of ['stress', 'normal']) {
  for (const size of ['1280x720', '1920x1080']) {
    for (const skin of skins) {
      const shot = `tabs4-${mode === 'normal' ? 'normal-' : ''}${skin}-${size}`;
      const steps = [`wait:2000`, ...(skin === 'default' ? [] : [`skin:user:${skin}`]), 'wait:1200', `eval:${mode === 'stress' ? SHOW_STATUS : NORMAL_STATUS}`, 'wait:900', `eval:${CHECK}`];
      for (let i = 0; i < 9; i++) steps.push('key:e', 'wait:150', `eval:${CHECK}`, ...(i === 5 ? ['wait:1400', `shot:${shot}`] : []));
      const run = spawnSync(process.execPath, [join(here, 'ui-shot.mjs'), '--out', opt.out, '--port', '5321', '--debug', '9421', '--size', size, '--catalog', opt.catalog, '--query', 'hatab=1', ...steps], { encoding: 'utf8', timeout: 240000 });
      const results = (run.stdout || '').split('\n').filter((l) => l.startsWith('eval -> "{')).map((l) => JSON.parse(JSON.parse(l.slice(8))));
      n++;
      const seen = new Set(results.map((r) => r.active));
      const problems = [...new Set(results.flatMap((r) => r.probs))];
      for (const name of ['Smart Home', 'Lumen']) if (!seen.has(name)) problems.push(`${name} was not reached with LB/RB`);
      if (results.length < 10) problems.push(`only ${results.length} states read (${(run.stderr || '').slice(0, 200)})`);
      const first = results[0];
      if (mode === 'normal' && size === '1920x1080' && ['default', 'stardust'].includes(skin) && first && (first.scrolls || !first.smartHomeReadable)) problems.push('normal state: Smart Home is not fully readable without scrolling');
      // The mode (fits / shrinks / scrolls) must not depend on the focus, and with LB/RB the tabs that are not the two involved must not move
      // more than a few pixels while the bar does not scroll (the focused tab takes from the others only what the one before gives back).
      let jump = 0;
      let shifted = 0;
      const modes = new Set(results.map((r) => r.scrolls));
      if (modes.size > 1) problems.push('the bar switches between scrolling and not scrolling with the focus');
      for (let i = 1; i < results.length; i++) {
        const p = results[i - 1]; const q = results[i];
        if (p.scrolls || q.scrolls || p.lefts.length !== q.lefts.length) continue;
        p.lefts.forEach((x, k) => {
          const name = p.widths[k].split(':')[0];
          const d = Math.abs(x - q.lefts[k]);
          jump = Math.max(jump, d);
          if (name !== p.active && name !== q.active) shifted = Math.max(shifted, d);
        });
      }
      if (shifted > 8) problems.push(`tabs move ${Math.round(shifted)}px when the focus moves (at most 8 allowed)`);
      console.log(`${problems.length ? 'FAIL' : 'ok  '} ${mode} ${skin} ${size}: ${first ? `${first.scrolls ? 'scrolls' : 'fits'}, Smart Home ${first.smartHomeReadable ? 'readable' : 'cut'}; jump ${Math.round(jump)}px, others ${Math.round(shifted)}px; ${first.widths.join(' ')}` : '-'}`);
      for (const p of problems) console.log(`     ${p}`);
      if (problems.length) failed++;
    }
  }
}
// The setup assistant hides the bar itself: with the runtime's inline styles no tab may show on it, in any skin.
for (const skin of skins) {
  const steps = ['wait:2500', ...(skin === 'default' ? [] : [`skin:user:${skin}`]), 'wait:1500', `eval:${WIZARD}`];
  const run = spawnSync(process.execPath, [join(here, 'ui-shot.mjs'), '--out', opt.out, '--port', '5321', '--debug', '9421', '--size', '1280x720', '--catalog', opt.catalog, '--query', 'terms&fresh&hatab=1', ...steps], { encoding: 'utf8', timeout: 240000 });
  const line = (run.stdout || '').split('\n').find((l) => l.startsWith('eval -> "{'));
  const r = line ? JSON.parse(JSON.parse(line.slice(8))) : null;
  n++;
  const bad = !r || r.view !== 'setup-wizard' || r.shown !== 0 || r.arrows !== 0;
  console.log(`${bad ? 'FAIL' : 'ok  '} wizard ${skin}: ${r ? `view ${r.view}, tabs shown ${r.shown}, arrows ${r.arrows}` : 'no reading'}`);
  if (bad) failed++;
}
console.log(failed ? `tabs: ${failed} of ${n} runs failed` : `tabs: ok (${n} runs)`);
process.exit(failed ? 1 : 0);
