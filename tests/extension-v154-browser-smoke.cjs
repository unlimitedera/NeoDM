'use strict';
/* Real-browser test for 1.5.4 (not part of `npm test`): MediaFire-style one-time download links against the real extension in Chrome.
   Loads extension/ unpacked into a throwaway profile (never the user's profile), serves tests/helpers/mediafire-mock.cjs on *.mf.localhost and drives real mouse input.
   The native host is replaced inside the extension's service worker (native() is overridden), so nothing reaches the desktop app and the registry is untouched.
   Env: NEODM_BROWSER=<exe>, NEODM_HEADLESS=1 (new headless instead of an off-screen window; headless Chrome does not report downloads to extensions), NEODM_SAVEAS=1 (also run the Save as experiment). */
const fs = require('node:fs'), path = require('node:path'), os = require('node:os'), crypto = require('node:crypto'), { spawn, spawnSync } = require('node:child_process');
const { startMediaFire } = require('./helpers/mediafire-mock.cjs');
const root = path.join(__dirname, '..'), EXT_ID = fs.readFileSync(path.join(root, 'tools/extension-id.txt'), 'utf8').trim();
const pf = [process.env.ProgramFiles, process.env['ProgramFiles(x86)'], process.env.LOCALAPPDATA].filter(Boolean);
const exe = [process.env.NEODM_BROWSER, ...pf.flatMap(p => ['Google/Chrome/Application/chrome.exe', 'Microsoft/Edge/Application/msedge.exe'].map(x => path.join(p, x)))].filter(Boolean).find(c => fs.existsSync(c));
if (!exe) { console.log('SKIP: no Chromium browser found'); process.exit(0); }
const sleep = ms => new Promise(r => setTimeout(r, ms));
const results = []; const check = (name, ok, detail = '') => { results.push(!!ok); console.log((ok ? 'PASS ' : 'FAIL ') + name + (detail ? ' - ' + detail : '')); };
const sha = file => crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');

