// The toast for a screenshot taken with Guide + Y or from the guide menu while CouchHUD is in front: the picture, where it went,
// and the camera sound of the sound set (if sounds are on). Over a game the host shows its own small toast instead.

import { $, el } from '../core/dom.js';
import { shotToast } from '../core/screenshot-rules.js';
import { sound } from '../sound.js';

let timer = null;

export function showShotToast(payload) {
  // The host takes the next picture: our toast must not be in it.
  if (payload?.hide === true) {
    clearTimeout(timer);
    const box = $('#shot-toast');
    if (box) box.hidden = true;
    return;
  }
  const view = shotToast(payload);
  if (!view.text) return;
  const box = $('#shot-toast');
  if (!box) return;
  box.replaceChildren(
    view.image ? el('img', { src: view.image, alt: '' }) : el('span', { className: 'shot-toast-icon', textContent: view.ok ? '📸' : '⚠' }),
    el('span', { className: 'shot-toast-text', textContent: view.text }));
  box.classList.toggle('failed', !view.ok);
  box.hidden = false;
  if (view.ok) sound.shutter();
  else sound.bump();
  clearTimeout(timer);
  timer = setTimeout(() => (box.hidden = true), view.ms);
}
