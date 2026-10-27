'use strict';
/* Real-browser test for the 1.5.7 request identity (not part of `npm test`): the extension must hand NeoDM the User-Agent, Accept and Accept-Language the browser sent with a download.
   A local server records the headers it receives from the browser for the file; the stubbed host must receive the same values. Nothing leaves this computer.
   Loads extension/ unpacked into a throwaway profile (never the user's profile). The native host is replaced inside the extension's service worker (native() is overridden).
   Env: NEODM_BROWSER=<exe>, NEODM_HEADLESS=1 (headless Chrome does not report downloads to extensions; the default is an off-screen window). */
const fs = require('node:fs'), path = require('node:path'), os = require('node:os'), http = require('node:http'), { spawn, spawnSync } = require('node:child_process');
const root = path.join(__dirname, '..'), EXT_ID = fs.readFileSync(path.join(root, 'tools/extension-id.txt'), 'utf8').trim();
const pf = [process.env.ProgramFiles, process.env['ProgramFiles(x86)'], process.env.LOCALAPPDATA].filter(Boolean);
const exe = [process.env.NEODM_BROWSER, ...pf.flatMap(p => ['Google/Chrome/Application/chrome.exe', 'Microsoft/Edge/Application/msedge.exe'].map(x => path.join(p, x)))].filter(Boolean).find(c => fs.existsSync(c));
if (!exe) { console.log('SKIP: no Chromium browser found'); process.exit(0); }
const sleep = ms => new Promise(r => setTimeout(r, ms));
const results = []; const check = (name, ok, detail = '') => { results.push(!!ok); console.log((ok ? 'PASS ' : 'FAIL ') + name + (detail ? ' - ' + detail : '')); };
const windows = () => { const r = spawnSync('powershell', ['-NoProfile', '-File', path.join(root, 'tests/helpers/list-windows.ps1')], { encoding: 'utf8' }); return r.status === 0 ? r.stdout.split(/\r?\n/).filter(Boolean) : null; };

