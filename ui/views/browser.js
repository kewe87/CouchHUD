// The Browser tab: its own page, like Home or Games. The page runs in a native web view (the host puts it exactly over
// the placeholder below the bar); the bar, the start page and the hints are HUD, so they follow every skin.
// Without a page open the tab is a start page: address or search, recent sites, bookmarks.
// The controller is either "in the page" (the host steers it: sticks are the mouse, or arrow keys on TV pages) or "in the
// bar" (the HUD steers: left/right pick a button, A presses, down or B go back to the page).

import { lang, t } from '../core/i18n.js';
import { iconEl } from '../core/glyph-icons.js';
import { api } from '../core/api.js';
import { el, keepInView, setFocused } from '../core/dom.js';
import { hue } from '../core/art.js';
import { attempt, toast } from '../core/feedback.js';
import { currentView, move, render, renderHints, switchTab, tabViews } from '../core/nav.js';
import { state } from '../core/state.js';
import { openKeyboard } from '../components/keyboard.js';
import { chooseDialog, closeTagged, confirmDialog, openOverlay, overlayFree } from '../components/overlay.js';
import { quick } from '../components/quick-menu.js';
import { sound } from '../sound.js';
import { ensureUnlocked } from '../components/passcode.js';
import { holdScreensaver, screensaverActivity } from '../core/screensaver.js';
import { cleanRecent, closeCountdown, dialogKind, fileSize, forgetRecent, forgetSite, host, initial, reconcileFocus, recentNote, rememberable, rememberSite, shortHost, siteHost, visibleRecent } from '../core/browser-logic.js';
import { ensureParentGrant } from '../components/parental.js';
import { favorites, toggleFavoriteSite } from '../core/favorites.js';

/** Whether a bookmark is marked as a favorite website (the Favorites tab shows it). */
const isFavoriteSite = (url) => favorites.entries.some((e) => e.kind === 'site' && e.url === url);

const ZOOMS = [100, 125, 150, 175, 200, 250];
const RECENT_KEY = 'couchhud.browser.recent';

let browserState = null;
/** When the last browser state arrived (performance.now()): the close countdown counts from here. */
let browserStateAt = 0;

/** An answer of the host with the browser state (null: none, the old one stays). */
function takeState(s) {
  if (!s) return;
  browserState = s;
  browserStateAt = performance.now();
  forgetAllWhenOff(s);
}

/**
 * History off in the settings: no recent sites are kept, and the ones stored before are deleted. Only on the host's own state, i.e.
 * after it accepted the switch (with parental controls only with the parents' code), never on the click alone.
 */
function forgetAllWhenOff(s) {
  if (forgetRecent(s) && recent.length) storeRecent([]);
}
let recent = loadRecent();

/**
 * The recent sites the start page may show right now (none while hidden, locked or unknown), for the Favorites tab. Computed from the
 * current state on every call, no copy: a lock, a profile switch or switching the history off shows no address, not even for one render.
 */
export const recentSitesNow = (limit = 4) => visibleRecent(recent, browserState, limit);

/** Asks the host for the browser's state when none arrived yet (the recent sites need it to know whether they may show). */
export async function ensureBrowserState() {
  if (browserState) return;
  takeState(await attempt(() => api.getBrowser()));
  if (browserState) render();
}
/** Where the user came from when the browser opened (tab and pages on top of it), for "back" on the first page. */
let origin = null;

function loadRecent() {
  try {
    const stored = localStorage.getItem(RECENT_KEY) ?? '[]';
    // Older versions kept full addresses (query strings can hold tokens): cleaned on load and written back without them.
    const list = cleanRecent(JSON.parse(stored));
    const cleaned = JSON.stringify(list);
    if (cleaned !== stored) localStorage.setItem(RECENT_KEY, cleaned);
    return list;
  } catch {
    return [];
  }
}

function remember(url, title) {
  storeRecent(rememberSite(recent, url, title));
}

function storeRecent(next) {
  if (next === recent) return;
  recent = next;
  try {
    localStorage.setItem(RECENT_KEY, JSON.stringify(recent));
  } catch { /* storage blocked: the list lives until the next start */ }
}

