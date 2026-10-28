// VPN from the couch (NordVPN first): see the state, quick connect, choose a country, disconnect. CouchHUD drives the VPN app that
// is installed on the PC (its command line, src/CouchHud.Shell/Services/VpnManager.cs); signing in happens in that app only.
// Reading works on a locked couch; every change needs the unlock (with parental controls the parents' code).

import { api } from '../core/api.js';
import { el, keepInView, setFocused } from '../core/dom.js';
import { attempt, toast } from '../core/feedback.js';
import { currentView, move, render } from '../core/nav.js';
import { state } from '../core/state.js';
import { chooseDialog } from '../components/overlay.js';
import { openKeyboard } from '../components/keyboard.js';
import { addQuickRows, quick, renderQuick } from '../components/quick-menu.js';
import { ensureUnlocked } from '../components/passcode.js';
import { ensureParentGrant, parental, refreshParental } from '../components/parental.js';
import { sound } from '../sound.js';
import { lang, t } from '../core/i18n.js';

/**
 * Last answer of the host: { provider, name, state, installed, appRunning, hint, targetKind, targetCountry, targetServer, targetConfirmed,
 * tunnel, lastCountry }. targetConfirmed: the tunnel went down and came up again after the request (else the place is only asked for).
 */
state.vpn = null;
/** The provider's fixed country list ({ id, name }), loaded once. */
let countries = null;
/** Expert setting "VPN in the Guide menu" (default on). */
let inGuide = true;
let page = null;

export const setVpnInGuide = (on) => { inGuide = !!on; };
export const vpnInstalled = (s = state.vpn) => !!s?.installed;

/** The country's name in the UI language (the browser knows them all), the provider's English name otherwise. */
export function countryName(id) {
  if (!id) return '';
  try {
    const name = new Intl.DisplayNames([lang()], { type: 'region' }).of(id.toUpperCase());
    if (name && name.toUpperCase() !== id.toUpperCase()) return name;
  } catch { /* old engine: the provider's name below */ }
  return countries?.find((c) => c.id === id)?.name ?? id.toUpperCase();
}

/** One line for the state: "Connected · Germany", "Connecting …", "Not connected". */
export function vpnLine(s = state.vpn) {
  if (!s) return '';
  // i18n-used: vpn.state.notInstalled vpn.state.disconnected vpn.state.connecting vpn.state.connected vpn.state.disconnecting
  const key = 'vpn.state.' + String(s.state).replace(/\W/g, '');
  const label = t(key);
  const where = s.targetServer ?? (s.targetCountry ? countryName(s.targetCountry) : s.targetKind === 'quick' ? t('vpn.quickPlace') : null);
  if (s.state === 'connected' && where && !s.targetConfirmed) return t('vpn.stateRequested', { state: label, place: where });
  if (s.state === 'connected' || s.state === 'connecting') return where ? t('vpn.stateAt', { state: label, place: where }) : s.state === 'connected' ? t('vpn.connectedOutside', { state: label, name: s.name }) : label;
  return label;
}

/** The hint under the state (codes from the host), or ''. */
export function vpnHint(s = state.vpn) {
  if (!s) return '';
  if (s.hint === 'untrusted') return t('vpn.hint.untrusted', { name: s.name });
  if (s.hint === 'noConnection') return t('vpn.hint.noConnection', { name: s.name });
  if (s.hint === 'stillConnected') return t('vpn.hint.stillConnected', { name: s.name });
  if (s.hint === 'startFailed') return t('vpn.hint.startFailed', { name: s.name });
  if (!s.installed) return t('vpn.hint.notInstalled', { name: s.name });
  return '';
}

/** Called once at the start: keeps `state.vpn` current for the page and the Guide menu. */
export async function startVpn() {
  if (!api.vpnStatus) return;
  api.onVpn?.((s) => {
    state.vpn = s;
    if (page && currentView() === page) render();
    if (quick.open) renderQuick();
  });
  const settings = await attempt(() => api.getSettings());
  if (settings) inGuide = settings.vpnInGuide !== false;
  await reloadVpn();
}

