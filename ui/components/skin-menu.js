import { api } from '../core/api.js';
import { state } from '../core/state.js';
import { applySkin } from '../core/skins.js';
import { render } from '../core/nav.js';
import { t } from '../core/i18n.js';
import { BUILTIN_SKINS } from '../skins/index.js';

/**
 * The skins as the host knows them now: a skin copied into %APPDATA%\CouchHud\skins while CouchHUD runs shows up without a restart
 * (the host reads the folder on every call). A failed read keeps the list from before.
 */
export async function reloadSkins() {
  try {
    const user = await api.listSkins();
    if (Array.isArray(user)) state.skins = [...BUILTIN_SKINS, ...user];
  } catch (err) {
    console.error(err);
  }
  return state.skins;
}

/** Skin picker with live preview while browsing; B reverts. The list is read again first (see reloadSkins). */
export async function skinMenu() {
  await reloadSkins();
  const original = state.skin;
  return {
    title: t('skinMenu.title'),
    // The screen behind stays visible: moving through the list shows each skin live.
    preview: true,
    hints: () => [['↕', t('skinMenu.hintPreview')], ['A', t('skinMenu.hintApply')], ['B', t('skinMenu.hintBack')]],
    // By id: a skin read again is a new object.
    index: Math.max(0, state.skins.findIndex((s) => s.id === original?.id)),
    onMove: (i) => applySkin(state.skins[i]),
    onCancel: () => applySkin(original),
    entries: state.skins.map((skin) => ({
      label: skin.name,
      detail: skin.description,
      run: async () => {
        applySkin(skin);
        await api.setSkin(skin.id);
        render();
      }
    }))
  };
}