/** With the couch lock on, websites need the code: their upload and print dialogs lead into Windows. */
export const open = async (text) => (await ensureUnlocked(t('browser.openLock')))
  && attempt(() => api.openBrowser(text), t('browser.openFailed'));

async function openAddress() {
  if (!(await ensureUnlocked(t('browser.openLock')))) return;
  const text = await openKeyboard({ title: t('browser.addressTitle'), hint: t('browser.addressHint') });
  if (!text) return;
  await open(text);
}

async function bookmarkThis() {
  const s = browserState;
  if (!s?.open) return;
  const name = await openKeyboard({ title: t('browser.bookmarkName'), value: s.title || host(s.url) });
  if (name === undefined) return;
  takeState(await attempt(() => api.addBookmark(name, s.url), t('browser.notSaved')));
  toast(t('browser.bookmarkSaved'));
  render();
}

// i18n-used: browser.bar.back browser.bar.forward browser.bar.reload browser.bar.zoomOut browser.bar.zoomIn browser.bar.fullscreen browser.bar.close browser.bookmarkThis
// Bar buttons: Segoe Fluent Icons (Windows 11; Segoe MDL2 Assets before).
const BAR = [
  { id: 'back', icon: '', label: 'browser.bar.back', run: () => api.browserCommand('back'), enabled: (s) => s.canGoBack },
  { id: 'forward', icon: '', label: 'browser.bar.forward', run: () => api.browserCommand('forward'), enabled: (s) => s.canGoForward },
  { id: 'reload', icon: '', label: 'browser.bar.reload', run: () => api.browserCommand('reload') },
  { id: 'address', wide: true, run: () => openAddress() },
  { id: 'bookmark', icon: '', label: 'browser.bookmarkThis', run: () => bookmarkThis() },
  { id: 'zoomOut', icon: '', label: 'browser.bar.zoomOut', run: () => api.browserCommand('zoomOut') },
  { id: 'zoomIn', icon: '', label: 'browser.bar.zoomIn', run: () => api.browserCommand('zoomIn') },
  { id: 'fullscreen', icon: '', label: 'browser.bar.fullscreen', run: () => { focusPage(); return api.browserCommand('fullscreen'); } },
  { id: 'close', icon: '', label: 'browser.bar.close', run: () => api.browserCommand('close') }
];

