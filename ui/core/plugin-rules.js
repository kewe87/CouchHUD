// Plugin catalog, detail page, actions, catalog sources and declarative pages: the rules without any DOM (tested in
// scripts/ui-logic-check.mjs). The host decides everything that matters (it checks every gate again); these rules only decide what the
// page offers and in which order it asks, so a refused call is the exception and never the way a page finds out.

/** Where a confirm dialog starts. A risky question starts on "Cancel": a double press on A must never confirm it. */
export const confirmStartIndex = ({ danger = false, focusCancel = false } = {}) => (danger || focusCancel ? 1 : 0);

/** A plugin or catalog row of a developer who is not CouchHUD: a signed third-party package, or anything from a catalog the user added. */
export const isThirdParty = (x) => !!x && (x.origin === 'thirdParty' || x.extra === true || x.thirdParty === true);

/** The mark in the list: 'thirdParty' (Fremdentwickler), 'local' (a plugin nobody signed), 'unsigned' (a source without signature) or null. */
export function badgeOf(x) {
  if (!x) return null;
  if (isThirdParty(x)) return 'thirdParty';
  if (x.origin === 'local' && x.kind !== 'skin') return 'local';
  if (x.unsigned) return 'unsigned';
  return null;
}

/** The publisher line: the author, then (third party) the short code of the key that signed the package. */
export function publisherOf(x, fallback) {
  const name = x?.author || fallback;
  return isThirdParty(x) && x?.publisher ? `${name} · ${x.publisher}` : name;
}

// i18n-used: plugin.capability.library plugin.capability.pages plugin.capability.quickActions plugin.capability.assistant plugin.capability.notify plugin.capability.browser plugin.capability.downloads plugin.capability.network_state plugin.capability.process plugin.capability.browser_signin plugin.capability.tabs plugin.capability.setup_run plugin.capability.lock_restrict plugin.capability.startup_args plugin.capability.native plugin.capability.registry plugin.capability.network_loopback plugin.capability.network_internet plugin.capability.dynamicCode plugin.capability.unknown
/** The ids of the capabilities as text keys: 'network:loopback' -> 'plugin.capability.network_loopback'. */
export const capabilityKey = (id) => `plugin.capability.${String(id).replace(/[^A-Za-z0-9]+/g, '_')}`;

const KNOWN_CAPABILITIES = new Set([
  'library', 'pages', 'quickActions', 'assistant', 'notify', 'browser', 'downloads', 'network.state', 'process', 'browser.signin', 'tabs', 'setup.run', 'lock.restrict',
  'startup.args', 'native', 'registry', 'network:loopback', 'network:internet', 'dynamicCode'
]);

/** Capability keys to show; an id this version does not know is shown as unknown (never as its raw id inside a sentence). */
export const capabilityKeys = (caps) => (caps ?? []).map((c) => (KNOWN_CAPABILITIES.has(c) ? capabilityKey(c) : 'plugin.capability.unknown'));

/** Capabilities that deserve a red mark: they reach beyond CouchHUD (programs, the registry, the internet, native code, loading code). */
export const RISKY_CAPABILITIES = new Set(['process', 'native', 'registry', 'network:internet', 'dynamicCode', 'setup.run']);
export const isRisky = (cap) => RISKY_CAPABILITIES.has(cap);

/**
 * What a catalog entry is, in one word: revoked | needsNewer | officialFirst | unavailable | restart | update | installed | new.
 * `installed` is the plugin the host has for it (getPlugins) or undefined.
 */
export function entryState(entry, installed) {
  if (installed?.revoked) return 'revoked';
  if (entry.status === 'unavailable') {
    if (entry.reasonCode === 'needsNewer') return 'needsNewer';
    if (entry.reasonCode === 'officialFirst') return 'officialFirst';
    return 'unavailable';
  }
  if (entry.restartPending) return 'restart';
  if (entry.status === 'update') return 'update';
  if (entry.status === 'installed') return 'installed';
  return 'new';
}

/** An installed plugin that came from a catalog but is not listed any more (the entry was taken out). Only once a catalog has loaded. */
export function isDelisted(plugin, catalogEntries) {
  if (!plugin || !Array.isArray(catalogEntries) || !catalogEntries.length) return false;
  if (plugin.origin === 'local' || !plugin.userInstalled) return false;
  const id = String(plugin.id).toLowerCase();
  const folder = String(plugin.folder).toLowerCase();
  return !catalogEntries.some((e) => e.kind !== 'skin' && [id, folder].includes(String(e.id).toLowerCase()));
}

