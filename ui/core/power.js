// Power actions in one place: Start menu, Guide menu and settings use the same
// entries, texts and confirmations.
//
//   requestPower(id)            asks first (focus on "Abbrechen"), then runs; resolves to true if it ran
//   powerMenu({ sleepTimer })   overlay menu definition for openOverlay(): two groups (CouchHUD, Computer), see power-rules.js
//   powerEntries(ids?)          [{ id, icon, label, detail, run }] for custom menus (settings, ...)
//   powerRows()                 Guide menu rows
//   chooseSleepTimer()          sleep timer picker (minutes, then shut down or standby); sleepTimerDetail() is its status text
//
// Ids: standby, pc-mode, restart-app, restart, restart-pc, shutdown, quit. The mode decides which are offered (see forMode).

import { api } from './api.js';
import { state } from './state.js';
import { attempt, toast } from './feedback.js';
import { t } from './i18n.js';
import { chooseDialog, closeOverlay, closeTagged, confirmDialog, openOverlay } from '../components/overlay.js';
import { ensureUnlocked } from '../components/passcode.js';
import { POWER_START, SLEEP_TIMER, forMode as forModeRule, isDangerous, powerGroups, powerIndex } from './power-rules.js';
import { TIMER_ACTIONS, TIMER_ACTION_KEY, TIMER_WARN_MS, timerAction, timerTexts, timerUnlock, warnDelay } from './sleep-timer-rules.js';

/** How the Start menu lays out its groups: 'list' (rows with headings) or 'tiles' (one row of large tiles per group). */
export const POWER_MENU_LAYOUT = 'tiles';

const dataLoss = () => t('power.dataLoss');

export const POWER_ACTIONS = [
  {
    id: 'standby',
    icon: '☾',
    get label() { return t('power.standby.label'); },
    get detail() { return t('power.standby.detail'); },
    get confirm() { return { title: t('power.standby.confirmTitle'), label: t('power.standby.label'), message: t('power.standby.confirmMessage') }; },
    run: () => api.power('sleep')
  },
  {
    id: 'pc-mode',
    icon: '▭',
    get label() { return t('power.pcMode.label'); },
    get detail() { return t('power.pcMode.detail'); },
    get confirm() { return { title: t('power.pcMode.confirmTitle'), label: t('power.pcMode.label'), message: t('power.pcMode.confirmMessage') }; },
    get unlock() { return t('power.pcMode.unlock'); },
    run: () => api.enterPcMode()
  },
  {
    // CouchHUD itself starts again (plugin changes take effect, the couch lock and the profile come back as they were): behind the same
    // couch lock as quitting; the host refuses while a game runs.
    id: 'restart-app',
    icon: '⟲',
    get label() { return t('power.restartApp.label'); },
    get detail() { return t('power.restartApp.detail'); },
    get confirm() { return { title: t('power.restartApp.confirmTitle'), label: t('power.restartApp.label'), message: t('power.restartApp.confirmMessage') }; },
    get unlock() { return t('power.restartApp.label'); },
    run: () => api.restartCouchHud()
  },
  {
    id: 'restart',
    icon: '↻',
    get label() { return t('power.restart.label'); },
    get detail() { return t('power.restart.detail'); },
    get confirm() { return { title: t('power.restart.confirmTitle'), label: t('power.restart.label'), message: dataLoss() }; },
    run: () => api.power('restart')
  },
  {
    id: 'restart-pc',
    icon: '⇄',
    get label() { return t('power.restartPc.label'); },
    get detail() { return t('power.restartPc.detail'); },
    get confirm() { return { title: t('power.restartPc.confirmTitle'), label: t('power.restart.label'), message: t('power.restartPc.confirmMessage', { loss: dataLoss() }) }; },
    get unlock() { return t('power.restartPc.unlock'); },
    run: () => api.power('restart-pc')
  },
  {
    // App mode: CouchHUD is a program on the desktop, so "PC mode" simply means closing it (the host asks for the couch lock too).
    id: 'quit',
    icon: '✕',
    get label() { return t('power.quit.label'); },
    get detail() { return t('power.quit.detail'); },
    get confirm() { return { title: t('power.quit.confirmTitle'), label: t('power.quit.label'), message: t('power.quit.confirmMessage') }; },
    get unlock() { return t('power.quit.label'); },
    run: () => api.quit()
  },
  {
    id: 'shutdown',
    icon: '⏻',
    get label() { return t('power.shutdown.label'); },
    get detail() { return t('power.shutdown.detail'); },
    get confirm() { return { title: t('power.shutdown.confirmTitle'), label: t('power.shutdown.label'), message: dataLoss() }; },
    run: () => api.power('shutdown')
  }
];