export const browserView = {
  id: 'browser',
  get name() { return t('browser.title'); },
  index: 0,
  /** 'page': the host steers the page; 'bar': the HUD steers the bar. */
  focus: 'bar',
  barIndex: 0,
  onEnter() {
    // Coming by LB/RB: the controller stays in the HUD, so LB/RB keep switching tabs.
    this.focus = 'bar';
    // The lock state may have changed since the last answer: asked again (the recent sites follow it).
    this.load();
  },
  async load() {
    takeState(await attempt(() => api.getBrowser(), t('browser.unavailable')));
    if (currentView() === this) render();
  },
  /** The parents' code for the recent sites; the host checks it (used up) and says whether they may be seen now. */
  async revealRecent() {
    if (!(await ensureParentGrant(t('browser.recentParentReason'), { action: 'browserRecent' }))) return;
    takeState(await attempt(() => api.revealBrowserRecent()));
    if (currentView() === this) render();
  },
  get isOpen() {
    return !!browserState?.open;
  },
  rows() {
    const s = browserState;
    const rows = [{ icon: '⌕', label: t('browser.enterAddress'), detail: t('browser.enterAddressDetail'), run: openAddress }];
    // Locked couch or parental controls: no titles, no addresses of the recent sites (see visibleRecent), only a note where they
    // went. The couch code opens the couch; with parental controls only the parents' code shows them (for this session).
    const note = recentNote(s, recent.length > 0);
    if (note === 'lock') {
      rows.push({ icon: '🔒', label: t('browser.recentLocked'), detail: t('browser.recentLockedDetail'), run: () => ensureUnlocked(t('browser.openLock')).then((ok) => ok && this.load()) });
    } else if (note === 'parent') {
      rows.push({ icon: '🔒', label: t('browser.recentLocked'), detail: t('browser.recentParentDetail'), run: () => this.revealRecent() });
    }
    for (const r of visibleRecent(recent, s)) {
      rows.push({ recent: r, label: r.title, detail: siteHost(r.url), run: () => open(r.url) });
    }
    for (const b of s?.bookmarks ?? []) {
      rows.push({ bookmark: b, label: b.name, detail: isFavoriteSite(b.url) ? `★ ${siteHost(b.url)}` : siteHost(b.url), run: () => open(b.url) });
    }
    rows.push({
      icon: '🔍',
      label: t('browser.zoom'),
      detail: t('browser.zoomDetail', { value: s?.zoom ?? 150 }),
      run: async () => {
        const z = await chooseDialog(t('browser.zoomTitle'), ZOOMS.map((v) => ({ label: t('browser.percent', { value: v }), value: v })), s?.zoom ?? 150);
        if (z === undefined) return;
        takeState(await attempt(() => api.setBrowserZoom(z)));
        render();
      }
    });
    return rows;
  },
  build(root) {
    if (!browserState) this.load();
    this.slot = null;
    if (this.isOpen) return this.buildPage(root);
    this.list = this.rows();
    this.index = Math.min(this.index, this.list.length - 1);
    this.items = this.list.map((r) => {
      const site = r.bookmark ?? r.recent;
      const badge = site
        ? el('span', { className: 'browser-badge', textContent: initial(site.url) })
        : el('span', { className: 'check-box' }, iconEl(r.icon));
      if (site) badge.style.setProperty('--h', hue(site.url));
      return el('li', { className: site ? 'check-row browser-site' : 'check-row' }, badge,
        el('span', { className: 'check-main' }, el('strong', { textContent: r.label }), el('small', { textContent: r.detail })));
    });
    this.scroller = el('ul', { className: 'check-list list-scroll' }, this.items);
    root.append(el('div', { className: 'files' },
      el('div', { className: 'files-head' },
        el('h2', { textContent: t('browser.title') }),
        el('p', { className: 'meta', textContent: t('browser.startHelp') })),
      this.scroller));
    this.update({ instant: true });
  },
  buildPage(root) {
    this.list = null;
    this.items = null;
    this.buttons = BAR.map((b) => b.id === 'address'
      ? el('div', { className: 'bbar-address', dataset: { bar: b.id } },
        el('span', { className: 'bbar-lock', textContent: '' }),
        el('span', { className: 'bbar-url' }))
      : el('button', { className: 'bbar-btn', type: 'button', tabIndex: -1, ariaLabel: t(b.label), title: t(b.label), dataset: { bar: b.id }, textContent: b.icon }));
    this.slot = el('div', { className: 'browser-slot' });
    root.append(el('div', { className: 'browser-page' },
      el('div', { className: 'bbar' }, this.buttons),
      this.slot));
    this.barIndex = Math.min(this.barIndex, BAR.length - 1);
    this.updateBar();
  },
  /** Texts and states of the bar without a rebuild (the placeholder must stay where it is). */
  updateBar() {
    if (!this.buttons) return;
    const s = browserState ?? {};
    BAR.forEach((b, i) => {
      const node = this.buttons[i];
      node.classList.toggle('disabled', b.enabled ? !b.enabled(s) : false);
      node.classList.toggle('focused', this.focus === 'bar' && i === this.barIndex);
      if (b.id === 'address') {
        node.querySelector('.bbar-url').textContent = s.title ? `${s.title} · ${host(s.url)}` : host(s.url ?? '');
        node.querySelector('.bbar-lock').hidden = !/^https:/i.test(s.url ?? '') || !!s.failed;
      }
    });
    this.slot?.classList.toggle('page-focus', this.focus === 'page');
  },
  update({ instant = false } = {}) {
    if (this.buttons) return this.updateBar();
    if (!this.items?.length) return;
    setFocused(this.items, this.index);
    keepInView(this.scroller, this.items[this.index], { instant });
  },
  captures(action) {
    // In the bar B returns to the page; everywhere else B keeps its global meaning (the tab bar, the page it came from).
    return this.isOpen && this.focus === 'bar' && action === 'b';
  },
  setFocus(focus) {
    this.focus = focus;
    this.updateBar();
    renderHints();
  },
  async handle(action) {
    if (this.isOpen) return this.handlePage(action);
    const row = this.list?.[this.index];
    switch (action) {
      case 'up': return move(this, -1, this.list.length, true);
      case 'down': return move(this, 1, this.list.length, true);
      case 'a':
        sound.select();
        return row?.run();
      case 'x': {
        // A bookmark becomes a favorite website (or stops being one); the host refuses while websites are off for Favorites.
        const mark = row?.bookmark;
        if (!mark) return sound.bump();
        if (!favorites.sites && !isFavoriteSite(mark.url)) return toast(t('browser.favoriteSitesOff'), 4000);
        await toggleFavoriteSite(mark.url, mark.name);
        return render();
      }
      case 'y': {
        // A recent site is only on the list while it may be seen (unlocked, parental controls answered), so it may be removed too.
        if (row?.recent) {
          if (!(await confirmDialog(t('browser.forgetConfirm', { name: row.recent.title }), t('browser.remove')))) return;
          storeRecent(forgetSite(recent, row.recent.url));
          return render();
        }
        const site = row?.bookmark;
        if (!site) return sound.bump();
        if (!(await confirmDialog(t('browser.removeConfirm', { name: site.name }), t('browser.remove')))) return;
        takeState(await attempt(() => api.removeBookmark(site.url)));
        return render();
      }
    }
  },
  handlePage(action) {
    // The page is the host's while it has the controller; these only arrive from the keyboard or when the host is not asked.
    if (this.focus === 'page') {
      if (action === 'up' || action === 'back' || action === 'view') return this.setFocus('bar');
      // Enter (A) while the keyboard is in the HUD (a mouse click beside the page took it): back into the page.
      if (action === 'a') return refocusPage();
      return;
    }
    const enabled = (i) => !BAR[i].enabled || BAR[i].enabled(browserState ?? {});
    switch (action) {
      case 'left':
      case 'right': {
        const step = action === 'left' ? -1 : 1;
        let i = this.barIndex + step;
        while (i >= 0 && i < BAR.length && !enabled(i)) i += step;
        if (i < 0 || i >= BAR.length) return sound.bump();
        this.barIndex = i;
        sound.move();
        return this.updateBar();
      }
      case 'a':
        if (!enabled(this.barIndex)) return sound.bump();
        sound.select();
        return BAR[this.barIndex].run();
      case 'y':
        return openAddress();
      case 'down':
      case 'b':
      case 'back':
      case 'view':
        return focusPage();
      default:
        return sound.bump();
    }
  },
  pointer(node, index, dispatch) {
    if (this.buttons) {
      const i = this.buttons.indexOf(node.closest('[data-bar]'));
      if (i < 0) return;
      this.focus = 'bar';
      this.barIndex = i;
      this.updateBar();
      return dispatch('a');
    }
    this.index = index;
    this.update();
    dispatch('a');
  },
  hints() {
    if (this.isOpen) {
      return this.focus === 'page'
        ? (state.inputSource === 'keyboard'
          ? [['F6', t('browser.hintBar')], ['F11', t('browser.hintFullscreen')], ['Y', t('browser.hintAddress')]]
          : [['A', t('browser.hintClick')], ['B', t('browser.hintBack')], ['X', t('browser.hintType')], ['Y', t('browser.hintAddress')], ['RT', t('browser.hintEnter')], ['LB', t('browser.hintZoomOut')], ['RB', t('browser.hintZoomIn')], ['View', t('browser.hintBar')]])
        : [['←→', t('browser.hintPick')], ['A', t('browser.hintPress')], ['Y', t('browser.hintAddress')], ['B', t('browser.hintToPage')], ['LB', t('browser.hintTabs')]];
    }
    const row = this.list?.[this.index];
    if (row?.bookmark) {
      const star = favorites.sites || isFavoriteSite(row.bookmark.url) ? [['X', isFavoriteSite(row.bookmark.url) ? t('browser.hintUnfavorite') : t('browser.hintFavorite')]] : [];
      return [['A', t('browser.hintOpen')], ...star, ['Y', t('browser.hintRemove')], ['B', t('browser.hintBack')]];
    }
    return row?.recent ? [['A', t('browser.hintOpen')], ['Y', t('browser.hintRemove')], ['B', t('browser.hintBack')]] : [['A', t('browser.hintOpen')], ['B', t('browser.hintBack')]];
  }
};

