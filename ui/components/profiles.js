// Profiles: the full-screen picker (at the start with several profiles, after the lock for a protected profile), the switch from the
// guide menu, and the parents' management page in the settings. The rules live in the host (Core/Config/ProfileSwitch.cs, Hud
// SwitchProfile): this file only shows what a switch needs and asks for it. Names come from the host and are only ever set as
// textContent; colors and avatars only from the fixed presets below, whatever the host sends.

import { api } from '../core/api.js';
import { el, keepInView, setFocused } from '../core/dom.js';
import { attempt, toast } from '../core/feedback.js';
import { move, push, removePage, render } from '../core/nav.js';
import { state } from '../core/state.js';
import { sound } from '../sound.js';
import { t } from '../core/i18n.js';
import { activeSystem, ageChoices, ageLabels, limitText, shownLimit } from '../core/age-rating-rules.js';
import { chooseDialog, closeOverlay, confirmDialog } from './overlay.js';
import { choosePasscode, enterPasscode } from './passcode.js';
import { ensureParentGrant, formatRemaining, parental, refreshParental } from './parental.js';
import { openKeyboard } from './keyboard.js';
import { addQuickRows, closeQuickMenu } from './quick-menu.js';
import { onScreensaverStop, screensaverShowing } from '../core/screensaver.js';

// The same presets as ProfileLooks in Core (the host refuses anything else).
const AVATARS = { fox: '🦊', panda: '🐼', frog: '🐸', lion: '🦁', octopus: '🐙', rocket: '🚀', cat: '🐱', dog: '🐶', unicorn: '🦄', robot: '🤖', alien: '👽', star: '⭐' };
const COLORS = ['#3fa9f5', '#ff8800', '#e5484d', '#30a46c', '#8e4ec6', '#f5d90a', '#e93d82', '#12a594'];
const NAME_MAX = 24;
const SWITCH = 'profileSwitch';
const MANAGE = 'profileManage';

export const profiles = { data: null };

const avatarOf = (p) => AVATARS[p?.avatar] ?? AVATARS.fox;
const colorOf = (p) => (COLORS.includes(p?.color) ? p.color : COLORS[0]);
const many = () => (profiles.data?.profiles?.length ?? 0) > 1;
/** The name to show: a default profile nobody named is "Standard" / "Default" (the parents can rename it). */
const nameOf = (p) => p?.name || (p?.isDefault ? t('profiles.defaultName') : '');

async function refresh() {
  const data = await attempt(() => api.getProfiles?.());
  if (data) profiles.data = data;
  return profiles.data;
}

/** A round avatar in the profile's color. */
function avatarEl(p, big = false) {
  const node = el('span', { className: big ? 'profile-avatar big' : 'profile-avatar', textContent: avatarOf(p) });
  node.style.setProperty('--profile-color', colorOf(p));
  return node;
}

// ---------------------------------------------------------------------------
// Start

export async function startProfiles() {
  if (!api.getProfiles) return;
  addQuickRows(() => (many() && !state.session ? [{
    icon: '👥',
    label: t('profiles.switch'),
    detail: nameOf(profiles.data?.profiles?.find((p) => p.active)),
    run: () => showPicker({ required: false })
  }] : []));
  api.onProfiles?.(async (event) => {
    const data = await refresh();
    if (!event?.picker || !data?.picker) return;
    // A protected profile went back to the picker: the page still has that person's skin, language and sounds. It starts again with the
    // household's (the host answers systemInfo with them while the picker waits), so the picker gives nothing of the person away.
    if (data.pickRequired && !heldAtStart) return neutralLook();
    showPicker({ required: !!data.pickRequired });
  });
  onScreensaverStop(() => {
    if (!relookPending) return;
    relookPending = false;
    reloadForProfile();
  });
  const data = await refresh();
  heldAtStart = !!data?.pickRequired;
  // A reload (switch, language) while the picker was open: the host's flag follows the screen.
  if (data?.picker) showPicker({ required: !!data.pickRequired });
  else api.profileScreen?.(false).catch(() => {});
}

// The page started with the picker waiting for a protected profile, so it already has the household's look.
let heldAtStart = false;
// The reload for the household's look waits until the screensaver is gone (it would end the screensaver and the display-off after it).
let relookPending = false;

function neutralLook() {
  if (screensaverShowing()) {
    relookPending = true;
    return;
  }
  reloadForProfile();
}

