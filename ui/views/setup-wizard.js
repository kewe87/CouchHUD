// First-run setup assistant: a few questions, one per screen, big cards instead of prose.
//
// It is only a guided front end. Every choice goes through the setting or flow it belongs to, the same call the settings make:
// the language and sound settings, the skin, the pointer, the game-client modes, the one-button console setup (and its way back)
// and the couch-lock enrollment from Settings › Console. Nothing here sets anything up by itself, and nothing is stored twice.
// The only own value is `setupWizardDone`, a version marker like `acceptedTerms`, so the assistant shows once.
// The mode question only records the choice: the console setup (or the way back) starts after the last question, with
// "Let's go", once the marker is written, so its restart offer cannot cut the assistant short or bring it back.
//
// Couch lock: the assistant never gets around it. Opening it from the settings asks for the same release the settings need,
// it does not start by itself on a locked couch, every write is checked by the host as usual, and a write the host refuses
// because the couch locked again in the meantime asks for the code before it is tried again.

import { api } from '../core/api.js';
import { lang, t } from '../core/i18n.js';
import { el, setFocused } from '../core/dom.js';
import { attempt, toast } from '../core/feedback.js';
import { push, removePage, render, renderHints } from '../core/nav.js';
import { state } from '../core/state.js';
import { applySkin } from '../core/skins.js';
import { iconEl } from '../core/glyph-icons.js';
import { categoryIcon } from './settings-icons.js';
import { setPointer } from '../components/mouse.js';
import { confirmDialog } from '../components/overlay.js';
import { ensureUnlocked } from '../components/passcode.js';
import { admitProtectedChange, configFaultNow } from '../core/lock-rules.js';
import { setAudioMode, sound } from '../sound.js';
import { showTermsIfNeeded, termsAccepted, termsPage } from './legal.js';
import { consoleRevertPage, consoleSetupPage, hasConsoleSetup, isConsoleReady } from './console-setup.js';
import { consoleDeps, enrollCouchCode } from './settings.js';
import { setCaseHudApi } from './casehud.js';
import { AREAS, AREA_DESC_KEYS, AREA_ICONS, AREA_NAME_KEYS, areaOn, enabledAreas, setAreas, shownSteps, toggleArea } from '../core/areas.js';
import { ensureParentGrant, parental } from '../components/parental.js';

/** Raise when the assistant gains a question every install should see once. */
export const WIZARD_VERSION = 1;

// A language switch reloads the page: the assistant comes back at the next step.
const RESUME_KEY = 'couchhud.wizard';

const where = (...parts) => t('settings.crumb', { page: parts.map((p) => t(p)).join(' › ') });

// ---------------------------------------------------------------------------
// Host calls

/** Runs a host call; when the couch locked meanwhile, asks for the code (as the settings do) and tries once more. */
async function call(run, label = t('wizard.notSaved')) {
  try {
    return { ok: true, value: await run() };
  } catch (err) {
    if (err?.locked) {
      if (!(await ensureUnlocked(t('wizard.name')))) return { ok: false };
      try {
        return { ok: true, value: await run() };
      } catch (again) {
        toast(`${label}: ${again?.message || again}`, 5000);
        return { ok: false };
      }
    }
    toast(`${label}: ${err?.message || err}`, 5000);
    return { ok: false };
  }
}

const save = async (key, value) => (await call(() => api.setSetting(key, value))).ok;

async function loadEnv() {
  const quiet = { quietLocked: true };
  // The game clients belong to Gaming: not asked for while it is off (the host refuses then).
  const [settings, security, launchers] = await Promise.all([
    attempt(() => api.getSettings(), undefined, quiet),
    attempt(() => api.getSecurity(), undefined, quiet),
    areaOn('games') ? attempt(() => api.getLaunchers(), undefined, quiet) : []
  ]);
  return { settings: settings ?? {}, security: security ?? {}, launchers: Array.isArray(launchers) ? launchers : [] };
}

