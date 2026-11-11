#!/usr/bin/env node
'use strict';
// Usage: node tools/build-store-zip.cjs [output.zip]
// Builds the store variant from extension/: the development manifest key is removed; page-link handoff is included.
// (the store assigns the extension ID; the key belongs to the development/unpacked copy). Default output: dist/NeoDM-extension-store.zip.
// The store ID this package gets is recorded in tools/extension-store-id.txt (trusted by the host, installer, watchdog and app since 1.5.6).
const fs = require('node:fs'), path = require('node:path'), crypto = require('node:crypto');
const { buildZip } = require('./crx3.cjs');
const root = path.resolve(__dirname, '..'), extDir = path.join(root, 'extension');
const ALLOWED = [/^manifest\.json$/, /^(background|bridge|sniff|subs|content|hooks|popup|i18n)\.js$/, /^popup\.(html|css)$/, /^icons\/(16|48|128)\.png$/, /^_locales\/[a-z]{2,3}(_[A-Za-z]{2,4})?\/messages\.json$/];
const walk = (dir, base = '') => fs.readdirSync(dir, { withFileTypes: true }).flatMap(e => e.isDirectory() ? walk(path.join(dir, e.name), base + e.name + '/') : [base + e.name]);

/** manifest.json text without the top-level "key" line (formatting of every other line is kept). */
function storeManifest(text) {
  const out = text.replace(/^[ \t]*"key"\s*:\s*"[^"]*",?[ \t]*\r?\n/m, '').replace(/("version_name"\s*:\s*")[^"]*(")/, '$1' + JSON.parse(text).version + ' store$2');
  const parsed = JSON.parse(out);
  if ('key' in parsed) throw new Error('manifest key was not removed');
  return out;
}
// Feature parity is deliberate. The store package omits only the development manifest key.
function storeFile(name, data) {
  if (name === 'manifest.json') return Buffer.from(storeManifest(data.toString('utf8')));
  return data;
}
function buildStoreZip() {
  const names = walk(extDir);
  const extra = names.filter(n => !ALLOWED.some(r => r.test(n)));
  if (extra.length) throw new Error('Non-runtime files in extension/: ' + extra.join(', '));
  const files = names.map(name => ({ name, data: storeFile(name, fs.readFileSync(path.join(extDir, name))) }));
  return { zip: buildZip(files), names };
}
module.exports = { storeManifest, storeFile, buildStoreZip };
if (require.main === module) {
  const out = path.resolve(process.argv[2] || path.join(root, 'dist', 'NeoDM-extension-store.zip'));
  const { zip, names } = buildStoreZip();
  fs.mkdirSync(path.dirname(out), { recursive: true }); fs.writeFileSync(out, zip);
  const version = JSON.parse(fs.readFileSync(path.join(extDir, 'manifest.json'), 'utf8')).version;
  console.log(`wrote ${out} (${zip.length} bytes, ${names.length} files, version ${version}, no key), sha256 ${crypto.createHash('sha256').update(zip).digest('hex')}`);
}
