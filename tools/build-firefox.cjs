#!/usr/bin/env node
// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
'use strict';
// Usage: node tools/build-firefox.cjs [output.zip] [--signed path/to/signed.xpi]
// Builds the Firefox edition from the SAME files as the Chrome build (extension/): only manifest.json is rewritten and extension-firefox/firefox-shim.js is added.
//  - background: an event page (scripts list) instead of a service worker; the shim makes importScripts a no-op and aliases chrome/browser
//  - browser_specific_settings.gecko: id neodm@unlimitedera, strict_min_version, data_collection_permissions { required: ['none'] }
//  - no "key" (Chrome only) and no "version_name" (Chrome only); the version is the app version
// Default output: dist/NeoDM-extension-firefox.zip (upload this to addons.mozilla.org; see docs/FIREFOX-STORE.md).
// --signed <file>: checks a Mozilla-signed .xpi (same id and version, signature files present) and copies it to dist/NeoDM-extension-firefox.xpi.
const fs = require('node:fs'), path = require('node:path'), crypto = require('node:crypto'), zlib = require('node:zlib');
const { buildZip } = require('./crx3.cjs');
const root = path.resolve(__dirname, '..'), extDir = path.join(root, 'extension'), shimPath = path.join(root, 'extension-firefox', 'firefox-shim.js');
const GECKO_ID = 'neodm@unlimitedera', MIN_FIREFOX = '140.0';
const ALLOWED = [/^manifest\.json$/, /^(background|bridge|sniff|subs|content|hooks|popup|i18n|widget)\.js$/, /^popup\.(html|css)$/, /^icons\/(16|48|128)\.png$/, /^_locales\/[a-z]{2,3}(_[A-Za-z]{2,4})?\/messages\.json$/];
const walk = (dir, base = '') => fs.readdirSync(dir, { withFileTypes: true }).flatMap(e => e.isDirectory() ? walk(path.join(dir, e.name), base + e.name + '/') : [base + e.name]);

/** The Firefox manifest (object) made from the Chrome manifest text. */
function firefoxManifest(text) {
  const m = JSON.parse(text);
  delete m.key; delete m.version_name;
  const worker = m.background && m.background.service_worker;
  if (!worker) throw new Error('the Chrome manifest has no background service worker to convert');
  m.background = { scripts: ['firefox-shim.js', 'i18n.js', 'bridge.js', 'sniff.js', 'subs.js', worker] };
  m.browser_specific_settings = { gecko: { id: GECKO_ID, strict_min_version: MIN_FIREFOX, data_collection_permissions: { required: ['none'] } } };
  return m;
}
function buildFirefoxZip() {
  const names = walk(extDir);
  const extra = names.filter(n => !ALLOWED.some(r => r.test(n)));
  if (extra.length) throw new Error('Non-runtime files in extension/: ' + extra.join(', '));
  const files = names.map(name => ({ name, data: name === 'manifest.json' ? Buffer.from(JSON.stringify(firefoxManifest(fs.readFileSync(path.join(extDir, name), 'utf8')), null, 2) + '\n') : fs.readFileSync(path.join(extDir, name)) }));
  files.push({ name: 'firefox-shim.js', data: fs.readFileSync(shimPath) });
  const manifest = firefoxManifest(fs.readFileSync(path.join(extDir, 'manifest.json'), 'utf8'));
  const have = new Set(files.map(f => f.name));
  const referenced = [...Object.values(manifest.icons || {}), ...manifest.background.scripts, ...manifest.content_scripts.flatMap(c => c.js), manifest.action.default_popup];
  const missing = referenced.filter(n => !have.has(n));
  if (missing.length) throw new Error('Firefox manifest references missing files: ' + missing.join(', '));
  return { zip: buildZip(files), names: files.map(f => f.name), manifest };
}
/** Reads one file out of a ZIP buffer (stored or deflated). */
function readZipEntry(zip, wanted) {
  let eocd = zip.length - 22; while (eocd >= 0 && zip.readUInt32LE(eocd) !== 0x06054b50) eocd--;
  if (eocd < 0) throw new Error('not a ZIP file');
  for (let n = zip.readUInt16LE(eocd + 10), p = zip.readUInt32LE(eocd + 16); n > 0; n--) {
    const method = zip.readUInt16LE(p + 10), csize = zip.readUInt32LE(p + 20), nl = zip.readUInt16LE(p + 28), el = zip.readUInt16LE(p + 30), cl = zip.readUInt16LE(p + 32), local = zip.readUInt32LE(p + 42);
    const name = zip.toString('utf8', p + 46, p + 46 + nl); p += 46 + nl + el + cl;
    if (name !== wanted) continue;
    const start = local + 30 + zip.readUInt16LE(local + 26) + zip.readUInt16LE(local + 28), data = zip.subarray(start, start + csize);
    return method === 8 ? zlib.inflateRawSync(data) : data;
  }
  return null;
}
/** Throws unless the signed .xpi belongs to this build: same id, same version, Mozilla's signature files present. */
function checkSigned(xpi, version) {
  const manifest = readZipEntry(xpi, 'manifest.json');
  if (!manifest) throw new Error('the .xpi has no manifest.json');
  const m = JSON.parse(manifest.toString('utf8'));
  if (m.browser_specific_settings?.gecko?.id !== GECKO_ID) throw new Error('the .xpi has another add-on id');
  if (m.version !== version) throw new Error(`the .xpi is version ${m.version}, the app is ${version}`);
  if (!readZipEntry(xpi, 'META-INF/mozilla.rsa') || !readZipEntry(xpi, 'META-INF/mozilla.sf') || !readZipEntry(xpi, 'META-INF/manifest.mf')) throw new Error('the .xpi is not signed by Mozilla (META-INF files missing)');
}
module.exports = { firefoxManifest, buildFirefoxZip, readZipEntry, checkSigned, GECKO_ID, MIN_FIREFOX };
if (require.main === module) {
  const args = process.argv.slice(2), signedAt = args.indexOf('--signed');
  const signedFile = signedAt >= 0 ? args.splice(signedAt, 2)[1] : null;
  const out = path.resolve(args[0] || path.join(root, 'dist', 'NeoDM-extension-firefox.zip'));
  const { zip, names, manifest } = buildFirefoxZip();
  fs.mkdirSync(path.dirname(out), { recursive: true }); fs.writeFileSync(out, zip);
  console.log(`wrote ${out} (${zip.length} bytes, ${names.length} files, version ${manifest.version}, id ${GECKO_ID}, min Firefox ${MIN_FIREFOX}), sha256 ${crypto.createHash('sha256').update(zip).digest('hex')}`);
  if (signedFile) {
    const xpi = fs.readFileSync(path.resolve(signedFile)); checkSigned(xpi, manifest.version);
    const dest = path.join(path.dirname(out), 'NeoDM-extension-firefox.xpi'); fs.writeFileSync(dest, xpi);
    console.log(`signed .xpi accepted, copied to ${dest}`);
  }
}
