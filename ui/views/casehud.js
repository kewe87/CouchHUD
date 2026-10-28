// CaseHUD: the LCDs in fans and pumps, controlled from the couch. CaseHUD is its own program (installed and started from
// here, talked to through its local API by the host); this page chooses and operates: power, scenes, rotation, automation,
// sets, per display brightness/theme/mode, lighting on/off/brightness/effect. Everything that builds or sets up (designs,
// mounting, effect parameters, devices, automation rules, community designs) stays in CaseHUD's window ("Open on the PC").
//
// Every screen of this file is a list of rows, some of them tiles in a grid (displays), and moves like the
// other pages: up/down between rows, left/right inside a row of tiles, A runs, B goes back.

import { iconEl } from '../core/glyph-icons.js';
import { api } from '../core/api.js';
import { el, keepInView, setFocused } from '../core/dom.js';
import { attempt, toast } from '../core/feedback.js';
import { push, render, renderHints } from '../core/nav.js';
import { state } from '../core/state.js';
import { chooseDialog, confirmDialog } from '../components/overlay.js';
import { addQuickRows } from '../components/quick-menu.js';
import { ensureUnlocked } from '../components/passcode.js';
import { sound } from '../sound.js';
import { t } from '../core/i18n.js';
import { settler } from './casehud-settle.js';
import { lightingPage } from './casehud-lighting.js';
import { activeSceneId, conflictNames, sceneLabel } from '../core/casehud-rules.js';

/** Last answer of the host (install state, running state, status, update, progress). */
state.caseHud = null;

const PREVIEW_MS = 3000;
const MODES = ['hud', 'media', 'off'];
const TILE_COLUMNS = 3;

const modeText = (mode) => ({ hud: t('casehud.mode.hud'), media: t('casehud.mode.media'), off: t('casehud.mode.off') })[mode] ?? mode;
const themeText = (theme) => ({ dark: t('casehud.theme.dark'), light: t('casehud.theme.light') })[theme] ?? theme;
const sourceText = (source) => ({ fps: t('casehud.source.fps'), external: t('casehud.source.external') })[source] ?? source;

export const caseHudInstalled = () => !!state.caseHud?.installed;
export const caseHudRunning = () => !!state.caseHud?.running;
/** Running, but its API is switched off in CaseHUD (Settings › Interfaces; CaseHUD ships with it off). */
export const caseHudApiOff = (s = state.caseHud) => s?.state === 'apiOff';
export const caseHudUpdateAvailable = () => !!state.caseHud?.update?.available;

/** Called once at the start: keeps `state.caseHud` current for the page, the settings badge and the guide menu. */
export async function startCaseHud() {
  if (!api.caseHudStatus) return;
  api.onCaseHud?.((s) => {
    const changed = JSON.stringify(s) !== JSON.stringify(state.caseHud);
    state.caseHud = s;
    if (changed) refreshOpenPage();
  });
  await reload();
}

// Guide menu: switch the displays off or on, next scene (only while CaseHUD runs); with its API off the hint instead.
addQuickRows(() => {
  const s = state.caseHud;
  if (caseHudApiOff(s)) {
    return [{
      icon: '!',
      label: t('casehud.api.quick'),
      detail: t('casehud.api.hint'),
      run: () => setCaseHudApi(true)
    }];
  }
  if (!s?.running || !s.status) return [];
  const on = s.status.power;
  return [
    {
      icon: '▣',
      label: on ? t('casehud.quick.off') : t('casehud.quick.on'),
      detail: t('casehud.title'),
      keepOpen: true,
      run: async () => {
        if ((await attempt(() => api.caseHudPower(!on), t('casehud.failed'))) !== undefined) await reload();
      }
    },
    {
      icon: '↻',
      label: t('casehud.quick.next'),
      detail: activeSceneId(s.status, state.scenes) ? sceneName(activeSceneId(s.status, state.scenes)) : t('casehud.title'),
      keepOpen: true,
      run: async () => {
        if ((await attempt(() => api.caseHudNextScene(), t('casehud.failed'))) !== undefined) await reload();
      }
    }
  ];
});

export const reloadCaseHud = () => reload();

async function reload() {
  state.caseHud = (await attempt(() => api.caseHudStatus())) ?? state.caseHud;
  // The status names the scene by id; the list has the names.
  if (state.caseHud?.running) state.scenes = (await api.caseHudScenes().catch(() => null)) ?? state.scenes;
  refreshOpenPage();
}

let openPages = [];
function refreshOpenPage() {
  openPages = openPages.filter((p) => p.root?.isConnected);
  for (const p of openPages) p.refresh?.();
}

// ---------------------------------------------------------------------------
// Previews: PNG as data: URL from the host, refreshed gently and only while the page is on screen.