export async function reloadVpn() {
  state.vpn = (await attempt(() => api.vpnStatus())) ?? state.vpn;
  if (page && currentView() === page) render();
  return state.vpn;
}

async function loadCountries() {
  countries ??= (await attempt(() => api.vpnCountries())) ?? null;
  return countries ?? [];
}

/**
 * A change of the VPN: always the unlocked couch first (the host refuses every change on a locked couch, a parents' code given there
 * would be lost), then with parental controls the parents' one-time release. The host checks both again.
 */
async function mayChange(reason) {
  if (!(await ensureUnlocked(reason))) return false;
  const status = parental.status ?? (await refreshParental());
  return status?.enabled ? ensureParentGrant(reason, { action: 'vpn' }) : true;
}

async function connect(target) {
  if (!(await mayChange(t('vpn.unlockReason')))) return;
  const s = await attempt(() => api.vpnConnect(target), t('vpn.failed'));
  if (s) state.vpn = s;
}

async function disconnect() {
  if (!(await mayChange(t('vpn.unlockReason')))) return;
  const s = await attempt(() => api.vpnDisconnect(), t('vpn.failed'));
  if (s) state.vpn = s;
}

/** The country list as a dialog, sorted by the name in the UI language; the current or last country preselected. */
async function chooseCountry() {
  const list = await loadCountries();
  if (!list.length) return;
  const s = state.vpn;
  const current = s?.targetCountry ?? s?.lastCountry ?? null;
  const options = list.map((c) => ({ label: countryName(c.id), value: c.id, icon: s?.state === 'connected' && s.targetCountry === c.id ? '✓' : '' }))
    .sort((a, b) => a.label.localeCompare(b.label, lang()));
  const id = await chooseDialog(t('vpn.chooseCountry'), options, current);
  if (id) await connect({ kind: 'country', country: id });
}

async function chooseServer() {
  const typed = await openKeyboard({ title: t('vpn.serverTitle'), hint: t('vpn.serverHint'), placeholder: 'Germany #812', maxLength: 48 });
  if (typed?.trim()) await connect({ kind: 'server', server: typed.trim() });
}

async function openApp() {
  if (!(await mayChange(t('vpn.unlockReason')))) return;
  if ((await attempt(() => api.vpnOpenApp(), t('vpn.openFailed'))) !== undefined) toast(t('vpn.opened', { name: state.vpn?.name ?? 'VPN' }));
}

// Guide menu: switch on or off (last country, else quick connect) and choose a country; only with an installed VPN app.
addQuickRows(() => {
  const s = state.vpn;
  if (!inGuide || !vpnInstalled(s)) return [];
  const busy = s.state === 'connecting' || s.state === 'disconnecting';
  const on = s.state === 'connected';
  return [
    {
      icon: '⛨',
      label: busy ? vpnLine(s) : on ? t('vpn.quick.off') : s.lastCountry ? t('vpn.quick.onCountry', { country: countryName(s.lastCountry) }) : t('vpn.quick.on'),
      detail: busy ? s.name : `${s.name} · ${vpnLine(s)}`,
      keepOpen: true,
      run: async () => {
        if (busy) return;
        if (on) await disconnect();
        else await connect(s.lastCountry ? { kind: 'country', country: s.lastCountry } : { kind: 'quick' });
      }
    },
    {
      icon: '🌐',
      label: t('vpn.chooseCountry'),
      detail: s.name,
      keepOpen: true,
      run: () => chooseCountry()
    }
  ];
});

