// Depth and motion: parallax between backdrop, logo and shelf on focus changes between tiles, and a
// gentle 3D tilt with a light reflection on the focused tile.
//
// Everything here only writes `transform` / `rotate` / `opacity` (compositor-only), never
// layout. Views stay unaware: a MutationObserver on #view sees which element just got the
// "focused" class and derives the direction from sibling order (no layout reads).
// Off with the system's reduced-motion setting or the ReduceMotion config (state.info.reduceMotion).

import { $ } from './dom.js';
import { state } from './state.js';
import { LAYERS, PARALLAX_CLAMP, PARALLAX_SPRING, nudgeVelocity, nudgesParallax, stepDirection } from './motion-rules.js';

const FOCUS_TARGETS = '.tile, .action';
const TILT_MAX = 7; // degrees, hard limit

const query = window.matchMedia?.('(prefers-reduced-motion: reduce)');

const par = { x: 0, y: 0, vx: 0, vy: 0 };
const tiles = new Map(); // tile -> tilt state
let layerNodes = null;
let remPx = 20;
let skinTilt = 1;
let enabled = false;
const paused = new Set(); // reasons the loop must not run: 'focus', 'screensaver', 'launch'
let raf = 0;
let last = 0;
let lastFocused = null;
const pointer = { x: 0, y: 0, moved: false, over: false };

export const motionEnabled = () => enabled;

function evaluate() {
  const next = !query?.matches && !state.info.reduceMotion;
  document.documentElement.dataset.motion = next ? 'on' : 'off';
  if (next === enabled) return;
  enabled = next;
  if (enabled) {
    focusCurrent();
    start();
  } else {
    stop();
    reset();
  }
}

function reset() {
  par.x = par.y = par.vx = par.vy = 0;
  applyLayers();
  for (const [tile] of tiles) clearTile(tile);
  tiles.clear();
}

function clearTile(tile) {
  tile.style.rotate = '';
  const glare = tile.querySelector(':scope > .art > .glare');
  if (glare) glare.style.transform = '';
}

/** Call after state.info changed (settings) to pick up ReduceMotion. */
export const refreshMotion = evaluate;

/** Pauses or resumes all JS motion for a reason ('focus', 'screensaver', 'launch'). */
export function setMotionActive(active, reason = 'focus') {
  if (active) paused.delete(reason);
  else paused.add(reason);
  syncLive();
  if (active) start();
  else stop();
}

/** data-live on :root: endless CSS animations only run while the HUD is in front and visible (see styles.css). */
function syncLive() {
  document.documentElement.dataset.live = paused.size || document.hidden ? 'off' : 'on';
}

function start() {
  if (raf || !enabled || paused.size || document.hidden) return;
  last = performance.now();
  raf = requestAnimationFrame(frame);
}

function stop() {
  cancelAnimationFrame(raf);
  raf = 0;
}

function layers() {
  if (!layerNodes) layerNodes = LAYERS.map(([sel, ax, ay]) => ({ nodes: [...document.querySelectorAll(sel)], ax, ay }));
  return layerNodes;
}

function applyLayers() {
  const moving = Math.abs(par.x) + Math.abs(par.y) > 0.002;
  for (const { nodes, ax, ay } of layers()) {
    for (const node of nodes) {
      node.style.transform = moving ? `translate3d(${(par.x * ax * remPx).toFixed(2)}px, ${(par.y * ay * remPx).toFixed(2)}px, 0)` : '';
    }
  }
}

/** Direction (-1/0/1 per axis) from the old to the new focused element, from sibling order only; null for no single step (see stepDirection). */
function direction(from, to) {
  if (!from || !from.isConnected || from.parentElement !== to.parentElement) return null;
  const siblings = [...to.parentElement.children];
  const cols = parseInt(to.parentElement.style.getPropertyValue('--cols'), 10) || 0;
  return stepDirection(siblings.indexOf(from), siblings.indexOf(to), cols);
}

function tileState(tile) {
  let s = tiles.get(tile);
  if (!s) {
    s = { ax: 0, ay: 0, vx: 0, vy: 0, seed: Math.random() * 6.28, glare: null, focused: true };
    const art = tile.querySelector(':scope > .art');
    if (art) {
      s.glare = art.querySelector(':scope > .glare');
      if (!s.glare) {
        s.glare = document.createElement('span');
        s.glare.className = 'glare';
        art.append(s.glare);
      }
    }
    tiles.set(tile, s);
  }
  return s;
}

function onFocused(node) {
  if (!enabled) return;
  const dir = direction(lastFocused, node);
  lastFocused = node;
  if (dir && nudgesParallax(document.body.dataset.view, node.classList.contains('tile') ? 'tile' : 'action')) {
    // Nudge the parallax layers in the focus direction; the spring brings them back.
    // Not on the detail page: moving between its buttons must not swing the picture.
    // A wrap (last tile to first) is no single step and gives none; fast steps fill only the room left (nudgeVelocity).
    par.vx = nudgeVelocity(par.vx, par.x, dir.x);
    par.vy = nudgeVelocity(par.vy, par.y, dir.y);
    layerNodes = null;
  }
  if (node.classList.contains('tile')) {
    const s = tileState(node);
    s.focused = true;
    // The tile swings in from the direction it was reached.
    if (dir) {
      s.vx += dir.x * 55;
      s.vy += dir.y * 55;
    }
  }
  start();
}

function focusCurrent() {
  const node = $('#view')?.querySelector('.tile.focused, .action.focused');
  if (node) onFocused(node);
}

