'use strict';
// 1.11.0 item 13, real Chrome smoke: throwaway profile, intercepted page, mocked native port. Runs against the manual build and against the real store ZIP.
// Proves: the X on the on-video button opens the menu; "Hide on this website" stores the site and a hidden site gets no ribbon and no shadow root; the popup lists it with Remove and
// "Show on all websites"; "Hide everywhere" switches both settings off and the popup switch is the way back. Nothing is downloaded; no real profile, registry or app is touched.
const fs = require('node:fs'), path = require('node:path'), assert = require('node:assert/strict'), zlib = require('node:zlib'), { spawn } = require('node:child_process');
const root = path.resolve(__dirname, '..');
const exe = process.env.NEODM_BROWSER || 'C:/Program Files/Google/Chrome/Application/chrome.exe';
const scratchRoot = process.env.NEODM_TEST_SCRATCH || path.join(process.env.LOCALAPPDATA, 'hermes', 'cache', 'scratch');
fs.mkdirSync(scratchRoot, { recursive: true });
const shots = process.env.NEODM_SHOTS || path.join(root, 'artifacts', 'ribbon-menu-screenshots');
fs.mkdirSync(shots, { recursive: true });
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

async function run(label, extensionDir) {
  const scratch = fs.mkdtempSync(path.join(scratchRoot, 'neodm-ribbon1110-'));
  const child = spawn(exe, [`--user-data-dir=${scratch}`, '--remote-debugging-pipe', '--enable-unsafe-extension-debugging', '--headless=new', '--no-first-run', '--no-default-browser-check', '--disable-background-networking', '--disable-component-update', '--host-resolver-rules=MAP * ~NOTFOUND', '--window-size=1000,800', 'about:blank'], { stdio: ['ignore', 'ignore', 'ignore', 'pipe', 'pipe'] });
  let next = 1, buffer = ''; const pending = new Map(), events = [];
  child.stdio[4].on('data', data => {
    buffer += data.toString(); let end;
    while ((end = buffer.indexOf('\0')) >= 0) {
      const m = JSON.parse(buffer.slice(0, end)); buffer = buffer.slice(end + 1);
      if (m.id && pending.has(m.id)) { const p = pending.get(m.id); pending.delete(m.id); clearTimeout(p.timer); m.error ? p.reject(new Error(m.error.message)) : p.resolve(m.result); } else for (const h of events) h(m);
    }
  });
  const call = (method, params = {}, sessionId) => new Promise((resolve, reject) => { const id = next++, timer = setTimeout(() => { pending.delete(id); reject(new Error('CDP timeout: ' + method)); }, 20000); pending.set(id, { resolve, reject, timer }); child.stdio[3].write(JSON.stringify({ id, method, params, ...(sessionId ? { sessionId } : {}) }) + '\0'); });
  const attach = async id => (await call('Target.attachToTarget', { targetId: id, flatten: true })).sessionId;
  const evaluate = async (session, expression) => { const r = await call('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true, userGesture: true }, session); if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description || r.exceptionDetails.text); return r.result.value; };
  const wait = async (fn, what = 'condition') => { for (let i = 0; i < 100; i++) { const x = await fn(); if (x) return x; await sleep(100); } throw new Error('timed out: ' + what); };
  const press = async (session, p, button = 'left') => { for (const type of ['mousePressed', 'mouseReleased']) await call('Input.dispatchMouseEvent', { type, x: p.x, y: p.y, button, buttons: type === 'mousePressed' ? 1 : 0, clickCount: 1 }, session); };
  try {
    const id = (await call('Extensions.loadUnpacked', { path: extensionDir })).id;
    const sw = await wait(async () => (await call('Target.getTargets')).targetInfos.find(t => t.type === 'service_worker' && t.url === `chrome-extension://${id}/background.js`), 'service worker');
    const worker = await attach(sw.targetId);
    await wait(() => evaluate(worker, 'typeof chrome !== "undefined" && !!chrome.runtime && !!chrome.storage').catch(() => false), 'worker ready');
    await evaluate(worker, `(() => { chrome.runtime.connectNative = () => { const l = []; const port = { onMessage: { addListener: f => l.push(f) }, onDisconnect: { addListener() {} }, disconnect() {}, postMessage: req => { queueMicrotask(() => l.forEach(f => f(req.action === 'ping' ? { ok: true, youtube: true } : req.action === 'browser-sites' ? { ok: true, sites: [] } : { pending: true }))); } }; return port; }; return true; })()`);
    const stored = key => evaluate(worker, `chrome.storage.local.get(${JSON.stringify(key)}).then(r => JSON.stringify(r))`).then(JSON.parse);
    const page = (await call('Target.createTarget', { url: 'about:blank' })).targetId, session = await attach(page);
    await call('Page.enable', {}, session); await call('DOM.enable', {}, session);
    events.push(m => { if (m.method === 'Fetch.requestPaused' && m.sessionId === session) call('Fetch.fulfillRequest', { requestId: m.params.requestId, responseCode: 200, responseHeaders: [{ name: 'Content-Type', value: 'text/html; charset=utf-8' }], body: Buffer.from('<!doctype html><title>Authorized fixture</title><body style="margin:0"><h1>Authorized video fixture</h1><video controls style="display:block;width:600px;height:340px"></video>').toString('base64') }, session).catch(() => {}); });
    await call('Fetch.enable', { patterns: [{ urlPattern: '*' }] }, session);
    const open = async url => { await call('Page.navigate', { url }, session); await wait(() => evaluate(session, 'document.readyState === "complete"'), 'page load'); };
    const flat = async () => (await call('DOM.getFlattenedDocument', { depth: -1, pierce: true }, session)).nodes;
    const centre = async backendNodeId => { const box = await call('DOM.getBoxModel', { backendNodeId }, session).catch(() => null); return box && { x: (box.model.content[0] + box.model.content[4]) / 2, y: (box.model.content[1] + box.model.content[5]) / 2 }; };
    const attrOf = n => { const o = {}; const a = n.attributes || []; for (let i = 0; i < a.length; i += 2) o[a[i]] = a[i + 1]; return o; };
    const buttons = async pred => (await flat()).filter(n => n.nodeName === 'BUTTON' && pred(attrOf(n)));
    const hover = async () => call('Input.dispatchMouseEvent', { type: 'mouseMoved', x: 200, y: 200 }, session);
    const mainButton = async () => { await hover(); const [b] = await buttons(a => a['data-act'] === 'main'); return b || null; };
    const shot = async (name, s = session) => { const png = await call('Page.captureScreenshot', { format: 'png' }, s); fs.writeFileSync(path.join(shots, `${label.replace(/\W+/g, '-')}-${name}.png`), Buffer.from(png.data, 'base64')); };
    const openMenu = async () => { await hover(); const [x] = await buttons(a => a.class === 'x' && a['aria-haspopup'] === 'menu'); assert.ok(x, 'the X button has the menu role'); await press(session, await centre(x.backendNodeId)); return wait(async () => { const items = await buttons(a => a.role === 'menuitem'); return items.length === 3 ? items : null; }, 'three menu items'); };

    // 1. the button shows, and its X opens the three item menu
    await open('https://www.youtube.com/watch?v=aqz-KE-bpKQ');
    check(`${label}: the on-video button appears on a fresh site`, !!(await wait(mainButton, 'on-video button')));
    const items = await openMenu(); await shot('menu-open');
    const all = await flat(); const labels = items.map(it => { const node = all.find(n => n.backendNodeId === it.backendNodeId); return all.filter(n => n.parentId === node.nodeId).map(n => n.nodeValue).join(''); });
    check(`${label}: the X menu has Hide for now, Hide on this website, Hide everywhere`, JSON.stringify(labels) === JSON.stringify(['Hide for now', 'Hide on this website', 'Hide everywhere']));
    await call('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 }, session); await call('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 }, session);
    check(`${label}: Escape closes the menu`, await wait(async () => (await buttons(a => a.role === 'menuitem')).length === 0, 'menu closed'));
    // 2. Hide on this website
    const menu2 = await openMenu(); await press(session, await centre(menu2[1].backendNodeId));
    const hidden = await wait(async () => { const s = await stored('videoButtonHiddenHosts'); return s.videoButtonHiddenHosts?.length ? s : null; }, 'stored site');
    check(`${label}: Hide on this website stores the site name only`, JSON.stringify(hidden.videoButtonHiddenHosts) === JSON.stringify(['youtube.com']));
    check(`${label}: the button is gone at once`, await wait(async () => (await buttons(a => a['data-act'] === 'main')).length === 0, 'button removed'));
    await open('https://www.youtube.com/watch?v=abcdefghijk'); await sleep(1500); await hover(); await sleep(500);
    const nodes = await flat();
    check(`${label}: a hidden site gets no button, no ribbon and no UI element at all after a reload`, !nodes.some(n => n.nodeName === 'BUTTON' && attrOf(n)['data-act']) && !nodes.some(n => { const a = attrOf(n); return 'data-neodm-ribbon' in a || 'data-neodm-bars' in a; }));
    await shot('hidden-site');
    // 3. popup lists it
    const popupTarget = (await call('Target.createTarget', { url: `chrome-extension://${id}/popup.html`, background: true })).targetId, popup = await attach(popupTarget);
    await wait(() => evaluate(popup, 'document.querySelectorAll("#hiddenSites li .host").length === 1'), 'popup list');
    check(`${label}: the popup lists the hidden site with its own Remove button`, await evaluate(popup, 'document.querySelector("#hiddenSites li .host").textContent === "youtube.com" && document.querySelector("#hiddenSites li button").textContent === "Remove" && !document.getElementById("hiddenAll").hidden'));
    await call('Page.bringToFront', {}, popup); await evaluate(popup, 'document.getElementById("hiddenPanel").open = true; document.getElementById("hiddenPanel").scrollIntoView()'); await shot('popup-hidden-list', popup);
    const removeBox = await evaluate(popup, '(() => { const e = document.querySelector("#hiddenSites li button"); e.scrollIntoView({ block: "center" }); const r = e.getBoundingClientRect(); return { x: r.x + r.width / 2, y: r.y + r.height / 2 }; })()');
    await press(popup, removeBox);
    check(`${label}: Remove takes the site off the list`, await wait(async () => JSON.stringify((await stored('videoButtonHiddenHosts')).videoButtonHiddenHosts) === '[]', 'removed'));
    await call('Target.activateTarget', { targetId: page }); await open('https://www.youtube.com/watch?v=aqz-KE-bpKQ');
    check(`${label}: the button is back after Remove`, !!(await wait(mainButton, 'button back')));
    // 4. Hide everywhere, popup switch is the way back
    const menu3 = await openMenu(); await press(session, await centre(menu3[2].backendNodeId));
    const off = await wait(async () => { const s = await stored(['ribbonEnabled', 'videoButtonEnabled']); return s.ribbonEnabled === false && s.videoButtonEnabled === false ? s : null; }, 'switches off');
    check(`${label}: Hide everywhere switches the ribbon and the video button off`, !!off);
    check(`${label}: and the button is gone`, await wait(async () => (await buttons(a => a['data-act'] === 'main')).length === 0, 'button removed'));
    await evaluate(popup, 'location.reload()'); await sleep(800); await wait(() => evaluate(popup, 'document.getElementById("ribbon") !== null && document.readyState === "complete"'), 'popup reload'); await sleep(400);
    check(`${label}: the popup shows both switches off`, await evaluate(popup, '!document.getElementById("ribbon").checked && !document.getElementById("videoButton").checked'));
    const sw1 = await evaluate(popup, '(() => { document.getElementById("ribbon").scrollIntoView({ block: "center" }); const r = document.getElementById("ribbon").getBoundingClientRect(); const v = document.getElementById("videoButton").getBoundingClientRect(); return [{ x: r.x + r.width / 2, y: r.y + r.height / 2 }, { x: v.x + v.width / 2, y: v.y + v.height / 2 }]; })()');
    await call('Page.bringToFront', {}, popup); await press(popup, sw1[0]); await press(popup, sw1[1]);
    check(`${label}: the popup switches turn them back on`, await wait(async () => { const s = await stored(['ribbonEnabled', 'videoButtonEnabled']); return s.ribbonEnabled === true && s.videoButtonEnabled === true; }, 'switched on'));
    await call('Target.activateTarget', { targetId: page }); await open('https://www.youtube.com/watch?v=aqz-KE-bpKQ');
    check(`${label}: the button shows again`, !!(await wait(mainButton, 'button again')));
    // 5. Show on all websites
    await evaluate(worker, `chrome.storage.local.set({ videoButtonHiddenHosts: ['a.example', 'b.example'] })`);
    await evaluate(popup, 'location.reload()'); await sleep(800); await wait(() => evaluate(popup, 'document.querySelectorAll("#hiddenSites li").length === 2'), 'two sites');
    await evaluate(popup, 'document.getElementById("hiddenPanel").open = true');
    const allBox = await evaluate(popup, '(() => { const e = document.getElementById("hiddenAll"); e.scrollIntoView({ block: "center" }); const r = e.getBoundingClientRect(); return { x: r.x + r.width / 2, y: r.y + r.height / 2 }; })()');
    await call('Page.bringToFront', {}, popup); await press(popup, allBox);
    check(`${label}: Show on all websites empties the list`, await wait(async () => JSON.stringify((await stored('videoButtonHiddenHosts')).videoButtonHiddenHosts) === '[]', 'list cleared'));
    check(`${label}: no new permission: the manifest still asks for the same ones`, JSON.stringify(JSON.parse(fs.readFileSync(path.join(extensionDir, 'manifest.json'), 'utf8')).permissions) === JSON.stringify(['nativeMessaging', 'downloads', 'contextMenus', 'storage', 'webRequest', 'tabs', 'cookies']));
  } finally {
    try { await call('Browser.close'); } catch { child.kill(); }
    await sleep(500); try { fs.rmSync(scratch, { recursive: true, force: true }); } catch {}
  }
}

(async () => {
  const work = fs.mkdtempSync(path.join(scratchRoot, 'neodm-ribbon1110-ext-'));
  try {
    const manual = path.join(work, 'manual'); copyDir(path.join(root, 'extension'), manual);
    const store = path.join(work, 'store'); unpackZip(buildStoreZip().zip, store);
    assert.ok(!('key' in JSON.parse(fs.readFileSync(path.join(store, 'manifest.json'), 'utf8'))), 'the store build has no manifest key');
    await run('manual build', manual);
    await run('store ZIP', store);
    console.log(`ALL ${checks} CHROMIUM RIBBON MENU CHECKS PASSED`);
  } finally { try { fs.rmSync(work, { recursive: true, force: true }); } catch {} }
})().catch(e => { console.error(e.stack); process.exitCode = 1; });
