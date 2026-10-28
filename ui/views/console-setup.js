// One-button console setup and its way back.
//
// A guided run through a list of steps. Every step looks at the current state first
// (so running it again repairs instead of doubling up), does exactly one thing and
// reports "done", "skipped" or "failed" with a reason. Cancelling stops after the
// current step. Every step is complete in itself, and the two that must go together
// are chained: the host enforces the couch lock only while CouchHUD runs as the shell,
// so the shell comes before the automatic sign-in and the sign-in is only set up when
// lock and shell are both in place. A stop therefore never leaves a PC that signs in
// by itself without a working lock.
//
// Both runs reach the whole system, so they ask for the couch code first, before looking at anything. A state that cannot be read
// is "unknown", never "done" (a locked read used to count as done and skipped the step), and every step is looked at again right
// before it runs. The way back works in every mode: removing what the setup created never depends on the integration level.

import { api } from '../core/api.js';
import { t } from '../core/i18n.js';
import { el, keepInView, setFocused } from '../core/dom.js';
import { attempt, toast } from '../core/feedback.js';
import { currentView, pop, renderHints } from '../core/nav.js';
import { state } from '../core/state.js';
import { confirmDialog } from '../components/overlay.js';
import { ensureUnlocked, isLocked, onApprovalAnnounced } from '../components/passcode.js';
import { beginSetupHold, lockStepStatus, setupCouchLock } from '../core/lock-rules.js';
import { sound } from '../sound.js';

const MARK = { done: '✓', ok: '✓', todo: '•', unknown: '?', na: '–', running: '…', skipped: '–', failed: '✕', aborted: '–' };
const LABEL = () => ({
  done: t('consoleSetup.status.done'),
  ok: t('consoleSetup.status.ok'),
  todo: t('consoleSetup.status.todo'),
  unknown: t('consoleSetup.status.unknown'),
  na: t('consoleSetup.status.na'),
  running: t('consoleSetup.status.running'),
  skipped: t('consoleSetup.status.skipped'),
  failed: t('consoleSetup.status.failed'),
  aborted: t('consoleSetup.status.aborted')
});
const SATISFIED = new Set(['done', 'ok', 'na']);

/** Fresh view of the host state; steps read it instead of trusting what the page loaded earlier. */
async function loadEnv() {
  const [settings, security] = await Promise.all([api.getSettings(), api.getSecurity()]);
  return { settings: settings ?? {}, security: security ?? {} };
}

const errorText = (err) => err?.message || String(err);

/**
 * The state of one step. A read that fails (locked, host error) is "unknown": it counts as still to do and the run looks again,
 * but it is never taken for "already done".
 */
async function readStatus(step, env) {
  try {
    return await step.check(env);
  } catch (err) {
    console.warn(`console step ${step.id}: ${errorText(err)}`);
    return 'unknown';
  }
}

// ---------------------------------------------------------------------------
// Engine

