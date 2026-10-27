'use strict';
/* 1.5.4 (amended): the Cookie header the browser sent with a download is forwarded to the app: header table, hops, TTL, caps, privacy, switch, migration. */
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs'), path = require('node:path'), vm = require('node:vm');
const { makeBackground, tick } = require('./extension-harness.cjs');
const root = path.join(__dirname, '..');
const MB = 1024 * 1024;
const PAGE = 'https://www.mediafire.com/file/g2ijb9cu0z794r3/test-payload.dat/file';
const DL = 'https://download2391.mediafire.com/' + 'T'.repeat(190) + '/g2ijb9cu0z794r3/test-payload.dat';
const item = (url = DL, extra = {}) => ({ id: 21, url, finalUrl: url, state: 'in_progress', referrer: PAGE, totalBytes: 6 * MB, mime: 'application/octet-stream', filename: 'test-payload.dat', ...extra });

function setup({ settings = { captureEnabled: true }, granted = false, cookieJar = [] } = {}) {
  const calls = [];
  const b = makeBackground({ settings, granted, cookieJar, nativeReply: req => (req.action === 'browser-sites' ? { ok: true, sites: [] } : { ok: true, id: 'job-1' }) });
  const d = b.chrome.downloads;
  d.cancel = async id => calls.push('cancel:' + id); d.erase = async o => calls.push('erase:' + o.id); d.removeFile = async () => {};
  d.download = async o => { calls.push('download:' + o.url); return 99; }; d.search = async () => [];
  return { b, calls };
}
const headers = (cookie, referer = PAGE) => [{ name: 'Host', value: 'x' }, ...(referer ? [{ name: 'Referer', value: referer }] : []), ...(cookie == null ? [] : [{ name: 'Cookie', value: cookie }]), { name: 'User-Agent', value: 'x' }];
/** One request hop as the browser makes it: onBeforeRequest, then onSendHeaders with the headers that went on the wire. */
function hop(b, id, url, cookie, extra = {}) {
  b.hooks.request({ requestId: id, url, method: 'GET', documentUrl: PAGE, tabId: 7 });
  b.hooks.sent({ requestId: id, url, method: 'GET', requestHeaders: extra.raw ?? headers(cookie, extra.referer) });
}
const captures = b => b.nativeRequests.filter(r => r.url);
const nativeCookie = b => captures(b).map(r => r.cookie);
const held = b => vm.runInContext('heldCookies.size', b.ctx);

test('the header listener asks for extraHeaders (Cookie is invisible without it) and needs no cookies permission', () => {
  const { b } = setup();
  assert.ok(b.hooks['sent:args'][1].includes('requestHeaders') && b.hooks['sent:args'][1].includes('extraHeaders'), JSON.stringify(b.hooks['sent:args']));
  const manifest = JSON.parse(fs.readFileSync(path.join(root, 'extension/manifest.json'), 'utf8'));
  assert.ok(manifest.permissions.includes('webRequest'));
  assert.ok(manifest.permissions.includes('cookies'), 'cookies is required since 1.5.9'); assert.equal(manifest.optional_permissions, undefined);
});

test('a captured download forwards the Cookie header the browser sent to that exact address, with referer and size', async () => {
  const { b, calls } = setup();
  hop(b, 'r1', DL, 'g2cn=1; ukey=abc123');
  await b.hooks.download(item());
  const req = captures(b)[0];
  assert.equal(req.cookie, 'g2cn=1; ukey=abc123');
  assert.equal(req.referer, 'https://www.mediafire.com/', 'cross-site referer is cut to the origin as before'); assert.equal(req.size, 6 * MB); assert.equal(req.url, DL);
  assert.ok(calls.includes('cancel:21'));
});

test('the cookie is the one of the FINAL hop: earlier hops of a redirect chain are never sent', async () => {
  const { b } = setup();
  const A = 'https://www.mediafire.com/dl/start', B = 'https://download2391.mediafire.com/tok/x.dat';
  b.hooks.request({ requestId: 'c1', url: A, method: 'GET', documentUrl: PAGE, tabId: 7 });
  b.hooks.sent({ requestId: 'c1', url: A, requestHeaders: headers('first=hop; sid=S') });
  b.hooks.redirect({ requestId: 'c1', url: A, statusCode: 302, redirectUrl: B });
  hop(b, 'c1', B, 'g2cn=1');
  await b.hooks.download(item(B, { url: A, finalUrl: B }));
  assert.deepEqual(nativeCookie(b), ['g2cn=1']);
});

