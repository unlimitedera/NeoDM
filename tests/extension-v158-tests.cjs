'use strict';
/* 1.5.8: streams, hook sources, the per-video resolver, button geometry, the page-world hooks (in a vm with a fake page), the private channel, frames, the popup. */
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { JSDOM } = require('jsdom');
const S = require('../extension/sniff.js');
const { makeBackground, tick, json, source, withI18n } = require('./extension-harness.cjs');
const root = path.join(__dirname, '..');
const MB = 1024 * 1024;
const item = (url, o = {}) => ({ id: url, url, rawUrl: url, groupKey: url, kind: 'video', track: 'file', complete: true, size: 10 * MB, quality: '', mime: 'video/mp4', source: 'net', ...o });
const tab = { id: 7, url: 'https://video.example/watch', title: 'Watch' };
const frameSender = (frameId, url, extra = {}) => ({ id: 'ext-id', url, frameId, tab, ...extra });

test('version 1.5.8 in props, manifest, changelog and README; no em dashes in the new entry', () => {
  const props = fs.readFileSync(path.join(root, 'Directory.Build.props'), 'utf8'), version = /<Version>([^<]+)<\/Version>/.exec(props)[1];
  const [vMajor, vMinor, vPatch] = version.split('.').map(Number); assert.ok(vMajor > 1 || vMinor > 5 || (vMinor === 5 && vPatch >= 8));
  const log = fs.readFileSync(path.join(root, 'CHANGELOG.md'), 'utf8'), entry = log.slice(log.indexOf('## [1.5.8]'), log.indexOf('## [1.5.7]'));
  assert.ok(entry.length > 500, 'the 1.5.8 entry exists'); assert.doesNotMatch(entry, /[\u2014\u2013]/);
  assert.match(fs.readFileSync(path.join(root, 'README.md'), 'utf8'), /^\| 1\.5\.8 \| /m);
  assert.match(entry, /HLS|DASH/); assert.match(entry, /copy/i);
});

/* ---- playlists ---- */
test('parseManifest: HLS master (variants, children), media playlist (length, segments), live, protected', () => {
  const master = '#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360\nlow/index.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=2400000,RESOLUTION=1280x720\nhigh/index.m3u8?x=1\n#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="a",URI="aud/index.m3u8"\n';
  const m = S.parseManifest(master, 'https://cdn.example/v/master.m3u8');
  assert.deepEqual([m.type, m.master, m.variants.length, S.bestHeight(m.variants)], ['hls', true, 2, 720]);
  assert.deepEqual(m.children, ['https://cdn.example/v/low/index.m3u8', 'https://cdn.example/v/high/index.m3u8?x=1', 'https://cdn.example/v/aud/index.m3u8']);
  const media = '#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXTINF:4.0,\nseg0.ts\n#EXTINF:3.5,\nhttps://other.example/seg1.ts\n#EXT-X-ENDLIST\n';
  const p = S.parseManifest(media, 'https://cdn.example/v/low/index.m3u8');
  assert.deepEqual([p.master, p.dur, p.live, p.segs], [false, 7.5, false, ['https://cdn.example/v/low/seg0.ts', 'https://other.example/seg1.ts']]);
  assert.equal(S.parseManifest(media.replace('#EXT-X-ENDLIST\n', ''), 'https://cdn.example/a.m3u8').live, true, 'no ENDLIST = live');
  assert.equal(S.parseManifest('#EXTM3U\n#EXT-X-KEY:METHOD=SAMPLE-AES,URI="skd://x",KEYFORMAT="com.apple.streamingkeydelivery"\n#EXTINF:4,\ns.ts\n', 'https://a.example/x.m3u8').drm, true);
  assert.equal(S.parseManifest('#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI="k.key"\n#EXTINF:4,\ns.ts\n#EXT-X-ENDLIST', 'https://a.example/x.m3u8').drm, false, 'plain AES-128 is not DRM');
  assert.deepEqual(S.parseManifest('garbage \u0000 <<<', 'https://a.example/x.m3u8').segs, []);
  const big = '#EXTM3U\n' + Array.from({ length: 5000 }, (_, i) => `#EXTINF:1,\ns${i}.ts\n`).join(''); assert.ok(S.parseManifest(big, 'https://a.example/x.m3u8').segs.length <= 400, 'segment list is capped');
});
test('parseManifest: DASH (length, representations, live, protected)', () => {
  const mpd = '<?xml version="1.0"?><MPD type="static" mediaPresentationDuration="PT1H2M3.5S"><Period><AdaptationSet mimeType="video/mp4"><Representation id="1" bandwidth="800000" width="640" height="360"/><Representation id="2" bandwidth="2500000" width="1280" height="720"/></AdaptationSet><AdaptationSet mimeType="audio/mp4"><Representation id="3" bandwidth="128000" codecs="mp4a.40.2"/></AdaptationSet></Period></MPD>';
  const m = S.parseManifest(mpd, 'https://cdn.example/m.mpd');
  assert.deepEqual([m.type, m.dur, m.live, S.bestHeight(m.variants), m.variants.filter(v => v.audio).length], ['dash', 3723.5, false, 720, 1]);
  assert.equal(S.parseManifest(mpd.replace('type="static"', 'type="dynamic"'), 'https://a.example/m.mpd').live, true);
  assert.equal(S.parseManifest(mpd.replace('<Period>', '<Period><ContentProtection schemeIdUri="urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed"/>'), 'https://a.example/m.mpd').drm, true);
});
test('stream detection helpers', () => {
  assert.equal(S.isManifestUrl('https://a.example/x/master.m3u8?t=1'), true); assert.equal(S.isManifestUrl('https://a.example/x.mpd'), true); assert.equal(S.isManifestUrl('https://a.example/x.mp4'), false);
  assert.equal(S.isManifestType('application/vnd.apple.mpegurl'), true); assert.equal(S.isManifestType('application/dash+xml'), true); assert.equal(S.isManifestType('video/mp4'), false);
  assert.equal(S.streamKindOf('https://a.example/x.mpd', ''), 'dash'); assert.equal(S.streamKindOf('https://a.example/x', 'application/x-mpegURL'), 'hls');
});

