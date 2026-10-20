'use strict';
/* 1.4.5: capture decisions are made per redirect hop (a form POST answered with 301/302/303 becomes a GET of the file), the final URL is sent,
   and the last decisions are logged for the popup. */
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs'), path = require('node:path');
const { JSDOM } = require('jsdom');
const { makeBackground, chain, tick, source, json, withI18n } = require('./extension-harness.cjs');

const PAGE = 'https://filekeeper.net/zu3qi0dj8x8i', FORM = 'https://filekeeper.net/download';
const TUNNEL = 'https://tunnel1.dlproxy.uk/download/' + 'Ab3_-xYz'.repeat(41).slice(0, 330 - 'https://tunnel1.dlproxy.uk/download/'.length);
const LOG = 'neodm.log';

function setup(settings = { captureEnabled: true }, extra = {}) {
  const b = makeBackground({ settings, ...extra }), calls = [];
  const d = b.chrome.downloads;
  d.cancel = async () => calls.push('cancel'); d.erase = async () => calls.push('erase'); d.removeFile = async () => calls.push('removeFile');
  d.search = async () => [{ state: 'interrupted' }]; d.download = async o => calls.push('restart:' + o.url);
  return { b, calls };
}
const item = (extra = {}) => ({ id: 21, url: FORM, finalUrl: TUNNEL, referrer: PAGE, state: 'in_progress', filename: 'C:\\Users\\me\\Downloads\\archive.zip', mime: 'application/zip', ...extra });
const logOf = b => json(b.sessionData[LOG] || []);
const sentUrls = b => b.nativeRequests.filter(r => r.url);

test('filekeeper chain: POST -> 302 -> GET is captured with the final URL and the page as referer, browser download removed first', async () => {
  assert.equal(TUNNEL.length, 330);
  const log = [];
  const { b } = setup({ captureEnabled: true });
  b.chrome.downloads.cancel = async () => log.push('cancel'); b.chrome.downloads.erase = async () => log.push('erase'); b.chrome.downloads.search = async () => { log.push('search'); return [{ state: 'interrupted' }]; };
  b.chrome.downloads.download = async () => log.push('restart');
  const b2 = b; // native reply order check
  chain(b, 'q1', [{ url: FORM, method: 'POST', status: 302 }, { url: TUNNEL, method: 'GET', status: 0 }], { documentUrl: PAGE });
  await b.hooks.download(item());
  const sent = sentUrls(b2);
  assert.equal(sent.length, 1);
  assert.equal(sent[0].url, TUNNEL); assert.equal(sent[0].referer, 'https://filekeeper.net/'); /* 1.5.3: the tunnel is another origin, so only the page origin is sent */ assert.equal(sent[0].method, 'GET'); assert.equal(sent[0].fileName, 'archive.zip');
  assert.equal(sent[0].noProbe, true, 'the app must not request a possibly single use address');
  assert.deepEqual(log, ['cancel', 'search', 'erase']);
  const entries = logOf(b); assert.equal(entries.length, 1);
  assert.equal(entries[0].decision, 'captured'); assert.equal(entries[0].host, 'filekeeper.net'); assert.equal(entries[0].code, 'sent');
});

test('without item.referrer the page of the first request is the referer', async () => {
  const { b } = setup();
  chain(b, 'q', [{ url: FORM, method: 'POST', status: 302 }, { url: TUNNEL, method: 'GET', status: 0 }], { documentUrl: PAGE });
  await b.hooks.download(item({ referrer: '' }));
  assert.equal(sentUrls(b)[0].referer, 'https://filekeeper.net/');
});

test('the chain is still usable when the browser reports the download after the requests completed (completion cleanup)', async () => {
  const { b } = setup();
  chain(b, 'c', [{ url: FORM, method: 'POST', status: 302 }, { url: TUNNEL, method: 'GET', status: 0 }]); // chain() fires onCompleted
  await tick();
  await b.hooks.download(item());
  assert.equal(sentUrls(b).length, 1);
});

for (const code of [301, 303]) {
  test(`POST -> ${code} -> GET is captured`, async () => {
    const { b } = setup();
    chain(b, 'r', [{ url: 'https://files.example/submit', method: 'POST', status: code }, { url: 'https://cdn.example/f/a.zip', method: 'GET', status: 0 }]);
    await b.hooks.download(item({ url: 'https://files.example/submit', finalUrl: 'https://cdn.example/f/a.zip' }));
    assert.equal(sentUrls(b).length, 1); assert.equal(sentUrls(b)[0].url, 'https://cdn.example/f/a.zip');
    assert.equal(logOf(b)[0].decision, 'captured');
  });
}

