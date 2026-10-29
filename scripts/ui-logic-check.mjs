// Pure UI rules with a test: node scripts/ui-logic-check.mjs (run by check.sh).
import assert from 'node:assert/strict';
import { clampPaste, remoteView } from '../ui/core/paste.js';
import { CODE_KEYS, holdsDialogs, isCodeEntry, padHelp, shouldShowCodeKeys, switchOffWarning } from '../ui/core/input-help.js';
import { step } from '../ui/core/paging.js';
import { POWER_GROUPS, SLEEP_TIMER, forMode as powerForMode, isDangerous, powerGroups, powerIndex } from '../ui/core/power-rules.js';
import { DEFAULT_TIMER_ACTION, TIMER_ACTIONS, TIMER_WARN_MS, timerAction, timerTexts, timerUnlock, warnDelay } from '../ui/core/sleep-timer-rules.js';
import { gridMove, groupRows } from '../ui/core/menu-grid.js';
import { afterRun, closeAction, recoveryChoices, createModeTransaction, describeRollback, describeTargets, manualUndo, parseJournal, validBefore } from '../ui/core/mode-transaction.js';
import { createUndoTables } from '../ui/core/mode-undo.js';
import { applyRollbackToRows, createStepShow, rollbackOutcome, rollbackRowView, runRows, SATISFIED } from '../ui/core/mode-run.js';
import { withTimeout } from '../ui/core/input-help.js';
import { admitProtectedChange, beginSetupHold, codeLockHeld, codeLockPlan, configFaultNow, lockStepStatus, setupCouchLock } from '../ui/core/lock-rules.js';
import { admitInputSwitch, pairOutcome } from '../ui/core/tv-rules.js';
import { CONTINUE_LIMIT, admitSaveChange, continueFacts, continuePlaying, continueDays, emptyHint, homeShelf, shelfHeading } from '../ui/core/continue-rules.js';
import { downloadActions, itemSize, pendingDownloads, updateSummary, validSize } from '../ui/core/update-rules.js';
import { TARGET_EXTENSIONS, isScript, launchRows, scriptArgsProblem, targetLabel } from '../ui/core/launch-rules.js';
import { INFO_FIELDS, STORE_RETRY_MS, ageBadge, ageRating, badgeText, descriptionState, fieldText, infoProblem, loadTickets, queryOk, splitList, storeRetryDelay, storeRows } from '../ui/core/item-info-rules.js';
import { LOW_PERCENT, bars, batteryEntries, padSteps, percentSteps, systemBadge, validPercent } from '../ui/core/battery-rules.js';
import { TAB_EDGE_SLOP, TAB_MIN_RANGE, TAB_MIN_REM, fitWidths, marqueeOffset, minTabWidth, needsMarquee, overflowHints, tabWindow } from '../ui/core/tab-fit.js';
import { tabProblems } from './skin-rules.mjs';
import { barStops, chipsBlocked, nextStop } from '../ui/core/tab-cycle.js';
import { notificationTarget } from '../ui/core/notify-rules.js';
import { searchMessage, searchProblems, searchSucceeded } from '../ui/core/update-search.js';
import { changeReporter, codeEntryTracker, PAIRING_SELECTOR, REPORT_REFRESH_MS, REPORT_RETRY_MS, shotToast } from '../ui/core/screenshot-rules.js';
import { RATING_CHOICES, activeSystem, ageChoices, ageLabels, convertLimit, limitLabel, limitText, ratingSteps, shownLimit } from '../ui/core/age-rating-rules.js';
import { GAME_LIMITS_MB, TOTAL_LIMITS_MB, formatBytes, limitChoices } from '../ui/core/size.js';
import { readFileSync, readdirSync } from 'node:fs';
import { MOVED_SKINS, RENAMED_SKINS, migrateSkinId, migrateSoundSetId, movedSkinCatalogId } from '../ui/core/skin-rules.js';
import * as pr from '../ui/core/plugin-rules.js';
import * as ptabs from '../ui/core/plugin-tabs.js';
import deCore from '../ui/i18n/de/core.js';
import enCore from '../ui/i18n/en/core.js';
import { AREAS, AREA_DESC_KEYS, AREA_NAME_KEYS, LAUNCH_AREAS, SETTING_AREAS, areaOfSetting, areaOn, enabledAreas, setAreas, shownRows, shownSteps, shownTabs, toggleArea, validChoice } from '../ui/core/areas.js';
import { NET_KEYS, privacyAfter, privacyDisabled, privacyDisabledReason, privacyShown, updateChoices } from '../ui/core/net-rules.js';
import { PAD_GLYPH_SETTINGS, glyphFor, glyphKind, isPlayStationLabel, kindFromGamepadId, kindFromPresses, MIRROR_WINDOW_MS } from '../ui/core/pad-glyphs.js';
import { HISTORY_OFF_ACTION, HOLD_LIMIT, RECENT_LIMIT, cleanRecent, closeCountdown, dialogKind, fileSize, forgetRecent, forgetSite, historyOffNeedsParent, holding, host, initial, reconcileFocus, recentNote, rememberable, rememberSite, recentWithin, shortHost, siteAddress, siteHost, visibleRecent } from '../ui/core/browser-logic.js';

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
// The couch browser: site names, recent sites, sizes, the controller's place and the page's questions.
assert.equal(host('https://www.example.org/a?b'), 'example.org');
assert.equal(host('http://localhost:3000/'), 'localhost:3000');
assert.equal(host('kein link'), 'kein link');
assert.equal(host(undefined), '');
assert.equal(initial('https://de.wikipedia.org/wiki/X'), 'W');
assert.equal(initial('https://example.com/'), 'E');
assert.equal(initial(''), '?');

const r0 = [];
const r1 = rememberSite(r0, 'https://a.example/', 'A');
assert.deepEqual(r1, [{ url: 'https://a.example/', title: 'A' }]);
assert.equal(rememberSite(r1, 'https://a.example/', 'A'), r1, 'the same visit again changes nothing (no storage write)');
assert.deepEqual(forgetSite(r1, 'https://a.example/'), [], 'a removed site leaves the recent list');
assert.equal(forgetSite(r1, 'https://b.example/'), r1, 'removing a site that is not there changes nothing (no storage write)');
assert.equal(rememberSite(r1, 'about:blank', 'x'), r1);
assert.equal(rememberSite(r1, 'data:text/html,x', 'x'), r1);
assert.equal(rememberSite(r1, 'javascript:alert(1)', 'x'), r1);
assert.equal(rememberSite(r1, undefined, 'x'), r1);
assert.deepEqual(rememberSite(r1, 'https://a.example/', ''), [{ url: 'https://a.example/', title: 'a.example' }]);
let many = r0;
for (let i = 0; i < 10; i++) many = rememberSite(many, `https://s${i}.example/`, `S${i}`);
assert.equal(many.length, 6);
assert.equal(many[0].url, 'https://s9.example/');
many = rememberSite(many, 'https://s5.example/', 'S5');
assert.equal(many[0].url, 'https://s5.example/');
assert.equal(many.filter((r) => r.url === 'https://s5.example/').length, 1);

// Recent sites keep origin plus path only: no query (tokens, codes, search terms), no fragment, no user name or password.
assert.equal(siteAddress('https://a.example/p?token=secret#x'), 'https://a.example/p');
assert.equal(siteAddress('https://user:pw@A.Example:8443/p/q?x=1'), 'https://a.example:8443/p/q');
assert.equal(siteAddress('http://a.example'), 'http://a.example/');
assert.equal(siteAddress('ftp://a.example/'), null);
assert.equal(siteAddress('javascript:alert(1)'), null);
assert.equal(siteAddress('no address'), null);
assert.equal(siteAddress(undefined), null);
const secret = rememberSite([], 'https://a.example/reset?token=abc123#code=xyz', 'Reset');
assert.deepEqual(secret, [{ url: 'https://a.example/reset', title: 'Reset' }]);
assert.ok(!JSON.stringify(secret).includes('abc123') && !JSON.stringify(secret).includes('xyz'), 'no query, no fragment stored');
assert.ok(!JSON.stringify(rememberSite([], 'https://me:hunter2@a.example/', 'A')).includes('hunter2'), 'no password stored');
// A page that rewrites its address with history.pushState (query and fragment) stays one entry, the newest visit on top.
let spa = rememberSite(seenBefore(), 'https://spa.example/watch?v=1', 'One');
for (let i = 2; i <= 40; i++) spa = rememberSite(spa, `https://spa.example/watch?v=${i}#t=${i}`, `Video ${i}`);
assert.equal(spa.filter((r) => r.url === 'https://spa.example/watch').length, 1, 'one entry per origin plus path');
assert.deepEqual(spa[0], { url: 'https://spa.example/watch', title: 'Video 40' }, 'a new visit replaces the old entry');
assert.equal(spa.length, 3, 'the other sites stay');
assert.equal(rememberSite(spa, 'https://spa.example/watch?v=40#again', 'Video 40'), spa, 'same page, same title: no storage write');
// Another path of the same origin is another entry; the list stays bounded.
let paths = [];
for (let i = 0; i < 30; i++) paths = rememberSite(paths, `https://spa.example/page/${i}?q=${i}`, `P${i}`);
assert.equal(paths.length, RECENT_LIMIT);
assert.equal(new Set(paths.map((r) => r.url)).size, RECENT_LIMIT);
function seenBefore() {
  return [{ url: 'https://b.example/', title: 'B' }, { url: 'https://c.example/', title: 'C' }];
}
// A list stored by an older version (full addresses, duplicates, junk) is cleaned on load by the same rules.
const old = [
  { url: 'https://spa.example/watch?v=9&token=t1', title: 'Newest' },
  { url: 'https://spa.example/watch?v=8', title: 'Older' },
  { url: 'https://b.example/#frag', title: 'B' },
  { url: 'javascript:alert(1)', title: 'x' },
  { title: 'no url' },
  null,
  { url: 'https://c.example/', title: 42 }
];
const cleaned = cleanRecent(old);
assert.deepEqual(cleaned, [
  { url: 'https://spa.example/watch', title: 'Newest' },
  { url: 'https://b.example/', title: 'B' },
  { url: 'https://c.example/', title: 'c.example' }
]);
assert.ok(!JSON.stringify(cleaned).includes('token'));
assert.deepEqual(cleanRecent('nonsense'), []);
assert.deepEqual(cleanRecent(cleaned), cleaned, 'a clean list stays as it is');
assert.equal(cleanRecent(Array.from({ length: 100 }, (_, i) => ({ url: `https://s${i}.example/`, title: 'S' }))).length, RECENT_LIMIT);

// Signing in to Home Assistant: its sign-in page never goes into the recent sites.
assert.equal(rememberable({ open: true, url: 'https://a.example/' }), true);
assert.equal(rememberable({ open: true, url: 'http://ha.local:8123/auth/authorize', signIn: true }), false);
assert.equal(rememberable({ open: false }), false);
assert.equal(rememberable(null), false);

// History off with parental controls is the parents' (audit 0.5 M1): only switching it off asks; the same action name as in Core.
assert.equal(historyOffNeedsParent(true, true, false), true);
assert.equal(historyOffNeedsParent(true, undefined, false), true, 'unknown counts as on (the default)');
assert.equal(historyOffNeedsParent(true, false, true), false, 'switching it on is free');
assert.equal(historyOffNeedsParent(true, false, false), false);
assert.equal(historyOffNeedsParent(false, true, false), false);
assert.equal(historyOffNeedsParent(undefined, true, false), false, 'the host decides when the status is unknown');
assert.ok(readFileSync(new URL('../src/CouchHud.Core/Security/BrowserRecentPrivacy.cs', import.meta.url), 'utf8').includes(`HistoryOffAction = "${HISTORY_OFF_ACTION}"`));
// The remembered sites go only on the host's own "history off", never on a switch it refused.
assert.equal(forgetRecent({ history: false }), true);
assert.equal(forgetRecent({ history: true }), false);
assert.equal(forgetRecent({}), false);
assert.equal(forgetRecent(null), false);

// A child at the locked couch sees no browsing history: only an explicit "open" from the host shows the recent sites.
const seen =[{ url: 'https://a.example/', title: 'A' }, { url: 'https://b.example/', title: 'B' }];
assert.deepEqual(visibleRecent(seen, { locked: true, bookmarks: [] }), []);
assert.deepEqual(visibleRecent(seen, { bookmarks: [] }), [], 'no lock state (old host) hides them');
assert.deepEqual(visibleRecent(seen, null), [], 'no answer yet hides them');
assert.deepEqual(visibleRecent(seen, { locked: 'false', recentHidden: false }), [], 'only the boolean false opens');
assert.deepEqual(visibleRecent(seen, { locked: false, recentHidden: false, bookmarks: [{ url: 'https://a.example/' }] }), [seen[1]], 'bookmarks are not repeated');
assert.equal(visibleRecent(many, { locked: false, recentHidden: false }).length, 4);
// Parental controls hide them at the open couch too, until the parents' code; fail-closed on anything unknown.
assert.deepEqual(visibleRecent(seen, { locked: false, recentHidden: true, recentNeedsParent: true }), [], 'parental controls hide them');
assert.deepEqual(visibleRecent(seen, { locked: false }), [], 'open couch, but no recentHidden (old host): hidden');
assert.deepEqual(visibleRecent(seen, { locked: false, recentHidden: 'false' }), [], 'only the boolean false shows');
assert.deepEqual(visibleRecent(seen, { locked: true, recentHidden: false }), [], 'the locked couch hides them whatever else says');
assert.equal(recentNote({ locked: false, recentHidden: true, recentNeedsParent: true }, true), 'parent', 'parental controls: the parents code');
assert.equal(recentNote({ locked: true, recentHidden: true, recentNeedsParent: false }, true), 'lock', 'locked couch: the couch code');
assert.equal(recentNote({ locked: true, recentHidden: true, recentNeedsParent: true }, true), 'lock', 'locked wins over parental controls');
assert.equal(recentNote(null, true), 'lock', 'no answer yet: the lock note');
assert.equal(recentNote({ locked: false }, true), 'lock', 'unknown: the lock note, never the list');
assert.equal(recentNote({ locked: false, recentHidden: false }, true), null, 'shown: no note');
assert.equal(recentNote({ locked: false, recentHidden: true, recentNeedsParent: true }, false), null, 'nothing to hide: no note');

assert.equal(fileSize(0), '');
assert.equal(fileSize(undefined), '');
assert.equal(fileSize(-5), '');
assert.equal(fileSize(512, 'en'), '512 B');
assert.equal(fileSize(1536, 'en'), '1.5 KB');
assert.equal(fileSize(5_000_000, 'en'), '5 MB');
assert.equal(fileSize(3 * 1000 ** 4, 'en'), '3 TB');

// One size format everywhere (downloads, updates, library, saves): decimal units like Windows Update and Steam, never MiB labelled MB.
assert.equal(formatBytes(5 * 1024 * 1024, 'en'), '5.2 MB');
assert.equal(formatBytes(52_428_800_000, 'en'), '52 GB');
assert.equal(formatBytes(1_500_000_000, 'de-DE'), '1,5 GB');
assert.equal(formatBytes(999_990, 'en'), '1 MB');
assert.equal(formatBytes(999, 'en'), '999 B');
assert.equal(formatBytes(12_300_000, 'en'), '12 MB');
assert.equal(formatBytes(9_960_000, 'en'), '10 MB');
for (const unknown of [0, -1, NaN, Infinity, null, undefined, '5000']) assert.equal(formatBytes(unknown, 'en'), null);

assert.equal(reconcileFocus('page', { requested: true, granted: false }), 'bar', 'a refused page focus goes back to the bar');
assert.equal(reconcileFocus('page', { requested: true, granted: true }), 'page');
assert.equal(reconcileFocus('bar', { requested: false, granted: false }), 'bar');
assert.equal(reconcileFocus('page', { requested: true, granted: undefined }), 'page', 'an old host without an answer changes nothing');

assert.equal(dialogKind({ id: 1, kind: 'alert' }), 'alert');
assert.equal(dialogKind({ id: 1, kind: 'download' }), 'download');
assert.equal(dialogKind({ id: 1, closed: true }), 'closed');
assert.equal(dialogKind({ id: 1, kind: 'file' }), null);
assert.equal(dialogKind('alert'), null);
assert.equal(dialogKind(null), null);

// The screensaver over a playing page: held, but never longer than 3 hours after the last input.
assert.equal(holding(true, 1000, 0), true);
assert.equal(holding(false, 1000, 0), false);
assert.equal(holding(true, HOLD_LIMIT + 1, 0), false);
assert.equal(holding(true, HOLD_LIMIT - 1, 0), true);
assert.equal(HOLD_LIMIT, 3 * 60 * 60_000);

// The host in the full-screen strip: at most 48 characters, cut in the middle, the end of the domain stays.
assert.equal(shortHost('www.example.com'), 'www.example.com');
assert.equal(shortHost('a'.repeat(48)), 'a'.repeat(48), 'exactly the limit: unchanged');
{
  const long = `${'sub.'.repeat(30)}bank-example.com`;
  const cut = shortHost(long);
  assert.equal([...cut].length, 48);
  assert.ok(cut.includes('…'));
  assert.ok(cut.endsWith('bank-example.com'), 'the end of the domain stays visible');
  assert.ok(cut.startsWith('sub.'), 'the start stays too');
}
assert.equal([...shortHost('ä'.repeat(100), 10)].length, 10, 'counts characters, not UTF-16 units');
assert.equal(shortHost('xn--' + 'b'.repeat(80) + '.de', 20).slice(-4), 'b.de');
assert.equal(shortHost(null), '');
assert.equal(shortHost(undefined), '');

// The second line of a recent site: no "www.", a very long host cut to one line with the end of the domain kept.
assert.equal(siteHost('https://www.example.com/a?b'), 'example.com');
{
  const cut = siteHost(`https://${'very-long-label.'.repeat(12)}example.org/path`);
  assert.equal([...cut].length, 48);
  assert.ok(cut.endsWith('example.org'));
  assert.ok(!cut.includes('/'), 'only the host, never the path');
}
assert.equal(siteHost('not a url'), 'not a url');

// The "browser closes soon" countdown in the full-screen strip: from the host's number, on the HUD's clock, gone without a warning.
assert.equal(closeCountdown({ closesInMs: 30_000 }, 1000, 1000), 30);
assert.equal(closeCountdown({ closesInMs: 30_000 }, 1000, 11_500), 20, 'counts down between two states');
assert.equal(closeCountdown({ closesInMs: 29_001 }, 0, 0), 30, 'whole seconds, rounded up');
assert.equal(closeCountdown({ closesInMs: 5000 }, 0, 60_000), 0, 'never below 0');
assert.equal(closeCountdown({ closesInMs: 5000 }, 1000, 0), 5, 'a clock going back does not add time');
assert.equal(closeCountdown({ closesInMs: null }, 0, 0), null, 'extended: the warning is gone');
assert.equal(closeCountdown({}, 0, 0), null, 'an old host without the field: none');
assert.equal(closeCountdown(null, 0, 0), null);
assert.equal(closeCountdown({ closesInMs: '30000' }, 0, 0), null, 'only a number');
assert.equal(closeCountdown({ closesInMs: 0 }, 0, 0), null);
assert.equal(closeCountdown({ closesInMs: Infinity }, 0, 0), null);

// Paged list of games that are not installed (pages of 10 here, 7 columns): steps stay on the page, cross to the next or previous
// one, land on the last item of a short last page, and stop at both ends.
assert.deepEqual(step({ index: 2, offset: 0, count: 10, total: 25 }, 1, 10), { index: 3, offset: 0 });
assert.deepEqual(step({ index: 8, offset: 0, count: 10, total: 25 }, 7, 10), { index: 5, offset: 10 });
assert.deepEqual(step({ index: 9, offset: 10, count: 10, total: 25 }, 7, 10), { index: 4, offset: 20 });
assert.deepEqual(step({ index: 9, offset: 10, count: 10, total: 25 }, 1, 10), { index: 0, offset: 20 });
assert.equal(step({ index: 4, offset: 20, count: 5, total: 25 }, 1, 10), null);
assert.equal(step({ index: 0, offset: 0, count: 10, total: 25 }, -1, 10), null);
assert.deepEqual(step({ index: 0, offset: 20, count: 5, total: 25 }, -1, 10), { index: 9, offset: 10 });
assert.deepEqual(step({ index: 2, offset: 10, count: 10, total: 25 }, -7, 10), { index: 5, offset: 0 });
assert.deepEqual(step({ index: 0, offset: 3, count: 10, total: 25 }, -7, 10), { index: 0, offset: 0 });
// TV input: unlock first, then (parental controls) the parents' code bound to exactly the chosen input.
{
  const calls = [];
  const unlock = (ok) => async (reason) => { calls.push(['unlock', reason]); return ok; };
  const parent = (ok) => async (reason, scope) => { calls.push(['parent', reason, scope]); return ok; };
  const base = { id: 'HDMI_2', reason: 'r', parentReason: 'p HDMI 2' };
  assert.equal(await admitInputSwitch({ ...base, parentalOn: true, ensureUnlocked: unlock(true), ensureParentGrant: parent(true) }), true);
  assert.deepEqual(calls, [['unlock', 'r'], ['parent', 'p HDMI 2', { action: 'tvInput', item: 'HDMI_2' }]]);
  calls.length = 0;
  assert.equal(await admitInputSwitch({ ...base, parentalOn: true, ensureUnlocked: unlock(false), ensureParentGrant: parent(true) }), false);
  assert.deepEqual(calls.map((c) => c[0]), ['unlock']);
  calls.length = 0;
  assert.equal(await admitInputSwitch({ ...base, parentalOn: true, ensureUnlocked: unlock(true), ensureParentGrant: parent(false) }), false);
  calls.length = 0;
  assert.equal(await admitInputSwitch({ ...base, parentalOn: false, ensureUnlocked: unlock(true), ensureParentGrant: parent(true) }), true);
  assert.deepEqual(calls.map((c) => c[0]), ['unlock']);
  assert.equal(await admitInputSwitch({ ...base, id: '', parentalOn: false, ensureUnlocked: unlock(true), ensureParentGrant: parent(true) }), false);
}
// "Continue playing": played games newest first, without hidden ones, apps, never played ones and the running game.
{
  const lib = [
    { id: 'a', type: 'game', lastPlayed: 100, playtimeMinutes: 600, lastSessionMinutes: 45 },
    { id: 'b', type: 'game', lastPlayed: 300, playtimeMinutes: 30, lastSessionMinutes: 30 },
    { id: 'c', type: 'game', lastPlayed: 200, hidden: true },
    { id: 'd', type: 'app', lastPlayed: 400 },
    { id: 'e', type: 'game', lastPlayed: 0 },
    { id: 'f', type: 'game', lastPlayed: 250, parentalLocked: true },
    ...Array.from({ length: 8 }, (_, i) => ({ id: `g${i}`, type: 'game', lastPlayed: 10 + i }))
  ];
  assert.deepEqual(continuePlaying(lib).map((i) => i.id), ['b', 'f', 'a', 'g7', 'g6', 'g5']);
  assert.equal(continuePlaying(lib).length, CONTINUE_LIMIT);
  // The running game is not repeated and takes one of the places.
  assert.deepEqual(continuePlaying(lib, { runningId: 'b' }).map((i) => i.id), ['f', 'a', 'g7', 'g6', 'g5']);
  assert.deepEqual(continuePlaying([], {}), []);
  // Facts: the last session only when it says something the total doesn't; nothing invented for unknown values.
  assert.deepEqual(continueFacts(lib[0]), { lastPlayed: 100, total: 600, last: 45 });
  assert.deepEqual(continueFacts(lib[1]), { lastPlayed: 300, total: 30, last: null });
  assert.deepEqual(continueFacts({ lastPlayed: 0 }), { lastPlayed: 0, total: null, last: null });
  assert.deepEqual(continueFacts({ lastPlayed: 5, lastSessionMinutes: 12 }), { lastPlayed: 5, total: null, last: 12 });
  // Shelf: running first (hero), then the row, then recent ones not yet shown, then the pinned collections.
  const running = { id: 'b', running: true };
  const shelf = homeShelf({ running, library: lib, recent: [lib[0], { id: 'fav' }], extras: [{ id: '__col:c', special: true }] });
  assert.deepEqual(shelf.list.map((i) => i.id), ['b', 'f', 'a', 'g7', 'g6', 'g5', 'fav', '__col:c']);
  assert.equal(shelf.heroId, 'b');
  assert.ok(shelf.continueIds.has('a') && !shelf.continueIds.has('fav'));
  // Favorites tab: what the person's rules keep out of sight (the host's `blocked`) never shows, not even as "Continue playing".
  const blocked = new Set(['b', 'a']);
  assert.deepEqual(continuePlaying(lib, { blocked }).map((i) => i.id), ['f', 'g7', 'g6', 'g5', 'g4', 'g3']);
  assert.deepEqual(homeShelf({ library: lib, recent: [], blocked }).list.map((i) => i.id).slice(0, 2), ['f', 'g7']);
  assert.deepEqual(continuePlaying(lib, { blocked: null }).map((i) => i.id), ['b', 'f', 'a', 'g7', 'g6', 'g5']);
  // Websites and the arrangement are the host's; the page only draws what it is given, after the continue row.
  const withSite = homeShelf({ library: lib, recent: [{ id: 'site:https://x.example/', site: true }, { id: 'a' }], extras: [] });
  assert.deepEqual(withSite.list.map((i) => i.id).slice(-1), ['site:https://x.example/']);
  // Parent to child: the page hands the shelf no library until the host answered for the child, then the child's blocked list applies.
  const parentView = homeShelf({ library: lib, recent: [], blocked: new Set() });
  assert.ok(parentView.continueIds.has('b'));
  const beforeAnswer = homeShelf({ library: [], recent: [], extras: [] });
  assert.deepEqual(beforeAnswer.list, []);
  const childView = homeShelf({ library: lib, recent: [], blocked: new Set(['b', 'f']) });
  assert.ok(!childView.continueIds.has('b') && !childView.continueIds.has('f') && childView.heroId === 'a');
  // The time span of the Favorites tab applies to "Continue playing" too: older games do not qualify, 0 is unlimited.
  {
    const now = 1_800_000_000_000;
    const day = 86_400_000;
    const old = Array.from({ length: 6 }, (_, k) => ({ id: `old${k}`, type: 'game', lastPlayed: now - (400 + k) * day }));
    const fresh = { id: 'fresh', type: 'game', lastPlayed: now - 2 * day };
    const edge = { id: 'edge', type: 'game', lastPlayed: now - 7 * day };
    const justOut = { id: 'out', type: 'game', lastPlayed: now - 7 * day - 1 };
    const all = [...old, fresh, edge, justOut];
    assert.deepEqual(continuePlaying(all, { days: 7, now }).map((i) => i.id), ['fresh', 'edge']);
    assert.equal(continuePlaying(all, { days: 0, now }).length, CONTINUE_LIMIT);
    assert.deepEqual(continuePlaying(old, { days: 30, now }), []);
    const s7 = homeShelf({ library: all, recent: [], days: 7, now });
    assert.deepEqual(s7.list.map((i) => i.id), ['fresh', 'edge']);
    assert.equal(s7.heroId, 'fresh');
    assert.equal(homeShelf({ library: old, recent: [], days: 7, now }).heroId, null);
  }
  const empty = homeShelf({ library: [], recent: [], extras: [] });
  assert.equal(empty.heroId, null);
  assert.deepEqual(empty.list, []);
}
// Restoring or deleting a save backup: unlock first, then (parental controls) the parents' code for exactly this game.
{
  const calls = [];
  const unlock = (ok) => async (reason) => { calls.push(['unlock', reason]); return ok; };
  const parent = (ok) => async (reason, scope) => { calls.push(['parent', reason, scope]); return ok; };
  const base = { id: 'steam:1', reason: 'r', parentReason: 'p' };
  for (const action of ['restoreSaves', 'deleteSaveBackup']) {
    calls.length = 0;
    assert.equal(await admitSaveChange({ ...base, action, parentalOn: true, ensureUnlocked: unlock(true), ensureParentGrant: parent(true) }), true);
    assert.deepEqual(calls, [['unlock', 'r'], ['parent', 'p', { action, item: 'steam:1' }]]);
    calls.length = 0;
    assert.equal(await admitSaveChange({ ...base, action, parentalOn: true, ensureUnlocked: unlock(false), ensureParentGrant: parent(true) }), false);
    assert.deepEqual(calls.map((c) => c[0]), ['unlock']);
    assert.equal(await admitSaveChange({ ...base, action, parentalOn: true, ensureUnlocked: unlock(true), ensureParentGrant: parent(false) }), false);
    calls.length = 0;
    assert.equal(await admitSaveChange({ ...base, action, parentalOn: false, ensureUnlocked: unlock(true), ensureParentGrant: parent(true) }), true);
    assert.deepEqual(calls.map((c) => c[0]), ['unlock']);
  }
  // Unknown action or no game: refused without asking anything.
  calls.length = 0;
  assert.equal(await admitSaveChange({ ...base, action: 'backupSaves', parentalOn: false, ensureUnlocked: unlock(true), ensureParentGrant: parent(true) }), false);
  assert.equal(await admitSaveChange({ ...base, id: '', action: 'restoreSaves', parentalOn: false, ensureUnlocked: unlock(true), ensureParentGrant: parent(true) }), false);
  assert.deepEqual(calls, []);
}
// TV pairing answers: plain only is asked in expert mode only, a replaced certificate is said.
assert.equal(pairOutcome({ paired: true, error: 'plainOnly' }, true), 'askPlain');
assert.equal(pairOutcome({ paired: false, error: 'plainOnly' }, false), 'plainRefused');
assert.equal(pairOutcome({ paired: true, error: null, certReplaced: true }, false), 'pairedNewCert');
assert.equal(pairOutcome({ paired: true, error: null }, false), 'paired');
assert.equal(pairOutcome({ paired: true, error: 'denied' }, false), 'failed');
assert.equal(pairOutcome(null, true), 'failed');

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
    // The host's stand-in of broken settings is "windows" too, but nobody set it up: never "done" (VM test 0.5).
    assert.equal(lockStepStatus({ couchLock: 'windows', configFault: true }), 'todo');
    assert.equal(lockStepStatus({ couchLock: 'code', hasPasscode: true, serviceCodes: { couch: true }, configFault: true }), 'todo');
  }
  // The service's code file is damaged: nothing is asked or called, the step fails with the note that points to the reset.
  for (const couchLock of ['off', 'code', 'windows']) {
    const damaged = { couchLock, hasPasscode: true, serviceCodes: { couch: true, damaged: true }, codesInService: true };
    assert.deepEqual(await setupCouchLock({ security: damaged, enroll: never, restore: never, askAgain: never }), { status: 'failed', note: 'codesDamaged' });
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
  // A refusal of the host is "failed", never "ok"; a cancelled entry asks again, then "aborted" (a cancel of the switch).
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
    assert.deepEqual(await setupCouchLock({ security: off, enroll: async () => 'cancelled', restore: never, askAgain: async () => false }), { status: 'aborted', note: 'noCode' });
  }
}

