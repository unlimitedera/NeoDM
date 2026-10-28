const { t } = NeoI18n;
NeoI18n.apply(document);
const $ = id => document.getElementById(id);
const show = text => $('status').textContent = text;
const PAUSE_KEY = 'neodm.pause', LOG_KEY = 'neodm.log';
let host = '';

async function renderPause() {
  const p = (await chrome.storage.session.get(PAUSE_KEY))[PAUSE_KEY] || {};
  const hosts = Array.isArray(p.hosts) ? p.hosts : [];
  const siteOff = !!host && hosts.includes(host), allOff = p.all === true;
  $('pauseAll').checked = allOff; $('pauseSite').checked = siteOff || allOff; $('pauseSite').disabled = allOff || !host;
  $('pauseSiteLabel').textContent = host ? t('popup_pause_site', host) : t('popup_pause_site_default');
  $('resume').hidden = !(siteOff || allOff);
  $('pauseState').textContent = allOff ? t('popup_paused_all') : siteOff ? t('popup_paused_site', host) : t('popup_active');
}
let activeTab = null, locateTimer = null, locateId = '', refreshTimer = null;
const ask = message => chrome.runtime.sendMessage(message).catch(() => null);
/** Show on page: ask the page (through the worker) to outline the element of a row. Renewed every second while the pointer stays; the page also stops after 3 s without renewal. */
function locate(id, on, scroll) { if (activeTab) ask({ action: 'popup-locate', tabId: activeTab.id, id, on, scroll }); }
function startLocate(id) { if (locateId === id) return; stopLocate(); locateId = id; locate(id, true, true); locateTimer = setInterval(() => locate(id, true, false), 1000); }
function stopLocate() { clearInterval(locateTimer); locateTimer = null; if (locateId) locate(locateId, false, false); locateId = ''; }
function mediaRow(tab, i) {
  const li = document.createElement('li'), seen = {};
  li.className = 'media'; li.dataset.id = i.id;
  const text = document.createElement('div'); text.className = 'text';
  const title = document.createElement('div'), meta = document.createElement('div'), url = document.createElement('div');
  title.className = 'title'; meta.className = 'meta'; url.className = 'host';
  const fill = () => { const d = NeoSniff.describeItem(i, t, seen); title.textContent = d.title; title.title = d.title; meta.textContent = d.meta; url.textContent = d.host; url.title = i.url; };
  fill();
  text.append(title, meta);
  if (i.playing) { const badge = document.createElement('span'); badge.className = 'badge'; badge.textContent = t('media_badge_playing'); text.append(badge); }
  text.append(url);
  const thumb = NeoSniff.thumbnail(document, i, { width: 96, height: 54, play: t('media_preview_play'), stop: t('media_preview_stop'), onMeta: m => { seen.dur = m.dur; seen.w = m.w; seen.h = m.h; fill(); } });
  const actions = document.createElement('div'); actions.className = 'actions';
  const block = i.track === 'stream' ? NeoSniff.streamBlock(i) : '';
  const b = document.createElement('button'); b.type = 'button'; b.textContent = i.track === 'stream' ? (block ? t('vb_cant_save') : t('popup_stream_btn')) : t('popup_download');
  if (i.track === 'stream') {
    const reason = ({ drm: t('vb_stream_drm'), live: t('vb_reason_live'), pieces: t('vb_stream_pieces') })[block] || t('ribbon_stream_ok');
    const why = document.createElement('div'); why.className = 'hint'; why.textContent = reason; text.append(why);
    if (block) { b.disabled = true; b.title = reason; b.setAttribute('aria-label', t('vb_cant_save') + ': ' + reason); }
  }
  b.onclick = async () => {
    b.disabled = true;
    const r = await chrome.runtime.sendMessage({ action: 'popup-download', tab: { id: tab.id, url: tab.url, title: tab.title }, id: i.id }).catch(() => null);
    show(r?.ok === true ? t('popup_accepted') : r?.error || t('popup_not_accepted')); b.disabled = false;
  };
  actions.append(b);
  if (i.dom || i.viaBlob || i.owner) {
    const where = document.createElement('button'); where.type = 'button'; where.className = 'link'; where.textContent = t('media_show_on_page');
    where.onclick = () => locate(i.id, true, true);
    actions.append(where);
  }
  li.addEventListener('mouseenter', () => startLocate(i.id)); li.addEventListener('focusin', () => startLocate(i.id));
  li.addEventListener('mouseleave', stopLocate); li.addEventListener('focusout', e => { if (!li.contains(e.relatedTarget)) stopLocate(); });
  li.append(thumb.el, text, actions);
  return li;
}
async function renderMedia(tab) {
  activeTab = tab; stopLocate(); NeoSniff.stopPreview();
  const list = $('mediaList'); list.textContent = '';
  let items = [], summary = null;
  try { const r = await chrome.runtime.sendMessage({ action: 'popup-media', tabId: tab.id }); items = r?.items || []; summary = r?.summary || null; } catch {}
  $('mediaNone').hidden = items.length > 0 || !!(summary && summary.videos > 0);
  $('videoSummary').hidden = !(summary && summary.videos > 0);
  if (summary && summary.videos > 0) $('videoSummary').textContent = t('popup_videos_summary', summary.videos, summary.streams || 0, summary.frames, summary.downloadable);
  const why = summary && summary.videos > 0 && summary.downloadable === 0 ? summary.reasons : [];
  $('whyNone').hidden = !why.length; $('whyList').textContent = '';
  for (const r of why) { const li = document.createElement('li'); li.textContent = t(NeoSniff.STREAM_REASON_KEY[r] || 'vb_reason_none'); $('whyList').append(li); }
  for (const i of items) {
    if (i.info) { const li = document.createElement('li'), what = document.createElement('span'); what.className = 'what note'; what.textContent = i.text; li.append(what); list.append(li); continue; }
    list.append(mediaRow(tab, i));
  }
}
/** The list follows the page (new files, playing state) unless the user is hovering a row or listening to a preview. */
function refreshSoon() {
  clearTimeout(refreshTimer);
  refreshTimer = setTimeout(() => { if (activeTab && !locateId && !NeoSniff.previewActive()) renderMedia(activeTab); else if (activeTab) refreshSoon(); }, 500);
}
try { chrome.storage.onChanged?.addListener((c, area) => { if (area === 'session' && activeTab && c['neodm.media.' + activeTab.id]) refreshSoon(); }); } catch {}
addEventListener('pagehide', () => { stopLocate(); NeoSniff.stopPreview(); });
async function renderRecent() {
  const list = $('recent'); list.textContent = '';
  let log = [];
  try { const stored = (await chrome.storage.session.get(LOG_KEY))[LOG_KEY]; if (Array.isArray(stored)) log = stored; } catch {}
  const rows = log.filter(e => e && typeof e === 'object').slice(-10).reverse();
  $('recentNone').hidden = rows.length > 0; $('recentClear').hidden = rows.length === 0;
  for (const e of rows) {
    const li = document.createElement('li'), when = new Date(Number(e.time) || 0);
    const stamp = Number.isNaN(when.getTime()) ? '' : when.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    const size = Number.isInteger(e.size) && e.size > 0 ? NeoSniff.fmtSizeShort(e.size) : ''; // what the browser reported for this download
    li.textContent = [stamp, String(e.host || t('popup_unknown_site')), size, e.decision === 'captured' ? t('popup_taken') : t('popup_left'), String(e.reason || '')].filter(Boolean).join(' · ');
    list.append(li);
  }
}
/** Sites where the on-video button is switched off (chrome.storage.local.videoButtonHiddenHosts). */
async function renderHidden() {
  let hosts = [];
  try { hosts = NeoSniff.cleanHiddenHosts((await chrome.storage.local.get('videoButtonHiddenHosts')).videoButtonHiddenHosts); } catch {}
  const list = $('hiddenSites'); list.textContent = ''; $('hiddenNone').hidden = hosts.length > 0;
  for (const h of hosts) {
    const li = document.createElement('li'), name = document.createElement('span'), remove = document.createElement('button');
    name.textContent = h; name.className = 'host'; remove.type = 'button'; remove.textContent = t('popup_site_remove'); remove.setAttribute('aria-label', t('popup_site_remove_for', h));
    remove.onclick = async () => { const left = NeoSniff.cleanHiddenHosts((await chrome.storage.local.get('videoButtonHiddenHosts')).videoButtonHiddenHosts).filter(x => x !== h); await chrome.storage.local.set({ videoButtonHiddenHosts: left }); await renderHidden(); };
    li.append(name, remove); list.append(li);
  }
}
/** Sites left to the browser (chrome.storage.local.browserHosts): list with a Remove button each; "Capture this site again" shows for the current site when it is listed. */
async function renderSites(sync = false) {
  if (sync) await ask({ action: 'sites-sync' }); // sites the app reported since the last capture
  let hosts = [];
  try { const stored = (await chrome.storage.local.get('browserHosts')).browserHosts; if (Array.isArray(stored)) hosts = stored.filter(h => typeof h === 'string'); } catch {}
  const list = $('sites'); list.textContent = '';
  $('sitesNone').hidden = hosts.length > 0;
  for (const h of hosts) {
    const li = document.createElement('li'), name = document.createElement('span'), remove = document.createElement('button');
    name.textContent = h; name.className = 'host'; remove.type = 'button'; remove.textContent = t('popup_site_remove'); remove.setAttribute('aria-label', t('popup_site_remove_for', h));
    remove.onclick = async () => { await removeSite(h); show(t('popup_site_captured_again', h)); };
    li.append(name, remove); list.append(li);
  }
  $('captureAgain').hidden = !(host && hosts.includes(NeoSniff.siteOf(host)));
}
async function removeSite(name) {
  await ask({ action: 'site-remove', host: name }); // the worker owns the list
  await renderSites();
}
$('captureAgain').onclick = async () => { await removeSite(host); show(t('popup_site_captured_again', NeoSniff.siteOf(host))); };
$('recentClear').onclick = async () => { try { await chrome.storage.session.set({ [LOG_KEY]: [] }); } catch {} await renderRecent(); show(t('popup_cleared')); };
async function setPause(scope, on) {
  await chrome.runtime.sendMessage({ action: 'pause-set', scope, host, on });
  await renderPause();
}

