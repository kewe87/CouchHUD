import { lockStepStatus } from './lock-rules.js';

// Pure rules of the setup assistant (views/setup-wizard.js): the order of the steps, which console setup steps a choice leaves out,
// the safe default focus and the summary. No DOM, no host calls, so ui-logic-check can test them.
//
// Every step that changes the PC has an explanation page before the choice (what it means, what it changes, the risks, the way
// back). The choice that changes nothing is "safe" and is the one focused first; the risky one never is.

/** All steps in the order they are asked. Which of them apply right now is decided by applies(). */
export const SEQUENCE = [
  'welcome', 'terms', 'areas', 'mode',
  'revert', 'shell', 'lock', 'autologon', 'launchers', 'services',
  'look', 'sound', 'clientsInfo', 'clients', 'tv',
  'summary', 'result'
];

/** The steps with an explanation page (the texts are wizard.x.<id>.*). The number is how many risks the page lists. */
export const EXPLAINS = {
  shell: { risks: 3, icon: 'console' },
  revert: { risks: 2, icon: 'console' },
  lock: { risks: 3, icon: 'shield' },
  autologon: { risks: 3, icon: 'shield' },
  launchers: { risks: 3, icon: 'console' },
  services: { risks: 3, icon: 'console' },
  clientsInfo: { risks: 2, icon: 'library' },
  tv: { risks: 3, icon: 'tv' }
};

/** The console setup steps (views/console-setup.js) each wizard decision stands for; declined, the setup leaves them out. */
export const SETUP_PARTS = {
  autologon: ['autologon'],
  launchers: ['autostart', 'suppress', 'xbox']
};

/**
 * The setup steps no choice can leave out: integration, lock and shell go together, and the services step belongs to the lock (it sets
 * up the helper tasks the lock's Windows policies need; without them every lock change asks Windows again). Declining "pause
 * services" only stops it from pausing services (the setup is then built with pauseServices false).
 */
export const SETUP_CORE = ['integration', 'lock', 'shell', 'services'];

/** The setup steps that stay when `skip` is left out; the core steps always stay. */
export const filterSetupSteps = (steps, skip) => steps.filter((s) => !skip.includes(s.id) || SETUP_CORE.includes(s.id));

/**
 * True when every part of the setup is in place. autologon: false when the user declined the automatic sign-in (the host keeps that
 * as `autoLogonDeclined`), it is no missing part then. It only decides what the assistant offers, nothing about protection.
 */
export function isConsoleReady(settings, security, { autologon = true } = {}) {
  return settings?.integrationMode === 'full' && !!settings?.isShell && lockStepStatus(security) === 'done' && (!autologon || !!security?.autoLogon || security?.accountPassword === false);
}

/** The resume payload after a language switch: only what the assistant itself writes (a known step and a yes/no), else null. */
export function parseResume(raw) {
  try {
    const resume = raw ? JSON.parse(raw) : null;
    if (!resume || typeof resume !== 'object' || !SEQUENCE.includes(resume.step) || resume.step === 'result' || typeof resume.firstRun !== 'boolean') return null;
    return { step: resume.step, firstRun: resume.firstRun };
  } catch {
    return null;
  }
}

/** Fresh decisions: everything off, every system change has to be asked for. */
export const freshChoices = () => ({ autologon: false, launchers: false, services: false, lock: false, clients: 'keep', clientModes: {}, tv: 'off' });

/** Shell mode as chosen here (set up at the end) or as it is now. */
export const isFullNow = (ctx) => ctx.intent === 'full' || (!!ctx.full && ctx.intent !== 'light');

