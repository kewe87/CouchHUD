// i18n-used: home.meta.pausedSince home.meta.runningSince
import { iconEl } from '../core/glyph-icons.js';
import { el, entriesLabel, formatDuration, relativeTime, setFocused, withoutTransition } from '../core/dom.js';
import { artwork, plainBackground, setBackground, showClearLogo } from '../core/art.js';
import { itemMeta, toggleFavorite } from '../core/library.js';
import { favorites, refreshFavorites, shelfEntries, toggleFavoriteSite } from '../core/favorites.js';
import { host, initial } from '../core/browser-logic.js';
import { hue } from '../core/art.js';
import { open as openSite, ensureBrowserState, recentSitesNow } from './browser.js';
import { continueDays, continueFacts, emptyHint, homeShelf, shelfHeading } from '../core/continue-rules.js';
import { move, push } from '../core/nav.js';
import { api } from '../core/api.js';
import { attempt } from '../core/feedback.js';
import { state } from '../core/state.js';
import { changeCollections, collectionItems } from '../core/collections.js';
import { openOverlay } from '../components/overlay.js';
import { detailPage } from './detail.js';
import { scheduleTrailer } from '../core/media.js';
import { t } from '../core/i18n.js';
import { glyph } from '../core/skins.js';
import { enabledAreas } from '../core/areas.js';

/** A website in the shelf: its first letter on a color made from the address (like the browser's start page). */
function siteTile(item) {
  const badge = el('span', { className: 'site-initial', textContent: initial(item.url) });
  const tile = el('div', { className: 'tile tile-site' }, badge, el('span', { className: 'site-name', textContent: item.name }),
    item.favorite ? el('span', { className: 'fav', textContent: '★' }) : null);
  tile.style.setProperty('--h', hue(item.url));
  return tile;
}

/** The line under the title for a "Continue playing" card: last played, total play time, last session. */
function continueMeta(item) {
  const facts = continueFacts(item);
  const total = formatDuration(facts.total);
  const last = formatDuration(facts.last);
  return [
    facts.lastPlayed ? relativeTime(facts.lastPlayed) : null,
    total ? t('library.played', { time: total }) : null,
    last ? t('home.continue.lastSession', { time: last }) : null
  ].filter(Boolean).join(' · ');
}

/**
 * The Favorites tab (the view keeps its old name "home"): "Continue playing" first (its first card big), then favorites and/or recent
 * entries of games, apps and, if switched on, websites, as the host arranged them (setting "Favorites shows"; Hud.GetFavorites).
 */
