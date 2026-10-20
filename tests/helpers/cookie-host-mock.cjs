'use strict';
/* Local imitation of the MediaFire download chain that needs the browser session cookie, for tests.
 *   GET /file/<key>/<name>/file     page with a Download link (its href is filled in by script after 400 ms); sets the HttpOnly cookie g2cn=1 (Path=/, optional Domain)
 *                                   and a second cookie ukey=<random>
 *   GET /dl/<token>/<name>          the file (6 MB, Content-Disposition, Range support) ONLY when the Cookie header contains g2cn=1;
 *                                   otherwise 302 to /download_repair.php?flag=2&dkey=... on the page host
 *   GET /download_repair.php        a 32 KB text/html "Repair your download" page
 * Options: viaRedirect (the page link is /r/<token>/<name>, a 302 to the /dl/ address on dlHost), pageHost / dlHost (host names, no port), cookieDomain (Domain= attribute of the cookies), size, name.
 * stats.dl lists every /dl request with the Cookie header the client sent, so a test can prove what the browser (or the app) sent. */
const http = require('node:http');
const crypto = require('node:crypto');
const { fileBytes } = require('./mediafire-mock.cjs');

function repairPage() {
  let body = '<!DOCTYPE html><html><head><title>Repair your download</title></head><body><h1>Repair your download</h1>';
  const pad = '<!-- ' + 'x'.repeat(100) + ' -->\n';
  while (Buffer.byteLength(body) < 32768) body += pad;
  return Buffer.from(body.slice(0, 32768 - '</body></html>'.length) + '</body></html>');
}

async function startCookieHost(options = {}) {
  const size = options.size ?? 6 * 1024 * 1024, name = options.name ?? 'test-payload.dat';
  const body = fileBytes(size), repair = repairPage();
  const stats = { pages: 0, dl: [], served: 0, repairRedirects: 0, repairPages: 0, redirectHops: 0 };
  let counter = 0;
  const server = http.createServer((req, res) => {
    const url = new URL(req.url, 'http://x');
    const m = /^\/file\/([^/]+)\/([^/]+)\/file$/.exec(url.pathname);
    if (m) {
      stats.pages++;
      const token = 't' + (++counter) + crypto.randomBytes(48).toString('hex'); // long token-like segment
      const dlHost = options.dlHost ? `${options.dlHost}:${server.address().port}` : req.headers.host;
      const href = `http://${options.viaRedirect ? (options.pageHost ? options.pageHost + ':' + server.address().port : req.headers.host) + '/r/' : dlHost + '/dl/'}${token}/${name}`;
      const domain = options.cookieDomain ? `; Domain=${options.cookieDomain}` : '';
      const html = `<!DOCTYPE html><html><head><meta charset="utf-8"><title>${name} - cookie host test</title></head><body>
<a id="dl" href="javascript:void(0)" style="display:block;width:300px;height:60px;background:#1e90ff;color:#fff;font:20px sans-serif">Download (6 MB)</a>
<script>setTimeout(function(){ var a=document.getElementById('dl'); a.href=${JSON.stringify(href)}; a.dataset.ready='1'; }, 400);</script></body></html>`;
      res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8', 'Set-Cookie': [`g2cn=1; Path=/; HttpOnly${domain}`, `ukey=${crypto.randomBytes(8).toString('hex')}; Path=/${domain}`] });
      res.end(html); return;
    }
    if (url.pathname === '/download_repair.php') {
      stats.repairPages++;
      res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8', 'Content-Length': repair.length }); res.end(req.method === 'HEAD' ? undefined : repair); return;
    }
    const r = /^\/r\/([^/]+)\/([^/]+)$/.exec(url.pathname);
    if (r) { stats.redirectHops++; res.writeHead(302, { Location: `http://${options.dlHost ? options.dlHost + ':' + server.address().port : req.headers.host}/dl/${r[1]}/${r[2]}` }); res.end(); return; } // a first hop that sends the browser to the file server
    const d = /^\/dl\/([^/]+)\/([^/]+)$/.exec(url.pathname);
    if (d && (req.method === 'GET' || req.method === 'HEAD')) {
      const cookie = req.headers.cookie || '';
      stats.dl.push({ method: req.method, range: req.headers.range || '', cookie, referer: req.headers.referer || '', host: req.headers.host || '' });
      if (!/(?:^|;\s*)g2cn=1(?:;|$)/.test(cookie)) {
        stats.repairRedirects++;
        res.writeHead(302, { Location: `http://${options.pageHost ? options.pageHost + ':' + server.address().port : req.headers.host}/download_repair.php?flag=2&dkey=${crypto.randomBytes(8).toString('hex')}&qkey=q&ip=127.0.0.1` }); res.end(); return;
      }
      stats.served++;
      const headers = { 'Content-Type': 'application/octet-stream', 'Content-Disposition': `attachment; filename="${name}"`, 'ETag': '"cookie-host"', 'Last-Modified': 'Wed, 01 Jan 2020 00:00:00 GMT', 'Accept-Ranges': 'bytes' };
      const range = /^bytes=(\d*)-(\d*)$/.exec(req.headers.range || '');
      if (range) {
        const start = range[1] === '' ? size - Number(range[2]) : Number(range[1]), end = range[1] === '' || range[2] === '' ? size - 1 : Math.min(size - 1, Number(range[2]));
        res.writeHead(206, { ...headers, 'Content-Range': `bytes ${start}-${end}/${size}`, 'Content-Length': end - start + 1 });
        res.end(req.method === 'HEAD' ? undefined : body.subarray(start, end + 1)); return;
      }
      res.writeHead(200, { ...headers, 'Content-Length': size }); res.end(req.method === 'HEAD' ? undefined : body); return;
    }
    res.writeHead(404); res.end('not found');
  });
  await new Promise(r => server.listen(options.port ?? 0, '127.0.0.1', r));
  const port = server.address().port;
  return { port, pageUrl: key => `http://${options.pageHost || '127.0.0.1'}:${port}/file/${key || 'abc123'}/${name}/file`, name, size, body, stats, close: () => new Promise(r => { server.closeAllConnections?.(); server.close(r); }) };
}
module.exports = { startCookieHost, repairPage };
