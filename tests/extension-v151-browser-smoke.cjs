'use strict';
/* Real-browser smoke test for 1.5.1 (not part of `npm test`). Loads extension/ unpacked into an isolated profile of an installed Chrome/Edge, serves a local page with
   three <video> elements (two direct files, one blob: player under an overlay), then checks: metadata reported by the page, popup rows, "show on page" outline,
   right-click resolution (the native download is stubbed, nothing is sent to the desktop app) and takes a screenshot of the popup.
   Env: NEODM_BROWSER=<exe>, NEODM_VIDEOS=<dir with a.mp4 b.mp4 c.mp4>, NEODM_SHOT=<png path for the popup>, NEODM_SHOT_PAGE=<png path for the page with the outline>. */
const fs = require('node:fs'), path = require('node:path'), os = require('node:os'), http = require('node:http'), { spawn } = require('node:child_process');
const root = path.join(__dirname, '..'), EXT_ID = fs.readFileSync(path.join(root, 'tools/extension-id.txt'), 'utf8').trim();
const pf = [process.env.ProgramFiles, process.env['ProgramFiles(x86)'], process.env.LOCALAPPDATA].filter(Boolean);
const candidates = [process.env.NEODM_BROWSER, ...pf.flatMap(p => ['Google/Chrome/Application/chrome.exe', 'Microsoft/Edge/Application/msedge.exe'].map(x => path.join(p, x)))].filter(Boolean);
const exe = candidates.find(c => fs.existsSync(c));
const dir = process.env.NEODM_VIDEOS || path.join(__dirname, '../artifacts/tmp/smoke');
if (!exe || !['a', 'b', 'c'].every(n => fs.existsSync(path.join(dir, n + '.mp4')))) { console.log('SKIP: no Chromium browser or no test videos in ' + dir); process.exit(0); }
const sleep = ms => new Promise(r => setTimeout(r, ms));
const HTML = `<!doctype html><title>Video Lab</title><meta property="og:title" content="Video Lab OG"><style>body{margin:0;font:14px sans-serif}.card{margin:6px 10px}.wrap{position:relative;width:320px;height:180px}#cover{position:absolute;inset:0;background:rgba(255,255,255,.01)}h2{margin:2px 0;font-size:15px}</style>
<div class="card"><h2>Alpha clip</h2><video id="a" src="/a.mp4" controls muted preload="auto" width="320" height="180"></video></div>
<div class="card"><h2>Bravo clip</h2><video id="b" src="/b.mp4" controls muted loop preload="auto" width="320" height="180"></video></div>
<div class="card"><h2>Charlie stream</h2><div class="wrap"><video id="c" controls muted preload="auto" width="320" height="180"></video><div id="cover"></div></div></div>
<script>fetch('/c.mp4').then(r => r.blob()).then(b => { document.getElementById('c').src = URL.createObjectURL(b); });</script>`;
const server = http.createServer((req, res) => {
  if (req.url === '/') { res.setHeader('Content-Type', 'text/html'); return res.end(HTML); }
  const m = /^\/([abc])\.mp4$/.exec(req.url);
  if (!m) { res.statusCode = 404; return res.end(); }
  const file = path.join(dir, m[1] + '.mp4'), size = fs.statSync(file).size, r = /bytes=(\d+)-(\d*)/.exec(req.headers.range || '');
  res.setHeader('Content-Type', 'video/mp4'); res.setHeader('Accept-Ranges', 'bytes');
  if (r) { const s = +r[1], e = r[2] ? +r[2] : size - 1; res.statusCode = 206; res.setHeader('Content-Range', `bytes ${s}-${e}/${size}`); res.setHeader('Content-Length', e - s + 1); return fs.createReadStream(file, { start: s, end: e }).pipe(res); }
  res.setHeader('Content-Length', size); fs.createReadStream(file).pipe(res);
});
(async () => {
  await new Promise(r => server.listen(0, '127.0.0.1', r)); const origin = `http://127.0.0.1:${server.address().port}`;
  const profile = fs.mkdtempSync(path.join(process.env.TMPDIR || os.tmpdir(), 'neodm-smoke151-'));
  const child = spawn(exe, [`--user-data-dir=${profile}`, '--remote-debugging-pipe', '--enable-unsafe-extension-debugging', '--no-first-run', '--no-default-browser-check', '--headless=new', '--autoplay-policy=no-user-gesture-required', '--window-size=900,700', 'about:blank'], { stdio: ['ignore', 'ignore', 'ignore', 'pipe', 'pipe'] });
  const results = []; const check = (name, ok, detail = '') => { results.push(ok); console.log((ok ? 'PASS ' : 'FAIL ') + name + (detail ? ' - ' + detail : '')); };
  let nextId = 1, buf = ''; const pending = new Map();
  child.stdio[4].on('data', d => { buf += d.toString('utf8'); let i; while ((i = buf.indexOf('\u0000')) >= 0) { const m = JSON.parse(buf.slice(0, i)); buf = buf.slice(i + 1); if (m.id && pending.has(m.id)) { const p = pending.get(m.id); pending.delete(m.id); m.error ? p.rej(new Error(m.error.message)) : p.res(m.result); } } });
  const call = (method, params = {}, sessionId) => new Promise((res, rej) => { const id = nextId++; pending.set(id, { res, rej }); child.stdio[3].write(JSON.stringify({ id, method, params, sessionId }) + '\u0000'); });
  const attach = async targetId => (await call('Target.attachToTarget', { targetId, flatten: true })).sessionId;
  const evalIn = async (session, expression) => { const r = await call('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true, userGesture: true }, session); if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description || r.exceptionDetails.text); return r.result.value; };
  try {
    console.log('browser:', exe, (await call('Browser.getVersion')).product);
    const loaded = await call('Extensions.loadUnpacked', { path: path.join(root, 'extension') });
    check('extension loaded under the manifest-key ID', loaded.id === EXT_ID, loaded.id);
    let sw; for (let i = 0; i < 50 && !sw; i++) { sw = (await call('Target.getTargets')).targetInfos.find(t => t.type === 'service_worker' && /^chrome-extension:\/\/[a-p]{32}\/background\.js$/.test(t.url)); if (!sw) await sleep(200); }
    check('service worker started', !!sw); if (!sw) throw new Error('no worker');
    const swS = await attach(sw.targetId), inSw = e => evalIn(swS, e);
    const page = (await call('Target.createTarget', { url: origin + '/' })).targetId, pageS = await attach(page), inPage = e => evalIn(pageS, e);
    await sleep(1500);
    await inPage('document.getElementById("b").play().then(() => true)'); // b is the one playing
    let tabId = null, state = null;
    for (let i = 0; i < 40; i++) {
      await sleep(400);
      tabId = await inSw(`chrome.tabs.query({}).then(ts => (ts.find(t => t.url.startsWith(${JSON.stringify(origin)})) || {}).id ?? null)`);
      if (tabId != null) state = await inSw(`chrome.storage.session.get('neodm.media.${tabId}').then(r => r['neodm.media.${tabId}'] || null)`);
      if (state && state.items.some(i => i.url.endsWith('/b.mp4') && i.playing && i.thumb) && state.items.some(i => i.url.endsWith('/a.mp4') && i.thumb) && state.blobs && state.blobs.length && state.items.some(i => i.url.endsWith('/c.mp4'))) break;
    }
    const byName = n => (state?.items || []).find(i => i.url.endsWith('/' + n + '.mp4'));
    console.log('stored items:', JSON.stringify((state?.items || []).map(i => ({ n: i.url.split('/').pop(), source: i.source, size: i.size, title: i.title, dur: i.dur, w: i.w, h: i.h, playing: i.playing, vis: i.vis, thumb: i.thumb ? i.thumb.length : 0, dom: i.dom }))), 'blobs:', JSON.stringify((state?.blobs || []).map(b => ({ title: b.title, playing: b.playing, vis: b.vis, dur: b.dur, w: b.w, thumb: b.thumb ? b.thumb.length : 0 }))));
    const A = byName('a'), B = byName('b'), C = byName('c');
    check('a: title from the heading, duration, size in pixels, thumbnail <= 20 KB', !!A && A.title === 'Alpha clip' && Math.abs(A.dur - 4) < 0.3 && A.w === 480 && A.h === 270 && A.thumb && A.thumb.length <= 20480 && A.size > 1e6, JSON.stringify(A && { title: A.title, dur: A.dur, w: A.w, thumb: A.thumb && A.thumb.length, size: A.size }));
    check('b: playing, 640x360, 8 s, merged with the network size', !!B && B.playing === true && B.w === 640 && Math.abs(B.dur - 8) < 0.3 && B.size > 5e6 && B.title === 'Bravo clip');
    check('c: network file exists, the blob player is reported separately', !!C && C.source === 'net' && state.blobs.length === 1 && state.blobs[0].title === 'Charlie stream' && !!state.blobs[0].thumb);
    const listed = await inSw(`chrome.storage.session.get('neodm.media.${tabId}').then(r => visibleItems(r['neodm.media.${tabId}']))`);
    console.log('listed order:', JSON.stringify(listed.map(i => [i.url.split('/').pop(), !!i.playing, !!i.viaBlob, i.title])));
    check('order: playing first, the blob file is paired with its player (title, viaBlob)', listed[0]?.url.endsWith('/b.mp4') && listed[0].playing && listed.some(i => i.url.endsWith('/c.mp4') && i.viaBlob && i.title === 'Charlie stream'));

    // the popup, opened as a background tab so the page stays the active tab
    const pop = (await call('Target.createTarget', { url: `chrome-extension://${EXT_ID}/popup.html`, background: true })).targetId, popS = await attach(pop), inPop = e => evalIn(popS, e);
    await sleep(1500);
    const rows = await inPop(`[...document.querySelectorAll('#mediaList li.media')].map(li => ({ title: li.querySelector('.title').textContent, meta: li.querySelector('.meta').textContent, badge: li.querySelector('.badge')?.textContent || '', img: li.querySelector('.neo-thumb img')?.src.slice(0, 22) || '', video: !!li.querySelector('.neo-thumb video'), host: li.querySelector('.host').textContent }))`);
    console.log('popup rows:', JSON.stringify(rows));
    check('popup shows three rows that differ by title, duration and size', rows.length === 3 && new Set(rows.map(r => r.title)).size === 3 && new Set(rows.map(r => r.meta)).size === 3);
    check('popup: Playing now badge on the playing video, listed first', rows[0]?.title === 'Bravo clip' && rows[0].badge === 'Playing now' && /^Video 640x360 · 00:08 · /.test(rows[0].meta));
    check('popup: thumbnails are real frames (data:image/jpeg)', rows.filter(r => r.img.startsWith('data:image/jpeg')).length === 3);
    const shot = process.env.NEODM_SHOT || path.join(root, 'artifacts/popup-smoke-151.png');
    await call('Emulation.setDeviceMetricsOverride', { width: 420, height: 760, deviceScaleFactor: 1, mobile: false }, popS);
    await sleep(500); const png = await call('Page.captureScreenshot', { format: 'png' }, popS); fs.writeFileSync(shot, Buffer.from(png.data, 'base64')); console.log('popup screenshot:', shot);
    const popWidth = await inPop('document.body.getBoundingClientRect().width'); check('popup body is 400 px wide', popWidth === 400, String(popWidth));

    // show on page
    const outline = id => inPage(`getComputedStyle(document.getElementById('${id}')).outlineStyle + ' ' + getComputedStyle(document.getElementById('${id}')).outlineWidth`);
    await inPop(`document.querySelectorAll('#mediaList li.media')[1].dispatchEvent(new MouseEvent('mouseenter'))`); await sleep(700);
    const hov = [await outline('a'), await outline('b'), await outline('c')];
    console.log('outlines while hovering row 2:', JSON.stringify(hov));
    check('hovering a row outlines exactly that video with 3px', hov.filter(x => x === 'solid 3px').length === 1);
    if (process.env.NEODM_SHOT_PAGE) { const p2 = await call('Page.captureScreenshot', { format: 'png' }, pageS); fs.writeFileSync(process.env.NEODM_SHOT_PAGE, Buffer.from(p2.data, 'base64')); }
    await inPop(`document.querySelectorAll('#mediaList li.media')[1].dispatchEvent(new MouseEvent('mouseleave'))`); await sleep(500);
    check('leaving the row removes the outline', (await Promise.all(['a', 'b', 'c'].map(outline))).every(x => !x.startsWith('solid')));
    await inPop(`chrome.runtime.sendMessage({ action: 'popup-locate', tabId: ${tabId}, id: ${JSON.stringify(origin + '/b.mp4')}, on: true, scroll: true })`); await sleep(500);
    const onNow = await outline('b'); await sleep(4000);
    check('a single request (popup closed, nothing renews it) expires after 3 s', onNow === 'solid 3px' && (await outline('b')) !== 'solid 3px', onNow + ' -> ' + await outline('b'));
    // the blob row's outline lands on the blob video (c)
    const cRow = await inPop(`[...document.querySelectorAll('#mediaList li.media')].findIndex(li => li.querySelector('.title').textContent === 'Charlie stream')`);
    await inPop(`document.querySelectorAll('#mediaList li.media')[${cRow}].dispatchEvent(new MouseEvent('mouseenter'))`); await sleep(700);
    check('the row of a blob video outlines the blob <video>', (await outline('c')) === 'solid 3px');
    await inPop(`document.querySelectorAll('#mediaList li.media')[${cRow}].dispatchEvent(new MouseEvent('mouseleave'))`);

    // right click: real mouse events in the page, then the real menu handler with the native call stubbed
    await inSw(`globalThis.__sent = []; send = async req => { __sent.push(req.url); return { ok: true, id: 'stub' }; }; 'stubbed'`);
    const pos = id => inPage(`(() => { const r = document.getElementById('${id}').getBoundingClientRect(); return { x: Math.round(r.left + r.width / 2), y: Math.round(r.top + r.height / 2) }; })()`);
    const rightClick = async ({ x, y }) => { for (const type of ['mousePressed', 'mouseReleased']) await call('Input.dispatchMouseEvent', { type, x, y, button: 'right', buttons: 2, clickCount: 1 }, pageS); await sleep(300); };
    const tab = await inSw(`chrome.tabs.get(${tabId}).then(t => ({ id: t.id, url: t.url, title: t.title }))`);
    await inPage('window.scrollTo(0,0)');
    await rightClick(await pos('c'));
    const covered = await inPage(`(() => { const r = document.getElementById('c').getBoundingClientRect(); return document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2).id; })()`);
    const target = await inSw(`chrome.tabs.sendMessage(${tabId}, { type: 'neodm-context-target' })`);
    console.log('covered by:', covered, 'target:', JSON.stringify(target));
    check('right click under the overlay finds the blob video', covered === 'cover' && target.found && target.blob === true);
    await inSw(`__sent.length = 0; downloadFromMenu({ menuItemId: 'page-video', pageUrl: ${JSON.stringify(tab.url)} }, ${JSON.stringify(tab)})`); await sleep(500);
    check('the "page" menu entry downloads the file behind the pointed-at blob video (c), not the playing b', JSON.stringify(await inSw('__sent')) === JSON.stringify([origin + '/c.mp4']), JSON.stringify(await inSw('__sent')));
    await rightClick(await pos('a'));
    await inSw(`__sent.length = 0; downloadFromMenu({ menuItemId: 'media', mediaType: 'video', srcUrl: ${JSON.stringify(origin + '/a.mp4')}, pageUrl: ${JSON.stringify(tab.url)} }, ${JSON.stringify(tab)})`); await sleep(500);
    check('the "media" menu entry on video a downloads a', JSON.stringify(await inSw('__sent')) === JSON.stringify([origin + '/a.mp4']));
    const menus = await inSw(`new Promise(r => chrome.contextMenus.update('page-video', { visible: true }, () => r(chrome.runtime.lastError ? chrome.runtime.lastError.message : 'ok')))`);
    check("the 'page-video' menu exists in the browser", menus === 'ok', menus);
    const shown = await inSw(`syncMenus().then(() => menuShown)`); check('menu visibility follows the tab (media present -> shown)', shown === true);
    await inSw(`Promise.all([buildMenus(), buildMenus()]).then(() => 'ok')`); check('rebuilding the menus twice causes no error', true);
    const pageOnly = await call('Target.createTarget', { url: origin + '/nothing' }); await sleep(800);
    await call('Target.activateTarget', { targetId: pageOnly.targetId }); await sleep(800);
    const hidden = await inSw(`syncMenus().then(() => menuShown)`); check('menu entry hidden on an active tab without media', hidden === false);
  } catch (e) { console.log('ERROR', e.stack || e.message); results.push(false); }
  try { child.kill(); } catch {} server.close(); await sleep(500);
  try { fs.rmSync(profile, { recursive: true, force: true }); } catch {}
  console.log(results.every(Boolean) ? `ALL ${results.length} CHECKS PASSED` : `FAILED (${results.filter(x => !x).length} of ${results.length})`);
  process.exit(results.every(Boolean) ? 0 : 1);
})();
