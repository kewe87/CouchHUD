// A page a plugin describes as data (rows, sections, kinds): CouchHUD draws it in the active skin, with controller, mouse and keyboard.
// A plugin never ships HTML or script: every text goes in as text, every picture is a PNG data URI the host already checked. The host
// decides again on every press (lock, parents' code, the values a row offers); this page only draws and sends what the user did.

import { iconEl } from '../core/glyph-icons.js';
import { api } from '../core/api.js';
import { el, keepInView } from '../core/dom.js';
import { attempt, toast } from '../core/feedback.js';
import { currentView, push, render, renderHints } from '../core/nav.js';
import { chooseDialog } from '../components/overlay.js';
import { settler } from '../core/settle.js';
import { sound } from '../sound.js';
import { withParentGrant } from '../core/parent-retry.js';
import { t } from '../core/i18n.js';
import { pageSync, currentOption, formatStepper, groupRows, layoutLines, parseStepper, pressKind, safeImage, stepValue, tileColumns } from '../core/plugin-rules.js';

// Pages that are open, so a push from the plugin ("this page changed") redraws the one on screen.
const open = new Set();
api.onPluginPageChanged?.(({ plugin, page }) => {
  for (const p of open) if (p.pluginId === plugin && p.pageId === page) p.reload();
});

/**
 * `asTab`: the page is a main tab (plugin-tabs.js): it reads itself again when the tab is entered, shows an unavailable note instead of a toast
 * when the plugin does not answer, and tells the tab bar to ask the host again (a tab whose plugin is gone disappears).
 */
