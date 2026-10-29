// Where a notification of CouchHUD itself leads: a fixed whitelist of screens, nothing else (pure, tested by scripts/ui-logic-check.mjs).

const TARGETS = Object.freeze({ downloads: 'downloads' });

/** The whitelisted target of a notification (exact name, own property only), else null. Never a URL, a path or a prototype member. */
export function notificationTarget(n) {
  const t = n?.target;
  return typeof t === 'string' && Object.hasOwn(TARGETS, t) ? TARGETS[t] : null;
}
