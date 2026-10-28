// UI sounds, synthesized live with Web Audio: no files needed, and every skin
// gets its own sound set. Skins can also bring their own files (skin.json "sounds").

import { t } from './core/i18n.js';
import { migrateSkinId } from './core/skin-rules.js';

let ctx = null;
let master = null;
let reverb = null;
let crusher = null;
let enabled = true;
let volume = 0.7;
let haptics = null;
let activeSet = 'default';
let forcedSet = null;
let customFiles = null;
const buffers = new Map();

/** Called with "select" / "bump" so the host can vibrate the controller. */
export function setHaptics(fn) {
  haptics = fn;
}

/** Kept for old callers: on means UI sounds, off means none. */
export function setSoundEnabled(on) {
  setAudioMode(on ? (audioMode === 'off' ? 'ui' : audioMode) : 'off');
}

/** 0..1 */
export function setSoundVolume(v) {
  volume = Math.max(0, Math.min(1, v));
  if (master) master.gain.value = volume;
}

/** The skin's set ("default", "deepsea", …) and optional files from a user skin: { move: url, … }. */
export function setSkinSounds(skinId, files = null) {
  skinId = migrateSkinId(skinId);
  activeSet = SETS[skinId] ? skinId : 'default';
  customFiles = files && Object.keys(files).length ? files : null;
  updateMusic();
}

/** null follows the skin; a set id uses that set for every skin. */
export function setSoundSet(id) {
  id = migrateSkinId(id);
  forcedSet = id && SETS[id] ? id : null;
  updateMusic();
}

export const SOUND_SETS = [
  { id: 'default', get label() { return t('sound.default'); } },
  { id: 'deepsea', get label() { return t('sound.deepsea'); } },
  { id: 'greenlight', get label() { return t('sound.greenlight'); } },
  { id: 'neoncity', get label() { return t('sound.neoncity'); } },
  { id: 'nova', get label() { return t('sound.nova'); } }
];

// ---------------------------------------------------------------------------
// Audio graph: voices → (dry | reverb | crusher) → master → speakers

function audio() {
  if (ctx) return ctx;
  ctx = new AudioContext();
  master = ctx.createGain();
  master.gain.value = volume;
  master.connect(ctx.destination);

  // A short synthetic room: decaying noise as impulse response.
  reverb = ctx.createConvolver();
  const length = Math.floor(ctx.sampleRate * 1.6);
  const impulse = ctx.createBuffer(2, length, ctx.sampleRate);
  for (let ch = 0; ch < 2; ch++) {
    const data = impulse.getChannelData(ch);
    for (let i = 0; i < length; i++) data[i] = (Math.random() * 2 - 1) * Math.pow(1 - i / length, 3.2);
  }
  reverb.buffer = impulse;
  const wet = ctx.createGain();
  wet.gain.value = 0.35;
  reverb.connect(wet).connect(master);

  // Bitcrusher look-alike: a stepped transfer curve, the 8-bit grit of the neoncity set.
  crusher = ctx.createWaveShaper();
  const curve = new Float32Array(1024);
  const steps = 10;
  for (let i = 0; i < curve.length; i++) {
    const x = (i / (curve.length - 1)) * 2 - 1;
    curve[i] = Math.round(x * steps) / steps;
  }
  crusher.curve = curve;
  crusher.connect(master);
  return ctx;
}

/**
 * One voice. f: frequency, d: duration, g: gain, w: wave, s: pitch slide factor,
 * t: delay, fx: "reverb" | "crush" | undefined, noise: true for a noise burst,
 * hp/lp: filter cutoffs, a: attack.
 */
