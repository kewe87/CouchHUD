// Own texts and the store match of an entry: what the edit menu offers and what it refuses before asking the host
// (the host checks the same again: InfoOverrides, StoreMatches).

/** The texts an entry can have of its own; lists are typed as "A, B". */
export const INFO_FIELDS = [
  { id: 'description', max: 1000, list: false },
  { id: 'developers', max: 80, list: true },
  { id: 'publishers', max: 80, list: true },
  { id: 'releaseDate', max: 40, list: false },
  { id: 'genres', max: 80, list: true }
];

export const MAX_LIST_ENTRIES = 8;
export const MAX_QUERY = 100;

// Control characters and invisible formatting (direction marks, zero width): text could read differently than it is.
const UNPLAIN = /[\p{Cc}\p{Cf}]/u;

export const isPlain = (text) => !UNPLAIN.test(String(text ?? ''));

/** "A, B; C" -> ['A', 'B', 'C'] */
export const splitList = (text) => String(text ?? '').split(/[,;]/).map((s) => s.trim()).filter(Boolean);

/** Why a value for a field is refused ('length', 'chars', 'entries') or null; empty means "back to the store's". */
export function infoProblem(fieldId, value) {
  const field = INFO_FIELDS.find((f) => f.id === fieldId);
  if (!field) return 'field';
  const text = String(value ?? '').trim();
  if (!text) return null;
  if (!field.list) return text.length > field.max ? 'length' : isPlain(text) ? null : 'chars';
  const parts = splitList(text);
  if (parts.length > MAX_LIST_ENTRIES) return 'entries';
  if (parts.some((p) => p.length > field.max)) return 'length';
  return parts.every(isPlain) ? null : 'chars';
}

/** The value shown and edited: lists joined back with commas. */
export function fieldText(view, fieldId) {
  const value = view?.[fieldId];
  return Array.isArray(value) ? value.join(', ') : (value ?? '');
}

/** A search the host would take. */
export const queryOk = (text) => {
  const q = String(text ?? '').trim();
  return q.length > 0 && q.length <= MAX_QUERY && isPlain(q);
};

/**
 * Rows of the store match menu. Steam's own entries have none (their app id is fixed). "confirm" shows while a stricter rating of an
 * earlier match is kept and parental controls are on: the parents' code confirms the match as it is.
 */
export function storeRows(store, { parental = false } = {}) {
  if (!store || store.mode === 'steam') return null;
  return {
    search: true,
    none: store.mode !== 'none',
    auto: store.mode !== 'auto',
    confirm: parental && store.ageFloor != null
  };
}

// --- The detail page's store texts -------------------------------------------------------------------------------------------------

/** How long the detail page waits before asking again while the store has not answered for the entry's match (offline, rate limit). */
export const STORE_RETRY_MS = [2000, 5000, 10000];

/** The wait before retry number <attempt> (0-based); null when the page stops asking and says there is no description. */
export const storeRetryDelay = (attempt) => STORE_RETRY_MS[attempt] ?? null;

/**
 * What the description line shows: 'loading' with no answer yet, or while the store's answer for a new match is still being asked for;
 * 'text' with a description; 'none' when the store really has none (or stopped answering for good).
 */
export function descriptionState(details, retrying) {
  if (!details) return 'loading';
  if (details.description) return 'text';
  return details.storePending && retrying ? 'loading' : 'none';
}

/**
 * Latest wins: every read of the details takes a ticket, and only the newest one's answer is shown. An answer for the old store match
 * that arrives after the read for the new one (a slower request, a retry) can then never put the old texts back.
 */
export function loadTickets() {
  let newest = 0;
  return { next: () => ++newest, isNewest: (ticket) => ticket === newest };
}

/**
 * The age rating to show (the one the age limit uses, a kept stricter one included): a whole number from 1 to 21; a floor above that
 * (a stricter rule than any rating) shows as 18, the adult rating, never as nothing. Not a whole number above 0: nothing.
 */
export const ageRating = (value) => (Number.isInteger(value) && value > 0 ? (value <= 21 ? value : 18) : null);

// What a label from the host may look like: a system's name and its step ("USK 12", "PEGI 7", "ESRB E10+", "CERO D", "ClassInd 14").
const AGE_LABEL = /^(USK|PEGI|ESRB|CERO|ClassInd) [0-9A-Z+]{1,4}$/;

/**
 * The badge's text: the active rating system's label when the host sends one that fits the age, else the age ("ab 16" / "16+"). Only with
 * an age to show; a label alone (no age) shows nothing, a label that does not look like one falls back to the age.
 */
export function ageBadge(value, label) {
  const age = ageRating(value);
  if (!age) return null;
  return { age, label: typeof label === 'string' && AGE_LABEL.test(label) && value <= 21 ? label : null };
}

// Systems that are no choice in CouchHUD and only count when the store gives nothing nearer (AgeRatings.Effective): named with their
// country, so "ClassInd 14" does not read like an unknown code (VM test 0.5, 3DMark).
const FOREIGN_SYSTEMS = { ClassInd: 'br', CERO: 'jp' };

/**
 * The badge's text and its title: the label as it is ("USK 12", "PEGI 16"), a foreign system with its country ("ClassInd (Brazil) 14"),
 * else the age. Display only: which rating counts is the host's (the strictest it knows when the active system has none; among equal
 * ages it names PEGI before the foreign ones).
 */
export function badgeText(badge, t) {
  if (!badge) return null;
  if (!badge.label) return { text: t('detail.ageBadge', { age: badge.age }), title: t('detail.ageTitle', { age: badge.age }) };
  const [system, step] = badge.label.split(' ');
  const country = FOREIGN_SYSTEMS[system];
  // i18n-used: detail.ageCountry.br detail.ageCountry.jp
  const countryKey = `detail.ageCountry.${country}`;
  const text = country ? t('detail.ageForeign', { system, country: t(countryKey), step }) : badge.label;
  return { text, title: t('detail.ageTitleLabel', { label: text }) };
}
