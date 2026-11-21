// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
'use strict';
/* 2.0.0 Firefox build (item 18): manifest rules and package content. No browser needed; the real-Firefox run is tests/extension-v200-firefox-smoke.cjs. */
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const root = path.join(__dirname, '..');
const { firefoxManifest, buildFirefoxZip, readZipEntry, checkSigned, GECKO_ID, MIN_FIREFOX } = require('../tools/build-firefox.cjs');
const chromeText = fs.readFileSync(path.join(root, 'extension', 'manifest.json'), 'utf8');
const chrome = JSON.parse(chromeText), ff = firefoxManifest(chromeText);
const version = /<Version>\s*([\d.]+)\s*<\/Version>/.exec(fs.readFileSync(path.join(root, 'Directory.Build.props'), 'utf8'))[1];

test('Firefox manifest: add-on id, minimum version, data collection declared as none', () => {
  assert.equal(GECKO_ID, 'neodm@unlimitedera');
  assert.equal(ff.browser_specific_settings.gecko.id, 'neodm@unlimitedera');
  assert.ok(parseFloat(ff.browser_specific_settings.gecko.strict_min_version) >= 128, 'world MAIN content scripts need Firefox 128 or newer');
  assert.equal(ff.browser_specific_settings.gecko.strict_min_version, MIN_FIREFOX);
  assert.deepEqual(ff.browser_specific_settings.gecko.data_collection_permissions, { required: ['none'] });
});
test('Firefox manifest: no Chrome-only key or version name, version equals the app version', () => {
  assert.ok(!('key' in ff) && !('version_name' in ff));
  assert.equal(ff.version, version); assert.equal(chrome.version, version); assert.equal(version, '2.0.0');
  assert.equal(ff.manifest_version, 3);
});
test('Firefox manifest: event page scripts instead of a service worker, shim first', () => {
  assert.ok(!ff.background.service_worker);
  assert.equal(ff.background.scripts[0], 'firefox-shim.js');
  assert.equal(ff.background.scripts.at(-1), 'background.js');
  for (const s of ['i18n.js', 'bridge.js', 'sniff.js', 'subs.js']) assert.ok(ff.background.scripts.indexOf(s) > 0 && ff.background.scripts.indexOf(s) < ff.background.scripts.length - 1, s + ' loads before background.js');
});
test('Firefox manifest: same permissions, hosts, content scripts, popup and CSP as the Chrome build', () => {
  for (const k of ['permissions', 'host_permissions', 'content_scripts', 'action', 'icons', 'content_security_policy', 'default_locale', 'name', 'description'])
    assert.deepEqual(ff[k], chrome[k], k);
  assert.deepEqual([...ff.permissions].sort(), ['cookies', 'contextMenus', 'downloads', 'nativeMessaging', 'storage', 'tabs', 'webRequest'].sort());
  assert.ok(ff.content_scripts.some(c => c.world === 'MAIN' && c.js.includes('hooks.js')));
});
test('Firefox manifest: a manifest without a service worker is refused', () => {
  assert.throws(() => firefoxManifest(JSON.stringify({ ...chrome, background: {} })), /service worker/);
});
test('Firefox zip: same files as the Chrome extension plus the shim; widget-pointing files byte for byte the same', () => {
  const { zip, names } = buildFirefoxZip();
  const own = fs.readdirSync(path.join(root, 'extension')).filter(n => fs.statSync(path.join(root, 'extension', n)).isFile());
  for (const n of own) assert.ok(names.includes(n), n + ' is in the Firefox zip');
  assert.ok(names.includes('firefox-shim.js'));
  for (const n of ['widget.js', 'content.js', 'background.js', 'bridge.js', 'sniff.js', 'subs.js', 'hooks.js', 'popup.js', 'i18n.js'])
    assert.ok(readZipEntry(zip, n).equals(fs.readFileSync(path.join(root, 'extension', n))), n + ' is unchanged');
  assert.deepEqual(JSON.parse(readZipEntry(zip, 'manifest.json').toString('utf8')), ff);
  assert.ok(!names.some(n => /\.(pem|crx|map)$/i.test(n) || /^(tests|tools|src)\//.test(n)), 'only runtime files');
  assert.equal(names.filter(n => n.startsWith('_locales/')).length, fs.readdirSync(path.join(root, 'extension', '_locales')).length);
});
test('Firefox shim: importScripts does nothing, chrome and browser name one API', () => {
  const vm = require('node:vm'); const ctx = { chrome: { x: 1 } }; ctx.globalThis = ctx; vm.createContext(ctx);
  vm.runInContext(fs.readFileSync(path.join(root, 'extension-firefox', 'firefox-shim.js'), 'utf8'), ctx);
  assert.equal(typeof ctx.importScripts, 'function'); assert.equal(ctx.importScripts('a.js'), undefined); assert.equal(ctx.browser, ctx.chrome);
  const only = { browser: { y: 2 } }; only.globalThis = only; vm.createContext(only);
  vm.runInContext(fs.readFileSync(path.join(root, 'extension-firefox', 'firefox-shim.js'), 'utf8'), only);
  assert.equal(only.chrome, only.browser);
});
test('Firefox background: the browser is reported as Firefox (user agent without brands)', () => {
  const src = fs.readFileSync(path.join(root, 'extension', 'background.js'), 'utf8');
  const i = src.indexOf('async function browserName()'), j = src.indexOf('\n}\n', i) + 3;
  const fn = new Function('navigator', 'let browserLabel;\n' + src.slice(i, j) + '\nreturn browserName();');
  return fn({ userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:140.0) Gecko/20100101 Firefox/140.0' }).then(n => assert.equal(n, 'Firefox'));
});
test('signed .xpi check: right id, version and Mozilla signature files', () => {
  const { buildZip } = require('../tools/crx3.cjs');
  const man = v => Buffer.from(JSON.stringify({ version: v, browser_specific_settings: { gecko: { id: GECKO_ID } } }));
  const sig = [{ name: 'META-INF/mozilla.rsa', data: Buffer.from('r') }, { name: 'META-INF/mozilla.sf', data: Buffer.from('s') }, { name: 'META-INF/manifest.mf', data: Buffer.from('m') }];
  checkSigned(buildZip([{ name: 'manifest.json', data: man(version) }, ...sig]), version);
  assert.throws(() => checkSigned(buildZip([{ name: 'manifest.json', data: man(version) }]), version), /not signed/);
  assert.throws(() => checkSigned(buildZip([{ name: 'manifest.json', data: man('1.0.0') }, ...sig]), version), /version/);
  assert.throws(() => checkSigned(buildZip([{ name: 'manifest.json', data: Buffer.from(JSON.stringify({ version, browser_specific_settings: { gecko: { id: 'x@y' } } })) }, ...sig]), version), /id/);
});
