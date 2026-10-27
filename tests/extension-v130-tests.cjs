'use strict';
/* 1.3.0: capture on by default, per-type filters (incl. GIF), session pause per site / all sites, popup UI. */
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs'), path = require('node:path');
const { JSDOM } = require('jsdom');
const S = require('../extension/sniff.js');
const { makeBackground, response, tick, source, json, withI18n } = require('./extension-harness.cjs');
const MB = 1024 * 1024;
const dl = (url, extra = {}) => ({ id: 11, url, finalUrl: url, state: 'in_progress', referrer: 'https://site.example/page', ...extra });

function wire(b, log) {
  const d = b.chrome.downloads;
  d.cancel = async () => log.push('cancel'); d.erase = async () => log.push('erase'); d.removeFile = async () => log.push('removeFile');
  d.search = async () => [{ state: 'interrupted' }]; d.download = async o => log.push('restart:' + o.url);
}
async function capture(b, item) {
  const log = [], before = b.nativeRequests.filter(r => r.url).length; wire(b, log);
  b.hooks.request({ requestId: 'r' + Math.random(), url: item.url, method: 'GET' });
  await b.hooks.download(item);
  return { log, sent: b.nativeRequests.filter(r => r.url).slice(before) };
}

test('defaults: capture is on for a fresh install and for existing users who never chose; an explicit off is kept', async () => {
  const fresh = makeBackground(); await tick();
  assert.equal(fresh.local.captureEnabled, true); assert.equal(fresh.local.schema, 4);
  assert.deepEqual(json(fresh.local.mediaTypes), S.DEFAULT_TYPES);
  const old = makeBackground({ settings: { ribbonEnabled: true } }); await tick(); assert.equal(old.local.captureEnabled, true, 'migration for users who never set it');
  const off = makeBackground({ settings: { captureEnabled: false } }); await tick(); assert.equal(off.local.captureEnabled, false, 'explicit choice survives the migration');
  const done = makeBackground({ settings: { schema: 3, captureEnabled: false } }); await tick(); assert.equal(done.local.captureEnabled, false);
  const unset = makeBackground({ settings: {} }); // no schema yet, but even before migration capture reads as on
  const { log, sent } = await capture(unset, dl('https://files.example/a.zip')); assert.equal(sent.length, 1); assert.deepEqual(log, ['cancel', 'erase']);
});
test('type defaults: Video, Audio, Archives, Programs, Documents, Other on; Images and GIF off', () => {
  assert.deepEqual(json(S.DEFAULT_TYPES), { video: true, audio: true, image: false, gif: false, archive: true, program: true, document: true, other: true });
  assert.deepEqual(S.TYPE_IDS, ['video', 'audio', 'image', 'gif', 'archive', 'program', 'document', 'other']);
});
test('typeOfFile: extensions first, then MIME; GIF is separate from images; unknown = other', () => {
  const t = S.typeOfFile;
  assert.equal(t('C:\\Users\\me\\Downloads\\movie.MKV'), 'video'); assert.equal(t('song.flac'), 'audio'); assert.equal(t('a.png'), 'image'); assert.equal(t('anim.gif'), 'gif');
  assert.equal(t('pack.7z'), 'archive'); assert.equal(t('setup.exe'), 'program'); assert.equal(t('setup.msi'), 'program'); assert.equal(t('doc.pdf'), 'document');
  assert.equal(t('https://x.example/dl?id=3', 'image/gif'), 'gif'); assert.equal(t('https://x.example/dl?id=3', 'video/mp4'), 'video'); assert.equal(t('https://x.example/dl?id=3', 'application/octet-stream'), 'other');
  assert.equal(t('https://x.example/files/a.zip?token=1'), 'archive'); assert.equal(t(''), 'other');
});
test('classify: images/GIFs are detected as their own kinds above the size gate only', () => {
  const g = S.classify(response('https://cdn.example/a.gif', 'image/gif', 2 * MB)); assert.equal(g.kind, 'gif'); assert.equal(S.typeOfItem(g), 'gif');
  const p = S.classify(response('https://cdn.example/a.png', 'image/png', 200 * 1024)); assert.equal(p.kind, 'image'); assert.equal(S.typeOfItem(p), 'image');
  assert.equal(S.classify(response('https://cdn.example/icon.png', 'image/png', 4096)), null);
  assert.equal(S.typeOfItem(S.classify(response('https://cdn.example/a.mp3', 'audio/mpeg', MB))), 'audio');
});

