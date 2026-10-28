// Turns controller, keyboard and (fallback) browser gamepad input into one
// stream of navigation actions, including key repeat for held directions.

import { kindFromGamepadId, kindFromPresses } from './core/pad-glyphs.js';

const REPEAT_DELAY = 380;
const REPEAT_RATE = 90;
const REPEATING = new Set(['up', 'down', 'left', 'right']);

const KEYMAP = {
  ArrowUp: 'up',
  ArrowDown: 'down',
  ArrowLeft: 'left',
  ArrowRight: 'right',
  Enter: 'a',
  ' ': 'a',
  Escape: 'b',
  Backspace: 'b',
  KeyX: 'x',
  KeyY: 'y',
  KeyF: 'y',
  KeyQ: 'lb',
  KeyE: 'rb',
  PageUp: 'lb',
  PageDown: 'rb',
  KeyM: 'start',
  Home: 'guide'
};

// Standard Gamepad mapping indices.
const GAMEPAD_BUTTONS = ['a', 'b', 'x', 'y', 'lb', 'rb', 'lt', 'rt', 'back', 'start', 'ls', 'rs', 'up', 'down', 'left', 'right', 'guide'];

export class Input {
  constructor(onAction) {
    this.onAction = onAction;
    this.timers = new Map();
    this.held = new Set();
    // False: held arrows give one step only (the code prompt: one press, one code step).
    this.repeatAllowed = () => true;
  }

  /** source: 'pad' or 'keyboard', so hints can show the right buttons. */
  press(button, source = 'pad') {
    if (this.held.has(button)) return;
    this.held.add(button);
    this.onAction(button, source);
    if (REPEATING.has(button) && this.repeatAllowed()) {
      const timer = setTimeout(() => {
        const interval = setInterval(() => this.onAction(button, source), REPEAT_RATE);
        this.timers.set(button, interval);
      }, REPEAT_DELAY);
      this.timers.set(button, timer);
    }
  }

  release(button) {
    this.held.delete(button);
    const t = this.timers.get(button);
    if (t !== undefined) {
      clearTimeout(t);
      clearInterval(t);
      this.timers.delete(button);
    }
  }

  releaseAll() {
    for (const b of [...this.held]) this.release(b);
  }

  attachNative(api) {
    api.onInput(({ type, button }) => (type === 'down' ? this.press(button) : this.release(button)));
  }

  attachKeyboard() {
    window.addEventListener('keydown', (e) => {
      const action = KEYMAP[e.key] || KEYMAP[e.code];
      if (!action) return;
      e.preventDefault();
      if (!e.repeat) this.press(action, 'keyboard');
    });
    window.addEventListener('keyup', (e) => {
      const action = KEYMAP[e.key] || KEYMAP[e.code];
      if (action) this.release(action);
    });
    window.addEventListener('blur', () => this.releaseAll());
  }

  /**
   * Browser Gamepad API, only used when the main process has no input of its own (the preview). Like the host's PadMerger, all
   * pads count as one: a button is down while any pad holds it, so a pad and its virtual mirror (Steam Input, DS4Windows) press
   * once. onKind hears the kind of the pad with a new press ("xbox" or "playstation") for the button symbols.
   */
  attachGamepadApi(onKind = () => {}) {
    const prev = new Map();
    let union = new Set();
    let lastPlayStationAt = -Infinity;
    const loop = () => {
      const next = new Set();
      const kinds = [];
      for (const gp of navigator.getGamepads ? navigator.getGamepads() : []) {
        if (!gp) continue;
        const now = new Set();
        gp.buttons.forEach((b, i) => b.pressed && GAMEPAD_BUTTONS[i] && now.add(GAMEPAD_BUTTONS[i]));
        const [lx = 0, ly = 0] = gp.axes;
        if (ly < -0.5) now.add('up');
        if (ly > 0.5) now.add('down');
        if (lx < -0.5) now.add('left');
        if (lx > 0.5) now.add('right');
        const before = prev.get(gp.index) || new Set();
        if ([...now].some((b) => !before.has(b))) kinds.push(kindFromGamepadId(gp.id));
        prev.set(gp.index, now);
        for (const b of now) next.add(b);
      }
      // One decision per frame: a PlayStation pad and its mirror pressing together never flip the symbols back and forth.
      const told = kindFromPresses(kinds, performance.now(), lastPlayStationAt);
      lastPlayStationAt = told.lastPlayStationAt;
      if (told.kind) onKind(told.kind);
      for (const b of next) if (!union.has(b)) this.press(b);
      for (const b of union) if (!next.has(b)) this.release(b);
      union = next;
      requestAnimationFrame(loop);
    };
    requestAnimationFrame(loop);
  }
}
