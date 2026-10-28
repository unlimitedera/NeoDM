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

test('docs/STREAMS.md and docs/THIRD-PARTY.md exist, agree with the pinned tool and carry the licence and source offer', () => {
  const pin = read('src/NDM.Core/ToolManager.cs');
  const url = /"(https:\/\/www\.gyan\.dev\/ffmpeg\/builds\/packages\/[^"]+\.zip)"/.exec(pin)[1];
  const hashes = [...pin.matchAll(/"([0-9a-f]{64})"/g)].map(m => m[1]);
  assert.equal(hashes.length, 2);
  const third = read('docs/THIRD-PARTY.md');
  assert.ok(third.includes(url)); for (const h of hashes) assert.ok(third.includes(h), h);
  assert.match(third, /GNU General Public License version 3/); assert.match(third, /source code offer/i); assert.match(third, /redistributed by NeoDM/);
  const streams = read('docs/STREAMS.md');
  for (const word of ['needs-muxer', 'mux-failed', 'stream-drm', 'stream-live', 'AES-128', '--real-ffmpeg', 'SegmentTimeline']) assert.ok(streams.includes(word), word);
  for (const f of ['docs/STREAMS.md', 'docs/THIRD-PARTY.md']) assert.doesNotMatch(read(f), /[\u2014]/, f);
  assert.match(read('SECURITY.md'), /Streaming downloads and the helper tool \(1\.6\.0\)/);
});

test('the tool is never bundled: no ffmpeg binary or installer entry in the repository sources', () => {
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
});
