// The run loop of the App/Shell mode switch and the bookkeeping after an undo (pure, no DOM; the page in views/console-setup.js passes its
// helpers in, scripts/ui-logic-check.mjs passes fakes). A row is { step, status, note }.

export const SATISFIED = new Set(['done', 'ok', 'na']);

/**
 * Runs the rows in order. ctx: { isCancelled(), cancel(), unlock(), loadEnv(), readStatus(step, env), snapshotOf(step, env), paint(), tx, deps,
 * texts: { cancelled, notUnlocked, needsFirst(title) } }.
 *
 * A step that comes back 'aborted' is the person saying no to something the switch cannot do without (the couch code of the shell
 * setup): that is a cancel exactly like B during the run, not a skipped step. The rest of the rows are marked cancelled and the caller
 * undoes what was recorded. The cancel is only looked at between two steps; a step in flight always ends first and is already recorded.
 */
export async function runRows(rows, ctx) {
  for (const row of rows) {
    if (ctx.isCancelled()) {
      if (!SATISFIED.has(row.status)) {
        row.status = 'aborted';
        row.note = ctx.texts.cancelled;
      }
      continue;
    }
    if (SATISFIED.has(row.status)) continue;
    const missing = (row.step.requires ?? []).map((rid) => rows.find((r) => r.step.id === rid)).find((r) => r && !SATISFIED.has(r.status));
    if (missing) {
      row.status = 'skipped';
      row.note = ctx.texts.needsFirst(missing.step.title);
      continue;
    }
    // The release of the couch lock is short; ask again whenever it ran out between two steps.
    if (!(await ctx.unlock())) {
      ctx.cancel();
      row.status = 'aborted';
      row.note = ctx.texts.notUnlocked;
      continue;
    }
    let result;
    try {
      // Right before: what the overview saw may be old (an earlier step, the lock) or was never readable.
      const env = await ctx.loadEnv();
      const again = await ctx.readStatus(row.step, env);
      if (SATISFIED.has(again)) {
        row.status = again;
        row.note = '';
        ctx.paint();
        continue;
      }
      row.status = 'running';
      row.note = row.step.working ?? '';
      ctx.paint();
      // Recorded before it runs (a step that fails half way has changed something too); a retry keeps the first record.
      await ctx.tx.begin(row.step.id, await ctx.snapshotOf(row.step, env));
      result = await row.step.run(env, ctx.deps);
    } catch (err) {
      console.error(err);
      result = { status: 'failed', note: err?.message || String(err) };
    }
    row.status = result.status;
    row.note = result.note ?? '';
    if (result.status === 'aborted') ctx.cancel();
    ctx.paint();
  }
  return runVerdict(rows, ctx.isCancelled());
}

/** failedRun: some row failed or was stopped (only retry or undo then). stopped: the person cancelled (B, a refused code, an aborted required step). */
export function runVerdict(rows, cancelled) {
  const failedRun = rows.some((r) => ['failed', 'aborted'].includes(r.status));
  return { failedRun, stopped: cancelled && rows.some((r) => r.status === 'aborted') };
}

/** What the undo did with the step of `id`: 'leftover', 'later', 'undone', 'unchanged' or null (never recorded, never touched). */
export function rollbackOutcome(result, id) {
  if (result.leftover.some((l) => l.id === id)) return 'leftover';
  if (result.later?.includes(id)) return 'later';
  if (result.undone.includes(id)) return 'undone';
  if (result.unchanged.includes(id)) return 'unchanged';
  return null;
}

/**
 * How a row reads after the undo of its step ({ status, note } of the row before the undo, the outcome of rollbackOutcome). Pure: the page
 * shows it row by row while the undo runs and applyRollbackToRows sets the same at the end, so both always agree.
 */
export function rollbackRowView(row, outcome, texts) {
  if (outcome === 'leftover') return { status: 'notundone', note: texts.notUndone };
  // The short label of the row says "later"; the note says what happens.
  if (outcome === 'later') return { status: 'later', note: texts.later ?? '' };
  if (outcome === 'undone') {
    // A stopped step that was then undone keeps its reason and adds what happened to it.
    const reason = row.status === 'aborted' && row.note !== texts.cancelled ? row.note : '';
    return { status: 'undone', note: reason ? `${reason} · ${texts.undone}` : '' };
  }
  // Stopped by the person: the reason stays (no code was entered); the generic "try again carries on" is not true after an undo.
  if (row.status === 'aborted') return { status: 'aborted', note: row.note === texts.cancelled ? texts.cancelledAfterUndo : row.note };
  if (outcome === 'unchanged') return { status: 'unchanged', note: '' };
  return { status: row.status, note: row.note };
}

/**
 * After an undo the rows say what really happened to each recorded step: 'undone' (put back), 'unchanged' (found as recorded already, e.g.
 * a step that was skipped or changed nothing) or 'notundone' (left over, with the reason). A row of a step that never ran is left as it is.
 */
export function applyRollbackToRows(rows, result, texts) {
  for (const row of rows) Object.assign(row, rollbackRowView(row, rollbackOutcome(result, row.step.id), texts));
}

/**
 * Shows the progress of an undo one row at a time (the events of tx.rollback's onStep). apply(event) paints it; after a 'start' the next
 * event waits `beat` ms so even an instant undo is seen running, row by row. It only paces what is shown: the undo itself never waits for
 * it (push returns at once). idle() resolves when everything pushed so far is shown.
 */
export function createStepShow({ apply, beat = 250, wait = (ms) => new Promise((resolve) => setTimeout(resolve, ms)) }) {
  const queue = [];
  let pumping = null;
  const pump = async () => {
    while (queue.length) {
      const event = queue.shift();
      try {
        apply(event);
      } catch (error) {
        console.error(error);
      }
      if (event.phase === 'start' && beat > 0) await wait(beat);
    }
    // Set back in the same turn as the empty queue was seen: a push after this starts a new pump.
    pumping = null;
  };
  return {
    push(event) {
      queue.push(event);
      // Started in a microtask, so `pumping` is set before the pump can end.
      pumping ??= Promise.resolve().then(pump);
    },
    async idle() {
      while (pumping) await pumping;
    }
  };
}
