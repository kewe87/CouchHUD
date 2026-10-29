import { el, loadFirst } from './dom.js';
import { iconHue, setAmbient } from './media.js';
import { cssUrl, heroKey } from './hero-rules.js';
import { squareArt, tileArtMode } from './square-rules.js';

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

let tileModeNow = null;
/** The skin's --tile-art, read once per render (from the root; the cache is dropped right after the synchronous render pass). */
function tileMode() {
  if (tileModeNow === null) {
    tileModeNow = tileArtMode(getComputedStyle(document.documentElement).getPropertyValue('--tile-art'));
    queueMicrotask(() => { tileModeNow = null; });
  }
  return tileModeNow;
}

/**
 * Artwork box. Without an image it is a generated banner (no network): a color from the exe icon or the
 * name, a monogram or the icon itself, and the name. The first URL that loads replaces it.
 */
export function artwork(item, kind, className, { tile = false } = {}) {
  const square = tile && kind === 'cover' && tileMode() === 'square';
  const box = el('div', { className: `art ${className}` });
  box.style.setProperty('--h', hue(item.name));
  const iconUrl = item.art?.icon?.[0];
  const icon = iconUrl ? el('img', { className: 'gen-icon', src: iconUrl, alt: '', draggable: false }) : null;
  icon?.addEventListener('error', () => icon.replaceWith(el('span', { className: 'gen-mark', textContent: initials(item.name) })));
  box.append(el('div', { className: 'art-fallback' },
    icon ?? el('span', { className: 'gen-mark', textContent: initials(item.name) }),
    el('span', { className: 'gen-name', textContent: item.name })));
  if (iconUrl) iconHue(iconUrl).then((h) => h != null && box.style.setProperty('--h', h));
  // A skin with square tiles: the square picture, or the cover cropped to a square (background-size: cover, centred) when there is none.
  const wanted = square ? squareArt(item) : null;
  if (square) box.classList.add('art-square');
  loadFirst(wanted ? wanted.urls : item.art?.[kind], (url, img) => {
    box.style.backgroundImage = cssUrl(url);
    box.classList.add('loaded');
    if (wanted) return box.classList.toggle('art-cropped', wanted.isCropped(url));
    // A landscape picture (a store header standing in for a cover) would lose its sides, and with them the
    // logo: show it whole over a blurred copy of itself instead.
    // A skin whose tiles are not 2:3 (a square row) can show a portrait cover whole the same way: the picture is at hand as --art-img.
    if (kind === 'cover') box.style.setProperty('--art-img', cssUrl(url));
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
    back.style.backgroundImage = url ? cssUrl(url) : '';
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
  heroShown = null; // the detail hero is looked up again on the next render
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

let heroToken = 0;
let heroShown = null;

/**
 * The game's own hero picture, sharp, on the detail page only (styles.css shows it for body[data-view="detail"] and nowhere else, so
 * nothing stays behind when the page is left). No hero: the layer stays empty and the blurred backdrop is all there is.
 * Called on every render of the page: the same item and hero leave the layer alone (no clearing, no new fade-in).
 */
export function setDetailHero(item) {
  const box = document.getElementById('bg-hero');
  if (!box) return;
  const key = heroKey(item);
  if (key === heroShown) return;
  heroShown = key;
  const token = ++heroToken;
  box.classList.remove('has-image');
  box.style.backgroundImage = '';
  loadFirst(item?.art?.hero, (url) => {
    if (token !== heroToken) return;
    box.style.backgroundImage = cssUrl(url);
    box.classList.add('has-image');
  });
}
