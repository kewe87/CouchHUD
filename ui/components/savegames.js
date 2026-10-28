// i18n-used: savegames.notBackedUp savegames.notRestored savegames.restoreReason savegames.restoreReasonNamed savegames.deleteReason savegames.deleteReasonNamed
// Save games: summary on the detail page, backup/restore page, backup offer before uninstalling
// and the settings block. Locations come from the Ludusavi manifest (community database).

import { iconEl } from '../core/glyph-icons.js';
import { api } from '../core/api.js';
import { el, formatSize, keepInView, setFocused } from '../core/dom.js';
import { attempt, toast } from '../core/feedback.js';
import { move, push, render } from '../core/nav.js';
import { sound } from '../sound.js';
import { t, fmtDateTime } from '../core/i18n.js';
import { chooseDialog, confirmDialog } from './overlay.js';
import { openFileBrowser } from './file-browser.js';
import { ensureUnlocked, isLocked } from './passcode.js';
import { ensureParentGrant, parental, refreshParental } from './parental.js';
import { admitSaveChange } from '../core/continue-rules.js';
import { admitProtectedChange } from '../core/lock-rules.js';
import { GAME_LIMITS_MB, TOTAL_LIMITS_MB, limitChoices } from '../core/size.js';

const dateTime = (ms) => fmtDateTime(ms);

/** Restoring or deleting a backup changes saves for good: the couch must be unlocked, with parental controls also the parents' code. */
async function admitChange(action, item) {
  const status = parental.status ?? (await refreshParental());
  return admitSaveChange({
    action,
    id: item.id,
    parentalOn: !!status?.enabled,
    reason: t(action === 'restoreSaves' ? 'savegames.restoreReason' : 'savegames.deleteReason'),
    parentReason: t(action === 'restoreSaves' ? 'savegames.restoreReasonNamed' : 'savegames.deleteReasonNamed', { name: item.name }),
    ensureUnlocked,
    ensureParentGrant
  });
}

/** One line for the facts list on the detail page; null while unknown or without saves. */
export function saveSummary(info) {
  if (!info?.supported) return null;
  if (!info.fileCount) return t('savegames.none');
  const parts = [t('savegames.files', { count: info.fileCount }), formatSize(info.totalBytes)];
  if (info.lastWrite) parts.push(t('savegames.lastWrite', { date: dateTime(info.lastWrite) }));
  if (info.backups?.length) parts.push(t('savegames.backups', { count: info.backups.length }));
  return parts.filter(Boolean).join(' · ');
}

/** Detail-page action for games the manifest knows; null otherwise. */
export function saveAction(item, info, onChange) {
  if (item.type === 'app' || !info?.supported) return null;
  return { label: t('savegames.action'), icon: '💾', run: () => push(savesPage(item, info, onChange)) };
}

function reportErrors(result, what) {
  if (!result.errors?.length) return;
  console.warn(`${what}:`, result.errors);
  toast(t(what === 'Sicherung' ? 'savegames.notBackedUp' : 'savegames.notRestored', { count: result.errors.length, error: result.errors[0] }), 7000);
}

const BIG_BYTES = 1_000_000_000;

/**
 * Backs up the saves. Resolves to { ok, clean }: ok = a ZIP exists, clean = nothing went wrong
 * (no unreadable files, nothing cut off). Asks first when the saves are 1 GB or more.
 */
export async function backupNow(item, info = null) {
  if (info?.totalBytes >= BIG_BYTES
      && !(await confirmDialog(t('savegames.bigConfirm', { size: formatSize(info.totalBytes) }), t('savegames.bigButton')))) {
    return { ok: false, clean: false, cancelled: true };
  }
  toast(t('savegames.backingUp'), 15000);
  const result = await attempt(() => api.backupSaves(item.id), t('savegames.backupFailed'));
  if (!result) return { ok: false, clean: false };
  if (!result.id) {
    toast(t('savegames.nothingBackedUp', { reason: result.errors?.[0] ?? t('savegames.noFiles') }), 6000);
    return { ok: false, clean: false };
  }
  if (result.unchanged && result.complete) {
    // Nothing changed since the last complete backup: that one still holds, no new version.
    toast(t('savegames.unchanged'), 4000);
    return { ok: true, clean: true };
  }
  if (result.unchanged) {
    // The same incomplete state as the last incomplete backup: that one is kept, no new version. Still incomplete, so not clean
    // (before an uninstall the person confirms).
    console.warn('Backup incomplete, unchanged:', result.errors);
    toast(t('savegames.unchangedIncomplete', { reason: result.errors?.[0] ?? t('savegames.errorFallback') }), 9000);
    return { ok: true, clean: false };
  }
  if (!result.complete || result.errors?.length) {
    console.warn('Backup incomplete:', result.errors);
    toast(t('savegames.incomplete', { count: result.fileCount, reason: result.errors?.[0] ?? t('savegames.errorFallback') }), 9000);
    return { ok: true, clean: false };
  }
  toast(t('savegames.backedUp', { count: result.fileCount, size: formatSize(result.bytes) ?? '0 B' }), 4000);
  return { ok: true, clean: true };
}

