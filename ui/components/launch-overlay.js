// Launch transition: while a game or app starts, the cover zooms up over a dimmed backdrop with
// the logo and "Wird gestartet …", like on consoles. It stays until the game is in front
// (session event or the HUD losing focus) or LIMIT_MS passed. B hides it (the start goes on).

import { el, loadFirst } from '../core/dom.js';
import { artwork } from '../core/art.js';
import { glyph } from '../core/skins.js';
import { toast } from '../core/feedback.js';
import { setMotionActive } from '../core/motion.js';
import { t } from '../core/i18n.js';

const LIMIT_MS = 20_000;
const GRACE_MS = 900; // after the session event: the game window may still be on its way
const FADE_MS = 380;

let shown = null; // { item, node, timers }

const verb = (item) => (item.type === 'app' ? t('launchOverlay.opening') : t('launchOverlay.starting'));

function build(item) {
  const heroUrls = [...(item.art?.hero || []), ...(item.art?.cover || [])];
  const backdrop = el('div', { className: 'launch-backdrop' });
  loadFirst(heroUrls, (url) => {
    backdrop.style.backgroundImage = `url("${url}")`;
    backdrop.classList.add('loaded');
  });

  const title = el('h1', { className: 'launch-title', textContent: item.name });
  loadFirst(item.art?.logo, (url) => {
    const img = el('img', { className: 'launch-logo', src: url, alt: item.name });
    title.replaceWith(img);
  });

  return el('div', { id: 'launch', className: 'launch', role: 'status', 'aria-live': 'polite' },
    backdrop,
    el('div', { className: 'launch-shade' }),
    el('div', { className: 'launch-stage' },
      el('div', { className: 'launch-card' }, artwork(item, 'cover', 'cover'), el('span', { className: 'launch-shine' })),
      el('div', { className: 'launch-info' },
        title,
        el('p', { className: 'launch-status' }, el('span', { className: 'launch-ring' }), `${verb(item)} `, el('span', { className: 'launch-dots', textContent: '…' })),
        el('div', { className: 'launch-bar' }, el('i')),
        el('p', { className: 'launch-hint' }, el('span', { className: 'glyph btn-b', dataset: { glyph: 'B' }, textContent: glyph('B') }), t('launchOverlay.hide')))));
}

/** Shows the transition for `item`; a second call replaces the first. */
export function showLaunch(item) {
  hideLaunch({ instant: true });
  setMotionActive(false, 'launch');
  const node = build(item);
  document.body.append(node);
  void node.offsetWidth;
  node.classList.add('in');
  const timers = [setTimeout(timeout, LIMIT_MS)];
  shown = { item, node, timers };
}

function timeout() {
  if (!shown) return;
  toast(t('launchOverlay.background'), 4000);
  hideLaunch();
}

/** Fades the transition out. `instant` skips the fade (focus lost: nobody sees it anyway). */
export function hideLaunch({ instant = false } = {}) {
  if (!shown) return;
  const { node, timers } = shown;
  shown = null;
  setMotionActive(true, 'launch');
  timers.forEach(clearTimeout);
  if (instant) return node.remove();
  node.classList.remove('in');
  node.classList.add('out');
  setTimeout(() => node.remove(), FADE_MS + 60);
}

export const launching = () => !!shown;

/** The start itself failed: take the transition away right away. */
export function cancelLaunch(item) {
  if (item && shown?.item !== item) return;
  hideLaunch();
}

/** Input while the transition is up: B hides it, everything else is swallowed. */
export function launchInput(action) {
  if (!shown) return false;
  // The guide and power menus must never open invisibly underneath.
  if (action === 'guide' || action === 'start') {
    hideLaunch();
    return false;
  }
  if (action === 'b') hideLaunch();
  return true;
}

/** Session event from the host: the launched item is running. */
export function launchSession(session) {
  if (!shown || !session || session.id !== shown.item.id) return;
  shown.timers.push(setTimeout(() => hideLaunch(), GRACE_MS));
}

/** The host found no window for a URL/protocol start within its wait: say so instead of hanging on. */
export function launchTimedOut() {
  if (!shown) return;
  toast(t('launchOverlay.noWindow'), 5000);
  hideLaunch();
}

/** The HUD lost the foreground: the game (or app) is in front now. */
export function launchFocus(active) {
  if (!active) hideLaunch({ instant: true });
}
