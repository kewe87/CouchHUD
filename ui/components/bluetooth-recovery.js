// Bluetooth went off while keyboard or mouse were in use: the host switches it on again after a countdown (a Bluetooth controller
// would be gone otherwise). This asks "now" or "leave it off"; nobody answers, it comes back on. B or a click next to the dialog only
// hides it, the countdown goes on: a stray Escape must not leave the couch without its controller.

import { api } from '../core/api.js';
import { state } from '../core/state.js';
import { attempt, toast } from '../core/feedback.js';
import { t } from '../core/i18n.js';
import { closeOverlay, closeTagged, openOverlay, overlayFree, renderOverlay } from './overlay.js';
import { currentView } from '../core/nav.js';
import { holdsDialogs } from '../core/input-help.js';

const TAG = 'bluetooth-recovery';
let deadline = 0;
let timer = null;
let menu = null;
let shown = -1;
// A countdown was announced and the host has not said how it ended yet.
let announced = false;

const remaining = () => Math.max(0, Math.ceil((deadline - Date.now()) / 1000));
const message = () => (remaining() > 0 ? t('btRecovery.countdown', { count: remaining() }) : t('btRecovery.switching'));

function stop() {
  clearInterval(timer);
  timer = null;
  deadline = 0;
  const open = menu;
  menu = null;
  if (open && state.overlay === open) closeTagged(TAG);
}

function tick() {
  if (!menu || state.overlay !== menu) return;
  // A code prompt or the time-up page came up under the dialog: step aside (the next button belongs to them), come back after.
  if (holdsDialogs(currentView())) {
    menu = null;
    clearInterval(timer);
    timer = null;
    closeOverlay({ cancelled: false });
    present(deadline);
    return;
  }
  const left = remaining();
  if (left === shown) return;
  shown = left;
  menu.message = message();
  renderOverlay();
  if (left === 0) clearInterval(timer);
}

async function answer(keepOff) {
  stop();
  const keptOff = await attempt(() => api.answerBluetoothRecovery(keepOff), t('btRecovery.failed'));
  // Only when the host really left it off: a countdown that had already run out switched it on.
  if (keepOff && keptOff === true) toast(t('btRecovery.keptOff'), 6000);
}

/**
 * Waits until no menu is open and no code prompt or time-up page is on screen (a dialog there would take the next button as its
 * answer). False once this countdown is over or replaced.
 */
async function freeFor(mine) {
  for (;;) {
    if (deadline !== mine || remaining() === 0) return false;
    if (holdsDialogs(currentView())) {
      await new Promise((resolve) => setTimeout(resolve, 300));
      continue;
    }
    if (!state.overlay) return true;
    await overlayFree();
  }
}

async function show(seconds) {
  stop();
  deadline = Date.now() + Math.max(1, Number(seconds) || 10) * 1000;
  announced = true;
  const mine = deadline;
  toast(t('btRecovery.toast', { count: remaining() }), 4000);
  await present(mine);
}

async function present(mine) {
  // A menu the user is in, the code prompt or the time-up page stay; the question follows once they are closed (if the countdown
  // still runs). Meanwhile the toast and the hint in the code prompt say what happens.
  if (!(await freeFor(mine))) return;
  const dialog = {
    title: t('btRecovery.title'),
    message: message(),
    tag: TAG,
    index: 0,
    onCancel: () => {
      if (menu !== dialog) return;
      menu = null;
      toast(t('btRecovery.continues'), 4000);
    },
    entries: [
      { label: t('btRecovery.now'), run: () => answer(false) },
      { label: t('btRecovery.keepOff'), run: () => answer(true) }
    ],
    hints: () => [['A', t('btRecovery.hintChoose')], ['B', t('btRecovery.hintHide')]]
  };
  menu = dialog;
  shown = -1;
  openOverlay(dialog);
  timer = setInterval(tick, 250);
}

export function startBluetoothRecovery() {
  api.onBluetoothRecovery?.((payload) => {
    const wasAnnounced = announced;
    if (payload?.phase !== 'countdown') announced = false;
    switch (payload?.phase) {
      case 'countdown':
        return show(payload.seconds);
      case 'enabled':
        stop();
        return toast(t('btRecovery.enabled'), 6000);
      case 'paused':
        stop();
        return toast(t('btRecovery.paused'), 10000);
      case 'failed':
        // Only worth a word when a countdown was announced (also after "now").
        stop();
        if (wasAnnounced) toast(t('btRecovery.switchFailed'), 6000);
        return;
      default:
        stop();
    }
  });
}