/**
 * Before uninstalling: offer a backup. Resolves true to go on with the uninstall, false to stop.
 * Whenever the saves might be unprotected (database or check unavailable, backup failed or
 * incomplete) the user has to confirm explicitly; the default is to cancel.
 */
export async function offerBackupBeforeUninstall(item) {
  let info = null;
  try {
    info = await api.getSaveInfo(item.id);
  } catch (err) {
    console.warn('saves', err);
  }
  if (!info || info.unavailable) {
    return confirmDialog(t('savegames.uncheckable'),
      t('savegames.uninstallAnyway'), { danger: true });
  }
  if (!info.supported || !info.fileCount) return true;
  const choice = await chooseDialog(t('savegames.offerTitle', { name: item.name, count: info.fileCount }), [
    { label: t('savegames.offerBackup'), detail: t('savegames.offerBackupDetail', { size: formatSize(info.totalBytes) ?? '', dir: info.backupDir }).trim(), value: 'backup' },
    { label: t('savegames.offerSkip'), detail: t('savegames.offerSkipDetail'), value: 'skip' },
    { label: t('savegames.cancel'), value: 'cancel' }
  ], 'backup');
  if (choice === 'skip') return true;
  if (choice !== 'backup') return false;
  // "Unchanged" points to a backup by hand with exactly these files: that one holds the state, nothing new is rotated in.
  const result = await backupNow(item, info);
  if (result.cancelled) return false;
  if (result.clean) return true;
  // A failed or incomplete backup must not silently lead to losing the saves.
  return confirmDialog(result.ok
    ? t('savegames.incompleteConfirm')
    : t('savegames.failedConfirm'), t('savegames.uninstallAnyway'), { danger: true });
}

