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
