// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
'use strict';
/* 2.0.0: refresh link in the extension. The app opens the page of an expired download with a one-time code; the worker asks the app (through the host)
   whether the code is on its list, reads the page's addresses, picks the same file with a new signature, and reports it. No browser needed. */
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { makeBackground, tick } = require('./extension-harness.cjs');
const NeoSniff = require('../extension/sniff.js');
const root = path.join(__dirname, '..');

const NONCE = '0123456789abcdef0123456789abcdef';
const PAGE = 'https://site.example/files/model?id=9';
const OLD = 'https://cdn.example/dl/model-00002-of-00005.gguf?sig=old&exp=1';
const FRESH = 'https://cdn.example/dl/model-00002-of-00005.gguf?sig=new&exp=2';
const entry = (extra = {}) => ({ nonce: NONCE, page: PAGE, old: OLD, until: Date.now() + 300000, ...extra });
function setup({ watch = [entry()], scans = [], act = true, watchReply } = {}) {
  const queue = [...scans];
  let last = null;
  const b = makeBackground({
    cookieJar: [{ name: 'sid', value: 'abc' }],
    nativeReply: req => {
      if (req.action === 'ping') return { ok: true };
      if (req.action === 'refresh-watch') return watchReply ?? { ok: true, watch };
      if (req.action === 'refresh-report') return { ok: true, act };
      return { ok: true, id: 'saved-1' };
    },
    tabReply: (tabId, msg) => { if (msg?.type !== 'neodm-refresh-scan') return undefined; if (queue.length) last = queue.shift(); return last; },
    navigator: { userAgent: 'Mozilla/5.0 Test' },
  });
  return b;
}
const scans = b => b.sentToTabs.filter(x => x.msg.type === 'neodm-refresh-scan');
const reports = b => b.nativeRequests.filter(r => r.action === 'refresh-report');
const open = (b, id = 11, nonce = NONCE) => b.hooks.tabUpdated(id, { url: PAGE + '#neodm-refresh=' + nonce }, { id, url: PAGE + '#neodm-refresh=' + nonce });
const step = async b => { const t = b.timers.splice(0); for (const x of t) x.fn(); await tick(30); };

test('a fresh address on the page is reported with the page, the cookie and the browser identity', async () => {
  const b = setup({ scans: [{ urls: ['https://site.example/a', OLD, FRESH], password: false, href: PAGE }] });
  await open(b); await tick(40);
  const r = reports(b);
  assert.deepEqual(r.map(x => x.state), ['contact', 'found']);
  assert.equal(r[0].nonce, NONCE);
  assert.equal(r[1].url, FRESH); assert.equal(r[1].referer, PAGE); assert.equal(r[1].cookie, 'sid=abc'); assert.equal(r[1].userAgent, 'Mozilla/5.0 Test');
  assert.equal(scans(b).length, 1);
  assert.deepEqual(b.sessionData['neodm.refresh'], {}, 'nothing is left in the session after the answer');
});

test('a tab whose code is not on the app list is never read', async () => {
  const b = setup({ watch: [] });
  await open(b); await tick(30);
  assert.equal(scans(b).length, 0);
  assert.equal(reports(b).length, 0);
  assert.deepEqual(b.nativeRequests.map(x => x.action).filter(a => a !== 'ping'), ['refresh-watch']);
});

test('a wrong code, a short code and an ordinary page do nothing', async () => {
  const b = setup();
  await open(b, 12, 'ffffffffffffffff');
  await b.hooks.tabUpdated(13, { url: PAGE + '#neodm-refresh=zz' }, { id: 13, url: PAGE });
  await b.hooks.tabUpdated(14, { url: PAGE }, { id: 14, url: PAGE });
  await tick(30);
  assert.equal(scans(b).length, 0);
  assert.equal(reports(b).length, 0);
});

test('an older app that does not know the request leaves the tab alone', async () => {
  for (const reply of [{ ok: false, error: 'Unsupported request.' }, { ok: true }, new Error('host missing')]) {
    const b = setup({ watchReply: reply });
    await open(b); await tick(30);
    assert.equal(scans(b).length, 0);
    assert.equal(reports(b).length, 0);
  }
});

test('a sign-in page is reported once, then the link is found after the person signed in', async () => {
  const b = setup({ scans: [
    { urls: [], password: true, href: 'https://site.example/login' },
    { urls: [], password: true, href: 'https://site.example/login' },
    { urls: [FRESH], password: false, href: PAGE },
  ] });
  await open(b); await tick(40);
  assert.deepEqual(reports(b).map(x => x.state), ['contact', 'signin']);
  await step(b);
  assert.deepEqual(reports(b).map(x => x.state), ['contact', 'signin'], 'asked again, still one sign-in report');
  await step(b);
  assert.deepEqual(reports(b).map(x => x.state), ['contact', 'signin', 'found']);
  assert.equal(reports(b)[2].url, FRESH);
});

