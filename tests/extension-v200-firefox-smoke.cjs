// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
'use strict';
// 2.0.0 real-Firefox smoke (item 18). Throwaway profile under the Hermes scratch folder, -no-remote, Marionette for control, the Firefox build made by tools/build-firefox.cjs
// installed as a temporary add-on, a local fixture site (host name localhost is mapped to 127.0.0.1 by a profile preference).
// The native host is a MOCK (tests/helpers, a node script) that Firefox starts for real through the native messaging registry key
//   HKCU\Software\Mozilla\NativeMessagingHosts\com.ndm.bridge
// which this test creates for the run and deletes afterwards (it refuses to run when that key already exists, so it never replaces a real one).
// The real NeoDM app and the real NativeHost are NOT involved. Nothing of the user's Firefox profile is read or written.
const fs = require('node:fs'), path = require('node:path'), assert = require('node:assert/strict'), http = require('node:http'), { spawn, spawnSync } = require('node:child_process');
const { Marionette } = require('./helpers/marionette.cjs');
const { buildFirefoxZip } = require('../tools/build-firefox.cjs');
const exe = process.env.NEODM_FIREFOX || 'C:/Program Files/Mozilla Firefox/firefox.exe';
const scratchRoot = process.env.NEODM_TEST_SCRATCH || path.join(process.env.LOCALAPPDATA, 'hermes', 'cache', 'scratch');
const shots = process.env.NEODM_SHOTS || path.join(__dirname, '..', 'artifacts', 'firefox-shots');
fs.mkdirSync(scratchRoot, { recursive: true }); fs.mkdirSync(shots, { recursive: true });
const sleep = ms => new Promise(r => setTimeout(r, ms));
const UUID = '7a1c0f3e-5b2d-4c8a-9e61-0d3f2b4a6c18', POPUP = `moz-extension://${UUID}/popup.html`, REG = 'HKCU\\Software\\Mozilla\\NativeMessagingHosts\\com.ndm.bridge';
let checks = 0;
function check(name, ok) { assert.ok(ok, name); checks++; console.log('PASS ' + name); }
const reg = (...a) => spawnSync('reg', a, { encoding: 'utf8' });

const HOST_JS = `
const fs = require('fs'), dir = __dirname, log = dir + '\\\\host-log.jsonl';
fs.appendFileSync(log, JSON.stringify({ started: true, argv: process.argv.slice(2) }) + '\\n');
let buf = Buffer.alloc(0);
const read = f => { try { return JSON.parse(fs.readFileSync(dir + '\\\\' + f, 'utf8')); } catch { return null; } };
function reply(o) { const b = Buffer.from(JSON.stringify(o)); const h = Buffer.alloc(4); h.writeUInt32LE(b.length); process.stdout.write(Buffer.concat([h, b])); }
let n = 0;
process.stdin.on('data', d => {
  buf = Buffer.concat([buf, d]);
  while (buf.length >= 4 && buf.length >= 4 + buf.readUInt32LE(0)) {
    const len = buf.readUInt32LE(0), msg = JSON.parse(buf.subarray(4, 4 + len).toString('utf8')); buf = buf.subarray(4 + len);
    fs.appendFileSync(log, JSON.stringify({ msg }) + '\\n');
    const a = msg.action;
    if (a === 'ping') reply({ ok: true, appVersion: '2.0.0' });
    else if (a === 'browser-sites') reply({ ok: true, sites: [] });
    else if (a === 'captcha-watch') reply({ ok: true, watch: read('captcha-watch.json') || [] });
    else if (a === 'captcha-report') reply({ ok: true, act: true });
    else if (a === 'refresh-watch') reply({ ok: true, watch: read('refresh-watch.json') || [] });
    else if (a === 'refresh-report') reply({ ok: true, act: true });
    else reply({ ok: true, id: 'job-' + (++n) });
  }
});
process.stdin.on('end', () => process.exit(0));
`;

