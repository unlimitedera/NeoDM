#!/usr/bin/env node
'use strict';
// Usage: node tools/pack-extension.cjs [--init-key]
//  --init-key  generate keys/neodm-extension.pem (RSA-2048) if missing, write its public key into extension/manifest.json
//              and its ID into tools/extension-id.txt. Never overwrites an existing key.
// Without flags: pack extension/ -> dist/NeoDM.crx, signed with keys/neodm-extension.pem. Fails if manifest key/ID disagree.
const fs = require('node:fs'), path = require('node:path'), crypto = require('node:crypto');
const { buildZip, buildCrx, idFromPublicDer, publicDerFromPem } = require('./crx3.cjs');
const root = path.resolve(__dirname, '..'), extDir = path.join(root, 'extension');
const pemPath = path.join(root, 'keys', 'neodm-extension.pem'), idPath = path.join(__dirname, 'extension-id.txt');
const manifestPath = path.join(extDir, 'manifest.json'), out = path.join(root, 'dist', 'NeoDM.crx');
// Runtime files only. Anything else in extension/ aborts the build rather than being silently shipped.
const ALLOWED = [/^manifest\.json$/, /^(background|bridge|sniff|content|popup|i18n)\.js$/, /^popup\.(html|css)$/, /^icons\/(16|48|128)\.png$/, /^_locales\/[a-z]{2,3}(_[A-Za-z]{2,4})?\/messages\.json$/];
function walk(dir, base = '') {
  return fs.readdirSync(dir, { withFileTypes: true }).flatMap(e => e.isDirectory() ? walk(path.join(dir, e.name), base + e.name + '/') : [base + e.name]);
}
if (process.argv.includes('--init-key')) {
  if (!fs.existsSync(pemPath)) {
    fs.mkdirSync(path.dirname(pemPath), { recursive: true });
    const { privateKey } = crypto.generateKeyPairSync('rsa', { modulusLength: 2048 });
    fs.writeFileSync(pemPath, privateKey.export({ type: 'pkcs8', format: 'pem' }), { mode: 0o600 });
    console.log('generated', pemPath, '- BACK THIS FILE UP; losing it changes the extension ID');
  }
  const der = publicDerFromPem(fs.readFileSync(pemPath)), m = fs.readFileSync(manifestPath, 'utf8');
  if (!/"key"\s*:\s*"[^"]*"/.test(m)) throw new Error('manifest.json has no "key" field to update');
  fs.writeFileSync(manifestPath, m.replace(/("key"\s*:\s*")[^"]*(")/, `$1${der.toString('base64')}$2`));
  fs.writeFileSync(idPath, idFromPublicDer(der) + '\n');
  console.log('extension id', idFromPublicDer(der)); process.exit(0);
}
const pem = fs.readFileSync(pemPath), der = publicDerFromPem(pem), id = idFromPublicDer(der);
const manifest = JSON.parse(fs.readFileSync(manifestPath, 'utf8'));
const manifestId = idFromPublicDer(Buffer.from(manifest.key, 'base64'));
const recorded = fs.readFileSync(idPath, 'utf8').trim();
if (manifestId !== id || recorded !== id) throw new Error(`ID mismatch: pem=${id} manifest=${manifestId} tools/extension-id.txt=${recorded}. Run with --init-key.`);
const names = walk(extDir);
const extra = names.filter(n => !ALLOWED.some(r => r.test(n)));
if (extra.length) throw new Error('Non-runtime files in extension/: ' + extra.join(', '));
const referenced = [...Object.values(manifest.icons || {}), 'i18n.js', manifest.background.service_worker, ...manifest.content_scripts.flatMap(c => c.js), manifest.action.default_popup];
const missing = referenced.filter(n => !names.includes(n));
if (missing.length) throw new Error('Manifest references missing files: ' + missing.join(', '));
const crx = buildCrx(buildZip(names.map(name => ({ name, data: fs.readFileSync(path.join(extDir, name)) }))), pem);
fs.mkdirSync(path.dirname(out), { recursive: true }); fs.writeFileSync(out, crx);
console.log(`wrote ${out} (${crx.length} bytes), id ${id}, sha256 ${crypto.createHash('sha256').update(crx).digest('hex')}`);
console.log('files:', names.join(', '));
