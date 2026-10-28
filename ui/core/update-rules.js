// Pure rules of the Updates page, without DOM (tested by scripts/ui-logic-check.mjs).
// The host only sends a Windows update's size when it is known well enough (UpdateSize.Shown in Core): the agent's largest package is
// gigabytes for a few MB of express download, so an uncertain size comes as null and is never guessed here.

/** A size the page may show: a finite number of bytes above 0; anything else (null, NaN, negative, text) is unknown. */
export function validSize(bytes) {
  return typeof bytes === 'number' && Number.isFinite(bytes) && bytes > 0 ? bytes : null;
}

/** The size on one update's line: none for an update that is already downloaded, none when unknown. */
export function itemSize(update) {
  return update?.downloaded ? null : validSize(update?.sizeBytes);
}

/**
 * The summary line of the Windows updates: how many, how many are already downloaded, and a total only when every update still to
 * load has a known size (a sum of only some of them would look like the whole download).
 */
export function updateSummary(updates) {
  const list = Array.isArray(updates) ? updates : [];
  const pending = list.filter((u) => !u?.downloaded);
  const sizes = pending.map(itemSize);
  const complete = sizes.every((s) => s !== null);
  const size = complete ? sizes.reduce((sum, s) => sum + s, 0) : null;
  return { count: list.length, downloaded: list.length - pending.length, size: size && Number.isFinite(size) ? size : null };
}

const RUNNING = new Set(['waiting', 'downloading', 'installing', 'inClient', 'paused', 'queued']);

/**
 * What the Downloads tab offers to start, from what the Updates page knows: Windows updates (all at once, as the Updates page does),
 * app updates (all), CouchHUD itself and each plugin or skin with a newer version. Something that already runs as a line of its own
 * (the same id the host uses) is not offered again, nor something whose line finished (done) after the page last heard of that kind of
 * update (`seen`: ms of the last news per kind): the list it came from is old until the host sends the next one.
 */
export function pendingDownloads({ updates = null, selfUpdate = null, catalogUpdates = [], seen = {} } = {}, entries = []) {
  const running = new Set((entries ?? []).filter((e) => RUNNING.has(e?.state)).map((e) => e.id));
  const doneAt = new Map();
  for (const e of entries ?? []) {
    if (e?.state === 'done' && typeof e.updatedAt === 'number') doneAt.set(e.id, Math.max(doneAt.get(e.id) ?? 0, e.updatedAt));
  }
  const at = (kind) => (typeof seen?.[kind] === 'number' ? seen[kind] : 0);
  const hidden = (id, kind) => running.has(id) || (doneAt.has(id) && doneAt.get(id) > at(kind));
  const list = [];
  if (updates?.windows?.length && !hidden('update:windows', 'updates')) list.push({ id: 'update:windows', kind: 'windows' });
  if (updates?.apps?.length && !hidden('update:apps', 'updates')) list.push({ id: 'update:apps', kind: 'apps' });
  if (selfUpdate?.available && !hidden('couchhud:self', 'selfUpdate')) list.push({ id: 'couchhud:self', kind: 'self' });
  for (const c of catalogUpdates ?? []) {
    const id = `catalog:${c?.kind}:${c?.id}`;
    if (c?.id && !hidden(id, 'catalogUpdates')) list.push({ id, kind: 'catalog', entry: c });
  }
  return list;
}

/**
 * The actions of one line of the Downloads tab, the first one on A: only what really works for its source. CouchHUD's own download
 * while it can be stopped: cancel (no pause: a cancelled setup is downloaded and verified again in full). Steam: its own download
 * page, where pausing and resuming work (Steam has no documented way to do it from outside). Windows Update and winget: nothing while
 * they run (no cancel through the service yet; stopping an installer midway can break the app). Finished lines and requests: remove.
 */
export function downloadActions(entry) {
  if (!entry) return [];
  const finished = entry.state === 'done' || entry.state === 'failed';
  const request = entry.state === 'waiting' || entry.state === 'inClient' || (entry.state === 'installing' && entry.source === 'epic');
  const actions = [];
  if (!finished && entry.cancellable === true) actions.push('cancel');
  if (!finished && entry.kind === 'game' && entry.source === 'steam') actions.push('openClient');
  if (finished || request) actions.push('remove');
  return actions;
}
