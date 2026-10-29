// LB/RB walk through the tabs and then on to the chips at the right of the bar (the bell, and the downloads chip while it is shown), and
// wrap around. Pure, no DOM (tested by scripts/ui-logic-check.mjs).

/** The stops of the bar in order: every tab, then the chips that exist. */
export function barStops(tabCount, chips = []) {
  const stops = [];
  for (let i = 0; i < tabCount; i++) stops.push({ kind: 'tab', index: i });
  for (const id of chips) stops.push({ kind: 'chip', id });
  return stops;
}

const same = (a, b) => a.kind === b.kind && (a.kind === 'tab' ? a.index === b.index : a.id === b.id);

/** The stop `delta` steps from `current` (a tab index or a chip id), wrapping at both ends; the first stop when the current one is not there any more. */
export function nextStop(stops, current, delta) {
  if (!stops.length) return null;
  const at = stops.findIndex((s) => same(s, current));
  if (at < 0) return stops[0];
  return stops[(at + delta + stops.length * 4) % stops.length];
}

/** The bar's chips do nothing while a dialog is open or the profile picker holds the screen (no tab switch under a dialog). */
export const chipsBlocked = (state) => !!state?.overlay || !!state?.pickerHold;
