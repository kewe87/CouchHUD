// Guide menu: the console-style side panel for what you need mid-game.
// Opens with the Guide button (tap in CouchHUD, hold over a game).

import { iconEl } from '../core/glyph-icons.js';
import { api } from '../core/api.js';
import { $, el, formatDuration, keepInView, setFocused } from '../core/dom.js';
import { attempt, toast } from '../core/feedback.js';
import { state } from '../core/state.js';
import { push, renderHints, switchTab, tabViews, render, clearStack } from '../core/nav.js';
import { tasksPage } from '../views/system-tools.js';
import { notificationsPage } from '../views/notifications.js';
import { bluetoothPage } from '../views/bluetooth.js';
import { appVolumePage } from '../views/app-volume.js';
import { galleryPage } from '../views/gallery.js';
import { capture } from '../views/deck.js';
import { downloadsQuickDetail } from '../views/downloads.js';
import { chooseDialog, confirmDialog } from './overlay.js';
import { sound } from '../sound.js';
import { withParentGrant } from '../core/parent-retry.js';
import { t, fmtNumber, fmtPercent, fmtTime, fmtDate } from '../core/i18n.js';
import { powerRows } from '../core/power.js';
import { weatherLine } from '../core/weather.js';
import { remainingLabel } from './parental.js';
import { batteryEntries, systemBadge } from '../core/battery-rules.js';
import { refreshBattery } from '../core/battery.js';

/** Icons of the Bluetooth devices with a battery (the kinds of views/bluetooth.js). */
const KIND_ICON = { controller: '🎮', headphones: '🎧', keyboard: '⌨', mouse: '🖱', speaker: '🔊', phone: '📱' };
const DEVICE_ICON = { tv: '📺', headphones: '🎧', speakers: '🔊', other: '🔈' };

/** No sound device at all: the slider would only show a misleading 0 %. */
const noOutput = () => !!quick.audio && (quick.audio.devices?.length ?? 0) === 0;

export const quick = {
  open: false,
  index: 0,
  audio: null,
  pads: [],
  rows: [],
  extraRows: [],
  hints() {
    const row = this.rows?.[this.index];
    if (row?.kind === 'volume') {
      return noOutput() ? [['B', t('quick.hintClose')]] : [['◀▶', t('quick.hintVolume')], ['A', t('quick.hintMute')], ['B', t('quick.hintClose')]];
    }
    // Rows of other modules with a value to change (the TV's volume): ◀▶ changes, A does the row's own thing.
    if (row?.adjust) return [['◀▶', row.adjustHint ?? t('quick.hintVolume')], ['A', row.hint ?? t('quick.hintSelect')], ['B', t('quick.hintClose')]];
    return [['A', row?.hint ?? t('quick.hintSelect')], ['B', t('quick.hintClose')]];
  }
};

/** Other modules add rows (smart home scenes, RGB effects ...): () => [{ icon, label, detail?, run, keepOpen?, hint?, adjust?(step), adjustHint? }] */
export function addQuickRows(provider) {
  quick.extraRows.push(provider);
}

export async function openQuickMenu() {
  // A protected profile waits at the profile picker (components/profiles.js): nothing opens over it.
  if (state.pickerHold) return;
  quick.weather = weatherLine(true);
  // Fresh battery values (the host answers with its last read and sends a new one when something changed).
  refreshBattery();
  quick.open = true;
  state.panel = quick;
  quick.index = 0;
  sound.open();
  renderQuick();
  const [audio, pads, session, actions] = await Promise.all([
    attempt(() => api.getAudio(), t('quick.audioUnavailable')),
    attempt(() => api.getPads()),
    attempt(() => api.getSession()),
    api.getQuickActions ? attempt(() => api.getQuickActions()) : []
  ]);
  quick.audio = audio ?? null;
  quick.pads = pads ?? [];
  quick.pluginActions = actions ?? [];
  if (session !== undefined) state.session = session;
  // Do not start on the dead "no audio output" row.
  if (quick.index === 0 && noOutput() && rows()[0]?.kind === 'volume') quick.index = 1;
  if (quick.open) renderQuick();
  pollPerformance();
}

