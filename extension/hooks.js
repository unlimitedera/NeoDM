/* NeoDM page-world hooks (runs in the page's own JavaScript world, at document_start, in every frame).
   It only OBSERVES: every wrapped function calls the original first with the original `this` and arguments and returns what the original returned; nothing it does can change what the page sees
   except the `toString` of the wrapped functions, which still answers like the originals. Any internal error switches all hooks off (they become pass-through) and is reported once.
   It reports what the page does with media (requests for media files and playlists, MediaSource / SourceBuffer activity, the <video> source changes, DRM use) to the extension's content script
   through a private channel: a CustomEvent whose name and token were chosen at random by the content script and handed over in a data attribute of <html> (read once and removed).
   The extension API is never reachable from here and the page can read nothing that the extension keeps. See SECURITY.md for the threat model. */
(() => {
  'use strict';
  const FLAG = Symbol.for('neodm.hooks');
  try {
    if (globalThis[FLAG]) return;
    const protoOk = /^https?:$/.test(location.protocol) || /^https?:\/\//.test(String(self.origin || ''));
    if (!protoOk) return; // opaque documents (about: frames without an http(s) origin, data:, file:) are left alone
    Object.defineProperty(globalThis, FLAG, { value: true, enumerable: false, configurable: false, writable: false });
  } catch { return; }

  /* ---- captured natives: the page may replace these later; the hooks keep the originals ---- */
  const N = {
    dispatch: EventTarget.prototype.dispatchEvent, CE: globalThis.CustomEvent, stringify: JSON.stringify, now: Date.now.bind(Date), setTimeout: globalThis.setTimeout.bind(globalThis),
    getAttr: Element.prototype.getAttribute, removeAttr: Element.prototype.removeAttribute, URL: globalThis.URL, WeakMap, Map, Set, getOwn: Object.getOwnPropertyDescriptor, defProp: Object.defineProperty,
    fnToString: Function.prototype.toString, apply: Reflect.apply, slice: Array.prototype.slice, headerGet: globalThis.Headers ? Headers.prototype.get : null,
  };
  const CAP = { queue: 300, nets: 400, urlsPerSb: 24, manifest: 512 * 1024, text: 2048 };
  let dead = false, reportedDead = false, channel = null, tries = 0, queue = [];
  const kill = why => { dead = true; if (!reportedDead) { reportedDead = true; try { push({ k: 'hooks', ok: false, why: String(why && why.message || why).slice(0, 120) }); } catch { /* nothing more to do */ } } };

  /* ---- the private channel ---- */
  function findChannel() {
    if (channel) return channel;
    const root = document.documentElement; if (!root) return null;
    const v = N.getAttr.call(root, 'data-neodm-ch');
    if (typeof v !== 'string') return null;
    const i = v.indexOf(':'); if (i < 8 || v.length > 200) return null;
    channel = { name: v.slice(0, i), token: v.slice(i + 1) };
    try { N.removeAttr.call(root, 'data-neodm-ch'); } catch { /* the attribute stays: harmless */ }
    return channel;
  }
  function deliver(msg, target) {
    const ch = findChannel(); if (!ch) return false;
    msg.t = ch.token;
    const text = N.stringify(msg); if (text.length > 70000) return false;
    N.dispatch.call(target || document, new N.CE(ch.name, { detail: text, bubbles: true, composed: true }));
    return true;
  }
  function flush() {
    if (!queue.length) return;
    const rest = [];
    for (const [msg, target] of queue) { let ok = false; try { ok = deliver(msg, target); } catch { ok = true; } if (!ok) rest.push([msg, target]); }
    queue = rest;
    if (queue.length && tries++ < 100) N.setTimeout(flush, 50); else if (tries >= 100) queue = [];
  }
  /** Sends a message to the content script; held (up to CAP.queue) until the channel exists. */
  function push(msg, target) {
    let ok = false; try { ok = queue.length === 0 && deliver(msg, target); } catch { ok = true; }
    if (!ok) { if (queue.length < CAP.queue) queue.push([msg, target]); if (queue.length === 1) N.setTimeout(flush, 50); }
  }
  const emit = (msg, target) => { if (dead) return; try { push(msg, target); } catch (e) { kill(e); } };

  /* ---- wrapping that keeps the page's view of the function: name, length, toString, property descriptor ---- */
  const wrapped = new N.WeakMap();
  const abs = (url, base) => { try { return new N.URL(String(url), base || location.href).href; } catch { return ''; } };
  function wrap(obj, name, make) {
    const d = N.getOwn(obj, name);
    if (!d || !d.configurable || typeof d.value !== 'function') throw new TypeError(name);
    const orig = d.value, fn = make(orig);
    try { N.defProp(fn, 'name', { value: orig.name, configurable: true }); N.defProp(fn, 'length', { value: orig.length, configurable: true }); } catch { /* cosmetic */ }
    wrapped.set(fn, orig);
    N.defProp(obj, name, { ...d, value: fn });
  }
  function wrapSetter(obj, name, make) {
    const d = N.getOwn(obj, name);
    if (!d || !d.configurable || typeof d.set !== 'function') throw new TypeError(name);
    const orig = d.set, fn = make(orig);
    try { N.defProp(fn, 'name', { value: orig.name, configurable: true }); N.defProp(fn, 'length', { value: orig.length, configurable: true }); } catch { /* cosmetic */ }
    wrapped.set(fn, orig);
    N.defProp(obj, name, { ...d, set: fn });
  }

  /* ---- tables (all capped) ---- */
  const tags = new N.WeakMap();            // ArrayBuffer -> { u } : which request produced these bytes
  const msIds = new N.WeakMap(), sbMeta = new N.WeakMap();
  let seq = 0, recent = [];                // recent media-looking responses, for appends whose bytes were re-made by the page (transmuxers)
  const mediaLike = (url, type) => /^(video|audio)\//i.test(type || '') || /mpegurl|dash\+xml/i.test(type || '') || /\.(mp4|m4s|m4v|m4a|webm|weba|ts|aac|mp3|ogg|opus|m3u8|mpd|cmfv|cmfa)(?:$|[?#])/i.test(url) || (/octet-stream/i.test(type || '') && /\.(mp4|m4s|ts)\b/i.test(url));
  const manifestLike = (url, type) => /mpegurl|dash\+xml/i.test(type || '') || /\.(m3u8|mpd)(?:$|[?#])/i.test(url);
  function note(u, extra) {
    recent.push({ u, t: N.now() }); if (recent.length > 8) recent.shift();
    return extra;
  }
  function guess() { const cut = N.now() - 3000; for (let i = recent.length - 1; i >= 0; i--) if (recent[i].t >= cut && !manifestLike(recent[i].u, '')) return recent[i].u; return ''; }
  function reportNet(url, method, type, status, total, via, manifest) {
    const msg = { k: 'net', u: url.slice(0, 2048), m: method, c: String(type || '').slice(0, 100), s: status | 0, n: total > 0 ? total : 0, x: via };
    if (manifest) msg.mf = manifest.slice(0, CAP.manifest);
    emit(msg);
  }
  const rangeTotal = (cr, cl) => { const m = /\/(\d+)\s*$/.exec(cr || ''); return m ? Number(m[1]) : Number(cl) || 0; };

  /* ---- fetch ---- */
  function readCapped(res) {
    // the playlist text of a clone, at most CAP.manifest bytes
    const clone = res.clone();
    if (!clone.body || !clone.body.getReader) return clone.text();
    const reader = clone.body.getReader(), parts = []; let size = 0;
    const step = () => reader.read().then(r => {
      if (r.done) return new TextDecoder().decode(concat(parts));
      parts.push(r.value); size += r.value.byteLength;
      if (size > CAP.manifest) { try { reader.cancel(); } catch { /* ignore */ } return new TextDecoder().decode(concat(parts)); }
      return step();
    });
    return step();
  }
  function concat(parts) { let n = 0; for (const p of parts) n += p.byteLength; const out = new Uint8Array(n); let o = 0; for (const p of parts) { out.set(p, o); o += p.byteLength; } return out; }
  function onFetchResponse(res, reqUrl, method) {
    const url = res.url || reqUrl, type = N.apply(N.headerGet, res.headers, ['content-type']) || '';
    if (!mediaLike(url, type)) return;
    const total = rangeTotal(N.apply(N.headerGet, res.headers, ['content-range']), N.apply(N.headerGet, res.headers, ['content-length']));
    if (manifestLike(url, type)) {
      readCapped(res).then(text => { if (!dead) reportNet(url, method, type, res.status, total, 'fetch', text); }, () => { if (!dead) reportNet(url, method, type, res.status, total, 'fetch'); });
    } else { note(url); reportNet(url, method, type, res.status, total, 'fetch'); }
  }

  /* ---- install ---- */
  function install() {
    // fetch
    if (typeof globalThis.fetch === 'function') wrap(globalThis, 'fetch', orig => function fetch() {
      const p = N.apply(orig, this, arguments);
      if (dead) return p;
      try {
        const a = arguments[0]; let url = '', method = 'GET';
        if (typeof a === 'string') url = abs(a); else if (a && typeof a.url === 'string') { url = a.url; method = String(a.method || 'GET'); } else url = abs(a);
        const o = arguments[1]; if (o && typeof o.method === 'string') method = o.method;
        if (method.toUpperCase() === 'GET' && p && typeof p.then === 'function') p.then(res => { if (dead) return; try { onFetchResponse(res, url, 'GET'); } catch (e) { kill(e); } }, () => {});
      } catch (e) { kill(e); }
      return p;
    });
    // bytes made by Response.arrayBuffer remember their URL
    if (typeof Response === 'function') wrap(Response.prototype, 'arrayBuffer', orig => function arrayBuffer() {
      const p = N.apply(orig, this, arguments);
      if (dead) return p;
      try { const u = this.url; if (u && p && typeof p.then === 'function') p.then(b => { try { if (!dead && b && b.byteLength > 256) tags.set(b, u); } catch (e) { kill(e); } }, () => {}); } catch (e) { kill(e); }
      return p;
    });
    // XMLHttpRequest
    const xhrs = new N.WeakMap();
    wrap(XMLHttpRequest.prototype, 'open', orig => function open(method) {
      const r = N.apply(orig, this, arguments);
      if (!dead) { try { xhrs.set(this, { m: String(method || 'GET').toUpperCase(), u: abs(arguments[1]) }); } catch (e) { kill(e); } }
      return r;
    });
    wrap(XMLHttpRequest.prototype, 'send', orig => function send() {
      const r = N.apply(orig, this, arguments);
      if (dead) return r;
      try {
        const info = xhrs.get(this);
        if (info && info.m === 'GET') this.addEventListener('loadend', function () {
          if (dead) return;
          try {
            if (this.status !== 200 && this.status !== 206) return;
            const url = this.responseURL || info.u, type = this.getResponseHeader('content-type') || '';
            if (!mediaLike(url, type)) return;
            const total = rangeTotal(this.getResponseHeader('content-range'), this.getResponseHeader('content-length'));
            if (this.responseType === 'arraybuffer' && this.response && this.response.byteLength > 256) tags.set(this.response, url);
            if (manifestLike(url, type)) { let text = ''; if (this.responseType === '' || this.responseType === 'text') text = this.responseText || ''; reportNet(url, 'GET', type, this.status, total, 'xhr', text.slice(0, CAP.manifest)); }
            else { note(url); reportNet(url, 'GET', type, this.status, total, 'xhr'); }
          } catch (e) { kill(e); }
        });
      } catch (e) { kill(e); }
      return r;
    });
    // MediaSource
    if (typeof MediaSource === 'function') {
      wrap(URL, 'createObjectURL', orig => function createObjectURL(obj) {
        const u = N.apply(orig, this, arguments);
        if (!dead) { try { if (typeof MediaSource === 'function' && obj instanceof MediaSource) { let id = msIds.get(obj); if (!id) { id = 'm' + (++seq); msIds.set(obj, id); } emit({ k: 'ms', ms: id, u: String(u) }); } } catch (e) { kill(e); } }
        return u;
      });
      wrap(MediaSource.prototype, 'addSourceBuffer', orig => function addSourceBuffer(mime) {
        const sb = N.apply(orig, this, arguments);
        if (!dead) { try { let ms = msIds.get(this); if (!ms) { ms = 'm' + (++seq); msIds.set(this, ms); } const id = 's' + (++seq); sbMeta.set(sb, { id, ms, bytes: 0, urls: [], g: false, n: 0 }); emit({ k: 'sb', ms, sb: id, mime: String(mime || '').slice(0, 200) }); } catch (e) { kill(e); } }
        return sb;
      });
      if (typeof SourceBuffer === 'function') wrap(SourceBuffer.prototype, 'appendBuffer', orig => function appendBuffer(data) {
        const r = N.apply(orig, this, arguments);
        if (dead) return r;
        try {
          const m = sbMeta.get(this);
          if (m) {
            const buf = data && data.buffer && typeof data.byteLength === 'number' && !(data instanceof ArrayBuffer) ? data.buffer : data;
            let u = buf && typeof buf === 'object' ? tags.get(buf) : '', g = false;
            if (!u) { u = guess(); g = !!u; }
            m.bytes += (data && data.byteLength) || 0; m.n++;
            if (u && m.urls.length < CAP.urlsPerSb && !m.urls.some(x => x.u === u)) m.urls.push({ u, g });
            if (!m.timer) { m.timer = true; N.setTimeout(() => { m.timer = false; if (dead) return; emit({ k: 'app', sb: m.id, ms: m.ms, b: m.bytes, a: m.n, us: m.urls.map(x => x.u), gs: m.urls.map(x => (x.g ? 1 : 0)) }); }, 250); }
          }
        } catch (e) { kill(e); }
        return r;
      });
    }
    // media elements
    const proto = HTMLMediaElement.prototype;
    wrapSetter(proto, 'src', orig => function (v) { const r = N.apply(orig, this, arguments); if (!dead) emit({ k: 'src', u: String(v == null ? '' : v).slice(0, 2048), a: abs(v) }, this); return r; });
    const sa = N.getOwn(Element.prototype, 'setAttribute');
    if (sa && sa.configurable && typeof sa.value === 'function') wrap(Element.prototype, 'setAttribute', orig => function setAttribute(name) {
      const r = N.apply(orig, this, arguments);
      if (!dead && typeof name === 'string' && name.length === 3 && (name === 'src' || name.toLowerCase() === 'src')) { try { if (this instanceof HTMLMediaElement) emit({ k: 'src', u: String(arguments[1]).slice(0, 2048), a: abs(arguments[1]) }, this); } catch (e) { kill(e); } }
      return r;
    });
    wrap(proto, 'load', orig => function load() { const r = N.apply(orig, this, arguments); if (!dead) emit({ k: 'load' }, this); return r; });
    wrap(proto, 'play', orig => function play() { const r = N.apply(orig, this, arguments); if (!dead) emit({ k: 'play' }, this); return r; });
    const so = N.getOwn(proto, 'srcObject');
    if (so && so.configurable && typeof so.set === 'function') wrapSetter(proto, 'srcObject', orig => function (v) {
      const r = N.apply(orig, this, arguments);
      if (!dead) { try { const ctor = v && v.constructor && v.constructor.name; const id = typeof MediaSource === 'function' && v instanceof MediaSource ? msIds.get(v) || '' : ''; emit({ k: 'srcobj', t: String(ctor || 'none').slice(0, 30), ms: id }, this); } catch (e) { kill(e); } }
      return r;
    });
    if (typeof proto.setMediaKeys === 'function') wrap(proto, 'setMediaKeys', orig => function setMediaKeys() { const r = N.apply(orig, this, arguments); if (!dead) emit({ k: 'drm' }, this); return r; });
    // keep the page's view of toString (also of Function.prototype.toString itself)
    const tsWrap = function toString() { const o = wrapped.get(this); return N.apply(N.fnToString, o || this, arguments); };
    N.defProp(tsWrap, 'name', { value: 'toString', configurable: true }); N.defProp(tsWrap, 'length', { value: 0, configurable: true });
    wrapped.set(tsWrap, N.fnToString);
    const d = N.getOwn(Function.prototype, 'toString');
    if (d && d.configurable) N.defProp(Function.prototype, 'toString', { ...d, value: tsWrap });
  }
  const steps = [];
  try { install(); emit({ k: 'hooks', ok: true }); } catch (e) { kill(e); }
  void steps;
})();
