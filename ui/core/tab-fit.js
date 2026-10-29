// Pure rules for the tab bar (no DOM; tested by scripts/ui-logic-check.mjs): the width the tabs get, where a scrolling bar stops, the hints
// at its edges and the run of a label that does not fit. No rule hides a tab: a tab is shortened, run through or scrolled to, never dropped.
/** The default smallest width of a tab that has to shrink, in rem (a label stays readable); a skin may ask for another with --tab-min-width. */
export const TAB_MIN_REM = 6.5;
/** What a skin's --tab-min-width may be at least and at most, in rem: a skin can neither make tabs tiny nor push them out of the bar. */
export const TAB_MIN_RANGE = [4, 10];
/** The widest a tab gets, in rem: only a longer name is cut (an ellipsis, a run-through when focused). */
export const TAB_MAX_REM = 12;
/** The room of an overflow arrow at an edge of a scrolling bar, in rem. */
export const TAB_GUTTER_REM = 1.5;
/** A cut tab shows at least this much (rem) or none of it. */
export const TAB_STUB_REM = 2;
/** Pixels of rounding that count as "not cut" and as "nothing hidden" (naturals are rounded, the room is measured in fractions). */
export const TAB_EDGE_SLOP = 2;

/**
 * The smallest tab width in px: the skin's --tab-min-width (a number of rem or px, like "7rem" or "112px") clamped to the allowed range;
 * anything else (empty, text, NaN, a negative number, other units) is the default.
 */
export function minTabWidth(raw, remPx) {
  const rem = Number.isFinite(remPx) && remPx > 0 ? remPx : 16;
  const m = /^\s*(\d+(?:\.\d+)?)\s*(rem|px)\s*$/i.exec(String(raw ?? ''));
  let value = TAB_MIN_REM;
  if (m) value = m[2].toLowerCase() === 'rem' ? Number(m[1]) : Number(m[1]) / rem;
  if (!Number.isFinite(value)) value = TAB_MIN_REM;
  return Math.min(TAB_MIN_RANGE[1], Math.max(TAB_MIN_RANGE[0], value)) * rem;
}

const cleanWidth = (w) => (Number.isFinite(w) && w > 0 ? w : 0);

/**
 * The widths of the tabs in a bar `room` wide. Everything fits: nothing changes. Otherwise first the gap gives way (down to `minGap`),
 * then the wide tabs shrink to a common upper limit (water filling: the sum of min(natural, limit) is the room that is left, short tabs
 * keep their width), never below min(natural, minWidth). When even that does not fit the bar scrolls (`scroll`) and the tabs keep their
 * natural width: shrinking and scrolling never happen together. Only a name wider than `maxWidth` is shortened, in every case.
 * @param {number[]} natural the natural width of every tab
 * @param {number} room the width the tabs and the gaps between them may use
 * @param {number} gap the gap the stylesheet asks for
 * @param {number} minGap the smallest gap
 * @param {number} minWidth the smallest width of a tab that has to shrink
 * @param {number} [maxWidth] the widest a tab gets (default: no limit)
 * @param {number} [keep] the index of a tab that never shrinks (the focused one; -1: none)
 * @returns {{widths: number[], gap: number, scroll: boolean, content: number}} whole pixels; `content` is the width of all tabs and gaps
 */
export function fitWidths(natural, room, gap, minGap, minWidth, maxWidth = Infinity, keep = -1) {
  const cap = Number.isFinite(maxWidth) && maxWidth > 0 ? maxWidth : Infinity;
  const nat = (Array.isArray(natural) ? natural : []).map((w) => Math.min(cleanWidth(w), cap));
  const n = nat.length;
  const g = cleanWidth(gap);
  const total = nat.reduce((a, b) => a + b, 0);
  const done = (widths, usedGap, scroll) => ({ widths, gap: usedGap, scroll, content: widths.reduce((a, b) => a + b, 0) + usedGap * Math.max(0, n - 1) });
  if (!n) return { widths: [], gap: g, scroll: false, content: 0 };
  // No usable room (not measured yet): leave everything as it is.
  if (!Number.isFinite(room) || room <= 0) return done(nat, g, false);
  const tight = Math.min(g, cleanWidth(minGap));
  if (total + g * (n - 1) <= room) return done(nat, g, false);
  if (total + tight * (n - 1) <= room) return done(nat, n > 1 ? Math.min(g, Math.max(tight, (room - total) / (n - 1))) : g, false);
  const budget = room - tight * (n - 1);
  // The mode is decided from all tabs alone, never from the focus: a bar that scrolls does so whichever tab is focused.
  const floor = nat.map((w) => Math.min(w, cleanWidth(minWidth)));
  const floors = floor.reduce((a, b) => a + b, 0);
  // Not even at the minimum: scroll, with the tabs as wide as they are (a very long name is cut at the maximum).
  if (floors + tight * (n - 1) > room) return done(nat.map(Math.floor), g, true);
  const fill = (idx, space) => {
    // Water filling over the tabs in idx: one upper limit, the short ones stay, none below its floor.
    let lo = 0;
    let hi = Math.max(0, ...idx.map((k) => nat[k]));
    for (let i = 0; i < 48; i++) {
      const limit = (lo + hi) / 2;
      const sum = idx.reduce((acc, k) => acc + Math.min(nat[k], Math.max(limit, floor[k])), 0);
      if (sum > space) hi = limit; else lo = limit;
    }
    return idx.map((k) => Math.floor(Math.min(nat[k], Math.max(hi, floor[k]))));
  };
  const all = nat.map((_, k) => k);
  if (!Number.isInteger(keep) || keep < 0 || keep >= n) {
    return done(fill(all, budget), tight, false);
  }
  // The focused tab gets as much of what is left as it can use (up to its natural width, never below its floor); the others give way only
  // as far as they stay above theirs. The widths then depend on the focus only by what the focused tab takes.
  const others = all.filter((k) => k !== keep);
  const want = Math.floor(Math.min(nat[keep], Math.max(floor[keep], budget - others.reduce((acc, k) => acc + floor[k], 0))));
  const widths = [];
  widths[keep] = want;
  fill(others, budget - want).forEach((w, i) => { widths[others[i]] = w; });
  return done(widths, tight, false);
}

