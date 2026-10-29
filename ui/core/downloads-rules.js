// The structure of the Downloads tab, pure (tested by scripts/ui-logic-check.mjs). The list holds only what loads, installs or waits
// to be installed; the page's own actions (search for updates, games that are not installed) are footer shortcuts, never rows.

export const isFinished = (entry) => entry?.state === 'done' || entry?.state === 'failed';

/**
 * The rows of the list, in order: running lines, the notes that start something about updates (a restart that is due), the updates
 * that can be started, then the finished lines. Nothing else: no search row, no row for the games that are not installed.
 */
export function downloadRows({ entries = [], first = [], pending = [] } = {}) {
  return [
    ...entries.filter((e) => !isFinished(e)).map((entry) => ({ kind: 'entry', entry })),
    ...first,
    ...pending,
    ...entries.filter(isFinished).map((entry) => ({ kind: 'entry', entry }))
  ];
}

/**
 * The page's footer actions, the way plugin pages have theirs: X searches for updates (X searches or reloads on every core page), Y
 * opens the games that are not installed (only with the Gaming area on). `searching`: the search runs, the X hint is busy.
 */
export function downloadShortcuts({ canSearch = false, searching = false, gamesOn = false } = {}) {
  return [
    ...(canSearch ? [{ button: 'X', id: 'search', busy: !!searching }] : []),
    ...(gamesOn ? [{ button: 'Y', id: 'uninstalled', busy: false }] : [])
  ];
}

/** Finished lines: with more than one, a finished line's menu also offers to clear them all (it was X before X became the search). */
export function finishedMenu(actions, entries = []) {
  return entries.filter(isFinished).length > 1 && actions.includes('remove') ? [...actions, 'clearFinished'] : actions;
}

/**
 * The A press on an update row and the X press of the search, with what they touch passed in (tested in Node with fakes). A busy row
 * or search only bumps. The lock is asked before anything looks busy, so a locked couch shows the unlock toast without a spinner
 * flash. Then the start runs under its busy key (a second press meanwhile is ignored there); a row without a key (the restart) runs
 * directly, and the restart (open) needs no unlock, like power.
 * deps: { busy, locked, run(key, fn), unlocked(), bump(), select() }
 */
export async function pressPending(row, { busy, locked, run, unlocked, bump, select }) {
  if (busy) return bump();
  select();
  if (!row.open && (await locked())) return unlocked();
  return row.busyKey ? run(row.busyKey, row.run) : row.run();
}

/** X: the same order for the search (key: the page's search key). */
export async function pressSearch(key, search, { busy, locked, run, unlocked, bump, select }) {
  if (busy) return bump();
  select();
  if (await locked()) return unlocked();
  return run(key, search);
}

/**
 * The footer hints: A stays while its row is busy and only greys out (like Bluetooth and Wi-Fi), then the page's shortcuts with their
 * busy state, then B. labels: { a, search, uninstalled, back }.
 */
export function downloadHints({ hasRow = false, rowBusy = false, shortcuts = [], labels }) {
  return [
    ...(hasRow ? [['A', labels.a, { busy: !!rowBusy }]] : []),
    ...shortcuts.map((s) => [s.button, labels[s.id], { busy: !!s.busy }]),
    ['B', labels.back]
  ];
}
