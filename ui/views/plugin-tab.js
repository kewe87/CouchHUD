// A main tab of a plugin: the plugin's page drawn by the page renderer (plugin-page.js) as a tab of the tab bar. The plugin brings no HTML or
// script; the host decides which tabs exist (getPluginTabs) and the page behind it is read, drawn and pressed like any plugin page, with
// the same lock and parents' code rules.

import { api } from '../core/api.js';
import { cleanPluginTabs, tabRegistry } from '../core/plugin-tabs.js';
import { declarativePage } from './plugin-page.js';

function makeView(tab) {
  const info = { id: tab.page, title: tab.pageTitle, description: tab.pageDescription };
  const view = declarativePage({ id: tab.plugin, name: tab.pluginName }, info, { asTab: true });
  view.tabInfo = info;
  // The tab bar reads the name each time it draws: a refreshed title shows at once.
  Object.defineProperty(view, 'name', { get: () => view.tabTitle, configurable: true });
  // The tab bar shows the title only, like every core tab: the icon and glyph a plugin sends are not drawn there.
  refreshView(view, tab);
  return view;
}

function refreshView(view, tab) {
  view.tabTitle = tab.title;
  view.tabArea = tab.area;
  Object.assign(view.tabInfo, { title: tab.pageTitle, description: tab.pageDescription });
}

// A tab that goes away (plugin switched off, removed, failed) lets go of its page: a value still settling is not sent any more.
const registry = tabRegistry(makeView, refreshView, (view) => view.onDrop?.());

/** Asks the host for the plugin tabs and returns their views (kept between calls). Nothing (an older host, an error): no tabs. */
export async function loadPluginTabs() {
  let list;
  try {
    list = await api.getPluginTabs?.();
  } catch (err) {
    console.error(err);
    list = [];
  }
  const views = registry.sync(cleanPluginTabs(list));
  return views;
}
