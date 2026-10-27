import { caseHudMock, setMockInstallApi } from './mock-casehud.js';
import { demoMode, applyDemo } from './mock-demo.js';

// Stand-in for the host bridge so the UI can be developed in a plain browser
// (node scripts/preview-server.js). Never loaded inside the app.

// Cannot import core/i18n.js (that module awaits systemInfo() from here); i18n.js publishes t on globalThis.
// Only call t() inside functions/getters that run after startup, never at module top level.
const t = (...a) => globalThis.__couchhudT?.(...a) ?? a[0];
// Defines properties whose text is computed on read (so the language is known by then) but can still be overwritten.
const lz = (o, f) => {
  for (const [key, fn] of Object.entries(f)) {
    let v, set = false;
    Object.defineProperty(o, key, { enumerable: true, configurable: true, get: () => (set ? v : fn()), set: (x) => { v = x; set = true; } });
  }
  return o;
};
const previewLanguage = () => { try { return localStorage.getItem('couchhud-preview-language'); } catch { return null; } };

const CDN = 'https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps';
const day = 86400000;

const steam = (appid, name, lastPlayedDaysAgo) => ({
  id: `steam:${appid}`,
  source: 'steam',
  type: 'game',
  name,
  lastPlayed: lastPlayedDaysAgo == null ? 0 : Date.now() - lastPlayedDaysAgo * day,
  favorite: false,
  art: {
    cover: [`${CDN}/${appid}/library_600x900.jpg`],
    hero: [`${CDN}/${appid}/library_hero.jpg`],
    logo: [`${CDN}/${appid}/logo.png`]
  }
});

const library = [
  steam(1245620, 'Elden Ring', 0),
  steam(1091500, 'Cyberpunk 2077', 2),
  steam(1086940, "Baldur's Gate 3", 5),
  steam(367520, 'Hollow Knight', 12),
  steam(413150, 'Stardew Valley', 40),
  steam(1145360, 'Hades', null),
  steam(271590, 'Grand Theft Auto V', null),
  steam(1174180, 'Red Dead Redemption 2', null),
  steam(990080, 'Hogwarts Legacy', null),
  steam(632360, 'Risk of Rain 2', null),
  steam(504230, 'Celeste', null),
  steam(374320, 'Dark Souls III', null),
  steam(292030, 'The Witcher 3: Wild Hunt', null),
  steam(1817070, "Marvel's Spider-Man Remastered", null),
  { id: 'epic:fortnite', source: 'epic', type: 'game', name: 'Fortnite', lastPlayed: Date.now() - 3 * day, favorite: false, art: { cover: [], hero: [], logo: [] } },
  { id: 'custom:youtube', source: 'custom', type: 'app', name: 'YouTube', lastPlayed: 0, favorite: false, art: { cover: [], hero: [], logo: [] } },
  { id: 'custom:netflix', source: 'custom', type: 'app', name: 'Netflix', lastPlayed: 0, favorite: false, art: { cover: [], hero: [], logo: [] } },
  { id: 'custom:spotify', source: 'custom', type: 'app', name: 'Spotify', lastPlayed: 0, favorite: false, art: { cover: [], hero: [], logo: [], icon: ['data:image/svg+xml,%3Csvg xmlns=%27http://www.w3.org/2000/svg%27 viewBox=%270 0 64 64%27%3E%3Ccircle cx=%2732%27 cy=%2732%27 r=%2730%27 fill=%27%231db954%27/%3E%3Cpath d=%27M16 24q16-5 32 3M18 33q14-4 28 3M20 41q12-3 24 2%27 stroke=%27%23000%27 stroke-width=%274%27 fill=%27none%27 stroke-linecap=%27round%27/%3E%3C/svg%3E'] } },
  { id: 'custom:kodi', source: 'custom', type: 'app', name: 'Kodi', lastPlayed: 0, favorite: false, art: { cover: [], hero: [], logo: [], icon: ['data:image/svg+xml,%3Csvg xmlns=%27http://www.w3.org/2000/svg%27 viewBox=%270 0 64 64%27%3E%3Cpath d=%27M32 4l28 28-28 28L4 32z%27 fill=%27%2317b2e7%27/%3E%3Ccircle cx=%2732%27 cy=%2732%27 r=%2710%27 fill=%27%23fff%27/%3E%3C/svg%3E'] } }
];
library[0].favorite = true;
// Preview: ?demo shows made-up games with generated artwork (README screenshots, no store covers).
if (demoMode) applyDemo(library);
// Preview: ?many=400 adds that many generated entries (scroll performance checks).
for (let i = 0, n = +(new URLSearchParams(location.search).get('many') || 0); i < n; i++) {
  library.push(lz({ id: `gen:${i}`, source: i % 2 ? 'epic' : 'gog', type: 'game', lastPlayed: Date.now() - (i + 5) * day, favorite: false, art: { cover: [], hero: [], logo: [] } }, { name: () => t('mock.generated', { n: i + 1 }) }));
}

const collections = [
  lz({ id: 'koop', items: ['steam:1091500', 'steam:632360', 'steam:504230', 'steam:367520', 'custom:kodi'], onHome: true }, { name: () => t('mock.collCoop') }),
  lz({ id: 'rpg', items: ['steam:1245620', 'steam:1086940', 'steam:292030', 'steam:374320'], onHome: false }, { name: () => t('mock.collAdventures') })
];

