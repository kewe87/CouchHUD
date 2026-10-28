// Pure rules of the couch lock in the console setup, without DOM (tested by scripts/ui-logic-check.mjs). The host checks it again
// (Hud.SetCouchLockAsync refuses the code lock without a code the CouchHUD service keeps through the switch).

/**
 * The code lock is in place: the mode is "code" and a code is there. When the codes go through the CouchHUD service (codesInService)
 * its own answer counts: a code it does not hold (serviceCodes.couch false) opens nothing, whatever the settings file says. Without the
 * service (a development copy) the settings file's hash is the code.
 */
export function codeLockHeld(security) {
  if (security?.couchLock !== 'code' || !security.hasPasscode) return false;
  return !security.codesInService || security.serviceCodes?.couch !== false;
}

/**
 * Before the console setup changes anything: not on the host's stand-in of broken settings (configFault, from a fresh read of the host,
 * not only from what the page read at its start), and not without the hold that keeps the couch open through the run (its lock step would
 * otherwise lock the HUD half way). readSecurity() and hold() are the host calls; a refusal of either stops the run before its first step.
 * Resolves to { ok: true, security } or { ok: false, reason: 'configFault' | 'locked' | 'failed', error }.
 */
export async function beginSetupHold({ readSecurity, hold }) {
  let security;
  try {
    security = await readSecurity();
  } catch (error) {
    return { ok: false, reason: error?.locked ? 'locked' : 'failed', error };
  }
  if (security?.configFault) return { ok: false, reason: 'configFault', security };
  try {
    await hold();
  } catch (error) {
    return { ok: false, reason: error?.locked ? 'locked' : 'failed', error, security };
  }
  return { ok: true, security };
}

/**
 * Whether the host runs on the stand-in of broken settings right now (the page reads systemInfo only once at its start: the settings may
 * have broken or been repaired since). The host's fresh answer counts; a read that fails or does not say keeps what was known.
 */
export async function configFaultNow(known, readSecurity) {
  try {
    const security = await readSecurity();
    if (typeof security?.configFault === 'boolean') return security.configFault;
  } catch {
    // Locked or not answered: what was known counts.
  }
  return !!known;
}

/**
 * The console setup's lock step: done with the Windows sign-in as the lock or with a code lock that has its code. Not on the host's
 * stand-in of broken settings (configFault): its "windows" is the fail-closed lock nobody chose, not a lock that was set up.
 */
export function lockStepStatus(security) {
  if (security?.configFault) return 'todo';
  if (security?.couchLock === 'windows' || codeLockHeld(security)) return 'done';
  return 'todo';
}

/**
 * A protected setting (rating system, areas, idle times with guarded profiles …) that the host takes only from an unlocked couch and,
 * with parental controls on, with the parents' code for exactly this change: first the unlock (the couch code or the Windows sign-in),
 * then the parents' code, so the save behind it is not refused with "unlock first" after the parents already said yes (finding B1 of
 * the 0.5 VM test). The dialogs are passed in: ensureUnlocked(reason) and ensureParentGrant(reason, scope), both resolving to true or
 * false. parentalOn false: the unlock alone.
 */
export async function admitProtectedChange({ reason, parentReason = reason, parentalOn, scope, ensureUnlocked, ensureParentGrant }) {
  if (!(await ensureUnlocked(reason))) return false;
  if (!parentalOn) return true;
  return !!(await ensureParentGrant(parentReason, scope));
}

/**
 * The console setup's lock step. A lock that is on stays as it is (the Windows sign-in, the code lock with its code). Everything else
 * takes the code in the same step as the switch (enroll: Settings › Console › Couch lock with "code" picked): a code set at the open
 * couch does not survive the lock being switched on in the CouchHUD service, so "keep the code" is not offered there; the person
 * enters the code (the old one again, or a new one) and it goes along. The code lock without a code (only an administrator could
 * have opened the couch) takes a new code for the lock that is on (restore).
 *
 * enroll() and restore() resolve to 'on', 'cancelled' or 'failed'; askAgain() to true when a cancelled entry is to be tried again.
 * Resolves to { status: 'ok' | 'skipped' | 'failed', note }, the note being a key below consoleSetup.lock.
 */
export async function setupCouchLock({ security, enroll, restore, askAgain }) {
  if (security?.couchLock === 'windows') return { status: 'ok', note: 'windowsNote' };
  if (codeLockHeld(security)) return { status: 'ok', note: security.accountPassword === false ? 'codeNoPassword' : 'existingActive' };
  const run = security?.couchLock === 'code' ? restore : enroll;
  for (;;) {
    const outcome = await run();
    if (outcome === 'on') return { status: 'ok', note: security?.accountPassword === false ? 'codeNoPassword' : 'codeActive' };
    // A lock of 0.4.0 not confirmed yet takes no single code: an administrator confirms it through the code entry first.
    if (outcome !== 'cancelled') return { status: 'failed', note: run === restore && security?.serviceCodes?.legacy ? 'legacyConfirmFirst' : 'notSaved' };
    if (!(await askAgain())) return { status: 'skipped', note: 'noCode' };
  }
}
