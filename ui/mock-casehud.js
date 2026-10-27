// Preview stand-in for the CaseHUD calls of the host (src/CouchHud.Shell/Services/CaseHudManager.cs), with the schema of
// CaseHUD API v1. Scenarios via the URL: ?casehud=none | stopped | update | busy | error | down | disclaimer | legacy | apioff  (default: running).
// apioff: CaseHUD runs with its API switched off (Settings › Interfaces, CaseHUD's default): no api.json, 403 api_disabled.
// disclaimer: CaseHUD 1.0 before its first-start notice was confirmed ("Open on the PC" confirms it here).
// legacy: a build before 1.0 (no disclaimerAccepted, no lighting in the status, no /lighting).
// Lighting (CaseHUD's "Merge semantics"): turning it on answers ackRequired until "Open on the PC" (or ?lightack); devices are
// listed only while the lighting is on; a PATCH merges (params by key, devices by id) and answers with the full state.
// Imported by mock-api.js; never loaded inside the app.

const query = new URLSearchParams(location.search);
const scenario = query.get('casehud') || 'running';

const svg = (body, w = 160, h = 160) => 'data:image/svg+xml,' + encodeURIComponent(`<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${w} ${h}">${body}</svg>`);

/** A round fan display: gradient, a ring as the gauge and a number. */
const faceArt = (hue, label, value, theme = 'dark') => svg(
  `<defs><radialGradient id="g" cx="50%" cy="40%" r="75%"><stop offset="0" stop-color="hsl(${hue},70%,${theme === 'dark' ? 28 : 78}%)"/><stop offset="1" stop-color="hsl(${hue + 40},60%,${theme === 'dark' ? 8 : 92}%)"/></radialGradient></defs>`
  + `<rect width="160" height="160" fill="url(#g)"/><circle cx="80" cy="80" r="58" fill="none" stroke="hsl(${hue},80%,60%)" stroke-opacity=".25" stroke-width="10"/>`
  + `<circle cx="80" cy="80" r="58" fill="none" stroke="hsl(${hue},90%,62%)" stroke-width="10" stroke-linecap="round" stroke-dasharray="${value * 3.64} 364" transform="rotate(-90 80 80)"/>`
  + `<text x="80" y="92" text-anchor="middle" font-family="sans-serif" font-weight="700" font-size="34" fill="${theme === 'dark' ? '#fff' : '#111'}">${value}</text>`
  + `<text x="80" y="118" text-anchor="middle" font-family="sans-serif" font-size="13" fill="${theme === 'dark' ? '#fff' : '#111'}" opacity=".7">${label}</text>`);

const SETS = [
  { set: 'clean', hue: 200 }, { set: 'neon', hue: 310 }, { set: 'retro', hue: 35 }, { set: 'cyberpunk', hue: 55 }
];
const NAMES = { clean: 'Clean', neon: 'Neon', retro: 'Retro', cyberpunk: 'Cyberpunk' };

const designs = [];
for (const { set, hue } of SETS) {
  // Designs are authored dark; the light variant is rendered on the fly (preview ?theme=light).
  for (const [kind, w, h, label] of [['cpu', 480, 480, 'CPU'], ['gpu', 480, 480, 'GPU'], ['coolant', 480, 480, 'Coolant'], ['wide', 480, 270, 'CPU + GPU']]) {
    designs.push({ id: `${set}-${kind}`, name: `${NAMES[set]} ${label}`, set, theme: 'dark', builtin: true, w, h, hue, label });
  }
}
designs.push({ id: 'my-custom-1', name: 'My gauge', set: 'custom', theme: 'dark', builtin: false, w: 480, h: 480, hue: 150, label: 'FPS' });

