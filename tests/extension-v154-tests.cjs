'use strict';
/* 1.5.4: browser size to the app, hand back to the browser, sites left to the browser, double start of one download, Save as window, popup list. */
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs'), path = require('node:path');
const { JSDOM } = require('jsdom');
const S = require('../extension/sniff.js');
const Bridge = require('../extension/bridge.js');
const { makeBackground, tick, source, json, withI18n } = require('./extension-harness.cjs');
const root = path.join(__dirname, '..');
const MB = 1024 * 1024;
const manifest = JSON.parse(fs.readFileSync(path.join(root, 'extension/manifest.json'), 'utf8'));
const DL = 'https://download1336.mediafire.com/' + 'a'.repeat(200) + '/abcdefgh/neodm-test-file.dat';
const dl = (url = DL, extra = {}) => ({ id: 11, url, finalUrl: url, state: 'in_progress', referrer: 'https://www.mediafire.com/file/abc/neodm-test-file.dat/file', totalBytes: 103 * MB, mime: 'application/octet-stream', filename: 'neodm-test-file.dat', ...extra });

/** A worker whose downloads API records what happens to the browser download. */
function setup({ settings = { captureEnabled: true }, nativeReply, wait } = {}) {
  const calls = [];
  const reply = req => (req.action === 'browser-sites' ? (nativeReply?.(req) ?? { ok: true, sites: [] }) : nativeReply?.(req) ?? { ok: true, id: 'job-1' });
  const b = makeBackground({ settings, nativeReply: wait ? async req => { if (req.action !== 'browser-sites') await wait(); return reply(req); } : reply });
  const d = b.chrome.downloads;
  d.cancel = async id => calls.push('cancel:' + id); d.erase = async o => calls.push('erase:' + o.id); d.removeFile = async id => calls.push('removeFile:' + id);
  d.download = async o => { calls.push('download:' + o.url); return 99; }; d.search = async () => [];
  return { b, calls };
}
const seen = (b, url = DL) => b.hooks.request({ requestId: 'r' + Math.random(), url, method: 'GET', documentUrl: 'https://www.mediafire.com/file/abc/neodm-test-file.dat/file' });
const log = b => (b.sessionData['neodm.log'] || []);

