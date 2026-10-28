// CaseHUD lighting (CaseHUD 1.0): on/off, brightness and the effect from CaseHUD's list. That is all the couch does here: effect
// parameters (CaseHUD keeps them per effect), devices and controller settings are built in CaseHUD's own window ("Open on the PC").
// The host checks every change against its last state (CaseHudLighting.BuildPatch), so this page only offers what CaseHUD listed.
// Brightness settles before it goes out (casehud-settle.js). Two refusals are questions, not errors: "ackRequired" (the one-time
// lighting notice has to be confirmed in CaseHUD's window) and "guiOnly" (only in CaseHUD's window).

import { api } from '../core/api.js';
import { attempt, toast } from '../core/feedback.js';
import { state } from '../core/state.js';
import { chooseDialog, confirmDialog } from '../components/overlay.js';
import { sound } from '../sound.js';
import { lang, t } from '../core/i18n.js';
import { settler } from './casehud-settle.js';
import { apiOffRow, bar, caseHudApiOff, disclaimerRow, listPage, needsDisclaimer, openCaseHudGui, pcHintRow, reloadCaseHud, unlocked } from './casehud.js';

// i18n-used: casehud.fx.solid casehud.fx.breathing casehud.fx.rainbow casehud.fx.cycle casehud.fx.wave casehud.fx.sensor casehud.fx.meteor
const KNOWN_EFFECTS = new Map(['solid', 'breathing', 'rainbow', 'cycle', 'wave', 'sensor', 'meteor'].map((name) => [name, 'casehud.fx.' + name]));

/** CaseHUD's own label (labelDe in German, else label), else our name for the first seven, else the raw name. Shown as text only. */
const ownLabel = (x) => (lang() === 'de' && x?.labelDe) || x?.label || '';
export const effectName = (e) => ownLabel(e) || (KNOWN_EFFECTS.has(e?.name) ? t(KNOWN_EFFECTS.get(e.name)) : e?.display || '');

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

  /** Brightness: the row changes at once, the value goes out once the input has settled (or on A). */
  async function stepBrightness(page, item, d) {
    const current = settle.value('brightness', light.brightness);
    const value = Math.max(0, Math.min(100, current + d * 5));
    if (value === current) return sound.bump();
    if (!settle.has('brightness') && !(await unlocked())) return;
    sound.move();
    settle.set('brightness', value, (v) => change(page, { brightness: v }, { guard: false }));
    page.patchRow(item, { detail: `${value} %`, pill: bar(value) });
  }

  const effectOf = (name) => light?.effects?.find((e) => e.name === name);

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
            return stepBrightness(pg, this, d);
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
          detail: effect ? effectName(effect) : light.effectDisplay || t('casehud.sceneNone'),
          hint: t('casehud.light.choose'),
          run: async () => {
            // Only featured effects are offered; the current one stays in the list even if it is not featured.
            // CaseHUD brings back the parameters it remembers for the chosen effect.
            const offered = (light.effects ?? []).filter((e) => e.featured || e.name === light.effect);
            if (!offered.length) return toast(t('casehud.light.noEffects'));
            const name = await chooseDialog(t('casehud.light.effect'), offered.map((e) => ({ label: effectName(e), icon: e.name === light.effect ? '✓' : '', value: e.name })), light.effect);
            if (name !== undefined && name !== light.effect) await change(page, { effect: name });
          }
        }]
      });

      // Effect parameters, devices and controller settings: CaseHUD's window only.
      rows.push(pcHintRow());
      return rows;
    }
  });
}
