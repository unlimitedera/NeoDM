'use strict';
/* The extension manifest, the changelog's top entry and Directory.Build.props carry the same version. */
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const root = path.join(__dirname, '..');
test('manifest, changelog and Directory.Build.props carry the same version', () => {
  const props = fs.readFileSync(path.join(root, 'Directory.Build.props'), 'utf8');
  const version = /<Version>([^<]+)<\/Version>/.exec(props)[1];
  assert.equal(JSON.parse(fs.readFileSync(path.join(root, 'extension/manifest.json'), 'utf8')).version, version);
  assert.match(fs.readFileSync(path.join(root, 'CHANGELOG.md'), 'utf8'), new RegExp('^## \\[' + version.replace(/\./g, '\\.') + '\\] - ', 'm'));
});
