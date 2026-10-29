// Pure rules of the "Continue playing" row on Home and of changing save backups, without DOM (tested by scripts/ui-logic-check.mjs).
// The host checks the save rules again (Hud.RequireSaveChange, LockPolicy).

/** Cards in the "Continue playing" part of the Home shelf, the running game included. */
export const CONTINUE_LIMIT = 6;

/**
 * The games of the "Continue playing" row: games played on this PC (or by the store's record), newest first, without the running
 * one (Home puts that first anyway). Hidden entries and apps stay out, like in the library. Games locked by the parental controls
 * are shown exactly as the library shows them: the gate at the start decides, the row invents no rule of its own.
 */
export function continuePlaying(library, { runningId = null, limit = CONTINUE_LIMIT, blocked = null, days = 0, now = Date.now() } = {}) {
  const room = Math.max(0, limit - (runningId ? 1 : 0));
  // The Favorites tab's time span ("Recently used: time span", days; 0 = unlimited) applies here too: older games do not qualify.
  const since = days > 0 ? now - days * 86_400_000 : -Infinity;
  return library
    .filter((i) => !i.hidden && i.type === 'game' && i.lastPlayed > 0 && i.lastPlayed >= since && i.id !== runningId && !blocked?.has(i.id))
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
 * The Favorites shelf (the home view) in order: the running game, the rest of "Continue playing", then the other recent games and favorites without
 * repeating one (`recent` is the host's arrangement of favorites and recent entries; `blocked` are entries the person's rules keep
 * out of sight, also here), then the pinned collections (an empty list is possible: the view shows its empty state). The first card of "Continue playing" is the big one (hero).
 * Returns { list, continueIds, heroId }.
 */
export function homeShelf({ running = null, library = [], recent = [], extras = [], limit = CONTINUE_LIMIT, blocked = null, days = 0, now = Date.now() }) {
  const cont = continuePlaying(library, { runningId: running?.id ?? null, limit, blocked, days, now });
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

/**
 * Which label the shelf shows above the focused tile: 'continue' inside "Continue playing", 'recent' for an entry that is no favorite
 * in the combined mode (it is shown for being used lately), else null (no label).
 */
export function shelfHeading({ inContinue = false, special = false, favorite = false, mode = 'combined', loaded = true } = {}) {
  if (inContinue) return 'continue';
  if (special || favorite || !loaded || mode !== 'combined') return null;
  return 'recent';
}

/**
 * What an empty Favorites tab says: the text key and the tab names (text keys) where a favorite is marked with Y, by the areas that
 * are on (websites only when they are allowed in the shelf). 'recent' mode says nothing was used within the time span.
 */
export function emptyHint({ mode = 'combined', areas = [], sites = false } = {}) {
  if (mode === 'recent') return { key: 'home.empty.recent', tabs: [] };
  const tabs = [];
  if (areas.includes('games')) tabs.push('app.games.title');
  if (areas.includes('apps')) tabs.push('app.apps.title');
  if (sites && areas.includes('browser')) tabs.push('browser.title');
  return { key: tabs.length > 1 ? 'home.empty.hintMany' : tabs.length ? 'home.empty.hint' : 'home.empty.hintPlain', tabs };
}

/** The time span that applies to "Continue playing": none in the favorites-only mode (its row is hidden there, like in the host), else the setting. */
export const continueDays = (mode, days) => (mode === 'favorites' ? 0 : days > 0 ? days : 0);
