// Adding, scanning, editing and hiding library entries - all with the controller.

import { iconEl } from '../core/glyph-icons.js';
import { api } from '../core/api.js';
import { el, formatSize, keepInView, setFocused } from '../core/dom.js';
import { attempt, toast } from '../core/feedback.js';
import { findItem, setLibrary } from '../core/library.js';
import { move, pop, push, render } from '../core/nav.js';
import { state } from '../core/state.js';
import { sound } from '../sound.js';
import { chooseDialog, confirmDialog, openOverlay } from './overlay.js';
import { openFileBrowser } from './file-browser.js';
import { openKeyboard } from './keyboard.js';
import { detailPage } from '../views/detail.js';
import { ensureParentGrant, parental } from './parental.js';
import { ensureUnlocked, guarded } from './passcode.js';
import { artworkPickerPage } from './artwork-picker.js';
import { infoMenu, storeMatchMenu } from './item-info.js';
import { isWebUrl } from '../core/links.js';
import { lower, t } from '../core/i18n.js';
import { TARGET_EXTENSIONS, isScript, launchRows, scriptArgsProblem, targetLabel } from '../core/launch-rules.js';

const PROGRAM_EXTENSIONS = ['.exe', '.lnk', '.url', '.bat', '.cmd'];

/** "D:\Games\Hades II\Hades2.exe" -> "Hades2" */
const baseName = (path) => path.split(/[\\/]/).pop().replace(/\.[^.]+$/, '');

/**
 * Adds a game, an app or a web page. With a kind (the page it was started from) the question what to add is shorter:
 * games go straight to the file browser, apps offer the list of installed programs or a web page.
 * Needs the unlock, like everything that adds an entry.
 */
export async function addEntryFlow(options) {
  const preset = typeof options?.kind === 'string' ? options.kind : '';
  if (!(await ensureUnlocked(t('libraryTools.add.guard')))) return;
  const choices = [
    { label: t('libraryTools.add.game'), detail: t('libraryTools.add.gameDetail'), value: 'game' },
    { label: t('libraryTools.add.app'), detail: t('libraryTools.add.appDetail'), value: 'app' },
    { label: t('libraryTools.add.web'), detail: t('libraryTools.add.webDetail'), value: 'web' }
  ].filter((c) => !preset || (preset === 'game' ? c.value === 'game' : c.value !== 'game'));
  const kind = choices.length === 1 ? choices[0].value : await chooseDialog(t('libraryTools.add.title'), choices);
  if (!kind) return;

  let entry;
  if (kind === 'web') {
    const url = await openKeyboard({ title: t('libraryTools.url.title'), value: 'https://', hint: t('libraryTools.url.hint') });
    if (!url || url === 'https://') return;
    const name = await openKeyboard({ title: t('libraryTools.tile.title'), value: guessSiteName(url) });
    if (!name) return;
    entry = { name, type: 'app', url };
  } else if (kind === 'app') {
    // The list of installed programs (Start menu, Store apps included); "Other" at its end is the file browser.
    push(installedAppsPage(() => addFileFlow('app')));
    return;
  } else {
    await addFileFlow(kind);
    return;
  }
  await saveEntries([entry]);
}

/** An exe or a shortcut from the file browser. */
async function addFileFlow(kind) {
  const path = await openFileBrowser({ title: kind === 'game' ? t('libraryTools.pickGame') : t('libraryTools.pickApp'), mode: 'file', extensions: PROGRAM_EXTENSIONS });
  if (!path) return;
  const name = await openKeyboard({ title: t('libraryTools.name'), value: baseName(path) });
  if (!name) return;
  await saveEntries([{ name, type: kind, path }]);
}

/**
 * The installed programs as a list: A adds, X searches (on-screen keyboard), LB/RB jump to the previous/next initial letter.
 * The icons come in small batches for the rows around the focus.
 */
