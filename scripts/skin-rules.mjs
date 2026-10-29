// Rules for a skin's stylesheet that keep the tab bar whole (pure, no files; the tests are in scripts/ui-logic-check.mjs, the check over
// real skin folders is scripts/skin-check.mjs). A skin may restyle the tabs (font, colours, gaps, the line under the active one) but never
// take one away: the plugin tabs (Smart Home, Lumen, ...) come after the built-in ones in the same #tab-list and the host and the page
// decide which tabs exist, not the skin. A rule that hides items of the tab bar, by name, position (nth-child, last-child) or as a whole,
// would make a plugin tab vanish in that skin only.
//
// This is a lint for the catalog review, not a protection: a stylesheet can always find another way. What enforces it is the runtime
// (ui/core/nav.js sets the visibility of the tabs, the arrows and the bar inline with !important); this finds the honest mistakes and the
// plain attempts at review time.

// Selectors that reach the tab bar or its items. The shoulder glyphs (LB/RB) beside the list are decoration and may be hidden.
const TAB_BAR = /#tab-list|#tabs(?![\w-])|\[data-tab\b|\.pinned\b|\.tab-more\b/i;
// The ids a selector like [id^="tab-"] may stand for.
const BAR_IDS = ['tab-list', 'tabs', 'top'];
const DECORATION = /\.shoulder\b/i;

// Declarations that make an element vanish or leave no room for it.
const HIDING = [
  [/^display$/i, (v) => /^none\b/i.test(v) || /^contents\b/i.test(v)],
  [/^visibility$/i, (v) => /^(hidden|collapse)\b/i.test(v)],
  [/^opacity$/i, (v) => /^0(\.0*)?%?$/.test(v)],
  [/^(width|max-width|height|max-height|font-size|flex-basis)$/i, (v) => /^0+(\.0+)?([a-z%]*)$/i.test(v)],
  [/^clip-path$/i, (v) => /inset\(\s*(50|100)%/i.test(v) || /circle\(\s*0/i.test(v)],
  [/^(transform)$/i, (v) => /scale(?:[XY])?\(\s*0\s*[,)]/i.test(v)],
  [/^scale$/i, (v) => /^0+(\.0+)?(\s|$)/.test(v)],
  [/^text-indent$/i, (v) => /^-\s*\d{3,}/.test(v)],
  [/^pointer-events$/i, (v) => /^none\b/i.test(v)],
  [/^filter$/i, (v) => /opacity\(\s*0/i.test(v)]
];

/** CSS escapes undone (\74 ab-list, \#, \-): a selector spelled with escapes is the selector it names. */
const unescape = (css) => css.replace(/\\([0-9a-fA-F]{1,6})\s?/g, (_, hex) => String.fromCodePoint(Math.min(parseInt(hex, 16) || 0xfffd, 0x10ffff))).replace(/\\(.)/gs, '$1');

/** Whether a selector reaches the tab bar or its items: by id, by an attribute selector on the id, by #top (itself or its lists) or by the tab marks. */
function reachesBar(sel) {
  if (TAB_BAR.test(sel)) return true;
  for (const m of sel.matchAll(/\[\s*id\s*([~|^$*]?=)\s*["']?([^"'\]\s]*)/gi)) {
    const value = m[2].toLowerCase();
    if (BAR_IDS.some((id) => (m[1] === '=' ? id === value : id.includes(value)))) return true;
  }
  if (/#top(?![\w-])/i.test(sel)) {
    const last = sel.split(/[\s>+~]+/).filter(Boolean).pop() ?? '';
    return /^#top(?![\w-])/i.test(last) || /^(li|ol|ul|nav)(?![\w-])/i.test(last);
  }
  return false;
}

const strip = (css) => unescape(css.replace(/\/\*[\s\S]*?\*\//g, ''));

/** The problems of one stylesheet: every rule that would hide the tab bar or its items, as readable lines (an empty list is fine). */
export function tabProblems(css) {
  const problems = [];
  const text = strip(String(css));
  // Plain rules; a rule inside @media/@supports is matched on its own ([^{}] cannot cross the at-rule's brace).
  for (const m of text.matchAll(/([^{}]+)\{([^{}]*)\}/g)) {
    const selector = m[1].trim();
    if (selector.startsWith('@')) continue;
    const reaches = selector.split(',').map((s) => s.trim()).filter((s) => reachesBar(s) && !DECORATION.test(s));
    if (!reaches.length) continue;
    for (const decl of m[2].split(';')) {
      const at = decl.indexOf(':');
      if (at < 0) continue;
      const prop = decl.slice(0, at).trim();
      const value = decl.slice(at + 1).replace(/!important/i, '').trim();
      if (HIDING.some(([name, hides]) => name.test(prop) && hides(value))) {
        problems.push(`"${reaches.join(', ')}": ${prop}: ${value} hides tabs (a skin must not take a tab away, plugin tabs included)`);
      }
    }
  }
  return problems;
}
