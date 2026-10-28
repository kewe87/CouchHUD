// "What CouchHUD changes in Windows": every intervention in one list, with where it
// happens, whether it needs administrator rights, its current state and how to undo it.

import { api } from '../core/api.js';
import { el, keepInView, setFocused } from '../core/dom.js';
import { attempt } from '../core/feedback.js';
import { currentView, move, render } from '../core/nav.js';
import { t } from '../core/i18n.js';
import { state } from '../core/state.js';

const on = (v) => (v ? t('transparency.state.on') : t('transparency.state.off'));
// The policies of the locked couch need the setup's helper tasks unless the account may write the keys itself.
const policyState = (s) => (s.security?.policies === 'none' ? t('transparency.state.notActive') : t('transparency.state.couchMode'));

/**
 * level: "full" = only with the full integration, "light" = only in light mode, "both".
 * admin: needs administrator rights (Windows asks each time or once at setup).
 */
const entries = () => [
  {
    title: t('transparency.shell.title'),
    level: 'full',
    what: t('transparency.shell.what'),
    where: t('transparency.shell.where'),
    undo: t('transparency.shell.undo'),
    state: (s) => on(s.settings.isShell)
  },
  {
    title: t('transparency.autologon.title'),
    level: 'full',
    admin: true,
    what: t('transparency.autologon.what'),
    where: t('transparency.autologon.where'),
    undo: t('transparency.autologon.undo'),
    state: (s) => (s.security?.autoLogon ? t('transparency.state.onUser', { user: s.security.autoLogonUser }) : t('transparency.state.off'))
  },
  {
    title: t('transparency.lock.title'),
    level: 'full',
    what: t('transparency.lock.what'),
    where: t('transparency.lock.where'),
    undo: t('transparency.lock.undo'),
    state: (s) => ({ off: t('transparency.state.off'), code: t('transparency.state.code'), windows: t('transparency.state.windows') })[s.security?.couchLock ?? 'off']
  },
  {
    title: t('transparency.parental.title'),
    level: 'full',
    what: t('transparency.parental.what'),
    where: t('transparency.parental.where'),
    undo: t('transparency.parental.undo'),
    state: (s) => (s.parental?.enabled ? (s.parental.enforced ? t('transparency.state.enforced') : t('transparency.state.hintsOnly')) : t('transparency.state.off'))
  },
  {
    title: t('transparency.taskmgr.title'),
    level: 'full',
    what: t('transparency.taskmgr.what'),
    where: t('transparency.taskmgr.where'),
    undo: t('transparency.taskmgr.undo'),
    state: (s) => (s.security?.couchLock !== 'off' && s.security?.blockTaskManager ? policyState(s) : t('transparency.state.off'))
  },
  {
    title: t('transparency.policies.title'),
    level: 'full',
    what: t('transparency.policies.what'),
    where: t('transparency.policies.where'),
    undo: t('transparency.policies.undo'),
    state: (s) => (s.security?.couchLock !== 'off' ? policyState(s) : t('transparency.state.off'))
  },
  {
    title: t('transparency.services.title'),
    level: 'full',
    admin: true,
    what: t('transparency.services.what'),
    where: t('transparency.services.where'),
    undo: t('transparency.services.undo'),
    state: () => t('transparency.state.see')
  },
  {
    title: t('transparency.adminService.title'),
    level: 'both',
    admin: true,
    what: t('transparency.adminService.what'),
    where: t('transparency.adminService.where'),
    undo: t('transparency.adminService.undo'),
    // Running, missing, blocked by another program, outdated … (the host's own words), with the version when it answered.
    state: (s) => (s.security?.service ? (s.security.service.version ? `${s.security.service.label} (${s.security.service.version})` : s.security.service.label) : undefined)
  },
  {
    title: t('transparency.startup.title'),
    level: 'full',
    what: t('transparency.startup.what'),
    where: t('transparency.startup.where'),
    undo: t('transparency.startup.undo'),
    state: (s) => on(s.settings.runStartupApps)
  },
  {
    title: t('transparency.restartPc.title'),
    level: 'full',
    what: t('transparency.restartPc.what'),
    where: t('transparency.restartPc.where'),
    undo: t('transparency.restartPc.undo'),
    state: () => t('transparency.state.onRequest')
  },
  {
    title: t('transparency.taskbar.title'),
    level: 'light',
    what: t('transparency.taskbar.what'),
    where: t('transparency.taskbar.where'),
    undo: t('transparency.taskbar.undo'),
    state: (s) => on(s.settings.closeExplorerInLight)
  },
  {
    title: t('transparency.xbox.title'),
    level: 'both',
    what: t('transparency.xbox.what'),
    where: t('transparency.xbox.where'),
    undo: t('transparency.xbox.undo'),
    state: (s) => (s.settings.gameBarButton ? 'Game Bar' : 'CouchHUD')
  },
  {
    title: t('transparency.clients.title'),
    level: 'full',
    what: t('transparency.clients.what'),
    where: t('transparency.clients.where'),
    undo: t('transparency.clients.undo'),
    state: () => t('transparency.state.couchMode')
  },
  {
    title: t('transparency.steam.title'),
    level: 'full',
    what: t('transparency.steam.what'),
    where: t('transparency.steam.where'),
    undo: t('transparency.steam.undo'),
    state: (s) => (s.settings.suppressCompetitors ? t('transparency.state.couchMode') : t('transparency.state.off'))
  },
  {
    title: t('transparency.guard.title'),
    level: 'full',
    what: t('transparency.guard.what'),
    where: t('transparency.guard.where'),
    undo: t('transparency.guard.undo'),
    state: (s) => (s.settings.suppressCompetitors ? t('transparency.state.couchMode') : t('transparency.state.off'))
  },
  {
    title: t('transparency.pause.title'),
    level: 'both',
    what: t('transparency.pause.what'),
    where: t('transparency.pause.where'),
    undo: t('transparency.pause.undo'),
    state: (s) => on(s.settings.pauseGameInMenu)
  },
  {
    title: t('transparency.display.title'),
    level: 'both',
    what: t('transparency.display.what'),
    where: t('transparency.display.where'),
    undo: t('transparency.display.undo'),
    state: (s) => ({ off: t('transparency.state.hdrOff'), auto: t('transparency.state.hdrAuto'), on: t('transparency.state.hdrOn') })[s.settings.uiHdr ?? 'auto']
  },
  {
    title: t('transparency.night.title'),
    level: 'both',
    what: t('transparency.night.what'),
    where: t('transparency.night.where'),
    undo: t('transparency.night.undo'),
    state: (s) => ({ off: t('transparency.state.off'), on: t('transparency.state.on'), evening: t('transparency.state.evening') })[s.settings.nightMode ?? 'off']
  },
  {
    title: t('transparency.audio.title'),
    level: 'both',
    what: t('transparency.audio.what'),
    where: t('transparency.audio.where'),
    undo: t('transparency.audio.undo'),
    state: (s) => (s.settings.autoSwitchHeadphones ? t('transparency.state.auto') : t('transparency.state.manual'))
  },
  {
    title: t('transparency.updates.title'),
    level: 'both',
    admin: true,
    what: t('transparency.updates.what'),
    where: t('transparency.updates.where'),
    undo: t('transparency.updates.undo'),
    state: () => t('transparency.state.onRequest')
  },
  {
    title: t('transparency.firewall.title'),
    level: 'both',
    admin: true,
    what: t('transparency.firewall.what'),
    where: t('transparency.firewall.where'),
    undo: t('transparency.firewall.undo'),
    state: () => t('transparency.state.onRequest')
  },
  {
    title: t('transparency.remote.title'),
    level: 'both',
    what: t('transparency.remote.what'),
    where: t('transparency.remote.where'),
    undo: t('transparency.remote.undo'),
    state: (s) => on(s.settings.remoteEnabled)
  },
  {
    title: t('transparency.mcp.title'),
    level: 'both',
    what: t('transparency.mcp.what'),
    where: t('transparency.mcp.where'),
    undo: t('transparency.mcp.undo'),
    state: (s) => on(s.settings.mcpEnabled)
  },
  {
    title: t('transparency.notifications.title'),
    level: 'both',
    what: t('transparency.notifications.what'),
    where: t('transparency.notifications.where'),
    undo: t('transparency.notifications.undo'),
    state: (s) => on(s.settings.mirrorNotifications)
  },
  {
    title: t('transparency.awake.title'),
    level: 'both',
    what: t('transparency.awake.what'),
    where: t('transparency.awake.where'),
    undo: t('transparency.awake.undo'),
    state: () => t('transparency.state.always')
  }
].filter((e) => !e.hidden?.());

