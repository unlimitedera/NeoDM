'use strict';
/* Real-browser test for the 1.5.8 on-video button and the page-world hooks (not part of `npm test`).
   Local pages imitate real players; nothing leaves this computer and no third-party site is visited:
   1 direct mp4 in the top frame, 2 the same video in a cross-origin iframe / sandboxed iframe / srcdoc iframe, 3 a feed with ONE recycled <video>,
   4 a MediaSource player that fetches fragmented-MP4 tracks in ranges (video and audio pair, then one muxed track), 5 an HLS player (playlist by XHR, segments by fetch),
   6 a player whose <video> sits under a canvas / is display:none inside a box, 7 a page that freezes the prototypes before the hooks load.
   Loads extension/ unpacked into a throwaway profile (never the user's profile); the native host is replaced inside the service worker (send() is overridden).
   Env: NEODM_BROWSER=<exe>, NEODM_SHOT_DIR=<dir for the screenshots>. */
const fs = require('node:fs'), path = require('node:path'), os = require('node:os'), http = require('node:http'), { spawn, spawnSync } = require('node:child_process');
const root = path.join(__dirname, '..'), EXT_ID = fs.readFileSync(path.join(root, 'tools/extension-id.txt'), 'utf8').trim();
const pf = [process.env.ProgramFiles, process.env['ProgramFiles(x86)'], process.env.LOCALAPPDATA].filter(Boolean);
const exe = [process.env.NEODM_BROWSER, ...pf.flatMap(p => ['Google/Chrome/Application/chrome.exe', 'Microsoft/Edge/Application/msedge.exe'].map(x => path.join(p, x)))].filter(Boolean).find(c => fs.existsSync(c));
if (!exe) { console.log('SKIP: no Chromium browser found'); process.exit(0); }
const only = (process.env.ONLY || '').split(',').filter(Boolean), want = n => !only.length || only.includes(String(n));
const sleep = ms => new Promise(r => setTimeout(r, ms));
const shotDir = process.env.NEODM_SHOT_DIR || path.join(root, 'artifacts');
const media = process.env.NEODM_MEDIA_DIR || path.join(root, 'artifacts/tmp/smoke158');
fs.mkdirSync(media, { recursive: true });

/* ---- test media made with ffmpeg (once) ---- */
function ff(args) { const r = spawnSync('ffmpeg', ['-hide_banner', '-loglevel', 'error', '-y', ...args], { cwd: media, encoding: 'utf8' }); if (r.status !== 0) throw new Error('ffmpeg ' + args.join(' ') + ': ' + (r.stderr || r.error)); }
const sources = ['testsrc2=size=640x360:rate=25', 'smptebars=size=640x360:rate=25', 'rgbtestsrc=size=640x360:rate=25', 'mandelbrot=size=640x360:rate=25'];
const enc = ['-c:v', 'libx264', '-profile:v', 'baseline', '-level', '3.0', '-pix_fmt', 'yuv420p', '-b:v', '600k', '-g', '25', '-c:a', 'aac', '-b:a', '64k'];
sources.forEach((src, i) => { const f = `clip${i + 1}.mp4`; if (!fs.existsSync(path.join(media, f))) ff(['-f', 'lavfi', '-i', src, '-f', 'lavfi', '-i', `sine=frequency=${300 + i * 150}:sample_rate=44100`, '-t', '40', ...enc, '-movflags', '+faststart', f]); });
const frag = ['-movflags', 'frag_keyframe+empty_moov+default_base_moof'];
if (!fs.existsSync(path.join(media, 'mse_v.mp4'))) ff(['-i', 'clip1.mp4', '-an', '-c:v', 'copy', ...frag, 'mse_v.mp4']);
if (!fs.existsSync(path.join(media, 'mse_a.mp4'))) ff(['-i', 'clip1.mp4', '-vn', '-c:a', 'copy', ...frag, 'mse_a.mp4']);
if (!fs.existsSync(path.join(media, 'mse_mux.mp4'))) ff(['-i', 'clip2.mp4', '-c', 'copy', ...frag, 'mse_mux.mp4']);
if (!fs.existsSync(path.join(media, 'mse_hd.mp4'))) ff(['-i', 'clip4.mp4', '-c', 'copy', ...frag, 'mse_hd.mp4']);
if (!fs.existsSync(path.join(media, 'hls/master.m3u8'))) { fs.mkdirSync(path.join(media, 'hls'), { recursive: true }); ff(['-i', 'clip3.mp4', '-c', 'copy', '-f', 'hls', '-hls_time', '4', '-hls_segment_type', 'fmp4', '-hls_playlist_type', 'vod', '-hls_fmp4_init_filename', 'init.mp4', '-hls_segment_filename', 'hls/seg%d.m4s', '-master_pl_name', 'master.m3u8', '-var_stream_map', 'v:0,a:0', 'hls/out_%v.m3u8']); }

