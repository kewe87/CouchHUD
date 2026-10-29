// Moving through a menu laid out in rows (tiles per group), without DOM (tested in scripts/ui-logic-check.mjs).

/** Rows of indices from the entries' group keys: a new row starts where the group changes. */
export function groupRows(groups) {
  const rows = [];
  groups.forEach((g, i) => {
    if (!rows.length || groups[i - 1] !== g) rows.push([]);
    rows.at(-1).push(i);
  });
  return rows;
}

/**
 * The next index for a D-pad step, or -1 at the edge (bump). Left/right move within the row and continue in the row before/after
 * (reading order); up/down change the row and keep the column, the last tile if the other row is shorter.
 */
export function gridMove(rows, index, action) {
  const r = rows.findIndex((row) => row.includes(index));
  if (r < 0) return -1;
  const c = rows[r].indexOf(index);
  switch (action) {
    case 'left':
      if (c > 0) return rows[r][c - 1];
      return r > 0 ? rows[r - 1].at(-1) : -1;
    case 'right':
      if (c < rows[r].length - 1) return rows[r][c + 1];
      return r < rows.length - 1 ? rows[r + 1][0] : -1;
    case 'up':
    case 'down': {
      const other = rows[r + (action === 'up' ? -1 : 1)];
      return other ? other[Math.min(c, other.length - 1)] : -1;
    }
    default:
      return -1;
  }
}