test('POST -> 302 -> GET -> 302 -> GET is captured (several hops)', async () => {
  const { b } = setup();
  chain(b, 'm', [{ url: FORM, method: 'POST', status: 302 }, { url: 'https://hop.example/x', method: 'GET', status: 302 }, { url: TUNNEL, method: 'GET', status: 0 }]);
  await b.hooks.download(item());
  assert.equal(sentUrls(b).length, 1);
});

for (const [code, finalMethod] of [[307, 'GET'], [307, 'POST'], [308, 'POST'], [308, 'GET']]) {
  test(`POST -> ${code} -> ${finalMethod} is skipped: Redirect keeps POST`, async () => {
    const { b, calls } = setup();
    chain(b, 'k', [{ url: FORM, method: 'POST', status: code }, { url: TUNNEL, method: finalMethod, status: 0 }]);
    await b.hooks.download(item());
    assert.equal(sentUrls(b).length, 0); assert.deepEqual(calls, [], 'the browser download is untouched');
    const e = logOf(b)[0]; assert.equal(e.decision, 'skipped'); assert.equal(e.code, 'redirect-keeps-post'); assert.equal(e.reason, 'Redirect keeps POST (307/308)');
  });
}

test('POST answered with the file itself (no redirect) is skipped: Form POST download', async () => {
  const { b, calls } = setup();
  chain(b, 'f', [{ url: 'https://files.example/get', method: 'POST', status: 0 }]);
  await b.hooks.download(item({ url: 'https://files.example/get', finalUrl: 'https://files.example/get' }));
  assert.equal(sentUrls(b).length, 0); assert.deepEqual(calls, []);
  const e = logOf(b)[0]; assert.equal(e.code, 'form-post'); assert.equal(e.reason, 'Form POST download, NeoDM cannot repeat a form submission');
});

test('GET -> 302 -> POST (final hop POST) is skipped', async () => {
  const { b } = setup();
  chain(b, 'g', [{ url: 'https://a.example/go', method: 'GET', status: 302 }, { url: 'https://b.example/post', method: 'POST', status: 0 }]);
  await b.hooks.download(item({ url: 'https://a.example/go', finalUrl: 'https://b.example/post' }));
  assert.equal(sentUrls(b).length, 0); assert.equal(logOf(b)[0].code, 'form-post');
});

test('GET -> 302 -> GET is still captured, without noProbe', async () => {
  const { b } = setup();
  chain(b, 'p', [{ url: 'https://a.example/go', method: 'GET', status: 302 }, { url: 'https://cdn.example/a.zip', method: 'GET', status: 0 }]);
  await b.hooks.download(item({ url: 'https://a.example/go', finalUrl: 'https://cdn.example/a.zip' }));
  assert.equal(sentUrls(b).length, 1); assert.equal('noProbe' in sentUrls(b)[0], false);
});

test('a plain GET is captured; a POST then GET to the same URL within 60 s is still vetoed', async () => {
  const { b } = setup();
  chain(b, 'a', [{ url: 'https://x.example/a.zip', method: 'GET', status: 0 }]);
  await b.hooks.download(item({ url: 'https://x.example/a.zip', finalUrl: 'https://x.example/a.zip' }));
  assert.equal(sentUrls(b).length, 1);
  const v = setup();
  chain(v.b, 'p1', [{ url: 'https://x.example/b.zip', method: 'POST', status: 0 }]);
  chain(v.b, 'g1', [{ url: 'https://x.example/b.zip', method: 'GET', status: 0 }]);
  await v.b.hooks.download(item({ url: 'https://x.example/b.zip', finalUrl: 'https://x.example/b.zip' }));
  assert.equal(sentUrls(v.b).length, 0); assert.equal(logOf(v.b)[0].code, 'post-ambiguous');
});

test('a download whose request was never seen is left to the browser', async () => {
  const { b } = setup();
  await b.hooks.download(item({ url: 'https://x.example/n.zip', finalUrl: 'https://x.example/n.zip' }));
  assert.equal(sentUrls(b).length, 0); assert.equal(logOf(b)[0].code, 'unseen');
});

test('an unrelated chain cannot vouch for the final URL', async () => {
  const { b } = setup();
  chain(b, 'u1', [{ url: FORM, method: 'POST', status: 302 }, { url: 'https://other.example/z', method: 'GET', status: 0 }]);
  chain(b, 'u2', [{ url: TUNNEL, method: 'GET', status: 0 }]);
  await b.hooks.download(item());
  assert.equal(sentUrls(b).length, 0);
});

