#!/usr/bin/env node
'use strict';
// Usage: node tools/build-store-zip.cjs [output.zip]
// Builds the store variant from extension/: manual-only handoff blocks and the manifest key are removed.
// (the store assigns the extension ID; the key belongs to the development/unpacked copy). Default output: dist/NeoDM-extension-store.zip.
// The store ID this package gets is recorded in tools/extension-store-id.txt (trusted by the host, installer, watchdog and app since 1.5.6).
const fs = require('node:fs'), path = require('node:path'), crypto = require('node:crypto');
const { buildZip } = require('./crx3.cjs');
const root = path.resolve(__dirname, '..'), extDir = path.join(root, 'extension');
const ALLOWED = [/^manifest\.json$/, /^(background|bridge|sniff|content|hooks|popup|i18n)\.js$/, /^popup\.(html|css)$/, /^icons\/(16|48|128)\.png$/, /^_locales\/[a-z]{2,3}(_[A-Za-z]{2,4})?\/messages\.json$/];
const walk = (dir, base = '') => fs.readdirSync(dir, { withFileTypes: true }).flatMap(e => e.isDirectory() ? walk(path.join(dir, e.name), base + e.name + '/') : [base + e.name]);

/** manifest.json text without the top-level "key" line (formatting of every other line is kept). */
function storeManifest(text) {
  const out = text.replace(/^[ \t]*"key"\s*:\s*"[^"]*",?[ \t]*\r?\n/m, '').replace(/("version_name"\s*:\s*")[^"]*(")/, '$1' + JSON.parse(text).version + ' store$2');
  const parsed = JSON.parse(out);
  if ('key' in parsed) throw new Error('manifest key was not removed');
  return out;
}
const manualBlocks = { 'background.js': 4, 'sniff.js': 1, 'popup.js': 2, 'popup.html': 1 };
function storeFile(name, data) {
  if (name === 'manifest.json') return Buffer.from(storeManifest(data.toString('utf8')));
  if (/^_locales\//.test(name)) { const messages = JSON.parse(data); for (const key of Object.keys(messages)) if (key.startsWith('youtube_')) delete messages[key]; return Buffer.from(JSON.stringify(messages, null, 2) + '\n'); }
  if (!manualBlocks[name]) return data;
  const text = data.toString('utf8');
  const starts = (text.match(/MANUAL-YOUTUBE-BEGIN/g) || []).length, ends = (text.match(/MANUAL-YOUTUBE-END/g) || []).length;
  if (starts !== manualBlocks[name] || ends !== starts) throw new Error('Manual block count changed: ' + name);
  const out = text.replace(/^[ \t]*(?:\/\/|<!--) MANUAL-YOUTUBE-BEGIN[^\r\n]*\r?\n[\s\S]*?^[ \t]*(?:\/\/|<!--) MANUAL-YOUTUBE-END[^\r\n]*(?:\r?\n|$)/gm, '');
  if (/MANUAL-YOUTUBE|youtube-send|sendYoutube|youtubePage|youtubeSend|youtubePanel|youtube-link/.test(out)) throw new Error('Manual handoff remains in store: ' + name);
  if (name.endsWith('.js')) new (require('node:vm').Script)(out, { filename: name });
  return Buffer.from(out);
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
