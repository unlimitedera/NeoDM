importScripts('i18n.js', 'bridge.js', 'sniff.js');
const { http, ack, transfer, sizeOf } = NDMBridge;
const { refererFor, ingest, upsert, resetTracking, noteEvents, isPageLink, isYouTubeHost, listItems, withInfo, isMediaResponse, domReport, youtubeNote, pickTarget, suggestedName, mimeFor, typeOfFile, typeOfItem, withDefaults, hostOf, isPaused, siteOf } = NeoSniff;
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
  request.referer = refererFor(request.referer, request.url); // every path (popup, ribbon, bar, context menu, captured download) ends here: the full page address goes to its own origin only
  if (settings.cookiesEnabled && await chrome.permissions.contains({ permissions: ['cookies'], origins: [new URL(request.url).origin + '/*'] })) {
    // Never log cookies. Preserve browser cookie-store selection when available.
    const stores = await chrome.cookies.getAllCookieStores();
    const store = stores.find(s => s.tabIds.includes(tab?.id));
    const cookies = await chrome.cookies.getAll({ url: request.url, ...(store ? { storeId: store.id } : {}) });
    request.cookie = cookies.map(c => `${c.name}=${c.value}`).join('; ');
  }
  const v = extVersion();
  const reply = await native({ ...request, ...(v ? { extVersion: v } : {}), keepAlive: true }); // keepAlive: older hosts ignore it and answer once, as before
  if (reply?.ok === false && typeof reply.error === 'string') { const e = new Error(reply.error); e.cancelled = reply.cancelled === true; e.handBack = reply.handBack === true; throw e; } // cancelled: the user pressed Cancel in the app dialog; handBack: "Download in browser instead"
  if (!ack(reply)) throw new Error(NeoI18n.t('err_not_durable'));
  return reply;
}

