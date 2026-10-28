// TV control (LG webOS first): switch the TV on and off, volume and mute, choose the input. Settings › TV pairs the TV (search in the
// network or, in expert mode, its address); the Guide menu has the everyday rows. The host side is src/CouchHud.Shell/Services/TvManager.cs.
// Locked couch: state, volume, mute and on/off work; pairing, removing the TV and switching the input need the unlock (with parental
// controls the input needs the parents' code). Names of inputs come from the TV: only ever shown as text.

import { api } from '../core/api.js';
import { el, keepInView, setFocused } from '../core/dom.js';
import { attempt, toast } from '../core/feedback.js';
import { currentView, move, render } from '../core/nav.js';
import { state } from '../core/state.js';
import { chooseDialog, confirmDialog } from '../components/overlay.js';
import { openKeyboard } from '../components/keyboard.js';
import { addQuickRows, quick, renderQuick } from '../components/quick-menu.js';
import { ensureUnlocked } from '../components/passcode.js';
import { ensureParentGrant, parental, refreshParental } from '../components/parental.js';
import { sound } from '../sound.js';
import { t } from '../core/i18n.js';
import { admitInputSwitch, pairOutcome } from '../core/tv-rules.js';

/** Last answer of the host: { provider, providerName, paired, address, name, model, secure, macKnown, mac, power, volume, muted, input, pairing, error }. */
state.tv = null;
/** The TV's inputs ({ id, label, connected }), read when they are needed (unlocked). */
let inputs = null;
let inGuide = true;
let page = null;
let lastRefresh = 0;
/** Volume steps pressed while one is on its way (sent as one absolute value afterwards). */
let pendingSteps = 0;
let stepping = false;

export const setTvInGuide = (on) => { inGuide = !!on; };
export const tvPaired = (s = state.tv) => !!s?.paired;

const tvName = (s = state.tv) => s?.name || s?.model || s?.providerName || t('tv.title');

/** One line for the state: "On · 23 %", "Off", "Switching on …", "Confirm on the TV". */
export function tvLine(s = state.tv) {
  if (!s) return '';
  if (s.pairing === 'prompt') return t('tv.pairing.prompt');
  if (s.pairing === 'connecting') return t('tv.pairing.connecting');
  if (!s.paired) return t('tv.notPaired');
  // i18n-used: tv.power.on tv.power.off tv.power.waking tv.power.unknown
  const powerKey = 'tv.power.' + String(s.power).replace(/\W/g, '');
  const power = t(powerKey);
  if (s.power !== 'on' || s.volume == null) return power;
  return t('tv.stateVolume', { state: power, volume: s.muted ? t('tv.muted') : t('tv.percent', { volume: s.volume }) });
}

/** The hint for a lasting problem (a code from the host), or ''. */
export function tvHint(s = state.tv) {
  if (!s?.paired) return '';
  if (s.error === 'certChanged') return t('tv.hint.certChanged');
  if (s.error === 'unpaired') return t('tv.hint.unpaired');
  if (!s.macKnown) return t('tv.hint.noMac');
  return '';
}

function inputLabel(id) {
  if (!id) return '';
  return inputs?.find((i) => i.id === id)?.label ?? id.replace(/_/g, ' ');
}

/** Called once at the start: keeps `state.tv` current for the page and the Guide menu. */
export async function startTv() {
  if (!api.tvStatus) return;
  api.onTv?.((s) => {
    state.tv = s;
    if (page && currentView() === page) render();
    if (quick.open) renderQuick();
  });
  const settings = await attempt(() => api.getSettings());
  if (settings) inGuide = settings.tvInGuide !== false;
  state.tv = (await attempt(() => api.tvStatus(false))) ?? state.tv;
}

/** A read of the TV (volume, input); the host skips it right after a failure. */
export async function refreshTv(force = false) {
  if (!api.tvStatus) return state.tv;
  lastRefresh = Date.now();
  const s = await attempt(() => (force ? api.tvVolume() : api.tvStatus(true)));
  if (s) state.tv = s;
  if (page && currentView() === page) render();
  if (quick.open) renderQuick();
  return state.tv;
}

async function power(on) {
  const s = await attempt(() => api.tvPower(on), on ? t('tv.onFailed') : t('tv.offFailed'));
  if (s) {
    state.tv = s;
    toast(on ? (s.power === 'on' ? t('tv.alreadyOn') : t('tv.waking')) : t('tv.turnedOff'));
  }
}

