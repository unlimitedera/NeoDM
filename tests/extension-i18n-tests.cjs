// chrome.i18n coverage: every locale has the same messages and placeholders, no user-visible English is hard-coded in the extension, and the popup,
// ribbon and service worker render in Arabic (right-to-left) when the browser UI language is Arabic.
'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs'), path = require('node:path');
const { JSDOM } = require('jsdom');
const { makeBackground, makeI18n, withI18n, tick, source, json } = require('./extension-harness.cjs');
const extDir = path.join(__dirname, '../extension'), localesDir = path.join(extDir, '_locales');
const readMessages = lang => JSON.parse(fs.readFileSync(path.join(localesDir, lang, 'messages.json'), 'utf8'));
const languages = fs.readdirSync(localesDir);
const en = readMessages('en');

test('locale folders: en and ar exist, default_locale is en, the manifest strings come from messages', () => {
  assert.ok(languages.includes('en') && languages.includes('ar'));
  const manifest = JSON.parse(source('manifest.json'));
  assert.equal(manifest.default_locale, 'en');
  for (const field of [manifest.name, manifest.description, manifest.action.default_title]) {
    const key = /^__MSG_(\w+)__$/.exec(field)?.[1]; assert.ok(key, `${field} is a message reference`);
    for (const lang of languages) assert.ok(readMessages(lang)[key]?.message, `${lang} defines ${key}`);
  }
  assert.ok(manifest.content_scripts[0].js.indexOf('i18n.js') < manifest.content_scripts[0].js.indexOf('content.js'), 'i18n.js loads before content.js');
  assert.match(source('popup.html'), /<script src="i18n\.js"><\/script>\s*(?:<script src="sniff\.js"><\/script>\s*)?<script src="popup\.js">/);
  assert.match(source('background.js'), /importScripts\('i18n\.js'/);
});

for (const lang of languages.filter(l => l !== 'en')) {
  test(`${lang}: same keys as en, identical placeholders, no empty or padded messages, NeoDM kept`, () => {
    const data = readMessages(lang);
    assert.deepEqual(Object.keys(data).sort(), Object.keys(en).sort(), 'identical key set');
    for (const [key, entry] of Object.entries(data)) {
      assert.deepEqual(json(entry.placeholders ?? {}), json(en[key].placeholders ?? {}), `${key}: placeholders block`);
      const tokens = s => [...new Set([...s.matchAll(/\$([A-Z0-9_@]+)\$/g)].map(m => m[1]))].sort();
      assert.deepEqual(tokens(entry.message), tokens(en[key].message), `${key}: placeholder tokens`);
      assert.ok(entry.message.trim() === entry.message && entry.message.length > 0, `${key}: not empty, not padded`);
      if (en[key].message.includes('NeoDM')) assert.ok(entry.message.includes('NeoDM'), `${key}: NeoDM kept`);
    }
  });
}

test('the checker tool accepts the shipped locales and rejects a damaged one', () => {
  const { checkExtension } = require('../tools/check-locales.cjs');
  for (const lang of languages.filter(l => l !== 'en')) assert.deepEqual(checkExtension(path.join(localesDir, lang, 'messages.json'), en), []);
  const tmp = path.join(require('node:os').tmpdir(), 'neodm-loc-' + process.pid + '.json');
  const broken = readMessages('ar'); delete broken.popup_test; broken.popup_pause_site.message = 'no placeholder'; broken.extra_key = { message: 'x' };
  fs.writeFileSync(tmp, JSON.stringify(broken));
  try { const problems = checkExtension(tmp, en).join('\n'); assert.match(problems, /missing message popup_test/); assert.match(problems, /placeholder/); assert.match(problems, /extra message extra_key/); } finally { fs.rmSync(tmp, { force: true }); }
});

/* ---- every key used in code exists; no hard-coded English ---- */
function stripComments(code) {
  let out = '', i = 0, quote = '';
  while (i < code.length) {
    const c = code[i], n = code[i + 1];
    if (quote) { out += c; if (c === '\\') { out += n ?? ''; i += 2; continue; } if (c === quote) quote = ''; i++; continue; }
    if (c === '\'' || c === '"' || c === '`') { quote = c; out += c; i++; continue; }
    if (c === '/' && n === '/') { while (i < code.length && code[i] !== '\n') i++; continue; }
    if (c === '/' && n === '*') { i += 2; while (i < code.length && !(code[i] === '*' && code[i + 1] === '/')) i++; i += 2; continue; }
    out += c; i++;
  }
  return out;
}
const scripts = ['background.js', 'bridge.js', 'content.js', 'hooks.js', 'popup.js', 'sniff.js'];
test('every message key used in the scripts and in popup.html exists in en', () => {
  const used = new Set();
  for (const file of scripts) for (const m of stripComments(source(file)).matchAll(/(?:NeoI18n\.t|\bt)\(\s*'([a-z0-9_]+)'/g)) used.add(m[1]);
  for (const file of scripts) for (const m of stripComments(source(file)).matchAll(/'((?:kind|vb|err)_[a-z0-9_]+)'/g)) if (m[1] in en) used.add(m[1]); // keys chosen by a condition or a table
  for (const m of source('popup.html').matchAll(/data-i18n(?:-title|-label)?="([a-z0-9_]+)"/g)) used.add(m[1]);
  for (const k of [...used]) if (k.endsWith('_')) used.delete(k); // prefixes of computed keys (reason_<code>, popup_type_<id>)
  for (const code of /new Set\(\[([^\]]*)\]\)/.exec(source('background.js').split('const REASONS')[1])[1].matchAll(/'([a-z-]+)'/g)) used.add('reason_' + code[1].replace(/-/g, '_'));
  const missing = [...used].filter(k => !(k in en));
  assert.deepEqual(missing, []);
  const unused = Object.keys(en).filter(k => !used.has(k) && !/^(ext_|popup_type_)/.test(k));
  assert.deepEqual(unused, [], 'messages nobody uses');
  assert.ok(used.size > 80, `${used.size} keys used`);
});

// Allowlist of literals that may stay in the scripts: file | text | reason.
const ALLOW = [
  ['popup.js', 'neodm.pause', 'storage key, never shown'],
  ['*', 'com.ndm.bridge', 'native host name, never shown'],
  ['content.js', 'NeoDM', 'product name used as the host element title'],
];
test('no user-visible English is hard-coded in the extension scripts or popup.html', () => {
  const hits = [];
  const allowed = (file, text) => ALLOW.some(([f, t]) => (f === '*' || f === file) && text === t);
  for (const file of scripts) {
    const code = stripComments(source(file));
    // sentences and labels: a capitalised word followed by more words, or text assigned to textContent/title/aria-label/message fields
    for (const m of code.matchAll(/(['"`])((?:\\.|(?!\1)[^\\\n])*)\1/g)) {
      const text = m[2];
      if (/^[A-Z][a-z]+(?:[ ,.'’:;()\/-]+[A-Za-z0-9][A-Za-z0-9'’.\/()-]*)+[.…!?]?$/.test(text) && /\s/.test(text) && !allowed(file, text)) hits.push(`${file}: "${text}"`);
    }
    for (const m of code.matchAll(/\b(?:textContent|title|placeholder|alt)\s*=\s*(['"`])([^'"`\n]*[A-Za-z]{3,}[^'"`\n]*)\1/g)) if (!allowed(file, m[2])) hits.push(`${file}: assigned "${m[2]}"`);
    for (const m of code.matchAll(/setAttribute\(\s*'(?:aria-label|title|placeholder|alt)'\s*,\s*(['"`])([^'"`\n]*[A-Za-z]{3,}[^'"`\n]*)\1/g)) hits.push(`${file}: attribute "${m[2]}"`);
    for (const m of code.matchAll(/new Error\(\s*(['"`])([^'"`\n]+)\1/g)) if (!allowed(file, m[2])) hits.push(`${file}: Error "${m[2]}"`);
  }
  const html = source('popup.html').replace(/<script[\s\S]*?<\/script>|<style[\s\S]*?<\/style>/g, '');
  for (const m of html.matchAll(/>([^<>]*[A-Za-z]{2,}[^<>]*)</g)) hits.push(`popup.html text "${m[1].trim()}"`);
  for (const m of html.matchAll(/\b(?:title|placeholder|alt|aria-label)="([^"]*[A-Za-z]{2,}[^"]*)"/g)) hits.push(`popup.html attribute "${m[1]}"`);
  assert.deepEqual(hits, []);
  // the scanner works: it flags a planted sentence
  const planted = stripComments("const x = 'Something went wrong here'; // 'A comment sentence'");
  assert.ok(/Something went wrong here/.test(planted) && !/comment sentence/.test(planted));
});

/* ---- Arabic behaviour ---- */
const MB = 1024 * 1024;
async function popup(lang, { local = {}, session = {}, tabUrl = 'https://site.example/page' } = {}) {
  const dom = new JSDOM(source('popup.html'), { runScripts: 'outside-only', url: 'chrome-extension://ext-id/popup.html' }), w = dom.window;
  w.chrome = {
    storage: { local: { get: async k => Object.fromEntries([].concat(k).filter(x => x in local).map(x => [x, local[x]])), set: async o => Object.assign(local, o) }, session: { get: async k => (k in session ? { [k]: session[k] } : {}) } },
    tabs: { query: async () => [{ id: 1, url: tabUrl }] },
    runtime: { sendMessage: async () => ({ ok: true }) },
    permissions: { request: async () => true, remove: async () => true },
  };
  withI18n(w, lang); w.eval(source('sniff.js')); w.eval(source('popup.js')); await tick(12);
  return { w, doc: w.document };
}
test('popup in Arabic: dir=rtl, rtl class, Arabic labels, parameters filled', async () => {
  const ar = readMessages('ar');
  const { doc } = await popup('ar');
  assert.equal(doc.documentElement.dir, 'rtl'); assert.equal(doc.documentElement.lang, 'ar'); assert.ok(doc.documentElement.classList.contains('rtl'));
  assert.equal(doc.querySelector('h1').textContent, ar.popup_title.message);
  assert.equal(doc.getElementById('test').textContent, ar.popup_test.message);
  assert.equal(doc.title, ar.popup_title.message);
  assert.match(doc.getElementById('pauseSiteLabel').textContent, /site\.example/);
  assert.ok(doc.getElementById('pauseSiteLabel').textContent.includes('NeoDM') && /[\u0600-\u06FF]/.test(doc.getElementById('pauseSiteLabel').textContent));
  for (const el of doc.querySelectorAll('[data-i18n]')) assert.ok(el.textContent.length > 0, `${el.dataset.i18n} is filled`);
  assert.equal(doc.getElementById('pauseState').textContent, ar.popup_active.message);
});
test('popup in English stays left to right; unknown languages fall back to English texts', async () => {
  const en1 = await popup('en'); assert.equal(en1.doc.documentElement.dir, 'ltr'); assert.equal(en1.doc.querySelector('h1').textContent, en.popup_title.message);
  const sv = await popup('sv'); assert.equal(sv.doc.documentElement.dir, 'ltr'); assert.equal(sv.doc.getElementById('test').textContent, en.popup_test.message);
  for (const tag of ['ar', 'ar-EG', 'fa', 'he', 'iw', 'ur']) assert.equal(makeI18n(tag).getUILanguage(), tag);
  const { NeoI18n } = (() => { const w = new JSDOM('', { runScripts: 'outside-only' }).window; withI18n(w, 'fa-IR'); return w; })(); assert.equal(NeoI18n.isRtl(), true);
});
test('i18n.js substitutes parameters and falls back to the key when a message is missing', () => {
  const w = new JSDOM('', { runScripts: 'outside-only' }).window; withI18n(w, 'ar');
  assert.match(w.NeoI18n.t('popup_version_note', '1.5.0', '1.5.0'), /1\.5\.0/);
  assert.equal(w.NeoI18n.t('no_such_key'), 'no_such_key');
});
function ribbon(lang, items) {
  const dom = new JSDOM('<!doctype html><body><video src="https://files.example/a.mp4"></video></body>', { url: 'https://page.example/watch', runScripts: 'outside-only', pretendToBeVisual: true }), w = dom.window;
  const roots = [], origAttach = w.Element.prototype.attachShadow;
  w.Element.prototype.attachShadow = function (init) { const r = origAttach.call(this, init); roots.push({ host: this, root: r }); return r; };
  w.chrome = {
    runtime: { sendMessage: async m => (m.action === 'media-list' ? { items } : { ok: true }), onMessage: { addListener() {} } },
    storage: { local: { get: async () => ({}) }, onChanged: { addListener() {} } },
  };
  withI18n(w, lang); w.eval(source('sniff.js')); w.eval(source('content.js'));
  return { w, roots };
}
test('ribbon in Arabic: rtl bar on the left edge, Arabic button, kind and state labels', async () => {
  const ar = readMessages('ar');
  const r = ribbon('ar', [{ id: 'a', url: 'https://v.example/a.mp4', kind: 'video', track: 'muxed', complete: true, size: 12 * MB, quality: '720p', mime: 'video/mp4', source: 'net' }]); await tick();
  const { host, root } = r.roots.at(-1);
  assert.equal(root.querySelector('.bar').dir, 'rtl'); assert.match(host.style.cssText, /left:\s*16px/); assert.doesNotMatch(host.style.cssText, /right:\s*16px/);
  assert.equal(root.querySelector('button.dl').textContent, ar.ribbon_download.message);
  assert.match(root.textContent, new RegExp(ar.kind_video.message)); assert.match(root.textContent, new RegExp(ar.ribbon_complete.message));
  assert.equal(root.querySelector('.bar').getAttribute('aria-label'), ar.ribbon_label.message);
  const en1 = ribbon('en', [{ id: 'a', url: 'https://v.example/a.mp4', kind: 'video', track: 'muxed', complete: true, size: 12 * MB, quality: '720p', mime: 'video/mp4', source: 'net' }]); await tick();
  assert.equal(en1.roots.at(-1).root.querySelector('.bar').dir, 'ltr'); assert.match(en1.roots.at(-1).host.style.cssText, /right:\s*16px/);
});
test('service worker in Arabic: context menu titles, capture log reasons and error texts', async () => {
  const ar = readMessages('ar');
  const b = makeBackground({ uiLanguage: 'ar' }); const titles = [];
  b.chrome.contextMenus.create = (m, cb) => { titles.push(m.title); cb && cb(); };
  await b.hooks.install({}); await tick();
  assert.deepEqual([...new Set(titles)], [ar.menu_link.message, ar.menu_media.message, ar.menu_video.message]);
  const reply = await b.message({ action: 'test' }); assert.equal(reply.ok, true);
  const bad = makeBackground({ uiLanguage: 'ar', nativeReply: () => new Error('x') });
  const res = await bad.message({ action: 'test' }); assert.equal(res.ok, false); assert.equal(res.error, ar.err_host_unavailable.message);
  const en1 = makeBackground({ nativeReply: () => new Error('x') }); const res2 = await en1.message({ action: 'test' }); assert.equal(res2.error, en.err_host_unavailable.message);
});
