/* NeoDM media ribbon. Renders the tab's media list (kept by the service worker) in a closed shadow root, describes the page's <video>/<audio> elements to the worker
   (with a stable key per element and the element's playback events, so the worker can tell which element fetched which file), draws a small bar over each video that has a file,
   outlines an element on request and answers "which element is under the pointer" for the context menu. */
(() => {
  if (globalThis.__neodmRibbon) return;
  const t = (...a) => NeoI18n.t(...a);
  const S = globalThis.NeoSniff;
  const isTop = window === window.top;
  /* ---- private channel with hooks.js (page world): random event name and token, handed over in a data attribute of <html> before page scripts run ---- */
  const rnd = n => { const b = new Uint8Array(n); crypto.getRandomValues(b); return Array.from(b, x => (x % 36).toString(36)).join(''); };
  const channelName = 'neodm-' + rnd(14), channelToken = rnd(24);
  function publishChannel() {
    const root = document.documentElement;
    if (root) { root.setAttribute('data-neodm-ch', channelName + ':' + channelToken); return; }
    const mo = new MutationObserver(() => { if (document.documentElement) { mo.disconnect(); document.documentElement.setAttribute('data-neodm-ch', channelName + ':' + channelToken); } });
    mo.observe(document, { childList: true });
  }
  publishChannel();
  addEventListener(channelName, e => { try { onHookMessage(e); } catch { /* a malformed message is ignored */ } }, true);
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
  /* A feed re-uses one <video> for many videos: the key changes with the element's current source (a new "generation"), so files are tied to the video shown NOW and earlier videos keep their entries. */
  const keys = new WeakMap(), byKey = new Map(); let keySeq = 0;
  const FRAME_TAG = Math.random().toString(36).slice(2, 6);
  const srcNow = el => (el.currentSrc || el.getAttribute('src') || '');
  function elementInfo(el) {
    let r = keys.get(el); const cs = srcNow(el);
    if (!r) { r = { base: 'e' + FRAME_TAG + (++keySeq).toString(36), gen: 0, cs, t0: 0, seen: Date.now() }; keys.set(el, r); }
    else if (cs && r.cs !== cs) { r.gen++; r.cs = cs; r.t0 = 0; r.seen = Date.now(); }
    r.key = r.gen ? r.base + '-' + r.gen : r.base;
    if (typeof WeakRef === 'function' && !byKey.has(r.key)) { byKey.set(r.key, new WeakRef(el)); if (byKey.size > 600) byKey.delete(byKey.keys().next().value); }
    return r;
  }
  const keyOf = el => elementInfo(el).key;
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
      const urls = [m.currentSrc, m.getAttribute('src') && m.src, ...Array.from(m.querySelectorAll('source[src]')).map(s => s.src)];
      const url = urls.find(u => u && /^https?:/i.test(u) && !S.isManifestUrl(u));
      if (url) out.push({ el: m, ek: keyOf(m), key: canon(url), url, blob: false });
      else if (/^blob:/i.test(m.currentSrc || '')) out.push({ el: m, ek: keyOf(m), key: 'blob:', url: '', blob: true });
      else if (m.tagName === 'VIDEO') out.push({ el: m, ek: keyOf(m), key: '', url: '', blob: false, none: true }); // no source yet (MediaStream, a player still starting): the button may still show
    }
    return out;
  }
  function scanDom() {
    timer = null;
    const found = collect(); syncBars(found);
    for (const f of found) if (!seen.has(f.el)) { seen.add(f.el); try { if (!seenIo && typeof IntersectionObserver === 'function') seenIo = new IntersectionObserver(() => schedule(), { threshold: [0, 0.25, 0.5, 0.75, 1] }); seenIo && seenIo.observe(f.el); } catch {} } // a video that scrolls into or out of view is described again
    const playing = found.filter(f => isPlaying(f.el)).sort((a, b) => area(b.el.getBoundingClientRect()) - area(a.el.getBoundingClientRect()));
    const main = playing.length ? playing[0].el : found.length === 1 ? found[0].el : null;
    const sources = [], sig = []; let ensured = false;
    for (const f of found) {
      const m = f.el, poster = m.tagName === 'VIDEO' && m.poster && /^https?:/i.test(m.poster) ? m.poster : '';
      const vis = Math.round(visibleFraction(m) * 4) / 4, thumb = grab(m);
      const s = { key: f.ek, drm: isDrm(m), surface: hiddenStyle(m) || area(m.getBoundingClientRect()) < 4, url: f.blob ? 'blob:' : f.url, tag: m.tagName.toLowerCase(), title: titleGuess(m, m === main), dur: Number.isFinite(m.duration) ? Math.round(m.duration * 10) / 10 : 0, w: m.videoWidth || 0, h: m.videoHeight || 0, playing: isPlaying(m), vis, ...(poster ? { poster } : {}), ...(thumb ? { thumb } : {}) };
      sources.push(s); sig.push({ ...s, thumb: !!thumb });
      if (!ensured) { ensured = true; ensureItems(); }
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

  /* ---- what the page's own scripts do with media (reported by hooks.js through the private channel) ---- */
  const H = { ms: new Map(), byBlob: new Map(), sbs: new Map(), manifests: new Map(), segIndex: new Map(), netSize: new Map(), ranged: new Set(), lastSeg: null, ok: null, drm: new WeakSet(), srcObj: new WeakMap(), srcAssigned: new WeakMap(), timer: null, last: new Map() };
  const capMap = (map, max) => { while (map.size > max) map.delete(map.keys().next().value); };
  const HOOK_RE = { ms: /^m\d{1,9}$/, sb: /^s\d{1,9}$/ };
  const httpUrl = u => typeof u === 'string' && u.length <= 2048 && /^https?:\/\//i.test(u);
  /** audio-only / video-only / file from a SourceBuffer MIME type with codecs. */
  function trackOfMime(mime) {
    const m = String(mime || '').toLowerCase(), type = m.split(';')[0].trim(), codecs = ((/codecs\s*=\s*([^;]*)/.exec(m) || [])[1] || '').replace(new RegExp(String.fromCharCode(34), 'g'), '');
    const list = codecs.split(',').map(x => x.trim()).filter(Boolean);
    const audio = list.filter(c => /^(mp4a|opus|vorbis|flac|ac-3|ec-3|alac)/.test(c)).length, video = list.filter(c => /^(avc|hvc|hev|vp0?8|vp0?9|av01|dvh|dvhe)/.test(c)).length;
    if (list.length) return audio && !video ? 'audio-only' : video && !audio ? 'video-only' : 'file';
    return type.startsWith('audio/') ? 'audio-only' : 'file';
  }
  function addManifest(url, text) {
    const parsed = S.parseManifest(text, url); if (!parsed.master && !parsed.segs.length && !parsed.variants.length) return;
    const key = canon(url);
    H.manifests.set(key, { ...parsed, url, t: Date.now() }); capMap(H.manifests, 20);
    for (const seg of parsed.segs) { H.segIndex.set(canon(seg), key); }
    for (const child of parsed.children) H.segIndex.set(canon(child), key); // a variant playlist belongs to its master
    capMap(H.segIndex, 4000);
  }
  function onHookMessage(e) {
    if (typeof e.detail !== 'string' || e.detail.length > 70000) return;
    let m; try { m = JSON.parse(e.detail); } catch { return; }
    if (!m || typeof m !== 'object' || m.t !== channelToken || typeof m.k !== 'string') return;
    const target = e.target && isMedia(e.target) ? e.target : null;
    switch (m.k) {
      case 'hooks': H.ok = m.ok === true; break;
      case 'ms': if (HOOK_RE.ms.test(m.ms) && typeof m.u === 'string' && /^blob:/i.test(m.u) && m.u.length <= 300) { const cur = H.ms.get(m.ms) || { blob: '', sbs: [] }; cur.blob = m.u; H.ms.set(m.ms, cur); H.byBlob.set(m.u, m.ms); capMap(H.ms, 40); capMap(H.byBlob, 40); } break;
      case 'sb': if (HOOK_RE.sb.test(m.sb) && HOOK_RE.ms.test(m.ms)) { H.sbs.set(m.sb, { ms: m.ms, mime: String(m.mime || '').slice(0, 200), track: trackOfMime(m.mime), bytes: 0, urls: new Map() }); capMap(H.sbs, 60); const ms = H.ms.get(m.ms) || { blob: '', sbs: [] }; if (!ms.sbs.includes(m.sb)) ms.sbs.push(m.sb); H.ms.set(m.ms, ms); } break;
      case 'app': {
        const sb = H.sbs.get(m.sb); if (!sb) break;
        sb.bytes = Math.max(sb.bytes, Number(m.b) || 0);
        const us = Array.isArray(m.us) ? m.us.slice(0, 24) : [], gs = Array.isArray(m.gs) ? m.gs : [];
        us.forEach((u, i) => { if (!httpUrl(u)) return; const c = canon(u), seg = H.segIndex.get(c); if (seg) H.lastSeg = { manifest: seg, t: Date.now() }; if (sb.urls.size < 24 || sb.urls.has(c)) sb.urls.set(c, { raw: u, g: gs[i] === 1 }); });
        break;
      }
      case 'net': {
        if (!httpUrl(m.u)) break;
        const c = canon(m.u);
        if (typeof m.mf === 'string' && m.mf.length) addManifest(m.u, m.mf);
        else if (m.n > 0 && !S.isManifestUrl(m.u)) { if (m.s === 206) { H.ranged.add(c); if (H.ranged.size > 400) H.ranged.delete(H.ranged.values().next().value); } H.netSize.set(c, Math.max(H.netSize.get(c) || 0, Math.min(Number(m.n) || 0, 1e13))); capMap(H.netSize, 300); const seg = H.segIndex.get(c); if (seg) H.lastSeg = { manifest: seg, t: Date.now() }; }
        break;
      }
      case 'src': if (target && typeof m.a === 'string') H.srcAssigned.set(target, m.a); break;
      case 'srcobj': if (target) H.srcObj.set(target, String(m.t || '').slice(0, 30)); break;
      case 'drm': if (target) H.drm.add(target); break;
      default: return;
    }
    scheduleHooks();
  }
  /** Manifests the frame saw that are not a child of another one: masters first. */
  function topManifests() {
    const all = Array.from(H.manifests.values()), children = new Set(all.flatMap(x => x.children.map(canon)));
    const roots = all.filter(x => !children.has(canon(x.url)));
    const masters = roots.filter(x => x.master); return (masters.length ? masters : roots.slice(-1)).slice(-3);
  }
  const mseElements = () => Array.from(document.querySelectorAll('video')).filter(v => /^blob:/i.test(v.currentSrc || '') && H.byBlob.has(v.currentSrc));
  /** Candidate sources of one element from the hook data: files (also those fetched in ranges through MediaSource), pieces, playlists. */
  function hookSources(el) {
    const out = [], seenKeys = new Set(), push = s => { const k = s.role + '|' + canon(s.url) + '|' + s.track; if (!seenKeys.has(k)) { seenKeys.add(k); out.push(s); } };
    const cs = el.currentSrc || '', msId = /^blob:/i.test(cs) ? H.byBlob.get(cs) : '';
    const wanted = new Set(); // playlists that this element's data came from
    if (msId) {
      const ms = H.ms.get(msId);
      for (const sbId of (ms ? ms.sbs : [])) {
        const sb = H.sbs.get(sbId); if (!sb || !sb.bytes) continue;
        const tagged = Array.from(sb.urls.entries()).filter(([, v]) => !v.g), guessed = Array.from(sb.urls.entries()).filter(([, v]) => v.g), use = tagged.length ? tagged : guessed.length === 1 || guessed.every(([c]) => H.segIndex.has(c)) ? guessed : []; // guesses (bytes the page re-made) only count when they are unambiguous or belong to a known playlist
        const fromList = use.filter(([c]) => H.segIndex.has(c));
        for (const [c] of fromList) wanted.add(H.segIndex.get(c));
        const kind = sb.track === 'audio-only' ? 'audio' : 'video', mime = (sb.mime.split(';')[0] || '').trim();
        const looseAll = use.filter(([c]) => !H.segIndex.has(c)), ranges = looseAll.filter(([c]) => H.ranged.has(c)), loose = ranges.length && ranges.length === looseAll.length ? [] : looseAll; // files read in ranges (206 with a total) are whole files, however many qualities the page switched between
        for (const [c, v] of (loose.length ? [] : looseAll)) push({ role: 'file', url: v.raw, kind, track: sb.track, size: H.netSize.get(c) || 0, mime, quality: sb.track === 'audio-only' ? '' : (el.videoHeight ? el.videoHeight + 'p' : '') });
        if (loose.length === 1) { const [c, v] = loose[0]; push({ role: 'file', url: v.raw, kind, track: sb.track, size: H.netSize.get(c) || 0, mime, quality: sb.track === 'audio-only' ? '' : (el.videoHeight ? el.videoHeight + 'p' : '') }); }
        else if (loose.length > 1 && !fromList.length) push({ role: 'pieces', url: loose[0][1].raw, kind, track: sb.track, pieces: loose.length });
      }
      if (!wanted.size && H.lastSeg && Date.now() - H.lastSeg.t < 8000) wanted.add(H.lastSeg.manifest);
      if (!wanted.size) { const mses = mseElements(); if (mses.length === 1 || (mses.length > 1 && isPlaying(el) && mses.filter(isPlaying).length === 1)) for (const x of topManifests()) wanted.add(canon(x.url)); }
    }
    // a playlist address given straight to the element
    const direct = [cs, el.getAttribute('src') && el.src, H.srcAssigned.get(el)].find(u => httpUrl(u) && S.isManifestUrl(u));
    if (direct) wanted.add(canon(direct));
    for (const key of wanted) {
      let man = H.manifests.get(key);
      if (man) { const top = Array.from(H.manifests.values()).find(x => x.children.map(canon).includes(key)); if (top) man = top; } // a variant playlist is shown as its master
      if (man) { const h = S.bestHeight(man.variants); push({ role: man.type, url: man.url, kind: 'video', track: 'file', quality: h ? h + 'p' : '', dur: man.dur, live: man.live, drm: man.drm, variants: man.variants.filter(v => !v.audio).length }); }
    }
    if (direct && !out.some(s => s.role === 'hls' || s.role === 'dash')) push({ role: S.streamKindOf(direct, ''), url: direct, kind: 'video', track: 'file' });
    return out.slice(0, 12);
  }
  const isDrm = el => H.drm.has(el) || !!el.mediaKeys || out_hasDrmManifest(el);
  function out_hasDrmManifest(el) { try { return hookSources(el).some(s => s.drm); } catch { return false; } }
  function scheduleHooks() { if (H.timer === null) H.timer = setTimeout(reportHooks, 250); }
  /** Tells the worker what the hooks learned, per element (only elements whose list changed). */
  function reportHooks() {
    H.timer = null;
    const entries = [];
    for (const el of Array.from(document.querySelectorAll('video,audio')).slice(0, MAX_DOM)) {
      const key = keyOf(el), list = hookSources(el), drm = H.drm.has(el) || list.some(s => s.drm), sig = JSON.stringify([list, drm]);
      if (H.last.get(key) === sig || (!list.length && !drm && !H.last.has(key))) continue;
      H.last.set(key, sig); entries.push({ key, list, ...(drm ? { drm: true } : {}) });
    }
    capMap(H.last, 400);
    if (entries.length) try { chrome.runtime.sendMessage({ action: 'media-hooks', entries }).catch(() => {}); } catch { /* extension reloaded */ }
  }

  /* ---- the download button on each video ----
     One closed shadow root holds every button of this frame (position:fixed, moved with transform, maximum z-index, only the pill takes pointer events). Layout runs in a requestAnimationFrame
     requested by scroll, resize, pointer, playback, ResizeObserver and IntersectionObserver events: it first reads every box, then writes every position. A 500 ms timer runs only while a button is on screen.
     The button shows on every video that plays (or is hovered / focused) and is large enough, whether or not a file is known yet; its state comes from S.resolveElement. */
  const BAR_TICK_MS = 500, NOTICE_MS = 8000;
  const bars = new Map(), dismissed = new WeakSet(), observed = new WeakSet();
  let barHost = null, barRoot = null, raf = 0, tick = null, ptr = null, ro = null, io = null, corner = '', hiddenHosts = [], checkTimer = null;
  const defaultCorner = () => (NeoI18n.isRtl() ? 'right' : 'left');
  const cornerNow = () => (corner === 'left' || corner === 'right' ? corner : defaultCorner());
  const topHost = () => { try { const o = location.ancestorOrigins; return o && o.length ? new URL(o[o.length - 1]).hostname : location.hostname; } catch { return location.hostname; } };
  const siteHidden = () => !!S && S.isHiddenHost(hiddenHosts, S.siteOf(location.hostname), S.siteOf(topHost()));
  const barsAllowed = () => buttonOn && !!S && !siteHidden() && !isMedia(document.fullscreenElement); // fullscreen on a <video> itself leaves no room for a button; a fullscreen container hosts it
  function ensureBarHost() {
    if (barHost && barHost.isConnected) return;
    if (!barHost) {
      barHost = document.createElement('div'); barHost.setAttribute('data-neodm-bars', '');
      barHost.style.cssText = 'all:initial!important;position:fixed!important;left:0!important;top:0!important;width:0!important;height:0!important;overflow:visible!important;z-index:2147483646!important;pointer-events:none!important;';
      barRoot = barHost.attachShadow({ mode: 'closed' });
      const style = document.createElement('style');
      style.textContent = `.b{position:fixed;left:0;top:0;font:600 12px/1 system-ui,Segoe UI,sans-serif;color:#f4f7fa;opacity:.94;pointer-events:none;will-change:transform;contain:layout style}
.pill{display:inline-flex;align-items:center;gap:6px;background:rgba(23,33,46,.92);border:1px solid #2f4258;border-radius:8px;padding:3px;padding-inline-start:8px;pointer-events:auto;white-space:nowrap;max-width:340px}
[hidden]{display:none!important}.b.off{visibility:hidden}.b.off .pill,.b.off .panel{pointer-events:none}.mk{color:#39b995;letter-spacing:.3px}.q{font-weight:500;opacity:.9;overflow:hidden;text-overflow:ellipsis;max-width:170px}.q.ok{color:#7be0b9}.q.err{color:#ff9d9d}
button{all:unset;cursor:pointer;box-sizing:border-box;font:inherit}button:focus-visible{outline:2px solid #39b995;outline-offset:1px}
.d{background:#2da57d;color:#fff;padding:5px 9px;border-radius:6px}.d:hover,.d:focus-visible,.o:hover,.o:focus-visible,.a:hover,.a:focus-visible{background:#34bd90}.d[disabled]{opacity:.6;cursor:default}
.b.checking .d,.b.unsupported .d{background:#4a5a6c;color:#e4eaf1}.b.unsupported .d:hover,.b.unsupported .d:focus-visible{background:#566a80}.b.checking .d{cursor:default}
.spin{display:inline-block;width:9px;height:9px;margin-inline-end:6px;border:2px solid #9fb0c2;border-top-color:transparent;border-radius:50%;animation:s .8s linear infinite;vertical-align:-1px}@keyframes s{to{transform:rotate(360deg)}}@media (prefers-reduced-motion:reduce){.spin{animation:none}}
.x{padding:3px 6px;font-size:15px;line-height:1;color:#c7d2de;border-radius:6px}.x:hover,.x:focus-visible{background:#263649;color:#fff}
.panel{position:absolute;top:100%;margin-top:4px;inset-inline-start:0;min-width:220px;max-width:320px;background:#17212e;border:1px solid #2f4258;border-radius:8px;padding:4px;pointer-events:auto;font-weight:500;line-height:1.35;white-space:normal}
.panel p{margin:4px 6px;font-size:12px;color:#c7d2de}.o,.a{display:flex;flex-direction:column;gap:2px;width:100%;background:#2c3b4d;color:#fff;padding:6px 9px;border-radius:6px;margin-top:3px}.o:first-child{margin-top:0}.o small{font-weight:400;opacity:.8;font-size:11px}.o[disabled]{opacity:.55;cursor:default;background:#2c3b4d}
.a{display:inline-block;width:auto;margin-inline-end:4px}.compact .mk,.compact .q{display:none}`;
      barRoot.append(style);
    }
    document.documentElement.append(barHost);
  }
  /** What the button says next to "Download": "720p 12.3 MB", plus "Video only" when this file has no sound. */
  function barText(item) {
    if (!item) return '';
    const q = item.quality || (item.h ? item.h + 'p' : ''), size = S.fmtSize(item.size);
    return [[q, size].filter(Boolean).join(' '), item.track === 'video-only' ? t('kind_video_only') : ''].filter(Boolean).join(' \u00b7 ');
  }
  const optionLabel = o => {
    const m = o.main, q = m.quality || (m.h ? m.h + 'p' : ''), size = S.fmtSize((m.size || 0) + (o.audio ? o.audio.size || 0 : 0));
    const kind = o.type === 'pair' ? t('vb_pair') : m.track === 'audio-only' ? t('kind_audio_only') : m.track === 'video-only' ? t('kind_video_only') : t('kind_video');
    const mime = (S.extensionFor(m) || '').replace('.', '').toUpperCase();
    return { title: [kind, q, mime].filter(Boolean).join(' '), sub: [size, o.type === 'pair' ? t('vb_pair_note') : '', o.guess ? t('vb_guess') : ''].filter(Boolean).join(' \u00b7 ') };
  };
  const streamLabel = i => i.stream === 'dash' ? t('kind_stream_dash') : i.stream === 'hls' ? t('kind_stream_hls') : t('kind_stream_pieces');
  const reasonText = r => t(({ drm: 'vb_reason_drm', stream: 'vb_reason_stream', youtube: 'vb_reason_youtube', none: 'vb_reason_none' })[r] || 'vb_reason_none');
  function sendOptionRequests(rec, opt) {
    return opt.type === 'pair' ? [opt.main, opt.audio] : [opt.main];
  }
  async function barDownload(rec, opt) {
    if (rec.busy || !opt) return;
    rec.busy = true; rec.status = null; rec.open = false; fillBar(rec);
    try {
      let last = null;
      for (const item of sendOptionRequests(rec, opt)) {
        last = await chrome.runtime.sendMessage({ action: 'media-download', id: item.id });
        if (!(last && last.ok === true && typeof last.id === 'string' && last.id.trim())) break;
      }
      const ok = !!(last && last.ok === true && typeof last.id === 'string' && last.id.trim());
      rec.status = ok ? { ok: true, text: opt.type === 'pair' ? t('vb_sent_pair') : t('ribbon_accepted') } : { ok: false, text: (last && last.error) || t('ribbon_not_accepted') };
    } catch { rec.status = { ok: false, text: t('ribbon_bridge_unavailable') }; }
    rec.busy = false; fillBar(rec);
    setTimeout(() => { rec.status = null; fillBar(rec); }, 6000);
  }
  function persist(update) { try { chrome.storage.local.set(update).catch(() => {}); } catch { /* extension reloaded */ } }
  function hideSite() {
    const site = S.siteOf(topHost()); if (!site) return;
    chrome.storage.local.get('videoButtonHiddenHosts').then(s => { const list = S.cleanHiddenHosts(s.videoButtonHiddenHosts); if (!list.includes(site)) list.push(site); persist({ videoButtonHiddenHosts: list.slice(-100) }); }).catch(() => {});
    hiddenHosts = S.cleanHiddenHosts([...hiddenHosts, site]); syncBars(collect());
  }
  function toggleCorner() { corner = cornerNow() === 'left' ? 'right' : 'left'; persist({ videoButtonCorner: corner }); for (const r of bars.values()) r.measure = true; wake(); }
  /** Rebuilds the contents of a button from its state (never touches the page; only this closed shadow root). */
  function fillBar(rec) {
    const n = rec.parts; if (!n) return;
    const res = rec.res || { state: 'checking', options: [], streams: [] }, st = rec.status;
    const sig = JSON.stringify([rec.mode, res.state, res.reason, res.options.map(o => [o.main.id, o.audio && o.audio.id, o.main.size, o.main.quality]), res.streams.map(i => i.id), !!rec.busy, st && st.text, !!rec.open, rec.compact, cornerNow()]);
    if (rec.sig === sig) return; rec.sig = sig;
    const node = rec.node, pill = n.pill, panel = n.panel;
    node.className = 'b ' + res.state + (rec.compact ? ' compact' : '') + (node.classList.contains('off') ? ' off' : ''); pill.textContent = ''; panel.textContent = ''; panel.hidden = true;
    if (rec.mode === 'notice') {
      const msg = document.createElement('span'); msg.className = 'q'; msg.style.maxWidth = 'none'; msg.textContent = t('vb_hidden_notice');
      const site = document.createElement('button'); site.type = 'button'; site.className = 'a'; site.textContent = t('vb_hide_site');
      const move = document.createElement('button'); move.type = 'button'; move.className = 'a'; move.textContent = t('vb_corner_move');
      const undo = document.createElement('button'); undo.type = 'button'; undo.className = 'a'; undo.textContent = t('vb_undo');
      site.addEventListener('click', e => { if (!e.isTrusted) return; hideSite(); });
      move.addEventListener('click', e => { if (!e.isTrusted) return; dismissed.delete(rec.el); rec.mode = ''; rec.sig = ''; clearTimeout(rec.noticeTimer); toggleCorner(); syncBars(collect()); });
      undo.addEventListener('click', e => { if (!e.isTrusted) return; dismissed.delete(rec.el); rec.mode = ''; rec.sig = ''; clearTimeout(rec.noticeTimer); syncBars(collect()); fillBar(rec); });
      pill.append(msg); panel.hidden = false; panel.append(site, move, undo); rec.measure = true; return;
    }
    const mk = document.createElement('span'); mk.className = 'mk'; mk.textContent = 'NeoDM';
    const dl = document.createElement('button'); dl.type = 'button'; dl.className = 'd'; dl.setAttribute('data-act', 'main');
    const q = document.createElement('span'); q.className = 'q';
    const x = document.createElement('button'); x.type = 'button'; x.className = 'x'; x.textContent = '\u00d7'; x.title = t('bar_hide'); x.setAttribute('aria-label', t('bar_hide'));
    x.addEventListener('click', e => { if (!e.isTrusted) return; dismissed.add(rec.el); rec.mode = 'notice'; rec.sig = ''; rec.open = false; fillBar(rec); wake(); clearTimeout(rec.noticeTimer); rec.noticeTimer = setTimeout(() => { removeBar(rec.key); }, NOTICE_MS); });
    for (const type of ['click', 'mousedown', 'mouseup', 'pointerdown', 'pointerup', 'dblclick']) node.addEventListener(type, e => e.stopPropagation()); // the page must not see clicks on the button (a link around the video would open)
    if (res.state === 'checking') { dl.disabled = false; dl.setAttribute('aria-disabled', 'true'); const sp = document.createElement('span'); sp.className = 'spin'; dl.append(sp, document.createTextNode(t('vb_checking'))); }
    else if (res.state === 'unsupported') { dl.textContent = t('vb_cant_save'); dl.setAttribute('aria-expanded', String(!!rec.open)); }
    else if (res.state === 'choose') { dl.textContent = rec.busy ? t('ribbon_waiting') : t('vb_choose', String(res.options.length)); dl.setAttribute('aria-expanded', String(!!rec.open)); dl.setAttribute('aria-haspopup', 'true'); }
    else dl.textContent = rec.busy ? t('ribbon_waiting') : st && st.ok ? t('ribbon_sent') : t('bar_download');
    dl.disabled = !!rec.busy;
    const only = res.options[0];
    q.textContent = st && !st.ok ? st.text : res.state === 'ready' && only ? barText(only.main) + (only.type === 'pair' ? ' \u00b7 ' + t('vb_pair_short') : '') : ''; q.title = st ? st.text : only ? only.main.url : ''; q.className = 'q' + (st ? (st.ok ? ' ok' : ' err') : '');
    dl.addEventListener('click', e => { if (!e.isTrusted) return;
      if (e.detail > 0 && res.state === 'ready') dl.blur();
      if (res.state === 'ready') barDownload(rec, only);
      else if (res.state === 'choose' || res.state === 'unsupported') { rec.open = !rec.open; rec.sig = ''; fillBar(rec); if (rec.open) { const first = panel.querySelector('button:not([disabled])'); if (first && e.detail === 0) first.focus(); } rec.measure = true; wake(); }
    });
    pill.append(mk, dl, q, x);
    if (rec.open && res.state === 'choose') {
      panel.hidden = false;
      for (const o of res.options) { const l = optionLabel(o), b = document.createElement('button'); b.type = 'button'; b.className = 'o'; b.setAttribute('role', 'menuitem'); const a = document.createElement('span'); a.textContent = l.title; const s = document.createElement('small'); s.textContent = l.sub; b.append(a, s); b.addEventListener('click', e => { if (!e.isTrusted) return; barDownload(rec, o); }); panel.append(b); }
      for (const i of res.streams) { const b = document.createElement('button'); b.type = 'button'; b.className = 'o'; b.disabled = true; b.setAttribute('aria-disabled', 'true'); const a = document.createElement('span'); a.textContent = [streamLabel(i), i.quality, S.fmtDuration(i.dur)].filter(Boolean).join(' '); const s = document.createElement('small'); s.textContent = t('vb_stream_disabled'); b.append(a, s); panel.append(b); }
    } else if (rec.open && res.state === 'unsupported') {
      panel.hidden = false; const p = document.createElement('p'); p.textContent = reasonText(res.reason); panel.append(p);
      for (const i of res.streams) { const p2 = document.createElement('p'); p2.textContent = [streamLabel(i), i.quality, S.fmtDuration(i.dur)].filter(Boolean).join(' '); panel.append(p2); }
    }
    rec.measure = true;
  }
  function buildBar(rec) {
    ensureBarHost();
    const node = document.createElement('div'); node.className = 'b off'; node.dir = NeoI18n.isRtl() ? 'rtl' : 'ltr'; node.setAttribute('role', 'group'); node.setAttribute('aria-label', t('ribbon_label'));
    const pill = document.createElement('div'); pill.className = 'pill'; const panel = document.createElement('div'); panel.className = 'panel'; panel.hidden = true;
    node.append(pill, panel); barRoot.append(node); rec.node = node; rec.parts = { pill, panel };
    node.addEventListener('keydown', e => { if (!e.isTrusted) return; if (e.key === 'Escape' && rec.open) { rec.open = false; rec.sig = ''; fillBar(rec); const b = pill.querySelector('.d'); b && b.focus(); e.stopPropagation(); } });
    fillBar(rec);
  }
  function removeBar(key) {
    const rec = bars.get(key); if (!rec) return;
    clearTimeout(rec.noticeTimer); rec.node?.remove(); bars.delete(key);
    try { ro && ro.unobserve(rec.el); io && io.unobserve(rec.el); } catch {}
    observed.delete(rec.el);
  }
  const hiddenStyle = el => { try { const cs = getComputedStyle(el); return cs.display === 'none' || cs.visibility === 'hidden' || (cs.opacity !== '' && Number(cs.opacity) === 0); } catch { return false; } };
  /** Box the button is placed in: the video's own box, else (hidden player surface / zero size) the largest visible ancestor, else the whole window. */
  function anchorBox(el) {
    const r = el.getBoundingClientRect();
    if (r.width >= S.BTN_MIN_W && r.height >= S.BTN_MIN_H && !hiddenStyle(el)) return { rect: r, corner: cornerNow() };
    if (!isPlaying(el) && !hovered(r)) return { rect: r, corner: cornerNow() };
    for (let n = el.parentElement, d = 0; n && n !== document.documentElement && d < 8; n = n.parentElement, d++) {
      const b = n.getBoundingClientRect();
      if (b.width >= S.BTN_MIN_W && b.height >= S.BTN_MIN_H && !hiddenStyle(n) && b.width < innerWidth * 1.01 + 1 && b.height <= innerHeight * 1.5) return { rect: b, corner: cornerNow() };
    }
    if (isPlaying(el) && (r.width < 2 || hiddenStyle(el))) return { rect: { left: 0, top: 0, right: innerWidth, bottom: innerHeight, width: innerWidth, height: innerHeight }, corner: 'right' };
    return { rect: r, corner: cornerNow() };
  }
  /** One record per video the button may show on; observers stay on exactly those. */
  function syncBars(found) {
    const keep = new Set();
    if (barsAllowed()) {
      const media = items.filter(i => !i.info && (i.kind === 'video' || i.kind === 'audio'));
      for (const f of found) {
        if (f.el.tagName !== 'VIDEO' || dismissed.has(f.el) && !(bars.get(f.ek) && bars.get(f.ek).mode === 'notice')) continue;
        keep.add(f.ek);
        const rec = bars.get(f.ek) || { key: f.ek, el: f.el, node: null }; bars.set(f.ek, rec); rec.el = f.el;
        const play = isPlaying(f.el), info = elementInfo(f.el);
        if (play && !info.t0) info.t0 = Date.now();
        const res = S.resolveElement({ key: f.ek, drm: isDrm(f.el), url: f.url, blob: f.blob, yt: S.isYouTubeHost(location.href) || S.isYouTubeHost(topHost()), age: Date.now() - (info.t0 || info.seen) }, media);
        const sigNow = JSON.stringify([res.state, res.reason, res.options.map(o => [o.main.id, o.audio && o.audio.id]), res.streams.map(i => i.id)]);
        if (rec.resSig !== sigNow) { rec.resSig = sigNow; rec.sig = ''; }
        rec.res = res; if (rec.node) fillBar(rec);
        if (res.state === 'checking' && !checkTimer) checkTimer = setTimeout(() => { checkTimer = null; syncBars(collect()); }, Math.max(60, S.CHECK_MS - (Date.now() - (info.t0 || info.seen)) + 50));
        if (!observed.has(f.el)) {
          observed.add(f.el);
          if (!ro && typeof ResizeObserver === 'function') ro = new ResizeObserver(() => wake());
          if (!io && typeof IntersectionObserver === 'function') io = new IntersectionObserver(() => wake(), { threshold: [0, 0.25, 0.5, 1] });
          try { ro && ro.observe(f.el); io && io.observe(f.el); } catch {}
        }
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
    const view = { w: innerWidth, h: innerHeight }, plan = [], fs = document.fullscreenElement;
    const home = fs && !isMedia(fs) ? fs : document.documentElement; // in fullscreen only the fullscreen element and its children are painted
    if (barHost && home && barHost.parentNode !== home) { try { home.append(barHost); } catch {} }
    // read phase: every box and every button size, no writes
    for (const rec of bars.values()) {
      if (rec.node && rec.measure) { rec.w = rec.node.firstChild.offsetWidth; rec.h = rec.node.firstChild.offsetHeight; rec.measure = false; }
      const vr = rec.el.isConnected ? anchorBox(rec.el) : null;
      const compact = !!vr && vr.rect.width < 320; if (compact !== !!rec.compact) { rec.compact = compact; rec.sig = ''; if (rec.node) rec.needFill = true; }
      const place = vr && (!fs || fs.contains(rec.el)) && S.buttonPlacement(vr.rect, view, { w: rec.w || (compact ? 120 : 210), h: rec.h || 28 }, vr.corner);
      const live = isPlaying(rec.el) || rec.mode === 'notice' || rec.open || document.activeElement === rec.el || (rec.node && rec.node.matches(':focus-within'));
      plan.push([rec, place, !!place && (hovered(vr.rect) || live) && document.visibilityState !== 'hidden']);
    }
    // write phase
    let onScreen = false;
    for (const [rec, place, show] of plan) {
      if (place && show) onScreen = true;
      if (show && !rec.node) buildBar(rec);
      if (!rec.node) continue;
      if (rec.needFill) { rec.needFill = false; fillBar(rec); }
      if (show) rec.node.style.transform = `translate(${place.x}px,${place.y}px)`;
      rec.node.classList.toggle('off', !show);
    }
    if (plan.some(([rec]) => rec.node && (rec.measure || rec.needFill))) wake(); // a new button is measured in the next frame
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
    const visible = isTop && (enabled || forced) && items.length && dismissedFor !== items.map(i => i.id).join('\n');
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
  let itemsRequested = false;
  /** A sub-frame asks for the tab's list only when it has a video (a page with many ad frames must not flood the worker). */
  function ensureItems() { if (itemsRequested) return; itemsRequested = true; try { chrome.runtime.sendMessage({ action: 'media-list' }).then(r => setItems(r?.items)).catch(() => { itemsRequested = false; }); } catch { itemsRequested = false; } }

  const obs = new MutationObserver(schedule);
  const events = ['loadedmetadata', 'loadeddata', 'loadstart', 'play', 'playing', 'pause', 'ended', 'seeked', 'durationchange', 'resize', 'progress', 'timeupdate'];
  function start() {
    obs.observe(document.documentElement, { subtree: true, childList: true, attributes: true, attributeFilter: ['src'] });
    for (const e of events) document.addEventListener(e, ev => { if (ev.type !== 'timeupdate' && ev.type !== 'progress') { schedule(); wake(); } noteEvent(ev); }, { capture: true, passive: true });
    document.addEventListener('encrypted', ev => { if (isMedia(ev.target)) { H.drm.add(ev.target); schedule(); } }, { capture: true, passive: true });
    addEventListener('scroll', () => { schedule(); wake(); }, { capture: true, passive: true });
    addEventListener('resize', () => { schedule(); wake(); }, { passive: true });
    document.addEventListener('fullscreenchange', () => { syncBars(collect()); wake(); });
    addEventListener('pointermove', e => { ptr = { x: e.clientX, y: e.clientY }; wake(); }, { capture: true, passive: true });
    chrome.storage.local.get(['ribbonEnabled', 'videoButtonEnabled', 'videoButtonCorner', 'videoButtonHiddenHosts']).then(s => { enabled = s.ribbonEnabled !== false; buttonOn = s.videoButtonEnabled !== false; corner = s.videoButtonCorner === 'left' || s.videoButtonCorner === 'right' ? s.videoButtonCorner : ''; hiddenHosts = S ? S.cleanHiddenHosts(s.videoButtonHiddenHosts) : []; if (isTop) ensureItems(); else if (document.querySelector('video,audio')) ensureItems(); syncBars(collect()); }).catch(() => {});
    scanDom();
  }
  if (document.documentElement) start(); else { const wait = new MutationObserver(() => { if (document.documentElement) { wait.disconnect(); start(); } }); wait.observe(document, { childList: true }); }

  chrome.runtime.onMessage.addListener((m, sender, respond) => {
    if (!m) return false;
    if (m.type === 'neodm-media') setItems(m.items);
    else if (m.type === 'neodm-highlight' && typeof m.id === 'string') { if (m.on === true) highlight(m.id, { blob: m.blob === true, scroll: m.scroll !== false, owner: typeof m.owner === 'string' ? m.owner : '' }); else unhighlight(); }
    else if (m.type === 'neodm-context-target') { if (respond) respond(contextTarget()); }
    else if (m.type === 'neodm-show-ribbon') { if (isTop) { forced = true; dismissedFor = ''; render(); } if (respond) respond({ ok: true }); }
    return false;
  });
  addEventListener('pagehide', () => { unhighlight(); if (S) S.stopPreview(); });
  chrome.storage.onChanged.addListener((c, area) => {
    if (area !== 'local') return;
    if (c.ribbonEnabled) { enabled = c.ribbonEnabled.newValue !== false; render(); }
    if (c.videoButtonEnabled) buttonOn = c.videoButtonEnabled.newValue !== false;
    if (c.videoButtonCorner) { const v = c.videoButtonCorner.newValue; corner = v === 'left' || v === 'right' ? v : ''; for (const r of bars.values()) r.measure = true; }
    if (c.videoButtonHiddenHosts) hiddenHosts = S ? S.cleanHiddenHosts(c.videoButtonHiddenHosts.newValue) : [];
    if (c.videoButtonEnabled || c.videoButtonHiddenHosts || c.videoButtonCorner) syncBars(collect());
  });
  globalThis.__neodmRibbon = { refresh: scanDom, items: () => items.slice(), highlight, unhighlight, contextTarget, hookSources, hooks: () => ({ ok: H.ok, ms: H.ms.size, sbs: H.sbs.size, manifests: H.manifests.size, detail: Array.from(H.sbs.values()).map(sb => [sb.track, sb.bytes, Array.from(sb.urls.entries()).map(([k, v]) => [k.split('/').pop(), v.g])]), ranged: Array.from(H.ranged).map(k => k.split('/').pop()) }), channel: () => ({ name: channelName, token: channelToken }),
    bars: () => Array.from(bars.values()).map(r => { const boxOf = n => { if (!n) return null; const b = n.getBoundingClientRect(); return { x: b.left, y: b.top, w: b.width, h: b.height, text: n.textContent, disabled: !!n.disabled }; }; const d = r.parts && r.parts.pill ? r.parts.pill.querySelector('.d') : null, b = d ? d.getBoundingClientRect() : null, pill = r.parts && r.parts.pill ? r.parts.pill.getBoundingClientRect() : null; return { key: r.key, id: r.res && r.res.options[0] && r.res.options[0].main.id, state: r.res && r.res.state, reason: r.res && r.res.reason, mode: r.mode || '', corner: cornerNow(), text: d ? d.textContent : '', shown: !!r.node && !r.node.classList.contains('off'), transform: r.node ? r.node.style.transform : '', dl: b ? { x: b.left, y: b.top, w: b.width, h: b.height } : null, pill: pill ? { x: pill.left, y: pill.top, w: pill.width, h: pill.height } : null, x: boxOf(r.parts && r.parts.pill && r.parts.pill.querySelector('.x')), panel: r.parts ? Array.from(r.parts.panel.querySelectorAll('button')).map(boxOf) : [], reasonText: r.parts && !r.parts.panel.hidden ? r.parts.panel.textContent : '' }; }),
    layout, debug: () => ({ ptr, view: [innerWidth, innerHeight], buttonOn, recs: Array.from(bars.values()).map(r => { const b = r.el.getBoundingClientRect(); return { key: r.key, rect: [b.left, b.top, b.width, b.height], w: r.w, h: r.h, node: !!r.node, connected: r.el.isConnected }; }) }), dispose() { obs.disconnect(); clearTimeout(timer); clearInterval(tick); unhighlight(); host?.remove(); barHost?.remove(); delete globalThis.__neodmRibbon; } };
})();
