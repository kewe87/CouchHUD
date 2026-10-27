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

/** The console setup's lock step: done with the Windows sign-in as the lock or with a code lock that has its code. */
export function lockStepStatus(security) {
  if (security?.couchLock === 'windows' || codeLockHeld(security)) return 'done';
  return 'todo';
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
