'use strict';
// 1.14.0 real-Chrome smoke: throwaway profile, the real extension (manual build and the real store ZIP), a local fixture server with an HLS stream and a separate WebVTT file.
// Proves: the extension notices the subtitle file the player loads on its own (also when the browser serves it from its cache), the ribbon shows the hint with the language
// under "Download stream", the hint has its own close button, a page without subtitles gets no hint, and the stream capture message carries the subtitle file.
// The native port is mocked inside the worker (it records the message the real extension builds); no real profile, registry or app is touched.
// Set NEODM_CAPTURE_OUT to a file path to keep the captured message (used by the real path test).
const fs = require('node:fs'), path = require('node:path'), assert = require('node:assert/strict'), zlib = require('node:zlib'), { spawn } = require('node:child_process');
const root = path.resolve(__dirname, '..');
const exe = process.env.NEODM_BROWSER || 'C:/Program Files/Google/Chrome/Application/chrome.exe';
const scratchRoot = process.env.NEODM_TEST_SCRATCH || path.join(process.env.LOCALAPPDATA, 'hermes', 'cache', 'scratch');
fs.mkdirSync(scratchRoot, { recursive: true });
const shots = process.env.NEODM_SHOTS || path.join(root, 'artifacts', 'subs-screenshots');
fs.mkdirSync(shots, { recursive: true });
const { buildStoreZip } = require('../tools/build-store-zip.cjs');
const fixture = require('./helpers/subs-fixture.cjs');
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

