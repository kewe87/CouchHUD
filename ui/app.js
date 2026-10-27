// CouchHUD UI entry point: wires input, tabs, pages and the host bridge together.

import { api } from './core/api.js';
import { fmtTime, t } from './core/i18n.js';
import { $ } from './core/dom.js';
import { state } from './core/state.js';
import { attempt, toast } from './core/feedback.js';
import { applySkin } from './core/skins.js';
import { apps, games, reloadLibrary, setLibrary } from './core/library.js';
import { currentView, pop, render, renderHints, setTabs, switchTab, push, clearStack } from './core/nav.js';
import { closeOverlay, handleOverlay, openOverlay } from './components/overlay.js';
import { Input } from './input.js';
import { attachMouse, hide as hideMouse, setPointer } from './components/mouse.js';
import { setAudioMode, setFocusActive, setGameRunning, setHaptics, setMusicVolume, setSoundSet, setSoundVolume, sound } from './sound.js';
import { startHdr } from './core/hdr.js';
import { refreshMotion, setMotionActive, startMotion } from './core/motion.js';
import { hideLaunch, launchFocus, launchInput, launchSession, launchTimedOut } from './components/launch-overlay.js';
import { closeQuickMenu, handleQuick, openQuickMenu, quick, renderQuick } from './components/quick-menu.js';
import { BUILTIN_SKINS } from './skins/index.js';
import { homeView } from './views/home.js';
import { browserView } from './views/browser.js';
import { startFirstRun } from './views/setup-wizard.js';
import { powerMenu } from './core/power.js';
import { configureScreensaver, screensaverInput, setScreensaverFocus, startScreensaverWatch } from './core/screensaver.js';
import { gridView } from './views/grid.js';
import { settingsView } from './views/settings.js';
import { preloadSmartHome, smartHomeView } from './views/smarthome.js';
import { newsView } from './views/news.js';
import { startUpdates } from './views/updates.js';
import { startCaseHud } from './views/casehud.js';
import { detailPage } from './views/detail.js';
import { startParental } from './components/parental.js';
import { startNotifications } from './views/notifications.js';
import { startBluetoothRecovery } from './components/bluetooth-recovery.js';
import { isCodeEntry } from './core/input-help.js';
import './views/deck.js';
import { assistantView, startAssistant } from './views/assistant.js';
import { startWeather } from './core/weather.js';
import { loadCollections } from './core/collections.js';
import './components/collections.js';
import { collectionPage } from './views/collection.js';
import './components/artwork-picker.js';

const gamesView = gridView(t('app.games.title'), games, {
  columns: 7,
  kind: 'cover',
  className: 'grid-covers',
  addKind: 'game',
  empty: [t('app.games.emptyTitle'), t('app.games.emptyText')]
});

// The browser is always there as the first app.
const BROWSER_TILE = { id: '__browser', name: 'Browser', type: 'app', source: 'couchhud', builtin: true, art: { cover: ['assets/browser-tile.svg'], hero: [], logo: [] }, open: () => switchTab(tabsIndexOf(browserView)) };

const appsView = gridView(t('app.apps.title'), () => [BROWSER_TILE, ...apps()], {
  columns: 4,
  kind: 'cover',
  className: 'grid-apps',
  addKind: 'app',
  empty: [t('app.apps.emptyTitle'), t('app.apps.emptyText')]
});

/** Smart Home only shows up once Home Assistant is connected. */
export function updateTabs() {
  setTabs(tabList());
}

const tabList = () => [homeView, gamesView, appsView, newsView, browserView, ...(state.info.homeAssistant ? [smartHomeView] : []), ...(state.info.assistant ? [assistantView] : []), settingsView];
const tabsIndexOf = (view) => tabList().indexOf(view);

updateTabs();

// A collection (from Home or the filter menu) opens as its own page with games and apps together.
window.addEventListener('couchhud:show-collection', (e) => push(collectionPage(e.detail.id)));

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
  // Pages like the on-screen keyboard claim buttons that are global elsewhere.
  if (!GLOBAL.has(action) || view.captures?.(action)) return view.handle(action);
  switch (action) {
    case 'lb': return switchTab(state.tab - 1);
    case 'rb': return switchTab(state.tab + 1);
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

async function init() {
  state.info = (await attempt(() => api.systemInfo(), t('app.systemInfoFailed'))) || {};
  setAudioMode(state.info.audioMode ?? (state.info.sounds === false ? 'off' : 'ui'));
  setSoundVolume((state.info.soundVolume ?? 70) / 100);
  setMusicVolume((state.info.musicVolume ?? 35) / 100);
  setSoundSet(state.info.soundSet || null);
  configureScreensaver({ minutes: state.info.screensaverMinutes ?? 5, style: state.info.screensaverStyle ?? 'art', displayOff: state.info.displayOffMinutes ?? 0, pixelShift: state.info.pixelShift ?? true });
  startHdr();
  startMotion();
  state.skins = [...BUILTIN_SKINS, ...((await attempt(() => api.listSkins(), t('app.skinsFailed'))) || [])];
  applySkin(state.skins.find((s) => s.id === state.info.skin) || BUILTIN_SKINS[0]);
  updateTabs();
  startWeather();
  startUpdates();
  startParental({ detailPage, closeQuickMenu, redrawQuick: () => quick.open && renderQuick() });
  startAssistant();
  startNotifications();
  startBluetoothRecovery();
  // First start: the setup assistant, with the disclaimer and privacy notice as its second step (on a locked couch only the notice).
  startFirstRun().catch(console.error);
  // Settings that add or remove a tab (assistant, smart home) ask for a refresh.
  window.addEventListener('couchhud:tabs', updateTabs);
  // The assistant can change the design and settings from the host side.
  api.onSkin?.((id) => {
    const skin = state.skins.find((s) => s.id === id);
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
  });
  attempt(() => api.getSettings()).then((s) => preloadSmartHome(s));
  startCaseHud();

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
  if (state.info.nativeInput) input.attachNative(api);
  else input.attachGamepadApi();

  api.onNav((target) => {
    input.releaseAll();
    if (state.overlay) closeOverlay();
    hideLaunch();
    if (target === 'quick') return openQuickMenu();
    closeQuickMenu();
    goHome();
    // Typing an address after the page's Y button: the Browser tab, controller on its bar.
    if (target === 'browser') switchTab(tabsIndexOf(browserView));
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
  api.onPad(async ({ connected }) => {
    if (connected) return toast(t('app.padConnected'));
    // The last controller is gone (Bluetooth off, battery empty): say that keyboard and mouse still work.
    const pads = await api.getPads?.().catch(() => null);
    toast(Array.isArray(pads) && pads.length === 0 ? t('app.padNone') : t('app.padDisconnected'), Array.isArray(pads) && pads.length === 0 ? 8000 : undefined);
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
  setTimeout(hideBoot, Math.max(0, 900 - (performance.now() - bootStarted)));
}

init();
