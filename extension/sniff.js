/* NeoDM media sniffing logic. Pure functions, shared by the service worker (importScripts) and the tests. */
(function (scope) {
  const MIN_SIZE = 512 * 1024, IMAGE_MIN_SIZE = 64 * 1024;
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
    const roundChunk = !partial && length >= 65536 && length <= 8 * 1024 * 1024 && length % 65536 === 0;
    if (partial ? total < MIN_SIZE : roundChunk && (ranged || !!(state && state.pieces && state.pieces[url]))) {
      out.piece = { key: url, mime: type, len: partial && cr ? cr.end - cr.start + 1 : length };
      return out;
    }
    if (total < MIN_SIZE) return out;
    out.item = { id: url, url, rawUrl: u.href, groupKey: url, kind: type.startsWith('audio/') ? 'audio' : 'video', track: 'file', complete: true, size: total, quality: '', mime: type, source: 'net' };
    if (!partial && ranged) out.item.ambiguous = true; // a 200 on a range-style URL: probably a piece, hidden when the page streams through MSE
    return out;
  }
  /** Decide whether a webRequest.onHeadersReceived details object is a downloadable media file. Item or null. */
  const classify = details => inspect(details).item || null;

  const META_STR = 200, META_URL = 2048, THUMB_MAX = 20 * 1024, MAX_DOM_SOURCES = 50, MAX_BLOBS = 8;
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
    return { id: key, url: key, rawUrl: u.href, groupKey: key, kind: tag === 'audio' ? 'audio' : 'video', track: 'file', complete: true, size: 0, quality: '', mime: '', source: 'dom', primary: true, ...(meta ? { ...meta, dom: true } : {}) };
  }
  /**
   * Apply one 'media-dom' report to the per-tab state. Direct sources become (or update) items with their metadata, blob: sources are kept
   * as state.blobs (MediaSource players), items of elements that disappeared stop being "playing". Returns true when anything changed.
   */
  function domReport(state, sources, now = Date.now()) {
    const list = (Array.isArray(sources) ? sources : []).slice(0, MAX_DOM_SOURCES);
    let changed = false;
    const seen = new Set(), blobs = [];
    for (const s of list) {
      const url = String(s && s.url || ''), tag = s && s.tag === 'audio' ? 'audio' : 'video', meta = cleanMeta(s);
      if (/^blob:/i.test(url)) { if (blobs.length < MAX_BLOBS) blobs.push({ tag, ...meta }); continue; }
      const item = fromDom(url, tag, meta); if (!item) continue;
      seen.add(item.id); if (upsert(state, item, now)) changed = true;
    }
    for (const i of state.items || []) if (i.dom && !seen.has(i.id) && (i.playing || i.vis)) { i.playing = false; i.vis = 0; changed = true; }
    if (blobs.length && !state.mse) { state.mse = true; changed = true; }
    if (JSON.stringify(blobs) !== JSON.stringify(state.blobs || [])) { state.blobs = blobs; changed = true; }
    return changed;
  }

  /** Insert/merge into a per-tab list. The first URL stays; network info (size, mime) wins over DOM info; the largest size is kept. Newest first, capped. */
  function merge(list, item, max = 30) {
    const out = list.filter(x => x.id !== item.id), old = list.find(x => x.id === item.id);
    if (!old) { out.unshift(item); return out.slice(0, max); }
    const m = { ...old, ...item, url: old.url, rawUrl: old.rawUrl || item.rawUrl, size: Math.max(item.size || 0, old.size || 0), mime: item.mime || old.mime, source: old.source === 'net' ? 'net' : item.source };
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
    const next = { ...item, time: now }; if (segKey) next.segKey = segKey;
    const before = state.items.find(i => i.id === next.id);
    state.items = merge(state.items, next);
    return !sameItem(before, state.items.find(i => i.id === next.id));
  }
  /** Clear everything learned about the current page. */
  function resetTracking(state) { state.items = []; state.pieces = {}; state.segs = {}; state.segmented = {}; state.mse = false; state.blobs = []; }

  const segmentedRow = () => ({ id: 'neodm-segmented', kind: 'info', info: true, text: scope.NeoI18n ? scope.NeoI18n.t('media_segmented') : '' });
  const familyOf = i => { try { return new URL(i.url).origin; } catch { return ''; } };
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
    const free = list.filter(i => isFile(i) && !i.dom && i.complete && i.kind === (player.tag === 'audio' ? 'audio' : 'video'));
    if (free.length !== 1) return list;
    return list.map(i => i !== free[0] ? i : { ...i, ...(player.title ? { title: i.title || player.title } : {}), ...(player.thumb ? { thumb: i.thumb || player.thumb } : {}), ...(player.poster ? { poster: i.poster || player.poster } : {}), ...(player.dur ? { dur: i.dur || player.dur } : {}), ...(player.w ? { w: i.w || player.w, h: i.h || player.h } : {}), playing: player.playing, vis: player.vis, viaBlob: true });
  }
  /** Items to show: playing now, then visible items matched to a page element, then primary, then complete files by size; leftovers dropped, capped. */
  function listItems(state, { max = 30, info = false } = {}) {
    let list = (state.items || []).filter(i => !(state.mse && i.ambiguous && !i.primary));
    if (onYouTube(state)) list = list.filter(i => !protectedFragment(i)); // YouTube's player sends protected fragments, not files
    list = attachBlob(state, list); // first: a file that belongs to the page's own player is never "a leftover"
    const biggest = {};
    for (const i of list) if (isFile(i)) biggest[familyOf(i)] = Math.max(biggest[familyOf(i)] || 0, i.size || 0);
    list = list.filter(i => i.primary || i.viaBlob || !isFile(i) || !(biggest[familyOf(i)] >= MIN_SIZE && (i.size || 0) < biggest[familyOf(i)] * 0.1));
    const seen = i => !!(i.dom && i.vis > 0);
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
  /**
   * Which sniffed item does a "download this video" request mean? `list` = the tab's listed items, `target` = what the page found under the
   * pointer ({ found, url, blob }), `srcUrl` = the menu's own source URL. Returns { item } | { url } | { ambiguous } | { none }.
   */
  function pickTarget(list, target, srcUrl) {
    const t = target && typeof target === 'object' ? target : {};
    let media = (list || []).filter(i => !i.info && (i.kind === 'video' || i.kind === 'audio'));
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
    try { const m = /\.([a-z0-9]{2,5})$/i.exec(new URL(item.url).pathname); if (m) return '.' + m[1].toLowerCase(); } catch {}
    return item.kind === 'audio' ? '.m4a' : item.kind === 'gif' ? '.gif' : item.kind === 'image' ? '.jpg' : '.mp4';
  }
  /**
   * Suggested file name sent as optional `fileName`. For URLs with a real file name we leave the choice to the app (empty)
   * unless the stream URL has no usable name (googlevideo "videoplayback"). Track suffix prevents audio/video clobbering.
   */
  function suggestedName(item, title) {
    const stem = safeName(title); if (!stem) return '';
    let urlHasName = false;
    try { const base = new URL(item.url).pathname.split('/').pop(); urlHasName = /\.[a-z0-9]{2,5}$/i.test(base) && !/^videoplayback/i.test(base); } catch {}
    if (urlHasName && item.source === 'dom') return '';
    const suffix = item.track === 'audio-only' ? ' (audio only)' : item.track === 'video-only' ? ' (video only)' : '';
    const quality = item.quality && item.track !== 'audio-only' ? ' ' + item.quality : '';
    const ext = extensionFor(item);
    return Array.from(stem).slice(0, 255 - ext.length - suffix.length - quality.length).join('') + quality + suffix + ext;
  }

  scope.NeoSniff = { TYPE_IDS, DEFAULT_TYPES, typeOfFile, typeOfItem, withDefaults, hostOf, isPaused, MIN_SIZE, IMAGE_MIN_SIZE, classify, inspect, ingest, upsert, resetTracking, listItems, withInfo, isMediaResponse, canonicalUrl, groupKey, segmentedRow, MAX_KEYS, fromDom, merge, safeName, suggestedName, extensionFor, isHttp, cleanMeta, domReport, youtubeNote, isYouTubeHost, isYouTubeVideoPage, fmtSize, fmtDuration, nameTitle, titleOf, describeItem, pickTarget, thumbnail, stopPreview, previewActive, THUMB_MAX };
  if (typeof module !== 'undefined') module.exports = scope.NeoSniff;
})(globalThis);
