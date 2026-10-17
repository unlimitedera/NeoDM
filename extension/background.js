importScripts('bridge.js', 'sniff.js');
const { http, ack, transfer } = NDMBridge;
const { ingest, upsert, resetTracking, listItems, withInfo, isMediaResponse, fromDom, suggestedName, typeOfFile, typeOfItem, withDefaults, hostOf, isPaused } = NeoSniff;
const PAUSE_KEY = 'neodm.pause', SCHEMA = 2;
// The manifest version travels with every request as the optional field extVersion, so the app can tell when this extension and the app differ.
const extVersion = () => { try { return String(chrome.runtime.getManifest().version || ''); } catch { return ''; } };
const methods = new Map();
let observations = new Map();
// The desktop app shows an options dialog and replies only after the user decides. The host sends {pending:true} every ~15 s while the app is
// alive and the dialog is open; each one restarts a short idle timer, so the wait is unbounded while the app answers and ends (browser download
// restarted) only when it goes silent. A host that does not send pending frames (older install) keeps the old 330 s limit.
const PING_TIMEOUT_MS = 12000, DOWNLOAD_TIMEOUT_MS = 330000, PENDING_IDLE_MS = 60000;
/* ---- preferences: capture on by default, media-type filter, session-scoped pause (chrome.storage.session resets with the browser) ---- */
async function ensureDefaults() {
  const s = await chrome.storage.local.get(['schema', 'captureEnabled', 'mediaTypes']);
  if (s.schema >= SCHEMA) return;
  const set = { schema: SCHEMA };
  if (typeof s.captureEnabled !== 'boolean') set.captureEnabled = true; // never chosen explicitly -> on; an explicit choice is kept
  if (!s.mediaTypes) set.mediaTypes = withDefaults();
  await chrome.storage.local.set(set);
}
async function prefs() {
  const s = await chrome.storage.local.get(['captureEnabled', 'mediaTypes']);
  const pause = (await chrome.storage.session.get(PAUSE_KEY))[PAUSE_KEY];
  return { capture: s.captureEnabled !== false, types: withDefaults(s.mediaTypes), pause: { all: pause?.all === true, hosts: Array.isArray(pause?.hosts) ? pause.hosts : [] } };
}
ensureDefaults().catch(() => {});
async function status(text) { await chrome.storage.local.set({ status: text }); }
function native(request, timeoutMs = request?.action === 'ping' ? PING_TIMEOUT_MS : DOWNLOAD_TIMEOUT_MS) {
  return new Promise((resolve, reject) => {
    const port = chrome.runtime.connectNative('com.ndm.bridge');
    let done = false;
    const finish = (error, reply) => { if (done) return; done = true; clearTimeout(timer); port.disconnect(); error ? reject(error) : resolve(reply); };
    let timer = setTimeout(() => finish(new Error('Bridge timed out')), timeoutMs);
    port.onMessage.addListener(reply => {
      if (reply?.pending === true && !done) { clearTimeout(timer); timer = setTimeout(() => finish(new Error('Bridge stopped answering')), PENDING_IDLE_MS); return; }
      finish(null, reply);
    });
    port.onDisconnect.addListener(() => finish(new Error(chrome.runtime.lastError?.message || 'Bridge disconnected')));
    try { port.postMessage(request); } catch (e) { finish(e); }
  });
}
async function send(request, tab) {
  if (!http(request.url) || (request.method && request.method !== 'GET')) throw new Error('Only direct HTTP(S) GET URLs are supported');
  const settings = await chrome.storage.local.get(['cookiesEnabled']);
  request.cookie = '';
  if (settings.cookiesEnabled && await chrome.permissions.contains({ permissions: ['cookies'], origins: [new URL(request.url).origin + '/*'] })) {
    // Never log cookies. Preserve browser cookie-store selection when available.
    const stores = await chrome.cookies.getAllCookieStores();
    const store = stores.find(s => s.tabIds.includes(tab?.id));
    const cookies = await chrome.cookies.getAll({ url: request.url, ...(store ? { storeId: store.id } : {}) });
    request.cookie = cookies.map(c => `${c.name}=${c.value}`).join('; ');
  }
  const v = extVersion();
  const reply = await native({ ...request, ...(v ? { extVersion: v } : {}), keepAlive: true }); // keepAlive: older hosts ignore it and answer once, as before
  if (reply?.ok === false && typeof reply.error === 'string') { const e = new Error(reply.error); e.cancelled = reply.cancelled === true; throw e; } // cancelled: the user pressed Cancel in the app dialog
  if (!ack(reply)) throw new Error('Application did not durably accept the download');
  return reply;
}