export function declarativePage(plugin, pageInfo, { asTab = false } = {}) {
  /** Latest answer of the host: { title, description, rows, sections }. */
  let data = null;
  /** One settler per stepper row: the value goes out when the input has settled. */
  const settlers = new Map();
  const settlerOf = (row) => {
    if (!settlers.has(row.id)) settlers.set(row.id, settler(row.settleMs ?? 400));
    return settlers.get(row.id);
  };
  const anyWaiting = () => [...settlers.entries()].some(([id, s]) => s.waiting(id));
  const sync = pageSync({ waiting: anyWaiting, load: () => page.load(), render: () => currentView() === page && render() });

  /** What a stepper row shows: the number still settling, or what the plugin reported. */
  const stepperState = (row) => {
    const parts = parseStepper(row.value);
    const shown = settlerOf(row).value(row.id, parts?.number ?? row.min);
    return { parts, number: shown, text: formatStepper(parts, shown) };
  };

  function control(row, refs) {
    switch (row.kind) {
      case 'toggle':
        return el('span', { className: `pill ${row.on ? 'on' : 'off'}`, textContent: row.value ?? (row.on ? t('plugins.value.on') : t('plugins.value.off')) });
      case 'stepper': {
        const value = el('span', { className: 'pp-stepper-value', textContent: stepperState(row).text });
        refs.value = value;
        return el('span', { className: 'pill pp-stepper' }, el('span', { className: 'pp-arrow', textContent: '◀' }), value, el('span', { className: 'pp-arrow', textContent: '▶' }));
      }
      case 'choice':
        return el('span', { className: 'pill off pp-choice', textContent: `${row.value ?? '–'} ▾` });
      case 'link':
        return el('span', { className: 'pp-link', textContent: '›' });
      default:
        return row.value != null ? el('span', { className: `pill ${row.on ? 'on' : 'off'}`, textContent: row.value }) : null;
    }
  }

  function listNode(row, nodes) {
    const refs = {};
    const image = safeImage(row.image);
    const node = el('div', { className: `check-row update-row pp-row pp-${row.kind ?? 'action'}` },
      el('span', { className: 'check-box' }, image ? el('img', { className: 'pp-icon', src: image, alt: '', draggable: false }) : iconEl(row.icon ?? '·')),
      el('span', { className: 'check-main' }, el('strong', { textContent: row.label }), row.detail ? el('small', { textContent: row.detail }) : null),
      row.badge ? el('span', { className: 'plug-badge', textContent: row.badge }) : null,
      control(row, refs));
    nodes.set(row.id, { node, refs });
    return node;
  }

  function tileNode(row, nodes) {
    const refs = {};
    const image = safeImage(row.image);
    const frame = el('div', { className: 'pp-frame' }, image ? el('img', { className: 'pp-art', src: image, alt: '', draggable: false }) : el('span', { className: 'pp-glyph' }, iconEl(row.icon ?? '◆')),
      row.badge ? el('span', { className: 'pp-badge', textContent: row.badge }) : null);
    const node = el('div', { className: `pp-tile${row.on ? ' on' : ''}` }, frame,
      el('strong', { className: 'pp-name', textContent: row.label }),
      row.detail || row.value ? el('small', { className: 'pp-detail', textContent: row.value ?? row.detail }) : null);
    nodes.set(row.id, { node, refs });
    return node;
  }

  /** The page is left: the host ends the visit the parents' code opened (it only ever makes things stricter; the host ends it by itself on a profile switch, the lock or a game start too). */
  const endVisit = () => {
    try {
      Promise.resolve(api.endPluginVisit?.(plugin.id, pageInfo.id)).catch(() => {});
    } catch {
      // nothing to end
    }
  };

  const page = {
    id: asTab ? 'plugin-tab' : 'plugin-page',
    name: pageInfo.title,
    pluginId: plugin.id,
    pageId: pageInfo.id,
    r: 0,
    c: 0,
    focusId: null,
    build(root) {
      open.add(this);
      if (!data) {
        if (!this.loading) {
          this.loading = true;
          this.load();
        }
        root.append(el('div', { className: 'files' }, el('div', { className: 'files-head' }, el('h2', { textContent: pageInfo.title })), el('p', { className: 'empty-note', textContent: t('plugins.loading') })));
        this.lines = [];
        return;
      }
      const groups = groupRows(data.rows, data.sections);
      this.lines = layoutLines(groups);
      this.nodes = new Map();
      const content = [];
      for (const g of groups) {
        if (g.title) content.push(el('div', { className: 'update-section', textContent: g.title }));
        if (g.kind === 'tiles') {
          const grid = el('div', { className: 'pp-grid' }, g.rows.map((row) => tileNode(row, this.nodes)));
          grid.style.setProperty('--cols', String(tileColumns(g.rows.length)));
          content.push(grid);
        } else content.push(...g.rows.map((row) => listNode(row, this.nodes)));
      }
      // Cursor: back on the row it was on (a redraw after a press must not jump to the top).
      if (this.focusId) {
        const at = this.lines.findIndex((cells) => cells.some((row) => row.id === this.focusId));
        if (at >= 0) {
          this.r = at;
          this.c = Math.max(0, this.lines[at].findIndex((row) => row.id === this.focusId));
        }
      }
      this.r = Math.min(this.r, Math.max(0, this.lines.length - 1));
      this.c = Math.min(this.c, Math.max(0, (this.lines[this.r]?.length ?? 1) - 1));
      this.scroller = el('div', { className: 'pp-scroll list-scroll' }, content.length ? content : el('p', { className: 'empty-note', textContent: t('plugins.page.empty') }));
      root.append(el('div', { className: 'files plugin-page' },
        el('div', { className: 'files-head' }, el('h2', { textContent: data.title || pageInfo.title }), data.description ?? pageInfo.description ? el('p', { className: 'meta', textContent: data.description ?? pageInfo.description }) : null),
        this.scroller));
      this.update({ instant: true });
    },
    /** Asks the host for the page (first open, or after the plugin said it changed). */
    async load() {
      let d;
      let note = t('plugins.page.locked');
      // Only the first read of a page asks for the parents' code (an admin page in a child's profile). A later read of the open page (a push, entering
      // the tab again) keeps what is shown when the host's visit has run out: the host decides, and every press then asks for itself.
      const read = () => api.getPluginPage(plugin.id, pageInfo.id);
      const readOnce = () => (data ? read() : withParentGrant(read));
      if (asTab) {
        // A tab is read on its own, not because the user pressed something: no toast when the plugin does not answer.
        try {
          d = await readOnce();
        } catch (err) {
          if (err?.held) return;
          if (err?.parent && data) return;
          if (!err?.locked) {
            console.error(err);
            note = t('plugins.tab.unavailable');
            // The plugin may be gone: the tab bar asks the host again and drops the tab (back to the first tab).
            window.dispatchEvent(new Event('couchhud:tabs'));
          }
        }
      } else {
        d = await attempt(async () => {
          try {
            return await readOnce();
          } catch (err) {
            if (err?.parent && data) return data;
            throw err;
          }
        }, plugin.name, { quietLocked: true });
      }
      data = d ?? { title: pageInfo.title, description: note, rows: [], sections: [] };
      if (currentView() === this) render();
    },
    /** Entering the tab again: read the page anew (it may have changed while another tab was open). */
    onEnter() {
      if (asTab && data) sync.push();
    },
    /** Leaving the tab for another tab: a value still settling goes out now. */
    onLeave() {
      for (const [id, s] of settlers) s.flush(id);
      endVisit();
    },
    /** The plugin pushed a change: read the page again unless a value is still settling (it would jump back under the user's fingers). */
    reload() {
      if (currentView() !== this) return;
      sync.push();
    },
    /** The tab is dropped (its plugin is gone or switched off): nothing is sent any more, the page is forgotten. */
    onDrop() {
      open.delete(this);
      for (const s of settlers.values()) s.cancelAll();
    },
    onClose() {
      open.delete(this);
      for (const [id, s] of settlers) s.flush(id);
      endVisit();
    },
    get row() {
      return this.lines?.[this.r]?.[this.c];
    },
    update({ instant = false } = {}) {
      const current = this.row;
      for (const [id, { node }] of this.nodes ?? []) node.classList.toggle('focused', id === current?.id);
      this.focusId = current?.id ?? null;
      keepInView(this.scroller, this.nodes?.get(current?.id)?.node, { instant });
    },
    pointer(node, _i, dispatch) {
      for (const [r, cells] of (this.lines ?? []).entries()) {
        const c = cells.findIndex((row) => this.nodes.get(row.id)?.node.contains(node));
        if (c >= 0) {
          this.r = r;
          this.c = c;
          this.update();
          dispatch('a');
          return;
        }
      }
    },
    /** The host's answer to a press or a change: the new rows (keeping the cursor) and a message. */
    applyResult(result) {
      if (!result) return;
      if (result.message) toast(result.message);
      if (result.rows) data = { ...data, rows: result.rows };
      if (currentView() === this) render();
    },
    async step(row, delta) {
      const s = settlerOf(row);
      const now = stepperState(row);
      const next = stepValue(row, now.number, delta);
      if (next === now.number) return sound.bump();
      sound.move();
      s.set(row.id, next, (value) => sync.send(async () => {
        const result = await attempt(() => withParentGrant(() => api.changePluginRow(plugin.id, pageInfo.id, row.id, String(value))), plugin.name);
        // Only the rows: a step made while this was on its way stays pending and keeps showing. The page is drawn (or read again after a
        // push that came meanwhile) by sync once nothing is waiting or on its way.
        if (result?.message) toast(result.message);
        if (result?.rows) data = { ...data, rows: result.rows };
      }));
      const ref = this.nodes.get(row.id)?.refs.value;
      if (ref) ref.textContent = stepperState(row).text;
    },
    async press(row) {
      switch (pressKind(row)) {
        case 'stepper':
          // A sends what is pending at once.
          return settlerOf(row).flush(row.id);
        case 'choice': {
          const options = (row.options ?? []).map((o) => ({ label: o.label, value: o.id }));
          if (!options.length) return sound.bump();
          const value = await chooseDialog(row.label, options, currentOption(row));
          if (value === undefined) return;
          return this.applyResult(await attempt(() => withParentGrant(() => api.changePluginRow(plugin.id, pageInfo.id, row.id, value)), plugin.name));
        }
        case 'link': {
          // The host opens only a page of the same plugin and hands it back; the plugin's code is not called for it.
          const result = await attempt(() => withParentGrant(() => api.activatePluginRow(plugin.id, pageInfo.id, row.id)), plugin.name);
          if (result?.navigate) push(declarativePage(plugin, result.navigate));
          return;
        }
        default:
          return this.applyResult(await attempt(() => withParentGrant(() => api.activatePluginRow(plugin.id, pageInfo.id, row.id)), plugin.name));
      }
    },
    async handle(action) {
      const move = (dr, dc) => {
        if (!this.lines?.length) return sound.bump();
        if (dr) {
          const nr = this.r + dr;
          if (nr < 0 || nr >= this.lines.length) return sound.bump();
          this.r = nr;
          this.c = Math.min(this.c, this.lines[nr].length - 1);
        } else {
          const nc = this.c + dc;
          if (nc < 0 || nc >= this.lines[this.r].length) return sound.bump();
          this.c = nc;
        }
        sound.move();
        this.update();
        renderHints();
      };
      const row = this.row;
      switch (action) {
        case 'up': return move(-1, 0);
        case 'down': return move(1, 0);
        case 'left':
        case 'right': {
          const delta = action === 'left' ? -1 : 1;
          if (row?.kind === 'stepper') return this.step(row, delta);
          return move(0, delta);
        }
        case 'a':
          if (!row) return sound.bump();
          sound.select();
          return this.press(row);
      }
    },
    hints() {
      const row = this.row;
      const label = { stepper: t('plugins.page.hint.send'), choice: t('plugins.page.hint.choose'), link: t('plugins.page.hint.open'), toggle: t('plugins.page.hint.toggle') }[row?.kind] ?? t('plugins.page.hint.press');
      return [...(row ? [['A', label]] : []), ...(row?.kind === 'stepper' ? [['◀▶', t('plugins.page.hint.change')]] : []), ['B', t('plugins.hint.back')]];
    }
  };
  return page;
}