/** Does this step apply right now? ctx: { withTerms, intent, full, lockOn, accountPassword, autoLogonDone, launchers, tvPaired, choices, areas } */
export function applies(id, ctx) {
  const full = isFullNow(ctx);
  const choices = ctx.choices ?? freshChoices();
  switch (id) {
    case 'terms': return !!ctx.withTerms;
    case 'revert': return ctx.intent === 'light';
    case 'shell': return ctx.intent === 'full';
    // Shell needs the lock; an install already in shell mode without one is asked too (it can say later).
    case 'lock': return full && !ctx.lockOn;
    // The sign-in only makes sense with the shell and the lock; an account without a password signs in by itself already.
    case 'autologon': return ctx.intent === 'full' && ctx.accountPassword !== false && !ctx.autoLogonDone;
    case 'launchers':
    case 'services': return ctx.intent === 'full';
    case 'clientsInfo': return full && ctx.launchers > 0 && (ctx.areas ?? ['games']).includes('games');
    case 'clients': return full && ctx.launchers > 0 && (ctx.areas ?? ['games']).includes('games') && choices.clients === 'adjust';
    case 'tv': return !!ctx.tvPaired;
    case 'result': return false;
    default: return true;
  }
}

/** The steps in order as far as they apply (the dots); the result page is no question. */
export const order = (ctx) => SEQUENCE.filter((id) => applies(id, ctx));

/**
 * The step after id. Works from the fixed sequence, not from the current list: a decision that removes id itself (declining shell
 * mode) must still go on to the next question and not back to the welcome. The accepted notice counts once and is no step then.
 */
export function nextOf(id, ctx) {
  const at = SEQUENCE.indexOf(id);
  for (let i = at + 1; i < SEQUENCE.length; i++) {
    const s = SEQUENCE[i];
    if (s === 'terms' && ctx.termsAccepted) continue;
    if (s === 'result' || applies(s, ctx)) return s === 'result' ? 'summary' : s;
  }
  return 'summary';
}

/** The step before id, or null at the first one. */
export function prevOf(id, ctx) {
  for (let i = SEQUENCE.indexOf(id) - 1; i >= 0; i--) {
    const s = SEQUENCE[i];
    if (s === 'terms' && ctx.termsAccepted) continue;
    if (applies(s, ctx)) return s;
  }
  return null;
}

/** Console setup steps to leave out because the user did not ask for them. */
export function skippedSetupSteps(choices) {
  return Object.entries(SETUP_PARTS).filter(([key]) => !choices?.[key]).flatMap(([, parts]) => parts);
}

/**
 * The option focused first: the safe one on every explanation page, never a risky one; else the chosen one, else the current one.
 * Options are { safe, chosen, current, chip }.
 */
export function defaultIndex(options) {
  const safe = options.findIndex((o) => o.safe);
  if (safe >= 0) return safe;
  const chosen = options.findIndex((o) => o.chosen);
  const current = options.findIndex((o) => o.current && !o.chip);
  return Math.max(0, chosen >= 0 ? chosen : current);
}

/**
 * What the summary lists. changes: what happens to the PC when "Apply" is pressed (ids; the texts are wizard.sum.<id>), kept: what was
 * chosen and is already saved. Nothing in changes means nothing on the PC is touched.
 */
export function summaryPlan(ctx) {
  const choices = ctx.choices ?? freshChoices();
  const changes = [];
  if (ctx.intent === 'full') {
    changes.push('shell');
    if (!ctx.lockOn) changes.push('lockSetup');
    if (applies('autologon', ctx) && choices.autologon) changes.push('autologon');
    if (choices.launchers) changes.push('launchers');
    if (choices.services) changes.push('services');
  } else if (ctx.intent === 'light') {
    changes.push('revert');
  } else if (isFullNow(ctx) && !ctx.lockOn && choices.lock) {
    changes.push('lockNow');
  }
  if (choices.clients === 'adjust' && Object.keys(choices.clientModes ?? {}).length) changes.push('clients');
  if (applies('tv', ctx) && choices.tv !== 'off') changes.push(choices.tv === 'both' ? 'tvBoth' : 'tvOn');
  const kept = ['language', 'mode', 'look', 'sound', 'areas'];
  if (!changes.length) kept.push('nothing');
  return { changes, kept };
}

