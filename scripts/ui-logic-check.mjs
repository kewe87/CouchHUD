// Pure UI rules with a test: node scripts/ui-logic-check.mjs (run by check.sh).
import assert from 'node:assert/strict';
import { clampPaste, remoteView } from '../ui/core/paste.js';
import { CODE_KEYS, holdsDialogs, isCodeEntry, padHelp, switchOffWarning } from '../ui/core/input-help.js';
import { step } from '../ui/core/paging.js';
import { admitProtectedChange, beginSetupHold, codeLockHeld, configFaultNow, lockStepStatus, setupCouchLock } from '../ui/core/lock-rules.js';
import { admitInputSwitch, pairOutcome } from '../ui/core/tv-rules.js';
import { CONTINUE_LIMIT, admitSaveChange, continueFacts, continuePlaying, homeShelf } from '../ui/core/continue-rules.js';
import { downloadActions, itemSize, pendingDownloads, updateSummary, validSize } from '../ui/core/update-rules.js';
import { TARGET_EXTENSIONS, isScript, launchRows, scriptArgsProblem, targetLabel } from '../ui/core/launch-rules.js';
import { INFO_FIELDS, STORE_RETRY_MS, ageBadge, ageRating, badgeText, descriptionState, fieldText, infoProblem, loadTickets, queryOk, splitList, storeRetryDelay, storeRows } from '../ui/core/item-info-rules.js';
import { LOW_PERCENT, bars, batteryEntries, lowControllerBadge, padSteps, percentSteps, systemBadge, validPercent } from '../ui/core/battery-rules.js';
import { changeReporter, codeEntryTracker, PAIRING_SELECTOR, REPORT_REFRESH_MS, REPORT_RETRY_MS, shotToast } from '../ui/core/screenshot-rules.js';
import { RATING_CHOICES, activeSystem, ageChoices, ageLabels, convertLimit, limitLabel, limitText, ratingSteps, shownLimit } from '../ui/core/age-rating-rules.js';
import { GAME_LIMITS_MB, TOTAL_LIMITS_MB, formatBytes, limitChoices } from '../ui/core/size.js';
import { readFileSync, readdirSync } from 'node:fs';
import { MOVED_SKINS, RENAMED_SKINS, migrateSkinId, migrateSoundSetId, movedSkinCatalogId } from '../ui/core/skin-rules.js';
import * as pr from '../ui/core/plugin-rules.js';
import * as ptabs from '../ui/core/plugin-tabs.js';
import deCore from '../ui/i18n/de/core.js';
import enCore from '../ui/i18n/en/core.js';
import { AREAS, AREA_DESC_KEYS, AREA_NAME_KEYS, LAUNCH_AREAS, SETTING_AREAS, areaOfSetting, areaOn, enabledAreas, homeEndTile, setAreas, shownRows, shownSteps, shownTabs, toggleArea, validChoice } from '../ui/core/areas.js';
import { PAD_GLYPH_SETTINGS, glyphFor, glyphKind, isPlayStationLabel, kindFromGamepadId, kindFromPresses, MIRROR_WINDOW_MS } from '../ui/core/pad-glyphs.js';
import { HISTORY_OFF_ACTION, HOLD_LIMIT, RECENT_LIMIT, cleanRecent, closeCountdown, dialogKind, fileSize, forgetRecent, forgetSite, historyOffNeedsParent, holding, host, initial, reconcileFocus, recentNote, rememberable, rememberSite, shortHost, siteAddress, siteHost, visibleRecent } from '../ui/core/browser-logic.js';

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
  // Shelf: running first (hero), then the row, then recent ones not yet shown, then the extra tiles.
  const running = { id: 'b', running: true };
  const shelf = homeShelf({ running, library: lib, recent: [lib[0], { id: 'fav' }], extras: [{ id: '__all', special: true }] });
  assert.deepEqual(shelf.list.map((i) => i.id), ['b', 'f', 'a', 'g7', 'g6', 'g5', 'fav', '__all']);
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
  const beforeAnswer = homeShelf({ library: [], recent: [], extras: [{ id: '__all', special: true }] });
  assert.deepEqual(beforeAnswer.list.map((i) => i.id), ['__all']);
  const childView = homeShelf({ library: lib, recent: [], blocked: new Set(['b', 'f']) });
  assert.ok(!childView.continueIds.has('b') && !childView.continueIds.has('f') && childView.heroId === 'a');
  const empty = homeShelf({ library: [], recent: [], extras: [{ id: '__all', special: true }] });
  assert.equal(empty.heroId, null);
  assert.deepEqual(empty.list.map((i) => i.id), ['__all']);
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

