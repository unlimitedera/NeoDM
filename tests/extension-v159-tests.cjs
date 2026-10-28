'use strict';
/* 1.5.9: the cookies option is on by default and `cookies` is a required permission: manifest, migration, the chrome.cookies fallback, privacy, popup, locales, documents. */
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { JSDOM } = require('jsdom');
const { makeBackground, tick, source, withI18n } = require('./extension-harness.cjs');
const root = path.join(__dirname, '..');
const read = p => fs.readFileSync(path.join(root, p), 'utf8');
const SECRET = 'sid=TOP-SECRET-COOKIE-77';
const tab = { id: 7, url: 'https://video.example/watch?v=1', title: 'Clip' };
const jar = [{ name: 'sid', value: 'TOP-SECRET-COOKIE-77' }, { name: 'theme', value: 'dark' }];

/** A media item on a CDN in the tab list, sent from the popup, the ribbon/button or the context menu: there was no download request with a captured header. */
async function mediaSetup({ settings = { captureEnabled: true }, cookieJar = jar } = {}) {
  const b = makeBackground({ settings, cookieJar, openTabs: [tab] });
  b.chrome.permissions.request = async () => { throw new Error('permissions.request must not be called'); };
  b.chrome.permissions.remove = async () => { throw new Error('permissions.remove must not be called'); };
  b.chrome.permissions.contains = async () => { throw new Error('permissions.contains must not be called'); };
  await tick(10);
  await b.message({ action: 'media-dom', sources: [{ url: 'https://cdn.example/clip.mp4', tag: 'video' }] }, b.pageSender(tab)); await tick();
  const list = await b.message({ action: 'media-list' }, b.pageSender(tab));
  return { b, id: list.items[0].id };
}
const sentCookie = b => b.nativeRequests.filter(r => r.url).map(r => r.cookie);