let isShell = false;
let session = null;
const audio = {
  volume: 42,
  muted: false,
  canSwitch: true,
  devices: [
    { id: 'tv', name: 'LG OLED (NVIDIA High Definition Audio)', kind: 'tv', isDefault: true },
    lz({ id: 'headset', kind: 'headphones', isDefault: false }, { name: () => t('mock.audioHeadset') }),
    { id: 'soundbar', name: 'Soundbar (Realtek)', kind: 'speakers', isDefault: false }
  ]
};
const appVolumes = [
  { id: '4711', name: 'Cyberpunk 2077', isSystem: false, isGame: true, volume: 80, muted: false },
  { id: '4812', name: 'Discord', isSystem: false, isGame: false, volume: 55, muted: false },
  { id: '5120', name: 'Microsoft Edge', isSystem: false, isGame: false, volume: 100, muted: true },
  { id: '5230', name: 'Spotify', isSystem: false, isGame: false, volume: 35, muted: false },
  lz({ id: 'system', isSystem: true, isGame: false, volume: 70, muted: false }, { name: () => t('mock.audioSystemSounds') })
];
const wifi = {
  available: !new URLSearchParams(location.search).has('nowifi'),
  ssid: 'FRITZ!Box 7590',
  known: ['FRITZ!Box 7590'],
  networks: [
    { ssid: 'FRITZ!Box 7590', signal: 88, secured: true, security: 'psk', supported: true },
    { ssid: 'FRITZ!Box 7590 5GHz', signal: 72, secured: true, security: 'psk', supported: true },
    { ssid: 'Nachbar-WLAN', signal: 46, secured: true, security: 'psk', supported: true },
    { ssid: 'Firma-Intern', signal: 41, secured: true, security: 'enterprise', supported: false },
    { ssid: 'Cafe Gast', signal: 30, secured: false, security: 'open', supported: true },
    { ssid: 'Vodafone Hotspot', signal: 18, secured: false, security: 'open', supported: true }
  ]
};
const listeners = { unlocked: [], session: [], audio: [], toast: [], updates: [], catalogUpdates: [], selfUpdate: [], assistant: [], remote: [], notification: [], parental: [], browser: [], browserui: [], pad: [], btRecovery: [] };
// ?nopad: no controller and Bluetooth off (the code prompt's help for keyboard and mouse); ?btcountdown: the automatic switch-on asks.
const noPad = new URLSearchParams(location.search).has('nopad');
let btOn = !noPad;
let btAutoOn = true;
let btDeclined = false;
if (new URLSearchParams(location.search).has('btcountdown')) setTimeout(() => listeners.btRecovery.forEach((cb) => cb({ phase: 'countdown', seconds: 10 })), 2500);
const profiles = {};
let dl = 0.31;
const display = {
  current: { width: 3840, height: 2160, hz: 60 },
  modes: [[3840, 2160, 120], [3840, 2160, 60], [2560, 1440, 120], [2560, 1440, 60], [1920, 1080, 120], [1920, 1080, 60]].map(([width, height, hz]) => ({ width, height, hz })),
  hdrSupported: true,
  hdrOn: false
};
// Preview: screenshots drawn as gradients (the real app shows the image files).
const fakeShot = (title, hue) => 'data:image/svg+xml,' + encodeURIComponent(`<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 160 90"><defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="hsl(${hue},70%,45%)"/><stop offset="1" stop-color="hsl(${hue + 50},60%,15%)"/></linearGradient></defs><rect width="160" height="90" fill="url(#g)"/><text x="8" y="80" font-family="sans-serif" font-size="9" fill="white" opacity="0.8">${title}</text></svg>`);
const plugins = [
  lz({
    folder: 'CouchHud.Plugin.Wand', id: 'wand', name: 'Wand', version: '1.0.0', author: 'kewe87', homepage: 'https://www.wand.com',
    enabled: true, loaded: true, error: null, userInstalled: false,
    pages: [lz({ id: 'status', title: 'Wand', icon: '🪄' }, { description: () => t('mock.wandPageDesc') })],
    settings: [
      lz({ key: 'autostart', kind: 'toggle', choices: [], value: 'false' }, { label: () => t('mock.wandAutostartLabel'), description: () => t('mock.wandAutostartDesc') }),
      lz({ key: 'path', kind: 'text', choices: [], value: '' }, { label: () => t('mock.wandPathLabel'), description: () => t('mock.wandPathDesc') })
    ],
    tools: ['wand_open_cheats']
  }, { description: () => t('mock.wandDesc') }),
  lz({ folder: 'Kodi', id: 'kodi', name: 'Kodi', version: '0.3.0', author: 'community', homepage: null, enabled: false, loaded: false, error: null, userInstalled: true, pages: [], settings: [], tools: [] }, { description: () => t('mock.kodiPluginDesc') })
];
// Preview catalog: one of each status (update, installed, new, needs newer CouchHUD, no hash).
const catalog = [
  lz({ id: 'wand', name: 'Wand', author: 'kewe87', kind: 'plugin', version: '1.1.0', installedVersion: '1.0.0', status: 'update', restartPending: false, reason: null }, { description: () => t('mock.wandDesc') }),
  lz({ id: 'kodi', name: 'Kodi', author: 'community', kind: 'plugin', version: '0.3.0', installedVersion: '0.3.0', status: 'installed', restartPending: false, reason: null }, { description: () => t('mock.catKodiDesc') }),
  lz({ id: 'synthwave', name: 'Synthwave', author: 'community', kind: 'skin', version: '1.0.2', installedVersion: null, status: 'notInstalled', restartPending: false, reason: null, unsigned: true }, { description: () => t('mock.catSynthDesc') }),
  lz({ id: 'signalrgb', name: 'SignalRGB', author: 'community', kind: 'plugin', version: '2.0.0', installedVersion: null, status: 'unavailable', restartPending: false, minCouchHud: '9.0.0' }, { description: () => t('mock.catSignalDesc'), reason: () => t('mock.catSignalReason', { min: '9.0.0', running: '0.3.0' }) }),
  lz({ id: 'broken', author: 'jemand', kind: 'plugin', version: '1.0.0', installedVersion: null, status: 'unavailable', restartPending: false }, { name: () => t('mock.catBrokenName'), description: () => t('mock.catBrokenDesc'), reason: () => t('mock.catBrokenReason') })
];
const catalogState = () => ({ entries: structuredClone(catalog), fromCache: false, fetchedAt: Date.now(), error: null, version: '0.3.0' });
const catalogUpdates = () => structuredClone(catalog.filter((c) => c.status === 'update'));
const wandRows = () => [
  { id: 'state', label: 'Wand', detail: t('mock.wandFound', { path: 'C:\\Users\\kewe87\\AppData\\Local\\Wand\\Wand.exe' }), icon: '✓', value: t('mock.wandInstalled'), on: true },
  { id: 'open', label: t('mock.wandOpen'), detail: session ? t('mock.wandCheatsFor', { name: session.name }) : t('mock.wandStartsOrFront'), icon: '🪄' }
];
const gameFolders = ['D:\\Games'];
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const saveBackups = [
  { id: '2026-10-18_21-40.zip', created: Date.now() - 86400_000, sizeBytes: 1_180_000, fileCount: 14, safety: false, complete: true },
  { id: '2026-10-15_20-00_unvollstaendig.zip', created: Date.now() - 4 * 86400_000, sizeBytes: 300_000, fileCount: 3, safety: false, complete: false },
  { id: '2026-10-12_19-05.zip', created: Date.now() - 7 * 86400_000, sizeBytes: 1_020_000, fileCount: 13, safety: false, complete: true }
];
const saveDir = 'C:\\Users\\kewe87\\AppData\\Roaming\\CouchHud\\Backups';
const saveSettings = { backupDir: saveDir, defaultBackupDir: saveDir, keep: 5, includeConfig: false };
const notes = [
  lz({ id: 'n1', source: 'windows', app: demoMode ? 'Messenger' : 'WhatsApp', time: Date.now() - 12 * 60_000, read: false }, { title: () => t('mock.noteMomTitle'), body: () => t('mock.noteMomBody') }),
  lz({ id: 'n2', source: 'couchhud', app: 'CouchHUD', title: library[0].name, time: Date.now() - 3 * 3_600_000, read: true }, { body: () => t('mock.notePlayed') }),
  lz({ id: 'n3', source: 'windows', app: demoMode ? 'Chat' : 'Discord', time: Date.now() - 5 * 3_600_000, read: true }, { title: () => t('mock.noteLenaTitle'), body: () => t('mock.noteLenaBody') })
];
// Preview: a WhatsApp message arrives a few seconds after start (not in ?demo, it would cover the screenshots).
if (!demoMode) setTimeout(() => {
  const n = { id: 'n4', source: 'windows', app: 'WhatsApp', title: 'Tim', body: t('mock.noteTimBody'), time: Date.now(), read: false };
  notes.unshift(n);
  listeners.notification.forEach((cb) => cb(n));
}, 4000);
// Preview only: a QR-looking pattern (the host draws a real one).
function fakeQr() {
  let path = '';
  const finder = (x, y) => (path += `M${x},${y}h7v7h-7zM${x + 1},${y + 1}v5h5v-5zM${x + 2},${y + 2}h3v3h-3z`);
  finder(1, 1); finder(21, 1); finder(1, 21);
  for (let y = 1; y < 28; y++) for (let x = 1; x < 28; x++) {
    const inFinder = (x < 9 && y < 9) || (x > 19 && y < 9) || (x < 9 && y > 19);
    if (!inFinder && ((x * 7 + y * 13 + x * y) % 5 < 2)) path += `M${x},${y}h1v1h-1z`;
  }
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 29 29" shape-rendering="crispEdges"><rect width="29" height="29" fill="#fff"/><path d="${path}" fill="#000" fill-rule="evenodd"/></svg>`;
}
const emit = (name, payload) => listeners[name].forEach((cb) => cb(payload));
let pendingConfirm = null;
const browser = {
  open: false, url: '', title: '', zoom: 150,
  bookmarks: [
    { name: 'YouTube', url: 'https://www.youtube.com/' },
    { name: 'Twitch', url: 'https://www.twitch.tv/' },
    { name: 'Wikipedia', url: 'https://de.wikipedia.org/' },
    { name: 'DuckDuckGo', url: 'https://duckduckgo.com/' }
  ]
};

const security = { policies: 'none', couchLock: 'code', hasPasscode: true, blockTaskManager: true, asShell: true, autoLogon: true, autoLogonUser: 'player', passwordlessOnly: false, accountPassword: true, user: 'player', service: { state: 'running', label: 'running', version: '0.4.1', expected: true, healthy: true, alarm: false, problem: false, repair: false, repairScript: true } };
let passcode = null;
let unlockedUntil = 0;

const launchers = [
  { id: 'steam', name: 'Steam', exe: 'C:\\Program Files (x86)\\Steam\\steam.exe', mode: 'standby', running: true },
  { id: 'epic', name: 'Epic Games', exe: 'C:\\Program Files (x86)\\Epic Games\\Launcher\\Portal\\Binaries\\Win64\\EpicGamesLauncher.exe', mode: 'demand', running: false },
  { id: 'ea', name: 'EA app', exe: 'C:\\Program Files\\Electronic Arts\\EA Desktop\\EA Desktop\\EADesktop.exe', mode: 'off', running: false }
];

