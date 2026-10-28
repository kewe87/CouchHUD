// Parental controls in the UI: the remaining time in the header, the "time is up" screen,
// the gate in front of a game start and the page of locked games. The rules live in the
// host (Core/Parental); this file only shows them and asks for the parents' code.

import { iconEl } from '../core/glyph-icons.js';
import { api } from '../core/api.js';
import { $, el, keepInView, setFocused } from '../core/dom.js';
import { attempt, toast } from '../core/feedback.js';
import { move, push, removePage, render } from '../core/nav.js';
import { state } from '../core/state.js';
import { sound } from '../sound.js';
import { t } from '../core/i18n.js';
import { chooseDialog, closeOverlay } from './overlay.js';
import { ensureParentCode, ensureUnlocked } from './passcode.js';

export const parental = { status: null };

// The guide menu is wired in from app.js (importing it here would make a cycle through the views).
let quickHooks = { close: () => {}, redraw: () => {} };

/** "1:20 h" from an hour on, "25 Min." below. */
/** The parents' code for one action; without a code set up, the plain unlock. */
export async function ensureParentGrant(reason = '', scope = {}) {
  const status = parental.status ?? (await refreshParental());
  return status?.hasParentCode ? ensureParentCode(reason, scope) : ensureUnlocked(reason);
}

export function formatRemaining(seconds) {
  const minutes = Math.max(0, Math.ceil(seconds / 60));
  if (minutes < 60) return t('parental.minutes', { count: minutes });
  return `${Math.floor(minutes / 60)}:${String(minutes % 60).padStart(2, '0')} h`;
}

/** The line for header and guide menu, or null when nothing is limited. */
export function remainingLabel() {
  const s = parental.status;
  if (!s?.enabled || s.remainingSeconds == null) return null;
  const left = s.remainingSeconds <= 0 ? t('parental.timeUp') : t('parental.todayLeft', { time: formatRemaining(s.remainingSeconds) });
  return s.enforced ? left : t('parental.advisorySuffix', { text: left });
}

function showBadge() {
  const badge = $('#parental-status');
  if (!badge) return;
  const label = remainingLabel();
  badge.hidden = !label;
  badge.textContent = label ? `⏳ ${label}` : '';
  badge.classList.toggle('warn', !!label && parental.status.remainingSeconds <= 15 * 60);
  badge.title = label && !parental.status.enforced ? t('parental.advisoryTip') : '';
}

export async function refreshParental() {
  const status = await attempt(() => api.getParental?.());
  if (status !== undefined) parental.status = status;
  showBadge();
  return parental.status;
}

export function startParental({ detailPage, closeQuickMenu, redrawQuick } = {}) {
  quickHooks = { close: closeQuickMenu ?? quickHooks.close, redraw: redrawQuick ?? quickHooks.redraw };
  detailPage?.extraActions.push((item) => itemLockAction(item));
  if (!api.getParental) return;
  refreshParental().then((s) => {
    if (s?.expired && state.session) timeUpPage();
  });
  api.onParental?.(async (event) => {
    const s = await refreshParental();
    if (event?.kind === 'warn') {
      sound.bump();
      api.rumble?.('alert').catch(() => {});
      toast(t('parental.warnToast', { count: event.minutes }), 8000);
    }
    if (event?.kind === 'expired' && state.session) timeUpPage();
    if (timeUp && s && !s.expired) closeTimeUp();
    // The game could not be ended: back to the choices.
    if (timeUp && s?.closeFailed && timeUp.closing) {
      timeUp.closing = false;
      render();
    }
    quickHooks.redraw();
  });
  api.onSession?.((session) => {
    if (!session) return closeTimeUp();
    // Frozen now or not (the host freezes only once the HUD is in front, never a game with an anti-cheat): the text follows.
    if (timeUp && timeUp.paused !== !!session.paused) {
      timeUp.paused = !!session.paused;
      render();
    }
  });
  setInterval(refreshParental, 60_000);
}

// ---------------------------------------------------------------------------
// Time is up

let timeUp = null;

function closeTimeUp() {
  if (!timeUp) return;
  const page = timeUp;
  timeUp = null;
  removePage(page);
  render();
}

