'use strict';
// 1.11.0 item 13: the X on the media ribbon and on the video button opens a menu (Hide for now / Hide on this website / Hide everywhere); the popup is the way back.
// Every scenario runs twice: against the manual build (extension/) and against the real store ZIP built by tools/build-store-zip.cjs.
const { test, after } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs'), path = require('node:path'), zlib = require('node:zlib');
const { JSDOM } = require('jsdom');
const { tick, json, source, withI18n, trustedDispatch } = require('./extension-harness.cjs');
const { buildStoreZip } = require('../tools/build-store-zip.cjs');
const ROOT = path.join(__dirname, '..');
const MB = 1024 * 1024;
const windows = []; after(() => { for (const w of windows) try { w.close(); } catch {} });

function unpack(zip) {
  let p = 0; const out = {};
  while (zip.readUInt32LE(p) === 0x04034b50) {
    const method = zip.readUInt16LE(p + 8), size = zip.readUInt32LE(p + 18), nl = zip.readUInt16LE(p + 26), el = zip.readUInt16LE(p + 28);
    const name = zip.toString('utf8', p + 30, p + 30 + nl), begin = p + 30 + nl + el, data = zip.subarray(begin, begin + size);
    out[name] = (method === 8 ? zlib.inflateRawSync(data) : data).toString('utf8'); p = begin + size;
  }
  return out;
}
const store = unpack(buildStoreZip().zip);
const builds = [['manual build', n => source(n)], ['store ZIP', n => store[n]]];
const S = require('../extension/sniff.js');
const item = (o = {}) => ({ id: 'https://v.example/a.mp4', url: 'https://v.example/a.mp4', kind: 'video', track: 'muxed', complete: true, size: 12 * MB, quality: '720p', mime: 'video/mp4', source: 'net', ...o });
const net = (url, o = {}) => ({ id: url, url, rawUrl: url, groupKey: url, kind: 'video', track: 'file', complete: true, size: 10 * MB, quality: '', mime: 'video/mp4', source: 'net', ...o });

/** A page with the content script. `local` is the storage; `delay` holds the storage answer until release() to prove nothing is injected before the settings are read. */
function page(read, { url = 'https://feed.example/home', local = {}, delay = false, lang = 'en' } = {}) {
  const dom = new JSDOM('<!doctype html><title>Feed</title><body><div role="article"><video id="v1"></video></div></body>', { url, runScripts: 'outside-only', pretendToBeVisual: true }), w = dom.window; windows.push(w);
  const roots = [], origAttach = w.Element.prototype.attachShadow;
  w.Element.prototype.attachShadow = function (init) { const r = origAttach.call(this, init); roots.push({ host: this, root: r }); return r; };
  let current = []; const sent = [], listeners = [], stored = [], changeListeners = []; let release; const gate = new Promise(r => { release = r; });
  Object.defineProperty(w.document.getElementById('v1'), 'currentSrc', { value: 'blob:' + url + '/uuid', configurable: true });
  w.HTMLCanvasElement.prototype.getContext = () => null;
  w.chrome = {
    runtime: { sendMessage: async m => { sent.push(m); if (m.action === 'media-list') await new Promise(r => setImmediate(r)); return m.action === 'media-list' ? { items: current } : { ok: true, id: 'job' }; }, onMessage: { addListener: f => listeners.push(f) } },
    storage: { local: { get: async () => { if (delay) await gate; return JSON.parse(JSON.stringify(local)); }, set: async o => { stored.push(JSON.parse(JSON.stringify(o))); Object.assign(local, o); } }, onChanged: { addListener: f => changeListeners.push(f) } },
  };
  withI18n(w, lang); w.eval(read('sniff.js')); w.eval(read('content.js'));
  const send = m => { if (m.type === 'neodm-media') current = m.items; listeners.forEach(f => f(m, {}, () => {})); };
  const rect = (el, r) => { el.getBoundingClientRect = () => ({ left: r[0], top: r[1], right: r[0] + r[2], bottom: r[1] + r[3], width: r[2], height: r[3] }); };
  const ribbonHost = () => w.document.querySelector('[data-neodm-ribbon]');
  const ribbonRoot = () => roots.find(r => r.host.hasAttribute('data-neodm-ribbon'))?.root;
  const barsRoot = () => roots.find(r => r.root.querySelector('style')?.textContent.includes('.b{'))?.root;
  const change = c => changeListeners.forEach(f => f(c, 'local'));
  return { w, d: w.document, sent, send, stored, local, roots, release, rect, ribbonHost, ribbonRoot, barsRoot, change };
}