/** The installed plugin that belongs to a catalog entry. */
export const installedFor = (entry, plugins) => (entry.kind === 'skin' ? undefined : (plugins ?? []).find((p) => [String(p.id).toLowerCase(), String(p.folder).toLowerCase()].includes(String(entry.id).toLowerCase())));

/**
 * The actions of a catalog entry, in the order the page lists them:
 * install | update | enable | disable | uninstall | acceptRevoked. A third-party plugin cannot even be installed outside expert mode
 * (the host refuses it; the page says so instead of offering it). A skin can only be installed or updated.
 */
export function entryActions(entry, installed, { expert = false } = {}) {
  const state = entryState(entry, installed);
  const actions = [];
  const own = !!installed && entry.kind !== 'skin';
  if (state === 'revoked') actions.push('acceptRevoked');
  else if (state === 'new') {
    if (!(isThirdParty(entry) && !expert)) actions.push('install');
  } else if (state === 'update') {
    // An update that works like an install for a third party (it adds permissions, or it is a skin of an added catalog) needs expert mode too.
    if (!(isThirdParty(entry) && !expert && usesInstallGate(entry, true))) actions.push('update');
  }
  if (own && state !== 'revoked') actions.push(installed.enabled ? 'disable' : 'enable');
  if (own && installed.userInstalled) actions.push('uninstall');
  return actions;
}

/** Why a third-party plugin is not installable here (null: nothing in the way). Shown instead of the install action. */
export const installBlock = (entry, { expert = false } = {}) => (isThirdParty(entry) && !expert && entryState(entry) === 'new' ? 'expert' : null);

/**
 * Does the host treat this change like an install? An update that adds capabilities does (PluginPolicy.Decide turns it into an install); a skin of
 * a catalog the user added always does, updates included (Hud.InstallCatalogEntryAsync).
 */
export const usesInstallGate = (entry, update) => !update || !!entry.addsCapabilities || (entry.kind === 'skin' && isThirdParty(entry));

/**
 * Does this change need the parents' code (or, without parental controls, the couch code again)? A mirror of PluginPolicy.Decide (Core): a plugin
 * from a third party or a local one for install/approve/accept/confirm and for everything under parental controls; CouchHUD's own plugins only for
 * accepting a withdrawn one, confirming a program, and (under parental controls) an install or update that reaches the assistant, a setup or a program.
 * An update that adds capabilities counts as an install. Origin 'skin' is a skin of CouchHUD's own catalog: the unlock is enough, no code.
 * The table in scripts/ui-logic-check.mjs and the one in PluginPolicyGateTableTests (Core) are the same; change both together.
 */
export function needsCode(change, origin, capabilities, parentalOn, addsCapabilities = false) {
  if (origin === 'skin') return false;
  const effective = change === 'update' && addsCapabilities ? 'install' : change;
  const always = ['install', 'approve', 'acceptRevoked', 'confirmExe'];
  if (origin === 'thirdParty' || origin === 'local') return always.includes(effective) || !!parentalOn;
  const reaches = (capabilities ?? []).some((c) => ['assistant', 'setup.run', 'process'].includes(c));
  return effective === 'acceptRevoked' || effective === 'confirmExe' || (!!parentalOn && ['install', 'update'].includes(effective) && reaches);
}

/** The origin the host gates a catalog row by: anything from an added catalog is third-party; a skin of CouchHUD's own catalog is 'skin'. */
export const gateOrigin = (entry) => (isThirdParty(entry) ? 'thirdParty' : entry.kind === 'skin' ? 'skin' : entry.origin ?? 'local');

/** The change name for the parents' grant of a catalog install or update (a skin of an added catalog always asks as an install). */
export const grantChange = (entry, update) => (update && !(entry.kind === 'skin' && isThirdParty(entry)) ? 'update' : 'install');

/** The name of the parents' grant the host expects for a change ('pluginInstall'...). */
export const grantAction = (change) => `plugin${change[0].toUpperCase()}${change.slice(1)}`;

/**
 * The questions before a catalog install or update, in order: a third-party package gets the Fremdentwickler warning first (install) or a
 * plain question (update: the key is pinned already); CouchHUD's own and skins get the plain question. Then always the code step (the parents'
 * code where needsCode says so, the plain unlock otherwise). Only after all of them the call goes out. { blocked } when the page must not ask at all.
 */
