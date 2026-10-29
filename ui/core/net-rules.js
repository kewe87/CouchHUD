// Pure rules of Settings > Privacy (views/settings.js): what the page shows while Offline is on, and which switches follow a change.
// The host decides for real (NetPolicy in Core); this only keeps the page honest. No DOM, no host calls, so ui-logic-check can test it.

/** The switches Offline overrides: every setting of the page except Offline itself. */
export const NET_KEYS = ['selfUpdateCheck', 'netCatalog', 'catalogUpdateCheck', 'pluginAutoUpdate', 'netSystemUpdates', 'onlineArtwork', 'netSaves', 'netWeather', 'netNews', 'netAssistant'];

/** Is the switch shown as on? Offline shows every switch below it as off. */
export const privacyShown = (settings, key) => (key === 'offline' ? !!settings?.offline : !settings?.offline && !!settings?.[key]);

/** Can the switch be changed? Not while Offline is on (except Offline itself), the plugin update search not while the catalog is off, the automatic update not while the search is off. */
export const privacyDisabled = (settings, key) => {
  if (key === 'offline') return false;
  if (settings?.offline) return true;
  if (key === 'pluginAutoUpdate') return settings?.netCatalog === false || settings?.catalogUpdateCheck === false;
  return key === 'catalogUpdateCheck' && settings?.netCatalog === false;
};

/**
 * Why the switch cannot be changed, for the message: 'offline' (Offline is on), 'catalog' (the catalog is off) or 'search' (the plugin update
 * search is off); null when it can be changed. Offline wins, then the catalog, then the search.
 */
export const privacyDisabledReason = (settings, key) => {
  if (key === 'offline') return null;
  if (settings?.offline) return 'offline';
  if (settings?.netCatalog === false && (key === 'catalogUpdateCheck' || key === 'pluginAutoUpdate')) return 'catalog';
  if (key === 'pluginAutoUpdate' && settings?.catalogUpdateCheck === false) return 'search';
  return null;
};

/** Other values that change with a switch (the host does the same when it saves): no plugin update search, no automatic plugin update. */
export const privacyAfter = (key, value) => ((key === 'netCatalog' || key === 'catalogUpdateCheck') && !value ? { pluginAutoUpdate: false } : {});

/** The wizard's update choices: the sub-option (automatic plugin updates) only counts while the plugins are updated at all. */
export const updateChoices = (choices) => ({
  selfUpdateCheck: !!choices.selfUpdateCheck,
  catalogUpdateCheck: !!choices.catalogUpdateCheck,
  pluginAutoUpdate: !!choices.catalogUpdateCheck && !!choices.pluginAutoUpdate
});