// The on-screen code buttons: only while no controller is connected (or the list cannot be read); never a way around the code.
assert.equal(shouldShowCodeKeys(0), true);
assert.equal(shouldShowCodeKeys(null), true);
assert.equal(shouldShowCodeKeys(undefined), true);
assert.equal(shouldShowCodeKeys(1), false);
assert.equal(shouldShowCodeKeys(3), false);

// Code lock from a code the service held from before (0.6.2, owner report of 0.6.1): back in the app mode the config says "no lock" while the
// service keeps its mode and couch code. No new code is asked then; a hash along would be refused ("an approved code exists").
{
  const base = { couchLock: 'off', hasPasscode: true, codesInService: true };
  // Couch code held under the code mode: keep it (raising is free), no code entry.
  assert.equal(codeLockPlan({ ...base, serviceCodes: { couch: true, parent: false, mode: 'code' } }), 'keep');
  assert.equal(codeLockPlan({ ...base, serviceCodes: { couch: true, parent: true, mode: 'code' } }), 'keep');
  // Only a parents' code held: the couch code is stored first with its approval (never planted next to it).
  assert.equal(codeLockPlan({ ...base, hasPasscode: false, serviceCodes: { couch: false, parent: true, mode: 'code' } }), 'store');
  assert.equal(codeLockPlan({ ...base, hasPasscode: false, serviceCodes: { couch: false, parent: true, mode: 'off' } }), 'store');
  // A lock mode on without a couch code: store first as well.
  assert.equal(codeLockPlan({ ...base, serviceCodes: { couch: false, parent: false, mode: 'windows' } }), 'store');
  // Nothing held, a couch code at the open couch (mode off), a lock of 0.4.0, no service, unknown: the old flow.
  assert.equal(codeLockPlan({ ...base, hasPasscode: false, serviceCodes: { couch: false, parent: false, mode: 'off' } }), 'default');
  assert.equal(codeLockPlan({ ...base, serviceCodes: { couch: true, parent: false, mode: 'off' } }), 'default');
  assert.equal(codeLockPlan({ ...base, serviceCodes: { couch: true, parent: false, mode: 'code', legacy: true } }), 'default');
  assert.equal(codeLockPlan({ ...base, codesInService: false, serviceCodes: { couch: true, mode: 'code' } }), 'default');
  assert.equal(codeLockPlan({ ...base, serviceCodes: null }), 'default');
  assert.equal(codeLockPlan(null), 'default');
  // The console setup with a held code: the lock step runs the enroll flow, which switches on without a hash (the fake host mirrors the host).
  const calls = [];
  const security = { ...base, serviceCodes: { couch: true, parent: false, mode: 'code' } };
  const setCouchLock = async (mode, code = null) => {
    calls.push(['setCouchLock', mode, code ? 'code' : null]);
    if (mode === 'code' && !code && !(security.serviceCodes.couch && security.serviceCodes.mode === 'code')) throw new Error('hud.setCodeFirst');
    security.couchLock = mode;
    return security;
  };
  const enroll = async () => {
    if (codeLockPlan(security) !== 'keep') return 'failed';
    await setCouchLock('code', null);
    return codeLockHeld(security) ? 'on' : 'failed';
  };
  assert.equal(lockStepStatus(security), 'todo');
  assert.deepEqual(await setupCouchLock({ security, enroll, restore: async () => assert.fail('not asked'), askAgain: async () => assert.fail('not asked') }), { status: 'ok', note: 'codeActive' });
  assert.deepEqual(calls, [['setCouchLock', 'code', null]]);
  assert.equal(lockStepStatus(security), 'done');
}

// Windows update sizes: only what the host is sure of; a total only when every update still to load has one.
{
  assert.equal(validSize(5_000_000), 5_000_000);
  for (const bad of [0, -1, NaN, Infinity, null, undefined, '5000']) assert.equal(validSize(bad), null);
  assert.equal(itemSize({ sizeBytes: 40_000_000 }), 40_000_000);
  assert.equal(itemSize({ sizeBytes: null }), null);
  assert.equal(itemSize({ sizeBytes: 40_000_000, downloaded: true }), null);
  assert.equal(itemSize(null), null);
  // All known: the sum.
  assert.deepEqual(updateSummary([{ sizeBytes: 30 }, { sizeBytes: 12 }]), { count: 2, downloaded: 0, size: 42 });
  // One unknown (min and max far apart): no total, not a sum of only part of it.
  assert.deepEqual(updateSummary([{ sizeBytes: 30 }, { sizeBytes: null }]), { count: 2, downloaded: 0, size: null });
  // Downloaded ones load nothing and do not spoil the total.
  assert.deepEqual(updateSummary([{ sizeBytes: 30 }, { sizeBytes: null, downloaded: true }]), { count: 2, downloaded: 1, size: 30 });
  // Everything downloaded: no size at all.
  assert.deepEqual(updateSummary([{ sizeBytes: 9, downloaded: true }]), { count: 1, downloaded: 1, size: null });
  // Foreign values never add up to NaN or a negative total.
  assert.deepEqual(updateSummary([{ sizeBytes: -5 }, { sizeBytes: NaN }]), { count: 2, downloaded: 0, size: null });
  assert.deepEqual(updateSummary(undefined), { count: 0, downloaded: 0, size: null });
}

// Downloads tab: pending updates as startable lines, never twice; per line only the actions that really work for its source.
{
  const updates = { windows: [{ id: 'kb1' }], apps: [{ id: 'Discord.Discord' }] };
  const selfUpdate = { available: true };
  const catalogUpdates = [{ id: 'wand', kind: 'plugin' }, { id: 'neon', kind: 'skin' }];
  assert.deepEqual(pendingDownloads({ updates, selfUpdate, catalogUpdates }, []).map((p) => p.id),
    ['update:windows', 'update:apps', 'couchhud:self', 'catalog:plugin:wand', 'catalog:skin:neon']);
  // Already running: not offered again; a finished line of the same id does not hide it.
  const running = [{ id: 'update:windows', state: 'downloading' }, { id: 'catalog:plugin:wand', state: 'installing' }, { id: 'update:apps', state: 'done' }];
  assert.deepEqual(pendingDownloads({ updates, selfUpdate, catalogUpdates }, running).map((p) => p.id), ['update:apps', 'couchhud:self', 'catalog:skin:neon']);
  assert.deepEqual(pendingDownloads({ updates: { windows: [], apps: [] }, selfUpdate: { available: false } }, []), []);
  assert.deepEqual(pendingDownloads(undefined, undefined), []);
  // Just finished: hidden while the list it came from is older than the done line, offered again once newer news says it is still there.
  const done = [{ id: 'update:apps', state: 'done', updatedAt: 2000 }, { id: 'catalog:skin:neon', state: 'done', updatedAt: 2000 }, { id: 'couchhud:self', state: 'failed', updatedAt: 2000 }];
  assert.deepEqual(pendingDownloads({ updates, selfUpdate, catalogUpdates, seen: { updates: 1000, selfUpdate: 1000, catalogUpdates: 1000 } }, done).map((p) => p.id),
    ['update:windows', 'couchhud:self', 'catalog:plugin:wand']);
  assert.deepEqual(pendingDownloads({ updates, selfUpdate, catalogUpdates, seen: { updates: 3000, selfUpdate: 1000, catalogUpdates: 1000 } }, done).map((p) => p.id),
    ['update:windows', 'update:apps', 'couchhud:self', 'catalog:plugin:wand']);
  // Nothing heard yet counts as old; a foreign timestamp never hides anything.
  assert.deepEqual(pendingDownloads({ updates, seen: undefined }, done).map((p) => p.id), ['update:windows']);
  assert.deepEqual(pendingDownloads({ updates }, [{ id: 'update:apps', state: 'done', updatedAt: '9999' }]).map((p) => p.id), ['update:windows', 'update:apps']);

  // CouchHUD's own download while it can be cancelled: cancel; after the hand-over (cancellable false) nothing.
  assert.deepEqual(downloadActions({ id: 'couchhud:self', kind: 'couchHud', source: 'couchhud', state: 'downloading', cancellable: true }), ['cancel']);
  assert.deepEqual(downloadActions({ id: 'couchhud:self', kind: 'couchHud', source: 'couchhud', state: 'downloading', cancellable: false }), []);
  // Windows Update and winget while they run: nothing (no pause, no cancel from here).
  assert.deepEqual(downloadActions({ id: 'update:windows', kind: 'windows', source: 'windows', state: 'downloading' }), []);
  assert.deepEqual(downloadActions({ id: 'update:apps', kind: 'app', source: 'winget', state: 'downloading' }), []);
  // Steam: its own download page (pause and resume there), for a running, paused or queued line and for a request.
  for (const s of ['downloading', 'paused', 'queued']) assert.deepEqual(downloadActions({ kind: 'game', source: 'steam', state: s }), ['openClient']);
  assert.deepEqual(downloadActions({ kind: 'game', source: 'steam', state: 'waiting' }), ['openClient', 'remove']);
  // Epic: no fixed link to its downloads, only "stop tracking".
  assert.deepEqual(downloadActions({ kind: 'game', source: 'epic', state: 'inClient' }), ['remove']);
  // Finished lines: remove only, a stale "cancellable" never offers a cancel.
  assert.deepEqual(downloadActions({ kind: 'couchHud', source: 'couchhud', state: 'failed', cancellable: true }), ['remove']);
  assert.deepEqual(downloadActions({ kind: 'game', source: 'steam', state: 'done' }), ['remove']);
  assert.deepEqual(downloadActions(null), []);
}

// Startup options: expert mode only, honest per source, batch file syntax caught before the host refuses it.
{
  const view = (o) => ({ argsMode: 'override', args: [], targetAllowed: true, target: null, custom: false, ...o });
  assert.equal(launchRows(view(), { expert: false }), null);
  assert.equal(launchRows(null, { expert: true }), null);
  assert.deepEqual(launchRows(view(), { expert: true }), { args: 'edit', cwd: true, target: true, reset: false });
  assert.deepEqual(launchRows(view({ argsMode: 'own', targetAllowed: false }), { expert: true }), { args: 'edit', cwd: true, target: false, reset: false });
  // Steam and the launchers: a note, no fields that pretend to work; the own target is still offered.
  assert.deepEqual(launchRows(view({ argsMode: 'steam' }), { expert: true }), { args: 'steam', cwd: false, target: true, reset: false });
  assert.deepEqual(launchRows(view({ argsMode: 'launcher', targetAllowed: false }), { expert: true }), { args: 'launcher', cwd: false, target: false, reset: false });
  assert.equal(launchRows(view({ argsMode: 'none', targetAllowed: false }), { expert: true }), null);
  assert.equal(launchRows(view({ argsMode: 'none', targetAllowed: false, custom: true }), { expert: true }).reset, true);
  assert.deepEqual(TARGET_EXTENSIONS, ['.exe', '.bat', '.cmd']);
  assert.equal(isScript('D:\\G\\start.BAT'), true);
  assert.equal(isScript('D:\\G\\run.cmd'), true);
  assert.equal(isScript('D:\\G\\game.exe'), false);
  assert.equal(isScript(null), false);
  for (const bad of ['a & calc', 'a|b', '^', '>x', '<x', '(x)', '%PATH%', '!x!']) assert.equal(scriptArgsProblem(bad, 'D:\\G\\s.bat'), true, bad);
  // Plain ASCII only (best-fit lookalikes), for a batch file; an exe keeps any text.
  for (const bad of ['-name=M\u00fcller', '\uff06calc', 'a\u00a0b']) assert.equal(scriptArgsProblem(bad, 'D:\\G\\s.bat'), true, bad);
  assert.equal(scriptArgsProblem('-name=M\u00fcller', 'D:\\G\\game.exe'), false);
  assert.equal(scriptArgsProblem('-dx11 "two words"', 'D:\\G\\s.bat'), false);
  assert.equal(scriptArgsProblem('a & b', 'D:\\G\\game.exe'), false);
  assert.equal(targetLabel(view({ target: 'D:\\Games\\X\\start dx11.bat' }), 'auto'), 'start dx11.bat');
  assert.equal(targetLabel(view(), 'auto'), 'auto');
}

// Own texts and the store match: the same bounds as the host, Steam's own entries without a choice, "confirm" only with parental controls.
{
  assert.deepEqual(INFO_FIELDS.map((f) => f.id), ['description', 'developers', 'publishers', 'releaseDate', 'genres']);
  assert.equal(infoProblem('description', ''), null);
  assert.equal(infoProblem('description', '<b>text</b> & more'), null);
  assert.equal(infoProblem('description', 'x'.repeat(1001)), 'length');
  assert.equal(infoProblem('description', 'line\nbreak'), 'chars');
  assert.equal(infoProblem('description', 'rtl‮txt'), 'chars');
  assert.equal(infoProblem('releaseDate', '1'.repeat(41)), 'length');
  assert.equal(infoProblem('developers', 'A, B; C'), null);
  assert.equal(infoProblem('genres', Array.from({ length: 9 }, (_, i) => `g${i}`).join(',')), 'entries');
  assert.equal(infoProblem('publishers', 'p'.repeat(81)), 'length');
  assert.equal(infoProblem('age', '0'), 'field');
  assert.deepEqual(splitList(' A ,, B;C '), ['A', 'B', 'C']);
  assert.equal(fieldText({ developers: ['A', 'B'] }, 'developers'), 'A, B');
  assert.equal(fieldText({ releaseDate: null }, 'releaseDate'), '');
  assert.equal(queryOk(' Hades '), true);
  assert.equal(queryOk(''), false);
  assert.equal(queryOk('q'.repeat(101)), false);
  assert.equal(queryOk('a\tb'), false);
  assert.equal(storeRows({ mode: 'steam' }), null);
  assert.equal(storeRows(null), null);
  assert.deepEqual(storeRows({ mode: 'auto', ageFloor: null }), { search: true, none: true, auto: false, confirm: false });
  assert.deepEqual(storeRows({ mode: 'manual', ageFloor: 18 }, { parental: true }), { search: true, none: true, auto: true, confirm: true });
  assert.deepEqual(storeRows({ mode: 'none', ageFloor: 18 }), { search: true, none: false, auto: true, confirm: false });
}

// The console setup changes nothing on the host's stand-in of broken settings or without its hold; the refusal is seen, not swallowed.
{
  const locked = Object.assign(new Error('Unlock first'), { locked: true });
  const refused = new Error('Not saved while the settings cannot be used');
  let holds = 0;
  const hold = async () => void holds++;
  assert.deepEqual(await beginSetupHold({ readSecurity: async () => ({ configFault: false }), hold }), { ok: true, security: { configFault: false } });
  assert.equal(holds, 1);
  // Broken since the page started: the fresh read stops it before the hold.
  holds = 0;
  const fault = await beginSetupHold({ readSecurity: async () => ({ configFault: true }), hold });
  assert.equal(fault.ok, false);
  assert.equal(fault.reason, 'configFault');
  assert.equal(holds, 0);
  // The host refuses the hold (its own fault check, not a lock): a failure with the host's text, no step runs.
  const notHeld = await beginSetupHold({ readSecurity: async () => ({}), hold: async () => { throw refused; } });
  assert.deepEqual([notHeld.ok, notHeld.reason, notHeld.error], [false, 'failed', refused]);
  assert.equal((await beginSetupHold({ readSecurity: async () => ({}), hold: async () => { throw locked; } })).reason, 'locked');
  // The state cannot be read: no start either.
  holds = 0;
  assert.equal((await beginSetupHold({ readSecurity: async () => { throw locked; }, hold })).ok, false);
  assert.equal(holds, 0);

  // The fault as the host sees it now; a read that fails keeps what was known.
  assert.equal(await configFaultNow(false, async () => ({ configFault: true })), true);
  assert.equal(await configFaultNow(true, async () => ({ configFault: false })), false);
  assert.equal(await configFaultNow(true, async () => { throw locked; }), true);
  assert.equal(await configFaultNow(false, async () => ({})), false);
  assert.equal(await configFaultNow(true, async () => null), true);
}

// Batteries: the 4 steps of a pad's level and of a percent, the bounds, no battery is no badge, the guide menu entries.
{
  assert.equal(padSteps('empty'), 0);
  assert.equal(padSteps('low'), 1);
  assert.equal(padSteps('medium'), 2);
  assert.equal(padSteps('full'), 3);
  assert.equal(padSteps('wired'), null);
  assert.equal(padSteps('unknown'), null);
  assert.equal(padSteps('toString'), null);
  assert.equal(bars(0), '▯▯▯');
  assert.equal(bars(2), '▮▮▯');
  assert.equal(bars(3), '▮▮▮');
  assert.equal(bars(null), '');
  assert.equal(bars(4), '');
  assert.equal(percentSteps(0), 0);
  assert.equal(percentSteps(LOW_PERCENT), 0);
  assert.equal(percentSteps(LOW_PERCENT + 1), 1);
  assert.equal(percentSteps(40), 1);
  assert.equal(percentSteps(41), 2);
  assert.equal(percentSteps(71), 3);
  assert.equal(percentSteps(100), 3);
  assert.equal(percentSteps(101), null);
  assert.equal(percentSteps(-1), null);
  assert.equal(percentSteps(50.5), null);
  assert.equal(percentSteps('50'), null);
  assert.equal(validPercent(0) && validPercent(100), true);
  assert.equal(validPercent(101) || validPercent(null), false);
  // A desktop (null), a broken value: no badge. On the charger: the plug, never low.
  assert.equal(systemBadge(null), null);
  assert.equal(systemBadge(undefined), null);
  assert.equal(systemBadge({ percent: 255 }), null);
  assert.deepEqual(systemBadge({ percent: 78, charging: false, pluggedIn: false, low: false }), { percent: 78, charging: false, low: false, icon: '🔋' });
  assert.deepEqual(systemBadge({ percent: 12, charging: false, pluggedIn: false, low: true }), { percent: 12, charging: false, low: true, icon: '🔋' });
  assert.deepEqual(systemBadge({ percent: 12, charging: true, pluggedIn: true, low: false }), { percent: 12, charging: true, low: false, icon: '⚡' });
  assert.equal(systemBadge({ percent: 10, charging: false, pluggedIn: true, low: false }).low, false);
  // Guide menu: pads with bars (wired: plug, unknown: nothing), Bluetooth devices only with a valid percent and a name.
  const entries = batteryEntries(
    [{ index: 0, battery: 'medium' }, { index: 1, battery: 'wired' }, { index: 2, battery: 'unknown' }, { index: 3, battery: 'empty' }, { battery: 'full' }],
    [{ id: 'a', name: 'Pad', kind: 'controller', percent: 10 }, { id: 'b', name: 'Headset', kind: 'headphones', percent: 80 }, { id: 'c', name: 'X', percent: 300 }, { id: 'd', name: '', percent: 50 }]);
  assert.deepEqual(entries.map((e) => [e.key, e.label, e.value, e.low]), [
    ['pad:0', '1', '▮▮▯', false], ['pad:1', '2', '🔌', false], ['pad:2', '3', '', false], ['pad:3', '4', '▯▯▯', true],
    ['bt:a', 'Pad', '▯▯▯', true], ['bt:b', 'Headset', '▮▮▮', false]
  ]);
  assert.deepEqual(batteryEntries(null, undefined), []);
  // A pad with a percent (Windows.Gaming.Input): bars and percent from it, the warning from the level; a broken percent is ignored.
  assert.deepEqual(batteryEntries([{ index: 0, battery: 'medium', percent: 64 }], []).map((e) => [e.value, e.percent, e.low]), [['▮▮▯', 64, false]]);
  assert.deepEqual(batteryEntries([{ index: 0, battery: 'medium', percent: 10 }], []).map((e) => [e.value, e.low]), [['▯▯▯', false]]);
  assert.deepEqual(batteryEntries([{ index: 0, level: 'low', percent: 12 }], []).map((e) => [e.value, e.low]), [['▯▯▯', true]]);
  assert.deepEqual(batteryEntries([{ index: 0, battery: 'full', percent: 300 }], []).map((e) => [e.value, e.percent]), [['▮▮▮', undefined]]);
  assert.deepEqual(batteryEntries([{ index: 0, battery: 'wired', percent: 50 }], []).map((e) => e.value), ['🔌']);
  // PlayStation pads read over HID (slots 4 to 7) are numbered after the XInput pads in their own order, not as "5" to "8".
  assert.deepEqual(batteryEntries([{ index: 0, battery: 'full' }, { index: 6, battery: 'low' }, { index: 5, battery: 'full' }], []).map((e) => e.label), ['1', '3', '2']);
  assert.deepEqual(batteryEntries([{ index: 4, battery: 'full' }], []).map((e) => e.label), ['1']);
  assert.deepEqual(batteryEntries([{ index: 1, battery: 'full' }, { index: 4, battery: 'full' }], []).map((e) => e.label), ['2', '3']);
}

// Screenshot toast: a picture only from CouchHUD's own file host and only for a saved shot; refusals and errors are text.
{
  const saved = shotToast({ ok: true, text: 'Saved', url: 'https://local.couchhud/file?p=x.png' });
  assert.deepEqual(saved, { ok: true, text: 'Saved', image: 'https://local.couchhud/file?p=x.png', ms: 3500 });
  assert.equal(shotToast({ ok: true, text: 'x', url: 'https://evil.example/x.png' }).image, null);
  assert.equal(shotToast({ ok: true, text: 'x', url: 'https://local.couchhud.evil.example/x.png' }).image, null);
  assert.equal(shotToast({ ok: true, text: 'x', url: 'data:image/png;base64,AAAA' }).image, null);
  assert.equal(shotToast({ ok: true, text: 'x', url: 'file:///C:/x.png' }).image, null);
  assert.deepEqual(shotToast({ ok: false, text: 'Refused', url: 'https://local.couchhud/file?p=x.png' }), { ok: false, text: 'Refused', image: null, ms: 5000 });
  assert.deepEqual(shotToast(null), { ok: false, text: '', image: null, ms: 5000 });
  assert.equal(shotToast({ ok: 'yes', text: 5 }).ok, false);
  assert.equal(shotToast({ ok: 'yes', text: 5 }).text, '');

  // Code entries report themselves: open while any is on screen, each end counted once.
  const reports = [];
  const entries = codeEntryTracker((open) => reports.push(open));
  const first = entries.begin();
  const second = entries.begin();
  assert.equal(entries.open, 2);
  first();
  first();
  assert.equal(entries.open, 1);
  second();
  assert.equal(entries.open, 0);
  second();
  assert.deepEqual(reports, [true, true, true, false]);

  // The pairing code: the first state always, then only changes; anything but true counts as "not shown".
  const pairing = [];
  const report = changeReporter((open) => pairing.push(open));
  report(false);
  report(false);
  report(true);
  report(true);
  report('yes');
  report(false);
  assert.deepEqual(pairing, [false, true, false]);
  for (const part of ['.pair-qr svg', '.osk-qr svg', '.pair-code']) assert.ok(PAIRING_SELECTOR.includes(part), part);
}

// A lost report is sent again (audit 0.5 A3): delivered only once the host answered; refresh() repeats the state regardless.
{
  const sent = [];
  let fail = true;
  const report = changeReporter((open) => {
    sent.push(open);
    return fail ? Promise.reject(new Error('lost')) : Promise.resolve();
  });
  const settle = () => new Promise((resolve) => setTimeout(resolve, 0));
  report(true);
  await settle();
  // The same value again: the first one never arrived, so it goes out again.
  fail = false;
  report(true);
  await settle();
  report(true);
  await settle();
  assert.deepEqual(sent, [true, true]);
  // A report that throws is lost too.
  const thrown = [];
  const throwing = changeReporter((open) => {
    thrown.push(open);
    if (thrown.length === 1) throw new Error('no bridge');
  });
  throwing(false);
  await settle();
  throwing(false);
  await settle();
  throwing(false);
  assert.deepEqual(thrown, [false, false]);
  // Only the answer to the latest report counts: true on its way, then false; the late answer to true does not make it "delivered".
  const order = [];
  const answers = [];
  const racing = changeReporter((open) => {
    order.push(open);
    return new Promise((resolve, reject) => answers.push({ resolve, reject }));
  });
  racing(true);
  racing(false);
  answers[1].reject(new Error('lost'));
  await settle();
  answers[0].resolve();
  await settle();
  racing(false);
  assert.deepEqual(order, [true, false, false]);
  // refresh(): the latest state again, delivered or not; nothing before the first report.
  const refreshed = [];
  const beat = changeReporter((open) => refreshed.push(open));
  beat.refresh();
  assert.deepEqual(refreshed, []);
  beat(true);
  await settle();
  beat.refresh();
  assert.deepEqual(refreshed, [true, true]);
  assert.ok(REPORT_REFRESH_MS > 0 && REPORT_REFRESH_MS <= 10_000);
  // A lost "a code is on screen" goes out once more by itself, a lost "nothing" waits for the next call; a newer state wins.
  const wait = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
  const retried = [];
  const lossy = changeReporter((open) => { retried.push(open); return Promise.reject(new Error('lost')); }, { retryMs: 5 });
  lossy(true);
  await wait(30);
  assert.deepEqual(retried, [true, true], 'one retry of a lost yes, not a loop');
  const quiet = [];
  const lostNo = changeReporter((open) => { quiet.push(open); return Promise.reject(new Error('lost')); }, { retryMs: 5 });
  lostNo(false);
  await wait(30);
  assert.deepEqual(quiet, [false]);
  const newer = [];
  let answerYes;
  const overtaken = changeReporter((open) => {
    newer.push(open);
    return open ? new Promise((resolve, reject) => { answerYes = reject; }) : Promise.resolve();
  }, { retryMs: 5 });
  overtaken(true);
  overtaken(false);
  answerYes(new Error('lost'));
  await wait(30);
  assert.deepEqual(newer, [true, false], 'the late loss of an old yes sends nothing');
  assert.ok(REPORT_RETRY_MS > 0 && REPORT_RETRY_MS < REPORT_REFRESH_MS);
}

// Detail page after a new store match: the newest read wins, a pending store answer is asked again, never "no description" too early.
{
  const tickets = loadTickets();
  const before = tickets.next(); // the read for the old match, still on its way
  const after = tickets.next(); // the read after the match changed
  assert.equal(tickets.isNewest(after), true);
  assert.equal(tickets.isNewest(before), false, 'the old match answering late must not put its texts back');
  assert.equal(descriptionState(null, false), 'loading');
  assert.equal(descriptionState({ description: 'Text' }, false), 'text');
  assert.equal(descriptionState({ description: null, storePending: true }, true), 'loading');
  assert.equal(descriptionState({ description: null, storePending: true }, false), 'none');
  assert.equal(descriptionState({ description: null, storePending: false }, true), 'none');
  assert.deepEqual([0, 1, 2, 3].map(storeRetryDelay), [STORE_RETRY_MS[0], STORE_RETRY_MS[1], STORE_RETRY_MS[2], null]);
  assert.ok(STORE_RETRY_MS.every((ms, i) => ms > 0 && (i === 0 || ms > STORE_RETRY_MS[i - 1])));
  // The age badge: a known rating only, nothing for unknown or nonsense.
  assert.equal(ageRating(18), 18);
  assert.equal(ageRating(6), 6);
  assert.equal(ageRating(21), 21);
  // A floor above every rating is shown as adult, not hidden.
  assert.equal(ageRating(22), 18);
  assert.equal(ageRating(99), 18);
  for (const none of [0, null, undefined, -1, 16.5, '18', NaN, Infinity]) assert.equal(ageRating(none), null);
}

// Save backup limits: the stored limit is offered even when it is no preset (moved up from binary MB), so the picker shows it chosen.
{
  assert.deepEqual(limitChoices(GAME_LIMITS_MB, 1000), GAME_LIMITS_MB);
  assert.deepEqual(limitChoices(TOTAL_LIMITS_MB, 60000), [1000, 5000, 10000, 20000, 50000, 60000, 100000]);
  assert.deepEqual(limitChoices(GAME_LIMITS_MB, undefined), GAME_LIMITS_MB);
  assert.deepEqual(limitChoices(GAME_LIMITS_MB, -5), GAME_LIMITS_MB);
  assert.equal(GAME_LIMITS_MB.includes(2000) && TOTAL_LIMITS_MB.includes(20000), true, 'the moved-up defaults are presets');
}