for (const [label, read] of builds) {
  test(`${label}: the ribbon X opens a three item menu (keyboard and pointer), Escape and an outside click close it`, async () => {
    const p = page(read); await tick(); p.send({ type: 'neodm-media', items: [item()] }); await tick();
    const root = p.ribbonRoot(); assert.ok(root, 'the ribbon is shown'); const x = root.querySelector('button.x'), menu = root.querySelector('.menu');
    assert.equal(menu.hidden, true, 'closed at first'); assert.equal(x.getAttribute('aria-haspopup'), 'menu'); assert.equal(x.getAttribute('aria-expanded'), 'false');
    assert.equal(x.getAttribute('aria-label'), 'Hide options');
    x.click(); assert.equal(menu.hidden, false); assert.equal(x.getAttribute('aria-expanded'), 'true');
    assert.deepEqual([...menu.querySelectorAll('button')].map(b => [b.textContent, b.getAttribute('role')]), [['Hide for now', 'menuitem'], ['Hide on this website', 'menuitem'], ['Hide everywhere', 'menuitem']]);
    assert.equal(menu.getAttribute('role'), 'menu');
    // keyboard: a click with detail 0 (Enter or Space) moves focus to the first item; Escape closes and returns focus to the X
    x.click(); assert.equal(menu.hidden, true, 'a second click closes'); x.click(); assert.equal(menu.hidden, false);
    trustedDispatch(root.querySelector('.bar'), new p.w.KeyboardEvent('keydown', { key: 'Escape', bubbles: true, composed: true }));
    assert.equal(menu.hidden, true, 'Escape closes'); assert.equal(x.getAttribute('aria-expanded'), 'false');
    // outside click
    x.click(); assert.equal(menu.hidden, false);
    trustedDispatch(p.d.body, new p.w.MouseEvent('pointerdown', { bubbles: true, composed: true }));
    assert.equal(menu.hidden, true, 'a click outside closes the menu');
    x.click(); trustedDispatch(p.ribbonHost(), new p.w.MouseEvent('pointerdown', { bubbles: true, composed: true })); assert.equal(menu.hidden, false, 'a click inside the ribbon keeps it');
    // a page script cannot drive the menu
    const fake = new p.w.MouseEvent('click', { bubbles: true, cancelable: true }); x.dispatchEvent(fake); assert.equal(menu.hidden, false, 'a synthetic click does nothing');
  });

  test(`${label}: ribbon, Hide for now: gone for this page view, back when something new appears; nothing is stored`, async () => {
    const p = page(read); await tick(); p.send({ type: 'neodm-media', items: [item()] }); await tick();
    const root = p.ribbonRoot(); root.querySelector('button.x').click(); root.querySelectorAll('.menu button')[0].click();
    assert.equal(p.ribbonHost(), null, 'removed'); assert.deepEqual(p.stored, [], 'nothing is stored for a page view');
    p.send({ type: 'neodm-media', items: [item()] }); assert.equal(p.ribbonHost(), null, 'the same list stays hidden');
    p.send({ type: 'neodm-media', items: [item({ id: 'https://v.example/b.mp4', url: 'https://v.example/b.mp4' }), item()] }); assert.ok(p.ribbonHost(), 'something new brings it back');
  });

  test(`${label}: ribbon, Hide on this website: stored by site name, ribbon gone, other sites unaffected, www and subdomains match`, async () => {
    const p = page(read, { url: 'https://www.news.example/story' }); await tick(); p.send({ type: 'neodm-media', items: [item()] }); await tick();
    const root = p.ribbonRoot(); root.querySelector('button.x').click(); root.querySelectorAll('.menu button')[1].click(); await tick();
    assert.deepEqual(p.stored, [{ videoButtonHiddenHosts: ['news.example'] }], 'the site name without www, no path, no scheme');
    assert.equal(p.ribbonHost(), null, 'the ribbon is gone at once');
    p.send({ type: 'neodm-media', items: [item(), item({ id: 'https://v.example/c.mp4', url: 'https://v.example/c.mp4' })] }); assert.equal(p.ribbonHost(), null, 'and stays gone when new media arrives');
    // a fresh page of the same site, a subdomain and a lookalike
    for (const [url, hidden] of [['https://news.example/other', true], ['https://m.news.example/x', true], ['https://WWW.NEWS.EXAMPLE/x', true], ['https://notnews.example/', false], ['https://news.example.evil.test/', false]]) {
      const q = page(read, { url, local: { videoButtonHiddenHosts: ['news.example'] } }); await tick(); q.send({ type: 'neodm-media', items: [item()] }); await tick();
      assert.equal(!q.ribbonHost(), hidden, `${url}: ${hidden ? 'no ribbon' : 'ribbon shown'}`);
    }
  });

  test(`${label}: ribbon, Hide everywhere: both switches go off, and a hidden site costs nothing (no host element, no shadow root)`, async () => {
    const p = page(read); await tick(); p.send({ type: 'neodm-media', items: [item()] }); await tick();
    const root = p.ribbonRoot(); root.querySelector('button.x').click(); root.querySelectorAll('.menu button')[2].click(); await tick();
    assert.deepEqual(p.stored, [{ ribbonEnabled: false, videoButtonEnabled: false }]); assert.equal(p.ribbonHost(), null);
    p.send({ type: 'neodm-media', items: [item()] }); assert.equal(p.ribbonHost(), null);
    // the setting is read before anything is injected
    const off = page(read, { local: { ribbonEnabled: false } }); await tick(); off.send({ type: 'neodm-media', items: [item()] }); await tick();
    assert.equal(off.ribbonHost(), null); assert.equal(off.roots.filter(r => r.host.hasAttribute('data-neodm-ribbon')).length, 0);
    const hiddenSite = page(read, { local: { videoButtonHiddenHosts: ['feed.example'] } }); await tick(); hiddenSite.send({ type: 'neodm-media', items: [item()] }); await tick();
    assert.equal(hiddenSite.roots.length, 0, 'a hidden site builds no shadow root at all');
    assert.equal(hiddenSite.d.documentElement.querySelectorAll('[data-neodm-ribbon],[data-neodm-bars]').length, 0);
    // the way back: the popup switch writes ribbonEnabled and the page follows live
    off.change({ ribbonEnabled: { newValue: true } }); assert.ok(off.ribbonHost(), 'switching it on shows the ribbon without a reload');
    hiddenSite.change({ videoButtonHiddenHosts: { newValue: [] } }); hiddenSite.send({ type: 'neodm-media', items: [item()] }); assert.ok(hiddenSite.ribbonHost(), 'removing the site shows it again');
  });

  test(`${label}: nothing is injected before the settings are read; a hidden site never gets a ribbon even when media arrives first`, async () => {
    const slow = page(read, { delay: true, local: { videoButtonHiddenHosts: ['feed.example'] } });
    slow.send({ type: 'neodm-media', items: [item()] }); await tick();
    assert.equal(slow.ribbonHost(), null, 'media arrived before the settings: still nothing');
    slow.release(); await tick(20);
    assert.equal(slow.ribbonHost(), null, 'the settings say hidden: never injected');
    const ok = page(read, { delay: true }); ok.send({ type: 'neodm-media', items: [item()] }); await tick(); assert.equal(ok.ribbonHost(), null, 'waiting for the settings');
    ok.release(); await tick(20); assert.ok(ok.ribbonHost(), 'shown once the settings allow it');
    const broken = page(read); broken.w.chrome.storage.local.get = () => Promise.reject(new Error('storage unavailable')); await tick();
  });

  test(`${label}: context menu "show the ribbon" still works on a hidden site (the user asked for it)`, async () => {
    const p = page(read, { local: { videoButtonHiddenHosts: ['feed.example'], ribbonEnabled: false } }); await tick();
    p.send({ type: 'neodm-media', items: [item()] }); assert.equal(p.ribbonHost(), null);
    p.send({ type: 'neodm-show-ribbon' }); assert.ok(p.ribbonHost(), 'an explicit request shows it');
  });

  test(`${label}: the on-video button X opens the same menu: now / this website / everywhere`, async () => {
    const run = async choice => {
      const p = page(read, { local: { ribbonEnabled: false } }); await tick();
      const v1 = p.d.getElementById('v1'); p.rect(v1, [100, 50, 640, 360]);
      p.send({ type: 'neodm-media', items: [net('https://cdn.example/dog.mp4', { size: 12 * MB })] }); await tick();
      p.w.dispatchEvent(new p.w.MouseEvent('pointermove', { clientX: 300, clientY: 200 })); p.w.__neodmRibbon.layout();
      const ui = p.barsRoot(); assert.ok(ui, 'the button exists'); const b = ui.querySelector('.b'); assert.ok(b);
      const x = b.querySelector('.x'); assert.equal(x.getAttribute('aria-haspopup'), 'menu'); assert.equal(x.getAttribute('aria-expanded'), 'false');
      x.click(); const items = [...b.querySelectorAll('.panel button')];
      assert.deepEqual(items.map(i => [i.textContent, i.getAttribute('role')]), [['Hide for now', 'menuitem'], ['Hide on this website', 'menuitem'], ['Hide everywhere', 'menuitem']]);
      assert.equal(b.querySelector('.x').getAttribute('aria-expanded'), 'true');
      if (choice === 'escape') { trustedDispatch(b, new p.w.KeyboardEvent('keydown', { key: 'Escape', bubbles: true, composed: true })); assert.equal(b.querySelectorAll('.panel button').length, 0, 'Escape closes the menu'); return p; }
      if (choice === 'outside') { trustedDispatch(p.d.body, new p.w.MouseEvent('pointerdown', { bubbles: true, composed: true })); assert.equal(b.querySelectorAll('.panel button').length, 0, 'an outside click closes the menu'); return p; }
      items[{ now: 0, site: 1, all: 2 }[choice]].click(); await tick(); return p;
    };
    await run('escape'); await run('outside');
    const now = await run('now'); assert.deepEqual(now.stored, []); assert.equal(now.w.__neodmRibbon.bars().find(b => b.mode === 'notice')?.mode, 'notice', 'Hide for now keeps the old behaviour: a notice with Undo');
    const site = await run('site'); assert.deepEqual(site.stored, [{ videoButtonHiddenHosts: ['feed.example'] }]); assert.equal(site.w.__neodmRibbon.bars().length, 0);
    const all = await run('all'); assert.deepEqual(all.stored, [{ ribbonEnabled: false, videoButtonEnabled: false }]); assert.equal(all.w.__neodmRibbon.bars().length, 0);
  });

  test(`${label}: the menu texts exist in every extension locale and the page uses them`, () => {
    const dirs = fs.readdirSync(path.join(ROOT, 'extension', '_locales')); assert.equal(dirs.length, 10);
    for (const d of dirs) {
      const m = JSON.parse(store[`_locales/${d}/messages.json`]);
      for (const k of ['ribbon_menu_label', 'ribbon_menu_now', 'ribbon_menu_site', 'ribbon_menu_all', 'popup_hidden_all', 'popup_hidden_all_done', 'popup_hidden_none']) assert.ok(m[k] && m[k].message.length > 0, `${d} has ${k}`);
      assert.doesNotMatch(['ribbon_menu_label', 'ribbon_menu_now', 'ribbon_menu_site', 'ribbon_menu_all', 'popup_hidden_all'].map(k => m[k].message).join(' '), /\u2014/, 'no em dashes');
      if (d !== 'en') assert.notEqual(m.ribbon_menu_site.message, 'Hide on this website', `${d} is translated`);
    }
  });
}