(async () => {
  const scratch = process.env.TMPDIR || os.tmpdir();
  const profile = fs.mkdtempSync(path.join(scratch, 'neodm-smoke154-')), downloads = fs.mkdtempSync(path.join(scratch, 'neodm-dl154-'));
  const saveAs = process.env.NEODM_SAVEAS === '1';
  // the download folder (and, for the Save as experiment, the 'ask where to save each file' setting) is written into the throwaway profile before the browser starts
  fs.mkdirSync(path.join(profile, 'Default'), { recursive: true }); fs.writeFileSync(path.join(profile, 'Default', 'Preferences'), JSON.stringify({ download: { prompt_for_download: saveAs, default_directory: downloads, directory_upgrade: true }, savefile: { default_directory: downloads } }));
  const args = [`--user-data-dir=${profile}`, '--remote-debugging-pipe', '--enable-unsafe-extension-debugging', '--no-first-run', '--no-default-browser-check', '--window-size=1000,700', ...(process.env.NEODM_HEADLESS === '1' ? ['--headless=new'] : ['--window-position=-3000,0']), 'about:blank'];
  const child = spawn(exe, args, { stdio: ['ignore', 'ignore', 'ignore', 'pipe', 'pipe'] });
  let nextId = 1, buf = ''; const pending = new Map(), cdpEvents = [];
  child.stdio[4].on('data', d => { buf += d.toString('utf8'); let i; while ((i = buf.indexOf('\u0000')) >= 0) { const m = JSON.parse(buf.slice(0, i)); buf = buf.slice(i + 1); if (m.id && pending.has(m.id)) { const p = pending.get(m.id); pending.delete(m.id); m.error ? p.rej(new Error(m.error.message)) : p.res(m.result); } else if (m.method) cdpEvents.push(m); } });
  const call = (method, params = {}, sessionId, ms = 30000) => new Promise((res, rej) => { const id = nextId++; const timer = setTimeout(() => { pending.delete(id); rej(new Error('timeout: ' + method)); }, ms); pending.set(id, { res: v => { clearTimeout(timer); res(v); }, rej: e => { clearTimeout(timer); rej(e); } }); child.stdio[3].write(JSON.stringify({ id, method, params, ...(sessionId ? { sessionId } : {}) }) + '\u0000'); });
  const attach = async targetId => (await call('Target.attachToTarget', { targetId, flatten: true })).sessionId;
  const evalIn = async (session, expression) => { const r = await call('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true, userGesture: true }, session); if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description || r.exceptionDetails.text); return r.result.value; };
  const poll = async (fn, ms = 12000, step = 200) => { const end = Date.now() + ms; let v; while (Date.now() < end) { v = await fn(); if (v) return v; await sleep(step); } return v; };
  const servers = [];
  try {
    console.log('browser:', exe, (await call('Browser.getVersion')).product, process.env.NEODM_HEADLESS === '1' ? '(new headless)' : '(headed, off screen)', saveAs ? '+ prompt_for_download' : '');
    const loaded = await call('Extensions.loadUnpacked', { path: path.join(root, 'extension') });
    check('extension loaded under the manifest-key ID', loaded.id === EXT_ID, loaded.id);
    let sw; for (let i = 0; i < 50 && !sw; i++) { sw = (await call('Target.getTargets')).targetInfos.find(t => t.type === 'service_worker' && /^chrome-extension:\/\/[a-p]{32}\/background\.js$/.test(t.url)); if (!sw) await sleep(200); }
    if (!sw) throw new Error('no service worker');
    const swS = await attach(sw.targetId), inSw = e => evalIn(swS, e);
    // observers and the native stub
    await inSw(`(() => {
      globalThis.__ev = []; globalThis.__native = []; globalThis.__mode = 'ok'; globalThis.__sites = []; globalThis.__delay = 0;
      const d = chrome.downloads, add = o => __ev.push({ at: Date.now(), ...o });
      d.onCreated.addListener(i => add({ t: 'created', id: i.id, url: i.url, by: i.byExtensionId || '', total: i.totalBytes, state: i.state }));
      d.onChanged.addListener(c => { if (c.state || c.error) add({ t: 'changed', id: c.id, state: c.state?.current, error: c.error?.current }); });
      d.onErased.addListener(id => add({ t: 'erased', id }));
      native = async req => {
        __native.push(req);
        if (req.action === 'browser-sites') return { ok: true, sites: __sites.splice(0) };
        if (req.action === 'ping') return { ok: true, host: 'com.ndm.bridge' };
        if (__delay) await new Promise(r => setTimeout(r, __delay));
        if (__mode === 'handback') return { ok: false, error: 'The download was handed back to the browser.', cancelled: false, handBack: true };
        if (__mode === 'error') return { ok: false, error: 'disk full' };
        return { ok: true, id: 'stub-job' };
      };
      return 'installed';
    })()`);
    const reset = async () => {
      await inSw(`__ev.length = 0; __native.length = 0; __mode = 'ok'; __sites.length = 0; __delay = 0; chrome.storage.local.set({ browserHosts: [], captureEnabled: true }).then(() => chrome.storage.session.set({ 'neodm.log': [] })).then(() => new Promise(r => chrome.downloads.search({}, items => Promise.all(items.map(i => new Promise(x => chrome.downloads.erase({ id: i.id }, x)))).then(r))))`);
      for (const f of fs.readdirSync(downloads)) fs.rmSync(path.join(downloads, f), { force: true });
    };
    const ev = () => inSw('__ev'), nat = async () => (await inSw('__native')).filter(r => r.action !== 'browser-sites'), logOf = () => inSw(`chrome.storage.session.get('neodm.log').then(r => r['neodm.log'] || [])`);
    const target = (await call('Target.createTarget', { url: 'about:blank' })).targetId, pageS = await attach(target);
    await call('Page.enable', {}, pageS); await call('Runtime.enable', {}, pageS); await call('Network.enable', {}, pageS); await call('Log.enable', {}, pageS);
    await call('Emulation.setDeviceMetricsOverride', { width: 1000, height: 700, deviceScaleFactor: 1, mobile: false }, pageS);
    const inPage = e => evalIn(pageS, e);
    const open = async url => { await call('Page.navigate', { url }, pageS); await sleep(1500); };
    const click = async () => {
      await poll(() => inPage(`document.getElementById('dl')?.dataset.ready === '1'`), 5000, 100);
      const r = await inPage(`(() => { const b = document.getElementById('dl').getBoundingClientRect(); return { x: b.left + b.width / 2, y: b.top + b.height / 2 }; })()`);
      console.log('click at', JSON.stringify(r), 'hit:', await inPage(`(() => { const e = document.elementFromPoint(${r.x}, ${r.y}); return e && e.id; })()`));
      await call('Input.dispatchMouseEvent', { type: 'mouseMoved', x: r.x, y: r.y }, pageS);
      for (const type of ['mousePressed', 'mouseReleased']) await call('Input.dispatchMouseEvent', { type, x: Math.round(r.x), y: Math.round(r.y), button: 'left', buttons: type === 'mousePressed' ? 1 : 0, clickCount: 1 }, pageS);
      if (false) await inPage(`document.getElementById('dl').click(); 1`);
    };
    const mk = async o => { const s = await startMediaFire({ pageHost: 'www.mf.localhost', dlHost: 'download1.mf.localhost', ...o }); servers.push(s); return s; };
    const settle = async (ms = 3500) => { await sleep(ms); };
    const dlFiles = () => fs.readdirSync(downloads).filter(f => !f.endsWith('.crdownload'));

    if (!saveAs) {
    // ---------------- S1: the capture itself (single-use link, the browser's request used the token) ----------------
    { await reset(); const s = await mk();
      await open(s.pageUrl()); if (process.env.NEODM_DEBUG) { inSw(`globalThis.__poll = []; (async () => { for (let i = 0; i < 60; i++) { const items = await new Promise(r => chrome.downloads.search({}, r)); __poll.push(items.map(x => [x.id, x.state, x.error || '', x.bytesReceived, x.totalBytes, x.byExtensionId || ''].join('/'))); await new Promise(r => setTimeout(r, 50)); } })(); 1`); } console.log('S1 page:', await inPage('location.href + " ready=" + document.getElementById("dl")?.dataset.ready + " title=" + document.title'), 'pages', s.stats.pages, JSON.stringify(s.stats.requests.slice(0, 3))); await click(); await settle(); console.log('S1 requests:', JSON.stringify(s.stats.requests.map(r => [r.path.slice(0, 12), r.range, r.ua.slice(0, 20), r.referer.slice(0, 20)])), 'worker globals:', await inSw('typeof __ev + " " + __ev.length + " " + typeof native + " " + chrome.runtime.id')); if (process.env.NEODM_DEBUG) console.log('S1 poll:', JSON.stringify((await inSw('__poll')).filter((x, i, a) => i === 0 || JSON.stringify(x) !== JSON.stringify(a[i - 1])))); console.log('S1 dbg:', JSON.stringify(await inSw(`Promise.all([chrome.storage.session.get(null), chrome.storage.local.get(null)]).then(([a, b]) => ({ session: Object.keys(a).map(k => k + ':' + JSON.stringify(a[k]).slice(0, 300)), local: b, ev: __ev.length, nat: __native.length }))`)).slice(0, 1500)); console.log('S1 items:', JSON.stringify(await inSw(`new Promise(r => chrome.downloads.search({}, r))`)).slice(0, 700)); void ('S1 after click:', await inPage('location.href + " " + document.getElementById("dl").href'), JSON.stringify(s.stats.requests.map(r => r.path.slice(0, 20) + ' ' + r.host)), JSON.stringify(cdpEvents.filter(x => /Download|Page.frame|Network.(requestWillBeSent|loadingFailed)|Log.entryAdded/.test(x.method)).map(x => x.method + ' ' + JSON.stringify(x.params).slice(0, 200))));
      const e = await ev(), n = await nat(), created = e.filter(x => x.t === 'created');
      console.log('S1 events:', JSON.stringify(e.map(x => [x.t, x.id, x.state || x.by || x.error || '']))); console.log('S1 native:', JSON.stringify(n.map(r => ({ ...r, url: r.url.slice(0, 60) + '...' }))));
      check('S1 one download started by the page, one capture request', created.length === 1 && n.length === 1, `created=${created.length} native=${n.length}`);
      check('S1 the request carries the browser size (6 MB) and the name', n[0]?.size === s.size && n[0]?.fileName === 'neodm-test-file.dat' && n[0]?.referer?.startsWith('http://www.mf.localhost:'), JSON.stringify({ size: n[0]?.size, name: n[0]?.fileName, ref: n[0]?.referer }));
      check('S1 the browser download is gone: no file, no history entry, no restart', dlFiles().length === 0 && created.every(x => !x.by) && (await inSw(`new Promise(r => chrome.downloads.search({}, r)).then(a => a.length)`)) === 0, JSON.stringify(dlFiles()));
      check('S1 the log says captured, with the browser size', (await logOf()).at(-1)?.code === 'sent' && (await logOf()).at(-1)?.size === s.size);
      check('S1 the host saw exactly one request for the token (the browser\'s); NeoDM would be the second', s.stats.served === 1 && s.stats.expiredServed === 0, JSON.stringify({ served: s.stats.served, expired: s.stats.expiredServed }));
      await s.close(); }

    // ---------------- S2: Download in browser instead (handBack) on a reusable link: exactly one restart, file exact; the site is remembered ----------------
    { await reset(); const s = await mk({ reusable: true });
      await inSw(`__mode = 'handback'; 1`);
      await open(s.pageUrl()); await click(); await settle(4000);
      const e = await ev(), n = await nat(), created = e.filter(x => x.t === 'created');
      console.log('S2 events:', JSON.stringify(e.map(x => [x.t, x.id, x.state || x.by || x.error || ''])));
      check('S2 one capture request, then exactly one download started by the extension', n.length === 1 && created.length === 2 && created[1].url === created[0].url, `created=${created.length}`);
      const files = dlFiles(); check('S2 the browser delivered the file, exact', files.length === 1 && files[0] === 'neodm-test-file.dat' && sha(path.join(downloads, files[0])) === s.sha256, JSON.stringify(files));
      const status = (await inSw(`chrome.storage.local.get('status')`)).status; check('S2 neutral status text, no error wording', status === 'NeoDM left this download to the browser; it continues there.', status);
      const hosts = (await inSw(`chrome.storage.local.get('browserHosts')`)).browserHosts; check('S2 the site is remembered by its main domain', JSON.stringify(hosts) === JSON.stringify(['mf.localhost']), JSON.stringify(hosts));
      // next download from the same site (another download host name): left to the browser, no capture request, file exact
      const before = (await nat()).length; fs.rmSync(path.join(downloads, 'neodm-test-file.dat'), { force: true });
      await open(s.pageUrl('second')); await click(); await settle(3500);
      const log2 = await logOf(); const f2 = dlFiles();
      check('S2 later capture of that site: left to the browser (no native capture request, log browser-site)', (await nat()).length === before && log2.at(-1)?.code === 'browser-site', JSON.stringify(log2.at(-1)));
      check('S2 and the browser saved it normally (file exact)', f2.length === 1 && sha(path.join(downloads, f2[0])) === s.sha256, JSON.stringify(f2));
      // capture this site again (what the popup button does through the worker)
      await inSw(`removeBrowserHost('www.mf.localhost')`); fs.rmSync(path.join(downloads, 'neodm-test-file.dat'), { force: true });
      await inSw(`__mode = 'ok'; 1`); await open(s.pageUrl('third')); await click(); await settle(3000);
      check('S2 after "Capture this site again" the site is captured again', (await nat()).length === before + 1 && dlFiles().length === 0, `native=${(await nat()).length}`);
      await s.close(); }

    // ---------------- S2b: hand back on a link that is really single use ----------------
    { await reset(); const s = await mk();
      await inSw(`__mode = 'handback'; 1`);
      await open(s.pageUrl()); await click(); await settle(4000);
      const f = dlFiles(); const info = f.length ? { name: f[0], size: fs.statSync(path.join(downloads, f[0])).size, isFile: f.length === 1 && sha(path.join(downloads, f[0])) === s.sha256 } : null;
      console.log('S2b observation (single-use token, browser restarted the download):', JSON.stringify(info), 'served', s.stats.served, 'expired', s.stats.expiredServed);
      check('S2b (observation) a truly single-use link cannot be given back: the restart gets the expired page', !info || info.isFile === false, JSON.stringify(info));
      await s.close(); }

    // ---------------- S3: the same download triggered twice by one click ----------------
    { await reset(); const s = await mk({ twice: true, reusable: true });
      await inSw(`__delay = 2500; 1`);
      await open(s.pageUrl()); await click(); await settle(7000);
      const e = await ev(), n = await nat(), created = e.filter(x => x.t === 'created'), l = await logOf();
      console.log('S3 events:', JSON.stringify(e.map(x => [x.t, x.id, x.state || x.by || x.error || '']))); console.log('S3 log:', JSON.stringify(l.map(x => x.code)));
      console.log('S3 observation: Chrome 155 reported', created.length, 'download(s) for one click that starts the same address twice (navigation and hidden frame)');
      check('S3 one capture request, the second start is dropped silently, nothing restarted', n.length === 1 && created.length === (l.some(x => x.code === 'duplicate') ? 2 : 1) && dlFiles().length === 0, JSON.stringify({ native: n.length, codes: l.map(x => x.code), files: dlFiles() }));
      await s.close(); }

    // ---------------- S4: the app answers with an error after the dialog: the old restart text, one restart ----------------
    { await reset(); const s = await mk({ reusable: true });
      await inSw(`__mode = 'error'; 1`);
      await open(s.pageUrl()); await click(); await settle(4000);
      const created = (await ev()).filter(x => x.t === 'created'); const status = (await inSw(`chrome.storage.local.get('status')`)).status;
      check('S4 error: one restart in the browser, the error text, the site is not remembered', created.length === 2 && /restarted in the browser/.test(status) && !(await inSw(`chrome.storage.local.get('browserHosts')`)).browserHosts?.length, status);
      await s.close(); }

    // ---------------- S5: the app reports a site (wrong-content later): the next capture of it is left alone ----------------
    { await reset(); const s = await mk({ reusable: true });
      await inSw(`__sites.push('download1.mf.localhost'); 1`);
      await open(s.pageUrl()); await click(); await settle(3500);
      const n = await nat(), l = await logOf();
      check('S5 site reported by the app: not captured, the browser keeps the download (file exact)', n.length === 0 && l.at(-1)?.code === 'browser-site' && dlFiles().length === 1 && sha(path.join(downloads, dlFiles()[0])) === s.sha256, JSON.stringify({ native: n.length, code: l.at(-1)?.code, files: dlFiles() }));
      await s.close(); }

    }
    // ---------------- Save as experiment ----------------
    if (saveAs) {
      await reset(); const s = await mk({ reusable: true });
      await open(s.pageUrl()); await click(); await sleep(4000);
      const e = await ev();
      console.log('SAVEAS events:', JSON.stringify(e.map(x => [x.t, x.id, x.state || x.by || x.error || ''])));
      const wins = spawnSync('powershell', ['-NoProfile', '-Command', `Add-Type -TypeDefinition 'using System;using System.Text;using System.Runtime.InteropServices;public class W{public delegate bool P(IntPtr h,IntPtr l);[DllImport("user32.dll")]public static extern bool EnumWindows(P p,IntPtr l);[DllImport("user32.dll")]public static extern int GetWindowText(IntPtr h,StringBuilder s,int n);[DllImport("user32.dll")]public static extern bool IsWindowVisible(IntPtr h);[DllImport("user32.dll")]public static extern int GetClassName(IntPtr h,StringBuilder s,int n);}'; $r=@(); [W]::EnumWindows({param($h,$l) if([W]::IsWindowVisible($h)){$t=New-Object Text.StringBuilder 256;$c=New-Object Text.StringBuilder 256;[void][W]::GetWindowText($h,$t,256);[void][W]::GetClassName($h,$c,256); if($t.Length -gt 0){$script:r+=($c.ToString()+'|'+$t.ToString())}}; $true},[IntPtr]::Zero)|Out-Null; $r -join [Environment]::NewLine`], { encoding: 'utf8' }).stdout.split(/\r?\n/).filter(x => x.startsWith('#32770|'));
      console.log('SAVEAS visible windows:', JSON.stringify(wins));
      check('SAVEAS no Save as window is left open after a captured download', wins.length === 0, JSON.stringify(wins));
      await s.close();
    }
  } catch (e) { console.log('ERROR', e.stack || e.message); results.push(false); }
  try { child.kill(); } catch {} for (const s of servers) { try { await s.close(); } catch {} } await sleep(500);
  try { fs.rmSync(profile, { recursive: true, force: true }); fs.rmSync(downloads, { recursive: true, force: true }); } catch {}
  console.log(results.every(Boolean) ? `ALL ${results.length} CHECKS PASSED` : `FAILED (${results.filter(x => !x).length} of ${results.length})`);
  process.exit(results.every(Boolean) ? 0 : 1);
})();
