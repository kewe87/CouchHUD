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
import { chooseDialog, confirmDialog } from '../components/overlay.js';
import { ensureUnlocked, isLocked, onApprovalAnnounced } from '../components/passcode.js';
import { beginSetupHold, codeLockPlan, lockStepStatus, setupCouchLock } from '../core/lock-rules.js';
import { filterSetupSteps, isConsoleReady } from '../core/wizard-rules.js';
import { createUndoTables } from '../core/mode-undo.js';
import { afterRun, closeAction, recoveryChoices, createModeTransaction, describeRollback, describeTargets, manualUndo, parseJournal } from '../core/mode-transaction.js';
import { applyRollbackToRows, createStepShow, rollbackRowView, runRows, SATISFIED } from '../core/mode-run.js';
import { sound } from '../sound.js';

// Open and put back: an empty box (the tick goes away again). In progress: no glyph, the box shows a ring (styles.css).
const MARK = { done: '✓', ok: '✓', todo: '', unknown: '?', na: '–', running: '', undoing: '', skipped: '–', failed: '✕', aborted: '–', undone: '', unchanged: '–', notundone: '✕', later: '↺' };
// How long a row shows its undo running at least, so an instant undo is still seen going back row by row (it never delays the undo).
const UNDO_BEAT = 250;
const LABEL = () => ({
  done: t('consoleSetup.status.done'),
  ok: t('consoleSetup.status.ok'),
  todo: t('consoleSetup.status.todo'),
  unknown: t('consoleSetup.status.unknown'),
  na: t('consoleSetup.status.na'),
  running: t('consoleSetup.status.running'),
  undoing: t('consoleSetup.status.undoing'),
  skipped: t('consoleSetup.status.skipped'),
  failed: t('consoleSetup.status.failed'),
  aborted: t('consoleSetup.status.aborted'),
  undone: t('consoleSetup.status.undone'),
  unchanged: t('consoleSetup.status.unchanged'),
  notundone: t('consoleSetup.status.notUndone'),
  // The full text is the row's note (the label has a fixed width).
  later: t('consoleSetup.status.laterShort')
});

/** The texts of a row after the undo (core/mode-run.js rollbackRowView). */
const rollbackTexts = () => ({
  notUndone: t('consoleSetup.rollbackNote.notUndone'),
  cancelled: t('consoleSetup.cancelledNote'),
  cancelledAfterUndo: t('consoleSetup.cancelledAfterUndo'),
  undone: t('consoleSetup.status.undone'),
  later: t('consoleSetup.status.later')
});

/** The host's record of a switch that was cut off (null: none, or one that does not pass the checks). */
async function readJournal() {
  try {
    return parseJournal((await api.getSettings())?.modeSwitchJournal ?? '');
  } catch {
    return null;
  }
}

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
const { SETUP_UNDO, REVERT_UNDO } = createUndoTables({ api, state });

/** The steps with their record and undo attached. */
const withUndo = (steps, table) => steps.map((step) => ({ ...step, snapshot: table[step.id]?.snapshot, undo: table[step.id]?.undo }));

/** What a step is about to change; null when it cannot be read (its undo then reports it instead of guessing). */
async function snapshotOf(step, env) {
  try {
    return step.snapshot ? await step.snapshot(env) : null;
  } catch (err) {
    console.warn(`console step ${step.id}: no snapshot (${errorText(err)})`);
    return null;
  }
}

/** { stepId: undo(before) } of a step list; a step without an undo is one only the person can reverse. */
function undoersOf(steps) {
  return Object.fromEntries(steps.map((step) => [step.id, step.undo ? (before) => step.undo(before) : () => { throw manualUndo(); }]));
}

// ---------------------------------------------------------------------------
// Engine