function focusPage() {
  browserView.setFocus('page');
  sync();
}

/** The HUD shows the page focused but the keys arrive here: ask the host again, it puts the keyboard focus into the page. */
function refocusPage() {
  sent = { ...sent, focus: false };
  sync();
}

const pageOpen = () => currentView() === browserView && browserView.isOpen;

// A click with the mouse beside the page puts the keyboard into the HUD: the bar gets the visible focus, so it is clear where keys go.
window.addEventListener('pointerdown', () => {
  if (pageOpen() && browserView.focus === 'page') {
    browserView.setFocus('bar');
    sync();
  }
}, true);

// F6 works in the HUD as in the page: between the page and the bar.
window.addEventListener('keydown', (e) => {
  if (e.key !== 'F6' || !pageOpen() || state.overlay) return;
  e.preventDefault();
  if (browserView.focus === 'page') {
    browserView.setFocus('bar');
    sync();
  } else {
    focusPage();
  }
});

function showTab() {
  const tabs = tabViews();
  const at = tabs.indexOf(browserView);
  if (at < 0) return;
  if (currentView() !== browserView) {
    origin = { tab: state.tab, stack: [...state.stack] };
    state.stack = [];
    state.tab = at;
    sound.tab();
  }
  browserView.focus = 'page';
  render();
}

