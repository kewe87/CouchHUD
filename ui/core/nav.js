// Tabs, the page stack on top of them, and the render/update cycle.
//
// Views build their DOM once (build) and only adjust focus, texts and position
// while navigating (update). Rebuilding on every move made lists flash back to
// their start for a frame and killed all transitions.

import { $, el } from './dom.js';
import { t } from './i18n.js';
import { state } from './state.js';
import { sound } from '../sound.js';
import { glyph } from './skins.js';
import { stopTrailer } from './media.js';
import { activeAfter, isPinnedTab } from './plugin-tabs.js';

let tabs = [];

/** Sets the tab views; the active tab stays active even when tabs are added before it. */
export function setTabs(views) {
  // The active tab is gone (a plugin was switched off or failed, its area went off): back to the first tab.
  state.tab = activeAfter(tabs, state.tab, views);
  tabs = views;
}

export const tabViews = () => tabs;

export const currentView = () => state.stack.at(-1) ?? tabs[state.tab];

/** Rebuilds the current view; for tab switches and changed data, not for moving the cursor. */
let lastRendered = null;
let lastTab = 0;

export function render() {
  renderTabs();
  const root = $('#view');
  const view = currentView();
  // Views with media restart their trailer in build(); everything else stays calm.
  if (!view.keepsTrailer) stopTrailer();
  root.replaceChildren();
  root.dataset.view = view.id || view.name.toLowerCase();
  document.body.dataset.view = root.dataset.view;
  view.build(root);
  renderHints();

  // Slide in when the screen changes, not when the same view redraws.
  if (lastRendered !== view) {
    const direction = state.stack.length || lastRendered?.id === 'detail' ? 'up' : state.tab >= lastTab ? 'left' : 'right';
    root.classList.remove('enter-left', 'enter-right', 'enter-up');
    void root.offsetWidth;
    root.classList.add(`enter-${direction}`);
  }
  // Leaving a tab for another tab: it may finish what it still holds (a plugin tab sends a value that is settling).
  if (lastRendered && lastRendered !== view && tabs.includes(lastRendered) && tabs.includes(view)) lastRendered.onLeave?.();
  lastRendered = view;
  lastTab = state.tab;
}

/** One entry of the tab bar; a tab may carry a small icon before its name (`tabIcon()`, plugin tabs). */
function tabItem(view, active, index) {
  const li = el('li', { className: active ? 'active' : '', textContent: view.name });
  li.dataset.tab = String(index);
  const icon = view.tabIcon?.();
  if (icon) li.prepend(icon);
  return li;
}

export function renderTabs() {
  const list = $('#tab-list');
  // Assistant and Settings stand apart at the end and never scroll out of sight; the others scroll (with many plugin tabs).
  const items = tabs.map((v, i) => ({ v, i, pinned: isPinnedTab(v) }));
  list.replaceChildren(...items.filter((x) => !x.pinned).map((x) => tabItem(x.v, x.i === state.tab, x.i)));
  $('#tab-pinned').replaceChildren(...items.filter((x) => x.pinned).map((x) => tabItem(x.v, x.i === state.tab, x.i)));
  // Many tabs and a full status on the right: the list is cut and fades at its edges; the active tab is always scrolled into it.
  const active = [...list.children].find((li) => Number(li.dataset.tab) === state.tab);
  if (active) {
    const left = active.offsetLeft - list.offsetLeft;
    if (left < list.scrollLeft) list.scrollLeft = left - 16;
    else if (left + active.offsetWidth > list.scrollLeft + list.clientWidth) list.scrollLeft = left + active.offsetWidth - list.clientWidth + 16;
  }
  list.classList.toggle('cut-start', list.scrollLeft > 0);
  list.classList.toggle('cut-end', list.scrollLeft + list.clientWidth < list.scrollWidth - 1);
}

export function renderHints() {
  const hints = state.overlay
    ? state.overlay.hints?.() ?? [['A', t('nav.select')], ['B', t('nav.back')]]
    : state.panel?.hints() ?? currentView().hints();
  $('#hints').replaceChildren(...hints.map(([btn, label]) =>
    el('span', { className: 'hint', dataset: { btn } },
      el('span', { className: `glyph btn-${btn.toLowerCase()}`, textContent: glyph(btn) }), label)));
}

/** Moves a view's cursor; wraps never, bumps at the ends. */
export function move(view, delta, length, clamp = false) {
  let next = view.index + delta;
  if (next < 0 || next >= length) {
    if (clamp || Math.abs(delta) > 1) {
      sound.bump();
      return false;
    }
    next = Math.max(0, Math.min(length - 1, next));
    if (next === view.index) {
      sound.bump();
      return false;
    }
  }
  view.index = next;
  sound.move();
  view.update();
  // The hints depend on what is focused now.
  renderHints();
  return true;
}

/**
 * Closes every page on the stack and tells each one (an open code prompt must
 * resolve, not hang). Sticky pages, like the first-start notice, stay.
 */
export function clearStack() {
  const closing = state.stack.filter((p) => !p.sticky);
  state.stack = state.stack.filter((p) => p.sticky);
  for (const page of closing.reverse()) page.onClose?.();
}

/** Removes one page if it is still open; never anything else. */
export function removePage(page) {
  const i = state.stack.indexOf(page);
  if (i >= 0) state.stack.splice(i, 1);
}

export function switchTab(tab) {
  const next = (tab + tabs.length) % tabs.length;
  clearStack();
  if (next === state.tab) return render();
  state.tab = next;
  tabs[next].onEnter?.();
  sound.tab();
  render();
}

/** Opens a page on top of the current one. */
export function push(page) {
  // A protected profile waits at the profile picker: no other page opens over it (its code prompts come through their own way).
  if (state.pickerHold && page.id !== 'profile-picker') return;
  state.stack.push(page);
  sound.select();
  render();
}

/** Closes the topmost page; false if there was none. */
export function pop() {
  if (!state.stack.length) return false;
  const page = state.stack.pop();
  page.onClose?.();
  sound.back();
  render();
  return true;
}

export function popTo(page) {
  while (state.stack.length && state.stack.at(-1) !== page) state.stack.pop();
  render();
}

/** Sorting changes after favorites/launches; keep the tab's cursor on the same item. */
export function keepFocusOn(item) {
  const view = tabs[state.tab];
  const index = view.items ? view.items().findIndex((i) => i.id === item.id) : -1;
  if (index >= 0) view.index = index;
}