test('capture honours the type filter: unticked types stay with the browser, ticked ones are taken over (GIF separately from images)', async () => {
  const cases = [['https://f.example/a.zip', true], ['https://f.example/a.exe', true], ['https://f.example/a.pdf', true], ['https://f.example/a.mp4', true], ['https://f.example/a.mp3', true], ['https://f.example/blob.bin', true],
    ['https://f.example/a.png', false], ['https://f.example/a.gif', false]];
  for (const [url, taken] of cases) {
    const b = makeBackground({ settings: { captureEnabled: true } });
    const { log, sent } = await capture(b, dl(url)); assert.equal(sent.length, taken ? 1 : 0, url); assert.deepEqual(log, taken ? ['cancel', 'erase'] : [], url);
  }
  let b = makeBackground({ settings: { captureEnabled: true, mediaTypes: { gif: true } } });
  assert.equal((await capture(b, dl('https://f.example/a.gif'))).sent.length, 1, 'GIF on');
  assert.equal((await capture(b, dl('https://f.example/a.jpg', { id: 12 }))).sent.length, 0, 'images still off');
  b = makeBackground({ settings: { captureEnabled: true, mediaTypes: { image: true, archive: false } } });
  assert.equal((await capture(b, dl('https://f.example/a.jpg'))).sent.length, 1, 'images on'); assert.equal((await capture(b, dl('https://f.example/a.gif'))).sent.length, 0, 'GIF off');
  assert.equal((await capture(b, dl('https://f.example/a.zip', { id: 13 }))).sent.length, 0, 'archives off');
});
test('ribbon detection honours the type filter: GIF/images only when ticked; video/audio can be switched off', async () => {
  const send = async (b, url, type, size) => { b.hooks.headers(response(url, type, size)); await tick(); };
  const list = async b => (await b.message({ action: 'media-list' }, b.pageSender())).items.map(i => i.kind);
  let b = makeBackground(); await send(b, 'https://c.example/a.gif', 'image/gif', 2 * MB); await send(b, 'https://c.example/a.png', 'image/png', 2 * MB); await send(b, 'https://c.example/a.mp4', 'video/mp4', 2 * MB);
  assert.deepEqual(json(await list(b)), ['video']);
  b = makeBackground({ settings: { mediaTypes: { gif: true } } }); await send(b, 'https://c.example/a.gif', 'image/gif', 2 * MB); await send(b, 'https://c.example/a.png', 'image/png', 2 * MB);
  assert.deepEqual(json(await list(b)), ['gif']);
  b = makeBackground({ settings: { mediaTypes: { image: true, gif: true, video: false } } }); await send(b, 'https://c.example/a.gif', 'image/gif', 2 * MB); await send(b, 'https://c.example/a.png', 'image/png', 2 * MB); await send(b, 'https://c.example/a.mp4', 'video/mp4', 2 * MB);
  assert.deepEqual(json(await list(b)).sort(), ['gif', 'image']);
  b.local.mediaTypes = { video: true }; assert.deepEqual(json(await list(b)), [], 'turning types off hides already-listed items of those types');
});