/** CPU, GPU and RAM while the guide menu is open, like a handheld's performance overlay. */
async function pollPerformance() {
  clearTimeout(quick.perfTimer);
  if (!quick.open || !api.getPerformance) return;
  // One loop at a time, even if the menu was closed and reopened during a request.
  const generation = (quick.perfGeneration = (quick.perfGeneration ?? 0) + 1);
  const perf = await api.getPerformance().catch(() => null);
  if (!quick.open || generation !== quick.perfGeneration) return;
  quick.perf = perf;
  renderQuick();
  quick.perfTimer = setTimeout(pollPerformance, 1500);
}

export function closeQuickMenu() {
  if (!quick.open) return;
  quick.open = false;
  sound.close();
  renderQuick();
}

/** Opens a page from the guide menu on top of wherever you are (article, game, tab): B returns right there. */
function openPage(page) {
  quick.open = false;
  renderQuick();
  push(page);
}

function goTab(index) {
  quick.open = false;
  clearStack();
  renderQuick();
  switchTab(index);
}

function rows() {
  const list = [];
  const s = state.session;
  if (s) {
    list.push({ kind: 'action', icon: '▶', label: t('quick.resume'), detail: s.name, run: () => attempt(() => api.resumeGame()) });
    list.push({
      kind: 'action',
      icon: '✕',
      label: t('quick.quit'),
      run: async () => {
        if (!(await confirmDialog(t('quick.quitConfirm', { name: s.name }), t('quick.quitButton'), { danger: true }))) return;
        if ((await attempt(() => api.quitGame(), t('quick.quitFailed'))) !== undefined) toast(t('quick.quitting', { name: s.name }));
      }
    });
    list.push({
      kind: 'action',
      icon: '⚠',
      label: t('quick.force'),
      detail: t('quick.forceDetail'),
      run: async () => {
        if (!(await confirmDialog(t('quick.forceConfirm', { name: s.name }), t('quick.forceButton'), { danger: true }))) return;
        if ((await attempt(() => api.quitGame(true), t('quick.quitFailed'))) !== undefined) toast(t('quick.forced', { name: s.name }));
      }
    });
  }
  // Unread notifications come right after the game, so a card that just went by is one press away.
  const unread = state.notifications?.unread ?? 0;
  const notificationsRow = { kind: 'action', icon: '🔔', label: t('quick.notifications'), detail: unread ? t('quick.unread', { count: unread }) : t('quick.noUnread'), run: () => openPage(notificationsPage()) };
  if (unread) list.push(notificationsRow);
  if (quick.audio) {
    list.push({ kind: 'volume' });
    list.push({ kind: 'action', icon: '♪', label: t('quick.appVolume'), detail: t('quick.appVolumeDetail'), run: () => openPage(appVolumePage()) });
    const current = quick.audio.devices.find((d) => d.isDefault);
    if (quick.audio.devices.length > 1 && quick.audio.canSwitch) {
      list.push({
        kind: 'action',
        icon: DEVICE_ICON[current?.kind] ?? '🔈',
        label: t('quick.audioOutput'),
        detail: current?.name ?? t('quick.noDevice'),
        keepOpen: true,
        run: async () => {
          const id = await chooseDialog(t('quick.audioOutput'), quick.audio.devices.map((d) => ({ icon: DEVICE_ICON[d.kind] ?? '🔈', label: d.name, value: d.id })), current?.id);
          if (!id) return;
          const audio = await attempt(() => api.setAudioOutput(id), t('quick.switchFailed'));
          if (audio) quick.audio = audio;
        }
      });
    }
  }
  if (!unread) list.push(notificationsRow);
  for (const provider of quick.extraRows) list.push(...provider().map((r) => ({ kind: 'action', ...r })));
  for (const a of quick.pluginActions ?? []) {
    list.push({
      kind: 'action',
      icon: a.icon,
      label: a.label,
      detail: a.detail,
      run: async () => {
        const message = await attempt(() => withParentGrant(() => api.runQuickAction(a.plugin, a.id)), a.label);
        if (message) toast(message);
      }
    });
  }
  // The browser row only while its area is on (Settings › Console › Areas): then it has a tab.
  const browserTab = tabViews().findIndex((v) => v.id === 'browser');
  if (browserTab >= 0) list.push({ kind: 'action', icon: '🌐', label: t('quick.browser'), run: () => goTab(browserTab) });
  list.push({ kind: 'action', icon: '⌂', label: t('quick.home'), run: () => goTab(0) });
  const assistantTab = tabViews().findIndex((v) => v.id === 'assistant');
  if (assistantTab >= 0) list.push({ kind: 'action', icon: '✦', label: t('quick.assistant'), run: () => goTab(assistantTab) });
  list.push({ kind: 'action', icon: '⚙', label: t('quick.settings'), run: () => goTab(tabViews().length - 1) });
  list.push({ kind: 'action', icon: '📸', label: t('quick.screenshot'), detail: s ? t('quick.screenshotOf', { name: s.name }) : t('quick.screenshotScreen'), run: () => capture('screenshot') });
  if (s) list.push({ kind: 'action', icon: '⏺', label: t('quick.clip'), detail: t('quick.clipDetail'), run: () => capture('clip') });
  list.push({ kind: 'action', icon: '▣', label: t('quick.captures'), detail: t('quick.capturesDetail'), run: () => openPage(galleryPage()) });
  list.push({ kind: 'action', icon: '⬇', label: t('quick.downloads'), detail: downloadsQuickDetail(), run: () => goTab(tabViews().findIndex((v) => v.id === 'downloads')) });
  list.push({ kind: 'action', icon: 'ᛒ', label: t('quick.bluetooth'), detail: t('quick.bluetoothDetail'), run: () => openPage(bluetoothPage()) });
  list.push({ kind: 'action', icon: '▤', label: t('quick.tasks'), run: () => openPage(tasksPage()) });
  list.push(...powerRows());
  return list;
}