async function run(label, extensionDir, site, keep) {
  const scratch = fs.mkdtempSync(path.join(scratchRoot, 'neodm-subs1140-'));
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
  const press = async (session, p) => { for (const type of ['mousePressed', 'mouseReleased']) await call('Input.dispatchMouseEvent', { type, x: p.x, y: p.y, button: 'left', buttons: type === 'mousePressed' ? 1 : 0, clickCount: 1 }, session); };
  try {
    const id = (await call('Extensions.loadUnpacked', { path: extensionDir })).id;
    const sw = await wait(async () => (await call('Target.getTargets')).targetInfos.find(t => t.type === 'service_worker' && t.url === `chrome-extension://${id}/background.js`), 'service worker');
    const worker = await attach(sw.targetId);
    await wait(() => evaluate(worker, 'typeof chrome !== "undefined" && !!chrome.runtime && !!chrome.storage').catch(() => false), 'worker ready');
    // the native port records what the real extension would send; it answers like an app that accepted the download
    await evaluate(worker, `(() => { globalThis.__sent = []; globalThis.__cache = []; chrome.webRequest.onResponseStarted.addListener(d => { if (/\\.vtt(\\?|$)/.test(d.url)) __cache.push({ url: d.url, fromCache: d.fromCache, tabId: d.tabId }); }, { urls: ['<all_urls>'] });
      chrome.runtime.connectNative = () => { const l = []; return { onMessage: { addListener: f => l.push(f) }, onDisconnect: { addListener() {} }, disconnect() {}, postMessage: req => { if (req.action !== 'ping' && req.action !== 'browser-sites') __sent.push(req); queueMicrotask(() => l.forEach(f => f(req.action === 'ping' ? { ok: true } : req.action === 'browser-sites' ? { ok: true, sites: [] } : { ok: true, id: 'smoke-job' }))); } }; }; return true; })()`);
    const page = (await call('Target.createTarget', { url: 'about:blank' })).targetId, session = await attach(page);
    await call('Page.enable', {}, session); await call('DOM.enable', {}, session);
    const open = async url => { await call('Page.navigate', { url }, session); await wait(() => evaluate(session, `location.href === ${JSON.stringify(url)} && document.readyState === "complete"`), 'page load'); };
    const flat = async () => (await call('DOM.getFlattenedDocument', { depth: -1, pierce: true }, session)).nodes;
    const centre = async backendNodeId => { const box = await call('DOM.getBoxModel', { backendNodeId }, session).catch(() => null); return box && { x: (box.model.content[0] + box.model.content[4]) / 2, y: (box.model.content[1] + box.model.content[5]) / 2, w: box.model.width, h: box.model.height }; };
    const attrOf = n => { const o = {}; const a = n.attributes || []; for (let i = 0; i < a.length; i += 2) o[a[i]] = a[i + 1]; return o; };
    const textUnder = (nodes, node) => { const out = []; const walk = n => { if (n.nodeType === 3) out.push(n.nodeValue); for (const c of nodes.filter(x => x.parentId === n.nodeId)) walk(c); }; walk(node); return out.join(''); };
    const hint = async () => { const nodes = await flat(); const n = nodes.find(x => x.attributes && 'data-neodm-subs' in attrOf(x)); if (!n) return null; const hidden = 'hidden' in attrOf(n); const c = hidden ? null : await centre(n.backendNodeId); return { hidden, box: c, text: textUnder(nodes, n), node: n, nodes }; };
    const ribbonDownload = async () => { const nodes = await flat(); const b = nodes.find(n => n.nodeName === 'BUTTON' && attrOf(n).class === 'dl'); return b ? { node: b, box: await centre(b.backendNodeId) } : null; };
    const shot = async name => { const png = await call('Page.captureScreenshot', { format: 'png' }, session); fs.writeFileSync(path.join(shots, `${label.replace(/\W+/g, '-')}-${name}.png`), Buffer.from(png.data, 'base64')); };
    const seenEvents = () => evaluate(worker, 'JSON.stringify(__cache)').then(JSON.parse);

    // 1. a page whose player never loads a subtitle file: the ribbon is there for the stream and has no hint
    await open(site.pageUrl('no-subs.html'));
    check(`${label}: the stream is listed in the ribbon on a page without subtitles`, !!(await wait(ribbonDownload, 'download stream button')));
    await sleep(800);
    const none = await hint();
    check(`${label}: no subtitle file was loaded, so the hint is not shown`, !none || none.hidden);

    // 2. the page's player loads a subtitle file the playlist does not list
    await open(site.pageUrl('with-subs.html'));
    const shown = await wait(async () => { const h = await hint(); return h && !h.hidden && h.box ? h : null; }, 'subtitle hint');
    check(`${label}: the hint appears with the language: "${shown.text}"`, shown.text.replace(/\u00d7$/, '') === 'Subtitles found: English. For other languages, pick them in the player first.');
    const dl = await ribbonDownload();
    check(`${label}: the hint sits under the stream row inside the ribbon, not over the video`, shown.box.y > dl.box.y && !(await evaluate(session, `document.elementFromPoint(${shown.box.x}, ${shown.box.y}) === document.getElementById("v")`)));
    await shot('hint');

    // 3. a page view served from the browser's cache is detected too
    await open(site.pageUrl('with-subs-again.html'));
    await wait(async () => { const h = await hint(); return h && !h.hidden && h.box ? h : null; }, 'hint after the second page view');
    const events = await seenEvents();
    check(`${label}: the second page view got the subtitle file from the browser cache and the hint still appeared`, events.some(e => e.fromCache === true));

    // 4. capture: the message the real extension builds for the stream carries the subtitle file
    await press(session, (await ribbonDownload()).box);
    const sent = await wait(async () => (await evaluate(worker, 'JSON.stringify(__sent)').then(JSON.parse)).find(r => r.stream), 'stream capture message');
    check(`${label}: the stream capture message carries the separate subtitle file (url, language, format)`, sent.stream === 'hls' && Array.isArray(sent.subtitles) && sent.subtitles.length === 1 && sent.subtitles[0].url === `http://cdn.localhost:${site.port}/subs/en.vtt` && sent.subtitles[0].language === 'en' && sent.subtitles[0].format === 'vtt');
    check(`${label}: the message keeps the stream's own headers (referer, user agent) and stays far under the 64 KiB frame`, (sent.referer || '').startsWith(`http://video.localhost:${site.port}/`) && /Chrome/.test(sent.userAgent || '') && JSON.stringify(sent).length < 65536);
    if (keep) fs.writeFileSync(keep, JSON.stringify(sent));

    // 5. the hint has its own close button and the ribbon stays
    const h5 = await wait(async () => { const h = await hint(); return h && !h.hidden && h.box ? h : null; }, 'hint before closing');
    const closeBtn = h5.nodes.find(n => n.nodeName === 'BUTTON' && n.parentId === h5.node.nodeId);
    await press(session, await centre(closeBtn.backendNodeId));
    check(`${label}: the hint closes with its own button and the ribbon stays`, await wait(async () => { const h = await hint(); return h && h.hidden; }, 'hint hidden') && !!(await ribbonDownload()));

    // 6. nothing was kept in storage
    const stored = await evaluate(worker, 'Promise.all([chrome.storage.local.get(null), chrome.storage.session.get(null)]).then(r => JSON.stringify(r))');
    check(`${label}: no subtitle address is stored in the extension's storage`, !/\.vtt/.test(stored));
    check(`${label}: no new permission: the manifest still asks for the same ones`, JSON.stringify(JSON.parse(fs.readFileSync(path.join(extensionDir, 'manifest.json'), 'utf8')).permissions) === JSON.stringify(['nativeMessaging', 'downloads', 'contextMenus', 'storage', 'webRequest', 'tabs', 'cookies']));
  } finally {
    try { await call('Browser.close'); } catch { child.kill(); }
    await sleep(500); try { fs.rmSync(scratch, { recursive: true, force: true }); } catch {}
  }
}

(async () => {
  const work = fs.mkdtempSync(path.join(scratchRoot, 'neodm-subs1140-ext-'));
  const site = await fixture.start({ dir: path.join(work, 'site') });
  try {
    const manual = path.join(work, 'manual'); copyDir(path.join(root, 'extension'), manual);
    const store = path.join(work, 'store'); unpackZip(buildStoreZip().zip, store);
    await run('manual build', manual, site, process.env.NEODM_CAPTURE_OUT);
    await run('store ZIP', store, site, null);
    console.log(`ALL ${checks} CHROMIUM SUBTITLE CHECKS PASSED`);
    if (process.env.NEODM_KEEP_SITE) copyDir(site.dir, process.env.NEODM_KEEP_SITE); // the stream files the browser played, for the app side of the real path test
    if (process.env.NEODM_HOLD_FIXTURE) {
      fs.writeFileSync(process.env.NEODM_HOLD_FILE || path.join(scratchRoot, 'subs-fixture-port.txt'), site.port + '\n' + site.dir);
      console.log('FIXTURE ' + site.port + ' ' + site.dir); await new Promise(r => setTimeout(r, Number(process.env.NEODM_HOLD_FIXTURE)));
    }
  } finally { await site.stop(); try { fs.rmSync(work, { recursive: true, force: true }); } catch {} }
})().catch(e => { console.error(e.stack); process.exitCode = 1; });