test('blob, data, filesystem, non-web and private-window downloads are skipped with a reason', async () => {
  for (const [extra, code, reason] of [
    [{ url: 'blob:https://x.example/1', finalUrl: 'blob:https://x.example/1' }, 'blob', 'Blob or data URL'],
    [{ url: 'data:text/plain,x', finalUrl: 'data:text/plain,x' }, 'blob', 'Blob or data URL'],
    [{ url: 'filesystem:https://x.example/temporary/a', finalUrl: 'filesystem:https://x.example/temporary/a' }, 'blob', 'Blob or data URL'],
    [{ url: 'ftp://x.example/a', finalUrl: 'ftp://x.example/a' }, 'protocol', 'Not a plain web address'],
    [{ incognito: true }, 'private', 'Private window'],
  ]) {
    const { b, calls } = setup();
    chain(b, 'i', [{ url: FORM, method: 'POST', status: 302 }, { url: TUNNEL, method: 'GET', status: 0 }]);
    await b.hooks.download(item(extra));
    assert.equal(sentUrls(b).length, 0, code); assert.deepEqual(calls, []);
    const e = logOf(b)[0]; assert.equal(e.code, code); assert.equal(e.reason, reason);
  }
});

test('capture off, paused site and a turned-off type are logged with their reasons and flash the badge', async () => {
  const run = async (settings, session, expectedCode, expectedReason) => {
    const { b } = setup(settings, { sessionData: session });
    chain(b, 'z', [{ url: 'https://s.example/a.zip', method: 'GET', status: 0 }]);
    await b.hooks.download(item({ url: 'https://s.example/a.zip', finalUrl: 'https://s.example/a.zip', referrer: 'https://s.example/' }));
    assert.equal(sentUrls(b).length, 0);
    const e = logOf(b).at(-1); assert.equal(e.code, expectedCode); assert.equal(e.reason, expectedReason); assert.equal(e.decision, 'skipped');
    assert.deepEqual(b.badge.map(x => x.text), ['!'], expectedCode);
    assert.ok(b.timers.some(t => t.ms === 2000), 'the badge is cleared after 2 seconds');
    b.timers.find(t => t.ms === 2000).fn(); await tick();
    assert.deepEqual(b.badge.map(x => x.text), ['!', '']);
  };
  await run({ captureEnabled: false }, {}, 'off', 'Capture is off');
  await run({ captureEnabled: true }, { 'neodm.pause': { all: false, hosts: ['s.example'] } }, 'paused', 'Paused for this site');
  await run({ captureEnabled: true, mediaTypes: { archive: false } }, {}, 'type', 'Type turned off in settings');
});

test('no badge for protocol reasons, and none for a captured download', async () => {
  const { b } = setup();
  chain(b, 'n1', [{ url: FORM, method: 'POST', status: 0 }]);
  await b.hooks.download(item({ finalUrl: FORM }));
  chain(b, 'n2', [{ url: FORM, method: 'POST', status: 302 }, { url: TUNNEL, method: 'GET', status: 0 }]);
  await b.hooks.download(item({ id: 22 }));
  assert.deepEqual(logOf(b).map(e => e.code), ['form-post', 'sent']);
  assert.deepEqual(b.badge, []);
});

test('NeoDM unreachable: the browser restarts the download and the log says so; user cancel is logged as captured', async () => {
  const down = setup();
  down.b.chrome.runtime.connectNative = () => { throw new Error('no host'); };
  chain(down.b, 'd', [{ url: FORM, method: 'POST', status: 302 }, { url: TUNNEL, method: 'GET', status: 0 }]);
  await down.b.hooks.download(item());
  assert.deepEqual(down.calls, ['cancel', 'erase', 'restart:' + TUNNEL]);
  const e = logOf(down.b)[0]; assert.equal(e.code, 'unreachable'); assert.equal(e.reason, 'NeoDM not reachable, browser kept the download');
  const cancelled = makeBackground({ settings: { captureEnabled: true }, nativeReply: () => ({ ok: false, error: 'cancelled', cancelled: true }) });
  Object.assign(cancelled.chrome.downloads, { cancel: async () => {}, erase: async () => {}, search: async () => [] });
  chain(cancelled, 'c', [{ url: FORM, method: 'POST', status: 302 }, { url: TUNNEL, method: 'GET', status: 0 }]);
  await cancelled.hooks.download(item());
  assert.equal(logOf(cancelled)[0].code, 'cancelled'); assert.equal(logOf(cancelled)[0].decision, 'captured');
});

test('downloads started by the extension itself, or not running, are not logged', async () => {
  const { b } = setup();
  await b.hooks.download(item({ byExtensionId: 'ext-id' })); await b.hooks.download(item({ state: 'complete' })); await b.hooks.download(item({ paused: true }));
  assert.deepEqual(logOf(b), []);
});

