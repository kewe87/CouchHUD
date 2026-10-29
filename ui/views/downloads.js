// Downloads tab: everything that loads or installs, in one place (like a console's download list). The host gathers the lines
// (Steam's manifests, Windows Update, winget, CouchHUD's own update, plugins and skins, CaseHUD, installs asked for in a store);
// see src/CouchHud.Core/Library/DownloadCenter.cs. A bar only where the source really tells its progress.

import { api } from '../core/api.js';
import { $, el, formatSize, keepInView, setFocused } from '../core/dom.js';
import { iconEl } from '../core/glyph-icons.js';
import { plainBackground } from '../core/art.js';
import { attempt, toast } from '../core/feedback.js';
import { isBusy, markBusy, prefersReducedMotion, runBusy } from '../core/busy.js';
import { bar, currentView, move, push, render, renderHints, showChipFocus } from '../core/nav.js';
import { activateChip } from '../core/bar-chips.js';
import { state } from '../core/state.js';
import { SOURCE_LABEL } from '../core/library.js';
import { chooseDialog, confirmDialog } from '../components/overlay.js';
import { ensureParentGrant, parental } from '../components/parental.js';
import { ensureUnlocked, isLocked } from '../components/passcode.js';
import { admitProtectedChange } from '../core/lock-rules.js';
import { downloadActions, pendingDownloads, updateSummary } from '../core/update-rules.js';
import { installSelfUpdate, startUpdate } from './updates.js';
import { installFromCatalog } from './plugins.js';
import { sound } from '../sound.js';
import { fmtPercent, t } from '../core/i18n.js';
import { timeAgo } from './news.js';
import { searchOutcome, searchStatus, searchSucceeded } from '../core/update-search.js';
import { downloadHints, downloadRows, downloadShortcuts, finishedMenu, isFinished, pressPending, pressSearch } from '../core/downloads-rules.js';
import { uninstalledPage } from './uninstalled.js';
import { areaOn } from '../core/areas.js';
import { stepLine } from '../core/update-progress.js';

const EMPTY = { entries: [], summary: { active: 0, finished: 0, progress: null } };
let data = null;
let loading = false;
let timer = null;
const redrawHooks = [];
/** The busy key of the search for updates (ui/core/busy.js): its footer hint is busy and a second press does nothing. */
const SEARCH = 'downloads:search';
/** What the last search of this visit found ("All up to date", "3 updates found", a problem): shown next to the heading. */
let outcome = null;

/** Redraws the tab while it is shown (a busy action started or ended), else only the hints (a dialog over it keeps its own). */
function redraw() {
  if (currentView() === downloadsView && !state.overlay) render();
  else renderHints();
}

const KIND_ICON = { game: '🎮', windows: '🪟', app: '⤓', couchHud: '↻', plugin: '🔌' };
const STATE_ICON = { downloading: '⬇', installing: '⬇', waiting: '⋯', inClient: '⋯', paused: '⏸', queued: '⋯', done: '✓', failed: '⚠' };

const OWN_SOURCE = {
  windows: () => t('downloads.source.windows'),
  winget: () => t('downloads.source.winget'),
  plugin: () => t('downloads.source.plugin'),
  skin: () => t('downloads.source.skin')
};

const STATE_TEXT = {
  waiting: () => t('downloads.state.waiting'),
  downloading: () => t('downloads.state.downloading'),
  installing: () => t('downloads.state.installing'),
  paused: () => t('downloads.state.paused'),
  queued: () => t('downloads.state.queued'),
  done: () => t('downloads.state.done'),
  failed: () => t('downloads.state.failed')
};

/** A source name for the line ("Steam", "Windows Update", "Plugin" …). */
export function sourceLabel(entry) {
  return SOURCE_LABEL[entry.source] ?? OWN_SOURCE[entry.source]?.() ?? entry.source;
}