const isFull = (env) => env.settings.integrationMode === 'full';
const lockOn = (env) => !!env.security.couchLock && env.security.couchLock !== 'off';
const pointerMode = (s) => (s.pointer === 'system' || (s.pointer === 'mouse' && s.mouseUnlocked) ? s.pointer : 'skin');
// i18n-used: wizard.sound.off wizard.sound.ui wizard.sound.music wizard.clients.standby wizard.clients.demand wizard.clients.off
const AUDIO = ['off', 'ui', 'music'];
const audioMode = (s) => (AUDIO.includes(s.audioMode) ? s.audioMode : s.sounds === false ? 'off' : 'ui');
const soundKey = (mode) => 'wizard.sound.' + mode;
const clientModeKey = (mode) => 'wizard.clients.' + (['standby', 'demand', 'off'].includes(mode) ? mode : 'standby');
/** CaseHUD is installed (and new enough) but its API is not on: the assistant offers to switch it on (full integration only). */
const caseHudWantsApi = () => {
  const s = state.caseHud;
  return !!s?.installed && !s.needsUpdate && (s.state === 'apiOff' || s.state === 'notRunning');
};

// ---------------------------------------------------------------------------
// Steps

/**
 * A step: title, lead, where it lives in the settings, and its options. An option is a card (icon, short title, up to three
 * bullets), a chip or a row; `row` puts it on a line of its own (up/down between lines). run() returns 'next' to go on.
 */
