// What the code prompt says when no controller can type the code: keyboard and mouse work too, and where Bluetooth stands.
// Pure (keys only, no DOM), checked by scripts/ui-logic-check.mjs.

/** The code buttons in the order of the on-screen row (same set as the controller and the keyboard). */
export const CODE_KEYS = ['a', 'b', 'x', 'y', 'lb', 'rb', 'up', 'down', 'left', 'right'];

/**
 * pads: number of connected controllers (null: not known), bt: the answer of getBluetooth() (null: not known).
 * Returns { noPad, bluetooth } where bluetooth is a text key or null. Nothing is said while the state is not known: a wrong
 * "no controller" would only confuse.
 */
export function padHelp(pads, bt) {
  if (pads !== 0) return { noPad: false, bluetooth: null };
  if (!bt || !bt.available) return { noPad: true, bluetooth: null };
  // i18n-used: passcode.bt.offAuto passcode.bt.off passcode.bt.onPaired
  if (!bt.on) return { noPad: true, bluetooth: bt.autoOn && bt.controllersPaired > 0 ? 'passcode.bt.offAuto' : 'passcode.bt.off' };
  return { noPad: true, bluetooth: bt.controllersPaired > 0 ? 'passcode.bt.onPaired' : null };
}

/**
 * Switching Bluetooth off cuts a Bluetooth controller off: ask first. Returns the text key of the warning, or null when nothing
 * needs to be said (no Bluetooth controller connected, or switching on).
 */
/**
 * A page where the code is typed (code prompt): every button is a code button, so the mouse wheel and the side buttons type nothing
 * and held arrows do not repeat (one press, one code step).
 */
export const isCodeEntry = (view) => view?.codeEntry === true;

/**
 * Pages that no dialog may cover: the code prompt (a dialog over it would take the next button press as an answer) and the
 * parental time-up page. The Bluetooth countdown waits behind them and shows up once they are closed.
 */
export const holdsDialogs = (view) => isCodeEntry(view) || view?.id === 'parental-timeup';

export function switchOffWarning(bt, turningOn) {
  if (turningOn || !bt?.controllerConnected) return null;
  // i18n-used: bluetooth.offWarnAuto bluetooth.offWarn
  return bt.autoOn ? 'bluetooth.offWarnAuto' : 'bluetooth.offWarn';
}
