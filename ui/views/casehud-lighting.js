// CaseHUD lighting (CaseHUD 1.0): on/off, brightness, effect with its parameters, devices. The host checks every change against
// its last state (CaseHudLighting.BuildPatch), so this page only offers what CaseHUD listed. CaseHUD merges a PATCH, so only what
// changed is sent: one param, one device, the effect alone when switching. Brightness and stepped values settle before they go
// out (casehud-settle.js). Two refusals are questions, not errors: "ackRequired" (the one-time lighting notice has to be confirmed
// in CaseHUD's window) and "guiOnly" (controller settings: only in CaseHUD's window, tab "Lighting").

import { api } from '../core/api.js';
import { attempt, toast } from '../core/feedback.js';
import { state } from '../core/state.js';
import { chooseDialog, confirmDialog } from '../components/overlay.js';
import { sound } from '../sound.js';
import { lang, t } from '../core/i18n.js';
import { settler } from './casehud-settle.js';
import { apiOffRow, bar, caseHudApiOff, cycle, disclaimerRow, listPage, needsDisclaimer, openCaseHudGui, reloadCaseHud, unlocked } from './casehud.js';

// i18n-used: casehud.fx.solid casehud.fx.breathing casehud.fx.rainbow casehud.fx.cycle casehud.fx.wave casehud.fx.sensor casehud.fx.meteor
const KNOWN_EFFECTS = new Map(['solid', 'breathing', 'rainbow', 'cycle', 'wave', 'sensor', 'meteor'].map((name) => [name, 'casehud.fx.' + name]));

/** CaseHUD's own label (labelDe in German, else label), else our name for the first seven, else the raw name. */
const ownLabel = (x) => (lang() === 'de' && x?.labelDe) || x?.label || '';
export const effectName = (e) => ownLabel(e) || (KNOWN_EFFECTS.has(e?.name) ? t(KNOWN_EFFECTS.get(e.name)) : e?.name ?? '');

// i18n-used: casehud.color.red casehud.color.orange casehud.color.yellow casehud.color.green casehud.color.cyan casehud.color.blue casehud.color.purple casehud.color.pink casehud.color.white
const PALETTE = [
  ['casehud.color.red', '#ff0000'], ['casehud.color.orange', '#ff7a00'], ['casehud.color.yellow', '#ffd400'], ['casehud.color.green', '#00ff40'], ['casehud.color.cyan', '#00e5ff'],
  ['casehud.color.blue', '#0040ff'], ['casehud.color.purple', '#8a00ff'], ['casehud.color.pink', '#ff2a9a'], ['casehud.color.white', '#ffffff']
];
const colorName = (hex) => {
  const known = PALETTE.find(([, h]) => h === String(hex).toLowerCase());
  return known ? t(known[0]) : String(hex ?? '');
};

/** A param key as a label when CaseHUD gives none: "fade_time" -> "Fade time". */
const keyLabel = (key) => {
  const s = String(key).replace(/[_-]+/g, ' ').replace(/([a-z])([A-Z])/g, '$1 $2').toLowerCase();
  return s.charAt(0).toUpperCase() + s.slice(1);
};

const isWhole = (p) => [p.min, p.max, p.default].every((x) => x == null || Number.isInteger(x));
/** CaseHUD's step when it gives one, else a twentieth of the range (whole for whole-number params). */
const numberStep = (p) => {
  if (p.step > 0) return p.step;
  if (p.min == null || p.max == null) return 1;
  const raw = (p.max - p.min) / 20;
  return isWhole(p) ? Math.max(1, Math.round(raw)) : raw;
};
const clampParam = (p, v) => Math.min(p.max ?? Infinity, Math.max(p.min ?? -Infinity, v));
const showNumber = (v) => (Number.isInteger(v) ? String(v) : (Math.round(v * 100) / 100).toString());

// i18n-used: casehud.light.state.conflict casehud.light.state.error
const DEVICE_STATES = new Map(['conflict', 'error'].map((s) => [s, 'casehud.light.state.' + s]));

/** state (conflict, error) and CaseHUD's message, only shown; "disabled" is the pill already. */
const deviceDetail = (d) => [DEVICE_STATES.has(d.state) ? t(DEVICE_STATES.get(d.state)) : '', d.message ?? ''].filter(Boolean).join(' · ') || null;

