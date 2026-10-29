// One icon family for the settings categories: 24 px outlines in the text colour,
// so every skin and font renders the same shapes (emoji and symbol fonts differ per PC).

const PATHS = {
  console: 'M5 3h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2zM10 8l6 4-6 4z',
  library: 'M4 4h7v7H4zM13 4h7v7h-7zM4 13h7v7H4zM13 13h7v7h-7z',
  appearance: 'M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18zM12 3v18M12 8h4M12 12h6M12 16h4',
  display: 'M3 5h18v11H3zM8 20h8M12 16v4',
  audio: 'M4 9h4l5-4v14l-5-4H4zM16.5 8.5a5 5 0 0 1 0 7M19 6a8.5 8.5 0 0 1 0 12',
  controller: 'M9 3h6v6h6v6h-6v6H9v-6H3V9h6z',
  phone: 'M8 2h8a1 1 0 0 1 1 1v18a1 1 0 0 1-1 1H8a1 1 0 0 1-1-1V3a1 1 0 0 1 1-1zM11 18h2',
  news: 'M5 4h11v16H5zM16 8h3v10a2 2 0 0 1-2 2M8 8h5M8 12h5M8 16h5',
  home: 'M3 11l9-8 9 8M5 10v10h14V10M10 20v-6h4v6',
  weather: 'M12 8a4 4 0 1 0 .01 0M12 2v2.5M12 19.5V22M2 12h2.5M19.5 12H22M4.9 4.9l1.8 1.8M17.3 17.3l1.8 1.8M4.9 19.1l1.8-1.8M17.3 6.7l1.8-1.8',
  bell: 'M6 16v-5a6 6 0 0 1 12 0v5l2 2H4zM10 21h4',
  update: 'M12 3v12M7 11l5 5 5-5M4 20h16',
  power: 'M12 3v9M6.3 6.3a8 8 0 1 0 11.4 0',
  info: 'M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18zM12 11v6M12 7.5h.01',
  expert: 'M4 7h10M18 7h2M4 17h2M10 17h10M14 5v4M8 15v4',
  parental: 'M9 7a3 3 0 1 0 .01 0M3 20v-2a5.5 5.5 0 0 1 11 0v2M17 10.5a2.3 2.3 0 1 0 .01 0M15.8 15.2a4 4 0 0 1 5.2 3.8v1',
  shield: 'M12 3l8 3v6c0 5-3.5 8-8 9-4.5-1-8-4-8-9V6z',
  privacy: 'M7 11V8a5 5 0 0 1 10 0v3M5 11h14v10H5zM12 15v2.5',
  startup: 'M4 6h12M4 12h12M4 18h8M18 15l4 3-4 3z',
  tv: 'M3 5h18v12H3zM8 21h8M12 17v4M9 2l3 3 3-3',
  network: 'M2 9a15 15 0 0 1 20 0M5 12.5a10 10 0 0 1 14 0M8.5 16a5 5 0 0 1 7 0M12 19.5h.01',
  ai: 'M12 3l2 6 6 2-6 2-2 6-2-6-6-2 6-2zM19 3v4M17 5h4',
  plugin: 'M10 4a2 2 0 0 1 4 0v2h4v4h-2a2 2 0 0 0 0 4h2v4h-4v-2a2 2 0 0 0-4 0v2H6v-4h2a2 2 0 0 0 0-4H6V6h4z'
};

const SVG = 'http://www.w3.org/2000/svg';

/** Returns an <svg> element for the named icon (falls back to the info icon). */
export function categoryIcon(name) {
  const svg = document.createElementNS(SVG, 'svg');
  svg.setAttribute('viewBox', '0 0 24 24');
  svg.setAttribute('class', 'cat-icon');
  svg.setAttribute('aria-hidden', 'true');
  const path = document.createElementNS(SVG, 'path');
  path.setAttribute('d', PATHS[name] ?? PATHS.info);
  svg.append(path);
  return svg;
}