function steps(page) {
  const env = page.env;
  return {
    welcome: {
      icon: 'globe',
      title: () => t('wizard.welcome.title'),
      lead: () => t('wizard.welcome.lead'),
      later: () => where('settings.cat.info', 'settings.language.title'),
      hero: true,
      options: () => [['de', 'Deutsch', t('wizard.welcome.de')], ['en', 'English', t('wizard.welcome.en')]].map(([id, title, bullet]) => ({
        id, title, icon: '🌐', bullets: [bullet], current: lang() === id, compact: true,
        run: async () => {
          if (id === lang()) return 'next';
          if (!(await save('language', id))) return;
          try { sessionStorage.setItem(RESUME_KEY, JSON.stringify({ step: page.nextOf('welcome'), firstRun: page.firstRun })); } catch { /* storage blocked: starts at the welcome again */ }
          location.reload();
        }
      }))
    },
    terms: {
      // The existing first-start notice is this step: same texts, same accept and decline.
      enter: () => page.openTerms()
    },
    areas: {
      glyph: '▦',
      title: () => t('wizard.areas.title'),
      lead: () => t('wizard.areas.lead'),
      later: () => where('settings.cat.console', 'settings.areas.title'),
      // One line per area (A switches it), then "Next" saves the choice; the last of Games, Apps and Browser stays on.
      options: () => [
        ...AREAS.map((a, i) => ({
          id: `area:${a}`, row: i, line: 0, listRow: true, title: t(AREA_NAME_KEYS[a]), detail: t(AREA_DESC_KEYS[a]), icon: AREA_ICONS[a],
          pill: page.areas.includes(a) ? t('settings.common.on') : t('settings.common.off'), pillOn: page.areas.includes(a),
          run: () => {
            const next = toggleArea(page.areas, a);
            if (next.blocked) toast(t('settings.areas.needOne'));
            page.areas = next.list;
          }
        })),
        { id: 'continue', row: AREAS.length, line: 1, button: true, title: t('wizard.next'), run: () => page.saveAreas() }
      ]
    },
    mode: {
      icon: 'console',
      title: () => t('wizard.mode.title'),
      lead: () => t('wizard.mode.lead'),
      later: () => where('settings.cat.console', 'settings.mode.title'),
      options: () => {
        const full = isFull(env);
        const ready = isConsoleReady(env.settings, env.security);
        return [
          {
            id: 'light', icon: '▭', title: t('wizard.mode.light'), tag: t('wizard.mode.lightTag'), current: !full, chosen: page.intent === 'light',
            bullets: [t('wizard.mode.light1'), t('wizard.mode.light2'), t('wizard.mode.light3')],
            foot: hasConsoleSetup(env.settings, env.security) ? t('wizard.mode.lightRevert') : null,
            run: () => {
              // Only the choice: the way back from Settings › Console runs after the last question ("Let's go").
              page.intent = hasConsoleSetup(env.settings, env.security) ? 'light' : null;
              return 'next';
            }
          },
          {
            id: 'full', icon: '📺', title: t('wizard.mode.full'), tag: t('wizard.mode.fullTag'), current: full, chosen: page.intent === 'full',
            bullets: [t('wizard.mode.full1'), t('wizard.mode.full2'), t('wizard.mode.full3')],
            foot: ready ? t('wizard.mode.fullReady') : t('wizard.mode.fullAdmin'),
            footIcon: ready ? null : 'shield',
            run: () => {
              // Only the choice: the console setup (with its UAC prompts and its restart offer) runs at the very end, so a restart
              // cannot cut the assistant short.
              page.intent = ready ? null : 'full';
              return 'next';
            }
          }
        ];
      }
    },
    look: {
      icon: 'appearance',
      title: () => t('wizard.look.title'),
      lead: () => t('wizard.look.lead'),
      later: () => where('settings.cat.appearance'),
      leave: () => page.revertSkin(),
      options: () => [
        ...state.skins.map((skin) => ({
          id: `skin:${skin.id}`, title: skin.name, icon: '🎨', bullets: [skin.description].filter(Boolean), current: skin.id === page.savedSkin.id, compact: true,
          focus: () => state.skin !== skin && applySkin(skin),
          run: async () => {
            applySkin(skin);
            if (!(await call(() => api.setSkin(skin.id))).ok) return;
            page.savedSkin = skin;
            return 'next';
          }
        })),
        ...['skin', 'system'].map((mode) => ({
          id: `pointer:${mode}`, row: 1, chip: true, label: t('settings.pointer.title'),
          title: t(mode === 'system' ? 'settings.pointer.system' : 'settings.pointer.skin'), current: pointerMode(env.settings) === mode,
          run: async () => {
            if (!(await save('pointer', mode))) return;
            env.settings.pointer = mode;
            state.info.pointer = mode;
            setPointer(mode, env.settings.mouseUnlocked);
          }
        }))
      ]
    },
    sound: {
      icon: 'audio',
      title: () => t('wizard.sound.title'),
      lead: () => t('wizard.sound.lead'),
      later: () => where('settings.cat.audio'),
      options: () => [
        { id: 'off', icon: '🔇', title: t('wizard.sound.off'), bullets: [t('wizard.sound.off1'), t('wizard.sound.off2')] },
        { id: 'ui', icon: '🔊', title: t('wizard.sound.ui'), bullets: [t('wizard.sound.ui1'), t('wizard.sound.ui2')] },
        { id: 'music', icon: '♪', title: t('wizard.sound.music'), bullets: [t('wizard.sound.music1'), t('wizard.sound.music2'), t('wizard.sound.music3')] }
      ].map((o) => ({
        ...o, current: audioMode(env.settings) === o.id,
        run: async () => {
          if (!(await save('audioMode', o.id))) return;
          env.settings.audioMode = o.id;
          state.info.audioMode = o.id;
          setAudioMode(o.id);
          return 'next';
        }
      }))
    },
    clients: {
      glyph: '🎮',
      title: () => t('wizard.clients.title'),
      lead: () => t('wizard.clients.lead'),
      later: () => where('settings.cat.library', 'settings.library.clients'),
      // Only what the settings offer: one mode per client (Settings › Library › game clients). The assistant switches
      // between "keep ready" and "on demand"; "off" stays a choice of the settings page.
      options: () => [
        ...env.launchers.map((l, i) => ({
          id: `client:${l.id}`, row: i, line: 0, listRow: true, title: l.name, icon: '🎮',
          pill: t(clientModeKey(l.mode)), pillOn: l.mode === 'standby',
          run: async () => {
            const next = l.mode === 'standby' ? 'demand' : 'standby';
            const result = await call(() => api.setLauncherMode(l.id, next));
            if (result.ok && Array.isArray(result.value)) env.launchers = result.value;
            else if (result.ok) l.mode = next;
          }
        })),
        { id: 'continue', row: env.launchers.length, line: 1, button: true, title: t('wizard.next'), run: () => 'next' }
      ]
    },
    casehud: {
      glyph: '▣',
      title: () => t('wizard.casehud.title'),
      lead: () => t('wizard.casehud.lead'),
      // Running with the API off: the same hint the CaseHUD page shows.
      note: () => (state.caseHud?.state === 'apiOff' ? t('casehud.api.hint') : null),
      later: () => where('settings.cat.casehud'),
      // CaseHUD ships with its API off and it is the user's choice: switching it on asks for the code (couch lock) and says what it does.
      options: () => [
        {
          id: 'on', icon: '⇄', title: t('wizard.casehud.on'), bullets: [t('wizard.casehud.on1'), t('wizard.casehud.on2')],
          run: async () => ((await setCaseHudApi(true)) ? 'next' : undefined)
        },
        { id: 'later', icon: '⏲', title: t('wizard.casehud.later'), bullets: [t('wizard.casehud.later1'), t('wizard.casehud.later2')], run: () => 'next' }
      ]
    },
    lock: {
      icon: 'shield',
      title: () => t('wizard.lock.title'),
      lead: () => t('wizard.lock.lead'),
      // The CouchHUD service holds codes the settings file does not know (an earlier installation, or someone set one): say so.
      note: () => {
        const held = page.env?.security?.serviceCodes;
        const conflict = page.env?.security?.codeConflict;
        return conflict || (held && (held.couch || held.parent) && !page.env?.security?.hasPasscode) ? t('wizard.lock.serviceCodes') : null;
      },
      later: () => where('settings.cat.console', 'settings.lock.title'),
      options: () => [
        {
          id: 'code', icon: '🔒', title: t('wizard.lock.code'), bullets: [t('wizard.lock.code1'), t('wizard.lock.code2'), t('wizard.lock.code3')],
          run: async () => {
            // The enrollment of Settings › Console › Couch lock, with "controller code" already picked.
            const on = await enrollCouchCode();
            await page.reload();
            return on ? 'next' : undefined;
          }
        },
        { id: 'later', icon: '⏲', title: t('wizard.lock.later'), bullets: [t('wizard.lock.later1'), t('wizard.lock.later2')], run: () => 'next' }
      ]
    },
    done: {
      glyph: '✦',
      title: () => t('wizard.done.title'),
      lead: () => t('wizard.done.lead'),
      later: () => t('wizard.done.later'),
      summary: true,
      note: () => (page.intent === 'full' ? t('wizard.done.noteFull') : page.intent === 'light' ? t('wizard.done.noteLight') : null),
      options: () => [{ id: 'go', button: true, primary: true, title: t('wizard.done.go'), run: () => page.finish({ runIntent: true }) }]
    }
  };
}