/** Blue only when something is really switched on; "Aus" and "nur auf Wunsch" stay grey. */
function statePill(text, active) {
  const idle = [t('transparency.state.off'), t('transparency.state.hdrOff'), t('transparency.state.manual'), t('transparency.state.onRequest'), t('transparency.state.see')].some((p) => text.startsWith(p)) || /^[–…]/.test(text);
  return el('span', { className: `pill ${active && !idle ? 'on' : 'off'}`, textContent: text });
}

export function transparencyPage() {
  return {
    id: 'transparency',
    name: t('transparency.title'),
    index: 0,
    data: null,
    details: false,
    async load() {
      const [settings, security, parental] = await Promise.all([attempt(() => api.getSettings()), attempt(() => api.getSecurity()), attempt(() => api.getParental?.())]);
      this.data = { settings: settings ?? {}, security, parental };
      if (currentView() === this) render();
    },
    build(root) {
      if (!this.data) this.load();
      const s = this.data ?? { settings: {}, security: null };
      const mode = s.settings.integrationMode ?? state.info.integrationMode ?? 'light';
      this.items = entries().map((e) => {
        const active = e.level === 'both' || e.level === mode;
        const tags = [
          e.level === 'full' ? t('transparency.tag.full') : e.level === 'light' ? t('transparency.tag.light') : null,
          e.admin ? t('transparency.tag.admin') : null,
          active ? null : t('transparency.tag.inactive')
        ].filter(Boolean);
        return el('li', { className: `check-row transparency-row${active ? '' : ' dim'}` },
          el('span', { className: 'check-main' },
            el('strong', { textContent: e.title }),
            el('small', { textContent: e.what }),
            this.details ? el('small', { className: 'transparency-where', textContent: t('transparency.where', { text: e.where }) }) : null,
            this.details ? el('small', { className: 'transparency-undo', textContent: t('transparency.undo', { text: e.undo }) }) : null,
            tags.length ? el('span', { className: 'transparency-tags' }, tags.map((tag) => el('span', { className: 'pill tag', textContent: tag }))) : null),
          statePill(this.data ? e.state(s) ?? '–' : '…', active));
      });
      this.scroller = el('ul', { className: 'check-list list-scroll' }, this.items);
      root.append(el('div', { className: 'files' },
        el('div', { className: 'files-head' },
          el('h2', { textContent: t('transparency.title') }),
          el('p', { className: 'meta', textContent: t('transparency.meta', { mode: mode === 'full' ? t('transparency.mode.full') : t('transparency.mode.light') }) })),
        this.scroller));
      this.update({ instant: true });
    },
    update({ instant = false } = {}) {
      if (!this.items?.length) return;
      setFocused(this.items, this.index);
      keepInView(this.scroller, this.items[this.index], { instant });
    },
    handle(action) {
      switch (action) {
        case 'up': return move(this, -1, this.items.length, true);
        case 'down': return move(this, 1, this.items.length, true);
        case 'a':
        case 'x':
          // Where it happens and how to undo it: registry paths and the like, only on request.
          this.details = !this.details;
          return render();
      }
    },
    hints() {
      const scrollable = this.scroller && this.scroller.scrollHeight > this.scroller.clientHeight + 4;
      return [['A', this.details ? t('transparency.hint.hideDetails') : t('transparency.hint.showDetails')], ...(scrollable ? [['↕', t('transparency.hint.scroll')]] : []), ['B', t('transparency.hint.back')]];
    }
  };
}