/* ---- per-tab media list: survives MV3 worker restarts through chrome.storage.session ---- */
const tabCache = new Map(), tabQueues = new Map();
const key = tabId => 'neodm.media.' + tabId;
const pageKey = url => { try { const u = new URL(url); u.hash = ''; return u.href; } catch { return ''; } };
async function loadTab(tabId) {
  if (tabCache.has(tabId)) return tabCache.get(tabId);
  const stored = (await chrome.storage.session.get(key(tabId)))[key(tabId)];
  const state = stored && Array.isArray(stored.items) ? stored : { page: '', items: [] };
  tabCache.set(tabId, state); return state;
}
/** Serialize all mutations per tab. fn(state) mutates state and returns true when the list changed. */
function mutateTab(tabId, fn) {
  const next = (tabQueues.get(tabId) || Promise.resolve()).then(async () => {
    const state = await loadTab(tabId), changed = fn(state);
    if (changed) { await chrome.storage.session.set({ [key(tabId)]: state }); if (changed !== 'quiet') { await badge(tabId, state); await notifyTab(tabId, state); } } // 'quiet': trackers changed, the list did not
    return state;
  }).catch(() => undefined);
  tabQueues.set(tabId, next); return next;
}
async function badge(tabId, state) {
  try { await chrome.action.setBadgeBackgroundColor({ tabId, color: '#2da57d' }); await chrome.action.setBadgeText({ tabId, text: state.items.length ? String(state.items.length) : '' }); } catch {}
}
/** Items the ribbon may show: honours the type filter and the session pause for this page. */
async function visibleItems(state) {
  const p = await prefs();
  if (isPaused(p.pause, state.page, state.host)) return [];
  return listItems({ ...state, items: state.items.filter(i => p.types[typeOfItem(i)]) });
}
/** Same list plus the informational row for segmented streams (popup only, never the ribbon). */
async function popupItems(state) {
  const p = await prefs();
  if (isPaused(p.pause, state.page, state.host)) return [];
  return withInfo(state, await visibleItems(state));
}
async function notifyTab(tabId, state) { try { const items = await visibleItems(state); chrome.tabs.sendMessage(tabId, { type: 'neodm-media', items }, () => void chrome.runtime.lastError); } catch {} }
async function refreshTabs() { try { for (const t of await chrome.tabs.query({})) if (Number.isInteger(t.id) && http(t.url)) notifyTab(t.id, await loadTab(t.id)); } catch {} }
function addItem(tabId, item) { return mutateTab(tabId, state => upsert(state, item)); }
chrome.webRequest.onHeadersReceived.addListener(async details => {
  if (!isMediaResponse(details)) return;
  const types = (await prefs()).types; // e.g. images/GIFs are only collected while their type is ticked
  let origin = ''; try { origin = new URL(details.initiator || details.documentUrl).origin; } catch {}
  await mutateTab(details.tabId, state => {
    const changed = ingest(state, details, types);
    if (origin && origin !== 'null' && state.host !== origin) { state.host = origin; return true; }
    return changed;
  });
}, { urls: ['http://*/*', 'https://*/*'], types: ['media', 'xmlhttprequest', 'other', 'object', 'image'] }, ['responseHeaders']);
chrome.tabs.onUpdated.addListener((tabId, change) => {
  if (!change.url) return;
  const page = pageKey(change.url); // also fires for SPA (pushState) navigation, e.g. the next YouTube video
  mutateTab(tabId, state => {
    if (state.page === page) return false;
    const first = !state.page; state.page = page;
    if (!first) resetTracking(state); // first sighting after a worker restart must not wipe items already sniffed
    return true;
  });
});
chrome.tabs.onRemoved.addListener(tabId => { tabCache.delete(tabId); tabQueues.delete(tabId); chrome.storage.session.remove(key(tabId)); });

chrome.runtime.onInstalled.addListener(async () => {
  await ensureDefaults();
  await chrome.contextMenus.removeAll();
  chrome.contextMenus.create({ id: 'link', title: 'Download link with NeoDM', contexts: ['link'] });
  chrome.contextMenus.create({ id: 'media', title: 'Download media with NeoDM', contexts: ['video', 'audio', 'image'] });
});
chrome.contextMenus.onClicked.addListener(async (info, tab) => {
  try {
    const url = info.menuItemId === 'link' ? info.linkUrl : info.srcUrl;
    if (!http(url)) throw new Error('Not a direct HTTP(S) URL. Blob, data and protected streams are unsupported.');
    await send({ url, fileName: '', referer: http(info.pageUrl) ? info.pageUrl : '', method: 'GET' }, tab);
    await status('Accepted by NeoDM');
  } catch (e) { await status(e.message); }
});
chrome.webRequest.onBeforeRequest.addListener(details => {
  // Keep the initial method across redirects. Unknown requests after worker restart are not captured.
  const method = methods.get(details.requestId) || details.method;
  methods.set(details.requestId, method);
  const previous = observations.get(details.url);
  // A POST on the same URL makes correlation ambiguous; retain its veto for 60s.
  observations.set(details.url, { method: previous?.method !== 'GET' && previous && Date.now() - previous.time < 60000 ? previous.method : method, time: Date.now() });
  if (observations.size > 500) observations.delete(observations.keys().next().value);
}, { urls: ['http://*/*', 'https://*/*'] });
const forget = details => methods.delete(details.requestId);
chrome.webRequest.onCompleted.addListener(forget, { urls: ['http://*/*', 'https://*/*'] });
chrome.webRequest.onErrorOccurred.addListener(forget, { urls: ['http://*/*', 'https://*/*'] });
// The browser's own decision (Content-Disposition, redirects, sniffed type) arrives here; it is remembered for the capture below. suggest() is never called, so the browser is not delayed.
const suggested = new Map();
chrome.downloads.onDeterminingFilename?.addListener(item => {
  suggested.set(item.id, { filename: item.filename, mime: item.mime });
  if (suggested.size > 100) suggested.delete(suggested.keys().next().value);
});
chrome.downloads.onCreated.addListener(async item => {
  const p = await prefs();
  if (!p.capture || isPaused(p.pause, item.referrer, item.finalUrl, item.url)) return;
  if (!p.types[typeOfFile(item.filename || item.finalUrl || item.url, item.mime)]) return; // type not ticked: the browser keeps it
  const observed = observations.get(item.url), final = observations.get(item.finalUrl || item.url);
  const fresh = o => o?.method === 'GET' && Date.now() - o.time < 60000;
  // transfer(): cancel + erase the browser download immediately, THEN NeoDM shows its dialog (up to 5 min).
  // Only app-unreachable/error restarts the browser download; the user's Cancel in NeoDM discards it.
  const hint = suggested.get(item.id); suggested.delete(item.id);
  const result = await transfer(item, chrome.downloads, req => send(req), fresh(observed) && fresh(final), hint);
  if (!result.skipped) await status(result.ok ? 'Taken over by NeoDM; the browser download was removed' : result.error);
});