// ---------------------------------------------------------------------------
// Page

function wizardPage({ firstRun = false, startAt = null } = {}) {
  const page = {
    id: 'setup-wizard',
    name: t('wizard.name'),
    // First start: host navigation must not skip it, and every button belongs to it (like the first-start notice).
    sticky: firstRun,
    captures: (action) => firstRun || action !== 'guide',
    firstRun,
    withTerms: firstRun && !termsAccepted(),
    step: startAt || 'welcome',
    index: 0,
    env: { settings: {}, security: {}, launchers: [] },
    loaded: false,
    awaiting: null,
    busy: false,
    // What the mode question decided: 'full' (console setup) or 'light' (way back), run after the last question; null: nothing.
    intent: null,
    // The areas as chosen in the areas step (saved with its "Next").
    areas: enabledAreas(),
    // No notification cards over the questions (views/notifications.js).
    quietNotifications: true,
    savedSkin: state.skin,

    /** The steps in order, as far as they apply right now. */
    order() {
      // Full as chosen here (set up at the end) or as it is now.
      const full = this.intent === 'full' || (isFull(this.env) && this.intent !== 'light');
      return shownSteps([
        'welcome',
        this.withTerms ? 'terms' : null,
        // Which parts of CouchHUD are wanted, before anything that belongs to one of them (the game clients need Gaming).
        'areas',
        'mode',
        'look',
        'sound',
        full && this.env.launchers.length ? 'clients' : null,
        full && caseHudWantsApi() ? 'casehud' : null,
        // The console setup enrolls the code itself; this step is only for an install already in Full without a lock.
        full && !this.intent && !lockOn(this.env) ? 'lock' : null,
        'done'
      ].filter(Boolean), this.areas);
    },

    /**
     * The areas step's "Next": saves the choice when it changed. A settings change like Settings › Console › Areas: with parental
     * controls on the parents' code (the host checks it again, and refuses on a locked couch: the unlock comes first, as there).
     */
    async saveAreas() {
      if (this.areas.join() === enabledAreas().join()) return 'next';
      const reason = t('settings.areas.reason');
      if (!(await admitProtectedChange({ reason, parentalOn: !!parental.status?.enabled, scope: { action: 'areas' }, ensureUnlocked, ensureParentGrant }))) return;
      const result = await call(() => api.setFeatureAreas(this.areas), t('settings.areas.failed'));
      if (!result.ok) return;
      this.areas = setAreas(result.value?.areas ?? this.areas);
      window.dispatchEvent(new CustomEvent('couchhud:areas', { detail: this.areas }));
      // The game clients appear (or go) with Gaming.
      await this.reload();
      return 'next';
    },
    nextOf(id) {
      const order = this.order();
      let i = order.indexOf(id) + 1;
      // The notice counts once: accepted, it is no step any more.
      while (order[i] === 'terms' && termsAccepted()) i++;
      return order[i] ?? 'done';
    },
    prevOf(id) {
      const order = this.order();
      let i = order.indexOf(id) - 1;
      while (order[i] === 'terms' && termsAccepted()) i--;
      return i >= 0 ? order[i] : null;
    },

    async reload() {
      this.env = await loadEnv();
      this.areas = setAreas(this.env.settings.areas ?? enabledAreas());
      this.loaded = true;
    },

    def() {
      return steps(this)[this.step];
    },

    go(step, { back = false } = {}) {
      this.def()?.leave?.();
      this.step = step;
      this.index = -1;
      if (back) sound.back();
      if (this.def().enter) return this.def().enter();
      this.paint();
    },

    openTerms() {
      if (termsAccepted()) return this.go(this.nextOf('terms'));
      this.awaiting = 'terms';
      // B on the notice goes back to the language; declining stays on the menu button, as before.
      const onBack = () => {
        this.awaiting = null;
        this.step = 'welcome';
        this.index = -1;
        render();
      };
      if (!state.stack.some((p) => p.id === 'terms')) state.stack.push(termsPage({ onBack }));
      render();
    },

    /** Back from the notice: accepted, on to the next question. */
    back() {
      this.awaiting = null;
      if (termsAccepted()) this.go(this.nextOf('terms'));
    },

    revertSkin() {
      if (state.skin !== this.savedSkin) applySkin(this.savedSkin);
    },

    /** Ends the assistant. runIntent: "Let's go" also starts what the mode question decided; ≡ (skip the rest) does not. */
    async finish({ runIntent = false } = {}) {
      this.revertSkin();
      // Marked before any flow starts: a restart offered by the console setup must not bring the assistant back.
      // The marker means nothing for security: if it cannot be saved (a toast said so), the assistant still closes.
      if (await save('setupWizardDone', WIZARD_VERSION)) state.info.setupWizardDone = WIZARD_VERSION;
      removePage(this);
      try { sessionStorage.removeItem(RESUME_KEY); } catch { /* storage blocked */ }
      if (this.firstRun) state.tab = 0;
      sound.select();
      const intent = runIntent && termsAccepted() ? this.intent : null;
      if (!intent) {
        if (this.firstRun) toast(t('wizard.done.toast'), 4000);
        render();
        // Skipped before the notice: it still comes, as before.
        showTermsIfNeeded();
        return;
      }
      // The same flows as Settings › Console, unchanged; afterwards a short note if they did not get everything done.
      const flow = intent === 'full' ? consoleSetupPage(consoleDeps) : consoleRevertPage(consoleDeps, !!this.env.settings.isShell);
      const close = flow.onClose?.bind(flow);
      flow.onClose = () => {
        close?.();
        checkIntent(intent).catch(console.error);
      };
      push(flow);
    },

    build(root) {
      this.root = root;
      if (this.awaiting) {
        this.paint();
        this.back();
        return;
      }
      if (this.step === 'terms' && termsAccepted()) {
        this.step = this.nextOf('terms');
        this.index = -1;
      }
      if (!this.loaded) {
        this.reload().then(() => this.def()?.enter ? this.def().enter() : this.paint()).catch(console.error);
      }
      if (this.loaded && this.def()?.enter) return this.def().enter();
      this.paint();
    },

    paint() {
      if (!this.root?.isConnected || state.stack.at(-1) !== this) return;
      const def = this.def();
      if (!def || def.enter) return;
      this.options = this.loaded ? def.options() : [];
      if (this.index < 0 || this.index >= this.options.length) {
        const chosen = this.options.findIndex((o) => o.chosen);
        const current = this.options.findIndex((o) => o.current && !o.chip);
        this.index = Math.max(0, chosen >= 0 ? chosen : current);
      }
      const order = this.order().filter((s) => s !== 'terms' || this.withTerms);
      const at = order.indexOf(this.step);
      const dots = el('ol', { className: 'wizard-dots', ariaLabel: t('wizard.progress', { n: at + 1, total: order.length }) },
        order.map((s, i) => el('li', { className: i < at ? 'done' : i === at ? 'active' : '' })));

      this.nodes = this.options.map((o) => optionNode(o));
      // `row` is for the cursor (up/down), `line` for the layout: the client rows are one list, one cursor row each.
      const groups = [];
      this.options.forEach((o, i) => {
        const line = o.line ?? o.row ?? 0;
        (groups[line] ??= []).push(this.nodes[i]);
      });
      const lines = groups.filter(Boolean).map((nodes) => {
        const first = this.options[this.nodes.indexOf(nodes[0])];
        const kind = first.listRow || first.button ? 'list' : first.chip ? 'chips' : 'cards';
        return el('div', { className: `wizard-line wizard-${kind}` },
          first.chip ? el('span', { className: 'wizard-line-label', textContent: first.label }) : null,
          el('ul', { className: 'wizard-options' }, nodes));
      });

      this.root.replaceChildren(el('div', { className: `wizard wizard-step-${this.step}` },
        dots,
        el('header', { className: 'wizard-head' },
          def.hero
            ? el('div', { className: 'wizard-logo' }, el('img', { src: 'assets/icon.svg', alt: '' }), el('span', { className: 'wizard-brand' }, 'Couch', el('b', { textContent: 'HUD' })))
            : el('span', { className: 'wizard-icon' }, def.glyph ? iconEl(def.glyph) : categoryIcon(def.icon)),
          el('h2', { textContent: def.title() }),
          el('p', { className: 'wizard-lead', textContent: def.lead() })),
        def.summary ? this.summary() : null,
        el('div', { className: 'wizard-body' }, this.loaded ? lines : el('p', { className: 'wizard-lead', textContent: t('wizard.loading') })),
        def.note?.() ? el('p', { className: 'wizard-note' }, categoryIcon('shield'), def.note()) : null,
        el('p', { className: 'wizard-later' }, def.summary ? def.later() : t('wizard.later', { where: def.later() }))));
      this.update();
    },

    summary() {
      const env = this.env;
      const clients = env.launchers.filter((l) => l.mode === 'standby').length;
      // As chosen: Full that is still to be set up counts as Full, the way back as Light.
      const full = this.intent === 'full' || (isFull(env) && this.intent !== 'light');
      const lock = this.intent === 'full' && !lockOn(env)
        ? ['🔒', t('wizard.done.lockSetup')]
        : [lockOn(env) ? '🔒' : '🔓', lockOn(env) ? t('wizard.done.lockOn') : t('wizard.done.lockOff')];
      const chips = [
        ['🌐', lang() === 'en' ? 'English' : 'Deutsch'],
        [full ? '📺' : '▭', full ? t('wizard.mode.full') : t('wizard.mode.light')],
        ['🎨', this.savedSkin.name],
        ['🔊', t(soundKey(audioMode(env.settings)))],
        ['▦', this.areas.map((a) => t(AREA_NAME_KEYS[a])).join(', ')],
        ...(full && env.launchers.length ? [['🎮', t('wizard.done.clients', { count: clients })]] : []),
        ...(full ? [lock] : [])
      ];
      return el('ul', { className: 'wizard-summary' }, chips.map(([icon, text]) => el('li', { className: 'wizard-chip' }, el('span', { className: 'wizard-chip-icon' }, iconEl(icon)), text)));
    },

    update() {
      setFocused(this.nodes ?? [], this.busy ? -1 : this.index);
      // The skin on screen stays marked while the cursor is on the pointer chips.
      (this.options ?? []).forEach((o, i) => this.nodes[i]?.classList.toggle('previewing', o.id === `skin:${state.skin.id}` && i !== this.index));
      renderHints();
    },

    async choose() {
      const option = this.options?.[this.index];
      if (!option || this.busy) return sound.bump();
      sound.select();
      this.busy = true;
      let result;
      try {
        result = await option.run();
      } finally {
        this.busy = false;
      }
      if (state.stack.at(-1) !== this) return;
      if (result === 'next') return this.go(this.nextOf(this.step));
      this.paint();
    },

    moveFocus(action) {
      const options = this.options ?? [];
      const here = options[this.index];
      if (!here) return sound.bump();
      const row = here.row ?? 0;
      let next = -1;
      if (action === 'left' || action === 'right') {
        const same = options.map((o, i) => [o, i]).filter(([o]) => (o.row ?? 0) === row).map(([, i]) => i);
        const at = same.indexOf(this.index) + (action === 'left' ? -1 : 1);
        next = same[at] ?? -1;
      } else {
        const rowsSorted = [...new Set(options.map((o) => o.row ?? 0))].sort((a, b) => a - b);
        const target = rowsSorted[rowsSorted.indexOf(row) + (action === 'up' ? -1 : 1)];
        if (target !== undefined) {
          const inRow = options.map((o, i) => [o, i]).filter(([o]) => (o.row ?? 0) === target).map(([, i]) => i);
          // Into the skins: back to the one being previewed; elsewhere the nearest by position.
          const previewed = inRow.find((i) => options[i].id === `skin:${state.skin.id}`);
          const column = options.filter((o, i) => (o.row ?? 0) === row && i < this.index).length;
          next = previewed ?? inRow[Math.min(column, inRow.length - 1)];
        }
      }
      if (next < 0 || next === undefined) return sound.bump();
      this.index = next;
      sound.move();
      options[next].focus?.();
      this.update();
    },

    async handle(action) {
      if (this.busy) return;
      switch (action) {
        case 'up':
        case 'down':
        case 'left':
        case 'right':
          return this.moveFocus(action);
        case 'a':
          return this.choose();
        case 'y':
          // "Later": nothing changes, on to the next question.
          if (this.step === 'done') return this.choose();
          sound.select();
          return this.go(this.nextOf(this.step));
        case 'b': {
          const prev = this.prevOf(this.step);
          if (prev) return this.go(prev, { back: true });
          if (this.firstRun) return sound.bump();
          this.revertSkin();
          removePage(this);
          sound.back();
          return render();
        }
        case 'start':
          // Skip the rest: everything stays as it is, the settings have it all.
          return this.finish();
      }
    },

    pointer(node, index, dispatch) {
      const i = this.nodes?.indexOf(node) ?? -1;
      if (i < 0) return;
      this.index = i;
      this.options[i].focus?.();
      this.update();
      dispatch('a');
    },

    hints() {
      const option = this.options?.[this.index];
      const verb = option?.listRow ? t('wizard.hint.toggle') : option?.chip ? t('wizard.hint.apply') : t('wizard.hint.choose');
      return [
        ['A', verb],
        ...(this.prevOf(this.step) || !this.firstRun ? [['B', t('wizard.hint.back')]] : []),
        ...(this.step !== 'done' ? [['Y', t('wizard.hint.later')]] : []),
        ['≡', t('wizard.hint.exit')]
      ];
    }
  };
  return page;
}