// Age ratings: the picker offers the active system's steps with its labels; a switch never loosens the limit (mirrors AgeRatings).
{
  assert.deepEqual(RATING_CHOICES, ['auto', 'usk', 'pegi', 'esrb']);
  assert.deepEqual(ratingSteps('usk'), [6, 12, 16, 18]);
  assert.deepEqual(ratingSteps('pegi'), [3, 7, 12, 16, 18]);
  // ESRB has no "AO" step: an 18 would let every M game through.
  assert.deepEqual(ratingSteps('esrb'), [6, 10, 13, 17]);
  assert.deepEqual(ratingSteps('strictest'), [3, 6, 12, 16, 18]);
  assert.equal(activeSystem('pegi'), 'pegi');
  for (const odd of ['auto', 'USK', '', null, undefined, '__proto__']) assert.equal(activeSystem(odd), 'strictest');
  assert.equal(limitLabel('usk', 12), 'USK 12');
  assert.equal(limitLabel('pegi', 7), 'PEGI 7');
  assert.equal(limitLabel('esrb', 10), 'ESRB E10+');
  assert.equal(limitLabel('esrb', 13), 'ESRB T');
  assert.equal(limitLabel('esrb', 17), 'ESRB M');
  assert.equal(limitLabel('esrb', 18), null);
  assert.equal(limitLabel('usk', 7), null);
  assert.equal(limitLabel('strictest', 12), null);
  assert.equal(limitLabel('usk', 0), null);
  const keys = [];
  const labels = ageLabels((key, params) => (keys.push(key), `${key}:${JSON.stringify(params ?? {})}`));
  const options = ageChoices('esrb', labels);
  assert.deepEqual(options.map((o) => o.value), [0, 6, 10, 13, 17]);
  assert.equal(options[2].label, 'settings.parental.blockAge:{"label":"ESRB E10+"}');
  assert.equal(ageChoices('strictest', labels)[1].label, 'settings.parental.blockAgeYears:{"age":3}');
  assert.equal(limitText('pegi', 16, labels), 'settings.parental.ageValue:{"label":"PEGI 16"}');
  assert.equal(limitText('usk', 3, labels), 'settings.parental.ageValueYears:{"age":3}');
  assert.equal(limitText('usk', 0, labels), 'settings.common.off:{}');
  // After a change of the Windows region the row names the system the limit is saved under (VM test 0.5: not "16 (ESRB)" for a USK 16).
  assert.deepEqual(shownLimit({ active: 'esrb', saved: 'usk', maxAge: 16, maxAgeSaved: 16 }), { system: 'usk', age: 16, saved: true });
  assert.equal(limitText(shownLimit({ active: 'esrb', saved: 'usk', maxAge: 16, maxAgeSaved: 16 }).system, 16, labels), 'settings.parental.ageValue:{"label":"USK 16"}');
  assert.deepEqual(shownLimit({ active: 'esrb', saved: 'usk', maxAge: 17, maxAgeSaved: 18 }), { system: 'usk', age: 18, saved: true });
  assert.deepEqual(shownLimit({ active: 'usk', saved: null, maxAge: 16, maxAgeSaved: null }), { system: 'usk', age: 16, saved: false });
  // An older host without the saved value, or no limit: the active system as before.
  assert.deepEqual(shownLimit({ active: 'esrb', saved: 'usk', maxAge: 13 }), { system: 'esrb', age: 13, saved: false });
  assert.deepEqual(shownLimit({ active: 'esrb', saved: 'usk', maxAge: 0, maxAgeSaved: 0 }), { system: 'esrb', age: 0, saved: false });
  assert.deepEqual(shownLimit({ active: 'bogus', saved: 'weird', maxAge: 12, maxAgeSaved: 12 }), { system: 'strictest', age: 12, saved: true });
  for (const from of ['usk', 'pegi', 'esrb', 'strictest']) {
    for (const to of ['usk', 'pegi', 'esrb', 'strictest']) {
      for (const step of ratingSteps(from)) {
        const converted = convertLimit(step, to);
        assert.ok(converted > 0 && converted <= step, `${from} ${step} -> ${to} ${converted}`);
        assert.ok(ratingSteps(to).includes(converted) || converted < Math.min(...ratingSteps(to)));
      }
    }
  }
  assert.equal(convertLimit(13, 'usk'), 12);
  assert.equal(convertLimit(17, 'pegi'), 16);
  assert.equal(convertLimit(18, 'esrb'), 17);
  assert.equal(convertLimit(7, 'esrb'), 6);
  assert.equal(convertLimit(3, 'usk'), 3);
  assert.equal(convertLimit(0, 'pegi'), 0);
  // The detail badge: the host's label when it looks like one, else the age.
  assert.deepEqual(ageBadge(16, 'USK 16'), { age: 16, label: 'USK 16' });
  assert.deepEqual(ageBadge(10, 'ESRB E10+'), { age: 10, label: 'ESRB E10+' });
  assert.deepEqual(ageBadge(16, null), { age: 16, label: null });
  assert.deepEqual(ageBadge(16, '<img src=x>'), { age: 16, label: null });
  assert.deepEqual(ageBadge(16, 'USK 16 and more text'), { age: 16, label: null });
  assert.deepEqual(ageBadge(30, 'USK 30'), { age: 18, label: null });
  assert.equal(ageBadge(0, 'USK 0'), null);
  assert.equal(ageBadge(null, 'PEGI 3'), null);
  // The badge's text: a known system as it is, a foreign one with its country (VM test 0.5, 3DMark), else the age.
  const tt = (key, vars = {}) => `${key}${Object.keys(vars).length ? JSON.stringify(vars) : ''}`;
  assert.deepEqual(badgeText(ageBadge(12, 'PEGI 12'), tt), { text: 'PEGI 12', title: 'detail.ageTitleLabel{"label":"PEGI 12"}' });
  const foreign = badgeText(ageBadge(14, 'ClassInd 14'), tt);
  assert.equal(foreign.text, 'detail.ageForeign{"system":"ClassInd","country":"detail.ageCountry.br","step":"14"}');
  assert.ok(foreign.title.includes('detail.ageForeign'));
  assert.equal(badgeText(ageBadge(17, 'CERO D'), tt).text, 'detail.ageForeign{"system":"CERO","country":"detail.ageCountry.jp","step":"D"}');
  assert.deepEqual(badgeText(ageBadge(16, null), tt), { text: 'detail.ageBadge{"age":16}', title: 'detail.ageTitle{"age":16}' });
  assert.equal(badgeText(null, tt), null);
}

// Feature areas: the host's list, an older host or a broken list means everything on, as before.
assert.deepEqual(setAreas(undefined), AREAS);
assert.deepEqual(setAreas('games'), AREAS);
assert.deepEqual(setAreas(['news']), AREAS); // no launch area: the host never sends this, the page does not take it either
assert.deepEqual(setAreas(['news', 'spaceships', 'apps']), ['apps', 'news']);
assert.equal(areaOn('games'), false);
assert.equal(areaOn('apps'), true);
assert.equal(areaOn('always'), true);
assert.equal(areaOn(undefined), true);
assert.equal(areaOn('unknown'), true);
assert.deepEqual(enabledAreas(), ['apps', 'news']);
// Switching: the last launch area stays on, news alone is never a choice, the order stays the areas' order.
assert.deepEqual(toggleArea(['apps', 'news'], 'apps'), { list: ['apps', 'news'], blocked: true });
assert.deepEqual(toggleArea(['apps', 'news'], 'games'), { list: ['games', 'apps', 'news'], blocked: false });
assert.deepEqual(toggleArea(['games', 'apps'], 'apps'), { list: ['games'], blocked: false });
assert.deepEqual(toggleArea(['games'], 'nothing'), { list: ['games'], blocked: false });
assert.equal(validChoice(['news']), false);
assert.equal(validChoice(['browser']), true);
assert.equal(validChoice(['browser', 'x']), false);
assert.equal(validChoice(null), false);
// Tabs, rows and steps of an area that is off are gone; everything without an area stays.
assert.deepEqual(shownTabs([{ id: 'home' }, { id: 'games' }, { id: 'apps' }, { id: 'browser' }, { id: 'news' }, {}]).map((v) => v.id), ['home', 'apps', 'news', undefined]);
assert.deepEqual(shownRows([{ key: 'a' }, { key: 'b', area: 'games' }, { key: 'c', area: 'apps' }, { key: 'd', area: 'always' }]).map((r) => r.key), ['a', 'c', 'd']);
assert.deepEqual(shownSteps(['welcome', 'areas', 'clients', 'lock'], ['apps']), ['welcome', 'areas', 'lock']);
assert.deepEqual(shownSteps(['welcome', 'areas', 'clients', 'lock'], ['games']), ['welcome', 'areas', 'clients', 'lock']);
// The websites switch of the Favorites tab goes with the browser area, the arrangement with none.
assert.equal(areaOfSetting('favoritesSites'), 'browser');
assert.equal(areaOfSetting('favoritesMode'), 'always');
setAreas(null);
// Favorites-only mode hides the span row, so Continue playing is not cut by it either.
assert.equal(continueDays('favorites', 7), 0);
assert.equal(continueDays('combined', 7), 7);
assert.equal(continueDays('recent', 30), 30);
assert.equal(continueDays('combined', 0), 0);
// Heading above the focused tile and the empty text, chosen by pure rules.
assert.equal(shelfHeading({ inContinue: true, mode: 'favorites' }), 'continue');
assert.equal(shelfHeading({ mode: 'combined' }), 'recent');
assert.equal(shelfHeading({ mode: 'combined', favorite: true }), null);
assert.equal(shelfHeading({ mode: 'recent' }), null);
assert.equal(shelfHeading({ mode: 'combined', special: true }), null);
assert.equal(shelfHeading({ mode: 'combined', loaded: false }), null);
assert.deepEqual(emptyHint({ mode: 'combined', areas: ['games', 'apps', 'browser'], sites: false }), { key: 'home.empty.hintMany', tabs: ['app.games.title', 'app.apps.title'] });
assert.deepEqual(emptyHint({ mode: 'favorites', areas: ['apps', 'browser'], sites: true }), { key: 'home.empty.hintMany', tabs: ['app.apps.title', 'browser.title'] });
assert.deepEqual(emptyHint({ mode: 'combined', areas: ['apps'] }), { key: 'home.empty.hint', tabs: ['app.apps.title'] });
assert.deepEqual(emptyHint({ mode: 'combined', areas: ['news'] }), { key: 'home.empty.hintPlain', tabs: [] });
assert.deepEqual(emptyHint({ mode: 'recent', areas: ['games'] }), { key: 'home.empty.recent', tabs: [] });
// Recent websites carry a visit time; the span filters them. Old entries without a time are outside a limited span, inside "unlimited".
{
  const now = 1_800_000_000_000;
  const day = 86_400_000;
  let list = rememberSite([], 'https://a.example/', 'A', now - 40 * day);
  list = rememberSite(list, 'https://b.example/', 'B', now - day);
  assert.equal(list[0].at, now - day);
  assert.deepEqual(recentWithin(list, 30, now).map((r) => r.title), ['B']);
  assert.equal(recentWithin(list, 0, now).length, 2);
  const legacy = cleanRecent([{ url: 'https://old.example/', title: 'Old' }, { url: 'https://new.example/', title: 'New', at: now - day }]);
  assert.equal(legacy.find((r) => r.title === 'Old').at, undefined);
  assert.deepEqual(recentWithin(legacy, 7, now).map((r) => r.title), ['New']);
  assert.equal(recentWithin(legacy, 0, now).length, 2);
  // The same page reported again within a minute keeps the list; later it refreshes the time.
  assert.equal(rememberSite(list, 'https://b.example/', 'B', now - day + 1000), list);
  assert.equal(rememberSite(list, 'https://b.example/', 'B', now)[0].at, now);
}
assert.deepEqual(LAUNCH_AREAS.filter((a) => AREAS.includes(a)), LAUNCH_AREAS);
for (const a of AREAS) assert.ok(AREA_NAME_KEYS[a] && AREA_DESC_KEYS[a], a);

