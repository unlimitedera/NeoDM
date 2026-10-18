'use strict';
/* 1.4.3: every capture and hand-off request carries the extension's manifest version (extVersion); the popup shows an app/extension version mismatch. */
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs'), path = require('node:path');
const { JSDOM } = require('jsdom');
const { makeBackground, response, tick, source, json, withI18n } = require('./extension-harness.cjs');
const manifest = JSON.parse(fs.readFileSync(path.join(__dirname, '../extension/manifest.json'), 'utf8'));
const FORMAT = /^\d{1,4}(\.\d{1,4}){1,3}$/;
const dl = (url, extra = {}) => ({ id: 11, url, finalUrl: url, state: 'in_progress', referrer: 'https://site.example/page', ...extra });
const withMedia = async b => { b.hooks.headers(response('https://cdn.example/v/clip.mp4', 'video/mp4', 80 * 1024 * 1024)); await tick(20); return json(b.sessionData['neodm.media.7']).items[0]; };

test('the manifest version is a valid wire version', () => assert.match(manifest.version, FORMAT));

test('browser download capture sends extVersion', async () => {
  const b = makeBackground({ settings: { captureEnabled: true } });
  b.chrome.downloads.cancel = async () => {}; b.chrome.downloads.erase = async () => {}; b.chrome.downloads.search = async () => [];
  b.hooks.request({ requestId: 'r', url: 'https://files.example/a.zip', method: 'GET' });
  await b.hooks.download(dl('https://files.example/a.zip'));
  assert.equal(b.nativeRequests.length, 1); assert.equal(b.nativeRequests[0].extVersion, manifest.version);
  assert.equal(b.nativeRequests[0].keepAlive, true, 'the existing fields are unchanged');
});

test('context menu (link and media) sends extVersion', async () => {
  const b = makeBackground();
  await b.hooks.menu({ menuItemId: 'link', linkUrl: 'https://files.example/a.zip', pageUrl: 'https://site.example/' }, { id: 1 });
  await b.hooks.menu({ menuItemId: 'media', srcUrl: 'https://files.example/b.mp4', pageUrl: 'https://site.example/' }, { id: 1 });
  assert.equal(b.nativeRequests.length, 2);
  for (const r of b.nativeRequests) assert.equal(r.extVersion, manifest.version);
});

test('ribbon and popup media downloads send extVersion', async () => {
  const b = makeBackground();
  const item = await withMedia(b);
  assert.equal((await b.message({ action: 'media-download', id: item.id }, b.pageSender())).ok, true);
  assert.equal((await b.message({ action: 'popup-download', tab: { id: 7, url: 'https://video.example/watch?v=1', title: 'Clip' }, id: item.id })).ok, true);
  assert.equal(b.nativeRequests.length, 2);
  for (const r of b.nativeRequests) assert.equal(r.extVersion, manifest.version);
});

test('ping carries extVersion and nothing else is added to it', async () => {
  const b = makeBackground();
  await b.message({ action: 'test' });
  assert.deepEqual(b.nativeRequests.at(-1), { action: 'ping', extVersion: manifest.version });
});

test('a failing getManifest leaves out the field instead of breaking the capture', async () => {
  const b = makeBackground();
  b.chrome.runtime.getManifest = () => { throw new Error('unavailable'); };
  await b.hooks.menu({ menuItemId: 'link', linkUrl: 'https://files.example/a.zip', pageUrl: 'https://site.example/' }, { id: 1 });
  assert.equal(b.nativeRequests.length, 1); assert.equal('extVersion' in b.nativeRequests[0], false);
});

async function popup(reply, own = manifest.version) {
  const dom = new JSDOM(fs.readFileSync(path.join(__dirname, '../extension/popup.html'), 'utf8'), { runScripts: 'outside-only', url: 'chrome-extension://ext-id/popup.html' }), w = dom.window;
  w.chrome = {
    storage: { local: { get: async () => ({}), set: async () => {} }, session: { get: async () => ({}) } },
    tabs: { query: async () => [{ id: 1, url: 'https://site.example/page' }] },
    runtime: { getManifest: () => ({ version: own }), sendMessage: async m => (m.action === 'test' ? reply : { ok: true }) },
    permissions: { request: async () => true, remove: async () => true },
  };
  withI18n(w); w.eval(source('sniff.js')); w.eval(source('popup.js')); await tick(12);
  w.document.getElementById('test').click(); await tick(12);
  return w.document;
}
test('popup: a different app version shows the update line; the same version, a missing or malformed one shows nothing', async () => {
  let doc = await popup({ ok: true, appVersion: '1.9.0' }, '1.4.3');
  assert.equal(doc.getElementById('versionNote').hidden, false);
  assert.equal(doc.getElementById('versionNote').textContent, 'NeoDM app 1.9.0, extension 1.4.3. Open Help > Browser extension install guide in NeoDM.');
  assert.equal(doc.getElementById('status').textContent, 'Host connected.');
  for (const reply of [{ ok: true, appVersion: '1.4.3' }, { ok: true }, { ok: true, appVersion: '<b>x</b>' }, { ok: true, appVersion: 143 }, { ok: false, error: 'down', appVersion: '1.9.0' }]) {
    doc = await popup(reply, '1.4.3');
    assert.equal(doc.getElementById('versionNote').hidden, true, JSON.stringify(reply)); assert.equal(doc.getElementById('versionNote').textContent, '');
  }
});