function optionNode(o) {
  if (o.chip) {
    return el('li', { className: `wizard-option wizard-chip-option${o.current ? ' current' : ''}` },
      o.current ? el('span', { className: 'wizard-check', textContent: '✓' }) : null, o.title);
  }
  if (o.button) {
    return el('li', { className: `wizard-option wizard-button${o.primary ? ' primary' : ''}` }, el('strong', { textContent: o.title }));
  }
  if (o.listRow) {
    return el('li', { className: 'wizard-option wizard-row' },
      el('span', { className: 'wizard-row-icon' }, iconEl(o.icon)),
      o.detail ? el('span', { className: 'wizard-row-text' }, el('strong', { textContent: o.title }), el('small', { textContent: o.detail })) : el('strong', { textContent: o.title }),
      el('span', { className: `pill ${o.pillOn ? 'on' : 'off'}`, textContent: o.pill }));
  }
  return el('li', { className: `wizard-option wizard-card${o.current ? ' current' : ''}${o.compact ? ' compact' : ''}` },
    el('div', { className: 'wizard-card-top' },
      el('span', { className: 'wizard-card-icon' }, iconEl(o.icon)),
      o.current ? el('span', { className: 'wizard-badge', textContent: t('wizard.current') }) : null),
    el('strong', { className: 'wizard-card-title', textContent: o.title }),
    o.tag ? el('span', { className: 'wizard-card-tag', textContent: o.tag }) : null,
    o.bullets?.length ? el('ul', { className: 'wizard-bullets' }, o.bullets.slice(0, 3).map((b) => el('li', { textContent: b }))) : null,
    o.foot ? el('p', { className: 'wizard-card-foot' }, o.footIcon ? categoryIcon(o.footIcon) : null, o.foot) : null);
}