test('session pause: this site only', async () => {
  const b = makeBackground({ settings: { captureEnabled: true } });
  const r = await b.message({ action: 'pause-set', scope: 'site', host: 'Site.Example', on: true }); assert.equal(r.ok, true);
  assert.deepEqual(json(b.sessionData['neodm.pause']), { all: false, hosts: ['site.example'] });
  let c = await capture(b, dl('https://other.example/a.zip', { referrer: 'https://site.example/page' })); assert.equal(c.sent.length, 0, 'paused page (referrer host)');
  c = await capture(b, dl('https://site.example/a.zip', { id: 20, referrer: '' })); assert.equal(c.sent.length, 0, 'paused download host');
  c = await capture(b, dl('https://third.example/a.zip', { id: 21, referrer: 'https://third.example/' })); assert.equal(c.sent.length, 1, 'other sites keep working');
  await b.message({ action: 'pause-set', scope: 'site', host: 'site.example', on: false });
  assert.deepEqual(json(b.sessionData['neodm.pause']), { all: false, hosts: [] });
  c = await capture(b, dl('https://site.example/b.zip', { id: 22 })); assert.equal(c.sent.length, 1, 're-enabled');
});
test('session pause: all sites; capture and ribbon are both off, one click re-enables', async () => {
  const b = makeBackground({ settings: { captureEnabled: true } });
  b.hooks.headers(response('https://c.example/a.mp4', 'video/mp4', 2 * MB, { initiator: 'https://video.example' })); await tick();
  const items = async () => (await b.message({ action: 'media-list' }, b.pageSender())).items.length;
  assert.equal(await items(), 1);
  await b.message({ action: 'pause-set', scope: 'all', on: true });
  assert.equal(await items(), 0, 'ribbon list is empty while paused');
  assert.equal((await capture(b, dl('https://f.example/a.zip'))).sent.length, 0);
  await b.message({ action: 'pause-set', scope: 'all', on: false });
  assert.equal(await items(), 1, 'ribbon is back'); assert.equal((await capture(b, dl('https://f.example/a.zip', { id: 30 }))).sent.length, 1);
});
test('session pause: ribbon hidden per site (host of the page), other sites unaffected; open tabs are told immediately', async () => {
  const tab = { id: 7, url: 'https://video.example/watch?v=1', title: 'x' };
  const b = makeBackground({ openTabs: [tab] });
  b.hooks.headers(response('https://c.example/a.mp4', 'video/mp4', 2 * MB, { initiator: 'https://video.example' })); await tick();
  b.sentToTabs.length = 0;
  await b.message({ action: 'pause-set', scope: 'site', host: 'video.example', on: true }); await tick();
  assert.deepEqual(json(b.sentToTabs.at(-1)), { tabId: 7, msg: { type: 'neodm-media', items: [] } });
  assert.equal((await b.message({ action: 'media-list' }, b.pageSender(tab))).items.length, 0);
  await b.message({ action: 'pause-set', scope: 'site', host: 'video.example', on: false }); await tick();
  assert.equal(b.sentToTabs.at(-1).msg.items.length, 1);
});
test('pause state lives in chrome.storage.session only, is popup-only and validated', async () => {
  const b = makeBackground();
  await b.message({ action: 'pause-set', scope: 'all', on: true });
  assert.ok('neodm.pause' in b.sessionData); assert.equal('neodm.pause' in b.local, false, 'never persisted to local storage');
  assert.equal(b.hooks.message({ action: 'pause-set', scope: 'all', on: true }, { id: 'ext-id', url: 'https://evil.example/', frameId: 0, tab: { id: 1 } }, () => {}), false, 'a web page cannot pause or resume');
  assert.equal((await b.message({ action: 'pause-set', scope: 'site', host: 'a b/c', on: true })).ok, false);
  assert.equal((await b.message({ action: 'pause-set', scope: 'bogus' })).ok, false);
});

