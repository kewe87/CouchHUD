// The App/Shell mode switch as a transaction (pure, no DOM; checked by scripts/ui-logic-check.mjs).
//
// Every step that is about to change something records what it found first (`begin`, before it runs: a step that fails half way has
// changed something too). A retry never stacks: a step that is already recorded keeps its first record. Cancelling or accepting a
// failure calls `rollback`: the recorded steps are undone in reverse order, each undo looks at the state as it is now and touches only
// what differs from the record, and what could not be undone is returned as a leftover, never hidden. The record is also kept by the host
// (`persist`) so a crash in the middle is found at the next start.

const MAX_JOURNAL = 4096;

/** The error an undo throws for a step that only the person can reverse (an administrator's approval, a password): reported, never retried. */
export const manualUndo = (note = '') => Object.assign(new Error('manual'), { manual: true, note });

// ---------------------------------------------------------------------------
// What a record may say. The settings file is writable by every program of the account, so a record is never trusted: only these step
// ids and keys, only values of the known sets or bounded plain names. The host checks the same (ModeSwitchJournal.IsValid) and the page
// checks again before it calls a bridge method with a value from a record.

const isBool = (v) => typeof v === 'boolean';
const isName = (v) => typeof v === 'string' && v.length > 0 && v.length <= 96 && !/[\u0000-\u001f\u007f]/.test(v);
const names = (v) => Array.isArray(v) && v.length <= 24 && v.every(isName);
const modes = (v) => ['off', 'code', 'windows'].includes(v);
const integration = (v) => ['light', 'full'].includes(v);
const SHAPES = {
  integration: { m: integration },
  autostart: { r: isBool, e: (v) => Array.isArray(v) && v.length <= 24 && v.every((p) => Array.isArray(p) && p.length === 2 && isName(p[0]) && ['couch', 'pc'].includes(p[1])) },
  services: { k: isBool, s: names },
  xbox: { v: isBool },
  suppress: { v: isBool },
  lock: { m: modes, c: isBool },
  shell: { s: isBool },
  autologon: { on: isBool },
  release: { p: isBool },
  profiles: { p: names },
  light: { m: integration, s: isBool, l: modes }
};

/** Whether `before` is a record this step could have written: null (nothing recorded) or an object of exactly its known keys and values. */
export function validBefore(id, before) {
  const shape = Object.hasOwn(SHAPES, id) ? SHAPES[id] : null;
  if (!shape) return false;
  if (before === null || before === undefined) return true;
  if (typeof before !== 'object' || Array.isArray(before)) return false;
  return Object.entries(before).every(([key, value]) => Object.hasOwn(shape, key) && shape[key](value));
}

/** The host's record text for a transaction ('' when nothing is pending). */
export function journalText(from, entries) {
  if (!entries.length) return '';
  const text = JSON.stringify({ from, steps: entries.map((e) => ({ id: e.id, before: e.before ?? null })) });
  return text.length <= MAX_JOURNAL ? text : JSON.stringify({ from, steps: entries.map((e) => ({ id: e.id, before: null })) });
}

/** Reads the host's record; null for anything that is not one. */
export function parseJournal(text) {
  if (typeof text !== 'string' || !text) return null;
  try {
    const data = JSON.parse(text);
    if (!data || !['app', 'shell'].includes(data.from) || !Array.isArray(data.steps)) return null;
    if (data.steps.length > 16 || Object.keys(data).some((k) => !['from', 'steps'].includes(k))) return null;
    const seen = new Set();
    const entries = [];
    for (const step of data.steps) {
      // One forged value and the whole record is refused (nothing is restored from it).
      if (!step || typeof step.id !== 'string' || seen.has(step.id) || !validBefore(step.id, step.before ?? null)) return null;
      seen.add(step.id);
      entries.push({ id: step.id, before: step.before ?? null });
    }
    return entries.length ? { from: data.from, entries } : null;
  } catch {
    return null;
  }
}

/**
 * from: the mode the switch starts in ('app' or 'shell'). entries: records of an earlier run (a crash). persist(text): the host's copy,
 * called after every change; a failure of it never stops the switch or the rollback.
 */
