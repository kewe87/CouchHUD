// The couch browser's pure rules on the HUD side (no DOM, no host): tested in scripts/ui-logic-check.mjs.

import { formatBytes } from './size.js';

/** The site's host without "www.", or the text itself when it is no address. */
export function host(url) {
  try {
    return new URL(url).host.replace(/^www\./, '');
  } catch {
    return url ?? '';
  }
}

/** One letter for the badge: the site's name, not its country prefix (de.wikipedia.org gives W). */
export function initial(url) {
  const parts = host(url).split('.');
  const name = parts.length > 2 ? parts.at(-2) : parts[0];
  return (name || '?').slice(0, 1).toUpperCase();
}

/** How many recent sites are kept. */
export const RECENT_LIMIT = 6;

/**
 * The address a recent site is kept under: origin plus path, nothing else. The query and the fragment are dropped (they can carry
 * tokens, codes and search terms, and a page that rewrites them with history.pushState would otherwise fill the list), and so are
 * a user name and password in the address. Null for anything but http(s) and for text that is no address.
 */
export function siteAddress(url) {
  if (typeof url !== 'string') return null;
  try {
    const u = new URL(url);
    return u.protocol === 'http:' || u.protocol === 'https:' ? u.origin + u.pathname : null;
  } catch {
    return null;
  }
}

/**
 * The recent sites after a visit: newest first, each origin plus path once (a new visit replaces the old entry), at most
 * RECENT_LIMIT, only http(s), never a query or fragment. Returns the same list when nothing changes (a page reports itself several
 * times per load: address, title, history), so the caller can skip storing it.
 */
export function rememberSite(recent, url, title, at = null) {
  const address = siteAddress(url);
  if (!address) return recent;
  // The visit time (ms) lets the Favorites tab apply its time span; entries of older versions have none (at = null).
  const entry = { url: address, title: (typeof title === 'string' && title) || host(address), ...(Number.isFinite(at) ? { at } : {}) };
  // The same page reported again within a minute changes nothing (a page reports itself several times).
  if (recent[0]?.url === entry.url && recent[0]?.title === entry.title && (!Number.isFinite(at) || Math.abs(at - (recent[0].at ?? 0)) < 60_000)) return recent;
  return [entry, ...recent.filter((r) => siteAddress(r?.url) !== address)].slice(0, RECENT_LIMIT);
}

/** The recent sites without one page (removed by the user); the same list when it is not in there. */
export function forgetSite(recent, url) {
  const address = siteAddress(url) ?? url;
  const next = (recent ?? []).filter((r) => (siteAddress(r?.url) ?? r?.url) !== address);
  return next.length === (recent ?? []).length ? recent : next;
}

/**
 * A stored list made safe again: lists of older versions kept the full address (with query and fragment) and may hold the same
 * page several times. Each entry goes through rememberSite, oldest first, so the result follows the same rules as a new visit.
 */
export function cleanRecent(list) {
  if (!Array.isArray(list)) return [];
  return list.slice(0, 50).reverse().reduce((acc, r) => rememberSite(acc, r?.url, r?.title, Number.isFinite(r?.at) ? r.at : null), []);
}

/**
 * Whether the page the host reports goes into the recent sites: an open page, but not while signing in to Home Assistant (signIn),
 * so its sign-in page stays out of the list.
 */
export function rememberable(browserState) {
  return !!browserState?.open && browserState.signIn !== true;
}

/**
 * The recent sites the start page shows: none unless the host said they may be seen (recentHidden === false: the couch is open and
 * parental controls are off, or the parents' code was given for them in this session). A locked couch, parental controls, an
 * unknown state (no answer from the host yet) and an old host without the field all hide titles and addresses: a child sees no
 * browsing history. Bookmarks are not repeated as recent sites.
 */
export function visibleRecent(recent, browserState, limit = 4) {
  if (!visibleRecentAllowed(browserState)) return [];
  const known = new Set((browserState.bookmarks ?? []).map((b) => siteAddress(b.url) ?? b.url));
  return (recent ?? []).filter((r) => !known.has(siteAddress(r.url) ?? r.url)).slice(0, limit);
}

/**
 * The recent sites inside the Favorites tab's time span (days; 0 or less = unlimited). An entry without a visit time (kept by an
 * older version) counts as outside a limited span: the safer choice, old history never comes back for a window it cannot prove.
 */
