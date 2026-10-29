// CouchHUD UI entry point: wires input, tabs, pages and the host bridge together.

import { api } from './core/api.js';
import { fmtTime, t } from './core/i18n.js';
import { $ } from './core/dom.js';
import { state } from './core/state.js';
import { attempt, toast } from './core/feedback.js';
import { applySkin, onGlyphsChanged, setPadGlyphs } from './core/skins.js';
import { apps, games, reloadLibrary, setLibrary } from './core/library.js';
import { currentView, pop, render, renderHints, setTabs, switchTab, push, clearStack } from './core/nav.js';
import { cycleBar, handleChip } from './core/bar-chips.js';
import { closeOverlay, handleOverlay, openOverlay } from './components/overlay.js';
import { Input } from './input.js';
import { attachMouse, hide as hideMouse, setPointer } from './components/mouse.js';
import { setAudioMode, setFocusActive, setGameRunning, setHaptics, setMusicVolume, setSoundSet, setSoundVolume, sound } from './sound.js';
import { startHdr } from './core/hdr.js';
import { refreshMotion, setMotionActive, startMotion } from './core/motion.js';
import { hideLaunch, launchFocus, launchInput, launchSession, launchTimedOut } from './components/launch-overlay.js';
import { closeQuickMenu, handleQuick, openQuickMenu, quick, renderQuick } from './components/quick-menu.js';
import { BUILTIN_SKINS } from './skins/index.js';
import { migrateSkinId, movedSkinCatalogId } from './core/skin-rules.js';
import { reloadSkins } from './components/skin-menu.js';
import { homeView } from './views/home.js';
import { browserView } from './views/browser.js';
import { startFirstRun } from './views/setup-wizard.js';
import { powerMenu } from './core/power.js';
import { configureScreensaver, screensaverInput, setScreensaverFocus, startScreensaverWatch } from './core/screensaver.js';
import { gridView } from './views/grid.js';
import { checkInterruptedSwitch, settingsView } from './views/settings.js';
import { newsView } from './views/news.js';
import { loadPluginTabs } from './views/plugin-tab.js';
import { arrangeTabs } from './core/plugin-tabs.js';
import { downloadsView, onDownloadsChanged, startDownloads } from './views/downloads.js';
import { startUpdates } from './views/updates.js';
import { offerPluginSuggestions } from './views/plugins.js';
import { startTv } from './views/tv.js';
import { detailPage } from './views/detail.js';
import { startParental } from './components/parental.js';
import { startProfiles } from './components/profiles.js';
import { startNotifications } from './views/notifications.js';
import { startBluetoothRecovery } from './components/bluetooth-recovery.js';
import { isCodeEntry } from './core/input-help.js';
import { showShotToast } from './components/shot-toast.js';
import { changeReporter, PAIRING_SELECTOR, REPORT_REFRESH_MS } from './core/screenshot-rules.js';
import { reportCodeEntries } from './components/passcode.js';
import './views/deck.js';
import { assistantView, startAssistant } from './views/assistant.js';
import { startWeather } from './core/weather.js';
import { onBatteryChanged, startBattery } from './core/battery.js';
import { loadCollections } from './core/collections.js';
import './components/collections.js';
import { collectionPage } from './views/collection.js';
import './components/artwork-picker.js';
import { areaOn, setAreas, shownTabs } from './core/areas.js';

const gamesView = gridView(t('app.games.title'), games, {
  columns: 7,
  kind: 'cover',
  className: 'grid-covers',
  addKind: 'game',
  empty: [t('app.games.emptyTitle'), t('app.games.emptyText')]
});
gamesView.id = 'games';

// The browser is the first app (while the Browser area is on).
const BROWSER_TILE = { id: '__browser', name: 'Browser', type: 'app', source: 'couchhud', builtin: true, art: { cover: ['assets/browser-tile.svg'], hero: [], logo: [] }, open: () => switchTab(tabsIndexOf(browserView)) };

const appsView = gridView(t('app.apps.title'), () => [...(areaOn('browser') ? [BROWSER_TILE] : []), ...apps()], {
  columns: 4,
  kind: 'cover',
  className: 'grid-apps',
  addKind: 'app',
  empty: [t('app.apps.emptyTitle'), t('app.apps.emptyText')]
});
appsView.id = 'apps';