/** The picker or the management page is on the TV: the phone's keys do not reach the HUD while it is. */
const screen = (open) => api.profileScreen?.(open).catch(() => {});

// ---------------------------------------------------------------------------
// The picker

let picker = null;

/** The full-screen choice. Required: nothing but a profile gets out of it (a protected profile waits for its code). */
export async function showPicker({ required = false } = {}) {
  const data = await refresh();
  if (!data || !data.profiles?.length) return;
  if (picker) {
    picker.required = picker.required || required;
    picker.sticky = picker.required;
    state.pickerHold = picker.required;
    render();
    return;
  }
  // Required: the guide menu and the host's navigation stay out until a profile is chosen (app.js, quick-menu.js).
  state.pickerHold = required;
  closeQuickMenu();
  if (state.overlay) closeOverlay();
  const page = {
    id: 'profile-picker',
    name: t('profiles.pickerTitle'),
    required,
    // Required: home, tab switches and the guide menu stay out (clearStack keeps it too).
    sticky: required,
    index: Math.max(0, data.profiles.findIndex((p) => p.active)),
    busy: false,
    captures() {
      return this.required;
    },
    onClose() {
      if (picker === this) picker = null;
      state.pickerHold = false;
      screen(false);
      // Closed without a choice (not required): the profile on the couch stays chosen.
      if (!this.chose) {
        const active = profiles.data?.profiles?.find((p) => p.active);
        if (active) api.switchProfile(active.id, null).catch(() => {});
      }
    },
    build(root) {
      this.list = profiles.data?.profiles ?? [];
      this.index = Math.min(this.index, Math.max(0, this.list.length - 1));
      this.tiles = this.list.map((p) => el('li', { className: 'profile-tile' },
        avatarEl(p, true),
        el('strong', { className: 'profile-name', textContent: nameOf(p) }),
        el('small', { className: 'profile-lock', textContent: p.hasCode ? '🔒' : p.child ? '⏳' : '' })));
      const d = profiles.data;
      root.append(el('div', { className: 'profile-picker' },
        el('h2', { textContent: t('profiles.pickerTitle') }),
        el('p', { className: 'meta', textContent: d?.gameRunning ? t('profiles.gameRunningHint') : d?.exhausted ? t('profiles.exhaustedHint') : t('profiles.pickerHint') }),
        el('ul', { className: 'profile-tiles' }, this.tiles)));
      this.update();
    },
    update() {
      if (this.tiles?.length) setFocused(this.tiles, this.index);
    },
    async handle(action) {
      if (this.busy) return;
      switch (action) {
        case 'left': return move(this, -1, this.list.length, true);
        case 'right': return move(this, 1, this.list.length, true);
        case 'a': {
          const p = this.list[this.index];
          if (!p) return;
          sound.select();
          this.busy = true;
          try {
            await enterProfile(p, this);
          } finally {
            this.busy = false;
          }
          return;
        }
        case 'b':
          if (this.required) return sound.bump();
          removePage(this);
          this.onClose();
          sound.back();
          return render();
        default:
          if (this.required) sound.bump();
      }
    },
    hints() {
      return this.required ? [['A', t('profiles.hintChoose')]] : [['A', t('profiles.hintChoose')], ['B', t('profiles.hintBack')]];
    }
  };
  picker = page;
  screen(true);
  push(page);
}

function closePicker(page) {
  if (!page) return;
  // The host refused everything else while the profile waited for its code (library, collections …): load it all fresh.
  if (page.required) return reloadForProfile();
  page.chose = true;
  page.sticky = false;
  removePage(page);
  page.onClose();
  render();
}

/** After a switch: the page reloads for the new profile's language, skin, sounds, favorites and collections. */
function reloadForProfile() {
  try {
    sessionStorage.removeItem('couchhud.resume');
  } catch { /* storage blocked */ }
  screen(false);
  location.reload();
}

