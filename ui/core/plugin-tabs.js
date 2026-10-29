// Main tabs that plugins ask for (pure, no DOM; tested by scripts/ui-logic-check.mjs). The host (PluginTabRules in Core) decides which tabs
// exist, checks ids, titles, glyphs and pictures and applies the areas, the profile and the lock; this module only distrusts the list once more
// before the tab bar draws it, keeps the views of the tabs stable between refreshes, and puts the tabs in their place. The title, glyph and
// area rules are the host's: ui-logic-check.mjs reads PluginTabs.cs and fails if the two lists or the look of a title differ.

import { safeImage } from './plugin-rules.js';
import { BUILTIN_GLYPHS } from './glyph-icons.js';

/** The same bounds as the host (PluginTabRules): a list that breaks them is cut, not trusted. */
export const MAX_PER_PLUGIN = 2;
export const MAX_TOTAL = 6;
/** What fits the tab bar next to six other tabs. */
export const MAX_TITLE = 14;
/** The glyph shown for a third party's glyph that is one of the HUD's own icons. */
export const GENERIC_GLYPH = '◆';

/** The titles of the built-in tabs and CouchHUD's own screens (both languages): a plugin tab never takes one. Same list as PluginTabRules.ReservedTitles. */
export const RESERVED_TITLES = [
  'Home', 'Favoriten', 'Favorites', 'Spiele', 'Games', 'Apps', 'Browser', 'Downloads', 'News', 'Nachrichten', 'Assistent', 'Assistant',
  'Einstellungen', 'Settings', 'Smart Home', 'Katalog', 'Catalog', 'Plugins', 'Guide', 'Profile', 'Profil', 'Sperre', 'Lock', 'Eltern', 'Parents',
  'Sicherheit', 'Security', 'CouchHUD', 'Update', 'Updates', 'Setup'
];
/** Only CouchHUD's own plugins may carry these (PluginTabRules.FirstPartyTitles). */
export const FIRST_PARTY_TITLES = ['Smart Home'];
/** The names of CouchHUD's own plugins (PluginNames.Reserved): no other plugin's tab carries one. */
export const RESERVED_NAMES = ['casehud', 'wand', 'couchhud', 'nordvpn', 'vpn', 'homeassistant', 'home-assistant', 'stardust', 'tvcontrol', 'webostv'];

// The look of a title as the host compares it (PluginTabRules.Look): lower case, diacritics gone (ß ss, æ ae, ø o, ð d, þ p, other letters
// above ASCII x), then PluginNames.Skeleton: look-alike characters folded, separators gone, rn/vv/cl folded.
const PAIRS = { ß: 'ss', æ: 'ae', ø: 'o', ð: 'd', þ: 'p' };
const FOLD = { 0: 'o', 1: 'l', i: 'l', '|': 'l', '!': 'l', 3: 'e', 4: 'a', '@': 'a', 5: 's', $: 's', 7: 't', 8: 'b' };
export function look(text) {
  const plain = [...String(text).toLowerCase().normalize('NFD').replace(/\p{Mn}/gu, '')]
    .map((c) => PAIRS[c] ?? (c.charCodeAt(0) < 128 ? c : 'x')).join('');
  const folded = [...plain].map((c) => FOLD[c] ?? c).filter((c) => /[a-z0-9]/.test(c)).join('');
  return folded.replaceAll('rn', 'm').replaceAll('vv', 'w').replaceAll('cl', 'd');
}

const RESERVED_LOOKS = RESERVED_TITLES.map(look);
const FIRST_PARTY_LOOKS = FIRST_PARTY_TITLES.map(look);
const NAME_LOOKS = RESERVED_NAMES.map(look);

