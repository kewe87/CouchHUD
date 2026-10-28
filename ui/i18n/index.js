// All UI texts, one file per area (the same file names in de/ and en/). Plain data, so Node can read it too
// (scripts/i18n-check.mjs). Keys are "area.name"; values are a string or { one, other } for plurals.
import deSettings from './de/settings.js';
import deSetup from './de/setup.js';
import deTransparency from './de/transparency.js';
import deLegal from './de/legal.js';
import deSystem from './de/system.js';
import deViews1 from './de/views1.js';
import deViews2 from './de/views2.js';
import deComponents1 from './de/components1.js';
import deComponents2 from './de/components2.js';
import deCore from './de/core.js';
import deMock from './de/mock.js';
import deWizard from './de/wizard.js';
import deDownloads from './de/downloads.js';
import deTv from './de/tv.js';
import deProfiles from './de/profiles.js';
import dePlugins from './de/plugins.js';
import enSettings from './en/settings.js';
import enSetup from './en/setup.js';
import enTransparency from './en/transparency.js';
import enLegal from './en/legal.js';
import enSystem from './en/system.js';
import enViews1 from './en/views1.js';
import enViews2 from './en/views2.js';
import enComponents1 from './en/components1.js';
import enComponents2 from './en/components2.js';
import enCore from './en/core.js';
import enMock from './en/mock.js';
import enWizard from './en/wizard.js';
import enDownloads from './en/downloads.js';
import enTv from './en/tv.js';
import enProfiles from './en/profiles.js';
import enPlugins from './en/plugins.js';

export const FILES = ['settings', 'setup', 'transparency', 'legal', 'system', 'views1', 'views2', 'components1', 'components2', 'core', 'mock', 'wizard', 'downloads', 'tv', 'profiles', 'plugins'];

export const dictionaries = {
  de: Object.assign({}, deSettings, deSetup, deTransparency, deLegal, deSystem, deViews1, deViews2, deComponents1, deComponents2, deCore, deMock, deWizard, deDownloads, deTv, deProfiles, dePlugins),
  en: Object.assign({}, enSettings, enSetup, enTransparency, enLegal, enSystem, enViews1, enViews2, enComponents1, enComponents2, enCore, enMock, enWizard, enDownloads, enTv, enProfiles, enPlugins)
};