export function lightingPage() {
  let light = null;
  let failed = false;
  const settle = settler();

  async function load(page) {
    const r = await attempt(() => api.caseHudLighting(), t('casehud.failed'));
    if (r) light = r;
    else failed = !light;
    page.refresh();
  }

  /** One change; the two refusals the user resolves in CaseHUD's window become a question instead of an error. */
  async function change(page, patch, { guard = true } = {}) {
    if (guard && !(await unlocked())) return;
    const r = await attempt(() => api.caseHudPatchLighting(patch), t('casehud.failed'));
    if (!r) return page.refresh();
    if (r.problem === 'ackRequired' || r.problem === 'guiOnly') {
      const ack = r.problem === 'ackRequired';
      const open = await confirmDialog(ack ? t('casehud.light.ackTitle') : t('casehud.guiOnlyTitle'), t('casehud.openWindow'),
        { message: ack ? t('casehud.light.ackText') : t('casehud.guiOnlyText') });
      if (open) await openCaseHudGui();
      return page.refresh();
    }
    if (r.state) light = r.state;
    // The status carries lighting.enabled for the main page.
    await reloadCaseHud();
    page.refresh();
  }

  /** A stepped value: the row changes at once, the value goes out once the input has settled (or on A). */
  async function stepValue(page, item, key, reported, next, show, toPatch) {
    const current = settle.value(key, reported);
    const value = next(current);
    if (value === current) return sound.bump();
    if (!settle.has(key) && !(await unlocked())) return;
    sound.move();
    settle.set(key, value, (v) => change(page, toPatch(v), { guard: false }));
    page.patchRow(item, show(value));
  }

  const effectOf = (name) => light?.effects?.find((e) => e.name === name);

  /** One param, nothing else: CaseHUD keeps the values of the keys that are not sent. */
  const paramsWith = (_effect, key, value) => ({ params: { [key]: value } });

  function paramRow(page, effect, p) {
    const reported = light.params?.[p.key] ?? p.default;
    const key = `param:${p.key}`;
    const label = ownLabel(p) || keyLabel(p.key);
    const readOnly = () => ({ icon: '·', label, detail: `${reported ?? ''} · ${t('casehud.light.inWindow')}`.replace(/^ · /, '') });
    switch (p.type) {
      case 'number': {
        // Without both bounds there is no range to step in; the host refuses such a param too, so it is only shown.
        if (p.min == null || p.max == null) return readOnly();
        const shown = settle.value(key, reported ?? p.min ?? 0);
        return {
          icon: '⇆',
          label,
          detail: showNumber(shown) + (p.min != null && p.max != null ? ` (${showNumber(p.min)}–${showNumber(p.max)})` : ''),
          adjustHint: t('casehud.hint.change'),
          adjust(d, pg) {
            return stepValue(pg, this, key, reported ?? p.min ?? 0, (v) => clampParam(p, Math.round((v + d * numberStep(p)) * 1000) / 1000),
              (v) => ({ detail: showNumber(v) + (p.min != null && p.max != null ? ` (${showNumber(p.min)}–${showNumber(p.max)})` : '') }),
              (v) => paramsWith(effect, p.key, v));
          },
          hint: t('casehud.light.apply'),
          run: () => (settle.has(key) ? settle.flush(key) : sound.bump())
        };
      }
      case 'color': {
        const shown = settle.value(key, reported ?? '#ffffff');
        return {
          icon: '◉',
          label,
          detail: colorName(shown),
          swatch: /^#[0-9a-f]{6}$/i.test(shown) ? shown : null,
          adjustHint: t('casehud.hint.change'),
          adjust(d, pg) {
            const hexes = PALETTE.map(([, h]) => h);
            return stepValue(pg, this, key, reported ?? '#ffffff', (v) => cycle(hexes, String(v).toLowerCase(), d),
              (v) => ({ detail: colorName(v), swatch: v }), (v) => paramsWith(effect, p.key, v));
          },
          hint: t('casehud.light.chooseColor'),
          run: async () => {
            if (settle.has(key)) return settle.flush(key);
            const v = await chooseDialog(label, PALETTE.map(([name, hex]) => ({ label: t(name), icon: hex === String(reported).toLowerCase() ? '✓' : '', value: hex })), String(reported).toLowerCase());
            if (v !== undefined) await change(page, paramsWith(effect, p.key, v));
          }
        };
      }
      case 'bool':
        return {
          icon: reported ? '✓' : '·',
          label,
          pill: reported ? t('casehud.on') : t('casehud.off'),
          hint: reported ? t('casehud.light.turnOff') : t('casehud.light.turnOn'),
          run: () => change(page, paramsWith(effect, p.key, !reported))
        };
      case 'enum': {
        const values = p.values ?? [];
        return {
          icon: '☰',
          label,
          detail: String(settle.value(key, reported ?? values[0] ?? '')),
          adjustHint: t('casehud.hint.change'),
          adjust(d, pg) {
            if (!values.length) return sound.bump();
            return stepValue(pg, this, key, reported ?? values[0], (v) => cycle(values, v, d), (v) => ({ detail: String(v) }), (v) => paramsWith(effect, p.key, v));
          },
          hint: t('casehud.light.choose'),
          run: async () => {
            if (settle.has(key)) return settle.flush(key);
            if (!values.length) return sound.bump();
            const v = await chooseDialog(label, values.map((x) => ({ label: String(x), icon: x === reported ? '✓' : '', value: x })), reported);
            if (v !== undefined) await change(page, paramsWith(effect, p.key, v));
          }
        };
      }
      default:
        // Text (and types this version does not know): no clean controller input, so it is shown and left as it is.
        return readOnly();
    }
  }

  return listPage({
    id: 'casehud-lighting',
    title: t('casehud.light.title'),
    onClose: () => settle.flushAll(),
    onEnter() {
      // API off: the rows show the hint; asked again once it is on (the page is built again then).
      if (this.asked || caseHudApiOff()) return;
      this.asked = true;
      load(this);
    },
    rows() {
      const page = this;
      const st = state.caseHud?.status;
      const rows = [];
      // API switched off in CaseHUD (also while this page is open): the hint where to switch it on, not a connection error.
      if (caseHudApiOff()) return [apiOffRow()];
      if (needsDisclaimer(st)) rows.push(disclaimerRow());
      if (!light) return [...rows, { items: [{ icon: failed ? '!' : '⟳', label: failed ? t('casehud.light.unavailable') : t('casehud.loading') }] }];

      rows.push({ section: t('casehud.light.title') });
      rows.push({
        items: [{
          icon: '💡',
          label: light.enabled ? t('casehud.light.turnOff') : t('casehud.light.turnOn'),
          detail: light.enabled ? t('casehud.light.isOn') : t('casehud.light.isOff'),
          pill: light.enabled ? t('casehud.on') : t('casehud.off'),
          hint: light.enabled ? t('casehud.light.turnOff') : t('casehud.light.turnOn'),
          run: () => change(page, { enabled: !light.enabled })
        }]
      });
      const shownBrightness = settle.value('brightness', light.brightness);
      rows.push({
        items: [{
          icon: '☀',
          label: t('casehud.brightness'),
          detail: `${shownBrightness} %`,
          pill: bar(shownBrightness),
          adjustHint: t('casehud.hint.change'),
          adjust(d, pg) {
            return stepValue(pg, this, 'brightness', light.brightness, (v) => Math.max(0, Math.min(100, v + d * 5)),
              (v) => ({ detail: `${v} %`, pill: bar(v) }), (v) => ({ brightness: v }));
          },
          hint: t('casehud.light.apply'),
          run: () => (settle.has('brightness') ? settle.flush('brightness') : sound.bump())
        }]
      });
      const effect = effectOf(light.effect);
      rows.push({
        items: [{
          icon: '✦',
          label: t('casehud.light.effect'),
          detail: effect ? effectName(effect) : light.effect || t('casehud.sceneNone'),
          hint: t('casehud.light.choose'),
          run: async () => {
            // Only featured effects are offered; the current one stays in the list even if it is not featured.
            const offered = (light.effects ?? []).filter((e) => e.featured || e.name === light.effect);
            if (!offered.length) return toast(t('casehud.light.noEffects'));
            const name = await chooseDialog(t('casehud.light.effect'), offered.map((e) => ({ label: effectName(e), icon: e.name === light.effect ? '✓' : '', value: e.name })), light.effect);
            if (name !== undefined && name !== light.effect) await change(page, { effect: name });
          }
        }]
      });

      const params = effect?.params ?? [];
      if (params.length) {
        rows.push({ section: t('casehud.light.params', { effect: effectName(effect) }) });
        for (const p of params) rows.push({ items: [paramRow(page, effect, p)] });
      }

      rows.push({ section: t('casehud.light.devices') });
      // CaseHUD lists only the devices it found, and none while the lighting is off.
      if (!light.devices?.length) {
        rows.push({ items: [{ icon: '·', label: light.enabled ? t('casehud.light.noDevices') : t('casehud.light.devicesWhenOn') }] });
      }
      for (const d of light.devices ?? []) {
        rows.push({
          items: [{
            icon: d.state === 'conflict' || d.state === 'error' ? '!' : d.disabled ? '·' : '✓',
            label: d.name || d.id,
            detail: deviceDetail(d),
            pill: d.disabled ? t('casehud.off') : t('casehud.on'),
            hint: d.disabled ? t('casehud.light.turnOn') : t('casehud.light.turnOff'),
            // Merged by id on CaseHUD's side: only this device and only "disabled".
            run: () => change(page, { devices: { [d.id]: { disabled: !d.disabled } } })
          }]
        });
      }

      // Controller settings and "ignore conflicts" are locked over the API (403 gui_only): a pointer to CaseHUD's window, nothing to set here.
      rows.push({ section: t('casehud.light.fans') });
      rows.push({
        items: [{
          icon: '✎',
          label: t('casehud.light.fansOnlyWindow'),
          detail: t('casehud.light.fansDetail'),
          hint: t('casehud.openWindow'),
          run: openCaseHudGui
        }]
      });
      return rows;
    }
  });
}
