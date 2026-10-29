// Controller passcode, like the Xbox passkey: six buttons in a row instead of a PIN pad.

import { el } from '../core/dom.js';
import { api } from '../core/api.js';
import { attempt, toast } from '../core/feedback.js';
import { glyph, onGlyphsChanged } from '../core/skins.js';
import { render, removePage } from '../core/nav.js';
import { state } from '../core/state.js';
import { sound } from '../sound.js';
import { t } from '../core/i18n.js';
import { confirmDialog } from './overlay.js';
import { CODE_KEYS, padHelp, shouldShowCodeKeys, withTimeout } from '../core/input-help.js';
import { REPORT_REFRESH_MS, changeReporter, codeEntryTracker } from '../core/screenshot-rules.js';

export const PASSCODE_LENGTH = 6;
const CODE_BUTTONS = new Set(CODE_KEYS);
const LABEL = { up: '↑', down: '↓', left: '←', right: '→' };

// The open code prompt, so a controller that comes or goes (and Bluetooth switching back on) updates its help line.
let openPrompt = null;
const refreshOpenPrompt = () => openPrompt?.refreshHelp();
api.onPad?.(refreshOpenPrompt);
api.onBluetoothRecovery?.(refreshOpenPrompt);
onGlyphsChanged(() => openPrompt?.renderGlyphText());

// The host refuses screenshots while a code entry is on screen: it hears of every one that opens and ends.
// A report that did not arrive is sent again (changeReporter): the host refuses screenshots until it knows (audit 0.5 A3).
const codeReport = changeReporter((open) => api.codeEntryState?.(open));
const codeEntries = codeEntryTracker(codeReport);

/** Tells the host whether a code entry is open (at start: none), and now and then again, changed or not (see changeReporter). */
export function reportCodeEntries() {
  codeReport(codeEntries.open > 0);
  setInterval(() => {
    codeReport(codeEntries.open > 0);
    codeReport.refresh();
  }, REPORT_REFRESH_MS);
}