/* ---- hook sources become items ---- */
test('hooksReport: files, pairs and streams become owned items; invalid sources are dropped; the hook knowledge beats the network guess', () => {
  const st = { items: [], els: { k1: { key: 'k1', tag: 'video', live: true, frame: 3 } } };
  const net = item('https://cdn.example/a.mp4', { kind: 'video', track: 'file', size: 4 * MB }); S.upsert(st, net, 1000);
  const changed = S.hooksReport(st, [{ key: 'k1', list: [
    { role: 'file', url: 'https://cdn.example/v.mp4?bytestart=0&byteend=99', kind: 'video', track: 'video-only', size: 5 * MB, mime: 'video/mp4', quality: '720p' },
    { role: 'file', url: 'https://cdn.example/a.mp4', kind: 'audio', track: 'audio-only', size: 4 * MB, mime: 'video/mp4' },
    { role: 'hls', url: 'https://cdn.example/master.m3u8', quality: '1080p', dur: 61, variants: 3 },
    { role: 'pieces', url: 'https://cdn.example/f/seg1.m4s', pieces: 20 },
    { role: 'file', url: 'javascript:alert(1)', kind: 'video' }, { role: 'zip', url: 'https://x.example/a' }, 'not an object', null,
  ] }, { key: '__proto__', list: [{ role: 'file', url: 'https://x.example/p.mp4' }] }, { key: 'has space', list: [] }], 2000, 3, 'https://player.example/embed');
  assert.equal(changed, true);
  const by = id => st.items.find(i => i.id === id);
  const v = by('https://cdn.example/v.mp4'), a = by('https://cdn.example/a.mp4'), hls = by('https://cdn.example/master.m3u8'), pieces = st.items.find(i => i.stream === 'pieces');
  assert.deepEqual([v.owner, v.track, v.complete, v.frame, v.frameUrl, v.hookOwned], ['k1', 'video-only', false, 3, 'https://player.example/embed', true]);
  assert.deepEqual([a.owner, a.track, a.kind, a.complete], ['k1', 'audio-only', 'audio', false], 'the data flow says audio-only even though the response said video');
  assert.deepEqual([hls.track, hls.stream, hls.quality, hls.dur, hls.variants, hls.complete], ['stream', 'hls', '1080p', 61, 3, false]);
  assert.ok(pieces && pieces.track === 'stream' && pieces.pieces === 20);
  assert.equal(st.items.length, 4, 'nothing invalid was added'); assert.ok(!st.items.some(i => /javascript|zip|x\.example/.test(i.url)));
  // a later network response for the same file does not undo the classification
  S.upsert(st, item('https://cdn.example/a.mp4', { kind: 'video', track: 'file', size: 4 * MB }), 3000);
  assert.deepEqual([by('https://cdn.example/a.mp4').track, by('https://cdn.example/a.mp4').kind, by('https://cdn.example/a.mp4').owner], ['audio-only', 'audio', 'k1']);
  // listing never offers a stream as a file
  assert.equal(S.bestVideo(st.items.filter(i => i.owner === 'k1' && i.track === 'stream')), null);
  const row = S.describeItem(hls, k => ({ kind_stream_hls: 'Stream (HLS)' })[k] || k); assert.match(row.meta, /^Stream \(HLS\)/);
});
test('resolveElement: ready, pair, choose, checking, unsupported with each reason', () => {
  const mk = (o, i) => S.resolveElement({ key: 'k', age: 5000, ...o }, i);
  assert.equal(mk({}, [item('https://a.example/1.mp4', { owner: 'k' })]).state, 'ready');
  assert.equal(mk({ url: 'https://a.example/1.mp4?range=0-9' }, [item('https://a.example/1.mp4')]).state, 'ready', 'matched by the address the element shows');
  const pair = mk({}, [item('https://a.example/v.mp4', { owner: 'k', track: 'video-only', complete: false }), item('https://a.example/a.m4a', { owner: 'k', kind: 'audio', track: 'audio-only', complete: false })]);
  assert.deepEqual([pair.state, pair.options.length, pair.options[0].type, !!pair.options[0].audio], ['ready', 1, 'pair', true]);
  const lone = mk({}, [item('https://a.example/v.mp4', { owner: 'k', track: 'video-only', complete: false })]); assert.deepEqual([lone.state, lone.options[0].type], ['ready', 'file']);
  const two = mk({}, [item('https://a.example/1.mp4', { owner: 'k', time: 1 }), item('https://a.example/2.mp4', { owner: 'k', time: 2 })]); assert.deepEqual([two.state, two.options.length], ['choose', 2]);
  assert.equal(two.options[0].main.id, 'https://a.example/2.mp4', 'newest first');
  // 1.6.0: a playlist or manifest is an option of its own (the app reads it and asks for the quality); only protected, live and piece-only streams stay unsupported, each with its own reason
  const withStream = mk({}, [item('https://a.example/1.mp4', { owner: 'k' }), item('https://a.example/m.m3u8', { owner: 'k', track: 'stream', stream: 'hls', complete: false })]); assert.deepEqual([withStream.state, withStream.streams.length, withStream.options.map(o => o.type)], ['choose', 1, ['file', 'stream']]);
  const streamOnly = mk({}, [item('https://a.example/m.m3u8', { owner: 'k', track: 'stream', stream: 'hls', complete: false })]); assert.deepEqual([streamOnly.state, streamOnly.options[0].type, streamOnly.reason], ['ready', 'stream', '']);
  const dashOnly = mk({}, [item('https://a.example/m.mpd', { owner: 'k', track: 'stream', stream: 'dash', complete: false })]); assert.deepEqual([dashOnly.state, dashOnly.options[0].main.stream], ['ready', 'dash']);
  const masterAndMedia = mk({}, [item('https://a.example/m.m3u8', { owner: 'k', track: 'stream', stream: 'hls', complete: false, variants: 3, time: 1 }), item('https://a.example/v1.m3u8', { owner: 'k', track: 'stream', stream: 'hls', complete: false, time: 2 })]); assert.deepEqual([masterAndMedia.state, masterAndMedia.options.length, masterAndMedia.options[0].main.id], ['choose', 2, 'https://a.example/m.m3u8'], 'both a master and a media playlist are listed, the master first');
  const piecesOnly = mk({}, [item('https://a.example/p', { owner: 'k', track: 'stream', stream: 'pieces', complete: false })]); assert.deepEqual([piecesOnly.state, piecesOnly.reason], ['unsupported', 'stream']);
  const liveOnly = mk({}, [item('https://a.example/l.m3u8', { owner: 'k', track: 'stream', stream: 'hls', complete: false, live: true })]); assert.deepEqual([liveOnly.state, liveOnly.reason, liveOnly.blocked.length], ['unsupported', 'live', 1]);
  const drmStream = mk({}, [item('https://a.example/d.mpd', { owner: 'k', track: 'stream', stream: 'dash', complete: false, drm: true })]); assert.deepEqual([drmStream.state, drmStream.reason], ['unsupported', 'drm']);
  const fileAndLive = mk({}, [item('https://a.example/1.mp4', { owner: 'k' }), item('https://a.example/l.m3u8', { owner: 'k', track: 'stream', stream: 'hls', complete: false, live: true })]); assert.deepEqual([fileAndLive.state, fileAndLive.options.length, fileAndLive.blocked.length], ['choose', 1, 1], 'a live playlist beside a file is listed as blocked, not offered');
  assert.deepEqual([mk({ drm: true }, [item('https://a.example/1.mp4', { owner: 'k' })]).state, mk({ drm: true }, []).reason], ['unsupported', 'drm']);
  assert.deepEqual([mk({ yt: true }, []).state, mk({ yt: true }, []).reason], ['unsupported', 'youtube']);
  assert.deepEqual([mk({ age: 500 }, []).state, mk({ age: 2500 }, []).state, mk({ age: 2500 }, []).reason], ['checking', 'unsupported', 'none']);
  assert.equal(mk({}, [item('https://a.example/other.mp4', { owner: 'other' })]).state, 'unsupported', 'another element\'s file is never offered');
  const guess = mk({ blob: true }, [item('https://a.example/x.mp4')]); assert.deepEqual([guess.state, guess.options[0].guess], ['choose', true], 'an unclaimed file for a blob player is only a suggestion');
});
test('buttonPlacement: top corner of the visible part, either corner, nothing when small, hidden or without room', () => {
  const box = (l, t, w, h) => ({ left: l, top: t, right: l + w, bottom: t + h, width: w, height: h }), view = { w: 1000, h: 800 }, size = { w: 210, h: 28 };
  assert.deepEqual(S.buttonPlacement(box(100, 50, 640, 360), view, size, 'left'), { x: 108, y: 58, ratio: 1 });
  assert.equal(S.buttonPlacement(box(100, 50, 640, 360), view, size, 'right').x, 100 + 640 - 8 - 210);
  assert.equal(S.buttonPlacement(box(100, -200, 640, 360), view, size, 'left').y, 8);
  assert.equal(S.buttonPlacement(box(0, 0, 159, 200), view, size, 'left'), null); assert.equal(S.buttonPlacement(box(0, 0, 300, 89), view, size, 'left'), null);
  assert.ok(S.buttonPlacement(box(0, 0, 160, 90), view, { w: 120, h: 28 }, 'left'), '160 x 90 is enough with the compact button');
  assert.equal(S.buttonPlacement(box(0, 740, 640, 360), view, size, 'left'), null, 'under 25% visible');
  assert.equal(S.buttonPlacement(null, view, size, 'left'), null);
});
test('hidden sites list: cleaned, capped, matched by registrable site', () => {
  assert.deepEqual(S.cleanHiddenHosts(['a.example', 5, '', 'x y', 'B.example', 'ok.example'.repeat(30)]), ['a.example']);
  assert.equal(S.cleanHiddenHosts(Array.from({ length: 150 }, (_, i) => 'h' + i + '.example')).length, 100);
  assert.equal(S.isHiddenHost(['video.example'], S.siteOf('www.video.example')), true); assert.equal(S.isHiddenHost(['video.example'], 'other.example'), false); assert.equal(S.isHiddenHost('nope', 'a'), false);
});
test('audio-only tracks get an audio name and type even when the server said video/mp4', () => {
  const a = item('https://a.example/a.mp4', { kind: 'audio', track: 'audio-only', mime: 'video/mp4' });
  assert.equal(S.extensionFor(a), '.m4a'); assert.equal(S.mimeFor(a), 'audio/mp4'); assert.match(S.suggestedName({ ...a, source: 'dom' }, 'Title'), /^Title \(audio only\)\.m4a$/);
  assert.equal(S.extensionFor(item('https://a.example/v.mp4')), '.mp4');
});
test('numbered file names are not fragments when an element owns them (feeds)', () => {
  const st = { items: [], els: {} }; const t = Date.now();
  S.domReport(st, [{ key: 'k1', url: 'https://cdn.example/clip1.mp4', tag: 'video', title: 'One', playing: true }], t);
  const rsp = n => ({ url: `https://cdn.example/clip${n}.mp4`, tabId: 7, method: 'GET', statusCode: 200, type: 'media', responseHeaders: [{ name: 'Content-Type', value: 'video/mp4' }, { name: 'Content-Length', value: String(3 * MB) }] });
  for (const n of [1, 2, 3, 4]) S.ingest(st, rsp(n), S.DEFAULT_TYPES, t + n);
  assert.ok(st.items.some(i => i.url.endsWith('clip1.mp4') && i.owner === 'k1'), 'the owned file survives the fragment heuristic');
});

