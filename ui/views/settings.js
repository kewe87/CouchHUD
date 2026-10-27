// Settings center: categories on the left, their settings on the right.
//
// Two levels. Simple mode shows what one needs day to day, and a single button that sets up
// the whole console. Expert mode adds every fine adjustment: as an "Experte" section inside
// a category where the topic is shared (audio, display …), or as categories of their own
// below a divider. Every setting lives in exactly one place.

import { fmtDateTime, fmtNumber, fmtPercent, t } from '../core/i18n.js';
import { iconEl } from '../core/glyph-icons.js';
import { api } from '../core/api.js';
import { el, entriesLabel, keepInView, setFocused } from '../core/dom.js';
import { setBackground } from '../core/art.js';
import { attempt, toast } from '../core/feedback.js';
import { reloadLibrary } from '../core/library.js';
import { currentView, move, push, render, renderHints } from '../core/nav.js';
import { state } from '../core/state.js';
import { SOUND_SETS, setAudioMode, setMusicVolume, setSoundSet, setSoundVolume, sound } from '../sound.js';
import { setHdrMode } from '../core/hdr.js';
import { setPointer } from '../components/mouse.js';
import { SCREENSAVER_STYLES, configureScreensaver, startScreensaver } from '../core/screensaver.js';
import { chooseDialog, confirmDialog, openOverlay } from '../components/overlay.js';
import { openKeyboard } from '../components/keyboard.js';
import { skinMenu } from '../components/skin-menu.js';
import { choosePasscode, ensureParentCode, ensureUnlocked, ensureWindowsSignIn, guarded, isLocked } from '../components/passcode.js';
import { addEntryFlow, gameFoldersPage, hiddenItemsPage, scanFolderFlow } from '../components/library-tools.js';
import { refreshWeather } from '../core/weather.js';
import { preloadSmartHome, smartHomeView } from './smarthome.js';
import { setTabs, tabViews } from '../core/nav.js';
import { updateCount, updatesPage } from './updates.js';
import { caseHudApiOff, caseHudPage, caseHudUpdateAvailable } from './casehud.js';
import { cleanupStuckPoliciesDialog, launchersPage, servicesPage, startupPage } from './system-tools.js';
import { transparencyPage } from './transparency.js';
import { disclaimerPage, privacyPage } from './legal.js';
import { openSetupWizard } from './setup-wizard.js';
import { pairingPage } from './phone.js';
import { pluginsPage } from './plugins.js';
import { networkPage } from './network.js';
import { chooseSleepTimer, powerEntries, sleepTimerDetail } from '../core/power.js';
import { displayItems } from './display.js';
import { nightModeItems } from './deck.js';
import { categoryIcon } from './settings-icons.js';
import { consoleRevertPage, consoleSetupPage, hasConsoleSetup, isConsoleReady, openSetupSteps } from './console-setup.js';
import { saveSettingsItems } from '../components/savegames.js';
import { ensureParentGrant, formatRemaining, lockedGamesPage, parental, refreshParental } from '../components/parental.js';
import { refreshMotion } from '../core/motion.js';
import { codeLockHeld } from '../core/lock-rules.js';

const audioModes = () => [
  { label: t('settings.common.off'), detail: t('settings.audioMode.offDetail'), value: 'off' },
  { label: t('settings.audioMode.ui'), detail: t('settings.audioMode.uiDetail'), value: 'ui' },
  { label: t('settings.audioMode.music'), detail: t('settings.audioMode.musicDetail'), value: 'music' }
];

let settings = null;
let feeds = null;
let security = null;
// Titles of the console setup steps that are not in place (null: not known yet). Filled in the background.
let consoleOpen = null;
// Tasks or policy files of the setup that are still there (also in the light mode): the way back stays offered for them.
let helperLeftovers = false;
// Our values in the Windows policies that nothing explains: one cleanup entry in Settings > Console, light mode too.
let policiesStuck = false;

const lockModes = () => [
  { label: t('settings.common.off'), detail: t('settings.lockMode.offDetail'), value: 'off' },
  { label: t('settings.lockMode.code'), detail: t('settings.lockMode.codeDetail'), value: 'code' },
  { label: t('settings.lockMode.windows'), detail: t('settings.lockMode.windowsDetail'), value: 'windows' }
];

/**
 * With the parental controls on, changing the lock or the code needs the parents' code each time. The action names what the CouchHUD
 * service then does with the parents' approval ("lockMode", "passcode"): its grant counts for exactly that.
 */
const lockGrant = (reason, action) => (parental.status?.enabled ? ensureParentGrant(reason, { action }) : ensureUnlocked(reason));

async function chooseLock(preset) {
  const mode = preset ?? await chooseDialog(t('settings.lock.title'), lockModes(), security?.couchLock ?? 'off');
  if (mode === undefined) return 'cancelled';
  if (mode === security?.couchLock) return 'unchanged';
  // The Windows sign-in as the lock is no lock on an account without a password; the host refuses it too.
  if (mode === 'windows' && security?.accountPassword === false) {
    toast(t('settings.lock.noPassword'), 8000);
    return 'failed';
  }
  // With the CouchHUD service the switch to the Windows sign-in deletes the couch code (the service keeps one lock's key, not two). Said
  // before, with the way back: a new code then needs this Windows sign-in once more (finding H1 of the 0.4.1 acceptance test).
  if (mode === 'windows' && security?.couchLock === 'code' && security?.serviceCodes
    && !(await confirmDialog(t('settings.lock.windowsDropsCodeTitle'), t('settings.common.continue'), { message: t('settings.lock.windowsDropsCode') }))) return 'cancelled';
  // The code lock needs a code the CouchHUD service holds: a new one goes along with the switch, in one step (a code set on its own at an
  // open couch would not survive the lock being switched on). Entered before the approval, which then names it ("lockModeCode").
  let code = null;
  if (mode === 'code' && (!security?.hasPasscode || security?.couchLock === 'off' || security?.serviceCodes?.couch === false)) {
    code = await choosePasscode();
    if (!code) return 'cancelled';
  }
  if (!(await lockGrant(t('settings.lock.changeReason'), code ? 'lockModeCode' : 'lockMode'))) return 'cancelled';
  // From the Windows sign-in back to a code: the service takes the new code with that lock's own proof, the Windows sign-in seen by it
  // (the open couch alone does not prove who is in front of it). Asked right here, so nobody needs an administrator for it (finding H1).
  // With parental controls the parents' code above is the proof. Whether a Windows grant is there is asked after that step (it may have
  // just given one), not taken from the page's older state.
  if (code && security?.couchLock === 'windows' && security?.serviceCodes && !parental.status?.enabled) {
    const now = await attempt(() => api.getSecurity());
    if (!now?.windowsGrant && !(await ensureWindowsSignIn(t('settings.lock.codeAfterWindowsReason')))) return 'cancelled';
  }
  const updated = await attempt(() => api.setCouchLock(mode, code), code ? t('settings.lock.codeNotSaved') : t('settings.common.notSaved'));
  if (!updated) return 'failed';
  security = updated;
  toast(mode === 'off' ? t('settings.lock.off') : t('settings.lock.on'));
  if (mode === 'code' && security?.accountPassword === false) toast(t('settings.lock.codeNoPasswordWarn'), 9000);
  render();
  return 'done';
}

/**
 * The setup assistant's "set a code now": the same flow as Settings › Console › Couch lock with "code" picked, so every check
 * (couch or parents' code, password rules, the host's own refusal) stays in one place. True when the code lock has its code afterwards.
 */
export async function enrollCouchCode() {
  security = (await attempt(() => api.getSecurity())) ?? security;
  await chooseLock('code');
  security = (await attempt(() => api.getSecurity())) ?? security;
  return codeLockHeld(security);
}

async function changePasscode() {
  if (security?.hasPasscode && !(await lockGrant(t('settings.passcode.changeReason'), 'passcode'))) return 'cancelled';
  const code = await choosePasscode();
  if (!code) return 'cancelled';
  if (parental.status?.enabled && !(await lockGrant(t('settings.passcode.saveReason'), 'passcode'))) return 'cancelled';
  const updated = await attempt(() => api.setPasscode(code), t('settings.lock.codeNotSaved'));
  if (!updated) return 'failed';
  security = updated;
  toast(t('settings.passcode.saved'));
  render();
  return 'done';
}

/**
 * The console setup's lock step (see setupCouchLock in core/lock-rules.js): a flow of this page, then the host asked again. 'on' only
 * when the code lock really has its code afterwards; a refusal of the host is 'failed', never taken for done.
 */
async function codeLockOutcome(flow) {
  security = (await attempt(() => api.getSecurity())) ?? security;
  const outcome = await flow();
  security = (await attempt(() => api.getSecurity())) ?? security;
  if (outcome === 'done' || outcome === 'unchanged') return codeLockHeld(security) ? 'on' : 'failed';
  return outcome === 'failed' ? 'failed' : 'cancelled';
}

const autoLogonInfo = () => t('settings.autoLogon.info');

/**
 * Sets up the automatic Windows sign-in end to end: info, couch lock check, password entry
 * (masked, phone typing works), check against Windows, then the UAC step. Also used by the
 * console setup. Resolves to true when the sign-in is on afterwards.
 */
let lastAutoLogonError = null;