export function updateTabs() {
  setTabs(tabList());
}

// Tabs of switched off areas (Settings › Console › Areas) are left out (core/areas.js).
// Tabs of plugins (capability "tabs") come after the content tabs and before the closing ones, so Settings stays last; the host has
// already left out those of areas that are off, the child's profile and plugins that are not loaded, and the page checks the area once more.
let pluginTabViews = [];
const tabList = () => arrangeTabs(
  shownTabs([homeView, gamesView, appsView, downloadsView, newsView, browserView]),
  pluginTabViews.filter((v) => areaOn(v.tabArea)),
  [...(state.info.assistant ? [assistantView] : []), settingsView]);

const tabSignature = () => tabList().map((v) => `${v.id}:${v.name}`).join('|');

/** Asks the host which plugin tabs exist now (at start, when plugins finished loading, after an area, profile or plugin change). */
let tabsLoading = false;
let tabsAgain = false;
export async function refreshPluginTabs() {
  if (tabsLoading) {
    tabsAgain = true;
    return;
  }
  tabsLoading = true;
  try {
    do {
      tabsAgain = false;
      const before = tabSignature();
      pluginTabViews = await loadPluginTabs();
      updateTabs();
      // Only a changed tab bar is drawn again (a page that is open keeps its place).
      if (tabSignature() !== before) render();
    } while (tabsAgain);
  } finally {
    tabsLoading = false;
  }
}
const tabsIndexOf = (view) => tabList().indexOf(view);

updateTabs();

/** The host switched areas on or off: tabs, shelf and library follow at once. */
function applyAreas(list) {
  state.info.areas = setAreas(list);
  updateTabs();
  refreshPluginTabs().catch(console.error);
  render();
  reloadLibrary().catch(console.error);
}

// A collection (from Home or the filter menu) opens as its own page with games and apps together.
window.addEventListener('couchhud:show-collection', (e) => push(collectionPage(e.detail.id)));

/**
 * Tells the host while the phone's pairing code (QR or text) is on screen: it refuses screenshots then. Watches the page itself, so
 * every place that shows the code counts without each having to say so; only changes are reported.
 */
function watchPairingCode() {
  // Not swallowed: a report that did not arrive is sent again (changeReporter), the host refuses screenshots until it knows.
  const report = changeReporter((open) => api.pairingState?.(open));
  const check = () => report(document.querySelector(PAIRING_SELECTOR) !== null);
  // Now and then the state again, whether it changed or not: a host that forgot it (renderer hung) hears it anew.
  setInterval(() => {
    check();
    report.refresh();
  }, REPORT_REFRESH_MS);
  let queued = false;
  new MutationObserver(() => {
    if (queued) return;
    queued = true;
    queueMicrotask(() => {
      queued = false;
      check();
    });
  }).observe(document.body, { childList: true, subtree: true });
  check();
}

function goHome() {
  clearStack();
  state.tab = 0;
  homeView.index = 0;
  render();
}

const GLOBAL = new Set(['lb', 'rb', 'start', 'guide', 'b']);

function onAction(action) {
  if (screensaverInput()) return;
  if (launchInput(action)) return;
  if (state.overlay) return handleOverlay(action);
  if (quick.open) return handleQuick(action);
  const view = currentView();
  // LB/RB leave the last tab for the chips at the right of the bar (bell, downloads); while one has the focus it takes A and B.
  if (action !== 'lb' && action !== 'rb' && action !== 'start' && action !== 'guide' && handleChip(action)) return;
  // Pages like the on-screen keyboard claim buttons that are global elsewhere.
  if (!GLOBAL.has(action) || view.captures?.(action)) return view.handle(action);
  switch (action) {
    case 'lb': return cycleBar(-1);
    case 'rb': return cycleBar(1);
    case 'start': return openOverlay(powerMenu());
    case 'guide': return openQuickMenu();
    case 'b':
      if (pop()) return;
      if (state.tab !== 0) {
        sound.back();
        state.tab = 0;
        render();
      }
  }
}