const previews = new Map();
const PREVIEW_CACHE = 60;
const PLACEHOLDER = 'data:image/svg+xml,' + encodeURIComponent('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 40 40"><rect width="40" height="40" fill="#222"/><path d="M12 28l6-8 4 5 3-4 5 7z" fill="#555"/></svg>');

function remember(key, data) {
  previews.delete(key);
  previews.set(key, data);
  // The oldest go first (a Map keeps the order of insertion).
  while (previews.size > PREVIEW_CACHE) previews.delete(previews.keys().next().value);
}

async function loadPreview(img, spec) {
  const key = `${spec.id}:${spec.size ?? ''}`;
  if (previews.has(key)) img.src = previews.get(key);
  if (spec.live || !previews.has(key)) {
    const data = await api.caseHudPreview(spec.id, spec.size).catch(() => null);
    if (data) {
      remember(key, data);
      if (img.isConnected) img.src = data;
    } else if (!previews.has(key) && img.isConnected) img.src = PLACEHOLDER;
  }
}

// ---------------------------------------------------------------------------
// Generic list page

/**
 * A page of rows. `rows()` gives [{ section } | { items: [item], tiles?: columns }]; an item is
 * { icon, label, detail, preview?, run?, adjust?(delta, page), hint?, highlight?, card?, pill? }.
 */
export function listPage({ id, title, rows, hints, extraKeys, onEnter, onClose, head }) {
  return {
    id,
    name: title,
    r: 0,
    c: 0,
    build(root) {
      this.root = root;
      if (!openPages.includes(this)) openPages.push(this);
      onEnter?.call(this);
      this.layout = [];
      this.nodes = new Map();
      this.live = [];
      const content = [];
      let tiles = null;
      for (const row of rows.call(this)) {
        if (row.section) {
          content.push(el('div', { className: 'update-section', textContent: row.section }));
          continue;
        }
        if (row.tiles) {
          const cells = row.items.map((item) => {
            const img = el('img', { className: 'ch-art', alt: '', draggable: false });
            if (item.preview) {
              loadPreview(img, item.preview);
              if (item.preview.live) this.live.push([img, item.preview]);
            }
            const node = el('div', { className: `ch-tile${item.dim ? ' dim' : ''}` },
              el('div', { className: 'ch-frame' }, img, item.badge ? el('span', { className: 'ch-badge', textContent: item.badge }) : null),
              el('strong', { className: 'ch-name', textContent: item.label }),
              item.detail ? el('small', { className: 'ch-detail', textContent: item.detail }) : null);
            this.nodes.set(item, node);
            return node;
          });
          tiles = el('div', { className: 'ch-grid' }, cells);
          tiles.style.setProperty('--cols', String(row.tiles));
          content.push(tiles);
          this.layout.push(row.items);
          continue;
        }
        const item = row.items[0];
        const node = el('div', { className: `check-row update-row${item.highlight ? ' highlight' : ''}${item.card ? ' ch-card' : ''}${item.run || item.adjust ? '' : ' static'}` },
          el('span', { className: 'check-box' }, iconEl(item.icon ?? '·')),
          el('span', { className: 'check-main' }, el('strong', { textContent: item.label }), item.detail ? el('small', { textContent: item.detail }) : null),
          item.pill ? el('span', { className: 'pill', textContent: item.pill }) : null);
        this.nodes.set(item, node);
        content.push(node);
        this.layout.push([item]);
      }
      // First time here: start on something A can do, not on an explanation.
      if (!this.started) {
        this.started = true;
        const first = this.layout.findIndex((cells) => cells.some((i) => i.run || i.adjust));
        if (first > 0) this.r = first;
      }
      this.r = Math.min(this.r, this.layout.length - 1);
      this.c = Math.min(this.c, (this.layout[this.r]?.length ?? 1) - 1);
      this.scroller = el('div', { className: 'ch-scroll list-scroll' }, content);
      root.append(el('div', { className: 'files casehud' }, el('div', { className: 'files-head' }, el('h2', { textContent: title }), head?.call(this)), this.scroller));
      this.update({ instant: true });
      this.watchPreviews();
    },
    /** Live frames: one request at a time, every few seconds, only while this page is the one on screen. */
    watchPreviews() {
      clearInterval(this.timer);
      if (!this.live.length) return;
      let busy = false;
      this.timer = setInterval(async () => {
        if (!this.root?.isConnected || state.stack.at(-1) !== this) return clearInterval(this.timer);
        if (busy || document.hidden || state.overlay || state.panel) return;
        busy = true;
        for (const [img, spec] of this.live) {
          if (!img.isConnected) break;
          await loadPreview(img, spec);
        }
        busy = false;
      }, PREVIEW_MS);
    },
    refresh() {
      if (state.stack.at(-1) === this) render();
    },
    /** Leaving the page (B, another tab): values still settling go out now instead of being lost. */
    onClose() {
      onClose?.call(this);
    },
    /** A step of a settling value: only the texts of its row change (no new build, no new preview requests). */
    patchRow(item, { detail, pill } = {}) {
      const node = this.nodes?.get(item);
      if (!node) return;
      const small = node.querySelector('.check-main small');
      if (detail !== undefined && small) small.textContent = detail;
      const p = node.querySelector('.pill');
      if (pill !== undefined && p) p.textContent = pill;
    },
    get item() {
      return this.layout?.[this.r]?.[this.c];
    },
    update({ instant = false } = {}) {
      const all = this.layout.flat();
      const current = this.item;
      for (const item of all) this.nodes.get(item)?.classList.toggle('focused', item === current);
      keepInView(this.scroller, this.nodes.get(current), { instant });
    },
    pointer(node, _i, dispatch) {
      for (const [r, cells] of this.layout.entries()) {
        const c = cells.findIndex((item) => this.nodes.get(item) === node || this.nodes.get(item)?.contains(node));
        if (c >= 0) {
          this.r = r;
          this.c = c;
          this.update();
          dispatch('a');
          return;
        }
      }
    },
    async handle(action) {
      const step = (dr, dc) => {
        const nr = this.r + dr;
        const nc = this.c + dc;
        if (dr) {
          if (nr < 0 || nr >= this.layout.length) return sound.bump();
          // A tile row remembers the column; a plain row has just one cell.
          this.r = nr;
          this.c = Math.min(this.c, this.layout[nr].length - 1);
        } else {
          if (nc < 0 || nc >= this.layout[this.r].length) return sound.bump();
          this.c = nc;
        }
        sound.move();
        this.update();
        renderHints();
      };
      const item = this.item;
      switch (action) {
        case 'up': return step(-1, 0);
        case 'down': return step(1, 0);
        case 'left':
        case 'right': {
          const delta = action === 'left' ? -1 : 1;
          if (item?.adjust) return item.adjust(delta, this);
          // Tile grids with more than one line: left/right wrap to the neighbour row only through the columns.
          return step(0, delta);
        }
        case 'a':
          if (!item?.run) return sound.bump();
          sound.select();
          return item.run();
        default:
          return extraKeys?.call(this, action, item);
      }
    },
    hints() {
      const item = this.item;
      const a = item?.run ? [['A', item.hint ?? t('casehud.hint.open')]] : [];
      const lr = item?.adjust ? [['◀▶', item.adjustHint ?? t('casehud.hint.change')]] : [];
      return [...a, ...lr, ...(hints?.call(this, item) ?? []), ['B', t('casehud.hint.back')]];
    }
  };
}