let panels = [
  { id: 'p1', name: 'Fan top', model: 'Lian Li UNI FAN TL LCD', w: 400, h: 400, connected: true, mode: 'hud', design: 'neon-cpu', theme: 'dark', brightness: 80, rotation: 0, fanNo: 1, error: '' },
  { id: 'p2', name: 'Fan rear', model: 'Lian Li UNI FAN TL LCD', w: 400, h: 400, connected: true, mode: 'hud', design: 'clean-gpu', theme: 'light', brightness: 60, rotation: 90, fanNo: 2, error: '' },
  { id: 'p3', name: 'Pump', model: 'Lian Li Galahad II LCD', w: 400, h: 400, connected: true, mode: 'hud', design: 'cyberpunk-coolant', theme: 'dark', brightness: 100, rotation: 0, fanNo: 3, error: scenario === 'error' ? 'device_error' : '' }
];
let scenes = [{ id: 'desktop', name: 'Desktop', active: true }, { id: 'gaming', name: 'Gaming', active: false }, { id: 'night', name: 'Night', active: false }, { id: 'media', name: 'Media', active: false }];
let power = true;
let rotation = { enabled: true, scene: 'desktop' };
let awtrix = { enabled: false, devices: [{ name: 'Clock', host: '192.168.1.40', model: 'AWTRIX Light', ok: true }] };
const community = [
  { id: 'c1', name: 'Aurora', author: 'nightowl', set: '', theme: '' }, { id: 'c2', name: 'Paper', author: 'minimal', set: '', theme: '' },
  { id: 'c3', name: 'CRT 84', author: 'retrofan', set: '', theme: '' }, { id: 'c4', name: 'Chrome City', author: 'kay', set: '', theme: '' }
];

let installed = !['none'].includes(scenario);
let running = ['running', 'update', 'busy', 'error', 'disclaimer', 'legacy'].includes(scenario);
let apiOff = scenario === 'apioff';
// The host's text for 403 api_disabled (StringsDe/En "casehud.apiOff"); the page's own key has the same words.
const API_HINT = () => globalThis.__couchhudT?.('casehud.api.hint') ?? 'Turn on the API in CaseHUD under Settings → Interfaces';
const legacy = scenario === 'legacy';
let disclaimerAccepted = scenario !== 'disclaimer';
let lightAck = query.has('lightack');

const num = (key, label, min, max, def, step = 1) => ({ key, type: 'number', label, min, max, default: def, step });
const color = (def) => ({ key: 'color', type: 'color', label: 'Color', default: def });
const EFFECTS = [
  { name: 'solid', label: 'Solid', labelDe: 'Einfarbig', featured: true, params: [color('#ff0000')] },
  { name: 'breathing', label: 'Breathing', labelDe: 'Atmen', featured: true, params: [color('#00e5ff'), num('period', 'Period (s)', 0.5, 10, 3, 0.1)] },
  {
    name: 'rainbow', label: 'Rainbow', labelDe: 'Regenbogen', featured: true, params: [
      num('speed', 'Speed', 1, 10, 5), { key: 'direction', type: 'enum', label: 'Direction', values: ['cw', 'ccw'], default: 'cw' },
      { key: 'mirror', type: 'bool', label: 'Mirror', default: false }
    ]
  },
  { name: 'cycle', label: 'Color cycle', labelDe: 'Farbwechsel', featured: true, params: [num('speed', 'Speed', 1, 10, 3), { key: 'phase', type: 'number', label: 'Phase', default: 0 }] },
  { name: 'wave', label: 'Wave', labelDe: 'Welle', featured: true, params: [color('#8a00ff'), num('length', 'Wave length', 1, 20, 6)] },
  {
    name: 'sensor', label: 'Sensor', labelDe: 'Nach Temperatur', featured: true, params: [
      { key: 'sensor', type: 'enum', label: 'Sensor', values: ['cpu', 'gpu', 'coolant'], default: 'cpu' }, { key: 'caption', type: 'text', label: 'Caption', default: 'CPU' }
    ]
  },
  { name: 'meteor', label: 'Meteor', labelDe: 'Meteor', featured: true, params: [color('#ffffff'), num('tail', 'Tail', 1, 20, 8)] },
  { name: 'lab-strobe', label: 'Strobe (lab)', featured: false, params: [] }
];
const defaults = (name) => Object.fromEntries((EFFECTS.find((e) => e.name === name)?.params ?? []).map((p) => [p.key, p.default]));
// Every effect keeps its last values; switching back brings them back.
const remembered = Object.fromEntries(EFFECTS.map((e) => [e.name, defaults(e.name)]));
const DEVICES = [
  { id: 'ctl-1', name: 'Lighting controller', backend: 'hid', state: 'ok', disabled: false, ignoreConflicts: false, zones: [{ id: 'z1', name: 'Fans' }] },
  { id: 'strip-1', name: 'LED strip', backend: 'hid', state: 'disabled', disabled: true, ignoreConflicts: false, zones: [] },
  { id: 'ram', name: 'RAM', backend: 'smbus', state: 'conflict', message: 'Another RGB program holds the device', disabled: false, ignoreConflicts: false, zones: [] }
];
let lighting = { enabled: false, brightness: 70, effect: 'rainbow' };
const lightingState = () => ({
  ...structuredClone(lighting), params: structuredClone(remembered[lighting.effect]),
  devices: lighting.enabled ? structuredClone(DEVICES) : [], effects: structuredClone(EFFECTS)
});
let busy = scenario === 'busy' ? 'Downloading CaseHUD 1.0.0 …' : null;
let error = scenario === 'down' ? 'CaseHUD did not report back within 15 seconds' : null;
const listeners = [];

