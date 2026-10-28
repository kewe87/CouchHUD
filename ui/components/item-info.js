// "Infos" and "Store entry" in the edit menu: own texts for an entry, and which store entry its description, rating and art come from.

import { api } from '../core/api.js';
import { attempt, toast } from '../core/feedback.js';
import { forgetStoreInfo } from '../core/media.js';
import { render } from '../core/nav.js';
import { state } from '../core/state.js';
import { chooseDialog, openOverlay } from './overlay.js';
import { openKeyboard } from './keyboard.js';
import { ensureParentGrant, parental } from './parental.js';
import { ensureUnlocked } from './passcode.js';
import { t } from '../core/i18n.js';
import { INFO_FIELDS, fieldText, infoProblem, queryOk, storeRows } from '../core/item-info-rules.js';

// i18n-used: itemInfo.field.description itemInfo.field.developers itemInfo.field.publishers itemInfo.field.releaseDate itemInfo.field.genres
// i18n-used: itemInfo.problem.length itemInfo.problem.chars itemInfo.problem.entries itemInfo.problem.field
const fieldLabel = (field) => {
  const key = `itemInfo.field.${field.id}`;
  return t(key);
};
const problemText = (problem) => {
  const key = `itemInfo.problem.${problem}`;
  return t(key);
};

/**
 * The detail page reads its texts again (and the focus preview forgets what it had). Every open detail page of the entry, not only the
 * top one (a code entry or a dialog may still be closing); its newest read wins over one that was still on its way.
 */
function refreshDetails(item) {
  forgetStoreInfo(item.id);
  for (const page of state.stack) {
    if (page?.id === 'detail' && page.item?.id === item.id && typeof page.refresh === 'function') page.refresh();
  }
  render();
}

/** "Infos": description, developer, publisher, release date and genres, each with "back to the store's". */
export async function infoMenu(item) {
  if (!(await ensureUnlocked(t('itemInfo.guard')))) return;
  const view = await attempt(() => api.getItemInfo(item.id), t('itemInfo.failed'));
  if (!view) return;
  const custom = new Set(view.custom ?? []);
  const entries = INFO_FIELDS.map((field) => {
    const value = fieldText(view, field.id);
    return {
      label: fieldLabel(field),
      detail: (value || t('itemInfo.empty')) + (custom.has(field.id) ? ` · ${t('itemInfo.own')}` : ''),
      run: () => editField(item, field, value, custom.has(field.id))
    };
  });
  entries.push({ label: t('itemInfo.close'), run: () => {} });
  openOverlay({ title: t('itemInfo.title', { name: item.name }), message: t('itemInfo.note'), entries });
}

async function editField(item, field, value, isCustom) {
  let choice = 'edit';
  if (isCustom) {
    choice = await chooseDialog(fieldLabel(field), [
      { label: t('itemInfo.edit'), icon: '✎', value: 'edit' },
      { label: t('itemInfo.reset'), detail: t('itemInfo.resetDetail'), icon: '↻', value: 'reset' }
    ]);
  }
  if (choice === 'reset') return saveField(item, field, null, t('itemInfo.resetDone'));
  if (choice !== 'edit') return;
  const text = await openKeyboard({
    title: fieldLabel(field),
    value,
    hint: field.list ? t('itemInfo.listHint') : '',
    maxLength: field.list ? 0 : field.max
  });
  if (text === undefined) return;
  const problem = infoProblem(field.id, text);
  if (problem) {
    toast(problemText(problem));
    return;
  }
  // Empty goes back to the store's text.
  await saveField(item, field, text.trim() || null, text.trim() ? t('itemInfo.saved') : t('itemInfo.resetDone'));
}

async function saveField(item, field, value, message) {
  const view = await attempt(() => api.setItemInfo(item.id, { [field.id]: value }), t('itemInfo.failed'));
  if (!view) return;
  toast(message);
  refreshDetails(item);
}

/**
 * The store match decides the age rating: the unlock, and with parental controls on the parents' code for exactly this entry, right before
 * the change. The host checks the same again (Hud.SetStoreMatchAsync) and keeps the stricter rating when no code confirmed it.
 */
async function guardStoreMatch(item) {
  if (!(await ensureUnlocked(t('itemInfo.guard')))) return false;
  return !parental.status?.enabled
    || !!(await ensureParentGrant(t('itemInfo.store.guardParental'), { action: 'storeMatch', item: item.id }));
}

async function saveMatch(item, match, name, message) {
  if (!(await guardStoreMatch(item))) return;
  const view = await attempt(() => api.setStoreMatch(item.id, match, name ?? null), t('itemInfo.failed'));
  if (!view) return;
  toast(message);
  refreshDetails(item);
}

function currentLabel(store) {
  if (store.mode === 'manual') return store.name ? t('itemInfo.store.manual', { name: store.name }) : t('itemInfo.store.manualId', { id: store.appId });
  if (store.mode === 'none') return t('itemInfo.store.none');
  return t('itemInfo.store.auto');
}

/** "Store entry": search the store and pick the right entry, say no entry fits, or go back to automatic. */
export async function storeMatchMenu(item) {
  if (!(await ensureUnlocked(t('itemInfo.guard')))) return;
  const view = await attempt(() => api.getItemInfo(item.id), t('itemInfo.failed'));
  if (!view) return;
  const store = view.store;
  const rows = storeRows(store, { parental: !!parental.status?.enabled });
  if (!rows) {
    toast(t('itemInfo.store.steam'));
    return;
  }
  const entries = [{
    label: t('itemInfo.store.search'),
    detail: t('itemInfo.store.searchDetail'),
    icon: '🔍',
    run: () => searchFlow(item)
  }];
  if (rows.none) entries.push({ label: t('itemInfo.store.pickNone'), detail: t('itemInfo.store.pickNoneDetail'), icon: '∅', run: () => saveMatch(item, 'none', null, t('itemInfo.store.noneDone')) });
  if (rows.auto) entries.push({ label: t('itemInfo.store.pickAuto'), detail: t('itemInfo.store.pickAutoDetail'), icon: '↻', run: () => saveMatch(item, null, null, t('itemInfo.store.autoDone')) });
  if (rows.confirm) {
    entries.push({
      label: t('itemInfo.store.confirm'),
      detail: t('itemInfo.store.confirmDetail', { age: store.ageFloor }),
      icon: '✓',
      run: () => saveMatch(item, store.mode === 'manual' ? store.appId : store.mode === 'none' ? 'none' : null, store.name, t('itemInfo.store.confirmDone'))
    });
  }
  entries.push({ label: t('itemInfo.close'), run: () => {} });
  const floor = store.ageFloor != null ? ` ${t('itemInfo.store.floor', { age: store.ageFloor })}` : '';
  openOverlay({ title: t('itemInfo.store.title', { name: item.name }), message: currentLabel(store) + floor, entries });
}

async function searchFlow(item) {
  const query = await openKeyboard({ title: t('itemInfo.store.searchTitle'), value: item.name, maxLength: 100 });
  if (query === undefined) return;
  if (!queryOk(query)) {
    toast(t('itemInfo.store.queryBad'));
    return;
  }
  const hits = await attempt(() => api.searchStore(query.trim()), t('itemInfo.store.searchFailed'));
  if (!hits) return;
  if (hits.length === 0) {
    toast(t('itemInfo.store.noHits'));
    return;
  }
  const pick = await chooseDialog(t('itemInfo.store.results'), hits.map((h) => ({ label: h.name, detail: `Steam ${h.appId}`, value: h })));
  if (pick) await saveMatch(item, pick.appId, pick.name, t('itemInfo.store.pickedDone', { name: pick.name }));
}