/** Asks for the couch-lock code first when a guest is on the couch; the host refuses otherwise. */
export const unlocked = () => ensureUnlocked(t('casehud.unlockReason'));

/** One call to the host: failures become a toast, then the new status is read. */
export async function act(call, { guard = true } = {}) {
  if (guard && !(await unlocked())) return undefined;
  const result = await attempt(call, t('casehud.failed'));
  await reload();
  return result;
}

const sceneName = (id) => sceneLabel(id, state.scenes);

// ---------------------------------------------------------------------------
// Main page

/** snake_case code of the API -> text; codes this version does not know are shown readable instead of failing. */
const errorText = (code) => (!code ? '' : ({
  device_error: t('casehud.err.device_error'),
  frame_error: t('casehud.err.frame_error'),
  content_error: t('casehud.err.content_error'),
  sensor_service_down: t('casehud.err.sensor_service_down')
})[code] ?? code.replace(/_/g, ' '));

function installRows(s) {
  const rows = [{ section: t('casehud.title') }];
  rows.push({ items: [{ icon: 'ℹ', label: t('casehud.about'), detail: t('casehud.aboutDetail') }] });
  if (s?.busy) rows.push({ items: [{ icon: '⟳', label: s.busy, detail: t('casehud.installWait') }] });
  else {
    rows.push({
      items: [{
        icon: '⬇',
        label: t('casehud.install'),
        detail: t('casehud.installDetail'),
        highlight: true,
        hint: t('casehud.install'),
        run: installNow
      }]
    });
    // Full integration: the user decides here whether CouchHUD switches CaseHUD's API on after installing (CaseHUD's own default is off).
    if (s?.options?.full) rows.push({ items: [installApiChoice(s)] });
    rows.push({ items: [{ icon: '!', label: t('casehud.installNoteTitle'), detail: t('casehud.installNote') }] });
    rows.push(...unavailableRows(s));
  }
  if (s?.error) rows.push({ items: [{ icon: '!', label: t('casehud.error'), detail: s.error }] });
  return rows;
}

