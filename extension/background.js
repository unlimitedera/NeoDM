importScripts('i18n.js', 'bridge.js', 'sniff.js');
const { http, ack, transfer } = NDMBridge;
const { ingest, upsert, resetTracking, listItems, withInfo, isMediaResponse, fromDom, suggestedName, typeOfFile, typeOfItem, withDefaults, hostOf, isPaused } = NeoSniff;
const PAUSE_KEY = 'neodm.pause', SCHEMA = 2;
// The manifest version travels with every request as the optional field extVersion, so the app can tell when this extension and the app differ.
const extVersion = () => { try { return String(chrome.runtime.getManifest().version || ''); } catch { return ''; } };
// Capture decisions are made per hop. chains: requestId -> { time, page, hops: [{ url, method, status }] }, where status is the redirect code that left the hop (0 if none).
// observations: url -> the latest hop seen for that URL, with a reference to its chain. A finished chain stays reachable through observations, because the browser
// reports the download after the request completed.
const chains = new Map();
let observations = new Map();
const HOP_CAP = 20, CHAIN_TTL_MS = 300000, SEEN_MS = 60000, TABLE_CAP = 500;
const LOG_KEY = 'neodm.log', LOG_MAX = 20;
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
    let timer = setTimeout(() => finish(new Error(NeoI18n.t('err_bridge_timeout'))), timeoutMs);
    port.onMessage.addListener(reply => {
      if (reply?.pending === true && !done) { clearTimeout(timer); timer = setTimeout(() => finish(new Error(NeoI18n.t('err_bridge_stopped'))), PENDING_IDLE_MS); return; }
      finish(null, reply);
    });
    port.onDisconnect.addListener(() => finish(new Error(chrome.runtime.lastError?.message || NeoI18n.t('err_bridge_disconnected'))));
    try { port.postMessage(request); } catch (e) { finish(e); }
  });
}
async function send(request, tab) {
  if (!http(request.url) || (request.method && request.method !== 'GET')) throw new Error(NeoI18n.t('err_only_http_get'));
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
  if (!ack(reply)) throw new Error(NeoI18n.t('err_not_durable'));
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
  chrome.contextMenus.create({ id: 'link', title: NeoI18n.t('menu_link'), contexts: ['link'] });
  chrome.contextMenus.create({ id: 'media', title: NeoI18n.t('menu_media'), contexts: ['video', 'audio', 'image'] });
});
chrome.contextMenus.onClicked.addListener(async (info, tab) => {
  try {
    const url = info.menuItemId === 'link' ? info.linkUrl : info.srcUrl;
    if (!http(url)) throw new Error(NeoI18n.t('err_not_direct'));
    await send({ url, fileName: '', referer: http(info.pageUrl) ? info.pageUrl : '', method: 'GET' }, tab);
    await status(NeoI18n.t('status_accepted'));
  } catch (e) { await status(e.message); }
});
function track(details) {
  const now = Date.now();
  for (const [id, c] of chains) { if (now - c.time > CHAIN_TTL_MS) chains.delete(id); else break; } // the worker may have restarted: stale chains are dropped
  let chain = chains.get(details.requestId);
  if (!chain) {
    chain = { time: now, page: details.documentUrl || '', hops: [] }; chains.set(details.requestId, chain);
    if (chains.size > TABLE_CAP) chains.delete(chains.keys().next().value);
  }
  if (chain.hops.length >= HOP_CAP) return;
  chain.hops.push({ url: details.url, method: details.method || 'GET', status: 0 });
  const hop = chain.hops[chain.hops.length - 1], previous = observations.get(details.url);
  // An earlier POST to the same URL from another request makes correlation ambiguous; retain its veto for 60 s.
  const sawPost = !!previous && now - previous.time < SEEN_MS && previous.chain !== chain && (previous.sawPost || previous.method === 'POST');
  observations.delete(details.url);
  observations.set(details.url, { method: hop.method, time: now, chain, index: chain.hops.length - 1, sawPost });
  if (observations.size > TABLE_CAP) observations.delete(observations.keys().next().value);
}
const WEB = { urls: ['http://*/*', 'https://*/*'] };
chrome.webRequest.onBeforeRequest.addListener(track, WEB);
chrome.webRequest.onBeforeRedirect?.addListener(details => { const hop = chains.get(details.requestId)?.hops.at(-1); if (hop) hop.status = details.statusCode; }, WEB);
const forget = details => chains.delete(details.requestId);
chrome.webRequest.onCompleted.addListener(forget, WEB);
chrome.webRequest.onErrorOccurred.addListener(forget, WEB);
// The browser's own decision (Content-Disposition, redirects, sniffed type) arrives here; it is remembered for the capture below. suggest() is never called, so the browser is not delayed.
const suggested = new Map();
chrome.downloads.onDeterminingFilename?.addListener(item => {
  suggested.set(item.id, { filename: item.filename, mime: item.mime });
  if (suggested.size > 100) suggested.delete(suggested.keys().next().value);
});
/** Decides from the recorded hops whether the browser download can be repeated by NeoDM as a plain GET of its final URL. */
function verdict(item) {
  const final = item.finalUrl || item.url, o = observations.get(final), first = observations.get(item.url);
  if (!o || Date.now() - o.time > SEEN_MS || (item.url !== final && (!first || first.chain !== o.chain || first.index !== 0))) return { code: 'unseen' };
  const hops = o.chain.hops, last = hops[o.index];
  if (last.method !== 'GET') {
    const before = hops[o.index - 1];
    return { code: before?.method === 'POST' && (before.status === 307 || before.status === 308) ? 'redirect-keeps-post' : 'form-post' };
  }
  let viaPost = false;
  for (let i = 0; i < o.index; i++) {
    if (hops[i].method !== 'POST') continue;
    viaPost = true; // only 301, 302 and 303 turn a POST into a GET; 307 and 308 keep the method and the body
    if (hops[i].status === 307 || hops[i].status === 308) return { code: 'redirect-keeps-post' };
    if (![301, 302, 303].includes(hops[i].status) || hops[i + 1].method !== 'GET') return { code: 'form-post' };
  }
  if (o.sawPost && !viaPost) return { code: 'post-ambiguous' };
  return { capture: true, viaPost, referer: o.chain.page };
}
// Codes of the capture log; the text of each is reason_<code> in _locales (hyphens become underscores).
const REASONS = new Set(['sent', 'cancelled', 'form-post', 'redirect-keeps-post', 'post-ambiguous', 'unseen', 'blob', 'protocol', 'private', 'type', 'paused', 'off', 'unreachable']);
const ACTIONABLE = new Set(['off', 'paused', 'type']);
let logQueue = Promise.resolve();
/** Keeps the last LOG_MAX capture decisions (time, host, decision, code, reason) in chrome.storage.session. */
function note(item, decision, code) {
  const entry = { time: Date.now(), host: hostOf(item.url) || '', decision, code, reason: REASONS.has(code) ? NeoI18n.t('reason_' + code.replace(/-/g, '_')) : code };
  logQueue = logQueue.then(async () => {
    const stored = (await chrome.storage.session.get(LOG_KEY))[LOG_KEY];
    await chrome.storage.session.set({ [LOG_KEY]: (Array.isArray(stored) ? stored : []).concat(entry).slice(-LOG_MAX) });
  }).catch(() => {});
  if (decision === 'skipped' && ACTIONABLE.has(code)) flashBadge();
  return logQueue;
}
function flashBadge() {
  try {
    Promise.resolve(chrome.action.setBadgeBackgroundColor({ color: '#c0392b' })).catch(() => {});
    Promise.resolve(chrome.action.setBadgeText({ text: '!' })).catch(() => {});
    setTimeout(() => { try { Promise.resolve(chrome.action.setBadgeText({ text: '' })).catch(() => {}); } catch {} }, 2000);
  } catch {}
}
chrome.downloads.onCreated.addListener(async item => {
  if (item.byExtensionId || item.paused || item.state !== 'in_progress') return; // our own restart, another extension, or not running: not a capture decision
  const p = await prefs(), final = item.finalUrl || item.url, skip = code => note(item, 'skipped', code);
  if (item.incognito) return skip('private');
  if (/^(blob|data|filesystem):/i.test(item.url) || /^(blob|data|filesystem):/i.test(final)) return skip('blob');
  if (!http(item.url) || !http(final)) return skip('protocol');
  if (!p.capture) return skip('off');
  if (isPaused(p.pause, item.referrer, item.finalUrl, item.url)) return skip('paused');
  if (!p.types[typeOfFile(item.filename || final, item.mime)]) return skip('type'); // type not ticked: the browser keeps it
  const v = verdict(item);
  if (!v.capture) return skip(v.code);
  // transfer(): cancel + erase the browser download immediately, THEN NeoDM shows its dialog (up to 5 min).
  // Only app-unreachable/error restarts the browser download; the user's Cancel in NeoDM discards it.
  const hint = suggested.get(item.id); suggested.delete(item.id);
  const result = await transfer(item, chrome.downloads, req => send(req), true, hint, { referer: v.referer, noProbe: v.viaPost });
  if (result.skipped) return skip('unseen');
  if (result.ok) await note(item, 'captured', 'sent'); else if (result.cancelled) await note(item, 'captured', 'cancelled'); else await note(item, 'skipped', 'unreachable');
  await status(result.ok ? NeoI18n.t('status_taken') : result.error);
});

