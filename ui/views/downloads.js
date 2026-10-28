// Downloads tab: everything that loads or installs, in one place (like a console's download list). The host gathers the lines
// (Steam's manifests, Windows Update, winget, CouchHUD's own update, plugins and skins, CaseHUD, installs asked for in a store);
// see src/CouchHud.Core/Library/DownloadCenter.cs. A bar only where the source really tells its progress.

import { api } from '../core/api.js';
import { $, el, formatSize, keepInView, setFocused } from '../core/dom.js';
import { iconEl } from '../core/glyph-icons.js';
import { plainBackground } from '../core/art.js';
import { attempt, toast } from '../core/feedback.js';
import { currentView, move, push, render } from '../core/nav.js';
import { state } from '../core/state.js';
import { SOURCE_LABEL } from '../core/library.js';
import { chooseDialog } from '../components/overlay.js';
import { ensureParentGrant, parental } from '../components/parental.js';
import { ensureUnlocked } from '../components/passcode.js';
import { admitProtectedChange } from '../core/lock-rules.js';
import { downloadActions, pendingDownloads, updateSummary } from '../core/update-rules.js';
import { installSelfUpdate, startUpdate } from './updates.js';
import { installFromCatalog } from './plugins.js';
import { sound } from '../sound.js';
import { fmtPercent, t } from '../core/i18n.js';
import { uninstalledPage } from './uninstalled.js';
import { areaOn } from '../core/areas.js';

const EMPTY = { entries: [], summary: { active: 0, finished: 0, progress: null } };
let data = null;
let loading = false;
let timer = null;
const redrawHooks = [];

const KIND_ICON = { game: '🎮', windows: '🪟', app: '⤓', couchHud: '↻', plugin: '🔌', caseHud: '🌀' };
const STATE_ICON = { downloading: '⬇', installing: '⬇', waiting: '⋯', inClient: '⋯', paused: '⏸', queued: '⋯', done: '✓', failed: '⚠' };

const OWN_SOURCE = {
  windows: () => t('downloads.source.windows'),
  winget: () => t('downloads.source.winget'),
  plugin: () => t('downloads.source.plugin'),
  skin: () => t('downloads.source.skin'),
  casehud: () => 'CaseHUD'
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
        hint: t('updates.install'),
        run: () => startUpdate('windows', null, t('updates.windows.confirm', { count: u.windows.length }), t('updates.install'))
      };
    }
    case 'apps': {
      const apps = t('updates.apps.count', { count: u.apps.length });
      return {
        kind: 'pending', icon: '⤓', label: t('updates.apps.updateAll'), detail: [t('downloads.source.winget'), apps].join(' · '), hint: t('updates.apps.update'),
        run: () => startUpdate('apps', null, t('updates.apps.confirm', { apps }), t('updates.apps.update'))
      };
    }
    case 'self': {
      const s = state.selfUpdate;
      return {
        kind: 'pending', icon: '↻', label: t('updates.self.install', { version: s.version }),
        detail: s.ready ? t('updates.self.installDetail', { current: s.current }) : (s.problem ?? t('updates.self.notReady')),
        hint: t('updates.install'), run: installSelfUpdate
      };
    }
    default: {
      const c = p.entry;
      return {
        kind: 'pending', icon: c.kind === 'skin' ? '🎨' : '🔌', label: c.name,
        detail: [c.kind === 'skin' ? t('downloads.source.skin') : t('downloads.source.plugin'), `${c.installedVersion ?? '?'} → ${c.version}`].join(' · '),
        hint: t('updates.install'), run: () => installFromCatalog(c)
      };
    }
  }
}

const ACTION = {
  cancel: () => ({ icon: '✕', label: t('downloads.cancel'), detail: t('downloads.cancelHint'), value: 'cancel' }),
  openClient: (e) => ({ icon: '↗', label: t('downloads.openClient', { client: sourceLabel(e) }), detail: t('downloads.openClientHint', { client: sourceLabel(e) }), value: 'openClient' }),
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
  const actions = downloadActions(entry);
  if (!actions.length) {
    // Windows Update, winget and Epic's running work: nothing that really works from here.
    sound.bump();
    return toast(entry.kind === 'game' ? t('downloads.controlInClient', { client: sourceLabel(entry) }) : t('downloads.running'));
  }
  const choice = await chooseDialog(entry.name, actions.map((a) => ACTION[a](entry)));
  if (choice === 'cancel') return accept(await attempt(() => api.cancelDownload(entry.id), t('downloads.cancelFailed')));
  if (choice === 'openClient') return openClient(entry);
  if (choice === 'remove') accept(await attempt(() => api.dismissDownload(entry.id), t('downloads.removeFailed')));
}