function volumeRow() {
  const a = quick.audio;
  if (noOutput()) {
    return el('li', { className: 'quick-row quick-volume' },
      el('span', { className: 'quick-icon' }, iconEl('🔇')),
      el('span', { className: 'quick-label' }, el('strong', { textContent: t('quick.noOutput') }), el('small', { textContent: t('quick.noOutputDetail') })));
  }
  const level = a.muted ? 0 : a.volume;
  return el('li', { className: 'quick-row quick-volume' },
    el('span', { className: 'quick-icon' }, iconEl(a.muted ? '🔇' : level > 60 ? '🔊' : level > 20 ? '🔉' : '🔈')),
    el('div', { className: 'quick-slider' },
      el('div', { className: 'quick-track' }, el('div', { className: 'quick-fill', style: { width: `${level}%` } })),
      el('span', { className: 'quick-level', textContent: a.muted ? t('quick.muted') : t('quick.percent', { value: a.volume }) })));
}

/**
 * The batteries in the menu's head: the PC's own (only where there is one), each controller ("🎮 1 ▮▮▯", wired 🔌) and each connected
 * Bluetooth device that reports a percent. Low ones stand out. Names are device names: text only.
 */
function batteryLine() {
  const nodes = [];
  const system = systemBadge(state.battery?.system);
  if (system) nodes.push(el('span', { className: `quick-battery${system.low ? ' battery-low' : ''}`, textContent: `${system.icon} ${fmtPercent(system.percent)}` }));
  for (const b of batteryEntries(quick.pads, state.battery?.devices)) {
    const icon = KIND_ICON[b.kind] ?? '◉';
    const value = b.percent != null ? `${b.value} ${fmtPercent(b.percent)}` : b.value;
    nodes.push(el('span', { className: `quick-pad${b.low ? ' battery-low' : ''}`, textContent: `${icon} ${b.label}${value ? ` ${value}` : ''}` }));
  }
  return nodes;
}

