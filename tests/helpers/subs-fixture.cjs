'use strict';
// 1.14.0 test fixture: a tiny HLS stream made with the bundled ffmpeg from a generated test pattern, plus a SEPARATE WebVTT file that the playlist does not list,
// and two pages: one whose player loads the subtitle file and one that does not. Local only: nothing here talks to a real site.
const http = require('node:http'), fs = require('node:fs'), path = require('node:path'), { spawnSync } = require('node:child_process');
const root = path.resolve(__dirname, '..', '..');
const ffmpeg = process.env.NEODM_FFMPEG || path.join(root, 'dist', 'app', 'tools', 'ffmpeg', 'ffmpeg.exe');

const VTT = {
  en: 'WEBVTT\nX-TIMESTAMP-MAP=MPEGTS:900000,LOCAL:00:00:00.000\n\nNOTE made for the NeoDM test\n\n1\n00:00:00.500 --> 00:00:01.500 align:start position:10%\nHello <b>there</b>, fixture\n\n2\n00:00:02.000 --> 00:00:03.000\nSecond cue\nwith two lines\n',
  ar: 'WEBVTT\n\n00:00:00.500 --> 00:00:01.500\nمرحبا\n',
};

function makeStream(dir) {
  fs.mkdirSync(dir, { recursive: true });
  const r = spawnSync(ffmpeg, ['-hide_banner', '-loglevel', 'error', '-y', '-f', 'lavfi', '-i', 'testsrc=size=160x120:rate=10', '-f', 'lavfi', '-i', 'sine=frequency=440:sample_rate=22050', '-t', '4',
    '-c:v', 'libx264', '-preset', 'ultrafast', '-pix_fmt', 'yuv420p', '-g', '10', '-c:a', 'aac', '-b:a', '32k', '-f', 'hls', '-hls_time', '2', '-hls_playlist_type', 'vod', '-hls_segment_filename', path.join(dir, 'seg%d.ts'), path.join(dir, 'index.m3u8')], { encoding: 'utf8' });
  if (r.status !== 0) throw new Error('ffmpeg could not make the fixture stream: ' + (r.stderr || r.error));
}

const page = (withSubs, label) => `<!doctype html><meta charset="utf-8"><title>${label}</title><body style="margin:0;background:#222"><video id="v" controls muted width="640" height="360" style="display:block;margin:20px"></video>
<script>
  // a player that reads its playlist itself and, only on the page that has subtitles, loads a subtitle file the playlist never lists
  (async () => {
    const base = '${'${BASE}'}';
    document.getElementById('v').src = base + '/hls/index.m3u8'; // the media element asks for the playlist itself, as a player does
    const pl = await fetch(base + '/hls/index.m3u8').then(r => r.text());
    document.title = 'playlist ' + pl.length;
    ${withSubs ? `await new Promise(r => setTimeout(r, 300)); await fetch(base + '/subs/en.vtt').then(r => r.text()); document.title += ' subs';` : ''}
  })();
</script>`;

/** Starts the server on a free port. Hosts: page and stream both answer on any *.localhost name, so the page and the stream can be on different origins. */
async function start({ dir = path.join(process.env.TEMP || require('node:os').tmpdir(), 'neodm-subs-fixture-' + process.pid) } = {}) {
  makeStream(path.join(dir, 'hls'));
  const requests = [];
  const server = http.createServer((req, res) => {
    const url = new URL(req.url, 'http://x'), p = url.pathname;
    requests.push({ path: p, referer: req.headers.referer || '', ua: req.headers['user-agent'] || '', cookie: req.headers.cookie || '', host: req.headers.host });
    const send = (code, type, body, extra = {}) => { res.writeHead(code, { 'Content-Type': type, 'Access-Control-Allow-Origin': '*', ...extra }); res.end(body); };
    const origin = `http://${req.headers.host}`;
    if (p === '/with-subs.html' || p === '/with-subs-again.html') return send(200, 'text/html; charset=utf-8', page(true, 'with subs').replace('${BASE}', 'http://cdn.localhost:' + server.address().port));
    if (p === '/no-subs.html') return send(200, 'text/html; charset=utf-8', page(false, 'no subs').replace('${BASE}', 'http://cdn.localhost:' + server.address().port));
    let m;
    if ((m = /^\/subs\/([a-z]{2})\.vtt$/.exec(p)) && VTT[m[1]]) return send(200, 'text/vtt; charset=utf-8', VTT[m[1]], { 'Cache-Control': 'max-age=3600' }); // cacheable: the second page view is served from the browser cache
    if ((m = /^\/hls\/([A-Za-z0-9_.]+)$/.exec(p)) && fs.existsSync(path.join(dir, 'hls', m[1]))) return send(200, m[1].endsWith('.m3u8') ? 'application/vnd.apple.mpegurl' : 'video/mp2t', m[1].endsWith('.m3u8') ? fs.readFileSync(path.join(dir, 'hls', m[1]), 'utf8').replace(/#EXTINF:2\.000000,/g, '#EXTINF:2.045000,') : fs.readFileSync(path.join(dir, 'hls', m[1]))); // the encoded audio runs a few frames longer than 2.000 s per segment
    send(404, 'text/plain', 'not found');
  });
  await new Promise(r => server.listen(0, '127.0.0.1', r));
  const port = server.address().port;
  return { port, dir, requests, pageUrl: name => `http://video.localhost:${port}/${name}`, streamUrl: `http://cdn.localhost:${port}/hls/index.m3u8`, vtt: VTT, stop: () => new Promise(r => server.close(r)) };
}
module.exports = { start, VTT };
if (require.main === module) start().then(f => console.log('fixture on port', f.port, f.dir));
