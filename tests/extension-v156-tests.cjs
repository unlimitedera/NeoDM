'use strict';
/* 1.5.6: trusted extension IDs (development + Chrome Web Store), store package, store-first docs. */
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs'), path = require('node:path'), zlib = require('node:zlib');
const root = path.join(__dirname, '..');
const read = f => fs.readFileSync(path.join(root, f), 'utf8');
const devId = read('tools/extension-id.txt').trim(), storeId = read('tools/extension-store-id.txt').trim();
const version = /<Version>([^<]+)<\/Version>/.exec(read('Directory.Build.props'))[1];
const STORE_URL = 'https://chromewebstore.google.com/detail/ojbaibjfbdjodgmfbiajkigpanbdkiid';

/** Minimal ZIP reader (central directory): name -> Buffer. */
function unzip(buf) {
  let eocd = buf.length - 22; while (eocd >= 0 && buf.readUInt32LE(eocd) !== 0x06054b50) eocd--;
  assert.ok(eocd >= 0, 'end of central directory');
  const count = buf.readUInt16LE(eocd + 10); let p = buf.readUInt32LE(eocd + 16); const out = new Map();
  for (let i = 0; i < count; i++) {
    const method = buf.readUInt16LE(p + 10), csize = buf.readUInt32LE(p + 20), nlen = buf.readUInt16LE(p + 28), elen = buf.readUInt16LE(p + 30), clen = buf.readUInt16LE(p + 32), lho = buf.readUInt32LE(p + 42);
    const name = buf.toString('utf8', p + 46, p + 46 + nlen); p += 46 + nlen + elen + clen;
    const lnl = buf.readUInt16LE(lho + 26), lel = buf.readUInt16LE(lho + 28), data = buf.subarray(lho + 30 + lnl + lel, lho + 30 + lnl + lel + csize);
    out.set(name, method === 8 ? zlib.inflateRawSync(data) : Buffer.from(data));
  }
  return out;
}

test('the development ID and the store ID are two different valid IDs', () => {
  assert.match(devId, /^[a-p]{32}$/); assert.match(storeId, /^[a-p]{32}$/);
  assert.notEqual(devId, storeId); assert.equal(storeId, 'ojbaibjfbdjodgmfbiajkigpanbdkiid'); assert.equal(devId, 'hohmmojlhbkelkobobpkochpdempcihd');
});

test('the pack tool validates the store ID format and still checks the development ID', () => {
  const src = read('tools/pack-extension.cjs');
  assert.match(src, /extension-store-id\.txt/); assert.match(src, /\^\[a-p\]\{32\}\$/); assert.match(src, /ID mismatch/);
  const out = require('node:child_process').execFileSync(process.execPath, [path.join(root, 'tools/pack-extension.cjs')], { cwd: root, encoding: 'utf8' });
  assert.match(out, new RegExp('trusted extension ids: development ' + devId + ' \\| store ' + storeId));
});

test('the manifest has the development key (ID stays valid) and the version of the product', () => {
  const m = JSON.parse(read('extension/manifest.json'));
  assert.equal(m.version, version); assert.ok(m.key);
  const crypto = require('node:crypto'); const hash = crypto.createHash('sha256').update(Buffer.from(m.key, 'base64')).digest().subarray(0, 16);
  assert.equal([...hash].map(b => String.fromCharCode(97 + (b >> 4), 97 + (b & 15))).join(''), devId);
});

test('store package: no key, same version, runtime files plus all locales', () => {
  const { buildStoreZip, storeManifest } = require('../tools/build-store-zip.cjs');
  const { zip, names } = buildStoreZip(); const files = unzip(zip);
  const m = JSON.parse(files.get('manifest.json').toString('utf8'));
  assert.equal('key' in m, false); assert.equal(m.version, version);
  assert.ok(files.size >= 22, 'files: ' + files.size);
  const locales = [...files.keys()].filter(n => /^_locales\/[^/]+\/messages\.json$/.test(n));
  assert.equal(locales.length, fs.readdirSync(path.join(root, 'extension/_locales')).length); assert.ok(locales.length >= 10);
  for (const n of names) if (n !== 'manifest.json') assert.ok(files.get(n).equals(fs.readFileSync(path.join(root, 'extension', n))), n);
  assert.equal(JSON.parse(storeManifest(read('extension/manifest.json'))).name, m.name);
});

test('host, installer, watchdog and app all know both IDs and the old single-ID config form', () => {
  const host = read('src/NDM.NativeHost/Program.cs');
  assert.match(host, /ExtensionIds/); assert.match(host, /CallerId/); assert.match(host, /return 2;/);
  const configure = read('installer/configure.ps1');
  assert.match(configure, /extensionIds/); assert.match(configure, /allowed_origins = @\(\$ExtIds/); assert.match(configure, /\^\[a-p\]\{32\}\$/);
  const wxs = read('installer/NeoDM.wxs'); assert.equal((wxs.match(/-ExtId &quot;\$\(ExtId\)&quot;/g) || []).length, 2);
  const dog = read('src/NDM.App/BridgeWatchdog.cs'); assert.ok(dog.includes(devId) && dog.includes(storeId));
  const build = read('build-installer.ps1'); assert.match(build, /extension-store-id\.txt/);
});

test('docs are store first and carry no approval-pending wording', () => {
  for (const f of ['README.md', 'PRIVACY.md', 'installer/README.md', 'docs/CHROME.md', 'docs/EXTENSION-INSTALL.md', 'docs/UPDATES.md']) {
    const text = read(f);
    assert.doesNotMatch(text, /waiting for approval|pending approval|not in a browser store|submitted to the Chrome Web Store/i, f);
    assert.ok(!text.includes('\u2014'), 'no em dash in ' + f);
  }
  const readme = read('README.md'); assert.ok(readme.split('\n').slice(0, 25).some(l => l.includes('Get it on the Chrome Web Store') && l.includes(STORE_URL)));
  const guide = read('docs/EXTENSION-INSTALL.md'); assert.ok(guide.includes(STORE_URL)); assert.ok(guide.indexOf(STORE_URL) < guide.indexOf('### Step 0'));
  assert.match(read('docs/UPDATES.md'), /store copy/i); assert.match(guide, /store copy/i);
});

test('the changelog top entry and the README release row name this version', () => {
  assert.match(read('CHANGELOG.md'), new RegExp('^## \\[' + version.replace(/\./g, '\\.') + '\\]', 'm'));
  assert.ok(read('README.md').includes('NeoDM-Setup-' + version + '.msi'));
});

test('locales: every app language has the new store strings and none of the old pending ones', () => {
  const dir = path.join(root, 'src/NDM.App/Locales');
  for (const f of fs.readdirSync(dir)) {
    const d = JSON.parse(read('src/NDM.App/Locales/' + f).replace(/^\uFEFF/, ''));
    for (const k of ['extver.store_older', 'notice.btn.open_store', 'guide.store.heading', 'guide.store.btn', 'guide.manual.heading', 'main.menu.install_from_the_chrome_web_store', 'about.btn.chrome_web_store', 'options.btn.install_from_the_chrome_web_store']) assert.ok(d[k], f + ' ' + k);
    assert.ok(!('guide.pending' in d) && !('guide.pending_doc' in d), f);
    for (const url of ['chrome://extensions', 'edge://extensions', 'brave://extensions']) assert.ok(d['extver.store_older'].includes(url), f + ' ' + url);
  }
});
