/* Original NDM widget reporter; no third-party browser integration code.
   Content script: finds a site-check widget (the box the site asks the person to press) on the page, measures where it sits
   on the screen, and tells the NeoDM app through the native host bridge - but only when the app is waiting for exactly this
   page (the app answers the report; a page nobody asked about is left alone). Once the app takes over it also watches for the
   answer field the widget fills and reports the value; then the page's own form is submitted once. */
(function (scope) {
  'use strict';
  /* Each selector with the widget kind it proves. Order matters: the dedicated frame or container is the better witness. */
  const MARKS = [
    ['iframe[src*="challenges.cloudflare.com"]', 'turnstile'],
    ['.cf-turnstile', 'turnstile'],
    ['iframe[src*="recaptcha/api2"]', 'recaptcha'],
    ['.h-captcha', 'hcaptcha'],
    ['[data-sitekey]', 'sitekey'],
  ];
  /** The kind a bare [data-sitekey] element carries, read from its own class list. */
  function kindOf(el, matched) {
    if (matched !== 'sitekey') return matched;
    const cls = String(el.className || '');
    if (/\bh-captcha\b/.test(cls)) return 'hcaptcha';
    if (/\bg-recaptcha\b/.test(cls)) return 'recaptcha';
    return 'turnstile';
  }
  /** A witness must have a box on the page (its own, or the largest of its children for a container). */
  function boxOf(el) {
    let r;
    try { r = el.getBoundingClientRect(); } catch { return null; }
    if (!r || !(r.width >= 2 && r.height >= 2)) return null;
    return r;
  }
  /** The best widget witness in this frame: the first mark that has a visible element, the largest box among them. */
  function find(doc) {
    for (const [selector, mark] of MARKS) {
      let list;
      try { list = doc.querySelectorAll(selector); } catch { continue; }
      let best = null, bestArea = 0;
      for (const el of Array.from(list).slice(0, 40)) {
        const box = boxOf(el);
        if (!box) continue;
        const area = box.width * box.height;
        if (area > bestArea) { bestArea = area; best = el; }
      }
      if (best) return { el: best, kind: kindOf(best, mark) };
    }
    return null;
  }
  /** The widget's rectangle in physical screen pixels: the frame's place on the screen plus the box, times the frame's pixel ratio. */
  function rectOf(el, win) {
    const box = boxOf(el);
    if (!box) return null;
    const dpr = Number(win.devicePixelRatio) > 0 ? Number(win.devicePixelRatio) : 1;
    const sx = Number.isFinite(win.screenX) ? win.screenX : 0, sy = Number.isFinite(win.screenY) ? win.screenY : 0;
    const x = Math.round((sx + box.left) * dpr), y = Math.round((sy + box.top) * dpr);
    const w = Math.max(1, Math.round(box.width * dpr)), h = Math.max(1, Math.round(box.height * dpr));
    return { x, y, w, h, dpr: Math.round(dpr * 100) / 100 };
  }
  /** The field the widget fills with its answer: the site's own hidden input (or the frame's answer box). */
  function responseField(doc) {
    for (const selector of ['[name="cf-turnstile-response"]', '#g-recaptcha-response', '[name="h-captcha-response"]']) {
      try { const el = doc.querySelector(selector); if (el) return el; } catch { }
    }
    return null;
  }
  /** The page's own form that carries the answer field, with a way to submit it; null when the page submits itself some other way. */
  function ownForm(field) {
    const form = field && field.closest ? field.closest('form') : null;
    if (!form) return null;
    const submit = form.querySelector('button[type="submit"],input[type="submit"],button:not([type])');
    return { form, submit };
  }
  scope.NeoWidget = { MARKS, kindOf, find, rectOf, responseField, ownForm };
  if (typeof module !== 'undefined') module.exports = scope.NeoWidget;
})(globalThis);

/* ---- content script side: watch the page for a widget, report it, then watch for the answer ---- */
(() => {
  if (typeof chrome === 'undefined' || !chrome.runtime || globalThis.__neodmWidget) return;
  globalThis.__neodmWidget = true;
  const SCAN_MS = 400, PROBE_MS = 30000, TOKEN_MS = 120000, POLL_MS = 400;
  let probing = false, active = false, scans = 0;
  const timer = setInterval(() => {
    if (active || document.visibilityState === 'unloaded') return;
    scans++;
    if (scans * SCAN_MS > PROBE_MS) { clearInterval(timer); return; }
    const hit = NeoWidget.find(document);
    if (!hit || probing) return;
    probing = true;
    const rect = NeoWidget.rectOf(hit.el, window);
    if (!rect) { probing = false; return; }
    try {
      chrome.runtime.sendMessage({ action: 'captcha-widget-probe', url: String(location.href).slice(0, 2048), widget: hit.kind, rect, tabId: -1 }, reply => {
        probing = false;
        if (chrome.runtime.lastError || !reply || reply.act !== true) return;
        active = true; clearInterval(timer);
        watchToken();
      });
    } catch { probing = false; }
  }, SCAN_MS);
  function watchToken() {
    const started = Date.now();
    let reported = false;
    const poll = setInterval(() => {
      if (reported || Date.now() - started > TOKEN_MS) { clearInterval(poll); return; }
      const field = NeoWidget.responseField(document);
      const value = field ? String(field.value || '') : '';
      if (!value) return;
      reported = true;
      try {
        chrome.runtime.sendMessage({ action: 'captcha-token', url: String(location.href).slice(0, 2048), token: value.slice(0, 4096) }, () => void chrome.runtime.lastError);
      } catch { }
      // The site's own form carries the answer: submit it once, the way the page would.
      const own = NeoWidget.ownForm(field);
      if (own) { try { if (own.submit) own.submit.click(); else if (typeof own.form.requestSubmit === 'function') own.form.requestSubmit(); else own.form.submit(); } catch { } }
    }, POLL_MS);
  }
})();
