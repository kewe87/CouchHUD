// Battery levels for the guide menu (components/quick-menu.js): the PC's own battery (only where there is one) and the controllers.
// The host reads the hardware once a minute and pushes changes ("battery"); it also says "battery low" itself (a toast).

import { api } from './api.js';
import { state } from './state.js';

const hooks = [];

/** Something else shows batteries too (the guide menu): redraw it when the values change. */
export function onBatteryChanged(hook) {
  hooks.push(hook);
}

function accept(snapshot) {
  if (!snapshot || typeof snapshot !== 'object') return;
  state.battery = snapshot;
  for (const hook of hooks) hook();
}

/** Asks the host again (the guide menu opens): the answer is the last read, a fresh one follows as an event when something changed. */
export async function refreshBattery() {
  try {
    accept(await api.getBattery?.());
  } catch {
    // Locked or held: the top bar simply keeps what it had.
  }
}

export function startBattery() {
  api.onBattery?.(accept);
  refreshBattery();
}