function voice({ f = 440, d = 0.1, g = 0.05, w = 'sine', s = 0, t = 0, fx, noise = false, hp, lp, a = 0.004 }) {
  const c = audio();
  const start = c.currentTime + t;
  const amp = c.createGain();
  amp.gain.setValueAtTime(0.0001, start);
  amp.gain.exponentialRampToValueAtTime(g, start + a);
  amp.gain.exponentialRampToValueAtTime(0.0001, start + d);

  let source;
  if (noise) {
    const buffer = c.createBuffer(1, Math.ceil(c.sampleRate * d), c.sampleRate);
    const data = buffer.getChannelData(0);
    for (let i = 0; i < data.length; i++) data[i] = Math.random() * 2 - 1;
    source = c.createBufferSource();
    source.buffer = buffer;
  } else {
    source = c.createOscillator();
    source.type = w;
    source.frequency.setValueAtTime(f, start);
    if (s) source.frequency.exponentialRampToValueAtTime(f * s, start + d);
  }

  let node = source;
  if (hp) {
    const filter = c.createBiquadFilter();
    filter.type = 'highpass';
    filter.frequency.value = hp;
    node = node.connect(filter);
  }
  if (lp) {
    const filter = c.createBiquadFilter();
    filter.type = 'lowpass';
    filter.frequency.value = lp;
    node = node.connect(filter);
  }
  node.connect(amp);
  amp.connect(fx === 'crush' ? crusher : master);
  if (fx === 'reverb') amp.connect(reverb);
  source.start(start);
  source.stop(start + d + 0.05);
}

// ---------------------------------------------------------------------------
// Sound sets: each event is a list of voices.

