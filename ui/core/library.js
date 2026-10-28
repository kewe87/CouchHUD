import { api } from './api.js';
import { locale, t } from './i18n.js';
import { state } from './state.js';
import { attempt, toast } from './feedback.js';
import { keepFocusOn, render } from './nav.js';
import { relativeTime, formatDuration } from './dom.js';
import { sound } from '../sound.js';
import { parentalGate } from '../components/parental.js';
import { ensureUnlocked } from '../components/passcode.js';
import { cancelLaunch, showLaunch } from '../components/launch-overlay.js';
import { refreshFavorites } from './favorites.js';

export const SOURCE_LABEL = {
  steam: 'Steam',
  epic: 'Epic Games',
  gog: 'GOG',
  ubisoft: 'Ubisoft Connect',
  xbox: 'Xbox',
  get folder() { return t('library.sourceFolder'); },
  get custom() { return t('library.sourceCustom'); },
  couchhud: 'CouchHUD'
};

export function sortLibrary(items) {
  return [...items].sort(
    (a, b) => (b.favorite - a.favorite) || (b.lastPlayed - a.lastPlayed) || a.name.localeCompare(b.name, locale())
  );
}

const visible = () => state.library.filter((i) => !i.hidden);
export const games = () => sortLibrary(visible().filter((i) => i.type === 'game'));
export const apps = () => sortLibrary(visible().filter((i) => i.type === 'app'));
export const recent = (n) => sortLibrary(visible()).slice(0, n);

export function itemMeta(item) {
  const parts = [SOURCE_LABEL[item.source] || item.source];
  if (item.type === 'app') parts.push(t('library.app'));
  else {
    parts.push(relativeTime(item.lastPlayed));
    const played = formatDuration(item.playtimeMinutes);
    if (played) parts.push(t('library.played', { time: played }));
  }
  return parts.filter(Boolean).join(' · ');
}

export const findItem = (id) => state.library.find((i) => i.id === id);

export async function launchItem(item) {
  // The one way to start anything (Home, apps, collections, search, history, guide, plugins all end here). A locked couch starts
  // games only (the host decides the same, see LockPolicy); apps and websites ask for the code first instead of failing.
  if (item.type !== 'game' && !(await ensureUnlocked(t('library.unlockToOpen', { name: item.name })))) return;
  if (!(await parentalGate(item))) return;
  sound.select();
  // A website in the couch browser needs no launch overlay: the browser is the answer.
  const embedded = item.launch?.embedded === true;
  if (!embedded) showLaunch(item);
  const updated = await attempt(() => api.launch(item.id), t('library.launchFailed'));
  if (!updated) return embedded ? undefined : cancelLaunch(item);
  Object.assign(item, updated);
  if (!state.stack.length) keepFocusOn(item);
  render();
}

export async function toggleFavorite(item) {
  const favorite = await attempt(() => api.toggleFavorite(item.id));
  if (favorite === undefined) return;
  item.favorite = favorite;
  sound.tab();
  toast(item.favorite ? t('library.favoriteOn', { name: item.name }) : t('library.favoriteOff', { name: item.name }));
  if (!state.stack.length) keepFocusOn(item);
  render();
  refreshFavorites();
}

export async function reloadLibrary(rescan = false) {
  if (rescan) toast(t('library.scanning'));
  const library = await attempt(() => api.getLibrary({ rescan }), t('library.loadFailed'));
  if (!library) return;
  setLibrary(library);
  if (rescan) toast(t('library.found', { count: library.length }));
}

/** Replaces the library and points open pages at the fresh item objects. */
export function setLibrary(items) {
  state.library = items;
  for (const page of state.stack) {
    if (page.item) page.item = findItem(page.item.id) ?? page.item;
  }
  render();
  refreshFavorites();
}
