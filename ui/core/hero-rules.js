// Pure rules for the detail page's hero layer (art.js applies them).

/** A url as a CSS url("…") value: quotes, backslashes and line breaks cannot end the string early. */
export function cssUrl(url) {
  return `url("${String(url).replace(/[\\"\n\r\f]/g, (c) => `\${c.charCodeAt(0).toString(16)} `)}")`;
}

/** What identifies the hero shown for an item: the item and its first hero address. Same key: nothing to reload (no flicker). */
export function heroKey(item) {
  return `${item?.id ?? ''}|${item?.art?.hero?.[0] ?? ''}`;
}