function stepsPage({ id, title, intro, notes, startLabel, steps, restartAsk, restartLabel, deps }) {
  const rows = steps.map((step) => ({ step, status: 'todo', note: '' }));
  let phase = 'loading'; // loading, overview, running, done
  let cancel = false;
  let closed = false;
  let adminPrompts = 0;
  let lastEnv = null;
  let actions = [];
  let loading = null;
  // Waiting for the couch to open (code entry, Windows sign-in) and, with a lock of 0.4.0, for an administrator's approval (UAC):
  // no step shows a status of its own until that is through.
  let waiting = null; // null, 'unlock', 'admin'

  const page = {
    id,
    name: title,
    index: 0,
    captures: (action) => phase === 'running' && ['b', 'lb', 'rb', 'start'].includes(action),
    onClose() {
      closed = true;
      cancel = true;
      deps.refresh();
    },

    load() {
      // Called twice in a row (opening the page, then the view coming to the front): one read, one code entry.
      loading ??= this.loadNow().finally(() => { loading = null; });
      return loading;
    },

    /** ensureUnlocked, and while it waits the page says what it waits for; a cancel leaves the page as it was before. */
    async unlock() {
      if (!(await isLocked())) return true;
      waiting = 'unlock';
      this.paint();
      const stop = onApprovalAnnounced(() => {
        waiting = 'admin';
        this.paint();
      });
      try {
        return await ensureUnlocked(title);
      } finally {
        stop();
        waiting = null;
        this.paint();
      }
    },

    async loadNow() {
      // Before anything is read: with the couch locked the reads are refused, and an answer from the refusal would be a guess.
      if (!(await this.unlock())) {
        if (currentView() === this) pop();
        return;
      }
      try {
        const env = await loadEnv();
        lastEnv = env;
        adminPrompts = 0;
        for (const row of rows) {
          row.note = '';
          row.status = await readStatus(row.step, env);
          if (row.status === 'unknown') row.note = t('consoleSetup.unknownNote');
          if (['todo', 'unknown'].includes(row.status) && row.step.admin) adminPrompts++;
        }
      } catch (err) {
        console.error(err);
        toast(t('consoleSetup.stateNotRead', { error: errorText(err) }), 5000);
      }
      phase = 'overview';
      // Never start on a button: a second A must not set everything up by accident.
      this.index = 0;
      this.paint();
    },

    summary() {
      const count = (status) => rows.filter((r) => r.status === status).length;
      const parts = [
        [count('ok') + count('done'), (n) => t('consoleSetup.summary.done', { count: n })],
        [count('skipped') + count('aborted'), (n) => t('consoleSetup.summary.skipped', { count: n })],
        [count('failed'), (n) => t('consoleSetup.summary.failed', { count: n })]
      ].filter(([n]) => n > 0).map(([n, text]) => text(n));
      return parts.join(' · ') || t('consoleSetup.summary.nothing');
    },

    async start() {
      // The lock step must not lock the HUD in the middle of the setup: the couch stays open until the end. Without that hold, and on the
      // stand-in of broken settings (the host refuses the hold there too), nothing runs: the reason is shown and the overview stays.
      if (!(await this.unlock())) return;
      const begun = await beginSetupHold({ readSecurity: () => api.getSecurity(), hold: () => api.holdUnlockForSetup(true) });
      if (begun.security) state.info.configFault = !!begun.security.configFault;
      if (!begun.ok) {
        if (begun.error) console.warn(`console setup not started: ${errorText(begun.error)}`);
        const reason = begun.reason === 'configFault' ? t('wizard.configFault') : begun.reason === 'locked' ? t('consoleSetup.notUnlocked') : t('consoleSetup.notStarted', { error: errorText(begun.error) });
        toast(reason, 7000);
        phase = 'overview';
        this.paint();
        return;
      }
      phase = 'running';
      cancel = false;
      this.paint();
      for (const row of rows) {
        if (cancel) {
          if (!SATISFIED.has(row.status)) {
            row.status = 'aborted';
            row.note = t('consoleSetup.cancelledNote');
          }
          continue;
        }
        if (SATISFIED.has(row.status)) continue;
        const missing = (row.step.requires ?? []).map((rid) => rows.find((r) => r.step.id === rid)).find((r) => r && !SATISFIED.has(r.status));
        if (missing) {
          row.status = 'skipped';
          row.note = t('consoleSetup.needsFirst', { step: missing.step.title });
          continue;
        }
        // The release of the couch lock is short; ask again whenever it ran out between two steps.
        if (!(await this.unlock())) {
          cancel = true;
          row.status = 'aborted';
          row.note = t('consoleSetup.notUnlocked');
          continue;
        }
        let result;
        try {
          // Right before: what the overview saw may be old (an earlier step, the lock) or was never readable.
          const env = await loadEnv();
          const again = await readStatus(row.step, env);
          if (SATISFIED.has(again)) {
            row.status = again;
            row.note = '';
            this.paint();
            continue;
          }
          row.status = 'running';
          row.note = row.step.working ?? '';
          this.paint();
          result = await row.step.run(env, deps);
        } catch (err) {
          console.error(err);
          result = { status: 'failed', note: errorText(err) };
        }
        row.status = result.status;
        row.note = result.note ?? '';
        this.paint();
      }
      // Now the lock may close the couch.
      await attempt(() => api.holdUnlockForSetup(false), undefined, { quietLocked: true });
      phase = 'done';
      const retry = rows.some((r) => ['failed', 'skipped', 'aborted'].includes(r.status));
      actions = [
        ...(restartAsk(rows) ? [{ label: restartLabel, run: () => this.restart() }] : []),
        ...(retry ? [{ label: t('consoleSetup.action.retry'), run: () => this.retry() }] : []),
        { label: restartAsk(rows) ? t('consoleSetup.action.later') : t('consoleSetup.action.done'), run: () => pop() }
      ];
      this.index = rows.length;
      sound.select();
      deps.refresh();
      this.paint();
    },

    async retry() {
      phase = 'loading';
      this.paint();
      await this.load();
      await this.start();
    },

    async restart() {
      if (!(await confirmDialog(t('consoleSetup.restartConfirm'), t('consoleSetup.restartConfirmButton')))) return;
      await attempt(() => api.power('restart'), t('consoleSetup.restartFailed'));
    },

    /** Rows (to scroll through) and buttons share one cursor. */
    focusNodes() {
      return [...(this.stepNodes ?? []), ...(this.actionNodes ?? [])];
    },

    paint() {
      if (closed || !this.root || currentView() !== this) return;
      if (phase === 'overview') actions = [{ label: startLabel, run: () => this.start().catch(console.error) }, { label: t('consoleSetup.action.cancel'), run: () => pop() }];
      if (phase === 'loading' || phase === 'running') actions = [];
      // Before the first read (code entry, approval) nothing is known yet: no "will be done" for a step that has not been looked at.
      const label = (row) => (phase === 'loading' ? t('consoleSetup.status.pending') : phase === 'overview' && row.status === 'todo' ? t('consoleSetup.status.open') : LABEL()[row.status]);
      this.stepNodes = rows.map((row) => el('li', { className: `check-row console-step status-${phase === 'loading' ? 'pending' : row.status}` },
        el('span', { className: 'check-box', textContent: phase === 'loading' ? MARK.na : MARK[row.status] }),
        el('span', { className: 'check-main' },
          el('strong', { textContent: row.step.title }),
          el('small', { textContent: (phase !== 'loading' && row.note) || row.step.detail })),
        el('span', { className: `pill ${phase !== 'loading' && (row.status === 'ok' || row.status === 'done') ? 'on' : 'off'}`, textContent: label(row) })));
      this.actionNodes = actions.map((a) => el('li', { className: 'check-row highlight console-action' },
        el('span', { className: 'check-main' }, el('strong', { textContent: a.label }))));
      const heading = phase === 'done' ? t('consoleSetup.headingDone', { summary: this.summary() }) : phase === 'running' ? t('consoleSetup.headingRunning') : title;
      const waitText = waiting === 'admin' ? t('consoleSetup.waitingAdmin') : waiting === 'unlock' ? t('consoleSetup.waitingUnlock') : '';
      const lead = waitText ? waitText : phase === 'done'
        ? (rows.some((r) => r.status === 'failed') ? t('consoleSetup.leadFailed') : rows.some((r) => ['skipped', 'aborted'].includes(r.status)) ? t('consoleSetup.leadSkipped') : t('consoleSetup.leadAllDone'))
        : phase === 'running' ? t('consoleSetup.leadRunning') : intro;
      const extra = phase === 'overview' ? notes(adminPrompts, lastEnv) : [];
      this.index = Math.min(this.index, Math.max(0, this.focusNodes().length - 1));
      this.root.replaceChildren(el('div', { className: 'files console-page' },
        el('div', { className: 'files-head' },
          el('div', { className: 'crumb', textContent: t('consoleSetup.crumb') }),
          el('h2', { textContent: heading }),
          lead ? el('p', { className: 'meta', textContent: lead }) : null),
        (this.stepList = el('ul', { className: 'check-list list-scroll console-steps' }, this.stepNodes)),
        extra.length ? el('div', { className: 'console-notes' }, extra.map((note) => el('p', { textContent: note }))) : null,
        this.actionNodes.length ? el('ul', { className: 'console-actions' }, this.actionNodes) : null));
      this.update();
      renderHints();
      const running = this.stepNodes.find((n) => n.classList.contains('status-running'));
      if (running) keepInView(this.stepList, running, { instant: true });
    },

    build(root) {
      this.root = root;
      if (phase === 'loading') this.load();
      this.paint();
    },

    update() {
      const nodes = this.focusNodes();
      // No cursor while the run is in progress.
      setFocused(nodes, phase === 'running' || phase === 'loading' ? -1 : this.index);
      if (this.index < this.stepNodes.length) keepInView(this.stepList, this.stepNodes[this.index]);
      renderHints();
    },

    async handle(action) {
      if (phase === 'running') {
        if (['b', 'lb', 'rb', 'start'].includes(action) && !cancel) {
          cancel = true;
          toast(t('consoleSetup.stopAfterStep'));
        }
        return;
      }
      if (!actions.length) return action === 'b' ? pop() : undefined;
      switch (action) {
        case 'up':
        case 'down': {
          const next = this.index + (action === 'up' ? -1 : 1);
          if (next < 0 || next >= this.focusNodes().length) return sound.bump();
          this.index = next;
          sound.move();
          return this.update();
        }
        case 'left':
        case 'right': {
          const at = this.index - this.stepNodes.length;
          const next = at + (action === 'left' ? -1 : 1);
          if (at < 0 || next < 0 || next >= actions.length) return sound.bump();
          this.index = this.stepNodes.length + next;
          sound.move();
          return this.update();
        }
        case 'a': {
          const button = actions[this.index - this.stepNodes.length];
          if (!button) return sound.bump();
          sound.select();
          return button.run();
        }
        case 'b':
          return pop();
      }
    },

    pointer(node, index, dispatch) {
      if (!node.closest('.console-actions')) return;
      this.index = this.stepNodes.length + index;
      this.update();
      dispatch('a');
    },

    hints() {
      if (phase === 'running') return [['B', t('consoleSetup.action.cancel')]];
      if (phase === 'loading') return [['B', t('consoleSetup.hint.back')]];
      const onButton = this.index >= (this.stepNodes?.length ?? 0);
      return [onButton ? ['A', t('consoleSetup.hint.select')] : ['↕', t('consoleSetup.hint.scroll')], ['B', phase === 'done' ? t('consoleSetup.hint.close') : t('consoleSetup.hint.back')]];
    }
  };
  return page;
}

