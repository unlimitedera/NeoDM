'use strict';
/* 1.6.0 part 2: streams in the extension. Manifest classification, row labels, the request a click sends (stream field, address, title name, referer, cookie, identity), locales. */
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const S = require('../extension/sniff.js');
const { makeBackground, json } = require('./extension-harness.cjs');
const root = path.join(__dirname, '..');
const MB = 1024 * 1024;
const item = (url, o = {}) => ({ id: url, url, rawUrl: url, groupKey: url, kind: 'video', track: 'file', complete: true, size: 10 * MB, quality: '', mime: 'video/mp4', source: 'net', ...o });
const hookEntries = (key, list, extra = {}) => ({ action: 'media-hooks', entries: [{ key, list, ...extra }] });
const tab = { id: 7, url: 'https://video.example/watch?v=1', title: 'My Film - Site' };

test('manifest classification: only DRM markers and live playlists are blocked; plain AES-128 and VOD playlists are saveable', () => {
  const media = (extra, end = true) => '#EXTM3U\n#EXT-X-TARGETDURATION:4\n' + extra + '#EXTINF:4.0,\ns0.ts\n#EXTINF:4.0,\ns1.ts\n' + (end ? '#EXT-X-ENDLIST\n' : '');
  const p = (text, url = 'https://cdn.example/v/index.m3u8') => S.parseManifest(text, url);
  assert.deepEqual([p(media('')).drm, p(media('')).live], [false, false]);
  assert.equal(p(media('#EXT-X-KEY:METHOD=AES-128,URI="k.key"\n')).drm, false, 'AES-128 with a key address is plain delivery encryption');
  assert.equal(p(media('#EXT-X-KEY:METHOD=SAMPLE-AES,URI="k.key"\n')).drm, true, 'SAMPLE-AES');
  assert.equal(p(media('#EXT-X-KEY:METHOD=AES-128,URI="skd://abc"\n')).drm, true, 'skd:// key address');
  assert.equal(p(media('#EXT-X-KEY:METHOD=SAMPLE-AES,KEYFORMAT="com.apple.streamingkeydelivery",URI="skd://x"\n')).drm, true, 'FairPlay');
  assert.equal(p(media('#EXT-X-KEY:METHOD=SAMPLE-AES-CTR,KEYFORMAT="urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed",URI="data:x"\n')).drm, true, 'Widevine key format');
  assert.equal(p(media('', false)).live, true, 'no ENDLIST and no VOD type is live');
  assert.equal(p(media('#EXT-X-PLAYLIST-TYPE:VOD\n', false)).live, false, 'a playlist declared VOD is not live even without ENDLIST');
  assert.equal(p(media('#EXT-X-PLAYLIST-TYPE:EVENT\n', false)).live, true, 'an EVENT playlist without an end is live');
  const master = '#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360\nlow.m3u8\n';
  assert.deepEqual([p(master).master, p(master).live], [true, false], 'a master playlist is not judged live');
  const mpd = (attrs, body = '') => '<?xml version="1.0"?><MPD xmlns="urn:mpeg:dash:schema:mpd:2011" ' + attrs + '><Period><AdaptationSet mimeType="video/mp4">' + body + '<Representation id="1" bandwidth="1000" width="640" height="360"/></AdaptationSet></Period></MPD>';
  const d = t => S.parseManifest(t, 'https://cdn.example/d/manifest.mpd');
  assert.deepEqual([d(mpd('type="static" mediaPresentationDuration="PT10M"')).type, d(mpd('type="static"')).live, d(mpd('type="static"')).drm], ['dash', false, false]);
  assert.equal(d(mpd('type="dynamic"')).live, true, 'DASH type dynamic is live');
  assert.equal(d(mpd('type="static"', '<ContentProtection schemeIdUri="urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed"/>')).drm, true, 'ContentProtection');
  assert.equal(d(mpd('type="static"', '<!-- Widevine --><Other/>')).drm, true, 'a Widevine marker');
  assert.deepEqual([S.streamBlock({ track: 'stream', stream: 'hls', drm: true }), S.streamBlock({ track: 'stream', stream: 'dash', live: true }), S.streamBlock({ track: 'stream', stream: 'hls' }), S.streamBlock({ track: 'stream', stream: 'pieces' })], ['drm', 'live', '', 'pieces']);
});

test('row label and action: "Stream (HLS) 1080p 1:42:01", "Download stream"', () => {
  const row = item('https://cdn.example/v/master.m3u8', { track: 'stream', stream: 'hls', complete: false, quality: '1080p', dur: 6121, variants: 4 });
  const text = S.describeItem(row);
  assert.match(text.meta, /kind_stream_hls/); assert.match(text.meta, /1080p/); assert.match(text.meta, /1:42:01/);
  const dash = S.describeItem(item('https://cdn.example/d/manifest.mpd', { track: 'stream', stream: 'dash', complete: false, quality: '720p', dur: 90 }));
  assert.match(dash.meta, /kind_stream_dash/);
  const en = JSON.parse(fs.readFileSync(path.join(root, 'extension/_locales/en/messages.json'), 'utf8'));
  assert.equal(en.kind_stream_hls.message, 'Stream (HLS)'); assert.equal(en.kind_stream_dash.message, 'Stream (DASH)');
  assert.equal(en.vb_download_stream.message, 'Download stream'); assert.equal(en.vb_stream_drm.message, 'Protected (DRM)'); assert.match(en.vb_reason_live.message, /Live stream: only finished videos can be saved/);
});

