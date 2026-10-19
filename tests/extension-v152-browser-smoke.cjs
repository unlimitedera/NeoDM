'use strict';
/* Real-browser smoke test for 1.5.2 (not part of `npm test`). Loads extension/ unpacked into an isolated profile of an installed Chrome/Edge and serves a local page that imitates an
   infinite-scroll feed: six <video> posts (three with a file URL, three played from a blob: URL), feed virtualization (nodes removed and added again while scrolling), history.pushState
   during the scroll, and one blob video inside a link to a page. It checks the per-video bars, the file-to-video links, the list across pushState and removal, and the right-click path
   (real right-click mouse events in the page, then the real menu handler with the native call stubbed, so nothing reaches the desktop app).
   Env: NEODM_BROWSER=<exe>, NEODM_VIDEOS=<dir with f1.mp4 ... f6.mp4>, NEODM_SHOT=<popup png>, NEODM_SHOT_PAGE=<page png>. */
const fs = require('node:fs'), path = require('node:path'), os = require('node:os'), http = require('node:http'), { spawn } = require('node:child_process');
const root = path.join(__dirname, '..'), EXT_ID = fs.readFileSync(path.join(root, 'tools/extension-id.txt'), 'utf8').trim();
const pf = [process.env.ProgramFiles, process.env['ProgramFiles(x86)'], process.env.LOCALAPPDATA].filter(Boolean);
const candidates = [process.env.NEODM_BROWSER, ...pf.flatMap(p => ['Google/Chrome/Application/chrome.exe', 'Microsoft/Edge/Application/msedge.exe'].map(x => path.join(p, x)))].filter(Boolean);
const exe = candidates.find(c => fs.existsSync(c));
const dir = process.env.NEODM_VIDEOS || path.join(__dirname, '../artifacts/tmp/smoke');
if (!exe || ![1, 2, 3, 4, 5, 6].every(n => fs.existsSync(path.join(dir, `f${n}.mp4`)))) { console.log('SKIP: no Chromium browser or no test videos f1..f6.mp4 in ' + dir); process.exit(0); }
const sleep = ms => new Promise(r => setTimeout(r, ms));
const NAMES = ['alpha', 'bravo', 'charlie', 'delta', 'echo', 'foxtrot'], nameOf = n => NAMES[n - 1] + '.mp4', CARD = 520, HTML = `<!doctype html><title>Feed Lab</title><style>body{margin:0;font:14px sans-serif}.card{height:${CARD}px;box-sizing:border-box;padding:10px 20px;border-bottom:1px solid #ccc}h3{margin:0 0 8px}video{width:320px;height:180px;background:#000;display:block}</style>
<div id="feed"></div>
<script>
const names = ['alpha', 'bravo', 'charlie', 'delta', 'echo', 'foxtrot'], direct = n => n <= 3, cards = {}, blobUrls = {};
function make(n, fresh) {
  const d = document.createElement('div'); d.className = 'card'; d.id = 'card' + n; d.setAttribute('role', 'article');
  d.innerHTML = '<h3>Post ' + n + ' by Someone</h3>';
  const v = document.createElement('video'); v.id = 'v' + n + (fresh ? 'b' : ''); v.muted = true; v.controls = true; v.preload = 'auto';
  if (direct(n)) v.src = '/media/' + names[n - 1] + '.mp4'; else if (blobUrls[n]) v.src = blobUrls[n];
  if (n === 5) { const a = document.createElement('a'); a.id = 'wrap5'; a.href = '/permalink/5'; a.append(v); d.append(a); } else d.append(v);
  return d;
}
for (let n = 1; n <= 6; n++) { cards[n] = make(n); document.getElementById('feed').append(cards[n]); }
async function loadBlob(n) { const r = await fetch('/media/' + names[n - 1] + '.mp4'); const b = await r.blob(); blobUrls[n] = URL.createObjectURL(b); document.getElementById('card' + n).querySelector('video').src = blobUrls[n]; }
function removeCard(n) { cards[n].remove(); }
function restoreCard(n, fresh) { if (fresh) cards[n] = make(n, true); const before = document.getElementById('card' + (n + 1)); document.getElementById('feed').insertBefore(cards[n], before); }
</script>`;
const server = http.createServer((req, res) => {
  if (/^\/(feed|permalink)/.test(req.url)) { res.setHeader('Content-Type', 'text/html'); return res.end(req.url.startsWith('/feed') ? HTML : '<!doctype html><title>Permalink</title><p>A post page.</p>'); }
  const m = /^\/media\/(alpha|bravo|charlie|delta|echo|foxtrot)\.mp4$/.exec(req.url);
  if (!m) { res.statusCode = 404; return res.end(); }
  const file = path.join(dir, `f${NAMES.indexOf(m[1]) + 1}.mp4`), size = fs.statSync(file).size, r = /bytes=(\d+)-(\d*)/.exec(req.headers.range || '');
  res.setHeader('Content-Type', 'video/mp4'); res.setHeader('Accept-Ranges', 'bytes');
  if (r) { const s = +r[1], e = r[2] ? +r[2] : size - 1; res.statusCode = 206; res.setHeader('Content-Range', `bytes ${s}-${e}/${size}`); res.setHeader('Content-Length', e - s + 1); return fs.createReadStream(file, { start: s, end: e }).pipe(res); }
  res.setHeader('Content-Length', size); fs.createReadStream(file).pipe(res);
});
(async () => {
  await new Promise(r => server.listen(0, '127.0.0.1', r)); const origin = `http://127.0.0.1:${server.address().port}`;
  const profile = fs.mkdtempSync(path.join(process.env.TMPDIR || os.tmpdir(), 'neodm-smoke152-'));
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
    const fileOf = i => i.url.split('/').pop();
    // infinite scroll: the blob videos load one after another as the posts come into view
    for (const n of [4, 5, 6]) { await inPage(`window.scrollTo(0, ${(n - 1) * CARD}); loadBlob(${n}).then(() => true)`); await sleep(900); }
    let st = await poll(async () => { const s = await state(); return s && s.items.length >= 6 && s.items.every(i => i.owner) ? s : null; }, 15000);
    console.log('items:', JSON.stringify((st?.items || []).map(i => ({ f: fileOf(i), owner: i.owner, title: i.title, size: i.size, dom: !!i.dom, ambiguous: !!i.ambiguous }))));
    check('six files, every one linked to an element', !!st && st.items.length === 6 && st.items.every(i => i.owner) && new Set(st.items.map(i => i.owner)).size === 6);
    const byFile = n => (st?.items || []).find(i => fileOf(i) === nameOf(n));
    check('each file carries the title of its own post (direct and blob videos)', [1, 2, 3, 4, 5, 6].every(n => byFile(n)?.title === `Post ${n} by Someone`), JSON.stringify([1, 2, 3, 4, 5, 6].map(n => byFile(n)?.title)));
    const sizes = (st?.items || []).map(i => i.size).filter(Boolean);
    check('the sizes differ by more than 10x and all stay listed (one origin)', sizes.length === 6 && Math.max(...sizes) > 10 * Math.min(...sizes), JSON.stringify(sizes));
    const listed = await inSw(`chrome.storage.session.get('neodm.media.${tabId}').then(r => visibleItems(r['neodm.media.${tabId}']))`);
    check('visible list has six rows', listed.length === 6);

    // bars: one per video, in the top corner, found by hit testing; moving the pointer away hides it again
    const rectOf = n => inPage(`(() => { const r = document.querySelector('#card${n} video').getBoundingClientRect(); return { x: r.left, y: r.top, w: r.width, h: r.height }; })()`);
    const move = (x, y) => call('Input.dispatchMouseEvent', { type: 'mouseMoved', x: Math.round(x), y: Math.round(y) }, pageS);
    const bars = async () => JSON.parse(await inExt('JSON.stringify(__neodmRibbon.bars())'));
    const barFor = async n => (await bars()).find(x => x.id && x.id.endsWith('/' + nameOf(n)));
    const parse = tr => { const m = /translate\(([-\d.]+)px,\s*([-\d.]+)px\)/.exec(tr || ''); return m ? { x: +m[1], y: +m[2] } : null; };
    const hit = (x, y) => inPage(`(() => { const e = document.elementFromPoint(${x}, ${y}); return e ? (e.hasAttribute('data-neodm-bars') ? 'NEODM-BAR' : e.tagName) : null; })()`);
    for (const n of [1, 2, 3, 4, 5, 6]) {
      await inPage(`window.scrollTo(0, ${(n - 1) * CARD})`); await sleep(300);
      const r = await rectOf(n); await move(r.x + r.w / 2, r.y + r.h / 2);
      const ok = await poll(async () => { const b = await barFor(n); return b && b.shown ? b : null; }, 4000, 150);
      const p = parse(ok?.transform);
      const onBar = p ? await hit(p.x + 14, p.y + 12) : null, onVideo = await hit(r.x + r.w / 2, r.y + r.h / 2);
      check(`video ${n}: bar shown in the top-left corner (${n <= 3 ? 'file URL' : 'blob'}${n === 5 ? ', inside a link' : ''})`, !!ok && p && Math.abs(p.x - (r.x + 8)) <= 1 && Math.abs(p.y - (r.y + 8)) <= 1, JSON.stringify({ rect: r, bar: ok?.transform }));
      check(`video ${n}: the bar takes the click at its own place, the rest of the video is still the video`, onBar === 'NEODM-BAR' && onVideo === 'VIDEO', `${onBar} / ${onVideo}`);
      if (n === 3) { const shot = process.env.NEODM_SHOT_PAGE || path.join(root, 'artifacts/bar-smoke-152.png'); const png = await call('Page.captureScreenshot', { format: 'png' }, pageS); fs.writeFileSync(shot, Buffer.from(png.data, 'base64')); console.log('page screenshot:', shot); }
    }
    await move(990, 5); await sleep(800);
    check('pointer away from every video: all bars hidden', (await bars()).every(b => !b.shown));
    // the bar follows the scroll
    await inPage('window.scrollTo(0, 0)'); await sleep(300);
    let r1 = await rectOf(1); await move(r1.x + r1.w / 2, r1.y + 60); await poll(async () => (await barFor(1))?.shown, 3000, 100);
    const y0 = parse((await barFor(1)).transform).y;
    await inPage('window.scrollTo(0, 30)'); await sleep(200); r1 = await rectOf(1); await move(r1.x + r1.w / 2, r1.y + 60); await sleep(500);
    const y1 = parse((await barFor(1)).transform).y;
    check('bar moves with the video while scrolling (30 px)', y0 - y1 === 30, `${y0} -> ${y1}`);
    await inPage('window.scrollTo(0, 100)'); await sleep(200); r1 = await rectOf(1); await move(r1.x + r1.w / 2, r1.y + 60); await sleep(500);
    const y2 = parse((await barFor(1)).transform).y; check('video partly above the screen: the bar stays at the top of the visible part', y2 === 8, String(y2));
    // a playing video shows its bar without hover
    await inPage('window.scrollTo(0, 0)'); await move(990, 5); await inPage(`document.getElementById('v1').play().then(() => true)`); await sleep(900);
    const playingBars = (await bars()).filter(b => b.shown).map(b => b.id.split('/').pop()); check('a playing video shows its bar without hover', playingBars.includes('alpha.mp4'), JSON.stringify(playingBars));
    await inPage(`document.getElementById('v1').pause()`);

    // pushState during the scroll, then scrolling back
    const before = (await state()).items.map(i => i.id + '>' + i.owner).sort();
    for (const n of [1, 2, 3]) { await inPage(`history.pushState({}, '', '/feed/story/${n}'); window.scrollTo(0, ${n * 200}); true`); await sleep(400); }
    await sleep(600);
    st = await state();
    check('items and owners survive history.pushState during scrolling', JSON.stringify(st.items.map(i => i.id + '>' + i.owner).sort()) === JSON.stringify(before) && st.page.endsWith('/feed/story/3'), st.page);
    await inPage('window.scrollTo(0, 3000)'); await sleep(400); await inPage('window.scrollTo(0, 0)'); await sleep(600);
    st = await state(); check('scrolling down and back leaves all six files in the list', st.items.length === 6 && (await inSw(`chrome.storage.session.get('neodm.media.${tabId}').then(r => visibleItems(r['neodm.media.${tabId}']))`)).length === 6);

    // virtualization: nodes leave and come back
    const key5 = byFile(5).owner, key2 = byFile(2).owner;
    await inPage('removeCard(2); removeCard(5); true'); await sleep(1200);
    st = await state();
    check('removed elements keep their files (still six), marked as not on the page', st.items.length === 6 && st.els[key2]?.live === false && st.els[key5]?.live === false && (await bars()).every(b => b.key !== key2 && b.key !== key5), JSON.stringify([st.els[key2]?.live, st.els[key5]?.live]));
    await inPage('restoreCard(2, false); restoreCard(5, true); true'); await sleep(1500);
    await inPage(`window.scrollTo(0, ${CARD}); true`); await sleep(500);
    st = await state(); const f2 = st.items.find(i => fileOf(i) === nameOf(2)), f5 = st.items.find(i => fileOf(i) === nameOf(5));
    console.log('after restore:', JSON.stringify({ f2: f2?.owner, key2, f5: f5?.owner, key5, els: Object.entries(st.els).map(([k, e]) => [k, e.live, e.title]) }));
    check('the same node comes back: same owner, live again', f2?.owner === key2 && st.els[key2]?.live === true);
    check('a new node for the same blob video takes the file over (old element forgotten)', !!f5 && f5.owner !== key5 && !st.els[key5] && st.els[f5.owner]?.live === true && st.items.length === 6, JSON.stringify({ owner: f5?.owner }));
    await inPage(`window.scrollTo(0, ${4 * CARD})`); await sleep(300);
    const rr5 = await rectOf(5); await move(rr5.x + rr5.w / 2, rr5.y + rr5.h / 2);
    const b5 = await poll(async () => { const b = await barFor(5); return b && b.shown ? b : null; }, 4000, 150);
    check('the restored blob video gets its bar again', !!b5, JSON.stringify(b5));

    // right click on the blob video that sits inside a link
    await inSw(`globalThis.__sent = []; send = async req => { __sent.push(req.url); return { ok: true, id: 'stub' }; }; 'stubbed'`);
    const tab = await inSw(`chrome.tabs.get(${tabId}).then(t => ({ id: t.id, url: t.url, title: t.title }))`);
    const rightClick = async (x, y) => { for (const type of ['mousePressed', 'mouseReleased']) await call('Input.dispatchMouseEvent', { type, x: Math.round(x), y: Math.round(y), button: 'right', buttons: 2, clickCount: 1 }, pageS); await sleep(300); };
    await rightClick(rr5.x + rr5.w / 2, rr5.y + rr5.h / 2 + 40);
    const target = await inSw(`chrome.tabs.sendMessage(${tabId}, { type: 'neodm-context-target' })`);
    console.log('context target:', JSON.stringify(target));
    check('the page finds the video under the pointer although a link wraps it', target?.found === true && target.blob === true && target.key === f5?.owner);
    const linkUrl = origin + '/permalink/5';
    await inSw(`__sent.length = 0; onMenuClick({ menuItemId: 'link', linkUrl: ${JSON.stringify(linkUrl)}, pageUrl: ${JSON.stringify(tab.url)} }, ${JSON.stringify(tab)}).then(() => 1)`); await sleep(500);
    const sent1 = await inSw('__sent'); const status1 = (await inSw(`chrome.storage.local.get('status')`)).status;
    check('"Download link" on the wrapped video sends the video file, not the link', JSON.stringify(sent1) === JSON.stringify([origin + '/media/echo.mp4']), JSON.stringify(sent1) + ' ' + status1);
    await inSw(`__sent.length = 0; onMenuClick({ menuItemId: 'media', mediaType: 'video', srcUrl: 'blob:${origin}/zzz', pageUrl: ${JSON.stringify(tab.url)} }, ${JSON.stringify(tab)}).then(() => 1)`); await sleep(500);
    check('"Download media" on the blob video uses the owned file', JSON.stringify(await inSw('__sent')) === JSON.stringify([origin + '/media/echo.mp4']));
    await rightClick(900, 600); // nothing there
    await inSw(`__sent.length = 0; onMenuClick({ menuItemId: 'link', linkUrl: ${JSON.stringify(linkUrl)}, pageUrl: ${JSON.stringify(tab.url)} }, ${JSON.stringify(tab)}).then(() => 1)`); await sleep(500);
    const status2 = (await inSw(`chrome.storage.local.get('status')`)).status;
    check('a link to a page with no video under the pointer sends nothing and says so', (await inSw('__sent')).length === 0 && status2 === 'This link is a web page, not a file.', status2);

    // popup
    const pop = (await call('Target.createTarget', { url: `chrome-extension://${EXT_ID}/popup.html`, background: true })).targetId, popS = await attach(pop), inPop = e => evalIn(popS, e);
    await sleep(1500);
    const rows = await inPop(`[...document.querySelectorAll('#mediaList li.media')].map(li => ({ title: li.querySelector('.title').textContent, meta: li.querySelector('.meta').textContent, where: !!li.querySelector('button.link') }))`);
    console.log('popup rows:', JSON.stringify(rows));
    check('popup lists six rows with the post titles and a "Show on page" button', rows.length === 6 && new Set(rows.map(r => r.title)).size === 6 && rows.every(r => r.where && /^Post \d by Someone$/.test(r.title)));
    const shot = process.env.NEODM_SHOT || path.join(root, 'artifacts/popup-smoke-152.png');
    try {
      await call('Page.bringToFront', {}, popS); await call('Emulation.setDeviceMetricsOverride', { width: 420, height: 800, deviceScaleFactor: 1, mobile: false }, popS);
      await sleep(500); const png = await call('Page.captureScreenshot', { format: 'png' }, popS); fs.writeFileSync(shot, Buffer.from(png.data, 'base64')); console.log('popup screenshot:', shot);
    } catch (e) { check('popup screenshot taken', false, e.message); }
    // a real navigation starts a new list
    await inPage(`location.href = '/permalink/9'`); await sleep(1500);
    const after = await state(); check('a real navigation clears the list', after && after.items.length === 0, String(after?.items?.length));
  } catch (e) { console.log('ERROR', e.stack || e.message); results.push(false); }
  try { child.kill(); } catch {} server.close(); await sleep(500);
  try { fs.rmSync(profile, { recursive: true, force: true }); } catch {}
  console.log(results.every(Boolean) ? `ALL ${results.length} CHECKS PASSED` : `FAILED (${results.filter(x => !x).length} of ${results.length})`);
  process.exit(results.every(Boolean) ? 0 : 1);
})();
