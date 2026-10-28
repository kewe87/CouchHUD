// Built-in skins. A skin is a stylesheet scoped to :root[data-skin="<id>"]
// plus optional button glyph labels. User skins in %APPDATA%\CouchHud\skins
// use the same shape (skin.json + skin.css), see docs/SKINS.md.

import { t } from '../core/i18n.js';

export const BUILTIN_SKINS = [
  {
    id: 'default',
    get name() { return t('skins.name.default'); },
    get description() { return t('skins.default'); },
    css: null,
    glyphs: {}
  },
  {
    id: 'deepsea',
    get name() { return t('skins.name.deepsea'); },
    get description() { return t('skins.deepsea'); },
    css: 'skins/deepsea.css',
    glyphs: {}
  },
  {
    id: 'greenlight',
    get name() { return t('skins.name.greenlight'); },
    get description() { return t('skins.greenlight'); },
    css: 'skins/greenlight.css',
    glyphs: { '≡': '☰' }
  },
  {
    id: 'neoncity',
    get name() { return t('skins.name.neoncity'); },
    get description() { return t('skins.neoncity'); },
    css: 'skins/neoncity.css',
    glyphs: { '≡': 'MENU' }
  },
  {
    id: 'nova',
    get name() { return t('skins.name.nova'); },
    get description() { return t('skins.nova'); },
    css: 'skins/nova.css',
    glyphs: {}
  }
];
