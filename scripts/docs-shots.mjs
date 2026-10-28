// Regenerates the screenshots of README and docs, in German (docs/<name>.png) and English (docs/<name>.en.png):
//
//   node scripts/docs-shots.mjs [--lang de|en|both] [--catalog DIR] [--only name,name] [--out DIR]
//
// Headless Edge with the preview's mock API only (scripts/ui-shot.mjs), always ?demo (made-up games, own covers, no notification
// pop-ups). 1920x1080 scenes are captured at device scale 2/3, so every image is 1280x720 like the ones before it. The skins image is
// an HTML page (Couch skin plus the catalog skins) rendered by Edge. --catalog is a folder with the catalog's skin folders
// (default: ../couchhud-catalog-src/skins next to the repository); without it the skins image is skipped.
// Env: EDGE (path to msedge.exe).
import { spawnSync } from 'node:child_process';
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const args = process.argv.slice(2);
const opt = { lang: 'both', catalog: resolve(root, '..', 'couchhud-catalog-src', 'skins'), only: '', out: join(root, 'docs') };
for (let i = 0; i < args.length; i += 2) opt[args[i].replace(/^--/, '')] = args[i + 1];
const langs = opt.lang === 'both' ? ['de', 'en'] : [opt.lang];
const only = opt.only ? opt.only.split(',') : null;
const edge = process.env.EDGE || 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe';
const tmp = mkdtempSync(join(tmpdir(), 'couchhud-docs-'));

// The mock's demo data without a battery, a running download and notification pop-ups.
const query = (lang) => `demo&favs=demo&nobattery&nodownloads&lang=${lang}`;
const uiShot = (lang, out, extra) => {
  const r = spawnSync(process.execPath, [join(root, 'scripts', 'ui-shot.mjs'), '--out', out, '--query', query(lang), ...extra], { stdio: ['ignore', 'inherit', 'inherit'] });
  if (r.status !== 0) throw new Error('ui-shot failed');
};

const SCENES = {
  // The first tab: Favorites in the combined mode (recent games, favorites, collections), focus on the first card.
  home: ['keys:ArrowLeft*14', 'shot:home'],
  library: ['key:e', 'wait:500', 'shot:library'],
  guide: ['keys:ArrowLeft*14', 'key:Home', 'wait:900', 'shot:guide'],
  // Settings > Mode > "Back to normal Windows" asks for the couch code; three buttons entered.
  lock: ['keys:e*8', 'key:ArrowRight', 'keys:ArrowDown*1', 'key:Enter', 'wait:500', 'key:ArrowDown', 'key:Enter', 'wait:700', 'key:x', 'key:y', 'key:ArrowUp', 'wait:300', 'shot:lock']
};

const SKINS = [ // id, short line per language, built in or from the catalog
  { id: 'default', name: { de: 'CouchHUD', en: 'CouchHUD' }, line: { de: 'klar und ruhig', en: 'clear and calm' }, builtIn: true },
  { id: 'user:deepsea', folder: 'deepsea', line: { de: 'Wellen in Tiefblau', en: 'waves in deep blue' } },
  { id: 'user:greenlight', folder: 'greenlight', line: { de: 'dunkel mit Grün', en: 'dark with green' } },
  { id: 'user:neoncity', folder: 'neoncity', line: { de: 'Neon und Scanlines', en: 'neon and scanlines' } },
  { id: 'user:nova', folder: 'nova', line: { de: 'Glas und Aurora-Licht', en: 'glass and aurora light' } }
];
const PANEL = {
  de: ['Couch-Skin und Katalog-Skins', 'Wechsle jederzeit über Einstellungen › Darstellung. Weitere Skins kommen über den Katalog oder als eigener Ordner mit skin.json + skin.css.'],
  en: ['Couch skin plus catalog skins', 'Switch any time under Settings › Look. More skins come from the catalog or as your own folder with skin.json + skin.css.']
};
const LABEL = { de: { builtIn: 'eingebaut', catalog: 'Katalog' }, en: { builtIn: 'built in', catalog: 'catalog' } };
const fileOf = (s) => `${s.id.replace(/\W/g, '_')}`;

