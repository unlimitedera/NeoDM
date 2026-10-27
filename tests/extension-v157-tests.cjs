'use strict';
/* 1.5.7: the browser's own request identity (User-Agent, Accept, Accept-Language) of the download's last hop goes to the app with a captured download.
   Header table, caps, privacy (memory only, never stored or logged), fallbacks for paths without a hop, release facts. */
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs'), path = require('node:path'), vm = require('node:vm');
const { makeBackground } = require('./extension-harness.cjs');
const root = path.join(__dirname, '..');
const read = f => fs.readFileSync(path.join(root, f), 'utf8');
const MB = 1024 * 1024;
const PAGE = 'https://files.example.com/get/abc';
const DL = 'https://cdn.example.com/' + 'T'.repeat(60) + '/archive.zip';
const UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/150.0.0.0 Safari/537.36';
const ACCEPT = 'text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7';
const LANG = 'de-DE,de;q=0.9,en-US;q=0.8,en;q=0.7';
const NAV = { userAgent: 'Mozilla/5.0 (X11; Linux x86_64) Browser/200 OwnNavigator', language: 'fr-FR', languages: ['fr-FR', 'fr', 'en-US', 'en'] };
const item = (url = DL, extra = {}) => ({ id: 31, url, finalUrl: url, state: 'in_progress', referrer: PAGE, totalBytes: 6 * MB, mime: 'application/zip', filename: 'archive.zip', ...extra });

function setup({ navigator = NAV, settings = { captureEnabled: true } } = {}) {
  const calls = [];
  const b = makeBackground({ settings, navigator, nativeReply: req => (req.action === 'browser-sites' ? { ok: true, sites: [] } : { ok: true, id: 'job-1' }) });
  const d = b.chrome.downloads;
  d.cancel = async id => calls.push('cancel:' + id); d.erase = async o => calls.push('erase:' + o.id); d.removeFile = async () => {};
  d.download = async o => { calls.push('download:' + o.url); return 99; }; d.search = async () => [];
  return { b, calls };
}
const wire = (over = {}) => [
  { name: 'Host', value: 'x' }, { name: 'Referer', value: PAGE }, { name: 'Accept-Encoding', value: 'gzip, deflate, br' }, { name: 'Cookie', value: 'sid=1' },
  { name: 'User-Agent', value: UA }, { name: 'Accept', value: ACCEPT }, { name: 'Accept-Language', value: LANG }, ...Object.entries(over).map(([name, value]) => ({ name, value }))
];
function hop(b, id, url, headers = wire()) {
  b.hooks.request({ requestId: id, url, method: 'GET', documentUrl: PAGE, tabId: 7 });
  b.hooks.sent({ requestId: id, url, method: 'GET', requestHeaders: headers });
}
const captures = b => b.nativeRequests.filter(r => r.url);
const identityOf = r => ({ userAgent: r.userAgent, accept: r.accept, acceptLanguage: r.acceptLanguage });
const held = b => vm.runInContext('heldIdentities.size', b.ctx);

test('a captured download carries the User-Agent, Accept and Accept-Language the browser sent to that address', async () => {
  const { b, calls } = setup();
  hop(b, 'i1', DL);
  await b.hooks.download(item());
  assert.deepEqual(identityOf(captures(b)[0]), { userAgent: UA, accept: ACCEPT, acceptLanguage: LANG });
  assert.ok(calls.includes('cancel:31'));
});

test('Accept-Encoding is not taken for Accept, header names are matched without case', async () => {
  const { b } = setup();
  hop(b, 'i2', DL, [{ name: 'user-agent', value: UA }, { name: 'ACCEPT-LANGUAGE', value: 'ja' }, { name: 'accept-encoding', value: 'gzip' }, { name: 'Accept', value: '*/*' }]);
  await b.hooks.download(item());
  assert.deepEqual(identityOf(captures(b)[0]), { userAgent: UA, accept: '*/*', acceptLanguage: 'ja' });
  const only = setup(); hop(only.b, 'i3', DL, [{ name: 'Accept-Encoding', value: 'gzip, br' }]);
  await only.b.hooks.download(item());
  assert.equal(captures(only.b)[0].accept, undefined, 'no Accept header seen: none is invented');
});