export function installedAppsPage(onOther) {
  const initial = (name) => (name.trim().charAt(0) || '#').toLocaleUpperCase();
  const letterOf = (name) => (/\p{L}/u.test(initial(name)) ? initial(name) : '#');
  return {
    id: 'installed-apps',
    name: t('libraryTools.apps.title'),
    index: 0,
    apps: null,
    query: '',
    icons: new Map(),
    asked: new Set(),
    captures: (action) => action === 'lb' || action === 'rb',
    async load() {
      const apps = await attempt(() => api.getInstalledApps(), t('libraryTools.apps.failed'));
      this.apps = apps ?? [];
      render();
    },
    shown() {
      const q = lower(this.query.trim());
      return (this.apps ?? []).filter((a) => !q || lower(a.name).includes(q));
    },
    build(root) {
      if (this.apps === null) {
        this.load();
        root.append(el('div', { className: 'files' }, el('div', { className: 'files-head' }, el('h2', { textContent: t('libraryTools.apps.title') }), el('p', { className: 'meta', textContent: t('libraryTools.apps.loading') }))));
        this.items = [];
        this.rows = [];
        return;
      }
      this.list = this.shown();
      // Every row, the "Other" row last: an extra entry in the same list, so it is reachable by scrolling down.
      this.rows = [...this.list.map((app) => ({ app })), { other: true }];
      this.index = Math.min(this.index, this.rows.length - 1);
      this.items = this.rows.map((row) => {
        if (row.other) {
          return el('li', { className: 'check-row update-row highlight' },
            el('span', { className: 'file-icon app-icon-box' }, iconEl('📁')),
            el('span', { className: 'check-main' }, el('strong', { textContent: t('libraryTools.apps.other') }), el('small', { textContent: t('libraryTools.apps.otherDetail') })));
        }
        const { app } = row;
        const icon = el('img', { className: 'app-icon', alt: '', dataset: { id: app.id } });
        if (this.icons.has(app.id)) icon.src = this.icons.get(app.id);
        const note = [app.isPackaged ? t('libraryTools.apps.store') : '', app.known ? t('libraryTools.apps.known') : ''].filter(Boolean).join(' · ');
        return el('li', { className: `check-row${app.known ? ' dim' : ''}` },
          el('span', { className: 'file-icon app-icon-box' }, icon),
          el('span', { className: 'check-main' }, el('strong', { textContent: app.name }), note ? el('small', { textContent: note }) : null),
          el('span', { className: 'letter', textContent: letterOf(app.name) }));
      });
      this.scroller = el('ul', { className: 'check-list list-scroll' }, this.items);
      const meta = [t('libraryTools.apps.count', { count: this.list.length }), this.query ? t('libraryTools.apps.search', { query: this.query }) : ''].filter(Boolean).join(' · ');
      root.append(el('div', { className: 'files app-picker' },
        el('div', { className: 'files-head' }, el('h2', { textContent: t('libraryTools.apps.title') }), el('p', { className: 'meta', textContent: meta })),
        this.list.length ? null : el('p', { className: 'empty-note', textContent: t('libraryTools.apps.none') }),
        this.scroller));
      this.update({ instant: true });
    },
    update({ instant = false } = {}) {
      if (!this.items?.length) return;
      setFocused(this.items, this.index);
      keepInView(this.scroller, this.items[this.index], { instant });
      this.loadIcons();
    },
    /** Icons for the rows around the focus that have none yet, a batch per call. */
    async loadIcons() {
      const from = Math.max(0, this.index - 6);
      const want = this.rows.slice(from, from + 24).map((r) => r.app).filter((a) => a && !this.icons.has(a.id) && !this.asked.has(a.id));
      if (!want.length) return;
      want.forEach((a) => this.asked.add(a.id));
      const found = await attempt(() => api.getAppIcons(want.map((a) => a.id)));
      for (const [id, url] of Object.entries(found ?? {})) {
        this.icons.set(id, url);
        for (const img of document.querySelectorAll('.app-picker .app-icon')) if (img.dataset.id === id) img.src = url;
      }
    },
    async add(app) {
      if (app.known && !(await confirmDialog(t('libraryTools.apps.again', { name: app.name }), t('libraryTools.apps.addAnyway')))) return;
      const library = await attempt(() => api.addInstalledApp(app.id, app.name), t('libraryTools.addFailed'));
      if (!library) return;
      setLibrary(library);
      app.known = true;
      toast(t('libraryTools.added', { name: app.name }));
      render();
    },
    async searchApps() {
      const query = await openKeyboard({ title: t('libraryTools.apps.searchTitle'), value: this.query, hint: t('libraryTools.apps.searchHint') });
      if (query === undefined) return;
      this.query = query.trim();
      this.index = 0;
      render();
    },
    jumpLetter(step) {
      if (!this.list.length) return;
      const letters = this.list.map((a) => letterOf(a.name));
      const here = letters[Math.min(this.index, letters.length - 1)];
      const distinct = [...new Set(letters)];
      const target = distinct[(distinct.indexOf(here) + step + distinct.length) % distinct.length];
      this.index = letters.indexOf(target);
      sound.tab();
      this.update();
    },
    async handle(action) {
      if (this.apps === null) return;
      switch (action) {
        case 'up': return move(this, -1, this.rows.length, true);
        case 'down': return move(this, 1, this.rows.length, true);
        case 'lb': return this.jumpLetter(-1);
        case 'rb': return this.jumpLetter(1);
        case 'x': return this.searchApps();
        case 'a': {
          sound.select();
          const row = this.rows[this.index];
          if (row?.other) {
            pop();
            return onOther();
          }
          return row ? this.add(row.app) : undefined;
        }
      }
    },
    pointer(node, index, dispatch) {
      this.index = index;
      this.update();
      dispatch('a');
    },
    hints() {
      return [['A', t('libraryTools.apps.hint.add')], ['X', t('libraryTools.apps.hint.search')], ['LB/RB', t('libraryTools.apps.hint.letters')], ['B', t('libraryTools.apps.hint.back')]];
    }
  };
}