/** After the console setup (or the way back) the assistant started: a short note when it did not get everything done. */
async function checkIntent(intent) {
  const env = await loadEnv();
  const done = intent === 'full' ? isConsoleReady(env.settings, env.security) : !hasConsoleSetup(env.settings, env.security);
  if (!done) toast(intent === 'full' ? t('wizard.notice.full') : t('wizard.notice.light'), 9000);
}

// ---------------------------------------------------------------------------
// Entry points

const STEP_IDS = ['welcome', 'terms', 'areas', 'mode', 'look', 'sound', 'clients', 'casehud', 'lock', 'done'];

/** Locked, or not known: the start-up decision fails closed (isLocked() answers "open" when the read fails, for its other callers). */
async function lockedOrUnknown() {
  try {
    const security = await api.getSecurity();
    return !security || security.unlocked === false;
  } catch {
    return true;
  }
}

function takeResume() {
  try {
    const raw = sessionStorage.getItem(RESUME_KEY);
    sessionStorage.removeItem(RESUME_KEY);
    const resume = raw ? JSON.parse(raw) : null;
    // Only what the assistant itself writes: a known step and a yes/no. Anything else means no resume.
    if (!resume || typeof resume !== 'object' || !STEP_IDS.includes(resume.step) || typeof resume.firstRun !== 'boolean') return null;
    return { step: resume.step, firstRun: resume.firstRun };
  } catch {
    return null;
  }
}

