// Values a plugin page sends for a stepper row (brightness, volume, ...): left/right only changes what the page shows;
// the value goes out once the input has settled (settleMs without a step) or at once on A. This keeps the intermediate
// steps off the wire.

export const SETTLE_MS = 700;

export function settler(ms = SETTLE_MS) {
  const pending = new Map();

  async function flush(key) {
    const entry = pending.get(key);
    if (!entry) return false;
    clearTimeout(entry.timer);
    if (entry.sending) return true;
    entry.sending = true;
    try {
      await entry.send(entry.value);
    } finally {
      // A step made while this was on its way stays pending.
      if (pending.get(key) === entry) pending.delete(key);
    }
    return true;
  }

  return {
    has: (key) => pending.has(key),
    /** Still waiting for its quiet moment (not yet on its way): a value that is being sent no longer counts. */
    waiting: (key) => pending.has(key) && !pending.get(key).sending,
    /** The value the page shows: the pending one, otherwise what the plugin reported. */
    value: (key, reported) => (pending.has(key) ? pending.get(key).value : reported),
    /** A new value from a step; `send(value)` runs once nothing has changed for `ms`. */
    set(key, value, send) {
      const before = pending.get(key);
      if (before) clearTimeout(before.timer);
      const entry = { value, send, timer: 0 };
      entry.timer = setTimeout(() => flush(key), ms);
      pending.set(key, entry);
    },
    flush,
    /** Drops everything still pending without sending it (the page it belongs to is gone). */
    cancelAll() {
      for (const entry of pending.values()) clearTimeout(entry.timer);
      pending.clear();
    },
    /** Sends everything still pending (leaving a page). */
    flushAll: () => Promise.all([...pending.keys()].map(flush))
  };
}