function guessSiteName(url) {
  try {
    const host = new URL(url).hostname.replace(/^www\./, '').split('.')[0];
    return host.charAt(0).toUpperCase() + host.slice(1);
  } catch {
    return '';
  }
}

async function saveEntries(entries) {
  // A game starts on a locked couch without asking: with parental controls on, only with the parents' code.
  if (parental.status?.enabled && entries.some((e) => e.type === 'game') && !(await ensureParentGrant(t('libraryTools.guardGame'), { action: 'addGame' }))) return false;
  const library = await attempt(() => api.addEntries(entries), t('libraryTools.addFailed'));
  if (!library) return false;
  setLibrary(library);
  toast(entries.length === 1 ? t('libraryTools.added', { name: entries[0].name }) : t('libraryTools.addedMany', { count: entries.length }));
  return true;
}

/** Folder scan: pick a folder, get one suggested exe per game folder, choose which to add. */
export async function scanFolderFlow() {
  const folder = await openFileBrowser({ title: t('libraryTools.scan.pickFolder'), mode: 'folder' });
  if (!folder) return;
  toast(t('libraryTools.scan.searching'), 1500);
  const found = await attempt(() => api.scanFolder(folder), t('libraryTools.scan.failed'));
  if (!found) return;
  if (!found.length) return toast(t('libraryTools.scan.none'), 4000);

  const known = new Set(state.library.filter((i) => i.launch?.kind === 'exe').map((i) => i.launch.target.toLowerCase()));
  const rows = found.map((c) => ({ ...c, checked: !known.has(c.path.toLowerCase()), known: known.has(c.path.toLowerCase()) }));
  push(scanResultsPage(folder, rows));
}

