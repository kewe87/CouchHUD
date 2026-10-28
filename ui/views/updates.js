// Updates: Windows Update and app updates (winget) in one list, like a console's
// system update screen. The host checks quietly in the background as well.

import { iconEl } from '../core/glyph-icons.js';
import { api } from '../core/api.js';
import { el, formatSize, keepInView, setFocused } from '../core/dom.js';
import { timeAgo } from './news.js';
import { attempt, toast } from '../core/feedback.js';
import { currentView, move, render } from '../core/nav.js';
import { state } from '../core/state.js';
import { confirmDialog } from '../components/overlay.js';
import { itemSize, updateSummary } from '../core/update-rules.js';
import { installFromCatalog } from './plugins.js';
import { sound } from '../sound.js';
import { t } from '../core/i18n.js';

state.updates = null;
/** Plugins and skins with a newer version in the catalog (the host checks in the background). */
state.catalogUpdates = [];
/** CouchHUD itself: newer release on GitHub (the host checks in the background). */
state.selfUpdate = null;
/** When the page last heard of each kind (ms): a download that finished later hides its update until the next news (Downloads tab). */
state.updatesSeen = { updates: 0, selfUpdate: 0, catalogUpdates: 0 };

function seen(kind) {
  state.updatesSeen = { ...state.updatesSeen, [kind]: Date.now() };
}

/** Pending updates, for badges in the settings and the guide menu. */
export function updateCount() {
  const u = state.updates;
  return (u ? u.windows.length + u.apps.length : 0) + (state.catalogUpdates?.length ?? 0) + (state.selfUpdate?.available ? 1 : 0);
}

/** The Updates page and the Downloads tab (it offers the pending updates too) show this state. */
function redrawIfShown() {
  const id = currentView()?.id;
  if (id === 'updates' || (id === 'downloads' && !state.overlay)) render();
}

export async function startUpdates() {
  api.onUpdates?.((u) => {
    state.updates = u;
    seen('updates');
    // New updates arrive as a notification from the host.
    redrawIfShown();
  });
  api.onCatalogUpdates?.((list) => {
    state.catalogUpdates = list ?? [];
    seen('catalogUpdates');
    redrawIfShown();
  });
  api.onSelfUpdate?.((s) => {
    state.selfUpdate = s;
    seen('selfUpdate');
    redrawIfShown();
  });
  state.selfUpdate = (await attempt(() => api.getSelfUpdate?.())) ?? null;
  seen('selfUpdate');
  state.updates = (await attempt(() => api.getUpdates())) ?? null;
  seen('updates');
  state.catalogUpdates = (await attempt(() => api.getCatalogUpdates?.())) ?? [];
  seen('catalogUpdates');
}

/** Starts Windows or app updates: the one way for the Updates page and the Downloads tab (same bridge command, same checks). */
export async function startUpdate(kind, id, question, label) {
  if (state.updates?.busy) return toast(t('updates.alreadyUpdating'));
  if (question && !(await confirmDialog(question, label))) return;
  await attempt(() => api.startUpdate(kind, id), t('updates.notStarted'));
}

/** Installs CouchHUD's own update after asking: the one way for the Updates page and the Downloads tab. */
export async function installSelfUpdate() {
  const s = state.selfUpdate;
  if (!s?.available || s.busy) return toast(t('updates.alreadyBusy'));
  if (!s.ready) return toast(s.problem ?? t('updates.notInstallable'));
  if (!s.canInstall) return toast(t('updates.installedOnly'));
  if (state.session) return toast(t('updates.notDuringGame'));
  const ok = await confirmDialog(t('updates.self.confirm', { version: s.version }), t('updates.install'));
  if (!ok) return;
  await attempt(() => api.startSelfUpdate(), t('updates.notStarted'));
}

