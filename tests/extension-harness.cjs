'use strict';
// Shared test harness: runs the real extension/background.js in a vm context with an in-memory chrome.* mock.
const fs = require('node:fs'), path = require('node:path'), vm = require('node:vm');
const extDir = path.join(__dirname, '../extension');
const source = name => fs.readFileSync(path.join(extDir, name), 'utf8');
const tick = async (n = 6) => { for (let i = 0; i < n; i++) await new Promise(r => setImmediate(r)); };
const json = v => JSON.parse(JSON.stringify(v));

function makeBackground({ uiLanguage = 'en', settings = {}, nativeReply = req => (req.action === 'ping' ? { ok: true, host: 'com.ndm.bridge' } : { ok: true, id: 'saved-1' }), granted = false, sessionData = {}, openTabs = [], tabReply = () => undefined } = {}) {
  const hooks = {}, nativeRequests = [], timers = [], sentToTabs = [], badge = [], menuUpdates = [], menus = [];
  const event = name => ({ addListener: (f, ...rest) => { hooks[name] = f; hooks[name + ':args'] = rest; } });
  const local = { ...settings };
  const chrome = {
    runtime: {
      id: 'ext-id', lastError: undefined, getManifest: () => ({ version: JSON.parse(source('manifest.json')).version }), getURL: x => 'chrome-extension://ext-id/' + x, onInstalled: event('install'), onStartup: event('startup'), onMessage: event('message'),
      connectNative: name => {
        if (name !== 'com.ndm.bridge') throw new Error('wrong host ' + name);
        let receive, disconnected;
        return {
          onMessage: { addListener: f => { receive = f; } }, onDisconnect: { addListener: f => { disconnected = f; } }, disconnect: () => {},
          postMessage: req => { nativeRequests.push(json(req)); Promise.resolve(nativeReply(req)).then(r => (r instanceof Error ? disconnected() : [].concat(r).forEach(x => receive(x))), () => disconnected()); },
        };
      },
    },
    storage: {
      local: { get: async keys => { const out = {}; for (const k of [].concat(keys ?? Object.keys(local))) if (k in local) out[k] = local[k]; return out; }, set: async o => Object.assign(local, o) },
      session: { get: async k => (typeof k === 'string' ? (k in sessionData ? { [k]: json(sessionData[k]) } : {}) : json(sessionData)), set: async o => Object.assign(sessionData, json(o)), remove: async k => { delete sessionData[k]; } },
    },
    tabs: { query: async () => openTabs, onUpdated: event('tabUpdated'), onActivated: event('tabActivated'), onRemoved: event('tabRemoved'), sendMessage: (tabId, msg, cb) => { sentToTabs.push({ tabId, msg: json(msg) }); Promise.resolve(tabReply(tabId, msg)).then(r => cb && cb(r)); } },
    action: { setBadgeText: async o => badge.push(o), setBadgeBackgroundColor: async () => {} },
    contextMenus: { removeAll: async () => {}, create: (m, cb) => { menus.push(m); cb && cb(); }, update: async (id, props) => { menuUpdates.push({ id, ...props }); }, onClicked: event('menu') },
    webRequest: { onBeforeRequest: event('request'), onBeforeRedirect: event('redirect'), onCompleted: event('complete'), onErrorOccurred: event('error'), onHeadersReceived: event('headers') },
    downloads: { onCreated: event('download'), onDeterminingFilename: event('determining'), calls: [], pause: async () => { throw new Error('pause must not be used'); } },
    permissions: { contains: async () => granted },
    i18n: makeI18n(uiLanguage),
    cookies: { getAllCookieStores: async () => [{ id: 's', tabIds: [1] }], getAll: async () => [{ name: 'a', value: 'b' }] },
  };
  const ctx = vm.createContext({ chrome, URL, setTimeout: (fn, ms) => { timers.push({ fn, ms }); return timers.length; }, clearTimeout: () => {}, console });
  ctx.importScripts = (...names) => names.forEach(n => vm.runInContext(source(n), ctx, { filename: n }));
  vm.runInContext(source('background.js'), ctx, { filename: 'background.js' });
  const sender = { id: 'ext-id', url: 'chrome-extension://ext-id/popup.html' };
  const pageSender = (tab = { id: 7, url: 'https://video.example/watch?v=1', title: 'My Clip - YouTube' }) => ({ id: 'ext-id', url: tab.url, frameId: 0, tab });
  const message = (m, s = sender) => new Promise(resolve => { const keep = hooks.message(m, s, resolve); if (!keep) resolve(undefined); });
  return { hooks, chrome, nativeRequests, timers, sentToTabs, menuUpdates, menus, sessionData, badge, local, sender, pageSender, message };
}
/** Replays a request chain through the webRequest hooks: hops = [{ url, method, status }], status = redirect code leaving the hop (0 = none). */
function chain(b, id, hops, extra = {}) {
  hops.forEach((h, i) => {
    b.hooks.request({ requestId: id, url: h.url, method: h.method, documentUrl: extra.documentUrl, tabId: 7 });
    if (h.status) b.hooks.redirect({ requestId: id, url: h.url, statusCode: h.status, redirectUrl: hops[i + 1]?.url });
  });
  if (!extra.open) b.hooks.complete({ requestId: id });
}
const response = (url, type, length, extra = {}) => ({ url, tabId: 7, method: 'GET', statusCode: 200, type: 'xmlhttprequest', responseHeaders: [{ name: 'Content-Type', value: type }, ...(length != null ? [{ name: 'Content-Length', value: String(length) }] : [])], ...extra });

/** chrome.i18n mock backed by the real _locales files: getMessage fills $NAME$ placeholders from the substitutions like the browser does, and falls back to the default locale. */
function makeI18n(lang = 'en') {
  const load = l => { try { return JSON.parse(fs.readFileSync(path.join(extDir, '_locales', l, 'messages.json'), 'utf8')); } catch { return {}; } };
  const base = lang.toLowerCase().split(/[-_]/)[0], tables = [load(lang.replace('-', '_')), load(base), load('en')];
  const getMessage = (key, subs) => {
    const entry = tables.map(x => x[key]).find(Boolean); if (!entry) return '';
    const list = subs === undefined ? [] : [].concat(subs);
    return entry.message.replace(/\$([A-Za-z0-9_@]+)\$/g, (m, name) => {
      const ph = entry.placeholders && Object.entries(entry.placeholders).find(([n]) => n.toLowerCase() === name.toLowerCase());
      if (!ph) return m;
      return String(ph[1].content).replace(/\$(\d)/g, (_, n) => list[Number(n) - 1] ?? '');
    });
  };
  return { getMessage, getUILanguage: () => lang };
}
/** Installs chrome.i18n on a jsdom window and runs the real i18n.js there (popup and content script tests). */
function withI18n(w, lang = 'en') { w.chrome = w.chrome || {}; w.chrome.i18n = makeI18n(lang); w.eval(source('i18n.js')); }
module.exports = { makeI18n, withI18n, source, tick, json, makeBackground, response, chain, extDir };