export function createModeTransaction({ from, entries = [], persist = async () => {} }) {
  const list = entries.filter((e) => validBefore(e.id, e.before ?? null)).map((e) => ({ id: e.id, before: e.before ?? null }));
  const save = async () => {
    try {
      await persist(journalText(from, list));
    } catch {
      // The record is a safety net; the switch goes on without it.
    }
  };
  let running = null;
  return {
    from,
    get pending() { return list.length > 0; },
    ids: () => list.map((e) => e.id),
    has: (id) => list.some((e) => e.id === id),
    /** Records the state before a step runs. False when the step is recorded already (a retry): the first record stays. */
    async begin(id, before) {
      if (list.some((e) => e.id === id)) return false;
      list.push({ id, before: validBefore(id, before ?? null) ? before ?? null : null });
      await save();
      return true;
    },
    /** Takes over the records of an older run that was cut off (a new switch must not overwrite its journal): own records stay, older ones come first. */
    async adopt(older) {
      const known = new Set(list.map((e) => e.id));
      const add = older.filter((e) => !known.has(e.id) && validBefore(e.id, e.before ?? null)).map((e) => ({ id: e.id, before: e.before ?? null }));
      if (!add.length) return;
      list.unshift(...add);
      await save();
    },
    /**
     * Undoes the recorded steps, last first. undoers[id](before) resolves to 'undone' or 'unchanged' (it found the state as recorded
     * already) and throws when it cannot. Returns { undone, unchanged, leftover: [{ id, error }] }; the leftovers stay recorded.
     * onStep({ id, phase: 'start' }) and onStep({ id, phase: 'end', outcome: 'undone' | 'unchanged' | 'later' | 'leftover' }) report the
     * progress as it happens (the page shows it row by row); it only watches: an error in it never stops or delays the undo.
     */
    rollback(undoers, { onStep } = {}) {
      // One undo at a time: a second caller (the page closing while the undo's code entry is open) gets the running one, never a second run.
      running ??= this.rollbackNow(undoers, onStep).finally(() => { running = null; });
      return running;
    },
    async rollbackNow(undoers, onStep) {
      const result = { undone: [], unchanged: [], later: [], leftover: [] };
      const report = (event) => {
        try {
          onStep?.(event);
        } catch (error) {
          console.error(error);
        }
      };
      for (const entry of [...list].reverse()) {
        const undo = undoers[entry.id];
        report({ id: entry.id, phase: 'start' });
        try {
          if (typeof undo !== 'function') throw manualUndo();
          // The page checks the record once more before a value of it reaches the host.
          if (!validBefore(entry.id, entry.before)) throw new Error('invalid record');
          const outcome = await undo(entry.before);
          const kind = outcome === 'unchanged' ? 'unchanged' : outcome === 'later' ? 'later' : 'undone';
          result[kind].push(entry.id);
          list.splice(list.indexOf(entry), 1);
          report({ id: entry.id, phase: 'end', outcome: kind });
          await save();
        } catch (error) {
          result.leftover.push({ id: entry.id, error: error?.message || String(error), manual: !!error?.manual, note: error?.note || '' });
          report({ id: entry.id, phase: 'end', outcome: 'leftover' });
          // What no program can give back is reported once and not asked about at every start; a failed undo stays recorded.
          if (error?.manual) {
            list.splice(list.indexOf(entry), 1);
            await save();
          }
        }
      }
      return result;
    },
    /** The switch ended as wanted: nothing to undo any more. */
    async finish() {
      list.length = 0;
      await save();
    }
  };
}

/**
 * What the person is told after an undo: nothing left means "everything is back as it was"; otherwise the leftovers by name and how to
 * continue. t(key, params) and titleOf(id) are passed in. Resolves to { ok, lines }.
 */
