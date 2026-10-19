'use strict';
/* 1.4.2: one video buffering in pieces must stay one entry; fragments of segmented streams are hidden and explained. */
const { test } = require('node:test');
const assert = require('node:assert/strict');
const S = require('../extension/sniff.js');
const { makeBackground, response, tick, json } = require('./extension-harness.cjs');

const MB = 1024 * 1024, TOTAL = 700 * MB;
const gv = (q, extra = {}) => response('https://rr1---sn-abc.googlevideo.com/videoplayback?' + new URLSearchParams({ expire: '1', id: 'o-abc', ...q }), q.mime || 'video/mp4', 1 * MB, extra);
const part = (url, from, to, total, type = 'video/mp4') => {
  const d = response(url, type, to - from + 1, { statusCode: 206 });
  d.responseHeaders.push({ name: 'Content-Range', value: `bytes ${from}-${to}/${total}` }); return d;
};
const stored = b => json(b.sessionData['neodm.media.7']);
const ribbonList = (b, tab) => b.message({ action: 'media-list' }, b.pageSender(tab));
const popupList = b => b.message({ action: 'popup-media', tabId: 7 });
const feed = async (b, list) => { for (const d of list) b.hooks.headers(d); await tick(20); };

test('googlevideo: ten chunks of one itag with changing range/rn/rbuf/sq are one entry with the clen size', async () => {
  const b = makeBackground();
  await feed(b, Array.from({ length: 10 }, (_, i) => gv({ itag: '18', mime: 'video/mp4', clen: String(90 * MB), range: `${i * MB}-${(i + 1) * MB - 1}`, rn: String(i), rbuf: String(1000 + i), sq: String(i), sig: 'abc' })));
  const items = stored(b).items; assert.equal(items.length, 1);
  assert.equal(items[0].size, 90 * MB); assert.equal(items[0].track, 'muxed');
  assert.doesNotMatch(items[0].url, /range=|rn=|rbuf=|sq=/); assert.match(items[0].url, /itag=18/); assert.match(items[0].url, /sig=abc/);
  assert.equal(b.sentToTabs.length, 1, 'buffering does not re-notify the ribbon');
});

test('googlevideo: each distinct track (muxed, video-only, audio-only) is exactly one entry however long it buffers', async () => {
  const b = makeBackground(), seq = [];
  for (let i = 0; i < 12; i++) for (const q of [{ itag: '18', clen: 50 * MB }, { itag: '137', clen: 80 * MB }, { itag: '140', mime: 'audio/mp4', clen: 6 * MB }]) seq.push(gv({ mime: 'video/mp4', ...q, clen: String(q.clen), range: `${i}-${i + 9}`, rn: String(i) }));
  await feed(b, seq);
  assert.deepEqual(stored(b).items.map(i => i.track).sort(), ['audio-only', 'muxed', 'video-only']);
});

test('generic mp4: ten 206 windows with a changing ?bytestart=&byteend= are one entry with the total size', async () => {
  const b = makeBackground();
  await feed(b, Array.from({ length: 10 }, (_, i) => part(`https://cdn.example/m/movie.mp4?id=42&sig=xyz&bytestart=${i * MB}&byteend=${(i + 1) * MB - 1}`, i * MB, (i + 1) * MB - 1, TOTAL)));
  const items = stored(b).items; assert.equal(items.length, 1);
  assert.equal(items[0].size, TOTAL); assert.equal(items[0].url, 'https://cdn.example/m/movie.mp4?id=42&sig=xyz');
  assert.equal(items[0].rawUrl, 'https://cdn.example/m/movie.mp4?id=42&sig=xyz&bytestart=0&byteend=1048575', 'the original URL is kept');
  assert.equal(b.sentToTabs.length, 1);
});

test('generic mp4: range expressed in the path and cache-busting parameters join the same entry', async () => {
  const b = makeBackground();
  await feed(b, [
    part('https://cdn.example/range/0-1048575/movie.mp4?v=1', 0, MB - 1, TOTAL), part('https://cdn.example/range/1048576-2097151/movie.mp4?v=1&_=123', MB, 2 * MB - 1, TOTAL),
    response('https://cdn.example/movie.mp4?v=1&ts=999', 'video/mp4', TOTAL),
  ]);
  assert.equal(stored(b).items.length, 1); assert.equal(stored(b).items[0].url, 'https://cdn.example/movie.mp4?v=1');
});

test('distinct tokens are distinct resources unless a range parameter is what differs (documented limit)', async () => {
  const b = makeBackground();
  await feed(b, [response('https://cdn.example/t/aaa/movie.mp4', 'video/mp4', 80 * MB), response('https://cdn.example/t/bbb/movie.mp4', 'video/mp4', 80 * MB),
    response('https://cdn.example/q.mp4?token=a', 'video/mp4', 80 * MB), response('https://cdn.example/q.mp4?token=b', 'video/mp4', 80 * MB)]);
  assert.equal(stored(b).items.length, 4);
  assert.equal(S.canonicalUrl('https://cdn.example/q.mp4?token=a&Range=0-9&BYTESTART=1'), 'https://cdn.example/q.mp4?token=a', 'parameter names are case-insensitive');
});

