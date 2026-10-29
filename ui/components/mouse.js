// Optional mouse: a big pointer that shows while the mouse moves and hides again
// after a moment or as soon as the controller is used. Clicks act on what they
// hit; the controller focus stays where it is until something is clicked.

import { el } from '../core/dom.js';
import { state } from '../core/state.js';
import { currentView, switchTab } from '../core/nav.js';
import { quick } from './quick-menu.js';
import { isCodeEntry } from '../core/input-help.js';

const HIDE_AFTER_MS = 2200;
const WHEEL_STEP = 60;

let cursor;
let hideTimer;
let last = { x: -1, y: -1 };
let wheelAccumulator = 0;
let movingTimer;

// The easter egg pointer (unlocked on the info page): a cartoon mouse whose nose is the hotspot. The tail wags while
// it moves, it blinks now and then, and the scarf takes the skin's accent color.
const MOUSE_SVG = `
<svg viewBox="0 0 64 64" xmlns="http://www.w3.org/2000/svg">
  <g class="cm-tail"><path d="M46 46 C54 55 62 50 58 42 C55 36 60 31 63 31" fill="none" stroke="#1b1e28" stroke-width="5" stroke-linecap="round"/>
    <path d="M46 46 C54 55 62 50 58 42 C55 36 60 31 63 31" fill="none" stroke="#f3a5b6" stroke-width="2.4" stroke-linecap="round"/></g>
  <ellipse class="cm-body" cx="36" cy="36" rx="15" ry="12.5" transform="rotate(45 36 36)" fill="#dfe3ec" stroke="#1b1e28" stroke-width="2.4"/>
  <circle cx="28.5" cy="14.5" r="7.5" fill="#dfe3ec" stroke="#1b1e28" stroke-width="2.4"/>
  <circle cx="28.5" cy="14.5" r="4" fill="#f3a5b6"/>
  <circle cx="14.5" cy="28.5" r="7.5" fill="#dfe3ec" stroke="#1b1e28" stroke-width="2.4"/>
  <circle cx="14.5" cy="28.5" r="4" fill="#f3a5b6"/>
  <ellipse cx="17" cy="17" rx="12.5" ry="9.5" transform="rotate(45 17 17)" fill="#dfe3ec" stroke="#1b1e28" stroke-width="2.4"/>
  <path class="cm-scarf" d="M19 31 Q24 24 31 19" fill="none" stroke-width="4.5" stroke-linecap="round"/>
  <g class="cm-eyes"><circle cx="17.5" cy="11.8" r="2" fill="#1b1e28"/><circle cx="11.8" cy="17.5" r="2" fill="#1b1e28"/>
    <circle cx="18.1" cy="11.2" r="0.7" fill="#fff"/><circle cx="12.4" cy="16.9" r="0.7" fill="#fff"/></g>
  <circle cx="6.5" cy="6.5" r="3.2" fill="#ff6f91" stroke="#1b1e28" stroke-width="1.6"/>
  <path d="M9 3 L15 1 M10 5 L16 4 M3 9 L1 15 M5 10 L4 16" stroke="#1b1e28" stroke-width="1" stroke-linecap="round" opacity="0.7"/>
</svg>`;

/**
 * The pointer inside the HUD: "skin" (the skin's --pointer image, the default), "system" (the real
 * Windows pointer, no overlay) or "mouse" (the comic mouse). Falls back to "skin" without the unlock.
 */
export function setPointer(mode, mouseUnlocked) {
  const next = mode === 'system' ? 'system' : mode === 'mouse' && mouseUnlocked ? 'mouse' : 'skin';
  document.documentElement.dataset.pointer = next;
  if (cursor) cursor.innerHTML = next === 'mouse' ? MOUSE_SVG : '<i class="cp"></i>';
}

function show(x, y) {
  cursor.style.transform = `translate(${x}px, ${y}px)`;
  cursor.classList.add('moving');
  clearTimeout(movingTimer);
  movingTimer = setTimeout(() => cursor.classList.remove('moving'), 180);
  document.body.classList.add('mouse-active');
  clearTimeout(hideTimer);
  hideTimer = setTimeout(hide, HIDE_AFTER_MS);
}

