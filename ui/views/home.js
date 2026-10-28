// i18n-used: home.meta.pausedSince home.meta.runningSince
import { iconEl } from '../core/glyph-icons.js';
import { el, entriesLabel, formatDuration, relativeTime, setFocused, withoutTransition } from '../core/dom.js';
import { artwork, plainBackground, setBackground, showClearLogo } from '../core/art.js';
import { games, itemMeta, recent, toggleFavorite } from '../core/library.js';
import { continueFacts, homeShelf } from '../core/continue-rules.js';
import { move, push } from '../core/nav.js';
import { api } from '../core/api.js';
import { attempt } from '../core/feedback.js';
import { state } from '../core/state.js';
import { changeCollections, collectionItems } from '../core/collections.js';
import { openOverlay } from '../components/overlay.js';
import { detailPage } from './detail.js';
import { scheduleTrailer } from '../core/media.js';
import { t } from '../core/i18n.js';
import { homeEndTile } from '../core/areas.js';

// i18n-used: home.allGames home.allApps home.openBrowser
const END_LABEL = { games: 'home.allGames', apps: 'home.allApps', browser: 'home.openBrowser' };

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

/** Horizontal shelf, console home screen style: "Continue playing" first (its first card big), then recent and favorite things. */
export const homeView = {
  get name() { return t('home.name'); },
  index: 0,
  wheelAxis: 'x',
  keepsTrailer: true,
  items() {
    // The running game comes first, like on a console: one press and you're back in. Then the games played last.
    const s = state.session;
    const running = s ? state.library.find((i) => i.id === s.id) ?? { id: s.id, name: s.name, art: {} } : null;
    // Collections the user pinned sit at the end of the shelf, before the "All games" tile ("All apps" or the browser without Gaming).
    const shelves = state.collections.filter((c) => c.onHome).map((c) => ({ id: `__col:${c.id}`, name: c.name, special: true, collection: c, members: collectionItems(c) }));
    const end = homeEndTile();
    const shelf = homeShelf({
      running: running ? { ...running, running: true } : null,
      library: state.library,
      recent: recent(14),
      extras: [...shelves, { id: end.id, name: t(END_LABEL[end.tab]), special: true, tab: end.tab }]
    });
    this.continueIds = shelf.continueIds;
    this.heroId = shelf.heroId;
    return shelf.list;
  },
  build(root) {
    this.list = this.items();
    this.index = Math.min(this.index, this.list.length - 1);

    this.row = el('div', { className: 'shelf' });
    this.tiles = this.list.map((item) => {
      const tile = item.special
        ? el('div', { className: 'tile tile-special' }, el('span', { className: 'special-icon' }, iconEl(item.collection ? '▤' : '▦')), el('span', { textContent: item.name }))
        : item.id === this.heroId
          // The first "Continue playing" card is the big one: the wide hero picture (the cover as a stand-in).
          ? el('div', { className: `tile tile-hero${item.running ? ' running' : ''}` },
            artwork({ ...item, art: { ...item.art, hero: [...(item.art?.hero ?? []), ...(item.art?.cover ?? [])] } }, 'hero', 'hero-art'),
            item.favorite ? el('span', { className: 'fav', textContent: '★' }) : null,
            item.running ? el('span', { className: 'running-badge', textContent: state.session?.paused ? t('home.badge.paused') : t('home.badge.running') }) : null)
          : el('div', { className: `tile${item.running ? ' running' : ''}` }, artwork(item, 'cover', 'cover'),
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
  update({ instant = false } = {}) {
    const focused = this.list[this.index];
    setFocused(this.tiles, this.index);

    if (focused.collection) {
      this.title.textContent = focused.name;
      this.meta.textContent = t('home.meta.collection', { entries: entriesLabel(focused.members.length) });
      if (focused.members.length) setBackground(focused.members[0]);
      else plainBackground();
    } else if (focused.special) {
      this.title.textContent = t('home.library');
      this.meta.textContent = t('home.meta.installed', { count: games().length });
      plainBackground();
    } else {
      this.title.textContent = focused.name;
      this.meta.textContent = focused.running && state.session
        ? t(state.session.paused ? 'home.meta.pausedSince' : 'home.meta.runningSince', { duration: formatDuration(Math.max(1, (Date.now() - state.session.started) / 60000)) })
        : this.continueIds?.has(focused.id) ? continueMeta(focused) : itemMeta(focused);
      setBackground(focused);
    }
    this.section.classList.toggle('shown', !!this.continueIds?.has(focused.id));
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
    if (item.collection) return window.dispatchEvent(new CustomEvent('couchhud:show-collection', { detail: { id: item.collection.id } }));
    if (item.special) return window.dispatchEvent(new CustomEvent('couchhud:open-tab', { detail: item.tab }));
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
    switch (action) {
      case 'left': return move(this, -1, this.list.length);
      case 'right': return move(this, 1, this.list.length);
      case 'a': return this.activate();
      case 'y':
        if (item.collection) return this.collectionMenu(item.collection);
        return !item.special && toggleFavorite(item);
      case 'x': return item.running && push(detailPage(item));
    }
  },
  hints() {
    const item = this.list?.[this.index];
    if (item?.running) return [['A', t('home.hint.resume')], ['X', t('home.hint.details')], ['Y', item.favorite ? t('home.hint.unfavorite') : t('home.hint.favorite')], ['≡', t('home.hint.power')]];
    if (item?.collection) return [['A', t('home.hint.openCollection')], ['Y', t('home.hint.menu')], ['≡', t('home.hint.power')]];
    if (item?.special) return [['A', item.name], ['≡', t('home.hint.power')]];
    return [['A', t('home.hint.open')], ['Y', item?.favorite ? t('home.hint.unfavorite') : t('home.hint.favorite')], ['≡', t('home.hint.power')]];
  }
};