/* ---- popup UI (real popup.html + popup.js in jsdom) ---- */
async function popup({ local = {}, session = {}, tabUrl = 'https://site.example/page' } = {}) {
  const dom = new JSDOM(fs.readFileSync(path.join(__dirname, '../extension/popup.html'), 'utf8'), { runScripts: 'outside-only', url: 'chrome-extension://ext-id/popup.html' }), w = dom.window;
  const messages = [];
  w.chrome = {
    storage: { local: { get: async k => Object.fromEntries([].concat(k).filter(x => x in local).map(x => [x, local[x]])), set: async o => Object.assign(local, o) }, session: { get: async k => (k in session ? { [k]: session[k] } : {}) } },
    tabs: { query: async () => [{ id: 1, url: tabUrl }] },
    runtime: { sendMessage: async m => { messages.push(m); if (m.action === 'pause-set') { const p = session['neodm.pause'] ||= { all: false, hosts: [] }; if (m.scope === 'all') p.all = m.on; else p.hosts = p.hosts.filter(h => h !== m.host).concat(m.on ? [m.host] : []); return { ok: true }; } return { ok: true }; } },
    permissions: { request: async () => true, remove: async () => true },
  };
  withI18n(w); w.eval(source('sniff.js')); w.eval(source('popup.js')); await tick(12);
  return { w, doc: w.document, local, session, messages };
}
test('popup renders: capture on by default, all eight type checkboxes with the right defaults, session pause controls', async () => {
  const { doc } = await popup();
  assert.equal(doc.getElementById('capture').checked, true, 'capture default on');
  const boxes = [...doc.querySelectorAll('#types input[data-type]')]; assert.deepEqual(boxes.map(x => x.dataset.type), S.TYPE_IDS);
  assert.deepEqual(Object.fromEntries(boxes.map(x => [x.dataset.type, x.checked])), S.DEFAULT_TYPES);
  assert.match(doc.getElementById('types').textContent, /Video[\s\S]*Audio[\s\S]*Images[\s\S]*GIF[\s\S]*Archives[\s\S]*Programs[\s\S]*Documents[\s\S]*Other/);
  assert.match(doc.getElementById('pauseSiteLabel').textContent, /site\.example/); assert.equal(doc.getElementById('resume').hidden, true);
  assert.equal(doc.getElementById('pauseState').textContent, 'NeoDM is active.');
  for (const id of ['capture', 'cookies', 'ribbon']) assert.ok(doc.getElementById(id), id);
});
test('popup: ticking a type stores it; pausing this site / all sites messages the worker; re-enable is one click', async () => {
  const p = await popup(), { doc } = p; const click = el => { el.checked = !el.checked; el.dispatchEvent(new p.w.Event('change')); };
  click(doc.querySelector('[data-type=gif]')); await tick(); assert.equal(p.local.mediaTypes.gif, true); assert.equal(p.local.mediaTypes.image, false);
  click(doc.querySelector('[data-type=archive]')); await tick(); assert.equal(p.local.mediaTypes.archive, false); assert.equal(p.local.mediaTypes.gif, true);
  click(doc.getElementById('pauseSite')); await tick();
  assert.deepEqual(json(p.messages.at(-1)), { action: 'pause-set', scope: 'site', host: 'site.example', on: true });
  assert.match(doc.getElementById('pauseState').textContent, /paused on site\.example/); assert.equal(doc.getElementById('resume').hidden, false);
  click(doc.getElementById('pauseAll')); await tick(); assert.equal(p.messages.at(-1).scope, 'all'); assert.match(doc.getElementById('pauseState').textContent, /all sites/);
  assert.equal(doc.getElementById('pauseSite').disabled, true, 'site switch is covered by all-sites');
  doc.getElementById('resume').click(); await tick(12);
  assert.equal(doc.getElementById('pauseState').textContent, 'NeoDM is active.'); assert.equal(doc.getElementById('resume').hidden, true);
  assert.equal(doc.getElementById('pauseAll').checked, false); assert.equal(doc.getElementById('pauseSite').checked, false);
});
test('popup shows the stored state: pause active on open, stored types, explicit capture off', async () => {
  const { doc } = await popup({ local: { captureEnabled: false, mediaTypes: { image: true, video: false } }, session: { 'neodm.pause': { all: false, hosts: ['site.example'] } } });
  assert.equal(doc.getElementById('capture').checked, false); assert.equal(doc.querySelector('[data-type=image]').checked, true); assert.equal(doc.querySelector('[data-type=video]').checked, false);
  assert.equal(doc.getElementById('resume').hidden, false);
});