const startup = [
  { id: 'Run:Discord', name: 'Discord', source: 'user', command: 'Update.exe --processStart Discord.exe', disabled: false, mode: 'couch', suggested: 'couch' },
  { id: 'Run32:Steam', name: 'Steam', source: 'all32', command: 'steam.exe -silent', disabled: false, mode: 'couch', suggested: 'couch' },
  { id: 'Run:SignalRGB', name: 'SignalRGB', source: 'user', command: 'SignalRgbLauncher.exe', disabled: false, mode: 'couch', suggested: 'couch' },
  { id: 'Run:OneDrive', name: 'OneDrive', source: 'user', command: 'OneDrive.exe /background', disabled: false, mode: 'couch', suggested: 'pc' },
  { id: 'Run:com.squirrel.Teams.Teams', name: 'Microsoft Teams', source: 'user', command: 'Update.exe --processStart Teams.exe', disabled: false, mode: 'pc', suggested: 'pc' },
  { id: 'Run:Spotify', name: 'Spotify', source: 'user', command: 'Spotify.exe /minimized', disabled: true, mode: 'couch', suggested: 'pc' }
];
startup.forEach((e) => { const kind = e.source; lz(e, { source: () => (kind === 'user' ? t('mock.srcUser') : t('mock.srcAllUsers32')) }); });
const services = { taskInstalled: false, policyHelper: false, policiesSet: false, stuckPolicies: false, services: [
  { name: 'WSearch', displayName: '', status: 'running', automatic: true, suggested: true, selected: true },
  { name: 'SysMain', displayName: 'SysMain (Superfetch)', status: 'running', automatic: true, suggested: true, selected: false },
  { name: 'Spooler', displayName: '', status: 'running', automatic: true, suggested: true, selected: false },
  { name: 'DiagTrack', displayName: '', status: 'running', automatic: true, suggested: true, selected: false },
  { name: 'NVDisplay.ContainerLocalSystem', displayName: 'NVIDIA Display Container LS', status: 'running', automatic: true, suggested: false, selected: false }
] };
{
  const names = { WSearch: () => t('mock.svcSearch'), Spooler: () => t('mock.svcSpooler'), DiagTrack: () => t('mock.svcTelemetry') };
  services.services.forEach((sv) => { if (names[sv.name]) lz(sv, { displayName: names[sv.name] }); });
}
let updates = {
  windows: [
    lz({ id: 'kb1', source: 'windows', sizeBytes: 812_000_000, important: true }, { name: () => t('mock.winUpdateName') }),
    { id: 'kb2', source: 'windows', name: 'NVIDIA - Display - 32.0.15.7700', sizeBytes: 905_000_000, important: false }
  ],
  apps: [
    { id: 'Discord.Discord', source: 'winget', name: 'Discord', version: '1.0.9163', available: '1.0.9164' },
    { id: 'Mozilla.Firefox.de', source: 'winget', name: 'Mozilla Firefox (x64 de)', version: '130.0', available: '131.0.2' },
    { id: 'Valve.Steam', source: 'winget', name: 'Steam', version: '2.10.91.91', available: '2.10.91.94' }
  ],
  rebootRequired: false,
  wingetAvailable: true,
  windowsError: null,
  busy: null,
  checkedAt: Date.now() - 42 * 60_000
};
let selfUpdate = {
  current: '0.3.0', enabled: true, available: true, version: '0.4.0',
  get notes() { return t('mock.selfNotes'); },
  pageUrl: 'https://github.com/kewe87/CouchHUD/releases/tag/v0.4.0', ready: true, canInstall: true,
  problem: null, error: null, checkedAt: Date.now() - 42 * 60_000, busy: null
};
const setSelfUpdate = (u) => emit('selfUpdate', (selfUpdate = { ...selfUpdate, ...u }));
const setUpdates = (u) => emit('updates', (updates = { ...updates, ...u }));
const haNames = {
  'scene.kino': () => t('mock.haCinema'), 'scene.gaming': () => t('mock.haGamingLight'), 'light.wohnzimmer': () => t('mock.haCeiling'),
  'light.tv_led': () => t('mock.haTvBacklight'), 'climate.wohnzimmer': () => t('mock.haHeating'), 'sensor.temp': () => t('mock.haTemp'),
  'sensor.humidity': () => t('mock.haHumidity'), 'sensor.pc_power': () => t('mock.haPcPower'), 'binary_sensor.door': () => t('mock.haDoor')
};
const ha = [
  { id: 'scene.kino', domain: 'scene', name: '', state: 'scening', controllable: true },
  { id: 'scene.gaming', domain: 'scene', name: '', state: 'scening', controllable: true },
  { id: 'light.wohnzimmer', domain: 'light', name: '', state: 'on', controllable: true },
  { id: 'light.tv_led', domain: 'light', name: '', state: 'off', controllable: true },
  { id: 'switch.subwoofer', domain: 'switch', name: 'Subwoofer', state: 'on', controllable: true },
  { id: 'media_player.sonos', domain: 'media_player', name: 'Sonos Arc', state: 'playing', controllable: true },
  { id: 'climate.wohnzimmer', domain: 'climate', name: '', state: 'heat', controllable: false },
  { id: 'sensor.temp', domain: 'sensor', name: '', state: '21.4', unit: '°C', deviceClass: 'temperature', controllable: false },
  { id: 'sensor.humidity', domain: 'sensor', name: '', state: '48', unit: '%', deviceClass: 'humidity', controllable: false },
  { id: 'sensor.pc_power', domain: 'sensor', name: '', state: '412', unit: 'W', deviceClass: 'power', controllable: false },
  { id: 'binary_sensor.door', domain: 'binary_sensor', name: '', state: 'off', deviceClass: 'door', controllable: false }
];
ha.forEach((e) => { if (haNames[e.id]) lz(e, { name: haNames[e.id] }); });
const settings = { language: ['auto', 'de', 'en'].includes(previewLanguage()) ? previewLanguage() : 'auto', weatherPlace: lz({ name: 'Berlin', latitude: 52.52, longitude: 13.41 }, { region: () => t('mock.regionBerlin') }), homeAssistantUrl: 'http://homeassistant.local:8123', homeAssistantTokenSet: true, homeAssistantPinned: ['scene.kino', 'light.tv_led'], sounds: true, audioMode: 'music', musicVolume: 35, maximizeBrowser: true, guideTapInGame: true, integrationMode: 'full', closeExplorerInLight: false, selfUpdateCheck: true, onlineArtwork: true, artworkCacheMb: 42, caseHudInstallAutostart: true, caseHudInstallFps: false, caseHudInstallApi: true, caseHudGamingLease: true, bluetoothAutoOn: true, reduceMotion: new URLSearchParams(location.search).has("calm"), pointer: new URLSearchParams(location.search).get('pointer') ?? 'skin', mouseUnlocked: new URLSearchParams(location.search).has('mouse'), acceptedTerms: new URLSearchParams(location.search).has('terms') ? 0 : 99, setupWizardDone: ['terms', 'wizard'].some((k) => new URLSearchParams(location.search).has(k)) ? 0 : 99, screensaverMinutes: 5, screensaverStyle: 'art', displayOffMinutes: 0, pixelShift: true, pauseGameInMenu: false, gameBarButton: false, suppressCompetitors: true, rumble: true, autoSwitchHeadphones: true, trailerPreviews: true, returnHoldMs: 1000, runStartupApps: true, killExplorerOnReturn: false, steamGridDbKeySet: false,
  aiProvider: 'anthropic', aiModel: '', anthropicKeySet: true, openAiKeySet: false,
  aiModels: { anthropic: ['claude-sonnet-5-5', 'claude-opus-5-5', 'claude-haiku-5-5'], openai: ['gpt-5', 'gpt-5-mini'] },
  mcpEnabled: true, mcpLan: true, mcpPort: 8765, mcpToken: '3f9c1e7a5b2d48e6a0c4f1b7d9e2a6c8b5f0e3d1a7c9b2e4',
  mcpUrls: ['http://localhost:8765/mcp', 'http://192.168.178.42:8765/mcp'], remoteEnabled: true,
  mirrorNotifications: true, notificationStatus: 'allowed', nightMode: 'evening', nightModeStrength: 50, soundVolume: 70, soundSet: '', uiHdr: 'auto' };

// A small fake file system for the file browser and folder scan.
const folder = (...names) => names.map((n) => ({ name: n, kind: 'folder' }));
const file = (name, size) => ({ name, kind: 'file', size });
const fakeFs = {
  'C:\\': folder('Program Files', 'Program Files (x86)', 'Users', 'Windows'),
  'D:\\': folder('Games', 'SteamLibrary', 'Emulatoren'),
  'D:\\Games': folder('Hades II', 'Hollow Knight Silksong', 'RetroArch', 'Tools'),
  'D:\\Games\\Hades II': [...folder('Content'), file('Hades2.exe', 98_000_000), file('unins000.exe', 3_000_000)],
  'D:\\Games\\Hollow Knight Silksong': [file('Hollow Knight Silksong.exe', 650_000), file('UnityCrashHandler64.exe', 1_200_000)],
  'D:\\Games\\RetroArch': [file('retroarch.exe', 18_000_000), file('retroarch_debug.exe', 22_000_000)],
  'D:\\Games\\Tools': [file('setup.exe', 5_000_000)],
  'D:\\Emulatoren': folder('Dolphin', 'PCSX2')
};
const join = (dir, name) => (dir.endsWith('\\') ? dir + name : `${dir}\\${name}`);
const parentOf = (p) => {
  if (/^[A-Z]:\\$/.test(p)) return null;
  const i = p.lastIndexOf('\\');
  return i <= 2 ? p.slice(0, 3) : p.slice(0, i);
};
const custom = (name, type, extra = {}) => ({
  id: `custom:${name.toLowerCase().replace(/[^a-z0-9]+/g, '-')}`,
  source: 'custom', type, name, lastPlayed: 0, favorite: false, hidden: false, art: { cover: [], hero: [], logo: [] }, ...extra
});
let skin = new URLSearchParams(location.search).get('skin') || 'default';
const noop = () => {};
// Calls are kept so a preview check can ask what the page asked of the host (scripts/ui-shot.mjs: eval:window.__mockCalls).
const log = (...args) => {
  (window.__mockCalls ??= []).push(args);
  console.log('[mock]', ...args);
};

