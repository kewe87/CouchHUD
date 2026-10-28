// Screenshots on the controller (Guide + Y, or the guide menu): pure rules, tested by scripts/ui-logic-check.mjs.

/** Only pictures CouchHUD serves itself from the PC: never an address from anywhere else. */
const LOCAL_PREFIX = 'https://local.couchhud/';

/**
 * What the toast shows for the host's "screenshot" event: { ok, text, url }. The picture only for a saved screenshot and only from
 * CouchHUD's own file host; a refusal or an error is text alone.
 */
export function shotToast(payload) {
  const ok = payload?.ok === true;
  const text = typeof payload?.text === 'string' ? payload.text : '';
  const url = ok && typeof payload?.url === 'string' && payload.url.startsWith(LOCAL_PREFIX) ? payload.url : null;
  return { ok, text, image: url, ms: ok ? 3500 : 5000 };
}

/** How often the page tells the host its screen state again, changed or not (see changeReporter's refresh). */
export const REPORT_REFRESH_MS = 5000;

/** A lost "a code is on screen" is sent once more after this long, not only with the next refresh. */
export const REPORT_RETRY_MS = 200;

/**
 * Reports a yes/no state only when it changes (the first report always goes out). `report(value)` gets the new state and may return
 * a promise: a state counts as delivered only once that settles fine. A lost report (rejected, thrown) is sent again with the next
 * call, even with the same value, so the host is never left with an old "nothing on screen" (audit 0.5 A3). `refresh()` sends the
 * latest state again regardless (the host forgets it when the page's renderer hung). A lost "yes" (a code is on screen) is sent once
 * more after `retryMs`, while it is still the latest state.
 */
export function changeReporter(report, { retryMs = REPORT_RETRY_MS } = {}) {
  let delivered = null;
  let wanted = null;
  let pending = false;
  let serial = 0;
  const send = (next, retry = false) => {
    wanted = next;
    pending = true;
    const id = ++serial;
    let result;
    try {
      result = report(next);
    } catch (err) {
      result = Promise.reject(err);
    }
    // Only the answer to the latest report counts: an older one settling late says nothing about the state now.
    Promise.resolve(result).then(
      () => {
        if (id !== serial) return;
        delivered = next;
        pending = false;
      },
      () => {
        if (id !== serial) return;
        delivered = null;
        pending = false;
        if (next && !retry) setTimeout(() => { if (id === serial && wanted === true) send(true, true); }, retryMs);
      }
    );
  };
  const reporter = (value) => {
    const next = value === true;
    if (next === wanted && (pending || next === delivered)) return;
    send(next);
  };
  reporter.refresh = () => {
    if (wanted !== null) send(wanted);
  };
  return reporter;
}

/** The phone's pairing code on screen: the QR codes (remote page, phone panel of the on-screen keyboard) and the code as text. */
export const PAIRING_SELECTOR = '.pair-qr svg, .osk-qr svg, .pair-code';

/**
 * Counts open code entries so the host knows when one is on screen (it refuses screenshots then). Each entry reports its own
 * end once, however it ends (entered, cancelled, removed); `report(open)` gets the new state on every change.
 */
export function codeEntryTracker(report) {
  let open = 0;
  return {
    get open() { return open; },
    /** A code entry appeared; returns its one-time "gone" function. */
    begin() {
      open++;
      report(true);
      let done = false;
      return () => {
        if (done) return;
        done = true;
        open = Math.max(0, open - 1);
        report(open > 0);
      };
    }
  };
}
