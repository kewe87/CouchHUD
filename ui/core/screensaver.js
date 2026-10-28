// Screensaver and burn-in protection for OLED TVs: after a few idle minutes the HUD
// gives way to a slow artwork slideshow, a wandering clock, a dimmed screen or black,
// and after a while longer the TV panel can go off. Pixel shift nudges the whole HUD
// a few pixels now and then so the header and hints never sit on the same pixels.

import { api } from './api.js';
import { fmtDate, fmtTime, t } from './i18n.js';
import { el } from './dom.js';
import { hue } from './art.js';
import { state } from './state.js';
import { setMotionActive } from './motion.js';
import { holding } from './browser-logic.js';

const options = { minutes: 5, style: 'art', displayOff: 0, pixelShift: true };
let lastInput = Date.now();
// The last time someone really did something (a hold never moves it): a hold ends after HOLD_LIMIT without that.
let lastRealInput = Date.now();
let wasHeld = false;
let focused = true;
let overlay = null;
let startedAt = 0;
let slideTimer = null;
let moveTimer = null;
let displayOffDone = false;
/** Callers that keep the screensaver away while they return true (a web page playing a video: nobody presses a button then). */
const holds = [];

export const SCREENSAVER_STYLES = [
  { get label() { return t('screensaver.art.label'); }, get detail() { return t('screensaver.art.detail'); }, value: 'art' },
  { get label() { return t('screensaver.clock.label'); }, get detail() { return t('screensaver.clock.detail'); }, value: 'clock' },
  { get label() { return t('screensaver.dim.label'); }, get detail() { return t('screensaver.dim.detail'); }, value: 'dim' },
  { get label() { return t('screensaver.black.label'); }, get detail() { return t('screensaver.black.detail'); }, value: 'black' }
];

export function configureScreensaver({ minutes, style, displayOff, pixelShift } = {}) {
  if (minutes !== undefined) options.minutes = minutes;
  if (style !== undefined) options.style = style;
  if (displayOff !== undefined) options.displayOff = displayOff;
  if (pixelShift !== undefined) {
    options.pixelShift = pixelShift;
    if (!pixelShift) shift(0, 0);
  }
}

/** Something else (a game) in front: no screensaver, the idle time starts fresh afterwards. */
export function setScreensaverFocus(active) {
  focused = active;
  lastInput = Date.now();
  if (!active) stopScreensaver();
}

/** Keeps the screensaver away while <fn> returns true, at most HOLD_LIMIT after the last input; the idle time starts fresh once it ends. */
export function holdScreensaver(fn) {
  holds.push(fn);
}

const held = () => holds.some((fn) => {
  try {
    return !!fn();
  } catch {
    return false;
  }
});

/** Someone is there, though the HUD saw no button (the controller on a web page): counts as input. */
export function screensaverActivity() {
  lastInput = lastRealInput = Date.now();
}

/**
 * Called for every input. Returns true if the input only woke the screen
 * (it must not also move the focus or launch something).
 */
export function screensaverInput() {
  lastInput = lastRealInput = Date.now();
  if (!overlay) return false;
  stopScreensaver();
  return true;
}

export function startScreensaver(style = options.style) {
  if (overlay) return;
  startedAt = Date.now();
  displayOffDone = false;
  setMotionActive(false, 'screensaver');
  overlay = el('div', { id: 'screensaver', className: `saver-style-${style}` });
  document.body.append(overlay);
  if (style === 'art') startSlides();
  if (style === 'clock' || style === 'art') startClock(style === 'art');
  // Fade in on the next frame so the transition runs.
  requestAnimationFrame(() => overlay?.classList.add('on'));
}

const stopHooks = [];

/** Runs <fn> each time the screensaver goes away (something waits for the screen to be seen again). */
export function onScreensaverStop(fn) {
  stopHooks.push(fn);
}

/** The screensaver covers the HUD right now. */
export const screensaverShowing = () => !!overlay;

export function stopScreensaver() {
  if (!overlay) return;
  setTimeout(() => {
    for (const fn of stopHooks) {
      try {
        fn();
      } catch (err) {
        console.error(err);
      }
    }
  }, 0);
  // The panel may have been switched off; whatever woke us, it is on again now.
  if (displayOffDone) api.displayOn?.().catch(() => {});
  clearInterval(slideTimer);
  clearInterval(moveTimer);
  const node = overlay;
  overlay = null;
  setMotionActive(true, 'screensaver');
  node.classList.remove('on');
  setTimeout(() => node.remove(), 400);
}

