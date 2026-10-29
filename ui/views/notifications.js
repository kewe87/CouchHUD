// Notification center, PlayStation style: a card slides in at the top right,
// the bell in the header counts unread ones, the list lives in the guide menu.

import { api } from '../core/api.js';
import { $, el, keepInView, setFocused } from '../core/dom.js';
import { attempt } from '../core/feedback.js';
import { hue } from '../core/art.js';
import { currentView, move, render, showChipFocus } from '../core/nav.js';
import { activateChip } from '../core/bar-chips.js';
import { notificationTarget } from '../core/notify-rules.js';
import { state } from '../core/state.js';
import { sound } from '../sound.js';
import { timeAgo } from './news.js';
import { t } from '../core/i18n.js';

state.notifications = { items: [], unread: 0, windows: 'off' };

let stackEl;

function bell() {
  let node = $('#notify-bell');
  if (!node) {
    node = el('span', { id: 'notify-bell' });
    $('#status')?.prepend(node);
  }
  const n = state.notifications.unread;
  // Always there (a stable target for LB/RB and the mouse), muted without unread ones.
  node.hidden = false;
  node.classList.toggle('none', n === 0);
  node.textContent = `🔔︎ ${n}`;
  node.title = t('notifications.title');
  node.onclick = () => activateChip('bell');
  showChipFocus();
}

/** Where a notification of CouchHUD itself leads: a fixed whitelist of screens (the host sets it for its own notices only). Anything else is ignored. */
const GO = { downloads: () => activateChip('downloads') };

/** Opens the notification's target; false when it has none or an unknown one. Showing the screen installs nothing. */
export function openNotificationTarget(n) {
  const target = notificationTarget(n);
  if (!target) return false;
  return GO[target]() === true;
}

/** Initials in a colored bubble stand in for app icons. */
export function appBadge(app) {
  const badge = el('span', { className: 'notify-app', textContent: (app || '?').slice(0, 1).toUpperCase() });
  badge.style.setProperty('--h', hue(app || '?'));
  return badge;
}

function popup(n) {
  if (!stackEl) {
    stackEl = el('div', { id: 'notify-stack' });
    document.body.append(stackEl);
  }
  const card = el('div', { className: `notify-card${n.security ? ' security' : ''}${n.target ? ' has-target' : ''}`, onclick: () => openNotificationTarget(n) },
    appBadge(n.app),
    el('div', { className: 'notify-text' },
      el('small', { textContent: n.app }),
      el('strong', { textContent: n.title }),
      n.body ? el('p', { textContent: n.body }) : null));
  stackEl.prepend(card);
  // Never more than three at once; the list keeps the rest.
  while (stackEl.children.length > 3) stackEl.lastElementChild.remove();
  setTimeout(() => {
    card.classList.add('leaving');
    setTimeout(() => card.remove(), 400);
  }, 6000);
}

/** Pages that ask for full attention set `quietNotifications`; cards already on screen hide with them (styles.css). */
const quiet = () => state.stack.some((p) => p.quietNotifications);

export async function startNotifications() {
  const initial = await attempt(() => api.getNotifications());
  if (initial) state.notifications = initial;
  bell();
  api.onNotification?.((n) => {
    state.notifications.items = [n, ...state.notifications.items.filter((x) => x.id !== n.id)].slice(0, 50);
    state.notifications.unread++;
    bell();
    // First start (setup assistant, first-start notice): no card over the questions, only the bell counts. Security notices
    // (lock, parental controls, shell entry, setup, damaged configuration) are flagged by the host and always show.
    if (quiet() && !n.security) return;
    popup(n);
    sound.notify();
    if (currentView()?.id === 'notifications') render();
  });
}

export function notificationsPage() {
  return {
    id: 'notifications',
    get name() { return t('notifications.title'); },
    index: 0,
    onShow() {
      attempt(() => api.markNotificationsRead());
      state.notifications.unread = 0;
      state.notifications.items = state.notifications.items.map((n) => ({ ...n, read: true }));
      bell();
    },
    build(root) {
      if (!this.shown) {
        this.shown = true;
        this.onShow();
      }
      const items = state.notifications.items;
      this.index = Math.min(this.index, Math.max(0, items.length - 1));
      this.rows = items.map((n) => el('li', { className: 'check-row notify-row', onclick: () => openNotificationTarget(n) },
        appBadge(n.app),
        el('span', { className: 'check-main' },
          el('strong', { textContent: n.title }),
          el('small', { textContent: [n.app, n.body, timeAgo(n.time)].filter(Boolean).join(' · ') }))));
      this.scroller = el('ul', { className: 'check-list list-scroll' }, this.rows);
      const status = state.notifications.windows;
      root.append(el('div', { className: 'files' },
        el('div', { className: 'files-head' },
          el('h2', { textContent: t('notifications.title') }),
          status !== 'allowed' ? el('p', { className: 'meta', textContent: t('notifications.tip') }) : null),
        items.length ? this.scroller : el('p', { className: 'empty-note', textContent: t('notifications.empty') })));
      this.update({ instant: true });
    },
    update({ instant = false } = {}) {
      if (!this.rows?.length) return;
      setFocused(this.rows, this.index);
      keepInView(this.scroller, this.rows[this.index], { instant });
    },
    async handle(action) {
      const count = this.rows?.length ?? 0;
      switch (action) {
        case 'up': return move(this, -1, count, true);
        case 'down': return move(this, 1, count, true);
        case 'a':
          if (!count || !openNotificationTarget(state.notifications.items[this.index])) return sound.bump();
          return sound.select();
        case 'y':
          if (!count) return sound.bump();
          await attempt(() => api.clearNotifications());
          state.notifications.items = [];
          sound.back();
          return render();
      }
    },
    hints() {
      const open = this.rows?.length && state.notifications.items[this.index]?.target;
      return [...(open ? [['A', t('notifications.hint.open')]] : []), ['Y', t('notifications.hint.clear')], ['B', t('notifications.hint.back')]];
    }
  };
}
