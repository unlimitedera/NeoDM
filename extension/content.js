/* NeoDM media ribbon. Renders the tab's media list (kept by the service worker) in a closed shadow root, describes the page's <video>/<audio> elements to the worker
   (with a stable key per element and the element's playback events, so the worker can tell which element fetched which file), draws a small bar over each video that has a file,
   outlines an element on request and answers "which element is under the pointer" for the context menu. */
(() => {
  if (globalThis.__neodmRibbon) return;
  const t = (...a) => NeoI18n.t(...a);
  const S = globalThis.NeoSniff;
  const MAX_DOM = 200, SCAN_MS = 400, HL_MS = 3000, STR = 200, THUMB_MAX = 20 * 1024, EAGER_PREVIEWS = 4, ACCENT = '#39b995';
  let items = [], dismissedFor = '', enabled = true, buttonOn = true, forced = false, host = null, root = null, timer = null, lastDom = '', pointer = null, rerender = false;
  const sending = new Set(), states = new Map(); // id -> {text, ok}
  const canon = url => (S && S.canonicalUrl ? S.canonicalUrl(url) : url);
  const clean = v => Array.from(String(v == null ? '' : v).replace(/\s+/g, ' ').trim()).slice(0, STR).join('');

  /* ---- describing the page's media elements ---- */
  const grabs = new WeakMap(), seen = new WeakSet(); let seenIo = null, sentAny = false;
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
  /* ---- a stable key per media element; elements removed by a virtualized feed keep their key while the page holds them ---- */
  const keys = new WeakMap(), byKey = new Map(); let keySeq = 0;
  const keyOf = el => {
    let k = keys.get(el);
    if (!k) { k = 'e' + Math.random().toString(36).slice(2, 7) + (++keySeq).toString(36); keys.set(el, k); if (typeof WeakRef === 'function') byKey.set(k, new WeakRef(el)); }
    return k;
  };
  const elOf = k => { const r = byKey.get(k), el = r && r.deref(); if (!el) byKey.delete(k); return el || null; };
  const isPlaying = m => !m.paused && !m.ended && m.readyState >= 2;
  const area = r => Math.max(0, r.width) * Math.max(0, r.height);
  function visibleFraction(m) {
    const r = m.getBoundingClientRect(), a = area(r); if (!a) return 0;
    const w = Math.max(0, Math.min(r.right, innerWidth) - Math.max(r.left, 0)), h = Math.max(0, Math.min(r.bottom, innerHeight) - Math.max(r.top, 0));
    return Math.min(1, w * h / a);
  }
  /** First ~80 characters of text in the post/article that contains the element (feeds such as Facebook put the caption there). */
  function articleText(m) {
    const box = m.closest('[role="article"],article,[data-pagelet^="FeedUnit"]'); if (!box || typeof document.createTreeWalker !== 'function') return '';
    let visited = 0;
    const walker = document.createTreeWalker(box, 4 /* SHOW_TEXT */, { acceptNode: n => { const p = n.parentElement; return ++visited < 400 && p && !/^(SCRIPT|STYLE|NOSCRIPT|BUTTON)$/.test(p.tagName) && !p.closest('video,audio') && n.nodeValue.trim().length > 1 ? 1 : 2; } });
    let out = ''; for (let n = walker.nextNode(); n && out.length < 160; n = walker.nextNode()) out += ' ' + n.nodeValue;
    return Array.from(clean(out)).slice(0, 80).join('').trim();
  }
  /** Social sites prefix the title with counters ("2.8M views · 1.3K reactions | Title | Facebook"): they are not part of the video's name. */
  const pageTitle = text => { let t = text; for (let i = 0; i < 3; i++) t = t.replace(/^\s*[\d.,]+\s?[KMB]?\s+(views?|reactions?|likes?|comments?)\b[^|]*\|\s*/i, ''); return t.replace(/\s*\|\s*Facebook\s*$/i, '').trim() || text; };
  /** The element's own label, else the heading of the closest container that holds only this element, else the post text around it, else (main or blob video) the page title. */
  function titleGuess(m, main) {
    const own = clean(m.getAttribute('aria-label') || m.getAttribute('title')); if (own) return own;
    for (let node = m.parentElement, depth = 0; node && node !== document.body && depth < 4; node = node.parentElement, depth++) {
      if (node.querySelectorAll('video,audio').length !== 1) break;
      const label = clean(node.getAttribute('aria-label') || node.getAttribute('title')); if (label) return label;
      const h = node.querySelector('h1,h2,h3'); const text = h && clean(h.textContent); if (text) return text;
    }
    const post = articleText(m); if (post) return post;
    if (!main && !/^blob:/i.test(m.currentSrc || '')) return '';
    const og = document.querySelector('meta[property="og:title"]');
    return pageTitle(clean((og && og.getAttribute('content')) || document.title));
  }
  /** Every <video>/<audio> with a usable source: { el, key, url|'' , blob }. DRM-protected elements are skipped. */
  function collect() {
    const out = [];
    for (const m of Array.from(document.querySelectorAll('video,audio')).slice(0, MAX_DOM)) {
      if (m.mediaKeys) continue;
      const urls = [m.currentSrc, m.getAttribute('src') && m.src, ...Array.from(m.querySelectorAll('source[src]')).map(s => s.src)];
      const url = urls.find(u => u && /^https?:/i.test(u));
      if (url) out.push({ el: m, ek: keyOf(m), key: canon(url), url, blob: false });
      else if (/^blob:/i.test(m.currentSrc || '')) out.push({ el: m, ek: keyOf(m), key: 'blob:', url: '', blob: true });
    }
    return out;
  }
  function scanDom() {
    timer = null;
    const found = collect(); syncBars(found);
    for (const f of found) if (!seen.has(f.el)) { seen.add(f.el); try { if (!seenIo && typeof IntersectionObserver === 'function') seenIo = new IntersectionObserver(() => schedule(), { threshold: [0, 0.25, 0.5, 0.75, 1] }); seenIo && seenIo.observe(f.el); } catch {} } // a video that scrolls into or out of view is described again
    const playing = found.filter(f => isPlaying(f.el)).sort((a, b) => area(b.el.getBoundingClientRect()) - area(a.el.getBoundingClientRect()));
    const main = playing.length ? playing[0].el : found.length === 1 ? found[0].el : null;
    const sources = [], sig = [];
    for (const f of found) {
      const m = f.el, poster = m.tagName === 'VIDEO' && m.poster && /^https?:/i.test(m.poster) ? m.poster : '';
      const vis = Math.round(visibleFraction(m) * 4) / 4, thumb = grab(m);
      const s = { key: f.ek, url: f.blob ? 'blob:' : f.url, tag: m.tagName.toLowerCase(), title: titleGuess(m, m === main), dur: Number.isFinite(m.duration) ? Math.round(m.duration * 10) / 10 : 0, w: m.videoWidth || 0, h: m.videoHeight || 0, playing: isPlaying(m), vis, ...(poster ? { poster } : {}), ...(thumb ? { thumb } : {}) };
      sources.push(s); sig.push({ ...s, thumb: !!thumb });
    }
    const key = JSON.stringify(sig);
    if (key === lastDom) return; lastDom = key;
    if (sources.length || sentAny) { sentAny = sources.length > 0; chrome.runtime.sendMessage({ action: 'media-dom', sources }).catch(() => {}); }
  }
  const schedule = () => { if (timer === null) timer = setTimeout(scanDom, SCAN_MS); };
  /* ---- playback events, batched: the worker pairs a newly seen file with the element whose events are closest in time ---- */
  const EVENT_GAP = { timeupdate: 600, progress: 300 }, lastEvent = new WeakMap(); let evQueue = [], evTimer = null;
  function flushEvents() {
    evTimer = null; const events = evQueue; evQueue = []; if (!events.length) return;
    try { chrome.runtime.sendMessage({ action: 'media-event', events }).catch(() => {}); } catch {}
  }
  function noteEvent(e) {
    const el = e.target; if (!el || (el.tagName !== 'VIDEO' && el.tagName !== 'AUDIO') || !(e.type in EVENT_TYPES)) return;
    const now = Date.now(), seen = lastEvent.get(el) || {};
    if (now - (seen[e.type] || 0) < (EVENT_GAP[e.type] || 0)) return;
    seen[e.type] = now; lastEvent.set(el, seen);
    evQueue.push({ key: keyOf(el), type: e.type, t: now }); if (evQueue.length > 100) evQueue.shift();
    if (evTimer === null) evTimer = setTimeout(flushEvents, 100);
  }
  const EVENT_TYPES = { loadstart: 1, loadedmetadata: 1, progress: 1, playing: 1, seeked: 1, timeupdate: 1 };

  /* ---- outline an element ("show on page") ---- */
  let hlEl = null, hlTimer = null, hlSaved = null;
  function unhighlight() {
    clearTimeout(hlTimer); hlTimer = null;
    if (hlEl && hlSaved) for (const [prop, [value, priority]] of Object.entries(hlSaved)) { if (value) hlEl.style.setProperty(prop, value, priority); else hlEl.style.removeProperty(prop); }
    hlEl = null; hlSaved = null;
  }
  function elementFor(id, blob, owner) {
    const own = owner && elOf(owner); if (own && own.isConnected) return own;
    const found = collect();
    const hit = found.find(f => f.key === id); if (hit) return hit.el;
    if (!blob) return null;
    const blobs = found.filter(f => f.blob).sort((a, b) => (isPlaying(b.el) - isPlaying(a.el)) || (area(b.el.getBoundingClientRect()) - area(a.el.getBoundingClientRect())));
    return blobs.length ? blobs[0].el : null;
  }
  /** Outlines the element behind a listed item for HL_MS (each call renews the timer, so it stops when the requests stop). */
  function highlight(id, { blob = false, scroll = true, owner = '' } = {}) {
    const el = elementFor(id, blob, owner); if (!el) return false;
    if (hlEl !== el) {
      unhighlight(); hlEl = el; hlSaved = {};
      for (const prop of ['outline', 'outline-offset']) hlSaved[prop] = [el.style.getPropertyValue(prop), el.style.getPropertyPriority(prop)];
      el.style.setProperty('outline', `3px solid ${ACCENT}`, 'important'); el.style.setProperty('outline-offset', '-3px', 'important');
    }
    if (scroll) { const r = el.getBoundingClientRect(); if (r.bottom < 0 || r.top > innerHeight || r.right < 0 || r.left > innerWidth) el.scrollIntoView({ behavior: 'smooth', block: 'center', inline: 'nearest' }); }
    clearTimeout(hlTimer); hlTimer = setTimeout(unhighlight, HL_MS);
    return true;
  }

  /* ---- context menu: which media element is under the pointer (works under an overlay and inside a link that wraps the video) ---- */
  const isMedia = n => n && (n.tagName === 'VIDEO' || n.tagName === 'AUDIO');
  function mediaAt(x, y) {
    const el = (document.elementsFromPoint ? document.elementsFromPoint(x, y) : []).find(isMedia);
    if (el) return el;
    return collect().map(f => f.el).find(m => { const r = m.getBoundingClientRect(); return area(r) > 0 && x >= r.left && x <= r.right && y >= r.top && y <= r.bottom; }) || null;
  }
  document.addEventListener('contextmenu', e => { pointer = { x: e.clientX, y: e.clientY, time: Date.now(), el: mediaAt(e.clientX, e.clientY) }; }, { capture: true, passive: true });
  function contextTarget() {
    if (!pointer || Date.now() - pointer.time > 60000) return { found: false };
    const el = pointer.el && pointer.el.isConnected ? pointer.el : mediaAt(pointer.x, pointer.y);
    if (!el) return { found: false };
    const f = collect().find(x => x.el === el);
    return { found: true, key: keyOf(el), title: titleGuess(el, false), tag: el.tagName.toLowerCase(), url: f && !f.blob ? f.url : '', blob: !!(f && f.blob), playing: isPlaying(el) };
  }

  /* ---- one small bar over each video that has a file ----
     All bars live in ONE closed shadow root (position:fixed, moved with transform). Layout runs in a requestAnimationFrame that is requested by scroll, resize,
     pointer, playback, ResizeObserver and IntersectionObserver events: it first reads every box, then writes every position. A 500 ms timer runs only while a bar is
     on screen, to follow layout shifts that fire no event. Only the bar itself takes pointer events. */
  const BAR_TICK_MS = 500;
  const bars = new Map(), dismissed = new WeakSet(), observed = new WeakSet();
  let barHost = null, barRoot = null, raf = 0, tick = null, ptr = null, ro = null, io = null;
  const barsAllowed = () => buttonOn && !!S && !S.isYouTubeHost(location.href) && !isMedia(document.fullscreenElement); // fullscreen on a <video> itself leaves no room for a bar; a fullscreen container hosts it
  function ensureBarHost() {
    if (barHost && barHost.isConnected) return;
    if (!barHost) {
      barHost = document.createElement('div'); barHost.setAttribute('data-neodm-bars', '');
      barHost.style.cssText = 'all:initial!important;position:fixed!important;left:0!important;top:0!important;width:0!important;height:0!important;overflow:visible!important;z-index:2147483646!important;pointer-events:none!important;';
      barRoot = barHost.attachShadow({ mode: 'closed' });
      const style = document.createElement('style');
      style.textContent = `.b{position:fixed;left:0;top:0;display:flex;align-items:center;gap:6px;font:600 12px/1 system-ui,Segoe UI,sans-serif;color:#f4f7fa;background:rgba(23,33,46,.92);border:1px solid #2f4258;border-radius:8px;padding:3px;padding-inline-start:8px;opacity:.92;pointer-events:auto;white-space:nowrap;max-width:320px;will-change:transform;contain:layout style}
[hidden]{display:none!important}.b.off{visibility:hidden;pointer-events:none}.mk{color:#39b995;letter-spacing:.3px}.q{font-weight:500;opacity:.9;overflow:hidden;text-overflow:ellipsis;max-width:150px}.q.ok{color:#7be0b9}.q.err{color:#ff9d9d}
button{all:unset;cursor:pointer;box-sizing:border-box;font:inherit}.d{background:#2da57d;color:#fff;padding:5px 9px;border-radius:6px}.d:hover,.d:focus-visible,.m:hover,.m:focus-visible,.a:hover,.a:focus-visible{background:#34bd90}.d[disabled]{opacity:.6;cursor:default}
.m{background:#3a4c61;color:#fff;padding:5px 6px;border-radius:6px}.x{padding:3px 6px;font-size:15px;line-height:1;color:#c7d2de;border-radius:6px}.x:hover,.x:focus-visible{background:#263649;color:#fff}
.menu{position:absolute;top:100%;margin-top:4px;inset-inline-start:0;background:#17212e;border:1px solid #2f4258;border-radius:8px;padding:3px;display:none}.menu.open{display:block}.a{display:block;background:#3a4c61;color:#fff;padding:6px 10px;border-radius:6px}`;
      barRoot.append(style);
    }
    document.documentElement.append(barHost);
  }
  /** What the bar says: "720p 12.3 MB", plus "Video only" when this file has no sound. */
  function barText(item) {
    const q = item.quality || (item.h ? item.h + 'p' : ''), size = S.fmtSize(item.size);
    return [[q, size].filter(Boolean).join(' '), item.track === 'video-only' ? t('kind_video_only') : ''].filter(Boolean).join(' \u00b7 ');
  }
  function fillBar(rec) {
    const n = rec.parts; if (!n) return;
    const st = rec.status, main = rec.main;
    n.dl.textContent = rec.busy ? t('ribbon_waiting') : st && st.ok ? t('ribbon_sent') : t('bar_download'); n.dl.disabled = !!rec.busy;
    n.q.textContent = st && !st.ok ? st.text : main ? barText(main) : ''; n.q.title = st ? st.text : main ? main.url : ''; n.q.className = 'q' + (st ? (st.ok ? ' ok' : ' err') : '');
    n.more.hidden = !rec.audio; n.menu.classList.remove('open'); n.more.setAttribute('aria-expanded', 'false');
    rec.measure = true;
  }
  function buildBar(rec) {
    ensureBarHost();
    const node = document.createElement('div'); node.className = 'b off'; node.dir = NeoI18n.isRtl() ? 'rtl' : 'ltr'; node.setAttribute('role', 'group'); node.setAttribute('aria-label', t('ribbon_label'));
    const mk = document.createElement('span'); mk.className = 'mk'; mk.textContent = 'NeoDM';
    const dl = document.createElement('button'); dl.type = 'button'; dl.className = 'd';
    const q = document.createElement('span'); q.className = 'q';
    const more = document.createElement('button'); more.type = 'button'; more.className = 'm'; more.textContent = '\u25be'; more.title = t('bar_more'); more.setAttribute('aria-label', t('bar_more')); more.setAttribute('aria-haspopup', 'true'); more.hidden = true;
    const x = document.createElement('button'); x.type = 'button'; x.className = 'x'; x.textContent = '\u00d7'; x.title = t('bar_hide'); x.setAttribute('aria-label', t('bar_hide'));
    const menu = document.createElement('div'); menu.className = 'menu'; menu.setAttribute('role', 'menu');
    const audio = document.createElement('button'); audio.type = 'button'; audio.className = 'a'; audio.setAttribute('role', 'menuitem'); audio.textContent = t('bar_audio_only'); menu.append(audio);
    node.append(mk, dl, q, more, x, menu);
    for (const type of ['click', 'mousedown', 'mouseup', 'pointerdown', 'pointerup', 'dblclick']) node.addEventListener(type, e => e.stopPropagation()); // the page must not see clicks on the bar (a link around the video would open)
    dl.addEventListener('click', e => { if (!e.isTrusted) return; if (e.detail > 0) dl.blur(); /* a mouse click must not keep the bar open */ if (rec.main) barDownload(rec, rec.main); else { forced = true; dismissedFor = ''; render(); } }); // no single file for this player: the list lets the user choose
    audio.addEventListener('click', e => { if (!e.isTrusted) return; menu.classList.remove('open'); if (rec.audio) barDownload(rec, rec.audio); });
    more.addEventListener('click', e => { if (!e.isTrusted) return; const open = !menu.classList.contains('open'); menu.classList.toggle('open', open); more.setAttribute('aria-expanded', String(open)); });
    x.addEventListener('click', e => { if (!e.isTrusted) return; dismissed.add(rec.el); removeBar(rec.key); });
    barRoot.append(node); rec.node = node; rec.parts = { dl, q, more, menu }; fillBar(rec);
  }
  async function barDownload(rec, item) {
    if (rec.busy || !item) return;
    rec.busy = true; rec.status = null; fillBar(rec);
    try {
      const r = await chrome.runtime.sendMessage({ action: 'media-download', id: item.id });
      rec.status = r && r.ok === true && typeof r.id === 'string' && r.id.trim() ? { ok: true, text: t('ribbon_accepted') } : { ok: false, text: (r && r.error) || t('ribbon_not_accepted') };
    } catch { rec.status = { ok: false, text: t('ribbon_bridge_unavailable') }; }
    rec.busy = false; fillBar(rec);
    setTimeout(() => { rec.status = null; fillBar(rec); }, 6000);
  }
  function removeBar(key) {
    const rec = bars.get(key); if (!rec) return;
    rec.node?.remove(); bars.delete(key);
    try { ro && ro.unobserve(rec.el); io && io.unobserve(rec.el); } catch {}
    observed.delete(rec.el);
  }
  /** Decides which elements get a bar (they own, or are URL-matched to, a listed file) and keeps the observers on exactly those. */
  function syncBars(found) {
    const media = barsAllowed() ? items.filter(i => !i.info && (i.kind === 'video' || i.kind === 'audio')) : [], keep = new Set();
    if (media.length) for (const f of found) {
      if (dismissed.has(f.el)) continue;
      const mine = media.filter(i => i.owner === f.ek || (f.url && i.id === f.key)), direct = f.url ? mine.find(i => i.id === f.key) : null; // the file the element shows now beats older files of a reused element
      let main = direct || S.bestVideo(mine) || S.bestAudio(mine);
      if (!main && f.blob && media.some(i => !i.owner && !i.dom && i.complete !== false)) main = null; // a player with no file of its own while the tab has unclaimed files: the bar opens the list
      else if (!main) continue;
      const audio = !main || main.kind === 'audio' ? null : S.bestAudio(mine.filter(i => i.track === 'audio-only' || i.kind === 'audio'));
      keep.add(f.ek);
      const rec = bars.get(f.ek) || { key: f.ek, el: f.el, node: null }; bars.set(f.ek, rec);
      const changed = rec.main !== main || rec.audio !== audio || rec.el !== f.el;
      rec.el = f.el; rec.main = main; rec.audio = audio;
      if (changed && rec.node) fillBar(rec);
      if (!observed.has(f.el)) {
        observed.add(f.el);
        if (!ro && typeof ResizeObserver === 'function') ro = new ResizeObserver(() => wake());
        if (!io && typeof IntersectionObserver === 'function') io = new IntersectionObserver(() => wake(), { threshold: [0, 0.25, 0.5, 1] });
        try { ro && ro.observe(f.el); io && io.observe(f.el); } catch {}
      }
    }
    for (const key of Array.from(bars.keys())) if (!keep.has(key)) removeBar(key);
    wake();
  }
  function wake() { if (!raf && bars.size) raf = requestAnimationFrame(layout); }
  const hovered = (r) => !!ptr && ptr.x >= r.left && ptr.x <= r.right && ptr.y >= r.top && ptr.y <= r.bottom;
  function layout() {
    raf = 0;
    if (!bars.size) { clearInterval(tick); tick = null; return; }
    const view = { w: innerWidth, h: innerHeight }, rtl = NeoI18n.isRtl(), plan = [], fs = document.fullscreenElement;
    const home = fs && !isMedia(fs) ? fs : document.documentElement; // in fullscreen only the fullscreen element and its children are painted
    if (barHost && home && barHost.parentNode !== home) { try { home.append(barHost); } catch {} }
    // read phase: every box and every bar size, no writes
    for (const rec of bars.values()) {
      if (rec.node && rec.measure) { rec.w = rec.node.offsetWidth; rec.h = rec.node.offsetHeight; rec.measure = false; }
      const r = rec.el.isConnected ? rec.el.getBoundingClientRect() : null;
      const place = r && (!fs || fs.contains(rec.el)) && S.barPlacement(r, view, { w: rec.w || 200, h: rec.h || 28 }, rtl);
      plan.push([rec, place, !!place && (hovered(r) || isPlaying(rec.el) || document.activeElement === rec.el || (rec.node && rec.node.matches(':focus-within')))]);
    }
    // write phase
    let onScreen = false;
    for (const [rec, place, show] of plan) {
      if (place) onScreen = true;
      if (show && !rec.node) buildBar(rec);
      if (!rec.node) continue;
      if (show) rec.node.style.transform = `translate(${place.x}px,${place.y}px)`;
      rec.node.classList.toggle('off', !show);
    }
    if (plan.some(([rec]) => rec.measure)) wake(); // a new bar is measured in the next frame
    if (onScreen && !tick) tick = setInterval(() => { if (document.visibilityState !== 'hidden') wake(); }, BAR_TICK_MS);
    else if (!onScreen && tick) { clearInterval(tick); tick = null; }
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
    x.addEventListener('click', e => { if (!e.isTrusted) return; dismissedFor = items.map(i => i.id).join('\n'); forced = false; unhighlight(); if (S) S.stopPreview(); render(); });
    head.append(title, x);
    const rows = document.createElement('div'); rows.className = 'rows'; rows.setAttribute('aria-live', 'polite');
    bar.append(head, rows); root.append(style, bar);
    document.documentElement.append(host);
  }
  /** Hover or keyboard focus on a row outlines its element; the outline is renewed while the pointer stays and removed on leave. */
  function hoverOutline(row, i) {
    let beat = null;
    const on = () => { if (beat) return; highlight(i.id, { blob: !!i.viaBlob, owner: i.owner }); beat = setInterval(() => highlight(i.id, { blob: !!i.viaBlob, scroll: false, owner: i.owner }), 1000); };
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
      b.addEventListener('click', e => { if (!e.isTrusted) return; download(i); });
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
  function setItems(next) { items = Array.isArray(next) ? next : []; if (!items.length) { dismissedFor = ''; forced = false; } render(); syncBars(collect()); }

  const obs = new MutationObserver(schedule);
  obs.observe(document.documentElement, { subtree: true, childList: true, attributes: true, attributeFilter: ['src'] });
  const events = ['loadedmetadata', 'loadeddata', 'loadstart', 'play', 'playing', 'pause', 'ended', 'seeked', 'durationchange', 'resize', 'progress', 'timeupdate'];
  for (const e of events) document.addEventListener(e, ev => { if (ev.type !== 'timeupdate' && ev.type !== 'progress') { schedule(); wake(); } noteEvent(ev); }, { capture: true, passive: true });
  addEventListener('scroll', () => { schedule(); wake(); }, { capture: true, passive: true });
  addEventListener('resize', () => { schedule(); wake(); }, { passive: true });
  document.addEventListener('fullscreenchange', () => { syncBars(collect()); wake(); });
  addEventListener('pointermove', e => { ptr = { x: e.clientX, y: e.clientY }; wake(); }, { capture: true, passive: true });

  chrome.runtime.onMessage.addListener((m, sender, respond) => {
    if (!m) return false;
    if (m.type === 'neodm-media') setItems(m.items);
    else if (m.type === 'neodm-highlight' && typeof m.id === 'string') { if (m.on === true) highlight(m.id, { blob: m.blob === true, scroll: m.scroll !== false, owner: typeof m.owner === 'string' ? m.owner : '' }); else unhighlight(); }
    else if (m.type === 'neodm-context-target') { if (respond) respond(contextTarget()); }
    else if (m.type === 'neodm-show-ribbon') { forced = true; dismissedFor = ''; render(); if (respond) respond({ ok: true }); }
    return false;
  });
  addEventListener('pagehide', () => { unhighlight(); if (S) S.stopPreview(); });
  chrome.storage.onChanged.addListener((c, area) => { if (area !== 'local') return; if (c.ribbonEnabled) { enabled = c.ribbonEnabled.newValue !== false; render(); } if (c.videoButtonEnabled) buttonOn = c.videoButtonEnabled.newValue !== false; syncBars(collect()); });
  chrome.storage.local.get(['ribbonEnabled', 'videoButtonEnabled']).then(s => { enabled = s.ribbonEnabled !== false; buttonOn = s.videoButtonEnabled !== false; return chrome.runtime.sendMessage({ action: 'media-list' }); }).then(r => setItems(r?.items)).catch(() => {});
  scanDom();
  globalThis.__neodmRibbon = { refresh: scanDom, items: () => items.slice(), highlight, unhighlight, contextTarget, bars: () => Array.from(bars.values()).map(r => ({ key: r.key, id: r.main && r.main.id, shown: !!r.node && !r.node.classList.contains('off'), transform: r.node ? r.node.style.transform : '', dl: r.parts && r.parts.dl ? (b => ({ x: b.left, y: b.top, w: b.width, h: b.height }))(r.parts.dl.getBoundingClientRect()) : null })), layout, debug: () => ({ ptr, view: [innerWidth, innerHeight], buttonOn, recs: Array.from(bars.values()).map(r => { const b = r.el.getBoundingClientRect(); return { key: r.key, rect: [b.left, b.top, b.width, b.height], w: r.w, h: r.h, node: !!r.node, connected: r.el.isConnected }; }) }), dispose() { obs.disconnect(); clearTimeout(timer); clearInterval(tick); unhighlight(); host?.remove(); barHost?.remove(); delete globalThis.__neodmRibbon; } };
})();
