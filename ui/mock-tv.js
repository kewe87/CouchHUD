// Preview of the TV control (LG webOS) without the host: a small state machine like TvController's.
// ?notv: no TV paired yet; ?tvoff: the TV is off; ?tvcert: the TV shows a changed certificate; ?tvnomac: no MAC (no switching on);
// ?tvplain: the TV has no TLS port (pairing asks for an unencrypted one in expert mode); with ?tvseen a TV was paired encrypted before.

const params = new URLSearchParams(location.search);
const INPUTS = [
  { id: 'HDMI_1', label: 'PlayStation 5', connected: true },
  { id: 'HDMI_2', label: 'PC', connected: true },
  { id: 'HDMI_3', label: 'HDMI 3', connected: false },
  { id: 'HDMI_4', label: '<b>Soundbar</b>', connected: true }
];

const tv = {
  paired: !params.has('notv'),
  power: params.has('tvoff') ? 'off' : 'on',
  volume: 18,
  muted: false,
  input: 'HDMI_2',
  pairing: 'idle',
  error: params.has('tvcert') ? 'certChanged' : null,
  mac: params.has('tvnomac') ? null : 'A8:23:FE:01:02:03',
  secure: true,
  certReplaced: false
};
const listeners = [];

const describe = () => ({
  provider: 'lg-webos',
  providerName: 'LG webOS',
  paired: tv.paired,
  address: tv.paired ? '192.168.1.20' : null,
  name: null,
  model: tv.paired ? 'OLED55C3' : null,
  secure: tv.secure,
  macKnown: !!tv.mac,
  mac: tv.mac,
  power: tv.paired ? tv.power : 'unknown',
  volume: tv.paired && tv.power === 'on' ? tv.volume : null,
  muted: tv.paired && tv.power === 'on' ? tv.muted : null,
  input: tv.paired && tv.power === 'on' ? tv.input : null,
  pairing: tv.pairing,
  error: tv.error,
  certReplaced: tv.certReplaced,
  secureBefore: params.has('tvplain') && params.has('tvseen')
});
const publish = () => listeners.forEach((cb) => cb(describe()));
const later = (ms, fn) => setTimeout(() => { fn(); publish(); }, ms);
const needOn = () => {
  if (!tv.paired) throw new Error('No TV paired yet (Settings › TV)');
  if (tv.error === 'certChanged') throw new Error('The TV shows a different certificate. Pair the TV again (Settings › TV)');
  if (tv.power !== 'on') throw new Error('The TV doesn’t answer. Is it on and in the same network?');
};

export function tvMock() {
  return {
    onTv: (cb) => listeners.push(cb),
    tvStatus: async () => describe(),
    tvVolume: async () => describe(),
    tvVolumeStep: async (up) => {
      needOn();
      tv.volume = Math.max(0, Math.min(100, tv.volume + (up ? 1 : -1)));
      return describe();
    },
    tvSetVolume: async (volume) => {
      needOn();
      tv.volume = Math.max(0, Math.min(100, volume));
      return describe();
    },
    tvMute: async (mute) => {
      needOn();
      tv.muted = mute ?? !tv.muted;
      return describe();
    },
    tvPower: async (on) => {
      if (!tv.paired) throw new Error('No TV paired yet (Settings › TV)');
      if (on) {
        if (tv.power === 'on') return describe();
        if (!tv.mac) throw new Error('Can’t switch on: CouchHUD doesn’t know the TV’s MAC address or finds no matching network');
        tv.power = 'waking';
        later(2500, () => { tv.power = 'on'; });
      } else {
        needOn();
        tv.power = 'off';
      }
      return describe();
    },
    tvInputs: async () => {
      needOn();
      return INPUTS;
    },
    tvSwitchInput: async (id) => {
      needOn();
      if (!INPUTS.some((i) => i.id === id)) throw new Error('The TV doesn’t know this input');
      tv.input = id;
      return describe();
    },
    tvDiscover: async () => {
      await new Promise((r) => setTimeout(r, 1200));
      return [{ address: '192.168.1.20', name: '[LG] webOS TV OLED55C3', provider: 'lg-webos' }];
    },
    tvPair: async (address, allowPlain) => {
      if (!/^(10|172\.(1[6-9]|2\d|3[01])|192\.168)\./.test(address)) throw new Error('That’s not an address in your home network (e.g. 192.168.1.20)');
      if (tv.pairing !== 'idle') throw new Error('A pairing is already running. Confirm it on the TV or cancel it first');
      if (params.has('tvplain') && !allowPlain) {
        tv.error = 'plainOnly';
        return describe();
      }
      const replaced = tv.error === 'certChanged';
      tv.pairing = 'connecting';
      publish();
      await new Promise((r) => setTimeout(r, 600));
      tv.pairing = 'prompt';
      publish();
      await new Promise((r) => setTimeout(r, 2500));
      Object.assign(tv, { paired: true, power: 'on', pairing: 'idle', error: null, mac: tv.mac ?? 'A8:23:FE:01:02:03', secure: !allowPlain, certReplaced: replaced });
      publish();
      return describe();
    },
    tvCancelPair: async () => describe(),
    tvForget: async () => {
      Object.assign(tv, { paired: false, error: null, certReplaced: false });
      publish();
      return describe();
    },
    tvSetMac: async (mac) => {
      if (mac && !/^[0-9A-Fa-f]{2}([:-][0-9A-Fa-f]{2}){5}$/.test(mac)) throw new Error('A MAC address looks like this: A8:23:FE:01:02:03');
      tv.mac = mac ? mac.toUpperCase().replace(/-/g, ':') : null;
      return describe();
    }
  };
}