/** Saves page: where they are, how big, backup and restore. */
export function savesPage(item, initial, onChange) {
  return {
    id: 'saves',
    name: t('savegames.pageName'),
    index: 0,
    info: initial,
    busy: false,
    async reload() {
      const info = await attempt(() => api.getSaveInfo(item.id), t('savegames.loadFailed'));
      if (info) {
        this.info = info;
        onChange?.(info);
      }
      render();
    },
    rows() {
      const i = this.info;
      const backups = i.backups ?? [];
      return [
        { icon: '⤓', label: t('savegames.backupNow'), detail: i.fileCount ? t('savegames.backupNowDetail', { count: i.fileCount, dir: i.backupDir }) : t('savegames.backupNowEmpty'), run: () => this.backup() },
        { icon: '↺', label: t('savegames.restore'), detail: backups.length ? t('savegames.restoreDetail', { count: backups.length, incomplete: backups.some((b) => !b.complete) ? t('savegames.restoreIncomplete') : '' }) : t('savegames.restoreNone'), run: () => this.restore() },
        ...(backups.length ? [{ icon: '🗑', label: t('savegames.delete'), detail: t('savegames.deleteDetail'), run: () => this.remove() }] : [])
      ];
    },
    build(root) {
      const i = this.info;
      this.list = this.rows();
      this.index = Math.min(this.index, this.list.length - 1);
      this.items = this.list.map((r) => el('li', { className: 'check-row update-row highlight' },
        el('span', { className: 'check-box' }, iconEl(r.icon)),
        el('span', { className: 'check-main' }, el('strong', { textContent: r.label }), el('small', { textContent: r.detail }))));
      this.scroller = el('ul', { className: 'check-list list-scroll' }, this.items);

      const facts = [saveSummary(i) ?? t('savegames.none')];
      const places = (i.locations ?? []).map((l) => el('li', { textContent: l }));
      root.append(el('div', { className: 'files' },
        el('div', { className: 'files-head' },
          el('h2', { textContent: t('savegames.title', { name: item.name }) }),
          el('p', { className: 'meta', textContent: facts[0] }),
          el('p', { className: 'meta', textContent: t('savegames.locations') }),
          el('ul', { className: 'meta' }, places),
          ...(i.warnings?.length ? [el('p', { className: 'meta', textContent: t('savegames.warning', { warning: i.warnings[0] }) })] : []),
          ...(i.registry?.length ? [el('p', { className: 'meta', textContent: t('savegames.registry', { count: i.registry.length }) })] : [])),
        this.scroller));
      this.update({ instant: true });
    },
    update({ instant = false } = {}) {
      if (!this.items?.length) return;
      setFocused(this.items, this.index);
      keepInView(this.scroller, this.items[this.index], { instant });
    },
    async backup() {
      if (this.busy) return;
      this.busy = true;
      try {
        await backupNow(item, this.info);
      } finally {
        this.busy = false;
      }
      await this.reload();
    },
    async pickBackup(title) {
      const backups = this.info.backups ?? [];
      if (!backups.length) {
        toast(t('savegames.noBackup'));
        return undefined;
      }
      return chooseDialog(title, backups.map((b) => ({
        label: dateTime(b.created) + (b.safety ? t('savegames.pickAuto') : '') + (b.auto ? t('savegames.pickAfterGame') : '') + (b.complete ? '' : t('savegames.pickIncomplete')),
        detail: t('savegames.pickDetail', { count: b.fileCount, size: formatSize(b.sizeBytes) ?? '' }) + (b.complete ? '' : t('savegames.pickDetailErrors')),
        value: b.id
      })));
    },
    async restore() {
      if (this.busy) return;
      const id = await this.pickBackup(t('savegames.pickRestore'));
      if (!id) return;
      const b = this.info.backups.find((x) => x.id === id);
      const note = b.complete ? '' : t('savegames.restoreWarn');
      if (!(await confirmDialog(t('savegames.restoreConfirm', { date: dateTime(b.created), note }), t('savegames.restore'), { danger: true }))) return;
      if (!(await admitChange('restoreSaves', item))) return;
      this.busy = true;
      try {
        toast(t('savegames.restoring'), 15000);
        const result = await attempt(() => api.restoreSaves(item.id, id), t('savegames.restoreFailed'));
        if (result?.rolledBack) {
          toast(t('savegames.rolledBack', { error: result.errors?.[0] ?? '' }), 9000);
        } else if (result?.errors?.length && !result.restored) {
          toast(t('savegames.rollbackIncomplete', { id: result.safetyBackupId, error: result.errors[0] }), 12000);
        } else if (result) {
          if (result.errors?.length) reportErrors(result, 'Wiederherstellung');
          else toast(t('savegames.restored', { count: result.restored }), 4000);
        }
      } finally {
        this.busy = false;
      }
      await this.reload();
    },
    async remove() {
      const id = await this.pickBackup(t('savegames.pickDelete'));
      if (!id) return;
      if (!(await confirmDialog(t('savegames.deleteConfirm'), t('savegames.deleteButton'), { danger: true }))) return;
      if (!(await admitChange('deleteSaveBackup', item))) return;
      await attempt(() => api.deleteSaveBackup(item.id, id), t('savegames.deleteFailed'));
      await this.reload();
    },
    handle(action) {
      switch (action) {
        case 'up': return move(this, -1, this.list.length, true);
        case 'down': return move(this, 1, this.list.length, true);
        case 'a':
          sound.select();
          return this.list[this.index]?.run();
      }
    },
    pointer(node, index, dispatch) {
      this.index = index;
      this.update();
      dispatch('a');
    },
    hints() {
      return [['A', t('savegames.hintSelect')], ['B', t('savegames.hintBack')]];
    }
  };
}

// --- Settings block -----------------------------------------------------

let saveSettings = null;
let loading = false;

async function loadSettings() {
  if (loading) return;
  // Locked, the host refuses to read them; the rows show "loading" until the next visit after the unlock.
  if (await isLocked()) return;
  loading = true;
  const s = await attempt(() => api.getSaveSettings(), t('savegames.settingsLoadFailed'), { quietLocked: true });
  loading = false;
  if (s) {
    saveSettings = s;
    render();
  }
}