export function describeRollback(result, titleOf, t) {
  if (!result.leftover.length) return { ok: true, lines: [t('consoleSetup.rollback.done')] };
  const steps = result.leftover.map((l) => titleOf(l.id)).join(', ');
  // i18n-used: consoleSetup.rollback.lockCode consoleSetup.rollback.lockMode
  const notes = ['lockCode', 'lockMode'].filter((n) => result.leftover.some((l) => l.note === n)).map((n) => t(n === 'lockCode' ? 'consoleSetup.rollback.lockCode' : 'consoleSetup.rollback.lockMode'));
  return { ok: false, lines: [t('consoleSetup.rollback.partial', { steps }), ...notes, t('consoleSetup.rollback.fix')] };
}

/** The choices of the interrupted-switch dialog. "finish" opens the page of the record's own direction, so a caller that is already on a run of the other direction hides it. */
export const recoveryChoices = (allowFinish = true) => (allowFinish ? ['undo', 'finish'] : ['undo']);

/**
 * What to do with the held couch when the page closes. 'rollback': a failed switch is still recorded, so closing is a cancel (the hold is
 * still on and the undo ends it). 'release': end the hold (it must never outlive the page, whatever phase the page was in, a retry that
 * never got to run included). 'none': a run or an undo is in progress and ends the hold itself.
 */
export function closeAction(phase, pending, failedRun) {
  if (phase === 'running' || phase === 'rolling') return 'none';
  // After an undo that left something behind ('rolled') it already ran once: the record stays for the next start, which asks again. It must
  // not run again silently on close (another approval prompt), so the hold just ends.
  if (phase === 'rolled') return 'release';
  // A failed run that is still recorded and a close without a decision count as a cancel (decided on purpose: a half-set-up PC must not be left
  // behind by closing the page); the undo ends the hold.
  return pending && failedRun ? 'rollback' : 'release';
}

/**
 * What a run does with the held couch when its last step is through. `closed`: the page went away while the run was in flight (nobody is left
 * to decide), so a failed run that is still recorded is undone and anything else just ends the hold. Otherwise a failed run keeps the hold for
 * the one decision on its page ('hold'), and a clean one ends it.
 */
export function afterRun(closed, failedRun, pending) {
  if (failedRun && pending) return closed ? 'rollback' : 'hold';
  return 'release';
}

/**
 * The target state of each recorded step, for the person to read before choosing "undo". Returns lines (t(key, params)); a step whose
 * record is missing says so. The values are localized names, never the raw record text.
 */
export function describeTargets(entries, t) {
  // i18n-used: consoleSetup.recover.valOn consoleSetup.recover.valOff
  const onKey = (v) => (v ? 'consoleSetup.recover.valOn' : 'consoleSetup.recover.valOff');
  const on = (v) => t(onKey(v));
  const to = (id) => `consoleSetup.recover.to.${id}`;
  const lockMode = (m) => t(m === 'code' ? 'settings.lockMode.code' : m === 'windows' ? 'settings.lockMode.windows' : 'settings.common.off');
  const modeName = (m) => t(m === 'full' ? 'settings.mode.console' : 'settings.integration.light');
  // i18n-used: consoleSetup.recover.to.integration consoleSetup.recover.to.autostart consoleSetup.recover.to.services consoleSetup.recover.to.xbox
  // i18n-used: consoleSetup.recover.to.suppress consoleSetup.recover.to.lock consoleSetup.recover.to.shell consoleSetup.recover.to.autologon
  // i18n-used: consoleSetup.recover.to.profiles consoleSetup.recover.to.light consoleSetup.recover.to.release consoleSetup.recover.to.none
  return entries.map(({ id, before }) => {
    if (!before) return t(id === 'release' ? 'consoleSetup.recover.to.release' : 'consoleSetup.recover.to.none', { step: id });
    switch (id) {
      case 'integration': case 'light': return t(to(id), { value: modeName(before.m) });
      case 'lock': return t('consoleSetup.recover.to.lock', { value: lockMode(before.m) });
      case 'xbox': case 'suppress': return t(to(id), { value: on(before.v) });
      case 'shell': return t('consoleSetup.recover.to.shell', { value: on(before.s) });
      case 'autologon': return t('consoleSetup.recover.to.autologon', { value: on(before.on) });
      default: return t(to(id), { value: '' });
    }
  });
}
