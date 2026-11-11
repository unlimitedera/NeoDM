'use strict';
// 1.14.0: subtitle files a player loads on its own (.vtt/.srt). Detection, per-tab list, the stream capture message and the ribbon hint.
const { test, after } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs'), path = require('node:path');
const { JSDOM } = require('jsdom');
const { tick, json, source, withI18n, makeBackground } = require('./extension-harness.cjs');
const Subs = require('../extension/subs.js');
const ROOT = path.join(__dirname, '..');
const windows = []; after(() => { for (const w of windows) try { w.close(); } catch {} });
const MB = 1024 * 1024;
const resp = (url, o = {}) => ({ url, tabId: 7, method: 'GET', statusCode: 200, type: 'media', fromCache: false, responseHeaders: [], ...o });
const ct = type => [{ name: 'Content-Type', value: type }];

test('format detection: extension (query ignored, any case), content type, and non-subtitles', () => {
  const f = Subs.formatOf;
  assert.equal(f('https://cdn.example/v/9/subs/a1b2.vtt'), 'vtt');
  assert.equal(f('https://cdn.example/x/Movie.EN.SRT?token=abc&e=1#frag'), 'srt');
  assert.equal(f('https://cdn.example/x/a.ass'), 'ass'); assert.equal(f('https://cdn.example/x/a.ttml'), 'ttml'); assert.equal(f('https://cdn.example/x/a.dfxp'), 'dfxp');
  assert.equal(f('https://cdn.example/x/captions', 'text/vtt; charset=utf-8'), 'vtt');
  assert.equal(f('https://cdn.example/x/captions', 'application/x-subrip'), 'srt');
  assert.equal(f('https://cdn.example/x/video.mp4', 'video/mp4'), '');
  assert.equal(f('https://cdn.example/x/page.html?file=a.vtt', 'text/html'), '', 'the query is not the file');
  assert.equal(f('ftp://cdn.example/a.vtt'), ''); assert.equal(f('not a url'), ''); assert.equal(f('https://u:p@cdn.example/a.vtt'), '');
});

test('language guessing table', () => {
  const g = Subs.guessLanguage;
  const table = [
    ['https://c.example/v/1/subs/en.vtt', 'en'], ['https://c.example/subs/eng.vtt', 'en'], ['https://c.example/subs/english.srt', 'en'], ['https://c.example/movie.en.vtt', 'en'],
    ['https://c.example/subs/ar/1.vtt', 'ar'], ['https://c.example/subs_ar.vtt', 'ar'], ['https://c.example/Arabic/x.vtt', 'ar'], ['https://c.example/a/b/fra.srt', 'fr'],
    ['https://c.example/x/sub.vtt?lang=de', 'de'], ['https://c.example/x/sub.vtt?language=spanish', 'es'], ['https://c.example/x/pt-BR.vtt', 'pt'], ['https://c.example/x/zh-Hans.vtt', 'zh'],
    ['https://c.example/v/1/subs/9f8e7d6c.vtt', null], ['https://c.example/v/id/subs/9f8e7d6c.vtt', null], ['https://c.example/v/it/9f8e7d6c.vtt', null],
    ['https://en.example.com/v/9f8e7d6c.vtt', null], ['https://c.example/v/subs/captions.vtt', null],
  ];
  for (const [url, want] of table) assert.equal(g(url), want, url);
  assert.equal(g('https://c.example/x/a.vtt', 'Arabic'), 'ar', 'a track label wins'); assert.equal(g('https://c.example/x/a.vtt', 'English (CC)'), 'en');
});

test('entryFor: failures and redirects are not subtitles; unknown language becomes "Subtitles"; fragments dropped', () => {
  assert.equal(Subs.entryFor(resp('https://c.example/a.vtt', { statusCode: 404 })), null);
  assert.equal(Subs.entryFor(resp('https://c.example/a.vtt', { statusCode: 302 })), null);
  assert.equal(Subs.entryFor(resp('https://c.example/a.vtt', { statusCode: 304 })), null, 'a revalidated copy has no body of its own to list twice');
  assert.deepEqual(Subs.entryFor(resp('https://c.example/v/subs/ab12.vtt#x')), { url: 'https://c.example/v/subs/ab12.vtt', language: '', label: 'Subtitles', format: 'vtt' });
  assert.equal(Subs.entryFor(resp('https://c.example/' + 'a'.repeat(3000) + '.vtt')), null, 'too long');
  assert.equal(Subs.entryFor(resp('https://c.example/clip.mp4', { responseHeaders: ct('video/mp4') })), null);
});