async function confirmOff() {
  if (await confirmDialog(t('tv.offConfirm', { name: tvName() }), t('tv.off'), { icon: '⏻' })) await power(false);
}

/** ◀▶ on the volume rows: steps while nothing is on its way, then the rest as one value. */
async function stepVolume(delta) {
  if (!tvPaired()) return;
  if (stepping) {
    pendingSteps += delta;
    return;
  }
  stepping = true;
  try {
    let s = await attempt(() => api.tvVolumeStep(delta > 0), t('tv.volumeFailed'));
    if (s) state.tv = s;
    while (pendingSteps !== 0 && state.tv?.volume != null) {
      const target = Math.max(0, Math.min(100, state.tv.volume + pendingSteps));
      pendingSteps = 0;
      s = await attempt(() => api.tvSetVolume(target), t('tv.volumeFailed'));
      if (s) state.tv = s;
    }
  } finally {
    stepping = false;
    pendingSteps = 0;
  }
}

async function toggleMute() {
  const s = await attempt(() => api.tvMute(null), t('tv.volumeFailed'));
  if (s) state.tv = s;
}

/**
 * Another input: the unlocked couch to read the list and choose; then, with parental controls, the parents' code for exactly the
 * chosen input (its name in the code dialog). The host checks the unlock, the id and the release for that id again.
 */
async function chooseInput() {
  if (!(await ensureUnlocked(t('tv.inputReason')))) return;
  const list = await attempt(() => api.tvInputs(), t('tv.inputsFailed'));
  if (!list) return;
  inputs = list;
  if (!list.length) return toast(t('tv.noInputs'));
  const current = state.tv?.input ?? null;
  const id = await chooseDialog(t('tv.chooseInput'), list.map((i) => ({
    label: i.label,
    detail: i.connected ? '' : t('tv.inputNotConnected'),
    icon: i.id === current ? '✓' : '',
    value: i.id
  })), current);
  if (!id || id === current) return;
  const status = parental.status ?? (await refreshParental());
  const allowed = await admitInputSwitch({
    id,
    parentalOn: !!status?.enabled,
    reason: t('tv.inputReason'),
    parentReason: t('tv.inputReasonNamed', { input: inputLabel(id) }),
    ensureUnlocked,
    ensureParentGrant
  });
  if (!allowed) return;
  const s = await attempt(() => api.tvSwitchInput(id), t('tv.switchFailed'));
  if (s) {
    state.tv = s;
    toast(t('tv.switched', { input: inputLabel(id) }));
  }
}

// ---------------------------------------------------------------------------------------------------------------------------
// Pairing

/**
 * Pairs with the TV at address. allowPlain: the user confirmed an unencrypted pairing (expert mode; the host refuses it anyway for a
 * TV that was paired with encryption before).
 */
async function pairWith(address, allowPlain = false) {
  if (!(await ensureUnlocked(t('tv.pairReason')))) return;
  // The TV showed another certificate than the paired one: say plainly what pairing again means before doing it.
  if (!allowPlain && state.tv?.paired && state.tv.error === 'certChanged'
    && !(await confirmDialog(t('tv.certWarnTitle'), t('tv.repair'), { danger: true, focusCancel: true, message: t('tv.certWarnMessage') }))) return;
  toast(t('tv.pairing.hint'), 8000);
  const s = await attempt(() => api.tvPair(address, allowPlain), t('tv.pairFailed'));
  if (s) {
    state.tv = s;
    switch (pairOutcome(s, !!page?.expert)) {
      case 'askPlain':
        if (await confirmDialog(t('tv.plainTitle'), t('tv.plainConfirm'), { danger: true, focusCancel: true, message: s.secureBefore ? `${t('tv.plainSecureBefore')} ${t('tv.plainMessage')}` : t('tv.plainMessage') })) return pairWith(address, true);
        break;
      case 'plainRefused':
        toast(t('tv.plainExpertOnly'), 8000);
        break;
      case 'pairedNewCert':
        toast(t('tv.certReplaced', { name: tvName(s) }), 10000);
        refreshTv(true);
        break;
      case 'paired':
        toast(t('tv.paired', { name: tvName(s) }));
        refreshTv(true);
        break;
      default:
        break;
    }
  }
  if (page && currentView() === page) render();
}