/** The small line under the name: kind, state and, where known, the bytes. Never a made-up percentage. */
export function downloadDetail(entry) {
  // An app update with winget's own step: "App 1 of 2 · Name · 45 %" (ui/core/update-progress.js).
  const stepped = stepLine(entry, { tr: t, percent: fmtPercent, size: formatSize });
  if (stepped) return `${sourceLabel(entry)} · ${stepped}`;
  const parts = [sourceLabel(entry)];
  if (entry.kind === 'game') parts.push(entry.isUpdate ? t('downloads.update') : t('downloads.install'));
  if (entry.state === 'inClient') {
    parts.push(t('downloads.state.inClient', { client: sourceLabel(entry) }));
  } else {
    parts.push(STATE_TEXT[entry.state]?.() ?? entry.state);
  }
  if (entry.bytesTotal > 0 && entry.state !== 'done' && entry.state !== 'failed') {
    parts.push(t('downloads.bytes', { done: formatSize(entry.bytesDone) ?? '0 MB', total: formatSize(entry.bytesTotal) }));
  } else if (entry.progress != null && entry.state !== 'done' && entry.state !== 'failed') {
    parts.push(fmtPercent(entry.progress * 100));
  }
  if (entry.detail) parts.push(entry.detail);
  return parts.join(' · ');
}

/** The header badge and the guide menu line: "2 · 45 %", or just the count when nobody tells a percentage. */
export function badgeLabel(summary) {
  if (!summary?.active) return '';
  return summary.progress == null ? t('downloads.badge', { count: summary.active }) : t('downloads.badgeProgress', { count: summary.active, percent: fmtPercent(summary.progress * 100) });
}

const isActive = (e) => ['waiting', 'downloading', 'installing', 'inClient'].includes(e.state);

function showBadge() {
  const badge = $('#downloads-status');
  if (!badge) return;
  const label = badgeLabel(data?.summary);
  badge.hidden = !label;
  badge.textContent = label ? `⬇ ${label}` : '';
  badge.onclick = () => activateChip('downloads');
  // The chip went away while it had the focus.
  if (!label && bar.chip === 'downloads') bar.chip = null;
  showChipFocus();
}

function accept(next) {
  if (!next) return;
  data = { entries: next.entries ?? [], summary: next.summary ?? EMPTY.summary };
  showBadge();
  for (const hook of redrawHooks) hook();
  if (currentView() === downloadsView && !state.overlay) render();
}

function load() {
  if (loading) return;
  loading = true;
  attempt(() => api.getDownloadCenter(), t('downloads.loadFailed')).then((d) => {
    loading = false;
    accept(d ?? data ?? EMPTY);
  });
}

/** The open tab asks every few seconds (that also makes the host look more often); it stops as soon as the tab is left. */
function watch() {
  clearTimeout(timer);
  timer = setTimeout(() => {
    if (currentView() !== downloadsView) return;
    load();
    watch();
  }, 4000);
}

/** The guide menu line: count and progress of what loads right now. */
export function downloadsQuickDetail() {
  return badgeLabel(data?.summary) || t('downloads.quickIdle');
}

/** Redraw something else (the guide menu) when the list changes. */
export function onDownloadsChanged(hook) {
  redrawHooks.push(hook);
}

export function startDownloads() {
  if (!api.getDownloadCenter) return;
  api.onDownloads?.((d) => accept(d));
  load();
}

/**
 * A row for something that can be started here (pending updates): the same functions and bridge commands as the Updates page, so the
 * same questions and the same checks of the host (couch lock, profile, parents) apply.
 */
function pendingRow(p) {
  const u = state.updates ?? { windows: [], apps: [] };
  switch (p.kind) {
    case 'windows': {
      const s = updateSummary(u.windows);
      return {
        kind: 'pending', icon: '🪟', label: t('updates.windows.install'),
        detail: [t('downloads.source.windows'), s.size ? t('updates.windows.countSize', { count: s.count, size: formatSize(s.size) }) : t('updates.windows.count', { count: s.count })].join(' · '),
        hint: t('updates.install'), busyKey: 'update:windows', hostBusy: !!u.busy,
        run: () => startUpdate('windows', null, t('updates.windows.confirm', { count: u.windows.length }), t('updates.install'))
      };
    }
    case 'apps': {
      const apps = t('updates.apps.count', { count: u.apps.length });
      return {
        kind: 'pending', icon: '⤓', label: t('updates.apps.updateAll'), detail: [t('downloads.source.winget'), apps].join(' · '), hint: t('updates.apps.update'),
        busyKey: 'update:apps', hostBusy: !!u.busy, run: () => startUpdate('apps', null, t('updates.apps.confirm', { apps }), t('updates.apps.update'))
      };
    }
    case 'self': {
      const s = state.selfUpdate;
      return {
        kind: 'pending', icon: '↻', label: t('updates.self.install', { version: s.version }),
        detail: s.ready ? t('updates.self.installDetail', { current: s.current }) : (s.problem ?? t('updates.self.notReady')),
        hint: t('updates.install'), busyKey: 'update:self', hostBusy: !!s.busy, run: installSelfUpdate
      };
    }
    default: {
      const c = p.entry;
      return {
        kind: 'pending', icon: c.kind === 'skin' ? '🎨' : '🔌', label: c.name,
        detail: [c.kind === 'skin' ? t('downloads.source.skin') : t('downloads.source.plugin'), `${c.installedVersion ?? '?'} → ${c.version}`].join(' · '),
        hint: t('updates.install'), busyKey: `catalog:${c.id}`, run: () => installFromCatalog(c)
      };
    }
  }
}

