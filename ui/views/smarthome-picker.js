// Smart home picker: which Home Assistant entities the Smart Home tab shows, and in which order.
// Big homes have well over a thousand entities; the tab only shows what is chosen here.
// LB/RB: filter (suggestions, chosen, all, per type), X: search, A: choose, Y: move (in "Chosen"), B: done.
// The host keeps the token; this page only sends entity ids and gets names, rooms and states.

import { api } from '../core/api.js';
import { el, keepInView, setFocused } from '../core/dom.js';
import { attempt, toast } from '../core/feedback.js';
import { currentView, move, pop, render } from '../core/nav.js';
import { state } from '../core/state.js';
import { t } from '../core/i18n.js';
import { sound } from '../sound.js';
import { openKeyboard } from '../components/keyboard.js';
import { iconFor } from './smarthome.js';
import { categoryOf, CATEGORIES, filterEntities, groupEntities, MAX_SELECTED, moveInOrder } from '../core/ha-pick.js';

// i18n-used: smartHome.pick.cat.light smartHome.pick.cat.switch smartHome.pick.cat.fan smartHome.pick.cat.climate smartHome.pick.cat.cover smartHome.pick.cat.media smartHome.pick.cat.scene smartHome.pick.cat.lock smartHome.pick.cat.sensor smartHome.pick.cat.other