/**
 * Install and update: the host looks at GitHub first and answers with the concrete version and a one-time token; the dialog
 * names that version and everything that will happen, and only the token of exactly that release lets the host install.
 */
async function setup(kind) {
  if (state.session) return toast(t('casehud.notDuringGame'));
  if (state.caseHud?.busy) return toast(t('casehud.installWait'));
  if (!(await unlocked())) return;
  toast(t('casehud.checking'));
  const prep = await attempt(() => api.caseHudPrepare(), t('casehud.failed'));
  if (!prep) return;
  if (!prep.token) return toast(prep.error ?? t('casehud.upToDate'), 4500);
  const o = state.caseHud?.options ?? {};
  // The API switch only follows a first installation in full integration, and only when chosen (CaseHudInstaller).
  const withApi = kind === 'install' && !prep.installed && o.full && o.api;
  const effects = [t('casehud.effect.service'), t('casehud.effect.driver'), o.autostart ? t('casehud.effect.autostart') : null, o.fps ? t('casehud.effect.group') : null,
    withApi ? t('casehud.effect.api') : null]
    .filter(Boolean).join(', ');
  const text = (kind === 'update' ? t('casehud.confirmUpdate', { version: prep.version, effects }) : t('casehud.confirmInstall', { version: prep.version, effects }))
    + (prep.notes ? ` ${t('casehud.changes', { notes: prep.notes.split('\n')[0] })}` : '')
    + (prep.unsigned ? ` ${t('casehud.unsignedNote')}` : ` ${t('casehud.signedNote')}`);
  if (!(await confirmDialog(text, kind === 'update' ? t('casehud.update') : t('casehud.install'), { danger: !!prep.unsigned }))) return;
  await attempt(() => api.caseHudInstall(prep.token), t('casehud.installFailed'));
  await reload();
}

const installNow = () => setup('install');
const updateNow = () => setup('update');

const updateRow = (s) => s.update?.available ? [{
  icon: '⬇',
  label: t('casehud.updateAvailable', { version: s.update.version }),
  detail: s.busy ?? (s.update.installable ? (s.update.notes?.split('\n')[0] || t('casehud.updateDetail')) : t('casehud.unverifiable')),
  highlight: s.update.installable,
  pill: t('casehud.update'),
  hint: t('casehud.update'),
  run: s.update.installable ? updateNow : () => toast(t('casehud.unverifiable'))
}] : [];

/** CaseHUD's own window on the PC: designs, effects, devices, automation rules, the first-start and lighting notices. */
export async function openCaseHudGui() {
  if (!(await unlocked())) return;
  if ((await attempt(() => api.caseHudOpenGui(), t('casehud.failed'))) !== undefined) toast(t('casehud.guiOpened'));
}

const guiRow = () => ({
  icon: '✎',
  label: t('casehud.openGui'),
  detail: t('casehud.openGuiDetail'),
  hint: t('casehud.openGui'),
  run: openCaseHudGui
});

/** Where the editors used to be: one line saying that building happens in CaseHUD's window, and A opens it. */
export const pcHintRow = () => ({
  items: [{
    icon: '✎',
    label: t('casehud.pcHint'),
    hint: t('casehud.pcHintOpen'),
    run: openCaseHudGui
  }]
});

/** Programs that may hold CaseHUD's devices (e.g. another RGB tool): a short warning; closing them is done in CaseHUD's window. */
const conflictRow = (st) => {
  const names = conflictNames(st?.conflicts);
  if (!names.length) return null;
  return {
    items: [{
      icon: '!',
      label: t('casehud.conflicts', { names: names.join(', ') }),
      detail: t('casehud.conflictsDetail'),
      highlight: true,
      hint: t('casehud.pcHintOpen'),
      run: openCaseHudGui
    }]
  };
};

// ---------------------------------------------------------------------------
// CaseHUD's API switch (Settings › Interfaces in CaseHUD, off by default)

/**
 * Switches CaseHUD's API on or off for this user: the host runs CaseHUD.exe --set-api=on|off from the verified installation. It is a
 * setting of CaseHUD, so it asks for the couch-lock code and says what it does first.
 */
export async function setCaseHudApi(on) {
  if (!(await unlocked())) return false;
  if (!(await confirmDialog(on ? t('casehud.api.confirmOn') : t('casehud.api.confirmOff'), on ? t('casehud.api.turnOn') : t('casehud.api.turnOff'), { danger: !on }))) return false;
  const r = await attempt(() => api.caseHudSetApi(on), t('casehud.api.failed'));
  if (r === undefined) return false;
  if (r) state.caseHud = r;
  toast(on ? t('casehud.api.turnedOn') : t('casehud.api.turnedOff'));
  await reload();
  return true;
}