// Preview switches for the console setup: ?fresh starts like a new Windows (light, no lock, no
// automatic sign-in, nothing paused); ?fail=services|autologon|shell makes that step fail.
// First run: ?terms&fresh starts the setup assistant (with the notice as its second step); ?wizard&fresh is an older install that
// accepted the notice before the assistant existed (it is offered once). Without ?fresh the couch is locked: only the notice shows.
const previewQuery = new URLSearchParams(location.search);
const failSteps = (previewQuery.get('fail') ?? '').split(',');
security.autoLogonState = 'couchhud';
services.pending = false;
if (previewQuery.has('expert')) settings.expertMode = true;
if (previewQuery.has('fresh')) {
  Object.assign(settings, { integrationMode: 'light', gameBarButton: true, guideTapInGame: false, suppressCompetitors: false, runStartupApps: false });
  Object.assign(security, { couchLock: 'off', hasPasscode: false, blockTaskManager: false, autoLogon: false, autoLogonState: 'off', asShell: false });
  isShell = false;
  startup.forEach((e) => (e.mode = 'couch'));
  services.services.forEach((s) => (s.selected = false));
  services.taskInstalled = false;
  services.policyHelper = false;
  security.policies = 'none';
}

// Parental controls: a small stand-in with a fake play time account (used 1:45 h of 3 h).
// In the preview: window.couchhudMockParental({ remainingSeconds: 840, expired: true }) simulates the clock.
const parental = {
  enabled: new URLSearchParams(location.search).has('parental'), weekdayMinutes: 120, weekendMinutes: 180, windowStart: -1, windowEnd: -1,
  perGameMinutes: 0, maxAge: 0, lockedItems: ['steam:271590'], usedMinutes: 45, bonusMinutes: 0, expired: false, remainingOverride: null
};
const parentalState = () => {
  const limit = parental.weekdayMinutes;
  const remaining = parental.remainingOverride ?? (limit ? (limit + parental.bonusMinutes - parental.usedMinutes) * 60 : null);
  const enforced = parental.enabled && security.couchLock !== 'off' && security.asShell;
  return {
    enabled: parental.enabled, enforced, advisory: parental.enabled && !enforced ? (security.couchLock === 'off' ? 'noLock' : 'notShell') : null,
    hasParentCode: security.couchLock !== 'off', weekdayMinutes: parental.weekdayMinutes, weekendMinutes: parental.weekendMinutes,
    windowStart: parental.windowStart, windowEnd: parental.windowEnd, perGameMinutes: parental.perGameMinutes, maxAge: parental.maxAge,
    lockedItems: [...parental.lockedItems], remainingSeconds: parental.enabled ? remaining : null, limiter: parental.enabled && remaining != null ? 'day' : null,
    inWindow: true, usedMinutes: parental.usedMinutes, bonusMinutes: parental.bonusMinutes, clockJumped: false,
    expired: parental.expired, closing: false, closeFailed: false, graceSeconds: 60, blockUnknownAge: parental.blockUnknownAge ?? true, countApps: parental.countApps ?? true, tampered: !!parental.tampered, timeZoneChanged: false, codeProblem: null, game: session?.name ?? null
  };
};
const parentalUnlocked = () => security.couchLock === 'off' || !security.asShell || Date.now() < unlockedUntil;
// The parents' code is a one-time release (own code entry), used up by the action it was given for.
let grantUntil = 0;
const takeGrant = () => {
  if (security.couchLock === 'off') return;
  if (Date.now() >= grantUntil) throw new Error(t('mock.needParentCode'));
  grantUntil = 0;
};
window.couchhudMockParental = (patch = {}) => {
  if ('remainingSeconds' in patch) parental.remainingOverride = patch.remainingSeconds;
  if ('expired' in patch) parental.expired = patch.expired;
  Object.assign(parental, Object.fromEntries(Object.entries(patch).filter(([k]) => k in parental && k !== 'remainingOverride')));
  emit('parental', { kind: patch.kind ?? 'status', minutes: patch.minutes });
};

