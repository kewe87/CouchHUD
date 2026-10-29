// One busy state for everything a press starts and that takes a while (a search, a connect, an install request). While it runs, the
// button, row or footer hint that started it looks busy (greyed, a small spinner, aria-busy) and a second press does nothing: no
// "already running" toast. The host keeps its own guards; this only changes what the screen says. No DOM at import time, so
// scripts/ui-logic-check.mjs tests it in Node with small fake nodes.

/**
 * A registry of running actions by key. `run(key, fn)` runs fn once per key at a time: a press while the key runs is ignored (onIgnored
 * is called, e.g. for a soft bump) and returns { ignored: true }; otherwise { value } with what fn returned. The key is cleared on
 * success and on error (the error is passed on). onChange(key, busy) fires on start and on end, e.g. to redraw the page and the hints;
 * a throwing onChange or listener is logged and never leaves the key stuck. `target` names what runs (a row, an SSID) and stays readable
 * through target(key) while the key runs, so a page opened again greys the right row.
 */
export function createBusy() {
  const running = new Map();
  const listeners = new Set();
  const safe = (fn, key, on) => {
    try { fn?.(key, on); } catch (err) { console.error(err); }
  };
  const notify = (key, on, onChange) => {
    safe(onChange, key, on);
    for (const listener of listeners) safe(listener, key, on);
  };
  return {
    isBusy: (key) => running.has(key),
    /** Any key that starts with the prefix ('update:' for every update row). */
    anyBusy: (prefix) => [...running.keys()].some((k) => k.startsWith(prefix)),
    /** What the running key belongs to (run's target option), or null when the key does not run. */
    target: (key) => (running.has(key) ? running.get(key) : null),
    onChange(listener) {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    async run(key, fn, { onChange, onIgnored, target = null } = {}) {
      if (running.has(key)) {
        safe(onIgnored);
        return { ignored: true };
      }
      running.set(key, target);
      try {
        notify(key, true, onChange);
        return { value: await fn() };
      } finally {
        running.delete(key);
        notify(key, false, onChange);
      }
    }
  };
}

/** The app's one registry. */
export const busy = createBusy();
export const isBusy = (key) => busy.isBusy(key);
export const busyTarget = (key) => busy.target(key);
export const runBusy = (key, fn, options) => busy.run(key, fn, options);

/** Reduced motion: the system setting or CouchHUD's own switch (motion.js writes data-motion="off" for both). */
export function prefersReducedMotion(doc = globalThis.document, win = globalThis) {
  if (doc?.documentElement?.dataset?.motion === 'off') return true;
  return !!win?.matchMedia?.('(prefers-reduced-motion: reduce)')?.matches;
}

/**
 * Marks a node (button, row, chip) busy or not: class is-busy (greyed via the skin's tokens), aria-busy and aria-disabled, and one
 * small spinner at its end. With reduced motion the spinner stands still (class static). Safe to call again with the same state.
 */
export function markBusy(node, on, { reducedMotion = prefersReducedMotion(), doc = globalThis.document } = {}) {
  if (!node) return node;
  node.classList.toggle('is-busy', !!on);
  if (on) {
    node.setAttribute('aria-busy', 'true');
    node.setAttribute('aria-disabled', 'true');
  } else {
    node.removeAttribute('aria-busy');
    node.removeAttribute('aria-disabled');
  }
  let spinner = node.querySelector?.(':scope > .busy-spinner') ?? null;
  if (on && !spinner && doc) {
    spinner = doc.createElement('span');
    spinner.className = 'spinner busy-spinner';
    spinner.setAttribute('aria-hidden', 'true');
    node.append(spinner);
  }
  if (!on && spinner) spinner.remove();
  else if (spinner) spinner.classList.toggle('static', !!reducedMotion);
  return node;
}

/** A footer hint that can be busy: [button, label, { busy }]; nav.renderHints draws it greyed with a spinner. */
export const busyHint = (button, label, key) => [button, label, { busy: isBusy(key) }];
