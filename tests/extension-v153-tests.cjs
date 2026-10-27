
'use strict';
/* 1.5.3 extension hardening: prototype pollution, referer scope, trusted clicks only, explicit CSP. */
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { JSDOM } = require('jsdom');
const S = require('../extension/sniff.js');
const { makeBackground, response, tick, json, source, withI18n } = require('./extension-harness.cjs');
const root = path.join(__dirname, '..');
const MB = 1024 * 1024;

test('1.5.3 entry is still in the changelog and readme table', () => {
  const log = fs.readFileSync(path.join(root, 'CHANGELOG.md'), 'utf8');
  assert.ok(log.includes('## [1.5.3]'));
  const readme = fs.readFileSync(path.join(root, 'README.md'), 'utf8');
  assert.match(readme, /^\| 1\.5\.3 \| /m);
  const entry = log.slice(log.indexOf('## [1.5.3]'), log.indexOf('## [1.5.2]'));
  assert.doesNotMatch(entry, /\u2014/, 'no em dashes in the entry');
});

/* ---- 16: prototype pollution ---- */
test('noteEvents: key __proto__ (or constructor, toString) never touches Object.prototype or the state tables', () => {
  const polluted = () => ({}).s !== undefined || ({}).w !== undefined || Object.prototype.s !== undefined || Object.s !== undefined || Object.w !== undefined;
  const state = { items: [], evt: {}, els: {} };
  S.noteEvents(state, [{ key: '__proto__', type: 'playing', t: 5 }, { key: 'constructor', type: 'timeupdate', t: 9 }, { key: 'toString', type: 'progress', t: 3 }, { key: 'prototype', type: 'playing', t: 1 }]);
  assert.equal(polluted(), false, 'Object.prototype and Object are untouched');
  assert.equal(Object.getPrototypeOf(state.evt), Object.prototype); assert.equal(Object.getPrototypeOf(state.els), Object.prototype);
  assert.deepEqual(Object.keys(state.evt), []); assert.deepEqual(Object.keys(state.els), []);
  S.noteEvents(state, [{ key: 'good-key_1', type: 'playing', t: 7 }]);
  assert.deepEqual(Object.keys(state.evt), ['good-key_1'], 'normal keys still work'); assert.equal(state.evt['good-key_1'].s, 7);
  assert.equal(S.validKey('__proto__'), false); assert.equal(S.validKey('constructor'), false); assert.equal(S.validKey('prototype'), false); assert.equal(S.validKey('e1abc'), true);
});
test('domReport and cleanMeta: element key __proto__ is dropped', () => {
  const state = { items: [], els: {}, evt: {} };
  assert.equal(S.cleanMeta({ key: '__proto__' }).key, undefined); assert.equal(S.cleanMeta({ key: 'constructor' }).key, undefined); assert.equal(S.cleanMeta({ key: 'ok1' }).key, 'ok1');
  S.domReport(state, [{ url: 'https://x.example/a.mp4', tag: 'video', key: '__proto__', title: 'T', playing: true }]);
  assert.equal(Object.prototype.playing, undefined); assert.equal(Object.getPrototypeOf(state.els), Object.prototype); assert.deepEqual(Object.keys(state.els), []);
});
test('worker: a media-event message with key __proto__ does not pollute the worker context', async () => {
  const b = makeBackground({ openTabs: [] });
  await b.message({ action: 'media-event', events: [{ key: '__proto__', type: 'playing', t: Date.now() }, { key: 'constructor', type: 'timeupdate', t: Date.now() }] }, b.pageSender());
  await tick();
  const inWorker = expr => require('node:vm').runInContext(expr, b.ctx);
  assert.equal(inWorker('({}).s'), undefined); assert.equal(inWorker('({}).w'), undefined); assert.equal(inWorker('Object.s'), undefined);
});

/* ---- 17: referer ---- */
test('refererFor: full page address only to the same origin; other origins get origin + "/"; none on https to http; fragment and credentials never', () => {
  const page = 'https://user:pw@site.example/watch?v=1#t=5';
  assert.equal(S.refererFor(page, 'https://site.example/files/a.mp4'), 'https://site.example/watch?v=1');
  assert.equal(S.refererFor('https://site.example/watch?v=1', 'https://cdn.example/a.mp4'), 'https://site.example/');
  assert.equal(S.refererFor('https://site.example:8443/p/q?x=1', 'https://site.example/a'), 'https://site.example:8443/', 'another port is another origin');
  assert.equal(S.refererFor('https://site.example/p', 'http://site.example/a'), '', 'https page to an http target: nothing');
  assert.equal(S.refererFor('http://site.example/p?x=1', 'https://cdn.example/a'), 'http://site.example/');
  assert.equal(S.refererFor('http://site.example/p?x=1', 'http://site.example/a'), 'http://site.example/p?x=1');
  for (const bad of ['', null, undefined, 'about:blank', 'file:///c:/a.html', 'chrome://newtab/', 'javascript:alert(1)', 'not a url']) assert.equal(S.refererFor(bad, 'https://cdn.example/a'), '');
  assert.equal(S.refererFor('https://site.example/p', 'ftp://x.example/a'), '');
});
test('worker: every request path sends only the page origin to another origin and the full page address to its own origin', async () => {
  const tab = { id: 7, url: 'https://video.example/watch?v=1&secret=abc', title: 'Clip' };
  // ribbon / button / popup download of a sniffed item hosted on a CDN (cross-origin)
  const b = makeBackground({ openTabs: [tab] });
  await b.message({ action: 'media-dom', sources: [{ url: 'https://cdn.example/clip.mp4', tag: 'video' }] }, b.pageSender(tab)); await tick();
  const list = await b.message({ action: 'media-list' }, b.pageSender(tab)); const id = list.items[0].id;
  await b.message({ action: 'media-download', id }, b.pageSender(tab)); await tick(10);
  let req = b.nativeRequests.filter(r => r.url).at(-1); assert.equal(req.url, 'https://cdn.example/clip.mp4'); assert.equal(req.referer, 'https://video.example/');
  await b.message({ action: 'popup-download', tab: { id: 7, url: tab.url, title: tab.title }, id }); await tick(10);
  req = b.nativeRequests.filter(r => r.url).at(-1); assert.equal(req.referer, 'https://video.example/', 'popup path');
  // same-origin target keeps the page address
  const same = makeBackground({ openTabs: [tab] });
  await same.message({ action: 'media-dom', sources: [{ url: 'https://video.example/files/clip.mp4', tag: 'video' }] }, same.pageSender(tab)); await tick();
  const sl = await same.message({ action: 'media-list' }, same.pageSender(tab));
  await same.message({ action: 'media-download', id: sl.items[0].id }, same.pageSender(tab)); await tick(10);
  req = same.nativeRequests.filter(r => r.url).at(-1); assert.equal(req.referer, 'https://video.example/watch?v=1&secret=abc');
  // context menu on a link / media address of another origin
  const m = makeBackground({ openTabs: [tab] });
  await m.hooks.menu({ menuItemId: 'link', linkUrl: 'https://files.example/archive.zip', pageUrl: tab.url }, tab); await tick(12);
  req = m.nativeRequests.filter(r => r.url).at(-1); assert.ok(req, 'the link entry sent a request'); assert.equal(req.referer, 'https://video.example/');
});

