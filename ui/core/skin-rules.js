// Skin ids: the built-in skins were renamed in 0.5 (no third-party names as skin names) and four of them moved to the catalog in 0.6.
// Pure, with a test in scripts/ui-logic-check.mjs.

/** Ids the built-in skins and their sound sets had up to 0.4 (the same map as SkinCatalog.MigrateId in Core). */
export const RENAMED_SKINS = Object.freeze({ ps4: 'deepsea', xbox: 'greenlight', cyberpunk: 'neoncity' });

/** The skins that left the app in 0.6 and live in the catalog; their id on the page is `user:<id>` (the same list as SkinCatalog.MovedToCatalogIds). */
export const MOVED_SKINS = Object.freeze(['deepsea', 'greenlight', 'neoncity', 'nova']);

/** The current id for a sound set id from an older version; any other value as it is (the four sound sets stay in the app). */
export const migrateSoundSetId = (id) => (typeof id === 'string' && Object.hasOwn(RENAMED_SKINS, id) ? RENAMED_SKINS[id] : id);

/** The current id for a skin id from an older version: the 0.4 ids first, then a skin that moved to the catalog becomes `user:<id>`. */
export function migrateSkinId(id) {
  const now = migrateSoundSetId(id);
  return typeof now === 'string' && MOVED_SKINS.includes(now) ? `user:${now}` : now;
}

/** The catalog id of a skin that moved to the catalog (`user:deepsea` → `deepsea`), else null. */
export const movedSkinCatalogId = (id) => (typeof id === 'string' && id.startsWith('user:') && MOVED_SKINS.includes(id.slice(5)) ? id.slice(5) : null);