const status = () => ({
  version: '1.0.0', apiVersion: 1, sensors: { ok: scenario !== 'error', error: scenario === 'error' ? 'sensor_service_down' : '' }, power,
  gaming: { active: scenario === 'update', source: scenario === 'update' ? 'external' : '', game: scenario === 'update' ? 'Cyber Racer 2077' : '' },
  rotation, awtrix, panels: disclaimerAccepted ? structuredClone(panels) : [], updateAvailable: scenario === 'update' ? '1.2.0' : '',
  // CaseHUD 1.0 only: older builds send neither field.
  ...(legacy ? {} : { disclaimerAccepted, lighting: { enabled: lighting.enabled, active: lighting.enabled && disclaimerAccepted } })
});

const stateName = () => (!installed ? 'notInstalled' : apiOff ? 'apiOff' : running ? 'apiOn' : 'notRunning');
const describe = () => ({
  installed, version: installed ? '1.0.0' : '', running: running && !apiOff, state: stateName(), status: running && !apiOff ? status() : null,
  update: { available: scenario === 'update', version: scenario === 'update' ? '1.2.0' : '', notes: scenario === 'update' ? '• New neon designs\n• Faster preview' : '', installable: true, problem: null, latest: '1.2.0' },
  busy, error, options: { autostart: true, fps: false, lease: true, api: installApi, full: true }, unsigned: true
});

let installApi = true;
const publish = () => listeners.forEach((cb) => cb(describe()));
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
let tick = 0;

/** The preview's setSetting('caseHudInstallApi') lands here (mock-api.js). */
export function setMockInstallApi(on) {
  installApi = !!on;
  publish();
}

