// Pure rules for square tiles. A skin asks for them with the CSS variable --tile-art: square (on :root, or on the tiles);
// art.js applies the answer, so every skin gets the same one.

/** The tile art a skin asks for: "square", or "cover" for anything else (also a missing or odd value). */
export function tileArtMode(cssValue) {
  const value = String(cssValue ?? '').trim().replace(/^["']|["']$/g, '').toLowerCase();
  return value === 'square' ? 'square' : 'cover';
}

/**
 * What a square tile shows: the item's square pictures first, the cover after them. Without any square picture the cover is shown
 * cropped to a square (cropped: true; the tile centres it with object-fit/background-size: cover), so the answer is the same in every skin.
 * `urls` is tried in order; `isCropped(url)` tells for the one that loaded whether it is a stand-in (a cover) and needs the crop.
 */
export function squareArt(item) {
  const square = (item?.art?.square ?? []).filter((u) => typeof u === 'string' && u);
  const cover = (item?.art?.cover ?? []).filter((u) => typeof u === 'string' && u);
  return {
    urls: [...square, ...cover.filter((u) => !square.includes(u))],
    hasSquare: square.length > 0,
    isCropped: (url) => !square.includes(url)
  };
}
