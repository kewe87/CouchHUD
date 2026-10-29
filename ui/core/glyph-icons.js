// Icons for rows and menus. Rows and menus pass a glyph (an emoji or symbol, as before); `iconEl` swaps the
// known ones for outlines in the same style as the settings categories (settings-icons.js), so a PC without
// the emoji font shows the same shapes and no colored emoji sneak into the menus. Unknown text stays text.

const SVG = 'http://www.w3.org/2000/svg';

const PATHS = {
  image: 'M4 5h16v14H4zM4 16l5-5 4 4 3-3 4 4M15 9.5h.01',
  save: 'M5 4h12l2 2v14H5zM8 4v5h7V4M8 20v-6h8v6',
  palette: 'M12 3a9 9 0 1 0 0 18c1.5 0 2-1 1.5-2-.6-1.2.2-2.5 1.6-2.5H17a4 4 0 0 0 4-4c0-5-4-9.5-9-9.5zM7.5 11h.01M10 7.5h.01M14.5 7.5h.01',
  trash: 'M4 7h16M9 7V4h6v3M6 7l1 13h10l1-13M10 11v6M14 11v6',
  key: 'M8 14a4 4 0 1 1 3.4-6H21v3h-2v2h-3v-2h-4.6A4 4 0 0 1 8 14z',
  lock: 'M6 11h12v9H6zM8.5 11V8a3.5 3.5 0 0 1 7 0v3',
  unlock: 'M6 11h12v9H6zM8.5 11V8a3.5 3.5 0 0 1 6.8-1.2',
  folder: 'M3 6h6l2 2h10v11H3z',
  bell: 'M6 16v-5a6 6 0 0 1 12 0v5l2 2H4zM10 21h4',
  globe: 'M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18zM3 12h18M12 3c3 3 3 15 0 18M12 3c-3 3-3 15 0 18',
  camera: 'M4 8h4l2-3h4l2 3h4v11H4zM12 11a3.2 3.2 0 1 0 .01 0',
  search: 'M10.5 4a6.5 6.5 0 1 0 0 13 6.5 6.5 0 0 0 0-13zM15.5 15.5L21 21',
  monitor: 'M3 5h18v11H3zM8 20h8M12 16v4',
  volume: 'M4 9h4l5-4v14l-5-4H4zM16.5 8.5a5 5 0 0 1 0 7M19 6a8.5 8.5 0 0 1 0 12',
  gamepad: 'M7 8h10a4 4 0 0 1 4 4l-1 5a2 2 0 0 1-3.4 1L15 16H9l-1.6 2A2 2 0 0 1 4 17l-1-5a4 4 0 0 1 4-4zM8 10.5v3M6.5 12h3M16 11h.01M18 13h.01',
  warn: 'M12 4l9 16H3zM12 10v4.5M12 17.5h.01',
  block: 'M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18zM5.6 5.6l12.8 12.8',
  moon: 'M20 14.5A8 8 0 0 1 9.5 4 8 8 0 1 0 20 14.5z',
  power: 'M12 3v9M6.3 6.3a8 8 0 1 0 11.4 0',
  pencil: 'M4 20l1-4L16 5l3 3L8 19zM14 7l3 3',
  play: 'M7 4l13 8-13 8z',
  record: 'M12 5a7 7 0 1 0 0 14 7 7 0 0 0 0-14z',
  download: 'M12 3v12M7 11l5 5 5-5M4 20h16',
  upload: 'M12 17V5M7 9l5-5 5 5M4 20h16',
  refresh: 'M20 11a8 8 0 1 0-2 6M20 5v6h-6',
  // Restarting CouchHUD (the app), not the PC: the turning arrow inside a screen frame, a different outline than the PC's arrow.
  appRestart: 'M3 4h18v16H3zM16.4 12.5a4.4 4.4 0 1 0-1.1 3.3M16.4 9.2v3.3h-3.3',
  swap: 'M4 8h14M14 4l4 4-4 4M20 16H6M10 12l-4 4 4 4',
  star: 'M12 3l2.7 5.6 6.1.9-4.4 4.3 1 6.1L12 17l-5.4 2.9 1-6.1L3.2 9.5l6.1-.9z',
  gear: 'M12 9a3 3 0 1 0 .01 0M5 12l-2 1.3 2 3.4 2.3-.8 1.7 1 .4 2.4h4l.4-2.4 1.7-1 2.3.8 2-3.4-2-1.3.1-2-.1 0 2-1.3-2-3.4-2.3.8-1.7-1-.4-2.4h-4l-.4 2.4-1.7 1-2.3-.8-2 3.4z',
  home: 'M3 11l9-8 9 8M5 10v10h14V10M10 20v-6h4v6',
  spark: 'M12 3l2 6 6 2-6 2-2 6-2-6-6-2 6-2zM19 3v4M17 5h4',
  list: 'M4 6h16M4 12h16M4 18h10',
  window: 'M4 5h16v14H4zM4 9h16',
  music: 'M9 18V6l10-2v12M9 18a2.5 2.5 0 1 1-.01 0M19 16a2.5 2.5 0 1 1-.01 0',
  bluetooth: 'M7 7l10 10-5 4V3l5 4L7 17',
  timer: 'M12 8v5l3 2M12 4a8 8 0 1 0 .01 0M10 2h4',
  desktop: 'M3 5h18v11H3zM8 20h8M12 16v4',
  pause: 'M8 5v14M16 5v14',
  plus: 'M12 5v14M5 12h14',
  dot: 'M12 8a4 4 0 1 0 .01 0',
  circle: 'M12 6a6 6 0 1 0 .01 0',
  dots: 'M6 12h.01M12 12h.01M18 12h.01',
  drive: 'M3 9l2-5h14l2 5v9H3zM3 14h18M17 16.5h.01',
  headphones: 'M4 15v-3a8 8 0 0 1 16 0v3M4 14h4v6H5a1 1 0 0 1-1-1zM20 14h-4v6h3a1 1 0 0 0 1-1z',
  clapper: 'M4 9h16v11H4zM4 9l2-5h3l-2 5M10 9l2-5h3l-2 5M16 9l2-5h2l-1 5',
  bulb: 'M9 18h6M10 21h4M12 3a6 6 0 0 0-3.5 10.9c.6.5 1 1.2 1 2.1h5c0-.9.4-1.6 1-2.1A6 6 0 0 0 12 3z',
  plug: 'M9 3v5M15 3v5M6 8h12v3a6 6 0 0 1-12 0zM12 17v4',
  thermometer: 'M10 14.5V5a2 2 0 0 1 4 0v9.5a4 4 0 1 1-4 0z',
  sun: 'M12 8a4 4 0 1 0 .01 0M12 2v2.5M12 19.5V22M2 12h2.5M19.5 12H22M4.9 4.9l1.8 1.8M17.3 17.3l1.8 1.8M4.9 19.1l1.8-1.8M17.3 6.7l1.8-1.8',
  drop: 'M12 3s6 6.5 6 11a6 6 0 0 1-12 0c0-4.5 6-11 6-11z',
  bolt: 'M13 3L5 13h6l-1 8 8-10h-6z',
  battery: 'M3 8h15v8H3zM18 11h3v2h-3zM6 11v2M9 11v2',
  door: 'M6 3h12v18H6zM15 12h.01',
  person: 'M12 5a2 2 0 1 0 .01 0M8 21l2-7-2-3 4-2 3 3 3 1M10 14l-2 7M13 11l1 4 3 6',
  robot: 'M5 8h14v10H5zM12 8V4M9 13h.01M15 13h.01M9 16h6M3 12v3M21 12v3',
  tv: 'M3 6h18v12H3zM8 21h8M9 3l3 3 3-3',
  grid: 'M4 4h7v7H4zM13 4h7v7h-7zM4 13h7v7H4zM13 13h7v7h-7z',
  volumeLow: 'M4 9h4l5-4v14l-5-4H4zM16.5 8.5a5 5 0 0 1 0 7',
  mute: 'M4 9h4l5-4v14l-5-4H4zM17 9.5l5 5M22 9.5l-5 5',
  shield: 'M12 3l8 3v6c0 4.5-3.4 8.2-8 9-4.6-.8-8-4.5-8-9V6zM9 12l2 2 4-4'
};