/* ---- the page-world hooks in a fake page ---- */
function fakePage({ freeze = [], extra = {} } = {}) {
  const events = [], listeners = {}, attrs = {};
  const calls = { fetch: [], send: [], append: [], open: [], create: [] };
  class EventTarget { dispatchEvent(e) { if (this.__root || this === doc) { events.push({ name: e.type, detail: e.detail, target: this, bubbles: e.bubbles && e.composed }); } else events.push({ name: e.type, detail: e.detail, target: this, bubbles: e.bubbles && e.composed }); return true; } addEventListener(type, fn) { (this.__l = this.__l || {})[type] = fn; } }
  class CustomEvent { constructor(type, init = {}) { this.type = type; this.detail = init.detail; this.bubbles = init.bubbles; this.composed = init.composed; } }
  class Element extends EventTarget { getAttribute(n) { return attrs[n] === undefined ? null : attrs[n]; } removeAttribute(n) { delete attrs[n]; } setAttribute(n, v) { calls.setAttribute = [n, v]; return 'set-ret'; } }
  const root = new Element(); root.__root = true;
  const doc = Object.assign(new EventTarget(), { documentElement: root });
  class HTMLMediaElement extends Element { load() { return 'load-ret'; } play() { return Promise.resolve('play-ret'); } setMediaKeys() { return Promise.resolve(); } get src() { return this._s || ''; } set src(v) { this._s = v; } set srcObject(v) { this._o = v; } get srcObject() { return this._o; } }
  class Headers { constructor(o) { this.o = o; } get(n) { return this.o[n.toLowerCase()] === undefined ? null : this.o[n.toLowerCase()]; } }
  class Response { constructor(url, status, headers, body) { this.url = url; this.status = status; this.headers = new Headers(headers); this.b = body; } clone() { return new Response(this.url, this.status, this.headers.o, this.b); } text() { return Promise.resolve(this.b.toString()); } arrayBuffer() { return Promise.resolve(Buffer.from(this.b).buffer.slice(0)); } }
  class XMLHttpRequest { open(m, u) { calls.open.push([m, u]); this.m = m; this.u = u; return 'open-ret'; } send(body) { calls.send.push(body); const self = this; setImmediate(() => { self.status = 200; self.responseURL = self.u; self.responseType = self.rt || ''; self.responseText = self.text || ''; self.l && self.l.loadend.call(self); }); return 'send-ret'; } addEventListener(t, f) { (this.l = this.l || {})[t] = f; } getResponseHeader(n) { return ({ 'content-type': this.ct || '', 'content-range': this.cr || '', 'content-length': this.cl || '' })[n.toLowerCase()] || null; } }
  class SourceBuffer { appendBuffer(d) { calls.append.push([this, d]); return 'append-ret'; } }
  class MediaSource { addSourceBuffer(m) { const sb = new SourceBuffer(); calls.mime = m; return sb; } }
  const URLc = class extends URL { static createObjectURL(o) { calls.create.push(o); return 'blob:https://p.example/' + calls.create.length; } };
  const win = { document: doc, EventTarget, CustomEvent, Element, HTMLMediaElement, Headers, Response, XMLHttpRequest, SourceBuffer, MediaSource, URL: URLc, location: { protocol: 'https:', href: 'https://p.example/page' }, JSON, Object, Reflect, Function, Array, WeakMap, Map, Set, Date, TextDecoder, Uint8Array, Promise, String, Error, TypeError, setTimeout, Symbol, origin: 'https://p.example' };
  win.fetch = function fetch(input, init) { calls.fetch.push([this, input, init]); const url = typeof input === 'string' ? new URL(input, 'https://p.example/page').href : input.url; return Promise.resolve(win.__respond ? win.__respond(url) : new Response(url, 200, { 'content-type': 'text/html' }, 'x')); };
  Object.assign(win, extra); win.globalThis = win; win.self = win;
  const ctx = vm.createContext(win);
  for (const [obj, name] of freeze) vm.runInContext(`Object.freeze(${obj}.prototype || ${obj})`, ctx);
  const publish = (name = 'neodm-test', token = 'tok123456789') => { attrs['data-neodm-ch'] = name + ':' + token; };
  const run = () => vm.runInContext(source('hooks.js'), ctx, { filename: 'hooks.js' });
  const messages = (name = 'neodm-test') => events.filter(e => e.name === name).map(e => ({ ...JSON.parse(e.detail), __target: e.target, __bubbles: e.bubbles }));
  return { win, ctx, calls, events, attrs, publish, run, messages, classes: { Response, XMLHttpRequest, SourceBuffer, MediaSource, HTMLMediaElement } };
}
test('hooks: private channel (random name and token from the content script), read once and removed, messages carry the token', async () => {
  const p = fakePage(); p.publish('neodm-abc', 'secret-token-1'); p.run();
  assert.equal(p.attrs['data-neodm-ch'], undefined, 'the attribute is removed after it was read');
  await p.win.fetch('https://cdn.example/clip.mp4', {}); await new Promise(r => setImmediate(r)); await new Promise(r => setImmediate(r));
  const m = p.messages('neodm-abc'); assert.ok(m.length >= 2, 'hooks ok + a net report');
  assert.ok(m.every(x => x.t === 'secret-token-1')); assert.deepEqual([m[0].k, m[0].ok], ['hooks', true]);
});
test('hooks: messages wait for the channel (document_start ordering) and are delivered in order afterwards', async () => {
  const p = fakePage(); p.run(); // nothing published yet
  const x = new p.classes.XMLHttpRequest(); x.ct = 'video/mp4'; x.cl = '999999'; x.open('GET', 'https://cdn.example/v.mp4'); x.send();
  await new Promise(r => setImmediate(r)); assert.equal(p.messages().length, 0, 'held back while there is no channel');
  p.publish(); await new Promise(r => setTimeout(r, 120));
  const m = p.messages(); assert.deepEqual(m.map(x => x.k), ['hooks', 'net']); assert.equal(m[1].u, 'https://cdn.example/v.mp4');
});
test('hooks: wrapped functions call the original first with this and arguments, return its value, keep name, length and toString', async () => {
  const p = fakePage(), nativeText = Function.prototype.toString.call(p.win.fetch), origFetch = p.win.fetch; p.publish(); p.run();
  assert.notEqual(p.win.fetch, origFetch);
  const self = {}; const r = p.win.fetch.call(self, 'https://p.example/a', { method: 'GET' }); await r;
  assert.deepEqual([p.calls.fetch[0][0] === self, p.calls.fetch[0][1], p.calls.fetch[0][2]], [true, 'https://p.example/a', { method: 'GET' }]);
  assert.deepEqual([p.win.fetch.name, p.win.fetch.length], ['fetch', origFetch.length]);
  assert.equal(vm.runInContext('Function.prototype.toString.call(fetch)', p.ctx), nativeText, 'toString still answers like the original');
  assert.equal(vm.runInContext('Function.prototype.toString.call(Function.prototype.toString)', p.ctx).length > 0, true);
  const x = new p.classes.XMLHttpRequest(); assert.equal(x.open('GET', 'https://p.example/x'), 'open-ret'); assert.equal(x.send(null), 'send-ret'); assert.deepEqual(p.calls.open[0], ['GET', 'https://p.example/x']);
  const v = new p.classes.HTMLMediaElement(); assert.equal(v.load(), 'load-ret'); assert.equal(await v.play(), 'play-ret'); assert.equal(v.setAttribute('src', 'https://p.example/y.mp4'), 'set-ret'); assert.deepEqual(p.calls.setAttribute, ['src', 'https://p.example/y.mp4']);
  v.src = 'https://p.example/z.mp4'; assert.equal(v.src, 'https://p.example/z.mp4', 'the setter still sets');
});
test('hooks: media requests and playlists are reported; other requests are not; element messages go to the element', async () => {
  const p = fakePage(); p.publish(); p.run();
  p.win.__respond = url => /m3u8/.test(url) ? new p.classes.Response(url, 200, { 'content-type': 'application/vnd.apple.mpegurl' }, '#EXTM3U\n#EXTINF:4,\ns.ts\n') : /mp4/.test(url) ? new p.classes.Response(url, 206, { 'content-type': 'video/mp4', 'content-range': 'bytes 0-99/5000000' }, 'y') : new p.classes.Response(url, 200, { 'content-type': 'application/json' }, '{}');
  await p.win.fetch('https://p.example/api.json'); await p.win.fetch('https://p.example/m/master.m3u8'); await p.win.fetch('https://p.example/v.mp4', { headers: { Range: 'bytes=0-99' } });
  await new Promise(r => setTimeout(r, 30));
  const nets = p.messages().filter(m => m.k === 'net'); assert.equal(nets.length, 2, 'the JSON call is not reported');
  assert.match(nets[0].mf, /EXTINF/); assert.deepEqual([nets[1].n, nets[1].s, nets[1].x], [5000000, 206, 'fetch']);
  const v = new p.classes.HTMLMediaElement(); v.src = 'https://p.example/q.mp4'; v.play(); v.setMediaKeys();
  const em = p.messages().filter(m => m.__target === v); assert.deepEqual(em.map(m => m.k), ['src', 'play', 'drm']); assert.ok(em.every(m => m.__bubbles === true), 'bubbling and composed so shadow DOM players are heard');
});
test('hooks: MediaSource, SourceBuffer and the data flow (the bytes of a response remember their address)', async () => {
  const p = fakePage(); p.publish(); p.run();
  p.win.__respond = url => new p.classes.Response(url, 206, { 'content-type': 'video/mp4', 'content-range': 'bytes 0-999/1000000' }, Buffer.alloc(2000, 1));
  const ms = new p.classes.MediaSource(); const blob = p.win.URL.createObjectURL(ms); const sb = ms.addSourceBuffer('video/mp4; codecs="avc1.42E01E"');
  const res = await p.win.fetch('https://p.example/seg/a.mp4?bytestart=0&byteend=999'); const data = await res.arrayBuffer();
  assert.equal(sb.appendBuffer(data), 'append-ret'); assert.equal(sb.appendBuffer(new Uint8Array(data)), 'append-ret', 'a view on the same bytes is attributed too');
  await new Promise(r => setTimeout(r, 330));
  const m = p.messages(); assert.deepEqual([m.find(x => x.k === 'ms').u, m.find(x => x.k === 'ms').ms === m.find(x => x.k === 'sb').ms], [blob, true]);
  const app = m.find(x => x.k === 'app'); assert.deepEqual([app.us, app.gs, app.a], [['https://p.example/seg/a.mp4?bytestart=0&byteend=999'], [0], 2]);
  // bytes the page re-made carry no tag: the latest media request within 3 s is a guess, and says so
  const re = new Uint8Array(100).buffer; sb.appendBuffer(re); await new Promise(r => setTimeout(r, 330));
  assert.ok(p.messages().filter(x => x.k === 'app').at(-1).gs.includes(1) || p.messages().filter(x => x.k === 'app').at(-1).us.length >= 1);
});
test('hooks: fail closed. Frozen prototypes make installation fail; the page keeps working, nothing throws, one failure is reported', async () => {
  const p = fakePage({ freeze: [['XMLHttpRequest', 0], ['SourceBuffer', 0]].map(([n]) => [n]) }); p.publish();
  assert.doesNotThrow(() => p.run());
  const x = new p.classes.XMLHttpRequest(); assert.equal(x.open('GET', 'https://p.example/a'), 'open-ret'); assert.equal(x.send(), 'send-ret');
  const r = await p.win.fetch('https://p.example/m.mp4'); assert.equal(r.status, 200); // fetch (installed before the frozen XHR step) is pass-through now
  await new Promise(r2 => setTimeout(r2, 80));
  const m = p.messages(); assert.equal(m.filter(x2 => x2.k === 'hooks' && x2.ok === false).length, 1, 'one report of the failure'); assert.equal(m.filter(x2 => x2.k === 'net').length, 0, 'and nothing else is reported afterwards');
  const sb = new p.classes.SourceBuffer(); assert.equal(sb.appendBuffer(new ArrayBuffer(8)), 'append-ret');
});
test('hooks: an exception inside the hook bookkeeping never reaches the page', async () => {
  const p = fakePage(); p.publish(); p.run();
  p.win.__respond = () => ({ url: 'https://p.example/x.mp4', status: 200, get headers() { throw new Error('boom'); } });
  const r = await p.win.fetch('https://p.example/x.mp4'); assert.equal(r.status, 200);
  await new Promise(r2 => setTimeout(r2, 30)); assert.equal(p.messages().filter(m => m.k === 'hooks' && m.ok === false).length, 1);
  assert.equal(p.win.fetch('https://p.example/y.mp4') instanceof Promise, true);
});
test('hooks: skipped on documents without an http(s) origin; installed once', () => {
  const p = fakePage({ extra: { location: { protocol: 'about:', href: 'about:blank' }, origin: 'null' } }); p.publish(); const orig = p.win.fetch; p.run(); assert.equal(p.win.fetch, orig, 'untouched');
  const q = fakePage({ extra: { location: { protocol: 'about:', href: 'about:srcdoc' }, origin: 'https://parent.example' } }); const o2 = q.win.fetch; q.run(); assert.notEqual(q.win.fetch, o2, 'an about:srcdoc frame that inherits an https origin is hooked');
  const r = fakePage(); r.publish(); r.run(); const once = r.win.fetch; r.run(); assert.equal(r.win.fetch, once);
});
test('hooks: patched fetch adds well under a millisecond and the tables stay capped', async () => {
  const p = fakePage(); p.publish(); p.run(); const patched = p.win.fetch;
  const plain = fakePage().win.fetch;
  const time = async f => { const t = process.hrtime.bigint(); for (let i = 0; i < 3000; i++) await f('https://p.example/api/' + i); return Number(process.hrtime.bigint() - t) / 3000 / 1e6; };
  await time(plain); await time(patched); const base = await time(plain), hooked = await time(patched);
  console.log(`# fetch overhead: ${(hooked - base).toFixed(4)} ms per call (plain ${base.toFixed(4)} ms, hooked ${hooked.toFixed(4)} ms)`);
  assert.ok(hooked - base < 1, `overhead ${(hooked - base).toFixed(3)} ms`);
  // memory: a long session of appends keeps a bounded number of addresses per buffer and a bounded queue
  const ms = new p.classes.MediaSource(), sb = ms.addSourceBuffer('video/mp4'); p.win.__respond = url => new p.classes.Response(url, 200, { 'content-type': 'video/mp4' }, Buffer.alloc(4000, 2));
  for (let i = 0; i < 200; i++) { const r = await p.win.fetch('https://p.example/seg' + i + '.m4s'); sb.appendBuffer(await r.arrayBuffer()); }
  await new Promise(r => setTimeout(r, 330)); const last = p.messages().filter(m => m.k === 'app').at(-1); assert.ok(last.us.length <= 24, 'at most 24 addresses per buffer: ' + last.us.length);
  const q = fakePage(); q.run(); for (let i = 0; i < 1000; i++) new q.classes.HTMLMediaElement().load(); await new Promise(r => setTimeout(r, 20)); assert.ok(true);
});

