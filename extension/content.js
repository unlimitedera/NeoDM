/* NeoDM media ribbon. Renders the tab's media list (kept by the service worker) in a closed shadow root, describes the page's <video>/<audio> elements to the worker,
   outlines an element on request and answers "which element is under the pointer" for the context menu. */
(() => {
  if (globalThis.__neodmRibbon) return;
  const t = (...a) => NeoI18n.t(...a);
  const S = globalThis.NeoSniff;
  const MAX_DOM = 50, SCAN_MS = 400, HL_MS = 3000, STR = 200, THUMB_MAX = 20 * 1024, EAGER_PREVIEWS = 4, ACCENT = '#39b995';
  let items = [], dismissedFor = '', enabled = true, forced = false, host = null, root = null, timer = null, lastDom = '', pointer = null, rerender = false;
  const sending = new Set(), states = new Map(); // id -> {text, ok}
  const canon = url => (S && S.canonicalUrl ? S.canonicalUrl(url) : url);
  const clean = v => Array.from(String(v == null ? '' : v).replace(/\s+/g, ' ').trim()).slice(0, STR).join('');

  /* ---- describing the page's media elements ---- */
  const grabs = new WeakMap();
  /** Small JPEG of the current frame (data URL <= 20 KB); '' when the canvas is tainted, the frame is not ready or canvas is unavailable. */
  function grab(m) {
    const src = m.currentSrc || m.src, g = grabs.get(m) && grabs.get(m).src === src ? grabs.get(m) : { src, tries: 0, thumb: '' };
    grabs.set(m, g);
    if (g.thumb || g.tries >= 3 || m.tagName !== 'VIDEO' || m.readyState < 2 || !m.videoWidth || !m.videoHeight) return g.thumb;
    g.tries++;
    try {
      for (const [w, q] of [[160, 0.6], [160, 0.4], [120, 0.4], [96, 0.35]]) {
        const c = document.createElement('canvas'); c.width = w; c.height = Math.max(1, Math.round(w * m.videoHeight / m.videoWidth));
        const ctx = c.getContext('2d'); if (!ctx) return '';
        ctx.drawImage(m, 0, 0, c.width, c.height);
        const url = c.toDataURL('image/jpeg', q);
        if (typeof url === 'string' && /^data:image\/jpeg/.test(url) && url.length <= THUMB_MAX) { g.thumb = url; break; }
      }
    } catch { /* cross-origin frame without CORS: the poster is used instead */ }
    return g.thumb;
  }
  const isPlaying = m => !m.paused && !m.ended && m.readyState >= 2;
  const area = r => Math.max(0, r.width) * Math.max(0, r.height);
  function visibleFraction(m) {
    const r = m.getBoundingClientRect(), a = area(r); if (!a) return 0;
    const w = Math.max(0, Math.min(r.right, innerWidth) - Math.max(r.left, 0)), h = Math.max(0, Math.min(r.bottom, innerHeight) - Math.max(r.top, 0));
    return Math.min(1, w * h / a);
  }
  /** The element's own label, else the heading of the closest container that holds only this element, else (main video) the page title. */
  function titleGuess(m, main) {
    const own = clean(m.getAttribute('aria-label') || m.getAttribute('title')); if (own) return own;
    for (let node = m.parentElement, depth = 0; node && node !== document.body && depth < 4; node = node.parentElement, depth++) {
      if (node.querySelectorAll('video,audio').length !== 1) break;
      const label = clean(node.getAttribute('aria-label') || node.getAttribute('title')); if (label) return label;
      const h = node.querySelector('h1,h2,h3'); const text = h && clean(h.textContent); if (text) return text;
    }
    if (!main) return '';
    const og = document.querySelector('meta[property="og:title"]');
    return clean((og && og.getAttribute('content')) || document.title);
  }
  /** Every <video>/<audio> with a usable source: { el, key, url|'' , blob }. DRM-protected elements are skipped. */
  function collect() {
    const out = [];
    for (const m of Array.from(document.querySelectorAll('video,audio')).slice(0, MAX_DOM)) {
      if (m.mediaKeys) continue;
      const urls = [m.currentSrc, m.getAttribute('src') && m.src, ...Array.from(m.querySelectorAll('source[src]')).map(s => s.src)];
      const url = urls.find(u => u && /^https?:/i.test(u));
      if (url) out.push({ el: m, key: canon(url), url, blob: false });
      else if (/^blob:/i.test(m.currentSrc || '')) out.push({ el: m, key: 'blob:', url: '', blob: true });
    }
    return out;
  }
  function scanDom() {
    timer = null;
    const found = collect(), playing = found.filter(f => isPlaying(f.el)).sort((a, b) => area(b.el.getBoundingClientRect()) - area(a.el.getBoundingClientRect()));
    const main = playing.length ? playing[0].el : found.length === 1 ? found[0].el : null;
    const sources = [], sig = [];
    for (const f of found) {
      const m = f.el, poster = m.tagName === 'VIDEO' && m.poster && /^https?:/i.test(m.poster) ? m.poster : '';
      const vis = Math.round(visibleFraction(m) * 4) / 4, thumb = grab(m);
      const s = { url: f.blob ? 'blob:' : f.url, tag: m.tagName.toLowerCase(), title: titleGuess(m, m === main), dur: Number.isFinite(m.duration) ? Math.round(m.duration * 10) / 10 : 0, w: m.videoWidth || 0, h: m.videoHeight || 0, playing: isPlaying(m), vis, ...(poster ? { poster } : {}), ...(thumb ? { thumb } : {}) };
      sources.push(s); sig.push({ ...s, thumb: !!thumb });
    }
    const key = JSON.stringify(sig);
    if (key === lastDom) return; lastDom = key;
    if (sources.length) chrome.runtime.sendMessage({ action: 'media-dom', sources }).catch(() => {});
  }
  const schedule = () => { if (timer === null) timer = setTimeout(scanDom, SCAN_MS); };

  /* ---- outline an element ("show on page") ---- */
  let hlEl = null, hlTimer = null, hlSaved = null;
  function unhighlight() {
    clearTimeout(hlTimer); hlTimer = null;
    if (hlEl && hlSaved) for (const [prop, [value, priority]] of Object.entries(hlSaved)) { if (value) hlEl.style.setProperty(prop, value, priority); else hlEl.style.removeProperty(prop); }
    hlEl = null; hlSaved = null;
  }
  function elementFor(id, blob) {
    const found = collect();
    const hit = found.find(f => f.key === id); if (hit) return hit.el;
    if (!blob) return null;
    const blobs = found.filter(f => f.blob).sort((a, b) => (isPlaying(b.el) - isPlaying(a.el)) || (area(b.el.getBoundingClientRect()) - area(a.el.getBoundingClientRect())));
    return blobs.length ? blobs[0].el : null;
  }
  /** Outlines the element behind a listed item for HL_MS (each call renews the timer, so it stops when the requests stop). */
  function highlight(id, { blob = false, scroll = true } = {}) {
    const el = elementFor(id, blob); if (!el) return false;
    if (hlEl !== el) {
      unhighlight(); hlEl = el; hlSaved = {};
      for (const prop of ['outline', 'outline-offset']) hlSaved[prop] = [el.style.getPropertyValue(prop), el.style.getPropertyPriority(prop)];
      el.style.setProperty('outline', `3px solid ${ACCENT}`, 'important'); el.style.setProperty('outline-offset', '-3px', 'important');
    }
    if (scroll) { const r = el.getBoundingClientRect(); if (r.bottom < 0 || r.top > innerHeight || r.right < 0 || r.left > innerWidth) el.scrollIntoView({ behavior: 'smooth', block: 'center', inline: 'nearest' }); }
    clearTimeout(hlTimer); hlTimer = setTimeout(unhighlight, HL_MS);
    return true;
  }

  /* ---- context menu: which media element is under the pointer (works under an overlay) ---- */
  document.addEventListener('contextmenu', e => { pointer = { x: e.clientX, y: e.clientY, time: Date.now() }; }, { capture: true, passive: true });
  function contextTarget() {
    if (!pointer || Date.now() - pointer.time > 60000) return { found: false };
    const found = collect(), isMedia = n => n && (n.tagName === 'VIDEO' || n.tagName === 'AUDIO');
    let el = (document.elementsFromPoint ? document.elementsFromPoint(pointer.x, pointer.y) : []).find(isMedia);
    if (!el) el = found.map(f => f.el).find(m => { const r = m.getBoundingClientRect(); return area(r) > 0 && pointer.x >= r.left && pointer.x <= r.right && pointer.y >= r.top && pointer.y <= r.bottom; });
    if (!el) return { found: false };
    const f = found.find(x => x.el === el);
    return { found: true, tag: el.tagName.toLowerCase(), url: f && !f.blob ? f.url : '', blob: !!(f && f.blob), playing: isPlaying(el) };
  }

  /* ---- the ribbon ---- */
  function build() {
    host = document.createElement('div'); host.setAttribute('data-neodm-ribbon', '');
    host.style.cssText = 'all:initial!important;position:fixed!important;' + (NeoI18n.isRtl() ? 'left' : 'right') + ':16px!important;bottom:16px!important;z-index:2147483647!important;';
    root = host.attachShadow({ mode: 'closed' });
    const style = document.createElement('style');
    style.textContent = `.bar{font:13px/1.3 system-ui,Segoe UI,sans-serif;color:#f4f7fa;background:#17212e;border:1px solid #2f4258;border-radius:10px;box-shadow:0 6px 20px rgba(0,0,0,.35);width:360px;overflow:hidden}
.head{display:flex;align-items:center;gap:8px;padding:6px;padding-inline-start:10px;border-bottom:1px solid #263649;font-weight:600;font-size:12px;letter-spacing:.4px;color:#39b995}
.head span{flex:1}.x{all:unset;cursor:pointer;padding:2px 8px;font-size:16px;color:#c7d2de;border-radius:6px}.x:hover,.x:focus-visible{background:#263649;color:#fff}
.rows{max-height:260px;overflow:auto}.row{display:flex;align-items:center;gap:8px;padding:8px 10px}.row+.row{border-top:1px solid #1f2d3e}.row:hover,.row:focus-within{background:#1c2a3b}
.info{flex:1;min-width:0}.t{display:-webkit-box;-webkit-line-clamp:2;-webkit-box-orient:vertical;overflow:hidden;overflow-wrap:anywhere;font-weight:600}.m{font-size:11px;opacity:.85;margin-top:2px;overflow-wrap:anywhere}.s{font-size:11px;opacity:.7;margin-top:2px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.s.err{color:#ff9d9d;opacity:1}.s.ok{color:#7be0b9;opacity:1}
.live{display:inline-block;font-size:10px;font-weight:700;letter-spacing:.3px;color:#0c131b;background:#39b995;border-radius:4px;padding:1px 5px;margin-top:3px}.note{padding:8px 10px;font-size:12px;line-height:1.4;color:#c7d2de}.note+.note,.row+.note{border-top:1px solid #1f2d3e}
.neo-thumb:focus-visible{outline:2px solid #39b995!important;outline-offset:1px}
.dl{all:unset;cursor:pointer;white-space:nowrap;background:#2da57d;color:#fff;font-weight:600;font-size:12px;padding:6px 10px;border-radius:6px}.dl:hover,.dl:focus-visible{background:#34bd90}.dl[disabled]{opacity:.55;cursor:default}.sub .dl{background:#3a4c61}`;
    const bar = document.createElement('div'); bar.className = 'bar'; bar.dir = NeoI18n.isRtl() ? 'rtl' : 'ltr'; bar.setAttribute('role', 'region'); bar.setAttribute('aria-label', t('ribbon_label'));
    const head = document.createElement('div'); head.className = 'head';
    const title = document.createElement('span'); title.textContent = 'NeoDM';
    const x = document.createElement('button'); x.className = 'x'; x.type = 'button'; x.textContent = '\u00d7'; x.title = t('ribbon_dismiss'); x.setAttribute('aria-label', t('ribbon_dismiss'));
    x.addEventListener('click', () => { dismissedFor = items.map(i => i.id).join('\n'); forced = false; unhighlight(); if (S) S.stopPreview(); render(); });
    head.append(title, x);
    const rows = document.createElement('div'); rows.className = 'rows'; rows.setAttribute('aria-live', 'polite');
    bar.append(head, rows); root.append(style, bar);
    document.documentElement.append(host);
  }
  /** Hover or keyboard focus on a row outlines its element; the outline is renewed while the pointer stays and removed on leave. */
  function hoverOutline(row, i) {
    let beat = null;
    const on = () => { if (beat) return; highlight(i.id, { blob: !!i.viaBlob }); beat = setInterval(() => highlight(i.id, { blob: !!i.viaBlob, scroll: false }), 1000); };
    const off = () => { clearInterval(beat); beat = null; unhighlight(); };
    row.addEventListener('mouseenter', on); row.addEventListener('focusin', on); row.addEventListener('mouseleave', off); row.addEventListener('focusout', off);
  }
  function render() {
    if (S && S.previewActive()) { rerender = true; return; } // do not cut a playing preview off
    rerender = false;
    const visible = (enabled || forced) && items.length && dismissedFor !== items.map(i => i.id).join('\n');
    if (!visible) { unhighlight(); host?.remove(); host = null; root = null; return; }
    if (!host) build(); else if (!host.isConnected) document.documentElement.append(host);
    const rows = root.querySelector('.rows'); rows.textContent = '';
    const ordered = items.map((i, n) => [i, n]).sort(([a, x], [b, y]) => (!!a.info - !!b.info) || (!!b.playing - !!a.playing) || (!!b.complete - !!a.complete) || (x - y)).map(([i]) => i);
    let previews = 0;
    for (const i of ordered) {
      if (i.info) { const note = document.createElement('div'); note.className = 'note'; note.textContent = i.text; note.dataset.id = i.id; rows.append(note); continue; }
      const row = document.createElement('div'); row.className = 'row' + (i.complete ? '' : ' sub'); row.dataset.id = i.id;
      const d = S ? S.describeItem(i, t) : { title: i.url, meta: '' };
      if (S) {
        const lazy = !i.thumb && !i.poster && previews++ >= EAGER_PREVIEWS;
        row.append(S.thumbnail(document, i, { width: 64, height: 36, eager: !lazy, play: t('media_preview_play'), stop: t('media_preview_stop'), onStop: () => { if (rerender) render(); } }).el);
      }
      const info = document.createElement('div'); info.className = 'info';
      const name = document.createElement('div'); name.className = 't'; name.textContent = d.title; name.title = d.title;
      const meta = document.createElement('div'); meta.className = 'm'; meta.textContent = d.meta; meta.title = i.url;
      const st = states.get(i.id), s = document.createElement('div'); s.className = 's' + (st ? (st.ok ? ' ok' : ' err') : ''); s.textContent = st ? st.text : (i.complete ? t('ribbon_complete') : t('ribbon_partial'));
      info.append(name, meta);
      if (i.playing) { const live = document.createElement('span'); live.className = 'live'; live.textContent = t('media_badge_playing'); info.append(live); }
      info.append(s);
      const b = document.createElement('button'); b.className = 'dl'; b.type = 'button';
      b.textContent = sending.has(i.id) ? t('ribbon_waiting') : st?.ok ? t('ribbon_sent') : t('ribbon_download');
      b.disabled = sending.has(i.id) || !!st?.ok;
      b.addEventListener('click', () => download(i));
      row.append(info, b); hoverOutline(row, i); rows.append(row);
    }
  }
  async function download(i) {
    if (sending.has(i.id)) return;
    sending.add(i.id); states.delete(i.id); render();
    try {
      const r = await chrome.runtime.sendMessage({ action: 'media-download', id: i.id });
      const ok = r?.ok === true && typeof r.id === 'string' && r.id.trim().length > 0;
      states.set(i.id, ok ? { ok: true, text: t('ribbon_accepted') } : { ok: false, text: r?.error || t('ribbon_not_accepted') });
    } catch { states.set(i.id, { ok: false, text: t('ribbon_bridge_unavailable') }); }
    sending.delete(i.id); render();
  }
  function setItems(next) { items = Array.isArray(next) ? next : []; if (!items.length) { dismissedFor = ''; forced = false; } render(); }

  const obs = new MutationObserver(schedule);
  obs.observe(document.documentElement, { subtree: true, childList: true, attributes: true, attributeFilter: ['src'] });
  const events = ['loadedmetadata', 'loadeddata', 'loadstart', 'play', 'playing', 'pause', 'ended', 'seeked', 'durationchange', 'resize'];
  for (const e of events) document.addEventListener(e, schedule, { capture: true, passive: true });
  addEventListener('scroll', schedule, { capture: true, passive: true });
  addEventListener('resize', schedule, { passive: true });

  chrome.runtime.onMessage.addListener((m, sender, respond) => {
    if (!m) return false;
    if (m.type === 'neodm-media') setItems(m.items);
    else if (m.type === 'neodm-highlight' && typeof m.id === 'string') { if (m.on === true) highlight(m.id, { blob: m.blob === true, scroll: m.scroll !== false }); else unhighlight(); }
    else if (m.type === 'neodm-context-target') { if (respond) respond(contextTarget()); }
    else if (m.type === 'neodm-show-ribbon') { forced = true; dismissedFor = ''; render(); if (respond) respond({ ok: true }); }
    return false;
  });
  addEventListener('pagehide', () => { unhighlight(); if (S) S.stopPreview(); });
  chrome.storage.onChanged.addListener((c, area) => { if (area === 'local' && c.ribbonEnabled) { enabled = c.ribbonEnabled.newValue !== false; render(); } });
  chrome.storage.local.get('ribbonEnabled').then(s => { enabled = s.ribbonEnabled !== false; return chrome.runtime.sendMessage({ action: 'media-list' }); }).then(r => setItems(r?.items)).catch(() => {});
  scanDom();
  globalThis.__neodmRibbon = { refresh: scanDom, items: () => items.slice(), highlight, unhighlight, contextTarget, dispose() { obs.disconnect(); clearTimeout(timer); unhighlight(); host?.remove(); delete globalThis.__neodmRibbon; } };
})();
