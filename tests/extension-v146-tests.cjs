'use strict';
/* 1.4.6: the extension manifest, the changelog's top entry and Directory.Build.props carry the same version. */
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const root = path.join(__dirname, '..');
test('manifest, changelog and Directory.Build.props agree on 1.4.6', () => {
  const props = fs.readFileSync(path.join(root, 'Directory.Build.props'), 'utf8');
  assert.equal(/<Version>([^<]+)<\/Version>/.exec(props)[1], '1.4.6');
  assert.equal(JSON.parse(fs.readFileSync(path.join(root, 'extension/manifest.json'), 'utf8')).version, '1.4.6');
  assert.match(fs.readFileSync(path.join(root, 'CHANGELOG.md'), 'utf8'), /^## \[1\.4\.6\] - 2026-10-18$/m);
});