const byId = (id) => POWER_ACTIONS.find((a) => a.id === id);

/** The action an id stands for in the current mode (rules in power-rules.js). */
export function forMode(id, isShell = !!state.info?.isShell) {
  return forModeRule(id, isShell);
}

/** Asks for confirmation (and the couch lock where needed), then runs the action. */
export async function requestPower(id) {
  const action = byId(forMode(id));
  if (!action) return false;
  const ok = await confirmDialog(action.confirm.title, action.confirm.label, { danger: true, message: action.confirm.message, icon: action.icon });
  if (!ok) return false;
  if (action.unlock && !(await ensureUnlocked(action.unlock))) return false;
  return (await attempt(async () => (await action.run(), true), action.label)) === true;
}

/** Entries for a custom menu. */
export function powerEntries(ids = POWER_ACTIONS.map((a) => a.id), { onCancel } = {}) {
  const shown = [...new Set(ids.map((id) => forMode(id)).filter(Boolean))];
  return shown.map(byId).filter(Boolean).map((a) => ({
    id: a.id,
    icon: a.icon,
    label: a.label,
    detail: a.detail,
    danger: isDangerous(a.id),
    run: async () => {
      // Cancelling the question goes back to the menu it came from, not out of everything.
      if (!(await requestPower(a.id))) onCancel?.(a.id);
    }
  }));
}

/**
 * The Start menu: CouchHUD's own entries on top, the computer's below a separator (power-rules.js). Focus starts on standby; after a
 * cancelled question the menu comes back on the entry the question was about. No "Abbrechen" entry: B, a click next to the menu
 * or the B hint close it.
 */
export function powerMenu({ sleepTimer = true, index, layout = POWER_MENU_LAYOUT } = {}) {
  const groups = powerGroups(!!state.info?.isShell, { sleepTimer });
  const ids = groups.flatMap((g) => g.ids);
  const reopen = (id) => openOverlay(powerMenu({ sleepTimer, layout, index: powerIndex(ids, id) }));
  const actions = new Map(powerEntries(ids.filter((id) => id !== SLEEP_TIMER), { onCancel: reopen }).map((e) => [e.id, e]));
  // i18n-used: power.group.app power.group.pc
  // Object.assign, not a spread: the timer's value is a getter that must stay live.
  const entries = groups.flatMap((g) => g.ids.map((id) => Object.assign(id === SLEEP_TIMER ? sleepTimerEntry(() => reopen(SLEEP_TIMER)) : actions.get(id), { group: t(g.heading) })));
  return {
    title: t('power.title'),
    entries,
    layout,
    details: layout === 'tiles' ? 'below' : 'focused',
    // A running timer counts down while the menu is open (the option above shadows the timer's state, hence timerRunning()).
    refreshMs: sleepTimer && timerRunning() ? 15_000 : 0,
    index: index ?? powerIndex(ids, POWER_START)
  };
}

/**
 * The timer in the menu: a running one says its remaining time once (on the right of the row, under the tile), "Aus" is its
 * detail. Cancelling the picker comes back to the menu on the timer.
 */
function sleepTimerEntry(back) {
  return {
    id: SLEEP_TIMER,
    icon: '⏲',
    label: t('power.sleepTimer'),
    get detail() { return sleepTimer.at ? '' : sleepTimerDetail(); },
    get value() { return sleepTimer.at ? sleepTimerDetail() : ''; },
    run: () => pickSleepTimer(back)
  };
}

/** One row for the Guide menu that opens the shared menu, plus the sleep timer. */
export function powerRows() {
  return [
    // i18n-used: power.rowDetail power.rowDetailApp
    { kind: 'action', icon: '⏻', label: t('power.title'), detail: t(state.info?.isShell ? 'power.rowDetail' : 'power.rowDetailApp'), run: () => openOverlay(powerMenu({ sleepTimer: false })) },
    { kind: 'action', icon: '⏲', label: t('power.sleepTimer'), detail: sleepTimerDetail(), keepOpen: true, run: chooseSleepTimer }
  ];
}

// Sleep timer: "in 60 minutes shut down" (or standby), like on a TV. The action is a power entry's id (sleep-timer-rules.js); when
// the timer runs out, that entry's own host call runs, without asking again: it was authorised when the timer was set.
// gen tells an old warning (its timer was cancelled, replaced or fired) from the current one.
const sleepTimer = { at: 0, action: '', handle: 0, warnHandle: 0, gen: 0 };
const WARN_TAG = 'sleep-timer-warning';

function timerRunning() {
  return sleepTimer.at > 0;
}

