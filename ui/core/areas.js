// Switchable feature areas, the page's side (pure, no DOM; tested by scripts/ui-logic-check.mjs). The one map of what belongs
// where is in the host (src/CouchHud.Core/Config/FeatureAreas.cs), which also refuses every call of an area that is off; this
// module only hides what the page would otherwise show. The test checks that both maps agree.

/** All areas, in the order the setup assistant and the settings show them. */
export const AREAS = ['games', 'apps', 'browser', 'news'];

/** Areas that start something: at least one of them stays on. */
export const LAUNCH_AREAS = ['games', 'apps', 'browser'];

/** Icons of the areas (setup assistant, settings dialog). */
export const AREA_ICONS = { games: '🎮', apps: '▦', browser: '🌐', news: '📰' };

/** Text keys of the areas' names and descriptions (setup assistant, settings). */
// i18n-used: settings.areas.games settings.areas.apps settings.areas.browser settings.areas.news
export const AREA_NAME_KEYS = { games: 'settings.areas.games', apps: 'settings.areas.apps', browser: 'settings.areas.browser', news: 'settings.areas.news' };
// i18n-used: settings.areas.gamesDesc settings.areas.appsDesc settings.areas.browserDesc settings.areas.newsDesc
export const AREA_DESC_KEYS = { games: 'settings.areas.gamesDesc', apps: 'settings.areas.appsDesc', browser: 'settings.areas.browserDesc', news: 'settings.areas.newsDesc' };

/** Setting keys that belong to an area (as FeatureAreas.SettingKeys in Core); every other key is "always". */
export const SETTING_AREAS = {
  pauseGameInMenu: 'games',
  steamGridDbApiKey: 'games',
  trailerPreviews: 'games',
  maximizeBrowser: 'browser',
  browserHistory: 'browser'
};

/** Main tabs that belong to an area (by the view's id). Home, downloads, smart home, assistant and settings are always there. */
export const TAB_AREAS = { games: 'games', apps: 'apps', browser: 'browser', news: 'news' };

/** Setup assistant steps that belong to an area: the game clients only make sense with Gaming. */
export const WIZARD_STEP_AREAS = { clients: 'games' };

let enabled = new Set(AREAS);

/** The areas the host reports as on. Missing or broken (an older host): everything on, as before. */
export function setAreas(list) {
  const known = Array.isArray(list) ? list.filter((a) => AREAS.includes(a)) : AREAS;
  enabled = new Set(LAUNCH_AREAS.some((a) => known.includes(a)) ? known : AREAS);
  return enabledAreas();
}

/** Whether an area is on; "always", empty and unknown areas count as on. */
export const areaOn = (area) => !area || area === 'always' || !AREAS.includes(area) || enabled.has(area);

export const enabledAreas = () => AREAS.filter((a) => enabled.has(a));

/** The area of a setting key ("always" for all keys not listed). */
export const areaOfSetting = (key) => SETTING_AREAS[key] ?? 'always';

/** A choice of areas the host would take: known names only, at least one launch area. */
export const validChoice = (list) => Array.isArray(list) && list.every((a) => AREAS.includes(a)) && LAUNCH_AREAS.some((a) => list.includes(a));

/**
 * Switches one area in a choice (wizard, settings dialog). The last launch area cannot be switched off: the choice stays as it was
 * and `blocked` says why. Returns { list, blocked }.
 */
export function toggleArea(list, area) {
  const current = AREAS.filter((a) => list.includes(a));
  if (!AREAS.includes(area)) return { list: current, blocked: false };
  const next = current.includes(area) ? current.filter((a) => a !== area) : AREAS.filter((a) => a === area || current.includes(a));
  return validChoice(next) ? { list: next, blocked: false } : { list: current, blocked: true };
}

/** Rows of a list (settings) without those of switched off areas; a row without `area` is "always". */
export const shownRows = (rows) => rows.filter((r) => areaOn(r?.area));

/** Main tabs without those of switched off areas (views carry their id). */
export const shownTabs = (views) => views.filter((v) => areaOn(TAB_AREAS[v?.id]));

/** Setup assistant steps without those of switched off areas (the choice made in the assistant counts, not the stored one). */
export const shownSteps = (steps, choice) => steps.filter((id) => !WIZARD_STEP_AREAS[id] || choice.includes(WIZARD_STEP_AREAS[id]));

/**
 * The tile at the end of the Home shelf: "All games" with Gaming, else "All apps", else the browser. It always leads somewhere that
 * is on (a shelf is never empty). Returns { id, tab }.
 */
export function homeEndTile() {
  if (areaOn('games')) return { id: '__all', tab: 'games' };
  if (areaOn('apps')) return { id: '__apps', tab: 'apps' };
  return { id: '__browser', tab: 'browser' };
}