test('no Cookie header on the final hop: nothing is sent, even when an earlier hop had one', async () => {
  const { b } = setup();
  const A = 'https://www.mediafire.com/dl/start', B = 'https://cdn.other.example/tok/x.dat';
  b.hooks.request({ requestId: 'c2', url: A, method: 'GET', documentUrl: PAGE, tabId: 7 });
  b.hooks.sent({ requestId: 'c2', url: A, requestHeaders: headers('first=hop') });
  b.hooks.redirect({ requestId: 'c2', url: A, statusCode: 302, redirectUrl: B });
  hop(b, 'c2', B, null);
  await b.hooks.download(item(B, { url: A, finalUrl: B }));
  assert.deepEqual(nativeCookie(b), ['']);
});

test('switch off: no Cookie header is forwarded, the rest of the request is unchanged', async () => {
  const { b } = setup({ settings: { captureEnabled: true, sessionCookies: false } });
  hop(b, 'r2', DL, 'g2cn=1');
  await b.hooks.download(item());
  const req = captures(b)[0];
  assert.equal(req.cookie, ''); assert.equal(req.size, 6 * MB); assert.equal(req.referer, 'https://www.mediafire.com/');
});

test('the cookies API path (required permission since 1.5.9): used when no header was seen, never preferred over the browser header', async () => {
  const none = setup({ settings: { captureEnabled: true, cookiesEnabled: true }, granted: true, cookieJar: [{ name: 'a', value: 'b' }] });
  none.b.hooks.request({ requestId: 'n1', url: DL, method: 'GET', documentUrl: PAGE, tabId: 7 }); // no onSendHeaders: nothing seen
  await none.b.hooks.download(item());
  assert.equal(captures(none.b)[0].cookie, 'a=b', 'cookies API as before');
  const both = setup({ settings: { captureEnabled: true, cookiesEnabled: true }, granted: true, cookieJar: [{ name: 'a', value: 'b' }] });
  hop(both.b, 'n2', DL, 'g2cn=1');
  await both.b.hooks.download(item());
  assert.equal(captures(both.b)[0].cookie, 'g2cn=1');
  const off = setup({ settings: { captureEnabled: true, cookiesEnabled: true, sessionCookies: false }, granted: true, cookieJar: [{ name: 'a', value: 'b' }] });
  hop(off.b, 'n3', DL, 'g2cn=1');
  await off.b.hooks.download(item());
  assert.equal(captures(off.b)[0].cookie, 'a=b', 'with the new switch off the old option behaves as before');
});

test('caps: 8192 characters pass, 8193 and control characters are dropped, table holds 200 and drops the oldest', async () => {
  const { b } = setup();
  hop(b, 'k1', DL, 'g2cn=1; p=' + 'x'.repeat(8192 - 10));
  await b.hooks.download(item());
  assert.equal(nativeCookie(b)[0].length, 8192);
  const big = setup(); hop(big.b, 'k2', DL, 'g2cn=1; p=' + 'x'.repeat(8193 - 10));
  await big.b.hooks.download(item());
  assert.deepEqual(nativeCookie(big.b), [''], 'an oversized header is not forwarded (never cut)');
  for (const bad of ['a=b\r\nHost: evil', 'a=b\u0000', 'a=b\u007f']) {
    const c = setup(); hop(c.b, 'k3', DL, bad); await c.b.hooks.download(item());
    assert.deepEqual(nativeCookie(c.b), [''], JSON.stringify(bad));
  }
  const many = setup();
  for (let i = 0; i < 230; i++) many.b.hooks.request({ requestId: 'm' + i, url: 'https://h' + i + '.example/f.dat', method: 'GET', documentUrl: PAGE, tabId: 7 });
  for (let i = 0; i < 230; i++) many.b.hooks.sent({ requestId: 'm' + i, url: 'https://h' + i + '.example/f.dat', requestHeaders: headers('sid=' + i) });
  assert.equal(held(many.b), 200, 'cap of 200 held headers');
  await many.b.hooks.download(item('https://h0.example/f.dat', { id: 30 })); await many.b.hooks.download(item('https://h229.example/f.dat', { id: 31 }));
  assert.deepEqual(nativeCookie(many.b), ['', 'sid=229']);
});

test('TTL: a header is dropped after 60 seconds and never used afterwards', async () => {
  const { b } = setup();
  hop(b, 't1', DL, 'g2cn=1');
  assert.equal(held(b), 1);
  b.advance(59000); await b.hooks.download(item());
  assert.deepEqual(nativeCookie(b), ['g2cn=1'], 'still fresh at 59 s');
  const late = setup(); hop(late.b, 't2', DL, 'g2cn=1');
  late.b.advance(61000);
  hop(late.b, 't3', 'https://other.example/x.dat', null); // any later request purges what is stale
  assert.equal(held(late.b), 0, 'purged');
  await late.b.hooks.download(item());
  assert.equal(captures(late.b).length, 0, 'the observation itself is stale after 60 s: not captured');
});

