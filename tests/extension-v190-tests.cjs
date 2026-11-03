'use strict';
// 1.9.0: the "protected streaming format" message is gone everywhere; the Send to NeoDM path stays the only YouTube action.
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs'), path = require('node:path'), zlib = require('node:zlib');
const { makeBackground, response, tick } = require('./extension-harness.cjs');
const S = require('../extension/sniff.js');
const root = path.join(__dirname, '..');
const gv = (itag, mime, clen) => response(`https://rr1---sn-abc.googlevideo.com/videoplayback?itag=${itag}&mime=${encodeURIComponent(mime)}&clen=${clen}&id=o1&expire=9`, mime, null, { initiator: 'https://www.youtube.com', tabId: 7 });
function unpack(zip) {
  let p = 0; const out = {};
  while (zip.readUInt32LE(p) === 0x04034b50) {
    const method = zip.readUInt16LE(p + 8), size = zip.readUInt32LE(p + 18), nl = zip.readUInt16LE(p + 26), el = zip.readUInt16LE(p + 28);
    const name = zip.toString('utf8', p + 30, p + 30 + nl), begin = p + 30 + nl + el, data = zip.subarray(begin, begin + size);
    out[name] = (method === 8 ? zlib.inflateRawSync(data) : data).toString('utf8'); p = begin + size;
  }
  return out;
}
const banned = /protected streaming|protected format|media_youtube|vb_reason_youtube|youtubeNote/i;
test('no extension source or locale file carries the removed YouTube message', () => {
  for (const f of ['sniff.js', 'content.js', 'popup.js', 'background.js', 'popup.html']) assert.doesNotMatch(fs.readFileSync(path.join(root, 'extension', f), 'latin1'), banned, f);
  for (const l of fs.readdirSync(path.join(root, 'extension/_locales'))) assert.doesNotMatch(fs.readFileSync(path.join(root, 'extension/_locales', l, 'messages.json'), 'utf8'), banned, l);
});
test('the real store zip does not carry it either and still ships the YouTube send handoff', () => {
  const { buildStoreZip } = require('../tools/build-store-zip.cjs'); const files = unpack(buildStoreZip().zip);
  assert.ok(Object.keys(files).length > 10);
  for (const [name, text] of Object.entries(files)) assert.doesNotMatch(text, banned, 'store zip ' + name);
  assert.match(files['popup.js'], /youtube-send/); assert.match(files['content.js'], /youtube-ribbon/);
});
test('YouTube watch page with fragment traffic: popup and ribbon lists are empty (no message row), the menu has no entry', async () => {
  const yt = { id: 7, url: 'https://www.youtube.com/watch?v=aqz-KE-bpKQ', title: 'x - YouTube' };
  const b = makeBackground({ openTabs: [yt] });
  b.hooks.tabUpdated(7, { url: yt.url }); await tick();
  b.hooks.headers(gv(137, 'video/mp4', 9e6)); b.hooks.headers(gv(251, 'audio/webm', 3e6)); await tick();
  const popup = (await b.message({ action: 'popup-media', tabId: 7 })).items, ribbon = (await b.message({ action: 'media-list' }, b.pageSender(yt))).items;
  assert.deepEqual(JSON.parse(JSON.stringify(popup)), []); assert.deepEqual(JSON.parse(JSON.stringify(ribbon)), []);
  assert.equal(b.menuUpdates.at(-1)?.visible ?? false, false);
});
test('a YouTube page with only a blob player and the home page also list nothing', async () => {
  for (const url of ['https://www.youtube.com/watch?v=aqz-KE-bpKQ', 'https://www.youtube.com/']) {
    const page = { id: 7, url, title: 'x' }, b = makeBackground({ openTabs: [page] });
    b.hooks.tabUpdated(7, { url }); await tick();
    await b.message({ action: 'media-dom', sources: [{ url: 'blob:', tag: 'video', title: 'x', playing: true, vis: 1 }] }, b.pageSender(page)); await tick();
    assert.deepEqual(JSON.parse(JSON.stringify((await b.message({ action: 'media-list' }, b.pageSender(page))).items.filter(i => i.info))), [], url);
  }
});
test('the send path itself is unchanged: canonical watch and shorts links are still recognised', () => {
  assert.equal(S.youtubePage('https://www.youtube.com/watch?v=aqz-KE-bpKQ&t=3'), 'https://www.youtube.com/watch?v=aqz-KE-bpKQ');
  assert.equal(S.youtubePage('https://www.youtube.com/shorts/aqz-KE-bpKQ'), 'https://www.youtube.com/watch?v=aqz-KE-bpKQ');
  assert.equal(S.youtubePage('https://www.youtube.com/'), '');
});
