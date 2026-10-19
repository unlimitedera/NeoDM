/* NeoDM media sniffing logic. Pure functions, shared by the service worker (importScripts) and the tests. */
(function (scope) {
  const MIN_SIZE = 512 * 1024, SMALL_MIN = 32 * 1024, IMAGE_MIN_SIZE = 64 * 1024;
  /** File-type groups the user can switch on/off (popup). Images exclude GIF, which is its own group. */
  const TYPE_IDS = ['video', 'audio', 'image', 'gif', 'archive', 'program', 'document', 'other'];
  const DEFAULT_TYPES = { video: true, audio: true, image: false, gif: false, archive: true, program: true, document: true, other: true };
  const EXT_TYPES = {
    video: 'mp4 mkv webm avi mov flv wmv m4v mpg mpeg 3gp ogv ts', audio: 'mp3 m4a aac flac wav ogg oga opus wma weba mid midi',
    image: 'jpg jpeg png bmp webp svg ico tif tiff avif heic heif', gif: 'gif', archive: 'zip rar 7z tar gz tgz bz2 xz zst iso cab img',
    program: 'exe msi msix appx apk dmg pkg deb rpm bat cmd jar ps1 appimage', document: 'pdf doc docx xls xlsx ppt pptx txt rtf odt ods odp csv epub mobi md json xml html htm',
  };
  const TYPE_BY_EXT = {}; for (const [type, list] of Object.entries(EXT_TYPES)) for (const e of list.split(' ')) TYPE_BY_EXT[e] = type;
  /** Type group of a download from its file name or URL (extension first), then its MIME type. Unknown = 'other'. */
  function typeOfFile(nameOrUrl, mime) {
    let path = String(nameOrUrl || '');
    try { if (/^https?:/i.test(path)) path = decodeURIComponent(new URL(path).pathname); } catch {}
    const m = /\.([a-z0-9]{1,8})$/i.exec(path.split(/[?#]/)[0]);
    if (m && TYPE_BY_EXT[m[1].toLowerCase()]) return TYPE_BY_EXT[m[1].toLowerCase()];
    const t = String(mime || '').toLowerCase();
    if (t === 'image/gif') return 'gif';
    if (t.startsWith('video/')) return 'video'; if (t.startsWith('audio/')) return 'audio'; if (t.startsWith('image/')) return 'image';
    if (/zip|rar|7z|tar|gzip/.test(t)) return 'archive'; if (/msdownload|msi|executable|x-apple-diskimage/.test(t)) return 'program';
    if (/pdf|msword|officedocument|text\//.test(t)) return 'document';
    return 'other';
  }
  /** Type group of a sniffed media-list item. */
  const typeOfItem = item => item.kind === 'gif' ? 'gif' : item.kind === 'image' ? 'image' : item.kind === 'audio' ? 'audio' : 'video';
  const withDefaults = stored => { const out = { ...DEFAULT_TYPES }; for (const id of TYPE_IDS) if (stored && typeof stored[id] === 'boolean') out[id] = stored[id]; return out; };
  /** Session pause state { all: bool, hosts: [hostname] } -> is NeoDM paused for this page/host? */
  const hostOf = url => { try { return new URL(url).hostname.toLowerCase(); } catch { return ''; } };
  const isPaused = (state, ...urls) => !!state && (state.all === true || urls.some(u => { const h = hostOf(u); return h && Array.isArray(state.hosts) && state.hosts.includes(h); }));
  // YouTube itags: formats that carry audio AND video in one file (progressive/muxed) -> label.
  const MUXED = { 5: '240p', 6: '270p', 13: '176p', 17: '144p', 18: '360p', 22: '720p', 34: '360p', 35: '480p', 36: '240p', 37: '1080p', 38: '3072p', 43: '360p', 44: '480p', 45: '720p', 46: '1080p', 59: '480p', 78: '480p' };
  const AUDIO_ITAGS = new Set([139, 140, 141, 171, 172, 249, 250, 251, 256, 258, 325, 328, 338, 380, 599, 600]);
  const VIDEO_ITAGS = { 133: '240p', 134: '360p', 135: '480p', 136: '720p', 137: '1080p', 160: '144p', 242: '240p', 243: '360p', 244: '480p', 247: '720p', 248: '1080p', 264: '1440p', 266: '2160p', 271: '1440p', 278: '144p', 298: '720p', 299: '1080p', 302: '720p', 303: '1080p', 308: '1440p', 313: '2160p', 394: '144p', 395: '240p', 396: '360p', 397: '480p', 398: '720p', 399: '1080p', 400: '1440p', 401: '2160p' };
  const MANIFEST_TYPES = /mpegurl|dash|vnd\.apple/i;
  const SEGMENT_TYPES = /^video\/(mp2t|iso\.segment)|^audio\/(aac|x-aac)$/i; // HLS/DASH segments, never a whole file
  const EXT_BY_MIME = { 'video/mp4': '.mp4', 'video/webm': '.webm', 'video/x-matroska': '.mkv', 'video/quicktime': '.mov', 'video/ogg': '.ogv', 'video/x-flv': '.flv', 'video/3gpp': '.3gp', 'audio/mp4': '.m4a', 'audio/x-m4a': '.m4a', 'audio/mpeg': '.mp3', 'audio/mp3': '.mp3', 'audio/webm': '.weba', 'audio/ogg': '.ogg', 'audio/wav': '.wav', 'audio/x-wav': '.wav', 'audio/flac': '.flac', 'audio/opus': '.opus', 'image/gif': '.gif', 'image/jpeg': '.jpg', 'image/png': '.png', 'image/webp': '.webp', 'image/avif': '.avif', 'image/svg+xml': '.svg', 'image/bmp': '.bmp' };

  const header = (headers, name) => { const h = (headers || []).find(x => x.name && x.name.toLowerCase() === name); return h ? String(h.value || '') : ''; };
  const isHttp = value => { try { const u = new URL(value); return (u.protocol === 'http:' || u.protocol === 'https:') && !u.username && !u.password; } catch { return false; } };
  const isGoogleVideo = u => /(^|\.)googlevideo\.com$/i.test(u.hostname) && /^\/videoplayback\/?$/i.test(u.pathname);

  /** Total resource size from Content-Range (206) or Content-Length (200); 0 when unknown. */
  function totalSize(status, headers) {
    if (status === 206) { const m = /\/(\d+)\s*$/.exec(header(headers, 'content-range')); return m ? Number(m[1]) : 0; }
    const n = Number(header(headers, 'content-length')); return Number.isFinite(n) && n > 0 ? n : 0;
  }
  /* ---- grouping: every piece of one resource maps to one canonical URL ---- */
  const RANGE_PARAMS = new Set('range bytestart byteend start end begin offset rn rbuf bytes from to sq seg segment fragment chunk part frag'.split(' '));
  const CACHE_PARAMS = new Set('_ _t ts cb nocache rnd random cachebust cachebuster timestamp time'.split(' '));
  const GOOGLE_PARAMS = new Set(['range', 'rn', 'rbuf', 'sq', 'ump', 'alr']); // per-request only; itag, id, expire, sig, lsig, mime, clen, dur stay
  const PATH_RANGE = /\/(?:range\/\d+-\d*|bytes[=_]\d+-\d*)(?=\/|$)/gi;
  const SEG_MAX = 16 * 1024 * 1024; // a fragment of an adaptive stream is never larger than this
  const MAX_KEYS = 200;
  const queryKeys = u => (u.search ? u.search.slice(1).split('&') : []).map(p => { let k = p.split('=')[0]; try { k = decodeURIComponent(k.replace(/\+/g, ' ')); } catch {} return k.toLowerCase(); });
  /** Query string without the named parameters; other parameters keep their exact bytes (signatures, tokens). */
  function stripQuery(u, names) {
    if (!u.search) return '';
    const parts = u.search.slice(1).split('&'), keys = queryKeys(u), keep = parts.filter((_, i) => !names.has(keys[i]));
    return keep.length === parts.length ? u.search : keep.length ? '?' + keep.join('&') : '';
  }
  const rangeLike = u => queryKeys(u).some(k => RANGE_PARAMS.has(k)) || (PATH_RANGE.lastIndex = 0, PATH_RANGE.test(u.pathname));
  /** URL without range, segment-number and cache-busting parameters or range path segments. Other parameters are kept. */
  function canonicalUrl(url) {
    let u; try { u = new URL(url); } catch { return String(url || ''); }
    const c = new URL(u.href); c.hash = '';
    if (isGoogleVideo(u)) { c.search = stripQuery(u, GOOGLE_PARAMS); return c.href; }
    c.search = stripQuery(u, new Set([...RANGE_PARAMS, ...CACHE_PARAMS]));
    const path = u.pathname.replace(PATH_RANGE, ''); if (path !== u.pathname) c.pathname = path || '/';
    return c.href;
  }
  const canonicalGoogleUrl = u => canonicalUrl(u.href);
  /** Key shared by every request for the same resource. */
  const groupKey = item => (item && (item.groupKey || canonicalUrl(item.rawUrl || item.url))) || '';
  const capKeys = (obj, max = MAX_KEYS) => { const keys = Object.keys(obj); for (let i = 0; i < keys.length - max; i++) delete obj[keys[i]]; };

  function describeGoogle(u) {
    const q = u.searchParams, itag = Number(q.get('itag')), mime = (q.get('mime') || '').toLowerCase();
    const clen = Number(q.get('clen')) || 0;
    let track, quality = '';
    if (mime.startsWith('audio/') || AUDIO_ITAGS.has(itag)) track = 'audio-only';
    else if (Object.prototype.hasOwnProperty.call(MUXED, itag) && mime.startsWith('video/')) { track = 'muxed'; quality = MUXED[itag]; }
    else if (mime.startsWith('video/')) { track = 'video-only'; quality = VIDEO_ITAGS[itag] || ''; }
    else return null;
    return { track, quality, mime: mime || (track === 'audio-only' ? 'audio/mp4' : 'video/mp4'), clen, itag };
  }

  const FRAG_NAME = /(?:^|[^a-z])(?:seg(?:ment)?|frag(?:ment)?|chunk|part)[-_ ]?\d+\.[a-z0-9]{2,5}$|^\d{3,}\.(?:mp4|m4s|m4v|m4a|webm|aac)$|[-_.]\d{4,}\.(?:mp4|m4v|m4a|webm)$|^init(?:ialization)?[-_.\d]*\.(?:mp4|m4v|m4a|webm)$/i;
  const parseRange = text => { const m = /bytes\s+(\d+)-(\d+)\/(\d+|\*)/i.exec(text); return m ? { start: Number(m[1]), end: Number(m[2]), total: m[3] === '*' ? 0 : Number(m[3]) } : null; };
  /** Fragment candidate: origin + path with digit runs replaced by '#' + mime. */
  function segmentCandidate(u, type, total) {
    if (total >= SEG_MAX) return null;
    let name = u.pathname.split('/').pop(); try { name = decodeURIComponent(name); } catch {}
    return { key: u.origin + u.pathname.replace(/\d+/g, '#') + '|' + type, path: u.pathname, nameHit: FRAG_NAME.test(name) };
  }
  /* ---- Facebook CDN files: the base64url JSON in the "efg" parameter names the encoding (vencode_tag) ---- */
  const B64 = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/';
  function b64Text(value) {
    let out = '', bits = 0, acc = 0;
    for (const ch of String(value).replace(/-/g, '+').replace(/_/g, '/').replace(/=+$/, '')) {
      const v = B64.indexOf(ch); if (v < 0) return '';
      acc = (acc << 6) | v; bits += 6;
      if (bits >= 8) { bits -= 8; out += String.fromCharCode((acc >> bits) & 255); }
      acc &= (1 << bits) - 1;
    }
    return out;
  }
  /** { track, kind, quality } from the efg parameter of a URL; null when it is absent or unreadable (failures are ignored). */
  function efgInfo(u) {
    try {
      const raw = u.searchParams.get('efg'); if (!raw || raw.length > 2000) return null;
      const info = JSON.parse(b64Text(raw)), tag = String(info && info.vencode_tag || '').toLowerCase(); if (!tag) return null;
      const q = /(\d{3,4})p/.exec(tag), quality = q ? q[1] + 'p' : '';
      if (tag.includes('audio')) return { track: 'audio-only', kind: 'audio', quality: '' };
      if ((tag.includes('video') || tag.includes('dash')) && !/progressive|muxed/.test(tag)) return { track: 'video-only', kind: 'video', quality };
      return { track: 'file', kind: 'video', quality };
    } catch { return null; }
  }
  const isMediaResponse = details => !!details && /^(video|audio|image)\//i.test(header(details.responseHeaders, 'content-type').trim());

  /**
   * Examine one onHeadersReceived details object. Returns { item?, piece?, seg? }:
   * item = a downloadable file, piece = a partial response whose total is unknown, seg = a possible stream fragment.
   * `state` (optional) is the per-tab tracker; it is only read here.
   */
  function inspect(details, state) {
    const out = {};
    if (!details || !isHttp(details.url) || !(details.tabId >= 0)) return out;
    if (details.method && details.method !== 'GET') return out;
    if (details.statusCode !== 200 && details.statusCode !== 206) return out;
    let u; try { u = new URL(details.url); } catch { return out; }
    if (/\.(m3u8|mpd|ts|m4s)$/i.test(u.pathname)) return out;
    const type = header(details.responseHeaders, 'content-type').split(';')[0].trim().toLowerCase();
    if (/^image\//.test(type) && !MANIFEST_TYPES.test(type)) {
      const size = totalSize(details.statusCode, details.responseHeaders);
      if (size < IMAGE_MIN_SIZE) return out;
      out.item = { id: u.href, url: u.href, kind: type === 'image/gif' ? 'gif' : 'image', track: 'file', complete: true, size, quality: '', mime: type, source: 'net' };
      return out;
    }
    if (!/^(video|audio)\//.test(type) || MANIFEST_TYPES.test(type) || SEGMENT_TYPES.test(type)) return out;
    if (isGoogleVideo(u)) {
      const g = describeGoogle(u); if (!g) return out;
      const size = g.clen || totalSize(details.statusCode, details.responseHeaders);
      if (size < MIN_SIZE) return out;
      const url = canonicalGoogleUrl(u);
      out.item = { id: url, url, rawUrl: u.href, groupKey: url, kind: g.track === 'audio-only' ? 'audio' : 'video', track: g.track, complete: g.track === 'muxed', size, quality: g.quality, mime: g.mime, source: 'net' };
      return out;
    }
    const partial = details.statusCode === 206, cr = partial ? parseRange(header(details.responseHeaders, 'content-range')) : null;
    const length = Number(header(details.responseHeaders, 'content-length')) || 0;
    const total = partial ? (cr ? cr.total : 0) : length;
    const url = canonicalUrl(u.href), ranged = rangeLike(u);
    out.seg = segmentCandidate(u, type, total);
    // A short clip (feeds serve many): a whole video file of 32 KB to 512 KB that is not a range-style address or a fragment name is a file too. Audio and everything ranged keep the old limit.
    const small = type.startsWith('video/') && total >= SMALL_MIN && total < MIN_SIZE && !ranged && (!partial || (!!cr && cr.start === 0 && cr.end + 1 === cr.total)) && !FRAG_NAME.test(decodeURIComponentSafe(u.pathname.split('/').pop()));
    const roundChunk = !partial && length >= 65536 && length <= 8 * 1024 * 1024 && length % 65536 === 0;
    if (!small && (partial ? total < MIN_SIZE : roundChunk && (ranged || !!(state && state.pieces && state.pieces[url])))) {
      out.piece = { key: url, mime: type, len: partial && cr ? cr.end - cr.start + 1 : length };
      return out;
    }
    if (total < MIN_SIZE && !small) return out;
    out.item = { id: url, url, rawUrl: u.href, groupKey: url, kind: type.startsWith('audio/') ? 'audio' : 'video', track: 'file', complete: true, size: total, quality: '', mime: type, source: 'net' };
    const efg = efgInfo(u);
    if (efg) Object.assign(out.item, { track: efg.track, kind: efg.kind, quality: efg.quality, complete: efg.track === 'file' });
    if (!partial && ranged) out.item.ambiguous = true; // a 200 on a range-style URL: probably a piece, hidden when the page streams through MSE
    return out;
  }
  /** Decide whether a webRequest.onHeadersReceived details object is a downloadable media file. Item or null. */
  const classify = details => inspect(details).item || null;

  const META_STR = 200, META_URL = 2048, THUMB_MAX = 20 * 1024, MAX_DOM_SOURCES = 200, MAX_BLOBS = 8;
  const text = (v, max = META_STR) => Array.from(String(v == null ? '' : v).replace(/[\x00-\x1f\x7f]/g, ' ').replace(/\s+/g, ' ').trim()).slice(0, max).join('');
  const num = (v, max) => { const n = Number(v); return Number.isFinite(n) && n >= 0 && n <= max ? n : 0; };
  /** Validated, size-capped metadata of one <video>/<audio> element reported by the page. Anything unusable is dropped. */
  function cleanMeta(src) {
    const s = src && typeof src === 'object' ? src : {}, out = {};
    const title = text(s.title); if (title) out.title = title;
    const dur = num(s.dur, 1e7); if (dur > 0) out.dur = Math.round(dur * 10) / 10;
    const w = Math.round(num(s.w, 16384)), h = Math.round(num(s.h, 16384)); if (w > 0 && h > 0) { out.w = w; out.h = h; }
    if (typeof s.poster === 'string' && s.poster.length <= META_URL && isHttp(s.poster)) out.poster = s.poster;
    if (typeof s.thumb === 'string' && s.thumb.length <= THUMB_MAX && /^data:image\/(jpeg|png|webp);base64,[A-Za-z0-9+/=]+$/.test(s.thumb)) out.thumb = s.thumb;
    if (typeof s.key === 'string' && /^[A-Za-z0-9_-]{1,40}$/.test(s.key)) out.key = s.key;
    out.playing = s.playing === true;
    out.vis = Math.round(Math.min(1, Math.max(0, Number(s.vis) || 0)) * 100) / 100;
    return out;
  }
  /** Item for a direct <video>/<audio> source reported by the page (size unknown). `meta` (optional) is the cleaned element description. */
  function fromDom(url, tag, meta) {
    if (!isHttp(url)) return null;
    let u; try { u = new URL(url); } catch { return null; }
    if (/\.(m3u8|mpd)$/i.test(decodeURIComponent(u.pathname))) return null;
    if (isGoogleVideo(u)) return null; // handled with proper itag knowledge by the network sniffer
    const key = canonicalUrl(u.href);
    const item = { id: key, url: key, rawUrl: u.href, groupKey: key, kind: tag === 'audio' ? 'audio' : 'video', track: 'file', complete: true, size: 0, quality: '', mime: '', source: 'dom', primary: true, ...(meta ? { ...meta, dom: true } : {}) };
    if (item.key) { item.owner = item.key; delete item.key; }
    return item;
  }
  /* ---- ownership: a network item belongs to the <video>/<audio> element that fetched it ---- */
  const OWN_BEFORE = 2500, OWN_AFTER = 1000, SETTLE_SLACK = 300, WEAK_PENALTY = 1500, TIE_MS = 40, EL_KEEP_MS = 30 * 60 * 1000, EL_CAP = 80, ITEMS_MAX = 200, ITEMS_HARD = 320, EVENT_TYPES = new Set(['loadstart', 'loadedmetadata', 'progress', 'playing', 'seeked', 'timeupdate']);
  /** Identity of what an element shows (source, poster, title, length). Empty when too little is known to recognise it again. */
  function elSig(meta, url) {
    const m = meta || {}, direct = url && !/^blob:/i.test(url) && isHttp(url) ? canonicalUrl(url) : '';
    const strong = direct || m.poster || (m.title && m.dur);
    return strong ? [direct, m.poster || '', m.title || '', m.dur ? String(Math.round(m.dur)) : ''].join('|').slice(0, 400) : '';
  }
  /** An item is protected from the cap while its element is on the page or was seen in the last 30 minutes. */
  const isProtected = (state, item, now = Date.now()) => { const el = item.owner && state.els && state.els[item.owner]; return !!el && (el.live === true || now - (el.seen || 0) < EL_KEEP_MS); };
  /** Keeps at most `max` items: unowned and stale ones go first (oldest first); protected ones only beyond the hard limit. Thumbnails are kept on the 40 newest items. */
  function trim(state, now = Date.now(), max = ITEMS_MAX) {
    const items = state.items || [];
    if (items.length > max) {
      const rank = i => isProtected(state, i, now) ? 2 : i.owner ? 1 : 0;
      const order = items.map((i, n) => [i, n]).sort(([a, x], [b, y]) => (rank(a) - rank(b)) || ((a.time || 0) - (b.time || 0)) || (y - x)).map(([i]) => i);
      const drop = new Set(order.filter(i => rank(i) < 2).slice(0, items.length - max));
      const left = items.length - drop.size;
      if (left > ITEMS_HARD) for (const i of order.filter(i => rank(i) === 2).slice(0, left - ITEMS_HARD)) drop.add(i);
      if (drop.size) state.items = items.filter(i => !drop.has(i));
    }
    let withThumb = 0; for (const i of state.items) if (i.thumb && ++withThumb > 40) delete i.thumb;
  }
  const capEls = state => {
    const keys = Object.keys(state.els || {}); if (keys.length <= EL_CAP) return;
    const owners = new Set((state.items || []).map(i => i.owner));
    keys.filter(k => !state.els[k].live && !owners.has(k)).sort((a, b) => (state.els[a].seen || 0) - (state.els[b].seen || 0)).slice(0, keys.length - EL_CAP).forEach(k => { delete state.els[k]; if (state.evt) delete state.evt[k]; });
  };
  /**
   * Gives every unowned media item to the element whose events are closest in time: from OWN_BEFORE ms before the item to OWN_AFTER ms after it.
   * The best candidate so far is kept on the item (cand) and the owner is set when the window has closed (plus SETTLE_SLACK for events still on their way from the page),
   * so an element that reacts a moment after the response can still win over one that was merely active before it.
   * Frequent timeupdate events count for less than loadstart/progress/seeked, and a tie (within TIE_MS) goes to the playing element. An owned item is never reassigned.
   */
  function assignOwners(state, now = Date.now()) {
    const evt = state.evt || {}, els = state.els || {}; let changed = false;
    for (const item of state.items || []) {
      if (item.owner || item.info || (item.kind !== 'video' && item.kind !== 'audio')) continue;
      const at = item.first || item.time || now; let best = item.cand && els[item.cand.key] ? { ...item.cand } : null;
      for (const key of Object.keys(evt)) {
        const el = els[key]; if (!el) continue;
        let score = Infinity;
        for (const [t, penalty] of [[evt[key].s, 0], [evt[key].w, WEAK_PENALTY]]) { const d = (t || 0) - at; if (t && d >= -OWN_BEFORE && d <= OWN_AFTER) score = Math.min(score, Math.abs(d) + penalty); }
        if (score === Infinity) continue;
        const tie = best && Math.abs(score - best.score) <= TIE_MS, playing = !!el.playing, bestPlaying = !!(best && els[best.key] && els[best.key].playing);
        if (!best || best.key === key || (tie && playing !== bestPlaying ? playing : score < best.score)) best = { key, score };
      }
      if (best && now - at >= OWN_AFTER + SETTLE_SLACK) { item.owner = best.key; delete item.cand; applyElement(item, els[best.key]); changed = true; }
      else if (best) item.cand = best;
    }
    return changed;
  }
  /** Element events from the page: [{ key, type, t }]. Returns true when an item got an owner, 'mem' when only the in-memory event table changed. */
  function noteEvents(state, events, now = Date.now()) {
    state.evt = state.evt || {}; state.els = state.els || {};
    for (const e of (Array.isArray(events) ? events : []).slice(0, 100)) {
      const key = e && e.key, type = e && e.type;
      if (typeof key !== 'string' || !/^[A-Za-z0-9_-]{1,40}$/.test(key) || !EVENT_TYPES.has(type)) continue;
      const t = Math.min(Number.isFinite(Number(e.t)) && Number(e.t) > 0 ? Number(e.t) : now, now), ev = state.evt[key] = state.evt[key] || { s: 0, w: 0 };
      if (type === 'timeupdate') ev.w = Math.max(ev.w, t); else ev.s = Math.max(ev.s, t);
      const el = state.els[key] = state.els[key] || { key, tag: 'video' }; el.seen = now; el.live = true;
    }
    capKeys(state.evt); capEls(state);
    return assignOwners(state, now) ? true : 'mem';
  }
  const ELEMENT_FIELDS = ['title', 'dur', 'w', 'h', 'poster'];
  /** Copies what the page told about an element (title, length, size in pixels, poster, frame, playing state) to a file the element owns. True when something changed. */
  function applyElement(item, el) {
    if (!el) return false;
    let changed = false; const set = (k, v) => { if (v !== undefined && v !== '' && item[k] !== v) { item[k] = v; changed = true; } };
    set('title', el.title); set('dur', el.dur);
    if (item.track !== 'audio-only') {
      set('poster', el.poster);
      if (el.w && el.h && (item.w !== el.w || item.h !== el.h)) { item.w = el.w; item.h = el.h; changed = true; }
      if (el.thumb && !item.thumb) { item.thumb = el.thumb; changed = true; }
    }
    set('playing', !!el.playing); set('vis', el.vis || 0);
    return changed;
  }
  /** Moves the items and events of element `from` to element `to` (the same video came back as a new element). */
  function relink(state, from, to) {
    for (const i of state.items) if (i.owner === from) i.owner = to;
    if (state.evt && state.evt[from]) { state.evt[to] = state.evt[to] || state.evt[from]; delete state.evt[from]; }
  }
  /**
   * Apply one 'media-dom' report to the per-tab state. Direct sources become (or update) items with their metadata, blob: sources are kept
   * as state.blobs (MediaSource players), elements are remembered in state.els (an element that left the page keeps its items; when the same video
   * shows up in a new element the items follow it), and the metadata of an element is merged into the items it owns. Returns true when anything changed.
   */
  function domReport(state, sources, now = Date.now()) {
    const list = (Array.isArray(sources) ? sources : []).slice(0, MAX_DOM_SOURCES);
    state.els = state.els || {};
    const shape = () => JSON.stringify([Object.values(state.els).map(e => ({ ...e, seen: 0 })), state.items.map(i => [i.id, i.owner || '', i.title, i.dur, i.w, i.playing, i.vis, !!i.thumb, !!i.poster])]);
    const before = shape(); let changed = false;
    const seen = new Set(), live = new Set(), blobs = [];
    for (const s of list) {
      const url = String(s && s.url || ''), tag = s && s.tag === 'audio' ? 'audio' : 'video', meta = cleanMeta(s), key = meta.key;
      if (key) {
        live.add(key);
        const sig = elSig(meta, url); let el = state.els[key];
        if (sig && !state.items.some(i => i.owner === key)) { // a new element that owns nothing yet: is it the video of an element that left the page?
          const old = Object.values(state.els).find(e => e.key !== key && !e.live && e.sig === sig && !live.has(e.key));
          if (old) { relink(state, old.key, key); el = { ...old, ...(el || {}), key }; delete state.els[old.key]; }
        }
        state.els[key] = { ...(el || {}), key, tag, sig, seen: now, live: true, playing: meta.playing, vis: meta.vis, ...Object.fromEntries(ELEMENT_FIELDS.filter(f => meta[f] !== undefined).map(f => [f, meta[f]])), ...(meta.thumb ? { thumb: meta.thumb } : {}) };
      }
      if (/^blob:/i.test(url)) { if (blobs.length < MAX_BLOBS) { const { key: _k, ...plain } = meta; blobs.push({ tag, ...plain }); } continue; }
      const item = fromDom(url, tag, meta); if (!item) continue;
      seen.add(item.id); if (upsert(state, item, now)) changed = true;
    }
    for (const el of Object.values(state.els)) if (!live.has(el.key) && (el.live || el.playing || el.vis)) { el.live = false; el.playing = false; el.vis = 0; }
    for (const i of state.items || []) {
      if (i.dom && !seen.has(i.id) && !i.owner && (i.playing || i.vis)) { i.playing = false; i.vis = 0; changed = true; }
      if (i.owner) { const el = state.els[i.owner]; if (el && !el.live && (i.playing || i.vis)) { i.playing = false; i.vis = 0; } else if (el) applyElement(i, el); }
    }
    for (const el of Object.values(state.els)) if (el.thumb && state.items.some(i => i.owner === el.key && i.thumb)) delete el.thumb; // the frame now lives on the file
    if (blobs.length && !state.mse) { state.mse = true; changed = true; }
    if (JSON.stringify(blobs) !== JSON.stringify(state.blobs || [])) { state.blobs = blobs; changed = true; }
    if (assignOwners(state, now)) changed = true;
    capEls(state); trim(state, now);
    return changed || shape() !== before;
  }

  /** Insert/merge into a per-tab list. The first URL stays; network info (size, mime) wins over DOM info; the largest size is kept. Newest first, capped. */
  function merge(list, item, max = 30) {
    const out = list.filter(x => x.id !== item.id), old = list.find(x => x.id === item.id);
    if (!old) { out.unshift(item); return out.slice(0, max); }
    const m = { ...old, ...item, url: old.url, rawUrl: old.rawUrl || item.rawUrl, size: Math.max(item.size || 0, old.size || 0), mime: item.mime || old.mime, source: old.source === 'net' ? 'net' : item.source };
    if (old.owner) m.owner = old.owner;
    if (old.primary || item.primary) m.primary = true; else delete m.primary;
    if (old.ambiguous && item.ambiguous) m.ambiguous = true; else delete m.ambiguous;
    out.unshift(m); return out.slice(0, max);
  }
  const sameItem = (a, b) => { const strip = x => x && JSON.stringify({ ...x, time: 0 }); return strip(a) === strip(b); };

  /**
   * Feed one response into the per-tab state { items, pieces, segs, segmented, mse }. Returns true when the visible list changed,
   * 'quiet' when only the trackers changed, false otherwise.
   */
  function ingest(state, details, types, now = Date.now()) {
    state.pieces = state.pieces || {}; state.segs = state.segs || {}; state.segmented = state.segmented || {};
    const r = inspect(details, state);
    if (r.seg) {
      if (state.segmented[r.seg.key]) return false;
      const list = state.segs[r.seg.key] = state.segs[r.seg.key] || [];
      if (!list.includes(r.seg.path) && list.length < 3) list.push(r.seg.path);
      capKeys(state.segs);
      if (r.seg.nameHit || list.length >= 3) {
        state.segmented[r.seg.key] = true; capKeys(state.segmented); delete state.segs[r.seg.key];
        state.items = state.items.filter(i => i.segKey !== r.seg.key);
        return true;
      }
    }
    if (r.piece) { state.pieces[r.piece.key] = { mime: r.piece.mime, len: r.piece.len }; capKeys(state.pieces); return 'quiet'; }
    if (!r.item || (types && !types[typeOfItem(r.item)])) return r.seg ? 'quiet' : false;
    return upsert(state, r.item, now, r.seg && r.seg.key) || (r.seg ? 'quiet' : false);
  }
  /** Add or merge one item (network or DOM); true when anything but the time changed. */
  function upsert(state, item, now = Date.now(), segKey) {
    state.pieces = state.pieces || {}; if (state.pieces[item.groupKey]) delete state.pieces[item.groupKey];
    const before = state.items.find(i => i.id === item.id), next = { ...item, time: now, first: before && before.first || now }; if (segKey) next.segKey = segKey;
    if (before && before.owner) next.owner = before.owner; // an owned item is never reassigned
    state.items = merge(state.items, next, Infinity);
    assignOwners(state, now); trim(state, now);
    return !sameItem(before, state.items.find(i => i.id === next.id));
  }
  /** Clear everything learned about the current page. */
  function resetTracking(state) { state.items = []; state.pieces = {}; state.segs = {}; state.segmented = {}; state.mse = false; state.blobs = []; state.els = {}; state.evt = {}; }

  const segmentedRow = () => ({ id: 'neodm-segmented', kind: 'info', info: true, text: scope.NeoI18n ? scope.NeoI18n.t('media_segmented') : '' });
  /** Origin plus directory of the file: files of one CDN folder are the only ones compared by size. */
  const familyOf = i => { try { const u = new URL(i.url); return u.origin + u.pathname.replace(/[^/]*$/, ''); } catch { return ''; } };
  const isFile = i => i.track === 'file' && i.kind !== 'image' && i.kind !== 'gif';
  const YT_HOST = /(^|\.)(youtube\.com|youtu\.be|youtube-nocookie\.com)$/i;
  const isYouTubeHost = url => { const h = hostOf(url); return !!h && YT_HOST.test(h); };
  const isYouTubeVideoPage = url => { try { const u = new URL(url); return YT_HOST.test(u.hostname) && (/^\/(watch|shorts|live|embed|v)\b/.test(u.pathname) || /(^|\.)youtu\.be$/i.test(u.hostname) || u.hostname.startsWith('music.')); } catch { return false; } };
  const onYouTube = state => isYouTubeHost(state.page) || isYouTubeHost(state.host);
  const isGoogleVideoUrl = url => { try { return isGoogleVideo(new URL(url)); } catch { return false; } };
  const protectedFragment = i => isGoogleVideoUrl(i.url) && !(i.complete && i.track === 'muxed');
  /** Gives a network file the metadata of the page's MediaSource (blob:) player when there is exactly one file and one player to pair. */
  function attachBlob(state, list) {
    const blobs = state.blobs || [], playing = blobs.filter(b => b.playing), player = playing.length === 1 ? playing[0] : blobs.length === 1 ? blobs[0] : null;
    if (!player) return list;
    const free = list.filter(i => isFile(i) && !i.dom && !i.owner && i.complete && i.kind === (player.tag === 'audio' ? 'audio' : 'video'));
    if (free.length !== 1) return list;
    return list.map(i => i !== free[0] ? i : { ...i, ...(player.title ? { title: i.title || player.title } : {}), ...(player.thumb ? { thumb: i.thumb || player.thumb } : {}), ...(player.poster ? { poster: i.poster || player.poster } : {}), ...(player.dur ? { dur: i.dur || player.dur } : {}), ...(player.w ? { w: i.w || player.w, h: i.h || player.h } : {}), playing: player.playing, vis: player.vis, viaBlob: true });
  }
  /** Items to show: playing now, then visible items matched to a page element, then primary, then complete files by size; leftovers dropped, capped. */
  function listItems(state, { max = ITEMS_MAX, info = false } = {}) {
    let list = (state.items || []).filter(i => !(state.mse && i.ambiguous && !i.primary && !i.owner));
    if (onYouTube(state)) list = list.filter(i => !protectedFragment(i)); // YouTube's player sends protected fragments, not files
    list = attachBlob(state, list); // first: a file that belongs to the page's own player is never "a leftover"
    // Only a fragment-like leftover is dropped: no element link at all, and under 10% of the biggest unlinked file in the same CDN folder.
    const linked = i => !!(i.primary || i.viaBlob || i.owner || i.dom), biggest = {};
    for (const i of list) if (isFile(i) && !linked(i)) biggest[familyOf(i)] = Math.max(biggest[familyOf(i)] || 0, i.size || 0);
    list = list.filter(i => linked(i) || !isFile(i) || !(biggest[familyOf(i)] >= MIN_SIZE && (i.size || 0) < biggest[familyOf(i)] * 0.1));
    const seen = i => !!((i.dom || i.owner) && i.vis > 0);
    list = list.map((i, n) => [i, n]).sort(([a, x], [b, y]) => (!!b.playing - !!a.playing) || (seen(b) - seen(a)) || (!!b.primary - !!a.primary) || (!!b.complete - !!a.complete) || ((b.size || 0) - (a.size || 0)) || (x - y)).map(([i]) => i).slice(0, max);
    return list;
  }
  /** Informational row explaining why nothing can be captured on YouTube; null elsewhere or when a complete file is listed. */
  function youtubeNote(state, list) {
    if (!onYouTube(state) || (list || []).some(i => !i.info && i.complete && isGoogleVideoUrl(i.url) && i.track === 'muxed')) return null;
    const hidden = (state.items || []).some(protectedFragment);
    if (!hidden && !(state.mse && isYouTubeVideoPage(state.page))) return null;
    return { id: 'neodm-youtube', kind: 'info', info: true, text: scope.NeoI18n ? scope.NeoI18n.t('media_youtube') : '' };
  }
  /* ---- how an item is described to the user (popup and ribbon) ---- */
  const fmtSize = n => !n ? '' : n >= 1073741824 ? (n / 1073741824).toFixed(2) + ' GB' : n >= 1048576 ? (n / 1048576).toFixed(1) + ' MB' : Math.max(1, Math.round(n / 1024)) + ' KB';
  const two = n => String(n).padStart(2, '0');
  /** Seconds -> 03:25 or 1:02:03; '' when unknown. */
  function fmtDuration(sec) {
    const s = Math.round(Number(sec)); if (!Number.isFinite(s) || s <= 0) return '';
    const h = Math.floor(s / 3600), m = Math.floor(s % 3600 / 60);
    return h ? `${h}:${two(m)}:${two(s % 60)}` : `${two(m)}:${two(s % 60)}`;
  }
  /** Title without page help: the decoded last path segment of the URL, else the host. */
  function nameTitle(item) {
    let u; try { u = new URL(item.url); } catch { return ''; }
    let name = u.pathname.split('/').filter(Boolean).pop() || '';
    try { name = decodeURIComponent(name); } catch {}
    name = text(name);
    return name && !/^videoplayback$/i.test(name) ? name : u.hostname;
  }
  const titleOf = item => text(item.title) || nameTitle(item);
  /** { title, meta, kind } for a list row. `override` carries values learned later by a preview element ({ dur, w, h }). */
  function describeItem(item, translate = scope.NeoI18n && scope.NeoI18n.t, override = {}) {
    const t = translate || (k => k);
    const kind = item.kind === 'gif' ? t('kind_gif') : item.kind === 'image' ? t('kind_image') : item.track === 'audio-only' ? t('kind_audio_only') : item.track === 'video-only' ? t('kind_video_only') : item.kind === 'audio' ? t('kind_audio') : t('kind_video');
    const w = override.w || item.w, h = override.h || item.h, dims = w && h ? `${w}x${h}` : '';
    const parts = [dims ? `${kind} ${dims}` : kind, dims ? '' : item.quality, fmtDuration(override.dur || item.dur), fmtSize(item.size)].filter(Boolean);
    return { title: titleOf(item), meta: parts.join(' · '), kind, host: hostOf(item.url) };
  }
  /** The item to offer for one element: complete files before video-only tracks, never an audio-only track; the most recently active one first. Null when there is none. */
  function bestVideo(items) {
    const rank = i => i.track === 'video-only' ? 1 : 0;
    return (items || []).filter(i => i && !i.info && i.kind === 'video' && i.track !== 'audio-only').sort((a, b) => (rank(a) - rank(b)) || ((b.time || 0) - (a.time || 0)) || ((b.size || 0) - (a.size || 0)))[0] || null;
  }
  /** The audio-only track of an element's items (or its audio element's file); null when there is none. */
  const bestAudio = items => (items || []).filter(i => i && !i.info && (i.track === 'audio-only' || i.kind === 'audio')).sort((a, b) => ((b.time || 0) - (a.time || 0)) || ((b.size || 0) - (a.size || 0)))[0] || null;
  /* ---- per-video bar geometry (pure, so it can be tested): the bar sits in the top corner on the start side of the visible part of the video ---- */
  const BAR_MIN_W = 200, BAR_MIN_H = 120, BAR_MIN_VISIBLE = 0.25, BAR_INSET = 8;
  /** rect = element box in viewport pixels, view = { w, h }, bar = { w, h }. Returns { x, y, ratio } for position:fixed, or null when no bar should be shown. */
  function barPlacement(rect, view, bar, rtl) {
    if (!rect || !(rect.width >= BAR_MIN_W) || !(rect.height >= BAR_MIN_H)) return null;
    const left = Math.max(rect.left, 0), right = Math.min(rect.right, view.w), top = Math.max(rect.top, 0), bottom = Math.min(rect.bottom, view.h);
    const ratio = Math.max(0, right - left) * Math.max(0, bottom - top) / (rect.width * rect.height);
    if (ratio < BAR_MIN_VISIBLE) return null;
    if (right - left < bar.w + 2 * BAR_INSET || bottom - top < bar.h + 2 * BAR_INSET) return null;
    return { x: Math.round(rtl ? right - BAR_INSET - bar.w : left + BAR_INSET), y: Math.round(top + BAR_INSET), ratio: Math.round(ratio * 100) / 100 };
  }
  /* ---- right click on a link ---- */
  const SOCIAL_HOST = /(^|\.)(facebook\.com|fb\.com|fb\.watch|instagram\.com|threads\.net|x\.com|twitter\.com|tiktok\.com|reddit\.com|linkedin\.com|vk\.com|ok\.ru|t\.me|pinterest\.com|snapchat\.com|tumblr\.com)$/i;
  const NOT_FILE_EXT = new Set(['html', 'htm', 'php', 'asp', 'aspx', 'jsp', 'xhtml']);
  const DOWNLOAD_HINT = /(^|[\/_.\-])(download|downloads|dl|file|files|attachment|attachments|export|get)([\/_.\-?=&]|$)/i;
  /** True when a link target is a web page and not a file: no recognisable file extension, and it is on the page's own site or on a social site. */
  function isPageLink(linkUrl, pageUrl) {
    let u; try { u = new URL(linkUrl); } catch { return false; }
    const m = /\.([a-z0-9]{1,8})$/i.exec(decodeURIComponentSafe(u.pathname));
    if (m && !NOT_FILE_EXT.has(m[1].toLowerCase()) && TYPE_BY_EXT[m[1].toLowerCase()]) return false;
    if (SOCIAL_HOST.test(u.hostname)) return true;
    let same = false; try { same = new URL(pageUrl).origin === u.origin; } catch {}
    return same && !DOWNLOAD_HINT.test(u.pathname + u.search);
  }
  function decodeURIComponentSafe(v) { try { return decodeURIComponent(v); } catch { return v; } }
  /**
   * Which sniffed item does a "download this video" request mean? `list` = the tab's listed items, `target` = what the page found under the
   * pointer ({ found, url, blob }), `srcUrl` = the menu's own source URL. Returns { item } | { url } | { ambiguous } | { none }.
   */
  function pickTarget(list, target, srcUrl) {
    const t = target && typeof target === 'object' ? target : {};
    let media = (list || []).filter(i => !i.info && (i.kind === 'video' || i.kind === 'audio'));
    if (t.found && typeof t.key === 'string' && t.key) { // 1. what the pointed-at element fetched itself
      const owned = bestVideo(media.filter(i => i.owner === t.key)); if (owned) return { item: owned };
    }
    if (t.found && t.blob) media = media.filter(i => !i.dom); // the pointed-at player has no file URL: elements with their own URL are other players
    for (const url of [t.url, srcUrl]) {
      if (!isHttp(url)) continue;
      const hit = media.find(i => i.id === canonicalUrl(url));
      return hit ? { item: hit } : { url };
    }
    if (!media.length) return { none: true };
    const playing = media.filter(i => i.playing);
    if (playing.length === 1) return { item: playing[0] };
    const pool = (playing.length ? playing : media).filter(i => i.complete).sort((a, b) => (b.size || 0) - (a.size || 0));
    if (pool.length === 1) return { item: pool[0] };
    if (pool.length > 1 && pool[0].size > 0 && pool[0].size >= 2 * pool[1].size) return { item: pool[0] };
    return { ambiguous: true };
  }
  /* ---- thumbnail with preview: one plays at a time ---- */
  let active = null;
  const previewActive = () => !!active;
  function stopPreview() {
    const a = active; active = null; if (!a) return;
    try { a.media.pause(); a.media.muted = true; } catch {}
    a.box.classList.remove('playing'); if (a.onStop) a.onStop();
  }
  /**
   * Builds the thumbnail of a row: the frame grabbed by the page (thumb), else the poster, else a muted <video preload=metadata> that shows
   * its own first frame. A click plays it unmuted (stopping any other preview); an error hides it. onMeta({ dur, w, h }) reports what the element learned.
   */
  function thumbnail(doc, item, { width = 96, height = 54, onMeta, onStop, eager = true, play = 'Play', stop = 'Stop' } = {}) {
    const box = doc.createElement('button'); box.type = 'button'; box.className = 'neo-thumb';
    box.style.cssText = `width:${width}px;height:${height}px;min-width:${width}px;position:relative;overflow:hidden;padding:0;border:0;border-radius:4px;background:#0c131b;cursor:pointer;flex:none`;
    box.title = play; box.setAttribute('aria-label', play);
    const fill = el => { el.style.cssText = 'position:absolute;inset:0;width:100%;height:100%;object-fit:cover;display:block'; return el; };
    let media = null, img = null;
    const still = item.kind === 'image' || item.kind === 'gif', audio = item.kind === 'audio', playable = isHttp(item.url) && !still;
    const makeMedia = () => {
      if (media || !playable) return media;
      media = fill(doc.createElement(audio ? 'audio' : 'video')); media.preload = 'metadata'; media.muted = true; media.setAttribute('playsinline', '');
      if (audio) media.style.display = 'none';
      media.addEventListener('loadedmetadata', () => {
        if (onMeta) onMeta({ dur: media.duration, w: media.videoWidth || 0, h: media.videoHeight || 0 });
        if (!audio && Number.isFinite(media.duration) && media.currentTime === 0) { try { media.currentTime = Math.min(0.1, media.duration / 2); } catch {} }
      });
      media.addEventListener('error', () => { if (active && active.media === media) stopPreview(); media.remove(); media = null; if (!img) box.hidden = true; else box.classList.remove('live'); });
      media.addEventListener('ended', () => { if (active && active.media === media) stopPreview(); });
      box.append(media); media.src = item.url; return media;
    };
    if (still) { img = fill(doc.createElement('img')); img.alt = ''; img.src = item.url; img.addEventListener('error', () => { box.hidden = true; }); box.append(img); box.disabled = true; box.style.cursor = 'default'; return { el: box, stop() {} }; }
    const src = item.thumb || item.poster;
    if (src) { img = fill(doc.createElement('img')); img.alt = ''; img.src = src; img.addEventListener('error', () => { img.remove(); img = null; if (!makeMedia()) box.hidden = true; }); box.append(img); }
    else if (eager && !makeMedia()) box.hidden = true;
    if (audio) { const g = doc.createElement('span'); g.textContent = '\u266B'; g.style.cssText = 'position:absolute;inset:0;display:flex;align-items:center;justify-content:center;font-size:22px;color:#39b995'; box.append(g); }
    const badge = doc.createElement('span'); badge.className = 'neo-play'; badge.textContent = '\u25B6'; badge.style.cssText = 'position:absolute;inset-inline-end:3px;bottom:3px;font-size:10px;line-height:1;color:#fff;background:rgba(0,0,0,.6);border-radius:3px;padding:2px 3px';
    box.append(badge);
    box.addEventListener('click', e => {
      e.stopPropagation();
      if (active && active.box === box) { stopPreview(); box.title = play; box.setAttribute('aria-label', play); return; }
      stopPreview();
      const m = makeMedia(); if (!m) return;
      m.muted = false; box.classList.add('playing', 'live'); if (img) img.style.display = 'none'; m.style.display = audio ? 'none' : 'block';
      box.title = stop; box.setAttribute('aria-label', stop);
      active = { media: m, box, onStop: () => { if (img) img.style.display = 'block'; box.title = play; box.setAttribute('aria-label', play); if (onStop) onStop(); } };
      const p = m.play(); if (p && p.catch) p.catch(() => { if (active && active.media === m) stopPreview(); });
    });
    return { el: box, stop() { if (active && active.box === box) stopPreview(); } };
  }
  /** The tab's list without the typed filter applied; used by tests and by the popup after filtering. */
  const withInfo = (state, list) => (state.segmented && Object.keys(state.segmented).length ? [...list, segmentedRow()] : list);

  const RESERVED = /^(con|prn|aux|nul|com[1-9]|lpt[1-9])$/i;
  /** Page title -> safe Windows file name stem (no path chars, <=120 chars). Empty string when nothing usable. */
  function safeName(title) {
    let s = String(title || '').replace(/\s+-\s+YouTube$/i, '').normalize('NFC');
    s = s.replace(/[\u0000-\u001f\u007f\\/:*?"<>|]/g, ' ').replace(/\s+/g, ' ').trim().replace(/^[. ]+|[. ]+$/g, '');
    s = Array.from(s).slice(0, 120).join('').trim().replace(/[. ]+$/g, '');
    return RESERVED.test(s) ? s + '_' : s;
  }
  function extensionFor(item) {
    if (EXT_BY_MIME[item.mime]) return EXT_BY_MIME[item.mime];
    try { const m = /\.([a-z0-9]{2,5})$/i.exec(decodeURIComponentSafe(new URL(item.url).pathname)); if (m && TYPE_BY_EXT[m[1].toLowerCase()]) return '.' + m[1].toLowerCase(); } catch {}
    return item.kind === 'audio' ? '.m4a' : item.kind === 'gif' ? '.gif' : item.kind === 'image' ? '.jpg' : '.mp4';
  }
  /**
   * File name sent as `fileName` with every request made from the page (button, right-click, popup, ribbon): title + extension, so the app never has to guess.
   * Without a title the name of the address is used when it has an extension; an address with neither gets the host as the stem. Track suffix prevents audio/video clobbering.
   */
  function suggestedName(item, title) {
    let stem = safeName(title);
    if (!stem) {
      if (item.source !== 'dom') return ''; // a sniffed network file with no title: the app reads the name from the address and the response
      let base = ''; try { const u = new URL(item.url); base = u.pathname.split('/').pop(); } catch {}
      if (/\.[a-z0-9]{2,5}$/i.test(base) && !/^videoplayback/i.test(base)) return '';
      stem = safeName(hostOf(item.url) || '') || (item.kind === 'audio' ? 'audio' : 'video');
    }
    const suffix = item.track === 'audio-only' ? ' (audio only)' : item.track === 'video-only' ? ' (video only)' : '';
    const quality = item.quality && item.track !== 'audio-only' ? ' ' + item.quality : '';
    const ext = extensionFor(item);
    return Array.from(stem).slice(0, 255 - ext.length - suffix.length - quality.length).join('') + quality + suffix + ext;
  }
  const MIME_BY_EXT = { '.mp4': 'video/mp4', '.m4v': 'video/mp4', '.webm': 'video/webm', '.mkv': 'video/x-matroska', '.mov': 'video/quicktime', '.ogv': 'video/ogg', '.flv': 'video/x-flv', '.3gp': 'video/3gpp', '.m4a': 'audio/mp4', '.mp3': 'audio/mpeg', '.weba': 'audio/webm', '.ogg': 'audio/ogg', '.wav': 'audio/wav', '.flac': 'audio/flac', '.opus': 'audio/opus' };
  /** Content type hint (type/subtype) sent as `mime` so the app can pick the extension even when the address has none; '' when unknown. */
  function mimeFor(item) {
    if (typeof item.mime === 'string' && /^(video|audio)\/[A-Za-z0-9.+-]+$/.test(item.mime)) return item.mime;
    return MIME_BY_EXT[extensionFor(item)] || '';
  }

  scope.NeoSniff = { TYPE_IDS, DEFAULT_TYPES, typeOfFile, typeOfItem, withDefaults, hostOf, isPaused, MIN_SIZE, IMAGE_MIN_SIZE, classify, inspect, ingest, upsert, resetTracking, listItems, withInfo, isMediaResponse, canonicalUrl, groupKey, segmentedRow, MAX_KEYS, fromDom, merge, safeName, suggestedName, mimeFor, extensionFor, isHttp, cleanMeta, domReport, youtubeNote, isYouTubeHost, isYouTubeVideoPage, fmtSize, fmtDuration, nameTitle, titleOf, describeItem, pickTarget, bestVideo, bestAudio, barPlacement, isPageLink, noteEvents, assignOwners, trim, elSig, efgInfo, isProtected, ITEMS_MAX, OWN_AFTER, OWN_BEFORE, SETTLE_SLACK, thumbnail, stopPreview, previewActive, THUMB_MAX };
  if (typeof module !== 'undefined') module.exports = scope.NeoSniff;
})(globalThis);