const GLYPHS = {
  '🔇': 'mute', '🔉': 'volumeLow',
  '🖼': 'image', '💾': 'save', '🎨': 'palette', '🗑': 'trash', '🔑': 'key', '🔒': 'lock', '🔓': 'unlock', '📁': 'folder',
  '🔔': 'bell', '🌐': 'globe', '📸': 'camera', '🔍': 'search', '⌕': 'search', '🖵': 'monitor', '🔊': 'volume', '🔈': 'volume',
  '🎮': 'gamepad', '⚠': 'warn', '⛔': 'block', '☾': 'moon', '⏻': 'power', '✎': 'pencil', '▶': 'play', '⏺': 'record',
  '⬇': 'download', '⤓': 'download', '⬆': 'upload', '↻': 'refresh', '⟳': 'refresh', '⟲': 'appRestart', '↺': 'refresh', '⇄': 'swap',
  '★': 'star', '☆': 'star', '⚙': 'gear', '⌂': 'home', '✦': 'spark', '▤': 'list', '▣': 'window', '♪': 'music',
  'ᛒ': 'bluetooth', '⏲': 'timer', '▭': 'desktop', '⏸': 'pause', '+': 'plus', '◆': 'dot', '○': 'circle', '⋯': 'dots', '🖴': 'drive', '▦': 'grid', '📺': 'tv', '🎧': 'headphones', '🎬': 'clapper', '📜': 'list', '💡': 'bulb', '🔌': 'plug', '🌀': 'refresh', '🔘': 'circle', '🪟': 'window', '🪄': 'spark', '🎵': 'music', '🌡️': 'thermometer', '🌡': 'thermometer', '🤖': 'robot', '🌤️': 'sun', '🌤': 'sun', '☀️': 'sun', '☀': 'sun', '💧': 'drop', '⚡': 'bolt', '🔋': 'battery', '🚪': 'door', '🏃': 'person', '⛨': 'shield'
};

/** Every glyph that is drawn as one of the HUD's own icons (a third party's plugin tab never carries one; see core/plugin-tabs.js). */
export const BUILTIN_GLYPHS = Object.keys(GLYPHS);

/** The icon a glyph is drawn as (undefined: shown as text). */
export const glyphName = (glyph) => GLYPHS[glyph];

const FILLED = new Set(['★', '▶', '⏺', '◆']);

/** An icon node for a row/menu glyph: an outline SVG for known glyphs, else the text itself. */
export function iconEl(glyph) {
  const name = GLYPHS[glyph];
  if (!name) return document.createTextNode(glyph ?? '');
  const svg = document.createElementNS(SVG, 'svg');
  svg.setAttribute('viewBox', '0 0 24 24');
  svg.setAttribute('class', `cat-icon glyph-icon${FILLED.has(glyph) ? ' filled' : ''}`);
  svg.setAttribute('aria-hidden', 'true');
  const path = document.createElementNS(SVG, 'path');
  path.setAttribute('d', PATHS[name]);
  svg.append(path);
  return svg;
}
