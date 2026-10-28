// Skin ids: the built-in skins were renamed in 0.5 (no third-party names as skin names). Pure, with a test in scripts/ui-logic-check.mjs.

/** Ids the built-in skins and their sound sets had up to 0.4 (the same map as SkinCatalog.MigrateId in Core). */
export const RENAMED_SKINS = Object.freeze({ ps4: 'deepsea', xbox: 'greenlight', cyberpunk: 'neoncity' });

/** The current id for a skin or sound set id from an older version; any other value as it is. */
export const migrateSkinId = (id) => (typeof id === 'string' && Object.hasOwn(RENAMED_SKINS, id) ? RENAMED_SKINS[id] : id);
