// "Artwork ändern": all known images per slot (cover, banner, logo, square) to choose from, or an own file.

import { iconEl } from '../core/glyph-icons.js';
import { api } from '../core/api.js';
import { el, keepInView, setFocused } from '../core/dom.js';
import { refreshBackground } from '../core/art.js';
import { attempt, toast } from '../core/feedback.js';
import { move, render } from '../core/nav.js';
import { sound } from '../sound.js';
import { openFileBrowser } from './file-browser.js';
import { t } from '../core/i18n.js';

const SLOTS = [
  { id: 'cover', get label() { return t('artworkPicker.slot.cover'); }, cols: 6 },
  { id: 'hero', get label() { return t('artworkPicker.slot.hero'); }, cols: 3 },
  { id: 'logo', get label() { return t('artworkPicker.slot.logo'); }, cols: 4 },
  { id: 'square', get label() { return t('artworkPicker.slot.square'); }, cols: 6 }
];
const IMAGE_EXTENSIONS = ['.jpg', '.jpeg', '.png', '.webp', '.gif'];

export function artworkPickerPage(item) {
  return {
    id: 'artwork-picker',
    name: t('artworkPicker.name'),
    item,
    slot: 0,
    index: 0,
    choices: null,
    failed: false,
    /** Cursor position per slot, so switching back and forth keeps your place. */
    positions: [0, 0, 0, 0],
    // LB / RB switch between cover, banner, logo and square instead of the tabs.
    captures: (action) => action === 'lb' || action === 'rb',
    entries() {
      const slot = SLOTS[this.slot];
      const current = this.item.art?.[slot.id]?.[0];
      const found = (this.choices?.[slot.id] ?? []).map((c) => ({ kind: 'choice', choice: c, current: c.current === true || c.thumb === current || c.url === current }));
      return [{ kind: 'own' }, { kind: 'auto' }, ...found];
    },
    async load() {
      this.choices = await attempt(() => api.getArtworkChoices(this.item.id), t('artworkPicker.loadFailed'));
      this.failed = !this.choices;
      render();
    },
    build(root) {
      if (!this.choices && !this.failed) {
        this.load();
        this.tiles = [];
        this.list = [];
        root.append(el('div', { className: 'files' }, el('div', { className: 'files-head' },
          el('h2', { textContent: t('artworkPicker.title') }),
          el('p', { className: 'meta', textContent: t('artworkPicker.searching') }))));
        return;
      }
      const slot = SLOTS[this.slot];
      this.list = this.entries();
      this.index = Math.min(this.index, this.list.length - 1);
      // Which image is in use: the choice whose thumbnail or url matches (file names compared as a fallback).
      const baseName = (u) => String(u ?? '').split(/[\\/?#]/).filter(Boolean).pop();
      const cur = baseName(this.item.art?.[slot.id]?.[0]);
      for (const e of this.list) {
        if (e.kind === 'choice' && !e.current && cur && (baseName(e.choice.thumb) === cur || baseName(e.choice.url) === cur)) e.current = true;
      }

      const chips = SLOTS.map((s, i) => {
        const n = this.choices?.[s.id]?.length ?? 0;
        return el('span', { className: `chip${i === this.slot ? ' primary active' : ''}`, textContent: `${s.label} (${n})` });
      });
      const grid = el('div', { className: `pick-grid pick-${slot.id}` });
      grid.style.setProperty('--cols', slot.cols);
      this.tiles = this.list.map((entry, i) => {
        const tile = el('div', { className: `tile pick-tile${entry.current ? ' current' : ''}`, dataset: { index: i } });
        if (entry.kind === 'choice') {
          const img = el('img', { src: entry.choice.thumb, alt: '', decoding: 'async' });
          // A picture that is still on its way to the disk gets one more try before the tile counts as broken.
          let retried = false;
          img.onerror = () => {
            if (retried || !entry.current) return tile.classList.add('broken');
            retried = true;
            setTimeout(() => { img.src = `${entry.choice.thumb}${entry.choice.thumb.includes('?') ? '&' : '?'}r=1`; }, 800);
          };
          tile.append(el('div', { className: 'pick-img' }, img),
            el('span', { className: 'pick-source', textContent: entry.current ? t('artworkPicker.current') : entry.choice.source }));
        } else {
          tile.classList.add('pick-action');
          tile.append(el('span', { className: 'special-icon' }, iconEl(entry.kind === 'own' ? '+' : '↺')),
            el('span', { textContent: entry.kind === 'own' ? t('artworkPicker.own') : t('artworkPicker.auto') }));
        }
        grid.append(tile);
        return tile;
      });

      // What happened here first, then why, then how to get more.
      const notes = [];
      if (this.failed) notes.push(t('artworkPicker.note.failed'));
      if (this.choices && !(this.choices[slot.id]?.length)) notes.push(t('artworkPicker.note.none'));
      if (this.choices?.note) notes.push(this.choices.note);
      if (this.choices && !this.choices.hasKey) {
        notes.push(t('artworkPicker.note.key'));
      }

      this.scroller = el('div', { className: 'grid-scroll' }, grid);
      root.append(el('div', { className: 'pick-page' },
        el('div', { className: 'files-head' },
          el('h2', { textContent: t('artworkPicker.title') }),
          el('p', { className: 'meta', textContent: this.item.name })),
        el('div', { className: 'pick-tabs' }, chips),
        notes.length ? el('p', { className: 'pick-note', textContent: notes.join(' ') }) : null,
        this.scroller));
      this.update({ instant: true });
    },
    update({ instant = false } = {}) {
      if (!this.tiles?.length) return;
      setFocused(this.tiles, this.index);
      keepInView(this.scroller, this.tiles[this.index], { center: true, instant });
    },
    switchSlot(delta) {
      const next = this.slot + delta;
      if (next < 0 || next >= SLOTS.length) return sound.bump();
      this.positions[this.slot] = this.index;
      this.slot = next;
      this.index = this.positions[next];
      sound.tab();
      render();
    },
    async apply(value, message) {
      // A pick can wait for the download: one at a time, and the slot of the pick is the one used afterwards, whatever the page shows then.
      if (this.busy) return;
      const slotIndex = this.slot;
      const slot = SLOTS[slotIndex];
      this.busy = true;
      if (value) toast(t('artworkPicker.busy'), 30000);
      try {
        const updated = await attempt(() => api.setArtwork(this.item.id, slot.id, value), t('artworkPicker.applyFailed'));
        if (!updated) return;
        Object.assign(this.item, updated);
        refreshBackground();
        toast(message);
        // The list of choices is read again: which one is "current" is decided on the host from the new pick, and the pick's
        // picture is on disk by now (the stale list would still mark the old image and show its thumbnail).
        try {
          this.choices = await api.getArtworkChoices(this.item.id);
        } catch (err) {
          console.error(err);
        }
        // The picked image moves to the front of the list as "current": the cursor follows it.
        const saved = this.slot;
        this.slot = slotIndex;
        const at = this.entries().findIndex((e) => e.kind === 'choice' && e.current);
        this.slot = saved;
        this.positions[slotIndex] = at >= 0 ? at : this.positions[slotIndex];
        if (saved === slotIndex && at >= 0) this.index = at;
      } finally {
        this.busy = false;
        render();
      }
    },
    async activate() {
      const entry = this.list[this.index];
      if (!entry) return;
      if (this.busy) return sound.bump();
      const slot = SLOTS[this.slot];
      if (entry.kind === 'auto') return this.apply(null, t('artworkPicker.autoAgain', { slot: slot.label }));
      if (entry.kind === 'own') {
        const path = await openFileBrowser({ title: t('artworkPicker.pickTitle', { slot: slot.label }), mode: 'file', extensions: IMAGE_EXTENSIONS });
        if (path) await this.apply(path, t('artworkPicker.changed', { slot: slot.label }));
        return;
      }
      if (this.tiles[this.index].classList.contains('broken')) return sound.bump();
      return this.apply(entry.choice.url, t('artworkPicker.changed', { slot: slot.label }));
    },
    handle(action) {
      // While a pick runs nothing but moving the cursor and going back counts.
      if (this.busy && ['a', 'y', 'lb', 'rb'].includes(action)) return sound.bump();
      const cols = SLOTS[this.slot].cols;
      const n = this.list?.length ?? 0;
      switch (action) {
        case 'lb': return this.switchSlot(-1);
        case 'rb': return this.switchSlot(1);
        case 'left': return n && move(this, -1, n);
        case 'right': return n && move(this, 1, n);
        case 'up': return n && move(this, -cols, n, true);
        case 'down': return n && move(this, cols, n, true);
        case 'a': return this.activate();
        case 'y': return this.apply(null, t('artworkPicker.autoAgain', { slot: SLOTS[this.slot].label }));
      }
    },
    pointer(node, index, dispatch) {
      if (node.matches('.chip')) {
        if (this.busy) return sound.bump();
        const target = index;
        if (target !== this.slot) this.switchSlot(target - this.slot);
        return;
      }
      this.index = index;
      this.update();
      dispatch('a');
    },
    hints() {
      const entry = this.list?.[this.index];
      const a = entry?.kind === 'own' ? t('artworkPicker.hint.pick') : entry?.kind === 'auto' ? t('artworkPicker.hint.auto') : t('artworkPicker.hint.apply');
      const slot = this.slot;
      return [['A', a], ...(entry?.kind === 'auto' ? [] : [['Y', t('artworkPicker.hint.auto')]]),
        ...(slot > 0 ? [['LB', SLOTS[slot - 1].label]] : []), ...(slot < SLOTS.length - 1 ? [['RB', SLOTS[slot + 1].label]] : []),
        ['B', t('artworkPicker.hint.back')]];
    }
  };
}

// Opened from the edit menu (library-tools.js), next to name, texts and store entry.