/** Switch to (or re-enter) one profile with what it needs: nothing, the child's own code, or the parents' code for this switch. */
async function enterProfile(p, page) {
  if (p.need === 'gameRunning') {
    sound.bump();
    return toast(t('profiles.gameRunning'), 5000);
  }
  let result;
  if (p.need === 'parentCode') {
    if (!(await ensureParentGrant(t('profiles.parentReason', { name: nameOf(p) }), { action: SWITCH, item: p.id }))) return;
    result = await attempt(() => api.switchProfile(p.id, null));
  } else if (p.need === 'profileCode') {
    result = await childCode(p);
  } else {
    result = await attempt(() => api.switchProfile(p.id, null));
  }
  // The host decides again: a parents' code it asks for after all (the time ran out meanwhile).
  if (result?.ok === false && result.need === 'parentCode') {
    if (!(await ensureParentGrant(t('profiles.parentReason', { name: nameOf(p) }), { action: SWITCH, item: p.id }))) return;
    result = await attempt(() => api.switchProfile(p.id, null));
  }
  if (!result?.ok) return;
  if (result.switched) return reloadForProfile();
  closePicker(page);
  await refresh();
}

/**
 * The child's own code, with its own lockout in the host. Cancelled: the parents may open it with their code, but only when there is a
 * parents' code (the host never takes the unlocked couch instead of a child's code).
 */
async function childCode(p) {
  let error = '';
  const status = parental.status ?? (await refreshParental());
  const parentWay = !!status?.hasParentCode;
  for (;;) {
    const code = await enterPasscode({ title: t('profiles.codeTitle', { name: nameOf(p) }), hint: parentWay ? t('profiles.codeHint') : '', error });
    if (!code) {
      if (!parentWay) return undefined;
      if (!(await confirmDialog(t('profiles.parentInstead'), t('profiles.parentInsteadButton')))) return undefined;
      if (!(await ensureParentGrant(t('profiles.parentReason', { name: nameOf(p) }), { action: SWITCH, item: p.id }))) return undefined;
      return attempt(() => api.switchProfile(p.id, null));
    }
    const result = await attempt(() => api.switchProfile(p.id, code));
    if (!result || result.ok) return result;
    sound.bump();
    error = result.waitSeconds ? t('passcode.wrongWait', { count: result.waitSeconds }) : t('passcode.wrong');
  }
}

// ---------------------------------------------------------------------------
// Management (Settings): everything with the controller, every change with the parents' code for exactly that change.

const manageGrant = (reason, id = null) => ensureParentGrant(reason, { action: MANAGE, item: id });

async function chooseName(title, value = '') {
  const name = await openKeyboard({ title, value, maxLength: NAME_MAX, hint: t('profiles.nameHint', { count: NAME_MAX }) });
  if (name === undefined) return undefined;
  const clean = name.replace(/\s+/g, ' ').trim();
  if (!clean) {
    toast(t('profiles.nameEmpty'));
    return undefined;
  }
  return clean;
}

const chooseAvatar = (current) => chooseDialog(t('profiles.avatarTitle'),
  Object.entries(AVATARS).map(([id, emoji]) => ({ label: emoji, icon: emoji, value: id })), current);

// i18n-used: profiles.color.0 profiles.color.1 profiles.color.2 profiles.color.3 profiles.color.4 profiles.color.5 profiles.color.6 profiles.color.7
const colorName = (i) => {
  const key = `profiles.color.${i}`;
  return t(key);
};
const chooseColor = (current) => chooseDialog(t('profiles.colorTitle'),
  COLORS.map((c, i) => ({ label: colorName(i), icon: '●', value: c })), current);

/** Runs one change after the parents' code; the host answers with the new list. */
async function change(reason, id, call, message) {
  if (!(await manageGrant(reason, id))) return false;
  const data = await attempt(call, t('profiles.notSaved'));
  if (!data) return false;
  profiles.data = data;
  if (message) toast(message);
  render();
  return true;
}

async function addProfile() {
  const d = profiles.data;
  if (d && d.profiles.length >= d.max) return toast(t('profiles.max', { count: d.max }), 5000);
  const name = await chooseName(t('profiles.newName'));
  if (!name) return;
  const kind = await chooseDialog(t('profiles.kindTitle'), [
    { label: t('profiles.kindChild'), detail: t('profiles.kindChildDetail'), icon: '⏳', value: 'child' },
    { label: t('profiles.kindAdult'), detail: t('profiles.kindAdultDetail'), icon: '👤', value: 'adult' }
  ], 'child');
  if (!kind) return;
  const avatar = await chooseAvatar('fox');
  if (!avatar) return;
  const color = await chooseColor(COLORS[0]);
  if (!color) return;
  const before = new Set((profiles.data?.profiles ?? []).map((x) => x.id));
  if (!(await change(t('profiles.addReason'), null, () => api.addProfile(name, avatar, color, kind === 'child'), t('profiles.added', { name })))) return;
  if (kind !== 'child') return;
  // A new child's profile: straight to its rules. It took over the default profile's, so with a default profile without limits it has none yet.
  const created = profiles.data?.profiles.find((x) => !before.has(x.id));
  if (!created) return;
  const fallback = profiles.data.profiles.find((x) => x.isDefault);
  if (fallback && !fallback.child) toast(t('profiles.noLimitsYet', { name }), 8000);
  push(rulesPage(created));
}

