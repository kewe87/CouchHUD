// Plugins and skins: what is installed, the catalog (a list like a store channel, a detail page per entry), the catalogs it comes
// from (expert mode only) and the settings and pages of each plugin. Everything works with the controller; every change goes through the
// same gates, in the same order: the warning (third-party), the code (the parents' code or the couch code), and only then the call.
// The host checks every gate again; this page never relies on it for the order, and never offers what the host would refuse.

import { iconEl } from '../core/glyph-icons.js';
import { api } from '../core/api.js';
import { el, formatSize, keepInView, setFocused } from '../core/dom.js';
import { offerRestart } from '../core/power.js';
import { attempt, toast } from '../core/feedback.js';
import { currentView, pop, push, render, renderHints } from '../core/nav.js';
import { chooseDialog, confirmDialog } from '../components/overlay.js';
import { ensureUnlocked } from '../components/passcode.js';
import { ensureParentGrant, parental, refreshParental } from '../components/parental.js';
import { openKeyboard } from '../components/keyboard.js';
import { openFileBrowser } from '../components/file-browser.js';
import { state } from '../core/state.js';
import { sound } from '../sound.js';
import { t, fmtDateTime } from '../core/i18n.js';
import { declarativePage } from './plugin-page.js';
import { withParentGrant } from '../core/parent-retry.js';
import {
  badgeOf, capabilityKeys, cleanKey, entryActions, entryState, grantAction, installBlock, installPlan, installedFor, isDelisted, isRisky,
  gateOrigin, grantChange, isPageSetting, isThirdParty, needsCode, publisherOf, runGates, safeImage, sourceAddCheck, sourceErrorKey, sourceRows, sourcesVisible, uninstallPlan
} from '../core/plugin-rules.js';

/** Generic list page: rows of { icon, img, label, detail, meta, value, on, badge, run }; a { section } row is a heading the cursor skips. */
function rowsPage({ id, title, intro, rows, hints, tabs, onKey, onClose, startOnAction = false }) {
  const tabList = (page) => (typeof tabs === 'function' ? tabs(page) : tabs);
  const label = (tb, page) => (typeof tb === 'function' ? tb(page) : tb);
  return {
    id,
    name: title,
    index: 0,
    tab: 0,
    started: false,
    /** With tabs, LB/RB switch them here instead of the main tabs underneath. */
    captures: tabs ? (action) => action === 'lb' || action === 'rb' : undefined,
    onClose,
    build(root) {
      this.rows = rows(this);
      const strip = () => (tabs ? el('div', { className: 'tab-strip' }, tabList(this).map((tb, i) => el('span', { className: `tab-chip${i === this.tab ? ' active' : ''}`, textContent: label(tb, this) }))) : null);
      if (!this.rows) {
        if (tabs) root.append(el('div', { className: 'files' }, el('div', { className: 'files-head' }, el('h2', { textContent: typeof title === 'function' ? title(this) : title })), strip()));
        root.append(el('p', { className: 'empty-note', textContent: t('plugins.loading') }));
        this.items = [];
        return;
      }
      // The first time here the cursor starts on something A can do, not on an explanation.
      if (startOnAction && !this.started) {
        this.started = true;
        const first = this.rows.findIndex((r) => r.run);
        if (first > 0) this.index = first;
      }
      this.index = Math.min(this.index, Math.max(0, this.rows.length - 1));
      this.items = this.rows.map((r) => {
        if (r.section) return el('li', { className: 'update-section plug-section', textContent: r.section });
        return el('li', { className: `check-row update-row${r.highlight ? ' highlight' : ''}${r.dim ? ' dim' : ''}${r.className ? ` ${r.className}` : ''}` },
          el('span', { className: `check-box${r.img ? ' has-img' : ''}` }, r.img ? el('img', { className: 'plug-icon', src: r.img, alt: '', draggable: false }) : iconEl(r.icon ?? '◆')),
          el('span', { className: 'check-main' },
            el('strong', { textContent: r.label }),
            r.detail ? el('small', { className: r.wrap ? 'plug-wrap' : '', textContent: r.detail }) : null,
            r.meta ? el('small', { className: 'plug-meta', textContent: r.meta }) : null,
            r.strip?.length ? el('span', { className: 'plug-strip' }, r.strip.map((uri) => el('img', { className: 'plug-shot', src: uri, alt: '', draggable: false }))) : null),
          r.badge ? el('span', { className: `plug-badge${r.badgeClass ? ` ${r.badgeClass}` : ''}`, textContent: r.badge }) : null,
          r.value != null ? el('span', { className: `pill ${r.on ? 'on' : 'off'}`, textContent: r.value }) : null);
      });
      this.scroller = el('ul', { className: 'check-list list-scroll' }, this.items);
      root.append(el('div', { className: 'files' },
        el('div', { className: 'files-head' }, el('h2', { textContent: typeof title === 'function' ? title(this) : title }), intro ? el('p', { className: 'meta', textContent: typeof intro === 'function' ? intro(this) : intro }) : null),
        strip(),
        this.rows.length ? this.scroller : el('p', { className: 'empty-note', textContent: t('plugins.empty') })));
      this.update({ instant: true });
    },
    update({ instant = false } = {}) {
      if (!this.items?.length) return;
      setFocused(this.items, this.index);
      keepInView(this.scroller, this.items[this.index], { instant });
    },
    /** Up or down to the next row that is not a heading. */
    stepRow(delta) {
      let next = this.index + delta;
      while (this.rows?.[next]?.section) next += delta;
      if (next < 0 || next >= (this.rows?.length ?? 0)) return sound.bump();
      this.index = next;
      sound.move();
      this.update();
      renderHints();
    },
    async handle(action) {
      const list = tabs ? tabList(this) : [];
      if (tabs && (action === 'lb' || action === 'rb' || action === 'left' || action === 'right')) {
        const next = this.tab + (action === 'lb' || action === 'left' ? -1 : 1);
        if (next < 0 || next >= list.length) return sound.bump();
        this.tab = next;
        this.index = 0;
        this.started = false;
        sound.tab?.();
        return render();
      }
      if (onKey && (await onKey(this, action)) === true) return;
      switch (action) {
        case 'up': return this.stepRow(-1);
        case 'down': return this.stepRow(1);
        case 'a': {
          const r = this.rows?.[this.index];
          if (!r?.run) return sound.bump();
          sound.select();
          return r.run(this);
        }
      }
    },
    pointer(node, index, dispatch) {
      this.index = index;
      this.update();
      dispatch('a');
    },
    hints() {
      return (typeof hints === 'function' ? hints(this) : hints) ?? [['A', t('plugins.hint.select')], ['B', t('plugins.hint.back')]];
    }
  };
}