const SETS = {
  default: {
    move: [{ f: 1320, d: 0.04, g: 0.018 }],
    tab: [{ f: 880, d: 0.07, g: 0.03, w: 'triangle' }, { f: 1320, d: 0.06, g: 0.015, t: 0.03 }],
    select: [{ f: 660, d: 0.12, g: 0.05, w: 'triangle' }, { f: 990, d: 0.16, g: 0.04, w: 'triangle', t: 0.05 }],
    back: [{ f: 660, d: 0.1, g: 0.035, w: 'triangle', s: 0.66 }],
    bump: [{ f: 140, d: 0.06, g: 0.025, w: 'square', lp: 900 }],
    open: [{ f: 520, d: 0.18, g: 0.03, s: 1.6 }, { noise: true, d: 0.18, g: 0.01, hp: 2500 }],
    close: [{ f: 830, d: 0.14, g: 0.025, s: 0.6 }],
    notify: [{ f: 988, d: 0.18, g: 0.04 }, { f: 1319, d: 0.28, g: 0.035, t: 0.1 }],
    toggle: [{ f: 1046, d: 0.05, g: 0.03, w: 'triangle' }],
    boot: [{ f: 392, d: 0.6, g: 0.04 }, { f: 587, d: 0.6, g: 0.035, t: 0.12 }, { f: 784, d: 0.9, g: 0.03, t: 0.24 }],
    // Two short clicks of a camera shutter.
    shutter: [{ noise: true, d: 0.03, g: 0.05, hp: 2500 }, { noise: true, d: 0.05, g: 0.04, hp: 1600, t: 0.08 }]
  },
  // Soft bells with a room, like a classic console menu.
  deepsea: {
    move: [{ f: 1568, d: 0.08, g: 0.014, fx: 'reverb' }],
    tab: [{ f: 1175, d: 0.16, g: 0.022, fx: 'reverb' }],
    select: [{ f: 1047, d: 0.35, g: 0.035, fx: 'reverb' }, { f: 2094, d: 0.25, g: 0.01, fx: 'reverb' }],
    back: [{ f: 784, d: 0.22, g: 0.025, fx: 'reverb', s: 0.8 }],
    bump: [{ f: 262, d: 0.08, g: 0.02, lp: 700 }],
    open: [{ noise: true, d: 0.4, g: 0.02, lp: 1800, a: 0.12, fx: 'reverb' }, { f: 1319, d: 0.4, g: 0.02, t: 0.12, fx: 'reverb' }],
    close: [{ noise: true, d: 0.3, g: 0.015, lp: 1200, fx: 'reverb' }],
    notify: [{ f: 1319, d: 0.5, g: 0.035, fx: 'reverb' }, { f: 1760, d: 0.6, g: 0.03, t: 0.15, fx: 'reverb' }],
    toggle: [{ f: 1568, d: 0.12, g: 0.02, fx: 'reverb' }],
    boot: [{ f: 523, d: 1.4, g: 0.03, fx: 'reverb', a: 0.2 }, { f: 784, d: 1.4, g: 0.025, t: 0.3, fx: 'reverb', a: 0.2 }, { f: 1047, d: 1.6, g: 0.02, t: 0.6, fx: 'reverb', a: 0.2 }]
  },
  // Short, crisp clicks and a punchy confirm.
  greenlight: {
    move: [{ noise: true, d: 0.025, g: 0.03, hp: 3500 }, { f: 1800, d: 0.02, g: 0.012, w: 'triangle' }],
    tab: [{ noise: true, d: 0.09, g: 0.025, hp: 1500, lp: 6000 }],
    select: [{ f: 220, d: 0.12, g: 0.07, s: 0.5 }, { noise: true, d: 0.03, g: 0.04, hp: 2500 }],
    back: [{ f: 330, d: 0.09, g: 0.04, w: 'triangle', s: 0.7 }],
    bump: [{ f: 90, d: 0.07, g: 0.05, w: 'square', lp: 400 }],
    open: [{ noise: true, d: 0.2, g: 0.03, hp: 800, lp: 5000, a: 0.08 }, { f: 160, d: 0.15, g: 0.04, s: 1.5 }],
    close: [{ noise: true, d: 0.14, g: 0.025, hp: 800, lp: 4000 }],
    notify: [{ f: 880, d: 0.1, g: 0.04, w: 'triangle' }, { f: 1175, d: 0.16, g: 0.04, w: 'triangle', t: 0.08 }],
    toggle: [{ noise: true, d: 0.03, g: 0.035, hp: 3000 }],
    boot: [{ f: 110, d: 1.0, g: 0.06, s: 2 }, { noise: true, d: 0.9, g: 0.02, hp: 600, a: 0.4 }]
  },
  // Glitchy arps through the bitcrusher.
  neoncity: {
    move: [{ f: 1760, d: 0.03, g: 0.02, w: 'square', fx: 'crush' }],
    tab: [{ f: 880, d: 0.03, g: 0.025, w: 'square', fx: 'crush' }, { f: 1320, d: 0.03, g: 0.025, w: 'square', t: 0.035, fx: 'crush' }],
    select: [440, 660, 880, 1320].map((f, i) => ({ f, d: 0.05, g: 0.03, w: 'sawtooth', t: i * 0.035, fx: 'crush', lp: 5000 })),
    back: [{ f: 880, d: 0.12, g: 0.03, w: 'sawtooth', s: 0.4, fx: 'crush', lp: 4000 }],
    bump: [{ f: 70, d: 0.09, g: 0.06, w: 'sawtooth', fx: 'crush' }, { noise: true, d: 0.05, g: 0.03, fx: 'crush' }],
    open: [{ f: 220, d: 0.2, g: 0.03, w: 'sawtooth', s: 4, fx: 'crush', lp: 3000 }, { noise: true, d: 0.08, g: 0.02, hp: 4000, t: 0.12 }],
    close: [{ f: 880, d: 0.18, g: 0.03, w: 'sawtooth', s: 0.25, fx: 'crush', lp: 3000 }],
    notify: [{ f: 1046, d: 0.08, g: 0.035, w: 'square', fx: 'crush' }, { f: 784, d: 0.08, g: 0.035, w: 'square', t: 0.1, fx: 'crush' }, { f: 1046, d: 0.12, g: 0.035, w: 'square', t: 0.2, fx: 'crush' }],
    toggle: [{ f: 1320, d: 0.04, g: 0.025, w: 'square', fx: 'crush' }],
    boot: [{ f: 55, d: 1.2, g: 0.07, w: 'sawtooth', s: 4, fx: 'crush', lp: 2500 }, { noise: true, d: 0.3, g: 0.03, t: 0.9, hp: 3000 }]
  },
  // Glassy chimes, a fifth apart, with a long tail.
  nova: {
    move: [{ f: 2093, d: 0.12, g: 0.012, fx: 'reverb' }],
    tab: [{ f: 1568, d: 0.25, g: 0.018, fx: 'reverb' }, { f: 2349, d: 0.25, g: 0.01, t: 0.04, fx: 'reverb' }],
    select: [1047, 1568, 2093].map((f, i) => ({ f, d: 0.45, g: 0.025, t: i * 0.05, fx: 'reverb' })),
    back: [{ f: 1568, d: 0.3, g: 0.02, s: 0.75, fx: 'reverb' }],
    bump: [{ f: 220, d: 0.1, g: 0.02, w: 'triangle', lp: 800 }],
    open: [{ f: 392, d: 0.7, g: 0.025, s: 2, a: 0.25, fx: 'reverb' }, { f: 587, d: 0.7, g: 0.018, s: 2, a: 0.25, fx: 'reverb' }],
    close: [{ f: 1175, d: 0.5, g: 0.018, s: 0.5, a: 0.05, fx: 'reverb' }],
    notify: [{ f: 1760, d: 0.6, g: 0.03, fx: 'reverb' }, { f: 2637, d: 0.7, g: 0.02, t: 0.12, fx: 'reverb' }],
    toggle: [{ f: 2637, d: 0.15, g: 0.015, fx: 'reverb' }],
    boot: [262, 392, 523, 784, 1047].map((f, i) => ({ f, d: 1.6, g: 0.022, t: i * 0.14, a: 0.3, fx: 'reverb' }))
  }
};

