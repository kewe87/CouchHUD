// Pure rules for battery levels, without DOM (tested by scripts/ui-logic-check.mjs). The host reads the hardware and decides
// when to warn (BatteryMonitor.cs, LowBatteryLatch in Core); these only turn its values into what the top bar and the guide menu show.

/** At or below this percent a battery counts as low (the same as BatteryRules.LowPercent on the host). */
export const LOW_PERCENT = 15;

/** XInput has four slots; the host numbers the PlayStation pads it reads itself after them. */
const XINPUT_SLOTS = 4;

const PAD_STEPS = { empty: 0, low: 1, medium: 2, full: 3 };

/** A percent the host may send: a whole number from 0 to 100; anything else is not shown. */
export const validPercent = (p) => Number.isInteger(p) && p >= 0 && p <= 100;

/** The 4-step icon of an XInput level: 0 (empty) to 3 (full); null for wired, unknown and anything else (no icon). */
export const padSteps = (level) => (Object.hasOwn(PAD_STEPS, level) ? PAD_STEPS[level] : null);

/** The same 4 steps for a percent (Bluetooth devices): the lowest step is exactly the low range. */
export function percentSteps(p) {
  if (!validPercent(p)) return null;
  return p <= LOW_PERCENT ? 0 : p <= 40 ? 1 : p <= 70 ? 2 : 3;
}

/** Steps as three bars, like the controller's own battery icon ("▮▮▯"); nothing for null. */
export const bars = (steps) => (Number.isInteger(steps) && steps >= 0 && steps <= 3 ? '▮'.repeat(steps) + '▯'.repeat(3 - steps) : '');

/**
 * The PC's battery in the top bar: null on a desktop (no system battery) or with a value the host could not read.
 * On the charger it shows the plug, never as low.
 */
export function systemBadge(system) {
  if (!system || !validPercent(system.percent)) return null;
  const power = !!(system.charging || system.pluggedIn);
  return { percent: system.percent, charging: power, low: !power && (system.low === true || system.percent <= LOW_PERCENT), icon: power ? '⚡' : '🔋' };
}

/** A pad's level is a warning: low or empty. */
export const padLow = (level) => level === 'low' || level === 'empty';

/**
 * The guide menu's battery line: one entry per pad ("🎮 1 ▮▮▯", with a percent where the host knows one, wired "🎮 1 🔌", unknown just "🎮 1") and per Bluetooth device
 * with a percent (controllers first, as the host sorts them). Names stay plain text for textContent.
 */
export function batteryEntries(pads, devices) {
  const list = [];
  const valid = (Array.isArray(pads) ? pads : []).filter((p) => Number.isInteger(p?.index) && p.index >= 0);
  // XInput pads keep their player number; PlayStation pads read over HID (slots from XINPUT_SLOTS on) follow in their own order.
  const xinput = valid.filter((p) => p.index < XINPUT_SLOTS);
  const hid = valid.filter((p) => p.index >= XINPUT_SLOTS).sort((a, b) => a.index - b.index);
  const after = xinput.reduce((max, p) => Math.max(max, p.index + 1), 0);
  const labels = new Map(hid.map((p, k) => [p.index, after + k + 1]));
  for (const p of valid) {
    // getPads says "battery", the host's battery snapshot "level".
    const level = p.battery ?? p.level;
    const label = String(p.index < XINPUT_SLOTS ? p.index + 1 : labels.get(p.index));
    // An Xbox pad read through Windows.Gaming.Input also has a percent: bars from it, the warning still from the level (the host
    // never calls a charging pad low).
    if (level !== 'wired' && validPercent(p.percent)) {
      list.push({ key: `pad:${p.index}`, kind: 'controller', label, percent: p.percent, value: bars(percentSteps(p.percent)), low: padLow(level) });
      continue;
    }
    list.push({ key: `pad:${p.index}`, kind: 'controller', label, value: level === 'wired' ? '🔌' : bars(padSteps(level)), low: padLow(level) });
  }
  for (const d of Array.isArray(devices) ? devices : []) {
    if (!d || typeof d.name !== 'string' || !d.name || !validPercent(d.percent)) continue;
    list.push({ key: `bt:${d.id}`, kind: d.kind, label: d.name, percent: d.percent, value: bars(percentSteps(d.percent)), low: d.percent <= LOW_PERCENT });
  }
  return list;
}