async function installZip(kind) {
  const path = await openFileBrowser({ title: kind === 'skin' ? t('plugins.zip.chooseSkin') : t('plugins.zip.choosePlugin'), extensions: ['.zip'] });
  if (!path) return;
  if (!(await gateOpen('install', kind === 'skin' ? 'skin' : 'local'))) return;
  // A skin is a stylesheet, not a program, but it can still load pictures from the internet and so tell a server when you are on the couch.
  if (!(await confirmDialog(kind === 'plugin' ? t('plugins.zip.warning') : t('plugins.zip.skinWarning'), t('plugins.install'), { danger: kind === 'plugin' }))) return;
  if (!(await ensureParentGrant(t('plugins.codeReason'), { action: grantAction('install'), item: path.split(/[\\/]/).pop() }))) return;
  const name = await attempt(() => api.installPackage(path, kind), t('plugins.installFailed'));
  if (!name) return;
  if (kind === 'skin') toast(t('plugins.zip.skinInstalled', { name }), 5000);
  else offerRestart(t('plugins.zip.pluginInstalled', { name }));
}

/** The host sends a code for network errors; the UI words them. */
const catalogError = (c) => (c.errorCode === 'notFound' ? t('plugins.catalog.notFound') : c.errorCode === 'unreachable' ? t('plugins.catalog.unreachable') : c.error ?? '');

const STATUS = () => ({
  update: { icon: '⬆', value: t('plugins.status.update'), on: true },
  installed: { icon: '✓', value: t('plugins.status.installed'), on: false },
  new: { icon: '⬇', value: t('plugins.status.new'), on: false },
  restart: { icon: '↻', value: t('plugins.status.restart'), on: true },
  needsNewer: { icon: '⛔', value: t('plugins.state.needsNewer'), on: false },
  officialFirst: { icon: '⏳', value: t('plugins.state.officialFirst'), on: false },
  unavailable: { icon: '⛔', value: t('plugins.status.unavailable'), on: false },
  revoked: { icon: '⛔', value: t('plugin.revoked.badge'), on: false }
});

/** Update row of the plugin list: the catalog says a newer version exists. */
const updateFor = (p) => (state.catalogUpdates ?? []).find((u) => u.kind === 'plugin' && (u.id.toLowerCase() === p.id.toLowerCase() || u.id.toLowerCase() === p.folder.toLowerCase()));

/** The text of a badge. */
function badgeText(x) {
  switch (badgeOf(x)) {
    case 'thirdParty': return t('plugin.thirdParty.badge');
    case 'local': return t('plugin.thirdParty.badgeLocal');
    case 'unsigned': return t('plugins.catalog.unsignedBadge');
    default: return null;
  }
}

const sizeText = (x) => (x?.size ? formatSize(x.size) : null);

// ---------------------------------------------------------------------------
// Gates

const LOCKED = Symbol('locked');

const isExpert = async () => !!(await attempt(() => api.getSettings()))?.expertMode;

/**
 * Asked before any question or code prompt: may this change go ahead at all (locked couch, child's profile, expert mode, developer
 * switch)? When not, the host's refusal is shown at once as a toast and nothing else is asked.
 */
async function gateOpen(change, origin = 'local') {
  const gate = await attempt(() => api.getPluginGate?.(change, origin), undefined, { quietLocked: true });
  if (!gate?.refused) return true;
  sound.bump();
  toast(gate.refused, 6000);
  return false;
}

async function parentalOn() {
  const status = parental.status ?? (await refreshParental());
  return !!status?.enabled;
}

/** The code step: the parents' code where the rules say so (the couch code when none is set up), the plain unlock otherwise. */
async function codeStep(change, who, item, grant = change) {
  const reason = t('plugins.codeReason');
  // `change` decides whether a code is needed (an update that adds permissions counts as an install), `grant` names the host's grant.
  return needsCode(change, gateOrigin(who), who?.capabilities, await parentalOn(), !!who?.addsCapabilities)
    ? ensureParentGrant(reason, { action: grantAction(grant), item })
    : ensureUnlocked(reason);
}

/** The Fremdentwickler warning: who, which key, what the plugin may do, and that it runs with the user's rights. Starts on "Cancel". */
function thirdPartyWarning(e) {
  const caps = capabilityKeys(e.capabilities);
  const list = caps.length ? caps.map((k) => `• ${t(k)}`).join('\n') : `• ${t('plugin.capability.none')}`;
  // A skin is not code, but it can load pictures from the internet and so tell a server when you are on the couch.
  const skin = e.kind === 'skin';
  const adds = e.status === 'update' && e.addsCapabilities ? t('plugin.thirdParty.addsPermissions') : null;
  return confirmDialog(t('plugin.thirdParty.title', { name: e.name }), skin ? t('plugin.thirdParty.trustSkin') : t('plugin.thirdParty.trust'), {
    danger: true,
    message: skin ? [t('plugin.thirdParty.bodySkin', { name: e.name, version: e.version }), t('plugin.thirdParty.author', { author: e.author || '–' })].join('\n') : [adds, t('plugin.thirdParty.body', { name: e.name, version: e.version }), t('plugin.thirdParty.author', { author: e.author || '–' }), e.publisher ? t('plugin.thirdParty.key', { code: e.publisher }) : null, t('plugin.thirdParty.caps'), list].filter(Boolean).join('\n')
  });
}

