// Modal menus: power menu, confirmations, pickers with live preview.

import { iconEl } from '../core/glyph-icons.js';
import { $, el, keepInView } from '../core/dom.js';
import { state } from '../core/state.js';
import { attempt } from '../core/feedback.js';
import { renderHints } from '../core/nav.js';
import { sound } from '../sound.js';
import { t } from '../core/i18n.js';
import { confirmStartIndex } from '../core/plugin-rules.js';
import { gridMove, groupRows } from '../core/menu-grid.js';

/**
 * menu: { title, message?, entries: [{ label, detail?, icon?, run }], index?, onMove?(i), onCancel?(), hints?(),
 *         preview? (the screen behind stays visible, e.g. the skin picker),
 *         layout? 'list' | 'tiles' (one row of tiles per group, D-pad in two directions),
 *         details? 'all' | 'focused' (only the focused row shows its detail) | 'below' (the focused entry's detail under the menu),
 *         refreshMs? (draws itself again this often while it is open, for live values such as a running timer) }
 * Entries may carry group (a heading; a new heading where it changes, the later ones behind a separator) and value (short status
 * text on the right, always shown).
 */
export function openOverlay(menu) {
  menu.index = menu.index || 0;
  state.overlay = menu;
  sound.open();
  renderOverlay();
  if (menu.refreshMs > 0) {
    const timer = setInterval(() => (state.overlay === menu ? renderOverlay() : clearInterval(timer)), menu.refreshMs);
  }
}

export function closeOverlay({ cancelled = true } = {}) {
  if (cancelled) state.overlay?.onCancel?.();
  state.overlay = null;
  renderOverlay();
}

// Questions that arrive from outside (assistant, MCP, Bluetooth) must not replace a menu the user is in:
// they wait until nothing is open, one after the other.
const freeWaiters = [];

export function overlayFree() {
  return state.overlay ? new Promise((resolve) => freeWaiters.push(resolve)) : Promise.resolve();
}

function releaseNextWaiter() {
  if (state.overlay || !freeWaiters.length) return;
  freeWaiters.shift()();
  // If whoever was released opens nothing after all, the next one must not wait forever.
  setTimeout(releaseNextWaiter, 0);
}

/**
 * Yes/no question as a promise; B or "Cancel" resolve to false.
 * focusCancel starts on "Cancel" (what comes from outside must be confirmed on purpose); tag names the question for closeTagged;
 * cancelLabel replaces "Cancel" where the second answer is not a cancel (e.g. "Later"), cancelIcon its icon.
 */
export function confirmDialog(title, confirmLabel, { danger = false, message = '', icon = '', focusCancel = false, tag = '', cancelLabel = '', cancelIcon = '', web = false } = {}) {
  return new Promise((resolve) => {
    openOverlay({
      title,
      message,
      tag,
      web,
      index: confirmStartIndex({ danger, focusCancel }),
      onCancel: () => resolve(false),
      entries: [
        { label: confirmLabel, icon, danger, run: () => resolve(true) },
        { label: cancelLabel || t('overlay.cancel'), icon: cancelIcon || (icon ? '✕' : ''), run: () => resolve(false) }
      ]
    });
  });
}

/** Choose one of several values; resolves to the chosen value or undefined. */
export function chooseDialog(title, options, current) {
  return new Promise((resolve) => {
    openOverlay({
      title,
      index: Math.max(0, options.findIndex((o) => o.value === current)),
      onCancel: () => resolve(undefined),
      entries: options.map((o) => ({ label: o.label, detail: o.detail, icon: o.icon, danger: o.danger, run: () => resolve(o.value) }))
    });
  });
}

/** Closes the open question with this tag (the host gave up on it). */
export function closeTagged(tag) {
  if (tag && state.overlay?.tag === tag) closeOverlay();
}

