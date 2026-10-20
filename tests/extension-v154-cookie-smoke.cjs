'use strict';
/* Real-browser test for the 1.5.4 cookie forwarding (not part of `npm test`): the extension must hand NeoDM the Cookie header the browser sent with a download.
   A local host imitates the MediaFire chain (tests/helpers/cookie-host-mock.cjs): the page sets the HttpOnly cookie g2cn=1, the file server answers 302 to a 32 KB "repair" page without it.
   Loads extension/ unpacked into a throwaway profile (never the user's profile). The native host is replaced inside the extension's service worker (native() is overridden), so nothing reaches the desktop app.
   Pages are served from *.localhost names and the file is called test-payload.dat, so a download manager that hooks browser downloads system-wide has little reason to react.
   Env: NEODM_BROWSER=<exe>, NEODM_HEADLESS=1 (headless Chrome does not report downloads to extensions; the default is an off-screen window). */
const fs = require('node:fs'), path = require('node:path'), os = require('node:os'), http = require('node:http'), { spawn, spawnSync } = require('node:child_process');
const { startCookieHost } = require('./helpers/cookie-host-mock.cjs');
const root = path.join(__dirname, '..'), EXT_ID = fs.readFileSync(path.join(root, 'tools/extension-id.txt'), 'utf8').trim();
const pf = [process.env.ProgramFiles, process.env['ProgramFiles(x86)'], process.env.LOCALAPPDATA].filter(Boolean);
const exe = [process.env.NEODM_BROWSER, ...pf.flatMap(p => ['Google/Chrome/Application/chrome.exe', 'Microsoft/Edge/Application/msedge.exe'].map(x => path.join(p, x)))].filter(Boolean).find(c => fs.existsSync(c));
if (!exe) { console.log('SKIP: no Chromium browser found'); process.exit(0); }
const sleep = ms => new Promise(r => setTimeout(r, ms));
const results = []; const check = (name, ok, detail = '') => { results.push(!!ok); console.log((ok ? 'PASS ' : 'FAIL ') + name + (detail ? ' - ' + detail : '')); };
const windows = () => { const r = spawnSync('powershell', ['-NoProfile', '-File', path.join(root, 'tests/helpers/list-windows.ps1')], { encoding: 'utf8' }); return r.status === 0 ? r.stdout.split(/\r?\n/).filter(Boolean) : null; };
/** The request the app would make: GET with the forwarded headers, no redirect following. */
const replay = (url, cookie, referer) => new Promise((resolve, reject) => { const u = new URL(url); const q = http.get({ host: u.hostname === 'localhost' || u.hostname.endsWith('.localhost') ? '127.0.0.1' : u.hostname, port: u.port, path: u.pathname + u.search, headers: { Host: u.host, 'User-Agent': 'NeoDM/1.0', ...(cookie ? { Cookie: cookie } : {}), ...(referer ? { Referer: referer } : {}) } }, res => { let n = 0; res.on('data', d => { n += d.length; }); res.on('end', () => resolve({ status: res.statusCode, location: res.headers.location || '', length: n, disposition: res.headers['content-disposition'] || '' })); }); q.on('error', reject); });