async function setupAutoLogon() {
  lastAutoLogonError = null;
  if (!(await ensureUnlocked(t('settings.autoLogon.title')))) return false;
  // A machine-wide setting: the CouchHUD service wants the parents' approval; without a parents' code Windows asks an administrator later.
  if (parental.status?.hasParentCode && !(await ensureParentCode(t('settings.autoLogon.title'), { action: 'autologon' }))) return false;
  security = (await attempt(() => api.getSecurity())) ?? security;
  if (security?.accountPassword === false) {
    toast(t('settings.autoLogon.noPassword'), 8000);
    return false;
  }
  if (!(await confirmDialog(autoLogonInfo(), t('settings.common.continue')))) return false;
  if (security?.passwordlessOnly) {
    // Windows ignores a stored password while "Windows Hello only" is on; CouchHUD restores it when switched off.
    if (!(await confirmDialog(t('settings.autoLogon.helloOnly'), t('settings.autoLogon.relax')))) return false;
  }
  let acceptNoLock = false;
  if (security?.couchLock === 'off') {
    acceptNoLock = await confirmDialog(t('settings.autoLogon.noLock'), t('settings.autoLogon.withoutLock'), { danger: true });
    if (!acceptNoLock) return false;
  }
  for (;;) {
    // The password lives only in this variable, for the length of one call.
    let password = await openKeyboard({
      title: security?.user ? t('settings.autoLogon.passwordFor', { user: security.user }) : t('settings.autoLogon.password'),
      secret: true,
      hint: t('settings.autoLogon.passwordHint')
    });
    if (!password) return false;
    toast(t('settings.autoLogon.checking'), 3000);
    let result;
    try {
      result = await api.enableAutoLogon(password, acceptNoLock);
    } catch (err) {
      console.error(err);
      lastAutoLogonError = err?.message || String(err);
      toast(t('settings.autoLogon.failed', { error: err?.message || err }), 6000);
      return false;
    } finally {
      password = '';
    }
    if (result?.ok) {
      security = result.security;
      toast(t('settings.autoLogon.isOn'));
      render();
      return true;
    }
    if (result?.needsLock) return false;
    if (result?.noPassword) {
      toast(result.error || t('settings.autoLogon.noPassword'), 8000);
      return false;
    }
    toast(result?.error || t('settings.autoLogon.rejected'), 6000);
  }
}

function autoLogonLabel() {
  if (!security?.autoLogon) return security?.autoLogonState === 'lost' ? t('settings.autoLogon.lost') : t('settings.common.off');
  const who = security.autoLogonUser ? ` (${security.autoLogonUser})` : '';
  return security.autoLogonState === 'couchhud' ? t('settings.autoLogon.labelOn', { who }) : t('settings.autoLogon.labelForeign', { who });
}

async function disableAutoLogon() {
  if (!(await confirmDialog(t('settings.autoLogon.disableConfirm'), t('settings.autoLogon.turnOff')))) return;
  toast(t('settings.common.confirmWindowsPrompt'), 4000);
  const updated = await attempt(() => api.setAutoLogon('off'), t('settings.autoLogon.notTurnedOff'));
  if (updated) {
    security = updated;
    toast(t('settings.autoLogon.isOff'));
    render();
  }
}

async function loadSettings() {
  settings = (await attempt(() => api.getSettings(), t('settings.loadFailed'))) ?? settings ?? {};
  feeds = (await attempt(() => api.getFeeds())) ?? feeds ?? [];
  security = (await attempt(() => api.getSecurity())) ?? security;
  await refreshParental();
  refreshConsoleOpen();
}

async function refreshConsoleOpen() {
  // Locked, the host refuses to look: the last known answer stays.
  if (!(await isLocked())) {
    try {
      const services = await api.getServices();
      helperLeftovers = !!services?.leftovers;
      policiesStuck = !!services?.stuckPolicies;
    } catch (err) {
      console.warn(`services: ${err?.message || err}`);
    }
  }
  if (!hasConsoleSetup(settings, security, helperLeftovers)) {
    consoleOpen = null;
    return;
  }
  const open = await openSetupSteps();
  const changed = JSON.stringify(open) !== JSON.stringify(consoleOpen);
  consoleOpen = open;
  if (changed && state.stack.length === 0 && currentView() === settingsView) render();
}

/** Paired phones: pick one to unpair it, or all of them. A phone that was not used for half a year is unpaired by the host anyway. */
async function manageRemoteDevices() {
  const info = await attempt(() => api.getRemote(), t('phone.unavailable'));
  if (!info) return;
  if (!info.devices?.length) return toast(t('phone.noneYet'));
  const when = (ms) => fmtDateTime(ms, { dateStyle: 'short', timeStyle: 'short' });
  const pick = await chooseDialog(t('settings.phone.manage'), [
    ...info.devices.map((d) => ({ label: d.name, detail: t('settings.phone.deviceDetail', { paired: when(d.paired), used: when(d.lastUsed ?? d.paired) }), icon: '📱', value: d.id })),
    { label: t('settings.phone.revoke'), detail: t('settings.phone.revokeDesc'), icon: '✕', danger: true, value: '*' }
  ]);
  if (pick === undefined) return;
  const all = pick === '*';
  const name = info.devices.find((d) => d.id === pick)?.name ?? '';
  if (!(await confirmDialog(all ? t('settings.phone.revokeConfirm') : t('settings.phone.revokeOne', { name }), t('settings.common.remove'), { danger: true }))) return;
  const done = await attempt(() => (all ? api.revokeRemote() : api.revokeRemoteDevice(pick)), t('settings.phone.notRemoved'));
  if (done !== undefined) toast(all ? t('settings.phone.revoked') : t('settings.phone.revokedOne', { name }));
}

/**
 * Saves one setting. Locked, the host refuses: the code dialog opens (as for every protected setting) and the save runs again,
 * instead of an error toast. undefined: not saved.
 */
async function saveSetting(key, value) {
  let failure;
  try {
    return await api.setSetting(key, value);
  } catch (err) {
    failure = err;
  }
  if (failure?.locked) {
    if (!(await ensureUnlocked(t('settings.windows.changeReason')))) return undefined;
    return attempt(() => api.setSetting(key, value), t('settings.common.saveFailed'));
  }
  return attempt(() => Promise.reject(failure), t('settings.common.saveFailed'));
}

async function change(key, value, message) {
  if ((await saveSetting(key, value)) === undefined) return;
  settings[key] = value;
  if (message) toast(message);
  render();
}

// ---------------------------------------------------------------------------
// Jugendschutz (rules live in the host, see components/parental.js)

const limitLabel = (m) => (m ? (m < 60 ? t('settings.parental.minutes', { m }) : formatRemaining(m * 60)) : t('settings.parental.unlimited'));
const limitOptions = () => [0, 60, 90, 120, 150, 180, 240, 300, 360].map((m) => ({ label: limitLabel(m), value: m }));
const gameLimitOptions = () => [0, 30, 45, 60, 90, 120].map((m) => ({ label: m ? limitLabel(m) : t('settings.common.off'), value: m }));
const ageOptions = () => [
  { label: t('settings.common.off'), value: 0 },
  ...[6, 12, 16, 18].map((a) => ({ label: t('settings.parental.blockAge', { age: a }), detail: t('settings.parental.blockAgeDetail'), value: a }))
];
const hourLabel = (minute) => `${Math.floor(minute / 60)}:${String(minute % 60).padStart(2, '0')}`;

/** Changes one profile setting; the parents' code first, the host checks again. */
async function setParentalValue(key, value, message) {
  // Switching the parental controls on chains the code: it becomes couch and parents' code in one step ("both"). The new code is entered
  // first, then one approval covers the switch and the code (an administrator through UAC where the service has no parents' code yet).
  const turningOn = key === 'enabled' && value === true;
  // Only from an unlocked couch: on a locked one the host does not take the code (switching on still works; the hint says what is left).
  const needsParentCode = turningOn && security?.couchLock === 'code' && security?.serviceCodes && security.serviceCodes.parent !== true && security?.unlocked !== false;
  let code = null;
  if (needsParentCode) {
    toast(t('settings.parental.setCodeFirst'), 6000);
    code = await choosePasscode();
    if (!code) return false;
  }
  // Switching off also removes the parents' code from the CouchHUD service: the approval is asked for that.
  const action = key !== 'enabled' ? 'profile' : value === false ? 'parentalOff' : 'parentalOn';
  const granted = needsParentCode
    ? !!(await attempt(() => api.parentalAuth(null, action, null)))?.ok
    : await ensureParentGrant(t('settings.parental.changeReason'), { action });
  if (!granted) {
    if (needsParentCode) toast(t('settings.parental.codeHint'), 9000);
    return false;
  }
  const status = await attempt(() => api.setParental(key, value), t('settings.common.notSaved'));
  if (!status) return false;
  if (code && !(await attempt(() => api.setParentCode(code), t('settings.lock.codeNotSaved')))) toast(t('settings.parental.codeHint'), 9000);
  if (turningOn && !code && security?.couchLock === 'code' && security?.serviceCodes && security.serviceCodes.parent !== true) toast(t('settings.parental.codeHint'), 9000);
  parental.status = status;
  security = (await attempt(() => api.getSecurity())) ?? security;
  await refreshParental();
  if (message) toast(message);
  render();
  return true;
}

async function chooseWindow() {
  const hours = Array.from({ length: 24 }, (_, h) => ({ label: t('settings.parental.hour', { h }), value: h * 60 }));
  const start = await chooseDialog(t('settings.parental.windowFrom'), [{ label: t('settings.parental.always'), detail: t('settings.parental.noWindow'), value: -1 }, ...hours], parental.status?.windowStart ?? -1);
  if (start === undefined) return;
  if (start < 0) {
    await setParentalValue('window', [-1, -1], t('settings.parental.noWindow'));
    return;
  }
  const endHours = hours.filter((h) => h.value !== start);
  const end = await chooseDialog(t('settings.parental.windowTo'), endHours, parental.status?.windowEnd >= 0 ? parental.status.windowEnd : (start + 360) % 1440);
  if (end === undefined) return;
  await setParentalValue('window', [start, end], t('settings.parental.windowSet', { start: hourLabel(start), end: hourLabel(end) }));
}

/** Runs one of the parents' one-time actions (reset counter, adopt the clock or zone) and refreshes. */
async function parentalAction(reason, call, message) {
  if (!(await ensureParentGrant(reason, { action: 'profile' }))) return;
  const status = await attempt(() => call(), t('settings.parental.notDone'));
  if (!status) return;
  parental.status = status;
  await refreshParental();
  toast(message);
  render();
}