/** The hint row of a CaseHUD whose API is off: where to switch it on, and A does it from here. Also used by the lighting page. */
export const apiOffRow = () => ({
  items: [{
    icon: '!',
    label: t('casehud.api.offTitle'),
    detail: t('casehud.api.hint'),
    card: true,
    highlight: true,
    hint: t('casehud.api.turnOn'),
    run: () => setCaseHudApi(true)
  }]
});

/** Running with the API off: nothing to control yet. The hint, the switch, and CaseHUD's own window (where the switch also is). */
function apiOffRows(s) {
  const rows = [{ section: t('casehud.title') }, apiOffRow()];
  rows.push({ items: [{ icon: 'ℹ', label: t('casehud.api.offWhy'), detail: t('casehud.api.offDetail') }] });
  rows.push({ items: [{ ...guiRow(), detail: t('casehud.api.openGuiDetail') }] });
  for (const item of updateRow(s)) rows.push({ items: [item] });
  if (s.error) rows.push({ items: [{ icon: '!', label: t('casehud.error'), detail: s.error }] });
  return rows;
}

/** Install page, full integration: switch the API on after installing? A setting (needs the code), named in the confirmation. */
function installApiChoice(s) {
  const on = !!s.options?.api;
  return {
    icon: '⇄',
    label: t('casehud.api.installChoice'),
    detail: t('casehud.api.installChoiceDetail'),
    pill: on ? t('casehud.on') : t('casehud.off'),
    hint: on ? t('casehud.off') : t('casehud.on'),
    run: async () => {
      if (!(await unlocked())) return;
      if ((await attempt(() => api.setSetting('caseHudInstallApi', !on), t('casehud.failed'))) !== undefined) await reload();
    }
  };
}

/** Running page: the API is on; switching it off again is offered here (CouchHUD then loses control until it is on again). */
const apiOnRow = () => ({
  icon: '⇄',
  label: t('casehud.api.row'),
  detail: t('casehud.api.rowOn'),
  pill: t('casehud.on'),
  hint: t('casehud.api.turnOff'),
  run: () => setCaseHudApi(false)
});

/**
 * First start of CaseHUD 1.0: until the notice in its window is confirmed it talks to no device. Only the user can confirm it,
 * there (never through the API); the card opens the window. `disclaimerAccepted` missing (older builds) is not "false".
 */
export const needsDisclaimer = (st) => st?.disclaimerAccepted === false;

export const disclaimerRow = () => ({
  items: [{
    icon: '!',
    label: t('casehud.disclaimer.title'),
    detail: t('casehud.disclaimer.detail'),
    card: true,
    highlight: true,
    hint: t('casehud.openWindow'),
    run: openCaseHudGui
  }]
});

/** The release files are not public (yet): say so, and let the user ask again by hand (the host does not ask on its own for 12 h). */
const unavailableRows = (s) => s?.update?.unavailable ? [{
  items: [{
    icon: '!',
    label: t('casehud.downloadsUnavailable'),
    detail: s.update.error || t('casehud.recheckDetail'),
    hint: t('casehud.recheck'),
    pill: t('casehud.recheck'),
    run: async () => {
      const r = await attempt(() => api.caseHudCheckUpdate(true), undefined, { quietLocked: true });
      if (r) {
        state.caseHud = r;
        refreshOpenPage();
      }
    }
  }]
}] : [];

/** A CaseHUD build that does not know the start flags has no API: nothing to control, only the way to the update. */
function needsUpdateRows(s) {
  const rows = [{ section: t('casehud.title') }];
  rows.push({ items: [{ icon: '!', label: t('casehud.needsUpdate'), detail: t('casehud.needsUpdateDetail', { version: s.version || '?' }) }] });
  if (s.busy) rows.push({ items: [{ icon: '⟳', label: s.busy }] });
  else {
    rows.push({
      items: [{
        icon: '⬇',
        label: t('casehud.update'),
        detail: t('casehud.updateDetail'),
        highlight: true,
        hint: t('casehud.update'),
        run: updateNow
      }]
    });
    rows.push(...unavailableRows(s));
  }
  if (s.error) rows.push({ items: [{ icon: '!', label: t('casehud.error'), detail: s.error }] });
  return rows;
}