test('Content-Range with an unknown total is not an entry; it is remembered and completed when a later response reveals the total', async () => {
  const b = makeBackground(), url = 'https://cdn.example/clip.mp4?id=9';
  await feed(b, [part(url + '&start=0', 0, MB - 1, '*'), part(url + '&start=1', MB, 2 * MB - 1, '*')]);
  assert.equal(stored(b).items?.length ?? 0, 0); assert.equal(b.sentToTabs.length, 0);
  assert.ok(Object.keys(stored(b).pieces).length === 1, 'one pending piece per group');
  await feed(b, [part(url + '&start=2', 2 * MB, 3 * MB - 1, 50 * MB)]);
  assert.equal(stored(b).items.length, 1); assert.equal(stored(b).items[0].size, 50 * MB); assert.deepEqual(stored(b).pieces, {});
  assert.equal(S.classify(part(url, 0, MB - 1, '*')), null);
});

test('a 206 whose total is smaller than the minimum is never an entry; a round chunk-sized 200 on a range URL is a piece too', async () => {
  const b = makeBackground();
  await feed(b, [part('https://cdn.example/s.mp4', 0, 99, 1000), response('https://cdn.example/c.mp4?range=0-1048575', 'video/mp4', MB), response('https://cdn.example/c.mp4?range=1048576-2097151', 'video/mp4', MB)]);
  assert.equal(stored(b).items?.length ?? 0, 0);
  assert.equal(S.classify(response('https://cdn.example/full.mp4?start=0', 'video/mp4', 20 * MB + 5)).ambiguous, true, 'a non-round 200 on a range URL is kept but flagged');
});

test('numbered fragments (seg-1.mp4 .. seg-30.mp4) give no downloadable entry and one informational row in the popup list only', async () => {
  const b = makeBackground();
  await feed(b, Array.from({ length: 30 }, (_, i) => response(`https://cdn.example/v/seg-${i + 1}.mp4`, 'video/mp4', 2 * MB)));
  assert.equal(stored(b).items.length, 0);
  assert.deepEqual(json((await ribbonList(b)).items), []);
  const rows = (await popupList(b)).items; assert.equal(rows.length, 1);
  assert.deepEqual(json(rows[0]), { id: 'neodm-segmented', kind: 'info', info: true, text: 'Segmented stream, cannot be saved as one file' });
});

test('init.mp4 plus frag_00001..00003 is a segmented stream', async () => {
  const b = makeBackground();
  await feed(b, [response('https://cdn.example/s/init.mp4', 'video/mp4', 900), ...[1, 2, 3].map(n => response(`https://cdn.example/s/frag_${String(n).padStart(5, '0')}.mp4`, 'video/mp4', 3 * MB))]);
  assert.equal(stored(b).items?.length ?? 0, 0); assert.equal((await popupList(b)).items.at(-1).info, true);
});

test('three numbered files of one pattern remove the members already listed and ignore the rest', async () => {
  const b = makeBackground();
  await feed(b, [response('https://cdn.example/clip/1080p/v121.mp4', 'video/mp4', 2 * MB), response('https://cdn.example/clip/1080p/v122.mp4', 'video/mp4', 2 * MB)]);
  assert.equal(stored(b).items.length, 2);
  await feed(b, [response('https://cdn.example/clip/1080p/v123.mp4', 'video/mp4', 2 * MB), response('https://cdn.example/clip/1080p/v124.mp4', 'video/mp4', 2 * MB)]);
  assert.equal(stored(b).items.length, 0); assert.equal((await popupList(b)).items.length, 1);
  assert.equal(S.inspect(response('https://cdn.example/clip/1080p/000125.mp4', 'video/mp4', 2 * MB)).seg.nameHit, true, 'zero-padded numeric names are fragment names');
});

test('a real full file is still offered next to a segmented stream, with the informational row last', async () => {
  const b = makeBackground();
  await feed(b, [response('https://cdn.example/film.mp4', 'video/mp4', 80 * MB), ...Array.from({ length: 4 }, (_, i) => response(`https://cdn.example/ad/chunk-${i}.webm`, 'video/webm', MB))]);
  const rows = (await popupList(b)).items; assert.deepEqual(json(rows.map(r => r.info ? 'info' : r.url)), ['https://cdn.example/film.mp4', 'info']);
  assert.deepEqual(json((await ribbonList(b)).items.map(i => i.url)), ['https://cdn.example/film.mp4']);
});

