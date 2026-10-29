// What a manual search for updates found out, in words: pure, no DOM (tested by scripts/ui-logic-check.mjs). The host answers a search
// that Privacy forbids (Offline, a switch off) with a plain text in the state, a failed search with an error text or code.

// i18n-used: updates.allCurrent updates.alreadyRunning updates.searchFailed updates.searching updates.found updates.lastChecked updates.notChecked updates.self.err.noRelease updates.self.err.rateLimit updates.self.err.noAnswer updates.self.err.unreachable
const SELF_CODES = ['noRelease', 'rateLimit', 'noAnswer', 'unreachable'];

/** The reasons a search did not get an answer: CouchHUD's own check, then Windows Update. Each text once, empty ones dropped; an unknown code never shows as a key. */
export function searchProblems(updates, selfUpdate, tr) {
  const list = [];
  const code = selfUpdate?.errorCode;
  const selfText = code ? (SELF_CODES.includes(code) ? tr(`updates.self.err.${code}`) : (selfUpdate?.error || tr('updates.searchFailed'))) : selfUpdate?.error;
  for (const text of [selfText, updates?.windowsError]) {
    if (typeof text === 'string' && text.trim() && !list.includes(text)) list.push(text);
  }
  return list;
}

/** Both calls of a search came back with an answer (a call that failed gives none; a missing CouchHUD check does not count against it). */
export const searchSucceeded = (updatesResult, selfResult, hasSelfCheck = true) => updatesResult != null && (!hasSelfCheck || selfResult != null);

/**
 * What to tell after a search. Nothing when a call failed (its own error notice stands). The problems when there are any. "Already
 * running" when the host turned this search away because one runs. "All up to date" only for a finished search with nothing pending.
 */
export function searchMessage(updates, selfUpdate, pendingCount, tr, succeeded = true) {
  if (!succeeded) return null;
  const problems = searchProblems(updates, selfUpdate, tr);
  if (problems.length) return problems.join(' · ');
  if (updates?.busy || selfUpdate?.busy) return tr('updates.alreadyRunning');
  return pendingCount > 0 ? null : tr('updates.allCurrent');
}

/** What a finished search tells, for the toast and the status line: searchMessage, or how many updates it found. */
export function searchOutcome(updates, selfUpdate, pendingCount, tr, succeeded = true) {
  const message = searchMessage(updates, selfUpdate, pendingCount, tr, succeeded);
  if (message || !succeeded) return message;
  return tr('updates.found', { count: pendingCount });
}

const text = (s) => (typeof s === 'string' && s.trim() ? s : null);

/**
 * The status line under the Downloads heading (it was the search row's detail). While this page's search runs: "Searching …", busy.
 * While the host is busy (a background search, an install), its own words, busy: the host would turn a search away now. After a
 * search of this visit: its outcome and when. Otherwise the reason the last search got no answer, or when it last searched.
 */
export function searchStatus({ searching = false, updates = null, selfUpdate = null, outcome = null, ago = () => '' } = {}, tr) {
  if (searching) return { text: tr('updates.searching'), busy: true };
  const hostBusy = text(updates?.busy) ?? text(selfUpdate?.busy) ?? (updates?.busy || selfUpdate?.busy ? tr('updates.searching') : null);
  if (hostBusy) return { text: hostBusy, busy: true };
  const when = updates?.checkedAt ? tr('updates.lastChecked', { ago: ago(updates.checkedAt) }) : tr('updates.notChecked');
  if (outcome) return { text: `${outcome} · ${when}`, busy: false };
  const problem = searchProblems(updates, selfUpdate, tr)[0];
  return { text: problem ?? when, busy: false, problem: !!problem };
}
