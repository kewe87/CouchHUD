// WLAN from the couch: see networks, connect with the on-screen keyboard, disconnect.

import { api } from '../core/api.js';
import { el, keepInView, setFocused } from '../core/dom.js';
import { attempt, toast } from '../core/feedback.js';
import { busyTarget, isBusy, markBusy, runBusy } from '../core/busy.js';
import { currentView, move, render, renderHints } from '../core/nav.js';
import { state } from '../core/state.js';
import { chooseDialog } from '../components/overlay.js';
import { openKeyboard } from '../components/keyboard.js';
import { sound } from '../sound.js';
import { fmtPercent, t } from '../core/i18n.js';

/** The busy key of a connect or disconnect (ui/core/busy.js): its row and hint show it, a second press does nothing (it was a toast). */
const BUSY = 'network:action';

const bars = (signal) => (signal >= 75 ? '▂▄▆█' : signal >= 50 ? '▂▄▆' : signal >= 25 ? '▂▄' : '▂');

export function networkPage() {
  return {
    id: 'network',
    name: t('network.name'),
    index: 0,
    status: null,
    networks: null,
    scanning: false,
    error: '',
    /** One scan at a time: a second call waits for the running one. */
    scan(quiet = false) {
      this.scanJob ??= this.doScan(quiet).finally(() => { this.scanJob = null; });
      return this.scanJob;
    },
    async doScan(quiet = false) {
      this.scanning = true;
      this.asking = false;
      this.error = '';
      // From build() the page is being drawn right now; a second render would flicker and double the work.
      if (!quiet && currentView() === this) render();
      try {
        this.status = await api.getWifi();
      } catch (e) {
        // Not "no adapter": the status could not be read at all.
        this.status = { available: true, failed: true };
        this.networks = [];
        this.error = e?.message || t('network.statusUnreadable');
      }
      if (!this.error) {
        if (this.status.available && !this.status.message) {
          try {
            if (this.status.access !== 'allowed') {
              // Windows asks for the location in a dialog the controller cannot use: say so first.
              this.asking = true;
              if (currentView() === this) render();
              await new Promise((r) => setTimeout(r, 900));
            }
            this.networks = await api.scanWifi();
          } catch (e) {
            this.networks = this.networks ?? [];
            this.error = e?.message || t('network.scanFailed');
          }
        } else {
          this.networks = [];
          this.error = this.status.message ?? '';
        }
      }
      this.scanning = false;
      this.asking = false;
      if (currentView() === this) render();
    },
    build(root) {
      if (this.networks === null && !this.scanning) this.scan(true);
      const st = this.status;
      const rows = (this.networks ?? []).map((n) => ({ kind: 'net', n }));
      this.list = rows;
      this.index = Math.max(0, Math.min(this.index, rows.length - 1));
      this.items = rows.map(({ n }) => el('li', { className: `check-row${n.connected ? ' checked' : ''}${n.supported ? '' : ' dim'}` },
        el('span', { className: 'check-box', textContent: n.secured ? '🔒︎' : bars(n.signal).slice(-1) }),
        el('span', { className: 'check-main' }, el('strong', { textContent: n.ssid }),
          el('small', { textContent: n.connected ? (st?.ip ? t('network.connectedIp', { ip: st.ip }) : t('network.connected')) : !n.supported ? t('network.unsupported') : n.secured ? t('network.secured') : t('network.open') })),
        el('span', { className: 'pill off', textContent: bars(n.signal), title: fmtPercent(n.signal) })));
      this.items.forEach((li, i) => markBusy(li, isBusy(BUSY) && rows[i].n.ssid === busyTarget(BUSY)));
      this.scroller = el('ul', { className: 'check-list list-scroll' }, this.items);

      let meta;
      if (this.scanning) meta = el('span', { className: 'update-busy' }, el('span', { className: 'spinner' }), el('span', { textContent: this.asking ? t('network.askLocation') : t('network.scanning') }));
      else if (st && !st.available) meta = t('network.noAdapter');
      else if (this.error) meta = this.error;
      else if (st?.ssid) meta = st.ip ? t('network.connectedToIp', { ssid: st.ssid, ip: st.ip }) : t('network.connectedTo', { ssid: st.ssid });
      else meta = t('network.notConnected');

      const showList = rows.length > 0;
      root.append(el('div', { className: 'files' },
        el('div', { className: 'files-head' }, el('h2', { textContent: t('network.heading') }), el('p', { className: 'meta' }, meta)),
        showList ? this.scroller : null,
        !showList && !this.scanning && st?.available && !this.error ? el('p', { className: 'meta', textContent: t('network.noneFound') }) : null));
      this.update({ instant: true });
    },
    update({ instant = false } = {}) {
      if (!this.items?.length) return;
      setFocused(this.items, this.index);
      keepInView(this.scroller, this.items[this.index], { instant });
    },
    async handle(action) {
      switch (action) {
        case 'up': return this.list.length && move(this, -1, this.list.length, true);
        case 'down': return this.list.length && move(this, 1, this.list.length, true);
        case 'x': return this.scan();
        case 'y':
          if (isBusy(BUSY)) return sound.bump();
          if (this.status?.ssid) return this.disconnect();
          return;
        case 'a': {
          const row = this.list[this.index];
          if (!row) return;
          if (isBusy(BUSY)) return sound.bump();
          sound.select();
          const n = row.n;
          if (!n.supported) return toast(t('network.enterprise'), 4500);
          if (n.connected) {
            const choice = await chooseDialog(n.ssid, [{ label: t('network.disconnect'), value: 'disconnect' }, { label: t('network.cancel'), value: null }]);
            return choice === 'disconnect' ? this.disconnect() : undefined;
          }
          let password = '';
          if (n.secured) {
            password = await this.askPassword(n);
            if (password === undefined) return;
          }
          return this.connect(n, password);
        }
      }
    },
    askPassword(n, hint = t('network.passwordHint')) {
      return openKeyboard({ title: t('network.passwordFor', { ssid: n.ssid }), secret: true, hint });
    },
    /** Redraws the page while it is shown (a connect started or ended), else only the hints. */
    redrawBusy() {
      if (currentView() === this && !state.overlay) render();
      else renderHints();
    },
    async connect(n, password) {
      let again = false;
      const run = await runBusy(BUSY, async () => {
        toast(t('network.connecting', { ssid: n.ssid }), 15000);
        const result = await attempt(() => api.connectWifi(n.ssid, password || null), t('network.connectFailed'));
        if (result) toast(result.message);
        again = !!result?.needsPassword;
      }, { onChange: () => this.redrawBusy(), onIgnored: () => sound.bump(), target: n.ssid });
      if (run.ignored) return;
      if (again) {
        // Windows doesn't know the network: ask for the password right away.
        const typed = await this.askPassword(n, t('network.unknownNetwork'));
        if (typed) return this.connect(n, typed);
      }
      return this.scan();
    },
    async disconnect() {
      const run = await runBusy(BUSY, async () => {
        const msg = await attempt(() => api.disconnectWifi(), t('network.disconnectFailed'));
        if (msg) toast(msg);
      }, { onChange: () => this.redrawBusy(), onIgnored: () => sound.bump(), target: this.status?.ssid ?? null });
      if (run.ignored) return;
      return this.scan();
    },
    pointer(node, index, dispatch) {
      this.index = index;
      this.update();
      dispatch('a');
    },
    hints() {
      if (this.scanning) return [['B', t('network.hint.back')]];
      const running = isBusy(BUSY);
      return [...(this.list?.length ? [['A', t('network.hint.open'), { busy: running }]] : []), ...(this.status?.available === false ? [] : [['X', t('network.hint.search')]]), ...(this.status?.ssid ? [['Y', t('network.disconnect'), { busy: running }]] : []), ['B', t('network.hint.back')]];
    }
  };
}