function stoppedRows(s) {
  const rows = [{ section: t('casehud.title') }];
  rows.push({ items: [{ icon: '·', label: t('casehud.stopped'), detail: s.version ? t('casehud.stoppedDetail', { version: s.version }) : t('casehud.stoppedDetail2') }] });
  if (s.busy) rows.push({ items: [{ icon: '⟳', label: s.busy }] });
  else {
    rows.push({
      items: [{
        icon: '▶',
        label: t('casehud.start'),
        detail: t('casehud.startDetail'),
        highlight: true,
        hint: t('casehud.start'),
        run: async () => {
          if (!(await unlocked())) return;
          await attempt(() => api.caseHudStart(), t('casehud.startFailed'));
          await reload();
        }
      }]
    });
  }
  for (const item of updateRow(s)) rows.push({ items: [item] });
  rows.push({ items: [guiRow()] });
  if (s.error) rows.push({ items: [{ icon: '!', label: t('casehud.error'), detail: s.error }] });
  return rows;
}

function panelItem(p) {
  const problem = p.error;
  return {
    id: p.id,
    label: p.name,
    detail: problem ? errorText(problem) : `${modeText(p.mode)} · ${p.brightness} %`,
    dim: !!problem,
    preview: { id: p.id, size: 240, live: true },
    run: () => push(panelPage(p.id))
  };
}

function runningRows(s) {
  const st = s.status;
  const rows = [];
  if (needsDisclaimer(st)) return disclaimerRows(s);
  const conflicts = conflictRow(st);
  if (conflicts) rows.push(conflicts);
  rows.push({ section: t('casehud.displays') });
  if (st.panels.length) rows.push({ tiles: TILE_COLUMNS, items: st.panels.map(panelItem) });
  else rows.push({ items: [{ icon: '·', label: t('casehud.noDisplays'), detail: t('casehud.noDisplaysDetail') }] });

  for (const item of updateRow(s)) rows.push({ items: [item] });

  rows.push({ section: t('casehud.global') });
  rows.push({
    items: [{
      icon: '▣',
      label: st.power ? t('casehud.power.turnOff') : t('casehud.power.turnOn'),
      detail: st.power ? t('casehud.power.isOn') : t('casehud.power.isOff'),
      hint: st.power ? t('casehud.power.turnOff') : t('casehud.power.turnOn'),
      run: () => act(() => api.caseHudPower(!st.power), { guard: false })
    }]
  });
  rows.push({
    items: [{
      icon: '🎬',
      label: t('casehud.scene'),
      detail: activeSceneId(st, state.scenes) ? sceneName(activeSceneId(st, state.scenes)) : t('casehud.sceneNone'),
      hint: t('casehud.sceneChoose'),
      run: chooseScene
    }]
  });
  rows.push({
    items: [{
      icon: '↻',
      label: t('casehud.rotation'),
      detail: st.rotation?.enabled ? t('casehud.on') : t('casehud.off'),
      pill: st.rotation?.enabled ? t('casehud.on') : t('casehud.off'),
      hint: st.rotation?.enabled ? t('casehud.rotationStop') : t('casehud.rotationStart'),
      run: () => act(() => api.caseHudRotation(!st.rotation?.enabled))
    }]
  });
  // Automation (scenes by rule, e.g. while a game runs): only on and off here; the rules are edited in CaseHUD's window.
  if (st.automation) {
    const on = !!st.automation.enabled;
    rows.push({
      items: [{
        icon: '⚙',
        label: t('casehud.automation'),
        detail: on ? (st.automation.activeRule >= 0 ? t('casehud.automationHolding') : t('casehud.automationOn')) : t('casehud.automationOff'),
        pill: on ? t('casehud.on') : t('casehud.off'),
        hint: on ? t('casehud.automationTurnOff') : t('casehud.automationTurnOn'),
        run: () => act(() => api.caseHudAutomation(!on))
      }]
    });
  }
  rows.push({
    items: [{
      icon: '⏭',
      label: t('casehud.nextScene'),
      hint: t('casehud.nextScene'),
      run: () => act(() => api.caseHudNextScene())
    }]
  });
  rows.push({
    items: [{
      icon: '🎨',
      label: t('casehud.setAll'),
      detail: t('casehud.setAllDetail'),
      hint: t('casehud.setAll'),
      run: chooseSetForAll
    }]
  });
  // Lighting: only builds whose status carries `lighting` have the endpoint.
  if (st.lighting) {
    rows.push({
      items: [{
        icon: '💡',
        label: t('casehud.light.title'),
        detail: st.lighting.enabled ? t('casehud.on') : t('casehud.off'),
        pill: st.lighting.enabled ? t('casehud.on') : t('casehud.off'),
        hint: t('casehud.hint.open'),
        run: () => push(lightingPage())
      }]
    });
  }
  const awtrixOk = st.awtrix?.devices?.filter((d) => d.ok).length ?? 0;
  rows.push({
    items: [{
      icon: '⌚',
      label: t('casehud.awtrix'),
      detail: st.awtrix?.enabled ? t('casehud.awtrixOn', { ok: awtrixOk, count: st.awtrix.devices?.length ?? 0 }) : t('casehud.awtrixOff'),
      pill: st.awtrix?.enabled ? t('casehud.on') : t('casehud.off'),
      hint: st.awtrix?.enabled ? t('casehud.awtrixTurnOff') : t('casehud.awtrixTurnOn'),
      run: () => act(() => api.caseHudAwtrix(!st.awtrix?.enabled))
    }]
  });
  rows.push({
    items: [{
      icon: '◎',
      label: t('casehud.identifyAll'),
      detail: t('casehud.identifyDetail'),
      hint: t('casehud.identify'),
      run: async () => { await attempt(() => api.caseHudIdentify(null), t('casehud.failed')); }
    }]
  });
  // Designs, effects, devices and community designs: CaseHUD's window (the row opens it).
  rows.push(pcHintRow());
  rows.push({ items: [apiOnRow()] });

  rows.push({ section: t('casehud.statusSection') });
  rows.push({
    items: [{
      icon: st.sensors?.ok ? '✓' : '!',
      label: st.sensors?.ok ? t('casehud.sensorsOk') : t('casehud.sensorsBad'),
      detail: st.sensors?.ok ? null : errorText(st.sensors?.error) || st.sensors?.error
    }]
  });
  rows.push({
    items: [{
      icon: st.gaming?.active ? '🎮' : '·',
      label: st.gaming?.active ? t('casehud.gamingOn') : t('casehud.gamingOff'),
      detail: st.gaming?.active ? [st.gaming.game, st.gaming.source && sourceText(st.gaming.source)].filter(Boolean).join(' · ') : null
    }]
  });
  rows.push({ items: [{ icon: 'ℹ', label: t('casehud.version', { version: st.version }) }] });
  return rows;
}

