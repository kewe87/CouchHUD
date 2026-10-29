// Which button symbols the screen shows, without DOM (tested by scripts/ui-logic-check.mjs). The controller in use decides:
// the host tells the kind of pad pressed last ("xbox" or "playstation"; a generic pad counts as Xbox), the phone and the keyboard
// never change it. Experts can fix it in Settings › Controller. Skins keep their look; a skin's own glyph labels are styling for
// the kind of pad they belong to: PlayStation labels (✕ ○ □ △, L1, OPTIONS …) only show with a PlayStation pad, all others only
// with an Xbox pad. So a skin with ✕ ○ □ △ labels shows them with a PlayStation pad and A B X Y with an Xbox pad.

export const PAD_GLYPH_SETTINGS = ['auto', 'xbox', 'playstation'];

/** The PlayStation names of the hint buttons; anything not listed stays as it is (arrows, keyboard keys). */
export const PLAYSTATION_GLYPHS = Object.freeze({
  A: '✕', B: '○', X: '□', Y: '△', LB: 'L1', RB: 'R1', LT: 'L2', RT: 'R2', LS: 'L3', RS: 'R3',
  '≡': 'OPTIONS', View: 'CREATE', Guide: 'PS'
});

// Labels that belong to PlayStation pads (also the older names a skin may use).
const PLAYSTATION_LABELS = new Set([...Object.values(PLAYSTATION_GLYPHS), '×', '✖', '◯', '⭘', '■', '▢', '▲', 'SHARE', 'OPTIONS', 'Options', 'Share', 'Create']);

/** The kind of symbols to show: the setting when it is fixed, else the pad used last (anything unknown is Xbox). */
export function glyphKind(setting, padKind) {
  if (setting === 'xbox' || setting === 'playstation') return setting;
  return padKind === 'playstation' ? 'playstation' : 'xbox';
}

/** Whether a label belongs to PlayStation pads. */
export const isPlayStationLabel = (label) => PLAYSTATION_LABELS.has(String(label ?? '').trim());

/**
 * The text of a button for a kind of pad: a skin's label for that button when it belongs to this kind, else the kind's own name
 * (PlayStation) or the button itself (Xbox).
 */
export function glyphFor(btn, kind, skinGlyphs, skin = null) {
  // A list made for exactly this kind of pad (skin.json glyphsXbox / glyphsPlayStation) is taken as it is; "glyphs" is the fallback.
  const family = skin?.[kind === 'playstation' ? 'glyphsPlayStation' : 'glyphsXbox'];
  if (family && Object.hasOwn(family, btn) && typeof family[btn] === 'string') return family[btn];
  const own = skinGlyphs && Object.hasOwn(skinGlyphs, btn) && typeof skinGlyphs[btn] === 'string' ? skinGlyphs[btn] : null;
  const playstation = kind === 'playstation';
  if (own !== null && isPlayStationLabel(own) === playstation) return own;
  if (playstation && Object.hasOwn(PLAYSTATION_GLYPHS, btn)) return PLAYSTATION_GLYPHS[btn];
  return btn;
}

/**
 * The kind of a pad the browser's Gamepad API sees (only used without the host's own input): Sony's vendor id or a known name
 * means PlayStation, everything else Xbox.
 */
export function kindFromGamepadId(id) {
  return /054c|dualsense|dualshock|playstation/i.test(String(id ?? '')) ? 'playstation' : 'xbox';
}

/** How long a PlayStation press keeps an Xbox press from switching the symbols: a PS pad and its mirror press together. */
export const MIRROR_WINDOW_MS = 250;

/**
 * The kind of pad one frame's new presses tell (null: nothing to change), and when a PlayStation pad pressed last. Within a frame
 * and within MIRROR_WINDOW_MS of a PlayStation press, an Xbox press is likely the mirror (Steam Input, DS4Windows): PlayStation wins.
 */
export function kindFromPresses(kinds, now, lastPlayStationAt = -Infinity) {
  if (kinds.includes('playstation')) return { kind: 'playstation', lastPlayStationAt: now };
  if (kinds.includes('xbox') && now - lastPlayStationAt > MIRROR_WINDOW_MS) return { kind: 'xbox', lastPlayStationAt };
  return { kind: null, lastPlayStationAt };
}