test('a page with nothing to find is reported as not found after the quiet time, not before', async () => {
  const b = setup({ scans: [{ urls: ['https://site.example/a'], password: false, href: PAGE }] });
  await open(b); await tick(30);
  assert.deepEqual(reports(b).map(x => x.state), ['contact']);
  b.advance(25000); await step(b);
  assert.deepEqual(reports(b).map(x => x.state), ['contact', 'notfound']);
  assert.deepEqual(b.sessionData['neodm.refresh'], {});
});

test('picking the fresh address: the same path wins, a file name needs an extension, the old address and other files are skipped', () => {
  const pick = NeoSniff.refreshPick;
  assert.equal(pick(OLD, [OLD, 'https://x.example/other.bin', FRESH], PAGE), FRESH);
  assert.equal(pick(OLD, [OLD], PAGE), '');
  assert.equal(pick(OLD, ['https://other.example/dl/model-00002-of-00005.gguf?t=2'], PAGE), 'https://other.example/dl/model-00002-of-00005.gguf?t=2');
  assert.equal(pick(OLD, ['https://other.example/dl/model-00003-of-00005.gguf?t=2'], PAGE), '', 'another part is not this file');
  assert.equal(pick('https://cdn.example/get/download?id=1&sig=a', ['https://cdn.example/x/download?id=2'], PAGE), '', 'a bare word is not a file name');
  assert.equal(pick('https://cdn.example/get/download?id=1&sig=a', ['https://cdn.example/get/download?id=1&sig=b'], PAGE), 'https://cdn.example/get/download?id=1&sig=b');
  assert.equal(pick(OLD, ['ftp://cdn.example/dl/model-00002-of-00005.gguf', 'https://u:p@cdn.example/dl/model-00002-of-00005.gguf', 'javascript:1', 5, null], PAGE), '');
  assert.equal(pick(OLD, 'nope', PAGE), '');
  assert.equal(pick('not an address', [FRESH], PAGE), '');
});

test('sign-in detection: a password box, or a move to a login path the page was not on', () => {
  const s = NeoSniff.refreshSignIn;
  assert.equal(s({ password: true, href: PAGE }, PAGE), true);
  assert.equal(s({ password: false, href: 'https://site.example/account/login?next=/x' }, PAGE), true);
  assert.equal(s({ password: false, href: 'https://site.example/users/sign_in' }, PAGE), true);
  assert.equal(s({ password: false, href: PAGE }, PAGE), false);
  assert.equal(s({ password: false, href: 'https://site.example/login' }, 'https://site.example/login'), false, 'the page itself is the login page');
  assert.equal(s(null, PAGE), false);
  assert.equal(NeoSniff.refreshNonce('https://a.example/p#neodm-refresh=' + NONCE), NONCE);
  assert.equal(NeoSniff.refreshNonce('https://a.example/p#neodm-refresh=XYZ'), '');
  assert.equal(NeoSniff.refreshNonce('https://a.example/p#other'), '');
});

test('a download request carries the page address it started from, without a fragment, and only when it is a web page', async () => {
  const b = setup();
  await b.ctx.send({ url: 'https://cdn.example/f.bin', fileName: 'f.bin', referer: 'https://site.example/files/model?id=9', method: 'GET' }, { id: 3, url: 'https://site.example/files/model?id=9#top' });
  assert.equal(b.nativeRequests.at(-1).pageUrl, 'https://site.example/files/model?id=9');
  await b.ctx.send({ url: 'https://cdn.example/f.bin', fileName: 'f.bin', referer: '', method: 'GET' }, { id: 3, url: 'chrome://newtab/' });
  assert.equal('pageUrl' in b.nativeRequests.at(-1), false);
  await b.ctx.send({ url: 'https://cdn.example/f.bin', fileName: 'f.bin', referer: '', method: 'GET' }, { id: 3, url: 'https://u:p@site.example/x' });
  assert.equal('pageUrl' in b.nativeRequests.at(-1), false);
});

test('the extension version equals the app version and no permission was added', () => {
  const manifest = JSON.parse(fs.readFileSync(path.join(root, 'extension/manifest.json'), 'utf8'));
  const props = fs.readFileSync(path.join(root, 'Directory.Build.props'), 'utf8');
  assert.equal(manifest.version, /<Version>([^<]+)<\/Version>/.exec(props)[1]);
  assert.deepEqual(manifest.permissions, ['nativeMessaging', 'downloads', 'contextMenus', 'storage', 'webRequest', 'tabs', 'cookies']);
  assert.deepEqual(manifest.content_scripts.map(c => c.js), [['i18n.js', 'sniff.js', 'content.js'], ['hooks.js'], ['widget.js']]);
});

test('the page scan answers only for the top frame and only for the extension itself', () => {
  const src = fs.readFileSync(path.join(root, 'extension/content.js'), 'utf8');
  assert.match(src, /m\.type === 'neodm-refresh-scan'\) \{ if \(isTop && sender\.id === chrome\.runtime\.id && respond\)/);
});