(async () => {
  const scratch = process.env.TMPDIR || os.tmpdir();
  const profile = fs.mkdtempSync(path.join(scratch, 'neodm-identity157-')), downloads = fs.mkdtempSync(path.join(scratch, 'neodm-identitydl157-'));
  fs.mkdirSync(path.join(profile, 'Default'), { recursive: true }); fs.writeFileSync(path.join(profile, 'Default', 'Preferences'), JSON.stringify({ download: { prompt_for_download: false, default_directory: downloads, directory_upgrade: true }, savefile: { default_directory: downloads } }));
  const before = windows();
  const extDir = path.join(root, 'extension');
  const args = [`--user-data-dir=${profile}`, '--remote-debugging-pipe', '--enable-unsafe-extension-debugging', '--no-first-run', '--no-default-browser-check', '--window-size=1000,700', ...(process.env.NEODM_HEADLESS === '1' ? ['--headless=new'] : ['--window-position=-3000,0']), 'about:blank'];
  const child = spawn(exe, args, { stdio: ['ignore', 'ignore', 'ignore', 'pipe', 'pipe'] });
  let nextId = 1, buf = ''; const pending = new Map();
  child.stdio[4].on('data', d => { buf += d.toString('utf8'); let i; while ((i = buf.indexOf('\u0000')) >= 0) { const m = JSON.parse(buf.slice(0, i)); buf = buf.slice(i + 1); if (m.id && pending.has(m.id)) { const p = pending.get(m.id); pending.delete(m.id); m.error ? p.rej(new Error(m.error.message)) : p.res(m.result); } } });
  const call = (method, params = {}, sessionId, ms = 30000) => new Promise((res, rej) => { const id = nextId++; const timer = setTimeout(() => { pending.delete(id); rej(new Error('timeout: ' + method)); }, ms); pending.set(id, { res: v => { clearTimeout(timer); res(v); }, rej: e => { clearTimeout(timer); rej(e); } }); child.stdio[3].write(JSON.stringify({ id, method, params, ...(sessionId ? { sessionId } : {}) }) + '\u0000'); });
  const attach = async targetId => (await call('Target.attachToTarget', { targetId, flatten: true })).sessionId;
  const evalIn = async (session, expression) => { const r = await call('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true, userGesture: true }, session); if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description || r.exceptionDetails.text); return r.result.value; };
  const poll = async (fn, ms = 12000, step = 200) => { const end = Date.now() + ms; let v; while (Date.now() < end) { v = await fn(); if (v) return v; await sleep(step); } return v; };
  const servers = [];
  try {
    const product = (await call('Browser.getVersion')).product;
    console.log('browser:', exe, product, process.env.NEODM_HEADLESS === '1' ? '(new headless)' : '(headed, off screen)');
    const loaded = await call('Extensions.loadUnpacked', { path: extDir });
    check('extension loaded under the manifest-key ID', loaded.id === EXT_ID, loaded.id);
    let sw; for (let i = 0; i < 50 && !sw; i++) { sw = (await call('Target.getTargets')).targetInfos.find(t => t.type === 'service_worker' && /^chrome-extension:\/\/[a-p]{32}\/background\.js$/.test(t.url)); if (!sw) await sleep(200); }
    if (!sw) throw new Error('no service worker');
    const swS = await attach(sw.targetId), inSw = e => evalIn(swS, e);
    await inSw(`(() => {
      globalThis.__native = []; globalThis.__heldMax = 0;
      native = async req => { __native.push(req); if (req.action === 'browser-sites') return { ok: true, sites: [] }; if (req.action === 'ping') return { ok: true, host: 'com.ndm.bridge' }; return { ok: true, id: 'stub-job' }; };
      return 'installed';
    })()`);
    const reset = async (extra = {}) => {
      await inSw(`__native.length = 0; chrome.storage.local.set({ browserHosts: [], captureEnabled: true, sessionCookies: ${extra.sessionCookies !== false} }).then(() => chrome.storage.session.set({ 'neodm.log': [] })).then(() => new Promise(r => chrome.downloads.search({}, items => Promise.all(items.map(i => new Promise(x => chrome.downloads.erase({ id: i.id }, x)))).then(r))))`);
      for (const f of fs.readdirSync(downloads)) fs.rmSync(path.join(downloads, f), { force: true });
    };
    const nat = async () => (await inSw('__native')).filter(r => r.action !== 'browser-sites' && r.action !== 'ping');
    const logOf = () => inSw(`chrome.storage.session.get('neodm.log').then(r => r['neodm.log'] || [])`);
    const target = (await call('Target.createTarget', { url: 'about:blank' })).targetId, pageS = await attach(target);
    await call('Page.enable', {}, pageS); await call('Runtime.enable', {}, pageS);
    await call('Emulation.setDeviceMetricsOverride', { width: 1000, height: 700, deviceScaleFactor: 1, mobile: false }, pageS);
    const inPage = e => evalIn(pageS, e);
    const open = async url => { await call('Page.navigate', { url }, pageS); await sleep(1500); };
    const click = async () => {
      await poll(() => inPage(`document.getElementById('dl')?.dataset.ready === '1'`), 5000, 100);
      const r = await inPage(`(() => { const b = document.getElementById('dl').getBoundingClientRect(); return { x: b.left + b.width / 2, y: b.top + b.height / 2 }; })()`);
      await call('Input.dispatchMouseEvent', { type: 'mouseMoved', x: r.x, y: r.y }, pageS);
      for (const type of ['mousePressed', 'mouseReleased']) await call('Input.dispatchMouseEvent', { type, x: Math.round(r.x), y: Math.round(r.y), button: 'left', buttons: type === 'mousePressed' ? 1 : 0, clickCount: 1 }, pageS);
    };
    // a local site: / is a page with a download link, /dl/file.dat is the file; every request for the file is recorded with the headers the browser sent
    const body = Buffer.alloc(3 * 1024 * 1024, 7);
    const mk = async () => {
      const stats = { dl: [] };
      const server = http.createServer((req, res) => {
        if (req.url.startsWith('/dl/')) {
          stats.dl.push({ ua: req.headers['user-agent'] || '', accept: req.headers.accept || '', lang: req.headers['accept-language'] || '', referer: req.headers.referer || '' });
          res.writeHead(200, { 'Content-Type': 'application/octet-stream', 'Content-Disposition': 'attachment; filename="identity-test.dat"', 'Content-Length': body.length }); res.end(body); return;
        }
        res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
        res.end('<!DOCTYPE html><html><head><title>identity test</title></head><body><a id="dl" href="javascript:void(0)" style="display:block;width:300px;height:60px;background:#1e90ff;color:#fff;font:20px sans-serif">Download</a><script>setTimeout(function(){var a=document.getElementById("dl");a.href="/dl/file.dat";a.dataset.ready="1";},300);</script></body></html>');
      });
      await new Promise(r => server.listen(0, '127.0.0.1', r));
      const s = { port: server.address().port, stats, pageUrl: () => 'http://www.identity.localhost:' + server.address().port + '/', close: () => new Promise(r => { server.closeAllConnections?.(); server.close(r); }) };
      servers.push(s); return s;
    };
    const settle = () => sleep(3500);
    const dlFiles = () => fs.readdirSync(downloads).filter(f => !f.endsWith('.crdownload'));
    const hasG2 = c => /(?:^|;\s*)g2cn=1(?:;|$)/.test(c || '');

    // ---------- I1: a captured download carries the headers the browser itself sent ----------
    { await reset(); const s = await mk();
      await open(s.pageUrl());
      const real = await inPage('({ ua: navigator.userAgent, language: navigator.language, languages: navigator.languages })');
      await click(); await settle();
      const n = await nat(), saw = s.stats.dl[0];
      check('I1 the browser fetched the file once and exactly one capture request reached the (stubbed) host', s.stats.dl.length >= 1 && n.length === 1, `dl=${s.stats.dl.length} native=${n.length}`);
      check('I1 the User-Agent is the browser\'s real one (Chrome, equal to navigator.userAgent and to what the server saw)', !!n[0]?.userAgent && n[0].userAgent === saw?.ua && n[0].userAgent === real.ua && /Chrome\//.test(n[0].userAgent), JSON.stringify({ sent: n[0]?.userAgent, saw: saw?.ua, nav: real.ua }));
      check('I1 the Accept-Language is the browser\'s real one (equal to what the server saw, starts with navigator.language)', !!n[0]?.acceptLanguage && n[0].acceptLanguage === saw?.lang && n[0].acceptLanguage.startsWith(real.language), JSON.stringify({ sent: n[0]?.acceptLanguage, saw: saw?.lang, nav: real.language }));
      check('I1 the Accept is what the browser sent for the download request', !!n[0]?.accept && n[0].accept === saw?.accept, JSON.stringify({ sent: n[0]?.accept, saw: saw?.accept }));
      check('I1 only printable ASCII within the limits (512 / 512 / 128)', /^[\u0020-\u007e]+$/.test(n[0].userAgent + n[0].accept + n[0].acceptLanguage) && n[0].userAgent.length <= 512 && n[0].accept.length <= 512 && n[0].acceptLanguage.length <= 128);
      check('I1 cookie, referer, size and name still travel as before', n[0]?.size === body.length && n[0]?.fileName === 'identity-test.dat' && n[0]?.referer === s.pageUrl(), JSON.stringify({ size: n[0]?.size, name: n[0]?.fileName, referer: n[0]?.referer }));
      const store = JSON.stringify([await inSw('chrome.storage.local.get(null)'), await inSw('chrome.storage.session.get(null)')]);
      check('I1 the identity is in neither chrome.storage area nor the capture log', !store.includes(real.ua) && !store.includes(n[0].acceptLanguage) && !JSON.stringify(await logOf()).includes('Chrome/'), store.slice(0, 160));
      await s.close(); }

    // ---------- I2: a request with no hop behind it (media button, ribbon, context menu) uses this browser's own identity ----------
    { await reset();
      const real = await inSw('({ ua: navigator.userAgent, language: navigator.language, languages: navigator.languages })');
      await inSw(`send({ url: 'http://127.0.0.1:9/x.bin', fileName: 'x.bin', referer: '', method: 'GET' }, null).catch(() => {}).then(() => 1)`);
      const n = await nat();
      check('I2 no hop: the request carries the extension\'s navigator.userAgent', n.length === 1 && n[0].userAgent === real.ua, JSON.stringify({ sent: n[0]?.userAgent, nav: real.ua }));
      check('I2 no hop: Accept-Language is built from navigator.languages and starts with navigator.language; no Accept is invented', n[0]?.acceptLanguage?.startsWith(real.language) && n[0].accept === undefined, JSON.stringify(n[0]?.acceptLanguage));
    }
  } catch (e) { console.log('ERROR', e.stack || e.message); results.push(false); }
  try { child.kill(); } catch {} for (const s of servers) { try { await s.close(); } catch {} } await sleep(800);
  const after = windows();
  if (before && after) { const fresh = after.filter(w => !before.includes(w) && !w.startsWith('Chrome_WidgetWin_1|') && !/^(Chrome_|Edge_)/.test(w)); console.log(fresh.length ? 'NOTE: new external windows appeared during the test (a download hook outside the project may have reacted): ' + JSON.stringify(fresh) : 'no new external windows appeared during the test'); }
  else console.log('NOTE: window list unavailable');
  try { fs.rmSync(profile, { recursive: true, force: true }); fs.rmSync(downloads, { recursive: true, force: true }); } catch {}
  console.log(results.every(Boolean) ? `ALL ${results.length} CHECKS PASSED` : `FAILED (${results.filter(x => !x).length} of ${results.length})`);
  process.exit(results.every(Boolean) ? 0 : 1);
})();