/** Library artwork, favorites and recently played first. */
function pictures() {
  const withArt = state.library.filter((i) => !i.hidden && (i.art?.hero?.length || i.art?.cover?.length));
  withArt.sort((a, b) => Number(b.favorite) - Number(a.favorite) || (b.lastPlayed ?? 0) - (a.lastPlayed ?? 0));
  return withArt.length ? withArt : state.library.slice(0, 12);
}

function startSlides() {
  const items = pictures();
  let i = 0;
  const show = () => {
    if (!overlay || !items.length) return;
    const item = items[i++ % items.length];
    const url = item.art?.hero?.[0] ?? item.art?.cover?.[0];
    const slide = el('div', { className: 'saver-slide' });
    slide.style.setProperty('--h', hue(item.name));
    if (url) slide.style.backgroundImage = `url("${url}")`;
    // Each slide drifts in a different direction (Ken Burns).
    slide.style.setProperty('--dx', `${(Math.random() * 6 - 3).toFixed(1)}%`);
    slide.style.setProperty('--dy', `${(Math.random() * 4 - 2).toFixed(1)}%`);
    overlay.prepend(slide);
    requestAnimationFrame(() => slide.classList.add('visible'));
    const old = [...overlay.querySelectorAll('.saver-slide')].slice(1);
    old.forEach((n) => n.classList.remove('visible'));
    setTimeout(() => old.forEach((n) => n.remove()), 3000);
  };
  show();
  slideTimer = setInterval(show, 20_000);
}

function startClock(small) {
  const clock = el('div', { className: `saver-clock${small ? ' small' : ''}` },
    el('strong'), el('span'));
  overlay.append(clock);
  const tick = () => {
    const now = new Date();
    clock.firstChild.textContent = fmtTime(now);
    clock.lastChild.textContent = fmtDate(now, { weekday: 'long', day: 'numeric', month: 'long' });
  };
  // A new place every minute, never twice on the same pixels.
  const place = () => {
    clock.style.left = `${8 + Math.random() * 64}%`;
    clock.style.top = `${10 + Math.random() * 62}%`;
  };
  tick();
  place();
  moveTimer = setInterval(() => {
    tick();
    if (new Date().getSeconds() < 15) place();
  }, 15_000);
}

function shift(x, y) {
  document.documentElement.style.setProperty('--shift-x', `${x}px`);
  document.documentElement.style.setProperty('--shift-y', `${y}px`);
}

/** Nothing watches a video or plays a trailer with sound on purpose? Then idle counts. */
function busy() {
  return [...document.querySelectorAll('video')].some((v) => !v.paused && !v.muted);
}

export function startScreensaverWatch() {
  const activity = (e) => {
    // A key that wakes the screen does nothing else.
    if (screensaverInput() && e.type === 'keydown') {
      e.preventDefault();
      e.stopImmediatePropagation();
    }
  };
  for (const type of ['pointermove', 'pointerdown', 'wheel', 'keydown']) window.addEventListener(type, activity, { passive: false, capture: true });

  setInterval(() => {
    const hold = holding(held(), Date.now(), lastRealInput);
    if (hold) wasHeld = true;
    else if (wasHeld) {
      // The video ended (or the limit passed): the normal idle time from here.
      wasHeld = false;
      lastInput = Date.now();
    }
    const idleMinutes = (Date.now() - lastInput) / 60_000;
    if (!overlay && !hold && options.minutes > 0 && focused && !busy() && idleMinutes >= options.minutes) {
      startScreensaver();
      // Nobody at the couch: a protected profile goes back to the profile picker (the host decides, a preview of the style does not count).
      api.profileIdle?.().catch(() => {});
    }
    if (overlay && options.displayOff > 0 && !displayOffDone && (Date.now() - startedAt) / 60_000 >= options.displayOff) {
      displayOffDone = true;
      api.displayOff?.().catch(() => {});
    }
  }, 10_000);

  // Pixel shift: a few pixels every three minutes, back and forth around the center.
  setInterval(() => {
    if (!options.pixelShift) return;
    shift(Math.round(Math.random() * 8 - 4), Math.round(Math.random() * 6 - 3));
  }, 3 * 60_000);
}
