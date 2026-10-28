// Preview of the VPN (NordVPN) without the host: a small state machine like VpnManager's.
// ?novpn: not installed; ?vpnuntrusted: found outside Program Files; ?vpnfail: connecting never works (not signed in in the app).

// Cannot import core/i18n.js (see mock-api.js); i18n.js publishes t on globalThis. Only called inside functions.
const t = (...a) => globalThis.__couchhudT?.(...a) ?? a[0];
const params = new URLSearchParams(location.search);
const COUNTRIES = [
  ['at', 'Austria'], ['be', 'Belgium'], ['ca', 'Canada'], ['ch', 'Switzerland'], ['de', 'Germany'], ['dk', 'Denmark'], ['es', 'Spain'],
  ['fr', 'France'], ['gb', 'United Kingdom'], ['it', 'Italy'], ['jp', 'Japan'], ['nl', 'Netherlands'], ['no', 'Norway'], ['pl', 'Poland'],
  ['se', 'Sweden'], ['us', 'United States']
].map(([id, name]) => ({ id, name }));

const vpn = {
  state: params.has('novpn') || params.has('vpnuntrusted') ? 'notInstalled' : 'disconnected',
  hint: params.has('vpnuntrusted') ? 'untrusted' : null,
  target: null,
  lastCountry: 'de',
  appRunning: true
};
const listeners = [];

const describe = () => ({
  provider: 'nordvpn',
  name: 'NordVPN',
  state: vpn.state,
  installed: vpn.state !== 'notInstalled',
  appRunning: vpn.appRunning,
  hint: vpn.hint,
  targetKind: vpn.target?.kind ?? null,
  targetCountry: vpn.target?.country ?? null,
  targetServer: vpn.target?.server ?? null,
  tunnel: vpn.state === 'connected' ? 'nordlynx' : null,
  targetConfirmed: vpn.state === 'connected' && !!vpn.target,
  lastCountry: vpn.lastCountry
});
const publish = () => listeners.forEach((cb) => cb(describe()));
const later = (ms, fn) => setTimeout(() => { fn(); publish(); }, ms);

const SERVER = /^([A-Za-z][A-Za-z .]{1,39}) #([1-9][0-9]{0,4})$/;

export function vpnMock() {
  return {
    onVpn: (cb) => listeners.push(cb),
    vpnStatus: async () => describe(),
    vpnCountries: async () => COUNTRIES,
    vpnConnect: async (target) => {
      if (vpn.state === 'notInstalled') throw new Error(t('mock.vpnNotInstalled'));
      if (target?.kind === 'country' && !COUNTRIES.some((c) => c.id === target.country)) throw new Error(t('mock.vpnBadTarget'));
      if (target?.kind === 'server') {
        const m = SERVER.exec(target.server ?? '');
        const country = m && COUNTRIES.find((c) => c.name.toLowerCase() === m[1].toLowerCase());
        if (!country) throw new Error(t('mock.vpnBadTarget'));
        target = { kind: 'server', server: `${country.name} #${m[2]}`, country: null };
        vpn.lastCountry = country.id;
      }
      if (target?.kind === 'country') vpn.lastCountry = target.country;
      vpn.state = 'connecting';
      vpn.hint = null;
      vpn.target = target;
      later(1800, () => {
        if (params.has('vpnfail')) {
          vpn.state = 'disconnected';
          vpn.target = null;
          vpn.hint = 'noConnection';
        } else vpn.state = 'connected';
      });
      return describe();
    },
    vpnDisconnect: async () => {
      vpn.state = 'disconnecting';
      vpn.hint = null;
      later(900, () => {
        vpn.state = 'disconnected';
        vpn.target = null;
      });
      return describe();
    },
    vpnOpenApp: async () => {
      vpn.appRunning = true;
      return { opened: true };
    }
  };
}