test('a DOM <video src=http> merges with its network item into one primary entry (network size wins)', async () => {
  const b = makeBackground();
  await feed(b, [response('https://cdn.example/other.mp4', 'video/mp4', 30 * MB), part('https://cdn.example/main.mp4?token=1&bytestart=0&byteend=9999', 0, 9999, 40 * MB)]);
  await b.message({ action: 'media-dom', sources: [{ url: 'https://cdn.example/main.mp4?token=1', tag: 'video' }] }, b.pageSender()); await tick();
  const items = (await ribbonList(b)).items; assert.equal(items.length, 2);
  assert.equal(items[0].primary, true); assert.equal(items[0].size, 40 * MB); assert.equal(items[0].source, 'net'); assert.equal(items[0].url, 'https://cdn.example/main.mp4?token=1');
  await feed(b, [part('https://cdn.example/main.mp4?token=1&bytestart=5&byteend=9', 5, 9, 40 * MB)]); assert.equal(stored(b).items.length, 2);
});

test('two different videos stay two entries', async () => {
  const b = makeBackground();
  await feed(b, [gv({ itag: '18', mime: 'video/mp4', clen: String(50 * MB), id: 'AAA' }), gv({ itag: '18', mime: 'video/mp4', clen: String(50 * MB), id: 'BBB' })]);
  assert.equal(stored(b).items.length, 2);
});

test('a blob: player (MSE) hides ambiguous range-URL items but keeps ordinary full files', async () => {
  const b = makeBackground();
  await feed(b, [response('https://cdn.example/full.mp4?start=0', 'video/mp4', 20 * MB + 5), response('https://cdn.example/plain.mp4', 'video/mp4', 20 * MB)]);
  assert.equal((await ribbonList(b)).items.length, 2);
  await b.message({ action: 'media-dom', sources: [{ url: 'blob:', tag: 'video' }] }, b.pageSender()); await tick();
  assert.deepEqual(json((await ribbonList(b)).items.map(i => i.url)), ['https://cdn.example/plain.mp4']);
});

test('images and GIFs keep their per-URL behaviour; type filters and pause still apply', async () => {
  const b = makeBackground({ settings: { mediaTypes: { image: true, gif: true } } });
  await feed(b, [response('https://i.example/a.gif', 'image/gif', 200 * 1024), response('https://i.example/b.gif', 'image/gif', 200 * 1024), response('https://i.example/c.jpg', 'image/jpeg', 100 * 1024)]);
  assert.equal(stored(b).items.length, 3);
  const off = makeBackground({ settings: { mediaTypes: { video: false } } });
  await feed(off, [response('https://cdn.example/film.mp4', 'video/mp4', 80 * MB)]); assert.equal(off.sessionData['neodm.media.7']?.items?.length ?? 0, 0);
  const paused = makeBackground({ sessionData: { 'neodm.pause': { all: true, hosts: [] } } });
  await feed(paused, [response('https://cdn.example/film.mp4', 'video/mp4', 80 * MB)]); assert.deepEqual(json((await ribbonList(paused)).items), []);
});

test('ordering: primary first, then larger files; leftovers under 10% of the biggest file of the origin are dropped; googlevideo tracks are kept', () => {
  const mk = (id, size, extra = {}) => ({ id, url: `https://cdn.example/${id}.mp4`, kind: 'video', track: 'file', complete: true, size, source: 'net', ...extra });
  const state = { items: [mk('small', 10 * MB), mk('tiny', 2 * MB), mk('big', 100 * MB), mk('dom', 0, { primary: true, source: 'dom' }), mk('lone', 1, { primary: true })] };
  assert.deepEqual(S.listItems(state).map(i => i.id), ['lone', 'dom', 'big', 'small']);
  const g = { items: [{ id: 'a', url: 'https://r1.googlevideo.com/videoplayback?itag=140', kind: 'audio', track: 'audio-only', complete: false, size: 3 * MB }, { id: 'v', url: 'https://r1.googlevideo.com/videoplayback?itag=137', kind: 'video', track: 'video-only', complete: false, size: 300 * MB }, { id: 'm', url: 'https://r1.googlevideo.com/videoplayback?itag=18', kind: 'video', track: 'muxed', complete: true, size: 50 * MB }] };
  assert.deepEqual(S.listItems(g).map(i => i.id), ['m', 'v', 'a']);
  assert.equal(S.listItems({ items: Array.from({ length: 250 }, (_, i) => mk('n' + i, 5 * MB)) }).length, 200, '1.5.2: the list shows up to 200 items');
});

