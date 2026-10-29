// Cursor steps across pages of a long list the host hands out piece by piece (the games that are not installed). Pure, for the tests.

/**
 * Where a step of the cursor lands: inside the page ({ index, offset } unchanged offset), on the next or previous page (new offset,
 * index there), or null at the very start or end of the list.
 * @param {{ index: number, offset: number, count: number, total: number }} at  cursor, page start, items on this page, items in all
 * @param {number} delta  -1/+1 for left/right, ±columns for up/down
 * @param {number} pageSize  how many items a full page has
 */
export function step({ index, offset, count, total }, delta, pageSize) {
  const next = index + delta;
  if (next >= 0 && next < count) return { index: next, offset };
  if (next >= count && offset + count < total) {
    const left = total - offset - count;
    return { index: Math.min(next - count, Math.min(left, pageSize) - 1), offset: offset + count };
  }
  if (next < 0 && offset > 0) {
    const before = Math.max(0, offset - pageSize);
    return { index: Math.max(0, offset - before + next), offset: before };
  }
  return null;
}

/**
 * Where a view's cursor goes on a step of `delta` in a list of `length`, or null when it stays (the caller bumps).
 * wrap: a horizontal tile row (Home) goes round, right on the last to the first and left on the first to the last, for single
 * steps only. Everything else stops at its ends.
 */
export function cursorStep(index, delta, length, { wrap = false } = {}) {
  if (!(length > 0) || !delta) return null;
  const next = index + delta;
  if (next >= 0 && next < length) return next;
  // A cursor left outside a list that shrank comes back to its nearest end.
  if (!(index >= 0 && index < length)) return Math.max(0, Math.min(length - 1, next));
  if (wrap && Math.abs(delta) === 1 && length > 1) return (next + length) % length;
  return null;
}