export const downloadsView = {
  get name() { return t('downloads.name'); },
  id: 'downloads',
  index: 0,
  onEnter() {
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
    // The first row opens the games that are not installed: the way to start a new download. Running lines first, then what can be
    // started (pending updates), then the finished lines.
    const pending = pendingDownloads({ updates: state.updates, selfUpdate: state.selfUpdate, catalogUpdates: state.catalogUpdates, seen: state.updatesSeen }, entries).map(pendingRow);
    const finished = (e) => e.state === 'done' || e.state === 'failed';
    this.rows = [
      // Games that are not installed belong to Gaming: with the area off the page keeps CouchHUD's and Windows' own downloads.
      ...(areaOn('games') ? [{ kind: 'uninstalled', icon: '⤓', label: t('downloads.uninstalled'), detail: t('downloads.uninstalledHint') }] : []),
      ...entries.filter((e) => !finished(e)).map((e) => ({ kind: 'entry', entry: e })),
      ...pending,
      ...entries.filter(finished).map((e) => ({ kind: 'entry', entry: e }))
    ];
    this.index = Math.max(0, Math.min(this.index, this.rows.length - 1));
    this.items = this.rows.map((r) => {
      if (r.kind !== 'entry') {
        return el('li', { className: `check-row update-row${r.kind === 'pending' ? ' download-pending' : ''}` },
          el('span', { className: 'check-box' }, iconEl(r.icon)),
          el('span', { className: 'check-main' }, el('strong', { textContent: r.label }), el('small', { textContent: r.detail })),
          r.kind === 'uninstalled' ? el('span', { className: 'setting-value', textContent: '›' }) : null);
      }
      const e = r.entry;
      const bar = isActive(e) && e.progress != null;
      return el('li', { className: `check-row update-row download-row state-${e.state}${e.state === 'downloading' ? ' highlight' : ''}` },
        el('span', { className: 'check-box' }, iconEl(e.state === 'done' || e.state === 'failed' ? STATE_ICON[e.state] : KIND_ICON[e.kind] ?? STATE_ICON[e.state])),
        el('span', { className: 'check-main' },
          el('strong', { textContent: e.name }),
          el('small', { textContent: downloadDetail(e) }),
          bar ? el('span', { className: 'progress' }, el('i', { style: { width: `${Math.round(e.progress * 100)}%` } })) : null,
          // Running without a known progress: a calm moving bar instead of a number.
          isActive(e) && !bar ? el('span', { className: 'progress progress-unknown' }, el('i')) : null));
    });
    this.scroller = el('ul', { className: 'check-list list-scroll' }, this.items);
    root.append(el('div', { className: 'files downloads' },
      el('div', { className: 'files-head' },
        el('h2', { textContent: t('downloads.title') }),
        el('p', { className: 'meta', textContent: entries.some(isActive) ? t('downloads.intro') : t('downloads.introIdle') })),
      this.scroller,
      entries.length || pending.length ? null : el('p', { className: 'empty-note', textContent: t('downloads.empty') }),
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
        if (row.kind === 'uninstalled') return push(uninstalledPage());
        sound.select();
        if (row.kind === 'pending') return row.run();
        return lineMenu(row.entry);
      }
      case 'x': {
        if (!data?.entries.some((e) => e.state === 'done' || e.state === 'failed')) return sound.bump();
        sound.select();
        return accept(await attempt(() => api.dismissDownload(null), t('downloads.removeFailed')));
      }
    }
  },
  pointer(node, index, dispatch) {
    this.index = index;
    this.update();
    dispatch('a');
  },
  hints() {
    const row = this.rows?.[this.index];
    const finished = data?.entries.some((e) => e.state === 'done' || e.state === 'failed');
    return [
      ...(row ? [['A', row.kind === 'uninstalled' ? t('downloads.hintOpen') : row.kind === 'pending' ? row.hint : t('downloads.hintMenu')]] : []),
      ...(finished ? [['X', t('downloads.hintClear')]] : []),
      ['B', t('downloads.hintHome')]
    ];
  }
};