test('privacy: the cookie reaches only the native request, never storage, the capture log, the status or the downloads API', async () => {
  const { b, calls } = setup();
  const secret = 'g2cn=1; ukey=TOP-SECRET-VALUE-9f3';
  hop(b, 'p1', DL, secret);
  await b.hooks.download(item()); await tick(10);
  assert.equal(captures(b)[0].cookie, secret);
  for (const [name, data] of [['local', b.local], ['session', b.sessionData]]) assert.ok(!JSON.stringify(data).includes('TOP-SECRET-VALUE-9f3'), name + ' storage must not hold the cookie');
  assert.ok(!JSON.stringify(b.sessionData['neodm.log']).includes('TOP-SECRET'), 'log');
  assert.ok(!calls.join('|').includes('TOP-SECRET'), 'downloads API calls');
  assert.ok(!JSON.stringify(b.badge).includes('TOP-SECRET'));
  assert.equal(JSON.stringify(b.sentToTabs).includes('TOP-SECRET'), false, 'tabs are not told');
  assert.ok(!/storage\.(local|session)\.set\([^)]*cookie/i.test(fs.readFileSync(path.join(root, 'extension/background.js'), 'utf8')), 'no code path stores a cookie');
});

test('the browser restart after a failure does not carry the cookie (it is the browser\'s own request)', async () => {
  const calls = [];
  const b = makeBackground({ settings: { captureEnabled: true }, nativeReply: req => (req.action === 'browser-sites' ? { ok: true, sites: [] } : { ok: false, error: 'busy' }) });
  const d = b.chrome.downloads; d.cancel = async () => {}; d.erase = async () => {}; d.removeFile = async () => {}; d.search = async () => []; d.download = async o => { calls.push(o); return 5; };
  hop(b, 'q1', DL, 'g2cn=1'); await b.hooks.download(item());
  assert.equal(calls.length, 1); assert.deepEqual(Object.keys(calls[0]).sort(), ['conflictAction', 'url']);
});

test('settings: the switch is on for new and existing users, an explicit choice is kept', async () => {
  for (const [start, expected] of [[{}, true], [{ schema: 2, captureEnabled: true }, true], [{ schema: 2, sessionCookies: false }, false], [{ schema: 3, sessionCookies: false }, false], [{ schema: 2, sessionCookies: true }, true]]) {
    const { b } = setup({ settings: start }); await tick(10);
    assert.equal(b.local.sessionCookies !== false, expected, JSON.stringify(start));
    if (!('sessionCookies' in start) && start.schema !== 3) assert.equal(b.local.sessionCookies, true, 'stored explicitly on');
    assert.equal(b.local.schema, 4);
  }
});

test('media paths (popup, ribbon, menu) send no browser cookie: those URLs were not requested by the browser as downloads', async () => {
  const { b } = setup();
  hop(b, 'v1', 'https://cdn.example/v.mp4', 'sid=1');
  await b.message({ action: 'popup-download', tab: { id: 7, url: 'https://video.example/', title: 't' }, id: 'none' }); // unknown id: nothing is sent
  assert.equal(b.nativeRequests.filter(r => r.cookie).length, 0);
});

test('popup and locales: the checkbox exists, is wired to sessionCookies and is translated in all 10 locales', () => {
  const html = fs.readFileSync(path.join(root, 'extension/popup.html'), 'utf8'), js = fs.readFileSync(path.join(root, 'extension/popup.js'), 'utf8');
  assert.match(html, /id="sessionCookies"/); assert.match(html, /data-i18n="popup_session_cookies_hint"/);
  assert.match(js, /\['sessionCookies', 'sessionCookies'\]/); assert.match(js, /sessionCookies !== false/);
  const dirs = fs.readdirSync(path.join(root, 'extension/_locales')); assert.equal(dirs.length, 10);
  const en = JSON.parse(fs.readFileSync(path.join(root, 'extension/_locales/en/messages.json'), 'utf8'));
  for (const d of dirs) {
    const m = JSON.parse(fs.readFileSync(path.join(root, 'extension/_locales', d, 'messages.json'), 'utf8'));
    for (const k of ['popup_session_cookies', 'popup_session_cookies_hint']) assert.ok(m[k] && m[k].message.length > 10, d + ' ' + k);
    assert.ok(m.popup_session_cookies_hint.message.includes('NeoDM'), d);
    if (d !== 'en') assert.notEqual(m.popup_session_cookies.message, en.popup_session_cookies.message, d);
  }
});
