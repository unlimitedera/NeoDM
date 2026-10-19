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
const FILES = { 1: 'f1', 2: 'f2', 3: 'f3', 4: 't1', 5: 't2', 6: 'f4', 7: 'f4', 8: 'f5', 9: 't3', 10: 'f6', 11: 'f1', 12: 'f2' };
if (!exe || !Object.values(FILES).every(f => fs.existsSync(path.join(dir, f + '.mp4')))) { console.log('SKIP: no Chromium browser or test videos in ' + dir); process.exit(0); }
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
const server = http.createServer((req, res) => {
  if (/^\/(feed|permalink)/.test(req.url)) { res.setHeader('Content-Type', 'text/html'); return res.end(req.url.startsWith('/feed') ? HTML : '<!doctype html><title>Permalink</title><p>A post page.</p>'); }
  const m = /^\/fbvid\/v(\d+)/.exec(req.url);
  if (!m || !FILES[+m[1]]) { res.statusCode = 404; return res.end(); }
  const file = path.join(dir, FILES[+m[1]] + '.mp4'), size = fs.statSync(file).size, r = /bytes=(\d+)-(\d*)/.exec(req.headers.range || '');
  res.setHeader('Content-Type', 'video/mp4'); res.setHeader('Accept-Ranges', 'bytes');
  if (r) { const s = +r[1], e = r[2] ? Math.min(+r[2], size - 1) : size - 1; res.statusCode = 206; res.setHeader('Content-Range', `bytes ${s}-${e}/${size}`); res.setHeader('Content-Length', e - s + 1); return fs.createReadStream(file, { start: s, end: e }).pipe(res); }
  res.setHeader('Content-Length', size); fs.createReadStream(file).pipe(res);
});
(async () => {
  await new Promise(r => server.listen(0, '127.0.0.1', r)); const origin = `http://127.0.0.1:${server.address().port}`;
  const profile = fs.mkdtempSync(path.join(process.env.TMPDIR || os.tmpdir(), 'neodm-feed152-'));
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
    const page = (await call('Target.createTarget', { url: origin + '/feed' })).targetId, pageS = await attach(page), inPage = e => evalIn(pageS, e);
    await call('Runtime.enable', {}, pageS); await call('Emulation.setDeviceMetricsOverride', { width: 1000, height: 700, deviceScaleFactor: 1, mobile: false }, pageS);
    await sleep(1500);
    let tabId = null; for (let i = 0; i < 20 && tabId == null; i++) { tabId = await inSw(`chrome.tabs.query({}).then(ts => (ts.find(t => t.url.startsWith(${JSON.stringify(origin)})) || {}).id ?? null)`); if (tabId == null) await sleep(300); }
    const state = () => inSw(`chrome.storage.session.get('neodm.media.${tabId}').then(r => r['neodm.media.${tabId}'] || null)`);
    const ctxs = () => cdpEvents.filter(e => e.method === 'Runtime.executionContextCreated' && e.sessionId === pageS && e.params.context.auxData?.type === 'isolated').map(e => e.params.context.id);
    let ext = null; for (let i = 0; i < 20 && !ext; i++) { for (const id of ctxs()) { try { if (await evalIn(pageS, `typeof __neodmRibbon`, id) === 'object') ext = id; } catch {} } if (!ext) await sleep(300); }
    check('content script world found', ext != null); const inExt = e => evalIn(pageS, e, ext);
    const titleOfCard = n => `Post ${n} by Someone`;
    const itemFor = (st, n) => (st?.items || []).filter(i => i.title === titleOfCard(n));

    // scroll down through the feed, one post at a time; blob players load when their post comes into view
    for (let n = 1; n <= N; n++) {
      await inPage(`window.scrollTo(0, ${(n - 1) * CARD}); ${BLOBS.includes(n) ? `loadBlob(${n}).then(() => true)` : 'true'}`); await sleep(BLOBS.includes(n) ? 1100 : 450);
      if (n === 6) { await inPage(`history.pushState({}, '', '/feed/story/6'); true`); }
      if (n === 9) { await inPage(`history.pushState({}, '', '/feed/story/9'); true`); }
    }
    await sleep(1500);
    let st = await poll(async () => { const s = await state(); return s && [...Array(N)].every((_, k) => itemFor(s, k + 1).length >= 1 && itemFor(s, k + 1).every(i => i.owner)) ? s : null; }, 15000);
    st = st || await state();
    console.log('items:', JSON.stringify((st?.items || []).map(i => ({ t: i.title, f: i.url.split('/').pop().slice(0, 28), owner: i.owner, size: i.size, dom: !!i.dom }))));
    check('every one of the 12 videos has an item of its own after scrolling down', [...Array(N)].every((_, k) => itemFor(st, k + 1).length === 1), JSON.stringify([...Array(N)].map((_, k) => itemFor(st, k + 1).length)));
    check('12 items, 12 different owners', st.items.length === N && new Set(st.items.map(i => i.owner)).size === N, String(st.items.length));
    check('the page address changed through pushState and nothing was dropped', st.page.endsWith('/feed/story/9'), st.page);
    const small = [4, 5, 9].map(n => itemFor(st, n)[0]);
    check('short clips (under 300 KB) are listed too: direct sources and a blob player (network)', small.every(i => i), JSON.stringify(small.map(i => i && [i.size, !!i.dom])));
    const visible = await inSw(`chrome.storage.session.get('neodm.media.${tabId}').then(r => visibleItems(r['neodm.media.${tabId}']))`);
    check('the list the popup and the page get has all 12', visible.length === N, String(visible.length));

    // scroll back up and down again: nothing disappears
    await inPage('window.scrollTo(0, 0)'); await sleep(700); await inPage(`window.scrollTo(0, ${(N - 1) * CARD})`); await sleep(700); await inPage('window.scrollTo(0, 0)'); await sleep(900);
    st = await state(); check('after scrolling down and back up all 12 are still there', st.items.length === N && [...Array(N)].every((_, k) => itemFor(st, k + 1).length === 1));

    // virtualization: posts leave the page and come back (same node, and a new node for the same blob video)
    const own = Object.fromEntries([...Array(N)].map((_, k) => [k + 1, itemFor(st, k + 1)[0]?.owner]));
    await inPage('removeCard(1); removeCard(2); removeCard(7); removeCard(12); true'); await sleep(1300);
    st = await state(); check('removed posts keep their files while their node is gone', st.items.length === N && [1, 2, 7, 12].every(n => st.els[own[n]]?.live === false));
    await inPage('restoreCard(12, false); restoreCard(7, true); restoreCard(2, true); restoreCard(1, false); true'); await sleep(1800);
    st = await state(); check('after they come back the list has 12 items and the visible ones are live again', st.items.length === N && [...Array(N)].every((_, k) => itemFor(st, k + 1).length === 1), JSON.stringify([...Array(N)].map((_, k) => itemFor(st, k + 1).length)));

    // the button on each video: position, click, what the worker would send
    await call('Page.bringToFront', {}, pageS); await call('Emulation.setFocusEmulationEnabled', { enabled: true }, pageS).catch(() => {});
    const rectOf = n => inPage(`(() => { const r = document.querySelector('#card${n} video').getBoundingClientRect(); return { x: r.left, y: r.top, w: r.width, h: r.height }; })()`);
    const move = (x, y) => call('Input.dispatchMouseEvent', { type: 'mouseMoved', x: Math.round(x), y: Math.round(y) }, pageS);
    const bars = async () => JSON.parse(await inExt('JSON.stringify(__neodmRibbon.bars())'));
    const clickAt = async (x, y) => { await call('Input.dispatchMouseEvent', { type: 'mouseMoved', x: Math.round(x), y: Math.round(y) }, pageS); for (const type of ['mousePressed', 'mouseReleased']) await call('Input.dispatchMouseEvent', { type: type, x: Math.round(x), y: Math.round(y), button: 'left', buttons: type === 'mousePressed' ? 1 : 0, clickCount: 1 }, pageS); };
    await inSw(`globalThis.__sent = []; send = async req => { __sent.push(JSON.parse(JSON.stringify(req))); return { ok: true, id: 'stub' }; }; 'stubbed'`);
    const tab = await inSw(`chrome.tabs.get(${tabId}).then(t => ({ id: t.id, url: t.url, title: t.title }))`);
    const strip = u => u.replace(/&?(bytestart|byteend)=\d+/g, '').replace(/\?&/, '?');
    const expectUrl = n => { const k = String(n); const base = '/fbvid/v' + n + '_' + 'A'.repeat(60); return n === 6 ? origin + '/fbvid/v6stream' : n === 12 ? null : null; };
    const sentFor = [];
    for (let n = 1; n <= N; n++) {
      await inPage(`window.scrollTo(0, ${(n - 1) * CARD}); true`); await sleep(500);
      const r = await rectOf(n); await move(r.x + r.w / 2, r.y + r.h / 2);
      const owner = (await state()).items.find(i => i.title === titleOfCard(n))?.owner;
      const b = await poll(async () => { await move(r.x + r.w / 2 + (Math.random() < .5 ? 1 : -1), r.y + r.h / 2); return (await bars()).find(x => x.key === owner && x.shown && x.dl); }, 4000, 200);
      if (n === 3) { const shot = process.env.NEODM_SHOT_FEED || path.join(root, 'artifacts/feed-button-152.png'); const png = await call('Page.captureScreenshot', { format: 'png' }, pageS); fs.writeFileSync(shot, Buffer.from(png.data, 'base64')); console.log('page screenshot:', shot); }
      if (!b) { check(`video ${n}: the button is shown over the video`, false, JSON.stringify(await inExt('JSON.stringify(__neodmRibbon.debug())')).slice(0, 900) + ' owner=' + owner); continue; }
      await inSw('__sent.length = 0; 1'); await clickAt(b.dl.x + b.dl.w / 2, b.dl.y + b.dl.h / 2); await poll(async () => (await inSw('__sent.length')) > 0, 3000, 100);
      const sent = await inSw('__sent'); sentFor[n] = sent[0];
      const want = (await state()).items.find(i => i.title === titleOfCard(n));
      const okUrl = sent.length === 1 && (sent[0].url === want.url || sent[0].url === want.rawUrl) && sent[0].url.length > 0;
      check(`video ${n}: the button sends this video (${n === 6 ? 'extensionless address' : n === 12 ? '600+ character address' : BLOBS.includes(n) ? 'blob player, its own sniffed file' : 'direct address'}), a title + .mp4 name, the type and the page as referer`,
        okUrl && sent[0].fileName === `${titleOfCard(n)}.mp4` && sent[0].mime === 'video/mp4' && sent[0].referer === tab.url, JSON.stringify({ url: (sent[0]?.url || '').slice(0, 70) + '...(' + (sent[0]?.url || '').length + ')', fileName: sent[0]?.fileName, mime: sent[0]?.mime, referer: sent[0]?.referer }));
      if (n === 12) check('the 600+ character address went out whole', (sent[0]?.url || '').length > 600, String((sent[0]?.url || '').length));
      if (!BLOBS.includes(n)) check(`video ${n}: the address is the one of its own element`, sent[0].url.includes('/fbvid/v' + n), sent[0].url.slice(0, 60));
      else check(`video ${n}: the address is the file its own player fetched (blob player ${n})`, sent[0].url.includes('/fbvid/v' + n + '/' + ({ 7: 'kilo', 8: 'lima', 9: 'mike' })[n] + '.mp4'), sent[0].url.slice(0, 60));
    }
    await move(990, 5); await sleep(700);
    check('pointer away from every video: all buttons hidden', (await bars()).every(b => !b.shown));

    // right click: direct source, overlay-covered video, blob video
    const menu = (info) => inSw(`__sent.length = 0; onMenuClick(${JSON.stringify({ ...info, pageUrl: tab.url })}, ${JSON.stringify(tab)}).then(() => __sent.slice())`);
    const rightClick = async (x, y) => { for (const type of ['mousePressed', 'mouseReleased']) await call('Input.dispatchMouseEvent', { type, x: Math.round(x), y: Math.round(y), button: 'right', buttons: 2, clickCount: 1 }, pageS); await sleep(300); };
    await inPage(`window.scrollTo(0, 0); true`); await sleep(400);
    const cur1 = await inPage(`document.querySelector('#card1 video').currentSrc`);
    let r = await rectOf(1); await rightClick(r.x + 100, r.y + 90);
    let got = await menu({ menuItemId: 'media', mediaType: 'video', srcUrl: cur1 });
    check('right click on a direct video: the real address, title + .mp4, type, referer', got.length === 1 && got[0].url.includes('/fbvid/v1_') && got[0].fileName === 'Post 1 by Someone.mp4' && got[0].mime === 'video/mp4' && got[0].referer === tab.url, JSON.stringify(got.map(x => [x.url.slice(0, 40), x.fileName, x.mime])));
    await inPage(`window.scrollTo(0, ${10 * CARD}); true`); await sleep(500);
    r = await rectOf(11); await rightClick(r.x + 100, r.y + 90);
    const tgt = await inSw(`chrome.tabs.sendMessage(${tabId}, { type: 'neodm-context-target' })`);
    check('overlay-covered video: the page finds the video under the pointer', tgt?.found === true && /^[A-Za-z0-9_-]+$/.test(tgt.key || '') && tgt.url.includes('/fbvid/v11_'), JSON.stringify(tgt).slice(0, 160));
    got = await menu({ menuItemId: 'page-video' });
    check('overlay-covered video: the page entry sends that video with its name', got.length === 1 && got[0].url.includes('/fbvid/v11_') && got[0].fileName === 'Post 11 by Someone.mp4', JSON.stringify(got.map(x => [x.url.slice(0, 40), x.fileName])));
    await inPage(`window.scrollTo(0, ${8 * CARD}); true`); await sleep(500);
    r = await rectOf(9); await rightClick(r.x + 100, r.y + 90);
    got = await menu({ menuItemId: 'media', mediaType: 'video', srcUrl: 'blob:' + origin + '/zzz' });
    check('right click on a blob video: its own sniffed file, never the blob address or the page', got.length === 1 && got[0].url.includes('/fbvid/v9/mike.mp4') && got[0].fileName === 'Post 9 by Someone.mp4' && got[0].mime === 'video/mp4', JSON.stringify(got.map(x => [x.url.slice(0, 50), x.fileName])));
    r = await rectOf(10); await inPage(`window.scrollTo(0, ${9 * CARD}); true`); await sleep(500); r = await rectOf(10); await rightClick(r.x + 100, r.y + 90);
    got = await menu({ menuItemId: 'link', linkUrl: origin + '/permalink/10' });
    check('the video inside a link: the link entry sends the video file, not the post page', got.length === 1 && got[0].url.includes('/fbvid/v10_') && got[0].fileName === 'Post 10 by Someone.mp4', JSON.stringify(got.map(x => [x.url.slice(0, 40), x.fileName])));

    // a real navigation starts a new list
    await inPage(`location.href = '/permalink/9'`); await sleep(1500);
    const after = await state(); check('a real navigation clears the list', after && after.items.length === 0, String(after?.items?.length));
  } catch (e) { console.log('ERROR', e.stack || e.message); results.push(false); }
  try { child.kill(); } catch {} server.close(); await sleep(500);
  try { fs.rmSync(profile, { recursive: true, force: true }); } catch {}
  console.log(results.every(Boolean) ? `ALL ${results.length} CHECKS PASSED` : `FAILED (${results.filter(x => !x).length} of ${results.length})`);
  process.exit(results.every(Boolean) ? 0 : 1);
})();