async function profileMenu(p) {
  const options = [];
  if (!p.active) options.push({ label: t('profiles.switchTo'), icon: '↪', value: 'switch' });
  options.push({ label: t('profiles.rename'), icon: '✎', value: 'rename' });
  options.push({ label: t('profiles.avatar'), icon: avatarOf(p), value: 'avatar' });
  options.push({ label: t('profiles.color'), icon: '●', value: 'color' });
  if (!p.isDefault) {
    options.push({ label: p.restricted ? t('profiles.makeAdult') : t('profiles.makeChild'), detail: p.restricted ? t('profiles.makeAdultDetail') : t('profiles.makeChildDetail'), icon: p.restricted ? '👤' : '⏳', value: 'kind' });
    // An adult's profile has no limits (the parents' code guards entering it); rules are for children's profiles.
    if (p.restricted) options.push({ label: t('profiles.rules'), detail: t('profiles.rulesDetail'), icon: '⏱', value: 'rules' });
  }
  if (p.restricted) {
    options.push({ label: p.hasCode ? t('profiles.codeChange') : t('profiles.codeSet'), detail: t('profiles.codeSetDetail'), icon: '🔒', value: 'code' });
    if (p.hasCode) options.push({ label: t('profiles.codeRemove'), icon: '🔓', value: 'uncode' });
  }
  if (!p.isDefault && !p.active) options.push({ label: t('profiles.delete'), detail: t('profiles.deleteDetail'), icon: '🗑', danger: true, value: 'delete' });
  const choice = await chooseDialog(nameOf(p), options);
  switch (choice) {
    case 'switch': return enterProfile(p, null);
    case 'rename': {
      const name = await chooseName(t('profiles.renameTitle'), p.name ?? '');
      if (name && name !== p.name) await change(t('profiles.renameReason', { name: nameOf(p) }), p.id, () => api.renameProfile(p.id, name), t('profiles.renamed', { name }));
      return;
    }
    case 'avatar': {
      const avatar = await chooseAvatar(p.avatar);
      if (avatar && avatar !== p.avatar) await change(t('profiles.lookReason', { name: nameOf(p) }), p.id, () => api.setProfileLook(p.id, avatar, colorOf(p)));
      return;
    }
    case 'color': {
      const color = await chooseColor(colorOf(p));
      if (color && color !== p.color) await change(t('profiles.lookReason', { name: nameOf(p) }), p.id, () => api.setProfileLook(p.id, p.avatar, color));
      return;
    }
    case 'kind':
      return change(t('profiles.kindReason', { name: nameOf(p) }), p.id, () => api.setProfileRestricted(p.id, !p.restricted),
        p.restricted ? t('profiles.nowAdult', { name: nameOf(p) }) : t('profiles.nowChild', { name: nameOf(p) }));
    case 'rules':
      return push(rulesPage(p));
    case 'code': {
      // The parents first, then the child's new code (twice): the code never leaves this call except to the host, which keeps its hash.
      if (!(await manageGrant(t('profiles.codeReason', { name: nameOf(p) }), p.id))) return;
      const code = await choosePasscode();
      if (!code) return;
      const data = await attempt(() => api.setProfileCode(p.id, code), t('profiles.notSaved'));
      if (!data) return;
      profiles.data = data;
      toast(t('profiles.codeSaved', { name: nameOf(p) }));
      return render();
    }
    case 'uncode':
      return change(t('profiles.codeReason', { name: nameOf(p) }), p.id, () => api.setProfileCode(p.id, null), t('profiles.codeRemoved', { name: nameOf(p) }));
    case 'delete': {
      if (!(await confirmDialog(t('profiles.deleteConfirm', { name: nameOf(p) }), t('profiles.deleteButton'), { danger: true, focusCancel: true, message: t('profiles.deleteMessage') }))) return;
      return change(t('profiles.deleteReason', { name: nameOf(p) }), p.id, () => api.removeProfile(p.id), t('profiles.deleted', { name: nameOf(p) }));
    }
  }
}