test('list bounds: de-duplicated, at most 20, language learned later, message validation', () => {
  const list = [];
  for (let i = 0; i < 25; i++) Subs.add(list, Subs.entryFor(resp(`https://c.example/s/${i}.vtt`)));
  assert.equal(list.length, 20); assert.equal(list[0].url, 'https://c.example/s/5.vtt', 'the oldest go first');
  assert.equal(Subs.add(list, Subs.entryFor(resp('https://c.example/s/24.vtt'))), false, 'same address twice');
  const one = []; Subs.add(one, Subs.entryFor(resp('https://c.example/s/x.vtt'))); assert.equal(one[0].language, '');
  assert.equal(Subs.add(one, { url: 'https://c.example/s/x.vtt', language: 'ar', label: 'Arabic', format: 'vtt' }), true); assert.deepEqual([one[0].language, one[0].label], ['ar', 'Arabic']);
  const bad = [{ url: 'javascript:alert(1)', format: 'vtt' }, { url: 'https://c.example/a.vtt', format: 'exe' }, null, 'x', { url: 'https://c.example/a.vtt', language: 'e n!', label: 'x\u0000y', format: 'vtt' }];
  assert.deepEqual(Subs.forMessage(bad), [{ url: 'https://c.example/a.vtt', language: '', label: 'x y', format: 'vtt' }]);
  const big = Array.from({ length: 20 }, (_, i) => ({ url: 'https://c.example/' + 'p'.repeat(1900) + i + '.vtt', language: 'en', label: 'L'.repeat(80), format: 'vtt' }));
  const sent = Subs.forMessage(big); assert.ok(sent.length < 20 && sent.length > 0); assert.ok(JSON.stringify(sent).length <= Subs.MAX_BYTES + 30); assert.ok(sent.at(-1).url.endsWith('19.vtt'), 'the newest are kept');
  assert.deepEqual(Subs.summary([{ language: 'en' }, { language: 'ar' }, { language: 'en' }, { language: '' }]), { count: 4, languages: ['en', 'ar'], unknown: true });
});

const tab = { id: 7, url: 'https://video.example/watch?v=1', title: 'My Film - Site' };
async function streamTab(b) {
  const top = b.pageSender(tab);
  await b.message({ action: 'media-dom', sources: [{ key: 'kp', url: 'blob:https://video.example/u', tag: 'video', title: 'My Film', playing: true, vis: 1 }] }, top);
  await b.message({ action: 'media-hooks', entries: [{ key: 'kp', list: [{ role: 'hls', url: 'https://cdn.example/vod/master.m3u8', quality: '1080p', dur: 6121, variants: 4 }] }] }, top);
  return { top, stream: json(b.sessionData['neodm.media.7']).items.find(i => i.track === 'stream') };
}

test('worker: captured subtitles ride on a stream capture only, are per tab, are cleared by navigation and tab close, and are never stored', async () => {
  const b = makeBackground({ settings: { cookiesEnabled: false } });
  const { top, stream } = await streamTab(b);
  b.hooks.started(resp('https://cdn.example/v/9/subs/a1b2.vtt', { responseHeaders: ct('text/vtt') }));
  b.hooks.started(resp('https://cdn.example/v/9/subs/ar.vtt', { fromCache: true }));      // served from the browser cache
  b.hooks.started(resp('https://cdn.example/v/9/subs/ar.vtt', { fromCache: true }));      // twice: one entry
  b.hooks.started(resp('https://other.example/x.vtt', { tabId: 8 }));                      // another tab
  b.hooks.started(resp('https://cdn.example/seg1.ts', { responseHeaders: ct('video/mp2t') }));
  await tick(10);
  assert.equal((await b.message({ action: 'media-download', id: stream.id }, top)).ok, true);
  const sent = b.nativeRequests.at(-1);
  assert.equal(sent.stream, 'hls');
  assert.deepEqual(sent.subtitles, [
    { url: 'https://cdn.example/v/9/subs/a1b2.vtt', language: '', label: 'Subtitles', format: 'vtt' },
    { url: 'https://cdn.example/v/9/subs/ar.vtt', language: 'ar', label: 'Subtitles', format: 'vtt' }]);
  assert.ok(Buffer.byteLength(JSON.stringify(sent)) < 64 * 1024);
  // the ribbon is told which languages were seen (codes only)
  const told = b.sentToTabs.filter(m => m.tabId === 7 && m.msg?.type === 'neodm-media' && m.msg.subs).at(-1);
  assert.ok(told, 'a neodm-media message carries subs'); assert.deepEqual(json(told.msg.subs), { languages: ['ar'], unknown: true });
  assert.deepEqual(json((await b.message({ action: 'media-list' }, top)).subs), { languages: ['ar'], unknown: true });
  // not persisted anywhere
  assert.ok(!JSON.stringify(b.sessionData).includes('.vtt'), 'session storage holds no subtitle address');
  // a plain file capture never carries them
  const file = { id: 'f', url: 'https://cdn.example/clip.mp4', rawUrl: 'https://cdn.example/clip.mp4', groupKey: 'f', kind: 'video', track: 'file', complete: true, size: 10 * MB, quality: '', mime: 'video/mp4', source: 'net' };
  await b.message({ action: 'media-hooks', entries: [{ key: 'kp', list: [{ role: 'file', url: file.url, kind: 'video', size: 5 * MB, quality: '360p' }] }] }, top);
  const fileItem = json(b.sessionData['neodm.media.7']).items.find(i => i.track === 'file');
  await b.message({ action: 'media-download', id: fileItem.id }, top);
  assert.ok(!('subtitles' in b.nativeRequests.at(-1)), 'no subtitles on a file request');
  // a different page address forgets them
  b.hooks.tabUpdated(7, { url: 'https://video.example/watch?v=2' }); await tick(6);
  assert.equal((await b.message({ action: 'media-list' }, top)).subs, undefined, 'navigation clears the list');
  await b.message({ action: 'media-download', id: stream.id }, top).catch(() => {});
  assert.ok(!('subtitles' in b.nativeRequests.at(-1)) || b.nativeRequests.at(-1).subtitles === undefined);
});

