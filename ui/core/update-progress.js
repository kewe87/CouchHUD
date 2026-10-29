// The progress line of an app update ("App 1 of 2 · Name · 45 %"). The host reads winget's output and sends the step with the line
// (see WingetProgressParser): which package of how many, its name and the phase. winget's text is untrusted: the name is only ever
// shown as text (textContent), stripped of control characters and cut here once more, and the numbers are range checked.

// i18n-used: downloads.step downloads.step.prepare downloads.step.download downloads.step.install
export const MAX_STEP_NAME = 60;
const PHASES = new Set(['prepare', 'download', 'install']);
// Control characters and everything that is invisible or reorders text: zero width and bidi marks, Arabic letter mark, mongolian vowel
// separator, word joiner and invisible operators, and the tag characters.
const HIDDEN = /[\u0000-\u001f\u007f-\u009f؜᠎​-‏‪-‮⁠-⁤⁦-⁩\u{e0000}-\u{e007f}]/gu;

/** At most `max` characters; a surrogate pair is never cut in two (counted in code points). */
const cut = (text, max) => Array.from(text).slice(0, max).join('');

/** A step from the host made safe, or null when it is not one. */
export function cleanStep(step) {
  if (!step || typeof step !== 'object') return null;
  const total = Number.isInteger(step.total) ? step.total : 0;
  const index = Number.isInteger(step.index) ? step.index : 0;
  if (index < 1 || total < 1 || total > 999) return null;
  const name = typeof step.name === 'string'
    ? cut(step.name.replace(HIDDEN, ' ').replace(/\s+/g, ' ').trim(), MAX_STEP_NAME)
    : '';
  return { index: Math.min(index, total), total, name, phase: PHASES.has(step.phase) ? step.phase : 'prepare' };
}

/**
 * The text of the line, or null when the entry has no step (then the page builds its usual line).
 * `tr` is the page's t, `percent` its fmtPercent, `size` its formatSize; passed in so this stays a plain function.
 */
export function stepLine(entry, { tr, percent, size }) {
  const step = cleanStep(entry?.step);
  if (!step) return null;
  const parts = [];
  if (step.total > 1) parts.push(tr('downloads.step', { index: step.index, total: step.total }));
  if (step.name) parts.push(step.name);
  const progress = Number.isFinite(entry.progress) ? Math.min(1, Math.max(0, entry.progress)) : null;
  if (step.phase === 'install') parts.push(tr('downloads.step.install'));
  else if (step.phase === 'download' && progress != null) {
    parts.push(percent(progress * 100));
    if (entry.bytesTotal > 0) parts.push(tr('downloads.bytes', { done: size(entry.bytesDone) ?? '0 MB', total: size(entry.bytesTotal) }));
  } else parts.push(tr(step.phase === 'download' ? 'downloads.step.download' : 'downloads.step.prepare'));
  return parts.join(' · ');
}