(async () => {
  const scratch = process.env.TMPDIR || os.tmpdir();
  const profile = fs.mkdtempSync(path.join(scratch, 'neodm-cookie154-')), downloads = fs.mkdtempSync(path.join(scratch, 'neodm-cookiedl154-'));
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
    const mk = async o => { const s = await startCookieHost(o); servers.push(s); return s; };
    const settle = () => sleep(3500);
    const dlFiles = () => fs.readdirSync(downloads).filter(f => !f.endsWith('.crdownload'));
    const hasG2 = c => /(?:^|;\s*)g2cn=1(?:;|$)/.test(c || '');

    // ---------- C1: page and file server on the same host ----------
    { await reset(); const s = await mk({ pageHost: 'www.cookie.localhost' });
      await open(s.pageUrl());
      check('C1 the cookie is HttpOnly: the page itself cannot read it (document.cookie), so only the request header can carry it', !/g2cn/.test(await inPage('document.cookie')), await inPage('document.cookie'));
      await click(); await settle();
      const n = await nat(), served = s.stats.dl;
      check('C1 exactly one capture request reached the (stubbed) host', n.length === 1, `native=${n.length} dl=${served.length}`);
      check('C1 the request carries the Cookie header the browser sent, including the HttpOnly g2cn=1', hasG2(n[0]?.cookie) && /ukey=/.test(n[0]?.cookie || ''), JSON.stringify(n[0]?.cookie));
      check('C1 the cookie equals what the file server saw from the browser', n[0]?.cookie === served[0]?.cookie, `${n[0]?.cookie} vs ${served[0]?.cookie}`);
      check('C1 the right referer (same origin: the full page address) and the browser size (6 MB) and name', n[0]?.referer === `http://www.cookie.localhost:${s.port}/file/abc123/test-payload.dat/file` && n[0]?.size === s.size && n[0]?.fileName === 'test-payload.dat', JSON.stringify({ ref: n[0]?.referer, size: n[0]?.size, name: n[0]?.fileName }));
      check('C1 the URL is the final file address', /\/dl\/t\d+[0-9a-f]{96}\/test-payload\.dat$/.test(n[0]?.url || ''), (n[0]?.url || '').slice(0, 80));
      check('C1 the browser got the file itself (served once, no repair redirect) and its own download was removed', s.stats.served === 1 && s.stats.repairRedirects === 0 && dlFiles().length === 0, JSON.stringify({ served: s.stats.served, repair: s.stats.repairRedirects, files: dlFiles() }));
      const withCookie = await replay(n[0].url, n[0].cookie, n[0].referer), without = await replay(n[0].url, '', n[0].referer);
      check('C1 replaying exactly what NeoDM received gets the file (200, 6 MB, Content-Disposition); without the cookie it is the 302 to the repair page', withCookie.status === 200 && withCookie.length === s.size && /attachment/.test(withCookie.disposition) && without.status === 302 && /download_repair\.php/.test(without.location), JSON.stringify({ withCookie, without }));
      const store = JSON.stringify([await inSw('chrome.storage.local.get(null)'), await inSw('chrome.storage.session.get(null)')]);
      check('C1 the cookie value is in neither chrome.storage area nor the capture log', !store.includes('g2cn=1') && !JSON.stringify(await logOf()).includes('g2cn'), store.slice(0, 200));
      await s.close(); }

    // ---------- C2: page on www.*, file server on another subdomain, cookie with Domain= (the real MediaFire layout) ----------
    { await reset(); const s = await mk({ pageHost: 'www.cookie.localhost', dlHost: 'download1.cookie.localhost', cookieDomain: 'cookie.localhost' });
      await open(s.pageUrl()); await click(); await settle();
      const n = await nat();
      console.log('C2 observation: the browser sent to the file server:', JSON.stringify(s.stats.dl.map(d => [d.host, d.cookie, d.referer])));
      if (!s.stats.dl.length || !hasG2(s.stats.dl[0].cookie)) console.log('NOTE C2: this browser did not accept Domain=cookie.localhost for a .localhost name, so cross-subdomain cannot be shown with this host name; same-host covered by C1');
      else {
        check('C2 cross-subdomain (Domain cookie): the request carries the cookie the browser sent to download1.cookie.localhost', n.length === 1 && hasG2(n[0]?.cookie) && n[0].cookie === s.stats.dl[0].cookie && /^http:\/\/download1\.cookie\.localhost:/.test(n[0].url), JSON.stringify({ cookie: n[0]?.cookie, url: n[0]?.url?.slice(0, 50) }));
        check('C2 referer is the page origin on the other subdomain', n[0]?.referer === `http://www.cookie.localhost:${s.port}/`, n[0]?.referer);
      }
      await s.close(); }

    // ---------- C3: a first hop redirects to the file server: the cookie of the LAST hop is sent, the URL is the last address ----------
    { await reset(); const s = await mk({ pageHost: 'www.cookie.localhost', dlHost: 'download1.cookie.localhost', cookieDomain: 'cookie.localhost', viaRedirect: true });
      await open(s.pageUrl()); await click(); await settle();
      const n = await nat();
      if (!s.stats.dl.length || !hasG2(s.stats.dl[0].cookie)) console.log('NOTE C3: cross-subdomain cookie not accepted by this browser for .localhost; hop test skipped');
      else check('C3 redirect chain: the capture is the final /dl/ address with the cookie of that hop', s.stats.redirectHops === 1 && n.length === 1 && /\/dl\//.test(n[0]?.url || '') && n[0].cookie === s.stats.dl[0].cookie, JSON.stringify({ hops: s.stats.redirectHops, url: n[0]?.url?.slice(0, 60), cookie: n[0]?.cookie }));
      await s.close(); }

    // ---------- C4: popup switch off: nothing is forwarded, the capture still happens ----------
    { await reset({ sessionCookies: false }); const s = await mk({ pageHost: 'www.cookie.localhost' });
      await open(s.pageUrl()); await click(); await settle();
      const n = await nat();
      check('C4 switch off: the capture request has no cookie, but still size, name and referer', n.length === 1 && !n[0].cookie && n[0].size === s.size && n[0].fileName === 'test-payload.dat', JSON.stringify({ cookie: n[0]?.cookie, size: n[0]?.size }));
      check('C4 switch off: the browser itself still sent the cookie (it got the file)', s.stats.served === 1 && hasG2(s.stats.dl[0]?.cookie), JSON.stringify(s.stats.dl[0]));
      const store = JSON.stringify([await inSw('chrome.storage.local.get(null)'), await inSw('chrome.storage.session.get(null)')]);
      check('C4 nothing in storage', !store.includes('g2cn'));
      await s.close(); }

    // ---------- C5: the popup page really shows the switch, default on, and a click stores the choice ----------
    { await reset(); await inSw(`chrome.storage.local.remove('sessionCookies')`);
      const popup = (await call('Target.createTarget', { url: `chrome-extension://${EXT_ID}/popup.html` })).targetId, popS = await attach(popup);
      await call('Runtime.enable', {}, popS); await sleep(1200);
      const state = await evalIn(popS, `({ checked: document.getElementById('sessionCookies').checked, label: document.querySelector('[data-i18n=popup_session_cookies]').textContent, hint: document.querySelector('[data-i18n=popup_session_cookies_hint]').textContent })`);
      check('C5 popup: the switch exists, is on by default, with its label and explanation in English', state.checked === true && /session cookies/.test(state.label) && /only to the NeoDM app on your PC/.test(state.hint), JSON.stringify(state));
      await evalIn(popS, `document.getElementById('sessionCookies').click(); 1`); await sleep(500);
      check('C5 popup: clicking stores sessionCookies=false; clicking again stores true', (await inSw(`chrome.storage.local.get('sessionCookies')`)).sessionCookies === false && (await evalIn(popS, `document.getElementById('sessionCookies').click(); new Promise(r => setTimeout(() => chrome.storage.local.get('sessionCookies', x => r(x.sessionCookies)), 300))`)) === true);
      await call('Target.closeTarget', { targetId: popup }); }
  } catch (e) { console.log('ERROR', e.stack || e.message); results.push(false); }
  try { child.kill(); } catch {} for (const s of servers) { try { await s.close(); } catch {} } await sleep(800);
  const after = windows();
  if (before && after) { const fresh = after.filter(w => !before.includes(w) && !w.startsWith('Chrome_WidgetWin_1|') && !/^(Chrome_|Edge_)/.test(w)); console.log(fresh.length ? 'NOTE: new external windows appeared during the test (a download hook outside the project may have reacted): ' + JSON.stringify(fresh) : 'no new external windows appeared during the test'); }
  else console.log('NOTE: window list unavailable');
  try { fs.rmSync(profile, { recursive: true, force: true }); fs.rmSync(downloads, { recursive: true, force: true }); } catch {}
  console.log(results.every(Boolean) ? `ALL ${results.length} CHECKS PASSED` : `FAILED (${results.filter(x => !x).length} of ${results.length})`);
  process.exit(results.every(Boolean) ? 0 : 1);
})();
