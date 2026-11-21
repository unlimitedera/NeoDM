// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
'use strict';
// 2.0.0 real-Chrome smoke for "Refresh link": throwaway profile, the real extension (manual build and the real store ZIP), a local fixture site whose page offers a freshly signed
// link for a file, and a second page that shows a sign-in form until a cookie is set. The native port is mocked inside the worker (it plays the app: it lists the refresh the
// app waits on and records what the extension reports); no real profile, registry or app is touched.
const fs = require('node:fs'), path = require('node:path'), assert = require('node:assert/strict'), zlib = require('node:zlib'), http = require('node:http'), { spawn } = require('node:child_process');
const root = path.resolve(__dirname, '..');
const exe = process.env.NEODM_BROWSER || 'C:/Program Files/Google/Chrome/Application/chrome.exe';
const scratchRoot = process.env.NEODM_TEST_SCRATCH || path.join(process.env.LOCALAPPDATA, 'hermes', 'cache', 'scratch');
fs.mkdirSync(scratchRoot, { recursive: true });
const { buildStoreZip } = require('../tools/build-store-zip.cjs');
const sleep = ms => new Promise(r => setTimeout(r, ms));
let checks = 0;
function check(name, ok) { assert.ok(ok, name); checks++; console.log('PASS ' + name); }
function unpackZip(zip, directory) {
  fs.mkdirSync(directory, { recursive: true }); let count = 0;
  for (let p = 0; zip.readUInt32LE(p) === 0x04034b50;) {
    const method = zip.readUInt16LE(p + 8), size = zip.readUInt32LE(p + 18), nl = zip.readUInt16LE(p + 26), el = zip.readUInt16LE(p + 28);
    const name = zip.toString('utf8', p + 30, p + 30 + nl), start = p + 30 + nl + el, bytes = zip.subarray(start, start + size), out = path.resolve(directory, name);
    assert.ok(out.startsWith(path.resolve(directory) + path.sep) && (method === 0 || method === 8), 'safe ZIP entry');
    fs.mkdirSync(path.dirname(out), { recursive: true }); fs.writeFileSync(out, method === 8 ? zlib.inflateRawSync(bytes) : bytes); p = start + size; count++;
  }
  assert.ok(count > 0);
}
function copyDir(from, to) { fs.mkdirSync(to, { recursive: true }); for (const e of fs.readdirSync(from, { withFileTypes: true })) e.isDirectory() ? copyDir(path.join(from, e.name), path.join(to, e.name)) : fs.copyFileSync(path.join(from, e.name), path.join(to, e.name)); }

function copyDir(from, to) { fs.mkdirSync(to, { recursive: true }); for (const e of fs.readdirSync(from, { withFileTypes: true })) e.isDirectory() ? copyDir(path.join(from, e.name), path.join(to, e.name)) : fs.copyFileSync(path.join(from, e.name), path.join(to, e.name)); }