// i18n-used: updates.self.err.noRelease updates.self.err.rateLimit updates.self.err.noAnswer updates.self.err.unreachable
/** Rows of the "CouchHUD" section: the newer version with a shortened changelog, or the current state. */
function selfRows() {
  const s = state.selfUpdate;
  if (!s) return [];
  const list = [{ section: 'CouchHUD' }];
  if (s.available) {
    list.push({
      icon: '⬇',
      label: t('updates.self.install', { version: s.version }),
      detail: s.busy ?? (s.ready ? t('updates.self.installDetail', { current: s.current }) : (s.problem ?? t('updates.self.notReady'))),
      highlight: s.ready,
      hint: t('updates.install'),
      run: installSelfUpdate
    });
    // Release notes as plain text, one line per row (the list scrolls).
    for (const line of (s.notes ?? '').split('\n').filter((l) => l.trim())) {
      list.push({ icon: '·', label: line.replace(/^•\s*/, ''), detail: null, hint: t('updates.install'), run: installSelfUpdate });
    }
  } else {
    // The host sends a code (noRelease, rateLimit, noAnswer, unreachable) and the UI translates it; other errors come as text.
    const noRelease = s.errorCode === 'noRelease';
    const errorKey = `updates.self.err.${s.errorCode}`;
    const errorText = s.errorCode ? t(errorKey) : s.error;
    const unchecked = !s.error && !s.checkedAt; // before the first check nothing is known: no "up to date"
    list.push({
      icon: noRelease || unchecked ? '·' : s.error ? '!' : '✓',
      label: noRelease ? t('updates.self.noRelease') : s.error ? t('updates.self.checkFailed') : unchecked ? t('updates.self.current', { current: s.current }) : t('updates.self.upToDate', { current: s.current }),
      detail: noRelease ? t('updates.self.noReleaseDetail', { current: s.current }) : errorText ?? (s.checkedAt ? t('updates.lastChecked', { ago: timeAgo(s.checkedAt) }) : t('updates.notChecked')),
      hint: t('updates.hint.search'),
      run: () => (s.busy ? toast(t('updates.alreadyRunning')) : attempt(() => api.checkSelfUpdate(), t('updates.searchFailed')))
    });
  }
  return list;
}

function rows() {
  const u = state.updates ?? { windows: [], apps: [] };
  const list = [];
  if (u.rebootRequired) {
    list.push({
      icon: '↻',
      label: t('updates.reboot.title'),
      detail: t('updates.reboot.detail'),
      highlight: true,
      hint: t('updates.reboot.confirm'),
      run: async () => (await confirmDialog(t('updates.reboot.question'), t('updates.reboot.confirm'))) && api.power('restart')
    });
  }
  list.push({
    icon: '⟳',
    label: t('updates.check'),
    detail: u.checkedAt ? t('updates.lastChecked', { ago: timeAgo(u.checkedAt) }) : t('updates.notChecked'),
    hint: t('updates.hint.search'),
    run: () => (u.busy ? toast(t('updates.alreadyRunning')) : Promise.all([attempt(() => api.checkUpdates(), t('updates.searchFailed')), attempt(() => api.checkSelfUpdate?.())]))
  });

  list.push(...selfRows());

  if (u.windows.length) {
    // Only sizes the host is sure of; a total only when every update still to load has one.
    const summary = updateSummary(u.windows);
    const summaryText = [
      summary.size ? t('updates.windows.countSize', { count: summary.count, size: formatSize(summary.size) }) : t('updates.windows.count', { count: summary.count }),
      summary.downloaded ? t('updates.windows.downloadedCount', { count: summary.downloaded }) : null
    ].filter(Boolean).join(' · ');
    const installAll = () => startUpdate('windows', null,
      t('updates.windows.confirm', { count: u.windows.length }), t('updates.install'));
    list.push({ section: t('updates.windows.section') });
    list.push({
      icon: '⬇',
      label: t('updates.windows.install'),
      detail: summaryText,
      highlight: true,
      hint: t('updates.install'),
      run: installAll
    });
    for (const w of u.windows) {
      const size = itemSize(w);
      const detail = [w.important ? t('updates.windows.important') : t('updates.windows.optional'), w.downloaded ? t('updates.windows.downloaded') : size ? formatSize(size) : null];
      list.push({ icon: w.important ? '★' : '·', label: w.name, detail: detail.filter(Boolean).join(' · '), hint: t('updates.install'), run: installAll });
    }
  } else if (u.windowsError) {
    list.push({ section: t('updates.windows.section') }, { icon: '!', label: u.windowsError, detail: t('updates.windows.retry') });
  }

  if (u.apps.length) {
    const appCount = t('updates.apps.count', { count: u.apps.length });
    list.push({ section: t('updates.apps.section') });
    list.push({
      icon: '⬇',
      label: t('updates.apps.updateAll'),
      detail: appCount,
      highlight: true,
      hint: t('updates.apps.update'),
      run: () => startUpdate('apps', null, t('updates.apps.confirm', { apps: appCount }), t('updates.apps.update'))
    });
    for (const a of u.apps) {
      list.push({
        icon: '▣',
        label: a.name,
        detail: `${a.version ?? '?'} → ${a.available}`,
        hint: t('updates.apps.update'),
        run: () => startUpdate('apps', a.id, null)
      });
    }
  } else if (u.wingetAvailable === false) {
    list.push({ section: t('updates.apps.section') }, { icon: '!', label: t('updates.apps.unavailable'), detail: t('updates.apps.unavailableDetail') });
  }

  const pluginUpdates = state.catalogUpdates ?? [];
  if (pluginUpdates.length) {
    list.push({ section: t('updates.plugins.section') });
    for (const p of pluginUpdates) {
      list.push({
        icon: p.kind === 'skin' ? '🎨' : '🧩',
        label: p.name,
        detail: `${p.installedVersion ?? '?'} → ${p.version}${p.kind === 'plugin' ? t('updates.plugins.restartAfter') : ''}`,
        highlight: true,
        hint: t('updates.install'),
        run: () => installFromCatalog(p)
      });
    }
  }

  if (u.checkedAt && !state.selfUpdate?.available && !u.windows.length && !u.apps.length && !u.windowsError && !pluginUpdates.length) {
    list.push({ icon: '✓', label: t('updates.allCurrent'), detail: t('updates.allCurrentDetail') });
  }
  return list;
}