/** Settings › Console: the same release as the settings it changes; a locked couch does not get in. */
export async function openSetupWizard() {
  // Asked again: what the page read at its start may be old.
  state.info.configFault = await configFaultNow(state.info.configFault, () => api.getSecurity());
  if (state.info.configFault) return toast(t('wizard.configFault'), 7000);
  if (!(await ensureUnlocked(t('settings.console.wizard')))) return;
  push(wizardPage({ firstRun: false }));
}

/**
 * At start: a new install gets the assistant (with the notice as its second step). An install that accepted the notice before the
 * assistant existed is only asked once whether it wants it. A locked couch gets neither: only the notice, as before.
 * Settings that are broken or gone after an earlier install (the host's stand-in, `configFault`) are no first start: nothing at all, the
 * host's notice says what is wrong (the assistant could set a lock and codes over settings that cannot be saved).
 */
export async function startFirstRun() {
  const resume = takeResume();
  if (state.info.configFault) return;
  const fresh = !((state.info.acceptedTerms ?? 0) > 0);
  const done = (state.info.setupWizardDone ?? 0) >= WIZARD_VERSION;
  const locked = await lockedOrUnknown();
  if (!locked && (resume || (fresh && !done))) {
    const firstRun = resume ? !!resume.firstRun || !termsAccepted() : true;
    const page = wizardPage({ firstRun, startAt: resume?.step });
    state.stack.push(page);
    return render();
  }
  showTermsIfNeeded();
  if (done || fresh || locked || !termsAccepted()) return;
  // Let the boot screen go and the HUD settle; never on top of something the user already opened.
  await new Promise((resolve) => setTimeout(resolve, 3000));
  if (state.overlay || state.stack.length) return;
  const start = await confirmDialog(t('wizard.offer.title'), t('wizard.offer.start'), { message: t('wizard.offer.text'), icon: '✦', cancelLabel: t('wizard.offer.later'), cancelIcon: '⏲' });
  // Asked once: whatever the answer, the entry under Settings › Console stays.
  const saved = await attempt(() => api.setSetting('setupWizardDone', WIZARD_VERSION), undefined, { quietLocked: true });
  if (saved !== undefined) state.info.setupWizardDone = WIZARD_VERSION;
  if (start) await openSetupWizard();
}