/** The picker as a page. onDone(selection) runs after the choice is stored. */
export function smartHomePicker({ onDone } = {}) {
  return {
    id: 'smarthome-picker',
    get name() { return t('smartHome.pick.title'); },
    index: 0,
    chip: null,
    query: '',
    catalog: null,
    failed: false,
    selected: [],
    saved: [],
    grabbed: null,
    captures: (action) => action === 'lb' || action === 'rb' || action === 'b',
    async load() {
      const catalog = await attempt(() => api.getHomeCatalog(), t('smartHome.unreachable'));
      this.failed = !catalog;
      this.catalog = catalog ?? { entities: [], selected: [], areas: false };
      this.selected = [...(this.catalog.selected ?? [])];
      this.saved = [...this.selected];
      this.chip ??= this.selected.length ? 'selected' : 'suggested';
      if (currentView() === this) render();
    },
    chips() {
      const present = new Set((this.catalog?.entities ?? []).map((e) => categoryOf(e.domain)));
      return ['suggested', 'selected', 'all', ...CATEGORIES.map(([c]) => c).filter((c) => present.has(c))];
    },
    chipLabel(chip) {
      if (chip === 'suggested') return t('smartHome.pick.suggested');
      if (chip === 'selected') return t('smartHome.pick.chosen', { count: this.selected.length });
      if (chip === 'all') return t('smartHome.pick.all');
      const key = `smartHome.pick.cat.${chip}`;
      return t(key);
    },
    visible() {
      return filterEntities(this.catalog?.entities ?? [], { chip: this.chip, query: this.query, selected: this.selected });
    },
    build(root) {
      if (!this.catalog) this.load();
      const loading = !this.catalog;
      const groups = loading ? [] : groupEntities(this.visible(), {
        byArea: !!this.catalog.areas,
        keepOrder: this.chip === 'selected',
        noArea: t('smartHome.pick.noArea'),
        categoryName: (c) => {
          const key = `smartHome.pick.cat.${c}`;
          return t(key);
        }
      });
      const chosen = new Set(this.selected);
      this.rows = [];
      this.ids = [];
      const list = [];
      for (const group of groups) {
        if (group.title) list.push(el('li', { className: 'update-section', textContent: group.title }));
        for (const e of group.items) {
          const on = chosen.has(e.id);
          const position = this.chip === 'selected' ? `${this.selected.indexOf(e.id) + 1}. ` : '';
          const where = e.missing ? [t('smartHome.pick.missing'), e.id] : [e.area, e.id];
          const row = el('li', { className: `check-row ha-pick-row${on ? ' checked' : ''}${e.missing ? ' missing' : ''}${this.grabbed === e.id ? ' grabbed' : ''}` },
            el('span', { className: 'check-box', textContent: on ? '✓' : '' }),
            el('span', { className: 'ha-pick-icon', textContent: iconFor(e) }),
            el('span', { className: 'check-main' },
              el('strong', { textContent: `${position}${e.name}` }),
              el('small', { textContent: where.filter(Boolean).join(' · ') })));
          this.rows.push(row);
          this.ids.push(e.id);
          list.push(row);
        }
      }
      this.index = Math.max(0, Math.min(this.index, this.rows.length - 1));
      this.scroller = el('ul', { className: 'check-list list-scroll' }, list);

      const meta = loading
        ? t('smartHome.connecting')
        : this.failed
          ? t('smartHome.unreachable')
          : [t('smartHome.pick.help'), this.query ? t('smartHome.pick.searching', { query: this.query }) : ''].filter(Boolean).join(' ');
      const empty = !loading && !this.rows.length;
      root.append(el('div', { className: 'files ha-pick' },
        el('div', { className: 'files-head' },
          el('h2', { textContent: t('smartHome.pick.title') }),
          el('p', { className: 'meta', textContent: meta })),
        loading ? null : el('div', { className: 'pick-tabs ha-pick-chips' },
          this.chips().map((c) => {
            const chip = el('span', { className: `chip${c === this.chip ? ' primary active' : ''}`, textContent: this.chipLabel(c) });
            if (c === 'selected') this.chosenChip = chip;
            return chip;
          })),
        empty ? el('p', { className: 'empty-note', textContent: this.query ? t('smartHome.pick.noMatch') : this.chip === 'selected' ? t('smartHome.pick.noneChosen') : t('smartHome.pick.nothing') }) : null,
        empty ? null : this.scroller));
      this.update({ instant: true });
    },
    update({ instant = false } = {}) {
      if (!this.rows?.length) return;
      setFocused(this.rows, this.index);
      keepInView(this.scroller, this.rows[this.index], { instant });
    },
    switchChip(delta) {
      const chips = this.chips();
      const next = chips.indexOf(this.chip) + delta;
      if (next < 0 || next >= chips.length) return sound.bump();
      this.chip = chips[next];
      this.grabbed = null;
      this.index = 0;
      sound.tab();
      render();
    },
    toggle() {
      const id = this.ids?.[this.index];
      if (!id) return sound.bump();
      if (this.selected.includes(id)) {
        this.selected = this.selected.filter((x) => x !== id);
        // In "Chosen" the row goes away: stay at the same place.
      } else {
        if (this.selected.length >= MAX_SELECTED) {
          sound.bump();
          return toast(t('smartHome.pick.full', { count: MAX_SELECTED }));
        }
        this.selected = [...this.selected, id];
      }
      sound.select();
      // In "Chosen" the list itself changes; elsewhere only this row and the count do (a long list stays put).
      if (this.chip === 'selected') return render();
      const on = this.selected.includes(id);
      const row = this.rows[this.index];
      row.classList.toggle('checked', on);
      row.querySelector('.check-box').textContent = on ? '✓' : '';
      if (this.chosenChip) this.chosenChip.textContent = this.chipLabel('selected');
    },
    /** Y in "Chosen": pick the row up, move it with up/down, put it down with A, Y or B. */
    grab() {
      if (this.chip !== 'selected') return sound.bump();
      const id = this.ids?.[this.index];
      if (!id) return sound.bump();
      this.grabbed = this.grabbed ? null : id;
      sound.select();
      render();
    },
    shift(delta) {
      const next = moveInOrder(this.selected, this.grabbed, delta, this.ids ?? []);
      if (next === this.selected) return sound.bump();
      this.selected = next;
      // The focus follows the row in the list as shown (a search may hide some chosen entries).
      this.index = this.visible().findIndex((e) => e.id === this.grabbed);
      sound.move();
      render();
    },
    async search() {
      const query = await openKeyboard({ title: t('smartHome.pick.searchTitle'), value: this.query, placeholder: t('smartHome.pick.searchPlaceholder'), maxLength: 60 });
      if (query === undefined) return;
      this.query = query.trim();
      this.index = 0;
      render();
    },
    changed() {
      return this.selected.length !== this.saved.length || this.selected.some((id, i) => id !== this.saved[i]);
    },
    /** Stores the choice (only when it changed). A failure (couch locked meanwhile, Home Assistant gone) keeps the old one. */
    async store() {
      if (this.failed || !this.changed()) return;
      const stored = await attempt(() => api.setHomeSelection(this.selected), t('smartHome.pick.saveFailed'));
      if (stored === undefined) return;
      this.saved = [...stored];
      toast(t('smartHome.pick.saved', { count: stored.length }));
    },
    /** B: store and close. */
    async finish() {
      if (this.closing) return;
      this.closing = true;
      await this.store();
      if (state.stack.at(-1) === this) pop();
      onDone?.(this.saved);
    },
    /** Closed another way (tab switch, guide menu): still store what was chosen. */
    onClose() {
      if (this.closing) return;
      this.closing = true;
      this.store().then(() => onDone?.(this.saved));
    },
    async handle(action) {
      const count = this.rows?.length ?? 0;
      if (this.grabbed) {
        switch (action) {
          case 'up': return this.shift(-1);
          case 'down': return this.shift(1);
          case 'a':
          case 'y':
          case 'b':
            this.grabbed = null;
            sound.select();
            return render();
          default: return sound.bump();
        }
      }
      switch (action) {
        case 'up': return count && move(this, -1, count, true);
        case 'down': return count && move(this, 1, count, true);
        case 'lb': return this.switchChip(-1);
        case 'rb': return this.switchChip(1);
        case 'a': return this.toggle();
        case 'x': return this.search();
        case 'y': return this.grab();
        case 'b': return this.finish();
      }
    },
    hints() {
      if (this.grabbed) return [['↕', t('smartHome.pick.hint.move')], ['A', t('smartHome.pick.hint.drop')]];
      const hints = [['A', t('smartHome.pick.hint.toggle')], ['X', t('smartHome.pick.hint.search')]];
      if (this.chip === 'selected' && this.rows?.length) hints.push(['Y', t('smartHome.pick.hint.order')]);
      hints.push(['LB/RB', t('smartHome.pick.hint.filter')], ['B', t('smartHome.pick.hint.done')]);
      return hints;
    }
  };
}