test('the changelog keeps the 1.5.4 entry; props, manifest and README agree on one current version', () => {
  const props = fs.readFileSync(path.join(root, 'Directory.Build.props'), 'utf8');
  const v = /<Version>([^<]+)<\/Version>/.exec(props)[1];
  assert.equal(manifest.version, v);
  const log = fs.readFileSync(path.join(root, 'CHANGELOG.md'), 'utf8');
  assert.equal(/^## \[([^\]]+)\]/m.exec(log)[1], v);
  const entry = log.slice(log.indexOf('## [1.5.4]'), log.indexOf('## [1.5.3]'));
  assert.ok(entry.length > 100); assert.doesNotMatch(entry, /\u2014/, 'no em dashes in the entry');
  const readme = fs.readFileSync(path.join(root, 'README.md'), 'utf8');
  assert.ok(readme.includes('Current version: ' + v)); assert.match(readme, /^\| 1\.5\.4 \| /m);
});

/* ---- registrable site ---- */
test('siteOf: registrable parent with a small public-suffix rule', () => {
  const cases = { 'download1336.mediafire.com': 'mediafire.com', 'www.mediafire.com': 'mediafire.com', 'mediafire.com': 'mediafire.com', 'WWW.MediaFire.COM.': 'mediafire.com', 'a.b.c.example.org': 'example.org',
    'www.bbc.co.uk': 'bbc.co.uk', 'bbc.co.uk': 'bbc.co.uk', 'cdn.shop.com.au': 'shop.com.au', 'x.y.co.jp': 'y.co.jp', 'localhost': 'localhost', 'intranet': 'intranet', '127.0.0.1': '127.0.0.1', '[::1]': '[::1]', 'a.mf.localhost': 'mf.localhost' };
  for (const [host, site] of Object.entries(cases)) assert.equal(S.siteOf(host), site, host);
  for (const bad of ['', null, undefined, 'a b.com', 'x/y.com', 'ex_ample.com', 'a'.repeat(300) + '.com']) assert.equal(S.siteOf(bad), '', String(bad));
  assert.equal(S.siteOf('notmediafire.com'), 'notmediafire.com', 'a longer name does not match');
});

/* ---- size to the app ---- */
test('bridge.sizeOf: totalBytes first, then fileSize; only integers from 1 to 2^50', () => {
  assert.equal(Bridge.sizeOf({ totalBytes: 108003328, fileSize: 5 }), 108003328);
  assert.equal(Bridge.sizeOf({ totalBytes: -1, fileSize: 777 }), 777);
  assert.equal(Bridge.sizeOf({ totalBytes: 0, fileSize: 0 }), 0);
  assert.equal(Bridge.sizeOf({ totalBytes: 1.5 }), 0); assert.equal(Bridge.sizeOf({ totalBytes: 2 ** 50 + 2 }), 0); assert.equal(Bridge.sizeOf({ totalBytes: 2 ** 50 }), 2 ** 50);
  assert.equal(Bridge.sizeOf({ totalBytes: '5' }), 5, 'a numeric string is still a number'); assert.equal(Bridge.sizeOf({ totalBytes: 'x' }), 0);
  assert.equal(Bridge.sizeOf({}, { totalBytes: 42 }), 42, 'the record seen while cancelling is used too'); assert.equal(Bridge.sizeOf(undefined, null), 0);
});
test('capture request carries size when the browser knows it, and omits it otherwise', async () => {
  const { b } = setup(); seen(b);
  await b.hooks.download(dl());
  const req = b.nativeRequests[0];
  assert.equal(req.size, 103 * MB); assert.equal(req.fileName, 'neodm-test-file.dat'); assert.equal(req.mime, 'application/octet-stream');
  const second = setup(); const url2 = DL.replace('abcdefgh', 'zzzzzzzz'); seen(second.b, url2);
  await second.b.hooks.download(dl(url2, { totalBytes: -1, fileSize: 0 }));
  assert.equal('size' in second.b.nativeRequests[0], false, 'unknown size: field absent so older hosts see the old shape');
  const third = setup(); seen(third.b, url2);
  await third.b.hooks.download(dl(url2, { totalBytes: 0, fileSize: 4096 }));
  assert.equal(third.b.nativeRequests[0].size, 4096, 'fileSize when totalBytes is unknown');
});

/* ---- hand back ---- */
test('hand back: the browser download restarts exactly once with a neutral text, the site is remembered', async () => {
  const { b, calls } = setup({ nativeReply: req => (req.action === 'browser-sites' ? { ok: true, sites: [] } : { ok: false, error: 'The download was handed back to the browser.', cancelled: false, handBack: true }) });
  seen(b); await b.hooks.download(dl());
  assert.deepEqual(calls.filter(c => c.startsWith('download:')), ['download:' + DL], 'restarted once');
  assert.equal(b.nativeRequests.length, 1, 'one capture request');
  assert.equal(b.local.status, 'NeoDM left this download to the browser; it continues there.');
  assert.doesNotMatch(b.local.status, /restarted|unavailable|error/i, 'not the error text');
  assert.deepEqual(json(b.local.browserHosts), ['mediafire.com']);
  const entry = log(b).at(-1); assert.equal(entry.decision, 'skipped'); assert.equal(entry.code, 'handed-back'); assert.equal(entry.size, 103 * MB);
  assert.equal(entry.reason, 'You chose Download in browser instead');
});
test('a real error still uses the old restart text; a user cancel restarts nothing', async () => {
  const err = setup({ nativeReply: req => (req.action === 'browser-sites' ? { ok: true, sites: [] } : { ok: false, error: 'disk full' }) }); seen(err.b); await err.b.hooks.download(dl());
  assert.match(err.b.local.status, /restarted in the browser/); assert.equal(err.b.local.browserHosts, undefined, 'an error does not remember the site');
  assert.equal(err.calls.filter(c => c.startsWith('download:')).length, 1);
  const cancel = setup({ nativeReply: req => (req.action === 'browser-sites' ? { ok: true, sites: [] } : { ok: false, error: 'cancelled', cancelled: true }) }); seen(cancel.b); await cancel.b.hooks.download(dl());
  assert.equal(cancel.calls.filter(c => c.startsWith('download:')).length, 0); assert.equal(cancel.b.local.browserHosts, undefined);
});
test('hand back when the restart itself fails: the error is reported, nothing loops', async () => {
  const { b, calls } = setup({ nativeReply: req => (req.action === 'browser-sites' ? { ok: true, sites: [] } : { ok: false, error: 'x', handBack: true }) });
  b.chrome.downloads.download = async () => { calls.push('download'); throw new Error('no'); };
  seen(b); await b.hooks.download(dl());
  assert.equal(calls.filter(c => c === 'download').length, 1); assert.match(b.local.status, /could not|cannot|Could not|Cannot/i);
});

/* ---- remembered sites ---- */
test('a remembered site is left to the browser: no cancel, no native capture request, log reason browser-site', async () => {
  const { b, calls } = setup({ settings: { captureEnabled: true, browserHosts: ['mediafire.com'] } });
  seen(b); await b.hooks.download(dl());
  assert.deepEqual(calls, [], 'the browser download is untouched'); assert.equal(b.nativeRequests.length, 0); assert.equal(b.siteRequests.length, 0, 'not even a question to the host');
  const entry = log(b).at(-1); assert.equal(entry.decision, 'skipped'); assert.equal(entry.code, 'browser-site'); assert.match(entry.reason, /Left to the browser/);
  const other = 'https://files.example.org/a/b.dat'; seen(b, other); await b.hooks.download(dl(other, { id: 12, referrer: 'https://example.org/' }));
  assert.equal(b.nativeRequests.length, 1, 'other sites are still captured');
});
test('the list matches by registrable parent for the final address and the original address', async () => {
  const { b, calls } = setup({ settings: { captureEnabled: true, browserHosts: ['mediafire.com'] } });
  const redirected = dl('https://cdn.other.example/x.dat', { finalUrl: 'https://download1.mediafire.com/t/x.dat', url: 'https://cdn.other.example/x.dat' });
  b.hooks.request({ requestId: 'q1', url: redirected.url, method: 'GET' }); b.hooks.redirect({ requestId: 'q1', url: redirected.url, statusCode: 302 }); b.hooks.request({ requestId: 'q1', url: redirected.finalUrl, method: 'GET' });
  await b.hooks.download(redirected); assert.deepEqual(calls, []); assert.equal(log(b).at(-1).code, 'browser-site');
  const notSame = setup({ settings: { captureEnabled: true, browserHosts: ['mediafire.com'] } });
  const lookalike = 'https://notmediafire.com/a.dat'; seen(notSame.b, lookalike); await notSame.b.hooks.download(dl(lookalike));
  assert.equal(notSame.b.nativeRequests.length, 1, 'notmediafire.com is another site');
});
test('the app reports a site that came back as a web page: the next capture of it is left to the browser', async () => {
  const { b, calls } = setup({ nativeReply: req => (req.action === 'browser-sites' ? { ok: true, sites: ['download1336.mediafire.com', 'bad host!', 5] } : { ok: true, id: 'x' }) });
  seen(b); await b.hooks.download(dl());
  assert.deepEqual(json(b.local.browserHosts), ['mediafire.com'], 'normalised to the registrable parent, junk dropped');
  assert.deepEqual(calls, [], 'the browser download is untouched'); assert.equal(b.nativeRequests.length, 0, 'no capture request was sent');
  assert.deepEqual(b.siteRequests, [{ action: 'browser-sites' }]);
  assert.equal(log(b).at(-1).code, 'browser-site');
});
test('an unanswered or failing site question never blocks a capture (older host)', async () => {
  for (const reply of [req => (req.action === 'browser-sites' ? new Error('host gone') : { ok: true, id: 'x' }), req => (req.action === 'browser-sites' ? { ok: false, error: 'Bridge unavailable or request rejected; browser download remains available' } : { ok: true, id: 'x' }), req => (req.action === 'browser-sites' ? { ok: true } : { ok: true, id: 'x' })]) {
    const { b } = setup({ nativeReply: reply }); seen(b); await b.hooks.download(dl());
    assert.equal(b.nativeRequests.length, 1, 'captured as before'); assert.equal(b.local.browserHosts, undefined);
  }
});
test('the list keeps at most 100 sites (newest kept), hostnames only', async () => {
  const sites = Array.from({ length: 130 }, (_, i) => `site${i}.example.org`);
  const { b } = setup({ nativeReply: req => (req.action === 'browser-sites' ? { ok: true, sites } : { ok: true, id: 'x' }) });
  seen(b, 'https://other.example/z.dat'); await b.hooks.download(dl('https://other.example/z.dat'));
  const stored = json(b.local.browserHosts);
  assert.equal(stored.length, 1, 'all share one registrable parent, so they collapse into one entry'); assert.ok(stored.every(h => /^[a-z0-9.\-]+$/.test(h) && !h.includes('/')));
  assert.equal(stored.at(-1), 'example.org');
  const many = Array.from({ length: 130 }, (_, i) => `x.site${i}.org`);
  const second = setup({ nativeReply: req => (req.action === 'browser-sites' ? { ok: true, sites: many } : { ok: true, id: 'x' }) });
  seen(second.b, 'https://other.example/z.dat'); await second.b.hooks.download(dl('https://other.example/z.dat'));
  const kept = json(second.b.local.browserHosts); assert.equal(kept.length, 100); assert.equal(kept.at(-1), 'site99.org', 'at most 100 entries are taken from one answer');
  for (let i = 0; i < 105; i++) await second.b.ctx.addBrowserHosts(`d.extra${i}.net`);
  const capped = json(second.b.local.browserHosts); assert.equal(capped.length, 100); assert.equal(capped.at(-1), 'extra104.net', 'newest kept'); assert.equal(capped[0], 'extra5.net', 'oldest dropped');
});

/* ---- double start of one download ---- */
test('the same download started twice while the first is being handled: one native request, the second is dropped silently', async () => {
  let release; const gate = new Promise(r => { release = r; });
  const { b, calls } = setup({ wait: () => gate });
  seen(b); const first = b.hooks.download(dl()); await tick(12);
  assert.equal(b.nativeRequests.length, 1, 'the first request is waiting for the user');
  seen(b); await b.hooks.download(dl(DL, { id: 12 }));
  assert.equal(b.nativeRequests.length, 1, 'no second dialog');
  assert.deepEqual(calls.filter(c => c.endsWith(':12')), ['cancel:12', 'erase:12'], 'the second browser download is cancelled and erased');
  assert.equal(calls.some(c => c.startsWith('download:')), false, 'and nothing is restarted in the browser');
  assert.equal(log(b).at(-1).code, 'duplicate');
  release(); await first;
  assert.equal(b.nativeRequests.length, 1);
  seen(b); await b.hooks.download(dl(DL, { id: 13 })); assert.equal(b.nativeRequests.length, 1); assert.equal(log(b).at(-1).code, 'duplicate');
  b.advance(6000); seen(b); await b.hooks.download(dl(DL, { id: 14 })); assert.equal(b.nativeRequests.length, 2, 'after the window a new start is captured again');
});
test('the duplicate check uses the address: another file in parallel is not a duplicate', async () => {
  let release; const gate = new Promise(r => { release = r; });
  const { b } = setup({ wait: () => gate });
  seen(b); const first = b.hooks.download(dl()); await tick(12);
  const other = 'https://download1336.mediafire.com/' + 'b'.repeat(200) + '/zzzz/other.dat'; seen(b, other);
  const second = b.hooks.download(dl(other, { id: 12 })); await tick(12);
  assert.equal(b.nativeRequests.length, 2, 'two different files: two dialogs');
  release(); await first; await second;
});

/* ---- the browser's Save as window ---- */
test('filename question: held until the capture decision; a captured download is cancelled while the browser still waits, so no Save as window opens', async () => {
  const { b, calls } = setup(); let answers = 0; const order = [];
  b.chrome.downloads.cancel = async id => { order.push('cancel'); calls.push('cancel:' + id); };
  const answer = () => { answers++; order.push('suggest'); };
  seen(b);
  assert.equal(b.hooks.determining({ id: 11, filename: 'neodm-test-file.dat', mime: 'application/octet-stream' }, answer), true, 'the answer is promised for later');
  await b.hooks.download(dl());
  assert.equal(answers, 1, 'answered exactly once'); assert.ok(order.indexOf('cancel') < order.indexOf('suggest'), 'cancelled first, then the browser is released: ' + order.join(','));
  assert.equal(b.timers.some(t => t.ms === 8000), true, 'a safety timer exists');
});
test('filename question: a download that is not captured is released at once with no change; a late question is not held', async () => {
  const { b } = setup({ settings: { captureEnabled: false } }); let answers = 0; seen(b);
  b.hooks.determining({ id: 11, filename: 'a.dat' }, () => answers++);
  await b.hooks.download(dl()); assert.equal(answers, 1, 'released after the (skip) decision');
  assert.equal(b.hooks.determining({ id: 11, filename: 'a.dat' }, () => answers++), undefined, 'decision already made: not held'); assert.equal(answers, 1);
  assert.equal(b.hooks.determining({ id: 50, filename: 'a.dat' }, undefined), undefined, 'no suggest callback: nothing to hold');
});
test('filename question: the safety timer answers if no decision ever comes', async () => {
  const { b } = setup(); let answers = 0;
  b.hooks.determining({ id: 77, filename: 'a.dat' }, () => answers++);
  const timer = b.timers.find(t => t.ms === 8000); assert.ok(timer); timer.fn(); timer.fn(); assert.equal(answers, 1, 'once, even if the timer fires again');
});
test('the extension\'s own restart is not captured again (Chrome reports no byExtensionId at creation)', async () => {
  const { b, calls } = setup({ nativeReply: req => (req.action === 'browser-sites' ? { ok: true, sites: [] } : { ok: false, error: 'x' }) });
  seen(b); await b.hooks.download(dl());
  assert.equal(calls.filter(c => c.startsWith('download:')).length, 1);
  const before = b.nativeRequests.length; seen(b); await b.hooks.download(dl(DL, { id: 12 }));
  assert.equal(b.nativeRequests.length, before, 'the restarted download is left to the browser'); assert.equal(calls.filter(c => c === 'cancel:12').length, 0);
});

/* ---- popup ---- */
function popup({ local = {}, session = {}, tabUrl = 'https://www.mediafire.com/file/abc/neodm-test-file.dat/file', onMessage = () => ({ ok: true }) } = {}) {
  const dom = new JSDOM(source('popup.html'), { runScripts: 'outside-only', url: 'chrome-extension://ext-id/popup.html' }), w = dom.window, sent = [];
  const store = { ...local };
  w.chrome = {
    storage: { local: { get: async k => { const o = {}; for (const key of [].concat(k)) if (key in store) o[key] = store[key]; return o; }, set: async o => Object.assign(store, o) }, session: { get: async k => (typeof k === 'string' && k in session ? { [k]: session[k] } : {}), set: async () => {} }, onChanged: { addListener() {} } },
    tabs: { query: async () => [{ id: 7, url: tabUrl, title: 'T' }] },
    runtime: { sendMessage: async m => { sent.push(m); if (m.action === 'popup-media') return { items: [] }; if (m.action === 'site-remove') { const site = S.siteOf(m.host); store.browserHosts = (store.browserHosts || []).filter(h => h !== site); } return onMessage(m); }, getManifest: () => ({ version: manifest.version }) }, permissions: {},
  };
  withI18n(w); w.eval(source('sniff.js')); w.eval(source('popup.js'));
  return { w, d: w.document, sent, store };
}
test('popup: sites left to the browser are in a collapsed section with a Remove button each', async () => {
  const p = popup({ local: { browserHosts: ['mediafire.com', 'example.co.uk'] } }); await tick(20);
  const section = p.d.getElementById('sitesPanel'); assert.equal(section.tagName, 'DETAILS'); assert.equal(section.open, false, 'collapsed');
  assert.equal(section.querySelector('summary').textContent, 'Sites left to the browser');
  const rows = [...p.d.querySelectorAll('#sites li')]; assert.deepEqual(rows.map(r => r.querySelector('.host').textContent), ['mediafire.com', 'example.co.uk']);
  assert.ok(p.sent.some(m => m.action === 'sites-sync'), 'the popup first takes what the app reported');
  rows[1].querySelector('button').click(); await tick(20);
  assert.deepEqual(p.store.browserHosts, ['mediafire.com']); assert.ok(p.sent.some(m => m.action === 'site-remove' && m.host === 'example.co.uk'));
  assert.deepEqual([...p.d.querySelectorAll('#sites li .host')].map(x => x.textContent), ['mediafire.com']);
  assert.match(p.d.getElementById('status').textContent, /example\.co\.uk is captured by NeoDM again/);
});
test('popup: empty list text; "Capture this site again" only for a listed current site', async () => {
  const none = popup(); await tick(20);
  assert.equal(none.d.getElementById('sitesNone').hidden, false); assert.equal(none.d.getElementById('captureAgain').hidden, true);
  const listed = popup({ local: { browserHosts: ['mediafire.com'] } }); await tick(20);
  const again = listed.d.getElementById('captureAgain'); assert.equal(again.hidden, false); assert.equal(again.textContent, 'Capture this site again');
  again.click(); await tick(20);
  assert.deepEqual(listed.store.browserHosts, []); assert.equal(again.hidden, true); assert.match(listed.d.getElementById('status').textContent, /mediafire\.com is captured by NeoDM again/);
  const elsewhere = popup({ local: { browserHosts: ['mediafire.com'] }, tabUrl: 'https://other.example/' }); await tick(20);
  assert.equal(elsewhere.d.getElementById('captureAgain').hidden, true, 'another site: no button');
});
test('popup: recent downloads show the browser-reported size', async () => {
  const entries = [{ time: Date.now(), host: 'download1336.mediafire.com', decision: 'skipped', code: 'handed-back', size: 103 * MB, reason: 'You chose Download in browser instead' }, { time: Date.now(), host: 'files.example', decision: 'captured', code: 'sent', reason: 'x' }, { time: Date.now(), host: 'big.example', decision: 'captured', code: 'sent', size: 1610612736, reason: 'y' }, { time: Date.now(), host: 'bad.example', decision: 'captured', code: 'sent', size: 'huge', reason: 'z' }];
  const p = popup({ session: { 'neodm.log': entries } }); await tick(20);
  const lines = [...p.d.querySelectorAll('#recent li')].map(li => li.textContent);
  assert.ok(lines.some(l => l.includes('download1336.mediafire.com · 103 MB · ')), lines.join('\n'));
  assert.ok(lines.some(l => l.includes('big.example · 1.5 GB · ')), lines.join('\n'));
  assert.ok(lines.some(l => l.includes('files.example · ') && !/\d MB|GB|KB/.test(l.split('files.example')[1])), 'no size when unknown');
  assert.ok(lines.some(l => l.includes('bad.example') && !/huge/.test(l)), 'a non-number is ignored');
});

/* ---- wording ---- */
test('new texts exist in all ten extension languages with their placeholders', () => {
  const keys = ['reason_browser_site', 'reason_handed_back', 'reason_duplicate', 'status_handed_back', 'popup_sites_heading', 'popup_sites_none', 'popup_sites_hint', 'popup_site_remove', 'popup_site_remove_for', 'popup_site_captured_again', 'popup_capture_again'];
  for (const lang of fs.readdirSync(path.join(root, 'extension/_locales'))) {
    const m = JSON.parse(fs.readFileSync(path.join(root, 'extension/_locales', lang, 'messages.json'), 'utf8'));
    for (const k of keys) assert.ok(m[k]?.message?.length > 1, `${lang}.${k}`);
    for (const k of ['popup_site_remove_for', 'popup_site_captured_again']) { assert.ok(m[k].message.includes('$HOST$'), `${lang}.${k} placeholder`); assert.equal(m[k].placeholders.host.content, '$1'); }
  }
  const en = JSON.parse(fs.readFileSync(path.join(root, 'extension/_locales/en/messages.json'), 'utf8'));
  for (const k of keys) assert.doesNotMatch(en[k].message, /\u2014/, 'no em dash in ' + k);
});
test('docs describe the design (browser-sites, handBack, size)', () => {
  const doc = fs.readFileSync(path.join(root, 'docs/CHROME.md'), 'utf8');
  for (const word of ['browser-sites', 'handBack', 'browserHosts', 'wrong-content', 'size', 'Save as']) assert.ok(doc.includes(word), word);
});