function parentalItems() {
  const p = parental.status;
  if (!p) return [info(t('settings.parental.title'), t('settings.parental.unavailable'))];
  const mode = !p.enabled ? t('settings.common.off')
    : p.enforced ? t('settings.parental.onEnforced')
      : ({
        noLock: t('settings.parental.advisoryNoLock'),
        notShell: t('settings.parental.advisoryNotShell'),
        pcMode: t('settings.parental.advisoryPcMode')
      })[p.advisory] ?? t('settings.parental.advisory');
  const status = info(t('settings.parental.status'), mode);
  const reliability = info(t('settings.parental.reliabilityTitle'), t('settings.parental.reliability'));
  const runtime = runtimeWarningItems();
  if (!p.hasParentCode) {
    return [
      status,
      reliability,
      ...runtime,
      ...(p.codeProblem === 'noPassword' ? [info(t('settings.parental.noPasswordTitle'), t('settings.parental.noPassword'))] : []),
      action(t('settings.parental.setupLock'), t('settings.parental.setupLockDetail'), chooseLock, '🔑')
    ];
  }
  const today = p.remainingSeconds == null
    ? t('settings.parental.played', { time: limitLabel(p.usedMinutes) })
    : t('settings.parental.playedLeft', { time: limitLabel(p.usedMinutes), left: p.remainingSeconds <= 0 ? t('settings.parental.minutes', { m: 0 }) : formatRemaining(p.remainingSeconds), bonus: p.bonusMinutes ? t('settings.parental.bonus', { minutes: p.bonusMinutes }) : '' });
  return [
    {
      kind: 'toggle',
      label: t('settings.parental.title'),
      desc: t('settings.parental.desc'),
      value: () => !!parental.status?.enabled,
      run: () => setParentalValue('enabled', !parental.status?.enabled, parental.status?.enabled ? t('settings.parental.off') : t('settings.parental.on'))
    },
    status,
    info(t('settings.parental.today'), today),
    ...(p.tampered ? [action(t('settings.parental.tamperedTitle'), t('settings.parental.tamperedDetail'), () => parentalAction(t('settings.parental.resetReason'), () => api.parentalReset(), t('settings.parental.resetDone')), '⚠')] : []),
    ...(p.clockJumped ? [action(t('settings.parental.clockTitle'), t('settings.parental.clockDetail'), () => parentalAction(t('settings.parental.clockReason'), () => api.parentalAcceptClock(), t('settings.parental.clockDone')), '⏱')] : []),
    ...(p.timeZoneChanged ? [action(t('settings.parental.zoneTitle'), t('settings.parental.zoneDetail'), () => parentalAction(t('settings.parental.zoneReason'), () => api.parentalAdoptZone(), t('settings.parental.zoneDone')), '🌍')] : []),
    {
      kind: 'choice',
      label: t('settings.parental.weekday'),
      desc: t('settings.parental.weekdayDesc'),
      value: () => limitLabel(p.weekdayMinutes),
      run: async () => {
        const v = await chooseDialog(t('settings.parental.weekdayTitle'), limitOptions(), p.weekdayMinutes);
        if (v !== undefined) await setParentalValue('weekdayMinutes', v);
      }
    },
    {
      kind: 'choice',
      label: t('settings.parental.weekend'),
      desc: t('settings.parental.weekendDesc'),
      value: () => limitLabel(p.weekendMinutes),
      run: async () => {
        const v = await chooseDialog(t('settings.parental.weekendTitle'), limitOptions(), p.weekendMinutes);
        if (v !== undefined) await setParentalValue('weekendMinutes', v);
      }
    },
    {
      kind: 'choice',
      label: t('settings.parental.window'),
      desc: t('settings.parental.windowDesc'),
      value: () => (p.windowStart < 0 ? t('settings.parental.windowAlways') : t('settings.parental.windowValue', { start: hourLabel(p.windowStart), end: hourLabel(p.windowEnd) })),
      run: chooseWindow
    },
    {
      kind: 'choice',
      label: t('settings.parental.perGame'),
      desc: t('settings.parental.perGameDesc'),
      value: () => (p.perGameMinutes ? limitLabel(p.perGameMinutes) : t('settings.common.off')),
      run: async () => {
        const v = await chooseDialog(t('settings.parental.perGameTitle'), gameLimitOptions(), p.perGameMinutes);
        if (v !== undefined) await setParentalValue('perGameMinutes', v);
      }
    },
    {
      kind: 'choice',
      label: t('settings.parental.age'),
      desc: t('settings.parental.ageDesc'),
      value: () => (p.maxAge ? t('settings.parental.ageValue', { age: p.maxAge }) : t('settings.common.off')),
      run: async () => {
        const v = await chooseDialog(t('settings.parental.age'), ageOptions(), p.maxAge);
        if (v !== undefined) await setParentalValue('maxAge', v);
      }
    },
    ...(p.maxAge ? [{
      kind: 'toggle',
      label: t('settings.parental.blockUnknown'),
      desc: t('settings.parental.blockUnknownDesc'),
      value: () => !!parental.status?.blockUnknownAge,
      run: () => setParentalValue('blockUnknownAge', !parental.status?.blockUnknownAge)
    }] : []),
    {
      kind: 'toggle',
      label: t('settings.parental.countApps'),
      desc: t('settings.parental.countAppsDesc'),
      value: () => !!parental.status?.countApps,
      run: () => setParentalValue('countApps', !parental.status?.countApps)
    },
    action(t('settings.parental.locked'), t('settings.parental.lockedDesc', { entries: entriesLabel(p.lockedItems.length) }), () => openPage(lockedGamesPage(), t('settings.parental.title'))),
    ...(security?.couchLock === 'code' ? [action(t('settings.parental.changeCode'), t('settings.parental.changeCodeDesc'), changePasscode)] : []),
    reliability,
    ...runtime,
    info(t('settings.parental.timeUp'), t('settings.parental.timeUpDesc', { seconds: p.graceSeconds ?? 60 }))
  ];
}

const integrationInfo = () => t('settings.integration.fullInfo');

async function chooseIntegration() {
  const current = settings.integrationMode ?? 'light';
  const mode = await chooseDialog(t('settings.integration.title'), [
    { label: t('settings.integration.light'), detail: t('settings.integration.lightDetail'), value: 'light' },
    { label: t('settings.integration.full'), detail: t('settings.integration.fullDetail'), value: 'full' }
  ], current);
  if (mode === undefined || mode === current) return;
  if (mode === 'full' && !(await confirmDialog(integrationInfo(), t('settings.integration.fullOn')))) return;
  if (mode === 'light') {
    if (!(await confirmDialog(t('settings.integration.lightConfirm'), t('settings.integration.toLight')))) return;
    if (!(await ensureUnlocked(t('settings.integration.changeReason')))) return;
  }
  const result = await attempt(() => api.setIntegrationMode(mode), t('settings.integration.notSwitched'));
  if (!result) return;
  settings.integrationMode = result.mode;
  state.info.integrationMode = result.mode;
  if (mode === 'light') {
    settings.isShell = false;
    state.info.isShell = false;
    security = (await attempt(() => api.getSecurity())) ?? security;
  }
  toast(result.undone?.length ? result.undone.join(' · ') : mode === 'full' ? t('settings.integration.fullDone') : t('settings.integration.lightDone'), 6000);
  render();
}

async function setShell(install) {
  if (install && !(await confirmDialog(t('settings.shell.confirm'), t('settings.shell.setup')))) return;
  if (!install && !(await ensureUnlocked(t('settings.shell.offReason')))) return;
  const isShell = await attempt(() => api.setShell(install));
  if (isShell === undefined) return;
  state.info.isShell = isShell;
  settings.isShell = isShell;
  toast(isShell ? t('settings.shell.isOn') : t('settings.shell.isOff'));
  render();
}

// ---------------------------------------------------------------------------
// Pointer in the HUD: the skin's own, the real Windows one, or (unlocked) the comic mouse

const pointerModes = (unlocked) => [
  { label: t('settings.pointer.skin'), detail: t('settings.pointer.skinDetail'), value: 'skin' },
  { label: t('settings.pointer.system'), detail: t('settings.pointer.systemDetail'), value: 'system' },
  ...(unlocked ? [{ label: t('settings.pointer.mouse'), detail: t('settings.pointer.mouseDetail'), value: 'mouse' }] : [])
];

const pointerMode = (s) => (s.pointer === 'system' || (s.pointer === 'mouse' && s.mouseUnlocked) ? s.pointer : 'skin');

function applyPointer() {
  state.info.pointer = settings.pointer;
  state.info.mouseUnlocked = settings.mouseUnlocked;
  setPointer(settings.pointer, settings.mouseUnlocked);
}

async function choosePointer() {
  const current = pointerMode(settings);
  const v = await chooseDialog(t('settings.pointer.title'), pointerModes(settings.mouseUnlocked), current);
  if (v === undefined || v === current) return;
  await change('pointer', v);
  applyPointer();
}

// Hidden: up up down down left right left right B A on the info page toggles the comic mouse.
const EGG = ['up', 'up', 'down', 'down', 'left', 'right', 'left', 'right', 'b', 'a'];
let eggStep = 0;

async function eggInput(action, catId) {
  eggStep = catId === 'info' && action === EGG[eggStep] ? eggStep + 1 : catId === 'info' && action === EGG[0] ? 1 : 0;
  if (eggStep < EGG.length) return false;
  eggStep = 0;
  const on = !settings.mouseUnlocked;
  if ((await saveSetting('mouseUnlocked', on)) === undefined) return false;
  settings.mouseUnlocked = on;
  if (!on && settings.pointer === 'mouse') settings.pointer = 'skin';
  applyPointer();
  toast(on ? t('settings.pointer.eggOn') : t('settings.pointer.eggOff'));
  sound.notify?.();
  return true;
}

