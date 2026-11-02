'use strict';
// Real Chromium, isolated profile, intercepted page fixtures, mocked native ports. No downloads or registry changes.
const fs = require('node:fs'), path = require('node:path'), assert = require('node:assert/strict'), zlib = require('node:zlib'), { spawn } = require('node:child_process');
const root = path.resolve(__dirname, '..');
const exe = process.env.NEODM_BROWSER || 'C:/Program Files/Google/Chrome/Application/chrome.exe';
const scratchRoot = process.env.NEODM_TEST_SCRATCH || path.join(process.env.LOCALAPPDATA, 'hermes', 'cache', 'scratch');
fs.mkdirSync(scratchRoot, { recursive: true });
const scratch = fs.mkdtempSync(path.join(scratchRoot, 'neodm-handoff171-'));
const shot = path.join(root, 'artifacts/youtube-screenshots/manual-extension.png');
fs.mkdirSync(path.dirname(shot), { recursive: true });
const child = spawn(exe, [`--user-data-dir=${scratch}`, '--remote-debugging-pipe', '--enable-unsafe-extension-debugging', '--headless=new', '--no-first-run', '--no-default-browser-check', '--disable-background-networking', '--disable-component-update', '--host-resolver-rules=MAP * ~NOTFOUND', 'about:blank'], { stdio: ['ignore', 'ignore', 'ignore', 'pipe', 'pipe'] });
let next = 1, buffer = '', checks = 0; const pending = new Map(), events = [];
child.stdio[4].on('data', data => {
  buffer += data.toString(); let end;
  while ((end = buffer.indexOf('\0')) >= 0) {
    const m = JSON.parse(buffer.slice(0, end)); buffer = buffer.slice(end + 1);
    if (m.id && pending.has(m.id)) { const p = pending.get(m.id); pending.delete(m.id); clearTimeout(p.timer); m.error ? p.reject(new Error(m.error.message)) : p.resolve(m.result); }
    else for (const handler of events) handler(m);
  }
});
function call(method, params = {}, sessionId) {
  return new Promise((resolve, reject) => { const id = next++, timer = setTimeout(() => { pending.delete(id); reject(new Error('CDP timeout: ' + method)); }, 15000); pending.set(id, { resolve, reject, timer }); child.stdio[3].write(JSON.stringify({ id, method, params, ...(sessionId ? { sessionId } : {}) }) + '\0'); });
}
const sleep = ms => new Promise(r => setTimeout(r, ms));
const attach = async id => (await call('Target.attachToTarget', { targetId: id, flatten: true })).sessionId;
async function evaluate(session, expression) { const r = await call('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true, userGesture: true }, session); if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description || r.exceptionDetails.text); return r.result.value; }
async function wait(fn) { for (let i = 0; i < 100; i++) { const x = await fn(); if (x) return x; await sleep(100); } throw new Error('condition timed out'); }
function check(name, ok) { assert.ok(ok, name); checks++; console.log('PASS ' + name); }
async function click(session, id) {
  const p = await evaluate(session, `(() => { const r = document.getElementById(${JSON.stringify(id)}).getBoundingClientRect(); return { x: r.x + r.width / 2, y: r.y + r.height / 2 }; })()`);
  for (const type of ['mousePressed', 'mouseReleased']) await call('Input.dispatchMouseEvent', { type, ...p, button: 'left', buttons: type === 'mousePressed' ? 1 : 0, clickCount: 1 }, session);
}
function unpackZip(zip, directory) {
  fs.mkdirSync(directory, { recursive: true });
  let count = 0;
  for (let p = 0; zip.readUInt32LE(p) === 0x04034b50;) {
    const method = zip.readUInt16LE(p + 8), size = zip.readUInt32LE(p + 18), nl = zip.readUInt16LE(p + 26), el = zip.readUInt16LE(p + 28);
    const name = zip.toString('utf8', p + 30, p + 30 + nl), start = p + 30 + nl + el, bytes = zip.subarray(start, start + size);
    const out = path.resolve(directory, name);
    assert.ok(out.startsWith(path.resolve(directory) + path.sep) && (method === 0 || method === 8), 'safe packaged ZIP entry');
    fs.mkdirSync(path.dirname(out), { recursive: true }); fs.writeFileSync(out, method === 8 ? zlib.inflateRawSync(bytes) : bytes); p = start + size; count++;
  }
  assert.ok(count > 0, 'packaged ZIP is not empty');
}
(async () => {
  try {
    console.log('BROWSER=' + (await call('Browser.getVersion')).product);
    const manualDir = path.join(scratch, 'manual-extension');
    const manualZip = fs.readFileSync(path.join(root, 'dist', 'NeoDM-extension-manual-unpacked.zip'));
    console.log('MANUAL_ZIP_SHA256=' + require('node:crypto').createHash('sha256').update(manualZip).digest('hex'));
    unpackZip(manualZip, manualDir);
    const id = (await call('Extensions.loadUnpacked', { path: manualDir })).id;
    const sw = await wait(async () => (await call('Target.getTargets')).targetInfos.find(t => t.type === 'service_worker' && t.url === `chrome-extension://${id}/background.js`));
    const worker = await attach(sw.targetId);
    await evaluate(worker, `(() => {
      globalThis.__native = []; globalThis.__ports = []; globalThis.__cookieReads = 0; globalThis.__mode = 'hold';
      chrome.cookies.getAll = async () => { __cookieReads++; throw Error('handoff must not read cookies'); };
      chrome.runtime.connectNative = () => {
        const listeners = []; let disconnect;
        const port = { onMessage: { addListener: f => listeners.push(f) }, onDisconnect: { addListener: f => { disconnect = f; } }, disconnect: () => {}, postMessage: req => {
          __native.push(req);
          const emit = reply => listeners.forEach(f => f(reply));
          if (req.action === 'ping') queueMicrotask(() => emit(__mode === 'old' ? { ok: true } : { ok: true, youtube: true }));
          else if (req.action === 'browser-sites') queueMicrotask(() => emit({ ok: true, sites: [] }));
          else if (__mode === 'missing') queueMicrotask(() => disconnect());
          else { __ports.push(emit); queueMicrotask(() => emit({ pending: true })); }
        }}; return port;
      }; return true;
    })()`);
    const page = (await call('Target.createTarget', { url: 'about:blank' })).targetId, pageSession = await attach(page);
    await call('Page.enable', {}, pageSession);
    events.push(m => { if (m.method === 'Fetch.requestPaused' && m.sessionId === pageSession) call('Fetch.fulfillRequest', { requestId: m.params.requestId, responseCode: 200, responseHeaders: [{ name: 'Content-Type', value: 'text/html; charset=utf-8' }], body: Buffer.from('<!doctype html><title>Authorized fixture</title><h1>Authorized video fixture</h1><video controls style="display:block;width:600px;height:340px"></video><a id="videoLink" href="https://youtu.be/abcdefghijk?si=discard">Video link</a>').toString('base64') }, pageSession).catch(() => {}); });
    await call('Fetch.enable', { patterns: [{ urlPattern: '*' }] }, pageSession);
    await call('Page.navigate', { url: 'https://www.youtube.com/' }, pageSession);
    await wait(() => evaluate(pageSession, 'document.getElementById("videoLink") !== null'));
    await evaluate(pageSession, `history.pushState({}, '', '/watch?v=aqz-KE-bpKQ&list=discard')`);
    const popupTarget = (await call('Target.createTarget', { url: `chrome-extension://${id}/popup.html`, background: true })).targetId, popup = await attach(popupTarget);
    await wait(() => evaluate(popup, 'document.getElementById("youtubePanel")?.hidden === false'));
    check('manual popup offers explicit labeled handoff on intercepted YouTube page', await evaluate(popup, 'document.getElementById("youtubeSend").textContent.includes("Send YouTube link") && document.getElementById("youtubePanel").textContent.includes("No browser cookies")'));
    await click(popup, 'youtubeSend'); await click(popup, 'youtubeSend');
    await wait(() => evaluate(worker, '__native.filter(r => r.youtube).length === 1'));
    check('real double mouse click forwards exactly once', await evaluate(worker, '__native.filter(r => r.youtube).length === 1'));
    check('only canonical URL and protocol metadata reach mock native port', await evaluate(worker, `JSON.stringify(__native.find(r => r.youtube)) === JSON.stringify({ url: 'https://www.youtube.com/watch?v=aqz-KE-bpKQ', youtube: true, extVersion: '1.8.0', keepAlive: true }) && __cookieReads === 0`));
    check('pending desktop decision keeps button disabled', await evaluate(popup, 'document.getElementById("youtubeSend").disabled'));
    await evaluate(worker, '__ports.shift()({ ok: false, error: "cancelled", cancelled: true })');
    await wait(() => evaluate(popup, '!document.getElementById("youtubeSend").disabled'));
    check('desktop cancellation is shown without a download', await evaluate(popup, 'document.getElementById("status").textContent.includes("Cancelled")'));
    await evaluate(pageSession, `history.pushState({}, '', '/shorts/abcdefghijk')`);
    await wait(() => evaluate(popup, 'activeTab.url.includes("/shorts/abcdefghijk")'));
    await click(popup, 'youtubeSend'); await wait(() => evaluate(worker, '__native.filter(r => r.youtube).length === 2'));
    check('SPA shorts navigation forwards the fresh video', await evaluate(worker, '__native.filter(r => r.youtube).at(-1).url === "https://www.youtube.com/watch?v=abcdefghijk"'));
    await evaluate(worker, '__ports.shift()({ ok: false, error: "cancelled", cancelled: true })');
    await wait(() => evaluate(popup, '!document.getElementById("youtubeSend").disabled'));
    await evaluate(worker, '__mode = "old"'); await click(popup, 'youtubeSend');
    await wait(() => evaluate(popup, 'document.getElementById("status").textContent.includes("1.7.1")'));
    check('older host sends no page request and shows update error', await evaluate(worker, '__native.filter(r => r.youtube).length === 2'));
    await evaluate(worker, '__mode = "hold"');
    // Headless Chromium has no visual context-menu UI. Dispatch a real right click, then invoke its registered handler.
    await call('Input.dispatchMouseEvent', { type: 'mousePressed', x: 100, y: 180, button: 'right', buttons: 2, clickCount: 1 }, pageSession);
    await call('Input.dispatchMouseEvent', { type: 'mouseReleased', x: 100, y: 180, button: 'right', buttons: 0, clickCount: 1 }, pageSession);
    await evaluate(worker, `void onMenuClick({ menuItemId: 'youtube-link', linkUrl: 'https://youtu.be/aqz-KE-bpKQ?si=discard' }, { id: 999, url: 'https://example.org/' })`);
    await wait(() => evaluate(worker, '__native.filter(r => r.youtube).length === 3'));
    check('right-click handler forwards clicked link instead of unrelated tab', await evaluate(worker, '__native.filter(r => r.youtube).at(-1).url === "https://www.youtube.com/watch?v=aqz-KE-bpKQ"'));
    await evaluate(worker, '__ports.shift()({ ok: false, error: "cancelled", cancelled: true })');
    await call('Target.activateTarget', { targetId: page });
    await call('DOM.enable', {}, pageSession);
    const ribbonButton = async () => {
      await call('Input.dispatchMouseEvent', { type: 'mouseMoved', x: 200, y: 150 }, pageSession);
      const { nodes } = await call('DOM.getFlattenedDocument', { depth: -1, pierce: true }, pageSession);
      const button = nodes.find(n => n.nodeName === 'BUTTON' && n.attributes?.includes('data-act') && n.attributes?.includes('main'));
      if (!button) return null;
      const resolved = await call('DOM.resolveNode', { backendNodeId: button.backendNodeId }, pageSession);
      const visible = await call('Runtime.callFunctionOn', { objectId: resolved.object.objectId, functionDeclaration: 'function(){ return getComputedStyle(this).visibility === "visible" && !this.disabled; }', returnByValue: true }, pageSession);
      if (!visible.result.value) return null;
      const box = await call('DOM.getBoxModel', { backendNodeId: button.backendNodeId }, pageSession).catch(() => null);
      return box && { button, x: (box.model.content[0] + box.model.content[4]) / 2, y: (box.model.content[1] + box.model.content[5]) / 2 };
    };
    let ribbon = await wait(ribbonButton);
    check('real closed-shadow on-video button has Send to NeoDM tooltip', ribbon.button.attributes.includes('Send to NeoDM'));
    const pressRibbon = async p => { for (const type of ['mousePressed', 'mouseReleased']) await call('Input.dispatchMouseEvent', { type, x: p.x, y: p.y, button: 'left', buttons: type === 'mousePressed' ? 1 : 0, clickCount: 1 }, pageSession); };
    const ribbonShot = await call('Page.captureScreenshot', { format: 'png' }, pageSession); fs.writeFileSync(path.join(path.dirname(shot), 'ribbon-fixture.png'), Buffer.from(ribbonShot.data, 'base64'));
    await pressRibbon(ribbon); await pressRibbon(ribbon);
    await sleep(500);
    await wait(() => evaluate(worker, '__native.filter(r => r.youtube).length === 4'));
    check('actual video ribbon double click sends one current canonical PAGE without cookies', await evaluate(worker, '__native.filter(r => r.youtube).at(-1).url === "https://www.youtube.com/watch?v=abcdefghijk" && __cookieReads === 0'));
    await evaluate(worker, '__ports.shift()({ ok: false, error: "cancelled", cancelled: true })');
    await evaluate(pageSession, `history.pushState({}, '', '/watch?v=aqz-KE-bpKQ')`); await sleep(1500);
    ribbon = await wait(ribbonButton); await pressRibbon(ribbon);
    await wait(() => evaluate(worker, '__native.filter(r => r.youtube).length === 5'));
    check('ribbon follows SPA navigation rather than sending previous video', await evaluate(worker, '__native.filter(r => r.youtube).at(-1).url === "https://www.youtube.com/watch?v=aqz-KE-bpKQ"'));
    await evaluate(worker, '__ports.shift()({ ok: false, error: "cancelled", cancelled: true })');
    await call('Page.bringToFront', {}, popup);
    const png = await call('Page.captureScreenshot', { format: 'png' }, popup); fs.writeFileSync(shot, Buffer.from(png.data, 'base64')); console.log('SCREENSHOT=' + shot);
    check('fresh profile has no browser download jobs', await evaluate(worker, 'chrome.downloads.search({}).then(items => items.length === 0)'));
    // Exercise the actual generated store ZIP in the same isolated browser, not source-code assertions alone.
    const storeZip = process.env.NEODM_STORE_ZIP || path.join(root, 'dist', 'NeoDM-extension-store.zip');
    const zip = fs.readFileSync(storeZip);
    console.log('STORE_ZIP=' + storeZip);
    console.log('STORE_ZIP_SHA256=' + require('node:crypto').createHash('sha256').update(zip).digest('hex'));
    const storeDir = path.join(scratch, 'store-extension'); unpackZip(zip, storeDir);
    const storeId = (await call('Extensions.loadUnpacked', { path: storeDir })).id;
    const storeWorkerTarget = await wait(async () => (await call('Target.getTargets')).targetInfos.find(t => t.type === 'service_worker' && t.url === `chrome-extension://${storeId}/background.js`));
    const storeWorker = await attach(storeWorkerTarget.targetId);
    await evaluate(storeWorker, `globalThis.__storeCalls = []; native = async r => { __storeCalls.push(r); return r.action === 'ping' ? { ok: true, youtube: true } : r.action === 'browser-sites' ? { ok: true, sites: [] } : { ok: true, id: 'store-fixture' }; }`);
    await call('Target.activateTarget', { targetId: page });
    const storePopupTarget = (await call('Target.createTarget', { url: `chrome-extension://${storeId}/popup.html`, background: true })).targetId;
    const storePopup = await attach(storePopupTarget);
    await wait(() => evaluate(storePopup, 'document.getElementById("youtubePanel")?.hidden === false'));
    check('actual store package has explicit page-link handoff button', await evaluate(storePopup, '!!document.getElementById("youtubeSend") && chrome.runtime.getManifest().version_name === "1.8.0 store"'));
    check('actual store worker has the same handoff functions', await evaluate(storeWorker, 'typeof sendYoutube === "function" && typeof NeoSniff.youtubePage === "function"'));
    await click(storePopup, 'youtubeSend'); await wait(() => evaluate(storeWorker, '__storeCalls.some(r => r.youtube === true)'));
    check('store popup actually forwards canonical PAGE with no cookies', await evaluate(storeWorker, '__storeCalls.filter(r => r.youtube).length === 1 && __storeCalls.find(r => r.youtube).url === "https://www.youtube.com/watch?v=aqz-KE-bpKQ" && !Object.hasOwn(__storeCalls.find(r => r.youtube), "cookie")'));
    await call('Extensions.uninstall', { id });
    await call('Target.activateTarget', { targetId: page });
    await call('Page.navigate', { url: 'https://www.youtube.com/watch?v=aqz-KE-bpKQ&store-fixture=1' }, pageSession);
    await wait(() => evaluate(pageSession, 'document.querySelector("video") !== null'));
    ribbon = await wait(ribbonButton); await pressRibbon(ribbon);
    await wait(() => evaluate(storeWorker, '__storeCalls.filter(r => r.youtube).length === 2'));
    check('generated store ZIP on-video button sends current canonical page in a fresh document without manual extension', await evaluate(storeWorker, '__storeCalls.filter(r => r.youtube).at(-1).url === "https://www.youtube.com/watch?v=aqz-KE-bpKQ"'));
    await evaluate(storeWorker, `void onMenuClick({ menuItemId: 'youtube-link', linkUrl: 'https://youtu.be/abcdefghijk?si=discard' }, { id: 999, url: 'https://example.org/' })`);
    await wait(() => evaluate(storeWorker, '__storeCalls.filter(r => r.youtube).length === 3'));
    check('generated store ZIP link context handler forwards canonical clicked page', await evaluate(storeWorker, '__storeCalls.filter(r => r.youtube).at(-1).url === "https://www.youtube.com/watch?v=abcdefghijk"'));
    await call('Page.bringToFront', {}, storePopup);
    const storeShot = path.join(path.dirname(shot), 'store-extension.png');
    const storePng = await call('Page.captureScreenshot', { format: 'png' }, storePopup); fs.writeFileSync(storeShot, Buffer.from(storePng.data, 'base64')); console.log('SCREENSHOT=' + storeShot);
    console.log('ALL ' + checks + ' CHROMIUM HANDOFF CHECKS PASSED');
  } finally {
    try { await call('Browser.close'); } catch { child.kill(); }
    await sleep(400); try { fs.rmSync(scratch, { recursive: true, force: true }); } catch {}
  }
})().catch(e => { console.error(e.stack); process.exitCode = 1; child.kill(); });
