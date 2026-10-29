// What the host knows about updates (Windows Update, winget, CouchHUD itself, plugins and skins): the state, its events and the
// ways to start them. The Downloads tab shows and starts all of it; the host checks quietly in the background.

import { api } from '../core/api.js';
import { attempt, toast } from '../core/feedback.js';
import { currentView, render } from '../core/nav.js';
import { state } from '../core/state.js';
import { confirmDialog } from '../components/overlay.js';
import { t } from '../core/i18n.js';

state.updates = null;
/** Plugins and skins with a newer version in the catalog (the host checks in the background). */
state.catalogUpdates = [];
/** CouchHUD itself: newer release on GitHub (the host checks in the background). */
state.selfUpdate = null;
/** When the page last heard of each kind (ms): a download that finished later hides its update until the next news (Downloads tab). */
state.updatesSeen = { updates: 0, selfUpdate: 0, catalogUpdates: 0 };

function seen(kind) {
  state.updatesSeen = { ...state.updatesSeen, [kind]: Date.now() };
}

/** Pending updates, for badges in the settings and the guide menu. */
export function updateCount() {
  const u = state.updates;
  return (u ? u.windows.length + u.apps.length : 0) + (state.catalogUpdates?.length ?? 0) + (state.selfUpdate?.available ? 1 : 0);
}

/** The Downloads tab shows this state. */
function redrawIfShown() {
  if (currentView()?.id === 'downloads' && !state.overlay) render();
}

export async function startUpdates() {
  api.onUpdates?.((u) => {
    state.updates = u;
    seen('updates');
    // New updates arrive as a notification from the host.
    redrawIfShown();
  });
  api.onCatalogUpdates?.((list) => {
    state.catalogUpdates = list ?? [];
    seen('catalogUpdates');
    redrawIfShown();
  });
  api.onSelfUpdate?.((s) => {
    state.selfUpdate = s;
    seen('selfUpdate');
    redrawIfShown();
  });
  state.selfUpdate = (await attempt(() => api.getSelfUpdate?.())) ?? null;
  seen('selfUpdate');
  state.updates = (await attempt(() => api.getUpdates())) ?? null;
  seen('updates');
  state.catalogUpdates = (await attempt(() => api.getCatalogUpdates?.())) ?? [];
  seen('catalogUpdates');
}

/** Starts Windows or app updates: the Downloads tab's way (the host checks the lock, profile and parents again). */
export async function startUpdate(kind, id, question, label) {
  if (state.updates?.busy) return toast(t('updates.alreadyUpdating'));
  if (question && !(await confirmDialog(question, label))) return;
  await attempt(() => api.startUpdate(kind, id), t('updates.notStarted'));
}

/** Installs CouchHUD's own update after asking: the Downloads tab's way. */
export async function installSelfUpdate() {
  const s = state.selfUpdate;
  if (!s?.available || s.busy) return toast(t('updates.alreadyBusy'));
  if (!s.ready) return toast(s.problem ?? t('updates.notInstallable'));
  if (!s.canInstall) return toast(t('updates.installedOnly'));
  if (state.session) return toast(t('updates.notDuringGame'));
  const ok = await confirmDialog(t('updates.self.confirm', { version: s.version }), t('updates.install'));
  if (!ok) return;
  await attempt(() => api.startSelfUpdate(), t('updates.notStarted'));
}