export const homeView = {
  get name() { return t('home.name'); },
  index: 0,
  wheelAxis: 'x',
  keepsTrailer: true,
  /** Recent sites (not bookmarks) behind the entries: only with websites on and a mode that shows recent entries; never favorites. */
  recentSites() {
    if (!favorites.loaded || !favorites.sites || favorites.mode === 'favorites') return [];
    return recentSitesNow(4, favorites.recentDays).map((s) => ({ id: `recent:${s.url}`, name: s.title, site: true, builtin: true, url: s.url, favorite: false, art: {} }));
  },
  onEnter() {
    // Bookmarks, settings or the person's rules may have changed since the last visit.
    refreshFavorites();
  },
  items() {
    // The running game comes first, like on a console: one press and you're back in. Then the games played last.
    const s = state.session;
    const running = s ? state.library.find((i) => i.id === s.id) ?? { id: s.id, name: s.name, art: {} } : null;
    // Collections the user pinned sit at the end of the shelf.
    const shelves = state.collections.filter((c) => c.onHome).map((c) => ({ id: `__col:${c.id}`, name: c.name, special: true, collection: c, members: collectionItems(c) }));
    const shelf = homeShelf({
      running: running ? { ...running, running: true } : null,
      // Until the host has answered (start, profile switch, a failed answer) nothing but the running game: what a person may not see
      // is only known from that answer.
      library: favorites.loaded ? state.library : [],
      recent: favorites.loaded ? [...shelfEntries(state.library), ...this.recentSites()] : [],
      blocked: favorites.blocked,
      days: favorites.loaded ? continueDays(favorites.mode, favorites.recentDays) : 0,
      extras: shelves
    });
    this.continueIds = shelf.continueIds;
    this.heroId = shelf.heroId;
    return shelf.list;
  },
  build(root) {
    if (favorites.sites) ensureBrowserState();
    this.list = this.items();
    if (this.focusId) {
      const at = this.list.findIndex((i) => i.id === this.focusId);
      if (at >= 0) this.index = at;
      this.focusId = null;
    }
    this.index = Math.max(0, Math.min(this.index, this.list.length - 1));

    if (!this.list.length) return this.buildEmpty(root);
    this.row = el('div', { className: 'shelf' });
    this.tiles = this.list.map((item) => {
      const tile = item.special
        ? el('div', { className: 'tile tile-special' }, el('span', { className: 'special-icon' }, iconEl(item.collection ? '▤' : '▦')), el('span', { textContent: item.name }))
        : item.site
          ? siteTile(item)
          : item.id === this.heroId
          // The first "Continue playing" card is the big one: the wide hero picture (the cover as a stand-in).
          ? el('div', { className: `tile tile-hero${item.running ? ' running' : ''}` },
            artwork({ ...item, art: { ...item.art, hero: [...(item.art?.hero ?? []), ...(item.art?.cover ?? [])] } }, 'hero', 'hero-art'),
            item.favorite ? el('span', { className: 'fav', textContent: '★' }) : null,
            item.running ? el('span', { className: 'running-badge', textContent: state.session?.paused ? t('home.badge.paused') : t('home.badge.running') }) : null)
          : el('div', { className: `tile${item.running ? ' running' : ''}` }, artwork(item, 'cover', 'cover', { tile: true }),
          item.favorite ? el('span', { className: 'fav', textContent: '★' }) : null,
          item.running ? el('span', { className: 'running-badge', textContent: state.session?.paused ? t('home.badge.paused') : t('home.badge.running') }) : null);
      this.row.append(tile);
      return tile;
    });
    this.tiles.forEach((t, i) => (t.dataset.index = i));
    this.title = el('h1');
    // No src until a logo has loaded (showClearLogo): an empty <img> would show a broken-image box.
    this.logo = el('img', { className: 'hero-logo', alt: '', hidden: true });
    this.meta = el('p', { className: 'meta' });
    // Names the part of the shelf the focus is in; it only fades, so the shelf never moves.
    this.section = el('p', { className: 'shelf-label', textContent: t('home.continue.title') });

    root.append(el('div', { className: 'home' },
      el('section', { className: 'hero-info' }, this.logo, this.title, this.meta),
      el('div', { className: 'shelf-wrap' }, this.section, this.row)));
    this.update({ instant: true });
  },
  /** Nothing to show (no running game, no favorites, nothing used lately, no pinned collection): say how a favorite is made. */
  buildEmpty(root) {
    this.tiles = [];
    this.heroId = null;
    this.continueIds = new Set();
    const empty = emptyHint({ mode: favorites.loaded ? favorites.mode : 'combined', areas: enabledAreas(), sites: favorites.sites });
    // i18n-used: home.empty.hint home.empty.hintMany home.empty.hintPlain home.empty.recent
    const hint = t(empty.key, { y: glyph('Y'), tabs: empty.tabs.map((k) => t(k)).join(' / ') });
    root.append(el('div', { className: 'home' },
      el('section', { className: 'hero-info' }, el('h1', { textContent: t('home.empty.title') }), el('p', { className: 'meta', textContent: hint }))));
    plainBackground();
  },
  update({ instant = false } = {}) {
    if (!this.list.length) return;
    const focused = this.list[this.index];
    setFocused(this.tiles, this.index);

    if (focused.site) {
      this.title.textContent = focused.name;
      this.meta.textContent = host(focused.url);
      plainBackground();
    } else if (focused.collection) {
      this.title.textContent = focused.name;
      this.meta.textContent = t('home.meta.collection', { entries: entriesLabel(focused.members.length) });
      if (focused.members.length) setBackground(focused.members[0]);
      else plainBackground();
    } else {
      this.title.textContent = focused.name;
      this.meta.textContent = focused.running && state.session
        ? t(state.session.paused ? 'home.meta.pausedSince' : 'home.meta.runningSince', { duration: formatDuration(Math.max(1, (Date.now() - state.session.started) / 60000)) })
        : this.continueIds?.has(focused.id) ? continueMeta(focused) : itemMeta(focused);
      setBackground(focused);
    }
    // The label names the part the focus is in. Combined mode: entries that are no favorites are the recently played ones.
    const heading = shelfHeading({ inContinue: !!this.continueIds?.has(focused.id), special: !!focused.special, favorite: !!focused.favorite, mode: favorites.mode, loaded: favorites.loaded });
    // i18n-used: home.recent.title home.continue.title
    if (heading) this.section.textContent = t(heading === 'recent' ? 'home.recent.title' : 'home.continue.title');
    this.section.classList.toggle('shown', !!heading);
    showClearLogo(this, focused);
    scheduleTrailer(focused);

    // Keep focus near the left edge like on a console: one tile of context on the left.
    const prev = this.tiles[this.index - 1];
    // A short shelf that fits on screen never scrolls; a long one stops when its last tile is in view.
    const first = this.tiles[0].offsetLeft;
    const last = this.tiles.at(-1);
    const wrap = this.row.parentElement;
    const pad = wrap ? parseFloat(getComputedStyle(wrap).paddingLeft) + parseFloat(getComputedStyle(wrap).paddingRight) : 0;
    // The focused tile grows a little, so keep a margin of half its growth at the right edge.
    const overflow = Math.max(0, last.offsetLeft + last.offsetWidth - first - ((wrap?.clientWidth ?? Infinity) - pad) + last.offsetWidth * 0.06);
    const offset = prev ? Math.min(prev.offsetLeft - first, overflow) : 0;
    const apply = () => (this.row.style.transform = `translateX(${-offset}px)`);
    if (instant) withoutTransition(this.row, apply);
    else apply();
  },
  activate() {
    const item = this.list[this.index];
    if (!item) return undefined;
    if (item.collection) return window.dispatchEvent(new CustomEvent('couchhud:show-collection', { detail: { id: item.collection.id } }));
    if (item.site) return openSite(item.url);
    if (item.running) return attempt(() => api.resumeGame(), t('home.resumeFailed'));
    push(detailPage(item));
  },
  /** Y on a pinned collection: open it or take it off Home (the collection itself stays). */
  collectionMenu(collection) {
    openOverlay({
      title: collection.name,
      entries: [
        { icon: '▤', label: t('home.menu.open'), run: () => this.activate() },
        {
          icon: '🗑',
          label: t('home.menu.remove'),
          detail: t('home.menu.removeHint'),
          run: () => changeCollections(() => api.setCollectionOnHome(collection.id, false), t('home.menu.removed', { name: collection.name }))
        },
        { icon: '✕', label: t('home.cancel'), run: () => {} }
      ]
    });
  },
  handle(action) {
    const item = this.list[this.index];
    if (!item) return undefined;
    switch (action) {
      // The row goes round: right on the last tile goes to the first, left on the first to the last.
      case 'left': return move(this, -1, this.list.length, false, true);
      case 'right': return move(this, 1, this.list.length, false, true);
      case 'a': return this.activate();
      case 'y':
        if (item.collection) return this.collectionMenu(item.collection);
        // A favorite website: Y takes it off. A recent site is no bookmark, so it cannot be one (mark it in the browser first).
        if (item.site) return item.favorite ? toggleFavoriteSite(item.url, item.name) : undefined;
        return toggleFavorite(item);
      case 'x': return item.running && push(detailPage(item));
    }
  },
  hints() {
    const item = this.list?.[this.index];
    if (!item) return [['≡', t('home.hint.power')]];
    if (item?.running) return [['A', t('home.hint.resume')], ['X', t('home.hint.details')], ['Y', item.favorite ? t('home.hint.unfavorite') : t('home.hint.favorite')], ['≡', t('home.hint.power')]];
    if (item?.collection) return [['A', t('home.hint.openCollection')], ['Y', t('home.hint.menu')], ['≡', t('home.hint.power')]];
    if (item?.site) return [['A', t('home.hint.open')], ...(item.favorite ? [['Y', t('home.hint.unfavorite')]] : []), ['≡', t('home.hint.power')]];
    return [['A', t('home.hint.open')], ['Y', item?.favorite ? t('home.hint.unfavorite') : t('home.hint.favorite')], ['≡', t('home.hint.power')]];
  }
};