async function extendFlow() {
  if (!(await ensureParentGrant(t('parental.extendReason'), { action: 'extend' }))) return;
  const minutes = await chooseDialog(t('parental.extendTitle'), [
    { label: t('parental.minutesLong', { count: 15 }), value: 15 },
    { label: t('parental.minutesLong', { count: 30 }), value: 30 },
    { label: t('parental.minutesLong', { count: 60 }), value: 60 }
  ], 15);
  if (!minutes) return;
  const status = await attempt(() => api.parentalExtend(minutes), t('parental.extendFailed'));
  if (!status) return;
  parental.status = status;
  showBadge();
  closeTimeUp();
  toast(t('parental.extended', { count: minutes }));
}

/** The screen that stays until the parents decide or the game is closed. Nothing but its two buttons works. */
export function timeUpPage() {
  if (timeUp) return;
  quickHooks.close();
  if (document.querySelector('#overlay:not([hidden])')) closeOverlay();
  const advisory = !!parental.status?.advisory;
  const page = {
    id: 'parental-timeup',
    name: t('parental.timeUp'),
    index: 0,
    closing: false,
    // Whether the game really is frozen (session.paused): one with an anti-cheat is not, the HUD only covers it; the text says which.
    paused: !!state.session?.paused,
    // Guide, B and Start must not get out of here.
    captures: () => true,
    entries() {
      const list = [
        { label: t('parental.timeupSave'), detail: t('parental.timeupSaveDetail'), icon: '💾', run: () => this.closeGame() },
        { label: t('parental.timeupExtend'), detail: t('parental.timeupExtendDetail'), icon: '🔑', run: extendFlow }
      ];
      if (advisory) list.push({ label: t('parental.timeupContinue'), detail: t('parental.timeupContinueDetail'), icon: '▶', run: () => attempt(() => api.resumeGame()).then(() => closeTimeUp()) });
      return list;
    },
    async closeGame() {
      this.closing = true;
      render();
      await attempt(() => api.parentalCloseGame(), t('parental.closeFailed'));
    },
    build(root) {
      this.list = this.entries();
      this.index = Math.min(this.index, this.list.length - 1);
      this.buttons = this.list.map((e) => el('li', { className: 'timeup-option' },
        el('span', { className: 'timeup-icon' }, iconEl(e.icon)),
        el('span', {}, el('strong', { textContent: e.label }), el('small', { textContent: e.detail }))));
      const game = parental.status?.game;
      root.append(el('div', { className: 'timeup' },
        el('div', { className: 'passcode-lock', textContent: '⏳' }),
        el('h2', { textContent: parental.status?.blocked ? t('parental.blockedTitle') : t('parental.timeUp') }),
        el('p', { className: 'meta', textContent: this.closing || parental.status?.closing
          ? t('parental.timeupClosing')
          : parental.status?.blocked
            ? t('parental.blockedText', { game: parental.status.blockedGame ?? '', reason: parental.status.blockedMessage ?? '', count: parental.status?.graceSeconds ?? 60 })
          : parental.status?.closeFailed
            ? t('parental.timeupCloseFailed')
            : advisory
              ? (!game ? t('parental.timeupAdvisory') : this.paused ? t('parental.timeupAdvisoryGame', { game }) : t('parental.timeupAdvisoryGameRunning', { game }))
              : (!game
                ? t('parental.timeupGrace', { count: parental.status?.graceSeconds ?? 60 })
                : this.paused
                  ? t('parental.timeupGraceGame', { game, count: parental.status?.graceSeconds ?? 60 })
                  : t('parental.timeupGraceGameRunning', { game, count: parental.status?.graceSeconds ?? 60 })) }),
        // While the game is being closed there is nothing to decide any more (the host also calls the closing off if time is added).
        this.closing || parental.status?.closing ? null : el('ul', { className: 'timeup-list' }, this.buttons)));
      this.update();
    },
    update() {
      if (this.buttons?.length) setFocused(this.buttons, this.index);
    },
    handle(action) {
      if (this.closing) return;
      switch (action) {
        case 'up': return move(this, -1, this.list.length, true);
        case 'down': return move(this, 1, this.list.length, true);
        case 'a': {
          sound.select();
          return attempt(() => this.list[this.index].run());
        }
        default: sound.bump();
      }
    },
    hints() {
      return this.closing ? [] : [['A', t('parental.hintSelect')]];
    }
  };
  timeUp = page;
  push(page);
}

