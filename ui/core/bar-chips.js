// The chips at the right of the tab bar (the bell, and the downloads chip while it is shown) as stops of LB/RB after the last tab.
// The order and the wrapping are the pure rules of tab-cycle.js; this module only reads the bar and acts.

import { $ } from './dom.js';
import { bar, clearStack, push, showChipFocus, switchTab, tabViews } from './nav.js';
import { barStops, chipsBlocked, nextStop } from './tab-cycle.js';
import { state } from './state.js';
import { sound } from '../sound.js';
import { notificationsPage } from '../views/notifications.js';
import { downloadsView } from '../views/downloads.js';

/** The chips that exist now: the bell always, the downloads chip while it is shown. */
export function chipIds() {
  const downloads = $('#downloads-status');
  return ['bell', ...(downloads && !downloads.hidden ? ['downloads'] : [])];
}

/** LB (-1) and RB (+1): the next tab or chip, wrapping around. */
export function cycleBar(delta) {
  // A chip that went away while it had the focus.
  if (bar.chip && !chipIds().includes(bar.chip)) bar.chip = null;
  const stops = barStops(tabViews().length, chipIds());
  const next = nextStop(stops, bar.chip ? { kind: 'chip', id: bar.chip } : { kind: 'tab', index: state.tab }, delta);
  if (!next) return;
  if (next.kind === 'tab') return switchTab(next.index);
  // Like a tab switch: pages opened over the tab close.
  clearStack();
  bar.chip = next.id;
  sound.tab();
  showChipFocus();
}

/** A on the focused chip, or a click on it: the bell opens the notifications, the downloads chip the Downloads tab. */
export function activateChip(id) {
  if (chipsBlocked(state)) return false;
  bar.chip = null;
  showChipFocus();
  if (id === 'bell') {
    push(notificationsPage());
    return true;
  }
  if (id === 'downloads') {
    const at = tabViews().indexOf(downloadsView);
    if (at >= 0) switchTab(at);
  }
  return true;
}

/** The chip's own buttons: A opens it, B (and left or down) gives the focus back to the tabs. Everything else waits. Returns true when handled. */
export function handleChip(action) {
  if (!bar.chip) return false;
  if (action === 'a') activateChip(bar.chip);
  else if (action === 'b' || action === 'down') {
    bar.chip = null;
    sound.back();
    showChipFocus();
  }
  return true;
}