/** Settings rows for "Spielstände" (spread into a category's items). */
export function saveSettingsItems() {
  if (!saveSettings) loadSettings();
  const s = saveSettings;
  const update = async (key, value, text) => {
    // These settings decide whether backups exist at all: the unlocked couch, with parental controls then the parents' code (the host
    // checks both again and refuses every one of them on a locked couch, so the unlock comes first).
    const parentalOn = key !== 'includeConfig' && !!parental.status?.enabled;
    if (!(await admitProtectedChange({ reason: t('savegames.settingsReason'), parentalOn, scope: { action: 'saveSettings' }, ensureUnlocked, ensureParentGrant }))) return;
    const next = await attempt(() => api.setSaveSetting(key, value), t('savegames.notSaved'));
    if (!next) return;
    saveSettings = next;
    if (text) toast(text);
    render();
  };
  // The limits are whole megabytes, decimal like every size in the HUD (ByteSize.Mega).
  const mb = (n) => formatSize(n * 1_000_000);
  const on = (v) => (v ? t('savegames.on') : t('savegames.off'));
  return [
    // Simple mode shows only this switch; the limits and the rest are for experts.
    {
      kind: 'choice',
      label: t('savegames.autoLabel'),
      desc: t('savegames.autoDesc'),
      value: () => on(saveSettings?.autoBackup ?? true),
      run: () => update('autoBackup', !(saveSettings?.autoBackup ?? true))
    },
    {
      kind: 'choice',
      expert: true,
      label: t('savegames.autoKeepLabel'),
      desc: t('savegames.autoKeepDesc'),
      value: () => `${saveSettings?.autoKeep ?? 3}`,
      run: async () => {
        const keep = await chooseDialog(t('savegames.autoKeepTitle'), [1, 2, 3, 5, 10].map((n) => ({ label: `${n}`, value: n })), saveSettings?.autoKeep ?? 3);
        if (keep !== undefined) await update('autoKeep', keep);
      }
    },
    {
      kind: 'choice',
      expert: true,
      label: t('savegames.autoMaxGameLabel'),
      desc: t('savegames.autoMaxGameDesc'),
      value: () => mb(saveSettings?.autoMaxGameMb ?? 1000),
      run: async () => {
        const v = await chooseDialog(t('savegames.autoMaxGameLabel'), limitChoices(GAME_LIMITS_MB, saveSettings?.autoMaxGameMb).map((n) => ({ label: mb(n), value: n })), saveSettings?.autoMaxGameMb ?? 1000);
        if (v !== undefined) await update('autoMaxGameMb', v);
      }
    },
    {
      kind: 'choice',
      expert: true,
      label: t('savegames.autoMaxTotalLabel'),
      desc: s?.totalBytes !== undefined ? t('savegames.autoMaxTotalDesc', { used: formatSize(s.totalBytes) ?? '0 B' }) : t('savegames.loading'),
      value: () => mb(saveSettings?.autoMaxTotalMb ?? 10000),
      run: async () => {
        const v = await chooseDialog(t('savegames.autoMaxTotalLabel'), limitChoices(TOTAL_LIMITS_MB, saveSettings?.autoMaxTotalMb).map((n) => ({ label: mb(n), value: n })), saveSettings?.autoMaxTotalMb ?? 10000);
        if (v !== undefined) await update('autoMaxTotalMb', v);
      }
    },
    {
      kind: 'action',
      expert: true,
      label: t('savegames.folderLabel'),
      desc: s ? (s.custom ? s.backupDir : t('savegames.folderDefault', { dir: s.backupDir })) : t('savegames.loading'),
      icon: '💾',
      run: async () => {
        if (!s) return;
        const choice = await chooseDialog(t('savegames.folderTitle'), [
          { label: t('savegames.folderPick'), detail: t('savegames.folderPickDetail'), value: 'pick' },
          { label: t('savegames.folderUseDefault'), detail: s.defaultBackupDir, value: 'default' }
        ]);
        if (choice === 'default') await update('backupDir', null, t('savegames.folderDefaultToast'));
        if (choice === 'pick') {
          const folder = await openFileBrowser({ title: t('savegames.folderPickTitle'), mode: 'folder' });
          if (folder) await update('backupDir', folder, t('savegames.folderSaved'));
        }
      }
    },
    {
      kind: 'choice',
      expert: true,
      label: t('savegames.keepLabel'),
      desc: t('savegames.keepDesc'),
      value: () => `${saveSettings?.keep ?? 5}`,
      run: async () => {
        const keep = await chooseDialog(t('savegames.keepTitle'), [1, 3, 5, 10, 20].map((n) => ({ label: `${n}`, value: n })), saveSettings?.keep ?? 5);
        if (keep !== undefined) await update('keep', keep);
      }
    },
    {
      kind: 'choice',
      expert: true,
      label: t('savegames.configLabel'),
      desc: t('savegames.configDesc'),
      value: () => on(saveSettings?.includeConfig),
      run: () => update('includeConfig', !saveSettings?.includeConfig)
    }
  ];
}