/** A title the bar may show (PluginTabRules.CleanTitle): trimmed, spaces collapsed, ASCII letters and digits, Latin-1 letters and a few signs, at most MAX_TITLE. */
export function cleanTitle(raw) {
  if (typeof raw !== 'string' || raw.length > MAX_TITLE * 4) return null;
  const text = raw.normalize('NFC').trim().replace(/[ \t ]+/g, ' ');
  if (!text || text.length > MAX_TITLE || !/^[A-Za-z0-9 \-&.'+/À-ÖØ-öø-ÿ]+$/.test(text)) return null; // i18n-ignore: the Latin-1 letters are the rule
  return text;
}

/** Whether a title is, looks like or contains a built-in title or (for others than CouchHUD's own plugins) the name of one of its plugins. */
export function isReservedTitle(title, firstParty) {
  const l = look(title);
  if (!l) return true;
  if (firstParty && FIRST_PARTY_LOOKS.includes(l)) return false;
  if (RESERVED_LOOKS.some((r) => l === r || (r.length >= 4 && l.includes(r)))) return true;
  return !firstParty && (l.startsWith(look('couchhud')) || NAME_LOOKS.some((r) => l === r || (r.length >= 5 && l.includes(r))));
}

/** One symbol (an emoji, arrow or shape) with at most a variation selector or skin tone; nothing else (PluginTabRules.CleanGlyph). */
export function cleanGlyph(glyph) {
  if (typeof glyph !== 'string' || !glyph || glyph.length > 16) return null;
  const graphemes = [...new Intl.Segmenter(undefined, { granularity: 'grapheme' }).segment(glyph)];
  if (graphemes.length !== 1) return null;
  const cps = [...glyph];
  if (cps.length > 3 || !/^[\p{So}\p{Sm}\p{Sk}]$/u.test(cps[0])) return null;
  for (const c of cps.slice(1)) {
    const n = c.codePointAt(0);
    if (n !== 0xfe0f && n !== 0xfe0e && !(n >= 0x1f3fb && n <= 0x1f3ff)) return null;
  }
  return glyph;
}

const plainGlyph = (g) => g.replaceAll('️', '').replaceAll('︎', '');
const BUILTIN_PLAIN = BUILTIN_GLYPHS.map(plainGlyph);

/** The glyph to draw: the clean one; for others than CouchHUD's own plugins a glyph that is one of the HUD's own icons becomes the generic dot. */
export function glyphFor(glyph, firstParty) {
  const clean = cleanGlyph(glyph);
  if (!clean) return null;
  if (firstParty) return clean;
  return BUILTIN_PLAIN.includes(plainGlyph(clean)) ? GENERIC_GLYPH : clean;
}

const AREAS = ['games', 'apps', 'browser', 'news', 'always'];

/** "always" only for CouchHUD's own plugins; anything unknown, and a third party's "always", is "apps" (PluginTabRules.AreaOf). */
export function areaOf(area, firstParty) {
  const a = typeof area === 'string' ? area.trim().toLowerCase() : '';
  if (!AREAS.includes(a)) return 'apps';
  return a === 'always' && !firstParty ? 'apps' : a;
}

/**
 * The tabs the host listed, cleaned: only entries with a plugin id, a tab id, a page id and a short plain title that is no built-in name;
 * at most two per plugin and six in all; no second tab with the same look of title; the area, the picture and the glyph as the rules above
 * say; the first-party flag stays as the host sent it. Anything else is dropped. Returns new objects (never the host's).
 */
export function cleanPluginTabs(list) {
  const out = [];
  const titles = new Set();
  const perPlugin = new Map();
  if (!Array.isArray(list)) return out;
  for (const x of list) {
    if (out.length >= MAX_TOTAL) break;
    if (!x || typeof x !== 'object') continue;
    const { plugin, id, page } = x;
    if (![plugin, id, page, x.title].every((v) => typeof v === 'string' && v.length > 0 && v.length <= 100)) continue;
    const own = x.origin === 'firstParty';
    const title = cleanTitle(x.title);
    if (!title || isReservedTitle(title, own)) continue;
    const key = look(title);
    if (titles.has(key)) continue;
    if ((perPlugin.get(plugin) ?? 0) >= MAX_PER_PLUGIN) continue;
    titles.add(key);
    perPlugin.set(plugin, (perPlugin.get(plugin) ?? 0) + 1);
    out.push({
      plugin,
      id,
      page,
      title,
      pluginName: typeof x.pluginName === 'string' ? x.pluginName.slice(0, 100) : plugin,
      pageTitle: typeof x.pageTitle === 'string' ? x.pageTitle.slice(0, 200) : title,
      pageDescription: typeof x.pageDescription === 'string' ? x.pageDescription.slice(0, 500) : null,
      area: areaOf(x.area, own),
      glyph: glyphFor(x.glyph, own),
      image: safeImage(x.image),
      origin: own ? 'firstParty' : 'other'
    });
  }
  return out;
}

export const tabKey = (t) => `${t.plugin}/${t.id}`;

/**
 * Keeps one view per tab between refreshes (the active tab is found by identity, and a page keeps its cursor): `make(tab)` builds a view,
 * `refresh(view, tab)` brings a kept one up to date (title, picture). A view of a tab that is gone is handed to `drop(view)` (it must let go of
 * what it holds: a value still settling is cancelled, an open page left) and forgotten.
 */
export function tabRegistry(make, refresh, drop = () => {}) {
  const views = new Map();
  return {
    sync(tabs) {
      const next = new Map();
      const list = tabs.map((tab) => {
        const key = tabKey(tab);
        let view = views.get(key);
        if (view) refresh(view, tab);
        else view = make(tab);
        next.set(key, view);
        return view;
      });
      for (const [key, view] of views) if (!next.has(key)) drop(view);
      views.clear();
      for (const [k, v] of next) views.set(k, v);
      return list;
    },
    get size() {
      return views.size;
    }
  };
}


/**
 * The tab bar: the built-in content tabs, then the plugin tabs, then the closing tabs (assistant, settings), so Settings stays last. The
 * plugin tabs keep the host's order (CouchHUD's own plugins first, then the others, each in the order they registered).
 */
/** The closing tabs that never scroll out of the bar, however many plugin tabs there are: the assistant and Settings. */
export const PINNED_TABS = ['assistant', 'settings'];
export const isPinnedTab = (view) => PINNED_TABS.includes(view?.id);

export const arrangeTabs = (head, plugin, tail) => [...head, ...plugin, ...tail];

/**
 * After the tab list changed: the index of the active tab. The same tab if it is still there; if it is gone (its plugin was switched off,
 * failed or unloaded, its area went off) the first tab; with no tab active before, the old index kept inside the list.
 */
export function activeAfter(oldTabs, index, newTabs) {
  const active = oldTabs[index];
  if (!active) return Math.max(0, Math.min(index, newTabs.length - 1));
  const at = newTabs.indexOf(active);
  return at >= 0 ? at : 0;
}
