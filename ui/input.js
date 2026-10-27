// Turns controller, keyboard and (fallback) browser gamepad input into one
// stream of navigation actions, including key repeat for held directions.

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

  /** Browser Gamepad API, only used when the main process can't read XInput. */
  attachGamepadApi() {
    const prev = new Map();
    const loop = () => {
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
        for (const b of now) if (!before.has(b)) this.press(b);
        for (const b of before) if (!now.has(b)) this.release(b);
        prev.set(gp.index, now);
      }
      requestAnimationFrame(loop);
    };
    requestAnimationFrame(loop);
  }
}
