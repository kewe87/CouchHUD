// Modal menus: power menu, confirmations, pickers with live preview.

import { iconEl } from '../core/glyph-icons.js';
import { $, el, keepInView } from '../core/dom.js';
import { state } from '../core/state.js';
import { attempt } from '../core/feedback.js';
import { renderHints } from '../core/nav.js';
import { sound } from '../sound.js';
import { t } from '../core/i18n.js';

/**
 * menu: { title, message?, entries: [{ label, detail?, icon?, run }], index?, onMove?(i), onCancel?(), hints?(),
 *         preview? (the screen behind stays visible, e.g. the skin picker) }
 */
export function openOverlay(menu) {
  menu.index = menu.index || 0;
  state.overlay = menu;
  sound.open();
  renderOverlay();
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
      index: danger || focusCancel ? 1 : 0,
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
  ul.classList.toggle('with-icons', hasIcons);
  ul.replaceChildren(...menu.entries.map((e, i) =>
    el('li', { className: `${i === menu.index ? 'focused' : ''}${e.danger ? ' danger' : ''}`.trim() },
      hasIcons ? el('span', { className: 'menu-icon' }, iconEl(e.icon || '')) : null,
      el('span', { className: 'menu-text' },
        el('span', { className: 'menu-label', textContent: e.label }),
        e.detail ? el('small', { textContent: e.detail }) : null))));
  keepInView(ul, ul.children[menu.index], { instant: true });
  // Long pickers: the edge fade alone does not say that there is more, so count along.
  const title = overlay.querySelector('.dialog-title');
  if (ul.scrollHeight > ul.clientHeight + 4) title.append(el('small', { className: 'dialog-count', textContent: t('overlay.count', { current: menu.index + 1, total: menu.entries.length }) }));
}

export function handleOverlay(action) {
  const menu = state.overlay;
  switch (action) {
    case 'up':
    case 'down': {
      const next = menu.index + (action === 'up' ? -1 : 1);
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