// Batteries: the 4 steps of a pad's level and of a percent, the bounds, no battery is no badge, low controllers in the top bar.
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
  // The top bar's controller icon: only while one is low; a low Bluetooth headset is no controller.
  assert.equal(lowControllerBadge(null), null);
  assert.equal(lowControllerBadge({ pads: [{ index: 0, level: 'medium' }], devices: [{ kind: 'headphones', percent: 5 }] }), null);
  assert.deepEqual(lowControllerBadge({ pads: [{ index: 0, level: 'low' }], devices: [] }), { icon: '🎮', value: '▮▯▯' });
  assert.deepEqual(lowControllerBadge({ pads: [{ index: 0, level: 'low' }, { index: 1, level: 'empty' }], devices: [] }), { icon: '🎮', value: '▯▯▯' });
  assert.deepEqual(lowControllerBadge({ pads: [], devices: [{ kind: 'controller', percent: LOW_PERCENT }] }), { icon: '🎮', value: '▯▯▯' });
  assert.equal(lowControllerBadge({ pads: [], devices: [{ kind: 'controller', percent: LOW_PERCENT + 1 }] }), null);
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
// The end of the Home shelf always leads to something that is on.
// The websites switch of the Favorites tab goes with the browser area, the arrangement with none.
assert.equal(areaOfSetting('favoritesSites'), 'browser');
assert.equal(areaOfSetting('favoritesMode'), 'always');
assert.deepEqual(homeEndTile(), { id: '__apps', tab: 'apps' });
setAreas(['browser']);
assert.deepEqual(homeEndTile(), { id: '__browser', tab: 'browser' });
setAreas(['games', 'browser']);
assert.deepEqual(homeEndTile(), { id: '__all', tab: 'games' });
setAreas(null);
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
  for (const cap of ['library', 'pages', 'quickActions', 'assistant', 'notify', 'browser', 'downloads', 'network.state', 'process', 'browser.signin', 'tabs', 'setup.run', 'lock.restrict', 'startup.args', 'native', 'registry', 'network:loopback', 'network:internet', 'dynamicCode']) {
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
  assert.deepEqual(W.order(ctx()), ['welcome', 'areas', 'mode', 'look', 'sound', 'summary']);
  // Shell mode chosen: the explanations come right after the question, in a fixed order, then look and sound, then the summary.
  assert.deepEqual(W.order(ctx({ intent: 'full' })), ['welcome', 'areas', 'mode', 'shell', 'lock', 'autologon', 'launchers', 'services', 'look', 'sound', 'clientsInfo', 'summary']);
  // The way back is explained too.
  assert.deepEqual(W.order(ctx({ intent: 'light', full: true })), ['welcome', 'areas', 'mode', 'revert', 'look', 'sound', 'summary']);
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
  assert.equal(ptabs.showTabIcons(3), true);
  assert.equal(ptabs.showTabIcons(4), false);

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
  assert.ok(cs.includes(`MaxTitle = ${ptabs.MAX_TITLE};`) && cs.includes(`MaxTotal = ${ptabs.MAX_TOTAL};`) && cs.includes(`MaxPerPlugin = ${ptabs.MAX_PER_PLUGIN};`) && cs.includes(`MaxTabsWithIcons = ${ptabs.MAX_TABS_WITH_ICONS};`));
}


// Assistant and Settings stay pinned at the end of the tab bar (they never scroll out of sight with many plugin tabs).
assert.deepEqual(ptabs.PINNED_TABS, ['assistant', 'settings']);
assert.equal(ptabs.isPinnedTab({ id: 'settings' }), true);
assert.equal(ptabs.isPinnedTab({ id: 'assistant' }), true);
assert.equal(ptabs.isPinnedTab({ id: 'plugin-lumen' }), false);
assert.equal(ptabs.isPinnedTab(null), false);
console.log('ui logic: ok');