/**
 * Installs (or updates) a catalog entry: the warning (third-party) or the plain question, then the code, then the call. Plugins are code
 * that runs with the user's rights; the host checks https, SHA-256, the signed package statement and minCouchHud again.
 * Resolves to the host's result or undefined when cancelled, blocked or failed.
 */
export async function installFromCatalog(entry, { expert } = {}) {
  const update = entry.status === 'update';
  if (!(await gateOpen(grantChange(entry, update), gateOrigin(entry)))) return undefined;
  const plan = installPlan(entry, { expert: expert ?? (await isExpert()), update });
  if (plan.blocked) {
    toast(t('plugin.thirdParty.needExpert'), 5000);
    return undefined;
  }
  const what = entry.author ? t('plugins.catalog.whatBy', { name: entry.name, version: entry.version, author: entry.author }) : t('plugins.catalog.what', { name: entry.name, version: entry.version });
  const source = entry.unsigned ? ` ${t('plugins.catalog.unsigned')}` : '';
  const question = entry.kind === 'plugin' ? `${t('plugins.catalog.askPlugin', { what })}${source}` : `${t('plugins.catalog.askSkin', { what })}${source}`;
  const ok = await runGates(plan.steps, {
    warning: () => thirdPartyWarning(entry),
    confirm: () => confirmDialog(question, update ? t('plugins.update') : t('plugins.install'), { danger: entry.kind === 'plugin' }),
    code: () => codeStep(grantChange(entry, update), entry, entry.id)
  });
  if (!ok) return undefined;
  toast(t('plugins.catalog.checking', { name: entry.name }), 4000);
  const result = await attempt(() => api.installCatalogEntry(entry.id, entry.kind), t('plugins.installFailed'));
  if (!result) return undefined;
  if (result.restartNeeded) offerRestart(t('plugins.catalog.restartNeeded', { message: result.message }));
  else toast(result.message, 7000);
  return result;
}

/** Enable or disable: the code step (the plain unlock for CouchHUD's own plugins), then the call. */
async function setEnabled(plugin, enabled) {
  if (!(await gateOpen(enabled ? 'enable' : 'disable', gateOrigin(plugin)))) return undefined;
  if (!(await runGates(['code'], { code: () => codeStep(enabled ? 'enable' : 'disable', plugin, plugin.folder) }))) return undefined;
  const list = await attempt(() => api.setPluginEnabled(plugin.folder, enabled), t('plugins.notSaved'));
  if (list) offerRestart(t('plugins.page.restartHint'));
  return list;
}

/** The label of a function level. Without couch lock and parental controls nothing is ever restricted, so admin and restricted both read "allowed". */
const levelLabel = (level, possible) => (level === 'off' ? t('plugin.fn.offLevel') : !possible ? t('plugin.fn.allowed') : level === 'restricted' ? t('plugin.fn.restricted') : t('plugin.fn.admin'));

/**
 * Who may use one function of a plugin: the choice, then the same protection as any change of protection (never on the locked couch or in a
 * child's profile, the parents' code when there is one, else the unlock). The host checks again and uses the code up.
 */
async function pickFunctionLevel(plugin, fn, onDone) {
  if (!(await gateOpen('functionLevel', 'firstParty'))) return;
  const possible = plugin.restrictionsPossible !== false;
  // Without couch lock and parental controls "admin" and "restricted" are both just "allowed": the dialog shows one entry for them, and choosing
  // it while the function is already one of the two writes nothing (the stored level stays as it is).
  const allowedNow = fn.level !== 'off';
  const options = possible
    ? [['admin', t('plugin.fn.admin')], ['restricted', t('plugin.fn.restricted')], ['off', t('plugin.fn.offLevel')]]
    : [['allowed', t('plugin.fn.allowed')], ['off', t('plugin.fn.offLevel')]];
  const current = possible ? fn.level : allowedNow ? 'allowed' : 'off';
  let level = await chooseDialog(t('plugin.fn.pick', { name: fn.label }), options.map(([value, label]) => ({ label, value })), current);
  if (level === undefined || level === current) return;
  // Allowed again after "nobody" starts at the safe level; the owner can pick the others once the lock is on.
  if (level === 'allowed') level = 'admin';
  if (!(await ensureParentGrant(t('plugin.fn.reason'), { action: 'pluginFunction', item: `${plugin.id}/${fn.id}` }))) return;
  const list = await attempt(() => api.setPluginFunctionLevel(plugin.id, fn.id, level), t('plugins.notSaved'));
  if (!list) return;
  toast(t('plugin.fn.saved', { name: fn.label, level: levelLabel(level, possible) }), 4000);
  onDone(list);
}

/** Removes a plugin: the question, the code step, then the call. */
async function uninstall(plugin) {
  if (!(await gateOpen('uninstall'))) return undefined;
  const ok = await runGates(uninstallPlan().steps, {
    confirm: () => confirmDialog(t('plugins.uninstall.ask', { name: plugin.name }), t('plugins.act.uninstall'), { danger: true }),
    code: () => codeStep('uninstall', plugin, plugin.folder)
  });
  if (!ok) return undefined;
  const result = await attempt(() => api.uninstallPlugin(plugin.folder), t('plugins.notSaved'));
  if (result && result.gone) toast(t('plugins.uninstall.done', { name: plugin.name }), 5000);
  else if (result) offerRestart(t('plugins.uninstall.atRestart', { name: plugin.name }));
  return result?.plugins;
}