test('the store ZIP carries the same ribbon, popup and shared code as the manual build; no new permission was added', () => {
  for (const name of ['content.js', 'sniff.js', 'popup.js', 'popup.html', 'i18n.js']) assert.equal(store[name], fs.readFileSync(path.join(ROOT, 'extension', name), 'utf8'), name + ' is identical');
  const manifest = JSON.parse(store['manifest.json']);
  assert.deepEqual(manifest.permissions, ['nativeMessaging', 'downloads', 'contextMenus', 'storage', 'webRequest', 'tabs', 'cookies'], 'the storage permission was already there; nothing was added');
  assert.deepEqual(manifest.host_permissions, ['http://*/*', 'https://*/*']);
});

test('hidden site list rules: normalised, deduplicated, validated, bounded to 500, host names only', () => {
  assert.equal(S.HIDDEN_HOSTS_MAX, 500);
  assert.deepEqual(S.addHiddenHost([], 'www.Example.org'), ['example.org']);
  assert.deepEqual(S.addHiddenHost(['example.org'], 'sub.example.org'), ['example.org'], 'a subdomain collapses to the site');
  assert.deepEqual(S.addHiddenHost(['example.org'], 'www.example.org'), ['example.org'], 'no duplicate');
  assert.deepEqual(S.addHiddenHost(['example.org'], 'video.example.co.uk'), ['example.org', 'example.co.uk'], 'two-level suffixes keep their registrable name');
  for (const bad of ['', null, undefined, 'https://example.org/path', 'example.org/path', 'exa mple.org', 'a_b.example', '<x>.example', 'x'.repeat(300) + '.example', 'user@example.org']) assert.deepEqual(S.addHiddenHost(['keep.example'], bad), ['keep.example'], `refused: ${String(bad).slice(0, 30)}`);
  let list = []; for (let i = 0; i < 520; i++) list = S.addHiddenHost(list, `site${i}.example`);
  assert.equal(list.length, 500); assert.equal(list[0], 'site20.example', 'the oldest entries go first'); assert.equal(list.at(-1), 'site519.example');
  assert.deepEqual(S.cleanHiddenHosts(['a.example', 'a.example', 7, '', 'B.example']), ['a.example']);
  assert.equal(S.isHiddenHost(['news.example'], S.siteOf('www.news.example')), true); assert.equal(S.isHiddenHost(['news.example'], S.siteOf('m.news.example')), true);
  assert.equal(S.isHiddenHost(['news.example'], S.siteOf('notnews.example')), false); assert.equal(S.isHiddenHost(['news.example'], S.siteOf('news.example.evil.test')), false);
});