export function hide() {
  if (!cursor) return;
  clearTimeout(hideTimer);
  document.body.classList.remove('mouse-active');
  for (const n of document.querySelectorAll('.hover')) n.classList.remove('hover');
}

/** Index of a clickable thing inside its list (tiles, rows, keys, buttons). */
function indexIn(node) {
  return node?.parentElement ? [...node.parentElement.children].indexOf(node) : -1;
}

function target(e) {
  return e.target instanceof Element ? e.target : null;
}

const CLICKABLE = '.passcode-key, .timeup-option, .quick-row, .ha-card, .news-card, .tile, .action, .settings li, .settings-cats li, .file-row, .check-row, .chip, .wizard-option, .osk-key, .menu li:not(.menu-group), #tab-list li, .hint';

// The code prompt without a menu over it: only clicks on its buttons count there.
const codeEntry = () => !state.overlay && !quick.open && isCodeEntry(currentView());

function onClick(e, dispatch) {
  // A click next to an open menu closes it, like on any desktop, and does nothing else (not a tile behind it). The hint bar
  // belongs to the menu: its buttons act on it.
  if (state.overlay && !target(e)?.closest('.dialog, #hints')) return dispatch('b');
  const node = target(e)?.closest(CLICKABLE);
  if (!node) {
    if (quick.open && !target(e)?.closest('#quick')) dispatch('b');
    return;
  }

  if (node.matches('.quick-row')) {
    quick.index = indexIn(node);
    return dispatch('a');
  }
  // Clicking next to the open guide menu closes it.
  if (quick.open) return dispatch('b');
  if (node.matches('#tab-list li')) return switchTab(Number(node.dataset.tab));
  if (node.matches('.hint')) return dispatch(node.dataset.btn === '≡' ? 'start' : node.dataset.btn.toLowerCase());
  if (node.matches('.menu li')) {
    // Group headings sit between the entries: the entry's own index, not its place among the list's children.
    state.overlay.index = node.dataset.index ? Number(node.dataset.index) : indexIn(node);
    return dispatch('a');
  }

  const view = currentView();
  if (view.pointer) return view.pointer(node, indexIn(node), dispatch);

  view.index = indexIn(node);
  view.update?.();
  dispatch('a');
}

function onWheel(e, dispatch) {
  // On the code prompt the arrows are code buttons: a wheel must not type them.
  if (codeEntry()) {
    wheelAccumulator = 0;
    return;
  }
  wheelAccumulator += e.deltaY;
  if (Math.abs(wheelAccumulator) < WHEEL_STEP) return;
  const forward = wheelAccumulator > 0;
  wheelAccumulator = 0;
  const horizontal = currentView().wheelAxis === 'x';
  dispatch(horizontal ? (forward ? 'right' : 'left') : (forward ? 'down' : 'up'));
}

/** dispatch(action): the same entry point the controller uses. */
export function attachMouse(dispatch, { pointer, mouseUnlocked } = {}) {
  cursor = el('div', { id: 'cursor', ariaHidden: 'true' });
  document.body.append(cursor);
  setPointer(pointer, mouseUnlocked);

  window.addEventListener('mousemove', (e) => {
    // Ignore the synthetic moves browsers send when the layout shifts under a still mouse.
    if (Math.abs(e.clientX - last.x) + Math.abs(e.clientY - last.y) < 3) return;
    last = { x: e.clientX, y: e.clientY };
    show(e.clientX, e.clientY);
    for (const n of document.querySelectorAll('.hover')) n.classList.remove('hover');
    target(e)?.closest(CLICKABLE)?.classList.add('hover');
  });
  window.addEventListener('mousedown', (e) => {
    e.preventDefault();
    show(e.clientX, e.clientY);
    cursor.classList.remove('click');
    void cursor.offsetWidth; // restart the squish animation
    cursor.classList.add('click');
    if (e.button === 0) onClick(e, dispatch);
    // Right button and the side button are B; on the code prompt B is a code button, so they type nothing there.
    if ((e.button === 2 || e.button === 3) && !codeEntry()) dispatch('b');
  });
  window.addEventListener('contextmenu', (e) => e.preventDefault());
  window.addEventListener('wheel', (e) => {
    e.preventDefault();
    show(e.clientX, e.clientY);
    onWheel(e, dispatch);
  }, { passive: false });
  document.documentElement.addEventListener('mouseleave', hide);
}