test('worker: a real navigation (main frame response) and closing the tab forget the files; the same page address keeps them', async () => {
  const b = makeBackground({}); const top = b.pageSender(tab);
  b.hooks.tabUpdated(7, { url: tab.url }); await tick(4);
  b.hooks.started(resp('https://cdn.example/s/en.vtt')); await tick(4);
  b.hooks.tabUpdated(7, { url: tab.url + '#t=5' }); await tick(4);
  assert.deepEqual(json((await b.message({ action: 'media-list' }, top)).subs), { languages: ['en'], unknown: false }, 'a hash change is the same page');
  b.navigate('https://video.example/other'); await tick(6);
  assert.equal((await b.message({ action: 'media-list' }, top)).subs, undefined);
  b.hooks.started(resp('https://cdn.example/s/fr.srt')); await tick(4);
  assert.deepEqual(json((await b.message({ action: 'media-list' }, top)).subs), { languages: ['fr'], unknown: false });
  b.hooks.tabRemoved(7); await tick(4);
  assert.equal((await b.message({ action: 'media-list' }, top)).subs, undefined);
});

test('worker: bounded at 20 per tab however many files a page loads', async () => {
  const b = makeBackground({}); const top = b.pageSender(tab);
  for (let i = 0; i < 60; i++) b.hooks.started(resp(`https://cdn.example/s/${i}/en.vtt`));
  await tick(10);
  const { stream } = await streamTab(b);
  await b.message({ action: 'media-download', id: stream.id }, top);
  assert.equal(b.nativeRequests.at(-1).subtitles.length, 20);
});

test('the response listener asks for the headers it needs and no new permission is used', () => {
  const b = makeBackground({}); const args = b.hooks['started:args'];
  assert.ok(args[0].types.includes('media') && args[0].types.includes('xmlhttprequest') && args[0].types.includes('other') && args[0].types.includes('main_frame'));
  assert.deepEqual(json(args[1]), ['responseHeaders']);
  const m = JSON.parse(fs.readFileSync(path.join(ROOT, 'extension/manifest.json'), 'utf8'));
  assert.deepEqual(m.permissions, ['nativeMessaging', 'downloads', 'contextMenus', 'storage', 'webRequest', 'tabs', 'cookies'], 'webRequest and the http(s) host permission were already there');
});