/** Asks for a passcode; resolves with the button list, or undefined when cancelled (View or Menu). */
export function enterPasscode({ title = t('passcode.enter'), hint = '', error = '' } = {}) {
  return new Promise((resolve) => {
    let settled = false;
    const gone = codeEntries.begin();
    const finish = (result) => {
      if (settled) return;
      settled = true;
      gone();
      resolve(result);
    };

    const page = {
      id: 'passcode',
      name: title,
      code: [],
      // Every code button belongs to the code here, B and the shoulders too.
      captures: (action) => action !== 'guide',
      // No mouse wheel, no side buttons, no held-arrow repeat here (see isCodeEntry).
      codeEntry: true,
      onClose: () => finish(undefined),
      help: { noPad: false, bluetooth: null },
      // The on-screen buttons are only the fallback for mouse and touch: shown while no controller is connected (or the pad list cannot be
      // read), hidden while one is. The keyboard and the controller work either way, nothing here changes what is accepted.
      showKeys: false,
      build(root) {
        this.dots = Array.from({ length: PASSCODE_LENGTH }, () => el('span', { className: 'passcode-dot' }));
        this.errorEl = el('p', { className: 'passcode-error', textContent: error });
        // Without a controller (Bluetooth off, battery empty) the code still has to be possible: the same buttons by mouse or touch,
        // and the keyboard keys spelled out. Same code, same check in the CouchHUD service, same lockout after wrong tries.
        this.keys = el('div', { className: 'passcode-keys', role: 'group', ariaLabel: t('passcode.onScreen') },
          // data-glyph: the key follows the controller in use (✕ ○ □ △ with a PlayStation pad), see refreshGlyphs.
          CODE_KEYS.map((code) => el('span', { className: `passcode-key key-${code}`, dataset: LABEL[code] ? { code } : { code, glyph: code.toUpperCase() }, textContent: LABEL[code] ?? glyph(code.toUpperCase()) })),
          el('span', { className: 'passcode-key passcode-cancel', dataset: { code: 'cancel' }, textContent: t('passcode.hintCancel') }));
        this.helpEl = el('div', { className: 'passcode-nopad', hidden: true });
        root.append(el('div', { className: 'passcode' },
          el('div', { className: 'passcode-lock', textContent: '🔒' }),
          el('h2', { textContent: title }),
          hint ? el('p', { className: 'meta', textContent: hint }) : null,
          el('div', { className: 'passcode-dots' }, this.dots),
          this.errorEl,
          this.buttonsEl = el('p', { className: 'passcode-help' }),
          this.helpEl,
          this.keys,
          this.legendEl = el('p', { className: 'passcode-legend' })));
        this.renderGlyphText();
        this.renderHelp();
        this.update();
      },
      /** The texts that name buttons, in the symbols of the controller in use. */
      renderGlyphText() {
        if (!this.buttonsEl) return;
        const [a, b, x, y, lb, rb] = ['A', 'B', 'X', 'Y', 'LB', 'RB'].map(glyph);
        this.buttonsEl.textContent = t('passcode.help', { count: PASSCODE_LENGTH, a, b, x, y, lb, rb });
        this.legendEl.textContent = t('passcode.keyboard', { a, b, x, y, lb, rb });
      },
      renderHelp() {
        if (this.keys) this.keys.hidden = !this.showKeys;
        if (this.legendEl) this.legendEl.hidden = !this.showKeys;
        if (!this.helpEl) return;
        const { noPad, bluetooth } = this.help;
        this.helpEl.hidden = !noPad;
        // (replaceChildren would show a null as the text "null".)
        this.helpEl.replaceChildren(...(noPad ? [
          el('strong', { textContent: t('passcode.noPad') }),
          bluetooth ? el('span', { textContent: t(bluetooth) }) : null
        ].filter(Boolean) : []));
      },
      /** Asks quietly (no error toasts over the code prompt): without an answer nothing is said. */
      async refreshHelp() {
        const quiet = (call) => (call ? call().catch(() => null) : Promise.resolve(null));
        // A controller list that does not answer within 2 seconds (or fails) counts as "none": the buttons must be there when they are needed.
        const [pads, bt] = await Promise.all([withTimeout(quiet(api.getPads), 2000, null), withTimeout(quiet(api.getBluetooth), 2000, null)]);
        if (settled) return;
        const count = Array.isArray(pads) ? pads.length : null;
        this.showKeys = shouldShowCodeKeys(count);
        this.help = padHelp(count, bt);
        this.renderHelp();
      },
      // Mouse and touch: a click on a key is that button, the same way the controller and keyboard enter it.
      pointer(node, index, dispatch) {
        if (!this.showKeys) return;
        const code = node.dataset?.code;
        if (code === 'cancel') return dispatch('start');
        if (CODE_BUTTONS.has(code)) dispatch(code);
      },
      update() {
        this.dots.forEach((d, i) => {
          d.classList.toggle('filled', i < this.code.length);
          d.textContent = '';
        });
      },
      close(result) {
        if (openPrompt === this) openPrompt = null;
        removePage(this);
        render();
        finish(result);
      },
      handle(action) {
        if (action === 'back' || action === 'start') {
          sound.back();
          return this.close(undefined);
        }
        if (!CODE_BUTTONS.has(action)) return;
        this.code.push(action);
        this.errorEl.textContent = '';
        sound.move();
        this.update();
        // A short glimpse of the last button, then only the dot.
        const dot = this.dots[this.code.length - 1];
        dot.textContent = LABEL[action] ?? glyph(action.toUpperCase());
        setTimeout(() => (dot.textContent = ''), 350);
        if (this.code.length === PASSCODE_LENGTH) {
          const code = this.code;
          setTimeout(() => this.close(code), 200);
        }
      },
      hints() {
        return [['≡', t('passcode.hintCancel')]];
      }
    };
    const onClose = page.onClose;
    page.onClose = () => {
      if (openPrompt === page) openPrompt = null;
      onClose();
    };
    openPrompt = page;
    state.stack.push(page);
    sound.open();
    render();
    page.refreshHelp();
  });
}

/** Sets a new code: entered twice so a slip doesn't lock you out. */
export async function choosePasscode() {
  const first = await enterPasscode({ title: t('passcode.newTitle'), hint: t('passcode.newHint') });
  if (!first) return undefined;
  const second = await enterPasscode({ title: t('passcode.repeatTitle') });
  if (!second) return undefined;
  if (first.join() !== second.join()) {
    toast(t('passcode.mismatch'));
    sound.bump();
    return undefined;
  }
  return first;
}

// Everyone waiting for the Windows sign-in gets the answer, not only the last one asking.
let pendingWindowsUnlocks = [];

/**
 * Windows mode: the sign-in alone opens nothing (Windows Hello signs in in passing, the parents may have unlocked for another
 * reason). The host sends "confirm", we show what was asked for, and only A on that opens it. Hosts that do not ask
 * for a confirmation (the preview) resolve at once.
 */
async function confirmAfterSignIn(payload, reason) {
  if (!payload?.confirm) return true;
  const accepted = await confirmDialog(t('passcode.confirmTitle'), t('passcode.confirmButton'), { message: reason || t('passcode.confirmGeneric') });
  const result = await attempt(() => api.confirmWindowsUnlock(accepted));
  return !!(accepted && result?.ok);
}

