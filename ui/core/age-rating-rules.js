// Age rating systems for the age limit (mirrors CouchHud.Core.Parental.AgeRatings; the host decides, this only shows and offers).
// The limit is stored as an age; the picker offers the active system's steps, the labels name them in that system.

/** What the parents can choose: by the Windows region, or one system. */
export const RATING_CHOICES = ['auto', 'usk', 'pegi', 'esrb'];

const STEPS = {
  usk: [6, 12, 16, 18],
  pegi: [3, 7, 12, 16, 18],
  // No AO step: an 18 would let every M game through (an AO game stays above every step).
  esrb: [6, 10, 13, 17]
};

// No single system for the region ("strictest"): plain ages.
const PLAIN_STEPS = [3, 6, 12, 16, 18];

const ESRB = { 3: 'EC', 6: 'E', 10: 'E10+', 13: 'T', 17: 'M' };

/** The steps the picker offers for a system (0, off, comes on top). */
export const ratingSteps = (system) => (Object.hasOwn(STEPS, system ?? '') ? STEPS[system] : PLAIN_STEPS);

/** A known system the host resolved to; anything else is read as "strictest". */
export const activeSystem = (system) => (Object.hasOwn(STEPS, system ?? '') ? system : 'strictest');

/** The label of a limit in a system ("USK 12", "PEGI 7", "ESRB T"), or null when the value is no step of it (shown as an age). */
export function limitLabel(system, age) {
  if (!Number.isInteger(age) || age <= 0 || !Object.hasOwn(STEPS, system ?? '') || !STEPS[system].includes(age)) return null;
  if (system === 'esrb') return `ESRB ${ESRB[age]}`;
  return `${system.toUpperCase()} ${age}`;
}

/**
 * The limit after a switch to another system: the nearest step at or below the old one, never looser. Below the lowest step the old age
 * stays (stricter than all of them); off stays off. Mirrors AgeRatings.ConvertLimit (the host does the real conversion).
 */
export function convertLimit(limit, system) {
  if (!limit) return 0;
  const lower = ratingSteps(system).filter((s) => s <= limit);
  return lower.length ? Math.max(...lower) : limit;
}

/** The texts the picker and the rows use, from the translation function. */
export const ageLabels = (t) => ({
  off: t('settings.common.off'),
  block: (label) => t('settings.parental.blockAge', { label }),
  blockYears: (age) => t('settings.parental.blockAgeYears', { age }),
  value: (label) => t('settings.parental.ageValue', { label }),
  valueYears: (age) => t('settings.parental.ageValueYears', { age })
});

/** The picker's options: off, then each step with its label in the active system (or "from age n"). */
export function ageChoices(system, labels) {
  return [
    { label: labels.off, value: 0 },
    ...ratingSteps(system).map((age) => ({
      label: limitLabel(system, age) ? labels.block(limitLabel(system, age)) : labels.blockYears(age),
      value: age
    }))
  ];
}

/**
 * Which system and age the age limit row names. Normally the active system. After a change of the Windows region under "auto" (saved:
 * the system the parents saved the limit under, which still counts next to the active one) the system it is stored in, with its value
 * there (maxAgeSaved, from the host), so the row and the hint below it ("saved under USK") say the same thing. Display only: the host
 * judges games by both systems either way.
 */
export function shownLimit({ active, saved, maxAge, maxAgeSaved }) {
  if (saved && maxAge && Number.isInteger(maxAgeSaved) && maxAgeSaved > 0) return { system: activeSystem(saved), age: maxAgeSaved, saved: true };
  return { system: activeSystem(active), age: maxAge, saved: false };
}

/** The text of a set limit: "Blocked from USK 12" or, off the active system's steps, "Blocked from age 7". */
export const limitText = (system, age, labels) =>
  !age ? labels.off : limitLabel(system, age) ? labels.value(limitLabel(system, age)) : labels.valueYears(age);
