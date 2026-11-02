'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs'), path = require('node:path'), zlib = require('node:zlib'), vm = require('node:vm');
const { JSDOM } = require('jsdom');
const { makeBackground, tick, source, withI18n } = require('./extension-harness.cjs');
const S = require('../extension/sniff.js');
const canonical = 'https://www.youtube.com/watch?v=aqz-KE-bpKQ';
const tab = { id: 7, url: canonical, title: 'Authorized sample' };
const action = { action: 'youtube-send', tabId: 7, url: canonical };
const nativeReply = r => r.action === 'ping' ? { ok: true, youtube: true } : { ok: true, id: 'fixture' };
function unpack(zip) {
  let p = 0; const out = {};
  while (zip.readUInt32LE(p) === 0x04034b50) {
    const method = zip.readUInt16LE(p + 8), size = zip.readUInt32LE(p + 18), nl = zip.readUInt16LE(p + 26), el = zip.readUInt16LE(p + 28);
    const name = zip.toString('utf8', p + 30, p + 30 + nl), begin = p + 30 + nl + el, data = zip.subarray(begin, begin + size);
    out[name] = (method === 8 ? zlib.inflateRawSync(data) : data).toString('utf8'); p = begin + size;
  }
  return out;
}
async function popup(files, b) {
  const dom = new JSDOM(files?.['popup.html'] || source('popup.html'), { runScripts: 'outside-only', url: 'chrome-extension://ext-id/popup.html' });
  const w = dom.window;
  w.chrome = { ...b.chrome, runtime: { ...b.chrome.runtime, sendMessage: m => b.message(m) } };
  withI18n(w); w.eval(files?.['sniff.js'] || source('sniff.js')); w.eval(files?.['popup.js'] || source('popup.js')); await tick(20); return dom;
}
test('manual page grammar canonicalizes supported pages and strips unrelated query and fragment', () => {
  for (const url of [canonical, 'http://youtube.com/watch?list=discard&v=aqz-KE-bpKQ&t=20#discard', 'https://m.youtube.com/shorts/aqz-KE-bpKQ', 'https://music.youtube.com/watch?v=aqz-KE-bpKQ', 'https://youtu.be/aqz-KE-bpKQ?si=discard']) assert.equal(S.youtubePage(url), canonical);
  for (const url of ['https://youtube.com.evil.test/watch?v=aqz-KE-bpKQ', 'https://evil.youtube.com/watch?v=aqz-KE-bpKQ', 'https://user@youtube.com/watch?v=aqz-KE-bpKQ', 'https://youtube.com:443/watch?v=aqz-KE-bpKQ', 'https://youtube.com:80/watch?v=aqz-KE-bpKQ', 'https://youtu.be/%61qz-KE-bpKQ', 'https://youtube.com/%77atch?v=aqz-KE-bpKQ', 'https://youtube.com/watch?v=aqz-KE-bpKQ&v=aqz-KE-bpKQ', 'https://youtu.be/aqz-KE-bpKQ/', 'https://youtube.com/embed/aqz-KE-bpKQ', 'https://youtube.com/playlist?list=x', 'javascript:alert(1)', ' https://youtu.be/aqz-KE-bpKQ', 'https://youtube.com\\@evil.test/watch?v=aqz-KE-bpKQ']) assert.equal(S.youtubePage(url), '', url);
});
test('manual explicit click sends canonical URL once without reading cookies or browser headers', async () => {
  const b = makeBackground({ openTabs: [tab], nativeReply, cookieJar: [{ name: 'sid', value: 'fixture' }], navigator: { userAgent: 'Fixture browser' } });
  assert.equal((await b.message(action)).ok, true);
  assert.deepEqual(b.nativeRequests, [{ action: 'ping' }, { url: canonical, youtube: true, extVersion: '1.8.0', keepAlive: true }]);
  assert.equal(b.cookieQueries.length, 0);
  assert.equal(await b.message(action, b.pageSender(tab)), undefined, 'page frames cannot send handoff');
});
test('old host and missing app fail closed, with no normal-file fallback', async () => {
  for (const reply of [{ ok: true, appVersion: '1.7.0' }, new Error('missing')]) {
    const b = makeBackground({ openTabs: [tab], nativeReply: () => reply });
    assert.equal((await b.message(action)).ok, false); assert.equal(b.nativeRequests.length, 1);
  }
});
test('SPA stale page or different active tab is refused before native messaging', async () => {
  for (const current of [{ ...tab, url: canonical.replace('aqz-KE-bpKQ', 'abcdefghijk') }, { ...tab, id: 8 }, { ...tab, url: 'https://example.org/' }]) {
    const b = makeBackground({ openTabs: [current], nativeReply });
    assert.equal((await b.message(action)).ok, false); assert.equal(b.nativeRequests.length, 0);
  }
  let finish;
  const tabs = [{ ...tab }];
  const b = makeBackground({ openTabs: tabs, nativeReply: () => new Promise(resolve => { finish = resolve; }) });
  const sending = b.message(action); await tick(); tabs[0].url = canonical.replace('aqz-KE-bpKQ', 'abcdefghijk'); finish({ ok: true, youtube: true });
  assert.equal((await sending).ok, false); assert.equal(b.nativeRequests.length, 1, 'page changed during native ping: no page sent');
});
test('pending heartbeat keeps one request in flight and cancel never starts browser download', async () => {
  let finish;
  const b = makeBackground({ openTabs: [tab], nativeReply: r => r.action === 'ping' ? nativeReply(r) : new Promise(resolve => { finish = resolve; }) });
  const first = b.message(action); await tick();
  assert.equal((await b.message(action)).ok, false); assert.equal(b.nativeRequests.length, 2);
  finish([{ pending: true }, { ok: false, cancelled: true, error: 'cancelled' }]);
  const reply = await first; assert.equal(reply.cancelled, true); assert.equal(b.chrome.downloads.calls.length, 0);
  assert.ok(b.timers.some(t => t.ms === 60000), 'pending switches to idle timeout');
});
test('dedicated right-click menu validates video links and forwards the clicked link, not the tab', async () => {
  const b = makeBackground({ nativeReply }); await tick();
  assert.ok(b.menus.some(m => m.id === 'youtube-link'));
  await b.hooks.menu({ menuItemId: 'youtube-link', linkUrl: 'https://youtu.be/aqz-KE-bpKQ?si=discard' }, { id: 7, url: 'https://example.org/' });
  assert.equal(b.nativeRequests.filter(r => r.youtube).length, 1);
  await b.hooks.menu({ menuItemId: 'youtube-link', linkUrl: 'https://youtube.com.evil.test/watch?v=aqz-KE-bpKQ' }, tab);
  assert.equal(b.nativeRequests.filter(r => r.youtube).length, 1);
});
test('manual popup actual button blocks repeated clicks and reflects tab URL updates', async () => {
  let finish;
  const b = makeBackground({ openTabs: [tab], nativeReply: r => r.action === 'ping' ? nativeReply(r) : new Promise(resolve => { finish = resolve; }) });
  const dom = await popup(null, b), w = dom.window;
  try {
    assert.equal(w.document.getElementById('youtubePanel').hidden, false);
    const button = w.document.getElementById('youtubeSend'); button.click(); button.click(); await tick();
    assert.equal(button.disabled, true); assert.equal(b.nativeRequests.filter(r => r.youtube).length, 1);
    finish({ ok: false, error: 'cancelled', cancelled: true }); await tick(); assert.equal(button.disabled, false);
    w.chrome.tabs.onUpdated = { addListener() {} }; // Existing listener was installed during popup startup.
    w.eval("renderMedia({ id: 7, url: 'https://example.org/', title: 'Fixture' })"); await tick();
    assert.equal(w.document.getElementById('youtubePanel').hidden, true);
  } finally { w.close(); }
});
test('actual built store ZIP includes the same page-link handoff without a development key or extra permissions', async () => {
  const { buildStoreZip } = require('../tools/build-store-zip.cjs'); const files = unpack(buildStoreZip().zip);
  const manifest = JSON.parse(files['manifest.json']); assert.equal(manifest.version_name, '1.8.0 store'); assert.equal(manifest.key, undefined);
  assert.deepEqual(manifest.permissions, JSON.parse(source('manifest.json')).permissions);
  for (const name of ['background.js', 'popup.js', 'popup.html', 'sniff.js', 'content.js']) assert.equal(files[name], source(name), name + ' feature parity');
  const b = makeBackground({ files, openTabs: [tab], nativeReply });
  assert.equal((await b.message(action)).ok, true); await tick(); assert.ok(b.menus.some(m => m.id === 'youtube-link'));
  assert.equal(vm.runInContext('typeof sendYoutube', b.ctx), 'function');
  const dom = await popup(files, b);
  try { assert.equal(dom.window.document.getElementById('youtubePanel').hidden, false); } finally { dom.window.close(); }
  assert.equal((await b.message({ action: 'youtube-ribbon', url: canonical }, { ...b.pageSender(tab), frameId: 0, url: canonical })).ok, true);
  assert.equal(b.nativeRequests.filter(r => r.youtube).length, 2); assert.equal(b.cookieQueries.length, 0);
  for (const name of Object.keys(files).filter(n => n.startsWith('_locales/'))) assert.ok(JSON.parse(files[name]).youtube_send);
});

