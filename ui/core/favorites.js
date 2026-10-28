// The Favorites tab's data (the home view): the host arranges it (mode, areas, the person's rules; Hud.GetFavorites), the page only
// shows it. Until the host answered, the shelf has no entries: nothing a child may not see is ever drawn from an older guess.
import { api } from './api.js';
import { attempt, toast } from './feedback.js';
import { currentView, render } from './nav.js';
import { t } from './i18n.js';
import { sound } from '../sound.js';
import { state } from './state.js';

export const favorites = { mode: 'combined', sites: false, entries: [], blocked: new Set(), loaded: false };

let sequence = 0;

/** Back to "nothing known": a profile switch, a lock or a failed answer must never leave another person's arrangement on the screen. */
export function resetFavorites() {
  sequence++;
  Object.assign(favorites, { mode: 'combined', sites: false, entries: [], blocked: new Set(), loaded: false });
}

/** Asks the host for the arrangement again (library, favorites, settings, areas, profile or rules changed). Redraws when it arrived. */
export async function refreshFavorites() {
  if (!api.getFavorites) return;
  const mine = ++sequence;
  const answer = await attempt(() => api.getFavorites(), t('favorites.loadFailed'), { quietLocked: true });
  if (mine !== sequence) return;
  // No answer (failed, held at the picker, locked): nothing stays of the earlier one.
  if (!answer) {
    resetFavorites();
    render();
    return;
  }
  // The tiles may change places; the focus stays on the same entry.
  const view = currentView();
  if (view?.list && !state.stack.length) view.focusId = view.list[view.index]?.id;
  favorites.mode = answer.mode ?? 'combined';
  favorites.sites = !!answer.sites;
  favorites.entries = Array.isArray(answer.entries) ? answer.entries : [];
  favorites.blocked = new Set(Array.isArray(answer.blocked) ? answer.blocked : []);
  favorites.loaded = true;
  render();
}

/** A favorite website off, or a bookmark on (only while websites are on for Favorites: the host decides). */
export async function toggleFavoriteSite(url, name) {
  const marked = await attempt(() => api.toggleFavoriteSite(url));
  if (marked === undefined) return undefined;
  sound.tab();
  toast(marked ? t('library.favoriteOn', { name }) : t('library.favoriteOff', { name }));
  await refreshFavorites();
  return marked;
}

/** The entries as the home view draws them: library entries from the page's library, websites as small objects of their own. */
export function shelfEntries(library) {
  const byId = new Map(library.map((i) => [i.id, i]));
  return favorites.entries
    .map((e) => (e.kind === 'site' ? { id: e.id, name: e.name, site: true, builtin: true, url: e.url, favorite: !!e.favorite, art: {} } : byId.get(e.id)))
    .filter((i) => i && !i.hidden);
}