// ---------------------------------------------------------------------------
// Before a start

/**
 * Asks the host whether this may start. True if yes (possibly after the parents' code).
 * Otherwise explains why, with the way to extend the time.
 */
export async function parentalGate(item) {
  if (!api.parentalCheck) return true;
  let check = await attempt(() => api.parentalCheck(item.id));
  if (!check || (check.ok && !check.message)) return true;
  if (check.ok) {
    toast(check.message, 6000);
    return true;
  }
  if (check.needsCode) {
    if (!(await ensureParentGrant(t('parental.gateReason', { name: item.name }), { action: 'launch', item: item.id }))) return false;
    check = await attempt(() => api.parentalCheck(item.id));
    if (check?.ok) return true;
  }
  if (!check) return false;
  sound.bump();
  api.rumble?.('alert').catch(() => {});
  const choice = await chooseDialog(t('parental.gateTitle', { name: item.name }), [
    { label: t('parental.gateOk'), detail: check.message, value: 'ok' },
    { label: t('parental.timeupExtend'), detail: t('parental.timeupExtendDetail'), value: 'extend' }
  ], 'ok');
  if (choice !== 'extend') return false;
  await extendFlow();
  const again = await attempt(() => api.parentalCheck(item.id));
  if (again?.ok) return true;
  if (again?.message) toast(again.message, 5000);
  return false;
}

// ---------------------------------------------------------------------------
// Locked games (settings page and detail menu)

/** "Nur mit Eltern-Code starten" toggle for one entry; asks for the code first. */
export async function toggleItemLock(item) {
  const locked = !!parental.status?.lockedItems?.includes(item.id);
  if (!(await ensureParentGrant(locked ? t('parental.lockOffReason') : t('parental.lockOnReason'), { action: 'lock', item: item.id }))) return;
  const status = await attempt(() => api.setParentalItemLock(item.id, !locked), t('parental.notSaved'));
  if (!status) return;
  parental.status = status;
  toast(locked ? t('parental.unlockedToast', { name: item.name }) : t('parental.lockedToast', { name: item.name }));
  render();
}

/** Detail page row; null while the profile is off, so the menu stays as it was. */
export const itemLockAction = (item) => {
  if (!parental.status?.enabled) return null;
  const locked = !!parental.status.lockedItems?.includes(item.id);
  return { label: locked ? t('parental.lockOff') : t('parental.lockOn'), icon: locked ? '🔓' : '🔒', run: () => toggleItemLock(item) };
};

/** All locked entries; A takes the lock off again. */
export function lockedGamesPage() {
  return {
    id: 'parental-locked',
    name: t('parental.lockedPageName'),
    index: 0,
    build(root) {
      const ids = parental.status?.lockedItems ?? [];
      this.list = ids.map((id) => state.library.find((i) => i.id === id)).filter(Boolean);
      this.index = Math.min(this.index, Math.max(0, this.list.length - 1));
      this.items = this.list.map((i) => el('li', { className: 'check-row' },
        el('span', { className: 'check-box', textContent: '🔒' }),
        el('span', { className: 'check-main' }, el('strong', { textContent: i.name }), el('small', { textContent: i.type === 'app' ? t('parental.typeApp') : t('parental.typeGame') }))));
      this.scroller = el('ul', { className: 'check-list list-scroll' }, this.items);
      root.append(el('div', { className: 'files' },
        el('div', { className: 'files-head' }, el('h2', { textContent: t('parental.lockedTitle') })),
        this.list.length ? this.scroller : el('p', { className: 'empty-note', textContent: t('parental.lockedEmpty') })));
      this.update({ instant: true });
    },
    update({ instant = false } = {}) {
      if (!this.items.length) return;
      setFocused(this.items, this.index);
      keepInView(this.scroller, this.items[this.index], { instant });
    },
    async handle(action) {
      switch (action) {
        case 'up': return move(this, -1, this.list.length, true);
        case 'down': return move(this, 1, this.list.length, true);
        case 'a': {
          const item = this.list[this.index];
          if (item) await toggleItemLock(item);
        }
      }
    },
    hints() {
      return this.list?.length ? [['A', t('parental.hintUnlock')], ['B', t('parental.hintBack')]] : [['B', t('parental.hintBack')]];
    }
  };
}