async function search() {
  if (!(await ensureUnlocked(t('tv.pairReason')))) return;
  toast(t('tv.searching'), 4000);
  const found = await attempt(() => api.tvDiscover(), t('tv.searchFailed'));
  if (!found) return;
  if (!found.length) return toast(t('tv.noneFound'), 6000);
  const address = await chooseDialog(t('tv.foundTitle'), found.map((d) => ({ label: d.name || t('tv.lgTv'), detail: d.address, icon: '📺', value: d.address })),
    state.tv?.address ?? null);
  if (address) await pairWith(address);
}

async function enterAddress() {
  if (!(await ensureUnlocked(t('tv.pairReason')))) return;
  const typed = await openKeyboard({ title: t('tv.addressTitle'), hint: t('tv.addressHint'), placeholder: '192.168.1.20', value: state.tv?.address ?? '', maxLength: 45 });
  if (typed?.trim()) await pairWith(typed.trim());
}

async function enterMac() {
  if (!(await ensureUnlocked(t('tv.pairReason')))) return;
  const typed = await openKeyboard({ title: t('tv.macTitle'), hint: t('tv.macHint'), placeholder: 'A8:23:FE:01:02:03', value: state.tv?.mac ?? '', maxLength: 17 });
  if (typed === undefined) return;
  const s = await attempt(() => api.tvSetMac(typed.trim()), t('tv.macFailed'));
  if (s) {
    state.tv = s;
    toast(s.macKnown ? t('tv.macSaved') : t('tv.macRemoved'));
  }
}

async function forget() {
  if (!(await ensureUnlocked(t('tv.pairReason')))) return;
  if (!(await confirmDialog(t('tv.forgetConfirm', { name: tvName() }), t('tv.forget'), { danger: true, message: t('tv.forgetMessage') }))) return;
  const s = await attempt(() => api.tvForget(), t('tv.forgetFailed'));
  if (s) {
    state.tv = s;
    inputs = null;
    toast(t('tv.forgotten'));
  }
}

// ---------------------------------------------------------------------------------------------------------------------------
// Guide menu: on/off, volume (◀▶, A mutes), input. Only with a paired TV.

addQuickRows(() => {
  const s = state.tv;
  if (!inGuide || !tvPaired(s)) return [];
  if (Date.now() - lastRefresh > 10000) setTimeout(() => refreshTv(false), 0);
  const on = s.power === 'on';
  const rows = [{
    icon: '📺',
    label: on ? t('tv.quick.off') : t('tv.quick.on'),
    detail: `${tvName(s)} · ${tvLine(s)}`,
    keepOpen: true,
    run: () => (on ? confirmOff() : power(true))
  }];
  if (on) {
    rows.push({
      icon: s.muted ? '🔇' : '🔊',
      label: t('tv.quick.volume'),
      detail: s.volume == null ? '…' : s.muted ? t('tv.muted') : t('tv.percent', { volume: s.volume }),
      keepOpen: true,
      hint: s.muted ? t('tv.unmute') : t('tv.mute'),
      adjustHint: t('tv.quick.volume'),
      adjust: (step) => stepVolume(step),
      run: () => toggleMute()
    });
    rows.push({
      icon: '⇄',
      label: t('tv.quick.input'),
      detail: s.input ? inputLabel(s.input) : t('tv.inputUnknown'),
      keepOpen: true,
      run: () => chooseInput()
    });
  }
  return rows;
});

// ---------------------------------------------------------------------------------------------------------------------------
// Settings › TV