/** Running, but the first-start notice is not confirmed yet: devices and sensors are not there yet, which is no error. */
function disclaimerRows(s) {
  const st = s.status;
  const rows = [{ section: t('casehud.title') }, disclaimerRow()];
  rows.push({ section: t('casehud.displays') });
  rows.push({ items: [{ icon: '·', label: t('casehud.disclaimer.devices'), detail: t('casehud.disclaimer.devicesDetail') }] });
  for (const item of updateRow(s)) rows.push({ items: [item] });
  rows.push({ section: t('casehud.statusSection') });
  rows.push({ items: [{ icon: '·', label: t('casehud.disclaimer.sensors') }] });
  rows.push({ items: [{ icon: 'ℹ', label: t('casehud.version', { version: st.version }) }] });
  return rows;
}

async function chooseScene() {
  const scenes = await attempt(() => api.caseHudScenes(), t('casehud.failed'));
  if (!scenes?.length) return toast(t('casehud.noScenes'));
  state.scenes = scenes;
  const active = activeSceneId(state.caseHud?.status, scenes);
  const id = await chooseDialog(t('casehud.scene'), scenes.map((s) => ({ label: s.name, icon: s.active ? '✓' : '', value: s.id })), active);
  if (id === undefined) return;
  await act(() => api.caseHudActivateScene(id), { guard: false });
}

async function chooseSetForAll() {
  if (!(await unlocked())) return;
  const sets = await attempt(() => api.caseHudSets(), t('casehud.failed'));
  if (!sets?.length) return;
  const value = await chooseDialog(t('casehud.setAll'), sets.map((s) => ({ label: s.name, detail: themeText(s.theme), value: `${s.set}|${s.theme}` })));
  if (value === undefined) return;
  const [set, theme] = value.split('|');
  if ((await act(() => api.caseHudApplySet(set, theme), { guard: false })) !== undefined) toast(t('casehud.setApplied'));
}

export function caseHudPage() {
  return listPage({
    id: 'casehud',
    title: t('casehud.title'),
    onEnter() {
      // A quiet look for a newer CaseHUD (cached by the host).
      if (!this.checked) {
        this.checked = true;
        // A quiet look: a locked couch is no error here.
        attempt(() => api.caseHudCheckUpdate(), undefined, { quietLocked: true }).then((s) => {
          if (s) {
            state.caseHud = s;
            refreshOpenPage();
          }
        });
        reload();
      }
    },
    head() {
      const busy = state.caseHud?.busy;
      return busy ? el('p', { className: 'update-busy' }, el('span', { className: 'spinner' }), el('span', { textContent: busy })) : null;
    },
    rows() {
      const s = state.caseHud;
      if (!s) return [{ section: t('casehud.title') }, { items: [{ icon: '⟳', label: t('casehud.loading') }] }];
      // While something is installing or starting the page shows nothing but that.
      if (s.busy) return [{ section: t('casehud.title') }, { items: [{ icon: '⟳', label: s.busy, detail: t('casehud.installWait') }] }];
      if (!s.installed) return installRows(s);
      if (s.needsUpdate) return needsUpdateRows(s);
      if (caseHudApiOff(s)) return apiOffRows(s);
      if (!s.running || !s.status) return stoppedRows(s);
      return runningRows(s);
    },
    extraKeys(action, item) {
      // X on a display: find it (its screen flashes).
      if (action === 'x' && item?.id) return attempt(() => api.caseHudIdentify(item.id), t('casehud.failed'));
    },
    hints(item) {
      return item?.id ? [['X', t('casehud.identify')]] : [];
    }
  });
}