/** One app update of its own (the "all" row starts every one). */
function appRow(a) {
  const u = state.updates;
  return {
    kind: 'pending', icon: '▣', label: a.name, detail: `${a.version ?? '?'} → ${a.available}`, hint: t('updates.apps.update'),
    busyKey: `update:app:${a.id}`, hostBusy: !!u?.busy, run: () => startUpdate('apps', a.id, null)
  };
}

/** A start row is busy while its own press runs or while the host reports that it is at work on that kind. */
const rowBusy = (row) => row.kind === 'pending' && (!!row.hostBusy || (!!row.busyKey && isBusy(row.busyKey)));

/** What is waiting to be installed: Windows and app updates, CouchHUD itself, plugins and skins. */
const pendingCount = (u, s) => (u?.windows?.length ?? 0) + (u?.apps?.length ?? 0) + (s?.available ? 1 : 0) + (state.catalogUpdates?.length ?? 0);

/**
 * Looks for Windows, app and CouchHUD updates now (X, or a click on its footer hint). While it runs the hint is busy and the status line
 * says so; a second press does nothing. A search that Privacy forbids (Offline, a switch off) or that failed says so; a finished one
 * tells what it found ("All up to date", "3 updates found"), in a toast and next to the heading.
 */
async function searchUpdates() {
  // On the locked couch nothing that starts a request is fired (the host would turn it away); asked before X looks busy.
  return pressSearch(SEARCH, async () => {
    outcome = null;
    const [windows, self] = await Promise.all([attempt(() => api.checkUpdates(), t('updates.searchFailed')), attempt(() => api.checkSelfUpdate?.())]);
    const ok = searchSucceeded(windows, self, !!api.checkSelfUpdate);
    const nowU = windows ?? state.updates;
    const nowS = self ?? state.selfUpdate;
    outcome = searchOutcome(nowU, nowS, pendingCount(nowU, nowS), t, ok);
    if (outcome) toast(outcome);
  }, pressDeps(searchBusy()));
}

/** What a press on this page touches (core/downloads-rules.js decides the order). */
function pressDeps(busy) {
  return {
    busy,
    locked: isLocked,
    run: (key, fn) => runBusy(key, fn, { onChange: redraw, onIgnored: () => sound.bump() }),
    unlocked: () => toast(t('downloads.unlockFirst')),
    bump: () => sound.bump(),
    select: () => sound.select()
  };
}

/** The search runs: this page's own, or the host is busy with a search or an install (it would turn a new search away). */
const searchBusy = () => isBusy(SEARCH) || !!state.updates?.busy || !!state.selfUpdate?.busy;

/** A restart that is due: the only note of the update check that is a row (it starts something). The others stand by the heading. */
function updateNotes() {
  const u = state.updates;
  if (!u) return { first: [] };
  const first = [];
  if (u.rebootRequired) {
    first.push({
      kind: 'pending', open: true, icon: '↻', label: t('updates.reboot.title'), detail: t('updates.reboot.detail'), hint: t('updates.reboot.confirm'),
      run: async () => (await confirmDialog(t('updates.reboot.question'), t('updates.reboot.confirm'))) && api.power('restart')
    });
  }
  return { first };
}