function leaveToOrigin() {
  if (!origin) return switchTab(0);
  const { tab, stack } = origin;
  origin = null;
  state.stack = stack;
  state.tab = tab;
  sound.back();
  render();
}

function onBrowserState(s) {
  const wasOpen = browserState?.open;
  const wasLocked = browserState?.locked;
  const wasHidden = [browserState?.recentHidden, browserState?.recentNeedsParent, browserState?.history].join();
  browserState = s;
  browserStateAt = performance.now();
  forgetAllWhenOff(s);
  if (s?.history !== false && rememberable(s)) remember(s.url, s.title);
  if (currentView() !== browserView) return;
  // Open/closed, locked/unlocked and parental controls change the start page (the recent sites and the note in their place).
  const hidden = [s?.recentHidden, s?.recentNeedsParent, s?.history].join();
  if (!!wasOpen !== !!s?.open || wasLocked !== s?.locked || wasHidden !== hidden) return render();
  browserView.updateBar();
}

api.onBrowser?.(onBrowserState);

// The host tells what the controller did on the page: to the bar, Y for the address, X for typing, B on the first page, and
// when it gave the page the controller itself (full screen). The HUD follows, so it never shows the page focused while the
// host steers the HUD, or the other way round (a browser that seemed not to react any more).
api.onBrowserUi?.((what) => {
  if (what && typeof what === 'object') {
    if (what.type === 'typing') typeIntoPage(!!what.secret).catch(console.error);
    return;
  }
  switch (what) {
    case 'show':
      return showTab();
    case 'closed':
      browserView.focus = 'bar';
      if (currentView() === browserView) render();
      return;
    case 'page':
      sent = { ...sent, focus: true };
      return browserView.setFocus('page');
    case 'bar':
      sent = { ...sent, focus: false };
      return browserView.setFocus('bar');
    case 'address':
      sent = { ...sent, focus: false };
      browserView.setFocus('bar');
      return openAddress();
    case 'exit':
      return leaveToOrigin();
    case 'activity':
      // The controller on the page: someone is there, though the HUD saw no button.
      return screensaverActivity();
  }
});

