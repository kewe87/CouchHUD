// Bluetooth from the couch: pair headsets and controllers without the Windows settings.

import { api } from '../core/api.js';
import { el, keepInView, setFocused } from '../core/dom.js';
import { attempt, toast } from '../core/feedback.js';
import { currentView, move, render } from '../core/nav.js';
import { chooseDialog, confirmDialog } from '../components/overlay.js';
import { switchOffWarning } from '../core/input-help.js';
import { sound } from '../sound.js';
import { fmtPercent, t } from '../core/i18n.js';
import { LOW_PERCENT, bars, percentSteps, validPercent } from '../core/battery-rules.js';

const ICON = { controller: '🎮', headphones: '🎧', keyboard: '⌨', mouse: '🖱', speaker: '🔊', phone: '📱', other: '◉' };

export function bluetoothPage() {
  return {
    id: 'bluetooth',
    name: 'Bluetooth',
    index: 0,
    radio: null,
    devices: null,
    scanning: false,
    busy: false,
    // quiet: called from build(), which is already drawing the page.
    async scan({ quiet = false } = {}) {
      if (this.scanning) return;
      this.scanning = true;
      if (!quiet && currentView() === this) render();
      const [radio, devices] = await Promise.all([
        attempt(() => api.getBluetooth(), t('bluetooth.unavailable')),
        attempt(() => api.scanBluetooth(), t('bluetooth.scanFailed'))
      ]);
      this.radio = radio ?? { available: false, on: false };
      this.devices = devices ?? this.devices ?? [];
      this.scanning = false;
      if (currentView() === this) render();
    },
    build(root) {
      if (this.devices === null && !this.scanning) this.scan({ quiet: true });
      const rows = [{ kind: 'radio' }, ...(this.devices ?? []).map((d) => ({ kind: 'device', d }))];
      this.list = rows;
      this.index = Math.min(this.index, rows.length - 1);
      this.items = rows.map((r) => {
        if (r.kind === 'radio') {
          const on = this.radio?.on;
          return el('li', { className: 'check-row update-row highlight' },
            el('span', { className: 'check-box', textContent: 'ᛒ' }),
            el('span', { className: 'check-main' }, el('strong', { textContent: 'Bluetooth' }),
              el('small', { textContent: this.radio === null ? t('bluetooth.checking') : this.radio.available ? (on ? t('bluetooth.switchedOn') : t('bluetooth.switchedOff')) : t('bluetooth.noAdapterFound') })),
            this.radio?.available ? el('span', { className: `pill ${on ? 'on' : 'off'}`, textContent: on ? t('bluetooth.on') : t('bluetooth.off') }) : null);
        }
        const d = r.d;
        // The battery Windows knows for a connected device (DEVPKEY_Bluetooth_Battery), with the same bars as the guide menu.
        const battery = d.connected && validPercent(d.battery) ? d.battery : null;
        const low = battery !== null && battery <= LOW_PERCENT;
        return el('li', { className: `check-row${d.connected ? ' checked' : ''}` },
          el('span', { className: 'check-box', textContent: ICON[d.kind] ?? '◉' }),
          el('span', { className: 'check-main' }, el('strong', { textContent: d.name }),
            el('small', { className: low ? 'battery-low' : '', textContent: battery !== null ? t('bluetooth.connectedBattery', { percent: fmtPercent(battery) }) : d.connected ? t('bluetooth.connected') : d.paired ? t('bluetooth.paired') : t('bluetooth.nearby') })),
          battery !== null ? el('span', { className: `battery-bars${low ? ' battery-low' : ''}`, textContent: bars(percentSteps(battery)) }) : null);
      });
      this.scroller = el('ul', { className: 'check-list list-scroll' }, this.items);
      root.append(el('div', { className: 'files' },
        el('div', { className: 'files-head' },
          el('h2', { textContent: 'Bluetooth' }),
          el('p', { className: 'meta' }, this.scanning ? el('span', { className: 'update-busy' }, el('span', { className: 'spinner' }), el('span', { textContent: t('bluetooth.scanning') })) : this.radio && !this.radio.available ? t('bluetooth.noAdapter') : t('bluetooth.pairingHint'))),
        this.scroller));
      this.update({ instant: true });
    },
    update({ instant = false } = {}) {
      setFocused(this.items, this.index);
      keepInView(this.scroller, this.items[this.index], { instant });
    },
    async handle(action) {
      switch (action) {
        case 'up': return move(this, -1, this.list.length, true);
        case 'down': return move(this, 1, this.list.length, true);
        case 'x': return this.radio?.available === false ? sound.bump() : this.scan();
        case 'a': {
          if (this.busy) return toast(t('bluetooth.busy'));
          const row = this.list[this.index];
          sound.select();
          this.busy = true;
          try {
            if (row.kind === 'radio') {
              if (!this.radio?.available) return;
              // Switching off cuts a Bluetooth controller off: ask first (Cancel is focused), and say whether it comes back by itself.
              const warning = switchOffWarning(this.radio, !this.radio.on);
              if (warning && !(await confirmDialog(t('bluetooth.offTitle'), t('bluetooth.offConfirm'), { message: t(warning), danger: true }))) return;
              // The host leaves Bluetooth off only when this was confirmed by keyboard or mouse (or another pad is there).
              const source = state.actionSource;
              const radio = await attempt(() => api.setBluetooth(!this.radio.on, source), t('bluetooth.switchFailed'));
              if (radio) this.radio = radio;
              return render();
            }
            const d = row.d;
            if (!d.paired) {
              toast(t('bluetooth.pairing', { name: d.name }), 8000);
              const msg = await attempt(() => api.pairBluetooth(d.id), t('bluetooth.pairFailed'));
              if (msg) toast(msg);
              return this.scan();
            }
            const choice = await chooseDialog(d.name, [{ label: t('bluetooth.remove'), value: 'unpair' }, { label: t('bluetooth.cancel'), value: null }]);
            if (choice !== 'unpair') return;
            const msg = await attempt(() => api.unpairBluetooth(d.id), t('bluetooth.removeFailed'));
            if (msg) toast(msg);
            return this.scan();
          } finally {
            this.busy = false;
          }
        }
      }
    },
    pointer(node, index, dispatch) {
      this.index = index;
      this.update();
      dispatch('a');
    },
    hints() {
      if (this.radio?.available === false) return [['B', t('bluetooth.hint.back')]];
      const row = this.list?.[this.index];
      const a = row?.kind === 'radio' ? (this.radio?.on ? t('bluetooth.hint.switchOff') : t('bluetooth.hint.switchOn')) : row?.d?.paired ? t('bluetooth.hint.options') : t('bluetooth.hint.pair');
      return [['A', a], ['X', t('bluetooth.hint.search')], ['B', t('bluetooth.hint.back')]];
    }
  };
}