const ACTION = {
  cancel: () => ({ icon: '✕', label: t('downloads.cancel'), detail: t('downloads.cancelHint'), value: 'cancel' }),
  openClient: (e) => ({ icon: '↗', label: t('downloads.openClient', { client: sourceLabel(e) }), detail: t('downloads.openClientHint', { client: sourceLabel(e) }), value: 'openClient' }),
  clearFinished: () => ({ icon: '✕', label: t('downloads.clearFinished'), detail: '', value: 'clearFinished' }),
  remove: (e) => {
    const finished = e.state === 'done' || e.state === 'failed';
    return { icon: '✕', label: finished ? t('downloads.remove') : t('downloads.forget'), detail: finished ? '' : t('downloads.forgetHint', { client: sourceLabel(e) }), value: 'remove' };
  }
};

/**
 * Opens the client's own download page (Steam): the unlocked couch first, then the parents' code when parental controls are on (the host
 * asks for both again; a parents' code given on the locked couch would be lost).
 */
async function openClient(entry) {
  const reason = t('downloads.openClientGuard', { client: sourceLabel(entry) });
  const scope = { action: 'openClientDownloads', item: entry.source };
  if (!(await admitProtectedChange({ reason, parentalOn: !!parental.status?.enabled, scope, ensureUnlocked, ensureParentGrant }))) return;
  accept(await attempt(() => api.openClientDownloads(entry.source), t('downloads.openClientFailed')));
}

async function lineMenu(entry) {
  const actions = finishedMenu(downloadActions(entry), data?.entries ?? []);
  if (!actions.length) {
    // Windows Update, winget and Epic's running work: nothing that really works from here.
    sound.bump();
    return toast(entry.kind === 'game' ? t('downloads.controlInClient', { client: sourceLabel(entry) }) : t('downloads.running'));
  }
  const choice = await chooseDialog(entry.name, actions.map((a) => ACTION[a](entry)));
  if (choice === 'cancel') return accept(await attempt(() => api.cancelDownload(entry.id), t('downloads.cancelFailed')));
  if (choice === 'openClient') return openClient(entry);
  if (choice === 'remove') return accept(await attempt(() => api.dismissDownload(entry.id), t('downloads.removeFailed')));
  if (choice === 'clearFinished') accept(await attempt(() => api.dismissDownload(null), t('downloads.removeFailed')));
}