window.couchhud = {
  getLibrary: async () => library.map((i) => ({ ...i })),
  launch: async (id) => {
    const item = library.find((i) => i.id === id);
    item.lastPlayed = Date.now();
    log('launch', id);
    if (item.type === 'game') {
      setTimeout(() => {
        session = { id, name: item.name, started: Date.now() - 42 * 60000, minutes: 42 };
        emit('session', session);
      }, 800);
    }
    return { ...item };
  },
  getNews: async () => {
    const now = Date.now();
    const item = (i, feed, title, summary, mins) => ({
      id: `n${i}`, feed, title, summary, link: `https://example.com/${i}`, image: null, published: now - mins * 60000,
      paragraphs: [summary, t('mock.newsPara1'), t('mock.newsPara2')]
    });
    return [
      item(1, 'WinFuture', t('mock.news1Title'), t('mock.news1Summary'), 18),
      item(2, 'Caschys Blog', t('mock.news2Title'), t('mock.news2Summary'), 45),
      item(3, 'WinFuture', t('mock.news3Title'), t('mock.news3Summary'), 120),
      item(4, 'Caschys Blog', t('mock.news4Title'), t('mock.news4Summary'), 240),
      item(5, 'WinFuture', t('mock.news5Title'), t('mock.news5Summary'), 300),
      item(6, 'Caschys Blog', t('mock.news6Title'), t('mock.news6Summary'), 600)
    ];
  },
  getFeeds: async () => [
    { name: 'WinFuture', url: 'https://static.winfuture.de/feeds/WinFuture-News-rss2.0.xml' },
    { name: 'Caschys Blog', url: 'https://stadt-bremerhaven.de/feed/' }
  ],
  addFeed: async () => [],
  removeFeed: async () => [],
  openUrl: async (url, source) => {
    log('openUrl', url, source);
    if (!/^https?:\/\//i.test(url)) throw new Error(t('browser.notALink'));
    Object.assign(browser, { open: true, url, title: url });
  },
  getWeather: async () => (settings.weatherPlace ? {
    place: settings.weatherPlace.name,
    temperature: 12.4,
    code: 2,
    isDay: true,
    days: [
      { date: '2026-10-18', min: 6.1, max: 13.8, code: 2 },
      { date: '2026-10-19', min: 7.0, max: 15.2, code: 61 },
      { date: '2026-10-20', min: 4.3, max: 11.0, code: 3 },
      { date: '2026-10-21', min: 3.9, max: 12.5, code: 0 }
    ]
  } : null),
  searchPlaces: async (q) => [
    { name: q, region: t('mock.regionBavaria'), latitude: 48.1, longitude: 11.6 },
    { name: t('mock.placeEast', { name: q }), region: t('mock.regionSaxony'), latitude: 51.0, longitude: 13.7 }
  ],
  getHomeEntities: async () => structuredClone(ha),
  activateHomeEntity: async (id) => {
    const e = ha.find((x) => x.id === id);
    if (['light', 'switch'].includes(e.domain)) e.state = e.state === 'on' ? 'off' : 'on';
    if (e.domain === 'media_player') e.state = e.state === 'playing' ? 'paused' : 'playing';
    log('activateHomeEntity', id);
    return null;
  },
  pinHomeEntity: async (id) => {
    const i = settings.homeAssistantPinned.indexOf(id);
    if (i >= 0) settings.homeAssistantPinned.splice(i, 1);
    else settings.homeAssistantPinned.push(id);
    return i < 0;
  },
  getStoreInfo: async (id) => ({
    description: '',
    trailers: id.startsWith('steam:') && !demoMode ? ['https://video.cloudflare.steamstatic.com/store_trailers/sample/movie_max.mp4'] : [],
    screenshots: []
  }),
  getDetails: async (id) => {
    const item = library.find((i) => i.id === id);
    await new Promise((r) => setTimeout(r, 300));
    return {
      id,
      description: demoMode ? t('mock.demoDesc') : t('mock.detailsDesc', { name: item.name }),
      developers: [demoMode ? 'Lanternlight Games' : t('mock.exampleStudio')],
      publishers: [demoMode ? 'Sofa Arcade' : t('mock.examplePublisher')],
      releaseDate: t('mock.exampleReleaseDate'),
      genres: [t('mock.genreAction'), t('mock.genreRpg')],
      installDir: `D:\\SteamLibrary\\steamapps\\common\\${item.name}`,
      sizeBytes: 52_428_800_000,
      canUninstall: item.source !== 'custom'
    };
  },
  uninstall: async (id) => (log('uninstall', id), null),
  getSaveInfo: async (id) => {
    await sleep(250);
    if (id.startsWith('custom:')) return { supported: false, unavailable: false, warnings: [], reason: t('mock.saveNoLocations'), backups: [], locations: [], registry: [], backupDir: saveSettings.backupDir };
    return {
      supported: true,
      unavailable: false,
      warnings: [],
      gameName: t('mock.sampleGame'),
      locations: ['C:\\Users\\kewe87\\AppData\\Roaming\\Beispielspiel\\saves', 'C:\\Program Files (x86)\\Steam\\userdata\\1234567\\4242\\remote'],
      fileCount: 14,
      totalBytes: 3_400_000,
      lastWrite: Date.now() - 3 * 3600_000,
      registry: id === 'steam:1245620' ? ['HKEY_CURRENT_USER/Software/Beispielspiel'] : [],
      backups: saveBackups.map((b) => ({ ...b })),
      backupDir: saveSettings.backupDir
    };
  },
  backupSaves: async () => {
    await sleep(600);
    const id = `${new Date().toISOString().slice(0, 16).replace('T', '_').replace(':', '-')}.zip`;
    saveBackups.unshift({ id, created: Date.now(), sizeBytes: 1_200_000, fileCount: 14, safety: false, complete: true });
    saveBackups.splice(saveSettings.keep);
    return { id, fileCount: 14, bytes: 3_400_000, errors: [], complete: true };
  },
  restoreSaves: async () => {
    await sleep(600);
    saveBackups.unshift({ id: `${Date.now()}_vorher.zip`, created: Date.now(), sizeBytes: 1_100_000, fileCount: 14, safety: true, complete: true });
    return { restored: 14, errors: [], safetyBackupId: saveBackups[0].id, rolledBack: false };
  },
  deleteSaveBackup: async (id, backupId) => {
    const i = saveBackups.findIndex((b) => b.id === backupId);
    if (i >= 0) saveBackups.splice(i, 1);
    return i >= 0;
  },
  getSaveSettings: async () => ({ ...saveSettings, custom: saveSettings.backupDir !== saveSettings.defaultBackupDir }),
  setSaveSetting: async (key, value) => {
    if (key === 'backupDir') saveSettings.backupDir = value || saveSettings.defaultBackupDir;
    else saveSettings[key] = value;
    return { ...saveSettings, custom: saveSettings.backupDir !== saveSettings.defaultBackupDir };
  },
  getSettings: async () => ({ ...settings, isShell, configPath: 'C:\\Users\\kewe87\\AppData\\Roaming\\CouchHud\\config.json', logPath: 'C:\\Users\\kewe87\\AppData\\Local\\CouchHud\\logs' }),
  setSetting: async (key, value) => {
    if (key === 'steamGridDbApiKey') settings.steamGridDbKeySet = !!value;
    else if (key === 'homeAssistantToken') settings.homeAssistantTokenSet = !!value;
    else if (key === 'weatherLocation') settings.weatherPlace = value;
    else if (key === 'anthropicApiKey') settings.anthropicKeySet = !!value;
    else if (key === 'openAiApiKey') settings.openAiKeySet = !!value;
    else settings[key] = value;
    if (key === 'language') { try { localStorage.setItem('couchhud-preview-language', value); } catch { /* storage unavailable */ } }
    // The host couples these two: only one of them may own the Xbox button.
    if (key === 'guideTapInGame' && value) settings.gameBarButton = false;
    if (key === 'gameBarButton' && value) settings.guideTapInGame = false;
    if (key === 'bluetoothAutoOn') btAutoOn = !!value;
    if (key === 'caseHudInstallApi') setMockInstallApi(value);
    log('setSetting', key, value);
    return null;
  },
  browse: async (path, extensions) => {
    await new Promise((r) => setTimeout(r, 120));
    if (!path) {
      return {
        path: null,
        parent: null,
        entries: [
          { name: 'System (C:)', path: 'C:\\', kind: 'drive', size: 120_000_000_000 },
          { name: t('mock.driveGames'), path: 'D:\\', kind: 'drive', size: 840_000_000_000 },
          { name: 'Desktop', path: 'C:\\Users\\kewe87\\Desktop', kind: 'place', size: 0 },
          { name: 'Downloads', path: 'C:\\Users\\kewe87\\Downloads', kind: 'place', size: 0 }
        ]
      };
    }
    const entries = (fakeFs[path] || [])
      .filter((e) => e.kind === 'folder' || !extensions || extensions.some((x) => e.name.toLowerCase().endsWith(x)))
      .map((e) => ({ name: e.name, path: join(path, e.name), kind: e.kind, size: e.size || 0 }));
    return { path, parent: parentOf(path), entries };
  },
  scanFolder: async (path) => {
    await new Promise((r) => setTimeout(r, 600));
    log('scanFolder', path);
    return [
      { name: 'Hades II', path: 'D:\\Games\\Hades II\\Hades2.exe', size: 98_000_000 },
      { name: 'Hollow Knight Silksong', path: 'D:\\Games\\Hollow Knight Silksong\\Hollow Knight Silksong.exe', size: 650_000 },
      { name: 'RetroArch', path: 'D:\\Games\\RetroArch\\retroarch.exe', size: 18_000_000 }
    ];
  },
  addEntries: async (entries) => {
    for (const e of entries) {
      library.push(custom(e.name, e.type || 'game', { launch: e.url ? { kind: 'url', target: e.url } : { kind: 'exe', target: e.path, args: [], workingDirectory: e.path.replace(/[\\/][^\\/]*$/, '') } }));
    }
    log('addEntries', entries);
    return library.map((i) => ({ ...i }));
  },
  // The Start menu list: Store apps (with "!") and classic programs; some of them are in the library already.
  getInstalledApps: async () => {
    await sleep(500);
    const names = ['7-Zip', 'Audiorekorder', 'Blender', 'Calculator', 'Discord', 'Firefox', 'GIMP', 'HandBrake', 'Kodi', 'Notepad++', 'OBS Studio',
      'Paint', 'Plex', 'Spotify', 'Steam', 'Terminal', 'VLC media player', 'WinRAR', 'Xbox'];
    const store = new Set(['Audiorekorder', 'Calculator', 'Paint', 'Spotify', 'Terminal', 'Xbox']);
    const have = new Set(library.map((i) => i.name.toLowerCase()));
    return names.map((name) => ({
      name,
      id: store.has(name) ? `Mock.${name.replace(/\W/g, '')}_8wekyb3d8bbwe!App` : `C:\\Program Files\\${name}\\${name.replace(/\W/g, '').toLowerCase()}.exe`,
      known: have.has(name.toLowerCase()),
      isPackaged: store.has(name)
    }));
  },
  getAppIcons: async (ids) => {
    const icons = {};
    ids.forEach((id, i) => {
      const hue = (id.length * 37 + i * 53) % 360;
      const letter = (id.replace(/^Mock\./, '').split(/[\\/]/).pop().charAt(0) || '?').toUpperCase();
      const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="64" height="64"><rect width="64" height="64" rx="14" fill="hsl(${hue} 55% 42%)"/><text x="32" y="43" fill="white" font-family="sans-serif" font-size="32" font-weight="700" text-anchor="middle">${letter}</text></svg>`;
      icons[id] = `data:image/svg+xml;utf8,${encodeURIComponent(svg)}`;
    });
    return icons;
  },
  addInstalledApp: async (id, name) => {
    const packaged = id.includes('!');
    library.push(custom(name, 'app', {
      launch: packaged ? { kind: 'url', target: `shell:AppsFolder\\${id}`, args: [] } : { kind: 'exe', target: id, args: [], workingDirectory: id.replace(/[\\/][^\\/]*$/, '') }
    }));
    log('addInstalledApp', id, name);
    return library.map((i) => ({ ...i }));
  },
  editLaunch: async (id, args, cwd) => {
    const item = library.find((i) => i.id === id);
    // The host splits the line like a command line (quotes group); good enough for the preview.
    const list = (args ?? '').match(/"[^"]*"|\S+/g)?.map((a) => a.replace(/^"|"$/g, '')) ?? [];
    item.launch = { ...item.launch, args: list, workingDirectory: cwd || item.launch.target.replace(/[\\/][^\\/]*$/, '') };
    log('editLaunch', id, args, cwd);
    return { ...item };
  },
  setEntryEdge: async (id, edge) => {
    const item = library.find((i) => i.id === id);
    if (item) item.launch = { ...item.launch, embedded: !edge };
    log('setEntryEdge', id, edge);
    return library.map((i) => ({ ...i }));
  },
  editItem: async (id, changes) => {
    const item = library.find((i) => i.id === id);
    if ('name' in changes) item.name = changes.name || item.name;
    if ('type' in changes) item.type = changes.type;
    if ('hidden' in changes) item.hidden = changes.hidden;
    return { ...item };
  },
  getArtworkChoices: async (id) => {
    const item = library.find((i) => i.id === id);
    await sleep(400);
    const steamId = id.startsWith('steam:') ? id.slice(6) : null;
    const variants = (files, source) => files.map((f) => ({ url: `${CDN}/${steamId}/${f}`, thumb: `${CDN}/${steamId}/${f}`, source }));
    // Stand-ins for SteamGridDB hits: generated SVGs in different colors and shapes.
    const fake = (w, h, n) => Array.from({ length: n }, (_, i) => {
      const hue = (i * 47 + item.name.length * 13) % 360;
      const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="${w}" height="${h}"><rect width="100%" height="100%" fill="hsl(${hue} 55% 30%)"/><text x="50%" y="50%" fill="white" font-family="sans-serif" font-size="${h / 8}" text-anchor="middle">${item.name.slice(0, 14)} #${i + 1}</text></svg>`;
      const url = `data:image/svg+xml;utf8,${encodeURIComponent(svg)}`;
      return { url, thumb: url, source: 'SteamGridDB' };
    });
    const hasKey = settings.steamGridDbKeySet;
    const current = (slot) => (item.art?.[slot]?.[0] ? [{ url: item.art[slot][0], thumb: item.art[slot][0], source: t('mock.artCurrent') }] : []);
    const merge = (slot, list) => [...current(slot), ...list.filter((c) => c.url !== item.art?.[slot]?.[0])];
    return {
      cover: merge('cover', [...(steamId ? variants(['library_600x900.jpg', 'header.jpg', 'capsule_616x353.jpg'], 'Steam') : []), ...(hasKey ? fake(600, 900, 9) : [])]),
      hero: merge('hero', [...(steamId ? variants(['library_hero.jpg', 'header.jpg'], 'Steam') : []), ...(hasKey ? fake(1920, 620, 5) : [])]),
      logo: merge('logo', [...(steamId ? variants(['logo.png'], 'Steam') : []), ...(hasKey ? fake(640, 360, 4) : [])]),
      hasKey,
      note: null
    };
  },
  setArtwork: async (id, slot, value) => {
    const item = library.find((i) => i.id === id);
    log('setArtwork', id, slot, value);
    item.autoArt ??= item.art;
    item.art = { ...item.art, [slot]: value ? [value, ...item.art[slot].filter((u) => u !== value)] : item.autoArt?.[slot] ?? item.art[slot] };
    return { ...item };
  },
  getCollections: async () => collections.map((c) => ({ ...c, items: [...c.items] })),
  createCollection: async (name) => {
    if (!name.trim()) throw new Error(t('mock.enterName'));
    const id = `c${collections.length + 1}${Date.now() % 1000}`;
    collections.push({ id, name: name.trim(), items: [], onHome: false });
    return { id, collections: collections.map((c) => ({ ...c, items: [...c.items] })) };
  },
  renameCollection: async (id, name) => {
    collections.find((c) => c.id === id).name = name.trim();
    return collections.map((c) => ({ ...c, items: [...c.items] }));
  },
  deleteCollection: async (id) => {
    collections.splice(collections.findIndex((c) => c.id === id), 1);
    return collections.map((c) => ({ ...c, items: [...c.items] }));
  },
  setCollectionMember: async (id, itemId, member) => {
    const c = collections.find((x) => x.id === id);
    c.items = c.items.filter((i) => i !== itemId);
    if (member) c.items.push(itemId);
    return collections.map((x) => ({ ...x, items: [...x.items] }));
  },
  setCollectionOnHome: async (id, onHome) => {
    collections.find((c) => c.id === id).onHome = onHome;
    return collections.map((c) => ({ ...c, items: [...c.items] }));
  },
  removeEntry: async (id) => {
    const i = library.findIndex((x) => x.id === id);
    if (i >= 0) library.splice(i, 1);
    return null;
  },
  toggleFavorite: async (id) => {
    const item = library.find((i) => i.id === id);
    item.favorite = !item.favorite;
    return item.favorite;
  },
  systemInfo: async () => ({ platform: 'browser', version: 'preview', dev: true, nativeInput: false, isShell, sounds: true, skin, language: settings.language, homeAssistant: true, weather: true, trailerPreviews: true, assistant: !!settings.aiProvider, uiHdr: settings.uiHdr, hdrOn: new URLSearchParams(location.search).has('hdr'), soundVolume: settings.soundVolume, soundSet: settings.soundSet, audioMode: settings.audioMode, musicVolume: settings.musicVolume, integrationMode: settings.integrationMode, acceptedTerms: settings.acceptedTerms, setupWizardDone: settings.setupWizardDone, screensaverMinutes: settings.screensaverMinutes, screensaverStyle: settings.screensaverStyle, displayOffMinutes: settings.displayOffMinutes, pixelShift: settings.pixelShift, reduceMotion: settings.reduceMotion, pointer: settings.pointer, mouseUnlocked: settings.mouseUnlocked }),
  listSkins: async () => [],
  setSkin: async (id) => (skin = id),
  setShell: async (install) => {
    await sleep(400);
    if (install && settings.integrationMode !== 'full') throw new Error(t('mock.fullOnly'));
    if (install && failSteps.includes('shell')) throw new Error(t('mock.accessDenied'));
    return (isShell = install);
  },
  enterPcMode: async () => log('enterPcMode'),
  power: async (action) => log('power', action),
  quit: async () => log('quit'),
  onInput: noop,
  onNav: noop,
  onLibrary: noop,
  onPad: (cb) => listeners.pad.push(cb),
  onBluetoothRecovery: (cb) => listeners.btRecovery.push(cb),
  onSession: (cb) => listeners.session.push(cb),
  onAudio: (cb) => listeners.audio.push(cb),
  onToast: (cb) => listeners.toast.push(cb),
  onUpdates: (cb) => listeners.updates.push(cb),
  onCatalogUpdates: (cb) => listeners.catalogUpdates.push(cb),
  onSelfUpdate: (cb) => listeners.selfUpdate.push(cb),
  ...caseHudMock(),
  onAssistant: (cb) => listeners.assistant.push(cb),
  onRemote: (cb) => listeners.remote.push(cb),
  onFocus: noop,
  onLaunchTimeout: noop,
  onUnlocked: (cb) => listeners.unlocked.push(cb),
  onParental: (cb) => listeners.parental.push(cb),
  onParentalAuth: noop,
  onAdminApproval: noop,
  onBrowser: (cb) => listeners.browser.push(cb),
  onBrowserUi: (cb) => listeners.browserui.push(cb),
  onNotification: (cb) => listeners.notification.push(cb),
  getDisplay: async () => structuredClone(display),
  getProfile: async (id) => ({ profile: structuredClone(profiles[id] ?? {}), modes: structuredClone(display.modes), hdrSupported: true, devices: structuredClone(audio.devices) }),
  setProfile: async (id, p) => ((profiles[id] = p), { profile: structuredClone(p), modes: structuredClone(display.modes), hdrSupported: true, devices: structuredClone(audio.devices) }),
  capture: async (kind) => log('capture', kind),
  getDownloads: async () => {
    dl = Math.min(1, dl + 0.07);
    return [
      { appId: '1091500', name: 'Cyberpunk 2077', state: 'downloading', progress: dl, bytesDone: Math.round(dl * 18_400_000_000), bytesTotal: 18_400_000_000, isUpdate: true },
      { appId: '1086940', name: "Baldur's Gate 3", state: 'queued', progress: 0, bytesDone: 0, bytesTotal: 2_100_000_000, isUpdate: true },
      { appId: '2358720', name: 'Black Myth: Wukong', state: 'paused', progress: 0.42, bytesDone: 54_000_000_000, bytesTotal: 128_000_000_000, isUpdate: false }
    ];
  },
  setDisplayMode: async (width, height, hz) => ((display.current = { width, height, hz }), structuredClone(display)),
  setHdr: async (on) => ((display.hdrOn = on), structuredClone(display)),
  getPerformance: async () => ({ cpu: 20 + Math.round(Math.random() * 25), gpu: 70 + Math.round(Math.random() * 25), ramUsedGb: 14.2, ramTotalGb: 31.9 }),
  getCaptures: async () => [
    { url: fakeShot('Elden Ring', 290), name: 'Elden Ring 2026-10-17 21-14-03', source: 'Game Bar', video: false, time: Date.now() - 3_600_000, size: 4_200_000 },
    { url: fakeShot('Cyberpunk 2077', 330), name: '20261016_1', source: 'Steam', video: false, time: Date.now() - 86_400_000, size: 2_900_000 },
    { url: fakeShot('Forza Horizon 5', 200), name: 'Forza Horizon 5 2026-10-14', source: 'Game Bar', video: false, time: Date.now() - 3 * 86_400_000, size: 3_100_000 },
    { url: fakeShot('Baldur\'s Gate 3', 30), name: 'bg3_0042', source: 'Windows', video: false, time: Date.now() - 6 * 86_400_000, size: 5_000_000 },
    { url: fakeShot('Hollow Knight', 250), name: 'hk_boss', source: 'Steam', video: false, time: Date.now() - 9 * 86_400_000, size: 1_100_000 }
  ],
  getPlugins: async () => structuredClone(plugins),
  approvePlugin: async (folder) => ((plugins.find((p) => p.folder === folder).needsApproval = false), structuredClone(plugins)),
  setPluginEnabled: async (folder, enabled) => ((plugins.find((p) => p.folder === folder).enabled = enabled), structuredClone(plugins)),
  setPluginSetting: async (id, key, value) => ((plugins.find((p) => p.id === id).settings.find((s) => s.key === key).value = value), structuredClone(plugins)),
  getPluginPage: async () => ({ title: 'Wand', description: t('mock.wandPageDesc'), rows: wandRows() }),
  activatePluginRow: async (id, page, row) => ({ message: row === 'open' ? t('mock.wandOpening') : null, rows: wandRows() }),
  getQuickActions: async () => [{ plugin: 'wand', id: 'open', label: t('mock.wandQuickLabel'), icon: '🪄', detail: session ? t('mock.wandQuickFor', { name: session.name }) : t('mock.wandQuickOpens') }],
  runQuickAction: async () => t('mock.wandOpening'),
  installPackage: async (path) => path.split('\\').pop().replace('.zip', ''),
  getCatalog: async () => (await new Promise((r) => setTimeout(r, 600)), catalogState()),
  getCatalogUpdates: async () => catalogUpdates(),
  installCatalogEntry: async (id, kind) => {
    await new Promise((r) => setTimeout(r, 1200));
    const c = catalog.find((x) => x.id === id && x.kind === kind);
    if (!c || c.status === 'unavailable') throw new Error(c?.reason ?? t('mock.notInCatalog'));
    Object.assign(c, { status: 'installed', installedVersion: c.version, restartPending: c.kind === 'plugin' });
    emit('catalogUpdates', catalogUpdates());
    return {
      message: c.kind === 'skin' ? t('mock.skinInstalled', { name: c.name, version: c.version }) : t('mock.pluginInstalled', { name: c.name, version: c.version }),
      restartNeeded: c.kind === 'plugin',
      state: catalogState()
    };
  },
  getGameFolders: async () => [...gameFolders],
  addGameFolder: async (path) => (gameFolders.includes(path) || gameFolders.push(path), [...gameFolders]),
  removeGameFolder: async (path) => (gameFolders.splice(gameFolders.indexOf(path), 1), [...gameFolders]),
  discoverGameFolders: async () => ['D:\\Spiele', 'E:\\GOG Games'].filter((f) => !gameFolders.includes(f)),
  getNotifications: async () => ({ items: structuredClone(notes), unread: notes.filter((n) => !n.read).length, windows: 'allowed' }),
  markNotificationsRead: async () => notes.forEach((n) => (n.read = true)),
  clearNotifications: async () => notes.splice(0),
  getBluetooth: async () => ({ available: true, on: btOn, autoOn: btAutoOn && !btDeclined, controllersPaired: 1, controllerConnected: btOn && !noPad, pads: noPad ? 0 : 1, countdown: false }),
  setBluetooth: async (on) => {
    btOn = on;
    // Like the host: a Bluetooth controller and nothing else, so the radio comes back after a countdown.
    if (!on && btAutoOn) setTimeout(() => listeners.btRecovery.forEach((cb) => cb({ phase: 'countdown', seconds: 10 })), 800);
    return { available: true, on, autoOn: btAutoOn, controllersPaired: 1, controllerConnected: on, pads: on ? 1 : 0, countdown: false };
  },
  answerBluetoothRecovery: async (keepOff) => {
    if (!keepOff) btOn = true;
    else btDeclined = true;
    listeners.btRecovery.forEach((cb) => cb({ phase: keepOff ? 'idle' : 'enabled' }));
    // Like the host: true only when it was really left off.
    return !!keepOff;
  },
  scanBluetooth: async () => {
    await new Promise((r) => setTimeout(r, 1500));
    return [
      { id: 'bt1', name: 'Xbox Wireless Controller', paired: true, connected: true, lowEnergy: true, kind: 'controller' },
      { id: 'bt2', name: 'Sony WH-1000XM5', paired: true, connected: false, lowEnergy: false, kind: 'headphones' },
      { id: 'bt3', name: 'DualSense Wireless Controller', paired: false, connected: false, lowEnergy: false, kind: 'controller' },
      { id: 'bt4', name: 'JBL Flip 6', paired: false, connected: false, lowEnergy: false, kind: 'speaker' }
    ];
  },
  pairBluetooth: async () => (await new Promise((r) => setTimeout(r, 900)), t('mock.btPaired', { name: 'DualSense Wireless Controller' })),
  unpairBluetooth: async () => t('mock.btRemoved'),
  getWifi: async () => ({ available: wifi.available, access: 'allowed', ssid: wifi.ssid, ip: wifi.ssid ? '192.168.178.42' : null, message: null }),
  scanWifi: async () => {
    await new Promise((r) => setTimeout(r, 1200));
    return wifi.networks.map((n) => ({ ...n, connected: n.ssid === wifi.ssid }))
      .sort((a, b) => b.connected - a.connected || b.signal - a.signal);
  },
  connectWifi: async (ssid, password) => {
    await new Promise((r) => setTimeout(r, 1200));
    const n = wifi.networks.find((x) => x.ssid === ssid);
    if (n && !n.supported) throw new Error(t('mock.wifiEnterprise'));
    if (n?.secured && password && password.length < 8) throw new Error(t('mock.wifiShort'));
    if (n?.secured && !password && !wifi.known.includes(ssid)) return { connected: false, needsPassword: true, message: t('mock.wifiUnknown') };
    if (n?.secured && password === 'falsch123') return { connected: false, needsPassword: false, message: t('mock.wifiWrong') };
    wifi.ssid = ssid;
    if (!wifi.known.includes(ssid)) wifi.known.push(ssid);
    return { connected: true, needsPassword: false, message: t('mock.wifiConnected', { ssid }) };
  },
  disconnectWifi: async () => ((wifi.ssid = null), t('mock.wifiDisconnected')),
  getAppVolumes: async () => structuredClone(appVolumes),
  setAppVolume: async (id, v) => {
    const a = appVolumes.find((x) => x.id === id);
    if (a) (a.volume = Math.max(0, Math.min(100, v)), v > 0 && (a.muted = false));
    return structuredClone(appVolumes);
  },
  setAppMute: async (id, m) => {
    const a = appVolumes.find((x) => x.id === id);
    if (a) a.muted = m;
    return structuredClone(appVolumes);
  },
  keyboardState: async () => null,
  getRemote: async (newCode) => ({
    enabled: settings.remoteEnabled,
    running: settings.remoteEnabled,
    address: 'http://192.168.178.42:8766',
    url: 'http://192.168.178.42:8766/#pair=K7MX42QP',
    code: newCode ? 'P3Q9TZ4R' : 'K7MX42QP',
    qr: settings.remoteEnabled ? fakeQr() : null,
    devices: [{ name: 'Pixel 9', paired: Date.now() - 86_400_000 }]
  }),
  revokeRemote: async () => null,
  revokeRemoteDevice: async () => null,
  allowFirewall: async () => null,
  onSkin: noop,
  onHdr: noop,
  onSettings: noop,
  assistantInfo: async () => ({ configured: !!settings.aiProvider, provider: settings.aiProvider, model: settings.aiModel || settings.aiModels[settings.aiProvider]?.[0] }),
  resetAssistant: async () => null,
  confirmAssistant: async (id, ok) => pendingConfirm?.(ok),
  askAssistant: async (text) => {
    const wait = (ms) => new Promise((r) => setTimeout(r, ms));
    const tool = (name) => emit('assistant', { type: 'tool', name });
    await wait(500);
    tool('search_library');
    await wait(700);
    if (/start|spiel|launch|play/i.test(text) && !/was soll|what should/i.test(text)) {
      const ok = await new Promise((resolve) => {
        pendingConfirm = resolve;
        emit('assistant', { type: 'confirm', id: 'c1', text: t('mock.assistantConfirm') });
      });
      return ok ? t('mock.assistantLaunching') : t('mock.assistantCancelled');
    }
    if (/wetter|weather/i.test(text)) {
      tool('get_weather');
      await wait(500);
      return t('mock.assistantWeather');
    }
    return t('mock.assistantSuggest');
  },
  getUpdates: async () => structuredClone(updates),
  checkUpdates: async () => {
    setUpdates({ busy: t('mock.updChecking') });
    await new Promise((r) => setTimeout(r, 1200));
    setUpdates({ busy: null, checkedAt: Date.now() });
    return structuredClone(updates);
  },
  getSelfUpdate: async () => structuredClone(selfUpdate),
  checkSelfUpdate: async () => {
    setSelfUpdate({ busy: t('mock.selfChecking') });
    await new Promise((r) => setTimeout(r, 900));
    setSelfUpdate({ busy: null, checkedAt: Date.now() });
    return structuredClone(selfUpdate);
  },
  startSelfUpdate: async () => {
    setSelfUpdate({ busy: t('mock.selfDownloading', { version: '0.4.0' }) });
    setTimeout(() => {
      setSelfUpdate({ busy: null });
      emit('toast', t('mock.previewRestart'));
    }, 1800);
  },
  startUpdate: async (kind, id) => {
    setUpdates({ busy: kind === 'windows' ? t('mock.updWindowsBusy') : t('mock.updBusy') });
    setTimeout(() => {
      if (kind === 'windows') setUpdates({ windows: [], rebootRequired: true, busy: null });
      else setUpdates({ apps: id ? updates.apps.filter((a) => a.id !== id) : [], busy: null });
      emit('toast', t('mock.updDone'));
    }, 1500);
  },
  getSession: async () => session,
  getStartup: async () => structuredClone(startup),
  getLaunchers: async () => structuredClone(launchers),
  displayOff: async () => log('displayOff'),
  displayOn: async () => log('displayOn'),
  setIntegrationMode: async (mode) => {
    await sleep(400);
    const undone = [];
    if (mode === 'light' && settings.integrationMode === 'full') {
      if (isShell) undone.push(t('mock.undoShell'));
      if (security.couchLock !== 'off') undone.push(t('mock.undoLock'));
      if (security.autoLogon) undone.push(t('mock.undoAutoLogon'));
    }
    if (mode === 'light') {
      security.couchLock = 'off';
      isShell = false;
    }
    settings.integrationMode = mode;
    return { mode, undone };
  },
  getBrowser: async () => structuredClone(browser),
  openBrowser: async (text) => {
    log('openBrowser', text);
    if (text) Object.assign(browser, { open: true, url: text.includes('.') && !text.includes(' ') ? `https://${text.replace(/^https?:\/\//, '')}` : `https://duckduckgo.com/?q=${encodeURIComponent(text)}`, title: text, canGoBack: true });
    emit('browser', structuredClone(browser));
    emit('browserui', 'show');
  },
  // The native page does not exist in the preview: the placeholder shows where it would be.
  browserLayout: async (x, y, w, h, visible) => log('browserLayout', visible ? [x, y, w, h].map(Math.round).join(',') : 'hidden'),
  browserFocus: async (page) => log('browserFocus', page),
  browserCommand: async (command) => {
    log('browserCommand', command);
    if (command === 'close') {
      Object.assign(browser, { open: false, url: '', title: '' });
      emit('browser', structuredClone(browser));
      emit('browserui', 'closed');
    }
  },
  setBrowserZoom: async (zoom) => ((browser.zoom = zoom), structuredClone(browser)),
  addBookmark: async (name, url) => (browser.bookmarks.push({ name, url }), structuredClone(browser)),
  removeBookmark: async (url) => ((browser.bookmarks = browser.bookmarks.filter((b) => b.url !== url)), structuredClone(browser)),
  getParental: async () => parentalState(),
  setParental: async (key, value) => {
    takeGrant();
    if (key === 'enabled' && value && security.couchLock === 'off') throw new Error(t('mock.parentalNeedsLock'));
    if (key === 'window') [parental.windowStart, parental.windowEnd] = value;
    else parental[key === 'enabled' ? 'enabled' : key] = value;
    return parentalState();
  },
  setParentalItemLock: async (id, locked) => {
    takeGrant();
    parental.lockedItems = parental.lockedItems.filter((x) => x !== id);
    if (locked) parental.lockedItems.push(id);
    return parentalState();
  },
  parentalCheck: async (id) => {
    const s = parentalState();
    if (!s.enabled) return { ok: true };
    const needsCode = parental.lockedItems.includes(id) && Date.now() >= grantUntil;
    if (parental.expired) return s.enforced ? { ok: false, reason: 'day', message: t('mock.parentalDayOver'), needsCode: false } : { ok: true, advisory: true, message: t('mock.parentalAdvisoryDayOver') };
    if (needsCode) return s.enforced ? { ok: false, reason: null, message: t('mock.parentalCodeOnly'), needsCode: true } : { ok: true, advisory: true, message: t('mock.parentalAdvisoryCodeOnly') };
    return { ok: true };
  },
  parentalExtend: async (minutes) => {
    takeGrant();
    parental.bonusMinutes += minutes;
    parental.expired = false;
    parental.remainingOverride = minutes * 60;
    emit('parental', { kind: 'status' });
    return parentalState();
  },
  parentalAuth: async (code) => {
    const ok = code?.join() === (passcode ?? ['a', 'a', 'b', 'b', 'x', 'y']).join();
    if (ok) grantUntil = Date.now() + 90_000;
    return ok ? { ok } : { ok, waitSeconds: 0 };
  },
  confirmWindowsUnlock: async () => ({ ok: true }),
  parentalReset: async () => (takeGrant(), parentalState()),
  parentalAcceptClock: async () => (takeGrant(), parentalState()),
  parentalAdoptZone: async () => (takeGrant(), parentalState()),
  parentalCloseGame: async () => {
    session = null;
    setTimeout(() => emit('session', null), 300);
    return null;
  },
  getSecurity: async () => ({ ...security, unlocked: security.couchLock === 'off' || !security.asShell || Date.now() < unlockedUntil }),
  getServiceStatus: async () => security.service,
  repairService: async () => ((security.service = { ...security.service, state: 'running', label: 'running', healthy: true, alarm: false, problem: false, repair: false }), security.service),
  unlock: async (code, _asNewCode = false) => {
    if (security.couchLock === 'windows') {
      setTimeout(() => { unlockedUntil = Date.now() + 120_000; listeners.unlocked.forEach((cb) => cb()); }, 1500);
      return { ok: false, pending: true };
    }
    // Preview code: A A B B X Y
    const ok = code?.join() === (passcode ?? ['a', 'a', 'b', 'b', 'x', 'y']).join();
    if (ok) unlockedUntil = Date.now() + 120_000;
    return ok ? { ok } : { ok, waitSeconds: 0 };
  },
  windowsSignIn: async () => {
    if (security.couchLock !== 'windows') return { ok: false, pending: false };
    setTimeout(() => { unlockedUntil = Date.now() + 120_000; listeners.unlocked.forEach((cb) => cb()); }, 1500);
    return { ok: false, pending: true };
  },
  setPasscode: async (code) => ((passcode = code), (security.hasPasscode = true), window.couchhud.getSecurity()),
  // Like the host with the service: the Windows lock or no lock drops the couch code.
  // The code lock without a code that survives the switch is refused, like the host (the service drops a code set at the open couch).
  setCouchLock: async (mode, code = null) => {
    if (mode === 'code' && !code && security.couchLock !== 'code') throw new Error(t('mock.setCodeFirst'));
    security.couchLock = mode;
    if (mode !== 'code') (passcode = null), (security.hasPasscode = false);
    if (code) (passcode = code), (security.hasPasscode = true);
    return window.couchhud.getSecurity();
  },
  setParentCode: async (code) => ((passcode = code), (security.hasPasscode = true), window.couchhud.getSecurity()),
  readClipboard: async () => ({ ok: false, reason: 'clipboard' }),
  holdUnlockForSetup: async () => null,
  setBlockTaskManager: async (on) => ((security.blockTaskManager = on), window.couchhud.getSecurity()),
  openAutoLogonDialog: async () => log('openAutoLogonDialog'),
  enableAutoLogon: async (password) => {
    await sleep(500);
    if (failSteps.includes('autologon')) throw new Error(t('mock.winPromptDenied'));
    if (password !== 'test') return { ok: false, error: t('mock.passwordWrong') };
    Object.assign(security, { autoLogon: true, autoLogonUser: security.user, autoLogonState: 'couchhud', autoLogonOtherUser: false });
    return { ok: true, security: structuredClone(security) };
  },
  setAutoLogon: async (mode) => {
    await sleep(400);
    if (mode === 'off') Object.assign(security, { autoLogon: false, autoLogonState: 'off' });
    if (mode === 'allow') security.passwordlessOnly = false;
    return window.couchhud.getSecurity();
  },
  setLauncherMode: async (id, mode) => ((launchers.find((l) => l.id === id).mode = mode), structuredClone(launchers)),
  openLauncher: async (id) => log('openLauncher', id),
  setStartupMode: async (id, mode) => ((startup.find((e) => e.id === id).mode = mode), structuredClone(startup)),
  applyStartupSuggestions: async () => (await sleep(300), startup.forEach((e) => (e.mode = e.suggested)), structuredClone(startup)),
  getServices: async () => structuredClone(services),
  toggleCouchService: async (name) => { const s = services.services.find((x) => x.name === name); s.selected = !s.selected; services.pending = true; return s.selected; },
  setupServiceTask: async (policies = true) => {
    await sleep(600);
    if (failSteps.includes('services')) throw new Error(t('mock.cancelledByUser'));
    services.policyHelper = policies;
    if (!policies) services.policiesSet = false;
    security.policies = policies ? 'helper' : 'none';
    services.taskInstalled = services.services.some((s) => s.selected) && policies;
    services.pending = false;
    return structuredClone(services);
  },
  releaseLockPolicies: async () => {
    await sleep(400);
    services.policiesSet = false;
    return { released: true, remaining: 0 };
  },
  cleanupStuckPolicies: async () => {
    await sleep(600);
    services.stuckPolicies = false;
    services.policiesSet = false;
    return structuredClone(services);
  },
  getProcesses: async () => [
    { name: 'eldenring', title: 'ELDEN RING™', count: 1, memoryBytes: 7_400_000_000, hasWindow: true, isGame: true },
    { name: 'Discord', title: 'Discord', count: 6, memoryBytes: 820_000_000, hasWindow: true, isGame: false },
    { name: 'steamwebhelper', title: 'Steam', count: 9, memoryBytes: 640_000_000, hasWindow: true, isGame: false },
    { name: 'SignalRgb', title: '', count: 2, memoryBytes: 310_000_000, hasWindow: false, isGame: false },
    { name: 'OneDrive', title: '', count: 1, memoryBytes: 95_000_000, hasWindow: false, isGame: false }
  ],
  killProcess: async (name, force) => (log('kill', name, force), 0),
  switchProcess: async (name) => log('switch', name),
  resumeGame: async () => log('resumeGame'),
  quitGame: async () => {
    session = null;
    setTimeout(() => emit('session', null), 300);
    return null;
  },
  getAudio: async () => structuredClone(audio),
  setVolume: async (v) => ((audio.volume = Math.max(0, Math.min(100, v))), v > 0 && (audio.muted = false), structuredClone(audio)),
  setMute: async (m) => ((audio.muted = m), structuredClone(audio)),
  setAudioOutput: async (id) => {
    audio.devices.forEach((d) => (d.isDefault = d.id === id));
    return structuredClone(audio);
  },
  getPads: async () => (noPad ? [] : [{ index: 0, battery: 'medium' }]),
  rumble: async () => null
};

// Preview helper: what the phone page would send (try it in the console).
window.couchhudMockPhone = (text, submit = false) => emit('remote', { type: 'text', text, submit });