async function playFile(url) {
  const c = audio();
  if (!buffers.has(url)) {
    buffers.set(url, fetch(url).then((r) => r.arrayBuffer()).then((b) => c.decodeAudioData(b)).catch(() => null));
  }
  const buffer = await buffers.get(url);
  if (!buffer) return false;
  const source = c.createBufferSource();
  source.buffer = buffer;
  source.connect(master);
  source.start();
  return true;
}

function play(event) {
  // Silent while a game or another window is in front.
  if (!enabled || volume === 0 || !focused) return;
  try {
    // A user skin's own file wins; the synth set is the fallback.
    if (!forcedSet && customFiles?.[event]) {
      playFile(customFiles[event]).then((ok) => ok || synth(event)).catch(() => {});
      return;
    }
    synth(event);
  } catch {
    // Audio is decoration, never let it break navigation.
  }
}

function synth(event, setId = forcedSet ?? activeSet) {
  const set = SETS[setId] ?? SETS.default;
  // A sound only the default set has (the shutter) plays from there in every set.
  for (const v of set[event] ?? SETS.default[event] ?? []) voice(v);
}

export const sound = {
  move: () => play('move'),
  tab: () => play('tab'),
  select: () => {
    haptics?.('select');
    play('select');
  },
  back: () => play('back'),
  bump: () => {
    haptics?.('bump');
    play('bump');
  },
  open: () => play('open'),
  close: () => play('close'),
  notify: () => play('notify'),
  toggle: () => play('toggle'),
  boot: () => play('boot'),
  /** A screenshot was saved (Guide + Y, guide menu). */
  shutter: () => play('shutter'),
  /** Preview a set in the settings. */
  preview: (id) => {
    if (!enabled) return;
    synth('select', id);
    setTimeout(() => synth('notify', id), 450);
  }
};

// ---------------------------------------------------------------------------
// Background music: a slow generative piece per skin, like the dashboard music on
// consoles. It only plays while CouchHUD is in front and no game runs.