/** Settings › Parental controls › Profiles. */
export function profilesPage() {
  return {
    id: 'profiles',
    name: t('profiles.manage'),
    index: 0,
    onClose: () => screen(false),
    build(root) {
      const d = profiles.data;
      const list = d?.profiles ?? [];
      this.rows = [...list.map((p) => ({ profile: p })), ...(list.length < (d?.max ?? 8) ? [{ add: true }] : [])];
      this.index = Math.min(this.index, Math.max(0, this.rows.length - 1));
      this.items = this.rows.map((r) => r.add
        ? el('li', { className: 'check-row' },
          el('span', { className: 'check-box', textContent: '+' }),
          el('span', { className: 'check-main' }, el('strong', { textContent: t('profiles.add') }), el('small', { textContent: t('profiles.addDetail', { count: d?.max ?? 8 }) })))
        : el('li', { className: 'check-row' },
          avatarEl(r.profile),
          el('span', { className: 'check-main' },
            el('strong', { textContent: nameOf(r.profile) }),
            el('small', { textContent: describe(r.profile) }))));
      this.scroller = el('ul', { className: 'check-list list-scroll' }, this.items);
      root.append(el('div', { className: 'files' },
        el('div', { className: 'files-head' }, el('h2', { textContent: t('profiles.manage') })),
        el('p', { className: 'meta', textContent: t('profiles.manageIntro') }),
        this.scroller));
      this.update({ instant: true });
    },
    update({ instant = false } = {}) {
      if (!this.items?.length) return;
      setFocused(this.items, this.index);
      keepInView(this.scroller, this.items[this.index], { instant });
    },
    async handle(action) {
      switch (action) {
        case 'up': return move(this, -1, this.rows.length, true);
        case 'down': return move(this, 1, this.rows.length, true);
        case 'a': {
          const row = this.rows[this.index];
          if (!row) return;
          sound.select();
          if (row.add) await addProfile();
          else await profileMenu(row.profile);
          await refresh();
          render();
        }
      }
    },
    hints() {
      return [['A', t('profiles.hintEdit')], ['B', t('profiles.hintBack')]];
    }
  };
}

/** Opens the management page: the host list first, the phone's keys off while it is open. */
export async function openProfilesPage(open) {
  await refresh();
  if (!profiles.data) return toast(t('profiles.unavailable'));
  screen(true);
  open(profilesPage());
}

function describe(p) {
  const parts = [];
  if (p.active) parts.push(t('profiles.active'));
  if (p.isDefault) parts.push(t('profiles.default'));
  parts.push(p.child ? t('profiles.child') : t('profiles.adult'));
  if (p.hasCode) parts.push(t('profiles.withCode'));
  // Without its own code, brothers and sisters can use a child's profile too (and its play time).
  else if (p.restricted) parts.push(t('profiles.noCodeHint'));
  return parts.join(' · ');
}

// ---------------------------------------------------------------------------
// The rules of one profile (the one on the couch also has them under Parental controls).

const limitLabel = (m) => (m ? (m < 60 ? t('settings.parental.minutes', { m }) : formatRemaining(m * 60)) : t('settings.parental.unlimited'));
const hourLabel = (minute) => `${Math.floor(minute / 60)}:${String(minute % 60).padStart(2, '0')}`;