export function renderQuick() {
  const panel = $('#quick');
  if (!quick.open && state.panel === quick) state.panel = null;
  panel.classList.toggle('open', quick.open);
  document.body.classList.toggle('quick-open', quick.open);
  if (quick.open) {
    quick.rows = rows();
    quick.index = Math.min(quick.index, quick.rows.length - 1);
  }
  renderHints();
  if (!quick.open) {
    // Slides out, but nothing in it may count as focused any more.
    quick.items?.forEach((i) => i.classList.remove('focused'));
    return;
  }
  const now = new Date();
  const s = state.session;

  const items = quick.rows.map((r) => r.kind === 'volume'
    ? volumeRow()
    : el('li', { className: 'quick-row' },
      el('span', { className: 'quick-icon' }, iconEl(r.icon)),
      el('span', { className: 'quick-label' }, el('strong', { textContent: r.label }), r.detail ? el('small', { textContent: r.detail }) : null)));
  quick.items = items;
  const list = el('ul', { className: 'quick-list list-scroll' }, items);

  // replaceChildren would print a null (no game running) as text.
  panel.replaceChildren(...[
    el('header', { className: 'quick-head' },
      el('div', { className: 'quick-time', textContent: fmtTime(now) }),
      el('div', { className: 'quick-date', textContent: fmtDate(now, { weekday: 'long', day: 'numeric', month: 'long' }) }),
      el('div', { className: 'quick-status' },
        quick.weather ? el('span', { textContent: quick.weather }) : null,
        remainingLabel() ? el('span', { className: 'quick-parental', textContent: `⏳ ${remainingLabel()}` }) : null,
        batteryLine()),
      // Space is reserved from the first frame, so the list does not jump once the numbers arrive.
      api.getPerformance ? el('div', { className: 'quick-perf' },
        el('span', {}, el('b', { textContent: quick.perf ? fmtPercent(quick.perf.cpu) : '–' }), 'CPU'),
        quick.perf?.gpu != null || !quick.perf ? el('span', {}, el('b', { textContent: quick.perf ? fmtPercent(quick.perf.gpu) : '–' }), 'GPU') : null,
        el('span', {}, el('b', { textContent: quick.perf ? t('quick.ramUsed', { value: fmtNumber(quick.perf.ramUsedGb) }) : '–' }), quick.perf ? t('quick.ramOf', { total: Math.round(quick.perf.ramTotalGb) }) : t('quick.ram'))) : null),
    s ? el('section', { className: 'quick-session' },
      el('small', { textContent: t('quick.running') }),
      el('strong', { textContent: s.name }),
      el('span', { textContent: t('quick.since', { duration: formatDuration(Math.max(1, (Date.now() - s.started) / 60000)) }) })) : null,
    list].filter(Boolean));
  setFocused(items, quick.index);
  keepInView(list, items[quick.index], { instant: true });
}

async function changeVolume(delta) {
  const a = quick.audio;
  const next = Math.max(0, Math.min(100, (a.muted ? 0 : a.volume) + delta));
  if (next === a.volume && !a.muted) return sound.bump();
  sound.move();
  const updated = await attempt(() => api.setVolume(next));
  if (updated) quick.audio = updated;
  renderQuick();
}

export async function handleQuick(action) {
  const row = quick.rows[quick.index];
  switch (action) {
    case 'up':
    case 'down': {
      const next = quick.index + (action === 'up' ? -1 : 1);
      if (next < 0 || next >= quick.rows.length) return sound.bump();
      quick.index = next;
      sound.move();
      return renderQuick();
    }
    case 'left':
    case 'right':
      if (row.kind === 'volume') return noOutput() ? sound.bump() : changeVolume(action === 'left' ? -5 : 5);
      if (row.adjust) {
        sound.move();
        await row.adjust(action === 'left' ? -1 : 1);
        return renderQuick();
      }
      return;
    case 'a':
      if (row.kind === 'volume' && noOutput()) return sound.bump();
      sound.select();
      if (row.kind === 'volume') {
        const updated = await attempt(() => api.setMute(!quick.audio.muted));
        if (updated) quick.audio = updated;
        return renderQuick();
      }
      if (row.keepOpen) {
        await row.run();
        return renderQuick();
      }
      quick.open = false;
      renderQuick();
      await row.run();
      return;
    case 'b':
    case 'guide':
    case 'start':
      return closeQuickMenu();
  }
}

/** Updates from the host while the panel is open. */
export function refreshQuick() {
  if (quick.open) renderQuick();
  else render();
}
