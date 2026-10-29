// Power menu rules without DOM or host calls (tested in scripts/ui-logic-check.mjs): which actions the mode offers, how the
// menu groups them and where the focus starts or comes back to.

/**
 * The action an id stands for in the current mode. Shell mode: the PC mode (back with the Guide button) and a restart into it; no
 * quit. App mode: "pc-mode" is closing CouchHUD, and a restart into the PC mode means nothing (Windows starts its desktop anyway).
 */
export function forMode(id, isShell) {
  if (isShell) return id === 'quit' ? null : id;
  if (id === 'pc-mode') return 'quit';
  return id === 'restart-pc' ? null : id;
}

/** The sleep timer sits in the menu like an action, but it is no power action (it only picks a time). */
export const SLEEP_TIMER = 'sleep-timer';

/**
 * The menu's two groups: what concerns CouchHUD itself on top, what the whole computer does below (behind a separator).
 * Ids before forMode; the heading keys are the i18n keys of the group titles.
 */
export const POWER_GROUPS = [
  { id: 'app', heading: 'power.group.app', ids: ['restart-app', 'pc-mode', SLEEP_TIMER] },
  { id: 'pc', heading: 'power.group.pc', ids: ['standby', 'restart', 'restart-pc', 'shutdown'] }
];

/** Entries that end something or lose unsaved work are tinted as dangerous; the CouchHUD entries and standby come back by themselves. */
export const isDangerous = (id) => id === 'restart' || id === 'restart-pc' || id === 'shutdown';

/** The groups for the mode: [{ id, heading, ids }] with forMode applied, empty groups dropped. */
export function powerGroups(isShell, { sleepTimer = true } = {}) {
  return POWER_GROUPS.map((g) => ({
    ...g,
    ids: [...new Set(g.ids.filter((id) => sleepTimer || id !== SLEEP_TIMER).map((id) => (id === SLEEP_TIMER ? id : forMode(id, isShell))).filter(Boolean))]
  })).filter((g) => g.ids.length);
}

/** Where the focus starts: on standby (reversible and the most common wish), else on the first entry. */
export const POWER_START = 'standby';

/** Index of an id in the flat entry list (a menu that comes back after a cancelled question); unknown ids start on POWER_START. */
export function powerIndex(ids, id) {
  const at = ids.indexOf(id);
  if (at >= 0) return at;
  return Math.max(0, ids.indexOf(POWER_START));
}
