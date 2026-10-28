import { el, loadFirst } from './dom.js';
import { iconHue, setAmbient } from './media.js';

export function hue(str) {
  let h = 0;
  for (const c of str) h = (h * 31 + c.charCodeAt(0)) % 360;
  return h;
}

/** One or two letters for the generated banner: initials of the first two words, or the first two letters. */
export function initials(name) {
  const words = String(name ?? '').match(/[\p{L}\p{N}]+/gu) || [];
  if (!words.length) return '?';
  const letters = words.length > 1 ? words[0][0] + words[1][0] : [...words[0]].slice(0, 2).join('');
  return letters.toUpperCase();
}

/**
 * Artwork box. Without an image it is a generated banner (no network): a color from the exe icon or the
 * name, a monogram or the icon itself, and the name. The first URL that loads replaces it.
 */
export function artwork(item, kind, className) {
  const box = el('div', { className: `art ${className}` });
  box.style.setProperty('--h', hue(item.name));
  const iconUrl = item.art?.icon?.[0];
  const icon = iconUrl ? el('img', { className: 'gen-icon', src: iconUrl, alt: '', draggable: false }) : null;
  icon?.addEventListener('error', () => icon.replaceWith(el('span', { className: 'gen-mark', textContent: initials(item.name) })));
  box.append(el('div', { className: 'art-fallback' },
    icon ?? el('span', { className: 'gen-mark', textContent: initials(item.name) }),
    el('span', { className: 'gen-name', textContent: item.name })));
  if (iconUrl) iconHue(iconUrl).then((h) => h != null && box.style.setProperty('--h', h));
  loadFirst(item.art?.[kind], (url, img) => {
    box.style.backgroundImage = `url("${url}")`;
    box.classList.add('loaded');
    // A landscape picture (a store header standing in for a cover) would lose its sides, and with them the
    // logo: show it whole over a blurred copy of itself instead.
    // A skin whose tiles are not 2:3 (a square row) can show a portrait cover whole the same way: the picture is at hand as --art-img.
    if (kind === 'cover') box.style.setProperty('--art-img', `url("${url}")`);
    if (kind === 'cover' && img && img.naturalWidth / img.naturalHeight > 1.15) box.classList.add('art-wide');
  });
  return box;
}

const bgLayers = () => [...document.querySelectorAll('.bg-layer')];
let bgFront = 0;
let bgItemId = null;

/** Cross-fades the blurred backdrop to the item's hero art (or a tinted gradient). */
export function setBackground(item) {
  if (!item || item.id === bgItemId) return;
  bgItemId = item.id;
  const tint = item.hue ?? hue(item.name); // neutral screens pick the default blue instead of a color from their name
  setAmbient(item, tint);
  const layers = bgLayers();
  const show = (url) => {
    if (item.id !== bgItemId) return;
    const back = layers[1 - bgFront];
    back.style.backgroundImage = url ? `url("${url}")` : '';
    back.style.setProperty('--h', tint);
    back.classList.add('visible');
    layers[bgFront].classList.remove('visible');
    bgFront = 1 - bgFront;
  };
  const urls = [...(item.art?.hero || []), ...(item.art?.cover || [])];
  if (!urls.length) return show(null);
  let loaded = false;
  loadFirst(urls, (url) => {
    loaded = true;
    show(url);
  });
  // Nothing loads (offline, no art): still switch the tint after a moment.
  setTimeout(() => !loaded && show(null), 1500);
}

/** Forgets the current backdrop so the next setBackground() reloads it, e.g. after new artwork was picked. */
export function refreshBackground() {
  bgItemId = null;
}

/** Neutral backdrop for screens without an item (empty lists, "Alle Spiele"). */
export function plainBackground() {
  setBackground({ id: '__plain', name: 'CouchHUD', hue: 220, art: {} });
}

/**
 * Swaps a text title for the item's clear logo once it has loaded. Text shows
 * first, so fast scrolling never waits on images; stale loads are ignored.
 */
export function showClearLogo(view, item) {
  const token = (view.logoToken = (view.logoToken || 0) + 1);
  view.logo.hidden = true;
  view.title.hidden = false;
  loadFirst(item.art?.logo, (url) => {
    if (token !== view.logoToken) return;
    view.logo.src = url;
    view.logo.alt = item.name;
    view.logo.hidden = false;
    view.title.hidden = true;
  });
}