/** The summary starts on "Back" while the PC is about to change, on "Apply" when it is not: A twice never sets up a shell by accident. */
export const summaryFocus = (plan) => (plan.changes.length ? 'back' : 'apply');

// The explanation texts are looked up by id and field (setup-wizard.js):
// i18n-used: wizard.x.shell.title wizard.x.shell.what wizard.x.shell.changes wizard.x.shell.risk1 wizard.x.shell.risk2 wizard.x.shell.risk3 wizard.x.shell.undo wizard.x.shell.detail wizard.x.shell.safe wizard.x.shell.safeSub wizard.x.shell.go wizard.x.shell.goSub
// i18n-used: wizard.x.revert.title wizard.x.revert.what wizard.x.revert.changes wizard.x.revert.risk1 wizard.x.revert.risk2 wizard.x.revert.undo wizard.x.revert.detail wizard.x.revert.safe wizard.x.revert.safeSub wizard.x.revert.go wizard.x.revert.goSub
// i18n-used: wizard.x.lock.title wizard.x.lock.what wizard.x.lock.changes wizard.x.lock.risk1 wizard.x.lock.risk2 wizard.x.lock.risk3 wizard.x.lock.undo wizard.x.lock.detail wizard.x.lock.safe wizard.x.lock.safeSub wizard.x.lock.go wizard.x.lock.goSub wizard.x.lock.needed wizard.x.lock.setup wizard.x.lock.setupSub
// i18n-used: wizard.x.autologon.title wizard.x.autologon.what wizard.x.autologon.changes wizard.x.autologon.risk1 wizard.x.autologon.risk2 wizard.x.autologon.risk3 wizard.x.autologon.undo wizard.x.autologon.detail wizard.x.autologon.safe wizard.x.autologon.safeSub wizard.x.autologon.go wizard.x.autologon.goSub
// i18n-used: wizard.x.launchers.title wizard.x.launchers.what wizard.x.launchers.changes wizard.x.launchers.risk1 wizard.x.launchers.risk2 wizard.x.launchers.risk3 wizard.x.launchers.undo wizard.x.launchers.detail wizard.x.launchers.safe wizard.x.launchers.safeSub wizard.x.launchers.go wizard.x.launchers.goSub
// i18n-used: wizard.x.services.title wizard.x.services.what wizard.x.services.changes wizard.x.services.risk1 wizard.x.services.risk2 wizard.x.services.risk3 wizard.x.services.undo wizard.x.services.detail wizard.x.services.safe wizard.x.services.safeSub wizard.x.services.go wizard.x.services.goSub
// i18n-used: wizard.x.clientsInfo.title wizard.x.clientsInfo.what wizard.x.clientsInfo.changes wizard.x.clientsInfo.risk1 wizard.x.clientsInfo.risk2 wizard.x.clientsInfo.undo wizard.x.clientsInfo.detail wizard.x.clientsInfo.safe wizard.x.clientsInfo.safeSub wizard.x.clientsInfo.go wizard.x.clientsInfo.goSub
// i18n-used: wizard.x.tv.title wizard.x.tv.what wizard.x.tv.changes wizard.x.tv.risk1 wizard.x.tv.risk2 wizard.x.tv.risk3 wizard.x.tv.undo wizard.x.tv.detail wizard.x.tv.safe wizard.x.tv.safeSub wizard.x.tv.go wizard.x.tv.goSub wizard.x.tv.goBoth wizard.x.tv.goBothSub
// i18n-used: wizard.x.lock.stay wizard.x.lock.staySub
// i18n-used: wizard.sum.shell wizard.sum.lockSetup wizard.sum.lockNow wizard.sum.autologon wizard.sum.launchers wizard.sum.services wizard.sum.revert wizard.sum.clients wizard.sum.tvOn wizard.sum.tvBoth