export function installPlan(entry, { expert = false, update = false } = {}) {
  const asInstall = usesInstallGate(entry, update);
  if (isThirdParty(entry) && asInstall && !expert) return { blocked: 'expert', steps: [] };
  return { blocked: null, steps: [isThirdParty(entry) && asInstall ? 'warning' : 'confirm', 'code'] };
}

/** The steps of removing a plugin: a plain question, then the code step. */
export const uninstallPlan = () => ({ blocked: null, steps: ['confirm', 'code'] });

/**
 * Runs the steps in order with the page's handlers ({ warning, confirm, code } -> Promise<boolean>); stops at the first one that says no.
 * Resolves to true only when every step agreed, so the call is never reached through a skipped or declined gate.
 */
export async function runGates(steps, handlers) {
  for (const step of steps) {
    const handler = handlers[step];
    if (typeof handler !== 'function') return false;
    if (!(await handler())) return false;
  }
  return true;
}

// ---------------------------------------------------------------------------
// Catalog sources

// i18n-used: plugins.srcerr.privateHost plugins.srcerr.nameImitates plugins.srcerr.pinMismatch plugins.srcerr.officialFirst plugins.srcerr.removed plugins.srcerr.httpsOnly plugins.srcerr.isOfficial plugins.srcerr.twice plugins.srcerr.tooMany plugins.srcerr.badName plugins.srcerr.badKey plugins.srcerr.officialStays plugins.srcerr.unknown plugins.srcerr.ackNeeded plugins.srcerr.officialOnly plugins.srcerr.reserved plugins.srcerr.needsStatement
export const SOURCE_ERROR_KEYS = [
  'privateHost', 'nameImitates', 'pinMismatch', 'officialFirst', 'removed', 'httpsOnly', 'isOfficial', 'twice', 'tooMany', 'badName', 'badKey',
  'officialStays', 'expert', 'unknown', 'ackNeeded', 'officialOnly', 'reserved', 'needsStatement'
];

/** The text key for an error of the catalog sources: the host words its errors itself; a bare key ('catalog.source.privateHost') is mapped here. */
export function sourceErrorKey(message) {
  const m = /^catalog\.source\.(\w+)$/.exec(String(message ?? '').trim());
  return m && SOURCE_ERROR_KEYS.includes(m[1]) ? `plugins.srcerr.${m[1]}` : null;
}

/** May this source be removed? Never the official one, whatever the list says. */
export const canRemoveSource = (source, official) => !!source && source.builtIn !== true && source.removable !== false && !(official && source.url === official.url);

export function hostOf(url) {
  try {
    return new URL(url).hostname;
  } catch {
    return '';
  }
}

/** The list the page shows: CouchHUD's own first and marked official, then the added ones in the order they were added. */
export function sourceRows(sources) {
  if (!sources) return [];
  const official = sources.official ? [{ official: true, url: sources.official.url, name: null, host: hostOf(sources.official.url), removable: false }] : [];
  const extra = (sources.extra ?? []).map((s) => ({ official: false, url: s.url, name: s.name ?? null, host: s.host ?? hostOf(s.url), keyCode: s.keyCode ?? null, signed: !!s.signed, removable: canRemoveSource(s, sources.official) }));
  return [...official, ...extra];
}

/** Sources can be changed only in expert mode (the host refuses elsewhere): in simple mode the page does not show them at all. */
export const sourcesVisible = (expert) => !!expert;

/** Can another source be added? { ok, reason } with reason 'expert' | 'tooMany'. */
export function sourceAddCheck(sources, expert) {
  if (!expert) return { ok: false, reason: 'expert' };
  if (sources && (sources.extra ?? []).length >= (sources.max ?? 8)) return { ok: false, reason: 'tooMany' };
  return { ok: true, reason: null };
}

/** The order of the add flow: the warning with the trust question, then the code, then the call with the acknowledgement. */
export const sourceAddSteps = () => ['warning', 'code'];

/** What the page may send as the publisher key: trimmed, empty is none. */
export const cleanKey = (text) => {
  const v = String(text ?? '').trim();
  return v.length ? v : null;
};

// ---------------------------------------------------------------------------
// Declarative plugin pages

