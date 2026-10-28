// Pure rules of the TV page, without DOM (tested by scripts/ui-logic-check.mjs). The host checks all of it again (TvPolicy, TvManager).

/**
 * Switching the TV's input, after the input was chosen: first the unlocked couch, then, with parental controls, the parents' code
 * for exactly this input (action "tvInput", item = the input id; the host spends the release only on that id).
 * The dialogs are passed in: ensureUnlocked(reason) and ensureParentGrant(reason, scope), both resolving to true or false.
 */
export async function admitInputSwitch({ id, parentalOn, reason, parentReason, ensureUnlocked, ensureParentGrant }) {
  if (!id) return false;
  if (!(await ensureUnlocked(reason))) return false;
  if (!parentalOn) return true;
  return !!(await ensureParentGrant(parentReason, { action: 'tvInput', item: id }));
}

/**
 * What the page does with the answer of tvPair. The error comes first: a TV without TLS port answers with the old pairing still
 * in place. 'askPlain': only in expert mode, an explicit question before an unencrypted pairing; 'plainRefused': not offered;
 * 'pairedNewCert': paired, but the TV now shows another certificate than the one paired before (said clearly).
 */
export function pairOutcome(status, expert) {
  if (!status) return 'failed';
  if (status.error === 'plainOnly') return expert ? 'askPlain' : 'plainRefused';
  if (status.error) return 'failed';
  if (status.paired && status.certReplaced) return 'pairedNewCert';
  return status.paired ? 'paired' : 'failed';
}