// ---------------------------------------------------------------------------
// Setup steps

const ok = (note) => ({ status: 'ok', note });
const skipped = (note) => ({ status: 'skipped', note });
const failed = (note) => ({ status: 'failed', note });

/**
 * The lock step (rules in core/lock-rules.js): the code goes along with the switch in one step, never "code set, then the lock on" (the
 * CouchHUD service drops a code set at the open couch when a lock is switched on, and the code lock would have no code; finding of the
 * 0.4.2 acceptance test). The flows are the settings page's own (deps), with every check of the couch, the parents and the host.
 */
async function setupLock(env, deps) {
  // A code from the open couch is not kept: said before the entry, so the old code can simply be entered again.
  if (env.security.couchLock !== 'code' && env.security.couchLock !== 'windows' && env.security.hasPasscode) toast(t('consoleSetup.lock.reenterCode'), 8000);
  const result = await setupCouchLock({
    security: env.security,
    enroll: () => deps.enrollCodeLock(),
    restore: () => deps.restoreCodeLock(),
    askAgain: () => confirmDialog(t('consoleSetup.lock.codeNotSet'), t('consoleSetup.lock.codeNotSetButton'))
  });
  // i18n-used: consoleSetup.lock.windowsNote consoleSetup.lock.codeNoPassword consoleSetup.lock.existingActive consoleSetup.lock.codeActive
  // i18n-used: consoleSetup.lock.notSaved consoleSetup.lock.noCode consoleSetup.lock.legacyConfirmFirst
  const note = `consoleSetup.lock.${result.note}`;
  return { status: result.status, note: t(note) };
}

