// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
'use strict';
/* 1.3.2: the browser's own file name and content type are forwarded to the app (optional fileName / mime fields). */
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { transfer, baseName, mimeOf } = require('../extension/bridge.js');
const { makeBackground } = require('./extension-harness.cjs');
const base = { id: 7, url: 'https://example.org/dl?id=5', finalUrl: 'https://example.org/dl?id=5', state: 'in_progress' };
const api = (current = {}) => ({ cancel: async () => {}, search: async () => [{ ...base, state: 'interrupted', ...current }], removeFile: async () => {}, erase: async () => {}, download: async () => {} });
const sentBy = async (item, apiObject, hint) => { let sent; await transfer(item, apiObject, async r => { sent = r; return { ok: true, id: 's' }; }, true, hint); return sent; };

test('baseName: basename of Windows and POSIX paths, safe characters, no traversal, capped length', () => {
  assert.equal(baseName('C:\\Users\\me\\Downloads\\Report 2026.pdf'), 'Report 2026.pdf');
  assert.equal(baseName('/home/me/a.zip'), 'a.zip');
  assert.equal(baseName('..\\..\\x.zip'), 'x.zip');
  assert.equal(baseName('C:\\d\\movie.mkv.crdownload'), 'movie.mkv');
  assert.equal(baseName('..'), ''); assert.equal(baseName(''), ''); assert.equal(baseName(undefined), '');
  assert.equal(baseName('a<b>c.txt'), 'a_b_c.txt');
  assert.ok(Array.from(baseName('C:\\' + 'x'.repeat(400) + '.zip')).length <= 255);
  assert.equal(baseName('C:\\\u65e5\u672c.zip'), '\u65e5\u672c.zip');
});
test('mimeOf: plain type/subtype only, parameters stripped, 100 characters at most', () => {
  assert.equal(mimeOf('application/zip'), 'application/zip'); assert.equal(mimeOf('Video/MP4; codecs=x'), 'Video/MP4');
  for (const bad of ['', 'zip', 'a b/c', 'application/', '/zip', 'a/b/c', 'x'.repeat(99) + '/y', 5, null, undefined]) assert.equal(mimeOf(bad), '', String(bad));
});
test('transfer forwards the browser file name basename and mime from the download item', async () => {
  const sent = await sentBy({ ...base, filename: 'C:\\Users\\me\\Downloads\\tool-setup.exe', mime: 'application/x-msdownload' }, api());
  assert.equal(sent.fileName, 'tool-setup.exe'); assert.equal(sent.mime, 'application/x-msdownload'); assert.equal(sent.url, base.finalUrl); assert.equal(sent.method, 'GET');
});
test('transfer falls back to the name the browser decided (onDeterminingFilename hint, then the record seen while cancelling)', async () => {
  let sent = await sentBy(base, api(), { filename: 'C:\\d\\from-hint.zip', mime: 'application/zip' });
  assert.equal(sent.fileName, 'from-hint.zip'); assert.equal(sent.mime, 'application/zip');
  sent = await sentBy(base, api({ filename: 'C:\\d\\from-search.7z', mime: 'application/x-7z-compressed' }));
  assert.equal(sent.fileName, 'from-search.7z'); assert.equal(sent.mime, 'application/x-7z-compressed');
});
test('wire contract stays backward compatible: nothing known -> fileName "" and no mime key; hostile values are dropped', async () => {
  let sent = await sentBy(base, api());
  assert.deepEqual(Object.keys(sent).sort(), ['fileName', 'method', 'referer', 'url']); assert.equal(sent.fileName, '');
  sent = await sentBy({ ...base, filename: 'C:\\d\\..', mime: 'not a mime' }, api());
  assert.equal(sent.fileName, ''); assert.equal('mime' in sent, false);
});
test('service worker: the browser name from onDeterminingFilename reaches the native host request together with the mime', async () => {
  const b = makeBackground({ settings: { captureEnabled: true } });
  const d = b.chrome.downloads; d.cancel = async () => {}; d.erase = async () => {}; d.removeFile = async () => {}; d.download = async () => {}; d.search = async () => [{ state: 'interrupted' }];
  const item = { ...base, id: 21, referrer: 'https://site.example/p' };
  b.hooks.request({ requestId: 'r1', url: item.url, method: 'GET' });
  b.hooks.determining({ id: 21, filename: 'Quarterly Report.pdf', mime: 'application/pdf' }); // no suggest(): the browser is not held up
  await b.hooks.download(item);
  const req = b.nativeRequests.find(r => r.url);
  assert.equal(req.fileName, 'Quarterly Report.pdf'); assert.equal(req.mime, 'application/pdf'); assert.equal(req.cookie, '');
});
test('context-menu path keeps sending only what it knows (no mime key); the app resolves the name from the URL', async () => {
  const b = makeBackground({ settings: { captureEnabled: true } });
  await b.hooks.menu({ menuItemId: 'link', linkUrl: 'https://example.org/get?id=3', pageUrl: 'https://example.org/' }, { id: 1 });
  const req = b.nativeRequests.at(-1); assert.equal(req.fileName, ''); assert.equal('mime' in req, false); assert.equal(req.url, 'https://example.org/get?id=3');
});