async function downloadItem(tab, id) {
  const state = await loadTab(tab.id), item = state.items.find(i => i.id === id);
  if (!item) return { ok: false, error: NeoI18n.t('err_media_gone') };
  const request = url => ({ url, fileName: suggestedName(item, tab.title), referer: http(tab.url) ? tab.url : '', method: 'GET' });
  try { return await send(request(item.url), tab); }
  catch (e) {
    // The URL without range parameters failed to start: try the URL exactly as the page requested it, once.
    if (!e?.cancelled && item.rawUrl && item.rawUrl !== item.url && http(item.rawUrl)) {
      try { return await send(request(item.rawUrl), tab); } catch (e2) { e = e2; }
    }
    return { ok: false, error: e?.message || NeoI18n.t('err_no_ack_url') };
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
  native({ action: 'ping', extVersion: extVersion() }).then(respond, () => respond({ ok: false, error: NeoI18n.t('err_host_unavailable') }));
  return true;
});

/** Session-scoped pause. scope 'all' = every site; scope 'site' = one hostname. on=false re-enables it. */
async function setPause({ scope, host, on }) {
  const p = (await prefs()).pause;
  if (scope === 'all') p.all = on === true;
  else if (scope === 'site') {
    const h = String(host || '').toLowerCase();
    if (!h || !/^[a-z0-9.\-:\[\]]+$/.test(h)) return { ok: false, error: NeoI18n.t('err_no_site') };
    p.hosts = p.hosts.filter(x => x !== h); if (on === true) p.hosts.push(h);
  } else return { ok: false };
  await chrome.storage.session.set({ [PAUSE_KEY]: p });
  await refreshTabs();
  return { ok: true, pause: p };
}