function setupSteps() {
  return [
    {
      id: 'integration',
      title: t('consoleSetup.step.integration.title'),
      detail: t('consoleSetup.step.integration.detail'),
      check: (env) => (env.settings.integrationMode === 'full' ? 'done' : 'todo'),
      run: async () => {
        const result = await api.setIntegrationMode('full');
        state.info.integrationMode = result.mode;
        return ok(t('consoleSetup.step.integration.done'));
      }
    },
    {
      id: 'autostart',
      title: t('consoleSetup.step.autostart.title'),
      detail: t('consoleSetup.step.autostart.detail'),
      requires: ['integration'],
      check: async (env) => {
        const entries = await api.getStartup();
        const aligned = entries.every((e) => e.disabled || e.mode === e.suggested);
        return env.settings.runStartupApps && aligned ? 'done' : 'todo';
      },
      run: async (env) => {
        if (!env.settings.runStartupApps) await api.setSetting('runStartupApps', true);
        const result = await api.applyStartupSuggestions();
        const waiting = Array.isArray(result) ? result.filter((e) => e.mode === 'pc').length : result;
        return ok(typeof waiting === 'number' ? t('consoleSetup.step.autostart.waiting', { count: waiting }) : t('consoleSetup.step.autostart.adopted'));
      }
    },
    {
      id: 'services',
      title: t('consoleSetup.step.services.title'),
      detail: t('consoleSetup.step.services.detail'),
      requires: ['integration'],
      admin: true,
      check: async () => {
        const data = await api.getServices();
        const suggested = (data?.services ?? []).filter((s) => s.suggested);
        // The same confirmation also sets up the tasks for the lock's Windows policies.
        if (!suggested.length) return data?.policyHelper === false || data?.cleanupNoteCurrent === false ? 'todo' : 'na';
        // A note of the lock's empty Policies keys that is out of date also counts as not done: only a setup run (re)writes it.
        const complete = suggested.every((s) => s.selected) && data.taskInstalled && !data.pending && !data.legacyTask && data.cleanupNoteCurrent !== false;
        return complete ? 'done' : 'todo';
      },
      run: async () => {
        const data = await api.getServices();
        if (data.protectedFolder === false) return skipped(t('consoleSetup.step.services.unprotected'));
        for (const s of data.services.filter((x) => x.suggested && !x.selected)) await api.toggleCouchService(s.name);
        try {
          await api.setupServiceTask();
        } catch (err) {
          return failed(t('consoleSetup.windowsPromptFailed', { error: errorText(err) }));
        }
        return ok(t('consoleSetup.step.services.done'));
      }
    },
    {
      id: 'xbox',
      title: t('consoleSetup.step.xbox.title'),
      detail: t('consoleSetup.step.xbox.detail'),
      requires: ['integration'],
      check: (env) => (env.settings.guideTapInGame ? 'done' : 'todo'),
      run: async () => {
        await api.setSetting('guideTapInGame', true);
        return ok(t('consoleSetup.step.xbox.done'));
      }
    },
    {
      id: 'suppress',
      title: t('consoleSetup.step.suppress.title'),
      detail: t('consoleSetup.step.suppress.detail'),
      requires: ['integration'],
      check: (env) => (env.settings.suppressCompetitors ? 'done' : 'todo'),
      run: async () => {
        await api.setSetting('suppressCompetitors', true);
        return ok(t('consoleSetup.step.suppress.done'));
      }
    },
    {
      id: 'lock',
      title: t('consoleSetup.step.lock.title'),
      detail: t('consoleSetup.step.lock.detail'),
      requires: ['integration'],
      check: (env) => lockStepStatus(env.security),
      run: (env, deps) => setupLock(env, deps)
    },
    {
      id: 'shell',
      title: t('consoleSetup.step.shell.title'),
      detail: t('consoleSetup.step.shell.detail'),
      requires: ['integration', 'lock'],
      check: (env) => (env.settings.isShell ? 'done' : 'todo'),
      run: async () => {
        const isShell = await api.setShell(true);
        state.info.isShell = isShell;
        return isShell ? ok(t('consoleSetup.step.shell.done')) : failed(t('consoleSetup.step.shell.failed'));
      }
    },
    {
      id: 'autologon',
      title: t('consoleSetup.step.autologon.title'),
      detail: t('consoleSetup.step.autologon.detail'),
      requires: ['lock', 'shell'],
      admin: true,
      // An account without a password signs in by itself: nothing to set up (the overview says so).
      check: (env) => (env.security.accountPassword === false ? 'na' : env.security.autoLogon && env.security.autoLogonState === 'couchhud' && !env.security.autoLogonOtherUser ? 'done' : 'todo'),
      run: async (env, deps) => {
        const done = await deps.setupAutoLogon();
        const security = await attempt(() => api.getSecurity());
        if (done || security?.autoLogon) return ok(t('consoleSetup.step.autologon.done'));
        const error = deps.autoLogonError?.();
        if (error) return failed(t('consoleSetup.step.autologon.error', { error }));
        return skipped(t('consoleSetup.step.autologon.notSet'));
      }
    }
  ];
}