test('manifest: cookies is a required permission, not an optional one; version and changelog agree with the props', () => {
  const m = JSON.parse(read('extension/manifest.json'));
  assert.ok(m.permissions.includes('cookies')); assert.equal(m.optional_permissions, undefined);
  assert.deepEqual([...m.permissions].sort(), ['contextMenus', 'cookies', 'downloads', 'nativeMessaging', 'storage', 'tabs', 'webRequest']);
  assert.deepEqual(m.host_permissions, ['http://*/*', 'https://*/*']);
  const version = /<Version>([^<]+)<\/Version>/.exec(read('Directory.Build.props'))[1];
  assert.equal(m.version, version); const [vMajor, vMinor, vPatch] = version.split('.').map(Number); assert.ok(vMajor > 1 || vMinor > 5 || (vMinor === 5 && vPatch >= 9));
  const log = read('CHANGELOG.md'); assert.match(log, /^## \[1\.5\.9\]/m);
  const entry = log.slice(log.indexOf('## [1.5.9]'), log.indexOf('## [1.5.8]'));
  assert.match(entry, /required permission/i); assert.match(entry, /approv/i); assert.doesNotMatch(entry, /[\u2014\u2013]/);
  assert.match(read('README.md'), /^\| 1\.5\.9 \| /m);
});

test('migration: new installs and existing users get the option on once; a later explicit off is kept; other choices stay', async () => {
  for (const [start, expected] of [[{}, true], [{ schema: 3, captureEnabled: true }, true], [{ schema: 3, cookiesEnabled: false }, true], [{ schema: 3, cookiesEnabled: true }, true], [{ schema: 2 }, true]]) {
    const { b } = await mediaSetup({ settings: start }); await tick(10);
    assert.equal(b.local.cookiesEnabled, true, JSON.stringify(start)); assert.equal(b.local.schema, 4);
  }
  const off = await mediaSetup({ settings: { schema: 4, captureEnabled: true, cookiesEnabled: false } }); await tick(10);
  assert.equal(off.b.local.cookiesEnabled, false, 'an explicit off after the migration is kept');
  const keep = await mediaSetup({ settings: { schema: 3, sessionCookies: false, captureEnabled: false } }); await tick(10);
  assert.equal(keep.b.local.sessionCookies, false); assert.equal(keep.b.local.captureEnabled, false); assert.equal(keep.b.local.cookiesEnabled, true);
  const unset = await mediaSetup({ settings: { schema: 4 } }); await tick(10);
  assert.equal(unset.b.local.cookiesEnabled, undefined, 'after the migration an unset value is not rewritten, and the default (on) applies');
});

test('media paths (popup list, ribbon and button, context menu) pass the cookies the browser would send for the exact URL', async () => {
  const { b, id } = await mediaSetup();
  await b.message({ action: 'popup-download', tab: { id: 7, url: tab.url, title: tab.title }, id }); await tick(10);
  await b.message({ action: 'media-download', id }, b.pageSender(tab)); await tick(10);
  assert.deepEqual(sentCookie(b), ['sid=TOP-SECRET-COOKIE-77; theme=dark', 'sid=TOP-SECRET-COOKIE-77; theme=dark']);
  assert.ok(b.cookieQueries.length >= 2 && b.cookieQueries.every(q => q.url === 'https://cdn.example/clip.mp4'), 'asked for the exact request URL: ' + JSON.stringify(b.cookieQueries));
  assert.equal(b.nativeRequests.filter(r => r.url)[0].url, 'https://cdn.example/clip.mp4');
});

test('on by default without any stored setting (before the migration has run), off when switched off', async () => {
  const fresh = makeBackground({ cookieJar: jar, openTabs: [tab] }); // no ensureDefaults wait on purpose
  await fresh.ctx.send({ url: 'https://cdn.example/a.mp4' }, tab);
  assert.equal(fresh.nativeRequests.filter(r => r.url)[0].cookie, 'sid=TOP-SECRET-COOKIE-77; theme=dark');
  const { b, id } = await mediaSetup({ settings: { schema: 4, captureEnabled: true, cookiesEnabled: false } });
  await b.message({ action: 'popup-download', tab: { id: 7, url: tab.url, title: tab.title }, id }); await tick(10);
  assert.deepEqual(sentCookie(b), ['']); assert.equal(b.cookieQueries.length, 0, 'switched off: the cookie store is not read at all');
});

test('the captured per-hop header stays the primary source; chrome.cookies is only the fallback', async () => {
  const b = makeBackground({ settings: { captureEnabled: true }, cookieJar: jar });
  b.chrome.downloads.cancel = async () => {}; b.chrome.downloads.erase = async () => {}; b.chrome.downloads.removeFile = async () => {}; b.chrome.downloads.search = async () => []; b.chrome.downloads.download = async () => 9;
  const url = 'https://download.example/file/x.dat', page = 'https://www.example/page';
  b.hooks.request({ requestId: 'h1', url, method: 'GET', documentUrl: page, tabId: 7 });
  b.hooks.sent({ requestId: 'h1', url, requestHeaders: [{ name: 'Cookie', value: 'hop=1' }, { name: 'Referer', value: page }] });
  await b.hooks.download({ id: 5, url, finalUrl: url, state: 'in_progress', referrer: page, totalBytes: 6 * 1024 * 1024, mime: 'application/octet-stream', filename: 'x.dat' });
  assert.deepEqual(sentCookie(b), ['hop=1']); assert.equal(b.cookieQueries.length, 0);
});

test('caps: 8192 characters pass, 8193 and control characters are dropped, a throwing cookie store gives no cookie', async () => {
  const mk = async value => { const { b, id } = await mediaSetup({ cookieJar: [{ name: 'a', value }] }); await b.message({ action: 'popup-download', tab: { id: 7, url: tab.url, title: tab.title }, id }); await tick(10); return sentCookie(b)[0]; };
  assert.equal((await mk('x'.repeat(8192 - 2))).length, 8192);
  assert.equal(await mk('x'.repeat(8193 - 1)), '');
  for (const bad of ['v\r\nHost: evil', 'v\u0000', 'v\u007f']) assert.equal(await mk(bad), '', JSON.stringify(bad));
  const { b, id } = await mediaSetup(); b.chrome.cookies.getAll = async () => { throw new Error('store unavailable'); };
  await b.message({ action: 'popup-download', tab: { id: 7, url: tab.url, title: tab.title }, id }); await tick(10);
  assert.deepEqual(sentCookie(b), [''], 'the download still goes, without a cookie');
});

test('privacy: the cookies live in memory only, never in storage, the capture log, the badge or the tab messages', async () => {
  const { b, id } = await mediaSetup();
  await b.message({ action: 'popup-download', tab: { id: 7, url: tab.url, title: tab.title }, id }); await tick(10);
  assert.equal(sentCookie(b)[0], 'sid=TOP-SECRET-COOKIE-77; theme=dark');
  for (const [name, data] of [['local', b.local], ['session', b.sessionData], ['badge', b.badge], ['tab messages', b.sentToTabs], ['status', b.local.status]]) assert.ok(!JSON.stringify(data ?? '').includes('TOP-SECRET'), name);
  const code = read('extension/background.js') + read('extension/popup.js');
  assert.ok(!/storage\.(local|session)\.set\([^)]*cookie\b/i.test(code.replace(/cookiesEnabled|sessionCookies/g, '')), 'no code path stores a cookie');
  assert.doesNotMatch(read('extension/background.js'), /permissions\.(request|remove|contains)/, 'the worker never asks for, drops or checks the permission');
  assert.doesNotMatch(read('extension/popup.js'), /permissions\./, 'the popup never touches permissions');
});

async function popupPage({ local = {}, lang = 'en' } = {}) {
  const dom = new JSDOM(source('popup.html'), { runScripts: 'outside-only', url: 'chrome-extension://ext-id/popup.html' }), w = dom.window, saved = [], perms = [];
  w.chrome = { storage: { local: { get: async k => Object.fromEntries([].concat(k).filter(x => x in local).map(x => [x, local[x]])), set: async o => { Object.assign(local, o); saved.push(o); } }, session: { get: async () => ({}) } }, tabs: { query: async () => [{ id: 7, url: 'https://video.example/', title: 'V' }] }, runtime: { sendMessage: async m => (m.action === 'popup-media' ? { items: [], summary: { videos: 0, frames: 0, downloadable: 0, reasons: [] } } : { ok: true }), getManifest: () => ({ version: '1.5.9' }) }, permissions: { request: async () => { perms.push('request'); return false; }, remove: async () => { perms.push('remove'); return true; }, contains: async () => { perms.push('contains'); return true; } } };
  withI18n(w, lang); w.eval(source('sniff.js')); w.eval(source('popup.js')); await tick(16);
  return { w, doc: w.document, local, saved, perms };
}

test('popup: the cookies option is checked by default, shows the stored off, and toggling only stores the choice (no permission calls)', async () => {
  const def = await popupPage();
  assert.equal(def.doc.getElementById('cookies').checked, true); assert.equal(def.doc.getElementById('sessionCookies').checked, true);
  const off = await popupPage({ local: { cookiesEnabled: false } }); assert.equal(off.doc.getElementById('cookies').checked, false);
  const box = def.doc.getElementById('cookies');
  box.checked = false; box.dispatchEvent(new def.w.Event('change')); await tick(10);
  assert.equal(def.local.cookiesEnabled, false); assert.deepEqual(def.perms, [], 'turning it off removes nothing');
  box.checked = true; box.dispatchEvent(new def.w.Event('change')); await tick(10);
  assert.equal(def.local.cookiesEnabled, true); assert.deepEqual(def.perms, [], 'turning it on asks for nothing');
});

test('popup text and locales: the hint exists, says it is on by default and can be turned off, no longer says optional or asks for a permission; all ten locales', () => {
  const html = read('extension/popup.html'); assert.match(html, /id="cookies"/); assert.match(html, /data-i18n="popup_cookies_hint"/);
  const dirs = fs.readdirSync(path.join(root, 'extension/_locales')); assert.equal(dirs.length, 10);
  const en = JSON.parse(read('extension/_locales/en/messages.json'));
  assert.match(en.popup_cookies_hint.message, /on by default/i); assert.match(en.popup_cookies_hint.message, /turn this off/i); assert.doesNotMatch(en.popup_cookies_hint.message, /optional|permission|allow|grant/i);
  for (const d of dirs) {
    const m = JSON.parse(read(`extension/_locales/${d}/messages.json`));
    assert.ok(m.popup_cookies_hint && m.popup_cookies_hint.message.length > 40, d);
    if (d !== 'en') assert.notEqual(m.popup_cookies_hint.message, en.popup_cookies_hint.message, d);
    assert.doesNotMatch(m.popup_cookies_hint.message, /[\u2014\u2013]/, d);
  }
});

test('popup renders the hint in English and Arabic (right-to-left)', async () => {
  const en = await popupPage(); assert.match(en.doc.querySelector('[data-i18n=popup_cookies_hint]').textContent, /On by default/);
  const ar = await popupPage({ lang: 'ar' }); assert.equal(ar.doc.documentElement.dir, 'rtl'); assert.ok(ar.doc.querySelector('[data-i18n=popup_cookies_hint]').textContent.length > 40);
});

test('documents: PRIVACY, SECURITY, CHROME, README and the changelog describe the required permission and the one-time approval prompt; the store listing no longer says optional', () => {
  assert.match(read('PRIVACY.md'), /on by default since 1\.5\.9/); assert.doesNotMatch(read('PRIVACY.md'), /the browser asks for the cookies permission/);
  assert.match(read('SECURITY.md'), /required permission/); assert.match(read('docs/CHROME.md'), /REQUIRED permission/); assert.doesNotMatch(read('docs/CHROME.md'), /the optional `cookies` permission and the popup option/);
  assert.match(read('README.md'), /approve/i);
  assert.match(read('CHANGELOG.md').slice(read('CHANGELOG.md').indexOf('## [1.5.9]'), read('CHANGELOG.md').indexOf('## [1.5.8]')), /disable[sd]? an extension update|turn off an extension update|turn(s)? off an extension update/i);
  const listing = 'F:/github release/chrome-web-store/STORE-LISTING.md';
  if (fs.existsSync(listing)) {
    const text = fs.readFileSync(listing, 'utf8');
    assert.doesNotMatch(text, /Optional `cookies`/); assert.match(text, /\| `cookies` \| Reads the cookies the browser would send for a download URL and hands them to the local NeoDM app so downloads from sites that need your session work; they stay on your PC; can be turned off in the popup\. \|/);
  }
});