chrome.storage.local.get(['captureEnabled', 'cookiesEnabled', 'sessionCookies', 'ribbonEnabled', 'videoButtonEnabled', 'videoButtonCorner', 'mediaTypes', 'status']).then(async s => {
  $('capture').checked = s.captureEnabled !== false; // on by default
  $('cookies').checked = s.cookiesEnabled !== false; // on by default (1.5.9)
  $('sessionCookies').checked = s.sessionCookies !== false; // on by default
  $('ribbon').checked = s.ribbonEnabled !== false;
  $('videoButton').checked = s.videoButtonEnabled !== false; // on by default
  const types = NeoSniff.withDefaults(s.mediaTypes);
  for (const box of document.querySelectorAll('#types input[data-type]')) box.checked = !!types[box.dataset.type];
  show(s.status || t('popup_ready'));
  try { const [tab] = await chrome.tabs.query({ active: true, currentWindow: true }); host = NeoSniff.hostOf(tab?.url); if (Number.isInteger(tab?.id)) await renderMedia(tab); } catch {}
  await renderPause();
  await renderRecent();
  await renderSites(true);
  await renderHidden();
  $('corner').value = s.videoButtonCorner === 'left' || s.videoButtonCorner === 'right' ? s.videoButtonCorner : (NeoI18n.isRtl() ? 'right' : 'left');
  try { $('footerVersion').textContent = t('popup_footer_version', chrome.runtime.getManifest().version); } catch { /* version unknown */ }
});
$('corner').onchange = async () => { await chrome.storage.local.set({ videoButtonCorner: $('corner').value === 'right' ? 'right' : 'left' }); };

