// AI assistant: ask with the on-screen keyboard (or a suggestion), the model
// uses the same tools as the MCP server. Every change needs an A on the pad.

import { t } from '../core/i18n.js';
import { iconEl } from '../core/glyph-icons.js';
import { api } from '../core/api.js';
import { el, setFocused } from '../core/dom.js';
import { attempt } from '../core/feedback.js';
import { isBusy, markBusy, runBusy } from '../core/busy.js';
import { setBackground } from '../core/art.js';
import { currentView, render, renderHints } from '../core/nav.js';
import { state } from '../core/state.js';
import { closeTagged, confirmDialog, overlayFree } from '../components/overlay.js';
import { openKeyboard } from '../components/keyboard.js';
import { sound } from '../sound.js';

// i18n-used: assistant.tool.searchLibrary assistant.tool.getItemDetails assistant.tool.launch assistant.tool.toggleFavorite assistant.tool.editItem assistant.tool.addEntry assistant.tool.rescanLibrary assistant.tool.getSettings assistant.tool.setSetting assistant.tool.listSkins assistant.tool.setSkin assistant.tool.getSession assistant.tool.getWeather assistant.tool.getNews assistant.tool.getUpdates assistant.tool.listHomeEntities assistant.tool.activateHomeEntity assistant.tool.getAudio assistant.tool.setVolume assistant.tool.setAudioOutput assistant.tool.power
const toolLabel = (name) => {
  switch (name) {
    case 'search_library': return t('assistant.tool.searchLibrary');
    case 'get_item_details': return t('assistant.tool.getItemDetails');
    case 'launch': return t('assistant.tool.launch');
    case 'toggle_favorite': return t('assistant.tool.toggleFavorite');
    case 'edit_item': return t('assistant.tool.editItem');
    case 'add_entry': return t('assistant.tool.addEntry');
    case 'rescan_library': return t('assistant.tool.rescanLibrary');
    case 'get_settings': return t('assistant.tool.getSettings');
    case 'set_setting': return t('assistant.tool.setSetting');
    case 'list_skins': return t('assistant.tool.listSkins');
    case 'set_skin': return t('assistant.tool.setSkin');
    case 'get_session': return t('assistant.tool.getSession');
    case 'get_weather': return t('assistant.tool.getWeather');
    case 'get_news': return t('assistant.tool.getNews');
    case 'get_updates': return t('assistant.tool.getUpdates');
    case 'get_audio': return t('assistant.tool.getAudio');
    case 'set_volume': return t('assistant.tool.setVolume');
    case 'set_audio_output': return t('assistant.tool.setAudioOutput');
    case 'power': return t('assistant.tool.power');
    default: return name;
  }
};

const messages = [];
/** The busy key of a running question (ui/core/busy.js). */
const ASK = 'assistant:ask';
const isAsking = () => isBusy(ASK);
let progress = '';
let info = null;

const dropped = new Set();

/** Confirmations come from the host while a question runs, whatever screen is open. */
export function startAssistant() {
  api.onAssistant?.(async (event) => {
    if (event.type === 'tool') {
      progress = toolLabel(event.name);
      if (currentView() === assistantView) assistantView.renderTranscript();
      return;
    }
    if (event.type === 'confirmCancel') {
      // The host gave up (nobody answered in time): close the question, or never show it if it is still waiting.
      dropped.add(event.id);
      closeTagged(event.id);
      return;
    }
    if (event.type === 'confirm') {
      await overlayFree();
      if (dropped.delete(event.id)) return;
      const ok = await confirmDialog(event.text, t('assistant.run'), { danger: !!event.danger, focusCancel: !!event.focusCancel, tag: event.id });
      attempt(() => api.confirmAssistant(event.id, ok));
    }
  });
}

function suggestions() {
  const list = [t('assistant.sugPlay'), t('assistant.sugRecent')];
  if (state.info.weather) list.push(t('assistant.sugWeather'));
  list.push(t('assistant.sugSkin'), t('assistant.sugUpdates'));
  return list.slice(0, 4);
}