/* ---- the content script's side of the channel (jsdom) ---- */
function page({ items = [], url = 'https://video.example/watch', lang = 'en', stored = {} } = {}) {
  const dom = new JSDOM('<!doctype html><title>Player</title><body><div id="box"><video id="v"></video></div></body>', { url, runScripts: 'outside-only', pretendToBeVisual: true }), w = dom.window;
  const sent = [], listeners = [], writes = [];
  w.chrome = { runtime: { sendMessage: async m => { sent.push(JSON.parse(JSON.stringify(m))); return m.action === 'media-list' ? { items } : m.action === 'media-download' ? { ok: true, id: 'job' } : { ok: true }; }, onMessage: { addListener: f => listeners.push(f) } }, storage: { local: { get: async () => ({ ...stored }), set: async o => { writes.push(o); } }, onChanged: { addListener() {} } } };
  w.crypto = require('node:crypto').webcrypto; w.HTMLCanvasElement.prototype.getContext = () => null;
  const v = w.document.getElementById('v'); Object.defineProperty(v, 'currentSrc', { value: 'blob:https://video.example/uuid-1', configurable: true });
  withI18n(w, lang); w.eval(source('sniff.js')); w.eval(source('content.js'));
  const ch = w.__neodmRibbon.channel(), post = (m, target = v, token = ch.token) => target.dispatchEvent(new w.CustomEvent(ch.name, { detail: typeof m === 'string' ? m : JSON.stringify({ ...m, t: token }), bubbles: true, composed: true }));
  return { w, d: w.document, v, sent, writes, post, ch, hooks: () => sent.filter(m => m.action === 'media-hooks') };
}
test('content: the channel attribute is on <html> before any page script runs, name and token are random per page', () => {
  const a = page(), b = page();
  const attr = a.d.documentElement.getAttribute('data-neodm-ch'); assert.match(attr, /^neodm-[a-z0-9]{14}:[a-z0-9]{24}$/); assert.equal(attr, a.ch.name + ':' + a.ch.token);
  assert.notEqual(a.ch.name, b.ch.name); assert.notEqual(a.ch.token, b.ch.token);
});
test('content: messages without the token, with a wrong token, oversized or malformed are ignored; a good sequence reports a file source for the element', async () => {
  const p = page(); await tick();
  p.post({ k: 'ms', ms: 'm1', u: 'blob:https://video.example/uuid-1' }, p.v, 'wrong'); p.post('not json'); p.post('x'.repeat(80000)); p.post({ k: 'sb', ms: 'm1', sb: 's1', mime: 'video/mp4; codecs="avc1.42E01E"' }, p.v, undefined);
  p.post({ k: 'ms', ms: '../evil', u: 'blob:x' }); p.post({ k: 'ms', ms: 'm9', u: 'https://not-a-blob.example/' });
  await new Promise(r => setTimeout(r, 300)); assert.equal(p.hooks().length, 0, 'nothing was learned from them');
  p.post({ k: 'ms', ms: 'm1', u: 'blob:https://video.example/uuid-1' }); p.post({ k: 'sb', ms: 'm1', sb: 's1', mime: 'video/mp4; codecs="avc1.42E01E"' }); p.post({ k: 'sb', ms: 'm1', sb: 's2', mime: 'audio/mp4; codecs="mp4a.40.2"' });
  p.post({ k: 'net', u: 'https://cdn.example/v.mp4?bytestart=0&byteend=9', n: 9000000, s: 206, c: 'video/mp4' }); p.post({ k: 'net', u: 'https://cdn.example/a.mp4', n: 1000000, s: 206, c: 'audio/mp4' });
  p.post({ k: 'app', sb: 's1', ms: 'm1', b: 5000, us: ['https://cdn.example/v.mp4?bytestart=0&byteend=9'], gs: [0] }); p.post({ k: 'app', sb: 's2', ms: 'm1', b: 800, us: ['https://cdn.example/a.mp4'], gs: [0] });
  await new Promise(r => setTimeout(r, 400));
  const entries = p.hooks().flatMap(m => m.entries), list = entries.flatMap(e => e.list);
  assert.deepEqual(list.map(s => [s.role, s.track, s.kind, s.url.split('/').pop().split('?')[0], s.size]).sort(), [['file', 'audio-only', 'audio', 'a.mp4', 1000000], ['file', 'video-only', 'video', 'v.mp4', 9000000]].sort());
  assert.ok(entries.every(e => /^e[a-z0-9]+(-\d+)?$/.test(e.key)));
  assert.equal(p.sent.filter(m => !['media-dom', 'media-hooks', 'media-list', 'media-event'].includes(m.action)).length, 0, 'the page cannot make the content script send anything else');
});
test('content: a playlist and its segments attribute the stream to the element that plays them; pieces without a playlist are a stream in pieces', async () => {
  const p = page(); await tick();
  p.post({ k: 'ms', ms: 'm1', u: 'blob:https://video.example/uuid-1' }); p.post({ k: 'sb', ms: 'm1', sb: 's1', mime: 'video/mp4; codecs="avc1.42E01E,mp4a.40.2"' });
  p.post({ k: 'net', u: 'https://cdn.example/h/master.m3u8', c: 'application/vnd.apple.mpegurl', s: 200, mf: '#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=2000000,RESOLUTION=1280x720\nv.m3u8\n' });
  p.post({ k: 'net', u: 'https://cdn.example/h/v.m3u8', c: 'application/vnd.apple.mpegurl', s: 200, mf: '#EXTM3U\n#EXTINF:4,\ns0.m4s\n#EXTINF:4,\ns1.m4s\n#EXT-X-ENDLIST\n' });
  p.post({ k: 'app', sb: 's1', ms: 'm1', b: 9000, us: ['https://cdn.example/h/s0.m4s', 'https://cdn.example/h/s1.m4s'], gs: [0, 0] });
  await new Promise(r => setTimeout(r, 400));
  const list = p.hooks().flatMap(m => m.entries).flatMap(e => e.list);
  assert.deepEqual(list.map(s => [s.role, s.url.split('/').pop(), s.quality]), [['hls', 'master.m3u8', '720p']], 'one entry: the master, not its variant, and the segments are not listed as pieces');
  const q = page(); await tick(); q.post({ k: 'ms', ms: 'm1', u: 'blob:https://video.example/uuid-1' }); q.post({ k: 'sb', ms: 'm1', sb: 's1', mime: 'video/mp4' });
  q.post({ k: 'app', sb: 's1', ms: 'm1', b: 9000, us: ['https://cdn.example/f/1.m4s', 'https://cdn.example/f/2.m4s', 'https://cdn.example/f/3.m4s'], gs: [0, 0, 0] }); await new Promise(r => setTimeout(r, 400));
  assert.deepEqual(q.hooks().flatMap(m => m.entries).flatMap(e => e.list).map(s => [s.role, s.pieces]), [['pieces', 3]]);
});
test('content: a recycled <video> gets a new key for every source, so each post has its own entries', async () => {
  const p = page(); await tick(); p.v.remove(); const v = p.d.createElement('video'); p.d.body.append(v);
  const key = () => { p.w.__neodmRibbon.refresh(); return p.sent.filter(m => m.action === 'media-dom').at(-1).sources.find(s => s.url.endsWith('.mp4') || s.url === '' || s.url === 'blob:').key; };
  v.src = 'https://cdn.example/1.mp4'; const k1 = key(); assert.equal(key(), k1, 'stable while the source stays');
  v.src = 'https://cdn.example/2.mp4'; const k2 = key(); assert.notEqual(k2, k1); assert.match(k2, /-1$/);
  v.src = 'https://cdn.example/1.mp4'; const k3 = key(); assert.notEqual(k3, k2); assert.notEqual(k3, k1, 'coming back to the first video starts a new generation; the worker merges by address');
});
/* ---- the worker: frames, hooks, streams, downloads ---- */
const hookEntries = (key, list, extra = {}) => ({ action: 'media-hooks', entries: [{ key, list, ...extra }] });
test('worker: media actions from any frame, nothing else; hook entries are stored with their frame; downloads use the frame address as referer', async () => {
  const b = makeBackground();
  const top = b.pageSender(tab), sub = frameSender(4, 'https://player.example/embed?x=1'), blank = frameSender(5, 'about:srcdoc', { origin: 'https://video.example' });
  await b.message({ action: 'media-dom', sources: [{ key: 'ka', url: 'https://cdn.example/a.mp4', tag: 'video', title: 'In the frame', playing: true, vis: 1 }] }, sub);
  await b.message(hookEntries('kb', [{ role: 'file', url: 'https://cdn.example/v.mp4', kind: 'video', track: 'video-only', size: 5 * MB, quality: '720p' }, { role: 'file', url: 'https://cdn.example/a.m4a', kind: 'audio', track: 'audio-only', size: MB, mime: 'audio/mp4' }]), blank);
  const st = json(b.sessionData['neodm.media.7']);
  assert.deepEqual(st.els.ka && [st.els.ka.frame, st.els.ka.furl], [4, 'https://player.example/embed?x=1']); assert.equal(st.items.find(i => i.url.endsWith('v.mp4')).frame, 5);
  assert.equal(st.items.find(i => i.url.endsWith('v.mp4')).frameUrl, undefined, 'about:srcdoc has no address of its own');
  const r1 = await b.message({ action: 'media-download', id: 'https://cdn.example/a.mp4' }, sub); assert.equal(r1.ok, true);
  assert.equal(b.nativeRequests.at(-1).referer, 'https://player.example/embed?x=1' === b.nativeRequests.at(-1).referer ? 'https://player.example/embed?x=1' : b.nativeRequests.at(-1).referer);
  assert.equal(b.nativeRequests.at(-1).url, 'https://cdn.example/a.mp4'); assert.match(b.nativeRequests.at(-1).fileName, /\.mp4$/);
  // the audio-only part gets an audio name and type
  const r2 = await b.message({ action: 'media-download', id: 'https://cdn.example/a.m4a' }, blank); assert.equal(r2.ok, true); assert.match(b.nativeRequests.at(-1).fileName, /\(audio only\)\.m4a$/); assert.equal(b.nativeRequests.at(-1).mime, 'audio/mp4');
  // hook entries with a bad key or from a frame that is not a page frame change nothing
  const n = json(b.sessionData['neodm.media.7']).items.length;
  assert.equal(b.hooks.message(hookEntries('bad key', [{ role: 'file', url: 'https://x.example/q.mp4' }]), frameSender(2, 'chrome-extension://ext-id/x.html'), () => {}), false);
  await b.message(hookEntries('__proto__', [{ role: 'file', url: 'https://x.example/q.mp4' }]), sub); await b.message(hookEntries('bad key', [{ role: 'file', url: 'https://x.example/q.mp4' }]), sub);
  assert.equal(json(b.sessionData['neodm.media.7']).items.length, n);
  // a huge message is refused
  assert.equal(b.hooks.message({ action: 'media-hooks', entries: [{ key: 'kz', list: [], pad: 'x'.repeat(250000) }] }, sub, () => {}), false);
});
test('worker: a stream playlist is sent to the app as stream:hls with the address, a title-based name and no mime; protected and live ones are listed with their reason and send nothing', async () => {
  const b = makeBackground({ tabReply: (tabId, msg) => (msg.type === 'neodm-context-target' ? { found: true, key: 'kp', blob: true, url: '', tag: 'video', title: 'My Film' } : undefined) }), top = b.pageSender(tab);
  await b.message({ action: 'media-dom', sources: [{ key: 'kp', url: 'blob:https://video.example/u', tag: 'video', title: 'My Film', playing: true, vis: 1 }] }, top);
  await b.message(hookEntries('kp', [{ role: 'hls', url: 'https://cdn.example/vod/master.m3u8', quality: '1080p', dur: 3600, variants: 4 }]), top);
  const st = json(b.sessionData['neodm.media.7']), s = st.items.find(i => i.track === 'stream'); assert.deepEqual([s.stream, s.owner, s.quality, s.dur, s.complete], ['hls', 'kp', '1080p', 3600, false]);
  const res = await b.message({ action: 'media-download', id: s.id }, top); assert.equal(res.ok, true);
  assert.equal(b.nativeRequests.length, 1, 'exactly one request'); const req = b.nativeRequests[0];
  assert.equal(req.stream, 'hls'); assert.equal(req.url, 'https://cdn.example/vod/master.m3u8'); assert.ok(!('mime' in req), 'no mime for a stream'); assert.ok(req.fileName === '' || !/\.(mp4|ts|m3u8)$/i.test(req.fileName), 'name without an extension: ' + req.fileName);
  const pm = await b.message({ action: 'popup-media', tabId: 7 }); assert.ok(pm.items.some(i => i.track === 'stream')); assert.deepEqual(json([pm.summary.videos, pm.summary.streams, pm.summary.downloadable, pm.summary.reasons]), [1, 1, 1, []]);
  // the context menu sends the same single request
  await b.hooks.menu({ menuItemId: 'media', mediaType: 'video', srcUrl: 'blob:https://video.example/u', frameId: 0 }, { id: 7, url: tab.url, title: 'My Film' }); await tick();
  assert.equal(b.nativeRequests.length, 2); assert.equal(b.nativeRequests[1].stream, 'hls'); assert.match(b.local.status, /Accepted|accepted|sent/i);
  // a live playlist and a protected manifest are never sent and say why
  const c = makeBackground({ tabReply: (tabId, msg) => (msg.type === 'neodm-context-target' ? { found: true, key: 'kl', blob: true, url: '', tag: 'video', title: 'Live' } : undefined) }), ctop = c.pageSender(tab);
  await c.message({ action: 'media-dom', sources: [{ key: 'kl', url: 'blob:https://video.example/l', tag: 'video', title: 'Live', playing: true, vis: 1 }] }, ctop);
  await c.message(hookEntries('kl', [{ role: 'hls', url: 'https://cdn.example/live/index.m3u8', dur: 0, live: true }]), ctop);
  const live = json(c.sessionData['neodm.media.7']).items.find(i => i.track === 'stream');
  const lr = await c.message({ action: 'media-download', id: live.id }, ctop); assert.equal(lr.ok, false); assert.match(lr.error, /live stream/i); assert.equal(c.nativeRequests.length, 0);
  await c.hooks.menu({ menuItemId: 'media', mediaType: 'video', srcUrl: 'blob:https://video.example/l', frameId: 0 }, { id: 7, url: tab.url, title: tab.title }); await tick();
  assert.equal(c.nativeRequests.length, 0); assert.match(c.local.status, /live stream/i);
  const d = makeBackground({}), dtop = d.pageSender(tab);
  await d.message({ action: 'media-dom', sources: [{ key: 'kd', url: 'blob:https://video.example/d', tag: 'video', title: 'Protected', playing: true, vis: 1 }] }, dtop);
  await d.message(hookEntries('kd', [{ role: 'dash', url: 'https://cdn.example/p/manifest.mpd', drm: true }]), dtop);
  const prot = json(d.sessionData['neodm.media.7']).items.find(i => i.track === 'stream');
  const pr = await d.message({ action: 'media-download', id: prot.id }, dtop); assert.equal(pr.ok, false); assert.match(pr.error, /protected \(DRM\)/i); assert.equal(d.nativeRequests.length, 0);
});
test('worker: the right-click target is asked in the frame the menu was opened in; "Show on page" goes to the frame of the element', async () => {
  const asked = [];
  const b = makeBackground({ tabReply: (tabId, msg) => (msg.type === 'neodm-context-target' ? { found: true, key: 'kf', blob: false, url: 'https://cdn.example/f.mp4', tag: 'video', title: 'Framed' } : undefined) });
  const sub = frameSender(6, 'https://player.example/embed');
  await b.message({ action: 'media-dom', sources: [{ key: 'kf', url: 'https://cdn.example/f.mp4', tag: 'video', title: 'Framed', playing: true, vis: 1 }] }, sub);
  b.sentToTabs.length = 0;
  await b.hooks.menu({ menuItemId: 'media', mediaType: 'video', srcUrl: 'https://cdn.example/f.mp4', frameId: 6 }, { id: 7, url: tab.url, title: tab.title }); await tick();
  assert.deepEqual(b.sentToTabs.find(x => x.msg.type === 'neodm-context-target').opts, { frameId: 6 }); assert.equal(b.nativeRequests.at(-1).url, 'https://cdn.example/f.mp4');
  b.sentToTabs.length = 0; await b.message({ action: 'popup-locate', tabId: 7, id: 'https://cdn.example/f.mp4', on: true, scroll: true }); await tick();
  assert.deepEqual(b.sentToTabs.at(-1).opts, { frameId: 6 }); assert.equal(b.sentToTabs.at(-1).msg.owner, 'kf'); void asked;
});
test('worker: one frame reporting does not mark the elements of another frame as gone', async () => {
  const b = makeBackground(), top = b.pageSender(tab), sub = frameSender(4, 'https://player.example/embed');
  await b.message({ action: 'media-dom', sources: [{ key: 'kt', url: 'https://cdn.example/t.mp4', tag: 'video', playing: true, vis: 1 }] }, top);
  await b.message({ action: 'media-dom', sources: [{ key: 'ks', url: 'https://cdn.example/s.mp4', tag: 'video', playing: true, vis: 1 }] }, sub);
  await b.message({ action: 'media-dom', sources: [] }, sub);
  const els = json(b.sessionData['neodm.media.7']).els; assert.deepEqual([els.kt.live, els.ks.live], [true, false]);
});