/* ---- per-tab media list: survives MV3 worker restarts through chrome.storage.session ---- */
const tabCache = new Map(), tabQueues = new Map();
const key = tabId => 'neodm.media.' + tabId;
const pageKey = url => { try { const u = new URL(url); u.hash = ''; return u.href; } catch { return ''; } };
const tabLoads = new Map();
/** One in-flight storage read per tab: two callers must never end up with two different state objects. */
function loadTab(tabId) {
  if (tabCache.has(tabId)) return Promise.resolve(tabCache.get(tabId));
  if (!tabLoads.has(tabId)) tabLoads.set(tabId, chrome.storage.session.get(key(tabId)).then(got => {
    const stored = got[key(tabId)], state = stored && Array.isArray(stored.items) ? stored : { page: '', items: [] };
    if (!tabCache.has(tabId)) tabCache.set(tabId, state);
    tabLoads.delete(tabId); return tabCache.get(tabId);
  }, e => { tabLoads.delete(tabId); throw e; }));
  return tabLoads.get(tabId);
}
/** Serialize all mutations per tab. fn(state) mutates state and returns true when the list changed. */
function mutateTab(tabId, fn) {
  const next = (tabQueues.get(tabId) || Promise.resolve()).then(async () => {
    const state = await loadTab(tabId), changed = fn(state); // 'mem': only in-memory trackers changed (element events), nothing to store or announce
    if (changed && changed !== 'mem') { await chrome.storage.session.set({ [key(tabId)]: state }); if (changed !== 'quiet') { await badge(tabId, state); await notifyTab(tabId, state); syncMenus(); } } // 'quiet': trackers changed, the list did not
    return state;
  }).catch(() => undefined);
  tabQueues.set(tabId, next); return next;
}
async function badge(tabId, state) {
  try { await chrome.action.setBadgeBackgroundColor({ tabId, color: '#2da57d' }); await chrome.action.setBadgeText({ tabId, text: listItems(state).length ? String(listItems(state).length) : '' }); } catch {}
}
/** Items the ribbon may show: honours the type filter and the session pause for this page. */
async function visibleItems(state) {
  const p = await prefs();
  if (isPaused(p.pause, state.page, state.host)) return [];
  const list = listItems({ ...state, items: state.items.filter(i => p.types[typeOfItem(i)]) }), note = youtubeNote(state, list);
  return note ? [...list, note] : list; // on YouTube the note explains why no file is offered
}
const isMediaRow = i => !i.info && (i.kind === 'video' || i.kind === 'audio');
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
  const state = await mutateTab(details.tabId, state => {
    const changed = ingest(state, details, types);
    if (origin && origin !== 'null' && state.host !== origin) { state.host = origin; return true; }
    return changed;
  });
  // The element that fetched the file may report its events up to a second later: decide the owner when that window has closed.
  if (state && undecided(state)) settleLater(details.tabId);
}, { urls: ['http://*/*', 'https://*/*'], types: ['media', 'xmlhttprequest', 'other', 'object', 'image'] }, ['responseHeaders']);
// A real navigation is a tab update with status 'loading' that carries the new URL (or follows a 'loading' update without one). A pushState change of the URL
// (infinite-scroll feeds change it while you scroll) keeps the list; only YouTube-style players, which swap the video through pushState, still start a fresh list.
const settling = new Set(), SETTLE_MS = NeoSniff.OWN_AFTER + NeoSniff.SETTLE_SLACK + 60;
const undecided = state => state.evt && Object.keys(state.evt).length > 0 && state.items.some(i => !i.owner && !i.info && (i.kind === 'video' || i.kind === 'audio') && Date.now() - (i.first || 0) < SETTLE_MS);
function settleLater(tabId) {
  if (settling.has(tabId)) return; settling.add(tabId);
  setTimeout(async () => {
    settling.delete(tabId);
    const state = await mutateTab(tabId, state => NeoSniff.assignOwners(state, Date.now()));
    if (state && undecided(state)) settleLater(tabId); // a later file is still waiting for its window to close
  }, SETTLE_MS);
}
chrome.tabs.onUpdated.addListener((tabId, change) => {
  if (change.url || change.status === 'complete') syncMenus();
  if (!change.url) return;
  const page = pageKey(change.url); // also fires for SPA (pushState) navigation, which Chrome reports with status 'loading' just like a real page load
  mutateTab(tabId, state => {
    if (state.page === page) return false;
    const first = !state.page, was = state.page; state.page = page;
    const origin = u => { try { return new URL(u).origin; } catch { return ''; } };
    // YouTube swaps videos through pushState; a change of origin is never a feed scroll. A feed that only changes its path keeps its items.
    if (!first && (isYouTubeHost(page) || isYouTubeHost(was) || (origin(was) && origin(page) && origin(was) !== origin(page)))) resetTracking(state); // first sighting after a worker restart must not wipe items already sniffed
    return true;
  });
});
// A real navigation is a top-level document response (a pushState URL change makes none). A download or an attachment is not a page and keeps the list.
chrome.webRequest.onResponseStarted.addListener(details => {
  if (details.type !== 'main_frame' || !(details.tabId >= 0) || (details.statusCode >= 300 && details.statusCode < 400 && details.statusCode !== 304)) return;
  const h = name => { const x = (details.responseHeaders || []).find(r => r.name && r.name.toLowerCase() === name); return x ? String(x.value || '') : ''; };
  if (/attachment/i.test(h('content-disposition'))) return;
  const page = pageKey(details.url);
  mutateTab(details.tabId, state => { resetTracking(state); state.page = page; return true; });
}, { urls: ['http://*/*', 'https://*/*'], types: ['main_frame'] }, ['responseHeaders']);
chrome.tabs.onActivated?.addListener(() => syncMenus());
chrome.tabs.onRemoved.addListener(tabId => { tabCache.delete(tabId); tabQueues.delete(tabId); chrome.storage.session.remove(key(tabId)); });