export function renderOverlay() {
  const overlay = $('#overlay');
  const menu = state.overlay;
  if (!menu) queueMicrotask(releaseNextWaiter);
  overlay.hidden = !menu;
  overlay.classList.toggle('preview', !!menu?.preview);
  // A web page's question: marked as the web's (globe, own frame), never in the look of CouchHUD's own questions.
  overlay.classList.toggle('web', !!menu?.web);
  document.body.classList.toggle('overlay-open', !!menu);
  renderHints();
  if (!menu) {
    // Nothing of a closed menu may stay focused behind the scenes.
    overlay.querySelector('.menu').replaceChildren();
    return;
  }
  overlay.querySelector('.dialog-title').textContent = menu.title;
  const text = overlay.querySelector('.dialog-text');
  text.textContent = menu.message || '';
  text.hidden = !menu.message;
  const ul = overlay.querySelector('.menu');
  const hasIcons = menu.entries.some((e) => e.icon);
  const tiles = menu.layout === 'tiles';
  const details = menu.details || 'all';
  ul.classList.toggle('with-icons', hasIcons);
  ul.classList.toggle('grouped', menu.entries.some((e) => e.group));
  ul.classList.toggle('tiles', tiles);
  // As many columns as the longest group has tiles.
  if (tiles) ul.style.setProperty('--menu-cols', String(Math.max(...groupRows(menu.entries.map((e) => e.group)).map((r) => r.length))));
  else ul.style.removeProperty('--menu-cols');
  const items = [];
  menu.entries.forEach((e, i) => {
    // A heading is no entry: it carries no index, so the mouse and the D-pad only ever land on entries.
    if (e.group && e.group !== menu.entries[i - 1]?.group) items.push(el('li', { className: 'menu-group', role: 'presentation', textContent: e.group }));
    const focused = i === menu.index;
    const showDetail = e.detail && (details === 'all' || (details === 'focused' && focused));
    items.push(el('li', { className: `${focused ? 'focused' : ''}${e.danger ? ' danger' : ''}`.trim(), dataset: { index: String(i) } },
      hasIcons ? el('span', { className: 'menu-icon' }, iconEl(e.icon || '')) : null,
      el('span', { className: 'menu-text' },
        el('span', { className: 'menu-label', textContent: e.label }),
        showDetail ? el('small', { textContent: e.detail }) : null),
      e.value ? el('span', { className: 'menu-value', textContent: e.value }) : null));
  });
  ul.replaceChildren(...items);
  // The focused entry's detail in one place under the menu (tiles), so the tiles stay short.
  let below = overlay.querySelector('.menu-detail');
  if (details === 'below') {
    if (!below) ul.after((below = el('p', { className: 'menu-detail' })));
    below.textContent = menu.entries[menu.index]?.detail || '';
  } else below?.remove();
  if (tiles) fitTileLabels(ul);
  keepInView(ul, ul.querySelector('li.focused'), { instant: true });
  // Long pickers: the edge fade alone does not say that there is more, so count along.
  const title = overlay.querySelector('.dialog-title');
  if (ul.scrollHeight > ul.clientHeight + 4) title.append(el('small', { className: 'dialog-count', textContent: t('overlay.count', { current: menu.index + 1, total: menu.entries.length }) }));
}

/**
 * A tile label never gets cut: a word wider than the tile (a skin with capitals and wide letter spacing, a long German word)
 * makes the label smaller step by step; the CSS breaks it as a last resort.
 */
function fitTileLabels(ul) {
  for (const label of ul.querySelectorAll('.menu-label')) {
    label.style.removeProperty('font-size');
    label.classList.remove('squeezed');
    const box = label.parentElement;
    let size = parseFloat(getComputedStyle(label).fontSize);
    const min = size * 0.7;
    while (label.scrollWidth > box.clientWidth + 0.5 && size > min) {
      size -= 1;
      label.style.fontSize = `${size}px`;
    }
    if (label.scrollWidth > box.clientWidth + 0.5) label.classList.add('squeezed');
  }
}

export function handleOverlay(action) {
  const menu = state.overlay;
  switch (action) {
    case 'left':
    case 'right':
    case 'up':
    case 'down': {
      // Tiles: rows per group, the D-pad moves in two directions. A list only goes up and down.
      if (menu.layout !== 'tiles' && (action === 'left' || action === 'right')) return;
      const next = menu.layout === 'tiles' ? gridMove(groupRows(menu.entries.map((e) => e.group)), menu.index, action) : menu.index + (action === 'up' ? -1 : 1);
      if (next < 0 || next >= menu.entries.length) return sound.bump();
      menu.index = next;
      menu.onMove?.(next);
      sound.move();
      return renderOverlay();
    }
    case 'a': {
      const entry = menu.entries[menu.index];
      closeOverlay({ cancelled: false });
      sound.select();
      return attempt(() => entry.run());
    }
    case 'b':
    case 'start':
      sound.back();
      return closeOverlay();
  }
}