/** A withdrawn plugin only runs again after a red warning and the parents' code, for exactly the files it has now. */
async function acceptRevoked(plugin) {
  if (!(await gateOpen('acceptRevoked', gateOrigin(plugin)))) return undefined;
  const reason = plugin.revokedReason || t('plugin.revoked.noReason');
  const ok = await runGates(['warning', 'code'], {
    warning: () => confirmDialog(t('plugin.revoked.acceptAsk', { name: plugin.name }), t('plugin.revoked.acceptYes'), { danger: true, message: t('plugin.revoked.info', { name: plugin.name, reason }) }),
    code: () => codeStep('acceptRevoked', plugin, plugin.folder)
  });
  if (!ok) return undefined;
  const list = await attempt(() => api.acceptRevokedPlugin(plugin.folder), t('plugins.notSaved'));
  if (list) offerRestart(t('plugin.revoked.accepted', { name: plugin.name }));
  return list;
}

// ---------------------------------------------------------------------------
// The page

/** One state the page shares between the lists and the detail pages. */
const newStore = () => ({ plugins: null, catalog: null, sources: null });

const actionLabel = (action) => ({
  install: t('plugins.install'),
  update: t('plugins.update'),
  enable: t('plugins.act.enable'),
  disable: t('plugins.act.disable'),
  uninstall: t('plugins.act.uninstall'),
  acceptRevoked: t('plugin.revoked.accept')
})[action];

const actionIcon = { install: '⬇', update: '⬆', enable: '⏻', disable: '⏻', uninstall: '🗑', acceptRevoked: '⚠' };

/** Words the problem an entry has, or null. */
function stateNote(e, installed) {
  switch (entryState(e, installed)) {
    case 'revoked': return t('plugin.revoked.info', { name: e.name, reason: installed.revokedReason || t('plugin.revoked.noReason') });
    case 'needsNewer': return e.reason || t('plugins.state.needsNewerDetail', { min: e.minCouchHud || '?' });
    case 'officialFirst': return t('plugins.state.officialFirstDetail');
    case 'unavailable': return e.reason || t('plugins.catalog.notInstallable');
    case 'restart': return t('plugins.catalog.alreadyApplied');
    default: return null;
  }
}

