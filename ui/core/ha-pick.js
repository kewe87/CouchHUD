// Pure rules of the Smart Home picker (no DOM): filter, groups, order. Tested by scripts/ui-logic-check.mjs.

/** Same cap as the host (HomeAssistantSelection.MaxEntities). */
export const MAX_SELECTED = 500;

/** Entity types for the filter chips, in this order. */
export const CATEGORIES = [
  ['light', ['light']],
  ['switch', ['switch', 'input_boolean']],
  ['fan', ['fan']],
  ['climate', ['climate']],
  ['cover', ['cover']],
  ['media', ['media_player']],
  ['scene', ['scene', 'script']],
  ['lock', ['lock']],
  ['sensor', ['sensor', 'binary_sensor', 'weather']],
  ['other', ['automation', 'button', 'input_button']]
];

export const categoryOf = (domain) => CATEGORIES.find(([, domains]) => domains.includes(domain))?.[0] ?? 'other';

/**
 * Pure filter for the list: chip ('suggested' | 'selected' | 'all' | category), search text, current choice.
 * A search looks through everything (sensors too) unless a type or "chosen" is picked. Kept apart from the DOM for the logic check.
 * In "chosen", ids Home Assistant does not list right now (offline, renamed) stay as placeholders marked missing, so they can be dropped.
 */
export function filterEntities(entities, { chip, query, selected }) {
  const q = (query ?? '').trim().toLowerCase();
  const chosen = new Set(selected);
  let list;
  if (chip === 'selected') {
    const byId = new Map(entities.map((e) => [e.id, e]));
    list = selected.map((id) => byId.get(id) ?? { id, domain: id.split('.')[0], name: id, state: '', missing: true });
  } else if (chip === 'all' || (chip === 'suggested' && q)) {
    list = entities;
  } else if (chip === 'suggested') {
    list = entities.filter((e) => e.suggested || chosen.has(e.id));
  } else {
    list = entities.filter((e) => categoryOf(e.domain) === chip);
  }
  if (q) list = list.filter((e) => `${e.name} ${e.id} ${e.area ?? ''}`.toLowerCase().includes(q));
  return list;
}

/** Groups for the list: by room when Home Assistant told us rooms, else by type. "Chosen" keeps its order, ungrouped. */
export function groupEntities(list, { byArea, keepOrder, noArea, categoryName }) {
  if (keepOrder) return [{ title: null, items: list }];
  const groups = new Map();
  for (const e of list) {
    const key = byArea ? e.area || '' : categoryOf(e.domain);
    if (!groups.has(key)) groups.set(key, []);
    groups.get(key).push(e);
  }
  const order = CATEGORIES.map(([c]) => c);
  return [...groups.entries()]
    .sort(([a], [b]) => (byArea
      ? (a === '') - (b === '') || a.localeCompare(b, undefined, { numeric: true })
      : order.indexOf(a) - order.indexOf(b)))
    .map(([key, items]) => ({
      title: byArea ? key || noArea : categoryName(key),
      items: items.slice().sort((x, y) => order.indexOf(categoryOf(x.domain)) - order.indexOf(categoryOf(y.domain)) || x.name.localeCompare(y.name, undefined, { numeric: true }))
    }));
}

/**
 * Moves one id inside the order past its neighbour in the visible list (a search may hide some chosen ids; they keep their place).
 * Returns a new array, or the same one at the ends.
 */
export function moveInOrder(order, id, delta, visible = order) {
  const at = visible.indexOf(id);
  const neighbour = visible[at + delta];
  const from = order.indexOf(id);
  const to = order.indexOf(neighbour);
  if (at < 0 || from < 0 || neighbour === undefined || to < 0) return order;
  const next = order.slice();
  [next[from], next[to]] = [next[to], next[from]];
  return next;
}