export function updatesPage() {
  return {
    id: 'updates',
    name: t('updates.title'),
    index: 0,
    onEnter() {
      // The first visit starts a search; later visits show what the background check found.
      if (!state.updates?.checkedAt && !state.updates?.busy) attempt(() => api.checkUpdates());
    },
    build(root) {
      if (!this.entered) {
        this.entered = true;
        this.onEnter();
      }
      const all = rows();
      this.list = all.filter((r) => !r.section);
      this.index = Math.min(this.index, this.list.length - 1);
      this.items = [];
      const nodes = all.map((r) => {
        if (r.section) return el('li', { className: 'update-section', textContent: r.section });
        const node = el('li', { className: `check-row update-row${r.highlight ? ' highlight' : ''}` },
          el('span', { className: 'check-box' }, iconEl(r.icon)),
          el('span', { className: 'check-main' }, el('strong', { textContent: r.label }), r.detail ? el('small', { textContent: r.detail }) : null));
        this.items.push(node);
        return node;
      });
      this.scroller = el('ul', { className: 'check-list list-scroll' }, nodes);
      const busy = state.updates?.busy ?? state.selfUpdate?.busy;
      root.append(el('div', { className: 'files updates' },
        el('div', { className: 'files-head' },
          el('h2', { textContent: t('updates.title') }),
          busy ? el('p', { className: 'update-busy' }, el('span', { className: 'spinner' }), el('span', { textContent: busy })) : null),
        this.scroller));
      this.update({ instant: true });
    },
    update({ instant = false } = {}) {
      setFocused(this.items, this.index);
      keepInView(this.scroller, this.items[this.index], { instant });
    },
    async handle(action) {
      switch (action) {
        case 'up': return move(this, -1, this.list.length, true);
        case 'down': return move(this, 1, this.list.length, true);
        case 'x': return Promise.all([attempt(() => api.checkUpdates()), attempt(() => api.checkSelfUpdate?.())]);
        case 'a': {
          const row = this.list[this.index];
          if (!row?.run) return sound.bump();
          sound.select();
          return row.run();
        }
      }
    },
    pointer(node, _index, dispatch) {
      // Section headings sit between the rows, so the sibling index is off.
      const index = this.items.indexOf(node);
      if (index < 0) return;
      this.index = index;
      this.update();
      dispatch('a');
    },
    hints() {
      // The verb says what A does on the focused row; rows that only inform have no A.
      const row = this.list?.[this.index];
      const a = row?.run ? [['A', row.hint ?? t('updates.hint.run')]] : [];
      const x = row?.hint === t('updates.hint.search') ? [] : [['X', t('updates.hint.search')]];
      return [...a, ...x, ['B', t('updates.hint.back')]];
    }
  };
}
