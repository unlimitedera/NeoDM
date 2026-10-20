'use strict';
/* Local imitation of a file host with one-time download links (the MediaFire pattern), for tests.
 *   GET /file/<key>/<name>/file   page with a Download button; its href is filled in by script after 600 ms with /dl/<token>/<name>
 *   GET /dl/<token>/<name>        the file (6 MB, Content-Disposition: attachment) on the FIRST request of a token; later requests of the same token get a
 *                                 32 KB text/html "link expired" page with status 200.
 * Options:
 *   cookie:true      /dl/ also needs the cookie mfsession=ok, set by the page response (Domain= the parent host name), otherwise it answers the expired page
 *   noRange:true     Range requests are answered with 200 and the full body (no resume)
 *   reusable:true    the token never expires (control case for the engine)
 *   size             file size in bytes (default 6 MB)
 *   name             file name (default neodm-test-file.dat)
 *   twice:true       the page's click handler starts the same download twice (navigation plus hidden frame)
 *   pageHost/dlHost  host NAMES (no port) used for the page address and in the download link (default 127.0.0.1 / the request host)
 */
const http = require('node:http');
const crypto = require('node:crypto');

function fileBytes(size) {
  const buffer = Buffer.alloc(size);
  let seed = 0x9e3779b9;
  for (let i = 0; i < size; i++) { seed = (Math.imul(seed, 1664525) + 1013904223) >>> 0; buffer[i] = seed >>> 24; }
  buffer[0] = 0x4e; buffer[1] = 0x44; buffer[2] = 0x4d; buffer[3] = 0x00; // binary, never like a web page
  return buffer;
}
const sha256 = buffer => crypto.createHash('sha256').update(buffer).digest('hex');

function expiredPage() {
  const head = '<!DOCTYPE html><html><head><title>Link expired</title></head><body><h1>This download link is not valid or has expired.</h1>';
  const pad = '<!-- ' + 'x'.repeat(100) + ' -->\n';
  let body = head;
  while (Buffer.byteLength(body) < 32768 - 20) body += pad;
  body = body.slice(0, 32768 - '</body></html>'.length) + '</body></html>';
  return Buffer.from(body);
}

async function startMediaFire(options = {}) {
  const size = options.size ?? 6 * 1024 * 1024, name = options.name ?? 'neodm-test-file.dat';
  const body = fileBytes(size), expired = expiredPage();
  const stats = { pages: 0, requests: [], served: 0, expiredServed: 0 };
  const used = new Set();
  let counter = 0;
  const server = http.createServer((req, res) => {
    const url = new URL(req.url, 'http://x');
    const record = { path: url.pathname, method: req.method, range: req.headers.range || '', cookie: req.headers.cookie || '', referer: req.headers.referer || '', ua: req.headers['user-agent'] || '', host: req.headers.host || '' };
    stats.requests.push(record);
    const m = /^\/file\/([^/]+)\/([^/]+)\/file$/.exec(url.pathname);
    if (m) {
      stats.pages++;
      const token = 't' + (++counter) + crypto.randomBytes(24).toString('hex'); // long, token-like path segment as on the real site
      const dlHost = options.dlHost ? `${options.dlHost}:${server.address().port}` : req.headers.host;
      const href = `http://${dlHost}/dl/${token}/${name}`;
      const twice = options.twice ? `var f=document.createElement('iframe');f.style.display='none';f.src=${JSON.stringify(href)};document.body.appendChild(f);` : '';
      const html = `<!DOCTYPE html><html><head><meta charset="utf-8"><title>${name} - MediaFire test</title></head><body>
<h1>${name}</h1>
<a id="dl" class="input popsok" href="javascript:void(0)" style="display:block;width:300px;height:60px;background:#1e90ff;color:#fff;font:20px sans-serif" data-scrambled-url="x">Download (6 MB)</a>
<script>
setTimeout(function(){ var a=document.getElementById('dl'); a.href=${JSON.stringify(href)}; a.dataset.ready='1'; }, 600);
document.getElementById('dl').addEventListener('click', function(){ if (this.dataset.ready==='1') { ${twice} } });
</script></body></html>`;
      const headers = { 'Content-Type': 'text/html; charset=utf-8' };
      if (options.cookie) headers['Set-Cookie'] = `mfsession=ok; Path=/${options.cookieDomain ? '; Domain=' + options.cookieDomain : ''}`;
      res.writeHead(200, headers); res.end(html); return;
    }
    const d = /^\/dl\/([^/]+)\/([^/]+)$/.exec(url.pathname);
    if (d && (req.method === 'GET' || req.method === 'HEAD')) {
      const token = d[1];
      const first = !used.has(token);
      if (!options.reusable) used.add(token);
      const cookieOk = !options.cookie || /(?:^|;\s*)mfsession=ok(?:;|$)/.test(req.headers.cookie || '');
      if ((first || options.reusable) && cookieOk) {
        stats.served++;
        const headers = { 'Content-Type': 'application/octet-stream', 'Content-Disposition': `attachment; filename="${name}"`, 'ETag': '"mf-etag"', 'Last-Modified': 'Wed, 01 Jan 2020 00:00:00 GMT' };
        const range = /^bytes=(\d*)-(\d*)$/.exec(req.headers.range || '');
        if (range && !options.noRange) {
          const start = range[1] === '' ? size - Number(range[2]) : Number(range[1]), end = range[1] === '' || range[2] === '' ? size - 1 : Math.min(size - 1, Number(range[2]));
          res.writeHead(206, { ...headers, 'Accept-Ranges': 'bytes', 'Content-Range': `bytes ${start}-${end}/${size}`, 'Content-Length': end - start + 1 });
          if (req.method === 'HEAD') return res.end();
          res.end(body.subarray(start, end + 1)); return;
        }
        res.writeHead(200, { ...headers, ...(options.noRange ? {} : { 'Accept-Ranges': 'bytes' }), 'Content-Length': size });
        if (req.method === 'HEAD') return res.end();
        res.end(body); return;
      }
      stats.expiredServed++;
      res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8', 'Content-Length': expired.length });
      if (req.method === 'HEAD') return res.end();
      res.end(expired); return;
    }
    res.writeHead(404); res.end('not found');
  });
  await new Promise(r => server.listen(options.port ?? 0, '127.0.0.1', r));
  const port = server.address().port;
  return { port, origin: `http://127.0.0.1:${port}`, pageUrl: key => `http://${options.pageHost || '127.0.0.1'}:${port}/file/${key || 'abc123'}/${name}/file`, name, size, body, sha256: sha256(body), stats, used, close: () => new Promise(r => { server.closeAllConnections?.(); server.close(r); }) };
}
module.exports = { startMediaFire, fileBytes, expiredPage, sha256 };