/* ---- context menus: 'link' and 'media' (video/audio/image) always, 'page-video' (page/frame) only while the active tab has a video or audio item ---- */
const PAGE_MENU = 'page-video';
let menuBuild = Promise.resolve(), menuSync = Promise.resolve(), menuShown = false;
function buildMenus() {
  menuBuild = menuBuild.then(async () => {
    try { await chrome.contextMenus.removeAll(); } catch {}
    const make = o => new Promise(done => { try { chrome.contextMenus.create(o, () => { void chrome.runtime.lastError; done(); }); } catch { done(); } }); // a duplicate id only sets lastError
    const pending = [
      make({ id: 'link', title: NeoI18n.t('menu_link'), contexts: ['link'] }),
      make({ id: 'media', title: NeoI18n.t('menu_media'), contexts: ['video', 'audio', 'image'] }),
      make({ id: PAGE_MENU, title: NeoI18n.t('menu_video'), contexts: ['page', 'frame'], visible: false }),
    ];
    await Promise.all(pending);
    menuShown = false;
    await syncMenus();
  }).catch(() => {});
  return menuBuild;
}
/** Shows the page entry only while the active tab has at least one listed video or audio item. */
function syncMenus() {
  menuSync = menuSync.then(async () => {
    let show = false;
    try {
      const [tab] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
      if (tab && Number.isInteger(tab.id) && http(tab.url)) show = (await visibleItems(await loadTab(tab.id))).some(isMediaRow);
    } catch {}
    if (show === menuShown) return;
    menuShown = show;
    try { await chrome.contextMenus.update(PAGE_MENU, { visible: show }); } catch {}
  }).catch(() => {});
  return menuSync;
}
chrome.runtime.onInstalled.addListener(async () => { await ensureDefaults(); await buildMenus(); });
chrome.runtime.onStartup?.addListener(() => { buildMenus(); });
buildMenus(); // also whenever the service worker starts (menus survive restarts, but this heals a missing or stale set)
const askTab = (tabId, message) => new Promise(resolve => { try { chrome.tabs.sendMessage(tabId, message, reply => { void chrome.runtime.lastError; resolve(reply || null); }); } catch { resolve(null); } });
/** Right click on a video / on the page: finds the sniffed item the user means and downloads it. */
async function downloadFromMenu(info, tab, known) {
  const tabId = tab?.id, direct = http(info.srcUrl) ? info.srcUrl : '';
  let pick, list = [], target = null;
  if (Number.isInteger(tabId)) {
    if (info.mediaType !== 'image') target = known || await askTab(tabId, { type: 'neodm-context-target' });
    list = (await visibleItems(await loadTab(tabId))).filter(isMediaRow);
  }
  pick = info.mediaType === 'image' ? (direct ? { url: direct } : { none: true }) : pickTarget(list, target, info.srcUrl);
  if (pick.ambiguous) {
    if (Number.isInteger(tabId)) await askTab(tabId, { type: 'neodm-show-ribbon' });
    await status(NeoI18n.t('status_choose_ribbon')); return;
  }
  if (pick.none) { if (info.srcUrl) throw new Error(NeoI18n.t('err_not_direct')); throw new Error(NeoI18n.t('err_no_video')); }
  if (pick.item) {
    const r = await downloadItem({ id: tabId, url: String(tab?.url || ''), title: String(tab?.title || '') }, pick.item.id);
    if (r && r.ok === false) throw new Error(r.error || NeoI18n.t('err_no_ack_url'));
  } else {
    if (pageKey(pick.url) === pageKey(info.pageUrl || tab?.url)) throw new Error(NeoI18n.t('err_link_page')); // the page itself is never a media file
    // A direct address (no sniffed item behind it): the request still carries a real file name (title + extension), the type and the page as referer.
    const like = { url: pick.url, kind: target?.tag === 'audio' || info.mediaType === 'audio' ? 'audio' : 'video', track: 'file', source: 'dom' };
    const title = String(target?.title || tab?.title || ''), mime = mimeFor(like);
    await send({ url: pick.url, fileName: suggestedName(like, title), referer: http(info.pageUrl) ? info.pageUrl : http(tab?.url) ? tab.url : '', method: 'GET', ...(mime ? { mime } : {}) }, tab);
  }
  await status(NeoI18n.t('status_accepted'));
}
async function onMenuClick(info, tab) {
  try {
    if (info.menuItemId !== 'link') return await downloadFromMenu(info, tab);
    const url = info.linkUrl;
    // A video inside a link (feeds wrap videos in post links) also gets the link entry: if a video is under the pointer, download that video, not the link.
    const target = Number.isInteger(tab?.id) ? await askTab(tab.id, { type: 'neodm-context-target' }) : null;
    if (target?.found) return await downloadFromMenu({ ...info, menuItemId: 'media', mediaType: 'video', srcUrl: '' }, tab, target);
    if (!http(url)) throw new Error(NeoI18n.t('err_not_direct'));
    if (isPageLink(url, info.pageUrl || tab?.url)) throw new Error(NeoI18n.t('err_link_page'));
    await send({ url, fileName: '', referer: http(info.pageUrl) ? info.pageUrl : '', method: 'GET' }, tab);
    await status(NeoI18n.t('status_accepted'));
  } catch (e) { await status(e.message); }
}
chrome.contextMenus.onClicked.addListener(onMenuClick);
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
// The browser's own decision (Content-Disposition, redirects, sniffed type) arrives here; it is remembered for the capture below.
// With "Ask where to save each file" the browser opens its Save as window right after this step, and cancelling the download afterwards does not close that window
// (observed in Chrome 155: the window stayed next to NeoDM's dialog). So the answer to the browser is held back (the listener says it will answer later) until the capture
// decision is made: a captured download is cancelled while the browser still waits, which means the Save as window never opens; every other outcome answers at once
// with no change (suggest() without a file name), and a safety timer answers after 8 s whatever happens.
const suggested = new Map(), waiting = new Map(), DETERMINE_HOLD_MS = 8000;
function release(id) {
  const w = waiting.get(id); if (!w) return;
  waiting.delete(id); clearTimeout(w.timer);
  try { w.suggest(); } catch {}
}
chrome.downloads.onDeterminingFilename?.addListener((item, suggest) => {
  suggested.set(item.id, { filename: item.filename, mime: item.mime });
  if (suggested.size > 100) suggested.delete(suggested.keys().next().value);
  if (typeof suggest !== 'function' || finishedIds.has(item.id)) return;
  waiting.set(item.id, { suggest, timer: setTimeout(() => release(item.id), DETERMINE_HOLD_MS) });
  return true; // the answer comes later (release)
});
const finishedIds = new Set(); // downloads whose capture decision is already made: a late filename question is answered at once
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
const REASONS = new Set(['browser-site', 'handed-back', 'duplicate', 'sent', 'cancelled', 'form-post', 'redirect-keeps-post', 'post-ambiguous', 'unseen', 'blob', 'protocol', 'private', 'type', 'paused', 'off', 'unreachable']);
const ACTIONABLE = new Set(['off', 'paused', 'type']);
let logQueue = Promise.resolve();
/** Keeps the last LOG_MAX capture decisions (time, host, decision, code, reason) in chrome.storage.session. */
function note(item, decision, code) {
  const size = sizeOf(item); // what the browser reported, so the popup line can say which file this was
  const entry = { time: Date.now(), host: hostOf(item.url) || '', decision, code, ...(size ? { size } : {}), reason: REASONS.has(code) ? NeoI18n.t('reason_' + code.replace(/-/g, '_')) : code };
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
/* ---- sites left to the browser: registrable host names (max 100) in chrome.storage.local.browserHosts ---- */
const BROWSER_HOSTS_MAX = 100;
async function browserHosts() {
  const stored = (await chrome.storage.local.get('browserHosts')).browserHosts;
  return Array.isArray(stored) ? stored.filter(h => typeof h === 'string' && h.length > 0 && h.length <= 253).slice(-BROWSER_HOSTS_MAX) : [];
}
/** Adds sites (any host names; stored as their registrable parent). Returns the list. Serialized so two additions never overwrite each other. */
let hostsQueue = Promise.resolve();
function addBrowserHosts(...names) {
  const run = hostsQueue.then(async () => {
    const list = await browserHosts();
    for (const name of names) { const site = siteOf(name); if (site && !list.includes(site)) list.push(site); }
    const kept = list.slice(-BROWSER_HOSTS_MAX);
    await chrome.storage.local.set({ browserHosts: kept });
    return kept;
  });
  hostsQueue = run.catch(() => {});
  return run;
}
function removeBrowserHost(name) {
  const run = hostsQueue.then(async () => {
    const site = siteOf(name), kept = (await browserHosts()).filter(h => h !== site);
    await chrome.storage.local.set({ browserHosts: kept });
    return kept;
  });
  hostsQueue = run.catch(() => {});
  return run;
}
/** Hosts the app found it cannot fetch (a captured download came back as a web page): asked from the native host, which never starts the app. Failures are ignored. */
async function syncBrowserHosts() {
  try {
    const reply = await native({ action: 'browser-sites' });
    if (reply?.ok === true && Array.isArray(reply.sites) && reply.sites.length) await addBrowserHosts(...reply.sites.filter(h => typeof h === 'string').slice(0, BROWSER_HOSTS_MAX));
  } catch {}
}
const isBrowserSite = async item => {
  const list = await browserHosts();
  if (!list.length) return false;
  return [item.finalUrl, item.url].some(u => list.includes(siteOf(hostOf(u))));
};
/* ---- the same download triggered twice (hidden frame plus navigation, two clicks): the second one is dropped silently ---- */
const DUPLICATE_MS = 15000, RECENT_AFTER_DONE_MS = 5000;
const handling = new Map(); // address -> { done: 0 while the first capture is still being decided, else the time it finished; started }
const addressesOf = item => [...new Set([item.url, item.finalUrl].filter(Boolean))];
function isDuplicate(item) {
  const now = Date.now();
  for (const [u, h] of handling) if (h.done && now - h.done > RECENT_AFTER_DONE_MS || now - h.started > 300000) handling.delete(u);
  return addressesOf(item).some(u => handling.has(u));
}
/* The browser download that this extension itself starts again (after an error or a hand back) is not a new capture. Chrome does not always mark it with byExtensionId
   at creation time, so the address is remembered for a short time and the first matching onCreated is ignored. */
const restarted = new Map();
const downloadsApi = {
  cancel: (...a) => chrome.downloads.cancel(...a), search: (...a) => chrome.downloads.search(...a), erase: (...a) => chrome.downloads.erase(...a), removeFile: (...a) => chrome.downloads.removeFile(...a),
  download: options => { restarted.set(options.url, Date.now()); return chrome.downloads.download(options); },
};
function isOwnRestart(item) {
  const now = Date.now();
  for (const [u, t] of restarted) if (now - t > 20000) restarted.delete(u);
  const hit = addressesOf(item).find(u => restarted.has(u));
  if (hit) restarted.delete(hit);
  return !!hit;
}
function markHandling(item) { const rec = { done: 0, started: Date.now() }; for (const u of addressesOf(item)) handling.set(u, rec); return rec; }
chrome.downloads.onCreated.addListener(async item => {
  try { await onDownloadCreated(item); }
  finally { finishedIds.add(item.id); if (finishedIds.size > 200) finishedIds.delete(finishedIds.values().next().value); release(item.id); }
});
async function onDownloadCreated(item) {
  if (item.byExtensionId || item.paused || item.state !== 'in_progress') return; // our own restart, another extension, or not running: not a capture decision
  const p = await prefs(), final = item.finalUrl || item.url, skip = code => note(item, 'skipped', code);
  if (item.incognito) return skip('private');
  if (/^(blob|data|filesystem):/i.test(item.url) || /^(blob|data|filesystem):/i.test(final)) return skip('blob');
  if (!http(item.url) || !http(final)) return skip('protocol');
  if (!p.capture) return skip('off');
  if (isPaused(p.pause, item.referrer, item.finalUrl, item.url)) return skip('paused');
  if (!p.types[typeOfFile(item.filename || final, item.mime)]) return skip('type'); // type not ticked: the browser keeps it
  // The very same download again while the first is being handled (or just finished): drop it without a second dialog and without a browser restart.
  if (isOwnRestart(item)) return; // our own restart in the browser: the browser keeps it
  if (isDuplicate(item)) { suggested.delete(item.id); await NDMBridge.discard(item, downloadsApi); return note(item, 'skipped', 'duplicate'); }
  if (await isBrowserSite(item)) return skip('browser-site');
  const v = verdict(item);
  if (!v.capture) return skip(v.code);
  const record = markHandling(item); // before any await: a second trigger that arrives meanwhile finds it
  try {
    // The app may know sites it cannot fetch (a download of that site came back as a web page earlier): one short question to the native host.
    await syncBrowserHosts();
    if (await isBrowserSite(item)) { for (const u of addressesOf(item)) handling.delete(u); return skip('browser-site'); }
    // transfer(): cancel + erase the browser download immediately, THEN NeoDM shows its dialog (up to 5 min).
    // Only app-unreachable/error restarts the browser download; the user's Cancel in NeoDM discards it.
    const hint = suggested.get(item.id); suggested.delete(item.id);
    const result = await transfer(item, downloadsApi, req => send(req), true, hint, { referer: v.referer, noProbe: v.viaPost, afterDiscard: () => release(item.id) });
    if (result.skipped) return skip('unseen');
    if (result.handedBack) await addBrowserHosts(hostOf(final) || '');
    if (result.ok) await note(item, 'captured', 'sent'); else if (result.cancelled) await note(item, 'captured', 'cancelled'); else if (result.handedBack) await note(item, 'skipped', 'handed-back'); else await note(item, 'skipped', 'unreachable');
    await status(result.ok ? NeoI18n.t('status_taken') : result.error);
  } finally { record.done = Date.now(); }
}

async function downloadItem(tab, id) {
  const state = await loadTab(tab.id), item = state.items.find(i => i.id === id);
  if (!item) return { ok: false, error: NeoI18n.t('err_media_gone') };
  const mime = mimeFor(item), request = url => ({ url, fileName: suggestedName(item, item.title || tab.title), referer: http(tab.url) ? tab.url : '', method: 'GET', ...(mime ? { mime } : {}) });
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
  if (page && message.action === 'media-dom') { mutateTab(sender.tab.id, state => domReport(state, message.sources)).then(() => respond({ ok: true })); return true; }
  if (page && message.action === 'media-event') { mutateTab(sender.tab.id, state => noteEvents(state, message.events)).then(state => { if (state && undecided(state)) settleLater(sender.tab.id); respond({ ok: true }); }); return true; }
  if (page && message.action === 'media-download') { downloadItem(sender.tab, String(message.id)).then(respond); return true; }
  if (popup && message.action === 'popup-media' && Number.isInteger(message.tabId)) { loadTab(message.tabId).then(popupItems).then(items => respond({ items }), () => respond({ items: [] })); return true; }
  if (popup && message.action === 'popup-download' && Number.isInteger(message.tab?.id)) { downloadItem({ id: message.tab.id, url: String(message.tab.url || ''), title: String(message.tab.title || '') }, String(message.id)).then(respond); return true; }
  if (popup && message.action === 'popup-locate' && Number.isInteger(message.tabId)) { locate(message.tabId, String(message.id), message.on === true, message.scroll === true).then(respond, () => respond({ ok: false })); return true; }
  if (popup && message.action === 'pause-set') {
    setPause(message).then(respond, () => respond({ ok: false })); return true;
  }
  if (popup && message.action === 'sites-sync') { syncBrowserHosts().then(() => respond({ ok: true })); return true; }
  // Removal first takes what the app has waiting, so a site the app reported a moment ago is not added again right after the user removed it.
  if (popup && message.action === 'site-remove' && typeof message.host === 'string') { syncBrowserHosts().then(() => removeBrowserHost(message.host)).then(hosts => respond({ ok: true, hosts }), () => respond({ ok: false })); return true; }
  if (!popup || message.action !== 'test') return false;
  native({ action: 'ping', extVersion: extVersion() }).then(respond, () => respond({ ok: false, error: NeoI18n.t('err_host_unavailable') }));
  return true;
});

/** Asks the page to outline (on) or stop outlining (off) the element behind a listed item. */
async function locate(tabId, id, on, scroll) {
  const item = (await visibleItems(await loadTab(tabId))).find(i => i.id === id && !i.info);
  chrome.tabs.sendMessage(tabId, { type: 'neodm-highlight', id, on, scroll, blob: !!item?.viaBlob, ...(item?.owner ? { owner: item.owner } : {}) }, () => void chrome.runtime.lastError);
  return { ok: true };
}
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
  await refreshTabs(); syncMenus();
  return { ok: true, pause: p };
}
