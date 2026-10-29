// Pure rules for ui/core/motion.js: which layers move with the parallax, how far, and which focus changes nudge it.
// Tested in scripts/ui-logic-check.mjs.

/** Strongest parallax position per axis (units); the spring is clamped to it. */
export const PARALLAX_CLAMP = 1.6;

/** Stiffness and damping of the parallax spring that brings the layers back to rest. */
export const PARALLAX_SPRING = { k: 70, d: 11 };

/** Overscan of the soft backdrop layers in rem (.bg-layer, .bg-video: inset -2rem in styles.css). */
export const BG_OVERSCAN_REM = 2;

// Parallax layers: [selector, x rem per unit, y rem per unit]. The backdrop moves against the focus
// direction, the foreground a bit with it. #bg-hero and .bg-shade move exactly back by #bg's amount:
// the sharp game picture and the screen shade stay put, so no edge of theirs can slide into the screen.
const BG_X = -0.85;
const BG_Y = -0.5;
export const LAYERS = [
  ['#bg', BG_X, BG_Y],
  ['#bg-hero', -BG_X, -BG_Y],
  ['#bg > .bg-shade', -BG_X, -BG_Y],
  ['.hero-info', 0.7, 0.3],
  ['.shelf-wrap', 0.35, 0.2]
];

/** Views on which focus changes never nudge the parallax: on the detail page the buttons move, the picture stays still. */
export const STILL_VIEWS = new Set(['detail']);

/**
 * Whether a focus change nudges the parallax layers.
 * view: body[data-view]; target: 'tile' | 'action' (the focused element's kind).
 */
export function nudgesParallax(view, target) {
  if (STILL_VIEWS.has(view)) return false;
  return target === 'tile';
}

/** Largest displacement of #bg in rem per axis at the clamp. */
export function maxBackdropShift() {
  return { x: PARALLAX_CLAMP * Math.abs(BG_X), y: PARALLAX_CLAMP * Math.abs(BG_Y) };
}

/**
 * Direction (-1/0/1 per axis) of a focus step from sibling index a to b, or null when it is no single step: the same element
 * (a refused move at an edge), an unknown one, or a jump (a wrap from the last tile to the first, a page). Only single steps nudge.
 * cols > 1: a grid with that many columns (a step is one to the side or one row up or down).
 */
export function stepDirection(a, b, cols = 0) {
  if (!(a >= 0) || !(b >= 0) || a === b) return null;
  const span = Math.abs(b - a);
  if (cols > 1) {
    if (span !== 1 && span !== cols) return null;
    return { x: Math.sign((b % cols) - (a % cols)), y: Math.sign(Math.floor(b / cols) - Math.floor(a / cols)) };
  }
  if (span !== 1) return null;
  return { x: Math.sign(b - a), y: 0 };
}

/** Velocity a single step gives the parallax spring (units per second). */
export const NUDGE = 16;
/** Displacement (units) at which further steps in the same direction add nothing: about the peak of one single step. */
export const NUDGE_ROOM = 0.8;

/**
 * The spring's velocity after a step in direction d (-1/0/1) on one axis, at position pos with velocity v. The push shrinks with
 * the room left in that direction, so fast or held scrolling never piles up to the clamp and the backdrop never swings back
 * from far out when the row stops at its end: it stays within about one step's swing.
 */
export function nudgeVelocity(v, pos, d) {
  if (!d) return v;
  const room = Math.max(0, Math.min(1, 1 - (d * pos) / NUDGE_ROOM));
  return v + d * NUDGE * room;
}