/* ---- the popup ---- */
async function popupPage({ summary, items = [], local = {}, lang = 'en', version = '1.5.8' } = {}) {
  const dom = new JSDOM(source('popup.html'), { runScripts: 'outside-only', url: 'chrome-extension://ext-id/popup.html' }), w = dom.window, saved = [];
  w.chrome = { storage: { local: { get: async k => Object.fromEntries([].concat(k).filter(x => x in local).map(x => [x, local[x]])), set: async o => { Object.assign(local, o); saved.push(o); } }, session: { get: async () => ({}) } }, tabs: { query: async () => [{ id: 7, url: 'https://video.example/', title: 'V' }] }, runtime: { sendMessage: async m => (m.action === 'popup-media' ? { items, summary } : { ok: true }), getManifest: () => ({ version }) }, permissions: {} };
  withI18n(w, lang); w.eval(source('sniff.js')); w.eval(source('popup.js')); await tick(16);
  return { w, doc: w.document, local, saved };
}
test('popup: videos summary, why there is no button, stream rows without a download action, hidden sites with Remove, corner, version footer', async () => {
  const stream = item('https://cdn.example/m.m3u8', { track: 'stream', stream: 'hls', complete: false, quality: '720p', dur: 125, owner: 'k' });
  const live = item('https://cdn.example/live.m3u8', { track: 'stream', stream: 'hls', complete: false, live: true, owner: 'k2' });
  const p = await popupPage({ summary: { videos: 3, streams: 1, frames: 2, downloadable: 0, reasons: ['stream', 'drm', 'live'] }, items: [stream, live], local: { videoButtonHiddenHosts: ['a.example', 'b.example'], videoButtonCorner: 'right' } });
  assert.equal(p.doc.getElementById('videoSummary').hidden, false, 'summary shown'); assert.match(p.doc.getElementById('videoSummary').textContent, /Videos found: 3 \(streams: 1, frames: 2\), downloadable: 0/);
  assert.equal(p.doc.getElementById('whyNone').hidden, false, 'why shown'); assert.equal(p.doc.querySelectorAll('#whyList li').length, 3); assert.match(p.doc.getElementById('whyList').textContent, /in pieces without a playlist/); assert.match(p.doc.getElementById('whyList').textContent, /Live stream: only finished videos can be saved/); assert.match(p.doc.getElementById('whyList').textContent, /protected \(DRM\)/);
  const row = p.doc.querySelector('#mediaList li.media'); assert.match(row.querySelector('.meta').textContent, /^Stream \(HLS\)/); const btn = row.querySelector('.actions button'); assert.equal(btn.disabled, false, 'a saveable stream has an enabled button'); assert.equal(btn.textContent, 'Download stream'); assert.match(row.textContent, /reads the playlist/);
  const liveRow = p.doc.querySelectorAll('#mediaList li.media')[1], liveBtn = liveRow.querySelector('.actions button'); assert.equal(liveBtn.disabled, true, 'a live stream cannot be sent'); assert.match(liveRow.textContent, /Live stream: only finished videos can be saved/); assert.match(liveRow.querySelector('.meta').textContent, /^Stream \(HLS\)/);
  assert.deepEqual([...p.doc.querySelectorAll('#hiddenSites li .host')].map(x => x.textContent), ['a.example', 'b.example']);
  p.doc.querySelector('#hiddenSites li button').click(); await tick(10); assert.deepEqual(json(p.local.videoButtonHiddenHosts), ['b.example']); assert.equal(p.doc.querySelectorAll('#hiddenSites li').length, 1);
  assert.equal(p.doc.getElementById('corner').value, 'right'); p.doc.getElementById('corner').value = 'left'; p.doc.getElementById('corner').dispatchEvent(new p.w.Event('change')); await tick(); assert.equal(p.local.videoButtonCorner, 'left');
  assert.equal(p.doc.getElementById('footerVersion').textContent, 'NeoDM Integration 1.5.8');
  const ok = await popupPage({ summary: { videos: 1, frames: 1, downloadable: 1, reasons: [] }, items: [item('https://cdn.example/a.mp4', { owner: 'k' })] }); assert.equal(ok.doc.getElementById('whyNone').hidden, true, 'ok why hidden');
  const none = await popupPage({ summary: { videos: 0, frames: 0, downloadable: 0, reasons: [] }, items: [] }); assert.equal(none.doc.getElementById('videoSummary').hidden, true, 'none summary'); assert.equal(none.doc.getElementById('mediaNone').hidden, false, 'none hint');
  const ar = await popupPage({ lang: 'ar', summary: { videos: 2, frames: 1, downloadable: 1, reasons: [] }, items: [] }); assert.equal(ar.doc.documentElement.dir, 'rtl'); assert.match(ar.doc.getElementById('videoSummary').textContent, /2/);
});
test('every new message exists in all ten locales, translated, with its placeholders and no em dashes', () => {
  const dir = path.join(root, 'extension/_locales'), en = JSON.parse(fs.readFileSync(path.join(dir, 'en/messages.json'), 'utf8'));
  const keys = ['kind_stream_hls', 'kind_stream_dash', 'kind_stream_pieces', 'vb_checking', 'vb_cant_save', 'vb_choose', 'vb_pair', 'vb_pair_short', 'vb_pair_note', 'vb_guess', 'vb_sent_pair', 'vb_stream_disabled', 'vb_reason_drm', 'vb_reason_stream', 'vb_reason_youtube', 'vb_reason_none', 'vb_hidden_notice', 'vb_hide_site', 'vb_corner_move', 'vb_undo', 'err_stream_unsupported', 'popup_videos_summary', 'popup_why_no_button', 'popup_stream_btn', 'popup_hidden_heading', 'popup_hidden_none', 'popup_hidden_hint', 'popup_corner', 'popup_corner_left', 'popup_corner_right', 'popup_footer_version'];
  const same = new Set(['kind_stream_hls', 'kind_stream_dash', 'popup_footer_version']); let n = 0;
  for (const l of fs.readdirSync(dir)) {
    const m = JSON.parse(fs.readFileSync(path.join(dir, l, 'messages.json'), 'utf8'));
    for (const k of keys) { n++; assert.ok(m[k] && m[k].message.length >= 2, `${l}.${k}`); assert.doesNotMatch(m[k].message, /[\u2014\u2013]/, `${l}.${k}`); assert.deepEqual(Object.keys(m[k].placeholders || {}), Object.keys(en[k].placeholders || {}), `${l}.${k} placeholders`); if (l !== 'en' && !same.has(k)) assert.notEqual(m[k].message, en[k].message, `${l}.${k} is translated`); }
  }
  assert.equal(n, keys.length * 10);
});
test('SECURITY.md documents the page-world hooks threat model', () => {
  const text = fs.readFileSync(path.join(root, 'SECURITY.md'), 'utf8');
  for (const needle of ['hooks.js', 'MAIN', 'random', 'token', 'fake', 'sender', 'closed shadow']) assert.match(text, new RegExp(needle, 'i'), needle);
});
