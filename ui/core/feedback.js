import { $ } from './dom.js';
import { t } from './i18n.js';

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
 */
export async function attempt(action, label, { quietLocked = false } = {}) {
  try {
    return await action();
  } catch (err) {
    if (quietLocked && err?.locked) return undefined;
    // A protected profile waits at the picker: the host answers only the picker; everything else is reloaded once it is chosen.
    if (err?.held) return undefined;
    console.error(err);
    toast(`${label ?? t('feedback.failed')}: ${err?.message || err}`, 5000);
    return undefined;
  }
}