export function caseHudMock() {
  return {
    onCaseHud: (cb) => listeners.push(cb),
    caseHudStatus: async () => describe(),
    caseHudCheckUpdate: async () => describe(),
    caseHudStart: async () => {
      busy = 'CaseHUD …';
      publish();
      await wait(600);
      busy = null;
      running = true;
      publish();
      return describe();
    },
    caseHudInstall: async () => {
      for (const text of ['Downloading CaseHUD 1.0.0 …', 'Waiting for administrator approval …', 'Installing CaseHUD 1.0.0 …']) {
        busy = text;
        publish();
        await wait(700);
      }
      busy = null;
      installed = true;
      running = true;
      publish();
      return describe();
    },
    caseHudPrepare: async () => ({ token: 'mock-token', version: '1.0.0', installed, isUpdate: installed, unsigned: false, notes: 'Neue Displays und Szenen', error: null }),
    caseHudUpdate: async () => describe(),
    // The preview stands in for the user confirming the notices in CaseHUD's window.
    caseHudSetApi: async (on) => {
      await wait(300);
      apiOff = !on;
      running = true;
      publish();
      return describe();
    },
    caseHudOpenGui: async () => {
      disclaimerAccepted = true;
      lightAck = true;
      setTimeout(publish, 400);
      return null;
    },
    caseHudLighting: async () => {
      if (apiOff) throw new Error(API_HINT());
      if (legacy) throw new Error('not_found');
      return lightingState();
    },
    caseHudPatchLighting: async (patch) => {
      await wait(150);
      // 409: nothing is applied, not even brightness or effect sent along.
      if (patch.enabled === true && !lightAck) return { problem: 'ackRequired' };
      // Only found devices (none while the lighting is off): an unknown id is a 400.
      const devices = Object.entries(patch.devices ?? {});
      if (devices.some(([id]) => !lighting.enabled || !DEVICES.some((d) => d.id === id))) throw new Error('CaseHUD refused the change: unknown device');
      if ('enabled' in patch) lighting.enabled = patch.enabled;
      if ('brightness' in patch) lighting.brightness = Math.max(0, Math.min(100, Math.round(patch.brightness)));
      if (patch.effect) lighting.effect = patch.effect;
      if (patch.params) Object.assign(remembered[lighting.effect], patch.params);
      for (const [id, change] of devices) {
        const device = DEVICES.find((d) => d.id === id);
        device.disabled = change.disabled;
        device.state = change.disabled ? 'disabled' : id === 'ram' ? 'conflict' : 'ok';
      }
      publish();
      return { state: lightingState() };
    },
    caseHudPanels: async () => structuredClone(panels),
    caseHudPatchPanel: async (id, patch) => {
      const p = panels.find((x) => x.id === id);
      Object.assign(p, Object.fromEntries(Object.entries(patch).filter(([, v]) => v !== undefined && v !== null)));
      publish();
      return structuredClone(p);
    },
    caseHudDesigns: async () => designs.map(({ hue, label, ...d }) => d),
    caseHudSets: async () => SETS.flatMap(({ set }) => ['dark', 'light'].map((theme) => ({ set, theme, name: `${NAMES[set]} ${theme === 'dark' ? 'Dark' : 'Light'}` }))),
    caseHudApplySet: async (set, theme) => {
      const fans = ['cpu', 'gpu', 'coolant'];
      panels.forEach((p, i) => { p.design = `${set}-${fans[i % 3]}`; p.theme = theme; p.mode = 'hud'; });
      publish();
      return null;
    },
    caseHudScenes: async () => structuredClone(scenes),
    caseHudActivateScene: async (id) => { scenes.forEach((s) => (s.active = s.id === id)); rotation = { ...rotation, scene: id }; publish(); return null; },
    caseHudRotation: async (enabled) => { rotation = { ...rotation, enabled }; publish(); return null; },
    caseHudNextScene: async () => {
      const i = scenes.findIndex((s) => s.active);
      const next = scenes[(i + 1) % scenes.length];
      scenes.forEach((s) => (s.active = s === next));
      rotation = { ...rotation, scene: next.id };
      publish();
      return null;
    },
    caseHudPower: async (on) => { power = on; publish(); return null; },
    caseHudIdentify: async () => null,
    caseHudCommunity: async () => structuredClone(community),
    caseHudImport: async (id) => {
      const c = community.find((x) => x.id === id);
      designs.push({ id: `community-${id}`, name: c.name, set: 'custom', theme: 'dark', builtin: false, w: 480, h: 480, hue: 90, label: c.name });
      return null;
    },
    caseHudAwtrix: async (enabled) => { awtrix = { ...awtrix, enabled }; publish(); return null; },
    caseHudPreview: async (kind, id, _size, theme) => {
      tick++;
      if (kind === 'panel') {
        const p = panels.find((x) => x.id === id);
        const d = designs.find((x) => x.id === p.design) ?? designs[0];
        // ?demo (README screenshots): steady, believable values instead of the ticking counter.
        const value = query.has('demo') ? [46, 63, 31][(p.fanNo - 1) % 3] : (42 + tick * 7 + p.fanNo * 11) % 100;
        return faceArt(d.hue, d.label, value, p.theme);
      }
      const d = designs.find((x) => x.id === id);
      if (d) return faceArt(d.hue, d.label, 64, theme || d.theme);
      const c = community.find((x) => x.id === id);
      return faceArt(90 + community.indexOf(c) * 70, c?.name ?? '', 58, theme || 'dark');
    }
  };
}