for (const [label, read] of builds) {
  async function popupPage(local = {}, lang = 'en') {
    const dom = new JSDOM(read('popup.html'), { runScripts: 'outside-only', url: 'chrome-extension://ext-id/popup.html' }), w = dom.window, saved = []; windows.push(w);
    w.chrome = { storage: { local: { get: async k => Object.fromEntries([].concat(k).filter(x => x in local).map(x => [x, local[x]])), set: async o => { Object.assign(local, o); saved.push(o); } }, session: { get: async () => ({}) } }, tabs: { query: async () => [{ id: 7, url: 'https://video.example/', title: 'V' }] }, runtime: { getManifest: () => ({ version: '1.11.0' }), sendMessage: async () => ({ ok: true, items: [], summary: null, sites: [], pause: {} }), onMessage: { addListener() {} } } };
    withI18n(w, lang); w.eval(read('sniff.js')); w.eval(read('popup.js')); await tick(20);
    return { w, doc: w.document, local, saved };
  }
  test(`${label}: popup: the ribbon switch is the way back from Hide everywhere; hidden websites list with Remove and Show on all websites`, async () => {
    const p = await popupPage({ ribbonEnabled: false, videoButtonEnabled: false, videoButtonHiddenHosts: ['a.example', 'b.example', 'c.example'] });
    assert.equal(p.doc.getElementById('ribbon').checked, false); assert.equal(p.doc.getElementById('videoButton').checked, false);
    const names = () => [...p.doc.querySelectorAll('#hiddenSites li .host')].map(x => x.textContent);
    assert.deepEqual(names(), ['a.example', 'b.example', 'c.example']); assert.equal(p.doc.getElementById('hiddenNone').hidden, true);
    const all = p.doc.getElementById('hiddenAll'); assert.equal(all.hidden, false); assert.equal(all.textContent, 'Show on all websites');
    p.doc.getElementById('ribbon').checked = true; p.doc.getElementById('ribbon').dispatchEvent(new p.w.Event('change')); await tick(); assert.equal(p.local.ribbonEnabled, true, 'the switch turns the ribbon back on');
    p.doc.getElementById('videoButton').checked = true; p.doc.getElementById('videoButton').dispatchEvent(new p.w.Event('change')); await tick(); assert.equal(p.local.videoButtonEnabled, true);
    p.doc.querySelectorAll('#hiddenSites li button')[1].click(); await tick(10); assert.deepEqual(json(p.local.videoButtonHiddenHosts), ['a.example', 'c.example']); assert.deepEqual(names(), ['a.example', 'c.example']);
    assert.match(p.doc.querySelector('#hiddenSites li button').getAttribute('aria-label'), /a\.example/, 'each Remove names its site');
    all.click(); await tick(10); assert.deepEqual(json(p.local.videoButtonHiddenHosts), []); assert.deepEqual(names(), []); assert.equal(p.doc.getElementById('hiddenNone').hidden, false); assert.equal(all.hidden, true, 'the button hides when the list is empty');
  });
  test(`${label}: popup: an empty list says how to add a site; Arabic is right to left`, async () => {
    const p = await popupPage({}); assert.equal(p.doc.getElementById('hiddenAll').hidden, true); assert.match(p.doc.getElementById('hiddenNone').textContent, /Hide on this website/);
    const ar = await popupPage({ videoButtonHiddenHosts: ['a.example'] }, 'ar'); assert.equal(ar.doc.documentElement.dir, 'rtl'); assert.notEqual(ar.doc.getElementById('hiddenAll').textContent, 'Show on all websites');
  });
}