const toggle = (key, label, desc, after) => ({
  kind: 'toggle',
  label,
  desc,
  value: () => !!settings?.[key],
  run: async () => {
    await change(key, !settings[key]);
    await after?.(settings[key]);
  }
});

const action = (label, desc, run, icon = '›', hint) => ({ kind: 'action', label, desc, icon, run, hint });
const info = (label, value) => ({ kind: 'info', label, value });

// ---------------------------------------------------------------------------
// Structure

/** Marks items as expert-only; simple mode hides them, expert mode shows them below a divider. */
const expert = (...items) => items.flat().map((item) => ({ ...item, expert: true }));

/** Opens a page below the settings and tells it where it came from (breadcrumb in its header). */
function openPage(page, crumb) {
  const build = page.build.bind(page);
  page.build = (root) => {
    build(root);
    const head = root.querySelector('.files-head');
    if (head && !head.querySelector('.crumb')) head.prepend(el('div', { className: 'crumb', textContent: t('settings.crumb', { page: crumb }) }));
  };
  push(page);
}

const xboxModes = (light) => [
  { label: t('settings.xbox.couchhud'), detail: light ? t('settings.xbox.couchhudLight') :t('settings.xbox.couchhudFull'), value: 'couchhud' },
  { label: t('settings.xbox.gamebar'), detail: t('settings.xbox.gamebarDetail'), value: 'gamebar' },
  { label: t('settings.xbox.hold'), detail: t('settings.xbox.holdDetail'), value: 'hold' }
];

const isLight = (s) => (s.integrationMode ?? 'light') !== 'full';
const xboxMode = (s) => (s.gameBarButton ? 'gamebar' : s.guideTapInGame ? 'couchhud' : 'hold');

async function chooseXboxButton() {
  const mode = await chooseDialog(t('settings.xbox.title'), xboxModes(isLight(settings)), xboxMode(settings));
  if (mode === undefined || mode === xboxMode(settings)) return;
  const set = (key, value) => saveSetting(key, value);
  if (mode === 'couchhud') {
    await set('guideTapInGame', true);
    // Light leaves the Game Bar alone by itself; choosing this explicitly turns its button off.
    if (isLight(settings)) await set('gameBarButton', false);
  } else if (mode === 'gamebar') await set('gameBarButton', true);
  else {
    await set('guideTapInGame', false);
    await set('gameBarButton', false);
  }
  // The host couples the two switches; read back what it ended up with.
  await loadSettings();
}

function holdLabel(ms) {
  const seconds = ms / 1000;
  return t('settings.hold', { count: seconds, n: fmtNumber(seconds) });
}

/** Also used by the setup assistant: it hands off to the same console setup and way back as the Console category. */
export const consoleDeps = {
  setupAutoLogon: () => setupAutoLogon(),
  // The code lock with its code in one step, as in Settings › Console › Couch lock.
  enrollCodeLock: () => codeLockOutcome(() => chooseLock('code')),
  // The code lock is on but no code opens it: a new code for the lock that is on.
  restoreCodeLock: () => codeLockOutcome(changePasscode),
  autoLogonError: () => lastAutoLogonError,
  async refresh() {
    await loadSettings();
    state.info.isShell = !!settings?.isShell;
    state.info.integrationMode = settings?.integrationMode ?? state.info.integrationMode;
    if (state.stack.length === 0 && currentView() === settingsView) render();
    refreshConsoleOpen();
  }
};

function consoleItems(s) {
  const switchesOn = isConsoleReady(s, security);
  // The main switches alone do not say that: a step such as the services can have failed.
  const open = switchesOn ? consoleOpen : null;
  const incomplete = !!open?.length;
  const ready = switchesOn && !incomplete;
  return [
    {
      kind: 'action',
      primary: true,
      label: incomplete ? t('settings.console.retry') : ready ? t('settings.console.check') : t('settings.console.setup'),
      desc: incomplete
        ? t('settings.console.openDesc', { steps: open.join(', ') })
        : ready
          ? (open ? t('settings.console.checkDesc') : t('settings.console.checkingDesc'))
          : t('settings.console.setupDesc'),
      icon: '›',
      run: () => openPage(consoleSetupPage(consoleDeps), t('settings.cat.console'))
    },
    ...(hasConsoleSetup(s, security, helperLeftovers)
      ? [hasConsoleSetup(s, security)
        ? action(t('settings.console.revert'), t('settings.console.revertDesc'), () => openPage(consoleRevertPage(consoleDeps, !!s.isShell), t('settings.cat.console')))
        : action(t('settings.console.cleanup'), t('settings.console.cleanupDesc'), () => openPage(consoleRevertPage(consoleDeps, false), t('settings.cat.console')))]
      : []),
    ...(policiesStuck ? [action(t('systemTools.services.stuckTitle'), t('systemTools.services.stuckDetail'), async () => {
      if (await cleanupStuckPoliciesDialog()) {
        policiesStuck = false;
        render();
      }
    })] : []),
    action(t('settings.console.changes'), t('settings.console.changesDesc'), () => openPage(transparencyPage(), t('settings.cat.console'))),
    info(t('settings.console.start'), s.isShell ? t('settings.console.startShell') : t('settings.console.startDesktop')),
    info(t('settings.console.lock'), ({ off: t('settings.common.off'), code: t('settings.lockMode.code'), windows: t('settings.lockMode.windows') })[security?.couchLock ?? 'off']),
    // The HUD runs on a WebView2 runtime the account's programs can change: the lock is weaker than it looks.
    ...runtimeWarningItems(),
    // Honest about the Windows policies of the locked couch: without the CouchHUD service they are not active.
    info(t('settings.console.policies'), security ? ({ service: t('settings.console.policiesService'), direct: t('settings.console.policiesDirect'), none: t('settings.console.policiesNone') })[security.policies ?? 'none'] : '–'),
    // The service itself: its state for the experts, a clear warning and the repair for everyone when something is wrong.
    ...(security?.service?.problem ? [repairServiceItem()] : []),
    ...(security?.service ? expert(info(t('settings.console.service'), security.service.version ? `${security.service.label} (${security.service.version})` : security.service.label)) : []),
    // A parents' code in the service that this HUD did not set (no parental controls here).
    ...(security?.parentCodeUnknown ? [info(t('settings.console.parentUnknown'), t('settings.console.parentUnknownDetail'))] : []),
    // The local code was not taken over: the service holds one of its own (review L-c).
    ...(security?.codeConflict ? [info(t('settings.console.codeConflict'), t('settings.console.codeConflictDetail'))] : []),
    // Which codes the service holds for this account (it checks them, not the settings file).
    ...(security?.serviceCodes ? [info(t('settings.console.serviceCodes'), t('settings.console.serviceCodesValue', {
      couch: security.serviceCodes.couch ? t('settings.console.codeHeld') : t('settings.console.codeNone'),
      parent: security.serviceCodes.parent ? t('settings.console.codeHeld') : t('settings.console.codeNone')
    }))] : []),
    info(t('settings.autoLogon.title'), security ? autoLogonLabel() : '–')
  ];
}

/**
 * A couch lock or parental controls are on and the WebView2 runtime of the HUD or the couch browser lies outside Program Files
 * (per-user install) or could not be checked at all.
 */
function runtimeWarningItems() {
  if (!security || security.runtimeUnprotected !== true) return [];
  if (security.couchLock === 'off' && !parental.status?.enabled) return [];
  const text = security.runtimeUnchecked ? t('settings.lock.runtimeUnchecked') : t('settings.lock.runtimeUnprotected');
  return [info(t('settings.windows.warning'), text)];
}

/** "Repair the service": shown in both modes whenever the CouchHUD service should be there and is not healthy. */
function repairServiceItem() {
  const svc = security.service;
  return action(t('settings.console.repairService'), svc.alarm ? t('settings.console.repairAlarm', { state: svc.label }) : t('settings.console.repairDesc', { state: svc.label }), repairService, '⚠');
}

async function repairService() {
  if (!security?.service?.repairScript) return toast(t('settings.console.repairMissing'), 8000);
  if (!(await confirmDialog(t('settings.console.repairQuestion'), t('settings.console.repairConfirm')))) return;
  toast(t('settings.common.confirmWindowsPrompt'), 4000);
  const result = await attempt(() => api.repairService(), t('settings.console.repairFailed'));
  if (!result) return;
  security = (await attempt(() => api.getSecurity())) ?? security;
  toast(t('settings.console.repairDone'));
  render();
}

