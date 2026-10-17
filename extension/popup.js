const $ = id => document.getElementById(id);
const show = text => $('status').textContent = text;
const PAUSE_KEY = 'neodm.pause', LOG_KEY = 'neodm.log';
let host = '';

async function renderPause() {
  const p = (await chrome.storage.session.get(PAUSE_KEY))[PAUSE_KEY] || {};
  const hosts = Array.isArray(p.hosts) ? p.hosts : [];
  const siteOff = !!host && hosts.includes(host), allOff = p.all === true;
  $('pauseAll').checked = allOff; $('pauseSite').checked = siteOff || allOff; $('pauseSite').disabled = allOff || !host;
  $('pauseSiteLabel').textContent = host ? `Pause NeoDM on ${host} for this session` : 'Pause NeoDM on this site for this session';
  $('resume').hidden = !(siteOff || allOff);
  $('pauseState').textContent = allOff ? 'NeoDM is paused on all sites until the browser closes.' : siteOff ? `NeoDM is paused on ${host} until the browser closes.` : 'NeoDM is active.';
}
const fmtSize = n => !n ? '' : n >= 1073741824 ? (n / 1073741824).toFixed(2) + ' GB' : n >= 1048576 ? (n / 1048576).toFixed(1) + ' MB' : Math.max(1, Math.round(n / 1024)) + ' KB';
const describe = i => [i.kind === 'gif' ? 'GIF' : i.kind === 'image' ? 'Image' : i.track === 'audio-only' ? 'Audio only' : i.track === 'video-only' ? 'Video only (no sound)' : i.kind === 'audio' ? 'Audio' : 'Video', i.quality, fmtSize(i.size)].filter(Boolean).join(' · ');
async function renderMedia(tab) {
  const list = $('mediaList'); list.textContent = '';
  let items = [];
  try { items = (await chrome.runtime.sendMessage({ action: 'popup-media', tabId: tab.id }))?.items || []; } catch {}
  $('mediaNone').hidden = items.length > 0;
  for (const i of items) {
    const li = document.createElement('li'), what = document.createElement('span'); what.className = 'what';
    if (i.info) { what.textContent = i.text; what.classList.add('note'); li.append(what); list.append(li); continue; }
    what.textContent = describe(i); what.title = i.url;
    const b = document.createElement('button'); b.type = 'button'; b.textContent = 'Download';
    b.onclick = async () => {
      b.disabled = true;
      const r = await chrome.runtime.sendMessage({ action: 'popup-download', tab: { id: tab.id, url: tab.url, title: tab.title }, id: i.id }).catch(() => null);
      show(r?.ok === true ? 'Accepted by NeoDM.' : r?.error || 'NeoDM did not accept this media.'); b.disabled = false;
    };
    li.append(what, b); list.append(li);
  }
}
async function renderRecent() {
  const list = $('recent'); list.textContent = '';
  let log = [];
  try { const stored = (await chrome.storage.session.get(LOG_KEY))[LOG_KEY]; if (Array.isArray(stored)) log = stored; } catch {}
  const rows = log.filter(e => e && typeof e === 'object').slice(-10).reverse();
  $('recentNone').hidden = rows.length > 0; $('recentClear').hidden = rows.length === 0;
  for (const e of rows) {
    const li = document.createElement('li'), when = new Date(Number(e.time) || 0);
    const stamp = Number.isNaN(when.getTime()) ? '' : when.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    li.textContent = [stamp, String(e.host || 'unknown site'), e.decision === 'captured' ? 'taken by NeoDM' : 'left to the browser', String(e.reason || '')].filter(Boolean).join(' · ');
    list.append(li);
  }
}
$('recentClear').onclick = async () => { try { await chrome.storage.session.set({ [LOG_KEY]: [] }); } catch {} await renderRecent(); show('Recent downloads cleared.'); };
async function setPause(scope, on) {
  await chrome.runtime.sendMessage({ action: 'pause-set', scope, host, on });
  await renderPause();
}

chrome.storage.local.get(['captureEnabled', 'cookiesEnabled', 'ribbonEnabled', 'mediaTypes', 'status']).then(async s => {
  $('capture').checked = s.captureEnabled !== false; // on by default
  $('cookies').checked = !!s.cookiesEnabled;
  $('ribbon').checked = s.ribbonEnabled !== false;
  const types = NeoSniff.withDefaults(s.mediaTypes);
  for (const box of document.querySelectorAll('#types input[data-type]')) box.checked = !!types[box.dataset.type];
  show(s.status || 'Ready');
  try { const [tab] = await chrome.tabs.query({ active: true, currentWindow: true }); host = NeoSniff.hostOf(tab?.url); if (Number.isInteger(tab?.id)) await renderMedia(tab); } catch {}
  await renderPause();
  await renderRecent();
});

/** The app version in a ping reply (added by newer hosts) when it differs from this extension's version; '' otherwise. */
function versionNote(reply) {
  const app = typeof reply?.appVersion === 'string' && /^\d{1,4}(\.\d{1,4}){1,3}$/.test(reply.appVersion) ? reply.appVersion : '';
  const own = chrome.runtime.getManifest().version;
  return app && app !== own ? `NeoDM app ${app}, extension ${own}. Open Help > Browser extension install guide in NeoDM.` : '';
}
$('test').onclick = async () => {
  try {
    const r = await chrome.runtime.sendMessage({ action: 'test' });
    show(r?.ok ? 'Host connected.' : r?.error || 'Host did not respond.');
    const note = r?.ok ? versionNote(r) : '';
    $('versionNote').textContent = note; $('versionNote').hidden = !note;
  } catch { show('Could not connect to host.'); }
};

$('ribbon').onchange = async () => {
  await chrome.storage.local.set({ ribbonEnabled: $('ribbon').checked });
  show($('ribbon').checked ? 'Media ribbon on.' : 'Media ribbon off.');
};

for (const box of document.querySelectorAll('#types input[data-type]')) {
  box.onchange = async () => {
    const stored = (await chrome.storage.local.get('mediaTypes')).mediaTypes;
    const types = NeoSniff.withDefaults(stored); types[box.dataset.type] = box.checked;
    await chrome.storage.local.set({ mediaTypes: types });
    show(`${box.parentElement.textContent.trim()} ${box.checked ? 'on' : 'off'}.`);
  };
}

$('pauseSite').onchange = () => setPause('site', $('pauseSite').checked);
$('pauseAll').onchange = () => setPause('all', $('pauseAll').checked);
$('resume').onclick = async () => { await setPause('all', false); await setPause('site', false); show('NeoDM re-enabled.'); };

for (const [id, key] of [['capture', 'captureEnabled'], ['cookies', 'cookiesEnabled']]) {
  $(id).onchange = async () => {
    let enabled = $(id).checked;
    if (enabled && id === 'cookies') {
      try { enabled = await chrome.permissions.request({ permissions: ['cookies'] }); } catch { enabled = false; }
    }
    $(id).checked = enabled;
    await chrome.storage.local.set({ [key]: enabled });
    if (!enabled && id === 'cookies') await chrome.permissions.remove({ permissions: ['cookies'] });
    show(enabled ? 'Enabled.' : 'Disabled.');
  };
}
