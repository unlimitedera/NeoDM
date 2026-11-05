'use strict';
// 1.11.0: the extension reports its browser name (optional field `browser`) next to extVersion. Mocked native transport: component evidence only.
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs'), path = require('node:path');
const { makeBackground, tick } = require('./extension-harness.cjs');
const VERSION = JSON.parse(fs.readFileSync(path.join(__dirname, '..', 'extension', 'manifest.json'), 'utf8')).version;
const canonical = 'https://www.youtube.com/watch?v=aqz-KE-bpKQ';
const tab = { id: 7, url: canonical, title: 'Sample' };
const nav = (brands, userAgent = 'Mozilla/5.0 Chrome/130.0.0.0 Safari/537.36') => ({ userAgent, userAgentData: { brands: brands.map(brand => ({ brand, version: '130' })) } });
async function pingOf(navigator) {
  const b = makeBackground({ navigator });
  await b.message({ action: 'test' });
  return b.nativeRequests.find(r => r.action === 'ping');
}

test('browser name comes from userAgentData brands, ignoring the grease brand and the engine name', async () => {
  const cases = [
    [nav(['Not A;Brand', 'Chromium', 'Google Chrome']), 'Google Chrome'],
    [nav(['Chromium', 'Not)A;Brand', 'Microsoft Edge']), 'Microsoft Edge'],
    [nav(['Brave', 'Chromium', 'Not/A)Brand']), 'Brave'],
    [nav(['Not A;Brand', 'Chromium', 'Opera']), 'Opera'],
    [nav(['Not A;Brand', 'Chromium']), 'Chromium'],
    [nav(['Not A;Brand', 'Chromium', 'Vivaldi']), 'Vivaldi'],
  ];
  for (const [navigator, expected] of cases) assert.equal((await pingOf(navigator)).browser, expected);
});

test('fallbacks: the user agent names Edge, Opera and Vivaldi; unknown software sends no browser field', async () => {
  assert.equal((await pingOf({ userAgent: 'Mozilla/5.0 Chrome/130.0.0.0 Safari/537.36 Edg/130.0.0.0' })).browser, 'Microsoft Edge');
  assert.equal((await pingOf({ userAgent: 'Mozilla/5.0 Chrome/130.0.0.0 Safari/537.36 OPR/110.0.0.0' })).browser, 'Opera');
  assert.equal((await pingOf({ userAgent: 'Mozilla/5.0 Chrome/130.0.0.0 Safari/537.36' })).browser, 'Chromium');
  const none = await pingOf({ userAgent: 'Fixture browser' });
  assert.equal('browser' in none, false, 'nothing to say, so the field is left out (an older app sees the old request)');
  assert.equal(none.extVersion, VERSION);
});

test('hostile brand text is cleaned to the wire rule: letters, digits, space, dot, dash, underscore, at most 32', async () => {
  const hostile = await pingOf(nav(['Evil<script>alert(1)</script>Browser With A Very Long Name Over Limit']));
  assert.match(hostile.browser, /^[A-Za-z0-9][A-Za-z0-9 ._-]{0,31}$/);
  assert.ok(hostile.browser.length <= 32);
  const onlySymbols = await pingOf(nav(['<<<>>>']));
  assert.ok(!('browser' in onlySymbols) || /^[A-Za-z0-9]/.test(onlySymbols.browser));
});

test('a captured download carries extVersion and browser; an extension that cannot name its browser sends the old request', async () => {
  const named = makeBackground({ navigator: nav(['Not A;Brand', 'Chromium', 'Microsoft Edge']) });
  await named.hooks.menu({ menuItemId: 'link', linkUrl: 'https://files.example/a.zip', pageUrl: 'https://site.example/' }, { id: 1 }); await tick(20);
  const sent = named.nativeRequests.find(r => r.url === 'https://files.example/a.zip');
  assert.ok(sent, 'the request reached the host');
  assert.equal(sent.browser, 'Microsoft Edge'); assert.equal(sent.extVersion, VERSION);
  const plain = makeBackground({ navigator: { userAgent: 'Fixture browser' } });
  await plain.hooks.menu({ menuItemId: 'link', linkUrl: 'https://files.example/a.zip', pageUrl: 'https://site.example/' }, { id: 1 }); await tick(20);
  const old = plain.nativeRequests.find(r => r.url === 'https://files.example/a.zip');
  assert.ok(old); assert.equal('browser' in old, false);
});

test('YouTube handoff: browser is sent only when the host says it understands it (old host refuses unknown fields)', async () => {
  const send = { action: 'youtube-send', tabId: 7, url: canonical };
  const newHost = makeBackground({ openTabs: [tab], navigator: nav(['Chromium', 'Google Chrome']), nativeReply: r => r.action === 'ping' ? { ok: true, youtube: true, browser: true } : { ok: true, id: 'x' } });
  assert.equal((await newHost.message(send)).ok, true);
  assert.deepEqual(newHost.nativeRequests.at(-1), { url: canonical, youtube: true, extVersion: VERSION, browser: 'Google Chrome', keepAlive: true });
  const oldHost = makeBackground({ openTabs: [tab], navigator: nav(['Chromium', 'Google Chrome']), nativeReply: r => r.action === 'ping' ? { ok: true, youtube: true } : { ok: true, id: 'x' } });
  assert.equal((await oldHost.message(send)).ok, true);
  assert.deepEqual(oldHost.nativeRequests.at(-1), { url: canonical, youtube: true, extVersion: VERSION, keepAlive: true }, 'new extension against an old host: the request is exactly the old one');
});

test('browser start sends one quiet ping with the browser and version; installing sends nothing', async () => {
  const b = makeBackground({ navigator: nav(['Chromium', 'Microsoft Edge']) });
  await b.hooks.install({}); await tick();
  assert.equal(b.nativeRequests.length, 0);
  await b.hooks.startup(); await tick(20);
  assert.deepEqual(b.nativeRequests, [{ action: 'ping', extVersion: VERSION, browser: 'Microsoft Edge' }]);
  const down = makeBackground({ navigator: nav(['Chromium', 'Microsoft Edge']), nativeReply: () => new Error('no host') });
  await down.hooks.startup(); await tick(20); // a missing host never throws out of the startup hook
});
