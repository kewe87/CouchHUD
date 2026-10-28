// First-run setup assistant: a few questions, one per screen, big cards instead of prose.
//
// It is only a guided front end. Every choice goes through the setting or flow it belongs to, the same call the settings make:
// the language and sound settings, the skin, the pointer, the game-client modes, the one-button console setup (and its way back)
// and the couch-lock enrollment from Settings › Console. Nothing here sets anything up by itself, and nothing is stored twice.
// The only own value is `setupWizardDone`, a version marker like `acceptedTerms`, so the assistant shows once.
//
// Everything that changes the PC (shell mode, the way back, the lock, automatic sign-in, Steam and the game clients, services,
// the TV) is explained first: a page with what it means, what it changes, the risks and the way back, and only then the
// choice. The choice that changes nothing is focused first. Nothing of that is done while asking: the choices are collected, the
// summary lists them, and only "Apply" does them (the console setup runs then, without the parts that were declined). Looks, sound,
// language and areas are harmless settings and take effect at once, as before.
//
// The mode question only records the choice: the console setup (or the way back) starts after the last question, with
// "Apply", once the marker is written, so its restart offer cannot cut the assistant short or bring it back.
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
import { consoleRevertPage, consoleSetupPage, hasConsoleSetup } from './console-setup.js';
import { consoleDeps, enrollCouchCode } from './settings.js';
import { AREAS, AREA_DESC_KEYS, AREA_ICONS, AREA_NAME_KEYS, areaOn, enabledAreas, setAreas, toggleArea } from '../core/areas.js';
import { ensureParentGrant, parental } from '../components/parental.js';
import { EXPLAINS, applies, parseResume, defaultIndex, freshChoices, isConsoleReady, isFullNow, nextOf, order, prevOf, skippedSetupSteps, summaryFocus, summaryPlan } from '../core/wizard-rules.js';

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
/** The summary line of a change (wizard.sum.<id>, listed in core/wizard-rules.js). */
const sumText = (id) => { const key = `wizard.sum.${id}`; return t(key); };
/** One result line: what, how it went and, when it did not, where to catch up. */
const resultLine = (r) => {
  const key = `wizard.result.${r.outcome}`; // i18n-used: wizard.result.ok wizard.result.failed wizard.result.skipped
  const mark = r.outcome === 'ok' ? '✓' : r.outcome === 'skipped' ? '–' : '✕';
  return `${mark} ${r.label ?? sumText(r.id)} – ${t(key)}${r.outcome === 'ok' ? '' : `. ${t('wizard.result.retry', { where: r.where })}`}`;
};
/** A TV is paired: the assistant may offer "TV follows CouchHUD". */
const tvPaired = () => !!(api.tvStatus && state.tv?.paired);

// ---------------------------------------------------------------------------
// Steps

/**
 * An explanation page: the texts are wizard.x.<id>.* (title, what it means, what it changes, risks, the way back, expert details)
 * and the choices are buttons in a row. `safe` marks the one that changes nothing: it is focused first, and Y picks it.
 */
function explainStep(id, { later, note = () => null, choices }) {
  const k = (field) => `wizard.x.${id}.${field}`;
  return {
    explain: id,
    layout: 'explain',
    icon: EXPLAINS[id].icon,
    title: () => t(k('title')),
    later,
    note,
    options: () => choices(k).map(([name, safe, run, label = name, sub = `${label}Sub`]) => ({
      id: name, button: true, action: true, safe, title: t(k(label)), sub: t(k(sub)), run
    }))
  };
}

/**
 * A step: title, lead, where it lives in the settings, and its options. An option is a card (icon, short title, up to three
 * bullets), a chip or a row; `row` puts it on a line of its own (up/down between lines). run() returns 'next' to go on.
 */
