// Smart home tab: the Home Assistant entities chosen in the picker, as cards, in the chosen order; switch lights,
// run scenes, read sensors. Pinned entities also show up in the guide menu. Nothing chosen yet: a way into the picker.

import { api } from '../core/api.js';
import { el, keepInView, setFocused } from '../core/dom.js';
import { setBackground } from '../core/art.js';
import { attempt, toast } from '../core/feedback.js';
import { move, push, render } from '../core/nav.js';
import { state } from '../core/state.js';
import { addQuickRows, quick, renderQuick } from '../components/quick-menu.js';
import { sound } from '../sound.js';
import { t } from '../core/i18n.js';
import { ensureUnlocked } from '../components/passcode.js';
import { smartHomePicker } from './smarthome-picker.js';

const COLUMNS = 4;
const POLL_MS = 5000;

const ICONS = {
  scene: '🎬', script: '📜', light: '💡', switch: '🔌', fan: '🌀', input_boolean: '🔘', cover: '🪟', media_player: '🎵',
  climate: '🌡️', lock: '🔒', automation: '🤖', button: '⏺', input_button: '⏺', weather: '🌤️'
};
const SENSOR_ICONS = { temperature: '🌡️', humidity: '💧', power: '⚡', energy: '⚡', battery: '🔋', door: '🚪', window: '🪟', motion: '🏃', illuminance: '☀️' };

// i18n-used: smartHome.state.on smartHome.state.off smartHome.state.playing smartHome.state.paused smartHome.state.idle smartHome.state.open smartHome.state.closed smartHome.state.heat smartHome.state.cool smartHome.state.locked smartHome.state.unlocked smartHome.state.home smartHome.state.not_home smartHome.state.unknown
const KNOWN_STATES = ['on', 'off', 'playing', 'paused', 'idle', 'open', 'closed', 'heat', 'cool', 'locked', 'unlocked', 'home', 'not_home', 'unknown'];

export const iconFor = (e) => (e.domain === 'sensor' || e.domain === 'binary_sensor' ? SENSOR_ICONS[e.deviceClass] ?? '📟' : ICONS[e.domain] ?? '•');

export function stateLabel(e) {
  if (e.domain === 'sensor') return `${e.state}${e.unit ? ` ${e.unit}` : ''}`;
  if (e.domain === 'scene' || e.domain === 'script' || e.domain === 'button') return t('smartHome.run');
  if (e.state === 'scening') return '';
  if (!KNOWN_STATES.includes(e.state)) return e.state;
  const key = `smartHome.state.${e.state}`;
  return t(key);
}

const isOn = (e) => ['on', 'playing', 'open', 'unlocked', 'heat', 'cool'].includes(e.state);

// What the host lets the HUD see: the chosen entities (selected) in order, then pinned ones that are not chosen.
let entities = [];
let loaded = false;
let pinned = new Set();
let pollTimer;
/** A load is running: the poll starts no second one (a slow Home Assistant must not pile up requests). */
let loading = 0;

/** The cards of the tab: only what is chosen, in the chosen order. */
const shown = () => entities.filter((e) => e.selected !== false);

async function load() {
  loading++;
  let list;
  try {
    list = await attempt(() => api.getHomeEntities(), t('smartHome.unreachable'));
  } finally {
    loading--;
  }
  if (list) {
    entities = list;
    loaded = true;
  }
  return !!list;
}

/** The picker; the choice belongs to the owner, so a locked couch asks for the code first. */
export async function openSmartHomePicker() {
  if (!(await ensureUnlocked(t('smartHome.pick.title')))) return;
  push(smartHomePicker({
    onDone: async (selection) => {
      // Pins of entities that are no longer chosen are gone on the host too.
      pinned = new Set([...pinned].filter((id) => selection.includes(id)));
      await load();
      if (state.stack.length === 0 && smartHomeView.active) render();
    }
  }));
}

async function activate(entity) {
  sound.select();
  if ((await attempt(() => api.activateHomeEntity(entity.id), t('smartHome.switchFailed'))) === undefined) return;
  if (entity.domain === 'scene' || entity.domain === 'script') toast(t('smartHome.started', { name: entity.name }));
  // Home Assistant needs a moment until the new state shows up.
  setTimeout(async () => {
    await load();
    if (quick.open) renderQuick();
    else if (state.stack.length === 0 && smartHomeView.active) smartHomeView.refreshCards();
  }, 350);
}

// Pinned entities in the guide menu.
addQuickRows(() => entities
  .filter((e) => pinned.has(e.id))
  .map((e) => ({
    icon: iconFor(e),
    label: e.name,
    detail: e.controllable ? stateLabel(e) : stateLabel(e),
    keepOpen: true,
    run: () => (e.controllable ? activate(e) : null)
  })));

