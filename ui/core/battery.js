// Battery levels in the top bar: the PC's own battery next to the clock (only where there is one) and a small controller icon while a
// controller runs low. The host reads the hardware once a minute and pushes changes ("battery"); it also says "battery low" itself.

import { api } from './api.js';
import { $ } from './dom.js';
import { fmtPercent, t } from './i18n.js';
import { state } from './state.js';
import { lowControllerBadge, systemBadge } from './battery-rules.js';

const hooks = [];

/** Something else shows batteries too (the guide menu): redraw it when the values change. */
export function onBatteryChanged(hook) {
  hooks.push(hook);
}

function show() {
  const node = $('#battery-status');
  if (node) {
    const badge = systemBadge(state.battery?.system);
    node.hidden = !badge;
    node.classList.toggle('low', !!badge?.low);
    node.textContent = badge ? `${badge.icon} ${fmtPercent(badge.percent)}` : '';
    node.title = badge ? (badge.charging ? t('battery.charging') : badge.low ? t('battery.low') : t('battery.system')) : '';
  }
  const pad = $('#pad-battery');
  if (pad) {
    const low = lowControllerBadge(state.battery);
    pad.hidden = !low;
    pad.textContent = low ? `${low.icon} ${low.value}` : '';
    pad.title = low ? t('battery.padLow') : '';
  }
}

function accept(snapshot) {
  if (!snapshot || typeof snapshot !== 'object') return;
  state.battery = snapshot;
  show();
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