/** The VPN page (Settings › Network › VPN). */
export function vpnPage() {
  page = {
    id: 'vpn',
    name: t('vpn.title'),
    index: 0,
    list: [],
    rows() {
      const s = state.vpn;
      if (!s) return [];
      if (!s.installed) return [{ icon: '↻', label: t('vpn.recheck'), detail: t('vpn.recheckDetail'), run: () => reloadVpn() }];
      const busy = s.state === 'connecting' || s.state === 'disconnecting';
      const rows = [];
      if (s.state === 'connected' || s.state === 'connecting') rows.push({ icon: '⏻', label: t('vpn.disconnect'), detail: t('vpn.disconnectDetail'), run: () => disconnect(), off: s.state === 'disconnecting' });
      else rows.push({ icon: '⚡', label: t('vpn.quickConnect'), detail: t('vpn.quickConnectDetail', { name: s.name }), run: () => connect({ kind: 'quick' }), off: busy });
      if (s.lastCountry && !(s.state === 'connected' && s.targetCountry === s.lastCountry)) {
        rows.push({ icon: '★', label: t('vpn.connectCountry', { country: countryName(s.lastCountry) }), detail: t('vpn.lastCountry'), run: () => connect({ kind: 'country', country: s.lastCountry }), off: busy });
      }
      rows.push({ icon: '⌖', label: t('vpn.chooseCountry'), detail: t('vpn.chooseCountryDetail'), run: () => chooseCountry(), off: busy });
      if (this.expert) rows.push({ icon: '#', label: t('vpn.server'), detail: t('vpn.serverDetail'), run: () => chooseServer(), off: busy });
      rows.push({ icon: '▣', label: t('vpn.openApp', { name: s.name }), detail: t('vpn.openAppDetail'), run: () => openApp() });
      return rows;
    },
    build(root) {
      const s = state.vpn;
      this.list = this.rows();
      this.index = Math.max(0, Math.min(this.index, this.list.length - 1));
      this.items = this.list.map((r) => el('li', { className: `check-row${r.off ? ' dim' : ''}` },
        el('span', { className: 'check-box', textContent: r.icon }),
        el('span', { className: 'check-main' }, el('strong', { textContent: r.label }), el('small', { textContent: r.detail ?? '' }))));
      this.scroller = el('ul', { className: 'check-list list-scroll' }, this.items);
      const hint = vpnHint(s);
      const pill = s?.state === 'connected' ? el('span', { className: 'pill on', textContent: s.tunnel === 'nordlynx' ? 'NordLynx' : s.tunnel === 'openvpn' ? 'OpenVPN' : t('vpn.on') })
        : s?.state === 'connecting' || s?.state === 'disconnecting' ? el('span', { className: 'spinner' }) : null;
      root.append(el('div', { className: 'files' },
        el('div', { className: 'files-head' },
          el('h2', { textContent: s?.name ?? t('vpn.title') }),
          el('p', { className: 'meta' }, s ? vpnLine(s) : t('vpn.loading'), pill ? ' ' : '', pill ?? ''),
          hint ? el('p', { className: 'meta', textContent: hint }) : null,
          s?.installed && !s.appRunning && s.state !== 'connected' ? el('p', { className: 'meta', textContent: t('vpn.appNotRunning', { name: s.name }) }) : null),
        this.scroller,
        el('p', { className: 'meta', textContent: t('vpn.privacy', { name: s?.name ?? 'VPN' }) })));
      this.update({ instant: true });
    },
    update({ instant = false } = {}) {
      if (!this.items?.length) return;
      setFocused(this.items, this.index);
      keepInView(this.scroller, this.items[this.index], { instant });
    },
    async handle(action) {
      switch (action) {
        case 'up': return this.list.length && move(this, -1, this.list.length, true);
        case 'down': return this.list.length && move(this, 1, this.list.length, true);
        case 'x': return reloadVpn();
        case 'a': {
          const row = this.list[this.index];
          if (!row) return;
          if (row.off) return toast(t('vpn.busyToast'));
          sound.select();
          await row.run();
          if (currentView() === this) render();
          return;
        }
      }
    },
    pointer(node, index, dispatch) {
      this.index = index;
      this.update();
      dispatch('a');
    },
    hints() {
      return [...(this.list?.length ? [['A', t('vpn.hint.select')]] : []), ['X', t('vpn.hint.refresh')], ['B', t('vpn.hint.back')]];
    }
  };
  const opened = page;
  reloadVpn();
  attempt(() => api.getSettings()).then((settings) => {
    opened.expert = !!settings?.expertMode;
    if (currentView() === opened) render();
  });
  return page;
}
