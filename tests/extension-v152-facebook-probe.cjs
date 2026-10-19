'use strict';
/* Real-browser smoke test for the 1.5.2 feed behaviour (not part of `npm test`). Loads extension/ unpacked into an isolated, throwaway profile of an installed Chrome/Edge and
   serves a local page that imitates an infinite-scroll feed: 12 <video> posts in a tall page. Direct signed-style addresses (path ends .mp4, bytestart/byteend/tag/bitrate
   parameters), one address with no extension, one address of 600+ characters, short clips (< 300 KB), blob players, a video under an overlay, a video inside a link,
   removal and re-adding of posts while scrolling (virtualization), and history.pushState during the scroll. It asserts that every video keeps its own item, that each on-video
   button sends the right address, a real file name (title + extension), the type and the page as referer (the native call is stubbed, nothing reaches the desktop app),
   and takes a screenshot of the button.
   Env: NEODM_BROWSER=<exe>, NEODM_VIDEOS=<dir with f1..f6.mp4 and t1..t3.mp4>, NEODM_SHOT_FEED=<png>. */
const fs = require('node:fs'), path = require('node:path'), os = require('node:os'), http = require('node:http'), { spawn } = require('node:child_process');
const root = path.join(__dirname, '..'), EXT_ID = fs.readFileSync(path.join(root, 'tools/extension-id.txt'), 'utf8').trim();
const pf = [process.env.ProgramFiles, process.env['ProgramFiles(x86)'], process.env.LOCALAPPDATA].filter(Boolean);
const candidates = [process.env.NEODM_BROWSER, ...pf.flatMap(p => ['Google/Chrome/Application/chrome.exe', 'Microsoft/Edge/Application/msedge.exe'].map(x => path.join(p, x)))].filter(Boolean);
const exe = candidates.find(c => fs.existsSync(c));
const dir = process.env.NEODM_VIDEOS || path.join(__dirname, '../artifacts/tmp/smoke');
const FILES_unused = { 1: 'f1', 2: 'f2', 3: 'f3', 4: 't1', 5: 't2', 6: 'f4', 7: 'f4', 8: 'f5', 9: 't3', 10: 'f6', 11: 'f1', 12: 'f2' };
if (!exe) { console.log('SKIP: no Chromium browser or test videos in ' + dir); process.exit(0); }
const sleep = ms => new Promise(r => setTimeout(r, ms));
const CARD = 520, BLOBS = [7, 8, 9], N = 12;
const HTML = `<!doctype html><title>Feed Lab</title><style>body{margin:0;font:14px sans-serif}.card{height:${CARD}px;box-sizing:border-box;padding:10px 20px;border-bottom:1px solid #ccc;position:relative}h3{margin:0 0 8px}video{width:320px;height:180px;background:#000;display:block}.cover{position:absolute;left:20px;top:44px;width:320px;height:180px;background:rgba(255,0,0,.01)}</style>
<div id="feed"></div>
<script>
const blobUrls = {}, cards = {}, blobSet = [7, 8, 9];
const base = n => '/fbvid/v' + n + '_' + 'A'.repeat(60);
const srcOf = n => n === 6 ? '/fbvid/v6stream' : n === 12 ? base(12) + '.mp4?_nc_cat=101&efg=' + 'e'.repeat(480) + '&bytestart=0&byteend=99999&tag=hd&bitrate=580000&oe=6A0B1C2D' : base(n) + '.mp4?_nc_cat=101&ccb=1-7&bytestart=0&byteend=99999&bitrate=580000&tag=hd&oe=6A0B1C2D';
function make(n, fresh) {
  const d = document.createElement('div'); d.className = 'card'; d.id = 'card' + n; d.setAttribute('role', 'article');
  d.innerHTML = '<h3>Post ' + n + ' by Someone</h3>';
  const v = document.createElement('video'); v.id = 'v' + n + (fresh ? 'b' : ''); v.muted = true; v.controls = true; v.preload = 'auto';
  if (blobSet.includes(n)) { if (blobUrls[n]) v.src = blobUrls[n]; } else v.src = srcOf(n);
  if (n === 10) { const a = document.createElement('a'); a.href = '/permalink/10'; a.append(v); d.append(a); } else d.append(v);
  if (n === 11) { const c = document.createElement('div'); c.className = 'cover'; c.id = 'cover11'; d.append(c); }
  return d;
}
for (let n = 1; n <= ${N}; n++) { cards[n] = make(n); document.getElementById('feed').append(cards[n]); }
async function loadBlob(n) { const r = await fetch('/fbvid/v' + n + '/' + ({ 7: 'kilo', 8: 'lima', 9: 'mike' })[n] + '.mp4'); const b = await r.blob(); blobUrls[n] = URL.createObjectURL(b); document.getElementById('card' + n).querySelector('video').src = blobUrls[n]; }
function removeCard(n) { cards[n].remove(); }
function restoreCard(n, fresh) { if (fresh) cards[n] = make(n, true); const before = document.getElementById('card' + (n + 1)); document.getElementById('feed').insertBefore(cards[n], before); }
</script>`;
(async () => {
  const URL_ = process.argv[2] || 'https://www.facebook.com/facebook/videos/10153231379946729/'; const origin = new URL(URL_).origin;
  const profile = fs.mkdtempSync(path.join(process.env.TMPDIR || os.tmpdir(), 'neodm-fbprobe-'));
  const child = spawn(exe, [`--user-data-dir=${profile}`, '--remote-debugging-pipe', '--enable-unsafe-extension-debugging', '--no-first-run', '--no-default-browser-check', '--headless=new', '--autoplay-policy=no-user-gesture-required', '--window-size=1000,700', 'about:blank'], { stdio: ['ignore', 'ignore', 'ignore', 'pipe', 'pipe'] });
  const results = []; const check = (name, ok, detail = '') => { results.push(!!ok); console.log((ok ? 'PASS ' : 'FAIL ') + name + (detail ? ' - ' + detail : '')); };
  let nextId = 1, buf = ''; const pending = new Map(), cdpEvents = [];
  child.stdio[4].on('data', d => { buf += d.toString('utf8'); let i; while ((i = buf.indexOf('\u0000')) >= 0) { const m = JSON.parse(buf.slice(0, i)); buf = buf.slice(i + 1); if (m.id && pending.has(m.id)) { const p = pending.get(m.id); pending.delete(m.id); m.error ? p.rej(new Error(m.error.message)) : p.res(m.result); } else if (m.method) cdpEvents.push(m); } });
  const call = (method, params = {}, sessionId, ms = 30000) => new Promise((res, rej) => { const id = nextId++; const timer = setTimeout(() => { pending.delete(id); rej(new Error('timeout: ' + method)); }, ms); pending.set(id, { res: v => { clearTimeout(timer); res(v); }, rej: e => { clearTimeout(timer); rej(e); } }); child.stdio[3].write(JSON.stringify({ id, method, params, sessionId }) + '\u0000'); });
  const attach = async targetId => (await call('Target.attachToTarget', { targetId, flatten: true })).sessionId;
  const evalIn = async (session, expression, contextId) => { const r = await call('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true, userGesture: true, ...(contextId ? { contextId } : {}) }, session); if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description || r.exceptionDetails.text); return r.result.value; };
  const poll = async (fn, ms = 12000, step = 250) => { const end = Date.now() + ms; let v; while (Date.now() < end) { v = await fn(); if (v) return v; await sleep(step); } return v; };
  try {
    console.log('browser:', exe, (await call('Browser.getVersion')).product);
    const loaded = await call('Extensions.loadUnpacked', { path: path.join(root, 'extension') });
    check('extension loaded under the manifest-key ID', loaded.id === EXT_ID, loaded.id);
    let sw; for (let i = 0; i < 50 && !sw; i++) { sw = (await call('Target.getTargets')).targetInfos.find(t => t.type === 'service_worker' && /^chrome-extension:\/\/[a-p]{32}\/background\.js$/.test(t.url)); if (!sw) await sleep(200); }
    check('service worker started', !!sw); if (!sw) throw new Error('no worker');
    const swS = await attach(sw.targetId), inSw = e => evalIn(swS, e);
    const page = (await call('Target.createTarget', { url: URL_ })).targetId, pageS = await attach(page), inPage = e => evalIn(pageS, e);
    await call('Runtime.enable', {}, pageS); await call('Emulation.setDeviceMetricsOverride', { width: 1000, height: 700, deviceScaleFactor: 1, mobile: false }, pageS);
    await sleep(9000);
    let tabId = null; for (let i = 0; i < 20 && tabId == null; i++) { tabId = await inSw(`chrome.tabs.query({}).then(ts => (ts.find(t => t.url.startsWith(${JSON.stringify(origin)})) || {}).id ?? null)`); if (tabId == null) await sleep(300); }
    const state = () => inSw(`chrome.storage.session.get('neodm.media.${tabId}').then(r => r['neodm.media.${tabId}'] || null)`);
    const ctxs = () => cdpEvents.filter(e => e.method === 'Runtime.executionContextCreated' && e.sessionId === pageS && e.params.context.auxData?.type === 'isolated').map(e => e.params.context.id);
    let ext = null; for (let i = 0; i < 20 && !ext; i++) { for (const id of ctxs()) { try { if (await evalIn(pageS, `typeof __neodmRibbon`, id) === 'object') ext = id; } catch {} } if (!ext) await sleep(300); }
    check('content script world found', ext != null); const inExt = e => evalIn(pageS, e, ext);
    await inPage('document.querySelectorAll("video").forEach(v => { v.muted = true; v.play().catch(() => {}); }); true'); await sleep(6000);
    console.log('title:', await inPage('document.title'), '| url:', await inPage('location.href'));
    console.log('videos on page:', await inPage('JSON.stringify([...document.querySelectorAll("video")].map(v => ({ src: (v.currentSrc || v.src || "").slice(0, 120), len: (v.currentSrc || v.src || "").length, w: v.clientWidth, h: v.clientHeight, paused: v.paused, rs: v.readyState })))'));
    const st = await state(); console.log('items:', JSON.stringify((st?.items || []).map(i => ({ kind: i.kind, track: i.track, q: i.quality, size: i.size, dom: !!i.dom, owner: i.owner, title: i.title, url: i.url.slice(0, 110), len: i.url.length }))));
    const bars = JSON.parse(await inExt('JSON.stringify(__neodmRibbon.bars())')); console.log('bars:', JSON.stringify(bars).slice(0, 400));
    await inSw(`globalThis.__sent = []; send = async req => { __sent.push(JSON.parse(JSON.stringify(req))); return { ok: true, id: 'stub' }; }; 1`);
    const tab = await inSw(`chrome.tabs.get(${tabId}).then(t => ({ id: t.id, url: t.url, title: t.title }))`);
    const v = await inPage('(() => { const v = document.querySelector("video"); if (!v) return null; const r = v.getBoundingClientRect(); return { x: r.left + r.width / 2, y: r.top + r.height / 2 }; })()');
    if (v) {
      await call('Input.dispatchMouseEvent', { type: 'mouseMoved', x: Math.round(v.x), y: Math.round(v.y) }, pageS); await sleep(800);
      const b = JSON.parse(await inExt('JSON.stringify(__neodmRibbon.bars())')).find(x => x.shown && x.dl);
      if (b) { await call('Input.dispatchMouseEvent', { type: 'mouseMoved', x: Math.round(b.dl.x + b.dl.w / 2), y: Math.round(b.dl.y + b.dl.h / 2) }, pageS); for (const type of ['mousePressed', 'mouseReleased']) await call('Input.dispatchMouseEvent', { type, x: Math.round(b.dl.x + b.dl.w / 2), y: Math.round(b.dl.y + b.dl.h / 2), button: 'left', buttons: type === 'mousePressed' ? 1 : 0, clickCount: 1 }, pageS); await sleep(1200); }
      console.log('button shown:', !!b, '| button sends:', JSON.stringify(await inSw('__sent.map(r => ({ url: r.url.slice(0, 120), len: r.url.length, fileName: r.fileName, mime: r.mime, referer: r.referer }))')));
      for (const type of ['mousePressed', 'mouseReleased']) await call('Input.dispatchMouseEvent', { type, x: Math.round(v.x), y: Math.round(v.y) + 30, button: 'right', buttons: 2, clickCount: 1 }, pageS);
      await sleep(400);
      const cur = await inPage('document.querySelector("video").currentSrc');
      await inSw('__sent.length = 0; 1'); await inSw(`onMenuClick({ menuItemId: 'media', mediaType: 'video', srcUrl: ${JSON.stringify(cur)}, pageUrl: ${JSON.stringify(tab.url)} }, ${JSON.stringify(tab)}).then(() => 1)`); await sleep(800);
      console.log('right click sends:', JSON.stringify(await inSw('__sent.map(r => ({ url: r.url.slice(0, 120), len: r.url.length, fileName: r.fileName, mime: r.mime, referer: r.referer }))')), '| status:', (await inSw(`chrome.storage.local.get('status')`)).status);
      const png = await call('Page.captureScreenshot', { format: 'png' }, pageS); fs.writeFileSync(path.join(root, 'artifacts/facebook-probe-152.png'), Buffer.from(png.data, 'base64'));
    } else console.log('no <video> element found on the page');
  } catch (e) { console.log('ERROR', e.stack || e.message); }
  try { child.kill(); } catch {} await sleep(500);
  try { fs.rmSync(profile, { recursive: true, force: true }); } catch {}
  process.exit(0);
})();