async function downloadItem(tab, id) {
  const state = await loadTab(tab.id), item = state.items.find(i => i.id === id);
  if (!item) return { ok: false, error: 'This media is no longer available on the page.' };
  const request = url => ({ url, fileName: suggestedName(item, tab.title), referer: http(tab.url) ? tab.url : '', method: 'GET' });
  try { return await send(request(item.url), tab); }
  catch (e) {
    // The URL without range parameters failed to start: try the URL exactly as the page requested it, once.
    if (!e?.cancelled && item.rawUrl && item.rawUrl !== item.url && http(item.rawUrl)) {
      try { return await send(request(item.rawUrl), tab); } catch (e2) { e = e2; }
    }
    return { ok: false, error: e?.message || 'NeoDM did not acknowledge this URL.' };
  }
}
chrome.runtime.onMessage.addListener((message, sender, respond) => {
  if (sender.id !== chrome.runtime.id || !message) return false;
  const popup = sender.url?.split(/[?#]/)[0] === chrome.runtime.getURL('popup.html');
  const page = Number.isInteger(sender.tab?.id) && sender.frameId === 0 && http(sender.url); // content script in a top frame
  if (page && message.action === 'media-list') { loadTab(sender.tab.id).then(visibleItems).then(items => respond({ items })); return true; }
  if (page && message.action === 'media-dom') {
    const sources = Array.isArray(message.sources) ? message.sources.slice(0, 50) : [];
    const found = sources.map(s => fromDom(String(s?.url), s?.tag === 'audio' ? 'audio' : 'video')).filter(Boolean);
    const mse = sources.some(s => /^blob:/i.test(String(s?.url))); // a blob: player streams through MediaSource: the network pieces are not files
    mutateTab(sender.tab.id, state => { if (!mse || state.mse) return false; state.mse = true; return true; })
      .then(() => Promise.all(found.map(i => addItem(sender.tab.id, i)))).then(() => respond({ ok: true })); return true;
  }
  if (page && message.action === 'media-download') { downloadItem(sender.tab, String(message.id)).then(respond); return true; }
  if (popup && message.action === 'popup-media' && Number.isInteger(message.tabId)) { loadTab(message.tabId).then(popupItems).then(items => respond({ items }), () => respond({ items: [] })); return true; }
  if (popup && message.action === 'popup-download' && Number.isInteger(message.tab?.id)) { downloadItem({ id: message.tab.id, url: String(message.tab.url || ''), title: String(message.tab.title || '') }, String(message.id)).then(respond); return true; }
  if (popup && message.action === 'pause-set') {
    setPause(message).then(respond, () => respond({ ok: false })); return true;
  }
  if (!popup || message.action !== 'test') return false;
  native({ action: 'ping', extVersion: extVersion() }).then(respond, () => respond({ ok: false, error: 'Native host unavailable. Check installation.' }));
  return true;
});

/** Session-scoped pause. scope 'all' = every site; scope 'site' = one hostname. on=false re-enables it. */
async function setPause({ scope, host, on }) {
  const p = (await prefs()).pause;
  if (scope === 'all') p.all = on === true;
  else if (scope === 'site') {
    const h = String(host || '').toLowerCase();
    if (!h || !/^[a-z0-9.\-:\[\]]+$/.test(h)) return { ok: false, error: 'No site to pause.' };
    p.hosts = p.hosts.filter(x => x !== h); if (on === true) p.hosts.push(h);
  } else return { ok: false };
  await chrome.storage.session.set({ [PAUSE_KEY]: p });
  await refreshTabs();
  return { ok: true, pause: p };
}