/* ---- the local sites: a.localhost and b.localhost are different origins on different ports ---- */
const results = []; const check = (name, ok, detail = '') => { results.push(!!ok); console.log((ok ? 'PASS ' : 'FAIL ') + name + (detail ? ' - ' + detail : '')); };
const VIDEO_ATTR = 'muted autoplay loop playsinline width="640" height="360" style="width:640px;height:360px;background:#000"';
let PA = 0, PB = 0;
const A = () => `http://a.localhost:${PA}`, B = () => `http://b.localhost:${PB}`;
const pages = {
  '/p1': () => `<!doctype html><title>Direct video</title><h1 style="margin:0">Direct video page</h1><video id="v" src="/media/clip1.mp4" ${VIDEO_ATTR}></video>`,
  '/embed': q => `<!doctype html><title>Embedded player</title><body style="margin:0"><video id="v" src="${q.get('src') || '/media/clip1.mp4'}" ${VIDEO_ATTR}></video>`,
  '/p2': () => `<!doctype html><title>Page with a cross-origin player</title><h1>Article</h1><iframe id="f" src="${B()}/embed?src=${encodeURIComponent(B() + '/media/clip1.mp4')}" width="660" height="380" style="border:0"></iframe>`,
  '/p2s': () => `<!doctype html><title>Page with a sandboxed player</title><h1>Article</h1><iframe id="f" sandbox="allow-scripts" src="${B()}/embed?src=${encodeURIComponent(B() + '/media/clip1.mp4')}" width="660" height="380" style="border:0"></iframe>`,
  '/p2d': () => `<!doctype html><title>Page with a srcdoc player</title><h1>Article</h1><iframe id="f" width="660" height="380" style="border:0" srcdoc="&lt;body style=margin:0&gt;&lt;video id=v src=${A()}/media/clip1.mp4 muted autoplay loop width=640 height=360 style=width:640px;height:360px&gt;&lt;/video&gt;"></iframe>`,
  '/p3': () => `<!doctype html><title>Feed</title><body style="margin:0"><div role="article" style="padding:10px"><h3 id="t">Post 0</h3><video id="v" muted playsinline loop width="640" height="360" style="width:640px;height:360px;background:#000"></video></div>
<script>function show(n){var v=document.getElementById('v');document.getElementById('t').textContent='Post '+n;v.src='/media/clip'+n+'.mp4';v.play();history.pushState({},'','/p3/story/'+n);}</script>`,
  '/p4': q => `<!doctype html><title>MediaSource player</title><body style="margin:0"><video id="v" ${VIDEO_ATTR}></video>
<script>
const mode = ${JSON.stringify(q.get('mode') || 'pair')};
const v = document.getElementById('v'), ms = new MediaSource(); v.src = URL.createObjectURL(ms);
async function pump(sb, url, chunk, params) {
  const head = await fetch(url, { headers: { Range: 'bytes=0-0' } }); const total = +/\\/(\\d+)/.exec(head.headers.get('content-range'))[1];
  for (let s = 0; s < total; s += chunk) {
    const e = Math.min(total - 1, s + chunk - 1);
    const r = params ? await fetch(url + '?bytestart=' + s + '&byteend=' + e) : await fetch(url, { headers: { Range: 'bytes=' + s + '-' + e } });
    const buf = await r.arrayBuffer();
    await new Promise((ok, bad) => { sb.addEventListener('updateend', ok, { once: true }); sb.addEventListener('error', bad, { once: true }); sb.appendBuffer(buf); });
  }
}
ms.addEventListener('sourceopen', async () => {
  try {
    if (mode === 'pair') {
      const sv = ms.addSourceBuffer('video/mp4; codecs="avc1.42E01E"'), sa = ms.addSourceBuffer('audio/mp4; codecs="mp4a.40.2"');
      await Promise.all([pump(sv, '/media/mse_v.mp4', 150000, false), pump(sa, '/media/mse_a.mp4', 40000, true)]);
    } else { const s = ms.addSourceBuffer('video/mp4; codecs="avc1.42E01E,mp4a.40.2"'); await pump(s, '/media/mse_mux.mp4', 200000, false); }
    ms.endOfStream(); document.title += ' (loaded)'; window.loaded = true; v.play();
  } catch (e) { window.failed = String(e); }
});
</script>`,
  '/p5': q => `<!doctype html><title>HLS player</title><body style="margin:0"><video id="v" ${VIDEO_ATTR}></video>
<script>
const copy = ${JSON.stringify(q.get('copy') === '1')};
const v = document.getElementById('v'), ms = new MediaSource(); v.src = URL.createObjectURL(ms);
const xhr = url => new Promise((ok, bad) => { const x = new XMLHttpRequest(); x.open('GET', url); x.onload = () => ok(x.responseText); x.onerror = bad; x.send(); });
ms.addEventListener('sourceopen', async () => {
  try {
    const master = await xhr('/hls/master.m3u8'); const variant = master.split('\\n').find(l => l && l[0] !== '#');
    const list = await xhr('/hls/' + variant); const lines = list.split('\\n');
    const init = /URI="([^"]+)"/.exec(list)[1], segs = lines.filter(l => l && l[0] !== '#');
    const sb = ms.addSourceBuffer('video/mp4; codecs="avc1.42E01E,mp4a.40.2"');
    const add = async url => { let buf = await (await fetch('/hls/' + url)).arrayBuffer(); if (copy) buf = new Uint8Array(buf).slice().buffer; await new Promise(ok => { sb.addEventListener('updateend', ok, { once: true }); sb.appendBuffer(buf); }); };
    await add(init); for (const s of segs) await add(s);
    ms.endOfStream(); window.loaded = true; v.play();
  } catch (e) { window.failed = String(e); }
});
</script>`,
  '/p6': q => (q.get('mode') === 'canvas'
    ? `<!doctype html><title>Hidden video</title><body style="margin:0"><div id="player" style="position:relative;width:640px;height:360px;background:#123"><video id="v" src="/media/clip1.mp4" muted autoplay loop style="display:none"></video><canvas id="c" width="640" height="360" style="position:absolute;left:0;top:0"></canvas></div>
<script>const v=document.getElementById('v'),c=document.getElementById('c').getContext('2d');(function d(){try{c.drawImage(v,0,0,640,360)}catch(e){}requestAnimationFrame(d)})();</script>`
    : `<!doctype html><title>Covered video</title><body style="margin:0"><div style="position:relative;width:640px;height:360px"><video id="v" src="/media/clip1.mp4" ${VIDEO_ATTR}></video><canvas id="c" width="640" height="360" style="position:absolute;left:0;top:0;width:640px;height:360px;z-index:50"></canvas></div>`),
  '/p8': () => `<!doctype html><title>Quality switching player</title><body style="margin:0"><video id="v" ${VIDEO_ATTR}></video>
<script>
// one element, one buffer, two quality files read in ranges (the player switched quality while playing)
const v = document.getElementById('v'), ms = new MediaSource(); v.src = URL.createObjectURL(ms);
async function range(sb, url, from, to) {
  const head = await fetch(url, { headers: { Range: 'bytes=0-0' } }); const total = +/\\/(\\d+)/.exec(head.headers.get('content-range'))[1];
  for (let s = from; s < Math.min(to, total); s += 250000) {
    const r = await fetch(url, { headers: { Range: 'bytes=' + s + '-' + Math.min(total - 1, s + 249999) } }); const b = await r.arrayBuffer();
    await new Promise(ok => { sb.addEventListener('updateend', ok, { once: true }); sb.appendBuffer(b); });
  }
}
ms.addEventListener('sourceopen', async () => {
  const sb = ms.addSourceBuffer('video/mp4; codecs="avc1.42E01E,mp4a.40.2"');
  try { await range(sb, '/media/mse_mux.mp4', 0, 500000); await range(sb, '/media/mse_hd.mp4', 0, 500000); } catch (e) { window.failed = String(e); }
  window.loaded = true; v.play();
});
</script>`,
  '/p7': () => `<!doctype html><title>Frozen page</title><body style="margin:0"><video id="v" src="/media/clip1.mp4" ${VIDEO_ATTR}></video>
<script>window.pageOk = []; try { pageOk.push(String(fetch).includes('native code')); } catch (e) {} fetch('/media/clip1.mp4', { headers: { Range: 'bytes=0-99' } }).then(r => r.arrayBuffer()).then(b => pageOk.push('fetch:' + b.byteLength));</script>`,
};
function mkServer(side) {
  return new Promise(resolve => {
    const requests = [];
    const server = http.createServer((req, res) => {
      const u = new URL(req.url, 'http://x'); requests.push(req.url);
      res.setHeader('Access-Control-Allow-Origin', '*'); res.setHeader('Access-Control-Allow-Headers', 'range'); res.setHeader('Access-Control-Expose-Headers', 'content-range, content-length, accept-ranges');
      if (req.method === 'OPTIONS') { res.statusCode = 204; return res.end(); }
      if (pages[u.pathname]) { res.setHeader('Content-Type', 'text/html; charset=utf-8'); return res.end(pages[u.pathname](u.searchParams)); }
      const m = /^\/(media|hls)\/(.+)$/.exec(u.pathname);
      const file = m && path.join(media, m[1] === 'hls' ? 'hls' : '', m[2]);
      if (!file || !file.startsWith(media) || !fs.existsSync(file)) { res.statusCode = 404; return res.end(); }
      const size = fs.statSync(file).size; let s = 0, e = size - 1, code = 200;
      const rg = /bytes=(\d+)-(\d*)/.exec(req.headers.range || ''), qs = u.searchParams.has('bytestart') ? [u.searchParams.get('bytestart'), u.searchParams.get('byteend')] : null;
      if (rg || qs) { s = +(rg ? rg[1] : qs[0]); e = Math.min(size - 1, rg ? (rg[2] ? +rg[2] : size - 1) : +qs[1]); code = 206; }
      const ext = path.extname(file), type = ext === '.m3u8' ? 'application/vnd.apple.mpegurl' : ext === '.m4s' ? 'video/iso.segment' : 'video/mp4';
      res.statusCode = code; res.setHeader('Content-Type', type); res.setHeader('Accept-Ranges', 'bytes'); res.setHeader('Content-Length', e - s + 1);
      if (code === 206) res.setHeader('Content-Range', `bytes ${s}-${e}/${size}`);
      fs.createReadStream(file, { start: s, end: e }).pipe(res);
    });
    server.listen(0, '127.0.0.1', () => resolve({ server, requests, port: server.address().port }));
  });
}