const MUSIC = {
  // Warm major sevenths, soft and slow.
  default: { bar: 5, chords: [[261.6, 329.6, 392, 493.9], [220, 261.6, 329.6, 392], [174.6, 220, 261.6, 329.6], [196, 246.9, 293.7, 329.6]], pad: 'triangle', cutoff: 1600, bells: 0.5 },
  // Dreamy arpeggios over airy pads.
  deepsea: { bar: 4.5, chords: [[293.7, 370, 440, 554.4], [246.9, 293.7, 370, 440], [196, 246.9, 293.7, 370], [220, 277.2, 329.6, 493.9]], pad: 'sine', cutoff: 2400, arp: 8, bells: 0.2 },
  // A calm drone with a slowly opening filter.
  greenlight: { bar: 6, chords: [[55, 82.4, 110, 164.8], [55, 82.4, 110, 146.8]], pad: 'sawtooth', cutoff: 500, sweep: true, bells: 0.15 },
  // Synthwave: pulsing bass, detuned saws, a crushed arp.
  neoncity: { bar: 3.2, chords: [[220, 261.6, 329.6], [174.6, 220, 261.6], [261.6, 329.6, 392], [196, 246.9, 293.7]], pad: 'sawtooth', cutoff: 1100, bass: true, arp: 8, crush: true },
  // Shimmering glass, very slow.
  nova: { bar: 6, chords: [[329.6, 415.3, 493.9, 622.3], [277.2, 329.6, 415.3, 493.9], [220, 277.2, 329.6, 415.3], [246.9, 311.1, 370, 415.3]], pad: 'sine', cutoff: 3200, bells: 0.8, shimmer: true }
};

let audioMode = 'ui';
let musicVolume = 0.35;
let focused = true;
let gameRunning = false;
let musicBus = null;
let musicTimer = null;
let musicBar = 0;
let musicSet = null;
let musicFile = null;

/** "off", "ui" or "music". */
export function setAudioMode(mode) {
  audioMode = mode;
  enabled = mode !== 'off';
  updateMusic();
}

export function setMusicVolume(v) {
  musicVolume = Math.max(0, Math.min(1, v));
  if (musicBus && musicTimer) musicBus.gain.setTargetAtTime(musicVolume * 0.5, ctx.currentTime, 0.4);
  updateMusic();
}

/** CouchHUD in front or not (a game or another full-screen app on top). */
export function setFocusActive(active) {
  focused = active;
  updateMusic();
}

export function setGameRunning(running) {
  gameRunning = running;
  updateMusic();
}

function wantsMusic() {
  return audioMode === 'music' && focused && !gameRunning && musicVolume > 0 && document.visibilityState !== 'hidden';
}

document.addEventListener('visibilitychange', () => updateMusic());

function updateMusic() {
  const setId = forcedSet ?? activeSet;
  const want = wantsMusic();
  if (want && (!musicTimer || musicSet !== setId || musicFile !== (customFiles?.music ?? null))) startMusic(setId);
  else if (!want && musicTimer) stopMusic();
}

function startMusic(setId) {
  stopMusic(true);
  try {
    const c = audio();
    if (c.state === 'suspended') c.resume().catch(() => {});
    musicSet = setId;
    musicFile = forcedSet ? null : customFiles?.music ?? null;
    musicBus = c.createGain();
    musicBus.gain.setValueAtTime(0.0001, c.currentTime);
    musicBus.gain.setTargetAtTime(musicVolume * 0.5, c.currentTime, 1.2); // slow fade in
    musicBus.connect(c.destination);
    musicBus.connect(reverb);

    if (musicFile) {
      // A user skin's own track, looped.
      // Only for the bus this load belongs to: a quick stop/start must not get a second loop.
      const bus = musicBus;
      fetch(musicFile).then((r) => r.arrayBuffer()).then((b) => c.decodeAudioData(b)).then((buffer) => {
        if (musicBus !== bus) return;
        const source = c.createBufferSource();
        source.buffer = buffer;
        source.loop = true;
        source.connect(bus);
        source.start();
        bus.source = source;
      }).catch(() => {});
      musicTimer = 'file';
      return;
    }

    const spec = MUSIC[setId] ?? MUSIC.default;
    musicBar = 0;
    const playBar = () => bar(spec, musicBar++);
    playBar();
    musicTimer = setInterval(playBar, spec.bar * 1000);
  } catch {
    // Music is decoration.
  }
}