test('navigation clears items, pending pieces, segment counters and the segmented mark; a hash change does not', async () => {
  const b = makeBackground();
  b.hooks.tabUpdated(7, { url: 'https://video.example/watch?v=1' }); await tick();
  await feed(b, [part('https://cdn.example/p.mp4', 0, MB - 1, '*'), ...[1, 2, 3].map(n => response(`https://cdn.example/seg-${n}.mp4`, 'video/mp4', MB))]);
  let s = stored(b); assert.equal(Object.keys(s.pieces).length, 1); assert.equal(Object.keys(s.segmented).length, 1);
  b.hooks.tabUpdated(7, { url: 'https://video.example/watch?v=1#t=5' }); await tick(); assert.equal(Object.keys(stored(b).segmented).length, 1);
  b.hooks.tabUpdated(7, { url: 'https://video.example/watch?v=2' }); await tick(); assert.equal(Object.keys(stored(b).segmented).length, 1, '1.5.2: a pushState URL change keeps what was learned');
  b.hooks.tabUpdated(7, { status: 'loading', url: 'https://video.example/watch?v=3' }); b.navigate('https://video.example/watch?v=3'); await tick();
  s = stored(b); assert.deepEqual([s.items, s.pieces, s.segs, s.segmented, s.mse], [[], {}, {}, {}, false]);
  assert.deepEqual(json((await popupList(b)).items), [], 'the informational row goes away with the page');
  await feed(b, [response('https://cdn.example/seg-9.mp4', 'video/mp4', MB)]); assert.equal(Object.keys(stored(b).segmented).length, 1, 'tracking starts again');
});

test('memory: pending pieces, segment counters and segmented marks are capped per tab', async () => {
  const b = makeBackground();
  for (let i = 0; i < 260; i++) b.hooks.headers(part(`https://cdn.example/p${i}.mp4`, 0, MB - 1, '*'));
  for (let i = 0; i < 260; i++) b.hooks.headers(response(`https://h${i}.example/x/${i}/file.mp4`, 'video/mp4', MB));
  for (let i = 0; i < 260; i++) b.hooks.headers(response(`https://k${i}.example/seg-${i}.mp4`, 'video/mp4', MB));
  await tick(60);
  const s = stored(b); assert.ok(Object.keys(s.pieces).length <= S.MAX_KEYS); assert.ok(Object.keys(s.segs).length <= S.MAX_KEYS); assert.ok(Object.keys(s.segmented).length <= S.MAX_KEYS);
  assert.ok(s.items.length <= S.ITEMS_MAX);
});

test('download sends the URL without range parameters; if NeoDM fails to start it, the original URL is tried once', async () => {
  const raw = 'https://cdn.example/m/movie.mp4?id=42&bytestart=0&byteend=1048575', clean = 'https://cdn.example/m/movie.mp4?id=42';
  const b = makeBackground({ nativeReply: req => (req.url === raw ? { ok: true, id: 'job-raw' } : { ok: false, error: 'HTTP 403' }) });
  await feed(b, [part(raw, 0, MB - 1, TOTAL)]);
  const r = await b.message({ action: 'media-download', id: clean }, b.pageSender());
  assert.equal(r.ok, true); assert.equal(r.id, 'job-raw'); assert.deepEqual(b.nativeRequests.filter(x => x.url).map(x => x.url), [clean, raw]);
  const ok = makeBackground(); await feed(ok, [part(raw, 0, MB - 1, TOTAL)]);
  await ok.message({ action: 'media-download', id: clean }, ok.pageSender()); assert.deepEqual(ok.nativeRequests.filter(x => x.url).map(x => x.url), [clean]);
  const both = makeBackground({ nativeReply: () => ({ ok: false, error: 'HTTP 403' }) }); await feed(both, [part(raw, 0, MB - 1, TOTAL)]);
  const f = await both.message({ action: 'media-download', id: clean }, both.pageSender()); assert.equal(f.ok, false); assert.equal(both.nativeRequests.filter(x => x.url).length, 2);
  const cancel = makeBackground({ nativeReply: () => ({ ok: false, error: 'cancelled', cancelled: true }) }); await feed(cancel, [part(raw, 0, MB - 1, TOTAL)]);
  await cancel.message({ action: 'media-download', id: clean }, cancel.pageSender()); assert.equal(cancel.nativeRequests.filter(x => x.url).length, 1, 'a user cancel is not retried');
});

test('popup list: entries can be downloaded from the popup', async () => {
  const b = makeBackground(); await feed(b, [response('https://cdn.example/film.mp4', 'video/mp4', 80 * MB)]);
  const rows = (await popupList(b)).items; assert.equal(rows.length, 1);
  const r = await b.message({ action: 'popup-download', tab: { id: 7, url: 'https://video.example/watch?v=1', title: 'Film' }, id: rows[0].id });
  assert.equal(r.ok, true); assert.equal(b.nativeRequests.at(-1).fileName, 'Film.mp4');
  assert.equal(await b.message({ action: 'popup-media', tabId: 7 }, b.pageSender()), undefined, 'page scripts cannot use the popup actions');
});