(async () => {
  const sa = await mkServer('a'), sb = await mkServer('b'); PA = sa.port; PB = sb.port;
  const profile = fs.mkdtempSync(path.join(process.env.TMPDIR || os.tmpdir(), 'neodm-v158-'));
  const child = spawn(exe, [`--user-data-dir=${profile}`, '--remote-debugging-pipe', '--enable-unsafe-extension-debugging', '--no-first-run', '--no-default-browser-check', '--headless=new', '--autoplay-policy=no-user-gesture-required', '--window-size=1100,800', '--host-resolver-rules=MAP *.localhost 127.0.0.1', 'about:blank'], { stdio: ['ignore', 'ignore', 'ignore', 'pipe', 'pipe'] });
  let nextId = 1, buf = ''; const pending = new Map(), events = [];
  child.stdio[4].on('data', d => { buf += d.toString('utf8'); let i; while ((i = buf.indexOf('\u0000')) >= 0) { const m = JSON.parse(buf.slice(0, i)); buf = buf.slice(i + 1); if (m.id && pending.has(m.id)) { const p = pending.get(m.id); pending.delete(m.id); m.error ? p.rej(new Error(m.error.message)) : p.res(m.result); } else if (m.method) events.push(m); } });
  const call = (method, params = {}, sessionId, ms = 30000) => new Promise((res, rej) => { const id = nextId++; const timer = setTimeout(() => { pending.delete(id); rej(new Error('timeout: ' + method)); }, ms); pending.set(id, { res: v => { clearTimeout(timer); res(v); }, rej: e => { clearTimeout(timer); rej(e); } }); child.stdio[3].write(JSON.stringify({ id, method, params, sessionId }) + '\u0000'); });
  const attach = async targetId => (await call('Target.attachToTarget', { targetId, flatten: true })).sessionId;
  const evalIn = async (session, expression, contextId) => { const r = await call('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true, userGesture: true, ...(contextId ? { contextId } : {}) }, session); if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description || r.exceptionDetails.text); return r.result.value; };
  const poll = async (fn, ms = 12000, step = 200) => { const end = Date.now() + ms; let v; while (Date.now() < end) { try { v = await fn(); } catch { v = null; } if (v) return v; await sleep(step); } return v; };
  const click = async (sess, x, y) => { await call('Input.dispatchMouseEvent', { type: 'mouseMoved', x: Math.round(x), y: Math.round(y) }, sess); for (const type of ['mousePressed', 'mouseReleased']) await call('Input.dispatchMouseEvent', { type, x: Math.round(x), y: Math.round(y), button: 'left', buttons: type === 'mousePressed' ? 1 : 0, clickCount: 1 }, sess); };
  try {
    console.log('browser:', exe, (await call('Browser.getVersion')).product);
    const loaded = await call('Extensions.loadUnpacked', { path: path.join(root, 'extension') });
    check('extension loaded under the manifest-key ID', loaded.id === EXT_ID, loaded.id);
    let sw; for (let i = 0; i < 50 && !sw; i++) { sw = (await call('Target.getTargets')).targetInfos.find(t => t.type === 'service_worker' && /^chrome-extension:\/\/[a-p]{32}\/background\.js$/.test(t.url)); if (!sw) await sleep(200); }
    check('service worker started', !!sw); if (!sw) throw new Error('no worker');
    const swS = await attach(sw.targetId), inSw = e => evalIn(swS, e);
    await inSw(`globalThis.__sent = []; send = async req => { __sent.push(JSON.parse(JSON.stringify(req))); return { ok: true, id: 'stub-' + __sent.length }; }; chrome.storage.local.set({ videoButtonEnabled: true }); 'stubbed'`);
    const manifest = JSON.parse(fs.readFileSync(path.join(root, 'extension/manifest.json'), 'utf8'));
    check('the extension under test is the product version', manifest.version === fs.readFileSync(path.join(root, 'Directory.Build.props'), 'utf8').match(/<Version>([^<]+)</)[1], manifest.version);

    /* ---- opening a scenario page and finding the content script of every frame ---- */
    async function open(url, { freeze = false } = {}) {
      const t = (await call('Target.createTarget', { url: 'about:blank' })).targetId, top = await attach(t);
      await call('Page.enable', {}, top); await call('Runtime.enable', {}, top);
      await call('Emulation.setDeviceMetricsOverride', { width: 1000, height: 700, deviceScaleFactor: 1, mobile: false }, top);
      await call('Target.setAutoAttach', { autoAttach: true, waitForDebuggerOnStart: false, flatten: true }, top);
      if (freeze) await call('Page.addScriptToEvaluateOnNewDocument', { source: `try { Object.defineProperty(window, 'fetch', { value: window.fetch, writable: false, configurable: false }); for (const o of [XMLHttpRequest.prototype, SourceBuffer.prototype, MediaSource.prototype, HTMLMediaElement.prototype, Response.prototype, Element.prototype]) Object.freeze(o); Object.freeze(URL); } catch (e) {}` }, top);
      await call('Page.navigate', { url }, top);
      return { t, top, url };
    }
    const sessions = pg => [pg.top, ...events.filter(e => e.method === 'Target.attachedToTarget' && e.sessionId === pg.top && e.params.targetInfo.type === 'iframe').map(e => e.params.sessionId)];
    /** Every isolated world that runs our content script: [{ sess, ctx, ev(expr) }] */
    async function frames(pg) {
      const out = [];
      for (const sess of sessions(pg)) {
        try { await call('Runtime.enable', {}, sess, 5000); } catch { continue; }
        for (const e of events.filter(e => e.method === 'Runtime.executionContextCreated' && e.sessionId === sess && e.params.context.auxData?.type === 'isolated')) {
          const ctx = e.params.context.id; try { if (await evalIn(sess, `typeof __neodmRibbon`, ctx) === 'object') out.push({ sess, ctx, ev: ex => evalIn(sess, ex, ctx), url: await evalIn(sess, 'location.href', ctx), main: ex => evalIn(sess, ex) }); } catch {}
        }
      }
      return out;
    }
    const barsOf = async f => JSON.parse(await f.ev('JSON.stringify(__neodmRibbon.bars())'));
    async function shown(pg, { frameMatch, ms = 14000, state } = {}) {
      return poll(async () => {
        for (const f of await frames(pg)) { if (frameMatch && !frameMatch(f)) continue; const b = (await barsOf(f)).find(x => x.shown && x.dl && (!state || x.state === state)); if (b) return { f, b }; }
        return null;
      }, ms, 300);
    }
    const iframeOffset = async (pg, f) => (f.sess === pg.top ? (f.url === pg.url || !(await evalIn(pg.top, `!!document.getElementById('f')`)) ? { x: 0, y: 0 } : await evalIn(pg.top, `(() => { const r = document.getElementById('f').getBoundingClientRect(); return { x: r.left, y: r.top }; })()`)) : await evalIn(pg.top, `(() => { const r = document.getElementById('f').getBoundingClientRect(); return { x: r.left, y: r.top }; })()`));
    const videoRect = (f) => f.ev(`(() => { const r = document.getElementById('v').getBoundingClientRect(); return { x: r.left, y: r.top, w: r.width, h: r.height }; })()`);
    const shot = async (pg, name) => { const png = await call('Page.captureScreenshot', { format: 'png' }, pg.top); const file = path.join(shotDir, name); fs.writeFileSync(file, Buffer.from(png.data, 'base64')); console.log('screenshot:', file); return file; };
    const sentNow = async () => inSw('__sent');
    const sentClear = () => inSw('__sent.length = 0; 1');
    const closeTab = async pg => { try { await call('Target.closeTarget', { targetId: pg.t }); } catch {} };
    const tabId = async url => poll(async () => inSw(`chrome.tabs.query({}).then(ts => (ts.find(t => t.url.startsWith(${JSON.stringify(url)})) || {}).id ?? null)`), 8000, 250);
    const stateOf = async id => inSw(`chrome.storage.session.get('neodm.media.${id}').then(r => r['neodm.media.${id}'] || null)`);
    const hidden = () => inSw(`chrome.storage.local.get('videoButtonHiddenHosts').then(r => r.videoButtonHiddenHosts || [])`);

    /* the common checks for a button on a playing video */
    async function standard(name, pg, found, { wantUrl, wantFile, frameMatch, rectOf, cornerCheck = true, referer }) {
      const { f, b } = found; const off = await iframeOffset(pg, f), vr = await (rectOf || videoRect)(f);
      const dx = b.pill.x - vr.x, dy = b.pill.y - vr.y;
      if (cornerCheck) check(`${name}: the button sits in the top-left corner of the video`, Math.abs(dx - 8) <= 2 && Math.abs(dy - 8) <= 2, `offset ${dx.toFixed(1)},${dy.toFixed(1)}`);
      await sentClear(); await click(pg.top, off.x + b.dl.x + b.dl.w / 2, off.y + b.dl.y + b.dl.h / 2);
      await poll(async () => (await sentNow()).length > 0, 5000, 100); await sleep(400);
      const sent = await sentNow();
      check(`${name}: the click sends exactly one request`, sent.length === 1, String(sent.length));
      if (sent.length) {
        check(`${name}: it is the right file`, wantUrl(sent[0].url), sent[0].url);
        check(`${name}: file name with extension and the video type`, wantFile(sent[0].fileName) && sent[0].mime === 'video/mp4', `${sent[0].fileName} ${sent[0].mime}`);
        if (referer) check(`${name}: the page address goes as referer`, referer(sent[0].referer), sent[0].referer);
      }
    }
    /* dismiss, "don't show" */
    async function dismissChecks(name, pg, found, host) {
      let { f, b } = found; const off = await iframeOffset(pg, f);
      await click(pg.top, off.x + b.x.x + b.x.w / 2, off.y + b.x.y + b.x.h / 2); await sleep(500);
      let now = (await barsOf(f)).find(x => x.key === b.key);
      check(`${name}: the cross hides the button and offers a notice with three choices`, now && now.mode === 'notice' && now.panel.length === 3, JSON.stringify(now && [now.mode, now.panel.length]));
      await sleep(8600);
      now = (await barsOf(f)).find(x => x.key === b.key);
      check(`${name}: after the notice the button is gone`, !now);
      await f.ev(`__neodmRibbon.refresh()`); await sleep(1200);
      check(`${name}: and it does not come back until the page is reloaded`, !(await barsOf(f)).some(x => x.shown));
    }

let pg, found;
    if (want('1')) {
        /* ======== 1: direct video in the top frame ======== */
    console.log('--- 1 direct video');
    pg = await open(A() + '/p1');
    found = await shown(pg, { state: 'ready' });
    check('1: the button shows on the playing video, ready with quality and size', !!found && /\d+p|MB|KB/.test(await found.f.ev(`document.title && JSON.stringify(__neodmRibbon.bars())`)), JSON.stringify(found && found.b.text));
    if (found) {
      await shot(pg, 'button-v158-direct.png');
      await standard('1', pg, found, { wantUrl: u => u === A() + '/media/clip1.mp4', wantFile: n => /^Direct video\.mp4$/.test(n) || /\.mp4$/.test(n), referer: r => r === A() + '/p1' });
      const hk = await found.f.ev('JSON.stringify(__neodmRibbon.hooks())'); check('1: the page-world hooks are installed in the page', JSON.parse(hk).ok === true, hk);
      await dismissChecks('1', pg, found);
      // reload: the button is back
      await call('Page.reload', {}, pg.top); const again = await shown(pg, { state: 'ready' }); check('1: after a reload the button is back', !!again);
      // "Don't show on this site"
      if (again) {
        const off = await iframeOffset(pg, again.f); await click(pg.top, off.x + again.b.x.x + again.b.x.w / 2, off.y + again.b.x.y + again.b.x.h / 2); await sleep(500);
        const b2 = (await barsOf(again.f)).find(x => x.key === again.b.key);
        if (b2 && b2.panel.length === 3) await click(pg.top, b2.panel[0].x + b2.panel[0].w / 2, b2.panel[0].y + b2.panel[0].h / 2); await sleep(700);
        check("1: \"Don't show on this site\" stores the site", (await hidden()).includes('a.localhost') || (await hidden()).some(h => h.endsWith('localhost')), JSON.stringify(await hidden()));
        check("1: and every button of the page is gone", (await barsOf(again.f)).length === 0);
        await call('Page.reload', {}, pg.top); await sleep(3500); const frs = await frames(pg);
        check("1: after a reload the site stays without the button", frs.length > 0 && (await Promise.all(frs.map(barsOf))).every(l => l.length === 0));
        await inSw(`chrome.storage.local.set({ videoButtonHiddenHosts: [] })`); await call('Page.reload', {}, pg.top);
        check('1: removing the site from the list brings the button back', !!(await shown(pg, { state: 'ready' })));
        // the corner can be changed
        await inSw(`chrome.storage.local.set({ videoButtonCorner: 'right' })`); await sleep(600);
        const r1 = await shown(pg); if (r1) { const vr = await videoRect(r1.f); check('1: videoButtonCorner=right puts it in the top-right corner', Math.abs((vr.x + vr.w) - (r1.b.pill.x + r1.b.pill.w) - 8) <= 2, `${(vr.x + vr.w) - (r1.b.pill.x + r1.b.pill.w)}`); }
        await inSw(`chrome.storage.local.remove('videoButtonCorner')`);
      }
    }
    await closeTab(pg);
    }

    if (want('2')) {
    /* ======== 2: the same video in other frames ======== */
    for (const [name, route, wantHost] of [['2 cross-origin iframe', '/p2', B()], ['2 sandboxed iframe', '/p2s', B()], ['2 srcdoc iframe', '/p2d', A()]]) {
      console.log('--- ' + name);
      pg = await open(A() + route);
      found = await shown(pg, { frameMatch: f => f.url !== pg.url && true, state: 'ready' });
      check(`${name}: the content script runs in the frame and shows the button`, !!found, found ? found.f.url : 'none');
      if (found) {
        await standard(name, pg, found, { wantUrl: u => u === wantHost + '/media/clip1.mp4', wantFile: n => /\.mp4$/.test(n), referer: r => /^http:\/\/[ab]\.localhost:\d+\//.test(r) });
        if (route === '/p2') { await shot(pg, 'button-v158-iframe.png'); const tid = await tabId(A()); const st = await stateOf(tid); const el = Object.values(st.els || {}).find(e => e.frame > 0); check(`${name}: the worker knows the frame of the element`, !!el, JSON.stringify(el && { frame: el.frame, furl: el.furl })); }
      }
      await closeTab(pg);
    }

    }
    if (want('3')) {
    /* ======== 3: a feed that recycles one <video> ======== */
    console.log('--- 3 feed with one recycled element');
    pg = await open(A() + '/p3'); await sleep(1500);
    const f0 = (await frames(pg))[0];
    const seen = [];
    for (const n of [1, 2, 3, 2, 4]) {
      await evalIn(pg.top, `show(${n})`); await sleep(1200);
      const b = await poll(async () => (await barsOf(f0)).find(x => x.shown && x.state === 'ready' && x.dl && /Post/.test('Post') ), 9000, 300);
      if (!b) { check(`3: post ${n}: the button is ready`, false, JSON.stringify(await barsOf(f0))); continue; }
      await sentClear(); await click(pg.top, b.dl.x + b.dl.w / 2, b.dl.y + b.dl.h / 2); await poll(async () => (await sentNow()).length > 0, 4000, 100); await sleep(300);
      const sent = await sentNow(); seen.push(sent[0] && sent[0].url);
      check(`3: post ${n}: exactly one request and it is this post's video, not an earlier one`, sent.length === 1 && sent[0].url === `${A()}/media/clip${n}.mp4` && sent[0].fileName === `Post ${n}.mp4`, JSON.stringify(sent.map(x => [x.url.split('/').pop(), x.fileName])));
      if (n === 1) await shot(pg, 'button-v158-feed.png');
    }
    const tid3 = await tabId(A()), st3 = await stateOf(tid3);
    check('3: earlier posts keep their entries after the element moved on', ['clip1', 'clip2', 'clip3', 'clip4'].every(c => (st3.items || []).some(i => i.url.includes(c))), JSON.stringify((st3.items || []).map(i => i.url.split('/').pop())));
    check('3: the page address changed through pushState and nothing was dropped', /\/p3\/story\/4$/.test(st3.page), st3.page);
    await closeTab(pg);

    }
    if (want('4')) {
    /* ======== 4: MediaSource player (hook attribution) ======== */
    console.log('--- 4 MediaSource player, video+audio pair');
    pg = await open(A() + '/p4?mode=pair'); await poll(() => evalIn(pg.top, 'window.loaded === true || window.failed'), 20000, 300);
    check('4: the page loaded its fragments (no error)', await evalIn(pg.top, 'window.loaded === true'), await evalIn(pg.top, 'String(window.failed)'));
    found = await shown(pg, { state: 'ready' });
    check('4: the button is ready for the MediaSource player (a pair is one entry)', !!found, JSON.stringify(found && found.b.text) + (found ? '' : ' ' + JSON.stringify(await Promise.all((await frames(pg)).map(async f => ({ bars: (await barsOf(f)).map(b => [b.state, b.reason, b.shown]), src: JSON.parse(await f.ev('JSON.stringify(__neodmRibbon.hookSources(document.getElementById("v")))')) }))))).slice(0, 900) + ' ' + JSON.stringify((await stateOf(await tabId(A()))).items.map(i => [i.url.split('/').pop(), i.track, i.owner, i.kind, i.complete])));
    if (found) {
      await shot(pg, 'button-v158-mse.png');
      await sentClear(); const b = found.b; await click(pg.top, b.dl.x + b.dl.w / 2, b.dl.y + b.dl.h / 2); await poll(async () => (await sentNow()).length >= 2, 6000, 100); await sleep(400);
      const sent = await sentNow();
      check('4: the click sends two jobs, video only then audio only, of this player', sent.length === 2 && sent[0].url === A() + '/media/mse_v.mp4' && sent[1].url === A() + '/media/mse_a.mp4', JSON.stringify(sent.map(x => x.url)));
      check('4: with clear names (video only) and (audio only)', sent.length === 2 && /\(video only\)\.mp4$/.test(sent[0].fileName) && /\(audio only\)\.m4a$/.test(sent[1].fileName), JSON.stringify(sent.map(x => x.fileName)));
      check('4: the hooks tied them to the element through the data flow', JSON.parse(await found.f.ev('JSON.stringify(__neodmRibbon.hooks())')).sbs >= 2);
    }
    await closeTab(pg);
    console.log('--- 4b MediaSource player, muxed track');
    pg = await open(A() + '/p4?mode=mux'); await poll(() => evalIn(pg.top, 'window.loaded === true || window.failed'), 20000, 300);
    found = await shown(pg, { state: 'ready' });
    if (found) {
      await sentClear(); await click(pg.top, found.b.dl.x + found.b.dl.w / 2, found.b.dl.y + found.b.dl.h / 2); await poll(async () => (await sentNow()).length >= 1, 5000, 100); await sleep(300);
      const sent = await sentNow(); check('4b: a muxed track becomes one request for its file', sent.length === 1 && sent[0].url === A() + '/media/mse_mux.mp4' && /\.mp4$/.test(sent[0].fileName), JSON.stringify(sent.map(x => [x.url, x.fileName])));
    } else check('4b: the button is ready for the muxed MediaSource player', false);
    await closeTab(pg);

    }
    if (want('5')) {
    /* ======== 5: HLS ======== */
    for (const copy of ['0', '1']) {
      console.log('--- 5 HLS player' + (copy === '1' ? ' (bytes re-made by the page, no tag)' : ''));
      pg = await open(A() + '/p5?copy=' + copy); await poll(() => evalIn(pg.top, 'window.loaded === true || window.failed'), 20000, 300);
      check('5: the HLS page loaded its segments', await evalIn(pg.top, 'window.loaded === true'), await evalIn(pg.top, 'String(window.failed)'));
      found = await shown(pg, { state: 'unsupported' });
      check('5: the button is grey and says it cannot save this video', !!found && /save/i.test(found.b.text), JSON.stringify(found && found.b.text));
      if (found) {
        const off = await iframeOffset(pg, found.f); await sentClear(); await click(pg.top, off.x + found.b.dl.x + found.b.dl.w / 2, off.y + found.b.dl.y + found.b.dl.h / 2); await sleep(700);
        const b = (await barsOf(found.f)).find(x => x.key === found.b.key);
        check('5: a click explains: stream in pieces (HLS/DASH), coming in a later version', /HLS|pieces/i.test(b.reasonText || ''), (b.reasonText || '').slice(0, 160));
        check('5: nothing is sent to the app', (await sentNow()).length === 0);
        if (copy === '0') await shot(pg, 'button-v158-hls.png');
        const tid = await tabId(A()), st = await stateOf(tid);
        const streams = (st.items || []).filter(i => i.track === 'stream');
        check('5: the playlist is listed as a stream (HLS) owned by the player', streams.length === 1 && streams[0].stream === 'hls' && /master\.m3u8$/.test(streams[0].url) && !!streams[0].owner, JSON.stringify(streams.map(i => [i.stream, i.url.split('/').pop(), i.quality, i.dur, !!i.owner])));
        const list = await inSw(`popupItems(${JSON.stringify(st)}).then(l => l.filter(i => i.track === 'stream').length)`);
        check('5: the popup list contains it', list === 1);
        const dl = await inSw(`downloadItem({ id: ${tid}, url: 'x', title: 'x' }, ${JSON.stringify(streams[0] && streams[0].id)})`);
        check('5: downloadItem refuses a stream (never sent)', dl && dl.ok === false && /streams|HLS/i.test(dl.error || ''), JSON.stringify(dl));
      }
      await closeTab(pg);
    }

    }
    if (want('6')) {
    /* ======== 6: hidden under an overlay / hidden video ======== */
    console.log('--- 6 covered / hidden video');
    pg = await open(A() + '/p6?mode=overlay'); found = await shown(pg, { state: 'ready' });
    check('6: the video under a canvas overlay: the button is on top and shown', !!found);
    if (found) { await standard('6 overlay', pg, found, { wantUrl: u => u === A() + '/media/clip1.mp4', wantFile: n => /\.mp4$/.test(n) }); }
    await closeTab(pg);
    pg = await open(A() + '/p6?mode=canvas'); found = await shown(pg, { state: 'ready' });
    check('6: a display:none video drawn into a canvas: the button is anchored to the player box', !!found);
    if (found) { await standard('6 canvas', pg, found, { wantUrl: u => u === A() + '/media/clip1.mp4', wantFile: n => /\.mp4$/.test(n), rectOf: f => f.ev(`(() => { const r = document.getElementById('player').getBoundingClientRect(); return { x: r.left, y: r.top, w: r.width, h: r.height }; })()`) }); }
    await closeTab(pg);

    }
    if (want('8')) {
    /* ======== 8: a player whose requests are made in one buffer, two quality files: several sources make a picker ======== */
    console.log('--- 8 quality-switching player: picker');
    pg = await open(A() + '/p8'); await poll(() => evalIn(pg.top, 'window.loaded === true'), 20000, 300); console.log('p8 loaded:', await evalIn(pg.top, 'String(window.loaded) + " " + document.getElementById("v").readyState + " " + (document.getElementById("v").error && document.getElementById("v").error.message)'));
    found = await poll(async () => { const s = await shown(pg, { state: 'choose', ms: 3000 }); return s && /\(2\)/.test(s.b.text) ? s : null; }, 20000, 300);
    check('8: the button offers a choice (Download (n)) when several sources could be this video', !!found && /\(\d+\)/.test(found.b.text), JSON.stringify(found && found.b.text) + (found ? '' : ' mem=' + JSON.stringify(await inSw(`loadTab(${await tabId(A())}).then(s => s.items.map(i => [i.url.split('/').pop(), i.track, i.owner, i.hookOwned, i.source]))`)) + ' src=' + JSON.stringify(JSON.parse(await (await frames(pg))[0].ev('JSON.stringify(__neodmRibbon.hookSources(document.getElementById("v")))'))) + JSON.stringify(await Promise.all((await frames(pg)).map(async f => (await barsOf(f)).map(b => [b.state, b.reason, b.shown, b.text])))) + ' hooks=' + JSON.stringify(await (await frames(pg))[0].ev('JSON.stringify(__neodmRibbon.hooks())')) + ' ' + JSON.stringify(((await stateOf(await tabId(A()))) || {}).items.map(i => [i.url.split('/').pop(), i.track, i.owner, i.complete, i.size]))));
    if (found) {
      await click(pg.top, found.b.dl.x + found.b.dl.w / 2, found.b.dl.y + found.b.dl.h / 2); await sleep(700);
      const b = (await barsOf(found.f)).find(x => x.key === found.b.key);
      check('8: the picker lists each source with its size', b.panel.length === 2 && b.panel.every(x => /MB|KB/.test(x.text)), JSON.stringify(b.panel.map(x => x.text)) + JSON.stringify(JSON.parse(await found.f.ev('JSON.stringify(__neodmRibbon.hookSources(document.getElementById("v")))'))) + JSON.stringify(((await stateOf(await tabId(A()))) || {}).items.map(i => [i.url.split("/").pop(), i.track, i.owner, i.complete, i.size])));
      await shot(pg, 'button-v158-picker.png');
      await sentClear(); const target = b.panel.find(x => /MP4/i.test(x.text)) || b.panel[0];
      await click(pg.top, target.x + target.w / 2, target.y + target.h / 2); await poll(async () => (await sentNow()).length > 0, 5000, 100); await sleep(300);
      const sent = await sentNow(); check('8: picking one entry sends exactly that one file', sent.length === 1 && /\/media\/(mse_mux|mse_hd)\.mp4$/.test(sent[0].url), JSON.stringify(sent.map(x => x.url)));
    }
    await closeTab(pg);
    }
    if (want('7')) {
    /* ======== 7: a page that freezes the prototypes ======== */
    console.log('--- 7 frozen page');
    pg = await open(A() + '/p7', { freeze: true }); await sleep(2500);
    const f7 = (await frames(pg))[0];
    const hk7 = f7 ? JSON.parse(await f7.ev('JSON.stringify(__neodmRibbon.hooks())')) : null;
    console.log('hooks state on the frozen page:', JSON.stringify(hk7));
    check('7: the page itself works (its fetch ran and returned bytes)', JSON.stringify(await evalIn(pg.top, 'window.pageOk')).includes('fetch:100'), JSON.stringify(await evalIn(pg.top, 'window.pageOk')));
    found = await shown(pg, { state: 'ready' });
    check('7: the button still works through the DOM and network detection', !!found);
    if (found) await standard('7', pg, found, { wantUrl: u => u === A() + '/media/clip1.mp4', wantFile: n => /\.mp4$/.test(n) });
    await closeTab(pg);

    }
    if (want('s')) {
    /* ======== the popup summary ======== */
    console.log('--- popup summary');
    pg = await open(A() + '/p2'); found = await shown(pg, { state: 'ready', frameMatch: f => f.url !== pg.url });
    const tid2 = await tabId(A()); await poll(async () => Object.keys((await stateOf(tid2)).els || {}).length > 0, 6000, 200); const st2 = await stateOf(tid2);
    const sum = await inSw(`loadTab(${tid2}).then(async s => NeoSniff.summarize(s, (await popupItems(s)).filter(isMediaRow)))`);
    check('popup: "1 video found in 1 frame, 1 downloadable" for the iframe page', sum.videos === 1 && sum.downloadable === 1 && sum.frames === 1, JSON.stringify(sum) + ' ' + JSON.stringify({ els: st2.els, items: (st2.items || []).map(i => [i.url, i.owner, i.track]) }).slice(0, 700));
    await closeTab(pg);
    }
  } catch (e) { console.log('ERROR', e.stack || e.message); results.push(false); }
  try { child.kill(); } catch {} sa.server.close(); sb.server.close(); await sleep(500);
  try { fs.rmSync(profile, { recursive: true, force: true }); } catch {}
  console.log(results.every(Boolean) ? `ALL ${results.length} CHECKS PASSED` : `FAILED (${results.filter(x => !x).length} of ${results.length})`);
  process.exit(results.every(Boolean) ? 0 : 1);
})();