function skinsImage(lang, out) {
  const dir = join(tmp, `skins-${lang}`);
  // One fresh page per skin: switching skins on one page can leave parts of the previous skin behind.
  for (const s of SKINS) uiShot(lang, dir, ['--size', '1920x1080', '--catalog', opt.catalog, 'keys:ArrowLeft*14', `skin:${s.id}`, 'wait:300', `shot:${fileOf(s)}`]);
  const cells = SKINS.map((s) => {
    const name = s.name?.[lang] ?? JSON.parse(readFileSync(join(opt.catalog, s.folder, 'skin.json'), 'utf8')).name[lang];
    const img = pathToFileURL(join(dir, `${fileOf(s)}.png`)).href;
    return `<figure><div class="shot"><img src="${img}"></div><figcaption><b>${name}</b><span>${s.line[lang]}</span><i>${s.builtIn ? LABEL[lang].builtIn : LABEL[lang].catalog}</i></figcaption></figure>`;
  }).join('') + `<aside><h2>${PANEL[lang][0]}</h2><p>${PANEL[lang][1]}</p></aside>`;
  const html = `<!doctype html><meta charset="utf-8"><style>
    html,body{margin:0;width:1600px;height:737px;overflow:hidden}
    body{background:linear-gradient(135deg,#0f1738,#2a1a5c);font-family:"Segoe UI",Arial,sans-serif;color:#fff}
    main{display:grid;grid-template-columns:repeat(3,1fr);column-gap:22px;row-gap:14px;padding:32px 36px 0}
    .shot{aspect-ratio:16/9;border-radius:14px;overflow:hidden;box-shadow:0 8px 24px #0008;outline:1px solid #ffffff22}
    img{width:100%;height:100%;display:block}
    aside{align-self:center;padding:0 12px 56px 24px}aside h2{margin:0 0 14px;font-size:34px;line-height:1.15;color:#8fb8ff}aside p{margin:0;font-size:22px;line-height:1.45;color:#c9cfe8}
    figure{margin:0}figcaption{display:flex;align-items:baseline;gap:12px;margin-top:10px;font-size:22px;color:#c9cfe8}
    figcaption b{font-size:28px;color:#fff}figcaption i{margin-left:auto;font-style:normal;font-size:15px;padding:2px 10px;border-radius:99px;background:#ffffff1c;color:#dfe3ff}
  </style><main>${cells}</main>`;
  const page = join(tmp, `skins-${lang}.html`);
  writeFileSync(page, html);
  spawnSync(edge, ['--headless=new', '--mute-audio', '--disable-gpu', '--force-color-profile=srgb', '--hide-scrollbars', `--user-data-dir=${join(tmp, 'edge-' + lang)}`,
    '--window-size=1600,737', `--screenshot=${out}`, pathToFileURL(page).href], { stdio: 'ignore' });
  if (!existsSync(out)) throw new Error('Edge could not render the skins image');
}

try {
  mkdirSync(opt.out, { recursive: true });
  for (const lang of langs) {
    const suffix = lang === 'en' ? '.en' : '';
    for (const [name, steps] of Object.entries(SCENES)) {
      if (only && !only.includes(name)) continue;
      uiShot(lang, tmp, ['--size', '1920x1080', '--dpr', '0.66666667', ...steps]);
      copyFileSync(join(tmp, `${name}.png`), join(opt.out, `screenshot-${name}${suffix}.png`));
      console.log('docs', `screenshot-${name}${suffix}.png`);
    }
    if (!only || only.includes('skins')) {
      if (existsSync(opt.catalog)) { skinsImage(lang, join(opt.out, `skins${suffix}.png`)); console.log('docs', `skins${suffix}.png`); }
      else console.log('skins image skipped: no catalog skins folder', opt.catalog);
    }
  }
} finally {
  rmSync(tmp, { recursive: true, force: true });
}