// The page's map agrees with the host's (src/CouchHud.Core/Config/FeatureAreas.cs), and every setting row of the settings page
// has a key the host knows, so it has an area there.
{
  const core = readFileSync(new URL('../src/CouchHud.Core/Config/FeatureAreas.cs', import.meta.url), 'utf8');
  const block = core.slice(core.indexOf('SettingKeys ='), core.indexOf('SaveSettingKeys ='));
  const coreMap = Object.fromEntries([...block.matchAll(/\["(\w+)"\] = (\w+)/g)].map((m) => [m[1], m[2].toLowerCase()]));
  assert.ok(Object.keys(coreMap).length > 40);
  const coreAreas = Object.fromEntries(Object.entries(coreMap).filter(([, a]) => a !== 'always'));
  assert.deepEqual(Object.keys(coreAreas).sort(), Object.keys(SETTING_AREAS).sort());
  for (const [k, a] of Object.entries(coreAreas)) assert.equal(areaOfSetting(k), a, k);
  const page = readFileSync(new URL('../ui/views/settings.js', import.meta.url), 'utf8');
  const keys = new Set([...page.matchAll(/(?<![\w.])(?:toggle|change|saveSetting)\('(\w+)'/g)].map((m) => m[1]));
  keys.delete('inactive'); // classList.toggle
  assert.ok(keys.size > 30);
  for (const k of keys) assert.ok(k in coreMap, `settings row ${k} has no area in FeatureAreas.SettingKeys`);
}

// Skins renamed in 0.5 and moved to the catalog in 0.6: the three old ids map to the new ones, the four catalog skins become
// `user:<id>`, everything else stays as it is (unknown ids fall back to the default skin where they are used), and the page's maps are
// the host's (SkinCatalog.MigrateId / MovedToCatalogIds). The sound sets keep their plain ids.
{
  assert.equal(migrateSkinId('ps4'), 'user:deepsea');
  assert.equal(migrateSkinId('xbox'), 'user:greenlight');
  assert.equal(migrateSkinId('cyberpunk'), 'user:neoncity');
  for (const id of MOVED_SKINS) assert.equal(migrateSkinId(id), `user:${id}`);
  for (const id of ['default', 'user:ps4', 'user:deepsea', 'user:stardust', 'PS4', 'gone', '', 'constructor', 'toString']) assert.equal(migrateSkinId(id), id);
  assert.equal(migrateSkinId(null), null);
  assert.equal(migrateSkinId(undefined), undefined);
  assert.equal(migrateSoundSetId('ps4'), 'deepsea');
  assert.equal(migrateSoundSetId('nova'), 'nova');
  assert.equal(movedSkinCatalogId('user:nova'), 'nova');
  assert.equal(movedSkinCatalogId('nova'), null);
  assert.equal(movedSkinCatalogId('user:stardust'), null);
  assert.equal(movedSkinCatalogId(undefined), null);
  const catalog = readFileSync(new URL('../src/CouchHud.Core/Skins/SkinCatalog.cs', import.meta.url), 'utf8');
  const block = catalog.slice(catalog.indexOf('RenamedIds ='), catalog.indexOf('};', catalog.indexOf('RenamedIds =')));
  assert.deepEqual(Object.fromEntries([...block.matchAll(/\["(\w+)"\] = "(\w+)"/g)].map((m) => [m[1], m[2]])), { ...RENAMED_SKINS });
  const list = (name) => catalog.match(new RegExp(`${name} = \\[([^\\]]*)\\]`))[1].match(/"(\w+)"/g).map((x) => x.slice(1, -1));
  assert.deepEqual(list('MovedToCatalogIds'), [...MOVED_SKINS]);
  const ids = list('BuiltinIds');
  assert.deepEqual(ids, ['default']);
  const index = readFileSync(new URL('../ui/skins/index.js', import.meta.url), 'utf8');
  assert.deepEqual([...index.matchAll(/^\s+id: '(\w+)'/gm)].map((m) => m[1]), ids);
  // The sound sets: the default one and one per skin that moved (the synthesized sets stay in the app).
  const sound = readFileSync(new URL('../ui/sound.js', import.meta.url), 'utf8');
  assert.deepEqual([...sound.matchAll(/\{ id: '(\w+)', get label\(\)/g)].map((m) => m[1]), ['default', ...MOVED_SKINS]);
  assert.ok(catalog.includes('SoundSetIds = ["default", .. MovedToCatalogIds]'));
}

// No third-party trademarks in the built-in skin: names, descriptions, sound set labels, stylesheets and the skin list. Controller
// names for compatibility live elsewhere (README, guide); the skins only describe their look. The names of the skins that moved to the
// catalog stay in the language files (the notice and the assistant use them).
{
  const brands = /playstation|ps[1-5]|sony|xbox|microsoft|cyberpunk|cd ?projekt|night ?city|series ?x|dualshock|dualsense/i;
  const files = readdirSync(new URL('../ui/skins/', import.meta.url));
  assert.deepEqual(files, ['index.js'], 'only the Couch skin ships in the app');
  for (const f of files) {
    assert.ok(!brands.test(f), f);
    const text = readFileSync(new URL(`../ui/skins/${f}`, import.meta.url), 'utf8');
    const hit = text.match(brands);
    assert.ok(!hit, `ui/skins/${f}: "${hit?.[0]}"`);
  }
  for (const [lang, dict] of [['de', deCore], ['en', enCore]]) {
    const keys = Object.keys(dict).filter((k) => k.startsWith('skins.') || k.startsWith('sound.'));
    assert.ok(keys.filter((k) => k.startsWith('skins.name.')).length === 5, lang);
    for (const k of keys) assert.ok(!brands.test(dict[k]), `${lang} ${k}: ${dict[k]}`);
    assert.ok(dict['skins.missing'], lang);
  }
  assert.equal(deCore['skins.name.deepsea'], 'Tiefsee');
  assert.equal(enCore['skins.name.deepsea'], 'Deep Sea');
}

{
  // Button symbols follow the controller in use; the expert setting fixes them; anything unknown is Xbox.
  assert.deepEqual(PAD_GLYPH_SETTINGS, ['auto', 'xbox', 'playstation']);
  assert.equal(glyphKind('auto', 'playstation'), 'playstation');
  assert.equal(glyphKind('auto', 'xbox'), 'xbox');
  assert.equal(glyphKind(undefined, undefined), 'xbox');
  assert.equal(glyphKind('auto', 'generic'), 'xbox');
  assert.equal(glyphKind('xbox', 'playstation'), 'xbox');
  assert.equal(glyphKind('playstation', 'xbox'), 'playstation');
  assert.equal(glyphKind('nonsense', 'playstation'), 'playstation');

  // Xbox: the buttons as they are; PlayStation: ✕ ○ □ △, L1/R1, OPTIONS, PS. Arrows and keyboard keys never change.
  const plain = ['A', 'B', 'X', 'Y', 'LB', 'RB', '≡'].map((b) => glyphFor(b, 'xbox', {}));
  assert.deepEqual(plain, ['A', 'B', 'X', 'Y', 'LB', 'RB', '≡']);
  const ps = ['A', 'B', 'X', 'Y', 'LB', 'RB', 'LT', 'RT', '≡', 'Guide'].map((b) => glyphFor(b, 'playstation', {}));
  assert.deepEqual(ps, ['✕', '○', '□', '△', 'L1', 'R1', 'L2', 'R2', 'OPTIONS', 'PS']);
  for (const key of ['↕', 'Esc', 'Enter', '⌨', '⌫', 'F11']) assert.equal(glyphFor(key, 'playstation', {}), key);

  // A skin's labels are styling for their own kind of pad: a skin with PlayStation labels shows A B X Y with an Xbox pad,
  // ✕ ○ □ △ with a PlayStation pad; a skin's ☰ or MENU only with an Xbox pad.
  const psLabels = { A: '✕', B: '○', X: '□', Y: '△', LB: 'L1', RB: 'R1', '≡': 'OPTIONS' };
  assert.deepEqual(['A', 'B', 'LB', '≡'].map((b) => glyphFor(b, 'xbox', psLabels)), ['A', 'B', 'LB', '≡']);
  assert.deepEqual(['A', 'B', 'LB', '≡'].map((b) => glyphFor(b, 'playstation', psLabels)), ['✕', '○', 'L1', 'OPTIONS']);
  assert.equal(glyphFor('≡', 'xbox', { '≡': '☰' }), '☰');
  assert.equal(glyphFor('≡', 'playstation', { '≡': '☰' }), 'OPTIONS');
  assert.equal(glyphFor('≡', 'xbox', { '≡': 'MENU' }), 'MENU');
  // Per-family lists (glyphsXbox / glyphsPlayStation) win for their own kind of pad; "glyphs" stays the fallback.
  const both = { glyphs: { A: 'a' }, glyphsXbox: { A: 'Ⓐ' }, glyphsPlayStation: { A: '✖', B: '◯' } };
  assert.equal(glyphFor('A', 'xbox', both.glyphs, both), 'Ⓐ');
  assert.equal(glyphFor('A', 'playstation', both.glyphs, both), '✖');
  assert.equal(glyphFor('B', 'playstation', both.glyphs, both), '◯');
  assert.equal(glyphFor('B', 'xbox', both.glyphs, both), 'B');
  assert.equal(glyphFor('X', 'playstation', both.glyphs, both), '□');
  // A user skin with its own PlayStation styling (a filled triangle) keeps it for PlayStation pads only.
  assert.equal(glyphFor('Y', 'playstation', { Y: '▲' }), '▲');
  assert.equal(glyphFor('Y', 'xbox', { Y: '▲' }), 'Y');
  // Nonsense in a skin.json changes nothing.
  assert.equal(glyphFor('A', 'xbox', { A: 42 }), 'A');
  assert.equal(glyphFor('A', 'playstation', null), '✕');
  assert.equal(glyphFor('toString', 'playstation', {}), 'toString');
  assert.ok(isPlayStationLabel(' ✕ '));
  assert.ok(!isPlayStationLabel('A'));

  // The browser's Gamepad API (preview only): Sony's vendor id or name means PlayStation; Xbox's "Wireless Controller" does not.
  assert.equal(kindFromGamepadId('Wireless Controller (STANDARD GAMEPAD Vendor: 054c Product: 09cc)'), 'playstation');
  assert.equal(kindFromGamepadId('DualSense Wireless Controller (STANDARD GAMEPAD Vendor: 054c Product: 0ce6)'), 'playstation');
  assert.equal(kindFromGamepadId('Xbox Wireless Controller (STANDARD GAMEPAD Vendor: 045e Product: 0b13)'), 'xbox');
  assert.equal(kindFromGamepadId('Xbox 360 Controller (XInput STANDARD GAMEPAD)'), 'xbox');
  assert.equal(kindFromGamepadId(undefined), 'xbox');
  // One decision per frame: a PlayStation pad and its mirror pressing together, or the mirror shortly after, stay PlayStation.
  assert.deepEqual(kindFromPresses(['xbox', 'playstation'], 1000), { kind: 'playstation', lastPlayStationAt: 1000 });
  assert.deepEqual(kindFromPresses(['xbox'], 1000 + MIRROR_WINDOW_MS, 1000), { kind: null, lastPlayStationAt: 1000 });
  assert.deepEqual(kindFromPresses(['xbox'], 1001 + MIRROR_WINDOW_MS, 1000), { kind: 'xbox', lastPlayStationAt: 1000 });
  assert.deepEqual(kindFromPresses([], 5000, 1000), { kind: null, lastPlayStationAt: 1000 });
}

// A protected setting (finding B1 of the 0.5 VM test): the unlock first, then the parents' code for exactly this change; never the
// parents' code alone on a locked couch (the host would refuse the save with "unlock first" afterwards).
{
  const order = [];
  const unlock = (ok) => async (reason) => (order.push(['unlock', reason]), ok);
  const parent = (ok) => async (reason, scope) => (order.push(['parent', reason, scope.action]), ok);
  const base = { reason: 'r', parentReason: 'p', scope: { action: 'ratingSystem' } };
  assert.equal(await admitProtectedChange({ ...base, parentalOn: true, ensureUnlocked: unlock(true), ensureParentGrant: parent(true) }), true);
  assert.deepEqual(order.splice(0), [['unlock', 'r'], ['parent', 'p', 'ratingSystem']]);
  assert.equal(await admitProtectedChange({ ...base, parentalOn: true, ensureUnlocked: unlock(false), ensureParentGrant: parent(true) }), false);
  assert.deepEqual(order.splice(0), [['unlock', 'r']]);
  assert.equal(await admitProtectedChange({ ...base, parentalOn: true, ensureUnlocked: unlock(true), ensureParentGrant: parent(false) }), false);
  order.length = 0;
  assert.equal(await admitProtectedChange({ ...base, parentalOn: false, ensureUnlocked: unlock(true), ensureParentGrant: parent(true) }), true);
  assert.deepEqual(order.splice(0), [['unlock', 'r']]);
  // Without its own parents' reason the unlock's reason is used.
  assert.equal(await admitProtectedChange({ reason: 'r', scope: { action: 'areas' }, parentalOn: true, ensureUnlocked: unlock(true), ensureParentGrant: parent(true) }), true);
  assert.deepEqual(order.splice(0), [['unlock', 'r'], ['parent', 'r', 'areas']]);
}

// ---------------------------------------------------------------------------------------------------------------------------------------
// Plugin catalog, detail page, actions, catalog sources and declarative pages (ui/core/plugin-rules.js, ui/views/plugins.js).
{
  const firstParty = { id: 'wand', kind: 'plugin', origin: 'firstParty', status: 'update', capabilities: ['pages'] };
  const third = { id: 'kodi', kind: 'plugin', origin: 'thirdParty', status: 'notInstalled', publisher: 'a1b2-c3d4', author: 'dev' };
  const foreign = { id: 'x', kind: 'skin', origin: 'local', extra: true, status: 'notInstalled' };

  // Badge logic: third-party (a signed developer key, or anything from an added catalog) is marked; CouchHUD's own is not; unsigned ones say so.
  assert.equal(pr.badgeOf(firstParty), null);
  assert.equal(pr.badgeOf(third), 'thirdParty');
  assert.equal(pr.badgeOf(foreign), 'thirdParty');
  assert.equal(pr.badgeOf({ kind: 'plugin', origin: 'local' }), 'local');
  assert.equal(pr.badgeOf({ kind: 'skin', origin: 'local', unsigned: true }), 'unsigned');
  assert.equal(pr.badgeOf({ kind: 'skin', origin: 'local' }), null);
  assert.equal(pr.badgeOf(null), null);
  assert.equal(pr.publisherOf(third), 'dev · a1b2-c3d4');
  assert.equal(pr.publisherOf(firstParty, 'CouchHUD'), 'CouchHUD');

  // State rendering: one word for every state the page shows.
  const entry = (o) => ({ id: 'e', kind: 'plugin', origin: 'firstParty', status: 'installed', ...o });
  assert.equal(pr.entryState(entry({ status: 'notInstalled' })), 'new');
  assert.equal(pr.entryState(entry({ status: 'update' })), 'update');
  assert.equal(pr.entryState(entry({ status: 'installed' })), 'installed');
  assert.equal(pr.entryState(entry({ status: 'installed', restartPending: true })), 'restart');
  assert.equal(pr.entryState(entry({ status: 'unavailable', reasonCode: 'needsNewer' })), 'needsNewer');
  assert.equal(pr.entryState(entry({ status: 'unavailable', reasonCode: 'officialFirst' })), 'officialFirst');
  assert.equal(pr.entryState(entry({ status: 'unavailable' })), 'unavailable');
  assert.equal(pr.entryState(entry({ status: 'installed' }), { revoked: true }), 'revoked');
  // Withdrawn wins over everything else: it is never offered an update.
  assert.equal(pr.entryState(entry({ status: 'update' }), { revoked: true }), 'revoked');
  // Delisted: installed from a catalog, no entry any more; only once a catalog has loaded; never a local plugin.
  const listed = [{ id: 'wand', kind: 'plugin' }, { id: 'wand', kind: 'skin' }];
  assert.equal(pr.isDelisted({ id: 'kodi', folder: 'Kodi', origin: 'thirdParty', userInstalled: true }, listed), true);
  assert.equal(pr.isDelisted({ id: 'wand', folder: 'CouchHud.Plugin.Wand', origin: 'firstParty', userInstalled: true }, listed), false);
  assert.equal(pr.isDelisted({ id: 'kodi', folder: 'Kodi', origin: 'thirdParty', userInstalled: true }, []), false);
  assert.equal(pr.isDelisted({ id: 'kodi', folder: 'Kodi', origin: 'thirdParty', userInstalled: true }, undefined), false);
  assert.equal(pr.isDelisted({ id: 'mine', folder: 'Mine', origin: 'local', userInstalled: true }, listed), false);
  assert.equal(pr.isDelisted({ id: 'shipped', folder: 'Shipped', origin: 'firstParty', userInstalled: false }, listed), false);
  // A skin with the id of a plugin is not that plugin.
  assert.equal(pr.installedFor({ id: 'wand', kind: 'skin' }, [{ id: 'wand', folder: 'W' }]), undefined);
  assert.equal(pr.installedFor({ id: 'WAND', kind: 'plugin' }, [{ id: 'wand', folder: 'W' }])?.id, 'wand');

  // Actions per state.
  const inst = (o) => ({ id: 'e', folder: 'E', enabled: true, userInstalled: true, ...o });
  assert.deepEqual(pr.entryActions(entry({ status: 'notInstalled' }), undefined, { expert: false }), ['install']);
  assert.deepEqual(pr.entryActions(entry({ status: 'update' }), inst(), { expert: false }), ['update', 'disable', 'uninstall']);
  assert.deepEqual(pr.entryActions(entry({ status: 'installed' }), inst({ enabled: false }), { expert: false }), ['enable', 'uninstall']);
  assert.deepEqual(pr.entryActions(entry({ status: 'installed' }), inst({ userInstalled: false }), {}), ['disable']);
  assert.deepEqual(pr.entryActions(entry({ status: 'installed' }), inst({ revoked: true }), {}), ['acceptRevoked', 'uninstall']);
  assert.deepEqual(pr.entryActions(entry({ status: 'unavailable', reasonCode: 'needsNewer' }), undefined, {}), []);
  assert.deepEqual(pr.entryActions(entry({ status: 'unavailable', reasonCode: 'officialFirst', extra: true }), undefined, { expert: true }), []);
  // A third-party plugin is not even offered outside expert mode; in expert mode it is.
  assert.deepEqual(pr.entryActions({ ...third }, undefined, { expert: false }), []);
  assert.equal(pr.installBlock({ ...third }, { expert: false }), 'expert');
  assert.equal(pr.installBlock({ ...third }, { expert: true }), null);
  assert.deepEqual(pr.entryActions({ ...third }, undefined, { expert: true }), ['install']);
  // A skin can only be installed or updated: no enable, no uninstall.
  assert.deepEqual(pr.entryActions({ id: 's', kind: 'skin', origin: 'local', status: 'update' }, undefined, {}), ['update']);

  // Capabilities in plain words: known ids map to keys that exist in both languages; an unknown id never reaches the text.
  const { dictionaries } = await import('../ui/i18n/index.js');
  for (const cap of ['library', 'pages', 'quickActions', 'assistant', 'notify', 'browser', 'downloads', 'network.state', 'process', 'browser.signin', 'tabs', 'setup.run', 'lock.restrict', 'startup.args', 'native', 'registry', 'network:loopback', 'network:lan', 'network:internet', 'dynamicCode']) {
    const key = pr.capabilityKey(cap);
    assert.ok(key in dictionaries.de && key in dictionaries.en, `capability text for ${cap}`);
  }
  assert.deepEqual(pr.capabilityKeys(['pages', 'bogus<script>']), ['plugin.capability.pages', 'plugin.capability.unknown']);
  assert.equal(pr.isRisky('registry'), true);
  assert.equal(pr.isRisky('pages'), false);

  // Gates, order: the warning first, then the code, and the call only after both. Third-party install: warning, code.
  assert.deepEqual(pr.installPlan(third, { expert: true }).steps, ['warning', 'code']);
  assert.deepEqual(pr.installPlan(third, { expert: true, update: true }).steps, ['confirm', 'code']);
  assert.deepEqual(pr.installPlan(firstParty, { expert: false }).steps, ['confirm', 'code']);
  assert.deepEqual(pr.installPlan(foreign, { expert: true }).steps, ['warning', 'code']);
  // Outside expert mode a third-party install does not even start (no dialog, no code, no call).
  assert.deepEqual(pr.installPlan(third, { expert: false }), { blocked: 'expert', steps: [] });
  assert.deepEqual(pr.installPlan(foreign, { expert: false }), { blocked: 'expert', steps: [] });
  assert.deepEqual(pr.uninstallPlan().steps, ['confirm', 'code']);
  {
    const seen = [];
    const handler = (name, answer) => async () => (seen.push(name), answer);
    const all = { warning: handler('warning', true), confirm: handler('confirm', true), code: handler('code', true) };
    assert.equal(await pr.runGates(['warning', 'code'], all), true);
    assert.deepEqual(seen.splice(0), ['warning', 'code']);
    // Declining the warning stops everything: the code is never asked, the call never reached.
    assert.equal(await pr.runGates(['warning', 'code'], { ...all, warning: handler('warning', false) }), false);
    assert.deepEqual(seen.splice(0), ['warning']);
    // A wrong or cancelled code stops it after the warning.
    assert.equal(await pr.runGates(['warning', 'code'], { ...all, code: handler('code', false) }), false);
    assert.deepEqual(seen.splice(0), ['warning', 'code']);
    // A step without a handler is a refusal, never a pass.
    assert.equal(await pr.runGates(['warning', 'code'], { code: handler('code', true) }), false);
    assert.deepEqual(seen.splice(0), []);
  }

  // The parents' code: mirrors the host's rules.
  assert.equal(pr.needsCode('install', 'thirdParty', [], false), true);
  assert.equal(pr.needsCode('update', 'thirdParty', [], false), false);
  assert.equal(pr.needsCode('update', 'thirdParty', [], true), true);
  assert.equal(pr.needsCode('enable', 'local', [], true), true);
  assert.equal(pr.needsCode('acceptRevoked', 'firstParty', [], false), true);
  assert.equal(pr.needsCode('install', 'firstParty', ['pages'], true), false);
  assert.equal(pr.needsCode('install', 'firstParty', ['assistant'], true), true);
  assert.equal(pr.needsCode('install', 'firstParty', ['assistant'], false), false);
  assert.equal(pr.needsCode('enable', 'firstParty', ['process'], true), false);
  assert.equal(pr.grantAction('acceptRevoked'), 'pluginAcceptRevoked');
  assert.equal(pr.grantAction('install'), 'pluginInstall');

  // No default focus on a risky confirm: it starts on "Cancel"; a plain question starts on the confirm.
  assert.equal(pr.confirmStartIndex({ danger: true }), 1);
  assert.equal(pr.confirmStartIndex({ focusCancel: true }), 1);
  assert.equal(pr.confirmStartIndex({}), 0);
  assert.equal(pr.confirmStartIndex(), 0);
  const overlaySrc = readFileSync(new URL('../ui/components/overlay.js', import.meta.url), 'utf8');
  assert.match(overlaySrc, /index: confirmStartIndex\(\{ danger, focusCancel \}\)/);

  // Catalog sources: the official one first, marked, never removable; added ones show name and host.
  const sources = {
    official: { url: 'https://raw.githubusercontent.com/kewe87/CouchHUD-Catalog/main/index.json', builtIn: true, removable: false },
    extra: [{ url: 'https://plugins.example-dev.net/index.json', host: 'plugins.example-dev.net', name: 'Example', keyCode: 'aaaa', signed: true }, { url: 'https://hub.example.org/c.json', name: null }],
    max: 8
  };
  const rows = pr.sourceRows(sources);
  assert.equal(rows.length, 3);
  assert.equal(rows[0].official, true);
  assert.equal(rows[0].removable, false);
  assert.equal(rows[0].host, 'raw.githubusercontent.com');
  assert.deepEqual(rows.slice(1).map((r) => [r.official, r.removable, r.name, r.host]), [[false, true, 'Example', 'plugins.example-dev.net'], [false, true, null, 'hub.example.org']]);
  // Even a list that wrongly calls the official source removable (or lists it as an extra) cannot remove it.
  assert.equal(pr.canRemoveSource(sources.official, sources.official), false);
  assert.equal(pr.canRemoveSource({ url: sources.official.url, removable: true }, sources.official), false);
  assert.equal(pr.canRemoveSource({ url: 'https://x.example.org/i.json', builtIn: true }, sources.official), false);
  assert.equal(pr.canRemoveSource({ url: 'https://x.example.org/i.json' }, sources.official), true);
  assert.deepEqual(pr.sourceRows({ official: sources.official, extra: [{ url: sources.official.url }], max: 8 }).map((r) => r.removable), [false, false]);
  assert.deepEqual(pr.sourceRows(null), []);
  // Simple mode hides the sources; expert mode shows them; at the limit no more can be added.
  assert.equal(pr.sourcesVisible(false), false);
  assert.equal(pr.sourcesVisible(true), true);
  assert.deepEqual(pr.sourceAddCheck(sources, false), { ok: false, reason: 'expert' });
  assert.deepEqual(pr.sourceAddCheck(sources, true), { ok: true, reason: null });
  assert.deepEqual(pr.sourceAddCheck({ extra: new Array(8).fill({}), max: 8 }, true), { ok: false, reason: 'tooMany' });
  // The add flow: warning, then code, then the call (checked in the page's source below).
  assert.deepEqual(pr.sourceAddSteps(), ['warning', 'code']);
  assert.equal(pr.cleanKey('  '), null);
  assert.equal(pr.cleanKey(' abc '), 'abc');
  assert.equal(pr.cleanKey(undefined), null);
  // Error keys are shown in the language: every key has a text in German and English; anything else is the host's own wording.
  for (const key of pr.SOURCE_ERROR_KEYS) {
    const text = pr.sourceErrorKey(`catalog.source.${key}`);
    assert.ok(text in dictionaries.de && text in dictionaries.en, `source error text for ${key}`);
  }
  assert.equal(pr.sourceErrorKey('catalog.source.privateHost'), 'plugins.srcerr.privateHost');
  assert.equal(pr.sourceErrorKey('catalog.source.nameImitates'), 'plugins.srcerr.nameImitates');
  assert.equal(pr.sourceErrorKey('catalog.source.pinMismatch'), 'plugins.srcerr.pinMismatch');
  assert.equal(pr.sourceErrorKey('catalog.source.officialFirst'), 'plugins.srcerr.officialFirst');
  assert.equal(pr.sourceErrorKey('catalog.source.removed'), 'plugins.srcerr.removed');
  assert.equal(pr.sourceErrorKey('catalog.source.nope'), null);
  assert.equal(pr.sourceErrorKey('Kataloge nur über https'), null);
  assert.equal(pr.sourceErrorKey(undefined), null);

  // The page's source keeps the order: the acknowledgement goes out only after the warning and the code, and nowhere else.
  const pluginsSrc = readFileSync(new URL('../ui/views/plugins.js', import.meta.url), 'utf8');
  const gates = pluginsSrc.indexOf("runGates(['warning', 'code']", pluginsSrc.indexOf('async function addSource'));
  const call = pluginsSrc.indexOf('api.addCatalogSource(');
  assert.ok(gates > 0 && call > gates, 'addCatalogSource comes after the warning and the code');
  assert.equal(pluginsSrc.split('api.addCatalogSource(').length - 1, 1);
  assert.match(pluginsSrc, /api\.addCatalogSource\(url, cleanKey\(keyText\), null, true\)/);
  // The warning is a danger dialog (starts on Cancel) and runs before any call; the same for the third-party install warning.
  assert.match(pluginsSrc, /confirmDialog\(warning\?\.title[^\n]*\{ danger: true/);
  assert.match(pluginsSrc, /return confirmDialog\(t\('plugin\.thirdParty\.title'[^\n]*\r?\n\s*danger: true/);
  // The official source row has no remove action in the page.
  assert.match(pluginsSrc, /run: src\.removable \? \(\) => removeSource\(src, page\) : undefined/);
  // Every change goes through runGates (or the gates of the add and remove flows) right before its call.
  for (const name of ['api.installCatalogEntry(', 'api.uninstallPlugin(', 'api.acceptRevokedPlugin(', 'api.setPluginEnabled(', 'api.removeCatalogSource(', 'api.approvePlugin(']) {
    const at = pluginsSrc.indexOf(name);
    assert.ok(at > 0, `${name} is used`);
    const gateAt = pluginsSrc.lastIndexOf('runGates(', at);
    assert.ok(gateAt > 0 && at - gateAt < 2500, `${name} follows its gates`);
  }

  // Declarative pages: sections, tiles, steppers.
  const rowsIn = [{ id: 'a', section: 's1' }, { id: 'b', section: 's2' }, { id: 'c' }, { id: 'd', section: 'zzz' }, { id: 'e', section: 's1' }];
  const grouped = pr.groupRows(rowsIn, [{ id: 's1', title: 'One', kind: 'tiles' }, { id: 's2', title: null, kind: 'list' }, { id: 's3', kind: 'list' }]);
  assert.deepEqual(grouped.map((g) => [g.id, g.kind, g.rows.map((r) => r.id)]), [['s1', 'tiles', ['a', 'c', 'd', 'e']], ['s2', 'list', ['b']]]);
  assert.deepEqual(pr.groupRows([{ id: 'x' }], []).map((g) => g.rows.length), [1]);
  assert.deepEqual(pr.groupRows([], []), []);
  assert.deepEqual(pr.groupRows(undefined, undefined), []);
  assert.equal(pr.tileColumns(2), 2);
  assert.equal(pr.tileColumns(9), 4);
  assert.equal(pr.tileColumns(0), 1);
  const lines = pr.layoutLines([{ kind: 'tiles', rows: [1, 2, 3, 4, 5, 6].map((id) => ({ id })) }, { kind: 'list', rows: [{ id: 'x' }, { id: 'y' }] }]);
  assert.deepEqual(lines.map((l) => l.map((r) => r.id)), [[1, 2, 3, 4], [5, 6], ['x'], ['y']]);
  const stepper = { min: 0, max: 100, step: 5 };
  assert.equal(pr.stepValue(stepper, 70, 1), 75);
  assert.equal(pr.stepValue(stepper, 70, -1), 65);
  assert.equal(pr.stepValue(stepper, 100, 1), 100);
  assert.equal(pr.stepValue(stepper, 0, -1), 0);
  assert.equal(pr.stepValue(stepper, 72, 1), 75);
  assert.equal(pr.stepValue({ min: 0, max: 1, step: 0.1 }, 0.2, 1), 0.3);
  assert.equal(pr.stepValue(stepper, 'x', 1), 5);
  assert.deepEqual(pr.parseStepper('70 %'), { before: '', number: 70, after: ' %' });
  assert.deepEqual(pr.parseStepper('-3,5 dB'), { before: '', number: -3.5, after: ' dB' });
  assert.equal(pr.parseStepper('none'), null);
  assert.equal(pr.formatStepper(pr.parseStepper('70 %'), 75), '75 %');
  assert.equal(pr.formatStepper(null, 75), '75');
  // Pictures: only a PNG data URI; anything else a plugin might smuggle in is dropped.
  assert.equal(pr.safeImage('data:image/png;base64,iVBORw0KGgo='), 'data:image/png;base64,iVBORw0KGgo=');
  for (const bad of ['http://x/y.png', 'javascript:alert(1)', 'data:image/svg+xml;base64,PHN2Zz4=', 'data:image/png;base64,abc"onerror="x', '', null, undefined, 5]) assert.equal(pr.safeImage(bad), null);
  assert.equal(pr.pressKind({ kind: 'link' }), 'link');
  assert.equal(pr.pressKind({ kind: 'stepper' }), 'stepper');
  assert.equal(pr.pressKind({ kind: 'choice' }), 'choice');
  assert.equal(pr.pressKind({ kind: 'toggle' }), 'activate');
  assert.equal(pr.pressKind(undefined), 'activate');
  assert.equal(pr.currentOption({ value: 'Ruhig', options: [{ id: 'calm', label: 'Ruhig' }] }), 'calm');
  // The page puts every plugin text in as text and never as markup.
  const pageSrc = readFileSync(new URL('../ui/views/plugin-page.js', import.meta.url), 'utf8');
  assert.equal(/innerHTML|insertAdjacentHTML|outerHTML/.test(pageSrc), false);
  assert.equal(/innerHTML|insertAdjacentHTML|outerHTML/.test(pluginsSrc), false);
  assert.match(pageSrc, /safeImage\(row\.image\)/);
}


// needsCode pinned against the table of PluginPolicy.Decide (Core). The same rows are in tests/CouchHud.Core.Tests/PluginPolicyGateTableTests.cs;
// if a rule changes on either side, both tables change, or one test fails. [change, origin, capabilities, parental controls on, update adds capabilities, code needed]
{
  const TABLE = [
    ['install', 'thirdParty', [], false, false, true],
    ['install', 'thirdParty', [], true, false, true],
    ['update', 'thirdParty', [], false, false, false],
    ['update', 'thirdParty', [], true, false, true],
    ['update', 'thirdParty', [], false, true, true],
    ['enable', 'thirdParty', [], false, false, false],
    ['enable', 'thirdParty', [], true, false, true],
    ['uninstall', 'local', [], false, false, false],
    ['uninstall', 'local', [], true, false, true],
    ['approve', 'local', [], false, false, true],
    ['acceptRevoked', 'firstParty', [], false, false, true],
    ['confirmExe', 'firstParty', [], false, false, true],
    ['install', 'firstParty', ['pages'], true, false, false],
    ['install', 'firstParty', ['assistant'], true, false, true],
    ['install', 'firstParty', ['assistant'], false, false, false],
    ['update', 'firstParty', ['process'], true, false, true],
    ['update', 'firstParty', ['assistant'], true, true, true],
    ['update', 'firstParty', ['pages'], true, true, false],
    ['enable', 'firstParty', ['process'], true, false, false]
  ];
  for (const [change, origin, caps, parental, adds, expected] of TABLE) assert.equal(pr.needsCode(change, origin, caps, parental, adds), expected, `${change} ${origin} ${caps} parental=${parental} adds=${adds}`);
  // Skins: CouchHUD's own catalog needs only the unlock; a skin of an added catalog always asks like a third-party install, updates included.
  assert.equal(pr.needsCode('install', 'skin', [], true), false);
  assert.equal(pr.needsCode('update', 'skin', [], true), false);
  const ownSkin = { id: 's', kind: 'skin', origin: 'local', status: 'update' };
  const foreignSkin = { id: 'h', kind: 'skin', origin: 'local', extra: true, status: 'update' };
  assert.equal(pr.gateOrigin(ownSkin), 'skin');
  assert.equal(pr.gateOrigin(foreignSkin), 'thirdParty');
  assert.equal(pr.gateOrigin({ kind: 'plugin', origin: 'firstParty' }), 'firstParty');
  assert.equal(pr.grantChange(foreignSkin, true), 'install');
  assert.equal(pr.grantChange(ownSkin, true), 'update');
  assert.equal(pr.needsCode(pr.grantChange(foreignSkin, true), pr.gateOrigin(foreignSkin), [], false), true);
  assert.equal(pr.needsCode(pr.grantChange(ownSkin, true), pr.gateOrigin(ownSkin), [], true), false);
  // A foreign skin update is blocked outside expert mode and then warns; an own skin never warns.
  assert.deepEqual(pr.installPlan(foreignSkin, { expert: false, update: true }), { blocked: 'expert', steps: [] });
  assert.deepEqual(pr.installPlan(foreignSkin, { expert: true, update: true }).steps, ['warning', 'code']);
  assert.deepEqual(pr.entryActions(foreignSkin, undefined, { expert: false }), []);
  assert.deepEqual(pr.entryActions(foreignSkin, undefined, { expert: true }), ['update']);
  assert.deepEqual(pr.installPlan(ownSkin, { expert: false, update: true }).steps, ['confirm', 'code']);
  // A third-party plugin update that adds capabilities is an install: warning, expert mode, code.
  const addsUpdate = { id: 'k', kind: 'plugin', origin: 'thirdParty', status: 'update', addsCapabilities: true };
  assert.deepEqual(pr.installPlan(addsUpdate, { expert: true, update: true }).steps, ['warning', 'code']);
  assert.deepEqual(pr.installPlan(addsUpdate, { expert: false, update: true }), { blocked: 'expert', steps: [] });
  assert.deepEqual(pr.entryActions(addsUpdate, { id: 'k', folder: 'K', enabled: true, userInstalled: true }, { expert: false }), ['disable', 'uninstall']);
  assert.deepEqual(pr.installPlan({ ...addsUpdate, addsCapabilities: false }, { expert: false, update: true }).steps, ['confirm', 'code']);
  // The grant of that update keeps the host's action name (the host names it before it turns the update into an install).
  assert.equal(pr.grantChange(addsUpdate, true), 'update');
  assert.equal(pr.needsCode('update', 'thirdParty', [], false, true), true);

  // Stepper settle -> redraw, and a push that arrives while a value waits or is on its way (the settler removes its entry only after the send).
  const { settler } = await import('../ui/core/settle.js');
  const delay = (ms) => new Promise((r) => setTimeout(r, ms));
  const make = () => {
    const s = settler(20);
    const log = [];
    const sync = pr.pageSync({ waiting: () => s.waiting('a'), load: () => log.push('load'), render: () => log.push('render') });
    return { s, log, sync };
  };
  {
    const { s, log, sync } = make();
    s.set('a', 5, () => sync.send(async () => { log.push('send'); await delay(10); }));
    await delay(80);
    assert.deepEqual(log, ['send', 'render']);
  }
  {
    // A push while the value is waiting is kept and replayed after the send (instead of a plain redraw).
    const { s, log, sync } = make();
    s.set('a', 5, () => sync.send(async () => { log.push('send'); await delay(10); }));
    assert.equal(sync.push(), false);
    await delay(80);
    assert.deepEqual(log, ['send', 'load']);
  }
  {
    // A push in the middle of the send: not read at once, read once afterwards.
    const { s, log, sync } = make();
    s.set('a', 5, () => sync.send(async () => { log.push('send'); await delay(10); sync.push(); await delay(10); }));
    await delay(100);
    assert.deepEqual(log, ['send', 'load']);
    assert.equal(sync.dirty, false);
  }
  {
    // A newer step while the first is on its way: only after the last send the page is drawn.
    const { s, log, sync } = make();
    s.set('a', 5, () => sync.send(async () => { log.push('send5'); await delay(40); }));
    await delay(35);
    s.set('a', 10, () => sync.send(async () => { log.push('send10'); await delay(5); }));
    await delay(150);
    assert.deepEqual(log, ['send5', 'send10', 'render']);
  }
  {
    // Nothing waiting: a push is read at once.
    const { log, sync } = make();
    assert.equal(sync.push(), true);
    assert.deepEqual(log, ['load']);
  }
  const pageSrc2 = readFileSync(new URL('../ui/views/plugin-page.js', import.meta.url), 'utf8');
  assert.match(pageSrc2, /sync\.send\(/);
  assert.equal(/pendingAny/.test(pageSrc2), false);
}
// Setup assistant: the order of the steps, the safe default focus, the summary and going back (core/wizard-rules.js).
{
  const W = await import('../ui/core/wizard-rules.js');
  const base = { withTerms: false, termsAccepted: true, intent: null, full: false, lockOn: false, accountPassword: true, autoLogonDone: false, launchers: 2, caseHudWantsApi: false, tvPaired: false, choices: W.freshChoices(), areas: ['games', 'apps', 'browser'] };
  const ctx = (extra = {}) => ({ ...base, choices: W.freshChoices(), ...extra });

  // App mode: no system page at all, straight from the questions to the summary.
  assert.deepEqual(W.order(ctx()), ['welcome', 'areas', 'updates', 'mode', 'look', 'sound', 'summary']);
  // Shell mode chosen: the explanations come right after the question, in a fixed order, then look and sound, then the summary.
  assert.deepEqual(W.order(ctx({ intent: 'full' })), ['welcome', 'areas', 'updates', 'mode', 'shell', 'lock', 'autologon', 'launchers', 'services', 'look', 'sound', 'clientsInfo', 'summary']);
  // The way back is explained too.
  assert.deepEqual(W.order(ctx({ intent: 'light', full: true })), ['welcome', 'areas', 'updates', 'mode', 'revert', 'look', 'sound', 'summary']);
  // No password on the account (it signs in by itself), a sign-in set up already, a lock set already: those pages fall away.
  assert.ok(!W.order(ctx({ intent: 'full', accountPassword: false })).includes('autologon'));
  assert.ok(!W.order(ctx({ intent: 'full', autoLogonDone: true })).includes('autologon'));
  assert.ok(!W.order(ctx({ intent: 'full', lockOn: true })).includes('lock'));
  // The notice is a step on the first start only; accepted, it is skipped when going on and back.
  assert.equal(W.order(ctx({ withTerms: true, termsAccepted: false }))[1], 'terms');
  assert.equal(W.nextOf('welcome', ctx({ withTerms: true, termsAccepted: true })), 'areas');
  assert.equal(W.prevOf('areas', ctx({ withTerms: true, termsAccepted: true })), 'welcome');
  // The game clients: the explanation first, the list only when "set up" was chosen, nothing without Gaming or clients.
  assert.ok(!W.order(ctx({ full: true })).includes('clients'));
  assert.ok(W.order(ctx({ full: true, choices: { ...W.freshChoices(), clients: 'adjust' } })).includes('clients'));
  assert.ok(!W.order(ctx({ full: true, areas: ['apps'] })).includes('clientsInfo'));
  assert.ok(!W.order(ctx({ full: true, launchers: 0 })).includes('clientsInfo'));
  // The TV is asked only when there is something to switch; CaseHUD has no step of its own any more (it is a plugin).
  assert.ok(!W.SEQUENCE.includes('casehud'));
  assert.ok(W.order(ctx({ tvPaired: true })).includes('tv'));
  assert.ok(!W.order(ctx()).includes('tv'));

  // Going on and back runs along the order, also when a decision removes the page itself (declining shell mode on its page).
  const full = ctx({ intent: 'full' });
  assert.equal(W.nextOf('mode', full), 'shell');
  assert.equal(W.prevOf('lock', full), 'shell');
  assert.equal(W.prevOf('look', full), 'services');
  assert.equal(W.prevOf('shell', full), 'mode');
  assert.equal(W.nextOf('shell', ctx({ intent: null })), 'look');
  assert.equal(W.nextOf('sound', ctx()), 'summary');
  assert.equal(W.prevOf('summary', ctx()), 'sound');
  assert.equal(W.prevOf('welcome', ctx()), null);
  // Walking the whole way and back lands where it started.
  for (const c of [ctx(), full, ctx({ intent: 'light', full: true }), ctx({ full: true, caseHudWantsApi: true, tvPaired: true, choices: { ...W.freshChoices(), clients: 'adjust' } })]) {
    const way = W.order(c);
    for (let i = 1; i < way.length; i++) {
      assert.equal(W.nextOf(way[i - 1], c), way[i]);
      assert.equal(W.prevOf(way[i], c), way[i - 1]);
    }
  }
  // The explanations have texts for every risk they list, in both languages.
  const wizDe = (await import('../ui/i18n/de/wizard.js')).default;
  const wizEn = (await import('../ui/i18n/en/wizard.js')).default;
  for (const [id, v] of Object.entries(W.EXPLAINS)) {
    const keys = ['title', 'what', 'changes', 'undo', 'detail', 'safe', 'safeSub', 'go', 'goSub', ...Array.from({ length: v.risks }, (_, i) => `risk${i + 1}`)];
    for (const key of keys) for (const dict of [wizDe, wizEn]) assert.ok(typeof dict[`wizard.x.${id}.${key}`] === 'string', `wizard.x.${id}.${key}`);
    assert.ok(!(`wizard.x.${id}.risk${v.risks + 1}` in wizDe), `${id}: a risk without a place`);
  }
  // Shell mode is explained with the owner's words: HUD, the PC mode as the way out, the uninstall.
  assert.match(wizDe['wizard.x.shell.undo'], /Neustart in den PC-Modus/);
  assert.match(wizEn['wizard.x.shell.undo'], /Restart into PC mode/);
  assert.match(wizDe['wizard.x.shell.undo'], /Deinstallieren/);
  assert.match(wizEn['wizard.x.shell.undo'], /Uninstalling/);
  assert.match(wizDe['wizard.x.shell.undo'], /Einstellungen › HUD/);
  assert.match(wizEn['wizard.x.shell.undo'], /Settings › HUD/);

  // The safe choice is focused first on every explanation page, whatever else is marked; never the risky one.
  const opts = (safeAt, extra = {}) => [0, 1, 2].map((i) => ({ safe: i === safeAt, current: i === 2, chosen: i === 2 && extra.chosen }));
  assert.equal(W.defaultIndex(opts(0)), 0);
  assert.equal(W.defaultIndex(opts(1)), 1);
  assert.equal(W.defaultIndex(opts(0, { chosen: true })), 0);
  assert.equal(W.defaultIndex([{ current: true }, { chip: true, current: true }]), 0);
  assert.equal(W.defaultIndex([{}, { chosen: true }]), 1);
  assert.equal(W.defaultIndex([]), 0);

  // The console setup leaves out what was declined; shell, lock and integration are not up for choice.
  assert.deepEqual(W.skippedSetupSteps(W.freshChoices()).sort(), ['autologon', 'autostart', 'suppress', 'xbox']);
  assert.deepEqual(W.skippedSetupSteps({ ...W.freshChoices(), autologon: true, launchers: true, services: true }), []);
  assert.deepEqual(W.skippedSetupSteps({ ...W.freshChoices(), launchers: true }), ['autologon']);
  assert.ok(!W.skippedSetupSteps(W.freshChoices()).some((s) => ['integration', 'lock', 'shell', 'services'].includes(s)));
  // Even a skip list that names every step leaves the core: integration, lock, shell and the services step (the lock's helper tasks:
  // without them every lock change asks Windows again).
  const everyStep = ['integration', 'autostart', 'services', 'xbox', 'suppress', 'lock', 'shell', 'autologon'].map((id) => ({ id }));
  assert.deepEqual(W.filterSetupSteps(everyStep, everyStep.map((s) => s.id)).map((s) => s.id), ['integration', 'services', 'lock', 'shell']);
  assert.deepEqual(W.filterSetupSteps(everyStep, W.skippedSetupSteps(W.freshChoices())).map((s) => s.id), ['integration', 'services', 'lock', 'shell']);
  assert.equal(W.filterSetupSteps(everyStep, []).length, everyStep.length);

  // Shell mode dropped (the stay choice on the lock page, or on the shell page): stale decisions for pages that fell away list nothing.
  assert.deepEqual(W.summaryPlan(ctx({ intent: null, choices: { ...W.freshChoices(), autologon: true, launchers: true, services: true, lock: false } })).changes, []);
  assert.ok(!W.order(ctx({ intent: null, choices: { ...W.freshChoices(), autologon: true } })).includes('autologon'));
  const stayed = W.summaryPlan(ctx({ intent: null, choices: { ...W.freshChoices(), autologon: true } })).changes;
  assert.ok(!stayed.includes('shell') && !stayed.includes('autologon') && !stayed.includes('lockSetup'));
  // The button on the lock page says what it really does.
  assert.match(wizDe['wizard.x.lock.stay'], /App-Modus/);
  assert.match(wizEn['wizard.x.lock.stay'], /App mode/);
  assert.match(wizEn['wizard.x.lock.staySub'], /not set up/);

  // "Set up" as far as the mode card counts it: a declined automatic sign-in is no missing part, an undecided one is.
  const sec = { couchLock: 'code', hasPasscode: true, autoLogon: false, accountPassword: true };
  const set = { integrationMode: 'full', isShell: true };
  assert.equal(W.isConsoleReady(set, sec), false);
  assert.equal(W.isConsoleReady(set, sec, { autologon: false }), true);
  assert.equal(W.isConsoleReady(set, { ...sec, autoLogon: true }), true);
  assert.equal(W.isConsoleReady(set, { ...sec, accountPassword: false }), true);
  // Still not ready without the shell or the lock, whatever was declined.
  assert.equal(W.isConsoleReady({ ...set, isShell: false }, sec, { autologon: false }), false);
  assert.equal(W.isConsoleReady({ ...set, integrationMode: 'light' }, sec, { autologon: false }), false);
  assert.equal(W.isConsoleReady(set, { ...sec, couchLock: 'off', hasPasscode: false }, { autologon: false }), false);

  // The resume after a language switch takes only what the assistant wrote: known steps (not the result), a yes/no, nothing else.
  assert.deepEqual(W.parseResume('{"step":"areas","firstRun":true}'), { step: 'areas', firstRun: true });
  assert.equal(W.parseResume('{"step":"nope","firstRun":true}'), null);
  assert.equal(W.parseResume('{"step":"result","firstRun":true}'), null);
  assert.equal(W.parseResume('{"step":"areas","firstRun":"yes"}'), null);
  assert.equal(W.parseResume('{"step":"areas"}'), null);
  assert.equal(W.parseResume('[]'), null);
  assert.equal(W.parseResume('null'), null);
  assert.equal(W.parseResume('not json'), null);
  assert.equal(W.parseResume(null), null);
  assert.deepEqual(W.parseResume('{"step":"areas","firstRun":false,"extra":"x"}'), { step: 'areas', firstRun: false });

  // The summary: nothing chosen, nothing changes on the PC; everything else is listed, each with its own line.
  assert.deepEqual(W.summaryPlan(ctx()).changes, []);
  assert.ok(W.summaryPlan(ctx()).kept.includes('nothing'));
  assert.deepEqual(W.summaryPlan(ctx({ intent: 'full' })).changes, ['shell', 'lockSetup']);
  assert.deepEqual(W.summaryPlan(ctx({ intent: 'full', lockOn: true, choices: { ...W.freshChoices(), autologon: true, launchers: true, services: true } })).changes, ['shell', 'autologon', 'launchers', 'services']);
  // An automatic sign-in that does not apply (no password) is not listed even if it was chosen earlier.
  assert.ok(!W.summaryPlan(ctx({ intent: 'full', accountPassword: false, choices: { ...W.freshChoices(), autologon: true } })).changes.includes('autologon'));
  assert.deepEqual(W.summaryPlan(ctx({ intent: 'light', full: true })).changes, ['revert']);
  assert.deepEqual(W.summaryPlan(ctx({ full: true, choices: { ...W.freshChoices(), lock: true } })).changes, ['lockNow']);
  assert.deepEqual(W.summaryPlan(ctx({ full: true, choices: { ...W.freshChoices(), clients: 'adjust', clientModes: { steam: 'demand' } } })).changes, ['clients']);
  assert.deepEqual(W.summaryPlan(ctx({ tvPaired: true, choices: { ...W.freshChoices(), tv: 'on' } })).changes, ['tvOn']);
  assert.deepEqual(W.summaryPlan(ctx({ tvPaired: true, choices: { ...W.freshChoices(), tv: 'both' } })).changes, ['tvBoth']);
  for (const id of ['shell', 'lockSetup', 'lockNow', 'autologon', 'launchers', 'services', 'revert', 'clients', 'tvOn', 'tvBoth']) {
    assert.ok(wizDe[`wizard.sum.${id}`] && wizEn[`wizard.sum.${id}`], `wizard.sum.${id}`);
  }
  // The summary starts on "Back" while the PC is about to change, on "Apply" when it is not: A in a row never sets up a shell.
  assert.equal(W.summaryFocus(W.summaryPlan(ctx())), 'apply');
  assert.equal(W.summaryFocus(W.summaryPlan(ctx({ intent: 'full' }))), 'back');
  assert.equal(W.summaryFocus(W.summaryPlan(ctx({ tvPaired: true, choices: { ...W.freshChoices(), tv: 'on' } }))), 'back');
}

// Tabs of plugins: the list from the host is distrusted once more, views stay stable, the order and the fallback are fixed.
{
  const png = 'data:image/png;base64,iVBORw0KGgo=';
  const tab = (o = {}) => ({ plugin: 'p', id: 'main', page: 'pg', title: 'Lichter', pluginName: 'P', pageTitle: 'Seite', area: 'apps', glyph: null, image: null, origin: 'other', ...o });
  // Good entries pass, in the host's order; the result is a copy.
  const listed = [tab({ plugin: 'a', title: 'Alpha', image: png }), tab({ plugin: 'b', title: 'Beta', glyph: '🏠' })];
  const cleaned = ptabs.cleanPluginTabs(listed);
  assert.deepEqual(cleaned.map((x) => x.title), ['Alpha', 'Beta']);
  assert.equal(cleaned[0].image, png);
  assert.equal(cleaned[1].glyph, '🏠');
  assert.notEqual(cleaned[0], listed[0]);
  // Not a list, or junk in it: nothing.
  assert.deepEqual(ptabs.cleanPluginTabs(null), []);
  assert.deepEqual(ptabs.cleanPluginTabs('x'), []);
  assert.deepEqual(ptabs.cleanPluginTabs([null, 5, 'x', {}, { plugin: 'p' }, tab({ id: '' }), tab({ page: undefined }), tab({ title: 12 })]), []);
  // A plugin tab never takes the name of a built-in tab (look-alikes too); a first-party plugin may carry the former Smart Home title only.
  for (const title of ['Home', 'home', 'H0me', 'Favoriten', 'Spiele', 'Games', 'Apps', 'Browser', 'Downloads', 'News', 'Einstellungen', 'Settings', 'Assistent', 'Ho me']) {
    assert.deepEqual(ptabs.cleanPluginTabs([tab({ title })]), [], title);
  }
  assert.equal(ptabs.cleanPluginTabs([tab({ title: 'Smart Home', origin: 'firstParty' })]).length, 1);
  // Overlong or invisible-character titles are dropped.
  assert.deepEqual(ptabs.cleanPluginTabs([tab({ title: 'x'.repeat(ptabs.MAX_TITLE + 1) }), tab({ title: 'Li‮cht' }), tab({ title: 'Li​cht' }), tab({ title: '   ' })]), []);
  // Pictures: only a PNG data URI; anything else is gone, the tab stays. A long glyph is dropped.
  const odd = ptabs.cleanPluginTabs([tab({ image: 'javascript:alert(1)', glyph: '12345' }), tab({ plugin: 'q', title: 'Q', image: 'data:image/svg+xml;base64,AAAA' })]);
  assert.deepEqual(odd.map((x) => [x.image, x.glyph]), [[null, null], [null, null]]);
  // Bounds: two per plugin, six in all, no second tab with the same title.
  const flood = Array.from({ length: 5 }, (_, p) => Array.from({ length: 4 }, (_, i) => tab({ plugin: `pl${p}`, id: `t${i}`, title: `Name${p}x${i}` }))).flat();
  const bounded = ptabs.cleanPluginTabs(flood);
  assert.equal(bounded.length, ptabs.MAX_TOTAL);
  const counts = {};
  for (const x of bounded) counts[x.plugin] = (counts[x.plugin] ?? 0) + 1;
  for (const n of Object.values(counts)) assert.ok(n <= ptabs.MAX_PER_PLUGIN);
  assert.deepEqual(ptabs.cleanPluginTabs([tab({ plugin: 'a', title: 'Lichter' }), tab({ plugin: 'b', title: 'L1chter' })]).map((x) => x.plugin), ['a']);

  // Stable views: the same tab keeps its view (the active tab is found by identity), a tab that is gone loses it, a changed title is refreshed.
  const made = [];
  const reg = ptabs.tabRegistry((x) => { const v = { title: x.title }; made.push(v); return v; }, (v, x) => { v.title = x.title; });
  const first = reg.sync(cleaned);
  const second = reg.sync([cleaned[0], { ...cleaned[1], title: 'Beta2' }]);
  assert.equal(second[0], first[0]);
  assert.equal(second[1], first[1]);
  assert.equal(second[1].title, 'Beta2');
  assert.equal(made.length, 2);
  reg.sync([cleaned[0]]);
  assert.equal(reg.size, 1);
  assert.notEqual(reg.sync(cleaned)[1], first[1]);

  // Home Assistant is a plugin: its Smart Home tab is the only one. Only the host's first-party mark lets a plugin carry the title.
  const haTab = (o = {}) => tab({ plugin: 'homeassistant', id: 'home', page: 'home', title: 'Smart Home', pluginName: 'Home Assistant', area: 'always', glyph: '🏠', origin: 'firstParty', ...o });
  const withHa = ptabs.cleanPluginTabs([haTab()]);
  assert.equal(withHa.length, 1);
  assert.equal(withHa[0].origin, 'firstParty');
  assert.deepEqual(ptabs.cleanPluginTabs([haTab({ plugin: 'evil', origin: 'other' })]), []);
  // The tab the child sees is the host's call (ChildSafe); the UI only never drops a tab the host sent, so Smart Home stays visible for
  // children: the plugin tab survives the cleaning exactly as the host sent it (area "always", no area check can hide it).
  assert.equal(withHa[0].area, 'always');
  // Order: content tabs, then plugin tabs, then the closing ones (Settings stays last).
  assert.deepEqual(ptabs.arrangeTabs(['home', 'games', 'browser'], ['lumen', 'other'], ['assistant', 'settings']), ['home', 'games', 'browser', 'lumen', 'other', 'assistant', 'settings']);
  assert.deepEqual(ptabs.arrangeTabs(['home'], [], ['settings']), ['home', 'settings']);

  // Fallback: the active tab stays active while it exists; when its plugin is gone the first tab is shown.
  const bar = ['home', 'games', 'lumen', 'settings'];
  assert.equal(ptabs.activeAfter(bar, 2, ['home', 'games', 'lumen', 'settings']), 2);
  assert.equal(ptabs.activeAfter(bar, 2, ['home', 'lumen', 'settings']), 1);
  assert.equal(ptabs.activeAfter(bar, 2, ['home', 'games', 'settings']), 0);
  assert.equal(ptabs.activeAfter([], 0, ['home', 'settings']), 0);
  assert.equal(ptabs.activeAfter(['home'], 5, ['home']), 0);

  // The text for the permission exists in both languages, and the failure note too.
  const { dictionaries: dicts } = await import('../ui/i18n/index.js');
  assert.ok(dicts.de['plugin.capability.tabs'] && dicts.en['plugin.capability.tabs']);
  assert.ok(dicts.de['plugins.tab.unavailable'] && dicts.en['plugins.tab.unavailable']);
  assert.equal(pr.isRisky('tabs'), false);
  assert.ok(dicts.de['plugin.capability.browser_signin'] && dicts.en['plugin.capability.browser_signin']);
  assert.deepEqual(pr.capabilityKeys(['browser.signin', 'tabs']), ['plugin.capability.browser_signin', 'plugin.capability.tabs']);

  // Umlauts and accents are fine; look-alike letters and other scripts are not (the same cases as the host's tests).
  for (const title of ['Küche', 'Übersicht', 'Größe', 'Café', 'Ärger']) assert.equal(ptabs.cleanPluginTabs([tab({ title })]).length, 1, title);
  for (const title of ['Settıngs', 'Settińgs', 'Łock', 'Homǀ', 'Brøwser', 'Ässistent', 'Spiéle', 'Einstellüngen', 'Àpps', 'Лампа', 'Fünfzehn Zeiche']) {
    assert.deepEqual(ptabs.cleanPluginTabs([tab({ title })]), [], title);
  }
  // Titles are cut at what the bar holds.
  assert.equal(ptabs.MAX_TITLE, 14);
  assert.deepEqual(ptabs.cleanPluginTabs([tab({ title: 'x'.repeat(15) })]), []);
  assert.equal(ptabs.cleanPluginTabs([tab({ title: 'x'.repeat(14) })]).length, 1);
  // The look is the host's (same values as PluginTabsTests.The_look_folds_umlauts_and_accents_like_the_ui_does).
  assert.equal(ptabs.look('Küche'), 'kuche');
  assert.equal(ptabs.look('Übersicht'), 'uberslcht');
  assert.equal(ptabs.look('Größe'), 'grosse');
  assert.equal(ptabs.look('Café'), 'cafe');
  assert.equal(ptabs.look('Brøwser'), ptabs.look('Browser'));
  assert.equal(ptabs.look('H0me'), 'home');
  assert.equal(ptabs.look('Smart Home'), 'smarthome');

  // The area: "always" only for CouchHUD's own plugins.
  assert.equal(ptabs.cleanPluginTabs([tab({ area: 'always' })])[0].area, 'apps');
  assert.equal(ptabs.cleanPluginTabs([tab({ area: 'always', origin: 'firstParty' })])[0].area, 'always');
  assert.equal(ptabs.cleanPluginTabs([tab({ area: 'bogus' })])[0].area, 'apps');
  assert.equal(ptabs.cleanPluginTabs([tab({ area: 'games' })])[0].area, 'games');

  // Glyphs: one symbol; built-in icons are for CouchHUD only (a third party gets the plain dot).
  for (const g of ['💡', '🌡️', '◆', '→', '🏠', '👍🏽']) assert.equal(ptabs.cleanGlyph(g), g, g);
  for (const g of ['12345', 'A', '7', 'ab', '💡💡', '💡x', '', '‮', '‍', '💡‍🔒', 'á', 'א', ' ', '', null]) assert.equal(ptabs.cleanGlyph(g), null, String(g));
  for (const g of ['🔒', '⚙', '🔔', '🌡️', '★', '🗑']) {
    assert.equal(ptabs.glyphFor(g, false), ptabs.GENERIC_GLYPH, g);
    assert.equal(ptabs.glyphFor(g, true), g, g);
  }
  assert.equal(ptabs.glyphFor('🏠', false), '🏠');
  assert.equal(ptabs.cleanPluginTabs([tab({ glyph: '🔒' })])[0].glyph, '◆');

  // The bar: no tab icons with more than three plugin tabs.

  // A dropped view is handed to drop() once, and a kept one is not.
  const gone = [];
  const reg2 = ptabs.tabRegistry((x) => ({ title: x.title }), () => {}, (v) => gone.push(v.title));
  const v2 = reg2.sync([cleaned[0], cleaned[1]]);
  reg2.sync([cleaned[0]]);
  assert.deepEqual(gone, [v2[1].title]);
  reg2.sync([cleaned[0]]);
  assert.equal(gone.length, 1);

  // The UI and the host keep the same lists: the reserved titles, the names of CouchHUD's plugins, the first-party titles and the glyphs
  // that are built-in icons are read from the C# file and compared.
  const cs = readFileSync(new URL('../src/CouchHud.Core/Plugins/PluginTabs.cs', import.meta.url), 'utf8');
  const strings = (name) => {
    const body = cs.slice(cs.indexOf(`${name} =`));
    return [...body.slice(0, body.indexOf('];')).matchAll(/"([^"]+)"/g)].map((m) => m[1]);
  };
  assert.deepEqual(strings('ReservedTitles'), ptabs.RESERVED_TITLES);
  assert.deepEqual(strings('FirstPartyTitles'), ptabs.FIRST_PARTY_TITLES);
  const { BUILTIN_GLYPHS } = await import('../ui/core/glyph-icons.js');
  assert.deepEqual(strings('BuiltInGlyphs'), BUILTIN_GLYPHS);
  const names = readFileSync(new URL('../src/CouchHud.Core/Plugins/PackageStatement.cs', import.meta.url), 'utf8');
  const reserved = names.slice(names.indexOf('Reserved = ['), names.indexOf('];', names.indexOf('Reserved = [')));
  assert.deepEqual([...reserved.matchAll(/"([^"]+)"/g)].map((m) => m[1]), ptabs.RESERVED_NAMES);
  assert.ok(cs.includes(`MaxTitle = ${ptabs.MAX_TITLE};`) && cs.includes(`MaxTotal = ${ptabs.MAX_TOTAL};`) && cs.includes(`MaxPerPlugin = ${ptabs.MAX_PER_PLUGIN};`));
}


// Assistant and Settings stay pinned at the end of the tab bar (they never scroll out of sight with many plugin tabs).
assert.deepEqual(ptabs.PINNED_TABS, ['assistant', 'settings']);
assert.equal(ptabs.isPinnedTab({ id: 'settings' }), true);
assert.equal(ptabs.isPinnedTab({ id: 'assistant' }), true);
assert.equal(ptabs.isPinnedTab({ id: 'plugin-lumen' }), false);
assert.equal(ptabs.isPinnedTab(null), false);
// Tab bar widths: everything fits, nothing changes; otherwise the gap gives way, then only the wide tabs shrink (water filling), never below
// the minimum width; when even that is not enough the bar scrolls. No rule hides a tab.
{
  const nat = [100, 80, 80, 120, 70, 90, 140, 80];
  const total = nat.reduce((a, b) => a + b, 0);
  const sum = (a) => a.reduce((x, y) => x + y, 0);
  // Fits: nothing changes.
  let r = fitWidths(nat, total + 7 * 24 + 10, 24, 12, 100);
  assert.deepEqual(r.widths, nat);
  assert.equal(r.gap, 24);
  assert.equal(r.scroll, false);
  // A little short: just the gap gives way, the widths stay.
  r = fitWidths(nat, total + 7 * 20, 24, 12, 100);
  assert.deepEqual(r.widths, nat);
  assert.ok(Math.abs(r.gap - 20) < 1e-9);
  assert.equal(r.scroll, false);
  // Short at the smallest gap: only the wide tabs shrink to a common limit, the short ones keep their width.
  r = fitWidths(nat, total - 60 + 7 * 12, 24, 12, 60);
  assert.equal(r.gap, 12);
  assert.equal(r.scroll, false);
  assert.ok(sum(r.widths) + 7 * 12 <= total - 60 + 7 * 12, 'it fits the room');
  assert.ok(sum(r.widths) + 7 * 12 >= total - 60 + 7 * 12 - nat.length, 'and uses it (rounded down per tab)');
  nat.forEach((w, i) => { assert.ok(r.widths[i] <= w); if (w <= 90) assert.equal(r.widths[i], w, 'a short tab stays'); });
  assert.ok(r.widths[6] < 140 && r.widths[3] < 120, 'the wide ones gave');
  assert.ok(r.widths[6] === r.widths[3] || Math.abs(r.widths[6] - r.widths[3]) <= 1 || r.widths[3] === 120, 'one common limit for those that shrink');
  // Not even the minimum fits: the bar scrolls and the tabs keep their natural width (no cutting and scrolling together).
  r = fitWidths(nat, 300, 24, 12, 75);
  assert.equal(r.scroll, true);
  assert.deepEqual(r.widths, nat);
  assert.equal(r.gap, 24);
  assert.equal(r.content, sum(r.widths) + 7 * 24);
  // Exactly the minimum fits: the tabs shrink to it, no scroll.
  r = fitWidths(nat, sum(nat.map((w) => Math.min(w, 75))) + 7 * 12, 24, 12, 75);
  assert.equal(r.scroll, false);
  nat.forEach((w, i) => assert.equal(r.widths[i], Math.min(w, 75)));
  // Only a name wider than the maximum is shortened, in every case (also while the bar scrolls).
  r = fitWidths([100, 300, 80], 1000, 10, 5, 50, 150);
  assert.deepEqual(r.widths, [100, 150, 80]);
  assert.equal(r.scroll, false);
  r = fitWidths([100, 300, 80, 200], 200, 10, 5, 50, 150);
  assert.equal(r.scroll, true);
  assert.deepEqual(r.widths, [100, 150, 80, 150]);
  assert.deepEqual(fitWidths([100, 300], 1000, 10, 5, 50, NaN).widths, [100, 300], 'a broken maximum is no maximum');
  assert.deepEqual(fitWidths([100, 300], 1000, 10, 5, 50, -4).widths, [100, 300]);
  // Invalid input: nothing is lost, nothing throws.
  assert.deepEqual(fitWidths([100, NaN, -5, Infinity], 500, 10, 5, 50).widths, [100, 0, 0, 0]);
  assert.deepEqual(fitWidths(nat, NaN, 24, 12, 100).widths, nat);
  assert.equal(fitWidths(nat, NaN, 24, 12, 100).scroll, false);
  assert.deepEqual(fitWidths(nat, -10, 24, 12, 100).widths, nat);
  assert.deepEqual(fitWidths(null, 100, 1, 1, 1), { widths: [], gap: 1, scroll: false, content: 0 });
  assert.deepEqual(fitWidths([], 100, 1, 1, 1).widths, []);
  assert.equal(fitWidths([300], 100, 24, 12, 80).widths[0], 100 - 0, 'one tab takes the room it has, never below the minimum');
  // The focused tab never shrinks (keep), the others give instead; a name over the maximum is the only thing that cuts it.
  r = fitWidths([100, 200, 80, 150], 420, 10, 10, 60, 1000, 1);
  assert.equal(r.scroll, false);
  assert.equal(r.widths[1], 200, 'the focused tab keeps its natural width');
  assert.ok(r.widths[0] < 100 || r.widths[3] < 150, 'the others shrink');
  assert.ok(r.widths.reduce((a, b) => a + b, 0) + 30 <= 420);
  r = fitWidths([100, 200, 80, 150], 420, 10, 10, 60, 120, 1);
  assert.equal(r.widths[1], 120, 'a focused name over the maximum is cut at the maximum');
  // Little room: the mode does not depend on the focus (scroll only when the floors without it do not fit); the focused tab takes what is left.
  r = fitWidths([100, 200, 80, 150], 300, 10, 10, 60, 1000, 1);
  assert.equal(r.scroll, false);
  assert.deepEqual(r.widths, [60, 90, 60, 60], 'the others at their floors, the focused tab with the rest (more than its floor, less than its natural width)');
  r = fitWidths([100, 200, 80, 150], 260, 10, 10, 60, 1000, 1);
  assert.equal(r.scroll, true, 'the floors do not fit: scroll, natural widths');
  assert.deepEqual(r.widths, [100, 200, 80, 150]);
  // The same list with every focus: the same mode and gap, and a tab that is not next to the focus moves only by what the focused one takes.
  {
    const list = [100, 90, 70, 130, 60, 110, 150, 80];
    for (const room of [900, 760, 700, 640, 560, 500, 440]) {
      const base = fitWidths(list, room, 12, 8, 64, 1000);
      for (let k = 0; k < list.length; k++) {
        const x = fitWidths(list, room, 12, 8, 64, 1000, k);
        assert.equal(x.scroll, base.scroll, 'room ' + room + ' focus ' + k + ': the mode');
        assert.equal(x.gap, base.gap);
        if (x.scroll) assert.deepEqual(x.widths, base.widths, 'scrolling: nothing depends on the focus');
        else {
          assert.ok(x.widths.reduce((p, q) => p + q, 0) + x.gap * 7 <= room, 'it fits');
          x.widths.forEach((w, i) => { assert.ok(w >= Math.min(list[i], 64) && w <= list[i]); if (i !== k) assert.ok(w <= base.widths[i], 'the others only give'); });
          assert.ok(x.widths[k] >= base.widths[k], 'the focused tab only gets');
        }
      }
    }
  }
  assert.deepEqual(fitWidths([100, 200], 1000, 10, 5, 50, 1000, 7).widths, [100, 200], 'a keep outside the list is nothing');
  // Rounding right at the boundary: natural widths are whole pixels, the room is a fraction. A room 0.5 px short is not "everything fits", and
  // a room 0.5 px long is (nothing is shrunk or scrolled for a rounding error once the caller keeps a pixel of slack per tab).
  r = fitWidths([100, 80, 60], 240 + 2 * 10 - 0.5, 10, 10, 50);
  assert.ok(r.scroll === false && r.widths.reduce((a, b) => a + b, 0) + 20 <= 259.5, 'it fits by shrinking a little, never by overflowing');
  r = fitWidths([100.4, 80.6, 60], 240 + 2 * 10 + 1, 10, 10, 50);
  assert.equal(r.scroll, false);
  assert.ok(r.content <= 261, 'the content never exceeds the room');
  // A skin that asks for a gap below the minimum gap is left alone.
  assert.equal(fitWidths(nat, total + 7 * 10, 10, 12, 100).gap, 10);
  // The skin's minimum width: a number of rem or px inside the allowed range; anything else is the default.
  assert.equal(minTabWidth('7rem', 20), 140);
  assert.equal(minTabWidth(' 112px ', 16), 112);
  assert.equal(minTabWidth(undefined, 20), TAB_MIN_REM * 20);
  assert.equal(minTabWidth('', 20), TAB_MIN_REM * 20);
  assert.equal(minTabWidth('big', 20), TAB_MIN_REM * 20);
  assert.equal(minTabWidth('NaNrem', 20), TAB_MIN_REM * 20);
  assert.equal(minTabWidth('-3rem', 20), TAB_MIN_REM * 20);
  assert.equal(minTabWidth('1rem', 20), TAB_MIN_RANGE[0] * 20, 'too small: lifted to the lowest allowed');
  assert.equal(minTabWidth('0', 20), TAB_MIN_REM * 20, 'no unit: the default');
  assert.equal(minTabWidth('40rem', 20), TAB_MIN_RANGE[1] * 20, 'too big: cut to the highest allowed');
  assert.equal(minTabWidth('5rem', 0), 5 * 16, 'a broken rem size falls back to 16');
}

// A scrolling bar keeps the focused tab whole, the edge between two tabs and room for an arrow only where tabs are hidden; the arrows show
// only on the side with hidden tabs.
{
  // Ten tabs of 100 with a gap of 10, a room of 450 and arrows of 24.
  const boxes = Array.from({ length: 10 }, (_, i) => ({ left: i * 110, width: 100 }));
  assert.equal(tabWindow(boxes, 450, 24, 0, 0), 0, 'the first tab: no scroll');
  assert.equal(tabWindow(boxes, 450, 24, 2, 0), 0, 'inside the window: stays');
  // From the first tab 4 fit in 450 minus the end arrow (4 x 100 + 3 x 10 = 430 > 426: 3 fit), so the fourth needs a move.
  const w = tabWindow(boxes, 450, 24, 3, 0);
  // The window starts at a tab: the scroll is that tab's left edge minus the start arrow's room, and the focused tab is whole in it.
  const k = boxes.findIndex((bx) => Math.abs(bx.left - 24 - w) < 1e-9);
  assert.ok(k > 0, 'the edge falls between two tabs');
  assert.ok(boxes[3].left >= w + 24 && boxes[3].left + 100 <= w + 450 - 24, 'the focused tab is whole, clear of both arrows');
  // Going back: the focused tab before the window becomes the first one.
  assert.equal(tabWindow(boxes, 450, 24, 1, w), 110 - 24);
  assert.equal(tabWindow(boxes, 450, 24, 0, w), 0);
  // The last tab: the window ends at the end of the content, no end arrow, never beyond.
  const end = tabWindow(boxes, 450, 24, 9, 0);
  assert.ok(end <= 1090 - 450 + 1e-9, 'not beyond the end');
  assert.ok(boxes[9].left + 100 <= end + 450 + 1e-9);
  // A small room: the focused tab comes before the gutters (it is whole, the arrows may touch its edges).
  assert.equal(tabWindow(boxes, 140, 24, 3, 0), 310);
  assert.equal(tabWindow(boxes, 100, 24, 3, 0), 330, 'a room as wide as the tab: exactly that tab');
  assert.equal(tabWindow(boxes, 80, 24, 3, 0), 330, 'a room smaller than the tab: its left edge');
  assert.equal(tabWindow(boxes, 140, 24, 0, 500), 0);
  assert.equal(tabWindow(boxes, 140, 24, 9, 0), 950, 'never past the end');
  // It all fits: no scroll. Invalid input: nothing throws, the scroll stays sane.
  assert.equal(tabWindow(boxes, 2000, 24, 5, 0), 0);
  assert.equal(tabWindow(boxes, 450, 24, -1, 200), 200, 'no focused tab: stays');
  assert.equal(tabWindow(boxes, NaN, 24, 3, 50), 50);
  assert.equal(tabWindow([], 450, 24, 0, 50), 50);
  assert.equal(tabWindow([{ left: NaN, width: 1 }], 450, 24, 0, 50), 50);
  assert.equal(tabWindow(boxes, 450, 24, 3, NaN), 0);
  assert.deepEqual(overflowHints(0, 500, 1000), { start: false, end: true });
  assert.deepEqual(overflowHints(200, 500, 1000), { start: true, end: true });
  assert.deepEqual(overflowHints(500, 500, 1000), { start: true, end: false });
  assert.deepEqual(overflowHints(0, 500, 500), { start: false, end: false }, 'it fits: no hint at all');
  assert.equal(TAB_EDGE_SLOP, 2);
  assert.deepEqual(overflowHints(0, 500, 501.5), { start: false, end: false }, 'a pixel of rounding is no hidden tab');
  assert.deepEqual(overflowHints(1.5, 500, 500), { start: false, end: false });
  assert.deepEqual(overflowHints(0, 500, 503), { start: false, end: true });
  assert.deepEqual(overflowHints(3, 500, 503), { start: true, end: false });
  assert.deepEqual(overflowHints(NaN, 500, 1000), { start: false, end: false });
}

// A label that does not fit runs through, with motion on, and only then.
{
  assert.equal(needsMarquee(160, 100, true), true);
  assert.equal(needsMarquee(100, 100, true), false, 'it fits');
  assert.equal(needsMarquee(100.5, 100, true), false, 'within rounding');
  assert.equal(needsMarquee(160, 100, false), false, 'reduced motion: only the ellipsis');
  assert.equal(needsMarquee(NaN, 100, true), false);
  assert.equal(needsMarquee(160, 0, true), false);
  // 40 px to run at 0.05 px/ms = 800 ms: a first pause of one second (a short pass over the tab moves nothing), run 800, pause 1000, back 800.
  assert.equal(marqueeOffset(0, 40), 0);
  assert.equal(marqueeOffset(999, 40), 0);
  assert.equal(marqueeOffset(1000 + 400, 40), 20);
  assert.equal(marqueeOffset(1000 + 800 + 450, 40), 40);
  assert.equal(marqueeOffset(1000 + 800 + 1000 + 400, 40), 20);
  assert.equal(marqueeOffset(2 * (1000 + 800) + 100, 40), 0, 'and again');
  assert.equal(marqueeOffset(1000, 0), 0);
  assert.equal(marqueeOffset(-5, 40), 0);
  assert.equal(marqueeOffset(NaN, 40), 0);
}

// Skins never take a tab away: rules that hide the tab bar or its items (by name, position or as a whole) are problems; styling is not.
{
  assert.deepEqual(tabProblems('[data-skin="user:x"] #tab-list li { font-size: 1.2rem; color: red; border-bottom-width: 0; }'), []);
  assert.deepEqual(tabProblems('[data-skin="user:x"] #tabs .shoulder { display: none; }'), [], 'the LB/RB glyphs may go');
  assert.deepEqual(tabProblems('#bg::after { display: none; } .tile { opacity: 0; }'), []);
  assert.deepEqual(tabProblems('#top { padding-top: 1.8rem; } #top #pad-status { display: none; } #top .x { opacity: 0 }'), [], 'styling #top and hiding a status icon in it is fine');
  assert.deepEqual(tabProblems('[id="status"] { display: none }'), [], 'another id is another element');
  for (const css of [
    '#tab-list li:nth-child(7) { display: none; }',
    '#tab-list li:last-child { visibility: hidden; }',
    '#tab-list > li:not(.active) { opacity: 0 }',
    '#tab-list { display: none !important; }',
    '#tabs { display: none; }',
    '#tab-list li.pinned { width: 0; }',
    '#tab-list li { max-width: 0px; font-size: 0 }',
    '@media (max-width: 1280px) { :root[data-skin="user:x"] #tab-list li { display: none } }',
    '#top #tabs, .other { transform: scale(0); }',
    'li[data-tab="6"] { clip-path: inset(100%); }',
    '.tab-more { display: none; }',
    String.raw`#tab\-list li { display: none }`,
    String.raw`#tab\2d list li { display: none }`,
    String.raw`#\74 ab-list li { DISPLAY: NONE }`,
    '[id="tab-list"] li { display: none }',
    '[id^="tab-l"] li { display: none }',
    "[id*='abs'] { visibility: hidden }",
    '[id="tabs"] li { opacity: 0 }',
    ':is(#tab-list) li { display: none }',
    '#top { display: none }',
    '#top ul > li:last-of-type { display: none }',
    '#top nav { display: none }',
    '@layer x { #tab-list li { display: none } }',
    '#tab-list li { text-indent: -9999px }',
    '#tab-list li { pointer-events: none }',
    '#tab-list li { filter: opacity(0) }',
    '#tab-list li { scale: 0 }'
  ]) assert.ok(tabProblems(css).length >= 1, css);
  assert.equal(tabProblems('/* #tab-list li { display: none } */ .x { color: red }').length, 0, 'comments are ignored');
}

// LB/RB: through the tabs, then the chips (bell, downloads when shown), wrapping at both ends.
{
  const stops = barStops(3, ['bell', 'downloads']);
  assert.equal(stops.length, 5);
  const tab = (i) => ({ kind: 'tab', index: i });
  assert.deepEqual(nextStop(stops, tab(1), 1), tab(2));
  assert.deepEqual(nextStop(stops, tab(2), 1), { kind: 'chip', id: 'bell' });
  assert.deepEqual(nextStop(stops, { kind: 'chip', id: 'bell' }, 1), { kind: 'chip', id: 'downloads' });
  assert.deepEqual(nextStop(stops, { kind: 'chip', id: 'downloads' }, 1), tab(0));
  assert.deepEqual(nextStop(stops, tab(0), -1), { kind: 'chip', id: 'downloads' });
  assert.deepEqual(nextStop(stops, { kind: 'chip', id: 'bell' }, -1), tab(2));
  // Without the downloads chip the bell is the last stop; a chip that went away starts at the first tab.
  const few = barStops(2, ['bell']);
  assert.deepEqual(nextStop(few, tab(1), 1), { kind: 'chip', id: 'bell' });
  assert.deepEqual(nextStop(few, { kind: 'chip', id: 'bell' }, 1), tab(0));
  assert.deepEqual(nextStop(few, { kind: 'chip', id: 'downloads' }, 1), tab(0));
  assert.equal(nextStop([], tab(0), 1), null);
}

// Settings > Privacy: Offline shows every switch below it as off and disabled; only Offline itself stays changeable.
{
  const on = { offline: false, selfUpdateCheck: true, netCatalog: true, catalogUpdateCheck: true, onlineArtwork: true, netNews: false };
  assert.equal(privacyShown(on, 'selfUpdateCheck'), true);
  assert.equal(privacyShown(on, 'netNews'), false);
  assert.equal(privacyDisabled(on, 'selfUpdateCheck'), false);
  const off = { ...on, offline: true };
  assert.equal(privacyShown(off, 'offline'), true);
  for (const key of NET_KEYS) {
    assert.equal(privacyShown(off, key), false, key);
    assert.equal(privacyDisabled(off, key), true, key);
  }
  assert.equal(privacyDisabled(off, 'offline'), false);
  // The plugin update search waits for the catalog switch; both drop the automatic plugin update.
  assert.equal(privacyDisabled({ ...on, netCatalog: false }, 'catalogUpdateCheck'), true);
  // The automatic update waits for the update search (and so for the catalog).
  assert.equal(privacyDisabled({ ...on, pluginAutoUpdate: false }, 'pluginAutoUpdate'), false);
  assert.equal(privacyDisabled({ ...on, catalogUpdateCheck: false }, 'pluginAutoUpdate'), true);
  assert.equal(privacyDisabled({ ...on, netCatalog: false }, 'pluginAutoUpdate'), true);
  // The message names the real reason: Offline, the catalog or the update search, never "Offline" when it is not on.
  assert.equal(privacyDisabledReason(off, 'pluginAutoUpdate'), 'offline');
  assert.equal(privacyDisabledReason({ ...on, catalogUpdateCheck: false }, 'pluginAutoUpdate'), 'search');
  assert.equal(privacyDisabledReason({ ...on, netCatalog: false }, 'pluginAutoUpdate'), 'catalog');
  assert.equal(privacyDisabledReason({ ...on, netCatalog: false }, 'catalogUpdateCheck'), 'catalog');
  assert.equal(privacyDisabledReason(on, 'pluginAutoUpdate'), null);
  assert.equal(privacyDisabledReason(off, 'offline'), null);
  for (const key of ['pluginAutoUpdate', 'catalogUpdateCheck', 'selfUpdateCheck', 'offline']) {
    const reason = privacyDisabledReason({ ...on, netCatalog: false, catalogUpdateCheck: false }, key);
    assert.equal(reason !== null, privacyDisabled({ ...on, netCatalog: false, catalogUpdateCheck: false }, key), key);
  }
  assert.deepEqual(privacyAfter('netCatalog', false), { pluginAutoUpdate: false });
  assert.deepEqual(privacyAfter('catalogUpdateCheck', false), { pluginAutoUpdate: false });
  assert.deepEqual(privacyAfter('catalogUpdateCheck', true), {});
  assert.deepEqual(privacyAfter('netNews', false), {});
  // The wizard's two checkboxes are independent; the automatic update needs the plugin update search.
  assert.deepEqual(updateChoices({ selfUpdateCheck: false, catalogUpdateCheck: true, pluginAutoUpdate: true }), { selfUpdateCheck: false, catalogUpdateCheck: true, pluginAutoUpdate: true });
  assert.deepEqual(updateChoices({ selfUpdateCheck: true, catalogUpdateCheck: false, pluginAutoUpdate: true }), { selfUpdateCheck: true, catalogUpdateCheck: false, pluginAutoUpdate: false });
  assert.deepEqual(updateChoices({}), { selfUpdateCheck: false, catalogUpdateCheck: false, pluginAutoUpdate: false });
}

console.log('ui logic: ok');


// The App/Shell mode switch as a transaction (0.6.2): fail, retry, fail, cancel ends in the exact previous state; a partly failed undo
// names what is left over.
{
  // A fake PC: the switch's steps change these values, the undos put them back, nothing real is touched.
  const pc = { integration: 'light', lock: 'off', shell: false };
  const before = JSON.stringify(pc);
  const persisted = [];
  const tx = createModeTransaction({ from: 'app', persist: async (text) => persisted.push(text) });
  const run = {
    integration: async () => { pc.integration = 'full'; },
    lock: async () => { pc.lock = 'code'; },
    shell: async () => { pc.shell = true; throw new Error('registry refused'); }
  };
  const snapshots = { integration: () => ({ m: pc.integration }), lock: () => ({ m: pc.lock }), shell: () => ({ s: pc.shell }) };
  const undone = [];
  const undoers = {
    integration: async (b) => { if (pc.integration === b.m) return 'unchanged'; pc.integration = b.m; undone.push('integration'); return 'undone'; },
    lock: async (b) => { if (pc.lock === b.m) return 'unchanged'; pc.lock = b.m; undone.push('lock'); return 'undone'; },
    shell: async (b) => { if (pc.shell === b.s) return 'unchanged'; pc.shell = b.s; undone.push('shell'); return 'undone'; }
  };
  const attemptRun = async () => {
    let failed = false;
    for (const id of ['integration', 'lock', 'shell']) {
      // Satisfied steps are skipped, like the page does on a retry; everything else is recorded first.
      if (id === 'integration' && pc.integration === 'full') continue;
      if (id === 'lock' && pc.lock === 'code') continue;
      await tx.begin(id, snapshots[id]());
      try { await run[id](); } catch { failed = true; break; }
    }
    return failed;
  };
  assert.equal(await attemptRun(), true);
  assert.equal(await attemptRun(), true); // retry fails again
  // Retry did not stack: one record per step, the first snapshot.
  assert.deepEqual(tx.ids(), ['integration', 'lock', 'shell']);
  assert.ok(persisted.length > 0 && parseJournal(persisted.at(-1)).entries.length === 3);
  const result = await tx.rollback(undoers);
  assert.deepEqual(undone, ['shell', 'lock', 'integration']); // last changed, first undone
  assert.deepEqual(result.leftover, []);
  assert.equal(JSON.stringify(pc), before);
  assert.equal(tx.pending, false);
  assert.equal(persisted.at(-1), ''); // the host's record is gone
  assert.equal(describeRollback(result, (id) => id, (key) => key).ok, true);
}
{
  // A step that changed nothing is found unchanged and not touched; a failing undo and a step only the person can reverse are leftovers.
  const tx = createModeTransaction({ from: 'shell' });
  await tx.begin('profiles', { p: [] });
  await tx.begin('services', null);
  await tx.begin('light', { m: 'full', s: true });
  let touched = 0;
  const result = await tx.rollback({
    profiles: async () => 'unchanged',
    services: async () => { throw manualUndo(); },
    light: async () => { touched++; throw new Error('admin refused'); }
  });
  assert.equal(touched, 1);
  assert.deepEqual(result.unchanged, ['profiles']);
  assert.deepEqual(result.leftover.map((l) => [l.id, l.manual]), [['light', false], ['services', true]]);
  // The failed undo stays recorded (the next start offers it again); the manual one is reported once and dropped.
  assert.deepEqual(tx.ids(), ['light']);
  const said = describeRollback(result, (id) => `T:${id}`, (key, params) => `${key}|${params?.steps ?? ''}`);
  assert.equal(said.ok, false);
  assert.deepEqual(said.lines, ['consoleSetup.rollback.partial|T:light, T:services', 'consoleSetup.rollback.fix|']);
  // A second try after the cause is gone finishes it.
  const second = await tx.rollback({ light: async () => 'undone' });
  assert.deepEqual(second.leftover, []);
  assert.equal(tx.pending, false);
  // The record: only its shape is read back.
  assert.equal(parseJournal('{"from":"pc","steps":[]}'), null);
  assert.equal(parseJournal('nonsense'), null);
  assert.equal(parseJournal('{"from":"app","steps":[{"id":"lock","before":{"m":"off"}},{"id":"Bad!"}]}'), null); // one bad step refuses the record
  assert.deepEqual(parseJournal('{"from":"app","steps":[{"id":"lock","before":{"m":"off"}}]}'), { from: 'app', entries: [{ id: 'lock', before: { m: 'off' } }] });
}

// Review fixes of the mode switch (0.6.2): records are never trusted, the hold ends with the page, the lock undo is honest.
{
  // Forged or oversized records are refused as a whole; nothing is restored from them.
  assert.equal(validBefore('lock', { m: 'off', c: true }), true);
  assert.equal(validBefore('lock', null), true);
  assert.equal(validBefore('evil', null), false);
  assert.equal(validBefore('lock', { m: 'admin' }), false);
  assert.equal(validBefore('lock', { m: 'off', extra: 1 }), false);
  assert.equal(validBefore('lock', { m: { x: 1 } }), false);
  assert.equal(validBefore('release', { m: 'off' }), false);
  assert.equal(validBefore('profiles', { p: Array(25).fill('a') }), false);
  assert.equal(validBefore('profiles', { p: ['x'.repeat(97)] }), false);
  assert.equal(validBefore('autostart', { e: [['id', 'off']] }), false);
  assert.equal(parseJournal('{"from":"app","steps":[{"id":"lock","before":{"m":"root"}}]}'), null);
  assert.equal(parseJournal('{"from":"app","steps":[{"id":"lock"},{"id":"lock"}]}'), null);
  assert.equal(parseJournal('{"from":"app","steps":[],"x":1}'), null);
  // A forged value handed to a transaction is dropped; the undo never sees it.
  const forged = createModeTransaction({ from: 'app', entries: [{ id: 'lock', before: { m: 'admin' } }] });
  assert.equal(forged.pending, false);
  const direct = createModeTransaction({ from: 'app' });
  await direct.begin('lock', { m: 'admin' });
  let called = 0;
  const forgedResult = await direct.rollback({ lock: async (b) => { called++; if (!b) throw new Error('no record'); return 'undone'; } });
  assert.equal(called, 1);
  assert.equal(forgedResult.leftover.length, 1); // stored as null: the undo refuses instead of guessing

  // The held couch never outlives the page: a retry that never ran (loading), a stopped run, a failed one all end it.
  assert.equal(closeAction('running', true, true), 'none');
  assert.equal(closeAction('rolling', true, true), 'none');
  assert.equal(closeAction('loading', true, true), 'rollback'); // Retry pressed, the page closes before it ran: closing is a cancel
  assert.equal(closeAction('done', true, true), 'rollback');
  assert.equal(closeAction('rolled', true, true), 'release'); // an undo that left something behind is not silently run again on close
  assert.equal(closeAction('overview', false, false), 'release');
  assert.equal(closeAction('done', false, false), 'release');
  assert.equal(closeAction('loading', true, false), 'release');
  {
    // The retry-then-close path end to end with a fake hold: the page closes during "loading" after a failed run.
    let hold = true;
    const pc = { shell: false };
    const tx = createModeTransaction({ from: 'app' });
    await tx.begin('shell', { s: false });
    pc.shell = true;
    const what = closeAction('loading', tx.pending, true);
    if (what === 'rollback') await tx.rollback({ shell: async (b) => { pc.shell = b.s; return 'undone'; } });
    if (what !== 'none') hold = false;
    assert.equal(hold, false);
    assert.equal(pc.shell, false);
    assert.equal(tx.pending, false);
  }

  // The real undo tables against a fake host.
  const host = (init) => {
    const db = {
      settings: { integrationMode: 'light', isShell: false, runStartupApps: true, guideTapInGame: false, suppressCompetitors: false, ...init.settings },
      security: { couchLock: 'off', hasPasscode: false, autoLogon: false, ...init.security },
      startup: init.startup ?? [],
      // Like the host: taskInstalled is the service's health (always true here), installed is the applied selection, pending = they differ.
      services: init.services ?? { services: [], taskInstalled: true, installed: [], paused: [] },
      calls: []
    };
    const api = {
      getSettings: async () => ({ ...db.settings }),
      getSecurity: async () => ({ ...db.security }),
      getStartup: async () => db.startup.map((e) => ({ ...e })),
      getServices: async () => ({ ...JSON.parse(JSON.stringify(db.services)), pending: db.services.services.filter((v) => v.selected).map((v) => v.name).sort().join() !== [...(db.services.installed ?? [])].sort().join() }),
      setSetting: async (k, v) => { db.calls.push(['setSetting', k, v]); db.settings[k] = v; },
      setIntegrationMode: async (m) => { db.calls.push(['setIntegrationMode', m]); db.settings.integrationMode = m; return { mode: m }; },
      setShell: async (v) => { db.calls.push(['setShell', v]); db.settings.isShell = v; return v; },
      setCouchLock: async (m) => { db.calls.push(['setCouchLock', m]); db.security.couchLock = m; if (m === 'off') db.security.hasPasscode = false; },
      setAutoLogon: async (m) => { db.calls.push(['setAutoLogon', m]); db.security.autoLogon = false; },
      setStartupMode: async (id, m) => { db.calls.push(['setStartupMode', id, m]); db.startup.find((e) => e.id === id).mode = m; },
      toggleCouchService: async (n) => { db.calls.push(['toggleCouchService', n]); const x = db.services.services.find((v) => v.name === n); x.selected = !x.selected; },
      setupServiceTask: async (on) => {
        db.calls.push(['setupServiceTask', on]);
        if (on === false) db.services.services.forEach((v) => { v.selected = false; });
        db.services.installed = db.services.services.filter((v) => v.selected).map((v) => v.name);
        db.services.paused = [...db.services.installed];
      }
    };
    return { db, api, state: { info: {} } };
  };
  {
    // Nothing changed since the record: every undo is "unchanged" and touches nothing.
    const h = host({});
    const { SETUP_UNDO } = createUndoTables(h);
    const env = { settings: h.db.settings, security: h.db.security };
    for (const id of ['integration', 'autostart', 'services', 'xbox', 'suppress', 'lock', 'shell', 'autologon']) {
      const before = await SETUP_UNDO[id].snapshot(env);
      assert.equal(validBefore(id, before), true, id);
      assert.equal(await SETUP_UNDO[id].undo(before), 'unchanged', id);
    }
    assert.deepEqual(h.db.calls, []);
    // No record: the undo refuses instead of guessing.
    for (const id of Object.keys(SETUP_UNDO)) await assert.rejects(() => SETUP_UNDO[id].undo(null), /no record/, id);
  }
  {
    // Changed since: only the differing parts are put back.
    const h = host({ startup: [{ id: 'a', mode: 'couch', suggested: 'pc', disabled: false }], services: { services: [{ name: 'WSearch', suggested: true, selected: false }], taskInstalled: true, installed: [], paused: [] } });
    const { SETUP_UNDO } = createUndoTables(h);
    const env = { settings: h.db.settings, security: h.db.security };
    const snaps = { integration: await SETUP_UNDO.integration.snapshot(env), autostart: await SETUP_UNDO.autostart.snapshot(env), services: await SETUP_UNDO.services.snapshot(env), shell: await SETUP_UNDO.shell.snapshot(env) };
    h.db.settings.integrationMode = 'full';
    h.db.settings.isShell = true;
    h.db.startup[0].mode = 'pc';
    h.db.services.services[0].selected = true;
    h.db.services.installed = ['WSearch']; // the setup applied it: the services are paused, the host lists them as installed
    h.db.services.paused = ['WSearch'];
    assert.equal(await SETUP_UNDO.shell.undo(snaps.shell), 'undone');
    assert.equal(await SETUP_UNDO.services.undo(snaps.services), 'undone');
    assert.equal(await SETUP_UNDO.autostart.undo(snaps.autostart), 'undone');
    assert.equal(await SETUP_UNDO.integration.undo(snaps.integration), 'undone');
    assert.deepEqual([h.db.settings.integrationMode, h.db.settings.isShell, h.db.startup[0].mode, h.db.services.services[0].selected, h.db.services.installed, h.db.services.paused], ['light', false, 'couch', false, [], []]);
    assert.ok(h.db.calls.some((c) => c[0] === 'setupServiceTask' && c[1] === true)); // applied by the host, so the services are resumed
    // The selection was applied before the setup (and not pending): it comes back applied, not cleared.
    const kept = host({ services: { services: [{ name: 'WSearch', suggested: true, selected: true }, { name: 'SysMain', suggested: true, selected: false }], taskInstalled: true, installed: ['WSearch'], paused: ['WSearch'] } });
    const keptUndo = createUndoTables(kept).SETUP_UNDO.services;
    const keptBefore = await keptUndo.snapshot({});
    assert.deepEqual(keptBefore, { k: true, s: ['WSearch'] });
    await kept.api.toggleCouchService('SysMain');
    await kept.api.setupServiceTask(true);
    assert.equal(await keptUndo.undo(keptBefore), 'undone');
    assert.deepEqual([kept.db.services.installed, kept.db.services.paused], [['WSearch'], ['WSearch']]);
  }
  {
    // M2: the lock goes back to "off", but a couch code held before is gone with it: a leftover, never "everything is back".
    const h = host({ security: { couchLock: 'off', hasPasscode: true } });
    const { SETUP_UNDO } = createUndoTables(h);
    const before = await SETUP_UNDO.lock.snapshot({ settings: h.db.settings, security: h.db.security });
    assert.deepEqual(before, { m: 'off', c: true });
    h.db.security.couchLock = 'code';
    const tx = createModeTransaction({ from: 'app' });
    await tx.begin('lock', before);
    const result = await tx.rollback({ lock: SETUP_UNDO.lock.undo });
    assert.equal(h.db.security.couchLock, 'off');
    assert.deepEqual(result.leftover.map((l) => [l.id, l.note]), [['lock', 'lockCode']]);
    const said = describeRollback(result, (id) => id, (key) => key);
    assert.equal(said.ok, false);
    assert.ok(said.lines.includes('consoleSetup.rollback.lockCode'));
    // Without a held code before, the same undo is clean.
    const clean = host({ security: { couchLock: 'off', hasPasscode: false } });
    const t2 = createUndoTables(clean).SETUP_UNDO.lock;
    const b2 = await t2.snapshot({ settings: clean.db.settings, security: clean.db.security });
    clean.db.security.couchLock = 'code';
    assert.equal(await t2.undo(b2), 'undone');
  }
  {
    // A cancelled way back with steps only a person can reverse never says "done".
    const { REVERT_UNDO } = createUndoTables(host({}));
    const tx = createModeTransaction({ from: 'shell' });
    await tx.begin('release', null);
    await tx.begin('services', null);
    await tx.begin('autologon', null);
    const result = await tx.rollback({}); // the revert tables have no undo for these three
    assert.equal(result.leftover.length, 3);
    assert.equal(describeRollback(result, (id) => id, (key) => key).ok, false);
    assert.equal(tx.pending, false); // reported once, not asked about at every start
    assert.equal(REVERT_UNDO.autologon, undefined); // a password and an administrator's approval: only the person can give it back
  }
  {
    // What the person reads before choosing "undo" (stub texts): target states, no raw record text.
    const lines = describeTargets([{ id: 'lock', before: { m: 'code', c: true } }, { id: 'shell', before: { s: false } }, { id: 'release', before: null }], (k, p) => `${k}|${p?.value ?? ''}`);
    assert.deepEqual(lines, ['consoleSetup.recover.to.lock|settings.lockMode.code|', 'consoleSetup.recover.to.shell|consoleSetup.recover.valOff|', 'consoleSetup.recover.to.release|']);
  }
  // The code buttons wait at most for the timeout for a silent controller list; a failing list counts as no answer.
  assert.equal(await withTimeout(new Promise(() => {}), 20, null), null);
  assert.equal(await withTimeout(Promise.reject(new Error('x')), 1000, null), null);
  assert.deepEqual(await withTimeout(Promise.resolve([1]), 1000, null), [1]);
  assert.equal(shouldShowCodeKeys(await withTimeout(new Promise(() => {}), 20, null)), true);
}

// The page closed while the last step of a run was in flight and that step failed: nobody is left to decide, so the run undoes itself and ends
// the hold (it used to keep the 30-minute hold with no page to release it).
{
  assert.equal(afterRun(true, true, true), 'rollback');
  assert.equal(afterRun(false, true, true), 'hold');
  assert.equal(afterRun(true, false, true), 'release');
  assert.equal(afterRun(true, true, false), 'release');
  assert.equal(afterRun(false, false, true), 'release');
  let hold = true;
  const pc = { shell: false };
  const tx = createModeTransaction({ from: 'app' });
  await tx.begin('shell', { s: false });
  pc.shell = true; // the last step ran, changed something and then failed while the page was closed (onClose answered 'none' during the run)
  assert.equal(closeAction('running', tx.pending, false), 'none');
  const next = afterRun(true, true, tx.pending);
  if (next === 'rollback') await tx.rollback({ shell: async (b) => { pc.shell = b.s; return 'undone'; } });
  if (next !== 'hold') hold = false;
  assert.deepEqual([hold, pc.shell, tx.pending], [false, false, false]);
  // A later successful retry carries nothing of the failed verdict: the verdict is computed again from the rows of each run.
  const rows = [{ status: 'ok' }, { status: 'done' }];
  assert.equal(rows.some((r) => ['failed', 'aborted'].includes(r.status)), false);
}

// "Finish it" leads nowhere when a run of the other direction asks (it would have to open that direction's page): only "undo" is offered there.
assert.deepEqual(recoveryChoices(), ['undo', 'finish']);
assert.deepEqual(recoveryChoices(true), ['undo', 'finish']);
assert.deepEqual(recoveryChoices(false), ['undo']);

// A notification leads only to a whitelisted screen: the exact name, never a prototype member, another spelling or an address.
for (const bad of ['__proto__', 'constructor', 'toString', 'hasOwnProperty', 'Downloads', 'downloads ', 'https://evil.example', 'C:\Windows\calc.exe', 'settings:updates', '', null, undefined, 5, {}]) {
  assert.equal(notificationTarget({ target: bad }), null, String(bad));
}
assert.equal(notificationTarget({ target: 'downloads' }), 'downloads');
assert.equal(notificationTarget(null), null);
assert.equal(notificationTarget({}), null);

// The bar's chips (bell, downloads) do nothing under a dialog or at the profile picker.
assert.equal(chipsBlocked({ overlay: { id: 'x' } }), true);
assert.equal(chipsBlocked({ pickerHold: true }), true);
assert.equal(chipsBlocked({ overlay: null, pickerHold: false }), false);
assert.equal(chipsBlocked(null), false);

// A search that Privacy forbids or that failed says so; a search that found nothing says "all current".
{
  const tr = (key) => `<${key}>`;
  const offline = 'Offline-Modus: Verbindung zu github.com nicht erlaubt';
  assert.deepEqual(searchProblems({ windowsError: 'Offline-Modus: Verbindung zu Windows Update nicht erlaubt' }, { error: offline }, tr),
    [offline, 'Offline-Modus: Verbindung zu Windows Update nicht erlaubt']);
  // Windows updates pending and the search blocked: the problem is still reported, not hidden behind the list.
  assert.equal(searchMessage({ windows: [{}], windowsError: 'blocked' }, null, 1, tr), 'blocked');
  assert.deepEqual(searchProblems({ windowsError: offline }, { error: offline }, tr), [offline]); // the same text once
  assert.deepEqual(searchProblems({ windowsError: '  ' }, { error: null }, tr), []);
  assert.equal(searchProblems(null, { errorCode: 'rateLimit' }, tr)[0], '<updates.self.err.rateLimit>');
  assert.equal(searchMessage({}, { available: false }, 0, tr), '<updates.allCurrent>');
  assert.equal(searchMessage({}, { available: true }, 1, tr), null);
}

// A search that failed, or that the host turned away because one runs, never claims "all up to date".
{
  const tr = (key) => `<${key}>`;
  assert.equal(searchSucceeded({}, {}, true), true);
  assert.equal(searchSucceeded(undefined, {}, true), false);
  assert.equal(searchSucceeded({}, undefined, true), false);
  assert.equal(searchSucceeded({}, undefined, false), true); // no CouchHUD check in this build
  assert.equal(searchMessage({}, {}, 0, tr, false), null);
  assert.equal(searchMessage({}, { available: false }, 0, tr, true), '<updates.allCurrent>');
  assert.equal(searchMessage({ busy: 'Suche läuft' }, {}, 0, tr, true), '<updates.alreadyRunning>');
  assert.equal(searchMessage({}, { busy: 'Prüfe' }, 0, tr, true), '<updates.alreadyRunning>');
  // An unknown error code never shows as a key: the host's text, else a generic one.
  assert.equal(searchProblems(null, { errorCode: 'weird', error: 'Kaputt' }, tr)[0], 'Kaputt');
  assert.equal(searchProblems(null, { errorCode: 'weird' }, tr)[0], '<updates.searchFailed>');
}

// The detail hero: one key per item and first hero address (a re-render with the same key leaves the layer alone), and a url cannot leave its quotes.
{
  const { cssUrl, heroKey } = await import('../ui/core/hero-rules.js');
  const a = { id: 'x', art: { hero: ['https://art.couchhud/1'] } };
  assert.equal(heroKey(a), heroKey({ ...a, name: 'renamed' }));
  assert.notEqual(heroKey(a), heroKey({ ...a, art: { hero: ['https://art.couchhud/2'] } }));
  assert.notEqual(heroKey(a), heroKey({ ...a, id: 'y' }));
  assert.equal(heroKey({ id: 'x', art: {} }), 'x|');
  assert.equal(cssUrl('https://a/b.png'), 'url("https://a/b.png")');
  assert.ok(!cssUrl('a");background:red;("').slice(5, -2).includes('"'));
  assert.ok(!/[\n\r]/.test(cssUrl('a\nb')));
}

// Parallax: only focus changes between tiles nudge it, never on the detail page (its buttons must not swing the picture);
// the soft layers' overscan covers the largest shift, and the sharp-edged hero and the screen shade are held still against #bg.
{
  const { BG_OVERSCAN_REM, LAYERS, STILL_VIEWS, maxBackdropShift, nudgesParallax } = await import('../ui/core/motion-rules.js');
  assert.equal(nudgesParallax('home', 'tile'), true);
  assert.equal(nudgesParallax('library', 'tile'), true);
  assert.equal(nudgesParallax('detail', 'action'), false);
  assert.equal(nudgesParallax('detail', 'tile'), false);
  assert.equal(nudgesParallax('home', 'action'), false);
  assert.equal(nudgesParallax(undefined, 'action'), false);
  assert.ok(STILL_VIEWS.has('detail'));
  const shift = maxBackdropShift();
  assert.ok(shift.x < BG_OVERSCAN_REM && shift.y < BG_OVERSCAN_REM);
  const layer = (sel) => LAYERS.find(([s]) => s === sel);
  const bg = layer('#bg');
  for (const pinned of ['#bg-hero', '#bg > .bg-shade']) {
    const [, ax, ay] = layer(pinned);
    assert.equal(ax + bg[1], 0, pinned);
    assert.equal(ay + bg[2], 0, pinned);
  }
  // Nothing of the detail page itself is a parallax layer any more.
  assert.ok(!LAYERS.some(([s]) => s.includes('detail')));
  // Every child of #bg in the page and the trailer layer either has the overscan or is held still.
  const css = readFileSync(new URL('../ui/styles.css', import.meta.url), 'utf8').replace(/\r/g, '');
  const rule = (sel) => {
    const at = css.indexOf(`\n${sel} {`);
    return at < 0 ? '' : css.slice(at, css.indexOf('}', at));
  };
  const overscan = `inset: -${BG_OVERSCAN_REM}rem;`;
  for (const soft of ['.bg-layer', '.bg-video']) assert.ok(rule(soft).includes(overscan), soft);
  const html = readFileSync(new URL('../ui/index.html', import.meta.url), 'utf8').replace(/\r/g, '');
  const bgBlock = html.slice(html.indexOf('<div id="bg">'), html.indexOf('<header'));
  const bgChildren = [...bgBlock.matchAll(/<div (?:id="([^"]+)"|class="([^"]+)")/g)].map((m) => (m[1] ? `#${m[1]}` : m[2] ? `.${m[2]}` : '#bg')).filter((c) => c !== '#bg');
  assert.deepEqual(bgChildren, ['.bg-layer', '.bg-layer', '#bg-hero', '.bg-ambient', '.bg-shade']);
  const held = new Set(['#bg-hero', '.bg-shade']);
  for (const child of bgChildren) {
    if (child === '.bg-ambient') continue; // a blurred radial glow with no visible edge
    assert.ok(held.has(child) || rule(child).includes(overscan), child);
  }
}

// Edges: the Home row goes round on single steps, everything else stops (the bump is sound and rumble only); a refused move, a
// wrap or a jump never nudges the parallax, and fast or held steps never pile the backdrop up to a big swing at the end of a row.
{
  const { cursorStep } = await import('../ui/core/paging.js');
  assert.equal(cursorStep(3, 1, 5), 4);
  assert.equal(cursorStep(4, 1, 5), null);
  assert.equal(cursorStep(0, -1, 5), null);
  assert.equal(cursorStep(4, 1, 5, { wrap: true }), 0);
  assert.equal(cursorStep(0, -1, 5, { wrap: true }), 4);
  assert.equal(cursorStep(2, 1, 5, { wrap: true }), 3);
  assert.equal(cursorStep(0, 1, 1, { wrap: true }), null); // one tile: nothing to go round to
  assert.equal(cursorStep(4, 4, 5, { wrap: true }), null); // only single steps wrap (grid rows, pages stop)
  assert.equal(cursorStep(0, 0, 5), null);
  assert.equal(cursorStep(0, 1, 0, { wrap: true }), null);
  assert.equal(cursorStep(7, -1, 5), 4); // a cursor outside a list that shrank comes back to its end
  const { NUDGE, NUDGE_ROOM, PARALLAX_CLAMP, PARALLAX_SPRING, nudgeVelocity, stepDirection } = await import('../ui/core/motion-rules.js');
  // No nudge on a refused move (the focus stays on the same tile), on a wrap or on a jump.
  assert.equal(stepDirection(4, 4), null);
  assert.equal(stepDirection(8, 0), null);
  assert.equal(stepDirection(0, 8), null);
  assert.equal(stepDirection(-1, 2), null);
  assert.deepEqual(stepDirection(3, 4), { x: 1, y: 0 });
  assert.deepEqual(stepDirection(4, 3), { x: -1, y: 0 });
  assert.equal(stepDirection(5, 5, 4), null);
  assert.deepEqual(stepDirection(1, 5, 4), { x: 0, y: 1 });
  assert.deepEqual(stepDirection(3, 4, 4), { x: -1, y: 1 }); // row end to the next row's start: one step
  assert.equal(stepDirection(0, 8, 4), null); // two rows: a jump
  assert.equal(nudgeVelocity(3, 0.2, 0), 3);
  assert.equal(nudgeVelocity(0, 0, 1), NUDGE);
  assert.equal(nudgeVelocity(0, NUDGE_ROOM, 1), 0);
  assert.equal(nudgeVelocity(0, -NUDGE_ROOM, 1), NUDGE); // against the current side: one normal step, never more
  // The spring as motion.js runs it: one step against eight fast steps (held direction, 90 ms apart) ending at the row's end.
  const run = (steps, gap) => {
    let x = 0;
    let v = 0;
    let peak = 0;
    let after = 0;
    const dt = 1 / 60;
    for (let f = 0; f < 240; f++) {
      const t = f * dt;
      const k = Math.floor(t / gap + 1e-9);
      if (k < steps && Math.abs(t - k * gap) < dt / 2) v = nudgeVelocity(v, x, 1);
      v += (-x * PARALLAX_SPRING.k - v * PARALLAX_SPRING.d) * dt;
      x = Math.max(-PARALLAX_CLAMP, Math.min(PARALLAX_CLAMP, x + v * dt));
      peak = Math.max(peak, x);
      if (t > steps * gap) after = Math.min(after, x);
    }
    return { peak, after };
  };
  const one = run(1, 1);
  const held = run(8, 0.09);
  assert.ok(held.peak < PARALLAX_CLAMP * 0.6, `held peak ${held.peak}`);
  assert.ok(held.peak <= one.peak * 1.1, `held ${held.peak} one ${one.peak}`);
  assert.ok(held.after > -0.1, `swing back ${held.after}`);
}

// Square tiles: a skin asks with --tile-art: square; no square picture means the cover, cropped (one answer for every skin).
{
  const { squareArt, tileArtMode } = await import('../ui/core/square-rules.js');
  assert.equal(tileArtMode('square'), 'square');
  assert.equal(tileArtMode(' Square '), 'square');
  assert.equal(tileArtMode('"square"'), 'square');
  for (const other of ['', 'cover', 'squares', 'square x', undefined, null, 5]) assert.equal(tileArtMode(other), 'cover');
  const withSquare = squareArt({ art: { square: ['https://art.couchhud/s'], cover: ['https://art.couchhud/c'] } });
  assert.deepEqual(withSquare.urls, ['https://art.couchhud/s', 'https://art.couchhud/c']);
  assert.equal(withSquare.hasSquare, true);
  assert.equal(withSquare.isCropped('https://art.couchhud/s'), false);
  assert.equal(withSquare.isCropped('https://art.couchhud/c'), true); // the square failed to load: the cover is cropped
  const coverOnly = squareArt({ art: { cover: ['https://art.couchhud/c'] } });
  assert.deepEqual(coverOnly.urls, ['https://art.couchhud/c']);
  assert.equal(coverOnly.hasSquare, false);
  assert.equal(coverOnly.isCropped('https://art.couchhud/c'), true);
  assert.deepEqual(squareArt({ art: { square: ['a'], cover: ['a', 'b'] } }).urls, ['a', 'b']);
  assert.deepEqual(squareArt({ art: { square: [null, '', 5] } }).urls, []);
  assert.deepEqual(squareArt({}).urls, []);
  assert.deepEqual(squareArt(null).urls, []);
}

// 0.6.3 acceptance findings of the mode switch (F3, F4): the run loop and the undo against fake steps and a fake host.
{
  const never = async () => assert.fail('not asked');
  const texts = { cancelled: 'cancelled', notUnlocked: 'locked', needsFirst: (title) => `first ${title}` };
  const makeCtx = (tx, state = {}) => ({
    isCancelled: () => !!state.cancel,
    cancel: () => { state.cancel = true; },
    unlock: async () => true,
    loadEnv: async () => ({}),
    readStatus: async (step) => step.check(),
    snapshotOf: async (step) => step.snapshot?.() ?? null,
    paint: () => state.afterPaint?.(),
    tx,
    deps: {},
    texts
  });

  // F3: the couch code entry is cancelled in the shell setup. The run stops as a cancel (never "5 done, 3 skipped"), the dependent steps are
  // cancelled with it, and the recorded steps are undone to the exact previous state.
  {
    const pc = { integration: 'light', services: false, suppress: false, lock: 'off', shell: false };
    const before = JSON.stringify(pc);
    const mk = (id, requires, check, run, snapshot) => ({ id, title: id, requires, check, run, snapshot });
    const steps = [
      mk('integration', [], () => (pc.integration === 'full' ? 'done' : 'todo'), async () => { pc.integration = 'full'; return { status: 'ok' }; }, () => ({ m: pc.integration })),
      mk('services', ['integration'], () => (pc.services ? 'done' : 'todo'), async () => { pc.services = true; return { status: 'ok' }; }, () => ({ k: pc.services, s: [] })),
      mk('suppress', ['integration'], () => (pc.suppress ? 'done' : 'todo'), async () => { pc.suppress = true; return { status: 'ok' }; }, () => ({ v: pc.suppress })),
      // The real lock rule: the code entry is cancelled and not tried again.
      mk('lock', ['integration'], () => 'todo', async () => {
        const result = await setupCouchLock({ security: { couchLock: 'off', hasPasscode: false }, enroll: async () => 'cancelled', restore: never, askAgain: async () => false });
        return { status: result.status, note: result.note };
      }, () => ({ m: pc.lock, c: false })),
      mk('shell', ['integration', 'lock'], () => (pc.shell ? 'done' : 'todo'), async () => { pc.shell = true; return { status: 'ok' }; }, () => ({ s: pc.shell })),
      mk('autologon', ['lock', 'shell'], () => 'todo', async () => ({ status: 'ok' }), () => ({ on: false }))
    ];
    const rows = steps.map((step) => ({ step, status: 'todo', note: '' }));
    const tx = createModeTransaction({ from: 'app' });
    const verdict = await runRows(rows, makeCtx(tx));
    assert.deepEqual(rows.map((r) => r.status), ['ok', 'ok', 'ok', 'aborted', 'aborted', 'aborted']);
    assert.ok(!rows.some((r) => r.status === 'skipped'));
    assert.deepEqual(verdict, { failedRun: true, stopped: true });
    assert.equal(tx.pending, true);
    const undoers = {
      integration: async (b) => { pc.integration = b.m; return 'undone'; },
      services: async (b) => { pc.services = b.k; return 'undone'; },
      suppress: async (b) => { pc.suppress = b.v; return 'undone'; },
      lock: async (b) => { if (pc.lock === b.m) return 'unchanged'; pc.lock = b.m; return 'undone'; }
    };
    const result = await tx.rollback(undoers);
    assert.deepEqual(result.leftover, []);
    assert.equal(JSON.stringify(pc), before);
    assert.equal(tx.pending, false);
    applyRollbackToRows(rows, result, { notUndone: 'n', cancelled: 'cancelled', cancelledAfterUndo: 'after' });
    // The lock row keeps its own reason (no code entered); the others that never ran lose the generic note.
    assert.deepEqual(rows.map((r) => r.status), ['undone', 'undone', 'undone', 'aborted', 'aborted', 'aborted']);
    assert.equal(rows[3].note, 'noCode');
    assert.deepEqual(rows.slice(4).map((r) => r.note), ['after', 'after']);
    // Asked again and the code then entered: the step is "ok", the run goes on.
    const tries = ['cancelled', 'on'];
    const again = await setupCouchLock({ security: { couchLock: 'off', hasPasscode: false }, enroll: async () => tries.shift(), restore: never, askAgain: async () => true });
    assert.equal(again.status, 'ok');
  }

  // F4: B in the middle of the way back to the app mode. The undo restores the shell state (policies, paused services, tasks), rows and
  // the leftover list say what really happened, and a step that did nothing is not listed.
  {
    const db = {
      settings: { integrationMode: 'full', isShell: true, suppressCompetitors: true },
      security: { couchLock: 'code', hasPasscode: true, autoLogon: false },
      startup: [{ id: 'steam', mode: 'pc' }],
      // Like the host: taskInstalled is the service's health (true all the time); the applied selection is separate; pending = they differ.
      services: { taskInstalled: true, policiesSet: true, installed: ['WSearch', 'SysMain'], paused: ['WSearch', 'SysMain'], services: [{ name: 'WSearch', selected: true }, { name: 'SysMain', selected: true }] },
      calls: []
    };
    const pendingOf = () => db.services.services.filter((v) => v.selected).map((v) => v.name).sort().join() !== [...db.services.installed].sort().join();
    const api = {
      getSettings: async () => ({ ...db.settings }),
      getSecurity: async () => ({ ...db.security }),
      getStartup: async () => db.startup.map((e) => ({ ...e })),
      getServices: async () => ({ ...JSON.parse(JSON.stringify(db.services)), pending: pendingOf() }),
      setSetting: async (k, v) => { db.settings[k] = v; },
      setIntegrationMode: async (m) => {
        db.settings.integrationMode = m;
        if (m === 'light') { db.settings.isShell = false; db.security.couchLock = 'off'; }
        return { mode: m };
      },
      setShell: async (v) => { db.settings.isShell = v; return v; },
      setCouchLock: async (m) => { db.security.couchLock = m; },
      setStartupMode: async (id, m) => { db.startup.find((e) => e.id === id).mode = m; },
      toggleCouchService: async (n) => { const x = db.services.services.find((v) => v.name === n); x.selected = !x.selected; },
      setupServiceTask: async (on) => {
        db.calls.push(['setupServiceTask', on]);
        if (on === false) {
          db.services.services.forEach((v) => { v.selected = false; });
          db.services.installed = [];
          db.services.paused = [];
          db.services.policiesSet = false;
          return;
        }
        db.services.installed = db.services.services.filter((v) => v.selected).map((v) => v.name);
        db.services.paused = [...db.services.installed];
      }
    };
    const { REVERT_UNDO } = createUndoTables({ api, state: { info: {} } });
    const before = JSON.stringify(db);
    const withUndo = (id, run, check) => ({ id, title: id, check, run, snapshot: () => REVERT_UNDO[id]?.snapshot({ settings: db.settings, security: db.security }), undo: REVERT_UNDO[id]?.undo });
    const steps = [
      withUndo('release', async () => { db.services.policiesSet = false; return { status: 'ok' }; }, async () => ((await api.getServices()).policiesSet ? 'todo' : 'done')),
      withUndo('services', async () => { for (const s of db.services.services) s.selected = false; await api.setupServiceTask(false); return { status: 'ok' }; }, async () => 'todo'),
      withUndo('autologon', async () => ({ status: 'ok' }), async () => 'done'),
      withUndo('profiles', async () => { db.startup[0].mode = 'couch'; return { status: 'ok' }; }, async () => 'todo'),
      withUndo('suppress', async () => { db.settings.suppressCompetitors = false; return { status: 'ok' }; }, async () => 'todo'),
      withUndo('light', async () => { await api.setIntegrationMode('light'); return { status: 'ok' }; }, async () => 'todo')
    ];
    const rows = steps.map((step) => ({ step, status: 'todo', note: '' }));
    rows[2].status = 'done'; // the overview saw the automatic sign-in as not set up
    const state = {};
    const tx = createModeTransaction({ from: 'shell' });
    // B lands while the services step is in flight: it ends first (and is recorded), the rest is cancelled.
    state.afterPaint = () => { if (rows[1].status === 'running') state.cancel = true; };
    const verdict = await runRows(rows, makeCtx(tx, state));
    assert.deepEqual(rows.map((r) => r.status), ['ok', 'ok', 'done', 'aborted', 'aborted', 'aborted']);
    assert.deepEqual(verdict, { failedRun: true, stopped: true });
    assert.deepEqual(tx.ids(), ['release', 'services']); // the satisfied step and the cancelled ones were never recorded
    assert.equal(db.services.policiesSet, false);
    assert.equal(db.services.taskInstalled, true); // the service is healthy all along: this does not tell that the tasks are there
    assert.deepEqual(db.services.installed, []);
    db.calls.length = 0;

    const undoers = Object.fromEntries(steps.map((s) => [s.id, s.undo ? (b) => s.undo(b) : () => { throw manualUndo(); }]));
    const result = await tx.rollback(undoers);
    assert.deepEqual(result.leftover, []);
    assert.deepEqual(result.undone, ['services']);
    assert.deepEqual(result.later, ['release']); // the policies are set when the couch locks: not claimed as undone, not a leftover
    // The selection is applied again (services paused again, recorded as installed), not only ticked in the list.
    assert.deepEqual(db.calls, [['setupServiceTask', true]]);
    assert.deepEqual([...db.services.installed].sort(), ['SysMain', 'WSearch']);
    assert.deepEqual([...db.services.paused].sort(), ['SysMain', 'WSearch']);
    // The policies are the host's to set when the hold ends and the couch locks; the undo found their conditions back (lock, full mode).
    db.services.policiesSet = true;
    db.calls.length = 0;
    const after = JSON.stringify(db);
    assert.equal(after.replace('"calls":[]', ''), before.replace('"calls":[]', '')); // exactly the shell state again
    applyRollbackToRows(rows, result, { notUndone: 'n' });
    assert.deepEqual(rows.map((r) => r.status), ['later', 'undone', 'done', 'aborted', 'aborted', 'aborted']);
    assert.equal(describeRollback(result, (id) => id, (key) => key).ok, true);
  }

  // F4: policies that do not come back are a leftover (named, never "done"); a step that changed nothing is "unchanged", not a leftover.
  {
    const db = { services: { policiesSet: false, taskInstalled: true, services: [] }, settings: { integrationMode: 'full' }, security: { couchLock: 'off' } };
    const api = { getServices: async () => db.services, getSettings: async () => db.settings, getSecurity: async () => db.security };
    const { REVERT_UNDO } = createUndoTables({ api, state: { info: {} } });
    await assert.rejects(() => REVERT_UNDO.release.undo({ p: true }), /policies not back/);
    db.security.couchLock = 'code';
    assert.equal(await REVERT_UNDO.release.undo({ p: true }), 'later'); // the lock is back; the host sets the policies when the couch locks
    db.services.taskInstalled = false; // the service is not healthy: the host cannot set them at all
    await assert.rejects(() => REVERT_UNDO.release.undo({ p: true }), /policies not back/);
    db.services.taskInstalled = true;
    db.security.couchLock = 'off';
    assert.equal(await REVERT_UNDO.release.undo({ p: false }), 'unchanged');
    assert.equal(await REVERT_UNDO.services.undo({ k: false, s: [] }), 'unchanged');
    await assert.rejects(() => REVERT_UNDO.release.undo(null), /no record/);
    const tx = createModeTransaction({ from: 'shell' });
    await tx.begin('release', { p: true });
    await tx.begin('services', { k: false, s: [] });
    const result = await tx.rollback({ release: REVERT_UNDO.release.undo, services: REVERT_UNDO.services.undo });
    assert.deepEqual(result.unchanged, ['services']);
    assert.deepEqual(result.leftover.map((l) => l.id), ['release']);
    const rows = [{ step: { id: 'release' }, status: 'ok', note: '' }, { step: { id: 'services' }, status: 'skipped', note: '' }];
    applyRollbackToRows(rows, result, { notUndone: 'n', cancelled: 'c', cancelledAfterUndo: 'a' });
    assert.deepEqual(rows.map((r) => r.status), ['notundone', 'unchanged']);
  }

  // The undo of the last step brings the couch lock back with the mode; when the code is gone that is a leftover, never a quiet loss.
  {
    const db = { settings: { integrationMode: 'light', isShell: false }, security: { couchLock: 'off' }, locked: false };
    const api = {
      getSettings: async () => ({ ...db.settings }),
      getSecurity: async () => ({ ...db.security }),
      setIntegrationMode: async (m) => { db.settings.integrationMode = m; return { mode: m }; },
      setShell: async (v) => { db.settings.isShell = v; return v; },
      setCouchLock: async (m) => { if (db.locked) throw new Error('setCodeFirst'); db.security.couchLock = m; }
    };
    const { REVERT_UNDO } = createUndoTables({ api, state: { info: {} } });
    assert.equal(await REVERT_UNDO.light.undo({ m: 'full', s: true, l: 'code' }), 'undone');
    assert.deepEqual([db.settings.integrationMode, db.settings.isShell, db.security.couchLock], ['full', true, 'code']);
    db.settings = { integrationMode: 'light', isShell: false };
    db.security.couchLock = 'off';
    db.locked = true;
    await assert.rejects(() => REVERT_UNDO.light.undo({ m: 'full', s: true, l: 'code' }), (e) => e.manual === true && e.note === 'lockCode');
    assert.equal(validBefore('light', { m: 'full', s: true, l: 'code' }), true);
    assert.equal(validBefore('light', { m: 'full', s: true, l: 'admin' }), false);
    assert.equal(validBefore('release', { p: true }), true);
    assert.equal(validBefore('release', { p: 'x' }), false);
  }
  // A lock that could not be read is not "off"; a lock that cannot come back says which kind of lock it was.
  {
    const { REVERT_UNDO } = createUndoTables({ api: {}, state: { info: {} } });
    assert.throws(() => REVERT_UNDO.light.snapshot({ settings: {}, security: {} }), /lock not read/);
    assert.throws(() => REVERT_UNDO.light.snapshot({ settings: {}, security: undefined }), /lock not read/);
    assert.deepEqual(REVERT_UNDO.light.snapshot({ settings: { integrationMode: 'full', isShell: true }, security: { couchLock: 'windows' } }), { m: 'full', s: true, l: 'windows' });
    const db = { settings: { integrationMode: 'light', isShell: false }, security: { couchLock: 'off' } };
    const api = { getSettings: async () => ({ ...db.settings }), getSecurity: async () => ({ ...db.security }), setIntegrationMode: async (m) => ({ mode: m }), setShell: async () => true, setCouchLock: async () => { throw new Error('x'); } };
    const tables = createUndoTables({ api, state: { info: {} } });
    await assert.rejects(() => tables.REVERT_UNDO.light.undo({ m: 'light', s: false, l: 'windows' }), (e) => e.note === 'lockMode');
    const said = describeRollback({ leftover: [{ id: 'light', note: 'lockMode' }] }, (id) => id, (key) => key);
    assert.ok(said.lines.includes('consoleSetup.rollback.lockMode') && !said.lines.includes('consoleSetup.rollback.lockCode'));
  }

  // A forged "l" or "p" in the host's record refuses the whole record on the page too.
  assert.equal(parseJournal('{"from":"shell","steps":[{"id":"light","before":{"m":"full","s":true,"l":"admin"}}]}'), null);
  assert.equal(parseJournal('{"from":"shell","steps":[{"id":"release","before":{"p":"yes"}}]}'), null);
  assert.deepEqual(parseJournal('{"from":"shell","steps":[{"id":"release","before":{"p":true}}]}'), { from: 'shell', entries: [{ id: 'release', before: { p: true } }] });

  // One undo at a time: the page closing while the undo waits for the code entry gets the running undo, not a second run.
  {
    const tx = createModeTransaction({ from: 'app' });
    await tx.begin('shell', { s: false });
    let calls = 0;
    let release;
    const gate = new Promise((resolve) => { release = resolve; });
    const undoers = { shell: async () => { calls++; await gate; return 'undone'; } };
    const first = tx.rollback(undoers);
    const second = tx.rollback(undoers);
    release();
    const [a, b] = await Promise.all([first, second]);
    assert.equal(calls, 1);
    assert.equal(a, b);
    assert.equal(tx.pending, false);
    assert.deepEqual((await tx.rollback(undoers)).undone, []); // nothing left: a later call does nothing
    assert.equal(calls, 1);
  }

  // F3 with the real undo tables of the setup against a fake host: the aborted lock step undoes integration, services and the rest.
  {
    const db = {
      settings: { integrationMode: 'light', isShell: false, runStartupApps: true, guideTapInGame: false, suppressCompetitors: false },
      security: { couchLock: 'off', hasPasscode: false, autoLogon: false },
      services: { services: [{ name: 'WSearch', suggested: true, selected: false }], taskInstalled: true, installed: [], paused: [] }
    };
    const api = {
      getSettings: async () => ({ ...db.settings }),
      getSecurity: async () => ({ ...db.security }),
      getStartup: async () => [],
      getServices: async () => ({ ...JSON.parse(JSON.stringify(db.services)), pending: db.services.services.filter((v) => v.selected).map((v) => v.name).join() !== db.services.installed.join() }),
      setSetting: async (k, v) => { db.settings[k] = v; },
      setIntegrationMode: async (m) => { db.settings.integrationMode = m; return { mode: m }; },
      setShell: async (v) => { db.settings.isShell = v; return v; },
      setCouchLock: async (m) => { db.security.couchLock = m; },
      setAutoLogon: async () => {},
      setStartupMode: async () => {},
      toggleCouchService: async (n) => { const x = db.services.services.find((v) => v.name === n); x.selected = !x.selected; },
      setupServiceTask: async () => { db.services.installed = db.services.services.filter((v) => v.selected).map((v) => v.name); db.services.paused = [...db.services.installed]; }
    };
    const { SETUP_UNDO } = createUndoTables({ api, state: { info: {} } });
    const before = JSON.stringify(db);
    const env = () => ({ settings: db.settings, security: db.security });
    const real = (id, run, requires = []) => ({ id, title: id, requires, check: async () => 'todo', run, snapshot: () => SETUP_UNDO[id].snapshot(env()), undo: SETUP_UNDO[id].undo });
    const steps = [
      real('integration', async () => { await api.setIntegrationMode('full'); return { status: 'ok' }; }),
      real('services', async () => { await api.toggleCouchService('WSearch'); await api.setupServiceTask(); return { status: 'ok' }; }, ['integration']),
      real('suppress', async () => { await api.setSetting('suppressCompetitors', true); return { status: 'ok' }; }, ['integration']),
      real('lock', async () => {
        const result = await setupCouchLock({ security: db.security, enroll: async () => 'cancelled', restore: never, askAgain: async () => false });
        return { status: result.status, note: result.note };
      }, ['integration']),
      real('shell', async () => { await api.setShell(true); return { status: 'ok' }; }, ['integration', 'lock'])
    ];
    const rows = steps.map((step) => ({ step, status: 'todo', note: '' }));
    const tx = createModeTransaction({ from: 'app' });
    const verdict = await runRows(rows, makeCtx(tx));
    assert.deepEqual(verdict, { failedRun: true, stopped: true });
    assert.notEqual(JSON.stringify(db), before);
    const result = await tx.rollback(Object.fromEntries(steps.map((s) => [s.id, s.undo])));
    assert.deepEqual(result.leftover, []);
    assert.equal(JSON.stringify(db), before);
  }
  // An aborted row that was undone keeps its reason and gets the outcome appended.
  {
    const rows = [{ step: { id: 'lock' }, status: 'aborted', note: 'noCode' }, { step: { id: 'shell' }, status: 'aborted', note: 'generic' }];
    applyRollbackToRows(rows, { undone: ['lock', 'shell'], unchanged: [], later: [], leftover: [] }, { notUndone: 'n', cancelled: 'generic', cancelledAfterUndo: 'a', undone: 'Undone' });
    assert.deepEqual(rows.map((r) => [r.status, r.note]), [['undone', 'noCode · Undone'], ['undone', '']]);
  }
  assert.ok(SATISFIED.has('ok'));
}
console.log('mode switch findings: ok');

// The undo shown row by row: tx.rollback reports every step as it runs (last first), the page paces what it shows and ends with the
// same row states as applyRollbackToRows.
{
  const tx = createModeTransaction({ from: 'app' });
  for (const id of ['integration', 'autostart', 'services', 'shell']) await tx.begin(id, null);
  const events = [];
  const undoers = {
    integration: async () => 'unchanged',
    autostart: async () => 'undone',
    services: async () => { throw new Error('boom'); },
    shell: async () => 'undone'
  };
  const result = await tx.rollback(undoers, { onStep: (e) => events.push(`${e.phase}:${e.id}${e.outcome ? `:${e.outcome}` : ''}`) });
  // Bottom to top, every step started before it ends, the next one only after.
  assert.deepEqual(events, ['start:shell', 'end:shell:undone', 'start:services', 'end:services:leftover', 'start:autostart', 'end:autostart:undone', 'start:integration', 'end:integration:unchanged']);
  assert.deepEqual(result.undone, ['shell', 'autostart']);
  assert.deepEqual(result.leftover.map((l) => l.id), ['services']);
  assert.deepEqual(['integration', 'autostart', 'services', 'shell', 'xbox'].map((id) => rollbackOutcome(result, id)), ['unchanged', 'undone', 'leftover', 'undone', null]);

  // A watcher that throws never stops the undo (its error is only logged).
  const logError = console.error;
  console.error = () => {};
  const tx2 = createModeTransaction({ from: 'app' });
  await tx2.begin('xbox', null);
  await tx2.begin('suppress', null);
  const r2 = await tx2.rollback({ xbox: async () => 'undone', suppress: async () => 'undone' }, { onStep: () => { throw new Error('paint'); } });
  console.error = logError;
  assert.deepEqual(r2.undone, ['suppress', 'xbox']);
  assert.equal(tx2.pending, false);
}
{
  // The row each end event shows is the row the end of the undo sets (shown live from the state before, applied once at the end).
  const texts = { notUndone: 'n', cancelled: 'generic', cancelledAfterUndo: 'after', undone: 'Undone', later: 'Set again when the couch locks' };
  const make = () => [
    { step: { id: 'release' }, status: 'ok', note: '' },
    { step: { id: 'integration' }, status: 'done', note: '' },
    { step: { id: 'services' }, status: 'ok', note: 'x' },
    { step: { id: 'lock' }, status: 'aborted', note: 'noCode' },
    { step: { id: 'shell' }, status: 'aborted', note: 'generic' },
    { step: { id: 'autologon' }, status: 'failed', note: 'err' }
  ];
  const result = { undone: ['lock', 'shell'], unchanged: ['autologon'], later: ['release'], leftover: [{ id: 'services', error: 'e' }] };
  const live = make().map((row) => rollbackRowView(row, rollbackOutcome(result, row.step.id), texts));
  const rows = make();
  applyRollbackToRows(rows, result, texts);
  assert.deepEqual(rows.map((r) => ({ status: r.status, note: r.note })), live);
  assert.deepEqual(rows.map((r) => [r.status, r.note]), [
    ['later', 'Set again when the couch locks'], // the short label says "later", the note the whole sentence
    ['done', ''], // never recorded: untouched
    ['notundone', 'n'],
    ['undone', 'noCode · Undone'],
    ['undone', ''],
    ['unchanged', '']
  ]);
  // A stopped row the undo never reached loses the generic "try again carries on".
  assert.deepEqual(rollbackRowView({ status: 'aborted', note: 'generic' }, null, texts), { status: 'aborted', note: 'after' });
}
{
  // The pacing: events are shown in the order they came, a 'start' stays at least one beat, and pushing never waits (the undo runs on).
  const shown = [];
  const waits = [];
  let release;
  const gate = new Promise((resolve) => { release = resolve; });
  const show = createStepShow({ beat: 250, apply: (e) => shown.push(`${e.phase}:${e.id}`), wait: (ms) => { waits.push(ms); return gate; } });
  const tx = createModeTransaction({ from: 'app' });
  for (const id of ['xbox', 'suppress', 'shell']) await tx.begin(id, null);
  const result = await tx.rollback({ xbox: async () => 'undone', suppress: async () => 'undone', shell: async () => 'unchanged' }, { onStep: (e) => show.push(e) });
  // The undo is through while the screen is still at the first beat.
  assert.deepEqual(result.undone, ['suppress', 'xbox']);
  assert.deepEqual(shown, ['start:shell']);
  assert.deepEqual(waits, [250]);
  release();
  await show.idle();
  assert.deepEqual(shown, ['start:shell', 'end:shell', 'start:suppress', 'end:suppress', 'start:xbox', 'end:xbox']);
  assert.deepEqual(waits, [250, 250, 250]); // one beat per row, none after a result
  // A show that went idle starts again with the next event; an error while painting does not stop it.
  const again = [];
  const logError = console.error;
  console.error = () => {};
  const show2 = createStepShow({ beat: 0, apply: (e) => { again.push(e.id); if (e.id === 'x') throw new Error('paint'); } });
  show2.push({ id: 'x', phase: 'end' });
  await show2.idle();
  show2.push({ id: 'y', phase: 'end' });
  await show2.idle();
  console.error = logError;
  assert.deepEqual(again, ['x', 'y']);
}
console.log('undo progress: ok');

// Power menu: CouchHUD's own entries on top, the computer's below; the mode decides PC mode vs. quit and the restart into PC mode.
{
  const ids = (shell, opts) => powerGroups(shell, opts).map((g) => [g.id, g.ids]);
  assert.deepEqual(ids(true), [['app', ['restart-app', 'pc-mode', SLEEP_TIMER]], ['pc', ['standby', 'restart', 'restart-pc', 'shutdown']]]);
  assert.deepEqual(ids(false), [['app', ['restart-app', 'quit', SLEEP_TIMER]], ['pc', ['standby', 'restart', 'shutdown']]]);
  // The Guide menu opens it without the timer (it has its own row).
  assert.deepEqual(ids(false, { sleepTimer: false }), [['app', ['restart-app', 'quit']], ['pc', ['standby', 'restart', 'shutdown']]]);
  // Headings are i18n keys with plain text in both languages.
  assert.deepEqual(POWER_GROUPS.map((g) => g.heading), ['power.group.app', 'power.group.pc']);
  for (const g of POWER_GROUPS) assert.ok(/^[A-Za-z]+$/.test(deCore[g.heading]) && /^[A-Za-z]+$/.test(enCore[g.heading]), g.heading);
  // forMode: shell has no quit, app mode has no restart into PC mode and "pc-mode" is quitting.
  assert.equal(powerForMode('quit', true), null);
  assert.equal(powerForMode('pc-mode', true), 'pc-mode');
  assert.equal(powerForMode('pc-mode', false), 'quit');
  assert.equal(powerForMode('restart-pc', false), null);
  assert.equal(powerForMode('shutdown', false), 'shutdown');
  // Only what ends the session for everybody is tinted as dangerous.
  assert.deepEqual(['standby', 'pc-mode', 'quit', 'restart-app', 'restart', 'restart-pc', 'shutdown', SLEEP_TIMER].filter(isDangerous), ['restart', 'restart-pc', 'shutdown']);
  // Focus starts on standby; a cancelled question brings the menu back on its own entry; an unknown id falls back to standby.
  const flat = powerGroups(true).flatMap((g) => g.ids);
  assert.equal(flat[powerIndex(flat, 'standby')], 'standby');
  for (const id of flat) assert.equal(flat[powerIndex(flat, id)], id);
  assert.equal(flat[powerIndex(flat, 'quit')], 'standby');
  assert.equal(powerIndex([], 'standby'), 0);
  const app = powerGroups(false).flatMap((g) => g.ids);
  assert.equal(app[powerIndex(app, 'quit')], 'quit');
  // The default layout is the tiles; the menu has no "Abbrechen" entry any more (B, the hint or a click next to it close it).
  const powerSrc = readFileSync(new URL('../ui/core/power.js', import.meta.url), 'utf8');
  assert.match(powerSrc, /export const POWER_MENU_LAYOUT = 'tiles';/);
  assert.doesNotMatch(powerSrc, /power\.cancel/);
  // Restarting CouchHUD and restarting the PC have different icons (not two arrows of the same shape), and both are drawn.
  const { glyphName } = await import('../ui/core/glyph-icons.js');
  const iconOf = (id) => new RegExp(`id: '${id}',\\s*icon: '([^']+)'`).exec(powerSrc)?.[1];
  const icons = ['restart-app', 'restart', 'restart-pc', 'shutdown', 'standby', 'pc-mode'].map((id) => glyphName(iconOf(id)));
  assert.ok(icons.every(Boolean), String(icons));
  assert.equal(new Set(icons).size, icons.length, String(icons));
  // The timer shows its remaining time once (a live value, no detail while it runs), and B in its picker goes back to the menu.
  assert.match(powerSrc, /get detail\(\) \{ return sleepTimer\.at \? '' : sleepTimerDetail\(\); \}/);
  assert.match(powerSrc, /get value\(\) \{ return sleepTimer\.at \? sleepTimerDetail\(\) : ''; \}/);
  assert.match(powerSrc, /run: \(\) => pickSleepTimer\(back\)/);
  assert.match(powerSrc, /if \(minutes === undefined\) return onCancel\?\.\(\);/);
  assert.match(powerSrc, /sleepTimerEntry\(\(\) => reopen\(SLEEP_TIMER\)\)/);
  // A click lands on the entry's own index: group headings sit between the entries and are no click target.
  const mouseSrc = readFileSync(new URL('../ui/components/mouse.js', import.meta.url), 'utf8');
  assert.match(mouseSrc, /\.menu li:not\(\.menu-group\)/);
  assert.match(mouseSrc, /node\.dataset\.index/);
}
console.log('power menu groups: ok');

// Sleep timer: shut down (default) or standby; unknown stored values fall back to shut down, never to the raw value.
{
  assert.equal(DEFAULT_TIMER_ACTION, 'shutdown');
  assert.deepEqual(TIMER_ACTIONS, ['shutdown', 'standby']);
  assert.equal(timerAction('standby'), 'standby');
  assert.equal(timerAction('shutdown'), 'shutdown');
  for (const bad of [null, undefined, '', 'sleep', 'restart', 'restart-pc', 'quit', 'Standby', ' shutdown', 'shutdown;restart', '__proto__', 'toString', 1, {}, ['standby']]) {
    assert.equal(timerAction(bad), 'shutdown', String(bad));
  }
  // Texts name the action, in both languages; an unknown action gets the shut-down texts.
  for (const a of TIMER_ACTIONS) for (const key of Object.values(timerTexts(a))) assert.ok(deCore[key] && enCore[key], key);
  assert.deepEqual(timerTexts('bogus'), timerTexts('shutdown'));
  assert.match(deCore[timerTexts('shutdown').status], /^Herunterfahren in \{n\} Min\.$/);
  assert.match(deCore[timerTexts('standby').status], /^Standby in \{n\} Min\.$/);
  assert.match(enCore[timerTexts('shutdown').status], /^Shut down in \{n\} min$/);
  assert.match(deCore[timerTexts('shutdown').warn], /heruntergefahren/);
  // The gate is the entry's own: whatever unlock shut down or standby carry, the timer carries the same; a tampered action is
  // gated like shut down, never like nothing.
  const unlocks = { shutdown: 'lock-shutdown', standby: 'lock-standby' };
  assert.equal(timerUnlock('shutdown', (id) => unlocks[id]), 'lock-shutdown');
  assert.equal(timerUnlock('standby', (id) => unlocks[id]), 'lock-standby');
  assert.equal(timerUnlock('restart', (id) => unlocks[id]), 'lock-shutdown');
  assert.equal(timerUnlock('shutdown', () => undefined), '');
  // The warning comes a minute before the end, at once for shorter timers.
  assert.equal(TIMER_WARN_MS, 60_000);
  assert.equal(warnDelay(30 * 60_000), 29 * 60_000);
  assert.equal(warnDelay(10_000), 0);
  // power.js: the timer runs a power entry's own run() (no host call of its own), checks the entry's unlock with ensureUnlocked,
  // B in the second step goes back to the minutes, and the warning is a controller question with "Jetzt".
  const src = readFileSync(new URL('../ui/core/power.js', import.meta.url), 'utf8');
  assert.equal((src.match(/api\.power\(/g) || []).length, 4, 'only the four entries call api.power');
  assert.match(src, /api\.power\('sleep'\)/);
  assert.match(src, /api\.power\('shutdown'\)/);
  assert.doesNotMatch(src, /api\.power\([^')]/);
  assert.match(src, /const action = byId\(timerAction\(sleepTimer\.action\)\);/);
  assert.match(src, /const unlock = timerUnlock\(action, \(id\) => byId\(id\)\?\.unlock\);\r?\n\s*if \(unlock && !\(await ensureUnlocked\(unlock\)\)\) return;/);
  assert.match(src, /\} while \(action === undefined\);/);
  assert.match(src, /t\('power\.timerNow'\)/);
  assert.match(src, /tag: WARN_TAG/);
  // The menu's refresh looks at the running timer, not at its own sleepTimer option.
  assert.match(src, /refreshMs: sleepTimer && timerRunning\(\) \? 15_000 : 0/);
}
console.log('sleep timer action: ok');

// Tiles: rows per group; left/right in reading order, up/down keep the column (the last tile of a shorter row), bump at the edges.
{
  const rows = groupRows(['a', 'a', 'a', 'b', 'b', 'b', 'b']);
  assert.deepEqual(rows, [[0, 1, 2], [3, 4, 5, 6]]);
  assert.deepEqual(groupRows([]), []);
  assert.deepEqual(groupRows([undefined, undefined]), [[0, 1]]);
  assert.equal(gridMove(rows, 0, 'right'), 1);
  assert.equal(gridMove(rows, 2, 'right'), 3);
  assert.equal(gridMove(rows, 3, 'left'), 2);
  assert.equal(gridMove(rows, 0, 'left'), -1);
  assert.equal(gridMove(rows, 6, 'right'), -1);
  assert.equal(gridMove(rows, 1, 'down'), 4);
  assert.equal(gridMove(rows, 4, 'up'), 1);
  assert.equal(gridMove(rows, 6, 'up'), 2);
  assert.equal(gridMove(rows, 0, 'up'), -1);
  assert.equal(gridMove(rows, 5, 'down'), -1);
  assert.equal(gridMove(rows, 9, 'down'), -1);
  assert.equal(gridMove(rows, 0, 'a'), -1);
  // App mode: two rows of three.
  const app = groupRows(['a', 'a', 'a', 'b', 'b', 'b']);
  assert.equal(gridMove(app, 5, 'up'), 2);
  assert.equal(gridMove(app, 2, 'down'), 5);
}
console.log('menu grid: ok');
// The shared busy state (ui/core/busy.js): a second press while an action runs is ignored, the state clears on success and on error,
// the spinner stands still with reduced motion.
{
  const { createBusy, markBusy, prefersReducedMotion } = await import('../ui/core/busy.js');
  const b = createBusy();
  const seen = [];
  b.onChange((key, on) => seen.push(`${key}:${on}`));
  let calls = 0;
  let ignored = 0;
  let release;
  const first = b.run('k', () => { calls++; return new Promise((r) => { release = r; }); });
  assert.equal(b.isBusy('k'), true);
  assert.equal(b.anyBusy('k'), true);
  const second = await b.run('k', () => { calls++; }, { onIgnored: () => ignored++ });
  assert.deepEqual(second, { ignored: true });
  assert.equal(calls, 1);
  assert.equal(ignored, 1);
  // Another key is independent.
  assert.deepEqual(await b.run('other', () => 7), { value: 7 });
  release('done');
  assert.deepEqual(await first, { value: 'done' });
  assert.equal(b.isBusy('k'), false);
  assert.deepEqual(seen, ['k:true', 'other:true', 'other:false', 'k:false']);
  // An error clears the state too and is passed on.
  await assert.rejects(b.run('k', async () => { throw new Error('boom'); }), /boom/);
  assert.equal(b.isBusy('k'), false);
  assert.deepEqual(await b.run('k', () => 1), { value: 1 });

  // A small fake node: classes, attributes, children.
  const fakeNode = () => {
    const classes = new Set();
    const node = {
      attrs: {}, children: [],
      classList: { toggle: (c, on) => (on ? classes.add(c) : classes.delete(c)), contains: (c) => classes.has(c) },
      get className() { return [...classes].join(' '); },
      set className(v) { classes.clear(); for (const c of v.split(' ').filter(Boolean)) classes.add(c); },
      setAttribute(k, v) { node.attrs[k] = v; },
      removeAttribute(k) { delete node.attrs[k]; },
      append(child) { child.parent = node; node.children.push(child); },
      remove() { node.parent.children = node.parent.children.filter((c) => c !== node); },
      querySelector: (sel) => (sel === ':scope > .busy-spinner' ? node.children.find((c) => c.classList.contains('busy-spinner')) ?? null : null)
    };
    return node;
  };
  const doc = { createElement: () => fakeNode() };
  const button = fakeNode();
  markBusy(button, true, { reducedMotion: false, doc });
  markBusy(button, true, { reducedMotion: false, doc }); // again: still one spinner
  assert.equal(button.classList.contains('is-busy'), true);
  assert.equal(button.attrs['aria-busy'], 'true');
  assert.equal(button.attrs['aria-disabled'], 'true');
  assert.equal(button.children.length, 1);
  assert.equal(button.children[0].classList.contains('spinner'), true);
  assert.equal(button.children[0].classList.contains('static'), false);
  assert.equal(button.children[0].attrs['aria-hidden'], 'true');
  markBusy(button, true, { reducedMotion: true, doc });
  assert.equal(button.children[0].classList.contains('static'), true); // reduced motion: a still ring, no spinning
  markBusy(button, false, { doc });
  assert.equal(button.classList.contains('is-busy'), false);
  assert.equal(button.attrs['aria-busy'], undefined);
  assert.equal(button.attrs['aria-disabled'], undefined);
  assert.equal(button.children.length, 0);
  assert.equal(markBusy(null, true, { doc }), null);
  // Reduced motion: CouchHUD's own switch (data-motion="off") or the system's setting.
  assert.equal(prefersReducedMotion({ documentElement: { dataset: { motion: 'off' } } }, {}), true);
  assert.equal(prefersReducedMotion({ documentElement: { dataset: { motion: 'on' } } }, { matchMedia: () => ({ matches: true }) }), true);
  assert.equal(prefersReducedMotion({ documentElement: { dataset: { motion: 'on' } } }, { matchMedia: () => ({ matches: false }) }), false);
  assert.equal(prefersReducedMotion(undefined, undefined), false);
}
console.log('busy state: ok');

// The Downloads tab: the list holds only what loads or waits to be installed; searching and the games that are not installed are footer
// shortcuts (X, Y), and the search's state stands by the heading.
{
  const { downloadRows, downloadShortcuts, finishedMenu } = await import('../ui/core/downloads-rules.js');
  const { searchOutcome, searchStatus } = await import('../ui/core/update-search.js');
  const entries = [
    { id: 'a', state: 'done' }, { id: 'b', state: 'downloading' }, { id: 'c', state: 'failed' }, { id: 'd', state: 'paused' }
  ];
  const reboot = { kind: 'pending', open: true, label: 'restart' };
  const pending = [{ kind: 'pending', label: 'Windows' }, { kind: 'pending', label: 'Plugin' }];
  const rows = downloadRows({ entries, first: [reboot], pending });
  assert.deepEqual(rows.map((r) => r.entry?.id ?? r.label), ['b', 'd', 'restart', 'Windows', 'Plugin', 'a', 'c']);
  assert.ok(rows.every((r) => r.kind === 'entry' || r.kind === 'pending'));
  assert.deepEqual(downloadRows(), []);
  assert.deepEqual(downloadShortcuts({ canSearch: true, searching: false, gamesOn: true }),
    [{ button: 'X', id: 'search', busy: false }, { button: 'Y', id: 'uninstalled', busy: false }]);
  assert.equal(downloadShortcuts({ canSearch: true, searching: true, gamesOn: true })[0].busy, true);
  assert.deepEqual(downloadShortcuts({ canSearch: true, gamesOn: false }).map((s) => s.id), ['search']); // Gaming off: no Y
  assert.deepEqual(downloadShortcuts({ canSearch: false, gamesOn: true }).map((s) => s.id), ['uninstalled']); // no update state yet
  // "Remove finished" moved from X into a finished line's menu, when there is more than one.
  assert.deepEqual(finishedMenu(['remove'], entries), ['remove', 'clearFinished']);
  assert.deepEqual(finishedMenu(['remove'], [{ state: 'done' }, { state: 'downloading' }]), ['remove']);
  assert.deepEqual(finishedMenu(['openClient'], entries), ['openClient']);
  // The page's source builds no action rows any more.
  const src = readFileSync(new URL('../ui/views/downloads.js', import.meta.url), 'utf8');
  assert.ok(!src.includes("kind: 'uninstalled'") && !src.includes('searchRow'), 'Downloads list has no action rows');
  // The status line: running, the host at work, the outcome of this visit, a problem, or when it last searched.
  const tr = (key, p) => (p ? `<${key}:${JSON.stringify(p)}>` : `<${key}>`);
  const ago = () => '5 min';
  assert.deepEqual(searchStatus({ searching: true, updates: { checkedAt: 1 } }, tr), { text: '<updates.searching>', busy: true });
  assert.deepEqual(searchStatus({ updates: { busy: 'Installing …' } }, tr), { text: 'Installing …', busy: true });
  assert.deepEqual(searchStatus({ updates: { busy: true } }, tr), { text: '<updates.searching>', busy: true });
  assert.deepEqual(searchStatus({ updates: { checkedAt: 1 }, outcome: 'All current', ago }, tr), { text: 'All current · <updates.lastChecked:{"ago":"5 min"}>', busy: false });
  assert.deepEqual(searchStatus({ updates: { checkedAt: 1, windowsError: 'blocked' }, ago }, tr), { text: 'blocked', busy: false, problem: true });
  assert.deepEqual(searchStatus({ updates: {} }, tr), { text: '<updates.notChecked>', busy: false, problem: false });
  // The outcome: problems, "all current", how many it found; nothing for a failed call (its own error notice stands).
  assert.equal(searchOutcome({}, { available: false }, 0, tr), '<updates.allCurrent>');
  assert.equal(searchOutcome({}, {}, 3, tr), '<updates.found:{"count":3}>');
  assert.equal(searchOutcome({ windowsError: 'blocked' }, {}, 3, tr), 'blocked');
  assert.equal(searchOutcome({}, {}, 0, tr, false), null);
}
console.log('downloads page: ok');

// A second press on a running action answers with its busy look, not with an "already running" toast.
for (const [file, gone] of [['views/assistant.js', 'assistant.busy'], ['views/bluetooth.js', 'bluetooth.busy'], ['views/network.js', 'network.busy'], ['views/downloads.js', "toast(t('updates.alreadyRunning')"]]) {
  assert.ok(!readFileSync(new URL(`../ui/${file}`, import.meta.url), 'utf8').includes(gone), `${file} still answers with ${gone}`);
}
console.log('busy instead of toasts: ok');

// Review follow-up: a throwing onChange never leaves a key stuck, the target of a running key (the row, the SSID) is readable for a
// page opened again, and the Downloads presses ask the lock before anything looks busy (no spinner flash on a locked couch).
{
  const { createBusy } = await import('../ui/core/busy.js');
  const quiet = console.error;
  console.error = () => {};
  try {
    const b = createBusy();
    // onChange throws at the start: fn still runs and the key clears.
    let ran = false;
    const r1 = await b.run('k', async () => { ran = true; return 1; }, { onChange: (_k, on) => { if (on) throw new Error('start'); } });
    assert.ok(ran && r1.value === 1 && !b.isBusy('k'), 'throwing onChange at start');
    // onChange throws at the end: the value comes back and the key clears.
    const r2 = await b.run('k', async () => 2, { onChange: (_k, on) => { if (!on) throw new Error('end'); } });
    assert.ok(r2.value === 2 && !b.isBusy('k'), 'throwing onChange at end');
    // A throwing listener does the same.
    const off = b.onChange(() => { throw new Error('listener'); });
    await b.run('k', async () => 3);
    assert.ok(!b.isBusy('k'), 'throwing listener');
    off();
    // The target belongs to the run that started, not to a refused one.
    let release;
    const first = b.run('net', () => new Promise((r) => { release = r; }), { target: 'Home' });
    assert.equal(b.target('net'), 'Home');
    const refused = await b.run('net', async () => {}, { target: 'Guest' });
    assert.ok(refused.ignored && b.target('net') === 'Home', 'a refused run does not move the busy row');
    release();
    await first;
    assert.equal(b.target('net'), null);
  } finally {
    console.error = quiet;
  }

  const { pressPending, pressSearch, downloadHints } = await import('../ui/core/downloads-rules.js');
  const fake = ({ busy = false, locked = false } = {}) => {
    const log = [];
    return {
      log,
      deps: {
        busy,
        locked: async () => { log.push('locked?'); return locked; },
        run: async (key, fn) => { log.push(`busy:${key}`); return fn(); },
        unlocked: () => log.push('unlock-toast'),
        bump: () => log.push('bump'),
        select: () => log.push('select')
      }
    };
  };
  const row = (extra = {}) => ({ busyKey: 'update:windows', run: () => 'started', ...extra });
  // Locked: the unlock toast, and nothing ever looked busy.
  let f = fake({ locked: true });
  await pressPending(row(), f.deps);
  assert.deepEqual(f.log, ['select', 'locked?', 'unlock-toast']);
  // Busy: only a bump, the lock is not even asked.
  f = fake({ busy: true });
  await pressPending(row(), f.deps);
  assert.deepEqual(f.log, ['bump']);
  // Free: under its busy key.
  f = fake();
  assert.equal(await pressPending(row(), f.deps), 'started');
  assert.deepEqual(f.log, ['select', 'locked?', 'busy:update:windows']);
  // The restart: no unlock asked, no busy key.
  f = fake({ locked: true });
  assert.equal(await pressPending(row({ open: true, busyKey: undefined }), f.deps), 'started');
  assert.deepEqual(f.log, ['select']);
  // The search: the same order.
  f = fake({ locked: true });
  await pressSearch('downloads:search', () => 'searched', f.deps);
  assert.deepEqual(f.log, ['select', 'locked?', 'unlock-toast']);
  f = fake({ busy: true });
  await pressSearch('downloads:search', () => 'searched', f.deps);
  assert.deepEqual(f.log, ['bump']);
  f = fake();
  assert.equal(await pressSearch('downloads:search', () => 'searched', f.deps), 'searched');
  assert.deepEqual(f.log, ['select', 'locked?', 'busy:downloads:search']);
  // Hints: A greys out on a busy row instead of disappearing; X carries the search's busy state.
  const labels = { a: 'Install', search: 'Search', uninstalled: 'Not installed', back: 'Home' };
  assert.deepEqual(downloadHints({ hasRow: true, rowBusy: true, shortcuts: [{ button: 'X', id: 'search', busy: true }, { button: 'Y', id: 'uninstalled' }], labels }),
    [['A', 'Install', { busy: true }], ['X', 'Search', { busy: true }], ['Y', 'Not installed', { busy: false }], ['B', 'Home']]);
  assert.deepEqual(downloadHints({ shortcuts: [], labels }), [['B', 'Home']]);
  // The view wires them (and no longer asks the lock inside the busy run).
  const src = readFileSync(new URL('../ui/views/downloads.js', import.meta.url), 'utf8');
  assert.ok(src.includes('pressPending(row, pressDeps(') && src.includes('pressSearch(SEARCH,') && src.includes('downloadHints({'), 'Downloads uses the press rules');
  assert.ok(!/runBusy\([^)]*isLocked/.test(src) && !src.includes("? toast(t('downloads.unlockFirst')) : row.run()"), 'no lock check inside the busy run');
  for (const file of ['views/network.js', 'views/bluetooth.js']) {
    const v = readFileSync(new URL(`../ui/${file}`, import.meta.url), 'utf8');
    assert.ok(v.includes('busyTarget(BUSY)') && !/busySsid|busyRow/.test(v), `${file} reads its busy row from the registry`);
  }
}
console.log('busy review follow-up: ok');
// App update progress line: "App 1 of 2 · Name · 45 %", from winget's step; hostile names stay harmless text.
{
  const { cleanStep, stepLine, MAX_STEP_NAME } = await import('../ui/core/update-progress.js');
  const tr = (key, p = {}) => ({ 'downloads.step': `App ${p.index} von ${p.total}`, 'downloads.step.prepare': 'vorbereitet', 'downloads.step.download': 'lädt', 'downloads.step.install': 'installiert', 'downloads.bytes': `${p.done} von ${p.total}` })[key] ?? key;
  const f = { tr, percent: (n) => `${Math.round(n)} %`, size: (b) => `${b} B` };
  const step = (o) => ({ index: 1, total: 2, name: 'Firefox', phase: 'download', ...o });
  assert.equal(stepLine({}, f), null);
  assert.equal(stepLine({ step: null }, f), null);
  assert.equal(stepLine({ step: step({}), progress: 0.45, bytesDone: 4, bytesTotal: 10 }, f), 'App 1 von 2 · Firefox · 45 % · 4 B von 10 B');
  assert.equal(stepLine({ step: step({}), progress: 0.5, bytesTotal: 0 }, f), 'App 1 von 2 · Firefox · 50 %');
  assert.equal(stepLine({ step: step({}), progress: null }, f), 'App 1 von 2 · Firefox · lädt');
  assert.equal(stepLine({ step: step({ phase: 'install' }), progress: 0.9 }, f), 'App 1 von 2 · Firefox · installiert');
  assert.equal(stepLine({ step: step({ phase: 'prepare' }), progress: null }, f), 'App 1 von 2 · Firefox · vorbereitet');
  // One package: no counter. No name: just the counter.
  assert.equal(stepLine({ step: step({ total: 1 }), progress: null }, f), 'Firefox · lädt');
  assert.equal(stepLine({ step: step({ name: '' }), progress: null }, f), 'App 1 von 2 · lädt');
  // Nonsense from the host is not a step; progress outside 0..1 is clamped; an unknown phase counts as preparing.
  for (const bad of [{}, 'x', 5, { index: 0, total: 2 }, { index: 1, total: 0 }, { index: 1.5, total: 2 }, { index: 1, total: 5000 }, { index: '1', total: '2' }]) assert.equal(cleanStep(bad), null);
  assert.equal(cleanStep(step({ index: 9, total: 3 })).index, 3);
  assert.equal(cleanStep(step({ phase: '<b>' })).phase, 'prepare');
  assert.equal(stepLine({ step: step({}), progress: 7 }, f), 'App 1 von 2 · Firefox · 100 %');
  // Names: control and bidi characters are gone, spaces collapse, length is cut; markup is not touched (it is only ever set as text).
  assert.equal(cleanStep(step({ name: 'A\u0000B‮C\n\n  D' })).name, 'A B C D');
  assert.equal(cleanStep(step({ name: 'x'.repeat(5000) })).name.length, MAX_STEP_NAME);
  // Further invisible characters go too; an astral character at the cut stays whole (or is dropped whole), never half.
  assert.equal(cleanStep(step({ name: 'A؜B⁠C⁤D᠎E\u{e0041}\u{e007f}F' })).name, 'A B C D E F');
  assert.equal(cleanStep(step({ name: 'a'.repeat(MAX_STEP_NAME - 1) + '\u{1f600}tail' })).name, 'a'.repeat(MAX_STEP_NAME - 1) + '\u{1f600}');
  assert.equal(cleanStep(step({ name: 'a'.repeat(MAX_STEP_NAME) + '\u{1f600}' })).name, 'a'.repeat(MAX_STEP_NAME));
  assert.ok(!/[\ud800-\udbff](?![\udc00-\udfff])/.test(cleanStep(step({ name: '\u{1f600}'.repeat(100) })).name));
  assert.equal(cleanStep(step({ name: '<img src=x onerror=alert(1)>' })).name, '<img src=x onerror=alert(1)>');
  assert.equal(cleanStep(step({ name: { toString: () => 'evil' } })).name, '');
  console.log('update progress line: ok');
}
// ---------------------------------------------------------------------------------------------------------------------------------------
// Settings > Plugins: the plugins listed with their own settings, and the settings entries that open a plugin page.
{
  const p = (o) => ({ id: 'x', name: 'X', loaded: true, enabled: true, revoked: false, settings: [{ key: 'k', kind: 'toggle' }], ...o });
  // Only running plugins with at least one setting or page entry, by name; withdrawn, switched off, not loaded and empty ones are left out.
  const list = [p({ id: 'b', name: 'Beta' }), p({ id: 'a', name: 'Alpha', settings: [{ key: 'open.devices', kind: 'page' }] }), p({ id: 'off', enabled: false }), p({ id: 'down', loaded: false }),
    p({ id: 'gone', revoked: true }), p({ id: 'none', settings: [] }), p({ id: 'nosettings', settings: undefined }), null];
  assert.deepEqual(pr.pluginsWithSettings(list).map((x) => x.id), ['a', 'b']);
  assert.deepEqual(pr.pluginsWithSettings(null), []);
  assert.deepEqual(pr.pluginsWithSettings('x'), []);
  assert.equal(pr.isPageSetting({ kind: 'page' }), true);
  for (const s of [{ kind: 'toggle' }, { kind: 'Page' }, { kind: 'text' }, null, undefined]) assert.equal(pr.isPageSetting(s), false);

  // The views: a page entry asks the host (never names a page itself), and Settings has a Plugins category in simple mode, too.
  const plugins = readFileSync(new URL('../ui/views/plugins.js', import.meta.url), 'utf8');
  assert.ok(plugins.includes('api.openPluginSettingPage(p.id, s.key)'));
  assert.ok(plugins.includes('push(declarativePage(p, result))'));
  assert.ok(!/\bs\.page\b/.test(plugins), 'the UI never takes a page id from the setting');
  const settingsView = readFileSync(new URL('../ui/views/settings.js', import.meta.url), 'utf8');
  const category = settingsView.slice(settingsView.indexOf("id: 'plugins'"), settingsView.indexOf("id: 'plugins'") + 200);
  assert.ok(!category.includes('expertOnly'), 'the Plugins category is in simple mode, too');
  assert.ok(settingsView.includes('pluginsWithSettings(installedPlugins)'));
  assert.ok(settingsView.includes("{ ...action(t('settings.plugins.manage')"), 'the manager row stays an expert row');
  // The description uses only the count (no plugin-supplied text); simple mode opens the reduced page, expert mode the manager's page.
  assert.ok(settingsView.includes("t('settings.plugins.entryDesc', { count: p.settings.length })"));
  assert.ok(!/entryDesc[^\n]*label/.test(settingsView));
  assert.ok(settingsView.includes("{ expert: !!settings?.expertMode }), t('settings.cat.plugins'))"));
  const reduced = plugins.slice(plugins.indexOf('function pluginSettingsOnlyPage'), plugins.indexOf("/** A plugin's own page in Settings > Plugins"));
  assert.ok(reduced.includes('settingRows(p, refresh)'));
  for (const word of ['setEnabled', 'approvePlugin', 'uninstall', 'functions', 'capabilities', 'plugins.page.folder', 'pickFunctionLevel', 'needsApproval']) assert.ok(!reduced.includes(word), `the reduced page has no ${word}`);
  assert.ok(plugins.includes('expert ? pluginPage(plugin, null, onChange) : pluginSettingsOnlyPage(plugin, onChange)'));
  // B returns to the settings page: the page opens on the navigation stack (push), never in place of it.
  const rows = plugins.slice(plugins.indexOf('function settingRows'), plugins.indexOf('function pluginSettingsOnlyPage'));
  assert.ok(rows.includes('push(declarativePage(p, result))') && !rows.includes('replace('));

  // The bridge sends only the plugin and the key.
  const bridge = readFileSync(new URL('../ui/bridge.js', import.meta.url), 'utf8');
  assert.ok(bridge.includes("openPluginSettingPage: (plugin, key) => call('openPluginSettingPage', plugin, key)"));
}
console.log('plugin settings pages: ok');
