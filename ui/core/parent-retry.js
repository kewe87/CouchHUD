// A call the host refused because it needs the parents' code for exactly this action and item (an admin function of a plugin in a child's
// profile): ask for the code, then repeat the call once. The host decides and checks the grant again; this only asks. Cancelling the code
// ends the call quietly (undefined); a second refusal is an error like any other.

import { ensureParentGrant } from '../components/parental.js';
import { t } from './i18n.js';

export async function withParentGrant(call) {
  try {
    return await call();
  } catch (err) {
    if (!err?.parent?.action) throw err;
    // The host names the plugin and the function in its prompt text; the page's own text is the fallback.
    if (!(await ensureParentGrant(err.parent.reason || t('plugin.fn.useReason'), { action: err.parent.action, item: err.parent.item ?? undefined }))) return undefined;
    return call();
  }
}