function displayCategoryItems(s) {
  const out = [];
  const rows = displayItems(() => state.stack.length === 0 && render());
  for (const item of rows) {
    if (item === rows[1]) out.push(...expert(item));
    else if (item.kind === 'toggle') {
      out.push({ ...item, label: t('settings.display.hdrWindows'), desc: t('settings.display.hdrWindowsDesc') });
    } else out.push(item);
  }
  const nightRows = nightModeItems(s, change);
  out.push(...nightRows.map((item) => (item === nightRows[0] ? item : { ...item, expert: true })));
  out.push(
    {
      kind: 'choice',
      label: t('settings.screensaver.title'),
      desc: t('settings.screensaver.desc'),
      value: () => (s.screensaverMinutes ? t('settings.after.minutes', { m: s.screensaverMinutes }) : t('settings.common.off')),
      run: async () => {
        const v = await chooseDialog(t('settings.screensaver.title'), [0, 2, 5, 10, 15, 30].map((m) => ({ label: m ? t('settings.screensaver.afterMinutes', { m }) : t('settings.common.off'), value: m })), s.screensaverMinutes ?? 5);
        if (v === undefined) return;
        await change('screensaverMinutes', v);
        configureScreensaver({ minutes: v });
      }
    },
    ...(s.screensaverMinutes ? [
      {
        kind: 'choice',
        label: t('settings.screensaver.displayOff'),
        desc: t('settings.screensaver.displayOffDesc'),
        value: () => (s.displayOffMinutes ? t('settings.after.minutes', { m: s.displayOffMinutes }) : t('settings.never')),
        run: async () => {
          const v = await chooseDialog(t('settings.screensaver.displayOff'), [0, 10, 30, 60].map((m) => ({ label: m ? t('settings.screensaver.afterSaver', { m }) : t('settings.never'), value: m })), s.displayOffMinutes ?? 0);
          if (v === undefined) return;
          await change('displayOffMinutes', v);
          configureScreensaver({ displayOff: v });
        }
      },
      ...expert(
        {
          kind: 'choice',
          label: t('settings.screensaver.style'),
          desc: t('settings.screensaver.styleDesc'),
          value: () => SCREENSAVER_STYLES.find((x) => x.value === (s.screensaverStyle ?? 'art'))?.label,
          run: async () => {
            const v = await chooseDialog(t('settings.screensaver.styleTitle'), SCREENSAVER_STYLES, s.screensaverStyle ?? 'art');
            if (v === undefined) return;
            await change('screensaverStyle', v);
            configureScreensaver({ style: v });
          }
        },
        action(t('settings.screensaver.preview'), t('settings.screensaver.previewDesc'), () => startScreensaver(s.screensaverStyle ?? 'art'))
      )
    ] : []),
    ...expert(
      {
        kind: 'choice',
        label: t('settings.hdr.title'),
        desc: t('settings.hdr.desc'),
        value: () => ({ off: t('settings.common.off'), auto: t('settings.common.auto'), on: t('settings.common.on') })[s.uiHdr ?? 'auto'],
        run: async () => {
          const mode = await chooseDialog(t('settings.hdr.title'), [
            { label: t('settings.common.auto'), detail: t('settings.hdr.autoDetail'), value: 'auto' },
            { label: t('settings.common.on'), detail: t('settings.hdr.onDetail'), value: 'on' },
            { label: t('settings.common.off'), detail: t('settings.hdr.offDetail'), value: 'off' }
          ], s.uiHdr ?? 'auto');
          if (mode === undefined) return;
          await change('uiHdr', mode);
          setHdrMode(mode);
        }
      },
      toggle('pixelShift', t('settings.pixelShift'), t('settings.pixelShiftDesc'), (on) => configureScreensaver({ pixelShift: on }))
    )
  );
  return out;
}

function audioItems(s) {
  const mode = s.audioMode ?? 'ui';
  return [
    {
      kind: 'choice',
      label: t('settings.audio.title'),
      desc: t('settings.audio.desc'),
      value: () => audioModes().find((m) => m.value === mode)?.label,
      run: async () => {
        const chosen = await chooseDialog(t('settings.audio.title'), audioModes(), mode);
        if (chosen === undefined) return;
        await change('audioMode', chosen);
        settings.sounds = chosen !== 'off';
        setAudioMode(chosen);
        sound.select();
      }
    },
    ...(mode === 'music' ? [{
      kind: 'choice',
      label: t('settings.audio.musicVolume'),
      desc: t('settings.audio.musicVolumeDesc'),
      value: () => fmtPercent(s.musicVolume ?? 35),
      run: async () => {
        const v = await chooseDialog(t('settings.audio.musicVolume'), [10, 20, 35, 50, 75, 100].map((x) => ({ label: fmtPercent(x), value: x })), s.musicVolume ?? 35);
        if (v === undefined) return;
        await change('musicVolume', v);
        setMusicVolume(v / 100);
      }
    }] : []),
    ...(mode !== 'off' ? [{
      kind: 'choice',
      label: t('settings.audio.uiVolume'),
      desc: t('settings.audio.uiVolumeDesc'),
      value: () => fmtPercent(s.soundVolume ?? 70),
      run: async () => {
        const v = await chooseDialog(t('settings.audio.volumeTitle'), [25, 50, 70, 100].map((x) => ({ label: fmtPercent(x), value: x })), s.soundVolume ?? 70);
        if (v === undefined) return;
        await change('soundVolume', v);
        setSoundVolume(v / 100);
        sound.select();
      }
    }] : []),
    toggle('autoSwitchHeadphones', t('settings.audio.headphones'), t('settings.audio.headphonesDesc')),
    ...(mode !== 'off' ? expert({
      kind: 'choice',
      label: t('settings.audio.soundSet'),
      desc: t('settings.audio.soundSetDesc'),
      value: () => (s.soundSet ? SOUND_SETS.find((x) => x.id === s.soundSet)?.label.split(':')[0] : t('settings.audio.soundSetSkin')),
      run: async () => {
        const options = [{ label: t('settings.audio.soundSetSkin'), value: '' }, ...SOUND_SETS.map((x) => ({ label: x.label, value: x.id }))];
        const set = await chooseOverlayWithPreview(t('settings.audio.soundSet'), options, s.soundSet ?? '');
        if (set === undefined) return;
        await change('soundSet', set);
        setSoundSet(set || null);
      }
    }) : []),
    info(t('settings.audio.outputTitle'), t('settings.audio.outputDesc'))
  ];
}

function windowsItems(s) {
  const full = (s.integrationMode ?? 'light') === 'full';
  if (!full) {
    return [
      { kind: 'choice', label: t('settings.integration.title'), desc: t('settings.windows.lightDesc'), value: () => t('settings.integration.light'), run: chooseIntegration },
      toggle('closeExplorerInLight', t('settings.windows.hideTaskbar'), t('settings.windows.hideTaskbarDesc')),
      ...(security?.autoLogon ? [action(t('settings.windows.autoOff'), t('settings.windows.autoOffDesc'), disableAutoLogon)] : []),
      info(t('settings.windows.fullTitle'), t('settings.windows.fullNote'))
    ];
  }
  return [
    { kind: 'choice', label: t('settings.integration.title'), desc: t('settings.windows.fullDesc'), value: () => t('settings.integration.fullShort'), run: chooseIntegration },
    {
      kind: 'toggle',
      label: t('settings.windows.shell'),
      desc: t('settings.windows.shellDesc'),
      value: () => !!s.isShell,
      run: () => setShell(!s.isShell)
    },
    {
      kind: 'choice',
      label: t('settings.lock.title'),
      desc: security?.couchLock !== 'off' && security?.accountPassword === false ? t('settings.lock.codeNoPasswordWarn') : t('settings.windows.lockDesc'),
      value: () => lockModes().find((m) => m.value === (security?.couchLock ?? 'off'))?.label,
      run: chooseLock
    },
    action(security?.hasPasscode ? t('settings.passcode.changeReason') : t('settings.windows.setCode'), t('settings.windows.codeDesc'), changePasscode),
    ...(security && security.couchLock !== 'off' ? [{
      kind: 'toggle',
      label: t('settings.windows.taskManager'),
      desc: t('settings.windows.taskManagerDesc'),
      value: () => !!security?.blockTaskManager,
      run: async () => {
        if (!(await ensureUnlocked(t('settings.windows.changeReason')))) return;
        const updated = await attempt(() => api.setBlockTaskManager(!security.blockTaskManager), t('settings.common.notSaved'));
        if (updated) security = updated;
        render();
      }
    }] : []),
    security?.autoLogon || security?.autoLogonState === 'lost'
      ? action(security.autoLogon ? t('settings.windows.autoOff') : t('settings.windows.deletePassword'), t('settings.windows.deletePasswordDesc'), disableAutoLogon)
      : action(t('settings.windows.autoSetup'), t('settings.windows.autoSetupDesc'), setupAutoLogon),
    ...((security?.autoLogon && (security.autoLogonState !== 'couchhud' || security.autoLogonOtherUser)) || security?.autoLogonState === 'lost' ? [action(t('settings.windows.autoRedo'), t('settings.windows.autoRedoDesc'), setupAutoLogon)] : []),
    action(t('settings.windows.netplwiz'), t('settings.windows.netplwizDesc'), async () => { await attempt(() => api.openAutoLogonDialog(), t('settings.windows.netplwizFailed')); }),
    toggle('suppressCompetitors', t('settings.windows.suppress'), t('settings.windows.suppressDesc')),
    toggle('killExplorerOnReturn', t('settings.windows.killExplorer'), t('settings.windows.killExplorerDesc')),
    ...(security?.autoLogon && security.autoLogonOtherUser ? [info(t('settings.windows.warning'), t('settings.windows.otherUser', { other: security.autoLogonUser, user: security.user }))] : []),
    ...(security?.autoLogonState === 'lost' ? [info(t('settings.windows.warning'), t('settings.windows.lost'))] : []),
    ...runtimeWarningItems(),
    info(t('settings.windows.emergency'), t('settings.windows.emergencyDesc')),
    info(t('settings.windows.note'), t('settings.windows.noteDesc'))
  ];
}

function startupItems(s) {
  const full = (s.integrationMode ?? 'light') === 'full';
  return [
    ...(full ? [
      toggle('runStartupApps', t('settings.startup.apps'), t('settings.startup.appsDesc')),
      action(t('settings.startup.choose'), t('settings.startup.chooseDesc'), () => openPage(startupPage(), t('settings.cat.startup'))),
      toggle('closePcAppsOnReturn', t('settings.startup.closePc'), t('settings.startup.closePcDesc')),
      action(t('settings.startup.services'), t('settings.startup.servicesDesc'), () => openPage(servicesPage(), t('settings.cat.startup')))
    ] : [info(t('settings.startup.fullOnly'), t('settings.startup.fullOnlyDesc'))])
  ];
}

/** Power rows from the shared module, so every menu asks the same questions. */
const powerItems = (ids) => powerEntries(ids).map((e) => action(e.label, e.detail, e.run));