test('every click and keydown handler added for the menu starts with the isTrusted guard', () => {
  const text = fs.readFileSync(path.join(ROOT, 'extension', 'content.js'), 'utf8');
  const handlers = [...text.matchAll(/addEventListener\('(click|keydown|pointerdown)', (?:e|ev) => \{(.{0,40})/g)];
  assert.ok(handlers.length >= 8, 'handlers found: ' + handlers.length);
  for (const [, kind, start] of handlers) assert.match(start.trim(), /^if \(!e\.isTrusted\) return;|^if \(!ev\.isTrusted\) return;|^const|^if \(!e\.isTrusted\)/, kind + ' starts with the guard: ' + start);
});

/* ---- item 5, extension side: playlist pages are handed to NeoDM too (the app opens the Playlist dialog) ---- */
const { makeBackground } = require('./extension-harness.cjs');
const LIST = 'PLav47HAVZMjnTFVZL-aImCQIC0uLZtNCz', PLAYLIST = 'https://www.youtube.com/playlist?list=' + LIST;
const manifestVersion = JSON.parse(fs.readFileSync(path.join(ROOT, 'extension', 'manifest.json'), 'utf8')).version;
test('playlist grammar in the extension agrees with the app: real playlists only, one list id, no mixes or account lists', () => {
  assert.equal(S.youtubePlaylist(PLAYLIST), PLAYLIST);
  assert.equal(S.youtubePlaylist('https://m.youtube.com/playlist?list=' + LIST + '&si=abc#x'), PLAYLIST);
  assert.equal(S.youtubePlaylist('https://music.youtube.com/playlist?list=UU' + 'a'.repeat(22)), 'https://www.youtube.com/playlist?list=UU' + 'a'.repeat(22));
  for (const bad of ['https://www.youtube.com/playlist?list=RDabcdefghijklmnop', 'https://www.youtube.com/playlist?list=WL', 'https://www.youtube.com/playlist?list=LL', 'https://www.youtube.com/playlist?list=PLshort',
    'https://www.youtube.com/playlist?list=' + LIST + '&list=' + LIST, 'https://www.youtube.com/playlist?list=' + LIST + '%20', 'https://evil.test/playlist?list=' + LIST, 'https://youtu.be/playlist?list=' + LIST,
    'https://www.youtube.com/watch?v=aqz-KE-bpKQ&list=' + LIST, 'https://user@www.youtube.com/playlist?list=' + LIST, 'ftp://www.youtube.com/playlist?list=' + LIST, '', null, 5]) assert.equal(S.youtubePlaylist(bad), '', String(bad));
  assert.equal(S.youtubeLink(PLAYLIST), PLAYLIST); assert.equal(S.youtubeLink('https://youtu.be/aqz-KE-bpKQ'), 'https://www.youtube.com/watch?v=aqz-KE-bpKQ'); assert.equal(S.youtubeLink('https://example.org/'), '');
  assert.equal(S.youtubePage(PLAYLIST), '', 'a playlist is still not a video page');
});
test('playlist handoff: sent with the playlist flag to a host that knows it, refused with an update message for an older host; video handoff is unchanged', async () => {
  const tab = { id: 7, url: PLAYLIST, title: 'List' }, send = { action: 'youtube-send', tabId: 7, url: PLAYLIST };
  const fresh = makeBackground({ openTabs: [tab], nativeReply: r => r.action === 'ping' ? { ok: true, youtube: true, playlist: true } : { ok: true, id: 'x' } });
  assert.equal((await fresh.message(send)).ok, true);
  assert.deepEqual(fresh.nativeRequests.at(-1), { url: PLAYLIST, youtube: true, playlist: true, extVersion: manifestVersion, keepAlive: true });
  const old = makeBackground({ openTabs: [tab], nativeReply: r => r.action === 'ping' ? { ok: true, youtube: true } : { ok: true, id: 'x' } });
  const refused = await old.message(send); assert.equal(refused.ok, false); assert.match(refused.error, /update/i);
  assert.equal(old.nativeRequests.filter(r => r.youtube).length, 0, 'nothing but the ping reached the older host');
  const video = 'https://www.youtube.com/watch?v=aqz-KE-bpKQ', vt = { id: 8, url: video, title: 'V' };
  const v = makeBackground({ openTabs: [vt], nativeReply: r => r.action === 'ping' ? { ok: true, youtube: true, playlist: true } : { ok: true, id: 'x' } });
  assert.equal((await v.message({ action: 'youtube-send', tabId: 8, url: video })).ok, true);
  assert.deepEqual(v.nativeRequests.at(-1), { url: video, youtube: true, extVersion: manifestVersion, keepAlive: true }, 'a video request has no playlist field');
  const menu = makeBackground({ nativeReply: r => r.action === 'ping' ? { ok: true, youtube: true, playlist: true } : { ok: true, id: 'x' } });
  await menu.hooks.menu({ menuItemId: 'youtube-link', linkUrl: PLAYLIST + '&si=x' }, { id: 9, url: 'https://example.org/' }); await tick(20);
  assert.equal(menu.nativeRequests.at(-1).url, PLAYLIST); assert.equal(menu.nativeRequests.at(-1).playlist, true);
});
test('popup: the YouTube panel shows on a playlist page too', async () => {
  const dom = new JSDOM(source('popup.html'), { runScripts: 'outside-only', url: 'chrome-extension://ext-id/popup.html' }), w = dom.window; windows.push(w);
  w.chrome = { storage: { local: { get: async () => ({}), set: async () => {} }, session: { get: async () => ({}) } }, tabs: { query: async () => [{ id: 7, url: PLAYLIST, title: 'List' }] }, runtime: { getManifest: () => ({ version: '1.11.0' }), sendMessage: async () => ({ ok: true, items: [], summary: null, sites: [], pause: {} }), onMessage: { addListener() {} } } };
  withI18n(w); w.eval(source('sniff.js')); w.eval(source('popup.js')); await tick(20);
  assert.equal(w.document.getElementById('youtubePanel').hidden, false);
});