export function pluginsPage(startTab = 0, { expert = false, openId = null } = {}) {
  const store = newStore();
  let loadingCatalog = false;
  let loadingPlugins = false;
  let opened = false;
  const tabsFor = () => [t('plugins.tab.installed'), t('plugins.tab.catalog'), ...(sourcesVisible(expert) ? [t('plugins.tab.sources')] : [])];

  const loadCatalog = (page, refresh) => {
    if (loadingCatalog) return;
    loadingCatalog = true;
    // A locked couch is a state of its own (a row that unlocks), never "not reachable".
    attempt(() => api.getCatalog(refresh), t('plugins.catalog.notLoaded'), { quietLocked: true, lockedValue: LOCKED }).then((c) => {
      loadingCatalog = false;
      store.catalog = c === LOCKED ? { entries: [], locked: true } : c ?? { entries: [], error: t('plugins.catalog.unreachableShort') };
      if (!store.catalog.locked && !store.catalog.entries?.length && !store.catalog.error) store.catalog.error = t('plugins.catalog.isEmpty');
      if (currentView() === page) render();
    });
  };
  const loadPlugins = (page) => {
    if (loadingPlugins) return;
    loadingPlugins = true;
    attempt(() => api.getPlugins(), t('plugins.notLoaded')).then((p) => {
      loadingPlugins = false;
      store.plugins = p ?? [];
      if (currentView() === page) render();
    });
  };
  const setPlugins = (list) => {
    if (list) store.plugins = list;
  };

  const installedRows = () => [
    { icon: '+', label: t('plugins.installed.pluginZip'), detail: t('plugins.installed.pluginZipDetail'), highlight: true, run: () => installZip('plugin') },
    { icon: '🎨', label: t('plugins.installed.skinZip'), detail: t('plugins.installed.skinZipDetail'), highlight: true, run: () => installZip('skin') },
    ...store.plugins.map((p) => {
      const update = updateFor(p);
      const delisted = isDelisted(p, store.catalog?.entries);
      const badge = p.revoked ? t('plugin.revoked.badge') : badgeText(p);
      return {
        icon: p.revoked ? '⛔' : p.loaded ? '◆' : p.enabled ? '⚠' : '○',
        label: `${p.name}${p.version ? ` ${p.version}` : ''}`,
        detail: p.error ? t('plugins.installed.error', { error: p.error }) : [update ? t('plugins.installed.updateAvailable', { version: update.version }) : null, delisted ? t('plugin.delisted.badge') : null, p.description, p.author ? t('plugins.by', { author: p.author }) : null].filter(Boolean).join(' · ') || p.folder,
        badge,
        badgeClass: p.revoked ? 'danger' : isThirdParty(p) || p.origin === 'local' ? 'warn' : '',
        // i18n-used: plugins.value.pausedOffline plugins.value.pausedOfflineLoaded plugins.value.startsAfterRestart
        value: p.pausedOffline ? t(p.loaded ? 'plugins.value.pausedOfflineLoaded' : 'plugins.value.pausedOffline') : p.startsAfterRestart ? t('plugins.value.startsAfterRestart') : p.needsApproval ? t('plugins.value.approval') : update ? t('plugins.value.update') : p.loaded ? t('plugins.value.active') : p.enabled ? t('plugins.value.error') : t('plugins.value.off'),
        on: !!update || p.loaded,
        dim: !p.enabled,
        run: () => push(pluginPage(p, store, setPlugins))
      };
    })
  ];

  const catalogRow = (e, page) => {
    const installed = installedFor(e, store.plugins);
    const st = entryState(e, installed);
    const status = STATUS()[st];
    const badge = badgeText(e);
    const primary = entryActions(e, installed, { expert }).find((a) => a === 'install' || a === 'update');
    const size = sizeText(e);
    const version = e.status === 'update' ? `${e.installedVersion} → ${e.version}` : e.version;
    return {
      icon: status.icon === '⬇' || status.icon === '⬆' ? (e.kind === 'skin' ? '🎨' : '🧩') : status.icon,
      img: safeImage(e.icon),
      label: e.name,
      detail: stateNote(e, installed) ?? e.description,
      meta: [e.kind === 'skin' ? t('plugins.kind.skin') : t('plugins.kind.plugin'), publisherOf(e) ? t('plugins.by', { author: publisherOf(e) }) : null, `v${version}`, size].filter(Boolean).join(' · '),
      badge,
      badgeClass: badge ? 'warn' : '',
      value: status.value,
      on: status.on,
      dim: st === 'unavailable' || st === 'needsNewer' || st === 'officialFirst' || st === 'revoked',
      hintLabel: t('plugins.hint.details'),
      primary,
      entry: e,
      run: () => push(entryDetailPage(e, store, { expert, setPlugins }))
    };
  };

  /** The one row of a tab on the locked couch: A unlocks and loads again. */
  const lockedRows = (page, reload) => [{
    icon: '🔒',
    label: t('plugins.locked.title'),
    detail: t('plugins.locked.detail'),
    highlight: true,
    hintLabel: t('plugins.locked.unlock'),
    run: async () => {
      if (!(await ensureUnlocked(t('plugins.codeReason')))) return;
      reload();
      render();
    }
  }];

  const catalogRows = (page) => {
    const c = store.catalog;
    if (c.locked) return lockedRows(page, () => { store.catalog = null; });
    const list = [];
    const pending = c.entries.filter((e) => e.restartPending);
    if (pending.length) list.push({ icon: '↻', label: t('plugins.catalog.restartTitle'), detail: t('plugins.catalog.restartDetail', { names: pending.map((e) => e.name).join(', ') }), highlight: true });
    list.push({
      icon: '⟳',
      label: t('plugins.catalog.refresh'),
      hintLabel: t('plugins.catalog.reload'),
      detail: (c.error ? catalogError(c) : null) ?? (c.fetchedAt ? t('plugins.catalog.asOf', { date: fmtDateTime(c.fetchedAt, { dateStyle: 'short', timeStyle: 'short' }) }) : t('plugins.catalog.notLoadedYet')),
      run: () => {
        store.catalog = null;
        loadCatalog(page, true);
        toast(t('plugins.catalog.refreshing'));
        render();
      }
    });
    if (c.fromCache) list.push({ icon: '⚠', label: t('plugins.catalog.offline'), detail: t('plugins.catalog.offlineDetail'), dim: true });
    for (const e of [...c.entries].sort((a, b) => (a.status === 'update' ? 0 : 1) - (b.status === 'update' ? 0 : 1) || a.name.localeCompare(b.name))) list.push(catalogRow(e, page));
    return list;
  };

  // ---- sources (expert mode only) ----

  const loadSources = (page) => {
    attempt(() => api.getCatalogSources(), t('plugins.src.notLoaded'), { quietLocked: true, lockedValue: LOCKED }).then((s) => {
      store.sources = s === LOCKED ? { official: null, extra: [], max: 8, locked: true } : s ?? { official: null, extra: [], max: 8 };
      if (currentView() === page) render();
    });
  };

  const sourcesRows = (page) => {
    const s = store.sources;
    if (s.locked) return lockedRows(page, () => { store.sources = null; });
    const rows = [{
      icon: '+',
      label: t('plugins.src.add'),
      detail: t('plugins.src.addDetail'),
      highlight: true,
      hintLabel: t('plugins.src.add'),
      run: () => addSource(s, page)
    }, { section: t('plugins.src.heading') }];
    for (const src of sourceRows(s)) {
      if (src.official) {
        rows.push({
          icon: '✓',
          label: t('plugins.src.officialName'),
          detail: `${src.host} · ${t('plugins.src.officialDetail')}`,
          badge: t('plugins.src.official'),
          hintLabel: t('plugins.hint.info'),
          run: () => toast(t('plugins.srcerr.officialStays'), 5000)
        });
      } else {
        rows.push({
          icon: '🌐',
          label: src.name || src.host,
          detail: [src.host, src.keyCode ? t('plugins.src.keyPinned', { code: src.keyCode }) : t('plugins.src.keyNone')].join(' · '),
          meta: src.url,
          badge: t('plugin.thirdParty.badge'),
          badgeClass: 'warn',
          hintLabel: t('plugins.src.remove'),
          run: src.removable ? () => removeSource(src, page) : undefined
        });
      }
    }
    return rows;
  };

  /** The host words its errors itself; a bare key (older host, preview) is worded here. */
  // i18n-used: plugins.src.failed
  const sourceFailed = (err) => toast(t(sourceErrorKey(err?.message) ?? 'plugins.src.failed', { error: err?.message ?? '' }), 7000);

  async function addSource(s, page) {
    const check = sourceAddCheck(s, expert);
    if (!check.ok) return toast(check.reason === 'tooMany' ? t('plugins.src.tooMany', { max: s?.max ?? 8 }) : t('plugins.srcerr.expert'), 5000);
    if (!(await gateOpen('install', 'thirdParty'))) return;
    const url = (await openKeyboard({ title: t('plugins.src.askUrl'), value: 'https://', paste: true, maxLength: 2048 }))?.trim();
    if (!url || url === 'https://') return;
    // The key is optional: empty skips it, B cancels the whole thing.
    const keyText = await openKeyboard({ title: t('plugins.src.askKey'), hint: t('plugins.src.askKeyHint'), value: '', paste: true, maxLength: 2048 });
    if (keyText === undefined) return;
    const warning = s?.warning;
    const ok = await runGates(['warning', 'code'], {
      warning: () => confirmDialog(warning?.title || t('plugins.src.warningTitle'), warning?.confirm || t('plugins.src.trust'), { danger: true, message: warning?.body || t('plugins.src.warningBody') }),
      code: () => ensureParentGrant(t('plugins.src.codeReason'), { action: 'catalogSourceAdd', item: url })
    });
    if (!ok) return;
    try {
      // The acknowledgement is true only here, after the warning was confirmed.
      store.sources = await api.addCatalogSource(url, cleanKey(keyText), null, true);
      store.catalog = null;
      toast(t('plugins.src.added'), 4000);
    } catch (err) {
      console.error(err);
      sourceFailed(err);
    }
    if (currentView() === page) render();
  }

  async function removeSource(src, page) {
    if (!(await gateOpen('install', 'thirdParty'))) return;
    const ok = await runGates(['confirm', 'code'], {
      confirm: () => confirmDialog(t('plugins.src.removeAsk', { name: src.name || src.host }), t('plugins.src.remove'), { danger: true, message: t('plugins.src.removeDetail') }),
      code: () => ensureParentGrant(t('plugins.src.codeReason'), { action: 'catalogSourceRemove', item: src.url })
    });
    if (!ok) return;
    try {
      store.sources = await api.removeCatalogSource(src.url);
      store.catalog = null;
      toast(t('plugins.src.removed'), 4000);
    } catch (err) {
      console.error(err);
      sourceFailed(err);
    }
    if (currentView() === page) render();
  }

  const page = rowsPage({
    id: 'plugins',
    title: t('plugins.title'),
    intro: t('plugins.intro'),
    tabs: tabsFor,
    hints: (p) => {
      const row = p.rows?.[p.index];
      const catalogTab = p.tab === 1;
      const tabs = tabsFor();
      return [
        ['A', row?.hintLabel ?? t('plugins.hint.select')],
        ...(catalogTab && row?.primary ? [['Y', actionLabel(row.primary)]] : []),
        ...(catalogTab && row?.hintLabel !== t('plugins.catalog.reload') ? [['X', t('plugins.hint.reloadCatalog')]] : []),
        ...(p.tab > 0 ? [['LB', tabs[p.tab - 1]]] : []),
        ...(p.tab < tabs.length - 1 ? [['RB', tabs[p.tab + 1]]] : []),
        ['B', t('plugins.hint.back')]
      ];
    },
    async onKey(p, action) {
      if (p.tab !== 1) return false;
      if (action === 'x') {
        store.catalog = null;
        loadCatalog(p, true);
        toast(t('plugins.catalog.refreshing'));
        render();
        return true;
      }
      // Y: the main action of the entry under the cursor (install or update) without opening its page.
      if (action === 'y') {
        const row = p.rows?.[p.index];
        const entry = row?.entry;
        if (!row?.primary || !entry) {
          sound.bump();
          return true;
        }
        sound.select();
        const result = await installFromCatalog(entry, { expert });
        if (result?.state) store.catalog = result.state;
        store.plugins = null;
        render();
        return true;
      }
      return false;
    },
    rows(p) {
      if (p.tab === 2 && sourcesVisible(expert)) {
        if (!store.sources) {
          loadSources(p);
          return null;
        }
        return sourcesRows(p);
      }
      if (p.tab === 1) {
        if (!store.plugins) loadPlugins(p);
        if (!store.catalog) {
          loadCatalog(p, false);
          return null;
        }
        if (store.plugins && openId && !opened) {
          // Opened from a suggestion: straight to the entry's page.
          opened = true;
          const entry = store.catalog.entries.find((e) => e.id.toLowerCase() === openId.toLowerCase());
          if (entry) setTimeout(() => push(entryDetailPage(entry, store, { expert, setPlugins })), 0);
        }
        return store.plugins ? catalogRows(p) : null;
      }
      if (!store.plugins) {
        loadPlugins(p);
        return null;
      }
      return installedRows();
    }
  });
  page.tab = Math.min(startTab, tabsFor().length - 1);
  return page;
}