test('manual ribbon validates top-frame current page and rechecks after native capability ping', async () => {
  const b = makeBackground({ openTabs: [tab], nativeReply });
  const sender = { ...b.pageSender(tab), frameId: 0, url: canonical };
  assert.equal((await b.message({ action: 'youtube-ribbon', url: canonical }, sender)).ok, true);
  assert.equal(b.nativeRequests.filter(r => r.youtube).length, 1);
  assert.equal(b.cookieQueries.length, 0);
  for (const invalid of [{ ...sender, frameId: 2 }, { ...sender, url: 'https://example.org/' }]) {
    assert.equal((await b.message({ action: 'youtube-ribbon', url: canonical }, invalid)).ok, false);
  }
  assert.equal(b.nativeRequests.filter(r => r.youtube).length, 1);
  let finish; const tabs = [{ ...tab }];
  const stale = makeBackground({ openTabs: tabs, nativeReply: () => new Promise(resolve => { finish = resolve; }) });
  const sending = stale.message({ action: 'youtube-ribbon', url: canonical }, { ...stale.pageSender(tab), frameId: 0, url: canonical });
  await tick(); tabs[0].url = canonical.replace('aqz-KE-bpKQ', 'abcdefghijk'); finish({ ok: true, youtube: true });
  assert.equal((await sending).ok, false); assert.equal(stale.nativeRequests.length, 1);
});

test('ribbon accepts a current video reached by SPA navigation from the same-origin home page', async () => {
  const b = makeBackground({ openTabs: [tab], nativeReply });
  const result = await b.message({ action: 'youtube-ribbon', url: canonical }, { ...b.pageSender(tab), frameId: 0, url: 'https://www.youtube.com/' });
  assert.equal(result.ok, true); assert.equal(b.nativeRequests.filter(r => r.youtube).length, 1);
  assert.equal(b.nativeRequests.find(r => r.youtube).url, canonical); assert.equal(b.cookieQueries.length, 0);
});
