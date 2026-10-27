import { BUILTIN_SKINS } from '../skins/index.js';

export const state = {
  info: {},
  library: [],
  /** The user's collections: [{ id, name, items: [itemId], onHome }]. */
  collections: [],
  /** Index into the tab views. */
  tab: 0,
  /** Pages opened on top of the current tab (detail page, settings pages, ...). B pops them. */
  stack: [],
  /** Open modal menu, see components/overlay.js. */
  overlay: null,
  /** Open side panel (the guide menu); takes input before the view. */
  panel: null,
  /** Running game as reported by the host: { id, name, started } or null. */
  session: null,
  /** Last input device ('pad' or 'keyboard'); hints show matching buttons. */
  inputSource: 'pad',
  // Where the last action came from: 'pad', 'keyboard' or 'mouse' (mouse and touch). Switching Bluetooth off tells the host.
  actionSource: 'pad',
  skins: BUILTIN_SKINS,
  skin: BUILTIN_SKINS[0]
};