// ---------------------------------------------------------------------------
// Detail page of a catalog entry

function entryDetailPage(entry, store, { expert, setPlugins }) {
  let e = entry;
  let detail = null;
  let loading = false;
  const refreshEntry = () => {
    const next = store.catalog?.entries?.find((x) => x.id === e.id && x.kind === e.kind);
    if (next) e = next;
  };

  const run = async (action, page) => {
    const installed = installedFor(e, store.plugins);
    let list;
    if (action === 'install' || action === 'update') {
      const result = await installFromCatalog(e, { expert });
      if (!result) return;
      if (result.state) store.catalog = result.state;
      refreshEntry();
      list = await attempt(() => api.getPlugins(), t('plugins.notLoaded'));
    } else if (action === 'enable' || action === 'disable') list = await setEnabled(installed, action === 'enable');
    else if (action === 'uninstall') list = await uninstall(installed);
    else if (action === 'acceptRevoked') list = await acceptRevoked(installed);
    if (list) setPlugins(list);
    if (currentView() === page) render();
  };

  return rowsPage({
    id: 'plugin-detail',
    title: () => e.name,
    intro: () => e.description,
    startOnAction: true,
    hints: (p) => [['A', p.rows?.[p.index]?.run ? p.rows[p.index].hintLabel ?? t('plugins.hint.select') : t('plugins.hint.select')], ['B', t('plugins.hint.back')]],
    rows(page) {
      if (!detail && !loading) {
        loading = true;
        attempt(() => api.getCatalogDetail(e.id, e.kind), e.name, { quietLocked: true }).then((d) => {
          detail = d ?? { row: e };
          loading = false;
          if (currentView() === page) render();
        });
      }
      if (!store.plugins) {
        attempt(() => api.getPlugins(), t('plugins.notLoaded')).then((p) => {
          store.plugins = p ?? [];
          if (currentView() === page) render();
        });
        return null;
      }
      refreshEntry();
      const d = detail ?? {};
      const installed = installedFor(e, store.plugins);
      const note = stateNote(e, installed);
      const caps = e.capabilities ?? d.row?.capabilities ?? [];
      const rows = [];
      const badge = badgeText(e);
      const block = installBlock(e, { expert });
      rows.push({
        icon: e.kind === 'skin' ? '🎨' : '🧩',
        img: safeImage(e.icon),
        label: `${e.name} ${e.version}`,
        detail: [publisherOf(e) ? t('plugins.by', { author: publisherOf(e) }) : null, sizeText(e), e.license].filter(Boolean).join(' · ') || null,
        meta: e.kind === 'skin' ? t('plugins.kind.skin') : t('plugins.kind.plugin'),
        badge,
        badgeClass: badge ? 'warn' : '',
        value: STATUS()[entryState(e, installed)].value,
        on: STATUS()[entryState(e, installed)].on,
        className: 'plug-head'
      });
      if (note) rows.push({ icon: '⚠', label: STATUS()[entryState(e, installed)].value, detail: note, wrap: true, highlight: true });
      if (isThirdParty(e)) rows.push({ icon: '⚠', label: t('plugin.thirdParty.badge'), detail: d.warning || t('plugin.thirdParty.short'), wrap: true, highlight: true });
      if (block) rows.push({ icon: '🔒', label: t('plugin.thirdParty.needExpert'), dim: true });
      for (const a of entryActions(e, installed, { expert })) rows.push({ icon: actionIcon[a], label: actionLabel(a), hintLabel: actionLabel(a), highlight: a === 'install' || a === 'update', run: () => run(a, page) });
      rows.push({ section: t('plugins.detail.about') });
      rows.push({ icon: 'ℹ', label: e.name, detail: d.longDescription || e.description || t('plugins.detail.noText'), wrap: true });
      rows.push({ section: t('plugins.detail.rights') });
      if (caps.length) {
        capabilityKeys(caps).forEach((key, i) => rows.push({ icon: isRisky(caps[i]) ? '⚠' : '✓', label: t(key), className: isRisky(caps[i]) ? 'plug-risky' : '' }));
      } else rows.push({ icon: '✓', label: t('plugin.capability.none') });
      rows.push({ section: t('plugins.detail.license') });
      rows.push({ icon: '⚖', label: e.license ? t('plugins.detail.licenseFree', { license: e.license }) : t('plugins.detail.licenseFreeOnly'), detail: t('plugins.detail.licenseNote'), wrap: true });
      if (d.screenshots?.length) {
        rows.push({ section: t('plugins.detail.shots') });
        rows.push({ icon: '🖼', label: t('plugins.detail.shotsCount', { count: d.screenshots.length }), strip: d.screenshots.map(safeImage).filter(Boolean) });
      }
      rows.push({ section: t('plugins.detail.changelog') });
      rows.push({ icon: '≡', label: e.version, detail: d.changelog || d.changelogUrl || t('plugins.detail.noChangelog'), wrap: true });
      return rows;
    }
  });
}