/**
 * Where a scrolling bar stands so that the focused tab is whole and the edge falls between two tabs: the first whole tab moves only as
 * far as it has to (to the focused tab when that is before it, forward until the focused one fits when it is behind). An arrow gutter
 * of `gutter` is kept at an edge only where tabs are hidden. The scroll is the left edge of the first tab minus the start gutter
 * (0 for the first tab), never more than the end of the content allows.
 * @param {{left: number, width: number}[]} boxes the tabs in the scrolled content, left to right
 * @param {number} region the width of the room for the tabs
 * @param {number} gutter the room of an arrow
 * @param {number} active the index of the focused tab (-1: none, the scroll stays)
 * @param {number} scroll the scroll now
 * @returns {number} the scroll to use
 */
export function tabWindow(boxes, region, gutter, active, scroll) {
  const ok = Array.isArray(boxes) && boxes.length > 0 && boxes.every((b) => Number.isFinite(b?.left) && Number.isFinite(b?.width)) && Number.isFinite(region) && region > 0 && Number.isFinite(gutter) && Number.isFinite(scroll);
  if (!ok) return Number.isFinite(scroll) ? Math.max(0, scroll) : 0;
  const n = boxes.length;
  const content = boxes[n - 1].left + boxes[n - 1].width;
  const max = Math.max(0, content - region);
  if (!Number.isInteger(active) || active < 0 || active >= n) return Math.min(max, Math.max(0, scroll));
  // A small room: the focused tab comes first, the arrows may lie over its edges (it is whole, never cut).
  const own = boxes[active].width;
  if (own + 2 * gutter > region) return Math.min(max, Math.max(0, boxes[active].left - Math.max(0, (region - own) / 2)));
  const startFor = (k) => (k === 0 ? 0 : boxes[k].left - gutter);
  // How far a window that starts at tab k reaches: the last tab that is whole inside it (an end gutter only when something is left over).
  const reach = (k) => {
    const from = boxes[k].left;
    const room = region - (k > 0 ? gutter : 0);
    let j = k;
    while (j + 1 < n && boxes[j + 1].left + boxes[j + 1].width - from <= room) j++;
    if (j === n - 1) return j;
    while (j > k && boxes[j].left + boxes[j].width - from > room - gutter) j--;
    return j;
  };
  let k = boxes.findIndex((b) => b.left >= scroll + (scroll > 0.5 ? gutter : 0) - 1);
  if (k < 0) k = n - 1;
  if (active < k) k = active;
  while (k < active && reach(k) < active) k++;
  return Math.min(max, Math.max(0, startFor(k)));
}

/** Which overflow hints are due: only on the side where tabs are hidden. */
export function overflowHints(scroll, region, content) {
  if (![scroll, region, content].every(Number.isFinite) || region <= 0) return { start: false, end: false };
  return { start: scroll > TAB_EDGE_SLOP, end: scroll + region < content - TAB_EDGE_SLOP };
}

/** Whether a label that does not fit its tab runs through (only with motion on). */
export const needsMarquee = (scrollWidth, clientWidth, motionOn) =>
  !!motionOn && Number.isFinite(scrollWidth) && Number.isFinite(clientWidth) && clientWidth > 0 && scrollWidth > clientWidth + 1;

/**
 * The scroll offset of a running label `elapsed` ms after the tab got the focus: a pause, a run to the end, a pause, a run back, then
 * again. `max` is how far the text overflows.
 */
export function marqueeOffset(elapsed, max, pause = 1000, speed = 0.05) {
  if (![elapsed, max, pause, speed].every(Number.isFinite) || max <= 0 || speed <= 0 || elapsed < 0) return 0;
  const run = max / speed;
  const t = elapsed % (2 * (pause + run));
  if (t < pause) return 0;
  if (t < pause + run) return (t - pause) * speed;
  if (t < 2 * pause + run) return max;
  return max - (t - 2 * pause - run) * speed;
}