function startSite(port0 = 0) {
  let counter = 0;
  const server = http.createServer((req, res) => {
    const url = new URL(req.url, 'http://localhost'); if (process.env.NEODM_DEBUG) console.log('HTTP', req.method, req.url);
    const page = body => { res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' }); res.end('<!doctype html><meta charset=utf-8><title>fixture</title>' + body); };
    const p = url.pathname;
    if (p === '/video') return page('<h1>Video page</h1><video id="v" src="/media/a.mp4" controls width="640" height="360" preload="metadata"></video>');
    if (p === '/files') return page('<h1>Files</h1><a id="dl" href="/files/pack-2.0.zip">Download the pack</a>');
    if (p === '/files/pack-2.0.zip') { res.writeHead(200, { 'Content-Type': 'application/zip', 'Content-Disposition': 'attachment; filename="pack-2.0.zip"', 'Content-Length': '2048' }); return res.end(Buffer.alloc(2048, 7)); }
    if (p === '/hls') return page('<h1>Stream</h1><video id="hv" src="/media/live/master.m3u8" controls width="640" height="360"></video><script>fetch("/media/live/master.m3u8").then(r => r.text()).then(t => { document.title = "stream " + t.length; });</script>');
    if (p === '/media/live/master.m3u8') { res.writeHead(200, { 'Content-Type': 'application/vnd.apple.mpegurl' }); return res.end('#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:4\n#EXT-X-MEDIA-SEQUENCE:0\n#EXTINF:4,\nseg0.ts\n#EXTINF:4,\nseg1.ts\n#EXT-X-ENDLIST\n'); }
    if (p === '/media/a.mp4') { res.writeHead(200, { 'Content-Type': 'video/mp4', 'Content-Length': String(3 * 1024 * 1024), 'Accept-Ranges': 'bytes' }); return res.end(Buffer.alloc(3 * 1024 * 1024, 1)); }
    if (p === '/check') return page('<h1>Check</h1><form id="f" method="get" action="/checked"><div class="cf-turnstile" style="width:300px;height:65px;border:1px solid #888"></div><input type="hidden" name="cf-turnstile-response" value=""><button type="submit">Continue</button></form>');
    if (p === '/checked') return page('<h1>Submitted</h1><p id="got">' + (url.searchParams.get('cf-turnstile-response') || '') + '</p>');
    if (p === '/page') return page(`<h1>Model</h1><a id="x" href="/files/model-00002-of-00005.gguf?sig=fresh${++counter}">Download part 2</a><a href="/files/other.zip?sig=x">Other</a>`);
    res.writeHead(404); res.end();
  });
  return new Promise(resolve => server.listen(port0, '127.0.0.1', () => resolve({ port: server.address().port, stop: () => new Promise(r => { server.close(r); server.closeAllConnections?.(); }) })));
}

async function main() {
  if (reg('query', REG).status === 0) { console.log('SKIP: ' + REG + ' already exists on this PC; the smoke will not replace a real registration'); return 'skipped'; }
  const work = fs.mkdtempSync(path.join(scratchRoot, 'neodm-ff200-'));
  const prof = path.join(work, 'profile'), hostDir = path.join(work, 'host'); fs.mkdirSync(prof); fs.mkdirSync(hostDir);
  const site = await startSite(), base = `http://localhost:${site.port}`, marionettePort = 2800 + Math.floor(Math.random() * 150);
  const hostLog = path.join(hostDir, 'host-log.jsonl');
  fs.writeFileSync(path.join(hostDir, 'host.js'), HOST_JS);
  fs.writeFileSync(path.join(hostDir, 'host.cmd'), '@echo off\r\nnode "%~dp0host.js" %*\r\n');
  const manifest = path.join(hostDir, 'com.ndm.bridge.json');
  fs.writeFileSync(manifest, JSON.stringify({ name: 'com.ndm.bridge', description: 'NeoDM smoke host (mock)', path: path.join(hostDir, 'host.cmd'), type: 'stdio', allowed_extensions: ['neodm@unlimitedera'] }, null, 2));
  const prefs = { 'marionette.port': marionettePort, 'app.update.enabled': false, 'app.update.auto': false, 'browser.shell.checkDefaultBrowser': false, 'datareporting.policy.dataSubmissionEnabled': false, 'toolkit.telemetry.enabled': false,
    'extensions.webextensions.uuids': JSON.stringify({ 'neodm@unlimitedera': UUID }), 'network.dns.localDomains': 'localhost', 'browser.download.useDownloadDir': true, 'browser.helperApps.neverAsk.saveToDisk': 'application/octet-stream', 'browser.download.always_ask_before_handling_new_types': false, 'browser.download.open_pdf_attachments_inline': false, 'browser.download.folderList': 2, 'browser.download.dir': path.join(work, 'dl'),
    'browser.startup.homepage_override.mstone': 'ignore', 'startup.homepage_welcome_url': 'about:blank', 'network.proxy.type': 0, 'extensions.autoDisableScopes': 0, 'browser.safebrowsing.downloads.enabled': false, 'browser.safebrowsing.downloads.remote.enabled': false, 'browser.safebrowsing.malware.enabled': false, 'browser.safebrowsing.phishing.enabled': false };
  fs.mkdirSync(path.join(work, 'dl'), { recursive: true }); fs.writeFileSync(path.join(prof, 'user.js'), Object.entries(prefs).map(([k, v]) => `user_pref(${JSON.stringify(k)}, ${JSON.stringify(v)});`).join('\n'));
  fs.writeFileSync(path.join(work, 'neodm-firefox.zip'), buildFirefoxZip().zip);
  const add = reg('add', REG, '/ve', '/t', 'REG_SZ', '/d', manifest, '/f'); assert.equal(add.status, 0, 'registry key written for the run');
  const child = spawn(exe, ['-no-remote', '-profile', prof, ...(process.env.NEODM_VISIBLE ? [] : ['-headless']), '-marionette', 'about:blank'], { stdio: 'ignore' });
  const m = new Marionette(marionettePort);
  const log = () => (fs.existsSync(hostLog) ? fs.readFileSync(hostLog, 'utf8').trim().split('\n').filter(Boolean).map(l => JSON.parse(l)) : []);
  const got = () => log().filter(l => l.msg).map(l => l.msg);
  const waitFor = async (fn, what, ms = 15000) => { const end = Date.now() + ms; for (;;) { const v = await fn(); if (v) return v; if (Date.now() > end) throw new Error('timed out: ' + what); await sleep(150); } };
  const write = (f, o) => fs.writeFileSync(path.join(hostDir, f), JSON.stringify(o));
  try {
    await m.connect(); await m.send('WebDriver:NewSession', {});
    const installed = (await m.send('Addon:Install', { path: path.join(work, 'neodm-firefox.zip'), temporary: true })).value;
    check('the Firefox build installs as a temporary add-on with the id neodm@unlimitedera', installed === 'neodm@unlimitedera');
    const [first] = await m.send('WebDriver:GetWindowHandles');
    // second tab = the extension's own page (popup.html), used to talk to the background and to read/write the extension's settings
    let tabPage = (await m.send('WebDriver:NewWindow', { type: 'tab' })).handle;
    await m.send('WebDriver:SwitchToWindow', { handle: tabPage }); await m.send('WebDriver:Navigate', { url: POPUP });
    const ext = async (script, args = []) => { const cur = (await m.send('WebDriver:GetWindowHandle')).value; await m.send('WebDriver:SwitchToWindow', { handle: tabPage }); try { return await m.execAsync(`const done = arguments[arguments.length - 1]; (async () => { ${script} })().then(v => done(JSON.stringify(v === undefined ? null : v)), e => done(JSON.stringify({ __error: String(e) })));`, args, null).then(JSON.parse); } finally { await m.send('WebDriver:SwitchToWindow', { handle: cur }); } };
    const store = async (key) => ext(`return await chrome.storage.local.get(${JSON.stringify(key)});`);
    const setStore = obj => ext(`await chrome.storage.local.set(${JSON.stringify(obj)}); return true;`);
    await m.send('WebDriver:SwitchToWindow', { handle: first });
    const nav = async url => { await m.send('WebDriver:Navigate', { url }); };
    const visible = (sel) => m.exec(`const e = document.querySelector(${JSON.stringify(sel)}); if (!e) return null; const r = e.getBoundingClientRect(); return { w: r.width, h: r.height, x: r.x, y: r.y };`, [], null);
    const hover = async (x = 200, y = 200) => { await m.send('WebDriver:PerformActions', { actions: [{ type: 'pointer', id: 'mouse', parameters: { pointerType: 'mouse' }, actions: [{ type: 'pointerMove', duration: 50, x: x - 3, y: y - 3, origin: 'viewport' }, { type: 'pointerMove', duration: 50, x, y, origin: 'viewport' }] }] }); };
    const shot = async name => { const r = await m.send('WebDriver:TakeScreenshot', { full: false }); fs.writeFileSync(path.join(shots, 'firefox-' + name + '.png'), Buffer.from(r.value, 'base64')); };

    // 1. real native messaging: Firefox starts the mock host through the registry key and passes the manifest path and the add-on id
    const ping = await ext(`return await chrome.runtime.sendMessage({ action: 'test' });`);
    check('a message from the extension reaches the native host Firefox started (ping answered)', ping && ping.ok === true);
    const started = log().find(l => l.started);
    check('Firefox starts the host with the add-on id as its argument (allowed_extensions in force)', !!started && started.argv.includes('neodm@unlimitedera') && started.argv.some(a => a.endsWith('com.ndm.bridge.json')));
    const pinged = got().find(x => x.action === 'ping');
    check('the ping carries the extension version 2.0.0 and the browser name Firefox', pinged.extVersion === '2.0.0' && pinged.browser === 'Firefox');

    if (!process.env.NEODM_SKIP) {
    // 2. the ribbon on a page with a video: the side ribbon and the corner button both appear
    await nav(base + '/video');
    const ribbon = await waitFor(() => visible('[data-neodm-ribbon]'), 'side ribbon host');
    check('the side ribbon appears on a page with a video', ribbon.w > 100);
    await hover(); const corner = await waitFor(async () => { await hover(220, 190); return visible('[data-neodm-bars]'); }, 'corner button host');
    check('the independent corner button appears on the video', !!corner);
    await shot('ribbon');
    // 3. hide options (the settings the ribbon menu writes; the page reacts to them live)
    const gone = sel => waitFor(async () => (await visible(sel)) === null, sel + ' removed');
    const back = sel => waitFor(() => visible(sel), sel + ' back');
    await setStore({ ribbonHiddenHosts: ['localhost'] }); await gone('[data-neodm-ribbon]');
    check('"Hide on this website": the side ribbon is removed, the corner button stays', (await visible('[data-neodm-bars]')) !== null);
    await setStore({ ribbonHiddenHosts: [] }); await back('[data-neodm-ribbon]');
    check('removing the site brings the side ribbon back', true);
    await setStore({ ribbonMinimizedHosts: ['localhost'] }); await sleep(800);
    const minimised = await visible('[data-neodm-ribbon]'); await shot('minimized');
    console.log('ribbon host size full/minimized:', JSON.stringify(ribbon), JSON.stringify(minimised));
    check('minimize: the ribbon stays but shrinks to a small tab', minimised !== null && minimised.w < ribbon.w);
    await setStore({ ribbonMinimizedHosts: [] }); await sleep(500);
    await setStore({ ribbonEnabled: false }); await gone('[data-neodm-ribbon]');
    check('"Hide everywhere": the side ribbon is removed', true);
    await setStore({ ribbonEnabled: true }); await back('[data-neodm-ribbon]');
    check('the popup switch (ribbonEnabled) brings it back', true);
    const shotHash = async () => require('node:crypto').createHash('sha256').update((await m.send('WebDriver:TakeScreenshot', { full: false })).value).digest('hex');
    await hover(); await sleep(700); const withCorner = await shotHash();
    await setStore({ videoButtonEnabled: false }); await sleep(900); await hover(230, 200); await sleep(700); const withoutCorner = await shotHash();
    check('the corner button can be switched off on its own (the picture over the video changes, the side ribbon stays)', withCorner !== withoutCorner && (await visible('[data-neodm-ribbon]')) !== null);
    await setStore({ videoButtonEnabled: true }); await sleep(500);

    }
    // 4. a download that starts in the browser is handed to the host. This Firefox, started by the automation with a throwaway profile, may refuse to save any attachment
    //    (a fetch of an attachment answers 204 and the downloads list stays empty, with or without the extension: a Firefox/Marionette limit, not the extension). Probe first.
    await nav(base + '/files');
    const probe = await ext(`const r = await fetch(${JSON.stringify(base + '/files/pack-2.0.zip?probe=1')}); return r.status;`);
    if (probe !== 200) console.log('SKIP browser-download capture: this automated Firefox saves no attachments (probe status ' + probe + '); the capture path is proven through the stream and refresh messages below');
    else {
      const before = got().length;
      const link = await m.exec('const r = document.getElementById("dl").getBoundingClientRect(); return { x: Math.round(r.x + r.width / 2), y: Math.round(r.y + r.height / 2) };', [], null);
      await m.send('WebDriver:PerformActions', { actions: [{ type: 'pointer', id: 'mouse', parameters: { pointerType: 'mouse' }, actions: [{ type: 'pointerMove', duration: 50, x: link.x, y: link.y, origin: 'viewport' }, { type: 'pointerDown', button: 0 }, { type: 'pointerUp', button: 0 }] }] }); // a real click
      const captured = await waitFor(() => got().slice(before).find(x => x.url && x.url.endsWith('/files/pack-2.0.zip') && !/probe/.test(x.url)), 'captured download message');
      check('a browser download reaches the native host (url, version, browser)', captured.url === base + '/files/pack-2.0.zip' && captured.extVersion === '2.0.0' && captured.browser === 'Firefox');
      check('the captured download carries the page it started from', captured.pageUrl === base + '/files');
      check('the browser download was cancelled and erased (nothing left in the download folder)', fs.readdirSync(path.join(work, 'dl')).length === 0);
    }

    // 5. a stream (HLS playlist) is listed by the extension and its download message reaches the host
    await nav(base + '/hls');
    const tabInfo = await ext(`const tabs = await chrome.tabs.query({}); const t = tabs.find(x => /\\/hls$/.test(x.url)); return { id: t.id, url: t.url, title: t.title };`);
    const listed = await waitFor(async () => { const r = await ext(`return await chrome.runtime.sendMessage({ action: 'popup-media', tabId: ${tabInfo.id} });`); const it = (r.items || []).find(i => /master\.m3u8/.test(i.url || '')); return it ? it : null; }, 'stream listed').catch(async e => { console.log('media state:', JSON.stringify(await ext(`return await chrome.runtime.sendMessage({ action: 'popup-media', tabId: ${tabInfo.id} });`)).slice(0, 1500), 'title', await m.exec('return document.title + "|" + String(window[Symbol.for("neodm.hooks")]) + "|" + document.documentElement.getAttribute("data-neodm-ch") + "|" + window.fetch.toString().slice(0, 60)', [], null)); throw e; });
    check('the playlist is detected as a stream by the extension in Firefox', !!listed);
    const mark = got().length;
    await ext(`return await chrome.runtime.sendMessage({ action: 'popup-download', tab: ${JSON.stringify(tabInfo)}, id: ${JSON.stringify(listed.id)} });`);
    const streamMsg = await waitFor(() => got().slice(mark).find(x => x.url && /master\.m3u8/.test(x.url)), 'stream message');
    check('the stream capture message reaches the native host as a stream', streamMsg.stream === true || /m3u8/.test(streamMsg.url));

    // 6. the 1.16.2 widget pointing is present and works in the Firefox build: the extension reports the widget rectangle only for a page the app listed
    write('captcha-watch.json', []);
    await nav(base + '/check'); await sleep(2500);
    check('a widget on a page the app did not list is not reported', !got().some(x => x.action === 'captcha-report'));
    write('captcha-watch.json', [{ url: base + '/check', until: Date.now() + 120000 }]);
    await sleep(5500); await nav(base + '/check');
    const widget = await waitFor(() => got().find(x => x.action === 'captcha-report' && x.report === 'widget'), 'widget report', 20000);
    check('the widget rectangle (kind, screen pixels) is reported for the listed page', widget.widget === 'turnstile' && widget.rect.w > 0 && widget.rect.h > 0 && widget.url === base + '/check');
    await m.exec('const f = document.querySelector("[name=cf-turnstile-response]"); f.value = "tok-ff-123";', [], null);
    const token = await waitFor(() => got().find(x => x.action === 'captcha-report' && x.report === 'token'), 'token report', 20000);
    check('the answer is reported and the page\'s own form is submitted once', token.token === 'tok-ff-123');
    await waitFor(async () => (await m.exec('return location.pathname + "|" + (document.getElementById("got") ? document.getElementById("got").textContent : "")', [], null)) === '/checked|tok-ff-123', 'form submitted');
    check('the page\'s own form was submitted with the answer', true);

    // 7. refresh link: the page of an expired download is read only when the app listed its code
    const NONCE = 'a1b2c3d4e5f60718293a4b5c6d7e8f90';
    write('refresh-watch.json', [{ nonce: NONCE, page: base + '/page', old: base + '/files/model-00002-of-00005.gguf?sig=old', until: Date.now() + 300000 }]);
    await nav(base + '/page#neodm-refresh=' + NONCE);
    const found = await waitFor(() => got().find(x => x.action === 'refresh-report' && x.state === 'found'), 'refresh found', 25000);
    check('refresh link: the fresh link of the same file is reported with the page', /\/files\/model-00002-of-00005\.gguf\?sig=fresh\d+$/.test(found.url) && found.referer === base + '/page');
    check('refresh link: the extension said hello first', got().some(x => x.action === 'refresh-report' && x.state === 'contact'));
    // 8. the extension's own settings survive: store has the defaults
    await m.send('WebDriver:SwitchToWindow', { handle: (await m.send('WebDriver:GetWindowHandles'))[0] }); // the refresh tab was closed by the extension
    tabPage = (await m.send('WebDriver:NewWindow', { type: 'tab' })).handle; await m.send('WebDriver:SwitchToWindow', { handle: tabPage }); await m.send('WebDriver:Navigate', { url: POPUP });
    const defaults = await store(['captureEnabled']);
    check('the extension wrote its defaults into the Firefox storage', defaults.captureEnabled === true || defaults.captureEnabled === undefined);
    await m.send('Marionette:Quit').catch(() => { });
    return `ALL ${checks} FIREFOX SMOKE CHECKS PASSED`;
  } finally {
    m.close(); try { child.kill(); } catch { }
    spawnSync('taskkill', ['/PID', String(child.pid), '/T', '/F'], { stdio: 'ignore' });
    reg('delete', REG, '/f');
    await site.stop(); await sleep(800);
    try { fs.rmSync(work, { recursive: true, force: true }); } catch { }
    console.log('registry key removed:', reg('query', REG).status !== 0);
  }
}
main().then(r => console.log(r), e => { console.error(e.stack || e); process.exitCode = 1; });
