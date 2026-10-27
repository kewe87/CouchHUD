// Pure UI rules with a test: node scripts/ui-logic-check.mjs (run by check.sh).
import assert from 'node:assert/strict';
import { clampPaste, remoteView } from '../ui/core/paste.js';
import { CODE_KEYS, holdsDialogs, isCodeEntry, padHelp, switchOffWarning } from '../ui/core/input-help.js';
import { codeLockHeld, lockStepStatus, setupCouchLock } from '../ui/core/lock-rules.js';

// A pasted text is cut to the field's limit.
assert.equal(clampPaste('abcdef', 4), 'abcd');
assert.equal(clampPaste('abcdef', 0), 'abcdef');
assert.equal(clampPaste(null, 4), '');

// The remote sees nothing of a secret field, nor of a pasted one until it is edited by hand.
assert.deepEqual(remoteView({ secret: true, text: 'token' }), { secret: true, text: '' });
assert.deepEqual(remoteView({ secret: false, pasted: true, text: 'token' }), { secret: true, text: '' });
assert.deepEqual(remoteView({ secret: false, pasted: false, text: 'https://x' }), { secret: false, text: 'https://x' });
assert.deepEqual(remoteView(null), { secret: false, text: '' });

// The on-screen code buttons are exactly the controller's code buttons: nothing that cancels, nothing extra.
assert.deepEqual([...CODE_KEYS].sort(), ['a', 'b', 'down', 'lb', 'left', 'rb', 'right', 'up', 'x', 'y']);

// Code prompt without a controller: say that keyboard and mouse work, and where Bluetooth stands; unknown says nothing.
assert.deepEqual(padHelp(1, null), { noPad: false, bluetooth: null });
assert.deepEqual(padHelp(null, { available: true, on: false }), { noPad: false, bluetooth: null });
assert.deepEqual(padHelp(0, null), { noPad: true, bluetooth: null });
assert.deepEqual(padHelp(0, { available: false }), { noPad: true, bluetooth: null });
assert.deepEqual(padHelp(0, { available: true, on: false, autoOn: true, controllersPaired: 1 }), { noPad: true, bluetooth: 'passcode.bt.offAuto' });
assert.deepEqual(padHelp(0, { available: true, on: false, autoOn: false, controllersPaired: 1 }), { noPad: true, bluetooth: 'passcode.bt.off' });
assert.deepEqual(padHelp(0, { available: true, on: false, autoOn: true, controllersPaired: 0 }), { noPad: true, bluetooth: 'passcode.bt.off' });
assert.deepEqual(padHelp(0, { available: true, on: true, controllersPaired: 1 }), { noPad: true, bluetooth: 'passcode.bt.onPaired' });
assert.deepEqual(padHelp(0, { available: true, on: true, controllersPaired: 0 }), { noPad: true, bluetooth: null });

// Switching Bluetooth off with a Bluetooth controller connected asks first; switching on never does.
assert.equal(switchOffWarning({ controllerConnected: true, autoOn: true }, false), 'bluetooth.offWarnAuto');
assert.equal(switchOffWarning({ controllerConnected: true, autoOn: false }, false), 'bluetooth.offWarn');
// Code prompt: no wheel, no side buttons, no key repeat; no dialog over it or over the time-up page.
assert.equal(isCodeEntry({ id: 'passcode', codeEntry: true }), true);
assert.equal(isCodeEntry({ id: 'settings' }), false);
assert.equal(isCodeEntry(undefined), false);
assert.equal(holdsDialogs({ id: 'passcode', codeEntry: true }), true);
assert.equal(holdsDialogs({ id: 'parental-timeup' }), true);
assert.equal(holdsDialogs({ id: 'home' }), false);
assert.equal(holdsDialogs(null), false);
assert.equal(switchOffWarning({ controllerConnected: false, autoOn: true }, false), null);
assert.equal(switchOffWarning({ controllerConnected: true, autoOn: true }, true), null);
assert.equal(switchOffWarning(null, false), null);

