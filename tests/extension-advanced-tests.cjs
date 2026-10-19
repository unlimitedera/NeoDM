'use strict';
/* Media sniffing, per-tab state, and ribbon DOM tests. jsdom is the only dependency (npm ci --prefix tests). */
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs'), path = require('node:path');
const { JSDOM } = require('jsdom');
const S = require('../extension/sniff.js');
const { makeBackground, response, tick, json, source, withI18n } = require('./extension-harness.cjs');
const MB = 1024 * 1024;
const gv = (q, extra = {}) => response('https://rr1---sn-abc.googlevideo.com/videoplayback?' + new URLSearchParams({ expire: '1', id: 'o-abc', ...q }), q.mime || 'video/mp4', 2000, extra);

test('sniff: direct media by Content-Type with size >= 512 KB; boundary and below', () => {
  const ok = S.classify(response('https://cdn.example/movie.mp4', 'video/mp4', 5 * MB));
  assert.deepEqual(json(ok), { id: 'https://cdn.example/movie.mp4', url: 'https://cdn.example/movie.mp4', rawUrl: 'https://cdn.example/movie.mp4', groupKey: 'https://cdn.example/movie.mp4', kind: 'video', track: 'file', complete: true, size: 5 * MB, quality: '', mime: 'video/mp4', source: 'net' });
  assert.equal(S.classify(response('https://cdn.example/a.mp4', 'video/mp4', 512 * 1024)).size, 512 * 1024);
  assert.equal(S.classify(response('https://cdn.example/a.mp4', 'video/mp4', 512 * 1024 - 1)), null);
  assert.equal(S.classify(response('https://cdn.example/a.mp3', 'audio/mpeg; charset=x', 3 * MB)).kind, 'audio');
  assert.equal(S.classify(response('https://cdn.example/a.mp4', 'video/mp4', null)), null, 'unknown size is not offered');
});
test('sniff: 206 responses use the Content-Range total, not the chunk length', () => {
  const d = response('https://cdn.example/a.webm', 'video/webm', 100, { statusCode: 206 }); d.responseHeaders.push({ name: 'content-range', value: 'bytes 0-99/7340032' });
  assert.equal(S.classify(d).size, 7340032);
  const star = response('https://cdn.example/a.webm', 'video/webm', 900000, { statusCode: 206 }); star.responseHeaders.push({ name: 'Content-Range', value: 'bytes 0-899999/*' });
  assert.equal(S.classify(star), null);
});
test('sniff: HLS/DASH manifests, segments, non-media, POST, errors and non-tab requests are excluded', () => {
  for (const [url, type] of [['https://c.example/x.m3u8', 'application/vnd.apple.mpegurl'], ['https://c.example/x', 'application/x-mpegURL'], ['https://c.example/x', 'audio/mpegurl'], ['https://c.example/x.mpd', 'application/dash+xml'], ['https://c.example/x', 'video/vnd.mpeg.dash.mpd'], ['https://c.example/seg1', 'video/MP2T'], ['https://c.example/s.ts', 'video/mp4'], ['https://c.example/s.m4s', 'video/mp4'], ['https://c.example/seg', 'video/iso.segment'], ['https://c.example/p', 'text/html'], ['https://c.example/p', 'application/octet-stream']])
    assert.equal(S.classify(response(url, type, 9 * MB)), null, url + ' ' + type);
  assert.equal(S.classify(response('https://c.example/a.mp4', 'video/mp4', 9 * MB, { method: 'POST' })), null);
  assert.equal(S.classify(response('https://c.example/a.mp4', 'video/mp4', 9 * MB, { statusCode: 404 })), null);
  assert.equal(S.classify(response('https://c.example/a.mp4', 'video/mp4', 9 * MB, { tabId: -1 })), null);
  assert.equal(S.classify(response('ftp://c.example/a.mp4', 'video/mp4', 9 * MB)), null);
  assert.equal(S.classify(response('https://u:p@c.example/a.mp4', 'video/mp4', 9 * MB)), null);
});
test('sniff googlevideo: muxed itag 18/22 are complete videos with quality; range/rn/rbuf stripped; clen is the size', () => {
  const a = S.classify(gv({ itag: '18', mime: 'video/mp4', clen: '12345678', range: '0-1048575', rn: '1', rbuf: '9000' }));
  assert.equal(a.complete, true); assert.equal(a.track, 'muxed'); assert.equal(a.quality, '360p'); assert.equal(a.size, 12345678); assert.equal(a.kind, 'video');
  const u = new URL(a.url); for (const p of ['range', 'rn', 'rbuf']) assert.equal(u.searchParams.has(p), false); assert.equal(u.searchParams.get('itag'), '18'); assert.equal(u.searchParams.get('id'), 'o-abc');
  const b = S.classify(gv({ itag: '18', mime: 'video/mp4', clen: '12345678', range: '1048576-2097151', rn: '2', rbuf: '1' }));
  assert.equal(b.id, a.id, 'every chunk of one stream maps to one item');
  const c = S.classify(gv({ itag: '22', mime: 'video/mp4', clen: '55000000' })); assert.equal(c.quality, '720p'); assert.equal(c.complete, true);
});
test('sniff googlevideo: audio-only and video-only streams are labelled and never complete', () => {
  const audio = S.classify(gv({ itag: '140', mime: 'audio/mp4', clen: '3000000' })); assert.equal(audio.track, 'audio-only'); assert.equal(audio.complete, false); assert.equal(audio.kind, 'audio');
  assert.equal(S.classify(gv({ itag: '251', mime: 'audio/webm', clen: '3000000' })).track, 'audio-only');
  const v = S.classify(gv({ itag: '137', mime: 'video/mp4', clen: '90000000' })); assert.equal(v.track, 'video-only'); assert.equal(v.complete, false); assert.equal(v.quality, '1080p');
  assert.equal(S.classify(gv({ itag: '248', mime: 'video/webm', clen: '90000000' })).complete, false);
  const unknown = S.classify(gv({ itag: '99999', mime: 'video/mp4', clen: '90000000' })); assert.equal(unknown.track, 'video-only'); assert.equal(unknown.complete, false, 'unknown itag must not be presented as complete');
  assert.equal(S.classify(gv({ itag: '18', clen: '90000000' })), null, 'no mime parameter -> not classified');
  assert.equal(S.classify(gv({ itag: '18', mime: 'video/mp4', clen: '400000' })), null, 'below size gate');
  assert.equal(S.classify(gv({ itag: '18', mime: 'video/mp4' })), null, 'no clen and tiny chunk');
  const cr = gv({ itag: '22', mime: 'video/mp4' }, { statusCode: 206 }); cr.responseHeaders.push({ name: 'Content-Range', value: 'bytes 0-1999/6000000' }); assert.equal(S.classify(cr).size, 6000000, 'falls back to Content-Range');
});
test('sniff: DOM items, merge precedence, newest first, cap', () => {
  assert.equal(S.fromDom('blob:https://x/1', 'video'), null); assert.equal(S.fromDom('https://x/a.m3u8', 'video'), null); assert.equal(S.fromDom('https://x/a.mpd?x=1'.replace('?x=1', ''), 'video'), null);
  assert.equal(S.fromDom('https://r1.googlevideo.com/videoplayback?itag=18', 'video'), null);
  const dom = S.fromDom('https://x/a.mp4', 'video'), net = S.classify(response('https://x/a.mp4', 'video/mp4', 4 * MB));
  let list = S.merge([], dom); list = S.merge(list, net); assert.equal(list.length, 1); assert.equal(list[0].size, 4 * MB); assert.equal(list[0].source, 'net'); assert.equal(list[0].mime, 'video/mp4');
  list = S.merge(list, dom); assert.equal(list[0].size, 4 * MB, 'DOM re-report does not erase sniffed size');
  let many = []; for (let i = 0; i < 40; i++) many = S.merge(many, S.fromDom(`https://x/${i}.mp4`, 'video')); assert.equal(many.length, 30); assert.equal(many[0].url, 'https://x/39.mp4');
});
test('file names: sanitized title + extension, track suffix, host-safe characters only', () => {
  const g = S.classify(gv({ itag: '18', mime: 'video/mp4', clen: '9000000' }));
  assert.equal(S.suggestedName(g, 'My: Clip? <1> - YouTube'), 'My Clip 1 360p.mp4');
  const a = S.classify(gv({ itag: '140', mime: 'audio/mp4', clen: '9000000' })); assert.equal(S.suggestedName(a, 'Song'), 'Song (audio only).m4a');
  const v = S.classify(gv({ itag: '137', mime: 'video/mp4', clen: '9000000' })); assert.equal(S.suggestedName(v, 'Song'), 'Song 1080p (video only).mp4');
  assert.equal(S.suggestedName(g, ''), ''); assert.equal(S.suggestedName(g, '  . '), '');
  const bad = S.suggestedName(g, 'a/b\\c:d*e?f"g<h>i|j\u0001k'); assert.doesNotMatch(bad, /[\\/:*?"<>|\u0000-\u001f]/);
  assert.ok(S.suggestedName(g, 'x'.repeat(600)).length <= 255); assert.equal(S.safeName('CON'), 'CON_'); assert.equal(S.safeName('trailing dot...'), 'trailing dot');
  const direct = S.classify(response('https://cdn.example/files/movie.mkv', 'video/x-matroska', 9 * MB)); assert.equal(S.suggestedName(direct, 'Page'), 'Page.mkv');
  assert.equal(S.suggestedName(S.fromDom('https://cdn.example/files/movie.mp4', 'video'), 'Page'), '', 'real URL file names are left to the app for DOM sources');
});

/* ---- service worker integration ---- */
const clip = response('https://files.example/clip.mp4', 'video/mp4', 6 * MB);
test('worker: sniffed items persist in session storage, set badge, notify the tab, and survive a worker restart', async () => {
  const b = makeBackground(); b.hooks.headers(clip); b.hooks.headers(clip); await tick();
  assert.deepEqual(json(b.hooks['headers:args']), [{ urls: ['http://*/*', 'https://*/*'], types: ['media', 'xmlhttprequest', 'other', 'object', 'image'] }, ['responseHeaders']]);
  const stored = json(b.sessionData['neodm.media.7']); assert.equal(stored.items.length, 1); assert.equal(stored.items[0].size, 6 * MB);
  assert.equal(b.sentToTabs.length, 1, 'identical repeat does not re-notify'); assert.equal(b.sentToTabs[0].msg.type, 'neodm-media'); assert.equal(b.badge.at(-1).text, '1');
  const restarted = makeBackground({ sessionData: b.sessionData });
  const r = await restarted.message({ action: 'media-list' }, restarted.pageSender()); assert.equal(r.items.length, 1); assert.equal(r.items[0].url, 'https://files.example/clip.mp4');
});
test('worker: navigation clears the tab list (also SPA url change), hash-only change and first sighting do not; tab close removes', async () => {
  const b = makeBackground(); b.hooks.headers(clip); await tick();
  b.hooks.tabUpdated(7, { url: 'https://video.example/watch?v=1' }); await tick();
  assert.equal(json(b.sessionData['neodm.media.7']).items.length, 1, 'first url sighting after a restart keeps sniffed items');
  b.hooks.tabUpdated(7, { url: 'https://video.example/watch?v=1#t=30' }); await tick(); assert.equal(json(b.sessionData['neodm.media.7']).items.length, 1);
  b.hooks.tabUpdated(7, { title: 'x' }); await tick(); assert.equal(json(b.sessionData['neodm.media.7']).items.length, 1);
  b.hooks.tabUpdated(7, { url: 'https://video.example/watch?v=2' }); await tick();
  assert.equal(json(b.sessionData['neodm.media.7']).items.length, 0); assert.deepEqual(b.sentToTabs.at(-1).msg.items, []); assert.equal(b.badge.at(-1).text, '');
  b.hooks.headers(clip); await tick(); assert.equal(json(b.sessionData['neodm.media.7']).items.length, 1);
  b.hooks.tabRemoved(7); await tick(); assert.equal(b.sessionData['neodm.media.7'], undefined);
});
test('worker: items are per tab', async () => {
  const b = makeBackground(); b.hooks.headers(clip); b.hooks.headers({ ...clip, tabId: 8, url: 'https://files.example/other.mp4' }); await tick();
  assert.equal((await b.message({ action: 'media-list' }, b.pageSender({ id: 7, url: 'https://a.example/' }))).items.length, 1);
  assert.equal((await b.message({ action: 'media-list' }, b.pageSender({ id: 8, url: 'https://a.example/' }))).items[0].url, 'https://files.example/other.mp4');
});
test('worker: ribbon download sends chosen URL, tab referer and title-based fileName through the unchanged wire contract', async () => {
  const b = makeBackground();
  b.hooks.headers(gv({ itag: '18', mime: 'video/mp4', clen: '9000000', range: '0-99', rn: '1' }, { tabId: 7 })); b.hooks.headers(gv({ itag: '140', mime: 'audio/mp4', clen: '3000000' }, { tabId: 7 })); await tick();
  const list = (await b.message({ action: 'media-list' }, b.pageSender())).items; assert.equal(list.length, 2);
  const mux = list.find(i => i.track === 'muxed'), aud = list.find(i => i.track === 'audio-only');
  const r = await b.message({ action: 'media-download', id: mux.id }, b.pageSender()); assert.equal(r.ok, true); assert.equal(r.id, 'saved-1');
  const req = b.nativeRequests.at(-1); assert.deepEqual(Object.keys(req).sort(), ['cookie', 'extVersion', 'fileName', 'keepAlive', 'method', 'referer', 'url']);
  assert.equal(req.url, mux.url); assert.equal(req.referer, 'https://video.example/watch?v=1'); assert.equal(req.fileName, 'My Clip 360p.mp4'); assert.equal(req.method, 'GET'); assert.equal(req.cookie, '');
  await b.message({ action: 'media-download', id: aud.id }, b.pageSender()); assert.equal(b.nativeRequests.at(-1).fileName, 'My Clip (audio only).m4a');
  const before = b.nativeRequests.length; const miss = await b.message({ action: 'media-download', id: 'https://evil.example/x.mp4' }, b.pageSender());
  assert.equal(miss.ok, false); assert.equal(b.nativeRequests.length, before, 'the page cannot make the worker download an URL it did not sniff');
});
test('worker: content script DOM sources are validated and merged; blob/HLS/credential URLs dropped', async () => {
  const b = makeBackground();
  await b.message({ action: 'media-dom', sources: [{ url: 'https://x.example/a.mp4', tag: 'video' }, { url: 'blob:https://x.example/1', tag: 'video' }, { url: 'https://x.example/l.m3u8', tag: 'video' }, { url: 'https://u:p@x.example/b.mp4', tag: 'audio' }, { url: 'https://x.example/c.mp3', tag: 'audio' }] }, b.pageSender());
  await tick(); const items = json(b.sessionData['neodm.media.7']).items; assert.deepEqual(items.map(i => [i.url, i.kind]).sort(), [['https://x.example/a.mp4', 'video'], ['https://x.example/c.mp3', 'audio']]);
});

/* ---- ribbon DOM (content.js) ---- */
function ribbon(items = [], { enabled = true, reply = { ok: true, id: 'job-1' } } = {}) {
  const dom = new JSDOM('<!doctype html><body><video src="https://files.example/a.mp4"></video></body>', { url: 'https://page.example/watch', runScripts: 'outside-only', pretendToBeVisual: true }), w = dom.window;
  const roots = [], origAttach = w.Element.prototype.attachShadow;
  w.Element.prototype.attachShadow = function (init) { const r = origAttach.call(this, init); roots.push({ host: this, root: r, mode: init.mode }); return r; };
  const sent = [], listeners = []; let storageChange;
  w.chrome = {
    runtime: { sendMessage: async m => { sent.push(m); if (m.action === 'media-list') return { items }; if (m.action === 'media-download') return reply instanceof Error ? Promise.reject(reply) : reply; return { ok: true }; }, onMessage: { addListener: f => listeners.push(f) } },
    storage: { local: { get: async () => (enabled ? {} : { ribbonEnabled: false }) }, onChanged: { addListener: f => { storageChange = f; } } },
  };
  withI18n(w); w.eval(source('sniff.js')); w.eval(source('content.js'));
  const ui = () => roots.at(-1), text = () => ui().root.textContent;
  return { w, dom, sent, roots, ui, text, push: next => listeners.forEach(f => f({ type: 'neodm-media', items: next })), storageChange: v => storageChange({ ribbonEnabled: { newValue: v } }, 'local') };
}
const item = (o = {}) => ({ id: 'https://v.example/a.mp4', url: 'https://v.example/a.mp4', kind: 'video', track: 'muxed', complete: true, size: 12 * MB, quality: '720p', mime: 'video/mp4', source: 'net', ...o });
test('ribbon: closed shadow root, type/quality/size, button label; page cannot reach it; nothing without items', async () => {
  const none = ribbon([]); await tick(); assert.equal(none.roots.length, 0); assert.equal(none.w.document.querySelector('[data-neodm-ribbon]'), null);
  const r = ribbon([item()]); await tick(); assert.equal(r.roots.length, 1); assert.equal(r.ui().mode, 'closed');
  const host = r.w.document.querySelector('[data-neodm-ribbon]'); assert.ok(host); assert.equal(host.shadowRoot, null, 'closed root is invisible to page scripts');
  assert.match(r.text(), /Video · 720p · 12\.0 MB/); const btn = r.ui().root.querySelector('button.dl'); assert.equal(btn.textContent, 'Download with NeoDM');
  assert.ok(r.ui().root.querySelector('button.x'));
});
test('ribbon: audio-only / video-only are labelled as partial and listed after complete videos', async () => {
  const r = ribbon([item({ id: 'a', track: 'audio-only', kind: 'audio', complete: false, quality: '', size: 3 * MB }), item({ id: 'v', track: 'video-only', complete: false, quality: '1080p', size: 80 * MB }), item({ id: 'm' })]); await tick();
  const rows = [...r.ui().root.querySelectorAll('.row')]; assert.deepEqual(rows.map(x => x.dataset.id), ['m', 'a', 'v']);
  assert.match(rows[1].textContent, /Audio only · 3\.0 MB/); assert.match(rows[1].textContent, /not a complete video/); assert.match(rows[2].textContent, /Video only \(no sound\) · 1080p · 80\.0 MB/);
  assert.doesNotMatch(rows[0].textContent, /not a complete|no sound|only/i);
});
test('ribbon: download sends only the item id, shows waiting then accepted; failure shows the error and allows retry', async () => {
  const r = ribbon([item()]); await tick();
  const click = () => r.ui().root.querySelector('button.dl').click();
  click(); assert.equal(r.ui().root.querySelector('button.dl').textContent, 'Waiting for NeoDM…'); assert.equal(r.ui().root.querySelector('button.dl').disabled, true); click();
  await tick(); assert.deepEqual(json(r.sent.filter(m => m.action === 'media-download')), [{ action: 'media-download', id: 'https://v.example/a.mp4' }]);
  assert.equal(r.ui().root.querySelector('button.dl').textContent, 'Sent'); assert.match(r.text(), /Accepted by NeoDM/);
  const f = ribbon([item()], { reply: { ok: false, error: 'Download cancelled by user' } }); await tick(); f.ui().root.querySelector('button.dl').click(); await tick();
  assert.match(f.text(), /Download cancelled by user/); assert.equal(f.ui().root.querySelector('button.dl').disabled, false); assert.equal(f.ui().root.querySelector('button.dl').textContent, 'Download with NeoDM');
  const e = ribbon([item()], { reply: new Error('worker gone') }); await tick(); e.ui().root.querySelector('button.dl').click(); await tick(); assert.match(e.text(), /unavailable/);
});
test('ribbon: updates from the worker, dismiss until something new appears, navigation clear removes it, setting toggles it', async () => {
  const r = ribbon([item()]); await tick();
  r.ui().root.querySelector('button.x').click(); assert.equal(r.w.document.querySelector('[data-neodm-ribbon]'), null);
  r.push([item()]); assert.equal(r.w.document.querySelector('[data-neodm-ribbon]'), null, 'same list stays dismissed');
  r.push([item({ id: 'https://v.example/b.mp4', url: 'https://v.example/b.mp4' }), item()]); assert.ok(r.w.document.querySelector('[data-neodm-ribbon]'), 'new item re-shows the ribbon'); assert.equal(r.ui().root.querySelectorAll('.row').length, 2);
  r.push([]); assert.equal(r.w.document.querySelector('[data-neodm-ribbon]'), null);
  r.push([item()]); r.storageChange(false); assert.equal(r.w.document.querySelector('[data-neodm-ribbon]'), null); r.storageChange(true); assert.ok(r.w.document.querySelector('[data-neodm-ribbon]'));
  const off = ribbon([item()], { enabled: false }); await tick(); assert.equal(off.w.document.querySelector('[data-neodm-ribbon]'), null);
});
test('ribbon: untrusted text (titles/URLs/errors) is rendered as text, never HTML; direct <video> sources are reported once; blob/encrypted skipped', async () => {
  const r = ribbon([item({ url: 'https://v.example/<img src=x onerror=1>.mp4', id: 'x' })], { reply: { ok: false, error: '<b>bad</b>' } }); await tick();
  r.ui().root.querySelector('button.dl').click(); await tick(); assert.equal(r.ui().root.querySelectorAll('img,b').length, 0);
  const dom = r.sent.filter(m => m.action === 'media-dom'); assert.equal(dom.length, 1); assert.deepEqual(json(dom[0].sources).map(s => [s.url, s.tag]), [['https://files.example/a.mp4', 'video']], 'the extra metadata fields ride along; url and tag are unchanged');
  const d = r.w.document, blob = d.createElement('video'); blob.src = 'blob:https://page.example/z'; const drm = d.createElement('video'); drm.src = 'https://files.example/drm.mp4'; Object.defineProperty(drm, 'mediaKeys', { value: {} }); d.body.append(blob, drm);
  r.w.__neodmRibbon.refresh(); await tick(); assert.equal(r.sent.filter(m => m.action === 'media-dom').length, 1, 'unchanged source set is not re-sent');
});
test('ribbon: content script injected twice does not duplicate', async () => {
  const r = ribbon([item()]); await tick(); r.w.eval(source('content.js')); await tick(); assert.equal(r.w.document.querySelectorAll('[data-neodm-ribbon]').length, 1);
});