/** Springs one value toward `target`; returns the new [pos, vel]. */
function spring(pos, vel, target, dt, k = 120, d = 15) {
  vel += ((target - pos) * k - vel * d) * dt;
  return [pos + vel * dt, vel];
}

function frame(now) {
  raf = 0;
  if (!enabled || paused.size || document.hidden) return;
  const dt = Math.min(0.05, (now - last) / 1000);
  last = now;

  // Parallax springs
  [par.x, par.vx] = spring(par.x, par.vx, 0, dt, PARALLAX_SPRING.k, PARALLAX_SPRING.d);
  [par.y, par.vy] = spring(par.y, par.vy, 0, dt, PARALLAX_SPRING.k, PARALLAX_SPRING.d);
  const clamp = (v) => Math.max(-PARALLAX_CLAMP, Math.min(PARALLAX_CLAMP, v));
  par.x = clamp(par.x);
  par.y = clamp(par.y);
  const parMoving = Math.abs(par.x) + Math.abs(par.y) > 0.01 || Math.abs(par.vx) + Math.abs(par.vy) > 0.1;
  if (parMoving) applyLayers();
  else if (par.x || par.y || par.vx || par.vy) {
    par.x = par.y = par.vx = par.vy = 0;
    applyLayers();
  }

  // Pointer position relative to the focused tile (mouse users)
  let px = 0;
  let py = 0;
  let pointerOn = false;
  const mouse = document.body.classList.contains('mouse-active');
  let busy = parMoving;

  for (const [tile, s] of tiles) {
    const focused = tile.classList.contains('focused') && tile.isConnected;
    s.focused = focused;
    let tx = 0;
    let ty = 0;
    if (focused) {
      if (mouse && pointer.over) {
        const r = tile.getBoundingClientRect();
        px = (pointer.x - r.left) / r.width - 0.5;
        py = (pointer.y - r.top) / r.height - 0.5;
        if (Math.abs(px) <= 0.5 && Math.abs(py) <= 0.5) {
          pointerOn = true;
          tx = px * 2 * 5;
          ty = py * 2 * 5;
        }
      }
    }
    // ax tilts around the vertical axis (left/right), ay around the horizontal one.
    [s.ax, s.vx] = spring(s.ax, s.vx, tx, dt);
    [s.ay, s.vy] = spring(s.ay, s.vy, ty, dt);
    const ax = Math.max(-TILT_MAX, Math.min(TILT_MAX, s.ax * skinTilt));
    const ay = Math.max(-TILT_MAX, Math.min(TILT_MAX, s.ay * skinTilt));
    const mag = Math.hypot(ax, ay);
    if (Math.abs(s.ax - tx) + Math.abs(s.ay - ty) > 0.02 || Math.abs(s.vx) + Math.abs(s.vy) > 0.05) busy = true;
    // rotateX(ay') then rotateY(ax') as one axis-angle: the axis is (-ay, ax, 0).
    tile.style.rotate = mag < 0.02 ? '' : `${(-ay).toFixed(3)} ${ax.toFixed(3)} 0 ${mag.toFixed(3)}deg`;
    if (s.glare) {
      // The reflection slides against the tilt, like light on a glossy surface.
      const gx = pointerOn && focused ? px * 60 : -ax * 7;
      const gy = pointerOn && focused ? py * 60 : ay * 7;
      s.glare.style.transform = `translate3d(${gx.toFixed(2)}%, ${gy.toFixed(2)}%, 0)`;
    }
    if (!focused && mag < 0.05 && Math.abs(s.vx) + Math.abs(s.vy) < 0.1) {
      clearTile(tile);
      tiles.delete(tile);
    }
  }
  pointer.moved = false;
  // Everything at rest: no more frames (and no JS writes) until the next impulse or pointer move.
  if (busy) start();
}

function readSkin() {
  const v = parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--tilt-scale'));
  skinTilt = Number.isFinite(v) ? v : 1;
}

export function startMotion() {
  remPx = parseFloat(getComputedStyle(document.documentElement).fontSize) || 20;
  window.addEventListener('resize', () => {
    remPx = parseFloat(getComputedStyle(document.documentElement).fontSize) || 20;
  });
  query?.addEventListener?.('change', evaluate);
  document.addEventListener('visibilitychange', () => {
    syncLive();
    if (document.hidden) stop();
    else start();
  });
  syncLive();
  // The skin's stylesheet arrives a moment after data-skin changes.
  new MutationObserver(() => setTimeout(readSkin, 200)).observe(document.documentElement, { attributes: true, attributeFilter: ['data-skin'] });
  document.addEventListener('pointermove', (e) => {
    pointer.x = e.clientX;
    pointer.y = e.clientY;
    pointer.moved = true;
    pointer.over = !!e.target.closest?.('.tile.focused');
    if (pointer.over) start();
  }, { passive: true });

  const view = $('#view');
  new MutationObserver((records) => {
    if (!enabled) return;
    for (const r of records) {
      const node = r.target;
      if (!node.classList?.contains('focused') || (r.oldValue || '').split(' ').includes('focused')) continue;
      if (node.matches(FOCUS_TARGETS)) onFocused(node);
    }
  }).observe(view, { subtree: true, attributes: true, attributeFilter: ['class'], attributeOldValue: true });
  // A rebuilt screen has new layers; the old direction reference is gone with its DOM.
  new MutationObserver(() => (layerNodes = null)).observe(view, { childList: true });
  evaluate();
  setTimeout(readSkin, 300);
}
