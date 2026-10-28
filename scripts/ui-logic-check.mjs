// Pure UI rules with a test: node scripts/ui-logic-check.mjs (run by check.sh).
import assert from 'node:assert/strict';
import { clampPaste, remoteView } from '../ui/core/paste.js';
import { categoryOf, filterEntities, groupEntities, moveInOrder } from '../ui/core/ha-pick.js';
import { CODE_KEYS, holdsDialogs, isCodeEntry, padHelp, switchOffWarning } from '../ui/core/input-help.js';
import { step } from '../ui/core/paging.js';
import { admitProtectedChange, beginSetupHold, codeLockHeld, configFaultNow, lockStepStatus, setupCouchLock } from '../ui/core/lock-rules.js';
import { admitInputSwitch, pairOutcome } from '../ui/core/tv-rules.js';
import { activeSceneId, conflictNames, sceneLabel } from '../ui/core/casehud-rules.js';
import { CONTINUE_LIMIT, admitSaveChange, continueFacts, continuePlaying, homeShelf } from '../ui/core/continue-rules.js';
import { downloadActions, itemSize, pendingDownloads, updateSummary, validSize } from '../ui/core/update-rules.js';
import { TARGET_EXTENSIONS, isScript, launchRows, scriptArgsProblem, targetLabel } from '../ui/core/launch-rules.js';
import { INFO_FIELDS, STORE_RETRY_MS, ageBadge, ageRating, badgeText, descriptionState, fieldText, infoProblem, loadTickets, queryOk, splitList, storeRetryDelay, storeRows } from '../ui/core/item-info-rules.js';
import { LOW_PERCENT, bars, batteryEntries, lowControllerBadge, padSteps, percentSteps, systemBadge, validPercent } from '../ui/core/battery-rules.js';
import { changeReporter, codeEntryTracker, PAIRING_SELECTOR, REPORT_REFRESH_MS, REPORT_RETRY_MS, shotToast } from '../ui/core/screenshot-rules.js';
import { RATING_CHOICES, activeSystem, ageChoices, ageLabels, convertLimit, limitLabel, limitText, ratingSteps, shownLimit } from '../ui/core/age-rating-rules.js';
import { GAME_LIMITS_MB, TOTAL_LIMITS_MB, formatBytes, limitChoices } from '../ui/core/size.js';
import { readFileSync, readdirSync } from 'node:fs';
import { RENAMED_SKINS, migrateSkinId } from '../ui/core/skin-rules.js';
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

// Smart home picker: suggestions, chosen order, search through everything, groups, moving.
const ents = [
  { id: 'light.a', domain: 'light', name: 'Sofa', area: 'Wohnzimmer', suggested: true },
  { id: 'sensor.t', domain: 'sensor', name: 'Temperatur', area: 'Wohnzimmer', suggested: false },
  { id: 'scene.kino', domain: 'scene', name: 'Kino', area: '', suggested: true },
  { id: 'switch.child_lock', domain: 'switch', name: 'Kindersicherung', area: 'Küche', suggested: false }
];
assert.deepEqual(filterEntities(ents, { chip: 'suggested', query: '', selected: [] }).map((e) => e.id), ['light.a', 'scene.kino']);
assert.deepEqual(filterEntities(ents, { chip: 'suggested', query: '', selected: ['sensor.t'] }).map((e) => e.id), ['light.a', 'sensor.t', 'scene.kino']);
assert.deepEqual(filterEntities(ents, { chip: 'suggested', query: 'temp', selected: [] }).map((e) => e.id), ['sensor.t']);
assert.deepEqual(filterEntities(ents, { chip: 'selected', query: '', selected: ['scene.kino', 'light.gone', 'light.a'] }).map((e) => e.id), ['scene.kino', 'light.gone', 'light.a']);
assert.equal(filterEntities(ents, { chip: 'selected', query: '', selected: ['light.gone'] })[0].missing, true);
assert.equal(filterEntities(ents, { chip: 'selected', query: '', selected: ['light.a'] })[0].missing, undefined);
assert.deepEqual(filterEntities(ents, { chip: 'switch', query: '', selected: [] }).map((e) => e.id), ['switch.child_lock']);
assert.deepEqual(filterEntities(ents, { chip: 'all', query: 'küche', selected: [] }).map((e) => e.id), ['switch.child_lock']);
assert.equal(categoryOf('media_player'), 'media');
assert.equal(categoryOf('unknown_domain'), 'other');
const groups = groupEntities(ents, { byArea: true, keepOrder: false, noArea: '–', categoryName: (c) => c });
assert.deepEqual(groups.map((g) => g.title), ['Küche', 'Wohnzimmer', '–']);
assert.deepEqual(groups[1].items.map((e) => e.id), ['light.a', 'sensor.t']);
assert.deepEqual(groupEntities(ents, { byArea: false, keepOrder: false, noArea: '–', categoryName: (c) => c }).map((g) => g.title), ['light', 'switch', 'scene', 'sensor']);
assert.deepEqual(moveInOrder(['a', 'b', 'c'], 'c', -1), ['a', 'c', 'b']);
const order = ['a', 'b'];
assert.equal(moveInOrder(order, 'a', -1), order);
// With a search, the move skips hidden ids: c goes past a (b is hidden and keeps its place).
assert.deepEqual(moveInOrder(['a', 'b', 'c'], 'c', -1, ['a', 'c']), ['c', 'b', 'a']);
assert.equal(moveInOrder(order, 'b', 1, ['a', 'b']), order);
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