// ---------------------------------------------------------------------------
// One display

const findPanel = (id) => state.caseHud?.status?.panels?.find((p) => p.id === id);

function cycle(list, value, delta) {
  const i = Math.max(0, list.indexOf(value));
  return list[(i + delta + list.length) % list.length];
}

function panelPage(panelId) {
  const patch = async (change) => {
    if (!(await unlocked())) return;
    await act(() => api.caseHudPatchPanel(panelId, change), { guard: false });
  };
  // Brightness ends in the display's flash: steps only change the row, the value goes out when the input settles or on A.
  const settle = settler();
  const step = async (page, item, key, reported, next, show) => {
    const current = settle.value(key, reported);
    const value = next(current);
    if (value === current) return sound.bump();
    // The code is asked once, at the first step, not when the value goes out.
    if (!settle.has(key) && !(await unlocked())) return;
    sound.move();
    settle.set(key, value, (v) => act(() => api.caseHudPatchPanel(panelId, { [key]: v }), { guard: false }));
    page.patchRow(item, show(value));
  };
  return listPage({
    id: 'casehud-panel',
    title: findPanel(panelId)?.name ?? t('casehud.title'),
    onClose: () => settle.flushAll(),
    head() {
      const p = findPanel(panelId);
      return p ? el('p', { className: 'meta', textContent: `${p.model} · ${p.w}×${p.h}` }) : null;
    },
    rows() {
      const p = findPanel(panelId);
      if (!p) return [{ items: [{ icon: '!', label: t('casehud.panelGone') }] }];
      const problem = p.error;
      return [
        { section: t('casehud.display') },
        { tiles: 1, items: [{ id: p.id, label: p.name, detail: problem ? errorText(problem) : p.model, preview: { id: p.id, size: 320, live: true }, dim: !!problem }] },
        {
          items: [{
            icon: '☀',
            label: t('casehud.brightness'),
            detail: `${settle.value('brightness', p.brightness)} %`,
            pill: bar(settle.value('brightness', p.brightness)),
            adjustHint: t('casehud.hint.change'),
            adjust(d, page) {
              return step(page, this, 'brightness', p.brightness, (v) => Math.max(0, Math.min(100, v + d * 10)), (v) => ({ detail: `${v} %`, pill: bar(v) }));
            },
            run: () => settle.has('brightness') ? settle.flush('brightness') : patch({ brightness: p.brightness >= 100 ? 10 : Math.min(100, p.brightness + 30) }),
            hint: t('casehud.brighter')
          }]
        },
        {
          items: [{
            icon: '◐',
            label: t('casehud.theme'),
            detail: themeText(p.theme),
            adjust: (d) => patch({ theme: cycle(['dark', 'light'], p.theme, d) }),
            run: () => patch({ theme: cycle(['dark', 'light'], p.theme, 1) }),
            hint: t('casehud.themeNext')
          }]
        },
        {
          items: [{
            icon: '▤',
            label: t('casehud.mode'),
            detail: modeText(p.mode),
            adjust: (d) => patch({ mode: cycle(MODES, p.mode, d) }),
            run: () => patch({ mode: cycle(MODES, p.mode, 1) }),
            hint: t('casehud.modeNext')
          }]
        },
        {
          items: [{
            icon: '◎',
            label: t('casehud.identify'),
            detail: t('casehud.identifyOne'),
            hint: t('casehud.identify'),
            run: () => attempt(() => api.caseHudIdentify(p.id), t('casehud.failed'))
          }]
        },
        // Design and mounting of this display: CaseHUD's window.
        pcHintRow()
      ];
    },
    hints() {
      return [['X', t('casehud.identify')]];
    },
    extraKeys(action) {
      if (action === 'x') return attempt(() => api.caseHudIdentify(panelId), t('casehud.failed'));
    }
  });
}

export const bar = (value) => '▮'.repeat(Math.round(value / 20)) + '▯'.repeat(5 - Math.round(value / 20));
