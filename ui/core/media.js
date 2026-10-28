// Background trailers and ambient color: the "PC Master Race" layer.
// A muted trailer fades in behind the UI once an item has kept the focus for a
// moment, like on current consoles; moving on stops it right away.

import { api } from './api.js';
import { state } from './state.js';

const storeInfo = new Map();
let video;
let timer;
let current = null;

function layer() {
  if (!video) {
    video = document.createElement('video');
    video.className = 'bg-video';
    video.muted = true;
    video.loop = true;
    video.playsInline = true;
    video.preload = 'auto';
    video.addEventListener('playing', () => video.classList.add('visible'));
    document.querySelector('#bg .bg-ambient')?.before(video);
  }
  return video;
}

async function infoFor(id) {
  if (!storeInfo.has(id)) {
    // Remember the promise so fast scrolling never asks twice.
    storeInfo.set(id, api.getStoreInfo(id).catch(() => null));
  }
  return storeInfo.get(id);
}

/** The entry's store match or own texts changed: ask again next time. */
export function forgetStoreInfo(id) {
  storeInfo.delete(id);
}

/** Fades the trailer out; the next one starts from a clean layer. */
export function stopTrailer() {
  clearTimeout(timer);
  current = null;
  if (!video) return;
  video.classList.remove('visible');
  const v = video;
  setTimeout(() => {
    // A newer trailer may already be fading in on the same element.
    if (v.classList.contains('visible')) return;
    v.pause();
    v.removeAttribute('src');
    v.load();
  }, 600);
}

/** Starts the item's trailer after `delay` ms of steady focus. */
export function scheduleTrailer(item, delay = 1800) {
  if (current === item?.id) return;
  stopTrailer();
  // Built-in tiles (browser) have no store page the host could look up.
  if (!item || item.special || item.builtin || String(item.id).startsWith('__') || state.info.trailerPreviews === false) return;
  current = item.id;
  timer = setTimeout(async () => {
    const info = await infoFor(item.id);
    const url = info?.trailers?.[0];
    if (!url || current !== item.id) return;
    const v = layer();
    v.src = url;
    v.play().catch(() => {});
  }, delay);
}

// ---------------------------------------------------------------------------
// Ambient color

const colorCache = new Map();

/** Average, saturation-weighted color of an image; null if the host forbids reading it (CORS). */
function sampleColor(url) {
  if (colorCache.has(url)) return colorCache.get(url);
  const promise = new Promise((resolve) => {
    const img = new Image();
    img.crossOrigin = 'anonymous';
    img.onload = () => {
      try {
        const canvas = document.createElement('canvas');
        canvas.width = canvas.height = 24;
        const ctx = canvas.getContext('2d', { willReadFrequently: true });
        ctx.drawImage(img, 0, 0, 24, 24);
        const data = ctx.getImageData(0, 0, 24, 24).data;
        let r = 0, g = 0, b = 0, weight = 0;
        for (let i = 0; i < data.length; i += 4) {
          const max = Math.max(data[i], data[i + 1], data[i + 2]);
          const min = Math.min(data[i], data[i + 1], data[i + 2]);
          // Colorful, not-too-dark pixels define the mood; grey and black don't.
          const w = (max - min) / 255 * (max / 255) + 0.02;
          r += data[i] * w;
          g += data[i + 1] * w;
          b += data[i + 2] * w;
          weight += w;
        }
        resolve(`rgb(${Math.round(r / weight)}, ${Math.round(g / weight)}, ${Math.round(b / weight)})`);
      } catch {
        resolve(null);
      }
    };
    img.onerror = () => resolve(null);
    img.src = url;
  });
  colorCache.set(url, promise);
  // Only answers are kept: an image that wasn't there yet (still downloading) is asked again next time.
  promise.then((color) => color == null && colorCache.get(url) === promise && colorCache.delete(url));
  return promise;
}

/** Hue (0-359) of an image's dominant color, for the generated banner; null if it can't be read. */
export async function iconHue(url) {
  const color = await sampleColor(url);
  const m = color && /rgb\((\d+), (\d+), (\d+)\)/.exec(color);
  if (!m) return null;
  const [r, g, b] = [m[1], m[2], m[3]].map((v) => v / 255);
  const max = Math.max(r, g, b);
  const d = max - Math.min(r, g, b);
  if (d < 0.08) return null; // grey icon: keep the hue from the name
  const h = max === r ? ((g - b) / d) % 6 : max === g ? (b - r) / d + 2 : (r - g) / d + 4;
  return Math.round((h * 60 + 360) % 360);
}

let ambientFor = null;

/** Tints the backdrop with the artwork's dominant color (falls back to the hue from the name). */
export async function setAmbient(item, fallbackHue) {
  ambientFor = item?.id ?? null;
  const root = document.documentElement;
  const url = item?.art?.cover?.[0] ?? item?.art?.hero?.[0];
  const color = url ? await sampleColor(url) : null;
  if (ambientFor !== (item?.id ?? null)) return;
  root.style.setProperty('--ambient', color ?? `hsl(${fallbackHue} 70% 45%)`);
}