function categories() {
  const s = settings ?? {};
  const [pcMode, standby, restart, shutdown, restartPc] = powerItems(['pc-mode', 'standby', 'restart', 'shutdown', 'restart-pc']);
  const holdOptions = [500, 1000, 1500, 2000].map((ms) => ({ label: holdLabel(ms), value: ms }));
  return [
    { id: 'console', icon: 'console', name: t('settings.cat.console'), items: consoleItems(s) },
    {
      id: 'library',
      icon: 'library',
      name: t('settings.cat.library'),
      items: [
        action(t('settings.library.add'), t('settings.library.addDesc'), addEntryFlow, '+'),
        action(t('settings.library.folders'), t('settings.library.foldersDesc'), () => openPage(gameFoldersPage(), t('settings.cat.library'))),
        action(t('settings.library.clients'), t('settings.library.clientsDesc'), () => openPage(launchersPage(), t('settings.cat.library'))),
        toggle('maximizeBrowser', t('settings.library.maximizeBrowser'), t('settings.library.maximizeBrowserDesc')),
        action(t('settings.library.hidden'), t('settings.library.hiddenCount', { count: state.library.filter((i) => i.hidden).length }), () => openPage(hiddenItemsPage(), t('settings.cat.library'))),
        ...expert(
          action(t('settings.library.scan'), t('settings.library.scanDesc'), scanFolderFlow),
          action(t('settings.library.rescan'), t('settings.library.rescanDesc'), () => reloadLibrary(true)),
          ...saveSettingsItems(),
          {
            kind: 'text',
            label: t('settings.library.sgdb'),
            desc: t('settings.library.sgdbDesc'),
            value: () => (s.steamGridDbKeySet ? t('settings.common.set') : t('settings.common.notSet')),
            run: async () => {
              const key = await openKeyboard({ title: t('settings.library.sgdb'), secret: true, hint: t('settings.library.sgdbHint'), paste: true });
              if (key === undefined) return;
              await change('steamGridDbApiKey', key, key ? t('settings.library.sgdbSaved') : t('settings.library.sgdbRemoved'));
              settings.steamGridDbKeySet = !!key;
              render();
            }
          },
          toggle('onlineArtwork', t('settings.library.onlineArt'), t('settings.library.onlineArtDesc')),
          action(t('settings.library.clearArt'), t('settings.library.clearArtDesc', { mb: s.artworkCacheMb ?? 0 }), async () => {
            if ((await saveSetting('clearArtworkCache', true)) === undefined) return;
            settings.artworkCacheMb = 0;
            toast(t('settings.library.artCleared'));
            render();
          })
        )
      ]
    },
    {
      id: 'appearance',
      icon: 'appearance',
      name: t('settings.cat.appearance'),
      items: [
        { kind: 'choice', label: t('settings.appearance.skin'), desc: state.skin.description, value: () => state.skin.name, run: () => openOverlay(skinMenu()) },
        { kind: 'choice', label: t('settings.pointer.title'), desc: t('settings.pointer.desc'), value: () => pointerModes(s.mouseUnlocked).find((m) => m.value === pointerMode(s))?.label, run: choosePointer },
        action(t('settings.appearance.install'), t('settings.appearance.installDesc'), () => openPage(pluginsPage(1), t('settings.cat.appearance'))),
        toggle('trailerPreviews', t('settings.appearance.trailers'), t('settings.appearance.trailersDesc'), (on) => (state.info.trailerPreviews = on)),
        toggle('reduceMotion', t('settings.appearance.motion'), t('settings.appearance.motionDesc'), (on) => {
          state.info.reduceMotion = on;
          refreshMotion();
        })
      ]
    },
    { id: 'display', icon: 'display', name: t('settings.cat.display'), items: displayCategoryItems(s) },
    { id: 'audio', icon: 'audio', name: t('settings.cat.audio'), items: audioItems(s) },
    {
      id: 'controller',
      icon: 'controller',
      name: t('settings.cat.controller'),
      items: [
        toggle('rumble', t('settings.controller.rumble'), t('settings.controller.rumbleDesc')),
        { kind: 'choice', label: t('settings.xbox.title'), desc: isLight(s) ? t('settings.controller.xboxLight') :t('settings.controller.xboxFull'), value: () => xboxModes(isLight(s)).find((m) => m.value === xboxMode(s))?.label, run: chooseXboxButton },
        ...expert(
          {
            kind: 'choice',
            label: t('settings.controller.hold'),
            desc: t('settings.controller.holdDesc'),
            value: () => holdOptions.find((o) => o.value === s.returnHoldMs)?.label ?? `${s.returnHoldMs} ms`,
            run: async () => {
              const ms = await chooseDialog(t('settings.controller.holdTitle'), holdOptions, s.returnHoldMs);
              if (ms !== undefined) await change('returnHoldMs', ms);
            }
          },
          toggle('pauseGameInMenu', t('settings.controller.pause'), t('settings.controller.pauseDesc')),
          toggle('bluetoothAutoOn', t('settings.controller.btAuto'), t('settings.controller.btAutoDesc')),
          info(t('settings.controller.input'), state.info.nativeInput ? t('settings.controller.inputNative') : t('settings.controller.inputBrowser'))
        ),
        info(t('settings.controller.keyboard'), t('settings.controller.keyboardDesc'))
      ]
    },
    ...(api.getParental ? [{ id: 'parental', icon: 'parental', name: t('settings.parental.title'), items: parentalItems() }] : []),
    {
      id: 'phone',
      icon: 'phone',
      name: t('settings.cat.phone'),
      items: [
        toggle('remoteEnabled', t('settings.phone.connect'), t('settings.phone.connectDesc')),
        ...(s.remoteEnabled ? [
          action(t('settings.phone.pair'), t('settings.phone.pairDesc'), () => openPage(pairingPage(), t('settings.cat.phone'))),
          ...expert(action(t('settings.phone.manage'), t('settings.phone.manageDesc'), manageRemoteDevices))
        ] : []),
        info(t('settings.phone.trouble'), t('settings.phone.troubleDesc'))
      ]
    },
    {
      id: 'news',
      icon: 'news',
      name: t('settings.cat.news'),
      items: [
        ...(feeds ?? []).map((f) => ({
          kind: 'action',
          label: f.name,
          desc: t('settings.news.feedDesc', { url: f.url }),
          icon: '✕',
          run: async () => {
            if (!(await confirmDialog(t('settings.news.removeConfirm', { name: f.name }), t('settings.common.remove'), { danger: true }))) return;
            feeds = (await attempt(() => api.removeFeed(f.url))) ?? feeds;
          }
        })),
        action(t('settings.news.add'), t('settings.news.addDesc'), async () => {
          const url = await openKeyboard({ title: t('settings.news.url'), value: 'https://', hint: t('settings.news.urlHint'), paste: true });
          if (!url || url === 'https://') return;
          const name = await openKeyboard({ title: t('settings.news.name'), placeholder: t('settings.news.namePlaceholder') });
          if (name === undefined) return;
          const updated = await attempt(() => api.addFeed(name, url), t('settings.news.notAdded'));
          if (updated) {
            feeds = updated;
            toast(t('settings.news.added'));
          }
        }, '+')
      ]
    },
    {
      id: 'home',
      icon: 'home',
      name: t('settings.cat.home'),
      items: [
        {
          kind: 'text',
          label: t('settings.home.place'),
          desc: t('settings.home.placeDesc'),
          value: () => s.weatherPlace?.name ?? t('settings.home.placeNone'),
          run: chooseWeatherPlace
        },
        ...expert(
          {
            kind: 'text',
            label: t('settings.home.haUrl'),
            desc: t('settings.home.haUrlDesc'),
            value: () => s.homeAssistantUrl || t('settings.common.notSet'),
            run: async () => {
              const url = await openKeyboard({ title: t('settings.home.haUrl'), value: s.homeAssistantUrl || 'http://homeassistant.local:8123', hint: t('settings.home.haUrlHint'), paste: true });
              if (url === undefined) return;
              // Another address asks for the token again (the host drops the old one).
              const norm = (u) => (u || '').trim().replace(/\/+$/, '').toLowerCase();
              const moved = norm(url) !== norm(s.homeAssistantUrl);
              await change('homeAssistantUrl', url, moved && s.homeAssistantTokenSet ? t('settings.home.tokenAgain') : undefined);
              if (moved) settings.homeAssistantTokenSet = false;
            }
          },
          {
            kind: 'text',
            label: t('settings.home.haToken'),
            desc: t('settings.home.haTokenDesc'),
            value: () => (s.homeAssistantTokenSet ? t('settings.common.set') : t('settings.common.notSet')),
            run: async () => {
              const token = await openKeyboard({ title: t('settings.home.haToken'), secret: true, hint: t('settings.home.haTokenHint'), paste: true });
              if (token === undefined) return;
              await change('homeAssistantToken', token, token ? t('settings.home.tokenSaved') : t('settings.home.tokenRemoved'));
              settings.homeAssistantTokenSet = !!token;
            }
          },
          action(t('settings.home.test'), t('settings.home.testDesc'), testHomeAssistant)
        )
      ]
    },
    {
      id: 'network',
      icon: 'network',
      name: t('settings.cat.network'),
      items: [
        action(t('settings.network.wifi'), t('settings.network.wifiDesc'), guarded(t('settings.network.wifi'), () => openPage(networkPage(), t('settings.cat.network')))),
        ...expert(
          action(t('settings.network.firewall'), t('settings.network.firewallDesc'), async () => {
            toast(t('settings.common.confirmWindowsPrompt'), 4000);
            const result = await attempt(() => api.allowFirewall(), t('settings.network.firewallFailed'));
            if (result === undefined) return;
            // Windows' own block rules for CouchHud.exe win against the allow rule: say so instead of "allowed".
            if (result?.blocked?.length) toast(t('settings.network.firewallBlocked', { names: result.blocked.join(', ') }), 12000);
            else toast(t('settings.network.firewallDone'));
          }),
          info(t('settings.network.rule'), t('settings.network.ruleDesc'))
        )
      ]
    },
    // CaseHUD (LCDs in fans and pumps): everything lives on its own page; only the install options are settings.
    ...(api.caseHudStatus ? [{
      id: 'casehud',
      icon: 'display',
      name: t('settings.cat.casehud'),
      badge: caseHudUpdateAvailable() || caseHudApiOff() ? 1 : 0,
      items: [
        action(t('settings.casehud.open'), caseHudUpdateAvailable() ? t('settings.casehud.update', { version: state.caseHud.update.version }) : caseHudApiOff() ? t('casehud.api.hint') : t('settings.casehud.openDesc'), () => openPage(caseHudPage(), t('settings.cat.casehud'))),
        ...expert(
          toggle('caseHudInstallAutostart', t('settings.casehud.autostart'), t('settings.casehud.autostartDesc')),
          toggle('caseHudInstallFps', t('settings.casehud.fps'), t('settings.casehud.fpsDesc')),
          toggle('caseHudInstallApi', t('settings.casehud.api'), t('settings.casehud.apiDesc')),
          toggle('caseHudGamingLease', t('settings.casehud.lease'), t('settings.casehud.leaseDesc'))
        )
      ]
    }] : []),
    {
      id: 'notifications',
      icon: 'bell',
      name: t('settings.cat.notifications'),
      items: [
        toggle('mirrorNotifications', t('settings.notifications.show'), t('settings.notifications.showDesc')),
        ...expert(info(t('settings.parental.status'), ({ allowed: t('settings.notifications.allowed'), denied: t('settings.notifications.denied'), unavailable: t('settings.notifications.unavailable'), unspecified: t('settings.notifications.unspecified'), off: t('settings.common.off') })[s.notificationStatus] ?? t('settings.common.off')))
      ]
    },
    {
      id: 'updates',
      icon: 'update',
      name: t('settings.cat.updates'),
      badge: updateCount(),
      items: [
        action(t('settings.updates.open'), updateCount() ? t('settings.updates.available', { count: updateCount() }) : t('settings.updates.openDesc'), () => openPage(updatesPage(), t('settings.cat.updates'))),
        info(t('settings.updates.auto'), t('settings.updates.autoDesc')),
        toggle('selfUpdateCheck', t('settings.updates.self'), t('settings.updates.selfDesc'))
      ]
    },
    {
      id: 'power',
      icon: 'power',
      name: t('settings.cat.power'),
      items: [
        standby,
        pcMode,
        restart,
        ...expert(restartPc),
        shutdown,
        { kind: 'choice', label: t('settings.power.timer'), desc: t('settings.power.timerDesc'), value: () => sleepTimerDetail(), run: chooseSleepTimer }
      ]
    },
    {
      id: 'info',
      icon: 'info',
      name: t('settings.cat.info'),
      items: [
        {
          kind: 'choice',
          label: t('settings.language.title'),
          desc: t('settings.language.desc'),
          value: () => ({ auto: t('settings.language.auto'), de: 'Deutsch', en: 'English' })[s.language ?? 'auto'] ?? t('settings.language.auto'),
          run: async () => {
            const current = s.language ?? 'auto';
            const v = await chooseDialog(t('settings.language.title'), [
              { label: t('settings.language.auto'), detail: t('settings.language.autoDetail'), value: 'auto' },
              { label: 'Deutsch', value: 'de' },
              { label: 'English', value: 'en' }
            ], current);
            if (v === undefined || v === current) return;
            if ((await saveSetting('language', v)) === undefined) return;
            try { sessionStorage.setItem('couchhud.resume', 'settings:info'); } catch { /* storage blocked: reload lands on Home */ }
            location.reload();
          }
        },
        action(t('settings.info.wizard'), t('settings.info.wizardDesc'), openSetupWizard, '✦'),
        action(t('settings.info.disclaimer'), t('settings.info.disclaimerDesc'), () => openPage(disclaimerPage(), t('settings.cat.info'))),
        action(t('settings.info.privacy'), t('settings.info.privacyDesc'), () => openPage(privacyPage(), t('settings.cat.info'))),
        ...(state.info.isShell ? [] : [action(t('settings.info.quit'), t('settings.info.quitDesc'), guarded(t('settings.info.quit'), () => attempt(() => api.quit())), '✕', t('settings.hint.quit'))]),
        info(t('settings.info.guide'), t('settings.info.guideDesc')),
        info(t('settings.info.version'), state.info.version || '–'),
        info(t('settings.info.license'), 'GPL-3.0 · © 2026 kewe87'),
        ...expert(
          info(t('settings.info.mode'), state.info.isShell ? t('settings.info.modeShell') : t('settings.info.modeApp')),
          info(t('settings.info.config'), s.configPath || '–'),
          info(t('settings.info.logs'), s.logPath || '–')
        )
      ]
    },
    {
      id: 'expert',
      icon: 'expert',
      name: t('settings.cat.expert'),
      items: [
        toggle('expertMode', t('settings.cat.expert'), t('settings.expert.desc')),
        s.expertMode
          ? info(t('settings.expert.visible'), t('settings.expert.visibleAll'))
          : info(t('settings.expert.visible'), t('settings.expert.visibleSimple'))
      ]
    },
    { id: 'windows', icon: 'shield', name: t('settings.cat.windows'), expertOnly: true, items: windowsItems(s) },
    { id: 'startup', icon: 'startup', name: t('settings.cat.startup'), expertOnly: true, items: startupItems(s) },
    { id: 'ai', icon: 'ai', name: t('settings.cat.ai'), expertOnly: true, items: aiItems(s) },
    {
      id: 'plugins',
      icon: 'plugin',
      name: t('settings.cat.plugins'),
      expertOnly: true,
      items: [
        action(t('settings.plugins.manage'), t('settings.plugins.manageDesc'), () => openPage(pluginsPage(), t('settings.cat.plugins'))),
        info(t('settings.plugins.own'), t('settings.plugins.ownDesc'))
      ]
    }
  ];
}