// CaseHUD: the scene on screen comes from status.scene (CaseHUD 1.0, "" = none); older builds: the list's active scene, then rotation.scene.
{
  const scenes = [{ id: 'desktop', name: 'Desktop', active: false }, { id: 'night', name: 'Night', active: true }];
  assert.equal(activeSceneId({ scene: 'gaming', rotation: { scene: 'desktop' } }, scenes), 'gaming');
  assert.equal(activeSceneId({ scene: '', rotation: { scene: 'desktop' } }, scenes), '');
  assert.equal(activeSceneId({ rotation: { scene: 'desktop' } }, scenes), 'night');
  assert.equal(activeSceneId({ scene: null, rotation: { scene: 'desktop' } }, scenes), 'night');
  assert.equal(activeSceneId({ rotation: { scene: 'desktop' } }, [{ id: 'night', active: false }]), 'desktop');
  assert.equal(activeSceneId({ rotation: { scene: 'desktop' } }, null), 'desktop');
  assert.equal(activeSceneId(null, null), '');
  assert.equal(sceneLabel('night', scenes), 'Night');
  assert.equal(sceneLabel('gone', scenes), 'gone');
  assert.equal(sceneLabel('x'.repeat(100), []).length, 64);
  // Conflicts: strings only, no doubles, capped.
  assert.deepEqual(conflictNames(['OpenRGB.exe', 7, null, '', ' openrgb.exe ', 'SignalRgb.exe']), ['OpenRGB.exe', 'SignalRgb.exe']);
  assert.deepEqual(conflictNames('OpenRGB.exe'), []);
  assert.equal(conflictNames(Array.from({ length: 20 }, (_, i) => `p${i}.exe`)).length, 8);
  assert.equal(conflictNames(['y'.repeat(200)])[0].length, 64);
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

// Skins renamed in 0.5: the three old ids map to the new ones, everything else stays as it is (unknown ids fall back to the default
// skin where they are used), and the page's map is the host's (SkinCatalog.MigrateId).
{
  assert.equal(migrateSkinId('ps4'), 'deepsea');
  assert.equal(migrateSkinId('xbox'), 'greenlight');
  assert.equal(migrateSkinId('cyberpunk'), 'neoncity');
  for (const id of ['default', 'deepsea', 'greenlight', 'neoncity', 'nova', 'user:ps4', 'PS4', 'gone', '', 'constructor', 'toString']) assert.equal(migrateSkinId(id), id);
  assert.equal(migrateSkinId(null), null);
  assert.equal(migrateSkinId(undefined), undefined);
  const catalog = readFileSync(new URL('../src/CouchHud.Core/Skins/SkinCatalog.cs', import.meta.url), 'utf8');
  const block = catalog.slice(catalog.indexOf('RenamedIds ='), catalog.indexOf('};', catalog.indexOf('RenamedIds =')));
  assert.deepEqual(Object.fromEntries([...block.matchAll(/\["(\w+)"\] = "(\w+)"/g)].map((m) => [m[1], m[2]])), { ...RENAMED_SKINS });
  const ids = catalog.match(/BuiltinIds = \[([^\]]*)\]/)[1].match(/"(\w+)"/g).map((x) => x.slice(1, -1));
  const index = readFileSync(new URL('../ui/skins/index.js', import.meta.url), 'utf8');
  assert.deepEqual([...index.matchAll(/^\s+id: '(\w+)'/gm)].map((m) => m[1]), ids);
  const sound = readFileSync(new URL('../ui/sound.js', import.meta.url), 'utf8');
  assert.deepEqual([...sound.matchAll(/\{ id: '(\w+)', get label\(\)/g)].map((m) => m[1]), ids);
}

// No third-party trademarks in the built-in skins: names, descriptions, sound set labels, stylesheets and the skin list. Controller
// names for compatibility live elsewhere (README, guide); the skins only describe their look.
{
  const brands = /playstation|ps[1-5]|sony|xbox|microsoft|cyberpunk|cd ?projekt|night ?city|series ?x|dualshock|dualsense/i;
  const files = readdirSync(new URL('../ui/skins/', import.meta.url)).filter((f) => f.endsWith('.css') || f.endsWith('.js'));
  assert.ok(files.includes('index.js') && files.filter((f) => f.endsWith('.css')).length >= 4);
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

console.log('ui logic: ok');