export function recentWithin(list, days, now = Date.now()) {
  if (!(days > 0)) return list ?? [];
  const since = now - days * 86_400_000;
  return (list ?? []).filter((r) => Number.isFinite(r?.at) && r.at >= since);
}

/**
 * The note in place of hidden recent sites (only when there are some): 'parent' when only parental controls hide them (the parents'
 * code shows them), 'lock' otherwise (the couch code; also for an unknown state), null when they are shown.
 */
export function recentNote(browserState, hasRecent) {
  if (!hasRecent || visibleRecentAllowed(browserState)) return null;
  return browserState?.locked === false && browserState?.recentNeedsParent === true ? 'parent' : 'lock';
}

function visibleRecentAllowed(browserState) {
  return browserState?.recentHidden === false && browserState?.locked === false;
}

/** "12,3 MB" style size for a download question (the HUD's one size format, decimal units); empty when unknown. */
export const fileSize = (bytes, locale = 'de') => formatBytes(bytes, locale) ?? '';

/**
 * The controller's place between the HUD and the page, kept in step with the host. The host is the authority: it gives the page
 * the controller only while the page is on screen, and it moves the controller itself (a button on the page, full screen, the
 * watchdog). The HUD follows every answer and every report, so it never shows "page" while the host steers the HUD (a browser
 * that seemed not to react any more).
 */
export function reconcileFocus(view, { requested, granted }) {
  // The HUD asked for the page and the host said no (the page was not on screen yet): back to the bar.
  if (requested && granted === false) return 'bar';
  return view;
}

/** What the HUD shows for a page's question (alert, confirm, prompt, download): always with the site's name, so a page cannot pass it off as CouchHUD's own. */
export function dialogKind(d) {
  if (!d || typeof d !== 'object') return null;
  if (d.closed) return 'closed';
  return ['alert', 'confirm', 'prompt', 'download'].includes(d.kind) ? d.kind : null;
}

/** Even a playing video gets the screensaver after this long without any input (OLED panels, someone fell asleep). */
export const HOLD_LIMIT = 3 * 60 * 60_000;

/** The screensaver is held off: something asks for it (a page that plays) and the last real input is less than the limit ago. */
export function holding(heldNow, now, realInputAt, limit = HOLD_LIMIT) {
  return !!heldNow && now - realInputAt < limit;
}

/**
 * A host for the full-screen strip, at most <max> characters: a longer one loses its middle ("…"), so its start and above all the
 * end of the domain (example.com) stay readable. The strip cuts it further with an ellipsis rather than push out the web mark.
 */
export function shortHost(name, max = 48) {
  const chars = [...String(name ?? '')];
  if (chars.length <= max) return chars.join('');
  const tail = Math.ceil((max - 1) * 0.6);
  const head = max - 1 - tail;
  return `${chars.slice(0, head).join('')}…${chars.slice(chars.length - tail).join('')}`;
}

/**
 * The host as the second line of a recent site or bookmark: without "www." and cut like the full-screen strip (shortHost), so a
 * very long host keeps the end of its domain on one line instead of breaking over several (the row cuts it further, see CSS).
 */
export function siteHost(url, max = 48) {
  return shortHost(host(url), max);
}

/**
 * Whole seconds until the unlock runs out and the browser closes, while the host's warning is up (closesInMs, sent with the
 * browser state at <paramref receivedAt>); null without a warning. Counts down on the HUD's own clock between two states; never
 * below 0. Only the host's number counts: nothing of the page is read here.
 */
export function closeCountdown(browserState, receivedAt, now) {
  const ms = browserState?.closesInMs;
  if (typeof ms !== 'number' || !Number.isFinite(ms) || ms <= 0) return null;
  const elapsed = Math.max(0, now - receivedAt);
  return Math.max(0, Math.ceil((ms - elapsed) / 1000));
}

/** The action the parents' code is given for when the history is switched off (BrowserRecentPrivacy.HistoryOffAction in Core). */
export const HISTORY_OFF_ACTION = 'browserHistoryOff';

/**
 * Switching the history off deletes the remembered sites: with parental controls on that is the parents' (the host asks for their
 * code, for exactly this). Switching it on stays free. Mirrors BrowserRecentPrivacy.HistoryChangeNeedsParent in Core.
 */
export function historyOffNeedsParent(parentalOn, historyOn, next) {
  return parentalOn === true && historyOn !== false && next === false;
}

/**
 * The remembered sites go only once the host has the history off (its browser state says history: false), never on a switch the
 * host refused.
 */
export function forgetRecent(browserState) {
  return browserState?.history === false;
}
