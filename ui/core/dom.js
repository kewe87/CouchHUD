import { fmtDate, fmtNumber, locale, t } from './i18n.js';
import { formatBytes } from './size.js';

export const $ = (sel) => document.querySelector(sel);

export const el = (tag, props = {}, ...children) => {
  const { dataset, style, ...rest } = props;
  const node = Object.assign(document.createElement(tag), rest);
  if (dataset) Object.assign(node.dataset, dataset);
  if (style) Object.assign(node.style, style);
  node.append(...children.flat().filter((c) => c != null && c !== false));
  return node;
};

/** Moves the cursor class from the old to the new element. */
export function setFocused(nodes, index) {
  for (const n of nodes) n.classList.remove('focused');
  nodes[index]?.classList.add('focused');
}

/** Applies a style change without its CSS transition, e.g. the initial position after a rebuild. */
export function withoutTransition(node, apply) {
  node.style.transition = 'none';
  apply();
  void node.offsetWidth; // flush, so the next change animates again
  node.style.transition = '';
}

/**
 * Scrolls a container so the element stays comfortably in view, without ever
 * scrolling the page itself (scrollIntoView would also move overflow:hidden parents).
 */
export function keepInView(container, element, { center = false, instant = false, margin = 24 } = {}) {
  if (!container || !element) return;
  // Position inside the scrolled content, independent of offsetParent chains.
  const top = element.getBoundingClientRect().top - container.getBoundingClientRect().top + container.scrollTop;
  const bottom = top + element.offsetHeight;
  let target = container.scrollTop;
  if (center) {
    target = top - container.clientHeight / 2 + element.offsetHeight / 2;
  } else if (top - margin < container.scrollTop) {
    target = top - margin;
  } else if (bottom + margin > container.scrollTop + container.clientHeight) {
    target = bottom + margin - container.clientHeight;
  }
  target = Math.max(0, Math.min(target, container.scrollHeight - container.clientHeight));
  if (Math.abs(target - container.scrollTop) < 1) return;
  container.scrollTo({ top: target, behavior: instant ? 'instant' : 'smooth' });
}

/** "1 Eintrag" / "3 Einträge". */
export const entriesLabel = (n) => t('dom.entries', { count: n });

/** A size in decimal units (1 MB = 1,000,000 bytes, like Windows Update and Steam); null when unknown. */
export const formatSize = (bytes) => formatBytes(bytes, locale());

export function formatDuration(minutes) {
  if (!minutes) return null;
  if (minutes < 60) return t('dom.minutes', { n: Math.round(minutes) });
  const h = minutes / 60;
  return t('dom.hours', { n: fmtNumber(h, { maximumFractionDigits: h < 10 ? 1 : 0 }) });
}

export function relativeTime(ts) {
  if (!ts) return t('dom.neverPlayed');
  const days = Math.floor((Date.now() - ts) / 86400000);
  if (days <= 0) return t('dom.playedToday');
  if (days === 1) return t('dom.playedYesterday');
  if (days < 30) return t('dom.playedDaysAgo', { days });
  return t('dom.playedOn', { date: fmtDate(ts, { dateStyle: 'short' }) });
}

/** Tries URLs in order and calls back with the first that loads. */
export function loadFirst(urls, onLoad) {
  const queue = [...(urls || [])];
  const next = () => {
    const url = queue.shift();
    if (!url) return;
    const img = new Image();
    img.decoding = 'async';
    img.onload = () => onLoad(url, img);
    img.onerror = next;
    img.src = url;
  };
  next();
}
