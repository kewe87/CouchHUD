import { $ } from './dom.js';
import { state } from './state.js';
import { setSkinSounds } from '../sound.js';
import { glyphFor, glyphKind } from './pad-glyphs.js';

/** "xbox" or "playstation": the expert setting when fixed, else the kind of pad the host saw pressed last. */
export const glyphKindNow = () => glyphKind(state.info?.padGlyphs, state.info?.padKind);

export const glyph = (btn) => glyphFor(btn, glyphKindNow(), state.skin.glyphs);

const glyphListeners = new Set();

/** Called whenever the button symbols change (pad kind, setting, skin); returns the unsubscribe. For text built with glyphs. */
export function onGlyphsChanged(fn) {
  glyphListeners.add(fn);
  return () => glyphListeners.delete(fn);
}

/** Redraws every button symbol on screen: hints, [data-glyph] nodes and whoever listens. */
export function refreshGlyphs() {
  document.documentElement.dataset.pad = glyphKindNow();
  for (const node of document.querySelectorAll('[data-glyph]')) node.textContent = glyph(node.dataset.glyph);
  for (const node of document.querySelectorAll('.hint[data-btn] .glyph')) {
    node.textContent = glyph(node.closest('.hint').dataset.btn);
  }
  for (const fn of glyphListeners) {
    try {
      fn();
    } catch (e) {
      console.error(e);
    }
  }
}

/** The host saw another kind of pad, or the setting changed: only redraws when the symbols really change. */
export function setPadGlyphs({ padKind, padGlyphs } = {}) {
  const before = glyphKindNow();
  if (padKind !== undefined) state.info.padKind = padKind;
  if (padGlyphs !== undefined) state.info.padGlyphs = padGlyphs;
  if (glyphKindNow() !== before) refreshGlyphs();
}

export function applySkin(skin) {
  const link = $('#skin-css');
  if (skin.css) link.href = skin.css;
  else link.removeAttribute('href');
  document.documentElement.dataset.skin = skin.id;
  state.skin = skin;
  setSkinSounds(skin.id, skin.sounds);
  refreshGlyphs();
}