export const downloadsView = {
  get name() { return t('downloads.name'); },
  id: 'downloads',
  index: 0,
  onEnter() {
    outcome = null;
    // A quiet check when the last good one is older than an hour (the host decides; the result arrives as an event).
    api.refreshUpdates?.()?.catch?.(() => {});
    load();
  },
  build(root) {
    plainBackground();
    if (!data) {
      load();
      root.append(el('p', { className: 'empty-note', textContent: t('downloads.loading') }));
      this.items = [];
      this.rows = [];
      return;
    }
    watch();
    const entries = data.entries;
    // Only what loads or waits to be installed: running lines first, then what can be started (pending updates), then the finished
    // lines. The search for updates and the games that are not installed are the page's footer actions (X, Y), not rows.
    const pending = pendingDownloads({ updates: state.updates, selfUpdate: state.selfUpdate, catalogUpdates: state.catalogUpdates, seen: state.updatesSeen }, entries)
      .flatMap((p) => (p.kind === 'apps' ? [pendingRow(p), ...(state.updates?.apps ?? []).map(appRow)] : [pendingRow(p)]));
    this.rows = downloadRows({ entries, first: updateNotes().first, pending });
    this.index = Math.max(0, Math.min(this.index, this.rows.length - 1));
    this.items = this.rows.map((r) => {
      if (r.kind !== 'entry') {
        const row = el('li', { className: `check-row update-row${r.kind === 'pending' ? ' download-pending' : ''}` },
          el('span', { className: 'check-box' }, iconEl(r.icon)),
          el('span', { className: 'check-main' }, el('strong', { textContent: r.label }), el('small', { textContent: r.detail })));
        // A start that runs (this press, or the host already at work): greyed with a spinner, A does nothing; the focus may stay here.
        return markBusy(row, rowBusy(r));
      }
      const e = r.entry;
      const bar = isActive(e) && e.progress != null;
      return el('li', { className: `check-row update-row download-row state-${e.state}${e.state === 'downloading' ? ' highlight' : ''}` },
        el('span', { className: 'check-box' }, iconEl(isFinished(e) ? STATE_ICON[e.state] : KIND_ICON[e.kind] ?? STATE_ICON[e.state])),
        el('span', { className: 'check-main' },
          el('strong', { textContent: e.name }),
          el('small', { textContent: downloadDetail(e) }),
          bar ? el('span', { className: 'progress' }, el('i', { style: { width: `${Math.round(e.progress * 100)}%` } })) : null,
          // Running without a known progress: a calm moving bar instead of a number.
          isActive(e) && !bar ? el('span', { className: 'progress progress-unknown' }, el('i')) : null));
    });
    this.scroller = el('ul', { className: 'check-list list-scroll' }, this.items);
    // The search's state stands by the heading: running, its outcome, a problem, or when it last searched.
    const status = state.updates ? searchStatus({ searching: isBusy(SEARCH), updates: state.updates, selfUpdate: state.selfUpdate, outcome, ago: timeAgo }, t) : null;
    const wingetMissing = state.updates && !state.updates.apps?.length && state.updates.wingetAvailable === false;
    const empty = !this.rows.length;
    root.append(el('div', { className: 'files downloads' },
      el('div', { className: 'files-head' },
        el('h2', { textContent: t('downloads.title') }),
        el('p', { className: 'meta', textContent: entries.some(isActive) ? t('downloads.intro') : t('downloads.introIdle') }),
        status ? el('p', { className: `meta downloads-search${status.problem ? ' problem' : ''}`, role: 'status' },
          status.busy ? el('span', { className: `spinner${prefersReducedMotion() ? ' static' : ''}`, ariaHidden: 'true' }) : null,
          el('span', { textContent: status.text })) : null,
        wingetMissing ? el('p', { className: 'meta downloads-search problem', textContent: `${t('updates.apps.unavailable')}. ${t('updates.apps.unavailableDetail')}` }) : null),
      empty ? null : this.scroller,
      empty ? el('div', { className: 'downloads-empty' },
        el('strong', { textContent: t('downloads.empty') }),
        el('p', { textContent: areaOn('games') ? t('downloads.emptyHint') : t('downloads.emptyHintNoGames') })) : null,
      el('p', { className: 'meta downloads-note', textContent: t('downloads.clientsNote') })));
    this.update({ instant: true });
  },
  update({ instant = false } = {}) {
    if (!this.items?.length) return;
    setFocused(this.items, this.index);
    keepInView(this.scroller, this.items[this.index], { instant });
  },
  async handle(action) {
    const count = this.rows?.length ?? 0;
    switch (action) {
      case 'up': return move(this, -1, count, true);
      case 'down': return move(this, 1, count, true);
      case 'a': {
        const row = this.rows?.[this.index];
        if (!row) return sound.bump();
        if (row.kind === 'pending') {
          // A start that runs: the row shows it, a second press does nothing (no "already running" toast). On the locked couch nothing
          // that starts a request is fired (the host would turn it away), asked before the row looks busy; the restart stays open, like power.
          return pressPending(row, pressDeps(rowBusy(row)));
        }
        sound.select();
        return lineMenu(row.entry);
      }
      case 'x':
        return state.updates ? searchUpdates() : sound.bump();
      case 'y':
        if (!areaOn('games')) return sound.bump();
        sound.select();
        return push(uninstalledPage());
    }
  },
  pointer(node, index, dispatch) {
    this.index = index;
    this.update();
    dispatch('a');
  },
  hints() {
    const row = this.rows?.[this.index];
    // The page's own actions sit between A and B, like a plugin page's footer actions; X is busy while the search runs, A on a busy row.
    const shortcuts = downloadShortcuts({ canSearch: !!state.updates, searching: searchBusy(), gamesOn: areaOn('games') });
    return downloadHints({
      hasRow: !!row,
      rowBusy: !!row && rowBusy(row),
      shortcuts,
      labels: { a: row?.kind === 'pending' ? row.hint : t('downloads.hintMenu'), search: t('updates.check'), uninstalled: t('downloads.uninstalled'), back: t('downloads.hintHome') }
    });
  }
};