/** The app version in a ping reply (added by newer hosts) when it differs from this extension's version; '' otherwise. */
function versionNote(reply) {
  const app = typeof reply?.appVersion === 'string' && /^\d{1,4}(\.\d{1,4}){1,3}$/.test(reply.appVersion) ? reply.appVersion : '';
  const own = chrome.runtime.getManifest().version;
  return app && app !== own ? t('popup_version_note', app, own) : '';
}
$('test').onclick = async () => {
  try {
    const r = await chrome.runtime.sendMessage({ action: 'test' });
    show(r?.ok ? t('popup_host_connected') : r?.error || t('popup_host_silent'));
    const note = r?.ok ? versionNote(r) : '';
    $('versionNote').textContent = note; $('versionNote').hidden = !note;
  } catch { show(t('popup_host_unreachable')); }
};

$('videoButton').onchange = async () => {
  await chrome.storage.local.set({ videoButtonEnabled: $('videoButton').checked });
  show($('videoButton').checked ? t('popup_video_button_on') : t('popup_video_button_off'));
};
$('ribbon').onchange = async () => {
  await chrome.storage.local.set({ ribbonEnabled: $('ribbon').checked });
  show($('ribbon').checked ? t('popup_ribbon_on') : t('popup_ribbon_off'));
};

for (const box of document.querySelectorAll('#types input[data-type]')) {
  box.onchange = async () => {
    const stored = (await chrome.storage.local.get('mediaTypes')).mediaTypes;
    const types = NeoSniff.withDefaults(stored); types[box.dataset.type] = box.checked;
    await chrome.storage.local.set({ mediaTypes: types });
    show(t(box.checked ? 'popup_type_on' : 'popup_type_off', t('popup_type_' + box.dataset.type)));
  };
}

$('pauseSite').onchange = () => setPause('site', $('pauseSite').checked);
$('pauseAll').onchange = () => setPause('all', $('pauseAll').checked);
$('resume').onclick = async () => { await setPause('all', false); await setPause('site', false); show(t('popup_reenabled')); };

for (const [id, key] of [['capture', 'captureEnabled'], ['cookies', 'cookiesEnabled'], ['sessionCookies', 'sessionCookies']]) {
  $(id).onchange = async () => {
    const enabled = $(id).checked; // the cookies permission is required since 1.5.9: the switch only starts or stops using it, nothing is requested or removed
    await chrome.storage.local.set({ [key]: enabled });
    show(enabled ? t('popup_enabled') : t('popup_disabled'));
  };
}