/** The TV is no place for markdown; keep the words, drop the syntax. */
function plain(text) {
  return (text ?? '')
    .replace(/\*\*(.+?)\*\*/g, '$1')
    .replace(/^#+\s*/gm, '')
    .replace(/`([^`]+)`/g, '$1')
    .trim();
}

async function ask(text) {
  if (!text?.trim()) return;
  // A question runs: the chips are greyed (busy) and a press does nothing; the answer is on its way (it was a toast).
  if (isAsking()) return sound.bump();
  progress = '';
  messages.push({ role: 'user', text: text.trim() });
  const redraw = () => (currentView() === assistantView && !state.overlay ? render() : renderHints());
  await runBusy(ASK, async () => {
    try {
      const answer = await api.askAssistant(text.trim());
      messages.push({ role: 'assistant', text: plain(answer) || t('assistant.done') });
      sound.select();
    } catch (e) {
      messages.push({ role: 'error', text: e?.message ?? String(e) });
      sound.bump();
    } finally {
      progress = '';
    }
  }, { onChange: redraw, onIgnored: () => sound.bump() });
}

export const assistantView = {
  id: 'assistant',
  get name() { return t('assistant.title'); },
  index: 0,
  async onEnter() {
    info = (await attempt(() => api.assistantInfo())) ?? info;
    if (currentView() === this) this.update();
  },
  build(root) {
    setBackground({ id: '__assistant', name: t('assistant.title'), art: {} });
    this.chips = [
      { icon: '✎', label: t('assistant.ask'), run: () => this.compose(), primary: true },
      ...suggestions().map((s) => ({ label: s, run: () => ask(s) })),
      { icon: '↺', label: t('assistant.new'), run: () => this.reset() }
    ];
    this.index = Math.min(this.index, this.chips.length - 1);
    this.transcript = el('div', { className: 'chat list-scroll' });
    // While a question runs every chip is busy (greyed, A does nothing); the focus stays where it is.
    this.chipNodes = this.chips.map((c) => markBusy(el('li', { className: `chip${c.primary ? ' primary' : ''}` }, c.icon ? iconEl(c.icon) : null, c.label), isAsking()));
    this.chipRow = el('ul', { className: 'chips' }, this.chipNodes);
    this.modelTag = el('span', { className: 'chat-model' });
    root.append(el('div', { className: 'assistant-page' },
      el('header', { className: 'assistant-head' },
        el('div', { className: 'assistant-orb' }),
        el('div', {},
          el('h2', { textContent: t('assistant.title') }),
          this.modelTag)),
      this.transcript,
      this.chipRow));
    this.renderTranscript();
    this.update({ instant: true });
  },
  renderTranscript() {
    if (!this.transcript?.isConnected) return;
    const nodes = messages.map((m) => el('div', { className: `bubble ${m.role}`, textContent: m.text }));
    if (!messages.length) {
      nodes.push(el('div', { className: 'chat-empty' },
        el('strong', { textContent: t('assistant.emptyTitle') }),
        el('p', { textContent: t('assistant.emptyText') })));
    }
    if (isAsking()) {
      nodes.push(el('div', { className: 'bubble assistant thinking' },
        el('span', { className: 'dots' }, el('i'), el('i'), el('i')),
        progress ? el('small', { textContent: t('assistant.progress', { text: progress }) }) : null));
    }
    this.transcript.replaceChildren(...nodes);
    this.transcript.scrollTop = this.transcript.scrollHeight;
  },
  update({ instant = false } = {}) {
    setFocused(this.chipNodes, this.index);
    this.keepChipInView(instant);
    if (this.modelTag) this.modelTag.textContent = info?.configured ? `${info.provider === 'openai' ? 'OpenAI' : 'Anthropic'} · ${info.model}` : t('assistant.notSetUp');
    renderHints();
  },
  /** The chip row never wraps: scroll it so the focused chip sits clear of the faded edges (4rem, see .chips.cut-*). */
  keepChipInView(instant) {
    const row = this.chipRow;
    const chip = this.chipNodes[this.index];
    if (!row?.isConnected || !chip) return;
    const edge = 4 * parseFloat(getComputedStyle(document.documentElement).fontSize);
    const max = row.scrollWidth - row.clientWidth;
    let left = row.scrollLeft;
    if (chip.offsetLeft - edge < left) left = chip.offsetLeft - edge;
    else if (chip.offsetLeft + chip.offsetWidth + edge > left + row.clientWidth) left = chip.offsetLeft + chip.offsetWidth + edge - row.clientWidth;
    left = Math.max(0, Math.min(left, max));
    row.classList.toggle('cut-start', left > 0);
    row.classList.toggle('cut-end', left < max - 1);
    row.scrollTo({ left, behavior: instant ? 'instant' : 'smooth' });
  },
  async compose() {
    const text = await openKeyboard({ title: t('assistant.kbTitle'), placeholder: t('assistant.kbPlaceholder') });
    if (text) await ask(text);
  },
  async reset() {
    if (isAsking()) return sound.bump();
    messages.length = 0;
    await attempt(() => api.resetAssistant());
    render();
  },
  async handle(action) {
    switch (action) {
      case 'left':
      case 'right': {
        const next = this.index + (action === 'left' ? -1 : 1);
        if (next < 0 || next >= this.chips.length) return sound.bump();
        this.index = next;
        sound.move();
        return this.update();
      }
      case 'up':
      case 'down':
        this.transcript.scrollBy({ top: action === 'up' ? -240 : 240, behavior: 'smooth' });
        return;
      case 'a':
        if (isAsking()) return sound.bump();
        sound.select();
        return this.chips[this.index].run();
      case 'y':
        return this.reset();
    }
  },
  pointer(node, index, dispatch) {
    if (!node.matches('.chip')) return;
    this.index = index;
    this.update();
    dispatch('a');
  },
  hints() {
    const busy = isAsking();
    return [['A', t('assistant.hintSelect'), { busy }], ['Y', t('assistant.hintNew'), { busy }], ['↕', t('assistant.hintHistory')], ['B', t('assistant.hintHome')]];
  }
};