/**
 * Groups the rows of a page by section. Sections come from the plugin in order; a row without a (known) section goes to the first one.
 * Without sections: one plain list. Returns [{ id, title, kind: 'list'|'tiles', rows }]; empty sections are dropped.
 */
export function groupRows(rows, sections) {
  const list = Array.isArray(rows) ? rows : [];
  const known = (sections ?? []).filter((s) => s && typeof s.id === 'string');
  if (!known.length) return list.length ? [{ id: '', title: null, kind: 'list', rows: list }] : [];
  const groups = known.map((s) => ({ id: s.id, title: s.title ?? null, kind: s.kind === 'tiles' ? 'tiles' : 'list', rows: [] }));
  for (const row of list) (groups.find((g) => g.id === row.section) ?? groups[0]).rows.push(row);
  return groups.filter((g) => g.rows.length);
}

/** Cells per line of a tile section: as many as fit a wide screen, never more than the rows. */
export const tileColumns = (count) => Math.max(1, Math.min(4, count));

/** Rows to cursor lines: a list row is its own line, a tile section is cut into lines of `tileColumns` cells. */
export function layoutLines(groups) {
  const lines = [];
  for (const g of groups) {
    if (g.kind === 'tiles') {
      const cols = tileColumns(g.rows.length);
      for (let i = 0; i < g.rows.length; i += cols) lines.push(g.rows.slice(i, i + cols));
    } else for (const row of g.rows) lines.push([row]);
  }
  return lines;
}

const decimals = (step) => (String(step).split('.')[1] ?? '').length;

/** A stepper moved by `delta` steps: stays inside min and max, on the step grid, without float noise (0.1 + 0.2). */
export function stepValue(row, current, delta) {
  const min = Number(row.min);
  const max = Number(row.max);
  const step = Number(row.step) > 0 ? Number(row.step) : 1;
  const from = Number.isFinite(Number(current)) ? Number(current) : min;
  const next = Math.min(max, Math.max(min, from + delta * step));
  const snapped = min + Math.round((next - min) / step) * step;
  return Number(Math.min(max, Math.max(min, snapped)).toFixed(decimals(step) + 2));
}

/** Only a PNG the host sent as a data URI becomes an image; anything else is dropped (the host filters too). */
export const safeImage = (uri) => (typeof uri === 'string' && /^data:image\/png;base64,[A-Za-z0-9+/=]+$/.test(uri) ? uri : null);

/** A choice row shows the label of the chosen option as its value: which option is that? */
export const currentOption = (row) => (row.options ?? []).find((o) => o.label === row.value)?.id;

/** What a press on a row does in the page. */
export function pressKind(row) {
  switch (row?.kind) {
    case 'link': return 'link';
    case 'choice': return 'choice';
    case 'stepper': return 'stepper';
    default: return 'activate';
  }
}

/** "70 %" -> { before: '', number: 70, after: ' %' }; null when the text holds no number. */
export function parseStepper(text) {
  const m = /^([^\d-]*)(-?\d+(?:[.,]\d+)?)(.*)$/s.exec(String(text ?? ''));
  return m ? { before: m[1], number: Number(m[2].replace(',', '.')), after: m[3] } : null;
}

/** A stepper's text with another number, keeping the unit the plugin wrote ("70 %" -> "75 %"). */
export function formatStepper(parts, number) {
  return parts ? `${parts.before}${number}${parts.after}` : String(number);
}

/**
 * Keeps a plugin page and its stepper sends in step. A push from the plugin ("this page changed") while a value is waiting or on its way is
 * remembered, not dropped, and replayed once everything went out; when nothing was pushed the page is just drawn again after the last send.
 * `waiting()` says whether a value still waits for its quiet moment (settler.waiting).
 */
export function pageSync({ waiting, load, render }) {
  let inFlight = 0;
  let dirty = false;
  const idle = () => inFlight === 0 && !waiting();
  return {
    /** The plugin pushed a change. True when the page was read at once. */
    push() {
      if (!idle()) {
        dirty = true;
        return false;
      }
      load();
      return true;
    },
    /** Wraps one send to the host; afterwards the page is read again (a push came meanwhile) or just drawn again. */
    async send(fn) {
      inFlight++;
      try {
        await fn();
      } finally {
        inFlight--;
        if (idle()) {
          if (dirty) {
            dirty = false;
            load();
          } else render();
        }
      }
    },
    get dirty() {
      return dirty;
    }
  };
}
