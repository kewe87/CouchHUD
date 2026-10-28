// Built-in skins: only the Couch skin ships with the app (since 0.6). Every other skin is a stylesheet scoped to
// :root[data-skin="user:<folder>"] plus an optional skin.json with names, descriptions and button glyph labels, installed from the
// catalog or by hand into %APPDATA%\CouchHud\skins, see docs/SKINS.md.

import { t } from '../core/i18n.js';

export const BUILTIN_SKINS = [
  {
    id: 'default',
    get name() { return t('skins.name.default'); },
    get description() { return t('skins.default'); },
    css: null,
    glyphs: {}
  }
];
