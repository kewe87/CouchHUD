// Pure rules of the "Continue playing" row on Home and of changing save backups, without DOM (tested by scripts/ui-logic-check.mjs).
// The host checks the save rules again (Hud.RequireSaveChange, LockPolicy).

/** Cards in the "Continue playing" part of the Home shelf, the running game included. */
export const CONTINUE_LIMIT = 6;

/**
 * The games of the "Continue playing" row: games played on this PC (or by the store's record), newest first, without the running
 * one (Home puts that first anyway). Hidden entries and apps stay out, like in the library. Games locked by the parental controls
 * are shown exactly as the library shows them: the gate at the start decides, the row invents no rule of its own.
 */
export function continuePlaying(library, { runningId = null, limit = CONTINUE_LIMIT } = {}) {
  const room = Math.max(0, limit - (runningId ? 1 : 0));
  return library
    .filter((i) => !i.hidden && i.type === 'game' && i.lastPlayed > 0 && i.id !== runningId)
    .sort((a, b) => b.lastPlayed - a.lastPlayed)
    .slice(0, room);
}

/**
 * What a card of the row tells: when it was played last, the total play time, and the last session when it says something
 * the total doesn't (null for what is unknown). Progress (achievements) is left out: it is not reliably available on this PC.
 */
export function continueFacts(item) {
  const total = item.playtimeMinutes > 0 ? item.playtimeMinutes : null;
  const last = item.lastSessionMinutes > 0 && (!total || total - item.lastSessionMinutes >= 1) ? item.lastSessionMinutes : null;
  return { lastPlayed: item.lastPlayed > 0 ? item.lastPlayed : 0, total, last };
}

/**
 * The Home shelf in order: the running game, the rest of "Continue playing", then the other recent games and favorites without
 * repeating one, then the extra tiles (collections, "All games"). The first card of "Continue playing" is the big one (hero).
 * Returns { list, continueIds, heroId }.
 */
export function homeShelf({ running = null, library = [], recent = [], extras = [], limit = CONTINUE_LIMIT }) {
  const cont = continuePlaying(library, { runningId: running?.id ?? null, limit });
  const first = [...(running ? [running] : []), ...cont];
  const continueIds = new Set(first.map((i) => i.id));
  const rest = recent.filter((i) => !continueIds.has(i.id));
  return { list: [...first, ...rest, ...extras], continueIds, heroId: first[0]?.id ?? null };
}

/**
 * Restoring or deleting a save backup, after the question was answered: first the unlocked couch, then, with parental controls on,
 * the parents' code for exactly this game (action "restoreSaves" or "deleteSaveBackup", item = the game's id), like installing a
 * game. The dialogs are passed in: ensureUnlocked(reason) and ensureParentGrant(reason, scope), both resolving to true or false.
 */
export async function admitSaveChange({ action, id, parentalOn, reason, parentReason, ensureUnlocked, ensureParentGrant }) {
  if (!id || !['restoreSaves', 'deleteSaveBackup'].includes(action)) return false;
  if (!(await ensureUnlocked(reason))) return false;
  if (!parentalOn) return true;
  return !!(await ensureParentGrant(parentReason, { action, item: id }));
}
