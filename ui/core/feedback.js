import { $ } from './dom.js';
import { t } from './i18n.js';

// The shared busy state of buttons, rows and footer hints (a running action ignores a second press instead of a toast).
export { isBusy, markBusy, runBusy } from './busy.js';

let toastTimer;

/** `long`: a text of several lines; it sits low on the screen so it never covers the title of the page behind it. */
export function toast(text, ms = 2500, { long = false } = {}) {
  const t = $('#toast');
  t.textContent = text;
  t.classList.toggle('long', long);
  t.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => (t.hidden = true), ms);
}

/**
 * Runs a UI action and turns failures into a toast instead of a silent unhandled rejection.
 * `quietLocked`: for reads the page makes on its own (not because the user asked): a locked couch is no error then, the
 * result is undefined and the page shows "locked" or nothing.
 * `onCodesDamaged`: who offers the reset when the service's code store is damaged (the settings' own code changes pass it); it gets the
 * message and answers whether it offered the reset. Without one, or when it did not, the message is a toast.
 */
export async function attempt(action, label, { quietLocked = false, lockedValue = undefined, onCodesDamaged = null } = {}) {
  try {
    return await action();
  } catch (err) {
    if (quietLocked && err?.locked) return lockedValue;
    // A protected profile waits at the picker: the host answers only the picker; everything else is reloaded once it is chosen.
    if (err?.held) return undefined;
    // The service's code file is damaged: the message says so, with the reset where the caller offers it.
    if (err?.codesDamaged) {
      if (!(onCodesDamaged && (await onCodesDamaged(err.message)))) toast(err.message, 8000, { long: true });
      return undefined;
    }
    console.error(err);
    toast(`${label ?? t('feedback.failed')}: ${err?.message || err}`, 5000);
    return undefined;
  }
}