/* ---- 18: only real clicks ---- */
function ribbonWindow(items) {
  const dom = new JSDOM('<!doctype html><body><video src="https://files.example/a.mp4"></video></body>', { url: 'https://page.example/watch', runScripts: 'outside-only', pretendToBeVisual: true }), w = dom.window;
  const roots = [], orig = w.Element.prototype.attachShadow; w.Element.prototype.attachShadow = function (i) { const r = orig.call(this, i); roots.push(r); return r; };
  const sent = [], listeners = [];
  w.chrome = { runtime: { sendMessage: async m => { sent.push(m); return m.action === 'media-list' ? { items } : { ok: true, id: 'j' }; }, onMessage: { addListener: f => listeners.push(f) } }, storage: { local: { get: async () => ({}) }, onChanged: { addListener() {} } } };
  w.HTMLCanvasElement.prototype.getContext = () => null;
  withI18n(w); w.eval(source('sniff.js')); w.eval(source('content.js'));
  return { w, sent, roots, ui: () => roots.at(-1) };
}
const item = (o = {}) => ({ id: 'https://v.example/a.mp4', url: 'https://v.example/a.mp4', kind: 'video', track: 'muxed', complete: true, size: 12 * MB, quality: '720p', mime: 'video/mp4', source: 'net', ...o });
test('ribbon buttons ignore clicks a page script made (isTrusted false) and still work for a real click', async () => {
  const r = ribbonWindow([item()]); await tick();
  const dl = r.ui().querySelector('button.dl'), x = r.ui().querySelector('button.x'), fake = () => new r.w.MouseEvent('click', { bubbles: true, cancelable: true });
  const probe = fake(); assert.equal(probe.isTrusted, false);
  dl.dispatchEvent(fake()); dl.dispatchEvent(new r.w.KeyboardEvent('keydown', { key: 'Enter', bubbles: true })); await tick();
  assert.equal(r.sent.filter(m => m.action === 'media-download').length, 0, 'a synthetic click starts nothing');
  x.dispatchEvent(fake()); assert.ok(r.w.document.querySelector('[data-neodm-ribbon]'), 'a synthetic click does not dismiss the ribbon');
  dl.click(); await tick(); assert.equal(r.sent.filter(m => m.action === 'media-download').length, 1, 'a real click works');
});
test('every click or keydown handler of the ribbon and the video bar starts with the isTrusted guard', () => {
  const src = source('content.js'), lines = src.split('\n');
  const handlers = lines.filter(l => /addEventListener\('(click|keydown|keyup|keypress)'/.test(l) && !/stopPropagation/.test(l));
  assert.ok(handlers.length >= 6, 'found the button handlers (' + handlers.length + ')');
  for (const l of handlers) assert.match(l, /addEventListener\('(click|keydown)', (e|ev) => \{ if \(!(e|ev)\.isTrusted\) return;/, l.trim().slice(0, 100));
  assert.doesNotMatch(src, /\.on(click|keydown)\s*=/, 'no property handlers that skip the guard');
});

/* ---- 19/20: manifest ---- */
test('manifest pins an explicit content security policy and keeps its permissions', () => {
  const m = JSON.parse(fs.readFileSync(path.join(root, 'extension/manifest.json'), 'utf8'));
  assert.deepEqual(m.content_security_policy, { extension_pages: "script-src 'self'; object-src 'self'" });
  assert.deepEqual([...m.permissions].sort(), ['contextMenus', 'cookies', 'downloads', 'nativeMessaging', 'storage', 'tabs', 'webRequest']);
  assert.equal(m.optional_permissions, undefined);
  const html = fs.readFileSync(path.join(root, 'extension/popup.html'), 'utf8');
  assert.doesNotMatch(html, /<script(?![^>]*\ssrc=)[^>]*>/i, 'no inline script in the popup (script-src self)');
  assert.doesNotMatch(html, /\son[a-z]+\s*=/i, 'no inline event handlers in the popup');
});
