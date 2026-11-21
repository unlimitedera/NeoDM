// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
'use strict';
/* 1.16.2 (part 3): the site check reporter. Widget detection on the saved markups of the three widgets, the screen
   rectangle arithmetic (pixel ratios, window offsets), the answer field order and the form lookup. No browser needed:
   the page is a small fake document, like the other extension suites. */
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const root = path.join(__dirname, '..');
const { find, rectOf, kindOf, responseField, ownForm } = require('../extension/widget.js');

/** A fake element: its box, its classes, its closest form and the query result for a submit control. */
function el({ x = 10, y = 20, w = 300, h = 65, className = '', src = '', value = '', form = null, submit = null } = {}) {
  return {
    className, src, value,
    getBoundingClientRect: () => ({ left: x, top: y, right: x + w, bottom: y + h, width: w, height: h }),
    closest: sel => (sel === 'form' ? form : null),
    querySelector: () => submit,
  };
}
/** A fake document answering querySelectorAll by the exact selector of the markup under test. */
function doc(table) {
  return {
    querySelectorAll: sel => (table[sel] || []),
    querySelector: sel => (table[sel] || [])[0] || null,
  };
}
const win = (screenX = 100, screenY = 50, dpr = 1) => ({ screenX, screenY, devicePixelRatio: dpr });

const MARKUPS = [
  ['iframe[src*="challenges.cloudflare.com"]', 'turnstile'],
  ['.cf-turnstile', 'turnstile'],
  ['iframe[src*="recaptcha/api2"]', 'recaptcha'],
  ['.h-captcha', 'hcaptcha'],
];
for (const [selector, kind] of MARKUPS) {
  test('widget detection: ' + selector + ' is a ' + kind + ' widget', () => {
    const hit = find(doc({ [selector]: [el()] }));
    assert.ok(hit, selector + ' is found');
    assert.equal(hit.kind, kind);
  });
}
test('a bare data-sitekey takes its kind from its own class list', () => {
  assert.equal(kindOf(el({ className: 'g-recaptcha' }), 'sitekey'), 'recaptcha');
  assert.equal(kindOf(el({ className: 'h-captcha' }), 'sitekey'), 'hcaptcha');
  assert.equal(kindOf(el({ className: 'cf-turnstile' }), 'sitekey'), 'turnstile');
  assert.equal(kindOf(el({ className: 'other' }), 'sitekey'), 'turnstile');
  assert.equal(find(doc({ '[data-sitekey]': [el({ className: 'g-recaptcha' })] })).kind, 'recaptcha');
});
test('a widget with no box on the page is not reported, and the largest box wins', () => {
  assert.equal(find(doc({ '.cf-turnstile': [el({ w: 0, h: 0 })] })), null);
  const hit = find(doc({ '.cf-turnstile': [el({ w: 100, h: 40 }), el({ w: 300, h: 65, x: 40 })] }));
  assert.equal(hit.el.getBoundingClientRect().width, 300);
});
test('the first known mark wins over a later one', () => {
  const hit = find(doc({ '.cf-turnstile': [el({ w: 300, h: 65 })], '.h-captcha': [el({ w: 400, h: 80 })] }));
  assert.equal(hit.kind, 'turnstile');
});
test('a document that throws on a selector is skipped, not fatal', () => {
  const broken = { querySelectorAll: sel => { if (sel.includes('cloudflare')) throw new Error('bad'); return [el()]; }, querySelector: () => null };
  assert.equal(find(broken).kind, 'turnstile'); // .cf-turnstile next in line
});

test('the screen rectangle adds the window offset and scales with the pixel ratio', () => {
  const box = el({ x: 10, y: 20, w: 300, h: 65 });
  assert.deepEqual(rectOf(box, win(100, 50, 1)), { x: 110, y: 70, w: 300, h: 65, dpr: 1 });
  assert.deepEqual(rectOf(box, win(100, 50, 1.5)), { x: 165, y: 105, w: 450, h: 98, dpr: 1.5 }); // 97.5 rounds away from zero
  assert.deepEqual(rectOf(box, win(-1920, 0, 2)), { x: -3820, y: 40, w: 600, h: 130, dpr: 2 }); // a monitor left of the first one
});
test('a nonsense pixel ratio reads as 1 and a missing box gives no rectangle', () => {
  assert.equal(rectOf(el(), win(0, 0, 0)).dpr, 1);
  assert.equal(rectOf(el({ w: 0, h: 0 }), win()), null);
});

test('the answer field is looked for in the order of the three widgets', () => {
  const field = el();
  assert.equal(responseField(doc({ '#g-recaptcha-response': [el()], '[name="h-captcha-response"]': [field] })).value, '');
  assert.equal(responseField(doc({ '[name="cf-turnstile-response"]': [field] })), field);
  assert.equal(responseField(doc({})), null);
});
test('the own form is the form around the field, with its submit control', () => {
  const submit = el();
  const form = { requestSubmit() {}, submit() {}, querySelector: () => submit };
  const own = ownForm(el({ form, submit }));
  assert.equal(own.form, form); assert.equal(own.submit, submit);
  assert.equal(ownForm(el()), null);
});

test('the manifest loads the reporter on pages and the source has no solving service name in it', () => {
  const manifest = JSON.parse(fs.readFileSync(path.join(root, 'extension/manifest.json'), 'utf8'));
  const entry = manifest.content_scripts.find(c => c.js.includes('widget.js'));
  assert.ok(entry, 'widget.js is a content script');
  assert.equal(entry.run_at, 'document_idle');
  assert.deepEqual(entry.matches, ['http://*/*', 'https://*/*']);
  assert.equal(manifest.version, fs.readFileSync(path.join(root, 'Directory.Build.props'), 'utf8').match(/<Version>([^<]+)<\/Version>/)[1]);
  const source = fs.readFileSync(path.join(root, 'extension/widget.js'), 'utf8');
  assert.doesNotMatch(source, /2captcha|anticaptcha|rucaptcha|hcaptcha\.com\/1\/api|api\.cloudflare\.com/i);
});