/** The site: /page has a link with a signature that changes on every request; /gated shows a password box until the cookie "ok=1" is set; /nothing has no link. */
function startSite() {
  let counter = 0;
  const server = http.createServer((req, res) => {
    const url = new URL(req.url, 'http://127.0.0.1');
    const cookie = req.headers.cookie || '';
    const html = body => { res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' }); res.end('<!doctype html><meta charset=utf-8><title>fixture</title>' + body); };
    if (url.pathname === '/page') return html(`<h1>Model</h1><a id="dl" href="/files/model-00002-of-00005.gguf?sig=fresh${++counter}">Download part 2</a><a href="/files/other.zip?sig=x">Other</a>`);
    if (url.pathname === '/gated') return /(^|; )ok=1/.test(cookie) ? html(`<h1>Welcome</h1><a href="/files/model-00002-of-00005.gguf?sig=signed${++counter}">Download part 2</a>`) : html('<h1>Sign in</h1><form><input type="password" name="p"><button>Go</button></form>');
    if (url.pathname === '/nothing') return html('<h1>Nothing here</h1><a href="/files/unrelated.bin?sig=1">unrelated</a>');
    res.writeHead(403); res.end();
  });
  return new Promise(resolve => server.listen(0, '127.0.0.1', () => resolve({ port: server.address().port, stop: () => new Promise(r => server.close(r)) })));
}

async function run(label, extensionDir, site) {
  const scratch = fs.mkdtempSync(path.join(scratchRoot, 'neodm-refresh200-'));
  const child = spawn(exe, [`--user-data-dir=${scratch}`, '--remote-debugging-pipe', '--enable-unsafe-extension-debugging', '--headless=new', '--no-first-run', '--no-default-browser-check', '--disable-background-networking', '--disable-component-update', '--window-size=1000,800', 'about:blank'], { stdio: ['ignore', 'ignore', 'ignore', 'pipe', 'pipe'] });
  let next = 1, buffer = ''; const pending = new Map();
  child.stdio[4].on('data', data => {
    buffer += data.toString(); let end;
    while ((end = buffer.indexOf('\0')) >= 0) {
      const m = JSON.parse(buffer.slice(0, end)); buffer = buffer.slice(end + 1);
      if (m.id && pending.has(m.id)) { const p = pending.get(m.id); pending.delete(m.id); clearTimeout(p.timer); m.error ? p.reject(new Error(m.error.message)) : p.resolve(m.result); }
    }
  });
  const call = (method, params = {}, sessionId) => new Promise((resolve, reject) => { const id = next++, timer = setTimeout(() => { pending.delete(id); reject(new Error('CDP timeout: ' + method)); }, 20000); pending.set(id, { resolve, reject, timer }); child.stdio[3].write(JSON.stringify({ id, method, params, ...(sessionId ? { sessionId } : {}) }) + '\0'); });
  const attach = async id => (await call('Target.attachToTarget', { targetId: id, flatten: true })).sessionId;
  const evaluate = async (session, expression) => { const r = await call('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true, userGesture: true }, session); if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description || r.exceptionDetails.text); return r.result.value; };
  const wait = async (fn, what = 'condition') => { for (let i = 0; i < 100; i++) { const x = await fn(); if (x) return x; await sleep(100); } throw new Error('timed out: ' + what); };
  try {
    const id = (await call('Extensions.loadUnpacked', { path: extensionDir })).id;
    const sw = await wait(async () => (await call('Target.getTargets')).targetInfos.find(t => t.type === 'service_worker' && t.url === `chrome-extension://${id}/background.js`), 'service worker');
    const worker = await attach(sw.targetId);
    await wait(() => evaluate(worker, 'typeof chrome !== "undefined" && !!chrome.runtime && !!chrome.storage').catch(() => false), 'worker ready');
    const base = `http://127.0.0.1:${site.port}`;
    const NONCE = 'a1b2c3d4e5f60718293a4b5c6d7e8f90';
    const OLD = `${base}/files/model-00002-of-00005.gguf?sig=old`;
    // the mocked port plays the app: it lists the refresh in progress, takes the reports and answers yes
    await evaluate(worker, `(() => { globalThis.__sent = []; globalThis.__nonce = ${JSON.stringify(NONCE)}; globalThis.__page = ''; globalThis.__old = ${JSON.stringify(OLD)};
      chrome.runtime.connectNative = () => { const l = []; return { onMessage: { addListener: f => l.push(f) }, onDisconnect: { addListener() {} }, disconnect() {}, postMessage: req => { if (req.action !== 'ping' && req.action !== 'browser-sites') __sent.push(req);
        const reply = req.action === 'ping' ? { ok: true } : req.action === 'browser-sites' ? { ok: true, sites: [] } : req.action === 'refresh-watch' ? { ok: true, watch: [{ nonce: __nonce, page: __page, old: __old, until: Date.now() + 300000 }] } : req.action === 'refresh-report' ? { ok: true, act: true } : { ok: true, id: 'smoke-1' };
        queueMicrotask(() => l.forEach(f => f(reply))); } }; }; })()`);
    const sent = () => evaluate(worker, 'JSON.stringify(__sent)').then(JSON.parse);
    const reports = async () => (await sent()).filter(r => r.action === 'refresh-report');
    const targets = async () => (await call('Target.getTargets')).targetInfos.filter(t => t.type === 'page');
    const openRefresh = async pagePath => {
      await evaluate(worker, `__page = ${JSON.stringify(base + pagePath)}; __sent.length = 0;`);
      const t = (await call('Target.createTarget', { url: base + pagePath + '#neodm-refresh=' + NONCE })).targetId;
      return t;
    };

    // 1. the page offers a freshly signed link of the same file: it is reported with the page, the identity and the cookie of the browser
    await call('Network.enable', {}, await attach((await targets())[0].targetId)).catch(() => {});
    const t1 = await openRefresh('/page');
    const found = await wait(async () => (await reports()).find(r => r.state === 'found'), 'found report');
    const all = await reports();
    check(`${label}: the extension first tells the app it saw the page`, all[0].state === 'contact' && all[0].nonce === NONCE);
    check(`${label}: the fresh link of the SAME file is reported (not the other file)`, /\/files\/model-00002-of-00005\.gguf\?sig=fresh\d+$/.test(found.url) && found.url.startsWith(base));
    check(`${label}: the page address and the browser's identity travel with it`, found.referer === base + '/page' && /Chrome\//.test(found.userAgent || ''));
    await wait(async () => !(await targets()).some(t => t.targetId === t1), 'the page tab is closed after the app took the link');
    check(`${label}: the tab opened for the refresh closes by itself`, true);

    // 2. a page that is not on the app's list is never read
    await evaluate(worker, '__sent.length = 0');
    const plain = (await call('Target.createTarget', { url: base + '/page#neodm-refresh=ffffffffffffffffffffffffffffffff' })).targetId;
    await sleep(2500);
    check(`${label}: a tab with a code the app did not list is left alone (asked once, nothing reported)`, (await reports()).length === 0);
    await call('Target.closeTarget', { targetId: plain });

    // 3. a sign-in page: reported once; after the person signs in (cookie plus a reload) the link is found
    await evaluate(worker, '__sent.length = 0');
    const t3 = await openRefresh('/gated');
    await wait(async () => (await reports()).some(r => r.state === 'signin'), 'sign-in report');
    await sleep(2500);
    check(`${label}: a sign-in page is reported once`, (await reports()).filter(r => r.state === 'signin').length === 1 && !(await reports()).some(r => r.state === 'found'));
    const session3 = await attach(t3);
    await call('Network.setCookie', { name: 'ok', value: '1', url: base }, session3);
    await call('Page.navigate', { url: base + '/gated' }, session3);
    const signed = await wait(async () => (await reports()).find(r => r.state === 'found'), 'found after sign-in');
    check(`${label}: after signing in the fresh link is found`, /sig=signed\d+$/.test(signed.url) && signed.cookie === 'ok=1');

    // 4. a request built in the worker carries the page it started from
    await evaluate(worker, '__sent.length = 0');
    await evaluate(worker, `send({ url: ${JSON.stringify(base + '/files/x.bin')}, fileName: 'x.bin', referer: '', method: 'GET' }, { id: 1, url: ${JSON.stringify(base + '/page#top')} })`).catch(() => {});
    const download = (await sent()).find(r => r.url && r.url.endsWith('/files/x.bin'));
    check(`${label}: a download request carries the page it started from, without the fragment`, !!download && download.pageUrl === base + '/page');
  } finally {
    try { await call('Browser.close'); } catch { child.kill(); }
    await sleep(500); try { fs.rmSync(scratch, { recursive: true, force: true }); } catch {}
  }
}

(async () => {
  const work = fs.mkdtempSync(path.join(scratchRoot, 'neodm-refresh200-ext-'));
  const site = await startSite();
  try {
    const manual = path.join(work, 'manual'); copyDir(path.join(root, 'extension'), manual);
    const store = path.join(work, 'store'); unpackZip(buildStoreZip().zip, store);
    await run('manual build', manual, site);
    await run('store ZIP', store, site);
    console.log(`ALL ${checks} CHROMIUM REFRESH LINK CHECKS PASSED`);
  } finally { await site.stop(); try { fs.rmSync(work, { recursive: true, force: true }); } catch {} }
})().catch(e => { console.error(e.stack); process.exitCode = 1; });