export function consoleSetupPage(deps) {
  return stepsPage({
    id: 'console-setup',
    title: t('consoleSetup.setup.title'),
    intro: t('consoleSetup.setup.intro'),
    notes: (admin, env) => [
      ...(env?.security?.accountPassword === false ? [t('consoleSetup.setup.needPassword')] : []),
      (admin === 0 ? t('consoleSetup.setup.adminNone') : admin === 1 ? t('consoleSetup.setup.adminOnce') : t('consoleSetup.setup.adminMany', { count: admin })),
      t('consoleSetup.setup.way'),
      t('consoleSetup.setup.bitlocker')
    ],
    startLabel: t('consoleSetup.setup.start'),
    steps: setupSteps(),
    restartAsk: (rows) => rows.some((r) => r.step.id === 'shell' && (r.status === 'ok' || r.status === 'done')) && rows.some((r) => r.status === 'ok'),
    restartLabel: t('consoleSetup.setup.restart'),
    deps
  });
}

// ---------------------------------------------------------------------------
// Way back

function revertSteps() {
  return [
    {
      // First: the lock's Windows policies go back while the helper tasks that can release them still exist. The tasks are only
      // deleted by the next step, and never while something of the lock is still in the registry.
      id: 'release',
      title: t('consoleSetup.step.revRelease.title'),
      detail: t('consoleSetup.step.revRelease.detail'),
      check: async () => {
        const data = await api.getServices();
        return data.policiesSet ? 'todo' : 'done';
      },
      run: async () => {
        let result;
        try {
          result = await api.releaseLockPolicies();
        } catch (err) {
          return failed(t('consoleSetup.step.revRelease.failed', { error: errorText(err) }));
        }
        // What the tasks could not give back, the next step (administrator) removes directly and checks again.
        return result?.released ? ok(t('consoleSetup.step.revRelease.done')) : skipped(t('consoleSetup.step.revRelease.admin'));
      }
    },
    {
      id: 'services',
      title: t('consoleSetup.step.revServices.title'),
      detail: t('consoleSetup.step.revServices.detail'),
      admin: true,
      check: async () => {
        const data = await api.getServices();
        return data.leftovers || data.taskInstalled || data.legacyTask || data.policyHelper || data.services.some((s) => s.selected) ? 'todo' : 'done';
      },
      run: async () => {
        const data = await api.getServices();
        for (const s of data.services.filter((x) => x.selected)) await api.toggleCouchService(s.name);
        // In every mode: the tasks and files of the setup go, also in the light mode.
        try {
          await api.setupServiceTask(false);
        } catch (err) {
          return failed(t('consoleSetup.step.revServices.failed', { error: errorText(err) }));
        }
        return ok(t('consoleSetup.step.revServices.done'));
      }
    },
    {
      id: 'autologon',
      title: t('consoleSetup.step.revAutologon.title'),
      detail: t('consoleSetup.step.revAutologon.detail'),
      admin: true,
      check: (env) => (env.security.autoLogon || env.security.autoLogonState === 'lost' ? 'todo' : 'done'),
      run: async (env) => {
        if (env.security.autoLogon && env.security.autoLogonState !== 'couchhud') return skipped(t('consoleSetup.step.revAutologon.foreign'));
        try {
          await api.setAutoLogon('off');
        } catch (err) {
          return failed(t('consoleSetup.windowsPromptFailed', { error: errorText(err) }));
        }
        return ok(t('consoleSetup.step.revAutologon.done'));
      }
    },
    {
      id: 'profiles',
      title: t('consoleSetup.step.profiles.title'),
      detail: t('consoleSetup.step.profiles.detail'),
      check: async () => {
        const entries = await api.getStartup();
        return entries.some((e) => e.mode === 'pc') ? 'todo' : 'done';
      },
      run: async () => {
        const entries = await api.getStartup();
        for (const e of entries.filter((x) => x.mode === 'pc')) await api.setStartupMode(e.id, 'couch');
        return ok(t('consoleSetup.step.profiles.done'));
      }
    },
    {
      id: 'suppress',
      title: t('consoleSetup.step.revSuppress.title'),
      detail: t('consoleSetup.step.revSuppress.detail'),
      check: (env) => (env.settings.suppressCompetitors ? 'todo' : 'done'),
      run: async () => {
        await api.setSetting('suppressCompetitors', false);
        return ok(t('consoleSetup.step.revSuppress.done'));
      }
    },
    {
      id: 'light',
      title: t('consoleSetup.step.light.title'),
      detail: t('consoleSetup.step.light.detail'),
      // The tasks go first: the way back must finish its cleanup before the mode (and the lock) changes.
      requires: ['services'],
      check: (env) => (env.settings.integrationMode === 'light' && !env.settings.isShell ? 'done' : 'todo'),
      run: async (env) => {
        // Without the lock (it goes with this step) an automatic sign-in lands everybody on the desktop.
        const security = (await attempt(() => api.getSecurity())) ?? env.security;
        if (security.autoLogon && !(await confirmDialog(t('consoleSetup.step.light.confirm'), t('consoleSetup.step.light.confirmButton'), { danger: true }))) {
          return skipped(t('consoleSetup.step.light.kept'));
        }
        const result = await api.setIntegrationMode('light');
        state.info.integrationMode = result.mode;
        state.info.isShell = false;
        return ok(result.undone?.length ? result.undone.join(' · ') : t('consoleSetup.step.light.done'));
      }
    }
  ];
}