test('the identity is the one of the FINAL hop of a redirect chain', async () => {
  const { b } = setup();
  const A = 'https://files.example.com/dl/start';
  b.hooks.request({ requestId: 'r', url: A, method: 'GET', documentUrl: PAGE, tabId: 7 });
  b.hooks.sent({ requestId: 'r', url: A, requestHeaders: wire({ 'Accept-Language': 'xx' }).map(h => (h.name === 'User-Agent' ? { ...h, value: 'FirstHop/1' } : h)) });
  b.hooks.redirect({ requestId: 'r', url: A, statusCode: 302, redirectUrl: DL });
  hop(b, 'r', DL);
  await b.hooks.download(item(DL, { url: A, finalUrl: DL }));
  assert.equal(captures(b)[0].userAgent, UA); assert.equal(captures(b)[0].acceptLanguage, LANG);
});

test('without a Cookie header on the hop the identity is still sent; with the cookie switch off too', async () => {
  const none = setup(); hop(none.b, 'n1', DL, wire().filter(h => h.name !== 'Cookie'));
  await none.b.hooks.download(item());
  assert.equal(captures(none.b)[0].cookie, ''); assert.equal(captures(none.b)[0].userAgent, UA);
  const off = setup({ settings: { captureEnabled: true, sessionCookies: false } }); hop(off.b, 'n2', DL);
  await off.b.hooks.download(item());
  assert.equal(captures(off.b)[0].cookie, ''); assert.equal(captures(off.b)[0].userAgent, UA); assert.equal(captures(off.b)[0].acceptLanguage, LANG);
});

test('caps: 512 / 512 / 128 pass; one more, control characters and non-ASCII are dropped (never cut), each field on its own', async () => {
  const edge = setup(); hop(edge.b, 'c1', DL, wire({ 'User-Agent': 'u'.repeat(512), Accept: 'a'.repeat(512), 'Accept-Language': 'l'.repeat(128) }).filter((h, i, all) => all.findLastIndex(x => x.name === h.name) === i));
  await edge.b.hooks.download(item());
  assert.deepEqual([captures(edge.b)[0].userAgent.length, captures(edge.b)[0].accept.length, captures(edge.b)[0].acceptLanguage.length], [512, 512, 128]);
  const over = setup(); hop(over.b, 'c2', DL, [{ name: 'User-Agent', value: 'u'.repeat(513) }, { name: 'Accept', value: 'a'.repeat(513) }, { name: 'Accept-Language', value: 'l'.repeat(129) }]);
  await over.b.hooks.download(item());
  assert.deepEqual(identityOf(captures(over.b)[0]), { userAgent: NAV.userAgent, accept: undefined, acceptLanguage: 'fr-FR,fr;q=0.9,en-US;q=0.8,en;q=0.7' }, 'too long values are not forwarded; the browser\'s own fallback applies to the two with a fallback');
  for (const bad of ['a\r\nB: c', 'a\u0000b', 'a\u007fb', 'caf\u00e9', '\u2028', '   ']) {
    const t = setup(); hop(t.b, 'c3', DL, [{ name: 'User-Agent', value: bad }, { name: 'Accept', value: bad }, { name: 'Accept-Language', value: bad }]);
    await t.b.hooks.download(item());
    const r = captures(t.b)[0];
    assert.equal(r.accept, undefined, JSON.stringify(bad)); assert.notEqual(r.userAgent, bad); assert.notEqual(r.acceptLanguage, bad);
    assert.ok(/^[\u0020-\u007e]*$/.test((r.userAgent || '') + (r.acceptLanguage || '')), 'what is sent is printable ASCII');
  }
  const mixed = setup(); hop(mixed.b, 'c4', DL, [{ name: 'User-Agent', value: UA }, { name: 'Accept', value: 'bad\u0001' }, { name: 'Accept-Language', value: 'en' }]);
  await mixed.b.hooks.download(item());
  assert.deepEqual(identityOf(captures(mixed.b)[0]), { userAgent: UA, accept: undefined, acceptLanguage: 'en' });
});

test('table: at most 200 hops hold an identity (the oldest go first) and each lives 60 seconds', async () => {
  const many = setup();
  for (let i = 0; i < 230; i++) hop(many.b, 'm' + i, 'https://h' + i + '.example/f.dat');
  assert.equal(held(many.b), 200);
  const { b } = setup(); hop(b, 't1', DL);
  assert.equal(held(b), 1);
  assert.equal(vm.runInContext("freshIdentity(chains.get('t1').hops[0])", b.ctx).userAgent, UA); b.advance(59000);
  assert.equal(vm.runInContext("freshIdentity(chains.get('t1').hops[0])", b.ctx).userAgent, UA, 'still valid at 59 s'); b.advance(2000);
  assert.equal(vm.runInContext("freshIdentity(chains.get('t1').hops[0])", b.ctx), null, 'after the 60 s TTL the value is not used');
  b.hooks.request({ requestId: 'again', url: 'https://x.example/y', method: 'GET', documentUrl: PAGE, tabId: 7 });
  assert.equal(held(b), 0, 'an expired entry is purged at the next request');
});

