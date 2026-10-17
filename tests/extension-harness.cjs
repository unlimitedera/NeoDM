'use strict';
// Shared test harness: runs the real extension/background.js in a vm context with an in-memory chrome.* mock.
const fs = require('node:fs'), path = require('node:path'), vm = require('node:vm');
const extDir = path.join(__dirname, '../extension');
const source = name => fs.readFileSync(path.join(extDir, name), 'utf8');
const tick = async (n = 6) => { for (let i = 0; i < n; i++) await new Promise(r => setImmediate(r)); };
const json = v => JSON.parse(JSON.stringify(v));

function makeBackground({ settings = {}, nativeReply = req => (req.action === 'ping' ? { ok: true, host: 'com.ndm.bridge' } : { ok: true, id: 'saved-1' }), granted = false, sessionData = {}, openTabs = [] } = {}) {
  const hooks = {}, nativeRequests = [], timers = [], sentToTabs = [], badge = [];
  const event = name => ({ addListener: (f, ...rest) => { hooks[name] = f; hooks[name + ':args'] = rest; } });
  const local = { ...settings };
  const chrome = {
    runtime: {
      id: 'ext-id', lastError: undefined, getManifest: () => ({ version: JSON.parse(source('manifest.json')).version }), getURL: x => 'chrome-extension://ext-id/' + x, onInstalled: event('install'), onMessage: event('message'),
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
    tabs: { query: async () => openTabs, onUpdated: event('tabUpdated'), onRemoved: event('tabRemoved'), sendMessage: (tabId, msg, cb) => { sentToTabs.push({ tabId, msg: json(msg) }); cb && cb(); } },
    action: { setBadgeText: async o => badge.push(o), setBadgeBackgroundColor: async () => {} },
    contextMenus: { removeAll: async () => {}, create: m => nativeRequests.push({ menu: m.id }), onClicked: event('menu') },
    webRequest: { onBeforeRequest: event('request'), onCompleted: event('complete'), onErrorOccurred: event('error'), onHeadersReceived: event('headers') },
    downloads: { onCreated: event('download'), onDeterminingFilename: event('determining'), calls: [], pause: async () => { throw new Error('pause must not be used'); } },
    permissions: { contains: async () => granted },
    cookies: { getAllCookieStores: async () => [{ id: 's', tabIds: [1] }], getAll: async () => [{ name: 'a', value: 'b' }] },
  };
  const ctx = vm.createContext({ chrome, URL, setTimeout: (fn, ms) => { timers.push({ fn, ms }); return timers.length; }, clearTimeout: () => {}, console });
  ctx.importScripts = (...names) => names.forEach(n => vm.runInContext(source(n), ctx, { filename: n }));
  vm.runInContext(source('background.js'), ctx, { filename: 'background.js' });
  const sender = { id: 'ext-id', url: 'chrome-extension://ext-id/popup.html' };
  const pageSender = (tab = { id: 7, url: 'https://video.example/watch?v=1', title: 'My Clip - YouTube' }) => ({ id: 'ext-id', url: tab.url, frameId: 0, tab });
  const message = (m, s = sender) => new Promise(resolve => { const keep = hooks.message(m, s, resolve); if (!keep) resolve(undefined); });
  return { hooks, chrome, nativeRequests, timers, sentToTabs, sessionData, badge, local, sender, pageSender, message };
}
const response = (url, type, length, extra = {}) => ({ url, tabId: 7, method: 'GET', statusCode: 200, type: 'xmlhttprequest', responseHeaders: [{ name: 'Content-Type', value: type }, ...(length != null ? [{ name: 'Content-Length', value: String(length) }] : [])], ...extra });
module.exports = { source, tick, json, makeBackground, response, extDir };
