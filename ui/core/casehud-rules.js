// Pure rules of the CaseHUD page (tested in scripts/ui-logic-check.mjs). Everything CaseHUD sends is untrusted text: shown with
// textContent only, and capped here as well as on the host.

const MAX_NAMES = 8;
const MAX_NAME = 64;

/**
 * Id of the saved scene on screen. `status.scene` (CaseHUD 1.0) says it whoever set it, "" for none; older builds lack the field,
 * then the scene the list marks active, then the rotation's scene.
 */
export function activeSceneId(status, scenes) {
  if (typeof status?.scene === 'string') return status.scene;
  const listed = Array.isArray(scenes) ? scenes.find((s) => s?.active)?.id : undefined;
  if (typeof listed === 'string' && listed) return listed;
  return typeof status?.rotation?.scene === 'string' ? status.rotation.scene : '';
}

/** A scene's name from the list, else its id; capped. */
export function sceneLabel(id, scenes) {
  const name = Array.isArray(scenes) ? scenes.find((s) => s?.id === id)?.name : undefined;
  return String(typeof name === 'string' && name ? name : id ?? '').slice(0, MAX_NAME);
}

/** The programs CaseHUD says may hold its devices: strings only, no empties or doubles, at most 8 names of 64 characters. */
export function conflictNames(list) {
  if (!Array.isArray(list)) return [];
  const names = [];
  for (const entry of list) {
    if (typeof entry !== 'string') continue;
    const name = entry.trim().slice(0, MAX_NAME);
    if (name && !names.some((n) => n.toLowerCase() === name.toLowerCase())) names.push(name);
    if (names.length >= MAX_NAMES) break;
  }
  return names;
}