test('the log keeps the 20 newest entries, with time, host, decision, code and reason only', async () => {
  const { b } = setup({ captureEnabled: false });
  for (let i = 0; i < 25; i++) await b.hooks.download(item({ url: `https://h${i}.example/p?secret=1`, finalUrl: `https://h${i}.example/p?secret=1` }));
  const log = logOf(b); assert.equal(log.length, 20);
  assert.equal(log[0].host, 'h5.example'); assert.equal(log.at(-1).host, 'h24.example');
  assert.deepEqual(Object.keys(log[0]).sort(), ['code', 'decision', 'host', 'reason', 'time']);
  assert.doesNotMatch(JSON.stringify(log), /secret|https?:/);
});

test('hop and chain tables stay bounded', async () => {
  const { b } = setup();
  const hops = Array.from({ length: 60 }, (_, i) => ({ url: `https://loop.example/${i}`, method: 'GET', status: i < 59 ? 302 : 0 }));
  chain(b, 'long', hops, { open: true });
  for (let i = 0; i < 700; i++) b.hooks.request({ requestId: 'many' + i, url: 'https://t.example/' + i, method: 'GET' });
  await b.hooks.download(item({ url: 'https://t.example/699', finalUrl: 'https://t.example/699' }));
  assert.equal(sentUrls(b).length, 1);
  await b.hooks.download(item({ id: 23, url: 'https://t.example/0', finalUrl: 'https://t.example/0' }));
  assert.equal(sentUrls(b).length, 1, 'the oldest observation was dropped');
});

async function popup(log) {
  const dom = new JSDOM(fs.readFileSync(path.join(__dirname, '../extension/popup.html'), 'utf8'), { runScripts: 'outside-only', url: 'chrome-extension://ext-id/popup.html' }), w = dom.window;
  const session = { [LOG]: log };
  w.chrome = {
    storage: { local: { get: async () => ({}), set: async () => {} }, session: { get: async k => (k in session ? { [k]: JSON.parse(JSON.stringify(session[k])) } : {}), set: async o => Object.assign(session, o) } },
    tabs: { query: async () => [{ id: 1, url: 'https://site.example/page' }] },
    runtime: { getManifest: () => ({ version: '1.4.5' }), sendMessage: async () => ({ items: [] }) },
    permissions: { request: async () => true, remove: async () => true },
  };
  withI18n(w); w.eval(source('sniff.js')); w.eval(source('popup.js')); await tick(14);
  return { doc: w.document, session };
}

test('popup: Recent downloads shows the newest 10 first as text, with host and reason, and Clear empties the log', async () => {
  const log = Array.from({ length: 15 }, (_, i) => ({ time: 1760000000000 + i * 60000, host: `<b>h${i}</b>.example`, decision: i % 2 ? 'captured' : 'skipped', code: i % 2 ? 'sent' : 'form-post', reason: i % 2 ? 'Sent to NeoDM' : 'Form POST download, NeoDM cannot repeat a form submission' }));
  const { doc, session } = await popup(log);
  const rows = [...doc.querySelectorAll('#recent li')];
  assert.equal(rows.length, 10);
  assert.match(rows[0].textContent, /h14/); assert.match(rows[9].textContent, /h5/);
  assert.match(rows[0].textContent, /left to the browser/); assert.match(rows[0].textContent, /Form POST download/); assert.match(rows[1].textContent, /taken by NeoDM/);
  assert.equal(doc.querySelector('#recent b'), null, 'text only, no markup');
  assert.doesNotMatch(doc.getElementById('recent').textContent, /https?:/);
  assert.equal(doc.getElementById('recentNone').hidden, true);
  doc.getElementById('recentClear').click(); await tick(10);
  assert.equal(JSON.stringify(session[LOG]), '[]'); assert.equal(doc.querySelectorAll('#recent li').length, 0);
  assert.equal(doc.getElementById('recentNone').hidden, false); assert.equal(doc.getElementById('recentClear').hidden, true);
});

test('popup: an empty or damaged log shows the empty text', async () => {
  for (const log of [undefined, 'x', [null], [42, 'y']]) {
    const { doc } = await popup(log);
    assert.equal(doc.querySelectorAll('#recent li').length, 0); assert.equal(doc.getElementById('recentNone').hidden, false);
  }
});

const root = path.join(__dirname, '..');
/* The address limits (400 and 8192 accepted, 8193 rejected, noProbe forwarded) are tested in the native host self-test and the app protocol test; a real-host
   run here would reach the user's running app through the fixed pipe name when the installed host is older. */
test('the changelog keeps the 1.4.5 entry', () => {
  assert.match(fs.readFileSync(path.join(root, 'CHANGELOG.md'), 'utf8'), /^## \[1\.4\.5\] - 2026-10-18$/m);
});