/** X on the page: the on-screen keyboard types into the page's focused field (masked, and without the phone, for a password). */
async function typeIntoPage(secret) {
  sent = { ...sent, focus: false };
  browserView.setFocus('bar');
  const text = await openKeyboard({ title: t('browser.typeTitle'), hint: t('browser.typeHint'), secret, maxLength: 4000 });
  // Back to the page in any case: the text goes into the field that has the focus there, so the page must be shown again first.
  focusPage();
  if (!text) return;
  await new Promise((resolve) => setTimeout(resolve, 150));
  await attempt(() => api.browserType(text, false), t('browser.typeFailed'));
}

// ---- A web page's questions (alert, confirm, prompt) and downloads: asked here with the site's name, never as a Windows window

/** The site that asks; a frame without an address of its own is shown as embedded in the page. */
const webSite = (d) => {
  const site = d.host || t('browser.dialog.page');
  return d.embedded ? t('browser.dialog.embedded', { host: site }) : site;
};

const answer = (id, ok, text = null) => Promise.resolve(api.browserDialogAnswer?.(id, ok, text)).catch(() => {});

// Questions the host ended while they were still waiting for the screen (the user left, the page went away).
const endedDialogs = new Set();

async function onPageDialog(d) {
  const kind = dialogKind(d);
  if (!kind) return;
  const tag = `browser-dialog-${d.id}`;
  if (kind === 'closed') {
    endedDialogs.add(d.id);
    return closeTagged(tag);
  }
  await overlayFree();
  if (endedDialogs.delete(d.id)) return;
  // The user is no longer at the page (the guide button went home meanwhile): its question is not shown over the HUD.
  if (currentView() !== browserView || !browserView.isOpen) return answer(d.id, false);
  // Always the site's name, and always marked as the web's (globe, own frame): never in the look of CouchHUD's own questions.
  const site = webSite(d);
  switch (kind) {
    case 'alert':
      return openOverlay({
        title: t('browser.dialog.says', { host: site }),
        message: d.message ?? '',
        tag,
        web: true,
        onCancel: () => answer(d.id, true),
        entries: [{ label: t('browser.dialog.ok'), run: () => answer(d.id, true) }]
      });
    case 'confirm':
      return answer(d.id, await confirmDialog(t('browser.dialog.asks', { host: site }), t('browser.dialog.ok'), { message: d.message ?? '', tag, web: true }));
    case 'prompt': {
      const text = await openKeyboard({ title: `🌐 ${t('browser.dialog.asks', { host: site })}`, hint: d.message ?? '', value: d.defaultText ?? '', maxLength: 2000, web: true });
      return answer(d.id, text !== undefined, text ?? null);
    }
    case 'download': {
      const size = fileSize(d.bytes, lang());
      // The file type on its own line, never cut with a long name.
      const type = t('browser.download.type', { type: d.extension ? d.extension.toUpperCase() : t('browser.download.noType') });
      const ok = await confirmDialog(t('browser.download.title', { host: site }), t('browser.download.save'),
        { message: [size ? `${d.message} · ${size}` : (d.message ?? ''), type].join('\n'), tag, focusCancel: true, web: true });
      return answer(d.id, ok);
    }
  }
}

api.onBrowserDialog?.((d) => { onPageDialog(d).catch(console.error); });

// ---- The page lies over the placeholder: report where it is (and whether it may be seen)

let sent = { visible: false, key: '', focus: false };

/** True while a menu, dialog or other page would be painted below the native page: it must step aside. */
const covered = () => !!state.overlay || !!quick.open || !!document.querySelector('#screensaver');

// No screensaver over a page that plays (sound or a full-screen video), at most 3 hours without input; reading counts through
// the host's activity reports ('activity' above), like any other input.
holdScreensaver(() => sent.visible && !!browserState?.playing);

/** Where the box is in the window, without the transforms of an enter animation (offsets ignore them). */
function layoutRect(node) {
  let left = 0;
  let top = 0;
  for (let n = node; n; n = n.offsetParent) {
    left += n.offsetLeft;
    top += n.offsetTop;
  }
  return { left, top, width: node.offsetWidth, height: node.offsetHeight };
}