export function tvPage() {
  page = {
    id: 'tv',
    name: t('tv.title'),
    index: 0,
    list: [],
    expert: false,
    rows() {
      const s = state.tv;
      if (!s) return [];
      if (s.pairing !== 'idle') return [{ icon: '✕', label: t('tv.cancelPair'), detail: t('tv.cancelPairDetail'), run: () => attempt(() => api.tvCancelPair()) }];
      if (!s.paired) {
        const rows = [{ icon: '⌕', label: t('tv.search'), detail: t('tv.searchDetail'), run: () => search() }];
        if (this.expert) rows.push({ icon: '#', label: t('tv.address'), detail: t('tv.addressDetail'), run: () => enterAddress() });
        return rows;
      }
      const on = s.power === 'on';
      const rows = [];
      if (s.error === 'certChanged' || s.error === 'unpaired') rows.push({ icon: '↻', label: t('tv.repair'), detail: t('tv.repairDetail'), run: () => pairWith(s.address) });
      rows.push(on
        ? { icon: '⏻', label: t('tv.off'), detail: t('tv.offDetail'), run: () => confirmOff() }
        : { icon: '⏻', label: t('tv.on'), detail: s.macKnown ? t('tv.onDetail') : t('tv.hint.noMac'), run: () => power(true) });
      if (on) {
        rows.push({
          icon: s.muted ? '🔇' : '🔊',
          label: t('tv.volume'),
          detail: s.volume == null ? t('tv.volumeUnknown') : s.muted ? t('tv.muted') : t('tv.percent', { volume: s.volume }),
          adjust: (step) => stepVolume(step),
          run: () => toggleMute()
        });
        rows.push({ icon: '⇄', label: t('tv.chooseInput'), detail: s.input ? inputLabel(s.input) : t('tv.inputUnknown'), run: () => chooseInput() });
      }
      rows.push({ icon: '↻', label: t('tv.refresh'), detail: t('tv.refreshDetail'), run: () => refreshTv(true) });
      if (this.expert) {
        rows.push({ icon: '#', label: t('tv.mac'), detail: s.mac ?? t('tv.macUnknown'), run: () => enterMac() });
        rows.push({ icon: '⌕', label: t('tv.pairOther'), detail: t('tv.pairOtherDetail'), run: () => search() });
        rows.push({ icon: '#', label: t('tv.address'), detail: t('tv.addressDetail'), run: () => enterAddress() });
      }
      rows.push({ icon: '✕', label: t('tv.forget'), detail: t('tv.forgetDetail'), run: () => forget() });
      return rows;
    },
    build(root) {
      const s = state.tv;
      this.list = this.rows();
      this.index = Math.max(0, Math.min(this.index, this.list.length - 1));
      this.items = this.list.map((r) => el('li', { className: 'check-row' },
        el('span', { className: 'check-box', textContent: r.icon }),
        el('span', { className: 'check-main' }, el('strong', { textContent: r.label }), el('small', { textContent: r.detail ?? '' }))));
      this.scroller = el('ul', { className: 'check-list list-scroll' }, this.items);
      const hint = tvHint(s);
      const pill = s?.paired && s.power === 'on' ? el('span', { className: 'pill on', textContent: t('tv.power.on') })
        : s?.pairing !== 'idle' && s?.pairing || s?.power === 'waking' ? el('span', { className: 'spinner' }) : null;
      root.append(el('div', { className: 'files' },
        el('div', { className: 'files-head' },
          el('h2', { textContent: s?.paired ? tvName(s) : t('tv.title') }),
          el('p', { className: 'meta' }, s ? tvLine(s) : t('tv.loading'), pill ? ' ' : '', pill ?? ''),
          s?.paired ? el('p', { className: 'meta', textContent: [s.model, s.address, s.secure ? t('tv.encrypted') : t('tv.unencrypted')].filter(Boolean).join(' · ') }) : null,
          hint ? el('p', { className: 'meta', textContent: hint }) : null),
        this.scroller,
        el('p', { className: 'meta', textContent: s?.paired ? t('tv.privacy') : t('tv.intro') })));
      this.update({ instant: true });
    },
    update({ instant = false } = {}) {
      if (!this.items?.length) return;
      setFocused(this.items, this.index);
      keepInView(this.scroller, this.items[this.index], { instant });
    },
    async handle(action) {
      const row = this.list[this.index];
      switch (action) {
        case 'up': return this.list.length && move(this, -1, this.list.length, true);
        case 'down': return this.list.length && move(this, 1, this.list.length, true);
        case 'left':
        case 'right':
          if (!row?.adjust) return;
          sound.move();
          await row.adjust(action === 'left' ? -1 : 1);
          if (currentView() === this) render();
          return;
        case 'x': return refreshTv(true);
        case 'a': {
          if (!row) return;
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
      const row = this.list?.[this.index];
      return [
        ...(row?.adjust ? [['◀▶', t('tv.volume')]] : []),
        ...(this.list?.length ? [['A', row?.adjust ? (state.tv?.muted ? t('tv.unmute') : t('tv.mute')) : t('tv.select')]] : []),
        ['X', t('tv.refresh')],
        ['B', t('tv.back')]
      ];
    }
  };
  const opened = page;
  refreshTv(false);
  attempt(() => api.getSettings()).then((settings) => {
    opened.expert = !!settings?.expertMode;
    if (currentView() === opened) render();
  });
  return page;
}
