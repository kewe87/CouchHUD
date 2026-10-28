import { iconEl } from '../core/glyph-icons.js';
import { t } from '../core/i18n.js';
import { api } from '../core/api.js';
import { el, formatDuration, formatSize, loadFirst, relativeTime, setFocused } from '../core/dom.js';
import { setBackground } from '../core/art.js';
import { scheduleTrailer } from '../core/media.js';
import { attempt, toast } from '../core/feedback.js';
import { SOURCE_LABEL, launchItem, reloadLibrary, toggleFavorite } from '../core/library.js';
import { move, pop, render } from '../core/nav.js';
import { state } from '../core/state.js';
import { sound } from '../sound.js';
import { confirmDialog } from '../components/overlay.js';
import { isLocked } from '../components/passcode.js';
import { offerBackupBeforeUninstall, saveAction, saveSummary } from '../components/savegames.js';
import { ageBadge, badgeText, descriptionState, loadTickets, storeRetryDelay } from '../core/item-info-rules.js';

let rescanOnReturn = false;

// The uninstaller runs outside CouchHUD; look again once we're back in front.
window.addEventListener('focus', async () => {
  if (!rescanOnReturn) return;
  rescanOnReturn = false;
  await reloadLibrary(true);
  const page = state.stack.at(-1);
  if (page?.id === 'detail' && !state.library.some((i) => i.id === page.item.id)) pop();
});

/** Logo image if one loads, otherwise the name as headline. */
function logoOrTitle(item) {
  const title = el('h1', { className: 'detail-title', textContent: item.name });
  loadFirst(item.art?.logo, (url) => {
    const img = el('img', { className: 'detail-logo', src: url, alt: item.name });
    title.replaceWith(img);
  });
  return title;
}

async function uninstall(item) {
  if (!(await confirmDialog(t('detail.uninstallConfirm', { name: item.name }), t('detail.uninstall'), { danger: true }))) return;
  if (!(await offerBackupBeforeUninstall(item))) return;
  const ok = await attempt(() => api.uninstall(item.id), t('detail.uninstallFailed'));
  if (ok === undefined) return;
  toast(t('detail.uninstallStarted'));
  rescanOnReturn = true;
}

