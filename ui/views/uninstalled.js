// Games of Steam and Epic that the account has but that are not installed here, as a paged cover grid. A on a game asks the store's
// client to install it through its own install link (the client's dialog asks where and how much space); the line then shows up
// in the Downloads tab. The host reads only local files of the clients (no account, no web); see src/CouchHud.Core/Library/OwnedGames.cs.

import { api } from '../core/api.js';
import { el, keepInView, setFocused } from '../core/dom.js';
import { artwork, plainBackground, setBackground } from '../core/art.js';
import { attempt, toast } from '../core/feedback.js';
import { currentView, move, render } from '../core/nav.js';
import { SOURCE_LABEL } from '../core/library.js';
import { chooseDialog, confirmDialog } from '../components/overlay.js';
import { openKeyboard } from '../components/keyboard.js';
import { ensureParentGrant, parental } from '../components/parental.js';
import { ensureUnlocked } from '../components/passcode.js';
import { sound } from '../sound.js';
import { t } from '../core/i18n.js';
import { step } from '../core/paging.js';

export const PAGE_SIZE = 56;
const COLUMNS = 7;

let unsubscribed = true;
let artHook = null;

export function uninstalledPage() {
  const page = {
    id: 'uninstalled',
    get name() { return t('uninstalled.title'); },
    index: 0,
    offset: 0,
    source: '',
    query: '',
    data: null,
    loading: false,
    async load({ refresh = false, keep = true } = {}) {
      if (this.loading) return;
      this.loading = true;
      const d = await attempt(() => api.getUninstalled({ offset: this.offset, limit: PAGE_SIZE, source: this.source || null, query: this.query || null, refresh }), t('uninstalled.loadFailed'));
      this.loading = false;
      if (d) {
        this.data = d;
        this.offset = d.offset;
        if (!keep) this.index = 0;
      } else if (!this.data) {
        this.data = { total: 0, offset: 0, items: [], counts: {}, scanning: false };
      }
      if (currentView() === this) render();
      // Another call started the first scan: its list is not there yet. Ask again shortly instead of showing "none".
      if (this.data?.scanning && this.data.scannedAt == null) {
        clearTimeout(this.retry);
        this.retry = setTimeout(() => currentView() === this && this.load(), 1500);
      }
    },
    onClose() {
      clearTimeout(this.retry);
      artHook = null;
    },
    build(root) {
      // Covers arrive in the image cache after the page asked: ask again quietly, keeping the cursor.
      artHook = () => currentView() === this && this.load();
      if (unsubscribed) {
        unsubscribed = false;
        api.onUninstalledArt?.(() => artHook?.());
      }
      if (!this.data || (this.data.scanning && this.data.scannedAt == null && !this.data.items.length)) {
        root.append(el('p', { className: 'empty-note', textContent: t('uninstalled.scanning') }));
        this.tiles = [];
        if (!this.data) this.load();
        return;
      }
      const d = this.data;
      const games = d.items;
      if (!games.length) {
        plainBackground();
        this.tiles = [];
        const filtered = this.source || this.query;
        root.append(el('div', { className: 'empty' },
          el('h2', { textContent: filtered ? t('uninstalled.noneFiltered') : t('uninstalled.none') }),
          el('p', { textContent: filtered ? t('uninstalled.noneFilteredText') : t('uninstalled.noneText') })));
        return;
      }
      this.index = Math.min(this.index, games.length - 1);
      const grid = el('div', { className: 'grid grid-covers grid-uninstalled' });
      grid.style.setProperty('--cols', COLUMNS);
      this.tiles = games.map((g, i) => {
        const tile = el('div', { className: 'tile tile-uninstalled', dataset: { index: i } },
          artwork(g, 'cover', 'cover'),
          el('span', { className: 'tile-badge', textContent: '⤓' }));
        grid.append(tile);
        return tile;
      });
      this.title = el('h2');
      this.meta = el('span', { className: 'meta' });
      this.scroller = el('div', { className: 'grid-scroll' }, grid);
      root.append(el('div', { className: 'grid-title' }, this.title, this.meta), this.scroller);
      this.update({ instant: true });
    },
    filterLabel() {
      return [this.source ? SOURCE_LABEL[this.source] : null, this.query ? `„${this.query}“` : null].filter(Boolean).join(' · ');
    },
    update({ instant = false } = {}) {
      if (!this.tiles?.length) return;
      const d = this.data;
      const game = d.items[this.index];
      setFocused(this.tiles, this.index);
      this.title.textContent = game.name;
      const filter = this.filterLabel();
      this.meta.textContent = `${SOURCE_LABEL[game.source] ?? game.source} · ${t('uninstalled.notInstalled')} · ${d.offset + this.index + 1} / ${d.total}${filter ? ` · ${filter}` : ''}`;
      setBackground({ ...game, art: { ...game.art, hero: [] } });
      keepInView(this.scroller, this.tiles[this.index], { center: true, instant });
    },
    go(delta) {
      const d = this.data;
      if (!d?.items.length) return sound.bump();
      const target = step({ index: this.index, offset: d.offset, count: d.items.length, total: d.total }, delta, PAGE_SIZE);
      if (!target) return sound.bump();
      if (target.offset === d.offset) return move(this, target.index - this.index, d.items.length, true);
      sound.move();
      this.offset = target.offset;
      this.index = target.index;
      return this.load();
    },
    async filterMenu() {
      const counts = this.data?.counts ?? {};
      const all = (counts.steam ?? 0) + (counts.epic ?? 0);
      const choice = await chooseDialog(t('uninstalled.filterTitle'), [
        { icon: '⌕', label: t('uninstalled.search'), detail: this.query ? t('uninstalled.searchCurrent', { query: this.query }) : '', value: 'search' },
        { icon: '▦', label: t('uninstalled.all'), detail: t('uninstalled.count', { count: all }), value: 'all' },
        { icon: '▦', label: 'Steam', detail: t('uninstalled.count', { count: counts.steam ?? 0 }), value: 'steam' },
        { icon: '▦', label: 'Epic Games', detail: t('uninstalled.count', { count: counts.epic ?? 0 }), value: 'epic' },
        { icon: '↻', label: t('uninstalled.rescan'), detail: t('uninstalled.rescanHint'), value: 'rescan' }
      ], this.source || 'all');
      if (!choice) return;
      if (choice === 'search') {
        const query = await openKeyboard({ title: t('uninstalled.search'), value: this.query, placeholder: t('uninstalled.searchPlaceholder') });
        if (query === undefined) return;
        this.query = query.trim();
      } else if (choice !== 'rescan') {
        this.source = choice === 'all' ? '' : choice;
      }
      this.offset = 0;
      this.index = 0;
      this.data = choice === 'rescan' ? null : this.data;
      render();
      return this.load({ refresh: choice === 'rescan', keep: false });
    },
    handle(action) {
      switch (action) {
        case 'x':
          sound.select();
          return this.filterMenu();
        case 'left': return this.go(-1);
        case 'right': return this.go(1);
        case 'up': return this.go(-COLUMNS);
        case 'down': return this.go(COLUMNS);
        case 'a': {
          const game = this.data?.items[this.index];
          if (!game) return sound.bump();
          sound.select();
          return installGame(game);
        }
      }
    },
    pointer(node, index, dispatch) {
      this.index = index;
      this.update();
      dispatch('a');
    },
    hints() {
      return this.tiles?.length
        ? [['A', t('uninstalled.hintInstall')], ['X', t('uninstalled.hintFilter')], ['B', t('uninstalled.hintBack')]]
        : [['X', t('uninstalled.hintFilter')], ['B', t('uninstalled.hintBack')]];
    }
  };
  return page;
}

/**
 * Asks the store's client to install the game. On a locked couch first the unlock; with parental controls on the parents' code for
 * exactly this game (the host checks both again). The client then shows its own dialog (folder, space), which CouchHUD lets to the front.
 */
export async function installGame(game) {
  const client = SOURCE_LABEL[game.source] ?? game.source;
  const sure = await confirmDialog(t('uninstalled.confirmTitle', { name: game.name }), t('uninstalled.installAction'), {
    icon: '⤓',
    message: game.source === 'epic' ? t('uninstalled.confirmEpic', { client }) : t('uninstalled.confirmSteam', { client })
  });
  if (!sure) return;
  // First the couch, then the parents: the parents' code alone does not open a locked couch (as for the VPN).
  if (!(await ensureUnlocked(t('uninstalled.unlock')))) return;
  if (parental.status?.enabled && !(await ensureParentGrant(t('uninstalled.guard'), { action: 'installGame', item: game.id }))) return;
  const result = await attempt(() => api.installGame(game.id), t('uninstalled.installFailed'));
  if (result !== undefined) toast(t('uninstalled.asked', { client }), 6000);
}
