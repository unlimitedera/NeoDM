'use strict';
// Localisation helper shared by the service worker, the popup and the page ribbon. Strings live in _locales/<lang>/messages.json (chrome.i18n);
// the language is the browser's UI language, so the extension and the browser agree. A missing message shows its English text.
(() => {
  const RTL = new Set(['ar', 'fa', 'he', 'iw', 'ur', 'ps', 'sd', 'ug', 'yi', 'ckb', 'dv']);
  const api = () => (typeof chrome !== 'undefined' && chrome.i18n) || null;
  const lang = () => { try { return String(api()?.getUILanguage() || 'en').toLowerCase().replace('_', '-'); } catch { return 'en'; } };
  const isRtl = tag => RTL.has(String(tag || lang()).toLowerCase().split(/[-_]/)[0]);
  /** t('key') or t('key', 'a', 'b'): substitutions fill $1, $2 ... of the message (see the placeholders in messages.json). */
  const t = (key, ...subs) => {
    let text = '';
    try { text = api()?.getMessage(key, subs.length ? subs.map(String) : undefined) || ''; } catch { text = ''; }
    return text || key;
  };
  /** Fills [data-i18n] (text), [data-i18n-title] (tooltip) and [data-i18n-label] (aria-label) and sets lang/dir on the document. */
  const apply = (doc = document) => {
    const root = doc.documentElement, tag = lang();
    root.lang = tag; root.dir = isRtl(tag) ? 'rtl' : 'ltr'; root.classList.toggle('rtl', root.dir === 'rtl');
    for (const el of doc.querySelectorAll('[data-i18n]')) el.textContent = t(el.dataset.i18n);
    for (const el of doc.querySelectorAll('[data-i18n-title]')) el.title = t(el.dataset.i18nTitle);
    for (const el of doc.querySelectorAll('[data-i18n-label]')) el.setAttribute('aria-label', t(el.dataset.i18nLabel));
    const title = doc.querySelector('title[data-i18n]'); if (title) doc.title = t(title.dataset.i18n);
  };
  globalThis.NeoI18n = { t, lang, isRtl, apply };
})();