/** Small "running" badge in the header while a game runs. */
function showSession() {
  const badge = $('#session-status');
  badge.hidden = !state.session;
  badge.textContent = state.session ? `${state.session.paused ? '❚❚' : '▶'} ${state.session.name}` : '';
}

function tickClock() {
  $('#clock').textContent = fmtTime();
}

/** The skin the settings ask for was one of the four that moved to the catalog and is not there: say where to get it. */
function missingSkinNotice(id) {
  const catalogId = movedSkinCatalogId(id);
  if (!catalogId) return;
  // Once per page session and skin: not at every profile reload after the host has given up.
  const seenKey = `couchhud.skinMissingShown.${catalogId}`;
  try {
    if (sessionStorage.getItem(seenKey)) return;
    sessionStorage.setItem(seenKey, '1');
  } catch {
    // No session storage (a private window, blocked): the notice may then come again, which only errs on the side of telling.
  }
  // i18n-used: skins.name.deepsea skins.name.greenlight skins.name.neoncity skins.name.nova
  const nameKey = `skins.name.${catalogId}`;
  toast(t(nameKey) + ': ' + t('skins.missing'), 12000, { long: true });
}

async function init() {
  state.info = (await attempt(() => api.systemInfo(), t('app.systemInfoFailed'))) || {};
  state.info.areas = setAreas(state.info.areas);
  setAudioMode(state.info.audioMode ?? (state.info.sounds === false ? 'off' : 'ui'));
  setSoundVolume((state.info.soundVolume ?? 70) / 100);
  setMusicVolume((state.info.musicVolume ?? 35) / 100);
  setSoundSet(state.info.soundSet || null);
  configureScreensaver({ minutes: state.info.screensaverMinutes ?? 5, style: state.info.screensaverStyle ?? 'art', displayOff: state.info.displayOffMinutes ?? 0, pixelShift: state.info.pixelShift ?? true });
  startHdr();
  startMotion();
  state.skins = [...BUILTIN_SKINS, ...((await attempt(() => api.listSkins(), t('app.skinsFailed'))) || [])];
  const wantedSkin = migrateSkinId(state.info.skin);
  const foundSkin = state.skins.find((s) => s.id === wantedSkin);
  applySkin(foundSkin || BUILTIN_SKINS[0]);
  // A skin that moved to the catalog and is not installed (yet): the Couch skin meanwhile. While the host still installs it nothing is
  // said; when it is clear it is not coming (offline, refused, tried enough times) the person is told once.
  if (!foundSkin && state.info.skinRestore?.state === 'missing') missingSkinNotice(wantedSkin);
  updateTabs();
  startWeather();
  startUpdates();
  startParental({ detailPage, closeQuickMenu, redrawQuick: () => quick.open && renderQuick() });
  startAssistant();
  startNotifications();
  // The guide menu's downloads line shows what loads right now; keep it current while the menu is open.
  onDownloadsChanged(() => quick.open && renderQuick());
  startDownloads();
  startBluetoothRecovery();
  // Batteries: the PC's next to the clock (laptops only), low controllers as a small icon; the guide menu shows them all.
  onBatteryChanged(() => quick.open && renderQuick());
  startBattery();
  // First start: the setup assistant, with the disclaimer and privacy notice as its second step (on a locked couch only the notice).
  startFirstRun().catch(console.error);
  // A mode switch that was cut off by a crash or a power loss: after the unlock the person can undo or finish it.
  setTimeout(() => checkInterruptedSwitch().catch(console.error), 4000);
  // Settings that add or remove a tab (assistant) ask for a refresh.
  window.addEventListener('couchhud:tabs', () => {
    updateTabs();
    refreshPluginTabs().catch(console.error);
  });
  api.onAreas?.((list) => applyAreas(list));
  // Plugins register tabs while they load; a profile change can hide them (a child's profile), a failed page can drop its tab.
  api.onPluginTabs?.(() => refreshPluginTabs().catch(console.error));
  api.onProfiles?.(() => refreshPluginTabs().catch(console.error));
  refreshPluginTabs().catch(console.error);
  window.addEventListener('couchhud:areas', (e) => applyAreas(e.detail));
  // The assistant can change the design and settings from the host side.
  api.onSkinRestore?.(async (restore) => {
    await reloadSkins();
    const id = migrateSkinId(restore?.skin);
    const skin = state.skins.find((s) => s.id === id);
    if (skin) applySkin(skin);
    else if (restore?.state === 'missing') missingSkinNotice(id);
  });
  api.onSkin?.((id) => {
    const skin = state.skins.find((s) => s.id === migrateSkinId(id));
    if (skin) applySkin(skin);
  });
  api.onSettings?.(async () => {
    const s = await attempt(() => api.getSettings());
    if (!s) return;
    setAudioMode(s.audioMode ?? (s.sounds ? 'ui' : 'off'));
    setSoundVolume((s.soundVolume ?? 70) / 100);
    setMusicVolume((s.musicVolume ?? 35) / 100);
    setSoundSet(s.soundSet || null);
    state.info.trailerPreviews = s.trailerPreviews;
    state.info.reduceMotion = s.reduceMotion;
    state.info.pointer = s.pointer;
    state.info.mouseUnlocked = s.mouseUnlocked;
    setPointer(s.pointer, s.mouseUnlocked);
    refreshMotion();
    setPadGlyphs({ padGlyphs: s.padGlyphs });
  });
  startTv();

  // Controller or keyboard input hides the mouse pointer right away.
  const input = new Input((action, source) => {
    state.actionSource = source === 'keyboard' ? 'keyboard' : 'pad';
    hideMouse();
    if (source && source !== state.inputSource) {
      state.inputSource = source;
      renderHints();
    }
    onAction(action);
  });
  input.repeatAllowed = () => !isCodeEntry(currentView());
  attachMouse((action) => {
    state.actionSource = 'mouse';
    onAction(action);
  }, state.info);
  input.attachKeyboard();
  // Never both: with the host's own input (XInput and PlayStation pads over HID, also on top of games) the page does not read
  // the browser's Gamepad API at all, so no pad reaches the UI twice.
  if (state.info.nativeInput) input.attachNative(api);
  else input.attachGamepadApi((padKind) => setPadGlyphs({ padKind }));
  // The kind of pad pressed last ("xbox" or "playstation") switches the button symbols; the phone and the keyboard never do.
  api.onPadKind?.(({ kind }) => setPadGlyphs({ padKind: kind }));
  // Hints and [data-glyph] nodes follow by themselves; the settings page names buttons in its rows (Screenshot with Guide + Y).
  onGlyphsChanged(() => {
    if (currentView() === settingsView && !state.overlay) render();
  });

  api.onNav((target) => {
    input.releaseAll();
    // A protected profile waits at the picker: no guide menu, no way home past it.
    if (state.pickerHold) return;
    if (state.overlay) closeOverlay();
    hideLaunch();
    if (target === 'quick') return openQuickMenu();
    closeQuickMenu();
    goHome();
    // Typing an address after the page's Y button: the Browser tab, controller on its bar.
    if (target === 'browser' && tabsIndexOf(browserView) >= 0) switchTab(tabsIndexOf(browserView));
  });
  api.onSession((session) => {
    const before = state.session;
    state.session = session;
    setGameRunning(!!session);
    showSession();
    launchSession(session);
    // The home shelf shows the running game first.
    if (currentView() === homeView && !state.overlay) render();
    if (session && !before) toast(t('app.running', { name: session.name }));
    if (!session && before) toast(t('app.ended', { name: before.name }));
    if (quick.open) renderQuick();
  });
  api.onAudio((audio) => {
    quick.audio = audio;
    if (quick.open) renderQuick();
  });
  api.onToast((text) => toast(text, 3500));
  api.onScreenshot?.(showShotToast);
  // A fresh page has no code entry and no pairing code on screen, whatever the last one said; from now on every change is reported.
  reportCodeEntries();
  watchPairingCode();
  api.onLaunchTimeout?.(() => launchTimedOut());
  setHaptics((kind) => api.rumble(kind).catch(() => {}));
  state.session = (await attempt(() => api.getSession())) ?? null;
  setGameRunning(!!state.session);
  // A game or another full-screen app in front: music and sounds go quiet.
  api.onFocus?.(({ active }) => {
    setFocusActive(active);
    setMotionActive(active);
    launchFocus(active);
    setScreensaverFocus(active);
  });
  startScreensaverWatch();
  showSession();
  api.onLibrary(setLibrary);
  // quiet: a pad that comes or goes within 1.5 s of a pad of the other kind (PlayStation and XInput), most likely the Steam Input
  // or DS4Windows mirror of the other one. Disconnects are told once the burst is over, so a pad and its mirror going away
  // together give one toast, and "no controller left" is seen even when the mirror goes a moment after the pad.
  let padGone = null;
  api.onPad(({ connected, quiet }) => {
    if (connected) return quiet ? undefined : toast(t('app.padConnected'));
    const loud = !quiet || padGone?.loud === true;
    clearTimeout(padGone?.timer);
    padGone = {
      loud,
      timer: setTimeout(async () => {
        padGone = null;
        // The last controller is gone (Bluetooth off, battery empty): say that keyboard and mouse still work.
        const pads = await api.getPads?.().catch(() => null);
        if (Array.isArray(pads) && pads.length === 0) toast(t('app.padNone'), 8000);
        else if (loud) toast(t('app.padDisconnected'));
      }, 1600)
    };
  });

  // Offsets depend on the layout (resolution, TV scaling).
  window.addEventListener('resize', () => currentView().update?.({ instant: true }));

  tickClock();
  setInterval(tickClock, 10_000);
  // A language switch reloads the page: come back to Settings > Info, where it happened.
  try {
    const back = sessionStorage.getItem('couchhud.resume');
    sessionStorage.removeItem('couchhud.resume');
    const at = back === 'settings:info' ? tabsIndexOf(settingsView) : -1;
    if (at >= 0) {
      state.tab = at;
      settingsView.catId = 'info';
      settingsView.pane = 'items';
    }
  } catch { /* storage blocked: start on Home */ }
  // Several profiles: the picker in front (a protected profile waits for its code; the host refuses starts until then).
  await startProfiles().catch(console.error);
  // Show the HUD immediately; a slow store or plugin must not keep the screen empty.
  render();
  const bootStarted = performance.now();
  const hideBoot = () => {
    const boot = document.querySelector('#boot');
    if (!boot || boot.classList.contains('done')) return;
    boot.classList.add('done');
    sound.boot();
  };
  // The boot screen stays at most a moment, even if the library is slow.
  setTimeout(hideBoot, 2500);
  await reloadLibrary();
  loadCollections();
  // A matching program is installed and the catalog has a plugin for it: asked once per Windows user, a while after the start, and only when the
  // HUD is in front with nothing open (no page, question or menu, no game or app running); if that is not so it is tried once more later (the
  // host also says nothing while the couch is locked).
  const canPrompt = () => !state.stack.length && !state.overlay && !state.panel && !state.session && !document.hidden && document.hasFocus();
  // The host says when the catalog's entries become available (event), so a look that found nothing because the catalog was still loading
  // is repeated then; no polling. The host keeps "once per Windows user" and "nothing on the locked couch".
  let offered = false;
  let offering = false;
  let askAgain = false;
  let armed = false;
  const offer = async (retries) => {
    if (offered) return;
    if (offering) {
      askAgain = true;
      return;
    }
    offering = true;
    try {
      const result = canPrompt() ? await offerPluginSuggestions(canPrompt) : 'skipped';
      if (result === 'shown') offered = true;
      else if (result === 'skipped' && retries > 0) setTimeout(() => offer(retries - 1).catch(console.error), 120_000);
    } finally {
      offering = false;
    }
    if (askAgain && !offered) {
      askAgain = false;
      await offer(retries);
    }
  };
  api.onPluginSuggestionsReady?.(() => {
    if (armed) offer(1).catch(console.error);
  });
  setTimeout(() => {
    armed = true;
    offer(1).catch(console.error);
  }, 8000);
  setTimeout(hideBoot, Math.max(0, 900 - (performance.now() - bootStarted)));
}

init();