function stopMusic(immediate = false) {
  if (musicTimer && musicTimer !== 'file') clearInterval(musicTimer);
  musicTimer = null;
  const bus = musicBus;
  musicBus = null;
  if (!bus) return;
  // Fade out quickly when a game comes to the front, then let go of the nodes.
  bus.gain.setTargetAtTime(0.0001, ctx.currentTime, immediate ? 0.05 : 0.25);
  setTimeout(() => {
    try {
      bus.source?.stop();
      bus.disconnect();
    } catch {
      // already gone
    }
  }, immediate ? 200 : 1500);
}

/** One bar: pad chord, optional bass pulse, arpeggio and bells. */
function bar(spec, n) {
  if (!musicBus) return;
  const c = ctx;
  const chord = spec.chords[n % spec.chords.length];
  const t0 = c.currentTime + 0.05;
  const len = spec.bar;

  const filter = c.createBiquadFilter();
  filter.type = 'lowpass';
  filter.frequency.setValueAtTime(spec.cutoff, t0);
  if (spec.sweep) {
    filter.frequency.linearRampToValueAtTime(spec.cutoff * 3, t0 + len / 2);
    filter.frequency.linearRampToValueAtTime(spec.cutoff, t0 + len);
  }
  filter.connect(musicBus);

  // Pad: two slightly detuned voices per note, long attack and release overlapping the next bar.
  for (const f of chord) {
    for (const detune of [-6, 6]) {
      const osc = c.createOscillator();
      const g = c.createGain();
      osc.type = spec.pad;
      osc.frequency.value = f;
      osc.detune.value = detune;
      g.gain.setValueAtTime(0.0001, t0);
      g.gain.linearRampToValueAtTime(0.022, t0 + len * 0.35);
      g.gain.linearRampToValueAtTime(0.0001, t0 + len * 1.25);
      osc.connect(g).connect(filter);
      osc.start(t0);
      osc.stop(t0 + len * 1.3);
    }
  }

  if (spec.bass) {
    const steps = 8;
    for (let i = 0; i < steps; i++) {
      const t = t0 + (len / steps) * i;
      const osc = c.createOscillator();
      const g = c.createGain();
      const lp = c.createBiquadFilter();
      lp.type = 'lowpass';
      lp.frequency.value = 600;
      osc.type = 'sawtooth';
      osc.frequency.value = chord[0] / 2;
      g.gain.setValueAtTime(0.05, t);
      g.gain.exponentialRampToValueAtTime(0.0001, t + len / steps * 0.9);
      osc.connect(lp).connect(g).connect(musicBus);
      osc.start(t);
      osc.stop(t + len / steps);
    }
  }

  if (spec.arp) {
    for (let i = 0; i < spec.arp; i++) {
      // Not every step: a sparse, wandering arpeggio sounds calmer.
      if (Math.random() < 0.35) continue;
      const t = t0 + (len / spec.arp) * i;
      const osc = c.createOscillator();
      const g = c.createGain();
      osc.type = spec.crush ? 'square' : 'triangle';
      osc.frequency.value = chord[(i * 3 + n) % chord.length] * 2;
      g.gain.setValueAtTime(0.0001, t);
      g.gain.exponentialRampToValueAtTime(spec.crush ? 0.008 : 0.014, t + 0.01);
      g.gain.exponentialRampToValueAtTime(0.0001, t + 0.6);
      // Everything goes through the music bus, so music volume and fade-out apply.
      osc.connect(g).connect(musicBus);
      osc.start(t);
      osc.stop(t + 0.65);
    }
  }

  if (spec.bells && Math.random() < spec.bells) {
    const f = chord[Math.floor(Math.random() * chord.length)] * (spec.shimmer ? 4 : 2);
    const t = t0 + Math.random() * len * 0.6;
    const osc = c.createOscillator();
    const g = c.createGain();
    osc.type = 'sine';
    osc.frequency.value = f;
    g.gain.setValueAtTime(0.0001, t);
    g.gain.exponentialRampToValueAtTime(0.012, t + 0.02);
    g.gain.exponentialRampToValueAtTime(0.0001, t + 2.5);
    osc.connect(g);
    g.connect(musicBus);
    osc.start(t);
    osc.stop(t + 2.6);
  }
}