function rulesPage(p) {
  return {
    id: 'profile-rules',
    name: t('profiles.rulesTitle', { name: nameOf(p) }),
    index: 0,
    rules: null,
    async load() {
      this.rules = await attempt(() => api.getProfileRules(p.id));
      render();
    },
    rows() {
      const r = this.rules;
      if (!r) return [];
      const shown = shownLimit({ active: r.ratingSystemActive, saved: r.ratingSystemSaved, maxAge: r.maxAge, maxAgeSaved: r.maxAgeSaved });
      return [
        { label: t('settings.parental.weekday'), value: limitLabel(r.weekdayMinutes), run: () => this.pick('weekdayMinutes', t('settings.parental.weekdayTitle'), [0, 60, 90, 120, 150, 180, 240, 300, 360].map((m) => ({ label: limitLabel(m), value: m })), r.weekdayMinutes) },
        { label: t('settings.parental.weekend'), value: limitLabel(r.weekendMinutes), run: () => this.pick('weekendMinutes', t('settings.parental.weekendTitle'), [0, 60, 90, 120, 150, 180, 240, 300, 360].map((m) => ({ label: limitLabel(m), value: m })), r.weekendMinutes) },
        { label: t('settings.parental.window'), value: r.windowStart < 0 ? t('settings.parental.windowAlways') : t('settings.parental.windowValue', { start: hourLabel(r.windowStart), end: hourLabel(r.windowEnd) }), run: () => this.window() },
        { label: t('settings.parental.perGame'), value: r.perGameMinutes ? limitLabel(r.perGameMinutes) : t('settings.common.off'), run: () => this.pick('perGameMinutes', t('settings.parental.perGameTitle'), [0, 30, 45, 60, 90, 120].map((m) => ({ label: m ? limitLabel(m) : t('settings.common.off'), value: m })), r.perGameMinutes) },
        // The limit in the rating system that counts now (the household's, see the parental settings); after a change of the Windows
        // region in the one it was saved under, as the parental settings name it.
        { label: t('settings.parental.age'), value: limitText(shown.system, shown.age, ageLabels(t)), run: () => this.pick('maxAge', t('settings.parental.age'), ageChoices(activeSystem(r.ratingSystemActive), ageLabels(t)), r.maxAge) },
        ...(r.maxAge ? [{ label: t('settings.parental.blockUnknown'), value: r.blockUnknownAge ? t('settings.common.on') : t('settings.common.off'), run: () => this.set('blockUnknownAge', !r.blockUnknownAge) }] : []),
        { label: t('settings.parental.countApps'), value: r.countApps ? t('settings.common.on') : t('settings.common.off'), run: () => this.set('countApps', !r.countApps) }
      ];
    },
    async pick(key, title, options, current) {
      const v = await chooseDialog(title, options, current);
      if (v !== undefined && v !== current) await this.set(key, v);
    },
    async window() {
      const r = this.rules;
      const hours = Array.from({ length: 24 }, (_, h) => ({ label: t('settings.parental.hour', { h }), value: h * 60 }));
      const start = await chooseDialog(t('settings.parental.windowFrom'), [{ label: t('settings.parental.always'), detail: t('settings.parental.noWindow'), value: -1 }, ...hours], r.windowStart);
      if (start === undefined) return;
      if (start < 0) return this.set('window', [-1, -1]);
      const end = await chooseDialog(t('settings.parental.windowTo'), hours.filter((h) => h.value !== start), r.windowEnd >= 0 ? r.windowEnd : (start + 360) % 1440);
      if (end !== undefined) await this.set('window', [start, end]);
    },
    async set(key, value) {
      if (!(await manageGrant(t('profiles.rulesReason', { name: nameOf(p) }), p.id))) return;
      const rules = await attempt(() => api.setProfileRule(p.id, key, value), t('profiles.notSaved'));
      if (!rules) return;
      this.rules = rules;
      if (p.active) refreshParental();
      render();
    },
    build(root) {
      if (!this.rules && !this.loading) {
        this.loading = true;
        this.load();
      }
      this.list = this.rows();
      this.index = Math.min(this.index, Math.max(0, this.list.length - 1));
      this.items = this.list.map((row) => el('li', { className: 'check-row' },
        el('span', { className: 'check-main' }, el('strong', { textContent: row.label }), el('small', { textContent: row.value }))));
      this.scroller = el('ul', { className: 'check-list list-scroll' }, this.items);
      root.append(el('div', { className: 'files' },
        el('div', { className: 'files-head' }, el('h2', { textContent: t('profiles.rulesTitle', { name: nameOf(p) }) })),
        this.list.length ? this.scroller : el('p', { className: 'empty-note', textContent: t('profiles.loading') })));
      this.update({ instant: true });
    },
    update({ instant = false } = {}) {
      if (!this.items?.length) return;
      setFocused(this.items, this.index);
      keepInView(this.scroller, this.items[this.index], { instant });
    },
    async handle(action) {
      switch (action) {
        case 'up': return move(this, -1, this.list.length, true);
        case 'down': return move(this, 1, this.list.length, true);
        case 'a': {
          const row = this.list[this.index];
          if (row) await row.run();
        }
      }
    },
    hints() {
      return [['A', t('profiles.hintEdit')], ['B', t('profiles.hintBack')]];
    }
  };
}
