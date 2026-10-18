'use strict';
/* 1.4.6: the changelog keeps the 1.4.6 entry (the version agreement test moves to the newest release file). */
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const root = path.join(__dirname, '..');
test('the changelog keeps the 1.4.6 entry', () => {
  assert.match(fs.readFileSync(path.join(root, 'CHANGELOG.md'), 'utf8'), /^## \[1\.4\.6\] - 2026-10-18$/m);
});