test('stream requests carry the referer, cookie and browser identity exactly like file requests, and never manifest text', async () => {
  const jar = [{ name: 'sid', value: 'abc', domain: 'cdn.example', path: '/', secure: true }];
  const b = makeBackground({ cookieJar: jar, settings: { cookiesEnabled: true }, tabReply: (id, msg) => (msg.type === 'neodm-context-target' ? { found: true, key: 'kp', blob: true, url: '', tag: 'video', title: 'My Film' } : undefined), navigator: { userAgent: 'Mozilla/5.0 Test Browser/1.0', language: 'en-GB', languages: ['en-GB', 'en'] } });
  const top = b.pageSender(tab);
  await b.message({ action: 'media-dom', sources: [{ key: 'kp', url: 'blob:https://video.example/u', tag: 'video', title: 'My Film', playing: true, vis: 1 }] }, top);
  await b.message(hookEntries('kp', [{ role: 'hls', url: 'https://cdn.example/vod/master.m3u8', quality: '1080p', dur: 6121, variants: 4 }, { role: 'file', url: 'https://cdn.example/clip.mp4', kind: 'video', size: 5 * MB, quality: '360p' }]), top);
  const items = json(b.sessionData['neodm.media.7']).items, stream = items.find(i => i.track === 'stream'), file = items.find(i => i.track === 'file');
  assert.ok(stream && file);
  assert.equal((await b.message({ action: 'media-download', id: file.id }, top)).ok, true);
  assert.equal((await b.message({ action: 'media-download', id: stream.id }, top)).ok, true);
  assert.equal(b.nativeRequests.length, 2, 'one native request per click');
  const [f, s] = b.nativeRequests;
  assert.equal(s.stream, 'hls'); assert.equal(s.url, 'https://cdn.example/vod/master.m3u8'); assert.ok(!('stream' in f), 'a file request has no stream field');
  for (const k of ['referer', 'userAgent', 'acceptLanguage']) assert.equal(s[k], f[k], k + ' is the same as for a file');
  assert.ok(s.referer && s.userAgent === 'Mozilla/5.0 Test Browser/1.0' && /en-GB/.test(s.acceptLanguage), JSON.stringify(s));
  assert.equal(s.cookie, 'sid=abc'); assert.equal(f.cookie, 'sid=abc');
  assert.match(s.fileName, /^My Film/); assert.doesNotMatch(s.fileName, /\.(mp4|ts|m3u8|mpd)$/i);
  assert.ok(Buffer.byteLength(JSON.stringify(s)) < 64 * 1024, 'far under the frame limit');
  assert.ok(!Object.keys(s).some(k => /text|manifest|body|content/i.test(k)), 'no manifest text is sent: ' + Object.keys(s));
  const d = makeBackground({}); const dtop = d.pageSender(tab);
  await d.message({ action: 'media-dom', sources: [{ key: 'kd', url: 'blob:https://video.example/d', tag: 'video', title: 'Dash Film', playing: true, vis: 1 }] }, dtop);
  await d.message(hookEntries('kd', [{ role: 'dash', url: 'https://cdn.example/d/manifest.mpd', quality: '720p', dur: 90 }]), dtop);
  const ds = json(d.sessionData['neodm.media.7']).items.find(i => i.track === 'stream');
  assert.equal((await d.message({ action: 'media-download', id: ds.id }, dtop)).ok, true); assert.equal(d.nativeRequests.length, 1); assert.equal(d.nativeRequests[0].stream, 'dash'); assert.equal(d.nativeRequests[0].url, 'https://cdn.example/d/manifest.mpd');
});

test('every stream string exists in all ten extension locales and the popup summary names streams', () => {
  const dirs = fs.readdirSync(path.join(root, 'extension/_locales')); assert.equal(dirs.length, 10);
  const keys = ['vb_download_stream', 'vb_reason_live', 'vb_stream_drm', 'vb_stream_pieces', 'err_stream_drm', 'err_stream_live', 'ribbon_stream_ok', 'popup_videos_summary', 'vb_stream_disabled', 'vb_reason_drm'];
  for (const d of dirs) {
    const raw = fs.readFileSync(path.join(root, 'extension/_locales', d, 'messages.json'), 'utf8'), m = JSON.parse(raw);
    for (const k of keys) assert.ok(m[k] && m[k].message && m[k].message.length > 2, d + ' ' + k);
    assert.match(JSON.stringify(m.popup_videos_summary.placeholders || {}), /streams/i, d + ' summary has a streams placeholder');
    assert.doesNotMatch(raw, /cannot save streams yet|planned for a later version/i, d);
  }
});
