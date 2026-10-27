'use strict';
/* 1.5.2: files are linked to the video element that fetched them, the list survives scrolling and pushState, one bar per video, and the right-click download never sends a web page. */
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
const tab = { id: 7, url: 'https://feed.example/home', title: 'Feed' };
const b64url = obj => Buffer.from(JSON.stringify(obj)).toString('base64url');
const fb = (name, o = {}) => `https://video-xyz1-1.fbcdn.net/o1/v/t42/${name}.mp4?${o.efg ? 'efg=' + o.efg + '&' : ''}_nc_cat=1&bytestart=0&byteend=999999`;
const stored = b => json(b.sessionData['neodm.media.7']);
const ev = (key, type, t) => ({ key, type, t });
/** Runs the timer that decides owners when a file's one-second window has closed (the harness collects timers instead of waiting). */
const settle = async b => { b.advance(S.OWN_AFTER + S.SETTLE_SLACK + 100); for (const t of b.timers) if (t.ms === S.OWN_AFTER + S.SETTLE_SLACK + 60 && !t.ran) { t.ran = true; t.fn(); } await tick(12); };

test('version 1.5.x in props, manifest, changelog and README', () => {
  const version = /<Version>([^<]+)<\/Version>/.exec(fs.readFileSync(path.join(root, 'Directory.Build.props'), 'utf8'))[1];
  assert.match(version, /^1\.5\.\d+$/);
  for (const tag of ['AssemblyVersion', 'FileVersion']) assert.match(fs.readFileSync(path.join(root, 'Directory.Build.props'), 'utf8'), new RegExp(`<${tag}>${version.replace(/\./g, '\\.')}\\.0</${tag}>`));
  assert.equal(JSON.parse(fs.readFileSync(path.join(root, 'extension/manifest.json'), 'utf8')).version, version);
  const log = fs.readFileSync(path.join(root, 'CHANGELOG.md'), 'utf8');
  assert.equal(/^## \[([^\]]+)\]/m.exec(log)[1], version);
  assert.ok(log.includes('## [1.5.2]'), 'the 1.5.2 entry is still in the changelog');
  const readme = fs.readFileSync(path.join(root, 'README.md'), 'utf8');
  assert.match(readme, new RegExp('Current version: ' + version.replace(/\./g, '\\.'))); assert.match(readme, /^\| 1\.5\.2 \| /m);
  assert.doesNotMatch(log.slice(0, log.indexOf('## [1.5.1]')), /\u2014/, 'no em dashes in the new entries');
});

/* ---- ownership ---- */
test('ownership: the element whose events are closest to the new file (2.5 s before to 1 s after) gets it once the window has closed; a tie goes to the playing element', () => {
  const T = 1_000_000;
  const mk = () => ({ items: [], els: {}, evt: {} });
  const owner = (st, at = T + 1400) => { S.assignOwners(st, at); return st.items[0].owner; };
  // closest wins
  let st = mk(); S.noteEvents(st, [ev('a', 'loadstart', T - 2000), ev('b', 'progress', T - 400)], T - 100);
  S.upsert(st, net('https://cdn.example/x.mp4'), T);
  assert.equal(st.items[0].owner, undefined, 'not decided while the window is open'); assert.equal(owner(st), 'b');
  // an element that reacts after the response beats one that was only active before it
  st = mk(); S.noteEvents(st, [ev('before', 'progress', T - 900)], T - 800); S.upsert(st, net('https://cdn.example/x.mp4'), T);
  S.noteEvents(st, [ev('after', 'loadstart', T + 120)], T + 130); assert.equal(st.items[0].owner, undefined); assert.equal(owner(st), 'after');
  // too early: unowned; an event inside the 1 s window claims it, one later than that does not
  st = mk(); S.noteEvents(st, [ev('a', 'loadstart', T - 3000)], T - 2900); S.upsert(st, net('https://cdn.example/x.mp4'), T);
  assert.equal(owner(st), undefined);
  st = mk(); S.upsert(st, net('https://cdn.example/x.mp4'), T); S.noteEvents(st, [ev('a', 'progress', T + 800)], T + 800); assert.equal(owner(st), 'a');
  st = mk(); S.upsert(st, net('https://cdn.example/x.mp4'), T); S.noteEvents(st, [ev('a', 'progress', T + 1500)], T + 1500);
  assert.equal(st.items[0].owner, undefined, 'more than 1 s after: not linked');
  // the decision waits for events still on their way from the page
  st = mk(); S.noteEvents(st, [ev('a', 'progress', T - 100)], T - 90); S.upsert(st, net('https://cdn.example/x.mp4'), T); S.assignOwners(st, T + 1200); assert.equal(st.items[0].owner, undefined); S.assignOwners(st, T + 1301); assert.equal(st.items[0].owner, 'a');
  // tie -> playing
  st = mk(); S.noteEvents(st, [ev('idle', 'progress', T - 500), ev('live', 'progress', T - 500)], T - 400); st.els.live.playing = true;
  S.upsert(st, net('https://cdn.example/x.mp4'), T); assert.equal(owner(st), 'live');
  // frequent timeupdate counts for less than a load event
  st = mk(); S.noteEvents(st, [ev('ticker', 'timeupdate', T - 50), ev('loader', 'loadstart', T - 1200)], T - 40);
  S.upsert(st, net('https://cdn.example/x.mp4'), T); assert.equal(owner(st), 'loader');
  st = mk(); S.noteEvents(st, [ev('ticker', 'timeupdate', T - 50)], T - 40); S.upsert(st, net('https://cdn.example/x.mp4'), T); assert.equal(owner(st), 'ticker', 'alone it still counts');
  // never reassigned
  st = mk(); S.noteEvents(st, [ev('a', 'progress', T - 100)], T - 90); S.upsert(st, net('https://cdn.example/x.mp4'), T); assert.equal(owner(st), 'a');
  S.noteEvents(st, [ev('b', 'progress', T + 1210)], T + 1220); S.upsert(st, net('https://cdn.example/x.mp4'), T + 1230); assert.equal(st.items[0].owner, 'a');
  // garbage is ignored
  st = mk(); assert.equal(S.noteEvents(st, [{ key: '../x', type: 'progress', t: 1 }, { key: 'ok', type: 'click', t: 1 }, null, 5], T), 'mem'); assert.deepEqual(Object.keys(st.els), []);
});
test('worker: a file is linked to the element that fetched it and takes that element\'s metadata (blob video, no file URL on the element)', async () => {
  const b = makeBackground(); const now = Date.now();
  await b.message({ action: 'media-event', events: [ev('k1', 'loadstart', now - 3000), ev('k2', 'progress', now - 80)] }, b.pageSender(tab));
  b.hooks.headers(response(fb('one'), 'video/mp4', 20 * MB, { statusCode: 200, initiator: 'https://feed.example' })); await tick();
  assert.equal(stored(b).items[0].owner, undefined, 'decided when the window has closed'); await settle(b);
  assert.equal(stored(b).items[0].owner, 'k2');
  await b.message({ action: 'media-dom', sources: [
    { key: 'k1', url: 'blob:https://feed.example/1', tag: 'video', title: 'Post one', dur: 30, w: 640, h: 360, vis: 0.5 },
    { key: 'k2', url: 'blob:https://feed.example/2', tag: 'video', title: 'Post two', dur: 61, w: 1280, h: 720, playing: true, vis: 1, thumb: 'data:image/jpeg;base64,AAAA', poster: 'https://cdn.example/p2.jpg' },
  ] }, b.pageSender(tab)); await tick();
  const it = stored(b).items[0];
  assert.deepEqual([it.title, it.dur, it.w, it.h, it.playing, it.vis, it.thumb, it.poster], ['Post two', 61, 1280, 720, true, 1, 'data:image/jpeg;base64,AAAA', 'https://cdn.example/p2.jpg']);
  const rows = (await b.message({ action: 'popup-media', tabId: 7 })).items;
  assert.equal(rows.length, 1); assert.equal(rows[0].title, 'Post two'); assert.equal(rows[0].owner, 'k2');
  // another file of a different element, later
  await b.message({ action: 'media-event', events: [ev('k1', 'progress', b.now() - 20)] }, b.pageSender(tab));
  b.hooks.headers(response(fb('two'), 'video/mp4', 8 * MB + 4321, { statusCode: 200 })); await tick(); await settle(b);
  assert.deepEqual(stored(b).items.map(i => [i.url.includes('/two.') ? 'two' : 'one', i.owner]).sort(), [['one', 'k2'], ['two', 'k1']]);
});
test('efg: audio-only, video-only and progressive files are labelled from vencode_tag; unreadable values are ignored', () => {
  const insp = u => S.inspect({ url: u, tabId: 1, method: 'GET', statusCode: 200, responseHeaders: [{ name: 'Content-Type', value: 'video/mp4' }, { name: 'Content-Length', value: String(5 * MB + 777) }] }).item;
  const audio = insp(fb('a', { efg: b64url({ vencode_tag: 'dash_ln_heaac_vbr3_audio' }) }));
  assert.deepEqual([audio.kind, audio.track, audio.complete], ['audio', 'audio-only', false]);
  const video = insp(fb('v', { efg: b64url({ vencode_tag: 'dash_h264-basic-gen2_720p' }) }));
  assert.deepEqual([video.kind, video.track, video.quality], ['video', 'video-only', '720p']);
  const prog = insp(fb('p', { efg: b64url({ vencode_tag: 'progressive_h264-basic-gen2_360p' }) }));
  assert.deepEqual([prog.kind, prog.track, prog.quality, prog.complete], ['video', 'file', '360p', true]);
  for (const bad of ['!!!', 'e30', b64url({ other: 1 }), 'A'.repeat(3000)]) { const x = insp(fb('b', { efg: bad })); assert.equal(x.track, 'file'); assert.equal(x.kind, 'video'); }
  assert.equal(S.typeOfItem(audio), 'audio');
});

/* ---- the list survives scrolling, element removal and pushState ---- */
test('persistence: pushState keeps files, elements and owners; a page load (top-level document response) clears them; YouTube keeps clearing on pushState', async () => {
  const b = makeBackground(); const now = Date.now();
  b.hooks.tabUpdated(7, { url: 'https://feed.example/home' }); await tick();
  await b.message({ action: 'media-event', events: [ev('k1', 'progress', now - 50)] }, b.pageSender(tab));
  b.hooks.headers(response(fb('one'), 'video/mp4', 20 * MB, { initiator: 'https://feed.example' })); await tick(); await settle(b);
  for (const url of ['https://feed.example/watch/?v=2', 'https://feed.example/photo/3', 'https://feed.example/home#x']) { b.hooks.tabUpdated(7, { status: 'loading', url }); b.hooks.tabUpdated(7, { status: 'complete' }); await tick(); } // Chrome reports pushState as loading + complete
  let s = stored(b); assert.equal(s.items.length, 1); assert.equal(s.items[0].owner, 'k1'); assert.ok(s.els.k1);
  b.navigate('https://feed.example/file.zip', { responseHeaders: [{ name: 'Content-Disposition', value: 'attachment; filename=a.zip' }] }); b.navigate('https://feed.example/moved', { statusCode: 302 }); b.navigate('https://feed.example/sub', { type: 'sub_frame' }); await tick();
  assert.equal(stored(b).items.length, 1, 'a download, a redirect and a frame are not page loads');
  b.hooks.tabUpdated(7, { status: 'loading', url: 'https://feed.example/next' }); b.navigate('https://feed.example/next'); await tick();
  s = stored(b); assert.deepEqual([s.items.length, Object.keys(s.els).length, s.page], [0, 0, 'https://feed.example/next'], 'a new document starts a new list');
  b.hooks.headers(response(fb('two'), 'video/mp4', 20 * MB, { initiator: 'https://feed.example' })); await tick();
  b.navigate('https://feed.example/next', { statusCode: 304, responseHeaders: [] }); await tick();
  assert.equal(stored(b).items.length, 0, 'a reload (same address) is a page load too');
  const yt = makeBackground(); yt.hooks.tabUpdated(7, { url: 'https://www.youtube.com/watch?v=1' }); await tick();
  yt.hooks.headers(response('https://cdn.example/a.mp4', 'video/mp4', 5 * MB, { initiator: 'https://www.youtube.com' })); await tick();
  yt.hooks.tabUpdated(7, { url: 'https://www.youtube.com/watch?v=2' }); await tick();
  assert.equal(stored(yt).items.length, 0, 'the YouTube player swaps videos through pushState');
});
test('virtualization: a removed element keeps its file; the same element or the same video in a new element takes it back', () => {
  const st = { items: [], els: {}, evt: {} }, T = 5_000_000;
  S.noteEvents(st, [ev('e1', 'progress', T - 50)], T - 40); S.upsert(st, net('https://cdn.example/x.mp4', { size: 40 * MB }), T); S.assignOwners(st, T + 1400);
  const vid = (key, extra = {}) => ({ key, url: 'blob:https://feed.example/' + key, tag: 'video', title: 'Dog video', dur: 42.4, w: 640, h: 360, playing: true, vis: 1, ...extra });
  S.domReport(st, [vid('e1')], T + 1500);
  assert.equal(st.items[0].playing, true);
  S.domReport(st, [], T + 1600); // scrolled away: the feed removed the element
  assert.equal(st.items.length, 1, 'the file stays'); assert.equal(st.items[0].owner, 'e1'); assert.equal(st.els.e1.live, false); assert.equal(st.items[0].playing, false); assert.equal(st.items[0].vis, 0);
  S.domReport(st, [vid('e1', { playing: false, vis: 0.5 })], T + 1700); // same node comes back
  assert.equal(st.els.e1.live, true); assert.equal(st.items[0].vis, 0.5); assert.equal(st.items[0].owner, 'e1');
  S.domReport(st, [], T + 1800);
  S.domReport(st, [vid('e9')], T + 1900); // a new node for the same video (same title, length)
  assert.equal(st.items[0].owner, 'e9'); assert.equal(st.els.e1, undefined); assert.equal(st.items.length, 1);
  S.domReport(st, [vid('e9'), vid('e10')], T + 2000); assert.equal(st.items.length, 1, 'a second live element with the same signature does not steal it'); assert.equal(st.items[0].owner, 'e9');
  assert.equal(S.elSig({ title: 'x' }, 'blob:abc'), '', 'too little to recognise an element again');
});
test('eviction: 200 files per page; the oldest unowned go first; owned files of live or recent elements are never dropped', () => {
  const st = { items: [], els: { live: { key: 'live', live: true, seen: 1 }, recent: { key: 'recent', live: false, seen: 10_000_000 - 60_000 }, stale: { key: 'stale', live: false, seen: 1 } }, evt: {} };
  const now = 10_000_000;
  S.upsert(st, net('https://cdn.example/live.mp4', { owner: 'live' }), now - 9_000_000);
  S.upsert(st, net('https://cdn.example/recent.mp4', { owner: 'recent' }), now - 8_000_000);
  S.upsert(st, net('https://cdn.example/stale.mp4', { owner: 'stale' }), now - 7_000_000);
  for (let i = 0; i < S.ITEMS_MAX + 10; i++) S.upsert(st, net(`https://cdn${i}.example/f${i}.mp4`), now - 6_000_000 + i * 1000);
  const ids = st.items.map(i => i.id);
  assert.equal(st.items.length, S.ITEMS_MAX);
  assert.ok(ids.includes('https://cdn.example/live.mp4') && ids.includes('https://cdn.example/recent.mp4'));
  assert.ok(ids.includes('https://cdn.example/stale.mp4'), 'stale-owned files go only after every unowned one');
  const left = { items: [], els: { stale: { key: 'stale', live: false, seen: 1 }, live: { key: 'live', live: true, seen: now } }, evt: {} };
  S.upsert(left, net('https://cdn.example/stale.mp4', { owner: 'stale' }), now - 5000);
  for (let i = 0; i < S.ITEMS_MAX; i++) S.upsert(left, net(`https://cdn.example/l${i}.mp4`, { owner: 'live' }), now - 4000 + i);
  assert.equal(left.items.length, S.ITEMS_MAX); assert.ok(!left.items.some(i => i.id.endsWith('stale.mp4')), 'an owner not seen for 30 minutes is evicted before protected files');
  assert.ok(!ids.includes('https://cdn0.example/f0.mp4') && ids.includes(`https://cdn${S.ITEMS_MAX + 9}.example/f${S.ITEMS_MAX + 9}.mp4`), 'oldest unowned out, newest in');
  // protected files beyond the cap stay, up to the hard limit
  const big = { items: [], els: {}, evt: {} };
  for (let i = 0; i < 250; i++) { big.els['p' + i] = { key: 'p' + i, live: true, seen: now }; S.upsert(big, net(`https://cdn.example/p${i}.mp4`, { owner: 'p' + i }), now - 90_000 + i); }
  S.upsert(big, net('https://cdn.example/extra.mp4'), now);
  assert.equal(big.items.length, 250, 'owned by live elements: all kept, the unowned extra file is the one that goes');
  for (let i = 250; i < 400; i++) { big.els['p' + i] = { key: 'p' + i, live: true, seen: now }; S.upsert(big, net(`https://cdn.example/p${i}.mp4`, { owner: 'p' + i }), now - 90_000 + i); }
  assert.equal(big.items.length, 320, 'the hard limit');
  // listItems shows the owned files even when many unowned ones exist
  assert.ok(S.listItems(st).length <= 200);
});
test('regression: five videos of very different sizes from one CDN host all stay listed (owned, and unowned when their folders differ)', async () => {
  const sizes = [0.7, 3, 12, 40, 150].map(m => Math.round(m * MB) + 4321);
  const b = makeBackground();
  for (let n = 0; n < 5; n++) {
    await new Promise(r => setTimeout(r, 70)); // events of different elements are at least a few tens of milliseconds apart
    await b.message({ action: 'media-event', events: [ev('v' + n, 'loadstart', b.now() - 30)] }, b.pageSender(tab));
    b.hooks.headers(response(fb(['alpha', 'bravo', 'charlie', 'delta', 'echo'][n] + '_x'), 'video/mp4', sizes[n], { initiator: 'https://feed.example' })); await tick(); await settle(b);
  }
  const rows = (await b.message({ action: 'media-list' }, b.pageSender(tab))).items;
  assert.equal(rows.length, 5); assert.deepEqual(json(rows.map(i => i.owner).sort()), ['v0', 'v1', 'v2', 'v3', 'v4']);
  // without any element link, files in different folders of the same host are not compared with each other
  const st = { items: sizes.map((size, n) => net(`https://video.fbcdn.example/o${n}/t42/${['alpha', 'bravo', 'charlie', 'delta', 'echo'][n]}.mp4`, { size })) };
  assert.equal(S.listItems(st).length, 5);
  // a fragment-like leftover (same folder, nothing links it, under 10% of the biggest) is still dropped
  const frag = { items: [net('https://video.fbcdn.example/o/t42/whole.mp4', { size: 150 * MB }), net('https://video.fbcdn.example/o/t42/odd.mp4', { size: 1 * MB })] };
  assert.deepEqual(S.listItems(frag).map(i => i.id.split('/').pop()), ['whole.mp4']);
  frag.items[1].owner = 'x'; assert.equal(S.listItems(frag).length, 2, 'an owner keeps it');
});

/* ---- bar geometry ---- */
test('barPlacement: top corner on the start side, sticks inside the visible part, null when too small, too hidden or no room', () => {
  const view = { w: 1000, h: 800 }, bar = { w: 200, h: 28 }, box = (l, t, w, h) => ({ left: l, top: t, right: l + w, bottom: t + h, width: w, height: h });
  assert.deepEqual(S.barPlacement(box(100, 50, 640, 360), view, bar, false), { x: 108, y: 58, ratio: 1 });
  assert.deepEqual(S.barPlacement(box(100, 50, 640, 360), view, bar, true), { x: 532, y: 58, ratio: 1 }, 'right-to-left: the right corner');
  assert.equal(S.barPlacement(box(100, -200, 640, 360), view, bar, false).y, 8, 'video partly above the screen: the bar stays at the top of the visible part');
  assert.equal(S.barPlacement(box(-100, 100, 640, 360), view, bar, false).x, 8, 'partly left of the screen');
  assert.equal(S.barPlacement(box(700, 100, 640, 360), view, bar, true).x, 1000 - 8 - 200, 'RTL clamps to the right screen edge');
  assert.equal(S.barPlacement(box(0, 0, 159, 200), view, bar, false), null, 'narrower than 160 px');
  assert.equal(S.barPlacement(box(0, 0, 300, 89), view, bar, false), null, 'lower than 90 px');
  assert.equal(S.barPlacement(box(0, 700, 640, 360), view, bar, false)?.ratio, 0.28, '28% visible is enough');
  assert.equal(S.barPlacement(box(0, 740, 640, 360), view, bar, false), null, 'under 25% visible');
  assert.equal(S.barPlacement(box(0, 800 - 40, 640, 40), view, bar, false), null);
  assert.equal(S.barPlacement(box(0, 0, 200, 100), view, { w: 190, h: 28 }, false), null, 'a bar that does not fit in the visible part');
});

/* ---- right click ---- */
async function menuSetup(items, target, extra = {}) {
  const b = makeBackground({ openTabs: [tab], tabReply: (id, msg) => (msg.type === 'neodm-context-target' ? target : undefined), ...extra });
  for (const i of items) {
    if (i.events) await b.message({ action: 'media-event', events: i.events.map(([key, type, offset]) => ev(key, type, b.now() + offset)) }, b.pageSender(tab));
    b.hooks.headers(response(i.url, 'video/mp4', i.size, { initiator: 'https://feed.example' })); await tick(); await settle(b);
  }
  return b;
}
test('right click: the link entry on a link that wraps a video downloads the video (owner item), never the link URL', async () => {
  const b = await menuSetup([{ url: fb('mine'), size: 30 * MB + 1, events: [['kv', 'progress', -600]] }, { url: fb('other'), size: 90 * MB + 1, events: [['ko', 'progress', -10]] }], { found: true, key: 'kv', blob: true, url: '', tag: 'video' });
  await b.message({ action: 'media-dom', sources: [{ key: 'kv', url: 'blob:x', tag: 'video', title: 'Mine', playing: false, vis: 1 }, { key: 'ko', url: 'blob:y', tag: 'video', title: 'Other', playing: true, vis: 1 }] }, b.pageSender(tab)); await tick();
  await b.hooks.menu({ menuItemId: 'link', linkUrl: 'https://www.facebook.com/123/videos/pcb.456/789', pageUrl: tab.url }, tab); await tick();
  const sent = b.nativeRequests.filter(r => r.url);
  assert.equal(sent.length, 1); assert.equal(sent[0].url, fb('mine').replace(/&?bytestart=0&byteend=999999/, ''), 'the file of the pointed-at element, not the playing one and not the link');
  assert.ok(b.sentToTabs.some(x => x.msg.type === 'neodm-context-target'), 'the tab was asked first');
  assert.match(b.local.status, /Accepted/); assert.ok(sent.every(r => !/facebook\.com/.test(r.url)));
});
test('right click: a link to a page with no video shows "This link is a web page, not a file." and sends nothing; files and download links are still sent', async () => {
  const b = await menuSetup([], { found: false });
  for (const linkUrl of ['https://www.facebook.com/123/videos/pcb.456/789', 'https://feed.example/post/42', 'https://www.instagram.com/p/abc/', 'https://feed.example/page.html']) {
    await b.hooks.menu({ menuItemId: 'link', linkUrl, pageUrl: tab.url }, tab); await tick();
    assert.equal(b.local.status, 'This link is a web page, not a file.', linkUrl); b.local.status = '';
  }
  assert.equal(b.nativeRequests.filter(r => r.url).length, 0);
  for (const linkUrl of ['https://files.example/a.zip', 'https://feed.example/files/report.pdf', 'https://other.example/get?id=5', 'https://feed.example/download?id=5', 'https://fbcdn.example/v/t42/clip.mp4?x=1']) {
    await b.hooks.menu({ menuItemId: 'link', linkUrl, pageUrl: tab.url }, tab); await tick();
  }
  assert.deepEqual(b.nativeRequests.filter(r => r.url).map(r => r.url), ['https://files.example/a.zip', 'https://feed.example/files/report.pdf', 'https://other.example/get?id=5', 'https://feed.example/download?id=5', 'https://fbcdn.example/v/t42/clip.mp4?x=1']);
  await b.hooks.menu({ menuItemId: 'link', linkUrl: 'javascript:void(0)', pageUrl: tab.url }, tab); await tick(); assert.match(b.local.status, /Not a direct HTTP/);
  assert.equal(S.isPageLink('https://feed.example/a/b', 'https://feed.example/'), true);
  assert.equal(S.isPageLink('https://elsewhere.example/a/b', 'https://feed.example/'), false, 'another site without an extension is left to the user');
});
test('right click: the media entry on a blob video uses the owned item; the page URL is never sent', async () => {
  const b = await menuSetup([{ url: fb('a'), size: 30 * MB + 1, events: [['k1', 'progress', -600]] }, { url: fb('b'), size: 31 * MB + 1, events: [['k2', 'progress', -10]] }], { found: true, key: 'k1', blob: true, url: '', tag: 'video' });
  await b.message({ action: 'media-dom', sources: [{ key: 'k1', url: 'blob:x', tag: 'video', title: 'A', vis: 1 }, { key: 'k2', url: 'blob:y', tag: 'video', title: 'B', playing: true, vis: 1 }] }, b.pageSender(tab)); await tick();
  await b.hooks.menu({ menuItemId: 'media', mediaType: 'video', srcUrl: 'blob:https://feed.example/uuid', pageUrl: tab.url }, tab); await tick();
  assert.deepEqual(b.nativeRequests.filter(r => r.url).map(r => r.url.includes('/a.mp4')), [true]);
  const none = await menuSetup([], { found: true, key: 'zz', blob: false, url: tab.url, tag: 'video' });
  await none.hooks.menu({ menuItemId: 'media', mediaType: 'video', srcUrl: tab.url, pageUrl: tab.url }, tab); await tick();
  assert.equal(none.nativeRequests.filter(r => r.url).length, 0); assert.equal(none.local.status, 'This link is a web page, not a file.');
});
test('right click: owned video-only and audio-only tracks of one element: the video track is chosen', async () => {
  const v = fb('v', { efg: b64url({ vencode_tag: 'dash_h264-basic-gen2_720p' }) }), a = fb('a', { efg: b64url({ vencode_tag: 'dash_ln_heaac_vbr3_audio' }) });
  const b = await menuSetup([{ url: a, size: 2 * MB, events: [['k1', 'progress', -20]] }, { url: v, size: 30 * MB, events: [['k1', 'progress', -10]] }], { found: true, key: 'k1', blob: true, url: '' });
  await b.hooks.menu({ menuItemId: 'link', linkUrl: 'https://www.facebook.com/x/videos/1', pageUrl: tab.url }, tab); await tick();
  const sent = b.nativeRequests.filter(r => r.url); assert.equal(sent.length, 1); assert.match(sent[0].url, /\/v\.mp4/); assert.match(sent[0].fileName, /\(video only\)/);
});

/* ---- content script (jsdom) ---- */
const windows = []; test.after(() => windows.forEach(w => { try { w.close(); } catch {} })); // the bars use timers
function page({ items = [], url = 'https://feed.example/home', lang = 'en', ribbon = true, button = true, html, prime } = {}) {
  const dom = new JSDOM(html || '<!doctype html><title>Feed</title><body><div role="article"><span>Alice Doe</span><span>Look at my dog doing a trick</span><a id="link" href="https://feed.example/post/1"><video id="v1"></video></a></div><div role="article"><video id="v2" src="https://cdn.example/two.mp4"></video></div></body>', { url, runScripts: 'outside-only', pretendToBeVisual: true }), w = dom.window;
  const sent = [], listeners = [], roots = [], stored = []; windows.push(w);
  const orig = w.Element.prototype.attachShadow; w.Element.prototype.attachShadow = function (i) { const r = orig.call(this, i); roots.push(r); return r; };
  for (const id of ['v1']) Object.defineProperty(w.document.getElementById(id), 'currentSrc', { value: 'blob:https://feed.example/uuid-' + id, configurable: true });
  w.chrome = { runtime: { sendMessage: async m => { sent.push(m); return m.action === 'media-list' ? { items } : m.action === 'media-download' ? { ok: true, id: 'job' } : { ok: true }; }, onMessage: { addListener: f => listeners.push(f) } }, storage: { local: { get: async () => ({ ribbonEnabled: ribbon, videoButtonEnabled: button, ...(prime || {}) }), set: async o => { stored.push(JSON.parse(JSON.stringify(o))); } }, onChanged: { addListener() {} } } };
  w.HTMLCanvasElement.prototype.getContext = () => null;
  const rect = (el, r) => { el.getBoundingClientRect = () => ({ left: r[0], top: r[1], right: r[0] + r[2], bottom: r[1] + r[3], width: r[2], height: r[3] }); };
  const state = (el, o) => { for (const [k, v] of Object.entries(o)) Object.defineProperty(el, k, { value: v, configurable: true }); };
  withI18n(w, lang); w.eval(source('sniff.js')); w.eval(source('content.js'));
  const send = m => { let reply; for (const f of listeners) f(m, {}, r => { reply = r; }); return reply; };
  const keys = () => Object.fromEntries(sent.filter(m => m.action === 'media-dom').at(-1).sources.map(s => [s.tag + ':' + (s.url === 'blob:' ? 'blob' : s.url.split('/').pop()), s.key]));
  return { w, d: w.document, sent, send, rect, state, roots, keys, stored, bars: () => roots.find(r => r.querySelector('style')?.textContent.includes('.b{')) };
}
test('content script: every element gets a stable key, playback events are batched to the worker, titles come from the post text', async () => {
  const p = page(); await tick();
  const first = p.sent.find(m => m.action === 'media-dom').sources; assert.equal(first.length, 2);
  const k1 = p.keys()['video:blob'], k2 = p.keys()['video:two.mp4']; assert.match(k1, /^e[a-z0-9]+$/); assert.notEqual(k1, k2);
  assert.equal(first.find(s => s.key === k1).title, 'Alice Doe Look at my dog doing a trick', 'post text of the article, 80 characters at most');
  const v1 = p.d.getElementById('v1');
  for (const type of ['loadstart', 'progress', 'progress', 'timeupdate', 'timeupdate', 'playing']) v1.dispatchEvent(new p.w.Event(type, { bubbles: false }));
  await new Promise(r => setTimeout(r, 160));
  const events = p.sent.filter(m => m.action === 'media-event').flatMap(m => m.events);
  assert.deepEqual(events.map(e => e.type), ['loadstart', 'progress', 'timeupdate', 'playing'], 'progress and timeupdate are throttled per element');
  assert.ok(events.every(e => e.key === k1 && Math.abs(e.t - Date.now()) < 2000));
  p.d.dispatchEvent(new p.w.Event('progress')); // not from a media element
  p.d.body.dispatchEvent(new p.w.Event('playing', { bubbles: true }));
  await new Promise(r => setTimeout(r, 160)); assert.equal(p.sent.filter(m => m.action === 'media-event').flatMap(m => m.events).length, 4);
  assert.equal(json(p.w.__neodmRibbon.items()).length, 0);
});
test('content script: right click on a link that wraps a video still finds the video (elementsFromPoint), with its key', async () => {
  const p = page(); await tick();
  const v1 = p.d.getElementById('v1'), link = p.d.getElementById('link'); p.rect(v1, [0, 0, 400, 300]);
  p.d.elementsFromPoint = () => [link, v1, p.d.body];
  link.dispatchEvent(new p.w.MouseEvent('contextmenu', { bubbles: true, clientX: 50, clientY: 50 }));
  p.d.elementsFromPoint = () => []; // asked later, at menu time
  const r = json(p.send({ type: 'neodm-context-target' }));
  assert.deepEqual([r.found, r.blob, r.url, r.key], [true, true, '', p.keys()['video:blob']]);
  link.dispatchEvent(new p.w.MouseEvent('contextmenu', { bubbles: true, clientX: 900, clientY: 900 }));
  assert.equal(p.send({ type: 'neodm-context-target' }).found, false, 'a link with no video under the pointer');
});
test('ribbon bars: one per video with a file, placed in the corner, shown on hover or while playing, follow scrolling, stay out of the way of clicks', async () => {
  const p = page(); await tick();
  const k = p.keys(), v1 = p.d.getElementById('v1'), v2 = p.d.getElementById('v2');
  const items = [net('https://cdn.example/dog.mp4', { owner: k['video:blob'], quality: '720p', size: 12.3 * MB }), net('https://cdn.example/two.mp4', { size: 5 * MB, owner: undefined })];
  p.rect(v1, [100, 50, 640, 360]); p.rect(v2, [100, 450, 640, 360]);
  p.send({ type: 'neodm-media', items }); await tick();
  assert.deepEqual(json(p.w.__neodmRibbon.bars()).map(b => b.key).sort(), [k['video:blob'], k['video:two.mp4']].sort(), 'owner link and URL match both get a bar');
  p.w.__neodmRibbon.layout();
  assert.equal(p.d.querySelector('[data-neodm-bars]'), null, 'nothing is built while no video is hovered or playing');
  p.w.dispatchEvent(new p.w.MouseEvent('pointermove', { clientX: 300, clientY: 200 })); p.w.__neodmRibbon.layout();
  const ui = p.bars(); assert.ok(ui, 'a single closed shadow root holds the bars'); assert.equal(p.d.querySelectorAll('[data-neodm-bars]').length, 1);
  const bar = () => [...ui.querySelectorAll('.b')];
  assert.equal(bar().length, 1); const b1 = bar()[0];
  assert.equal(b1.className, 'b ready'); assert.equal(b1.style.transform, 'translate(108px,58px)');
  assert.equal(b1.querySelector('.mk').textContent, 'NeoDM'); assert.equal(b1.querySelector('.d').textContent, 'Download'); assert.equal(b1.querySelector('.q').textContent, '720p 12.3 MB'); assert.equal(b1.querySelector('.m'), null, 'no menu button any more: several sources open the picker from the Download button');
  p.w.dispatchEvent(new p.w.MouseEvent('pointermove', { clientX: 900, clientY: 900 })); p.w.__neodmRibbon.layout(); assert.equal(b1.className, 'b ready off', 'pointer left: hidden, and not clickable');
  assert.match(ui.querySelector('style').textContent, /\.b\.off\{visibility:hidden\}\.b\.off \.pill,\.b\.off \.panel\{pointer-events:none\}/); assert.match(p.d.querySelector('[data-neodm-bars]').getAttribute('style'), /pointer-events: none !important/);
  p.state(v2, { paused: false, ended: false, readyState: 4 }); p.w.__neodmRibbon.layout();
  assert.equal(bar().length, 2, 'a playing video shows its bar without hover'); assert.equal(bar()[1].className, 'b ready'); assert.equal(bar()[1].style.transform, 'translate(108px,458px)');
  p.rect(v1, [100, 20, 640, 360]); p.rect(v2, [100, -200, 640, 360]); p.w.dispatchEvent(new p.w.MouseEvent('pointermove', { clientX: 300, clientY: 100 })); p.w.__neodmRibbon.layout();
  assert.equal(b1.style.transform, 'translate(108px,28px)', 'follows the scroll'); assert.equal(bar()[1].style.transform, 'translate(108px,8px)', 'sticks to the top of the visible part');
  p.rect(v2, [100, 2000, 640, 360]); p.w.__neodmRibbon.layout(); assert.equal(bar()[1].className, 'b ready off', 'off screen');
  // download: sends the media-download message for this element's file and shows the result in the bar
  b1.querySelector('.d').click(); await tick();
  assert.deepEqual(p.sent.filter(m => m.action === 'media-download').map(m => m.id), ['https://cdn.example/dog.mp4']); assert.equal(b1.querySelector('.d').textContent, 'Sent'); assert.equal(b1.querySelector('.q').className, 'q ok');
  // close: the button turns into a notice (Don't show on this site / Move to the other corner / Undo), then goes away for this element until reload, even when new items arrive
  b1.querySelector('.x').click(); assert.equal(json(p.w.__neodmRibbon.bars()).find(b => b.key === k['video:blob']).mode, 'notice');
  assert.equal(b1.querySelectorAll('.panel button').length, 3); assert.equal(b1.querySelector('.panel').hidden, false);
  p.send({ type: 'neodm-media', items: items.slice() }); p.w.dispatchEvent(new p.w.MouseEvent('pointermove', { clientX: 300, clientY: 100 })); p.w.__neodmRibbon.layout();
  assert.equal(json(p.w.__neodmRibbon.bars()).find(b => b.key === k['video:blob']).mode, 'notice', 'new items do not bring the button back');
  b1.querySelectorAll('.panel button')[2].click(); assert.equal(json(p.w.__neodmRibbon.bars()).find(b => b.key === k['video:blob']).mode, '', 'Undo brings it back');
  b1.querySelector('.x').click(); b1.querySelectorAll('.panel button')[0].click(); await tick();
  assert.deepEqual(p.stored.at(-1), { videoButtonHiddenHosts: ['feed.example'] }, "Don't show on this site stores the site"); assert.equal(p.w.__neodmRibbon.bars().length, 0, 'and removes every button of the page');
  // clicks on a bar do not reach the page (a link around the video must not open)
  let reached = 0; p.d.addEventListener('click', () => reached++); const other = bar().find(x => x !== b1); if (other) other.querySelector('.d').click(); assert.equal(reached, 0);
});
test('ribbon bars: separate audio track gets the "Audio only" entry; a failed download shows the error; right-to-left puts the bar on the right', async () => {
  const p = page({ lang: 'ar' }); await tick();
  const k = p.keys(), v1 = p.d.getElementById('v1'); p.rect(v1, [100, 50, 640, 360]);
  const video = net('https://cdn.example/v.mp4', { owner: k['video:blob'], track: 'video-only', complete: false, quality: '1080p', size: 20 * MB }), audio = net('https://cdn.example/a.mp4', { owner: k['video:blob'], kind: 'audio', track: 'audio-only', complete: false, size: 2 * MB });
  p.w.chrome.runtime.sendMessage = async m => { p.sent.push(m); return m.action === 'media-download' ? { ok: false, error: 'NeoDM is not running' } : { ok: true }; };
  p.send({ type: 'neodm-media', items: [audio, video] });
  p.w.dispatchEvent(new p.w.MouseEvent('pointermove', { clientX: 300, clientY: 200 })); p.w.__neodmRibbon.layout();
  const b1 = p.bars().querySelector('.b');
  assert.equal(b1.dir, 'rtl'); assert.equal(b1.style.transform, 'translate(522px,58px)');
  assert.equal(b1.querySelector('.d').textContent, 'تنزيل'); assert.match(b1.querySelector('.q').textContent, /^1080p 20\.0 MB · /, 'a video track and an audio track are one entry');
  b1.querySelector('.d').click(); await tick();
  assert.deepEqual(p.sent.filter(m => m.action === 'media-download').map(m => m.id), ['https://cdn.example/v.mp4'], 'the pair stops at the first failure');
  assert.equal(b1.querySelector('.q').textContent, 'NeoDM is not running'); assert.equal(b1.querySelector('.q').className, 'q err');
});
test('video button: YouTube gets the grey explanation, the setting turns it off, a file-less player waits then explains, unclaimed files make it a picker, small videos get none', async () => {
  const yt = page({ url: 'https://www.youtube.com/watch?v=1' }); await tick();
  yt.rect(yt.d.getElementById('v1'), [100, 50, 640, 360]);
  yt.send({ type: 'neodm-media', items: [] }); yt.w.dispatchEvent(new yt.w.MouseEvent('pointermove', { clientX: 300, clientY: 200 })); yt.w.__neodmRibbon.layout();
  const ytb = yt.w.__neodmRibbon.bars().filter(b => b.key === yt.keys()['video:blob']); assert.equal(ytb.length, 1); assert.deepEqual([ytb[0].state, ytb[0].reason], ['unsupported', 'youtube']);
  const off = page({ button: false }); await tick();
  off.send({ type: 'neodm-media', items: [net('https://cdn.example/two.mp4', { owner: off.keys()['video:blob'] })] }); assert.equal(off.w.__neodmRibbon.bars().length, 0);
  const none = page(); await tick(); none.send({ type: 'neodm-media', items: [] }); const waiting = none.w.__neodmRibbon.bars(); assert.ok(waiting.length >= 1 && waiting.every(b => b.state === 'checking'), 'no file yet: checking for two seconds ' + JSON.stringify(waiting.map(b => b.state)));
  none.send({ type: 'neodm-media', items: [net('https://cdn.example/unrelated.mp4')] }); const amb = none.w.__neodmRibbon.bars().find(b => b.key === none.keys()['video:blob']); assert.equal(amb.state, 'choose', 'a blob player and an unclaimed file: the user chooses');
  const small = page(); await tick(); small.rect(small.d.getElementById('v1'), [10, 10, 150, 100]);
  small.send({ type: 'neodm-media', items: [net('https://cdn.example/x.mp4', { owner: small.keys()['video:blob'] })] }); small.w.dispatchEvent(new small.w.MouseEvent('pointermove', { clientX: 20, clientY: 20 })); small.w.__neodmRibbon.layout();
  assert.equal(small.d.querySelector('[data-neodm-bars]'), null, 'never built');
});
test('popup: a row owned by an element shows the element metadata and the "Show on page" button; show-on-page carries the owner key', async () => {
  const items = [net('https://cdn.example/dog.mp4', { owner: 'k1', title: 'Alice Doe Look at my dog', w: 640, h: 360, dur: 42, size: 12 * MB })];
  const dom = new JSDOM(source('popup.html'), { runScripts: 'outside-only', url: 'chrome-extension://ext-id/popup.html' }), w = dom.window;
  w.chrome = { storage: { local: { get: async () => ({}), set: async () => {} }, session: { get: async () => ({}) } }, tabs: { query: async () => [{ id: 7, url: 'https://feed.example/', title: 'Feed' }] }, runtime: { sendMessage: async m => (m.action === 'popup-media' ? { items } : { ok: true }), getManifest: () => ({ version: '1.5.2' }) }, permissions: {} };
  withI18n(w); w.eval(source('sniff.js')); w.eval(source('popup.js')); await tick(14);
  const row = w.document.querySelector('#mediaList li.media');
  assert.equal(row.querySelector('.title').textContent, 'Alice Doe Look at my dog'); assert.equal(row.querySelector('.meta').textContent, 'Video 640x360 · 00:42 · 12.0 MB'); assert.ok(row.querySelector('button.link'));
  const b = makeBackground(); await b.message({ action: 'media-dom', sources: [{ key: 'k1', url: 'blob:x', tag: 'video', title: 'T', dur: 42, vis: 1 }] }, b.pageSender(tab));
  const now = Date.now(); await b.message({ action: 'media-event', events: [ev('k1', 'progress', now - 10)] }, b.pageSender(tab)); b.hooks.headers(response(fb('dog'), 'video/mp4', 12 * MB, {})); await tick(); await settle(b);
  b.sentToTabs.length = 0; await b.message({ action: 'popup-locate', tabId: 7, id: stored(b).items[0].id, on: true, scroll: true }); await tick();
  assert.equal(b.sentToTabs.at(-1).msg.owner, 'k1');
});

/* ---- 1.5.2 (second part): real file name and type on every request, the video button setting, fullscreen, origin change ---- */
const longUrl = 'https://video-dus1-1.xx.fbcdn.net/o1/v/t2/f2/m412/' + 'A'.repeat(90) + '.mp4?_nc_cat=101&efg=' + 'e'.repeat(420) + '&bytestart=0&byteend=99999&bitrate=580000&tag=hd&oe=6A0B1C2D';
const cleanLong = longUrl.replace(/&?bytestart=0&byteend=99999/, '');
test('right click on a video with a direct address: the request carries title + extension, the type and the page as referer (also for a 600+ character address)', async () => {
  assert.ok(longUrl.length > 600);
  const b = await menuSetup([], { found: true, key: 'k1', blob: false, url: longUrl, tag: 'video', title: 'Post 1 by Someone' });
  await b.hooks.menu({ menuItemId: 'media', mediaType: 'video', srcUrl: longUrl, pageUrl: tab.url }, tab); await tick();
  const r = b.nativeRequests.filter(x => x.url);
  assert.equal(r.length, 1); assert.ok(r[0].url === longUrl || r[0].url === cleanLong, 'the address is sent whole (only the byte range parameters may go)'); assert.ok(r[0].url.length > 600);
  assert.equal(r[0].fileName, 'Post 1 by Someone.mp4'); assert.equal(r[0].mime, 'video/mp4'); assert.equal(r[0].referer, new URL(tab.url).origin + '/'); assert.match(b.local.status, /Accepted/);
});
test('right click: an address with no extension still gets a .mp4 name and the video type; an audio element gets .m4a; no title falls back to the tab title', async () => {
  const plain = 'https://cdn.example/stream/a1B2c3D4e5F6g7H8i9J0k1L2m3N4o5?sig=' + 'a'.repeat(300);
  const b = await menuSetup([], { found: true, key: 'k1', blob: false, url: plain, tag: 'video', title: '' });
  await b.hooks.menu({ menuItemId: 'media', mediaType: 'video', srcUrl: plain, pageUrl: tab.url }, tab); await tick();
  const a = await menuSetup([], { found: true, key: 'k2', blob: false, url: 'https://cdn.example/s/xyz', tag: 'audio', title: 'Song' });
  await a.hooks.menu({ menuItemId: 'media', mediaType: 'audio', srcUrl: 'https://cdn.example/s/xyz', pageUrl: tab.url }, tab); await tick();
  const r = b.nativeRequests.find(x => x.url), q = a.nativeRequests.find(x => x.url);
  assert.equal(r.fileName, 'Feed.mp4'); assert.equal(r.mime, 'video/mp4'); assert.equal(q.fileName, 'Song.m4a'); assert.equal(q.mime, 'audio/mp4');
});
test('right click on the page entry over an overlay: the element under the pointer decides, the request is complete', async () => {
  const b = await menuSetup([], { found: true, key: 'k1', blob: false, url: longUrl, tag: 'video', title: 'Covered clip' });
  await b.hooks.menu({ menuItemId: 'page-video', pageUrl: tab.url }, tab); await tick();
  const r = b.nativeRequests.find(x => x.url); assert.ok(r.url === longUrl || r.url === cleanLong); assert.equal(r.fileName, 'Covered clip.mp4'); assert.equal(r.referer, new URL(tab.url).origin + '/');
});
test('ribbon, popup and button downloads of a sniffed item send title + extension and the type', async () => {
  const b = await menuSetup([{ url: fb('mine'), size: 30 * MB + 1, events: [['kv', 'progress', -600]] }], { found: true, key: 'kv', blob: true, url: '', tag: 'video', title: 'Mine' });
  await b.message({ action: 'media-dom', sources: [{ key: 'kv', url: 'blob:x', tag: 'video', title: 'Post about cats', playing: true, vis: 1 }] }, b.pageSender(tab)); await tick();
  const item = stored(b).items[0]; await b.message({ action: 'media-download', id: item.id }, b.pageSender(tab)); await tick();
  const r = b.nativeRequests.filter(x => x.url).at(-1); assert.equal(r.fileName, 'Post about cats.mp4'); assert.equal(r.mime, 'video/mp4'); assert.equal(r.referer, new URL(tab.url).origin + '/');
});
test('persistence: a change of origin (not of the path) starts a fresh list; a feed that only changes its path keeps it', async () => {
  const b = makeBackground(); b.hooks.tabUpdated(7, { url: 'https://feed.example/home' }); await tick();
  b.hooks.headers(response(fb('one'), 'video/mp4', 20 * MB, { initiator: 'https://feed.example' })); await tick();
  b.hooks.tabUpdated(7, { url: 'https://feed.example/watch/9' }); await tick(); assert.equal(stored(b).items.length, 1);
  b.hooks.tabUpdated(7, { url: 'https://other.example/x' }); await tick(); assert.equal(stored(b).items.length, 0);
});
test('content script: the video button follows its own setting; fullscreen on a container moves the host inside it, fullscreen on the video hides the bars', async () => {
  const p = page({ ribbon: false }); await tick();
  const k = p.keys(), v1 = p.d.getElementById('v1'); p.rect(v1, [100, 50, 640, 360]);
  p.send({ type: 'neodm-media', items: [net('https://cdn.example/dog.mp4', { owner: k['video:blob'] })] }); await tick();
  assert.equal(p.w.__neodmRibbon.bars().length, 2, 'the page ribbon setting is off but the video button is on (a record for each video)');
  p.w.dispatchEvent(new p.w.MouseEvent('pointermove', { clientX: 300, clientY: 200 })); p.w.__neodmRibbon.layout();
  const host = p.d.querySelector('[data-neodm-bars]'); assert.ok(host); assert.equal(host.parentNode, p.d.documentElement);
  const box = v1.parentElement; Object.defineProperty(p.d, 'fullscreenElement', { value: box, configurable: true });
  p.d.dispatchEvent(new p.w.Event('fullscreenchange')); p.w.__neodmRibbon.layout();
  assert.equal(host.parentNode, box, 'the host lives inside the fullscreen element');
  Object.defineProperty(p.d, 'fullscreenElement', { value: v1, configurable: true });
  p.d.dispatchEvent(new p.w.Event('fullscreenchange')); await tick(); assert.equal(p.w.__neodmRibbon.bars().length, 0, 'fullscreen on the <video> itself: no bar');
  Object.defineProperty(p.d, 'fullscreenElement', { value: null, configurable: true });
  p.d.dispatchEvent(new p.w.Event('fullscreenchange')); await tick(); p.w.__neodmRibbon.layout(); assert.equal(host.parentNode, p.d.documentElement); assert.equal(p.w.__neodmRibbon.bars().length, 2);
});
test('popup: the video button checkbox is on by default and stores videoButtonEnabled', async () => {
  const dom = new JSDOM(source('popup.html'), { runScripts: 'outside-only', url: 'chrome-extension://ext-id/popup.html' }), w = dom.window; const saved = [];
  w.chrome = { storage: { local: { get: async () => ({}), set: async o => { saved.push(o); } }, session: { get: async () => ({}) } }, tabs: { query: async () => [{ id: 7, url: 'https://feed.example/', title: 'Feed' }] }, runtime: { sendMessage: async () => ({ items: [] }), getManifest: () => ({ version: '1.5.2' }) }, permissions: {} };
  withI18n(w); w.eval(source('sniff.js')); w.eval(source('popup.js')); await tick(14);
  const box = w.document.getElementById('videoButton'); assert.ok(box); assert.equal(box.checked, true);
  box.checked = false; box.dispatchEvent(new w.Event('change')); await tick(); assert.deepEqual(json(saved.at(-1)), { videoButtonEnabled: false }); w.close();
});
test('the three video button messages exist in all ten locales, translated, without em dashes', () => {
  const dir = path.join(root, 'extension/_locales'), en = JSON.parse(fs.readFileSync(path.join(dir, 'en/messages.json'), 'utf8'));
  assert.equal(en.popup_video_button.message, 'Show a button on videos');
  for (const l of fs.readdirSync(dir)) {
    const m = JSON.parse(fs.readFileSync(path.join(dir, l, 'messages.json'), 'utf8'));
    for (const k of ['popup_video_button', 'popup_video_button_on', 'popup_video_button_off']) { assert.ok(m[k] && m[k].message.length >= 2, `${l}.${k}`); assert.doesNotMatch(m[k].message, /[\u2014\u2013]/); if (l !== 'en') assert.notEqual(m[k].message, en[k].message, `${l}.${k}`); }
  }
  const html = source('popup.html'); assert.match(html, /id="videoButton"/); assert.match(html, /data-i18n="popup_video_button"/);
});

/* ---- texts ---- */
test('the five new messages exist in all ten locales, translated, without em dashes', () => {
  const keys = ['bar_download', 'bar_hide', 'err_link_page'];
  const dir = path.join(root, 'extension/_locales'), langs = fs.readdirSync(dir); assert.equal(langs.length, 10);
  const en = JSON.parse(fs.readFileSync(path.join(dir, 'en/messages.json'), 'utf8'));
  assert.equal(en.err_link_page.message, 'This link is a web page, not a file.');
  for (const l of langs) {
    const m = JSON.parse(fs.readFileSync(path.join(dir, l, 'messages.json'), 'utf8'));
    for (const k of keys) {
      assert.ok(m[k] && m[k].message.length >= 2, `${l}.${k}`); assert.doesNotMatch(m[k].message, /[\u2014\u2013]/);
      if (l !== 'en') assert.notEqual(m[k].message, en[k].message, `${l}.${k} is translated`);
    }
  }
});