/** The categories for the current mode, with expert sections hidden or shown. */
function visibleCategories() {
  const on = !!settings?.expertMode;
  return categories()
    .filter((c) => on || !c.expertOnly)
    // Without full integration this category would only hold a note; the Windows category explains it.
    .filter((c) => c.id !== 'startup' || (settings?.integrationMode ?? 'light') === 'full')
    .map((c) => ({ ...c, items: c.items.filter((i) => on || !i.expert) }));
}

const providers = () => [
  { label: t('settings.common.off'), value: '' },
  { label: 'Anthropic', detail: t('settings.ai.claude'), value: 'anthropic' },
  { label: 'OpenAI', detail: t('settings.ai.gpt'), value: 'openai' }
];

function aiItems(s) {
  const provider = s.aiProvider ?? '';
  const keySetting = provider === 'openai' ? 'openAiApiKey' : 'anthropicApiKey';
  const keySet = provider === 'openai' ? s.openAiKeySet : s.anthropicKeySet;
  const models = s.aiModels?.[provider] ?? [];
  const items = [
    {
      kind: 'choice',
      label: t('settings.ai.provider'),
      desc: t('settings.ai.providerDesc'),
      value: () => providers().find((p) => p.value === provider)?.label ?? t('settings.common.off'),
      run: async () => {
        const chosen = await chooseDialog(t('settings.ai.provider'), providers(), provider);
        if (chosen === undefined) return;
        await change('aiProvider', chosen);
        settings.aiModel = '';
        await refreshAssistantTab();
      }
    }
  ];
  if (!provider) return [...items, ...mcpItems(s)];
  items.push(
    {
      kind: 'text',
      label: t('settings.ai.key'),
      desc: provider === 'openai' ? t('settings.ai.keyOpenAi') : t('settings.ai.keyAnthropic'),
      value: () => (keySet ? t('settings.ai.keySaved') : t('settings.common.notSet')),
      run: async () => {
        const key = await openKeyboard({ title: t('settings.ai.key'), secret: true, hint: t('settings.ai.keyHint'), paste: true });
        if (key === undefined) return;
        await change(keySetting, key, key ? t('settings.ai.keySavedToast') : t('settings.ai.keyRemovedToast'));
        settings[provider === 'openai' ? 'openAiKeySet' : 'anthropicKeySet'] = !!key;
        await refreshAssistantTab();
      }
    },
    {
      kind: 'choice',
      label: t('settings.ai.model'),
      desc: t('settings.ai.modelDesc'),
      value: () => s.aiModel || models[0] || '–',
      run: async () => {
        const options = [...models.map((m) => ({ label: m, value: m })), { label: t('settings.ai.customModel'), value: '__custom' }];
        let model = await chooseDialog(t('settings.ai.model'), options, s.aiModel || models[0]);
        if (model === '__custom') model = await openKeyboard({ title: t('settings.ai.modelId'), value: s.aiModel, paste: true });
        if (model !== undefined) await change('aiModel', model);
      }
    }
  );
  return [...items, ...mcpItems(s)];
}

function mcpItems(s) {
  const items = [
    toggle('mcpEnabled', t('settings.mcp.server'), t('settings.mcp.serverDesc'), () => loadSettings()),
  ];
  if (!s.mcpEnabled) return items;
  items.push(
    toggle('mcpLan', t('settings.mcp.lan'), t('settings.mcp.lanDesc'), () => loadSettings()),
    info(t('settings.mcp.address'), (s.mcpUrls ?? []).join('  ·  ') || '–'),
    { kind: 'info', label: t('settings.mcp.token'), value: s.mcpToken || '–', secret: true },
    action(t('settings.mcp.newToken'), t('settings.mcp.newTokenDesc'), async () => {
      if (!(await confirmDialog(t('settings.mcp.newTokenConfirm'), t('settings.mcp.create')))) return;
      await change('mcpToken', 'new', t('settings.mcp.created'));
      await loadSettings();
    }, '⟳')
  );
  return items;
}