api.onUnlocked?.(async (payload) => {
  const waiting = pendingWindowsUnlocks;
  pendingWindowsUnlocks = [];
  for (const entry of waiting) entry.resolve(await confirmAfterSignIn(payload, entry.reason));
});

/** True when the couch lock is closed right now. A read that fails counts as "not locked": the caller's own request then shows the truth. */
export async function isLocked() {
  try {
    const security = await api.getSecurity();
    return !!security && security.unlocked === false;
  } catch {
    return false;
  }
}

/**
 * True once the couch lock lets the user through: the controller code, or the
 * Windows lock screen (Windows Hello, PIN) in Windows mode.
 */
export async function ensureUnlocked(reason = '') {
  const security = await attempt(() => api.getSecurity());
  if (!security || security.unlocked) return true;

  if (security.couchLock === 'windows') {
    toast(t('passcode.signInWindows'), 4000);
    const unlocked = new Promise((resolve) => {
      pendingWindowsUnlocks.push({ resolve, reason });
      // The host drops the request after three minutes.
      setTimeout(() => resolve(false), 3 * 60_000);
    });
    const asked = await attempt(() => api.unlock(null));
    // Windows could not be locked: nothing will come.
    if (asked?.pending === false) pendingWindowsUnlocks = pendingWindowsUnlocks.filter((e) => e.reason !== reason);
    return asked?.pending === false ? false : unlocked;
  }

  // One code entry at a time: a second request that arrives while the first one is open (a page that loads twice, a protected
  // entry and the page behind it) waits for that entry instead of opening a second one on top.
  codeEntry ??= askForCode(reason).finally(() => { codeEntry = null; });
  return codeEntry;
}

let codeEntry = null;

/**
 * Windows lock mode, also while the couch is open: the Windows sign-in once more (and its confirmation), so the CouchHUD service sees it
 * and gives its proof for a new couch code. True when that proof is there afterwards. A host without the service answers at once.
 */
export async function ensureWindowsSignIn(reason = '') {
  const entry = { reason };
  const signedIn = new Promise((resolve) => {
    entry.resolve = resolve;
    pendingWindowsUnlocks.push(entry);
    // The host drops the request after three minutes; so does this, and its entry goes.
    setTimeout(() => {
      pendingWindowsUnlocks = pendingWindowsUnlocks.filter((e) => e !== entry);
      resolve(false);
    }, 3 * 60_000);
  });
  const asked = await attempt(() => api.windowsSignIn());
  if (asked?.pending !== true) {
    pendingWindowsUnlocks = pendingWindowsUnlocks.filter((e) => e !== entry);
    return !!asked?.ok;
  }
  toast(t('passcode.signInWindows'), 4000);
  return signedIn;
}

async function askForCode(reason) {
  let error = '';
  for (;;) {
    const code = await enterPasscode({ title: t('passcode.enter'), hint: reason, error });
    if (!code) return false;
    let result = await attempt(() => api.unlock(code));
    // A lock of 0.4.0 whose stored code does not match the one entered (mistyped, or changed by a program of the account): UAC only when
    // the person chooses it here, then an administrator confirms the code as the lock's new one (third security review).
    if (result?.legacyMismatch && (await confirmDialog(t('passcode.legacyMismatch'), t('passcode.adminFallbackButton')))) {
      result = await attempt(() => api.unlock(code, true));
    }
    if (result?.ok) {
      sound.select();
      // The lock of 0.4.0 confirmed with its old code: that code lay readable in the configuration (review 4).
      if (result.legacyOldCode) toast(t('passcode.legacyOldCode'), 12000);
      return true;
    }
    sound.bump();
    // The host refuses to open while the CouchHUD service is missing or blocked (fail-closed): its reason, not "wrong code".
    error = result?.blocked ? result.blocked : result?.waitSeconds ? t('passcode.wrongWait', { count: result.waitSeconds }) : t('passcode.wrong');
  }
}