// ---------------------------------------------------------------------------
// An installed plugin: approval, switch, its pages and settings

/** The id of the core's settings page of a plugin as the host knows it (PluginSettingRules.SettingsPageId); only ever sent to end a visit. */
const SETTINGS_PAGE = '$settings';

function endSettingsVisit(pluginId) {
  try {
    Promise.resolve(api.endPluginVisit?.(pluginId, SETTINGS_PAGE)).catch(() => {});
  } catch {
    // nothing to end
  }
}

/**
 * The rows for a plugin's settings: values to change, and entries (kind "page") that open one of the plugin's own pages. Shared by the
 * plugin manager's page and the reduced page of Settings > Plugins. The host decides every press (the lock, the owner's level, the
 * parents' code in a child's profile); the page entry hands back only the page, which is opened on the stack, so B returns here.
 */
function settingRows(p, refresh) {
  return (p.settings ?? []).map((s) => isPageSetting(s) ? {
    // An entry that opens one of the plugin's own pages. The host decides (the lock, the owner's level, the parents' code in a child's profile)
    // and hands back only the page; B on that page returns here.
    icon: '⚙',
    label: s.label,
    detail: s.description,
    value: '›',
    run: async () => {
      const result = await attempt(() => withParentGrant(() => api.openPluginSettingPage(p.id, s.key)), p.name);
      if (result?.id) push(declarativePage(p, result));
    }
  } : ({
    icon: '⚙',
    label: s.label,
    detail: s.description,
    value: s.kind === 'toggle' ? (s.value === 'true' ? t('plugins.value.on') : t('plugins.value.off')) : s.kind === 'secret' ? (s.value ? t('plugins.value.saved') : t('plugins.value.notSet')) : s.value || '–',
    on: s.kind === 'toggle' && s.value === 'true',
    run: async () => {
      let value;
      if (s.kind === 'toggle') value = s.value === 'true' ? 'false' : 'true';
      else if (s.kind === 'choice') value = await chooseDialog(s.label, s.choices.map((c) => ({ label: c, value: c })), s.value);
      else value = await openKeyboard({ title: s.label, value: s.kind === 'secret' ? '' : s.value ?? '', secret: s.kind === 'secret' });
      if (value === undefined) return;
      // A child's profile: the host asks for the parents' code once (a visit of this plugin's settings); then the change is repeated once.
      refresh(await attempt(() => withParentGrant(() => api.setPluginSetting(p.id, s.key, value)), t('plugins.notSaved')));
    }
  }));
}

/**
 * Settings > Plugins in simple mode: only the plugin's own settings and the pages they open. The switch, approval, function levels,
 * uninstall, rights and folder stay on the plugin manager (expert mode).
 */
function pluginSettingsOnlyPage(plugin, onChange) {
  let p = plugin;
  const refresh = (list) => {
    const next = list?.find((x) => x.folder === p.folder && x.id === p.id);
    if (next) p = next;
    if (list) onChange(list);
    render();
  };
  return rowsPage({
    id: 'plugin-settings',
    // Back ends the visit the parents' code opened for this plugin's settings.
    onClose: () => endSettingsVisit(p.id),
    title: () => p.name,
    rows: () => settingRows(p, refresh)
  });
}

/** A plugin's own page in Settings > Plugins: in expert mode the plugin manager's page, in simple mode only its settings. */
export function pluginSettingsPage(plugin, onChange, { expert = false } = {}) {
  return expert ? pluginPage(plugin, null, onChange) : pluginSettingsOnlyPage(plugin, onChange);
}