function stepsPage({ id, title, intro, notes, doneNotes = () => [], startLabel, steps, restartAsk, restartLabel, deps, autoStart = false, onDone = null, from = 'app', carriedEntries = [] }) {
  // What the switch changed, to undo it on cancel (core/mode-transaction.js). The host keeps a copy: a crash in the middle is found at the next start.
  const tx = createModeTransaction({ from, entries: carriedEntries, persist: (text) => api.setSetting('modeSwitchJournal', text) });
  const undoers = undoersOf(steps);
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
  let rollbackReport = null; // after an undo that left something behind: the titles
  let rollingNow = null;
  // While an undo is shown: what each row shows (the rows themselves get the result at the end), and the pacing of it.
  let shown = null;
  let show = null;
  let failedRun = false; // the last run ended with a failed or stopped step: only retry or undo

  const page = {
    id,
    name: title,
    index: 0,
    captures: (action) => (phase === 'running' && ['b', 'lb', 'rb', 'start'].includes(action)) || (['done', 'rolling', 'rolled'].includes(phase) && failedRun && action === 'guide'),
    onClose() {
      closed = true;
      cancel = true;
      // The held couch must never outlive the page, in any phase. A failed switch that is still recorded is undone (closing is a cancel;
      // the undo ends the hold), everything else just ends the hold.
      const what = closeAction(phase, tx.pending, failedRun);
      if (what === 'rollback') this.rollbackNow().finally(() => deps.refresh());
      else if (what === 'release') this.releaseHold().finally(() => deps.refresh());
      else deps.refresh();
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
        // An older switch that was cut off is still on record: this one takes it over instead of overwriting it (same direction).
        const older = await readJournal();
        if (older?.from === from) await tx.adopt(older.entries);
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
      // Started by the setup assistant's "Apply", which already said what happens: no second "Start". The unlock above, the hold
      // and the administrator prompts of the steps stay; B during the run stops after the current step.
      if (autoStart && !closed && phase === 'overview') {
        autoStart = false;
        queueMicrotask(() => this.start().catch(console.error));
      }
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
      // A record of the other direction would be overwritten by this switch: it has to be settled first (the next start asks about it).
      const older = await readJournal();
      if (older && older.from !== from) {
        // Settle it first, here and now (same unlock): undo it, or leave it for the next start. Finishing it is another page's job.
        const settled = await resolveInterruptedSwitch(deps, (await api.getSettings())?.modeSwitchJournal ?? '', { chooseDialog, allowFinish: false });
        if (settled === 'undone') state.info.modeSwitchPending = false;
        else {
          toast(t('consoleSetup.recover.waiting'), 9000);
          return;
        }
      }
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
      // Fresh for every run: a successful retry must not carry the verdict of the failed one.
      failedRun = false;
      this.paint();
      const verdict = await runRows(rows, {
        isCancelled: () => cancel,
        cancel: () => { cancel = true; },
        unlock: () => this.unlock(),
        loadEnv,
        readStatus,
        snapshotOf,
        paint: () => this.paint(),
        tx,
        deps,
        texts: { cancelled: t('consoleSetup.cancelledNote'), notUnlocked: t('consoleSetup.notUnlocked'), needsFirst: (title) => t('consoleSetup.needsFirst', { step: title }) }
      });
      failedRun = verdict.failedRun;
      // Stopped by the person (B during the run): that is a cancel, everything done so far is undone.
      const stopped = verdict.stopped;
      // A failed switch keeps the couch open for the one decision (try again or undo) and nothing else: no code again to undo what this
      // very flow changed, and no way to pick another mode from here. Otherwise the lock may close the couch now.
      const next = afterRun(closed, failedRun, tx.pending);
      if (next === 'rollback') {
        // The page was closed during the run and its last step failed: nobody is left to decide, undo it (this also ends the hold).
        phase = 'done';
        await this.rollbackNow();
        return;
      }
      if (next === 'release') {
        await this.releaseHold();
        if (!failedRun) await tx.finish();
      }
      phase = 'done';
      if (stopped && tx.pending) {
        this.paint();
        return this.abandon();
      }
      // The setup assistant writes its marker only now that the run really happened (a restart offered below must not bring it back).
      try { await onDone?.(); } catch (err) { console.error(err); }
      const retry = rows.some((r) => ['failed', 'skipped', 'aborted'].includes(r.status));
      actions = failedRun && tx.pending
        ? [
          { label: t('consoleSetup.action.retry'), run: () => this.retry() },
          { label: t('consoleSetup.action.undo'), run: () => this.abandon() }
        ]
        : [
          ...(restartAsk(rows) ? [{ label: restartLabel, run: () => this.restart() }] : []),
          ...(retry ? [{ label: t('consoleSetup.action.retry'), run: () => this.retry() }] : []),
          { label: restartAsk(rows) ? t('consoleSetup.action.later') : t('consoleSetup.action.done'), run: () => pop() }
        ];
      this.index = rows.length;
      sound.select();
      deps.refresh();
      this.paint();
    },

    async releaseHold() {
      await attempt(() => api.holdUnlockForSetup(false), undefined, { quietLocked: true });
    },

    /** Undoes what this flow recorded, last first. { undone, unchanged, leftover }; the hold goes when it is through. */
    rollbackNow() {
      // The page closing during the undo's code entry asks again: one undo, one report, one end of the hold.
      rollingNow ??= this.rollbackRun().finally(() => { rollingNow = null; });
      return rollingNow;
    },

    async rollbackRun() {
      let result;
      try {
        result = await tx.rollback(undoers, { onStep: (event) => show?.push(event) });
        if (!tx.pending) await tx.finish();
      } finally {
        await this.releaseHold();
      }
      // The undo is through; its report waits until the rows on screen have gone back too (only pacing, nothing runs any more; not when the page is gone).
      if (!closed) await show?.idle();
      // The rows say what really happened to each step, not what the run said before the undo.
      applyRollbackToRows(rows, result, rollbackTexts());
      if (result.leftover.length) {
        const titles = result.leftover.map((l) => steps.find((s) => s.id === l.id)?.title ?? l.id);
        toast(describeRollback(result, (id) => steps.find((s) => s.id === id)?.title ?? id, t).lines.join(' '), 20000, { long: true });
        rollbackReport = titles;
      }
      return result;
    },

    /** Cancel after a failed or stopped switch: back to the exact previous state, and say how far that worked. */
    async abandon() {
      if (phase === 'rolling') return;
      // The hold normally lasts through the decision; if it ran out the couch code is asked again (the lock was not meant to stay open).
      if (!(await this.unlock())) {
        await this.releaseHold();
        return;
      }
      // The page closed during the code entry: onClose has undone it already; nothing more to say or to pop.
      if (closed) return;
      phase = 'rolling';
      waiting = null;
      actions = [];
      this.showUndo();
      this.paint();
      const result = await this.rollbackNow();
      // The rows have gone back on screen (rollbackRun waited for it); the last one stays a moment.
      if (shown && !closed) await new Promise((resolve) => setTimeout(resolve, UNDO_BEAT * 2));
      shown = null;
      show = null;
      if (closed) return;
      if (result.leftover.length) {
        phase = 'rolled';
        actions = [{ label: t('consoleSetup.action.done'), run: () => pop() }];
        this.index = rows.length;
        deps.refresh();
        return this.paint();
      }
      toast(t('consoleSetup.rollback.done'), 6000);
      failedRun = false;
      deps.refresh();
      pop();
    },

    /**
     * The rows go back one by one, from the bottom up, as the undo really runs (the order of tx.rollback): a row shows its undo running,
     * then what came of it, the same text the rows get at the end (rollbackRowView).
     */
    showUndo() {
      const before = rows.map((row) => ({ status: row.status, note: row.note }));
      shown = before.map((view) => ({ ...view }));
      const texts = rollbackTexts();
      show = createStepShow({
        beat: UNDO_BEAT,
        apply: (event) => {
          const at = rows.findIndex((row) => row.step.id === event.id);
          if (at < 0 || !shown) return;
          shown[at] = event.phase === 'start' ? { status: 'undoing', note: shown[at].note } : rollbackRowView(before[at], event.outcome, texts);
          this.paint();
        }
      });
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
      // During an undo the rows show its progress (showUndo), afterwards their own state again.
      const view = (row, at) => (shown?.[at] ? { step: row.step, ...shown[at] } : row);
      this.stepNodes = rows.map((source, at) => {
        const row = view(source, at);
        return el('li', { className: `check-row console-step status-${phase === 'loading' ? 'pending' : row.status}` },
          el('span', { className: 'check-box', textContent: phase === 'loading' ? '' : MARK[row.status] ?? '' }),
          el('span', { className: 'check-main' },
            el('strong', { textContent: row.step.title }),
            el('small', { textContent: (phase !== 'loading' && row.note) || row.step.detail })),
          el('span', { className: `pill ${phase !== 'loading' && (row.status === 'ok' || row.status === 'done') ? 'on' : 'off'}`, textContent: label(row) }));
      });
      this.actionNodes = actions.map((a) => el('li', { className: 'check-row highlight console-action' },
        el('span', { className: 'check-main' }, el('strong', { textContent: a.label }))));
      const heading = phase === 'rolling' ? t('consoleSetup.rollback.running') : phase === 'rolled' ? t('consoleSetup.rollback.headingPartial') : phase === 'done' ? t('consoleSetup.headingDone', { summary: this.summary() }) : phase === 'running' ? t('consoleSetup.headingRunning') : title;
      const waitText = waiting === 'admin' ? t('consoleSetup.waitingAdmin') : waiting === 'unlock' ? t('consoleSetup.waitingUnlock') : '';
      const lead = waitText ? waitText : phase === 'rolling' ? t('consoleSetup.rollback.leadRunning') : phase === 'rolled' ? t('consoleSetup.rollback.leadPartial') : phase === 'done' && failedRun && tx.pending ? t('consoleSetup.leadFailedUndo') : phase === 'done'
        ? (rows.some((r) => r.status === 'failed') ? t('consoleSetup.leadFailed') : rows.some((r) => ['skipped', 'aborted'].includes(r.status)) ? t('consoleSetup.leadSkipped') : t('consoleSetup.leadAllDone'))
        : phase === 'running' ? t('consoleSetup.leadRunning') : intro;
      const extra = phase === 'overview' ? notes(adminPrompts, lastEnv) : phase === 'rolled' ? [t('consoleSetup.rollback.partial', { steps: (rollbackReport ?? []).join(', ') }), t('consoleSetup.rollback.fix')] : phase === 'done' ? doneNotes(rows) : [];
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
      const running = this.stepNodes.find((n) => n.classList.contains('status-running') || n.classList.contains('status-undoing'));
      if (running) keepInView(this.stepList, running, { instant: true });
    },

    build(root) {
      this.root = root;
      if (phase === 'loading') this.load();
      this.paint();
    },

    update() {
      const nodes = this.focusNodes();
      // No cursor while the run or the undo is in progress.
      setFocused(nodes, ['running', 'loading', 'rolling'].includes(phase) ? -1 : this.index);
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
          // After a failed switch B is the cancel: it undoes, it does not just leave the half-done state behind.
          return phase === 'done' && failedRun && tx.pending ? this.abandon() : pop();
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
      if (phase === 'rolling') return [];
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
  if (env.security.couchLock !== 'code' && env.security.couchLock !== 'windows' && env.security.hasPasscode && codeLockPlan(env.security) !== 'keep') toast(t('consoleSetup.lock.reenterCode'), 8000);
  const resets = deps.codesResets?.() ?? 0;
  const offers = deps.codesResetOffers?.() ?? 0;
  const result = await setupCouchLock({
    security: env.security,
    enroll: () => deps.enrollCodeLock(),
    restore: () => deps.restoreCodeLock(),
    askAgain: () => confirmDialog(t('consoleSetup.lock.codeNotSet'), t('consoleSetup.lock.codeNotSetButton'))
  });
  // The service's code store is damaged: the reset is offered right here, once per step (not again after it was declined in the code
  // dialog of this step); once it went through, the step says so and "Try again" runs it.
  if (result.note === 'codesDamaged' && (deps.codesResetOffers?.() ?? 0) === offers) await deps.offerCodesReset?.(t('consoleSetup.lock.codesDamaged'));
  if (result.status === 'failed' && (deps.codesResets?.() ?? 0) > resets) return failed(t('consoleSetup.lock.codesReset'));
  // i18n-used: consoleSetup.lock.windowsNote consoleSetup.lock.codeNoPassword consoleSetup.lock.existingActive consoleSetup.lock.codeActive
  // i18n-used: consoleSetup.lock.notSaved consoleSetup.lock.noCode consoleSetup.lock.legacyConfirmFirst consoleSetup.lock.codesDamaged
  const note = `consoleSetup.lock.${result.note}`;
  return { status: result.status, note: t(note) };
}

/**
 * pauseServices false: the services step only prepares the lock's helper tasks (what it always does when no service is suggested) and
 * pauses nothing. The lock's Windows policies need those tasks, so the step is never left out of a setup that sets up the lock.
 */
function setupSteps({ pauseServices = true } = {}) {
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
      title: pauseServices ? t('consoleSetup.step.services.title') : t('consoleSetup.step.servicesTasks.title'),
      detail: pauseServices ? t('consoleSetup.step.services.detail') : t('consoleSetup.step.servicesTasks.detail'),
      requires: ['integration'],
      admin: true,
      check: async () => {
        const data = await api.getServices();
        const suggested = pauseServices ? (data?.services ?? []).filter((s) => s.suggested) : [];
        // The same confirmation also sets up the tasks for the lock's Windows policies.
        if (!suggested.length) return data?.policyHelper === false || data?.cleanupNoteCurrent === false ? 'todo' : 'na';
        // A note of the lock's empty Policies keys that is out of date also counts as not done: only a setup run (re)writes it.
        const complete = suggested.every((s) => s.selected) && data.taskInstalled && !data.pending && !data.legacyTask && data.cleanupNoteCurrent !== false;
        return complete ? 'done' : 'todo';
      },
      run: async () => {
        const data = await api.getServices();
        if (data.protectedFolder === false) return skipped(t('consoleSetup.step.services.unprotected'));
        if (pauseServices) for (const s of data.services.filter((x) => x.suggested && !x.selected)) await api.toggleCouchService(s.name);
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

/**
 * skip: step ids the setup leaves out because the user declined them in the setup assistant (automatic sign-in, the Steam and
 * startup changes, the services). The steps that must go together stay: lock, shell and integration are never skipped.
 */
export function consoleSetupPage(deps, { skip = [], pauseServices = true, carried = [], autoStart = false, onDone = null, carriedEntries = [] } = {}) {
  return stepsPage({
    id: 'console-setup',
    title: t('consoleSetup.setup.title'),
    intro: t('consoleSetup.setup.intro'),
    autoStart,
    onDone,
    // The last page says what was done and what failed (also what the assistant did before: `carried`); the way back stays in sight.
    doneNotes: () => [...carried, t('wizard.result.undo')],
    notes: (admin, env) => [
      ...(env?.security?.accountPassword === false ? [t('consoleSetup.setup.needPassword')] : []),
      (admin === 0 ? t('consoleSetup.setup.adminNone') : admin === 1 ? t('consoleSetup.setup.adminOnce') : t('consoleSetup.setup.adminMany', { count: admin })),
      t('consoleSetup.setup.way'),
      t('consoleSetup.setup.bitlocker')
    ],
    startLabel: t('consoleSetup.setup.start'),
    // Never left out: integration, lock and shell go together, and the services step (at least its lock tasks) belongs to the lock.
    steps: withUndo(filterSetupSteps(setupSteps({ pauseServices }), skip), SETUP_UNDO),
    from: 'app',
    carriedEntries,
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

export function consoleRevertPage(deps, wasShell, { carried = [], autoStart = false, onDone = null, carriedEntries = [] } = {}) {
  return stepsPage({
    id: 'console-revert',
    title: t('consoleSetup.revert.title'),
    intro: t('consoleSetup.revert.intro'),
    autoStart,
    onDone,
    doneNotes: () => carried,
    notes: (admin) => [
      admin === 0 ? t('consoleSetup.revert.adminNone') : admin === 1 ? t('consoleSetup.revert.adminOnce') : t('consoleSetup.revert.adminMany', { count: admin }),
      t('consoleSetup.revert.after')
    ],
    startLabel: t('consoleSetup.revert.start'),
    steps: withUndo(revertSteps(), REVERT_UNDO),
    from: 'shell',
    carriedEntries,
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

/**
 * A mode switch that was cut off (crash, power loss, a closed window) is found at the next start by the host's record. After the unlock
 * the person chooses: undo what it changed (back to the exact previous mode), finish it (the guided run again, which does only what is
 * missing), or decide later (the record stays). Resolves to 'undone', 'finish', 'later' or 'partial'.
 */
export async function resolveInterruptedSwitch(deps, journalText, { chooseDialog, allowFinish = true }) {
  const record = parseJournal(journalText);
  if (!record) {
    await attempt(() => api.setSetting('modeSwitchJournal', ''), undefined, { quietLocked: true });
    return 'later';
  }
  // i18n-used: consoleSetup.recover.undo consoleSetup.recover.undoDetail consoleSetup.recover.finish consoleSetup.recover.finishDetail
  const all = [
    { label: t('consoleSetup.recover.undo'), detail: t('consoleSetup.recover.undoDetail', { steps: describeTargets([...record.entries].reverse(), t).join(' · ') }), value: 'undo' },
    { label: t('consoleSetup.recover.finish'), detail: t('consoleSetup.recover.finishDetail'), value: 'finish' }
  ];
  const options = all.filter((o) => recoveryChoices(allowFinish).includes(o.value));
  const pick = await chooseDialog(t('consoleSetup.recover.title'), options, 'undo');
  if (pick === 'finish') return { choice: 'finish', record };
  if (pick !== 'undo') return 'later';
  if (!(await ensureUnlocked(t('consoleSetup.recover.title')))) return 'later';
  const steps = record.from === 'shell' ? withUndo(revertSteps(), REVERT_UNDO) : withUndo(setupSteps(), SETUP_UNDO);
  const tx = createModeTransaction({ from: record.from, entries: record.entries, persist: (text) => api.setSetting('modeSwitchJournal', text) });
  const result = await tx.rollback(undoersOf(steps));
  if (!tx.pending) await tx.finish();
  deps.refresh?.();
  if (!result.leftover.length) {
    toast(t('consoleSetup.rollback.done'), 6000);
    return 'undone';
  }
  toast(describeRollback(result, (id) => steps.find((s) => s.id === id)?.title ?? id, t).lines.join(' '), 20000, { long: true });
  return 'partial';
}

export { isConsoleReady };