export function consoleRevertPage(deps, wasShell) {
  return stepsPage({
    id: 'console-revert',
    title: t('consoleSetup.revert.title'),
    intro: t('consoleSetup.revert.intro'),
    notes: (admin) => [
      admin === 0 ? t('consoleSetup.revert.adminNone') : admin === 1 ? t('consoleSetup.revert.adminOnce') : t('consoleSetup.revert.adminMany', { count: admin }),
      t('consoleSetup.revert.after')
    ],
    startLabel: t('consoleSetup.revert.start'),
    steps: revertSteps(),
    restartAsk: (rows) => wasShell && rows.some((r) => r.step.id === 'light' && (r.status === 'ok' || r.status === 'done')),
    restartLabel: t('consoleSetup.revert.restart'),
    deps
  });
}

/**
 * Titles of the setup steps that are not in place right now: an empty list means everything is set up, null that
 * the state could not be read. The settings page shows this instead of claiming "all set up" from the main switches alone.
 */
export async function openSetupSteps() {
  try {
    // Locked, the reads are refused: nothing to say then (the page shows no verdict) instead of a refusal per step.
    if (await isLocked()) return null;
    const env = await loadEnv();
    const open = [];
    for (const step of setupSteps()) {
      if (!SATISFIED.has(await readStatus(step, env))) open.push(step.title);
    }
    return open;
  } catch (err) {
    console.error(err);
    return null;
  }
}

/**
 * True when there is anything to undo; the settings page shows the way back only then. `leftovers`: tasks or policy files of the
 * setup are still there (e.g. after a switch to the light mode), which only the way back can remove.
 */
export function hasConsoleSetup(settings, security, leftovers = false) {
  return settings?.integrationMode === 'full' || !!settings?.isShell || !!security?.autoLogon || security?.autoLogonState === 'lost' || leftovers;
}

/** True when every part of the setup is in place. */
export function isConsoleReady(settings, security) {
  return settings?.integrationMode === 'full' && !!settings?.isShell && lockStepStatus(security) === 'done' && (!!security?.autoLogon || security?.accountPassword === false);
}
