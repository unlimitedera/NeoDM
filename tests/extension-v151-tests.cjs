'use strict';
/* 1.5.1: identifiable media rows (metadata, ordering, badges), "show on page", the video context menu, the YouTube note, and the new texts in every language. */
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { JSDOM } = require('jsdom');
const S = require('../extension/sniff.js');
const { makeBackground, response, tick, json, source, withI18n } = require('./extension-harness.cjs');
const root = path.join(__dirname, '..');
const MB = 1024 * 1024;
const net = (url, o = {}) => ({ id: url, url, rawUrl: url, groupKey: url, kind: 'video', track: 'file', complete: true, size: 10 * MB, quality: '', mime: 'video/mp4', source: 'net', ...o });
const tab = { id: 7, url: 'https://video.example/watch?v=1', title: 'My Clip' };

test('manifest, changelog and Directory.Build.props carry 1.5.1; sniff.js loads before content.js', () => {
  const version = /<Version>([^<]+)<\/Version>/.exec(fs.readFileSync(path.join(root, 'Directory.Build.props'), 'utf8'))[1];
  assert.match(version, /^1\.5\.\d+$/);
  const manifest = JSON.parse(fs.readFileSync(path.join(root, 'extension/manifest.json'), 'utf8'));
  assert.equal(manifest.version, version);
  assert.deepEqual(manifest.content_scripts[0].js, ['i18n.js', 'sniff.js', 'content.js']);
  assert.match(fs.readFileSync(path.join(root, 'CHANGELOG.md'), 'utf8'), /^## \[1\.5\.1\] - /m);
  assert.match(fs.readFileSync(path.join(root, 'README.md'), 'utf8'), new RegExp('Current version: ' + version.replace(/\./g, '\\.')));
});

/* ---- metadata: validation, caps, merge ---- */
test('cleanMeta caps strings, drops oversized or malformed thumbnails and posters, clamps numbers', () => {
  const ok = 'data:image/jpeg;base64,' + 'A'.repeat(1000);
  const big = 'data:image/jpeg;base64,' + 'A'.repeat(S.THUMB_MAX);
  const m = S.cleanMeta({ title: 'x'.repeat(500) + '\n\u0001', dur: 205.123, w: 1280, h: 720, poster: 'https://p.example/a.jpg', thumb: ok, playing: true, vis: 7 });
  assert.equal(Array.from(m.title).length, 200); assert.equal(m.dur, 205.1); assert.equal(m.w, 1280); assert.equal(m.thumb, ok); assert.equal(m.playing, true); assert.equal(m.vis, 1);
  assert.equal(S.cleanMeta({ thumb: big }).thumb, undefined, 'thumbnail over 20 KB is dropped');
  assert.equal(S.cleanMeta({ thumb: 'https://evil.example/x.jpg' }).thumb, undefined, 'only data:image URLs');
  assert.equal(S.cleanMeta({ thumb: 'data:text/html;base64,AAAA' }).thumb, undefined);
  assert.equal(S.cleanMeta({ poster: 'javascript:alert(1)' }).poster, undefined);
  assert.equal(S.cleanMeta({ poster: 'https://p.example/' + 'a'.repeat(3000) }).poster, undefined);
  const bad = S.cleanMeta({ title: { a: 1 }, dur: 'abc', w: -5, h: 10, vis: -1, playing: 'yes' });
  assert.deepEqual(json(bad), { title: '[object Object]', playing: false, vis: 0 }); // no dims unless both are valid; playing only when exactly true
  assert.deepEqual(json(S.cleanMeta(null)), { playing: false, vis: 0 });
});
test('domReport: at most 200 elements are read, metadata lands on the item, blob players become state.blobs', () => {
  const st = { items: [] };
  const sources = Array.from({ length: 260 }, (_, n) => ({ url: `https://s.example/v${n}.mp4`, tag: 'video', title: `T${n}` }));
  assert.equal(S.domReport(st, sources), true);
  assert.equal(st.items.length, 200, 'at most 200 elements are read');
  const one = { items: [] };
  S.domReport(one, [{ url: 'https://s.example/a.mp4?range=0-9', tag: 'video', title: 'Clip A', dur: 205, w: 1280, h: 720, playing: true, vis: 1 }, { url: 'blob:', tag: 'video', title: 'Stream', playing: true, vis: 0.5 }]);
  assert.equal(one.items.length, 1); assert.equal(one.items[0].id, 'https://s.example/a.mp4'); assert.equal(one.items[0].title, 'Clip A'); assert.equal(one.items[0].dom, true);
  assert.equal(one.mse, true); assert.equal(one.blobs.length, 1); assert.equal(one.blobs[0].title, 'Stream');
  assert.equal(S.domReport(one, [{ url: 'https://s.example/a.mp4', tag: 'video', title: 'Clip A', dur: 205, w: 1280, h: 720, playing: true, vis: 1 }, { url: 'blob:', tag: 'video', title: 'Stream', playing: true, vis: 0.5 }]), false, 'identical report changes nothing');
  S.domReport(one, []);
  assert.equal(one.items[0].playing, false, 'an element that vanished stops being "playing"'); assert.equal(one.items[0].vis, 0); assert.deepEqual(one.blobs, []);
});
test('network info merges into a DOM item (both orders) without losing the element metadata', () => {
  const url = 'https://s.example/a.mp4';
  const st = { items: [] };
  S.domReport(st, [{ url, tag: 'video', title: 'Clip A', dur: 60, w: 640, h: 360, playing: true, vis: 1, thumb: 'data:image/jpeg;base64,AAAA' }]);
  S.upsert(st, net(url, { size: 12 * MB, mime: 'video/mp4' }));
  let it = st.items.find(i => i.id === url);
  assert.equal(it.size, 12 * MB); assert.equal(it.mime, 'video/mp4'); assert.equal(it.title, 'Clip A'); assert.equal(it.thumb, 'data:image/jpeg;base64,AAAA'); assert.equal(it.dur, 60); assert.equal(it.playing, true);
  const st2 = { items: [] };
  S.upsert(st2, net(url, { size: 12 * MB }));
  S.domReport(st2, [{ url: url + '?_=123', tag: 'video', title: 'Clip A', dur: 60, playing: false, vis: 0.25 }]);
  it = st2.items[0]; assert.equal(it.size, 12 * MB); assert.equal(it.title, 'Clip A'); assert.equal(it.vis, 0.25); assert.equal(st2.items.length, 1, 'cache-busting parameter maps to the same canonical item');
  S.domReport(st2, [{ url, tag: 'video', dur: 60, playing: true, vis: 1 }]); // later report without title or thumb keeps what was learned
  assert.equal(st2.items[0].title, 'Clip A'); assert.equal(st2.items[0].playing, true);
});

/* ---- ordering, titles, row text ---- */
test('ordering: playing now, then visible matched elements, then primary, then complete files by size', () => {
  const mk = (n, o) => net(`https://s.example/${n}.mp4`, o);
  const st = { items: [mk('small', { size: 20 * MB }), mk('big', { size: 90 * MB }), mk('seen', { dom: true, vis: 0.5, primary: true, size: 1 * MB }), mk('hidden', { dom: true, vis: 0, primary: true, size: 1 * MB }), mk('live', { dom: true, vis: 0.1, playing: true, primary: true, size: 0 })] };
  assert.deepEqual(S.listItems(st).map(i => i.id.split('/').pop()), ['live.mp4', 'seen.mp4', 'hidden.mp4', 'big.mp4', 'small.mp4']);
  // a blob player is paired with the single free network file: that file becomes "playing" and takes the player's title
  const m = { items: [net('https://cdn.example/stream.mp4', { size: 50 * MB })], mse: true, blobs: [{ tag: 'video', title: 'Live show', playing: true, vis: 1, dur: 3600, w: 1920, h: 1080 }] };
  const [first] = S.listItems(m); assert.equal(first.playing, true); assert.equal(first.title, 'Live show'); assert.equal(first.viaBlob, true); assert.equal(first.dur, 3600);
  const small = { ...m, items: [net('https://cdn.example/big.mp4', { size: 90 * MB, dom: true, primary: true }), net('https://cdn.example/stream.mp4', { size: 1 * MB })] };
  assert.ok(S.listItems(small).some(i => i.url.endsWith('stream.mp4') && i.viaBlob), 'a small file that belongs to the player is not dropped as a leftover');
  const two = { ...m, items: [...m.items, net('https://cdn.example/other.mp4', { size: 40 * MB })] };
  assert.ok(S.listItems(two).every(i => !i.playing && !i.title), 'two candidates: no guess');
});
test('row text: title (element title, else decoded file name, else host), "Video 1280x720 · 03:25 · 45.2 MB", duration formats', () => {
  const t = k => ({ kind_video: 'Video', kind_audio: 'Audio', kind_audio_only: 'Audio only' }[k] || k);
  const d = S.describeItem(net('https://cdn.example/a.mp4', { title: 'Holiday', w: 1280, h: 720, dur: 205, size: 45.2 * MB }), t);
  assert.equal(d.title, 'Holiday'); assert.equal(d.meta, 'Video 1280x720 · 03:25 · 45.2 MB'); assert.equal(d.host, 'cdn.example');
  assert.equal(S.describeItem(net('https://cdn.example/dir/My%20Trip%20%C3%A9.mp4'), t).title, 'My Trip \u00e9.mp4');
  assert.equal(S.describeItem(net('https://rr1.googlevideo.com/videoplayback?x=1'), t).title, 'rr1.googlevideo.com');
  assert.equal(S.describeItem(net('https://cdn.example/a.mp4', { quality: '720p' }), t).meta, 'Video · 720p · 10.0 MB');
  assert.equal(S.describeItem(net('https://cdn.example/a.mp4', { size: 0 }), t, { dur: 61, w: 320, h: 240 }).meta, 'Video 320x240 · 01:01', 'values from the preview element');
  assert.deepEqual(['', 0, NaN, 59, 3725].map(S.fmtDuration), ['', '', '', '00:59', '1:02:05']);
});

/* ---- YouTube ---- */
const gv = (itag, mime, clen) => response(`https://rr1---sn-abc.googlevideo.com/videoplayback?itag=${itag}&mime=${encodeURIComponent(mime)}&clen=${clen}&id=o1&expire=9`, mime, null, { initiator: 'https://www.youtube.com', tabId: 7 });
test('YouTube: fragment rows are hidden, a note explains the limit, a complete muxed file would still be listed; other sites are untouched', async () => {
  const yt = { id: 7, url: 'https://www.youtube.com/watch?v=abc', title: 'x - YouTube' };
  const b = makeBackground({ openTabs: [yt] });
  b.hooks.tabUpdated(7, { url: yt.url }); await tick();
  b.hooks.headers(gv(137, 'video/mp4', 9 * MB)); b.hooks.headers(gv(251, 'audio/webm', 3 * MB)); await tick();
  assert.equal(json(b.sessionData['neodm.media.7']).items.length, 2, 'the fragments are still tracked');
  const popupRows = (await b.message({ action: 'popup-media', tabId: 7 })).items;
  assert.deepEqual(json(popupRows.map(i => i.id)), ['neodm-youtube']); assert.match(popupRows[0].text, /YouTube sends video in a protected streaming format/);
  const ribbonRows = (await b.message({ action: 'media-list' }, b.pageSender(yt))).items; assert.deepEqual(json(ribbonRows.map(i => i.id)), ['neodm-youtube']);
  assert.equal(b.menuUpdates.at(-1)?.visible ?? false, false, 'no video menu entry without a downloadable item');
  b.hooks.headers(gv(18, 'video/mp4', 20 * MB)); await tick(); // a complete muxed file
  const rows = (await b.message({ action: 'media-list' }, b.pageSender(yt))).items;
  assert.deepEqual(json(rows.map(i => [i.track, i.quality])), [['muxed', '360p']], 'the complete file is shown, no note');
  assert.equal(b.menuUpdates.at(-1).visible, true);
  const other = makeBackground({ openTabs: [tab] });
  other.hooks.headers(response('https://rr1.googlevideo.com/videoplayback?itag=137&mime=video%2Fmp4&clen=9000000', 'video/mp4', null, { initiator: 'https://video.example' })); await tick();
  assert.equal((await other.message({ action: 'media-list' }, other.pageSender(tab))).items.some(i => i.info), false, 'no YouTube note elsewhere');
});
test('YouTube: a blob player on a watch page shows the note even when no fragment was seen; the home page does not', async () => {
  const watch = { id: 7, url: 'https://www.youtube.com/watch?v=abc', title: 'x' }, home = { id: 7, url: 'https://www.youtube.com/', title: 'YouTube' };
  for (const [page, expectNote] of [[watch, true], [home, false]]) {
    const b = makeBackground({ openTabs: [page] });
    b.hooks.tabUpdated(7, { url: page.url }); await tick();
    await b.message({ action: 'media-dom', sources: [{ url: 'blob:', tag: 'video', title: 'x', playing: true, vis: 1 }] }, b.pageSender(page)); await tick();
    assert.equal((await b.message({ action: 'media-list' }, b.pageSender(page))).items.some(i => i.id === 'neodm-youtube'), expectNote, page.url);
  }
});

/* ---- context-target resolution ---- */
test('pickTarget: DOM url match, direct source, single playing, clear largest, else ambiguous', () => {
  const a = net('https://s.example/a.mp4', { size: 5 * MB }), b2 = net('https://s.example/b.mp4', { size: 80 * MB }), c = net('https://s.example/c.mp4', { size: 70 * MB, playing: true });
  assert.equal(S.pickTarget([a, b2, c], { found: true, url: 'https://s.example/a.mp4?_=1' }).item, a);
  assert.deepEqual(S.pickTarget([a], { found: true, url: 'https://other.example/x.mp4' }), { url: 'https://other.example/x.mp4' });
  assert.deepEqual(S.pickTarget([a], null, 'https://direct.example/y.mp4'), { url: 'https://direct.example/y.mp4' });
  assert.equal(S.pickTarget([a, b2, c], { found: true, blob: true }, 'blob:https://p/1').item, c, 'the single playing item');
  const live = net('https://s.example/live.mp4', { size: 1 * MB, playing: true, dom: true });
  assert.equal(S.pickTarget([live, b2], { found: true, blob: true }).item, b2, 'a blob player under the pointer is not the other, playing player with its own URL');
  assert.equal(S.pickTarget([live, b2], { found: false }).item, live, 'nothing under the pointer: the playing one');
  assert.equal(S.pickTarget([a, b2], { found: true, blob: true }).item, b2, 'largest complete file when clearly larger');
  assert.deepEqual(S.pickTarget([net('https://s.example/x.mp4', { size: 50 * MB }), net('https://s.example/y.mp4', { size: 45 * MB })], null), { ambiguous: true });
  assert.deepEqual(S.pickTarget([net('https://s.example/x.mp4', { playing: true }), net('https://s.example/y.mp4', { playing: true })], null), { ambiguous: true });
  assert.deepEqual(S.pickTarget([], null), { none: true });
  assert.deepEqual(S.pickTarget([{ info: true, id: 'neodm-youtube' }, net('https://s.example/i.png', { kind: 'image' })], null), { none: true }, 'info rows and images never count');
});
async function menuSetup(items, tabReply, extra = {}) {
  const b = makeBackground({ openTabs: [tab], tabReply: (id, msg) => (msg.type === 'neodm-context-target' ? tabReply : undefined), ...extra });
  for (const i of items) b.hooks.headers(response(i.url, i.type || 'video/mp4', i.size, { initiator: 'https://video.example' }));
  await tick(); return b;
}
test('right click: a blob player with two similar candidate files is not guessed; the ribbon is asked to open', async () => {
  const b = await menuSetup([{ url: 'https://cdn.example/a.mp4', size: 5 * MB }, { url: 'https://cdn.example/b.mp4', size: 6 * MB }], { found: true, blob: true, url: '', playing: true });
  await b.message({ action: 'media-dom', sources: [{ url: 'blob:', tag: 'video', playing: true, vis: 1 }] }, b.pageSender());
  await b.hooks.menu({ menuItemId: 'media', mediaType: 'video', srcUrl: 'blob:https://video.example/uuid', pageUrl: tab.url }, tab); await tick();
  assert.ok(b.sentToTabs.some(x => x.msg.type === 'neodm-context-target'));
  assert.equal(b.nativeRequests.filter(r => r.url).length, 0, 'two candidates and one blob player: nothing is guessed'); assert.ok(b.sentToTabs.some(x => x.msg.type === 'neodm-show-ribbon'));
});
test('right click: DOM url match is downloaded through the item (name from the page title)', async () => {
  const b = await menuSetup([{ url: 'https://cdn.example/a.mp4', size: 5 * MB }, { url: 'https://cdn.example/b.mp4', size: 6 * MB }], { found: true, url: 'https://cdn.example/a.mp4?rn=3', blob: false });
  await b.message({ action: 'media-dom', sources: [{ url: 'https://cdn.example/a.mp4', tag: 'video', playing: false, vis: 1 }] }, b.pageSender()); await tick();
  await b.hooks.menu({ menuItemId: 'page-video', pageUrl: tab.url }, tab); await tick();
  const req = b.nativeRequests.find(r => r.url); assert.equal(req.url, 'https://cdn.example/a.mp4'); assert.equal(req.referer, tab.url); assert.match(b.local.status, /Accepted/);
});
test('right click: srcUrl is used when nothing better exists; several candidates open the ribbon and ask the user', async () => {
  const direct = await menuSetup([], { found: false });
  await direct.hooks.menu({ menuItemId: 'media', mediaType: 'video', srcUrl: 'https://files.example/v.mp4', pageUrl: tab.url }, tab); await tick();
  assert.equal(direct.nativeRequests.find(r => r.url).url, 'https://files.example/v.mp4');
  const many = await menuSetup([{ url: 'https://cdn.example/a.mp4', size: 50 * MB }, { url: 'https://cdn.example/b.mp4', size: 45 * MB }], { found: false });
  await many.hooks.menu({ menuItemId: 'page-video', pageUrl: tab.url }, tab); await tick();
  assert.equal(many.nativeRequests.filter(r => r.url).length, 0);
  assert.ok(many.sentToTabs.some(x => x.tabId === 7 && x.msg.type === 'neodm-show-ribbon'));
  assert.match(many.local.status, /Choose the one you want in the NeoDM ribbon|choose/i);
  const none = await menuSetup([], { found: false });
  await none.hooks.menu({ menuItemId: 'page-video', pageUrl: tab.url }, tab); await tick();
  assert.match(none.local.status, /No downloadable video/);
});
test('right click: a lone sniffed file is chosen for a blob video; a clearly largest file wins', async () => {
  const lone = await menuSetup([{ url: 'https://cdn.example/only.mp4', size: 8 * MB }], { found: true, blob: true });
  await lone.hooks.menu({ menuItemId: 'media', mediaType: 'video', srcUrl: 'blob:https://video.example/u', pageUrl: tab.url }, tab); await tick();
  assert.equal(lone.nativeRequests.find(r => r.url).url, 'https://cdn.example/only.mp4');
  const big = await menuSetup([{ url: 'https://cdn.example/s.mp4', size: 3 * MB }, { url: 'https://cdn.example/l.mp4', size: 40 * MB }], { found: true, blob: true });
  await big.hooks.menu({ menuItemId: 'media', mediaType: 'video', srcUrl: 'blob:https://video.example/u', pageUrl: tab.url }, tab); await tick();
  assert.equal(big.nativeRequests.find(r => r.url).url, 'https://cdn.example/l.mp4');
});
test('video menu entry: created on install, startup and when the worker starts; visible only while the active tab has a video; follows activation and updates', async () => {
  const open = [{ id: 7, url: tab.url, title: 'x', active: true }];
  const b = makeBackground({ openTabs: open }); await tick();
  assert.ok(b.menus.some(m => m.id === 'page-video' && m.visible === false && m.contexts.join() === 'page,frame'), 'created hidden when the worker starts');
  const startCount = b.menus.filter(m => m.id === 'link').length;
  await b.hooks.startup(); await tick(); await b.hooks.install({}); await tick();
  assert.equal(b.menus.filter(m => m.id === 'link').length, startCount + 2, 'startup and install each rebuild once (removeAll first)');
  assert.deepEqual(json(b.menus.filter(m => m.id === 'media').at(-1).contexts), ['video', 'audio', 'image']);
  assert.equal(b.menuUpdates.length, 0);
  b.hooks.headers(response('https://cdn.example/a.mp4', 'video/mp4', 5 * MB, { initiator: 'https://video.example' })); await tick();
  assert.deepEqual(json(b.menuUpdates.at(-1)), { id: 'page-video', visible: true });
  await b.message({ action: 'pause-set', scope: 'all', on: true }, b.sender); await tick(12);
  assert.deepEqual(json(b.menuUpdates.at(-1)), { id: 'page-video', visible: false }, 'a pause hides it');
  await b.message({ action: 'pause-set', scope: 'all', on: false }, b.sender); await tick(12);
  assert.equal(b.menuUpdates.at(-1).visible, true);
  open[0] = { id: 9, url: 'https://other.example/', title: 'y', active: true }; b.hooks.tabActivated({ tabId: 9 }); await tick(12);
  assert.equal(b.menuUpdates.at(-1).visible, false, 'another tab without media');
  open[0] = { id: 7, url: tab.url, title: 'x', active: true }; b.hooks.tabUpdated(7, { status: 'complete' }); await tick(12);
  assert.equal(b.menuUpdates.at(-1).visible, true);
});

/* ---- show on page: worker plumbing ---- */
test('popup-locate forwards the outline request to the tab (popup only), with the blob hint for paired items', async () => {
  const b = makeBackground();
  b.hooks.headers(response('https://cdn.example/a.mp4', 'video/mp4', 5 * MB, { initiator: 'https://video.example' })); await tick();
  await b.message({ action: 'media-dom', sources: [{ url: 'blob:', tag: 'video', playing: true, vis: 1 }] }, b.pageSender()); await tick();
  b.sentToTabs.length = 0;
  await b.message({ action: 'popup-locate', tabId: 7, id: 'https://cdn.example/a.mp4', on: true, scroll: true }); await tick();
  assert.deepEqual(b.sentToTabs.at(-1), { tabId: 7, msg: { type: 'neodm-highlight', id: 'https://cdn.example/a.mp4', on: true, scroll: true, blob: true } });
  await b.message({ action: 'popup-locate', tabId: 7, id: 'https://cdn.example/a.mp4', on: false }); await tick();
  assert.equal(b.sentToTabs.at(-1).msg.on, false);
  b.sentToTabs.length = 0;
  assert.equal(b.hooks.message({ action: 'popup-locate', tabId: 7, id: 'x', on: true }, b.pageSender(), () => {}), false, 'a web page cannot trigger it');
  assert.equal(b.sentToTabs.length, 0);
});
test('media-dom ignores nonsense: non-array, oversize thumbnails, non-http posters', async () => {
  const b = makeBackground();
  await b.message({ action: 'media-dom', sources: 'nope' }, b.pageSender());
  await b.message({ action: 'media-dom', sources: [{ url: 'https://a.example/v.mp4', tag: 'video', thumb: 'data:image/jpeg;base64,' + 'A'.repeat(30000), poster: 'file:///etc/passwd', title: 't'.repeat(900) }] }, b.pageSender()); await tick();
  const it = json(b.sessionData['neodm.media.7']).items[0];
  assert.equal(it.thumb, undefined); assert.equal(it.poster, undefined); assert.equal(Array.from(it.title).length, 200);
});

/* ---- content script (jsdom) ---- */
function page({ items = [] } = {}) {
  const dom = new JSDOM('<!doctype html><title>Page Title</title><meta property="og:title" content="OG Title"><body><div id="c1"><h2>Card One</h2><video id="v1" src="https://files.example/one.mp4"></video></div><div id="c2"><h2>Card Two</h2><video id="v2" src="https://files.example/two.mp4" poster="https://files.example/two.jpg"></video></div><div id="cover" style="position:absolute"></div></body>', { url: 'https://page.example/watch', runScripts: 'outside-only', pretendToBeVisual: true }), w = dom.window;
  const sent = [], listeners = [];
  w.chrome = { runtime: { sendMessage: async m => { sent.push(m); return m.action === 'media-list' ? { items } : { ok: true }; }, onMessage: { addListener: f => listeners.push(f) } }, storage: { local: { get: async () => ({}) }, onChanged: { addListener() {} } } };
  const canvas = { grabs: 0, data: 'data:image/jpeg;base64,' + 'B'.repeat(800), fail: false };
  w.HTMLCanvasElement.prototype.getContext = function () { return { drawImage() { canvas.grabs++; if (canvas.fail) throw new Error('tainted'); } }; };
  w.HTMLCanvasElement.prototype.toDataURL = function () { return canvas.data; };
  const rect = (el, r) => { el.getBoundingClientRect = () => ({ left: r[0], top: r[1], right: r[0] + r[2], bottom: r[1] + r[3], width: r[2], height: r[3] }); };
  const state = (el, o) => { for (const [k, v] of Object.entries(o)) Object.defineProperty(el, k, { value: v, configurable: true }); };
  withI18n(w); w.eval(source('sniff.js')); w.eval(source('content.js'));
  const send = (m) => { let reply; for (const f of listeners) f(m, {}, r => { reply = r; }); return reply; };
  return { w, d: w.document, sent, send, canvas, rect, state, media: () => sent.filter(m => m.action === 'media-dom') };
}
test('content script reports title, duration, size, poster, playing state, visibility and a small frame grab per element', async () => {
  const p = page(); await tick();
  p.state(p.d.getElementById('v1'), { paused: false, ended: false, readyState: 4, duration: 205.34, videoWidth: 1280, videoHeight: 720 });
  p.rect(p.d.getElementById('v1'), [0, 100, 640, 360]); p.rect(p.d.getElementById('v2'), [0, 5000, 640, 360]);
  p.w.__neodmRibbon.refresh();
  const last = p.media().at(-1).sources, one = last.find(s => s.url.endsWith('one.mp4')), two = last.find(s => s.url.endsWith('two.mp4'));
  assert.equal(one.title, 'Card One'); assert.equal(one.dur, 205.3); assert.equal(one.w, 1280); assert.equal(one.h, 720); assert.equal(one.playing, true); assert.equal(one.vis, 1); assert.match(one.thumb, /^data:image\/jpeg;base64,/);
  assert.ok(one.thumb.length <= 20 * 1024);
  assert.equal(two.title, 'Card Two'); assert.equal(two.playing, false); assert.equal(two.vis, 0); assert.equal(two.poster, 'https://files.example/two.jpg'); assert.equal(two.thumb, undefined, 'not ready: no frame, poster is reported');
  const before = p.media().length; p.w.__neodmRibbon.refresh(); assert.equal(p.media().length, before, 'an unchanged report is not re-sent');
  const clean = S.cleanMeta(one); assert.equal(clean.thumb, one.thumb);
});
test('content script: a tainted canvas or an oversize frame falls back to the poster; the main playing video without a heading uses og:title', async () => {
  const p = page(); p.canvas.fail = true;
  const v2 = p.d.getElementById('v2'); p.state(v2, { paused: false, ended: false, readyState: 4, duration: 10, videoWidth: 640, videoHeight: 360 });
  p.d.querySelector('#c2 h2').remove(); p.rect(v2, [0, 0, 640, 360]);
  p.w.__neodmRibbon.refresh();
  let s = p.media().at(-1).sources.find(x => x.url.endsWith('two.mp4'));
  assert.equal(s.thumb, undefined); assert.equal(s.poster, 'https://files.example/two.jpg'); assert.equal(s.title, 'OG Title');
  const q = page(); q.canvas.data = 'data:image/jpeg;base64,' + 'C'.repeat(25000);
  const v1 = q.d.getElementById('v1'); q.state(v1, { readyState: 4, duration: 10, videoWidth: 640, videoHeight: 360 }); q.rect(v1, [0, 0, 10, 10]);
  q.w.__neodmRibbon.refresh();
  assert.equal(q.media().at(-1).sources.find(x => x.url.endsWith('one.mp4')).thumb, undefined, 'a frame larger than 20 KB is never sent');
  assert.ok(q.canvas.grabs <= 4 * 3 + 4, 'bounded attempts');
});
test('show on page: outline 3px accent, smooth scroll only when off screen, restored on off, auto-removed after 3 s', async () => {
  const p = page(); await tick();
  const v2 = p.d.getElementById('v2'); let scrolled = 0; v2.scrollIntoView = o => { scrolled++; assert.equal(o.behavior, 'smooth'); };
  p.rect(v2, [0, 5000, 640, 360]); v2.style.outline = '1px dotted red';
  const timers = []; const realSet = p.w.setTimeout; p.w.setTimeout = (f, ms) => { timers.push({ f, ms }); return timers.length; };
  p.send({ type: 'neodm-highlight', id: 'https://files.example/two.mp4', on: true, scroll: true });
  assert.equal(v2.style.getPropertyValue('outline'), '3px solid #39b995'); assert.equal(v2.style.getPropertyPriority('outline'), 'important'); assert.equal(scrolled, 1);
  assert.equal(timers.at(-1).ms, 3000);
  p.send({ type: 'neodm-highlight', id: 'https://files.example/two.mp4', on: true, scroll: true }); assert.equal(scrolled, 2, 'still off screen: still scrolls'); p.rect(v2, [0, 10, 640, 360]);
  p.send({ type: 'neodm-highlight', id: 'https://files.example/two.mp4', on: true, scroll: true }); assert.equal(scrolled, 2, 'on screen: no scroll');
  p.send({ type: 'neodm-highlight', id: 'https://files.example/two.mp4', on: false });
  assert.equal(v2.style.getPropertyValue('outline'), '1px dotted red'); assert.equal(v2.style.getPropertyPriority('outline'), '');
  p.send({ type: 'neodm-highlight', id: 'https://files.example/one.mp4', on: true, scroll: false });
  const v1 = p.d.getElementById('v1'); assert.equal(v1.style.getPropertyValue('outline'), '3px solid #39b995');
  timers.at(-1).f(); assert.equal(v1.style.getPropertyValue('outline'), '', 'the 3 s timeout removes it');
  assert.equal(p.send({ type: 'neodm-highlight', id: 'https://nowhere.example/x.mp4', on: true }), undefined);
  p.w.setTimeout = realSet;
});
test('context target: finds the video under the pointer even beneath an overlay; blob videos are reported as blob', async () => {
  const p = page(); await tick();
  const v1 = p.d.getElementById('v1'), v2 = p.d.getElementById('v2'), cover = p.d.getElementById('cover');
  p.rect(v1, [0, 0, 300, 200]); p.rect(v2, [0, 300, 300, 200]);
  p.d.elementsFromPoint = (x, y) => (y < 250 ? [cover, v1, p.d.body] : [cover, v2, p.d.body]);
  assert.deepEqual(json(p.send({ type: 'neodm-context-target' })), { found: false }, 'no right click yet');
  cover.dispatchEvent(new p.w.MouseEvent('contextmenu', { bubbles: true, clientX: 50, clientY: 350 }));
  let r = json(p.send({ type: 'neodm-context-target' })); assert.deepEqual([r.found, r.url, r.blob, r.tag], [true, 'https://files.example/two.mp4', false, 'video']);
  cover.dispatchEvent(new p.w.MouseEvent('contextmenu', { bubbles: true, clientX: 50, clientY: 50 }));
  assert.equal(p.send({ type: 'neodm-context-target' }).url, 'https://files.example/one.mp4');
  p.d.elementsFromPoint = () => [cover, p.d.body]; // the page hides it from hit testing: the bounding boxes decide
  cover.dispatchEvent(new p.w.MouseEvent('contextmenu', { bubbles: true, clientX: 10, clientY: 310 }));
  assert.equal(p.send({ type: 'neodm-context-target' }).url, 'https://files.example/two.mp4');
  cover.dispatchEvent(new p.w.MouseEvent('contextmenu', { bubbles: true, clientX: 900, clientY: 900 }));
  assert.equal(p.send({ type: 'neodm-context-target' }).found, false);
  const blob = p.d.createElement('video'); Object.defineProperty(blob, 'currentSrc', { value: 'blob:https://page.example/z' }); p.d.body.append(blob); p.rect(blob, [500, 0, 100, 100]);
  p.d.elementsFromPoint = () => [blob];
  cover.dispatchEvent(new p.w.MouseEvent('contextmenu', { bubbles: true, clientX: 510, clientY: 10 }));
  r = json(p.send({ type: 'neodm-context-target' })); assert.deepEqual([r.found, r.blob, r.url], [true, true, '']);
});
test('ribbon: rows show title, thumbnail, details and the badge; hovering a row outlines the video; neodm-show-ribbon brings it back after a dismissal', async () => {
  const items = [net('https://files.example/one.mp4', { title: 'Card One', w: 1280, h: 720, dur: 205, size: 45 * MB, playing: true, dom: true, vis: 1, thumb: 'data:image/jpeg;base64,AAAA' }), net('https://files.example/two.mp4', { size: 3 * MB })];
  const p = page({ items }); await tick();
  const roots = []; // ribbon root is closed: reach it through attachShadow of a fresh page
  const dom2 = new JSDOM('<!doctype html><body><video id="v1" src="https://files.example/one.mp4"></video></body>', { url: 'https://page.example/x', runScripts: 'outside-only', pretendToBeVisual: true }), w = dom2.window;
  const orig = w.Element.prototype.attachShadow; w.Element.prototype.attachShadow = function (i) { const r = orig.call(this, i); roots.push(r); return r; };
  const ls = []; w.chrome = { runtime: { sendMessage: async m => (m.action === 'media-list' ? { items } : { ok: true }), onMessage: { addListener: f => ls.push(f) } }, storage: { local: { get: async () => ({}) }, onChanged: { addListener() {} } } };
  withI18n(w); w.eval(source('sniff.js')); w.eval(source('content.js')); await tick();
  const ui = roots.at(-1), rows = [...ui.querySelectorAll('.row')];
  assert.equal(rows.length, 2); assert.equal(rows[0].querySelector('.t').textContent, 'Card One'); assert.equal(rows[0].querySelector('.m').textContent, 'Video 1280x720 · 03:25 · 45.0 MB');
  assert.equal(rows[0].querySelector('.live').textContent, 'Playing now'); assert.equal(rows[1].querySelector('.live'), null); assert.equal(rows[1].querySelector('.t').textContent, 'two.mp4');
  assert.equal(rows[0].querySelector('img').src, 'data:image/jpeg;base64,AAAA'); assert.ok(rows[1].querySelector('.neo-thumb video'), 'a file without page info gets a preview element');
  assert.equal(rows[1].querySelector('video').preload, 'metadata'); assert.equal(rows[1].querySelector('video').muted, true); assert.equal(rows[1].querySelector('video').src, 'https://files.example/two.mp4');
  rows[0].dispatchEvent(new w.Event('mouseenter')); assert.equal(w.document.getElementById('v1').style.getPropertyValue('outline'), '3px solid #39b995');
  rows[0].dispatchEvent(new w.Event('mouseleave')); assert.equal(w.document.getElementById('v1').style.getPropertyValue('outline'), '');
  ui.querySelector('button.x').click(); assert.equal(w.document.querySelector('[data-neodm-ribbon]'), null);
  ls.forEach(f => f({ type: 'neodm-show-ribbon' }, {}, () => {})); assert.ok(w.document.querySelector('[data-neodm-ribbon]'), 'the dismissed ribbon is shown again');
  const note = [{ id: 'neodm-youtube', kind: 'info', info: true, text: 'Protected note' }];
  ls.forEach(f => f({ type: 'neodm-media', items: note })); assert.equal(roots.at(-1).querySelector('.note').textContent, 'Protected note'); assert.equal(roots.at(-1).querySelectorAll('button.dl').length, 0);
});

/* ---- popup (jsdom) ---- */
async function popup(items, lang = 'en') {
  const dom = new JSDOM(source('popup.html'), { runScripts: 'outside-only', url: 'chrome-extension://ext-id/popup.html' }), w = dom.window;
  const sent = [];
  w.chrome = {
    storage: { local: { get: async () => ({}), set: async () => {} }, session: { get: async () => ({}) } },
    tabs: { query: async () => [{ id: 7, url: 'https://video.example/watch', title: 'Watch' }] },
    runtime: { sendMessage: async m => { sent.push(m); return m.action === 'popup-media' ? { items } : { ok: true }; }, getManifest: () => ({ version: '1.5.1' }) },
    permissions: { request: async () => true, remove: async () => true },
  };
  const played = [];
  w.HTMLMediaElement.prototype.play = function () { played.push(this); Object.defineProperty(this, 'paused', { value: false, configurable: true }); return Promise.resolve(); };
  w.HTMLMediaElement.prototype.pause = function () { Object.defineProperty(this, 'paused', { value: true, configurable: true }); };
  withI18n(w, lang); w.eval(source('sniff.js')); w.eval(source('popup.js')); await tick(14);
  return { w, doc: w.document, sent, played };
}
test('popup rows: title with full-text tooltip, details line, badge, host, preview; playing first; the YouTube-style note has no button', async () => {
  const items = [net('https://files.example/one.mp4', { title: 'A very long title '.repeat(10), w: 1280, h: 720, dur: 205, size: 45.2 * MB, playing: true, dom: true, vis: 1 }), net('https://files.example/dir/two%20file.mp4', { size: 3 * MB }), { id: 'neodm-youtube', kind: 'info', info: true, text: 'Protected note' }];
  const { doc } = await popup(items);
  const rows = [...doc.querySelectorAll('#mediaList li.media')];
  assert.equal(rows.length, 2); assert.equal(doc.querySelectorAll('#mediaList li').length, 3);
  assert.equal(rows[0].querySelector('.title').title, items[0].title.slice(0, 200).trim()); assert.equal(rows[0].querySelector('.meta').textContent, 'Video 1280x720 · 03:25 · 45.2 MB');
  assert.equal(rows[0].querySelector('.badge').textContent, 'Playing now'); assert.equal(rows[0].querySelector('.host').textContent, 'files.example');
  assert.equal(rows[1].querySelector('.title').textContent, 'two file.mp4'); assert.equal(rows[1].querySelector('.badge'), null);
  assert.ok(rows[1].querySelector('.neo-thumb video'));
  assert.equal(doc.querySelector('#mediaList .note').textContent, 'Protected note'); assert.equal(doc.querySelector('#mediaNone').hidden, true);
  assert.equal(doc.querySelectorAll('#mediaList button.link').length, 1, '"Show on page" only for rows matched to a page element');
  const css = fs.readFileSync(path.join(root, 'extension/popup.css'), 'utf8'); assert.match(css, /body\{[^}]*width:400px/);
});
test('popup: hover/focus asks the worker to outline the video and renews it; leaving or closing removes it; previews play one at a time', async () => {
  const items = [net('https://files.example/one.mp4', { dom: true, vis: 1, title: 'One' }), net('https://files.example/two.mp4', { title: 'Two' })];
  const { w, doc, sent, played } = await popup(items);
  const rows = [...doc.querySelectorAll('#mediaList li.media')];
  rows[0].dispatchEvent(new w.Event('mouseenter')); await tick();
  assert.deepEqual(json(sent.filter(m => m.action === 'popup-locate').at(-1)), { action: 'popup-locate', tabId: 7, id: 'https://files.example/one.mp4', on: true, scroll: true });
  rows[0].dispatchEvent(new w.Event('mouseleave')); await tick();
  assert.equal(sent.filter(m => m.action === 'popup-locate').at(-1).on, false);
  rows[1].dispatchEvent(new w.Event('focusin')); await tick(); assert.equal(sent.filter(m => m.action === 'popup-locate').at(-1).id, 'https://files.example/two.mp4');
  w.dispatchEvent(new w.Event('pagehide')); await tick(); assert.equal(sent.filter(m => m.action === 'popup-locate').at(-1).on, false, 'closing the popup removes the outline');
  // previews
  const [t1, t2] = rows.map(r => r.querySelector('.neo-thumb'));
  t1.click(); assert.equal(played.length, 1); assert.equal(played[0].muted, false, 'unmuted, on a user click');
  t2.click(); assert.equal(played.length, 2); assert.equal(played[0].paused, true, 'starting another preview stops the first'); assert.equal(played[1].muted, false);
  t2.click(); assert.equal(played[1].paused, true, 'second click stops it');
  t1.click(); w.dispatchEvent(new w.Event('pagehide')); assert.equal(played.at(-1).paused, true, 'closing the popup stops the preview');
});
test('popup: a preview that cannot load is hidden, a loaded one fills in the length and size', async () => {
  const items = [net('https://files.example/bad.mp4', { size: 3 * MB }), net('https://files.example/good.mp4', { size: 2 * MB })];
  const { w, doc } = await popup(items);
  const rows = [...doc.querySelectorAll('#mediaList li.media')];
  const bad = rows[0].querySelector('.neo-thumb video'); bad.dispatchEvent(new w.Event('error'));
  assert.equal(rows[0].querySelector('.neo-thumb').hidden, true);
  const good = rows[1].querySelector('.neo-thumb video'); Object.defineProperty(good, 'duration', { value: 125 }); Object.defineProperty(good, 'videoWidth', { value: 640 }); Object.defineProperty(good, 'videoHeight', { value: 360 });
  good.dispatchEvent(new w.Event('loadedmetadata'));
  assert.equal(rows[1].querySelector('.meta').textContent, 'Video 640x360 · 02:05 · 2.0 MB');
});
test('popup in Arabic: rows keep the right-to-left document and translated badge', async () => {
  const ar = JSON.parse(fs.readFileSync(path.join(root, 'extension/_locales/ar/messages.json'), 'utf8'));
  const { doc } = await popup([net('https://files.example/one.mp4', { playing: true, dom: true, vis: 1, title: 'x' })], 'ar');
  assert.equal(doc.documentElement.dir, 'rtl'); assert.equal(doc.querySelector('.badge').textContent, ar.media_badge_playing.message);
});

/* ---- texts ---- */
test('every new message exists in all ten locales with a real translation and the NeoDM name where English has it', () => {
  const keys = ['menu_video', 'media_badge_playing', 'media_preview_play', 'media_preview_stop', 'media_show_on_page', 'media_youtube', 'status_choose_ribbon', 'err_no_video'];
  const dir = path.join(root, 'extension/_locales'), langs = fs.readdirSync(dir);
  assert.equal(langs.length, 10);
  const en = JSON.parse(fs.readFileSync(path.join(dir, 'en/messages.json'), 'utf8'));
  for (const l of langs) {
    const m = JSON.parse(fs.readFileSync(path.join(dir, l, 'messages.json'), 'utf8'));
    for (const k of keys) {
      assert.ok(m[k] && typeof m[k].message === 'string' && m[k].message.length >= 2, `${l}.${k}`);
      if (l !== 'en') assert.notEqual(m[k].message, en[k].message, `${l}.${k} is translated`);
      if (/NeoDM/.test(en[k].message)) assert.match(m[k].message, /NeoDM/, `${l}.${k} keeps the product name`);
    }
  }
  assert.doesNotMatch(en.media_youtube.message, /\u2014/);
});
