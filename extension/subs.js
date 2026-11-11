/* NeoDM 1.14.0: subtitle files that a player loads on its own (.vtt, .srt and friends). Pure helpers, no browser calls: the service worker feeds them
   what chrome.webRequest saw. Nothing here is stored on disk and nothing leaves the browser until the user captures a stream. */
(function (scope) {
  const MAX_BYTES = 12000, MAX_ENTRIES = 20, MAX_URL = 2048, MAX_LABEL = 80, MAX_LANG = 40;
  const FORMATS = ['vtt', 'srt', 'ass', 'ttml', 'dfxp'];
  const TYPES = { 'text/vtt': 'vtt', 'application/x-subrip': 'srt', 'text/x-subrip': 'srt', 'application/ttml+xml': 'ttml', 'application/ttaf+xml': 'dfxp' };
  const UNKNOWN_LABEL = 'Subtitles';

  /** [code, names...]: names are whole tokens of a path, a file name or a label (lower case). Two letter codes that are common words elsewhere are listed in AMBIGUOUS. */
  const LANGUAGES = [
    ['en', 'en', 'eng', 'english', 'en-us', 'en-gb', 'en_us', 'en_gb'], ['ar', 'ar', 'ara', 'arabic'], ['es', 'es', 'spa', 'spanish', 'espanol', 'es-es', 'es-la', 'es-419'],
    ['fr', 'fr', 'fra', 'fre', 'french', 'francais'], ['de', 'de', 'deu', 'ger', 'german', 'deutsch'], ['pt', 'pt', 'por', 'portuguese', 'pt-br', 'pt_br', 'pt-pt'],
    ['ru', 'ru', 'rus', 'russian'], ['tr', 'tr', 'tur', 'turkish'], ['zh', 'zh', 'zho', 'chi', 'chinese', 'zh-cn', 'zh-tw', 'zh-hans', 'zh-hant', 'zh_cn', 'zh_tw'],
    ['ja', 'ja', 'jpn', 'japanese', 'jp'], ['ko', 'ko', 'kor', 'korean'], ['it', 'it', 'ita', 'italian', 'italiano'], ['nl', 'nl', 'nld', 'dut', 'dutch'],
    ['pl', 'pl', 'pol', 'polish'], ['sv', 'sv', 'swe', 'swedish'], ['da', 'da', 'dan', 'danish'], ['fi', 'fi', 'fin', 'finnish'], ['nb', 'nob', 'norwegian'],
    ['cs', 'cs', 'ces', 'cze', 'czech'], ['el', 'el', 'ell', 'gre', 'greek'], ['he', 'he', 'heb', 'hebrew'], ['hi', 'hi', 'hin', 'hindi'], ['id', 'ind', 'indonesian'],
    ['th', 'th', 'tha', 'thai'], ['vi', 'vi', 'vie', 'vietnamese'], ['uk', 'uk', 'ukr', 'ukrainian'], ['ro', 'ro', 'ron', 'rum', 'romanian'], ['hu', 'hu', 'hun', 'hungarian'],
    ['fa', 'fa', 'fas', 'per', 'persian', 'farsi'], ['bn', 'bn', 'ben', 'bengali'], ['ms', 'ms', 'msa', 'may', 'malay'],
  ];
  /** Two letter tokens that are also ordinary words or ids in addresses: only trusted when they are the whole file name or a language query value. */
  const AMBIGUOUS = new Set(['it', 'jp']);
  const TOKEN_TO_CODE = new Map();
  for (const [code, ...names] of LANGUAGES) for (const n of names) if (!TOKEN_TO_CODE.has(n)) TOKEN_TO_CODE.set(n, code);

  const cleanText = (v, max) => typeof v === 'string' ? v.replace(/[\u0000-\u001f\u007f]/g, ' ').replace(/\s+/g, ' ').trim().slice(0, max) : '';
  const ext = path => { const m = /\.([A-Za-z0-9]{2,5})$/.exec(path); return m ? m[1].toLowerCase() : ''; };
  const parse = url => { try { const u = new URL(url); return (u.protocol === 'http:' || u.protocol === 'https:') && !u.username && !u.password ? u : null; } catch { return null; } };

  /** The subtitle format a response is, or '': by the path's extension (the query is ignored), else by Content-Type. */
  function formatOf(url, contentType) {
    const u = parse(url); if (!u) return '';
    const byPath = ext(decodeURIComponentSafe(u.pathname));
    if (FORMATS.includes(byPath)) return byPath;
    const type = String(contentType || '').split(';')[0].trim().toLowerCase();
    return TYPES[type] || '';
  }
  function decodeURIComponentSafe(s) { try { return decodeURIComponent(s); } catch { return s; } }

  /** Language code from the address (path segments and file name; a lang/language/locale/hl query value) and, when given, the player's track label or srclang. null when unknown. */
  function guessLanguage(url, label) {
    const fromLabel = codeOfText(label, true);
    if (fromLabel) return fromLabel;
    const u = parse(url); if (!u) return null;
    for (const key of ['lang', 'language', 'locale', 'hl', 'srclang']) { const v = u.searchParams.get(key); const c = v && codeOfText(v, true); if (c) return c; }
    const parts = decodeURIComponentSafe(u.pathname).split('/').filter(Boolean);
    const stem = (parts.pop() || '').replace(/\.[A-Za-z0-9]{2,5}$/, '');
    const fileTokens = stem.split(/[^A-Za-z0-9]+/).filter(Boolean).map(x => x.toLowerCase());
    // The file name wins (clip.en.vtt, en.vtt, subs_ar.srt), then the folders from the nearest outward (/subs/en/1.vtt).
    const whole = stem.toLowerCase();
    if (TOKEN_TO_CODE.has(whole)) return TOKEN_TO_CODE.get(whole);
    for (let i = fileTokens.length - 1; i >= 0; i--) { const t = fileTokens[i]; if (!AMBIGUOUS.has(t) && TOKEN_TO_CODE.has(t)) return TOKEN_TO_CODE.get(t); }
    for (let i = parts.length - 1; i >= 0; i--) {
      const seg = decodeURIComponentSafe(parts[i]).toLowerCase();
      if (!AMBIGUOUS.has(seg) && TOKEN_TO_CODE.has(seg)) return TOKEN_TO_CODE.get(seg);
      for (const t of seg.split(/[^a-z0-9]+/).filter(Boolean)) if (t.length >= 3 && TOKEN_TO_CODE.has(t)) return TOKEN_TO_CODE.get(t);
    }
    return null;
  }
  function codeOfText(text, trustShort) {
    const v = cleanText(text, MAX_LABEL).toLowerCase(); if (!v) return null;
    if (TOKEN_TO_CODE.has(v)) return TOKEN_TO_CODE.get(v);
    for (const t of v.split(/[^a-z0-9-_]+/).filter(Boolean)) if (TOKEN_TO_CODE.has(t) && (trustShort || t.length >= 3)) return TOKEN_TO_CODE.get(t);
    return null;
  }

  /** One entry for the list, or null when this is not a subtitle response. */
  function entryFor(details) {
    if (!details || typeof details.url !== 'string' || details.url.length > MAX_URL) return null;
    if (details.statusCode && (details.statusCode < 200 || details.statusCode >= 300)) return null;
    const header = name => { const h = (details.responseHeaders || []).find(r => r && typeof r.name === 'string' && r.name.toLowerCase() === name); return h ? String(h.value || '') : ''; };
    const format = formatOf(details.url, header('content-type'));
    if (!format) return null;
    const u = parse(details.url); u.hash = '';
    const language = guessLanguage(u.href, details.label) || '';
    return { url: u.href, language, label: cleanText(details.label, MAX_LABEL) || UNKNOWN_LABEL, format };
  }

  /** Adds an entry to a tab's list: de-duplicated by address, at most MAX_ENTRIES (the oldest go first). True when the list changed. */
  function add(list, entry) {
    if (!entry) return false;
    const at = list.findIndex(e => e.url === entry.url);
    if (at >= 0) {
      const old = list[at];
      if (!old.language && entry.language) { old.language = entry.language; if (old.label === UNKNOWN_LABEL) old.label = entry.label; return true; }
      return false;
    }
    list.push({ ...entry }); while (list.length > MAX_ENTRIES) list.shift();
    return true;
  }

  /** The list as it travels with a stream capture: validated again, bounded, plain objects only. */
  function forMessage(list) {
    const out = [];
    // The whole native message is limited to 64 KiB (a cookie header can take half of it): the newest files that fit in MAX_BYTES travel, the rest are left out.
    let bytes = 0;
    for (const e of (Array.isArray(list) ? list : []).slice().reverse()) {
      if (out.length >= MAX_ENTRIES) break;
      if (!e || typeof e !== 'object' || typeof e.url !== 'string' || e.url.length > MAX_URL || !parse(e.url) || !FORMATS.includes(e.format)) continue;
      const language = typeof e.language === 'string' && /^[A-Za-z0-9][A-Za-z0-9_.-]{0,39}$/.test(e.language) ? e.language : '';
      if (out.some(o => o.url === e.url)) continue;
      const entry = { url: e.url, language, label: cleanText(e.label, MAX_LABEL) || UNKNOWN_LABEL, format: e.format };
      bytes += JSON.stringify(entry).length + 1; if (bytes > MAX_BYTES) break;
      out.push(entry);
    }
    return out.reverse();
  }

  /** What the ribbon hint needs: distinct language codes (in the order seen) and whether any file had no known language. */
  function summary(list) {
    const languages = [];
    let unknown = false;
    for (const e of list || []) { if (e.language) { if (!languages.includes(e.language)) languages.push(e.language); } else unknown = true; }
    return { count: (list || []).length, languages, unknown };
  }

  scope.NeoSubs = { MAX_BYTES, MAX_ENTRIES, MAX_URL, MAX_LABEL, MAX_LANG, FORMATS, UNKNOWN_LABEL, formatOf, guessLanguage, entryFor, add, forMessage, summary };
  if (typeof module !== 'undefined') module.exports = scope.NeoSubs;
})(globalThis);