export function sleepTimerDetail() {
  return sleepTimer.at ? t(timerTexts(sleepTimer.action).status, { n: Math.max(1, Math.round((sleepTimer.at - Date.now()) / 60_000)) }) : t('power.off');
}

export function chooseSleepTimer() {
  return pickSleepTimer();
}

/** The last used action (a fresh install, blocked storage or an unknown stored value: shut down). */
function lastAction() {
  try { return timerAction(localStorage.getItem(TIMER_ACTION_KEY)); } catch { return timerAction(''); }
}

function rememberAction(action) {
  try { localStorage.setItem(TIMER_ACTION_KEY, action); } catch { /* storage blocked: the default comes back next time */ }
}

/**
 * The picker: the minutes, then what happens (preselected: the last used action). B in the second step goes back to the minutes,
 * B in the minutes runs onCancel (the power menu comes back). "Timer aus" is one step.
 */
async function pickSleepTimer(onCancel) {
  const options = [30, 60, 90, 120].map((m) => ({ label: t('power.inMinutes', { n: m }), value: m }));
  if (sleepTimer.at) options.push({ label: t('power.timerOff'), value: 0 });
  let minutes;
  let action;
  do {
    minutes = await chooseDialog(t('power.timerTitle'), options, minutes);
    if (minutes === undefined) return onCancel?.();
    if (!minutes) {
      stopSleepTimer();
      return toast(t('power.timerOff'));
    }
    action = await chooseDialog(t('power.timerActionTitle', { n: minutes }), TIMER_ACTIONS.map((id) => {
      const entry = byId(id);
      return { label: entry.label, icon: entry.icon, value: id };
    }), lastAction());
  } while (action === undefined);
  action = timerAction(action);
  // The same couch lock as the entry itself: setting a timer is never a way around it.
  const unlock = timerUnlock(action, (id) => byId(id)?.unlock);
  if (unlock && !(await ensureUnlocked(unlock))) return;
  rememberAction(action);
  startSleepTimer(minutes * 60_000, action);
  toast(t(timerTexts(action).set, { n: minutes }));
}

function stopSleepTimer() {
  clearTimeout(sleepTimer.handle);
  clearTimeout(sleepTimer.warnHandle);
  sleepTimer.at = 0;
  sleepTimer.action = '';
  sleepTimer.gen++;
  closeTagged(WARN_TAG);
}

function startSleepTimer(ms, action) {
  stopSleepTimer();
  sleepTimer.at = Date.now() + ms;
  sleepTimer.action = action;
  sleepTimer.warnHandle = setTimeout(warnSleepTimer, warnDelay(ms));
  sleepTimer.handle = setTimeout(fireSleepTimer, ms);
}

/** Runs the power entry the timer was set for (its own host call only; the action was checked when it was stored). */
function fireSleepTimer() {
  const action = byId(timerAction(sleepTimer.action));
  stopSleepTimer();
  attempt(() => action.run(), action.label);
}

/**
 * A minute before the end: a question the controller can reach, "Abbrechen" (the timer ends) or "Jetzt". Without an answer the
 * timer fires at 0 and closes the question. A menu that is open gives way (cancelled, as with B).
 */
async function warnSleepTimer() {
  const gen = sleepTimer.gen;
  const action = timerAction(sleepTimer.action);
  if (state.overlay) closeOverlay();
  await new Promise((resolve) => setTimeout(resolve, 0));
  if (gen !== sleepTimer.gen) return;
  const entry = byId(action);
  const now = await confirmDialog(t(timerTexts(action).warn, { n: Math.round(TIMER_WARN_MS / 1000) }), t('power.timerNow'), {
    danger: action === 'shutdown', icon: entry.icon, tag: WARN_TAG, focusCancel: true
  });
  // The timer was cancelled, replaced or fired meanwhile: this answer is about nothing any more.
  if (gen !== sleepTimer.gen) return;
  if (now) return fireSleepTimer();
  stopSleepTimer();
  toast(t('power.timerOff'));
}

/**
 * A notice that a plugin change takes effect at the next start, with a way to do it now: a question ("Jetzt neu starten" / "Später"),
 * because a toast cannot be reached with the controller. The same couch lock and the same host call as the power menu's entry.
 */
export async function offerRestart(text) {
  const ok = await confirmDialog(text, t('power.restartApp.now'), { message: t('power.restartApp.offer'), icon: '⟲', cancelLabel: t('power.restartApp.later') });
  if (!ok) return false;
  const action = byId('restart-app');
  if (!(await ensureUnlocked(action.unlock))) return false;
  return (await attempt(async () => (await action.run(), true), action.label)) === true;
}