// Before Windows' UAC prompt for an administrator's approval: what exactly is approved (the prompt itself only names sc.exe).
// i18n-used: passcode.approve.autoLogon passcode.approve.lockMode passcode.approve.removeCode passcode.approve.changeCode passcode.approve.release passcode.approve.firewall passcode.approve.update passcode.approve.resetCodes
const LEGACY_TEXT = {
  legacyCode: () => t('passcode.approve.legacyCode'),
  legacyWindows: () => t('passcode.approve.legacyWindows'),
  legacyNewCode: () => t('passcode.approve.legacyNewCode')
};
const APPROVAL_TEXT = {
  SetAutoLogon: 'passcode.approve.autoLogon',
  SetLockMode: 'passcode.approve.lockMode',
  RemovePasscode: 'passcode.approve.removeCode',
  ChangePasscode: 'passcode.approve.changeCode',
  EnrollPasscode: 'passcode.approve.changeCode',
  ReleaseLockPolicies: 'passcode.approve.release',
  CleanupPolicyKeys: 'passcode.approve.release',
  SetRemoteFirewall: 'passcode.approve.firewall',
  InstallCouchHudUpdate: 'passcode.approve.update',
  ResetCodeStore: 'passcode.approve.resetCodes'
};
// Pages that wait for an unlock hear when an administrator's approval is coming (UAC), so they say so instead of a status of their own.
const approvalListeners = new Set();

/** Calls `listener` whenever the host announces an administrator's approval; returns the function that stops it. */
export function onApprovalAnnounced(listener) {
  approvalListeners.add(listener);
  return () => approvalListeners.delete(listener);
}

api.onAdminApproval?.((payload) => {
  for (const listener of [...approvalListeners]) {
    try {
      listener(payload);
    } catch (err) {
      console.error(err);
    }
  }
  // The lock of 0.4.0 the service took over: confirmed once, and the text says what (the code just entered, or the Windows sign-in) and
  // why (security review H-1). Long texts sit low on the screen, so the page title above stays readable.
  const legacy = Object.hasOwn(LEGACY_TEXT, payload?.reason ?? '') ? LEGACY_TEXT[payload.reason] : null;
  if (legacy) {
    toast(legacy(), 20000, { long: true });
    return;
  }
  const what = [...new Set((payload?.what ?? []).map((op) => APPROVAL_TEXT[op]).filter(Boolean))].map((key) => t(key));
  toast(t('passcode.approve.notice', { what: what.length ? what.join(', ') : t('passcode.approve.confirmOnly') }), 12000, { long: true });
});

// Same for the parents' code: a one-time release for one action, never the 2-minute unlock.
let pendingParentAuth = [];
api.onParentalAuth?.(async (payload) => {
  const waiting = pendingParentAuth;
  pendingParentAuth = [];
  for (const entry of waiting) entry.resolve(await confirmAfterSignIn(payload, entry.reason));
});

/**
 * The parents' code for exactly one action (extend time, release a locked game, change the profile). Asks every
 * time, even right after another unlock; the host uses it up with the action. Windows mode: lock screen.
 */
export async function ensureParentCode(reason = '', scope = {}) {
  const security = await attempt(() => api.getSecurity());
  const { action = null, item = null } = scope;
  if (security?.couchLock === 'windows') {
    toast(t('passcode.signInWindows'), 4000);
    const done = new Promise((resolve) => {
      pendingParentAuth.push({ resolve, reason });
      setTimeout(() => resolve(false), 3 * 60_000);
    });
    const asked = await attempt(() => api.parentalAuth(null, action, item));
    if (asked?.pending === false) {
      pendingParentAuth = pendingParentAuth.filter((e) => e.reason !== reason);
      return false;
    }
    return done;
  }
  let error = '';
  for (;;) {
    const code = await enterPasscode({ title: t('passcode.parentCode'), hint: reason, error });
    if (!code) return false;
    const result = await attempt(() => api.parentalAuth(code, action, item));
    if (result?.ok) {
      sound.select();
      return true;
    }
    sound.bump();
    // Locked out, or no parents' code in the CouchHUD service yet: an administrator of this PC can approve instead (Windows' own UAC
    // prompt), so a forgotten, missing or planted parents' code never shuts the parents out.
    const fallback = result?.admin === 'noCode' ? t('passcode.adminFallbackNoCode') : t('passcode.adminFallback');
    if (result?.admin && (await confirmDialog(fallback, t('passcode.adminFallbackButton')))) {
      const viaAdmin = await attempt(() => api.parentalAuth(null, action, item));
      if (viaAdmin?.ok) {
        sound.select();
        return true;
      }
      if (viaAdmin?.blocked) toast(viaAdmin.blocked, 6000);
    }
    // The CouchHUD service checks the parents' code too: when it cannot, its reason, not "wrong code".
    error = result?.blocked ? result.blocked : result?.waitSeconds ? t('passcode.wrongWait', { count: result.waitSeconds }) : t('passcode.wrong');
  }
}

/** Wraps an action that leaves the couch (PC mode, quit …) in the lock. */
export const guarded = (reason, run) => async () => (await ensureUnlocked(reason)) ? run() : undefined;