function pluginPage(plugin, store, onChange) {
  let p = plugin;
  const refresh = (list) => {
    const next = list?.find((x) => x.folder === p.folder && x.id === p.id);
    if (next) p = next;
    if (list) onChange(list);
    render();
  };
  return rowsPage({
    id: 'plugin',
    // Back ends the visit the parents' code opened for this plugin's settings (the host also ends it itself on a profile switch, the lock or a game start).
    onClose: () => endSettingsVisit(p.id),
    title: () => p.name,
    intro: () => [p.description, p.homepage].filter(Boolean).join(' · '),
    rows: () => [
      ...(isThirdParty(p) ? [{ icon: '⚠', label: t('plugin.thirdParty.badge'), detail: t('plugin.thirdParty.short'), wrap: true, highlight: true }] : []),
      ...(p.revoked ? [{
        icon: '⛔',
        label: t('plugin.revoked.badge'),
        detail: t('plugin.revoked.info', { name: p.name, reason: p.revokedReason || t('plugin.revoked.noReason') }),
        wrap: true,
        highlight: true,
        hintLabel: t('plugin.revoked.accept'),
        run: async () => {
          const list = await acceptRevoked(p);
          if (list) refresh(list);
        }
      }] : []),
      ...(p.needsApproval ? [{
        icon: '🔒',
        label: t('plugins.page.approve'),
        detail: p.changed ? t('plugins.page.approveChanged', { code: p.approvalCode || '–' }) : t('plugins.page.approveNew', { code: p.approvalCode || '–' }),
        highlight: true,
        run: async () => {
          if (!(await gateOpen('approve', 'local'))) return;
          // The approval question shows the code of exactly the files the host will approve (its hash goes along); then the code.
          const ok = await runGates(['confirm', 'code'], {
            confirm: () => confirmDialog(p.changed ? t('plugins.approve.askChanged', { name: p.name, code: p.approvalCode || '–' }) : t('plugins.approve.ask', { name: p.name, code: p.approvalCode || '–' }), t('plugins.approve.yes'), { danger: true }),
            code: () => ensureParentGrant(t('plugins.approve.reason'), { action: grantAction('approve'), item: p.folder })
          });
          if (!ok) return;
          const list = await attempt(() => api.approvePlugin(p.folder, p.approvalHash), t('plugins.notSaved'));
          if (!list) return;
          refresh(list);
          offerRestart(t('plugins.approve.done', { name: p.name }));
        }
      }] : []),
      {
        icon: '⏻',
        label: t('plugins.page.active'),
        detail: p.restartNeeded ? t('plugins.page.restartPending') : t('plugins.page.restartHint'),
        value: p.enabled ? t('plugins.value.on') : t('plugins.value.off'),
        on: p.enabled,
        run: async () => {
          const list = await setEnabled(p, !p.enabled);
          if (list) refresh(list);
        }
      },
      ...(p.pages ?? []).map((pg) => ({ icon: pg.icon, label: pg.title, detail: pg.description, highlight: true, run: () => push(declarativePage(p, pg)) })),
      ...settingRows(p, refresh),
      ...(p.functions?.length ? [
        { section: t('plugin.fn.title') },
        ...p.functions.map((fn) => ({
          icon: fn.level === 'off' ? '⛔' : '🔑',
          label: fn.label,
          detail: fn.custom ? t('plugin.fn.custom') : t('plugin.fn.suggested'),
          value: levelLabel(fn.level, p.restrictionsPossible !== false),
          on: fn.level !== 'off',
          run: () => pickFunctionLevel(p, fn, refresh)
        })),
        { icon: 'ℹ', label: p.restrictionsPossible !== false ? t('plugin.fn.hint') : t('plugin.fn.hintNoLock'), wrap: true }
      ] : []),
      ...(p.capabilities?.length ? [{ icon: '✓', label: t('plugins.detail.rights'), detail: capabilityKeys(p.capabilities).map((k) => t(k)).join(' · '), wrap: true }] : []),
      ...(p.tools?.length ? [{ icon: '✦', label: t('plugins.page.forAssistant'), detail: p.tools.join(', ') }] : []),
      ...(p.userInstalled ? [{
        icon: '🗑',
        label: t('plugins.act.uninstall'),
        hintLabel: t('plugins.act.uninstall'),
        run: async () => {
          const list = await uninstall(p);
          if (list) {
            onChange(list);
            pop();
          }
        }
      }] : []),
      { icon: '📁', label: t('plugins.page.folder'), detail: `${p.userInstalled ? '%APPDATA%\\CouchHud\\plugins\\' : 'plugins\\'}${p.folder}` }
    ]
  });
}

// ---------------------------------------------------------------------------
// Suggestions: a matching program is installed (CaseHUD) or Wand was in use. Once per Windows user; never installs by itself.

/**
 * Asks, once, about what the host points out; "view" opens the entry's page in the catalog, where the usual gates apply. `canPrompt` is asked
 * again right before each dialog (the awaits before it take time): 'skipped' when the moment is gone, so the caller can try later.
 */
export async function offerPluginSuggestions(canPrompt = () => true) {
  let list;
  try {
    list = await api.getPluginSuggestions?.();
  } catch {
    return 'none';
  }
  if (!list?.length) return 'none';
  const expert = await isExpert();
  for (const s of list) {
    if (!canPrompt()) return 'skipped';
    const view = await confirmDialog(t('plugin.suggest.title'), t('plugin.suggest.view'), { message: s.text, cancelLabel: t('plugin.suggest.later') });
    // Asked once: whatever the answer, it is not asked again (the entry stays in the catalog).
    await api.dismissPluginSuggestion?.(s.entryId).catch(() => {});
    if (view) {
      push(pluginsPage(1, { expert, openId: s.entryId }));
      return 'shown';
    }
  }
  return 'shown';
}