function steps(page) {
  const env = page.env;
  const ch = page.choices;
  /** Sets one decision and goes on. */
  const decide = (change) => () => {
    change();
    return 'next';
  };
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
        const ready = isConsoleReady(env.settings, env.security, { autologon: !env.settings.autoLogonDeclined });
        return [
          {
            id: 'light', icon: '▭', title: t('wizard.mode.light'), tag: t('wizard.mode.lightTag'), current: !full, chosen: page.intent === 'light',
            bullets: [t('wizard.mode.light1'), t('wizard.mode.light2'), t('wizard.mode.light3')],
            foot: hasConsoleSetup(env.settings, env.security) ? t('wizard.mode.lightRevert') : null,
            run: () => {
              // Only the choice: the way back from Settings › Console runs after the last question ("Apply"), once explained.
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
    // Shell mode: the explanation comes before anything is set up. "Stay in App mode" drops the choice again.
    shell: explainStep('shell', {
      later: () => where('settings.cat.console', 'settings.mode.title'),
      choices: () => [
        ['safe', true, decide(() => { page.intent = null; })],
        ['go', false, () => 'next']
      ]
    }),
    revert: explainStep('revert', {
      later: () => where('settings.cat.console', 'settings.mode.title'),
      choices: () => [
        ['safe', true, decide(() => { page.intent = null; })],
        ['go', false, () => 'next']
      ]
    }),
    // The lock: needed for the shell (then the code is asked for during the setup) or optional for a shell that has none yet.
    lock: explainStep('lock', {
      // The CouchHUD service holds codes the settings file does not know (an earlier installation, or someone set one): say so.
      note: () => {
        const held = page.env?.security?.serviceCodes;
        const conflict = page.env?.security?.codeConflict;
        if (conflict || (held && (held.couch || held.parent) && !page.env?.security?.hasPasscode)) return t('wizard.lock.serviceCodes');
        return page.intent === 'full' ? t('wizard.x.lock.needed') : null;
      },
      later: () => where('settings.cat.console', 'settings.lock.title'),
      choices: () => (page.intent === 'full'
        ? [
          ['safe', true, decide(() => { page.intent = null; }), 'stay', 'staySub'],
          ['go', false, decide(() => { ch.lock = true; }), 'setup', 'setupSub']
        ]
        : [
          ['safe', true, decide(() => { ch.lock = false; })],
          ['go', false, decide(() => { ch.lock = true; })]
        ])
    }),
    autologon: explainStep('autologon', {
      later: () => where('settings.cat.console', 'settings.mode.title'),
      choices: () => [
        ['safe', true, decide(() => { ch.autologon = false; })],
        ['go', false, decide(() => { ch.autologon = true; })]
      ]
    }),
    launchers: explainStep('launchers', {
      later: () => where('settings.cat.startup'),
      choices: () => [
        ['safe', true, decide(() => { ch.launchers = false; })],
        ['go', false, decide(() => { ch.launchers = true; })]
      ]
    }),
    services: explainStep('services', {
      later: () => where('settings.cat.startup'),
      choices: () => [
        ['safe', true, decide(() => { ch.services = false; })],
        ['go', false, decide(() => { ch.services = true; })]
      ]
    }),
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
    clientsInfo: explainStep('clientsInfo', {
      later: () => where('settings.cat.library'),
      choices: () => [
        ['safe', true, decide(() => { ch.clients = 'keep'; ch.clientModes = {}; })],
        ['go', false, decide(() => { ch.clients = 'adjust'; })]
      ]
    }),
    clients: {
      glyph: '🎮',
      title: () => t('wizard.clients.title'),
      lead: () => t('wizard.clients.lead'),
      later: () => where('settings.cat.library', 'settings.library.clients'),
      // Only what the settings offer: one mode per client (Settings › Library › game clients). The assistant switches
      // between "keep ready" and "on demand"; "off" stays a choice of the settings page. The choice is only noted: "Apply" saves it.
      options: () => [
        ...env.launchers.map((l, i) => {
          const mode = ch.clientModes[l.id] ?? l.mode;
          return {
            id: `client:${l.id}`, row: i, line: 0, listRow: true, title: l.name, icon: '🎮',
            pill: t(clientModeKey(mode)), pillOn: mode === 'standby',
            run: () => {
              const next = mode === 'standby' ? 'demand' : 'standby';
              if (next === l.mode) delete ch.clientModes[l.id];
              else ch.clientModes[l.id] = next;
            }
          };
        }),
        { id: 'continue', row: env.launchers.length, line: 1, button: true, title: t('wizard.next'), run: () => 'next' }
      ]
    },
    tv: explainStep('tv', {
      later: () => where('settings.cat.tv'),
      choices: () => [
        ['safe', true, decide(() => { ch.tv = 'off'; })],
        ['go', false, decide(() => { ch.tv = 'on'; })],
        ['goBoth', false, decide(() => { ch.tv = 'both'; }), 'goBoth', 'goBothSub']
      ]
    }),
    summary: {
      layout: 'summary',
      glyph: '✦',
      title: () => t('wizard.sum.title'),
      lead: () => t('wizard.sum.lead'),
      later: () => t('wizard.sum.later'),
      options: () => {
        const plan = summaryPlan(page.ctx());
        const back = summaryFocus(plan) === 'back';
        return [
          { id: 'apply', button: true, action: true, primary: true, safe: !back, title: t('wizard.sum.apply'), sub: t('wizard.sum.applySub'), run: () => page.apply() },
          { id: 'back', button: true, action: true, safe: back, title: t('wizard.sum.back'), sub: t('wizard.sum.backSub'), run: () => page.go(page.prevOf('summary') ?? 'welcome', { back: true }) }
        ];
      }
    },
    result: {
      layout: 'result',
      glyph: '✦',
      title: () => t('wizard.result.title'),
      later: () => t('wizard.sum.later'),
      options: () => [{ id: 'finish', button: true, action: true, primary: true, safe: true, title: t('wizard.result.finish'), run: () => page.finish() }]
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
    // What the explanation pages decided; nothing of it is done before "Apply".
    choices: freshChoices(),
    // The technical details of an explanation page (expert mode only).
    details: false,
    // What "Apply" did, for the result page: [{ id, ok }].
    results: [],
    // The areas as chosen in the areas step (saved with its "Next").
    areas: enabledAreas(),
    // No notification cards over the questions (views/notifications.js).
    quietNotifications: true,
    savedSkin: state.skin,

    /** What the step rules (core/wizard-rules.js) need to know. */
    ctx() {
      const env = this.env;
      return {
        withTerms: this.withTerms,
        termsAccepted: termsAccepted(),
        intent: this.intent,
        full: isFull(env),
        lockOn: lockOn(env),
        accountPassword: env.security.accountPassword,
        autoLogonDone: !!env.security.autoLogon && env.security.autoLogonState === 'couchhud' && !env.security.autoLogonOtherUser,
        launchers: env.launchers.length,
        tvPaired: tvPaired(),
        choices: this.choices,
        areas: this.areas
      };
    },

    /** The steps in order, as far as they apply right now. */
    order() {
      return order(this.ctx());
    },

    expert() {
      return !!this.env.settings.expertMode;
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
      return nextOf(id, this.ctx());
    },
    prevOf(id) {
      return prevOf(id, this.ctx());
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

    /**
     * The summary's "Apply": the choices that are no flow of their own (the code of the lock, the clients, the TV) are done
     * first, then the marker is written, then the console setup (or its way back) starts without the parts that were declined and
     * is the result page. Without such a flow the assistant's own result page follows.
     */
    async apply() {
      const plan = summaryPlan(this.ctx());
      const results = [];
      // ok, skipped (the code or a confirmation was declined: nothing happened) or failed; each with where to catch up.
      const run = async (id, work, extra = {}) => {
        let outcome = 'failed';
        try {
          const r = await work();
          outcome = r === true || r === 'ok' ? 'ok' : r === 'cancelled' ? 'skipped' : 'failed';
        } catch (err) {
          console.error(err);
        }
        results.push({ id, outcome, ...extra });
      };
      if (plan.changes.includes('lockNow')) await run('lockNow', () => enrollCouchCode(), { where: where('settings.cat.console', 'settings.lock.title') });
      if (plan.changes.includes('clients')) {
        // One line per client: when only some fail, the user sees which.
        for (const [id, mode] of Object.entries(this.choices.clientModes)) {
          const name = this.env.launchers.find((l) => l.id === id)?.name ?? id;
          await run('client', async () => (await call(() => api.setLauncherMode(id, mode))).ok, { label: t('wizard.result.client', { name }), where: where('settings.cat.library') });
        }
      }
      // The explanation and the summary were the confirmation; the unlock and the host's checks stay.
      if (plan.changes.includes('tvOn') || plan.changes.includes('tvBoth')) {
        await run(plan.changes.includes('tvBoth') ? 'tvBoth' : 'tvOn', async () => {
          const on = await save('tvOnWithCouchHud', true);
          return plan.changes.includes('tvBoth') ? (await save('tvOffWithPc', true)) && on : on;
        }, { where: where('settings.cat.tv') });
      }
      // What the mode question counts as "set up" (the card says "Already set up" without a sign-in that was declined).
      if (this.intent === 'full' && applies('autologon', this.ctx())) await save('autoLogonDeclined', !this.choices.autologon);
      this.results = results;
      if (this.intent && termsAccepted()) return this.finish({ runIntent: true });
      // The marker means nothing for security: if it cannot be saved (a toast said so), the assistant goes on.
      if (await save('setupWizardDone', WIZARD_VERSION)) state.info.setupWizardDone = WIZARD_VERSION;
      this.go('result');
    },

    /** Ends the assistant. runIntent: "Apply" also starts what the mode question decided; ≡ (skip the rest) does not. */
    async finish({ runIntent = false } = {}) {
      this.revertSkin();
      const intent = runIntent && termsAccepted() ? this.intent : null;
      // The marker means nothing for security: if it cannot be saved (a toast said so), the assistant still closes.
      const mark = async () => { if (await save('setupWizardDone', WIZARD_VERSION)) state.info.setupWizardDone = WIZARD_VERSION; };
      // Without a flow now; with one only once it has really run (a flow that never starts leaves the assistant to be offered again),
      // but before its restart offer can be used: a restart must not bring the assistant back.
      if (!intent) await mark();
      removePage(this);
      try { sessionStorage.removeItem(RESUME_KEY); } catch { /* storage blocked */ }
      if (this.firstRun) state.tab = 0;
      sound.select();
      if (!intent) {
        if (this.firstRun) toast(t('wizard.done.toast'), 4000);
        render();
        // Skipped before the notice: it still comes, as before.
        showTermsIfNeeded();
        return;
      }
      // The same flows as Settings › Console, unchanged apart from the parts that were declined, started right away (this assistant's
      // summary was the confirmation; the unlock and the administrator prompts stay). Its last page is the result: what was done, what
      // failed (also what the assistant did before it, by name), the way back. Afterwards a short note if it did not get everything done.
      const skip = skippedSetupSteps(this.choices);
      const carried = this.results.filter((r) => r.outcome !== 'ok').map(resultLine);
      const options = { carried, autoStart: true, onDone: mark };
      const flow = intent === 'full'
        ? consoleSetupPage(consoleDeps, { ...options, skip, pauseServices: !!this.choices.services })
        : consoleRevertPage(consoleDeps, !!this.env.settings.isShell, options);
      const close = flow.onClose?.bind(flow);
      flow.onClose = () => {
        close?.();
        checkIntent(intent, skip).catch(console.error);
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
      if (this.index < 0 || this.index >= this.options.length) this.index = defaultIndex(this.options);
      const order = this.order().filter((s) => s !== 'terms' || this.withTerms);
      const at = this.step === 'result' ? order.length : order.indexOf(this.step);
      const dots = el('ol', { className: 'wizard-dots', ariaLabel: t('wizard.progress', { n: Math.min(at, order.length - 1) + 1, total: order.length }) },
        order.map((s, i) => el('li', { className: i < at ? 'done' : i === at ? 'active' : '' })));

      this.nodes = this.options.map((o) => optionNode(o));
      // The same page painted again (details on or off, a choice that stays): the reading position stays.
      const keepScroll = this.paintedStep === this.step ? this.doc?.scrollTop ?? 0 : 0;
      this.paintedStep = this.step;
      this.doc = null;
      const doc = def.layout ? this.docBody(def) : null;
      // `row` is for the cursor (up/down), `line` for the layout: the client rows are one list, one cursor row each.
      const groups = [];
      this.options.forEach((o, i) => {
        const line = o.line ?? o.row ?? 0;
        (groups[line] ??= []).push(this.nodes[i]);
      });
      const lines = groups.filter(Boolean).map((nodes) => {
        const first = this.options[this.nodes.indexOf(nodes[0])];
        const kind = first.action ? 'actions' : first.listRow || first.button ? 'list' : first.chip ? 'chips' : 'cards';
        return el('div', { className: `wizard-line wizard-${kind}` },
          first.chip ? el('span', { className: 'wizard-line-label', textContent: first.label }) : null,
          el('ul', { className: 'wizard-options' }, nodes));
      });

      this.root.replaceChildren(el('div', { className: `wizard wizard-step-${this.step}${def.layout ? ' wizard-doc-page' : ''}` },
        dots,
        el('header', { className: 'wizard-head' },
          def.hero
            ? el('div', { className: 'wizard-logo' }, el('img', { src: 'assets/icon.svg', alt: '' }), el('span', { className: 'wizard-brand' }, 'Couch', el('b', { textContent: 'HUD' })))
            : el('span', { className: 'wizard-icon' }, def.glyph ? iconEl(def.glyph) : categoryIcon(def.icon)),
          el('h2', { textContent: def.title() }),
          def.lead ? el('p', { className: 'wizard-lead', textContent: def.lead() }) : null),
        el('div', { className: 'wizard-body' }, doc, doc ? el('p', { className: 'wizard-more', textContent: `↓ ${t('wizard.doc.more')}` }) : null,
          this.loaded ? lines : el('p', { className: 'wizard-lead', textContent: t('wizard.loading') })),
        !doc && def.note?.() ? el('p', { className: 'wizard-note' }, categoryIcon('shield'), def.note()) : null,
        el('p', { className: 'wizard-later' }, def.layout === 'summary' || def.layout === 'result' ? def.later() : t('wizard.later', { where: def.later() }))));
      this.update();
      if (this.doc && keepScroll) this.doc.scrollTop = keepScroll;
      this.updateMore();
    },

    /** The reading part of an explanation, summary or result page; scrolls with up/down and the right stick. */
    docBody(def) {
      const parts = def.layout === 'explain' ? this.explainParts(def) : def.layout === 'summary' ? this.summaryParts(def) : this.resultParts();
      this.doc = el('div', { className: `wizard-doc wizard-doc-${def.layout}` }, parts);
      this.doc.addEventListener('scroll', () => this.updateMore(), { passive: true });
      return this.doc;
    },

    /** Meaning and change on the left, risks and way back on the right; the technical details only for experts, behind X. */
    explainParts(def) {
      const id = def.explain;
      const k = (field) => { const key = `wizard.x.${id}.${field}`; return t(key); };
      const note = def.note?.();
      return [
        el('section', { className: 'wizard-doc-col' },
          el('p', { className: 'wizard-doc-what', textContent: k('what') }),
          el('p', { textContent: k('changes') }),
          note ? el('p', { className: 'wizard-doc-note' }, categoryIcon('shield'), note) : null,
          this.expert() && this.details ? el('section', { className: 'wizard-doc-details' }, el('h3', { textContent: t('wizard.doc.details') }), el('p', { textContent: k('detail') })) : null),
        el('section', { className: 'wizard-doc-col' },
          el('h3', { textContent: t('wizard.doc.risks') }),
          el('ul', { className: 'wizard-doc-risks' }, Array.from({ length: EXPLAINS[id].risks }, (_, i) => el('li', { textContent: k(`risk${i + 1}`) }))),
          el('h3', { textContent: t('wizard.doc.undo') }),
          el('p', { textContent: k('undo') }))
      ];
    },

    /** What will change on the PC, what was chosen, and the way back. */
    summaryParts() {
      const plan = summaryPlan(this.ctx());
      const needsAdmin = plan.changes.some((c) => ['shell', 'revert', 'autologon', 'services'].includes(c));
      return [
        el('section', { className: 'wizard-doc-col' },
          el('h3', { textContent: t('wizard.sum.changesHead') }),
          plan.changes.length
            ? el('ul', { className: 'wizard-doc-changes' }, plan.changes.map((c) => el('li', { textContent: sumText(c) })))
            : el('p', { textContent: t('wizard.sum.nothing') })),
        el('section', { className: 'wizard-doc-col' },
          el('h3', { textContent: t('wizard.sum.keptHead') }),
          this.summary(),
          needsAdmin ? el('p', { className: 'wizard-doc-note' }, categoryIcon('shield'), t('wizard.sum.admin')) : null,
          plan.changes.length ? el('p', { textContent: t('wizard.sum.undo') }) : null)
      ];
    },

    /** What "Apply" did and what failed, and the way back. */
    resultParts() {
      const failed = this.results.some((r) => r.outcome !== 'ok');
      return [
        el('section', { className: 'wizard-doc-col' },
          el('p', { className: 'wizard-doc-what', textContent: failed ? t('wizard.result.leadFailed') : t('wizard.result.lead') }),
          this.results.length
            ? el('ul', { className: 'wizard-doc-changes' }, this.results.map((r) => el('li', { className: r.outcome === 'failed' ? 'failed' : '' }, resultLine(r))))
            : el('p', { textContent: t('wizard.result.nothing') })),
        el('section', { className: 'wizard-doc-col' },
          el('h3', { textContent: t('wizard.doc.undo') }),
          el('p', { textContent: t('wizard.result.undo') }))
      ];
    },

    /** Shows the cue under the text while there is more to read below. */
    updateMore() {
      const more = this.root?.querySelector('.wizard-more');
      if (!more || !this.doc) return;
      more.classList.toggle('show', this.doc.scrollHeight - this.doc.clientHeight - this.doc.scrollTop > 16);
    },

    scrollDoc(direction) {
      if (!this.doc) return sound.bump();
      const before = this.doc.scrollTop;
      this.doc.scrollTop += direction * Math.max(60, this.doc.clientHeight * 0.3);
      if (this.doc.scrollTop === before) sound.bump();
      this.updateMore();
    },

    summary() {
      const env = this.env;
      const choices = this.choices;
      const clients = env.launchers.filter((l) => (choices.clientModes[l.id] ?? l.mode) === 'standby').length;
      // As chosen: Full that is still to be set up counts as Full, the way back as Light.
      const full = isFullNow(this.ctx());
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
      const reading = !!this.def()?.layout;
      switch (action) {
        case 'up':
        case 'down':
          // On the reading pages the buttons sit side by side: up and down read.
          if (reading) return this.scrollDoc(action === 'up' ? -1 : 1);
          return this.moveFocus(action);
        case 'rsup':
        case 'rsdown':
          return reading ? this.scrollDoc(action === 'rsup' ? -1 : 1) : undefined;
        case 'left':
        case 'right':
          return this.moveFocus(action);
        case 'a':
          return this.choose();
        case 'x':
          // Technical details: experts only; simple mode explains shorter.
          if (!reading || this.def().layout !== 'explain' || !this.expert()) return;
          this.details = !this.details;
          sound.select();
          return this.paint();
        case 'y': {
          // On an explanation: the choice that changes nothing. Summary and result have no "later".
          if (reading) {
            if (this.def().layout !== 'explain') return;
            this.index = Math.max(0, this.options.findIndex((o) => o.safe));
            return this.choose();
          }
          // "Later": nothing changes, on to the next question.
          sound.select();
          return this.go(this.nextOf(this.step));
        }
        case 'b': {
          // The result is the end: nothing to go back to.
          if (this.step === 'result') return sound.bump();
          const prev = this.prevOf(this.step);
          if (prev) return this.go(prev, { back: true });
          if (this.firstRun) return sound.bump();
          this.revertSkin();
          removePage(this);
          sound.back();
          return render();
        }
        case 'start':
          // Skip the rest: everything stays as it is, the settings have it all. Choices not yet applied are dropped.
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
      const layout = this.def()?.layout;
      const verb = option?.listRow ? t('wizard.hint.toggle') : option?.chip ? t('wizard.hint.apply') : t('wizard.hint.choose');
      return [
        ['A', verb],
        ...(layout ? [['↕', t('wizard.hint.scroll')]] : []),
        ...(this.step !== 'result' && (this.prevOf(this.step) || !this.firstRun) ? [['B', t('wizard.hint.back')]] : []),
        ...(layout === 'explain' ? [['Y', t('wizard.hint.safe')]] : []),
        ...(layout === 'explain' && this.expert() ? [['X', this.details ? t('wizard.hint.detailsOff') : t('wizard.hint.details')]] : []),
        ...(!layout ? [['Y', t('wizard.hint.later')]] : []),
        ...(this.step !== 'result' ? [['≡', t('wizard.hint.exit')]] : [])
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
    return el('li', { className: `wizard-option wizard-button${o.primary ? ' primary' : ''}${o.safe ? ' safe' : ''}` },
      el('strong', { textContent: o.title }), o.sub ? el('small', { textContent: o.sub }) : null);
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
async function checkIntent(intent, skip = []) {
  const env = await loadEnv();
  // A declined automatic sign-in is no missing part.
  const done = intent === 'full' ? isConsoleReady(env.settings, env.security, { autologon: !skip.includes('autologon') }) : !hasConsoleSetup(env.settings, env.security);
  if (!done) toast(intent === 'full' ? t('wizard.notice.full') : t('wizard.notice.light'), 9000);
}

// ---------------------------------------------------------------------------
// Entry points



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
    // Only what the assistant itself writes: a known step and a yes/no. Anything else means no resume.
    return parseResume(raw);
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