function scanResultsPage(folder, rows) {
  return {
    id: 'scan-results',
    name: t('libraryTools.scan.name'),
    index: 0,
    captures: (action) => action === 'start',
    build(root) {
      this.items = rows.map((r) => el('li', { className: `check-row${r.checked ? ' checked' : ''}` },
        el('span', { className: 'check-box', textContent: r.checked ? '✓' : '' }),
        el('span', { className: 'check-main' },
          el('strong', { textContent: r.name }),
          el('small', { textContent: `${r.path} · ${formatSize(r.size) ?? ''}${r.known ? ` · ${t('libraryTools.scan.known')}` : ''}` }))));
      const count = rows.filter((r) => r.checked).length;
      this.scroller = el('ul', { className: 'check-list list-scroll' }, this.items);
      root.append(el('div', { className: 'files' },
        el('div', { className: 'files-head' },
          el('h2', { textContent: t('libraryTools.scan.title', { count: rows.length }) }),
          el('p', { className: 'meta files-path', textContent: t('libraryTools.scan.summary', { folder, count }) })),
        this.scroller));
      this.update({ instant: true });
    },
    update({ instant = false } = {}) {
      setFocused(this.items, this.index);
      keepInView(this.scroller, this.items[this.index], { instant });
    },
    async rename() {
      const row = rows[this.index];
      const name = await openKeyboard({ title: t('libraryTools.name'), value: row.name });
      if (name) row.name = name;
      render();
    },
    async add() {
      const chosen = rows.filter((r) => r.checked);
      if (!chosen.length) return toast(t('libraryTools.scan.nothing'));
      if (await saveEntries(chosen.map((r) => ({ name: r.name, type: 'game', path: r.path })))) pop();
    },
    handle(action) {
      switch (action) {
        case 'up': return move(this, -1, rows.length, true);
        case 'down': return move(this, 1, rows.length, true);
        case 'a':
          rows[this.index].checked = !rows[this.index].checked;
          sound.tab();
          return render();
        case 'x': return this.rename();
        case 'start': return this.add();
      }
    },
    hints() {
      const row = rows[this.index];
      return [['A', row?.checked ? t('libraryTools.scan.hint.deselect') : t('libraryTools.scan.hint.select')], ['X', t('libraryTools.scan.hint.rename')], ['≡', t('libraryTools.scan.hint.add')], ['B', t('libraryTools.scan.hint.cancel')]];
    }
  };
}

async function applyEdit(item, changes, message) {
  const updated = await attempt(() => api.editItem(item.id, changes), t('libraryTools.edit.failed'));
  if (!updated) return;
  Object.assign(item, updated);
  toast(message);
  render();
}