/** Loads pinned entities for the guide menu without opening the tab. */
export async function preloadSmartHome(settings) {
  pinned = new Set(settings?.homeAssistantPinned ?? []);
  if (state.info.homeAssistant) await load();
}

export const smartHomeView = {
  get name() { return t('smartHome.title'); },
  id: 'smarthome',
  index: 0,
  active: false,
  onEnter() {
    this.index = 0;
  },
  build(root) {
    this.active = true;
    clearInterval(pollTimer);
    pollTimer = setInterval(async () => {
      if (!this.root?.isConnected) {
        this.active = false;
        return clearInterval(pollTimer);
      }
      if (loading) return;
      if (await load()) this.refreshCards();
    }, POLL_MS);

    this.root = el('div', { className: 'smarthome' });
    root.append(this.root);
    setBackground({ id: '__smarthome', name: t('smartHome.title'), art: {} });
    if (!loaded) {
      this.root.append(el('p', { className: 'empty-note', textContent: t('smartHome.connecting') }));
      load().then((ok) => ok && state.stack.length === 0 && this.active && render());
      return;
    }
    const list = shown();
    if (!list.length) {
      // Nothing chosen yet (new, or an install from before the picker): one button into the picker.
      this.cards = [el('div', { className: 'ha-card ha-choose' },
        el('span', { className: 'ha-icon', textContent: '＋' }),
        el('strong', { className: 'ha-name', textContent: t('smartHome.choose') }),
        el('span', { className: 'ha-state', textContent: t('smartHome.chooseShort') }))];
      this.index = 0;
      this.scroller = el('div', { className: 'ha-grid list-scroll' }, this.cards);
      this.root.append(el('div', { className: 'grid-title' }, el('h2', { textContent: t('smartHome.title') })),
        el('div', { className: 'ha-empty' },
          el('p', { className: 'ha-empty-title', textContent: t('smartHome.empty.title') }),
          el('p', { className: 'empty-note', textContent: t('smartHome.empty.text') })),
        this.scroller);
      this.update({ instant: true });
      return;
    }
    this.cards = list.map((e) => el('div', { className: `ha-card ha-${e.domain}${isOn(e) ? ' on' : ''}` },
      el('span', { className: 'ha-icon', textContent: iconFor(e) }),
      el('strong', { className: 'ha-name', textContent: e.name }),
      el('span', { className: 'ha-state', textContent: stateLabel(e) }),
      pinned.has(e.id) ? el('span', { className: 'fav', textContent: '★' }) : null));
    this.index = Math.min(this.index, this.cards.length - 1);
    this.scroller = el('div', { className: 'ha-grid list-scroll' }, this.cards);
    this.root.append(el('div', { className: 'grid-title' }, el('h2', { textContent: t('smartHome.title') }), el('span', { className: 'meta', textContent: t('smartHome.count', { count: list.length }) })), this.scroller);
    this.update({ instant: true });
  },
  /** Live updates without rebuilding (keeps scroll position and animations). */
  refreshCards() {
    const list = shown();
    if (!list.length) return this.cards?.[0]?.classList.contains('ha-choose') ? undefined : render();
    if (!this.cards || this.cards.length !== list.length || this.cards[0].classList.contains('ha-choose')) return render();
    list.forEach((e, i) => {
      const card = this.cards[i];
      card.classList.toggle('on', isOn(e));
      card.querySelector('.ha-state').textContent = stateLabel(e);
    });
  },
  update({ instant = false } = {}) {
    if (!this.cards) return;
    setFocused(this.cards, this.index);
    keepInView(this.scroller, this.cards[this.index], { instant });
  },
  activate() {
    const list = shown();
    if (!list.length) return openSmartHomePicker();
    const e = list[this.index];
    if (!e) return;
    if (!e.controllable) return sound.bump();
    return activate(e);
  },
  async handle(action) {
    const list = shown();
    const count = Math.max(list.length, 1);
    switch (action) {
      case 'left': return move(this, -1, count);
      case 'right': return move(this, 1, count);
      case 'up': return move(this, -COLUMNS, count, true);
      case 'down': return move(this, COLUMNS, count, true);
      case 'a': return this.activate();
      case 'x': return openSmartHomePicker();
      case 'y': {
        const e = list[this.index];
        if (!e) return sound.bump();
        const on = await attempt(() => api.pinHomeEntity(e.id));
        if (on === undefined) return;
        if (on) pinned.add(e.id);
        else pinned.delete(e.id);
        toast(on ? t('smartHome.pinned', { name: e.name }) : t('smartHome.unpinned', { name: e.name }));
        return render();
      }
    }
  },
  hints() {
    if (loaded && !shown().length) return [['A', t('smartHome.hint.choose')], ['B', t('smartHome.hint.home')]];
    return [['A', t('smartHome.hint.switch')], ['X', t('smartHome.hint.choose')], ['Y', t('smartHome.hint.pin')], ['B', t('smartHome.hint.home')]];
  }
};
