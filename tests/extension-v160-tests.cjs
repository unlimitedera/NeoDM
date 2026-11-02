'use strict';
/* 1.6.0 (part 1): streaming downloads in the engine. Version agreement, changelog wording and the documents that must exist and agree with the pinned tool. */
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const root = path.join(__dirname, '..');
const read = p => fs.readFileSync(path.join(root, p), 'utf8');

test('props, manifest and changelog name 1.6.0 or later; the entry is plain and has no em dashes', () => {
  const version = /<Version>([^<]+)<\/Version>/.exec(read('Directory.Build.props'))[1];
  const [major, minor] = version.split('.').map(Number);
  assert.ok(major > 1 || minor >= 6, 'version ' + version);
  assert.equal(JSON.parse(read('extension/manifest.json')).version, version);
  const log = read('CHANGELOG.md');
  const start = log.indexOf('## [1.6.0]'); assert.ok(start > 0, 'the 1.6.0 entry exists');
  const next = log.indexOf('\n## [', start + 5), entry = log.slice(start, next < 0 ? undefined : next);
  assert.match(entry, /HLS and DASH/); assert.doesNotMatch(entry, /[\u2014\u2013]/);
  assert.match(read('README.md'), /^\| 1\.6\.0 \| /m);
});

test('stream and dependency docs agree with pinned shared helper bytes, license and verified source gate', () => {
  const pin = read('src/NDM.Core/ToolManager.cs');
  const url = /"(https:\/\/github\.com\/NevermindNilas\/TAS-FFMPEG\/releases\/download\/[^"]+\.zip)"/.exec(pin)[1];
  const hashes = [...pin.matchAll(/"([0-9a-f]{64})"/g)].map(m => m[1]);
  assert.equal(hashes.length, 2);
  const third = read('docs/THIRD-PARTY.md');
  assert.ok(third.includes(url)); for (const h of hashes) assert.ok(third.includes(h), h);
  assert.match(third, /GNU General Public License version 2 or later/);
  assert.match(third, /tas-ffmpeg-9\.0\.2-corresponding-source\.tar\.xz/);
  assert.match(third, /seven private DLLs/);
  assert.match(read('tools/provision-bundled-tools.py'), /runtimeHashes/);
  assert.match(third, /BUNDLE-VERIFICATION\.md/);
  const streams = read('docs/STREAMS.md');
  for (const word of ['needs-muxer', 'mux-failed', 'stream-drm', 'stream-live', 'AES-128', '--real-ffmpeg', 'SegmentTimeline']) assert.ok(streams.includes(word), word);
  for (const f of ['docs/STREAMS.md', 'docs/THIRD-PARTY.md']) assert.doesNotMatch(read(f), /[\u2014]/, f);
  assert.match(read('SECURITY.md'), /Streaming downloads and the helper tool \(1\.6\.0\)/);
});

test('helper binaries are provisioned at build time rather than checked into repository sources', () => {
  const skip = new Set(['node_modules', 'bin', 'obj', 'artifacts', '.git', 'dist', 'keys']);
  const found = [];
  (function walk(dir) {
    for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
      if (e.isDirectory()) { if (!skip.has(e.name)) walk(path.join(dir, e.name)); }
      else if (/^ffmpeg.*\.(exe|dll)$/i.test(e.name)) found.push(path.join(dir, e.name));
    }
  })(root);
  assert.deepEqual(found, []);
  assert.doesNotMatch(read('installer/NeoDM.wxs'), /ffmpeg/i);
  assert.match(read('build-installer.ps1'), /provision-bundled-tools\.py/);
  assert.match(read('build-installer.ps1'), /--verify --output \$AppDir/);
  const lock = JSON.parse(read('tools/bundled-tools.lock.json'));
  assert.deepEqual(lock.tools.map(t => t.name), ['yt-dlp', 'deno', 'ffmpeg']);
  assert.equal(lock.tools.find(t => t.name === 'ffmpeg').exeSha256, hashesForFfmpeg());
});
function hashesForFfmpeg() {
  return [...read('src/NDM.Core/ToolManager.cs').matchAll(/"([0-9a-f]{64})"/g)].at(-1)[1];
}
