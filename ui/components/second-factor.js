// The second factor on a phone (0.7), TV side. The host answers a critical action with "twofa" once the code was accepted: this shows the
// question (a two-digit number, the phone offers three to pick from), waits for the phone, and offers the Windows administrator as the
// emergency way. B cancels. The bridge repeats the call when this resolves true; the host decides again with a one-time ticket.

import { api } from '../core/api.js';
import { t } from '../core/i18n.js';
import { toast } from '../core/feedback.js';
import { noticeDialog, openOverlay, renderOverlay } from './overlay.js';
import { state } from '../core/state.js';
import { sound } from '../sound.js';
import { secondFactorText } from '../core/second-factor-rules.js';

export function startSecondFactor() {
  api.setSecondFactorHandler?.(askSecondFactor);
}

/** What the TV shows: the number big enough to read from the couch, the action, the time left and where it is answered. */
function show(info, left) {
  return secondFactorText(info, left, t);
}

/**
 * One question until it ends. Resolves true when a phone or the administrator confirmed, false when it was cancelled, answered wrongly or ran out.
 * Several rounds are possible: the administrator's way that was not completed returns to the question (same request, same number).
 */
export async function askSecondFactor(info) {
  // A question was asked a moment ago: new ones come at most every 10 seconds, nothing to confirm yet.
  if ((info.throttledSeconds ?? 0) > 0) {
    toast(t('twofa.throttledToast', { seconds: info.throttledSeconds }), 6000);
    return false;
  }
  let current = info;
  for (;;) {
    const outcome = await ask(current);
    if (outcome === 'confirmed') return true;
    if (outcome === 'admin') {
      toast(t('twofa.adminPrompt'), 5000);
      const result = await api.twoFactorAdmin(current.requestId).catch(() => ({ ok: false }));
      if (result?.ok) return true;
      if (result?.blocked) toast(result.blocked, 8000);
      // Not confirmed (cancelled UAC): the question is still open if its time is not up.
      const again = await api.twoFactorState().catch(() => null);
      if (!again || again.requestId !== current.requestId) return false;
      current = { ...current, leftMs: again.secondsLeft * 1000, reachable: again.reachable, lockedOutSeconds: again.lockedOutSeconds };
      continue;
    }
    api.twoFactorCancel(current.requestId).catch(() => {});
    return false;
  }
}

function ask(info) {
  return new Promise((resolve) => {
    let left = Math.max(1, Math.ceil((info.leftMs ?? 60000) / 1000));
    let settled = false;
    let misses = 0;
    const menu = {
      title: t('twofa.title'),
      message: show(info, left),
      entries: [
        { label: t('twofa.admin'), detail: t('twofa.adminDesc'), icon: '🛡', run: () => finish('admin') },
        { label: t('overlay.cancel'), icon: '✕', run: () => finish('cancel') }
      ],
      // The admin entry first only when no phone can answer (lost, empty, locked out); otherwise Cancel is where the focus starts.
      index: info.reachable && !info.lockedOutSeconds ? 1 : 0,
      onCancel: () => finish('cancel')
    };
    const timer = setInterval(async () => {
      if (settled) return;
      left -= 1;
      if (left <= 0) return finish('expired');
      const live = await api.twoFactorState().catch(() => undefined);
      if (settled) return;
      if (live === null) {
        // The question is gone: answered (the event tells how), cancelled or replaced. Two quiet polls and it counts as over.
        if (++misses >= 2) finish('ended');
      } else if (live) {
        misses = 0;
        menu.message = show({ ...info, ...live, leftMs: left * 1000 }, left);
      }
      if (state.overlay === menu) renderOverlay();
    }, 1000);

    function onEvent(event) {
      if (settled) return;
      if (event?.kind === 'confirmed') finish('confirmed');
      else if (event?.kind === 'answered') finish(event.result === 'LockedOut' ? 'locked' : 'wrong');
    }

    function finish(outcome) {
      if (settled) return;
      settled = true;
      clearInterval(timer);
      handlers.delete(onEvent);
      if (state.overlay === menu) {
        state.overlay = null;
        renderOverlay();
      }
      if (outcome === 'confirmed') sound.select?.();
      else if (outcome === 'wrong') toast(t('twofa.wrong'), 8000);
      else if (outcome === 'locked') {
        // A dialog, not a toast: the reason stays until it is read. The refusal itself (cancel) is the same, only a moment later.
        noticeDialog(t('consoleSetup.refused.title'), t('twofa.lockedOutToast')).then(() => resolve('cancel'));
        return;
      }
      else if (outcome === 'expired' || outcome === 'ended') toast(t('twofa.expired'), 6000);
      resolve(outcome === 'confirmed' || outcome === 'admin' ? outcome : 'cancel');
    }

    handlers.add(onEvent);
    openOverlay(menu);
  });
}

// api.onTwoFactor can only add listeners: one dispatcher serves every open question.
const handlers = new Set();
let wired = false;
function wire() {
  if (wired || !api.onTwoFactor) return;
  wired = true;
  api.onTwoFactor((event) => handlers.forEach((h) => h(event)));
}
wire();