// ---------------- the ribbon hint
function page(lang = 'en') {
  const dom = new JSDOM('<!doctype html><title>V</title><body><video id="v1"></video></body>', { url: 'https://video.example/watch?v=1', runScripts: 'outside-only', pretendToBeVisual: true }), w = dom.window; windows.push(w);
  const roots = [], orig = w.Element.prototype.attachShadow;
  w.Element.prototype.attachShadow = function (init) { const r = orig.call(this, init); roots.push({ host: this, root: r }); return r; };
  let current = []; const listeners = [], local = {};
  Object.defineProperty(w.document.getElementById('v1'), 'currentSrc', { value: 'blob:https://video.example/u', configurable: true });
  w.HTMLCanvasElement.prototype.getContext = () => null;
  w.chrome = { runtime: { sendMessage: async m => (m.action === 'media-list' ? { items: current } : { ok: true, id: 'job' }), onMessage: { addListener: f => listeners.push(f) } },
    storage: { local: { get: async () => ({ ...local }), set: async o => Object.assign(local, o) }, onChanged: { addListener() {} } } };
  withI18n(w, lang); w.eval(source('sniff.js')); w.eval(source('content.js'));
  const send = m => { if (m.type === 'neodm-media') current = m.items; listeners.forEach(f => f(m, {}, () => {})); };
  return { w, send, root: () => roots.find(r => r.host.hasAttribute('data-neodm-ribbon'))?.root };
}
const streamItem = { id: 'https://cdn.example/m.m3u8', url: 'https://cdn.example/m.m3u8', kind: 'video', track: 'stream', stream: 'hls', complete: false, quality: '1080p', dur: 100, source: 'net' };
const fileItem = { id: 'https://v.example/a.mp4', url: 'https://v.example/a.mp4', kind: 'video', track: 'muxed', complete: true, size: 12 * MB, quality: '720p', mime: 'video/mp4', source: 'net' };
const hint = p => p.root()?.querySelector('.subs');

test('ribbon hint: one language, two languages, unknown only; absent without subtitles or without a stream; own dismiss; never the corner button', async () => {
  const p = page(); await tick();
  p.send({ type: 'neodm-media', items: [streamItem] }); await tick();
  assert.ok(p.root(), 'ribbon shown'); assert.equal(hint(p).hidden, true, 'no subtitles seen: no hint');
  p.send({ type: 'neodm-media', items: [streamItem], subs: { languages: ['en'], unknown: false } }); await tick();
  assert.equal(hint(p).hidden, false);
  assert.equal(hint(p).querySelector('span').textContent, 'Subtitles found: English. For other languages, pick them in the player first.');
  p.send({ type: 'neodm-media', items: [streamItem], subs: { languages: ['en', 'ar'], unknown: false } }); await tick();
  assert.equal(hint(p).querySelector('span').textContent, 'Subtitles found: English, Arabic. For other languages, pick them in the player first.');
  p.send({ type: 'neodm-media', items: [streamItem], subs: { languages: [], unknown: true } }); await tick();
  assert.equal(hint(p).querySelector('span').textContent, 'Subtitles found. For other languages, pick them in the player first.');
  p.send({ type: 'neodm-media', items: [streamItem], subs: { languages: ['en'], unknown: false } }); await tick();
  const row = p.root().querySelector('.rows'); assert.ok(row.compareDocumentPosition(hint(p)) & 4, 'the hint comes after the stream rows');
  hint(p).querySelector('button').click(); assert.equal(hint(p).hidden, true, 'its own dismiss');
  assert.ok(p.root().querySelector('.bar'), 'the ribbon itself stays');
  const q = page(); await tick(); q.send({ type: 'neodm-media', items: [fileItem], subs: { languages: ['en'], unknown: false } }); await tick();
  assert.equal(hint(q).hidden, true, 'a plain file has no stream button, so no hint');
  const r = page(); await tick(); r.send({ type: 'neodm-media', items: [streamItem], subs: { languages: ['x'.repeat(50)], unknown: 'yes' } }); await tick();
  assert.equal(hint(r).hidden, true, 'invalid summary ignored');
});

test('ribbon hint: Arabic is right to left and names the language in Arabic; all ten locales carry the three strings', async () => {
  const p = page('ar'); await tick(); p.send({ type: 'neodm-media', items: [streamItem], subs: { languages: ['en', 'ar'], unknown: false } }); await tick();
  assert.equal(p.root().querySelector('.bar').dir, 'rtl'); assert.match(hint(p).querySelector('span').textContent, /الإنجليزية/);
  for (const loc of fs.readdirSync(path.join(ROOT, 'extension/_locales'))) {
    const m = JSON.parse(fs.readFileSync(path.join(ROOT, 'extension/_locales', loc, 'messages.json'), 'utf8'));
    for (const k of ['subs_hint', 'subs_hint_unknown', 'subs_hint_dismiss']) assert.ok(m[k] && m[k].message.length > 2, loc + ' ' + k);
    assert.match(m.subs_hint.message, /\$LANGS\$/, loc); assert.equal(m.subs_hint.placeholders.langs.content, '$1'); assert.doesNotMatch(JSON.stringify(m.subs_hint), /\u2014/);
  }
});