/** Full-screen page for one game or app. */
export function detailPage(item) {
  return {
    id: 'detail',
    get name() { return t('detail.title'); },
    keepsTrailer: true,
    item,
    index: 0,
    details: null,
    loading: null,
    saves: null,
    tickets: loadTickets(),
    retries: 0,
    retryTimer: null,
    actions() {
      const it = this.item;
      const list = [
        { label: it.type === 'app' ? t('detail.open') : t('detail.play'), icon: '▶', run: () => launchItem(it) },
        { label: it.favorite ? t('detail.favoriteRemove') : t('detail.favorite'), icon: it.favorite ? '★' : '☆', run: () => toggleFavorite(it) }
      ];
      for (const extra of detailPage.extraActions) {
        const action = extra(it, this);
        if (action) list.push(action);
      }
      const saves = saveAction(it, this.saves, (info) => (this.saves = info));
      if (saves) list.push(saves);
      if (this.details?.canUninstall) list.push({ label: t('detail.uninstall'), icon: '🗑', run: () => uninstall(it) });
      return list;
    },
    build(root) {
      const it = this.item;
      if (!this.loading) {
        this.load();
        this.loadSaves();
      }
      this.list = this.actions();
      this.index = Math.min(this.index, this.list.length - 1);
      const d = this.details;

      this.buttons = this.list.map((a) => el('button', { className: 'action', tabIndex: -1 },
        el('span', { className: 'action-icon' }, iconEl(a.icon)), a.label));

      const facts = [
        [t('detail.lastPlayed'), it.type === 'app' ? null : relativeTime(it.lastPlayed)],
        [t('detail.playtime'), formatDuration(it.playtimeMinutes)],
        [t('detail.source'), SOURCE_LABEL[it.source] || it.source],
        [t('detail.developer'), d?.developers?.join(', ')],
        [t('detail.publisher'), d?.publishers?.join(', ')],
        [t('detail.released'), d?.releaseDate],
        [t('detail.genres'), d?.genres?.join(', ')],
        [t('detail.size'), formatSize(d?.sizeBytes)],
        [t('detail.saves'), saveSummary(this.saves)],
        [t('detail.installDir'), d?.installDir],
        // Own startup options are on: the start differs from what the store would do.
        [t('detail.launch'), d?.customLaunch ? t('detail.customLaunch') : null]
      ].filter(([, v]) => v);

      const shown = descriptionState(d, !!this.retryTimer);
      const description = shown === 'text' ? d.description : shown === 'none' ? t('detail.noDescription') : t('detail.loadingInfo');
      // The rating the age limit goes by (a kept stricter one included): shown only, nothing to choose here.
      // In the rating system that counts now ("USK 12", "PEGI 16", "ESRB T"); Steam's own age or a kept floor as a plain age.
      // A foreign system (Brazil's ClassInd, Japan's CERO) is named with its country.
      const badge = badgeText(ageBadge(d?.ageRating, d?.ageLabel), t);

      root.append(el('div', { className: 'detail' },
        el('section', { className: 'detail-main' },
          logoOrTitle(it),
          el('div', { className: 'detail-actions' }, this.buttons),
          el('p', { className: 'detail-desc', textContent: description })),
        el('aside', { className: 'detail-info about' },
          badge ? el('span', { className: 'age-badge', textContent: badge.text, title: badge.title, ariaLabel: badge.title }) : null,
          el('dl', {}, facts.flatMap(([k, v]) => [el('dt', { textContent: k }), el('dd', { textContent: v })])))));
      setBackground(it);
      scheduleTrailer(it, 700);
      this.update();
    },
    async load() {
      const ticket = this.tickets.next();
      this.loading = attempt(() => api.getDetails(this.item.id), t('detail.infoFailed'));
      const details = await this.loading;
      // A newer read started meanwhile (the store match changed): its answer counts, not this older one.
      if (!this.tickets.isNewest(ticket)) return;
      if (!details) {
        // A failed retry: stop waiting, the line says what is known.
        if (this.retryTimer) {
          this.retryTimer = null;
          if (state.stack.at(-1) === this) render();
        }
        return;
      }
      this.details = details;
      this.scheduleRetry();
      // Only redraw if this page is still the one on screen.
      if (state.stack.at(-1) === this) render();
    },
    /** The entry's texts or store match changed (edit menu): read everything again; an older answer still on its way is dropped. */
    refresh() {
      clearTimeout(this.retryTimer);
      this.retryTimer = null;
      this.retries = 0;
      this.details = null;
      this.load();
    },
    /** The store has not answered for the entry's match yet (offline, rate limit, a slow answer): ask again a few times, then stop. */
    scheduleRetry() {
      clearTimeout(this.retryTimer);
      this.retryTimer = null;
      if (!this.details?.storePending) return;
      const delay = storeRetryDelay(this.retries);
      if (delay == null) return;
      this.retries++;
      this.retryTimer = setTimeout(() => {
        // Closed in the meantime: nothing to ask for.
        if (state.stack.includes(this)) this.load();
        else this.retryTimer = null;
      }, delay);
    },
    async loadSaves() {
      if (this.item.type === 'app') return;
      // Locked, the host refuses: no call then, the section stays away like for a game without a known save folder.
      if (await isLocked()) return;
      // Quietly: most games without a known save folder just don't show the section.
      try {
        this.saves = await api.getSaveInfo(this.item.id);
      } catch (err) {
        console.warn('saves', err);
        return;
      }
      if (state.stack.at(-1) === this) render();
    },
    update() {
      setFocused(this.buttons, this.index);
    },
    activate() {
      return this.list[this.index]?.run();
    },
    handle(action) {
      switch (action) {
        case 'left': return move(this, -1, this.list.length, true);
        case 'right': return move(this, 1, this.list.length, true);
        case 'up':
        case 'down': {
          // The buttons wrap onto several rows: go to the nearest button in the row above/below.
          const rects = this.buttons.map((b) => b.getBoundingClientRect());
          const cur = rects[this.index];
          if (!cur) return;
          const cx = cur.left + cur.width / 2;
          const rowsAway = rects
            .map((r, i) => ({ i, dy: r.top - cur.top, dx: Math.abs(r.left + r.width / 2 - cx) }))
            .filter((c) => (action === 'down' ? c.dy > 8 : c.dy < -8));
          if (!rowsAway.length) return;
          const nearestRow = rowsAway.reduce((m, c) => (Math.abs(c.dy) < Math.abs(m) ? c.dy : m), Infinity);
          const best = rowsAway.filter((c) => Math.abs(c.dy - nearestRow) < 8).sort((a, b) => a.dx - b.dx)[0];
          this.index = best.i;
          sound.move();
          return this.update();
        }
        case 'a': return attempt(() => this.activate());
        case 'y': return toggleFavorite(this.item);
      }
    },
    hints() {
      return [['A', this.list?.[this.index]?.label ?? t('detail.hintSelect')], ['Y', this.item.favorite ? t('detail.favoriteRemove') : t('detail.favorite')], ['B', t('detail.hintBack')]];
    }
  };
}

/** Other modules (library editing, save games, ...) add buttons here: (item, page) => action | null. */
detailPage.extraActions = [];