test('privacy: the identity is in memory only, never in chrome.storage and never in the capture log', async () => {
  const { b } = setup(); hop(b, 'p1', DL);
  await b.hooks.download(item());
  await new Promise(r => setImmediate(r));
  const everything = JSON.stringify([b.local, b.sessionData, b.badge, b.menuUpdates]);
  assert.ok(!everything.includes('Chrome/150') && !everything.includes(LANG) && !everything.includes('image/avif'), everything.slice(0, 300));
  const log = JSON.stringify(b.sessionData['neodm.log'] || []);
  assert.ok(log.length > 2 && !log.includes('Mozilla') && !log.includes('de-DE'), 'the capture log has entries and none of them carries the identity: ' + log.slice(0, 200));
  const source = read('extension/background.js');
  assert.ok(!/storage\.(local|session)\.set\([^)]*(userAgent|acceptLanguage|identity)/.test(source), 'no storage write names the identity');
});

test('paths without a hop (media button, popup, ribbon, context menu) send this browser\'s own User-Agent and a language list from navigator', async () => {
  const { b } = setup();
  const direct = vm.runInContext('(async () => { const r = { url: "https://video.example/a.mp4", fileName: "a.mp4", referer: "", method: "GET" }; try { await send(r, null); } catch {} return r; })()', b.ctx);
  const sent = await direct;
  assert.equal(sent.userAgent, NAV.userAgent); assert.equal(sent.acceptLanguage, 'fr-FR,fr;q=0.9,en-US;q=0.8,en;q=0.7'); assert.equal(sent.accept, undefined);
  const one = setup({ navigator: { userAgent: 'Mozilla/5.0 Single', language: 'en-US' } });
  const r2 = await vm.runInContext('(async () => { const r = { url: "https://video.example/a.mp4", fileName: "a.mp4", referer: "", method: "GET" }; try { await send(r, null); } catch {} return r; })()', one.b.ctx);
  assert.equal(r2.acceptLanguage, 'en-US', 'a single language is sent as it is');
  const bare = setup({ navigator: null });
  const r3 = await vm.runInContext('(async () => { const r = { url: "https://video.example/a.mp4", fileName: "a.mp4", referer: "", method: "GET" }; try { await send(r, null); } catch {} return r; })()', bare.b.ctx);
  assert.equal(r3.userAgent, undefined); assert.equal(r3.acceptLanguage, undefined);
});

test('a caller cannot smuggle bad identity fields through send(): invalid ones are replaced or removed', async () => {
  const { b } = setup();
  const r = await vm.runInContext('(async () => { const r = { url: "https://video.example/a.mp4", fileName: "a.mp4", referer: "", method: "GET", userAgent: "x\\r\\ny", accept: "q\\u0000", acceptLanguage: 5 }; try { await send(r, null); } catch {} return r; })()', b.ctx);
  assert.equal(r.userAgent, NAV.userAgent); assert.equal(r.accept, undefined); assert.equal(r.acceptLanguage, 'fr-FR,fr;q=0.9,en-US;q=0.8,en;q=0.7');
});

test('the manifest asks for nothing new: webRequest only, the same permissions as 1.5.6', () => {
  const manifest = JSON.parse(read('extension/manifest.json'));
  assert.deepEqual(manifest.permissions.slice().sort(), ['contextMenus', 'downloads', 'nativeMessaging', 'storage', 'tabs', 'webRequest'].filter(p => manifest.permissions.includes(p)).sort());
  assert.ok(!manifest.permissions.includes('cookies') && !manifest.permissions.includes('webRequestBlocking'));
});

test('docs name the request identity: README compatibility note, CHROME.md, PRIVACY.md', () => {
  assert.match(read('README.md'), /Compatibility/); assert.match(read('README.md'), /User-Agent/);
  assert.match(read('docs/CHROME.md'), /User-Agent/); assert.match(read('docs/CHROME.md'), /Accept-Language/);
  const privacy = read('PRIVACY.md');
  assert.match(privacy, /User-Agent/); assert.match(privacy, /Accept-Language/); assert.match(privacy, /not stored|never stored|is not stored/i);
});