/** One argument the way the host reads it back (the rules of CommandLineToArgvW): quoted when it has spaces or quotes. */
const quoteArg = (arg) => {
  if (arg.length > 0 && !/[\s"]/.test(arg)) return arg;
  return `"${arg.replace(/(\\*)"/g, '$1$1\\"').replace(/(\\+)$/, '$1$1')}"`;
};

export const formatArgs = (args = []) => args.map(quoteArg).join(' ');

/**
 * Changing what an entry starts runs any program: the unlock, and with parental controls on the parents' code for exactly this entry
 * (games and apps alike). The host checks the same again (Hud.RequireLaunchChange).
 */
async function guardLaunchChange(item) {
  if (!(await ensureUnlocked(t('libraryTools.edit.guardLaunch')))) return false;
  return !parental.status?.enabled
    || !!(await ensureParentGrant(t('libraryTools.edit.guardLaunchParental'), { action: 'startOptions', item: item.id }));
}

/** The detail page shows that own startup options are on. */
function markCustomLaunch(custom) {
  const page = state.stack.at(-1);
  if (page?.details) page.details.customLaunch = custom;
  render();
}

/** An entry the user added keeps its parameters and folder in its own fields. */
async function saveOwnLaunch(item, args, cwd) {
  if (!(await guardLaunchChange(item))) return false;
  const updated = await attempt(() => api.editLaunch(item.id, args, cwd), t('libraryTools.edit.failed'));
  if (!updated) return false;
  Object.assign(item, updated);
  toast(t('libraryTools.edit.launchSaved'));
  render();
  return true;
}

/** Every other entry: an override ({ args, cwd, target }: a string sets, null goes back to automatic). */
async function saveLaunchOverride(item, changes, message = t('libraryTools.edit.launchSaved')) {
  if (!(await guardLaunchChange(item))) return null;
  const view = await attempt(() => api.setLaunchOverride(item.id, changes), t('libraryTools.edit.failed'));
  if (!view) return null;
  toast(message);
  markCustomLaunch(view.custom);
  return view;
}

/** A note where the parameters are set instead (Steam's properties, the store's launcher): nothing pretends to work. */
function launchNote(title, message) {
  openOverlay({ title, message, entries: [{ label: t('libraryTools.edit.understood'), run: () => {} }] });
}

/** "Startup options": parameters, working folder and an own start target (.exe, .bat, .cmd in the game's folder). Expert mode only. */
async function startOptionsMenu(item) {
  if (!(await ensureUnlocked(t('libraryTools.edit.guardLaunch')))) return;
  const view = await attempt(() => api.getLaunchOptions(item.id), t('libraryTools.edit.failed'));
  const rows = launchRows(view, { expert: true });
  if (!rows) {
    if (view) toast(t('libraryTools.edit.launchNone'));
    return;
  }
  const own = view.argsMode === 'own';
  const args = formatArgs(view.args);
  const cwd = view.workingDirectory ?? '';
  const isDefaultCwd = !cwd || cwd.toLowerCase() === (view.programFolder ?? '').toLowerCase();
  const program = view.target ?? item.launch?.target;
  const saveArgs = (value) => (own ? saveOwnLaunch(item, value, isDefaultCwd ? '' : cwd) : saveLaunchOverride(item, { args: value }));
  const saveCwd = (folder) => (own ? saveOwnLaunch(item, args, folder ?? '') : saveLaunchOverride(item, { cwd: folder }));
  const entries = [];
  if (rows.args === 'edit') {
    entries.push({
      label: t('libraryTools.edit.args'),
      detail: args || t('libraryTools.edit.argsNone'),
      run: async () => {
        const value = await openKeyboard({ title: t('libraryTools.edit.argsTitle'), value: args, hint: isScript(program) ? t('libraryTools.edit.argsHintScript') : t('libraryTools.edit.argsHint') });
        if (value === undefined) return;
        if (scriptArgsProblem(value, program)) {
          toast(t('libraryTools.edit.scriptArgs'));
          return;
        }
        await saveArgs(value);
      }
    });
  } else if (rows.args === 'steam') {
    entries.push({
      label: t('libraryTools.edit.args'),
      detail: t('libraryTools.edit.argsInSteam'),
      run: () => launchNote(t('libraryTools.edit.args'), t('libraryTools.edit.argsInSteamHow'))
    });
  } else if (rows.args === 'launcher') {
    entries.push({
      label: t('libraryTools.edit.args'),
      detail: t('libraryTools.edit.argsInLauncher'),
      run: () => launchNote(t('libraryTools.edit.args'), t('libraryTools.edit.argsInLauncherHow'))
    });
  }
  if (rows.cwd) {
    entries.push({
      label: t('libraryTools.edit.cwd'),
      detail: isDefaultCwd ? t('libraryTools.edit.cwdDefault') : cwd,
      run: async () => {
        const choice = await chooseDialog(t('libraryTools.edit.cwd'), [
          { label: t('libraryTools.edit.cwdPick'), icon: '📁', value: 'pick' },
          ...(isDefaultCwd ? [] : [{ label: t('libraryTools.edit.cwdReset'), icon: '↻', value: 'reset' }])
        ]);
        if (choice === 'reset') await saveCwd(null);
        else if (choice === 'pick') {
          const folder = await openFileBrowser({ title: t('libraryTools.edit.cwdPickTitle'), mode: 'folder', start: view.programFolder ?? null });
          if (folder) await saveCwd(folder);
        }
      }
    });
  }
  if (rows.target) {
    entries.push({
      label: t('libraryTools.edit.target'),
      detail: targetLabel(view, t('libraryTools.edit.targetAuto')),
      run: async () => {
        const choice = await chooseDialog(t('libraryTools.edit.target'), [
          { label: t('libraryTools.edit.targetPick'), detail: t('libraryTools.edit.targetPickDetail'), icon: '📄', value: 'pick' },
          ...(view.target ? [{ label: t('libraryTools.edit.targetReset'), icon: '↻', value: 'reset' }] : [])
        ]);
        if (choice === 'reset') await saveLaunchOverride(item, { target: null }, t('libraryTools.edit.launchResetDone'));
        else if (choice === 'pick') {
          const path = await openFileBrowser({ title: t('libraryTools.edit.targetPickTitle'), mode: 'file', extensions: TARGET_EXTENSIONS, start: view.installFolder ?? null });
          if (path) await saveLaunchOverride(item, { target: path });
        }
      }
    });
  }
  if (rows.reset) {
    entries.push({
      label: t('libraryTools.edit.launchReset'),
      detail: t('libraryTools.edit.launchResetDetail'),
      run: async () => {
        if (!(await confirmDialog(t('libraryTools.edit.launchResetConfirm', { name: item.name }), t('libraryTools.edit.launchResetButton')))) return;
        await saveLaunchOverride(item, { target: null }, t('libraryTools.edit.launchResetDone'));
      }
    });
  }
  entries.push({ label: t('libraryTools.edit.cancel'), run: () => {} });
  openOverlay({ title: t('libraryTools.edit.launchTitle', { name: item.name }), entries });
}

/**
 * "Reload artwork": looks for this entry's pictures again, whatever the cache says. It changes nothing but the picture cache
 * (a picked image stays), so it needs no code, like renaming. The host sends the new library; the tile and the banner follow.
 */
async function reloadArtwork(item) {
  toast(t('artworkReload.running'));
  const result = await attempt(() => api.reloadArtwork(item.id), t('artworkReload.failed'));
  if (result === undefined) return;
  // i18n-used: artworkReload.found artworkReload.nothing artworkReload.failed artworkReload.busy
  toast(t(result === 'found' ? 'artworkReload.found' : result === 'nothing' ? 'artworkReload.nothing' : result === 'busy' ? 'artworkReload.busy' : 'artworkReload.failed'));
}

/**
 * Edit menu on the detail page: name, artwork, texts and store entry in both modes; startup options (parameters, folder, own start target)
 * are technical, so expert mode only, for every source that has any. Then game/app, hiding, and what only own entries have.
 */
export async function editItemMenu(item) {
  const expert = !!(await attempt(() => api.getSettings()))?.expertMode;
  const entries = [
    {
      label: t('libraryTools.edit.rename'),
      icon: '✎',
      run: async () => {
        const name = await openKeyboard({ title: t('libraryTools.edit.newName'), value: item.name });
        if (name !== undefined) await applyEdit(item, { name }, name ? t('libraryTools.edit.renamedTo', { name }) : t('libraryTools.edit.nameReset'));
      }
    },
    { label: t('artworkPicker.action'), icon: '🖼', run: guarded(t('artworkPicker.title'), () => push(artworkPickerPage(item))) },
    { label: t('artworkReload.action'), detail: t('artworkReload.detail'), icon: '↻', run: () => reloadArtwork(item) },
    { label: t('itemInfo.action'), detail: t('itemInfo.actionDetail'), icon: 'ℹ', run: () => infoMenu(item) },
    ...(item.source !== 'steam'
      ? [{ label: t('itemInfo.store.action'), detail: t('itemInfo.store.actionDetail'), icon: '🛒', run: () => storeMatchMenu(item) }]
      : []),
    ...(expert && item.launch?.embedded !== true
      ? [{ label: t('libraryTools.edit.launchOptions'), detail: t('libraryTools.edit.launchOptionsDetail'), icon: '⚙', run: () => startOptionsMenu(item) }]
      : []),
    {
      label: item.type === 'game' ? t('libraryTools.edit.toApp') : t('libraryTools.edit.toGame'),
      run: async () => {
        // With parental controls on, game or app decides which rules apply: only with the parents' code.
        if (parental.status?.enabled && !(await ensureUnlocked(t('libraryTools.edit.guard')))) return;
        if (parental.status?.enabled && !(await ensureParentGrant(t('libraryTools.edit.guardType'), { action: 'editType', item: item.id }))) return;
        await applyEdit(item, { type: item.type === 'game' ? 'app' : 'game' }, item.type === 'game' ? t('libraryTools.edit.nowApp') : t('libraryTools.edit.nowGame'));
      }
    },
    {
      label: t('libraryTools.edit.hide'),
      detail: t('libraryTools.edit.hideDetail'),
      run: async () => {
        await applyEdit(item, { hidden: true }, t('libraryTools.edit.hidden', { name: item.name }));
        pop();
      }
    }
  ];
  if (item.source === 'custom' && isWebUrl(item.launch?.target)) {
    // Copy-protected streaming (Netflix, Disney+ …) does not play in the built-in browser: those entries open in Edge.
    const edge = item.launch.embedded !== true;
    entries.push({
      label: edge ? t('libraryTools.edit.edgeOff') : t('libraryTools.edit.edgeOn'),
      detail: t('libraryTools.edit.edgeDetail'),
      run: async () => {
        const library = await attempt(() => api.setEntryEdge(item.id, !edge), t('libraryTools.edit.edgeFailed'));
        if (!library) return;
        setLibrary(library);
        toast(edge ? t('libraryTools.edit.edgeNowBuiltin', { name: item.name }) : t('libraryTools.edit.edgeNowEdge', { name: item.name }));
      }
    });
  }
  if (item.source === 'custom') {
    entries.push({
      label: t('libraryTools.edit.remove'),
      run: async () => {
        if (!(await confirmDialog(t('libraryTools.edit.removeConfirm', { name: item.name }), t('libraryTools.edit.removeButton'), { danger: true }))) return;
        if ((await attempt(() => api.removeEntry(item.id), t('libraryTools.edit.removeFailed'))) === undefined) return;
        state.library = state.library.filter((i) => i.id !== item.id);
        toast(t('libraryTools.edit.removed', { name: item.name }));
        pop();
      }
    });
  }
  entries.push({ label: t('libraryTools.edit.cancel'), run: () => {} });
  openOverlay({ title: item.name, entries });
}

detailPage.extraActions.push((item) => ({ label: t('libraryTools.edit.action'), icon: '✎', run: () => editItemMenu(item) }));

/** Lists hidden entries; A shows one again. */
export function hiddenItemsPage() {
  return {
    id: 'hidden',
    name: t('libraryTools.hidden.name'),
    index: 0,
    build(root) {
      this.list = state.library.filter((i) => i.hidden);
      this.index = Math.min(this.index, Math.max(0, this.list.length - 1));
      this.items = this.list.map((i) => el('li', { className: 'check-row' },
        el('span', { className: 'check-box', textContent: '👁' }),
        el('span', { className: 'check-main' }, el('strong', { textContent: i.name }), el('small', { textContent: i.source }))));
      this.scroller = el('ul', { className: 'check-list list-scroll' }, this.items);
      root.append(el('div', { className: 'files' },
        el('div', { className: 'files-head' }, el('h2', { textContent: t('libraryTools.hidden.title') })),
        this.list.length ? this.scroller : el('p', { className: 'empty-note', textContent: t('libraryTools.hidden.empty') })));
      this.update({ instant: true });
    },
    update({ instant = false } = {}) {
      if (!this.items.length) return;
      setFocused(this.items, this.index);
      keepInView(this.scroller, this.items[this.index], { instant });
    },
    async handle(action) {
      switch (action) {
        case 'up': return move(this, -1, this.list.length, true);
        case 'down': return move(this, 1, this.list.length, true);
        case 'a': {
          const item = findItem(this.list[this.index]?.id);
          if (item) await applyEdit(item, { hidden: false }, t('libraryTools.hidden.shown', { name: item.name }));
        }
      }
    },
    hints() {
      return this.list?.length ? [['A', t('libraryTools.hidden.show')], ['B', t('libraryTools.hidden.back')]] : [['B', t('libraryTools.hidden.back')]];
    }
  };
}

/** Watched game folders: add (by browsing or from the suggestions), remove. */
export function gameFoldersPage() {
  return {
    id: 'game-folders',
    name: t('libraryTools.folders.name'),
    index: 0,
    folders: null,
    async load() {
      this.folders = (await attempt(() => api.getGameFolders(), t('libraryTools.folders.loadFailed'))) ?? [];
      render();
    },
    build(root) {
      if (this.folders === null) {
        this.load();
        this.rows = [];
        return;
      }
      this.rows = [
        { icon: '+', label: t('libraryTools.folders.add'), detail: t('libraryTools.folders.addDetail'), run: () => this.add() },
        { icon: '⌕', label: t('libraryTools.folders.discover'), detail: t('libraryTools.folders.discoverDetail'), run: () => this.discover() },
        ...this.folders.map((f) => ({ icon: '📁', label: f, detail: t('libraryTools.folders.watched'), folder: f, run: () => this.remove(f) }))
      ];
      this.index = Math.min(this.index, this.rows.length - 1);
      this.items = this.rows.map((r) => el('li', { className: `check-row update-row${r.folder ? '' : ' highlight'}` },
        el('span', { className: 'check-box' }, iconEl(r.icon)),
        el('span', { className: 'check-main' }, el('strong', { textContent: r.label }), el('small', { textContent: r.detail }))));
      this.scroller = el('ul', { className: 'check-list list-scroll' }, this.items);
      root.append(el('div', { className: 'files' },
        el('div', { className: 'files-head' },
          el('h2', { textContent: t('libraryTools.folders.name') }),
          el('p', { className: 'meta', textContent: t('libraryTools.folders.intro') })),
        this.scroller));
      this.update({ instant: true });
    },
    update({ instant = false } = {}) {
      if (!this.items?.length) return;
      setFocused(this.items, this.index);
      keepInView(this.scroller, this.items[this.index], { instant });
    },
    async add(path) {
      const folder = path ?? (await openFileBrowser({ title: t('libraryTools.folders.pick'), mode: 'folder' }));
      if (!folder) return;
      if (parental.status?.enabled && !(await ensureParentGrant(t('libraryTools.guardGame'), { action: 'addGame' }))) return;
      toast(t('libraryTools.scan.searching'), 2500);
      const folders = await attempt(() => api.addGameFolder(folder), t('libraryTools.addFailed'));
      if (!folders) return;
      this.folders = folders;
      toast(t('libraryTools.folders.watching'));
      render();
    },
    async discover() {
      toast(t('libraryTools.folders.looking'), 2000);
      const found = await attempt(() => api.discoverGameFolders(), t('libraryTools.folders.discoverFailed'));
      if (!found) return;
      if (!found.length) return toast(t('libraryTools.folders.noMore'), 4000);
      const chosen = await chooseDialog(t('libraryTools.folders.found', { count: found.length }), found.map((f) => ({ label: f, value: f })));
      if (chosen) await this.add(chosen);
    },
    async remove(folder) {
      if (!(await confirmDialog(t('libraryTools.folders.unwatchConfirm', { folder }), t('libraryTools.folders.removeButton')))) return;
      const folders = await attempt(() => api.removeGameFolder(folder), t('libraryTools.folders.removeFailed'));
      if (folders) {
        this.folders = folders;
        render();
      }
    },
    async handle(action) {
      switch (action) {
        case 'up': return move(this, -1, this.rows.length, true);
        case 'down': return move(this, 1, this.rows.length, true);
        case 'a':
          sound.select();
          return this.rows[this.index]?.run();
      }
    },
    pointer(node, index, dispatch) {
      this.index = index;
      this.update();
      dispatch('a');
    },
    hints() {
      const row = this.rows?.[this.index];
      return [['A', row?.folder ? t('libraryTools.folders.hint.unwatch') : t('libraryTools.folders.hint.open')], ['B', t('libraryTools.folders.hint.back')]];
    }
  };
}
