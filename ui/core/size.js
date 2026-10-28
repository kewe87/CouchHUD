// One size format for the whole HUD (downloads, updates, library, save games, files): decimal units, 1 MB = 1,000,000 bytes, the way
// Windows Update, Steam and the stores count. The host's texts use the same rule (Core: ByteSize.Format).

const UNITS = ['B', 'KB', 'MB', 'GB', 'TB'];

/** "1,5 GB" / "1.5 GB" in the given locale; null when unknown (not a finite number above 0). */
export function formatBytes(bytes, locale = 'de-DE') {
  if (typeof bytes !== 'number' || !Number.isFinite(bytes) || bytes <= 0) return null;
  let value = bytes;
  let unit = 0;
  while (value >= 1000 && unit < UNITS.length - 1) {
    value /= 1000;
    unit++;
  }
  const digits = (v, u) => (u === 0 || v >= 10 ? 0 : 1);
  // 999.96 KB would round to "1000 KB": that is 1 MB.
  if (unit < UNITS.length - 1 && Number(value.toFixed(digits(value, unit))) >= 1000) {
    value /= 1000;
    unit++;
  }
  return `${value.toLocaleString(locale, { maximumFractionDigits: digits(value, unit) })} ${UNITS[unit]}`;
}

// The save backup limits the page offers, whole decimal MB (Core: SaveGameService.GamePresetsMb / TotalPresetsMb).
export const GAME_LIMITS_MB = [100, 250, 500, 1000, 2000, 5000];
export const TOTAL_LIMITS_MB = [1000, 5000, 10000, 20000, 50000, 100000];

/** The offered limits plus the stored one when it is none of them (a limit moved up from binary MB), so the picker can show it as chosen. */
export function limitChoices(presets, current) {
  const list = [...presets];
  if (Number.isInteger(current) && current > 0 && !list.includes(current)) list.push(current);
  return list.sort((a, b) => a - b);
}