async function refreshAssistantTab() {
  await loadSettings();
  const info = await attempt(() => api.systemInfo());
  if (info) state.info.assistant = info.assistant;
  window.dispatchEvent(new Event('couchhud:tabs'));
  render();
}

/** Like chooseDialog, but moving through the list plays each sound set. */
function chooseOverlayWithPreview(title, options, current) {
  return new Promise((resolve) => {
    openOverlay({
      title,
      index: Math.max(0, options.findIndex((o) => o.value === current)),
      onCancel: () => resolve(undefined),
      onMove: (i) => sound.preview(options[i].value || state.skin.id),
      entries: options.map((o) => ({ label: o.label, run: () => resolve(o.value) }))
    });
  });
}

async function chooseWeatherPlace() {
  const query = await openKeyboard({ title: t('settings.weather.search'), placeholder: t('settings.weather.placeholder') });
  if (!query) return;
  const places = await attempt(() => api.searchPlaces(query), t('settings.weather.searchFailed'));
  if (!places) return;
  const options = [
    ...places.map((p) => ({ label: p.name, detail: p.region, value: p })),
    { label: t('settings.weather.hide'), value: null }
  ];
  const chosen = await chooseDialog(places.length ? t('settings.weather.which') : t('settings.weather.none'), options);
  if (chosen === undefined) return;
  await change('weatherLocation', chosen, chosen ? t('settings.weather.setFor', { name: chosen.name }) : t('settings.weather.hidden'));
  settings.weatherPlace = chosen;
  await refreshWeather();
}

async function testHomeAssistant() {
  const entities = await attempt(() => api.getHomeEntities(), t('settings.home.noConnection'));
  if (!entities) return;
  toast(t('settings.home.connected', { count: entities.length }), 4000);
  state.info.homeAssistant = true;
  await preloadSmartHome(settings);
  const tabs = tabViews();
  if (!tabs.includes(smartHomeView)) setTabs([...tabs.slice(0, -1), smartHomeView, tabs.at(-1)]);
  render();
}

function valueLabel(item) {
  switch (item.kind) {
    case 'toggle': return el('span', { className: `pill ${item.value() ? 'on' : 'off'}`, textContent: item.value() ? t('settings.common.on') : t('settings.common.off') });
    case 'choice':
    case 'text': return el('span', { className: 'setting-value', textContent: `${item.value()} ›` });
    case 'action': return el('span', { className: 'setting-value' }, iconEl(item.icon));
    default: return null;
  }
}

const divider = (text) => el('li', { className: 'settings-divider', textContent: text });

export const settingsView = {
  get name() {
    return t('settings.tab');
  },
  id: 'settings',
  index: 0,
  itemIndex: 0,
  pane: 'cats',
  catId: 'console',
  onEnter() {
    this.pane = 'cats';
    settings = null; // reload, e.g. after the shell state changed elsewhere
  },
  // B in the right pane goes back to the categories instead of leaving the tab.
  captures(action) {
    return action === 'b' && this.pane === 'items';
  },
  build(root) {
    if (!settings) loadSettings().then(() => state.stack.length === 0 && currentView() === this && render());
    this.cats = visibleCategories();
    this.syncCategory();
    this.catList = null;
    this.itemPane = el('div', { className: 'settings-items' });
    this.center = el('div', { className: `settings-center pane-${this.pane}` }, this.itemPane);
    root.append(this.center);
    setBackground({ id: '__settings', name: t('settings.tab'), art: {} });
    this.buildCats();
    this.buildItems();
    this.update({ instant: true });
  },
  /** Keeps the same category selected when the list of categories changes (expert mode). */
  syncCategory() {
    const found = this.cats.findIndex((c) => c.id === this.catId);
    this.index = found >= 0 ? found : Math.min(this.index, this.cats.length - 1);
    this.catId = this.cats[this.index].id;
  },
  buildCats() {
    this.catRows = [];
    const nodes = [];
    this.cats.forEach((c, i) => {
      if (c.expertOnly && !this.cats[i - 1]?.expertOnly) nodes.push(divider(t('settings.expertDivider')));
      const row = el('li', { className: c.expertOnly ? 'expert-cat' : '' },
        el('span', { className: 'icon' }, categoryIcon(c.icon)),
        el('strong', { textContent: c.name }),
        c.badge ? el('span', { className: 'badge', textContent: c.badge }) : null);
      this.catRows.push(row);
      nodes.push(row);
    });
    const list = el('ul', { className: 'settings-cats list-scroll' }, nodes);
    if (this.catList?.isConnected) this.catList.replaceWith(list);
    else this.center.prepend(list);
    this.catList = list;
  },
  buildItems() {
    const cat = this.cats[this.index];
    this.catId = cat.id;
    // Everyday items first, expert items after the divider, whatever order the category lists them in.
    const focusable = cat.items.filter((i) => i.kind !== 'info');
    this.items = [...focusable.filter((i) => !i.expert), ...focusable.filter((i) => i.expert)];
    const facts = cat.items.filter((i) => i.kind === 'info');
    this.itemIndex = Math.max(0, Math.min(this.itemIndex, this.items.length - 1));
    this.itemRows = [];
    const nodes = [];
    let expertShown = cat.expertOnly;
    for (const item of this.items) {
      if (item.expert && !expertShown) {
        expertShown = true;
        nodes.push(divider(t('settings.expertDivider')));
      }
      const row = el('li', { className: `setting setting-${item.kind}${item.primary ? ' setting-primary' : ''}` },
        el('div', { className: 'setting-text' },
          el('strong', { textContent: item.label }),
          el('p', { textContent: item.desc })),
        valueLabel(item));
      this.itemRows.push(row);
      nodes.push(row);
    }
    this.itemScroller = el('div', { className: 'settings-scroll list-scroll' },
      el('ul', { className: 'settings' }, nodes),
      facts.length ? (this.factsNode = el('dl', { className: `settings-facts${this.items.length ? ' after-items' : ''}` }, facts.map((f) => el('div', { className: 'fact' },
        el('dt', { textContent: f.label }),
        el('dd', { className: f.secret ? 'setting-secret' : '', textContent: f.value })))) ) : (this.factsNode = null));
    this.itemPane.replaceChildren(el('h2', { className: 'settings-heading', textContent: cat.name }), this.itemScroller);
  },
  /** Fades the lower edge of the category list while more categories wait below the visible part. */
  markMore() {
    const list = this.catList;
    if (!list) return;
    const check = () => list.classList.toggle('more-below', list.scrollTop + list.clientHeight < list.scrollHeight - 8);
    if (!list.dataset.moreBound) {
      list.dataset.moreBound = '1';
      list.addEventListener('scroll', check, { passive: true });
    }
    check();
    setTimeout(check, 350);
  },
  /** Rebuilds categories and items in place after a value changed. */
  refresh() {
    this.cats = visibleCategories();
    this.syncCategory();
    this.buildCats();
    this.buildItems();
    this.update({ instant: true });
  },
  update({ instant = false } = {}) {
    renderHints();
    setFocused(this.catRows, this.index);
    keepInView(this.catList, this.catRows[this.index], { instant });
    this.catList.classList.toggle('inactive', this.pane === 'items');
    this.markMore();
    this.center.className = `settings-center pane-${this.pane}`;
    if (this.pane === 'items') {
      setFocused(this.itemRows, this.itemIndex);
      // On the last row the notes below it must come into view too, they cannot be focused themselves.
      const last = this.itemIndex === this.itemRows.length - 1 && this.factsNode;
      keepInView(this.itemScroller, last ? this.factsNode : this.itemRows[this.itemIndex], { instant });
    } else {
      setFocused(this.itemRows, -1);
    }
  },
  async handle(action) {
    if (settings && (await eggInput(action, this.catId))) return this.refresh();
    if (this.pane === 'cats') {
      switch (action) {
        case 'up':
        case 'down':
          if (move(this, action === 'up' ? -1 : 1, this.cats.length, true)) {
            this.itemIndex = 0;
            this.buildItems();
            this.update({ instant: true });
          }
          return;
        case 'right':
        case 'a':
          if (!this.items.length) return sound.bump();
          this.pane = 'items';
          sound.select();
          return this.update();
      }
      return;
    }
    switch (action) {
      case 'up':
      case 'down': {
        const next = this.itemIndex + (action === 'up' ? -1 : 1);
        if (next < 0 || next >= this.items.length) return sound.bump();
        this.itemIndex = next;
        sound.move();
        return this.update();
      }
      case 'left':
      case 'b':
        this.pane = 'cats';
        sound.back();
        return this.update();
      case 'a': {
        const item = this.items[this.itemIndex];
        if (!item?.run) return sound.bump();
        sound.select();
        await attempt(() => item.run());
        // Values may have changed (or expert mode toggled); rebuild in place.
        if (state.stack.length === 0 && this.itemPane?.isConnected) this.refresh();
      }
    }
  },
  pointer(node, index, dispatch) {
    if (node.closest('.settings-cats')) {
      const i = this.catRows.indexOf(node);
      if (i < 0) return;
      this.pane = 'cats';
      if (i !== this.index) {
        this.index = i;
        this.itemIndex = 0;
        this.buildItems();
      }
      return this.update({ instant: true });
    }
    const i = this.itemRows.indexOf(node);
    if (i < 0) return;
    this.pane = 'items';
    this.itemIndex = i;
    this.update();
    dispatch('a');
  },
  hints() {
    if (this.pane === 'items') {
      const kind = this.items[this.itemIndex]?.kind;
      const verb = kind === 'toggle' ? t('settings.hint.toggle') : kind === 'action' ? (this.items[this.itemIndex].hint ?? t('settings.hint.open')) : t('settings.hint.change');
      return [['A', verb], ['B', t('settings.hint.categories')]];
    }
    return this.items?.length ? [['A', t('settings.hint.open')], ['B', t('settings.hint.home')]] : [['B', t('settings.hint.home')]];
  }
};