// Console setup, lock step: the code goes along with the switch in one step; a code from the open couch is not "kept" (the CouchHUD
// service drops it when a lock is switched on, finding of the 0.4.2 acceptance test). The fake host below behaves like Hud.SetCouchLockAsync.
{
  const host = (security) => {
    const calls = [];
    const setCouchLock = async (mode, code = null) => {
      calls.push(['setCouchLock', mode, code ? 'code' : null]);
      if (mode === 'code' && !code && security.couchLock !== 'code') throw new Error('hud.setCodeFirst');
      security.couchLock = mode;
      if (code) Object.assign(security, { hasPasscode: true, serviceCodes: { couch: true } });
      return security;
    };
    // The settings page's flows: enroll = chooseLock('code') with a code entered, restore = a new code for the lock that is on.
    const flow = (entered, run) => async () => {
      calls.push(['flow']);
      if (!entered) return 'cancelled';
      try {
        await run();
      } catch {
        return 'failed';
      }
      return codeLockHeld(security) ? 'on' : 'failed';
    };
    return { calls, setCouchLock, security, enroll: (entered = true) => flow(entered, () => setCouchLock('code', ['a', 'b'])) };
  };
  const never = async () => assert.fail('not asked');

  // New code from no lock: one call with the code, the lock is on with its code.
  {
    const h = host({ couchLock: 'off', hasPasscode: false, serviceCodes: { couch: false }, codesInService: true });
    assert.equal(lockStepStatus(h.security), 'todo');
    const result = await setupCouchLock({ security: h.security, enroll: h.enroll(), restore: never, askAgain: never });
    assert.deepEqual(result, { status: 'ok', note: 'codeActive' });
    assert.deepEqual(h.calls.filter((c) => c[0] === 'setCouchLock'), [['setCouchLock', 'code', 'code']]);
    assert.equal(lockStepStatus(h.security), 'done');
  }
  // "Keep" from no lock: a code set at the open couch is not kept; the code is entered and goes along, never setCouchLock('code') alone.
  {
    const h = host({ couchLock: 'off', hasPasscode: true, serviceCodes: { couch: true }, codesInService: true });
    assert.equal(lockStepStatus(h.security), 'todo');
    const result = await setupCouchLock({ security: h.security, enroll: h.enroll(), restore: never, askAgain: never });
    assert.equal(result.status, 'ok');
    assert.ok(h.calls.every((c) => c[0] !== 'setCouchLock' || c[2] === 'code'));
    // The old way (code first, then the mode alone) is refused by the host instead of arming a lock without a code.
    const old = host({ couchLock: 'off', hasPasscode: true, serviceCodes: { couch: true } });
    await assert.rejects(old.setCouchLock('code'), /setCodeFirst/);
    assert.equal(old.security.couchLock, 'off');
  }
  // From the Windows sign-in: the lock that is on stays, nothing is called.
  {
    const h = host({ couchLock: 'windows', hasPasscode: false, serviceCodes: { couch: false } });
    assert.equal(lockStepStatus(h.security), 'done');
    assert.deepEqual(await setupCouchLock({ security: h.security, enroll: never, restore: never, askAgain: never }), { status: 'ok', note: 'windowsNote' });
    assert.deepEqual(h.calls, []);
  }
  // The code lock with its code: kept. Without a code the service holds: not done, a new code for the lock that is on.
  {
    const held = { couchLock: 'code', hasPasscode: true, serviceCodes: { couch: true }, accountPassword: false };
    assert.equal(lockStepStatus(held), 'done');
    assert.deepEqual(await setupCouchLock({ security: held, enroll: never, restore: never, askAgain: never }), { status: 'ok', note: 'codeNoPassword' });
    const lost = { couchLock: 'code', hasPasscode: true, serviceCodes: { couch: false }, codesInService: true };
    assert.equal(lockStepStatus(lost), 'todo');
    let restored = 0;
    const result = await setupCouchLock({ security: lost, enroll: never, restore: async () => (restored++, 'on'), askAgain: never });
    assert.equal(result.status, 'ok');
    assert.equal(restored, 1);
    // Without the service (a development copy) the settings file's hash is the code; an old answer of a service does not count.
    assert.equal(lockStepStatus({ couchLock: 'code', hasPasscode: true, serviceCodes: { couch: false }, codesInService: false }), 'done');
    assert.equal(codeLockHeld({ couchLock: 'code', hasPasscode: false, codesInService: false }), false);
    // A lock of 0.4.0 not confirmed: the failure names the administrator's confirmation through the code entry.
    const legacy = { ...lost, serviceCodes: { couch: false, legacy: true } };
    assert.deepEqual(await setupCouchLock({ security: legacy, enroll: never, restore: async () => 'failed', askAgain: never }), { status: 'failed', note: 'legacyConfirmFirst' });
    assert.deepEqual(await setupCouchLock({ security: lost, enroll: never, restore: async () => 'failed', askAgain: never }), { status: 'failed', note: 'notSaved' });
  }
  // A refusal of the host is "failed", never "ok"; a cancelled entry asks again, then "skipped".
  {
    const h = host({ couchLock: 'off', hasPasscode: false });
    const result = await setupCouchLock({ security: h.security, enroll: async () => 'failed', restore: never, askAgain: never });
    assert.deepEqual(result, { status: 'failed', note: 'notSaved' });
    let asked = 0;
    const tries = [h.enroll(false), h.enroll(true)];
    const again = await setupCouchLock({ security: h.security, enroll: () => tries.shift()(), restore: never, askAgain: async () => (asked++, true) });
    assert.equal(again.status, 'ok');
    assert.equal(asked, 1);
    const off = { couchLock: 'off' };
    assert.deepEqual(await setupCouchLock({ security: off, enroll: async () => 'cancelled', restore: never, askAgain: async () => false }), { status: 'skipped', note: 'noCode' });
  }
}

console.log('ui logic: ok');