// In full screen the page leaves a strip at the top to the HUD: there the HUD says it is a website, which no page can paint over.
let fullBadge = null;

function showFullBadge(on) {
  if (!on) {
    fullBadge?.remove();
    fullBadge = null;
    return;
  }
  if (!fullBadge?.isConnected) {
    // The web mark ("Website in full screen") never shrinks; only the host is cut (shortHost keeps the end of the domain).
    // The countdown is not read out every second (aria-live off); a screen reader hears the start of the warning once (.web-badge-say).
    fullBadge = el('div', { id: 'web-fullscreen-badge', role: 'status' },
      el('span', { className: 'web-badge-icon', textContent: '🌐' }),
      el('span', { className: 'web-badge-label' }),
      el('span', { className: 'web-badge-host' }),
      el('span', { className: 'web-badge-say' }));
    document.body.append(fullBadge);
  }
  const label = fullBadge.querySelector('.web-badge-label');
  const labelText = `${t('browser.fullscreenLabel')} ·`;
  if (label.textContent !== labelText) label.textContent = labelText;
  // The site of right now (a redirect in full screen changes it), on every round.
  const site = host(browserState?.url ?? '') || t('browser.dialog.page');
  const hostEl = fullBadge.querySelector('.web-badge-host');
  const hostText = shortHost(site);
  if (hostEl.textContent !== hostText) hostEl.textContent = hostText;
  // The unlock runs out: the page covers the HUD's toast, so the warning and its countdown are here, where no page can paint
  // (nor fake or hide it). Any key or button extends; the host then sends closesInMs null and the warning is gone.
  const left = closeCountdown(browserState, browserStateAt, performance.now());
  const say = fullBadge.querySelector('.web-badge-say');
  let warn = fullBadge.querySelector('.web-badge-warn');
  if (left === null) {
    warn?.remove();
    if (say.textContent) say.textContent = '';
    fullBadge.classList.remove('closing');
    return;
  }
  if (!warn) {
    warn = el('span', { className: 'web-badge-warn' });
    warn.setAttribute('aria-live', 'off');
    fullBadge.insertBefore(warn, say);
    // Said once, when the warning starts.
    say.textContent = t('browser.closesSoonSay');
  }
  fullBadge.classList.add('closing');
  const warnText = t('browser.closesSoonStrip', { s: left });
  if (warn.textContent !== warnText) warn.textContent = warnText;
}

function sync() {
  const slot = browserView.slot;
  const visible = currentView() === browserView && browserView.isOpen && !!slot?.isConnected && !covered();
  showFullBadge(visible && !!browserState?.fullScreen);
  let key = '';
  let r = null;
  if (visible) {
    r = layoutRect(slot);
    key = [r.left, r.top, r.width, r.height].join(',');
  }
  const focus = visible && browserView.focus === 'page';
  if (visible && (!sent.visible || key !== sent.key)) {
    api.browserLayout(r.left, r.top, r.width, r.height, true).catch(() => {});
    sent = { ...sent, visible: true, key };
  } else if (!visible && sent.visible) {
    api.browserLayout(0, 0, 0, 0, false).catch(() => {});
    sent = { ...sent, visible: false, key: '' };
  }
  if (focus !== sent.focus) {
    sent = { ...sent, focus };
    // The host answers who has the controller now. A refused "page" puts the HUD back on the bar; a failed call lets the next
    // sync ask again.
    Promise.resolve(api.browserFocus(focus)).then((granted) => {
      if (sent.focus !== focus) return;
      const next = reconcileFocus(browserView.focus, { requested: focus, granted });
      if (next === browserView.focus) return;
      sent = { ...sent, focus: false };
      browserView.setFocus(next);
    }).catch(() => {
      if (sent.focus === focus) sent = { ...sent, focus: !focus };
    });
  }
}

setInterval(sync, 100);
window.addEventListener('resize', () => sync());
