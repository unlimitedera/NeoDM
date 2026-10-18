/* NeoDM media ribbon. Renders the tab's media list (kept by the service worker) in a closed shadow root. */
(() => {
  if (globalThis.__neodmRibbon) return;
  const t = (...a) => NeoI18n.t(...a);
  const MAX_DOM = 50, SCAN_MS = 400;
  let items = [], dismissedFor = '', enabled = true, host = null, root = null, timer = null, lastDom = '';
  const sending = new Set(), states = new Map(); // id -> {text, ok}

  const fmtSize = n => !n ? '' : n >= 1073741824 ? (n / 1073741824).toFixed(2) + ' GB' : n >= 1048576 ? (n / 1048576).toFixed(1) + ' MB' : Math.max(1, Math.round(n / 1024)) + ' KB';
  function label(i) {
    const kind = i.kind === 'gif' ? t('kind_gif') : i.kind === 'image' ? t('kind_image') : i.track === 'audio-only' ? t('kind_audio_only') : i.track === 'video-only' ? t('kind_video_only') : i.kind === 'audio' ? t('kind_audio') : t('kind_video');
    return [kind, i.quality, fmtSize(i.size)].filter(Boolean).join(' · ');
  }
  function build() {
    host = document.createElement('div'); host.setAttribute('data-neodm-ribbon', '');
    host.style.cssText = 'all:initial!important;position:fixed!important;' + (NeoI18n.isRtl() ? 'left' : 'right') + ':16px!important;bottom:16px!important;z-index:2147483647!important;';
    root = host.attachShadow({ mode: 'closed' });
    const style = document.createElement('style');
    style.textContent = `.bar{font:13px/1.3 system-ui,Segoe UI,sans-serif;color:#f4f7fa;background:#17212e;border:1px solid #2f4258;border-radius:10px;box-shadow:0 6px 20px rgba(0,0,0,.35);width:340px;overflow:hidden}
.head{display:flex;align-items:center;gap:8px;padding:6px;padding-inline-start:10px;border-bottom:1px solid #263649;font-weight:600;font-size:12px;letter-spacing:.4px;color:#39b995}
.head span{flex:1}.x{all:unset;cursor:pointer;padding:2px 8px;font-size:16px;color:#c7d2de;border-radius:6px}.x:hover,.x:focus-visible{background:#263649;color:#fff}
.rows{max-height:210px;overflow:auto}.row{display:flex;align-items:center;gap:8px;padding:8px 10px}.row+.row{border-top:1px solid #1f2d3e}
.info{flex:1;min-width:0}.t{white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.s{font-size:11px;opacity:.7;margin-top:2px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.s.err{color:#ff9d9d;opacity:1}.s.ok{color:#7be0b9;opacity:1}
.dl{all:unset;cursor:pointer;white-space:nowrap;background:#2da57d;color:#fff;font-weight:600;font-size:12px;padding:6px 10px;border-radius:6px}.dl:hover,.dl:focus-visible{background:#34bd90}.dl[disabled]{opacity:.55;cursor:default}.sub .dl{background:#3a4c61}`;
    const bar = document.createElement('div'); bar.className = 'bar'; bar.dir = NeoI18n.isRtl() ? 'rtl' : 'ltr'; bar.setAttribute('role', 'region'); bar.setAttribute('aria-label', t('ribbon_label'));
    const head = document.createElement('div'); head.className = 'head';
    const title = document.createElement('span'); title.textContent = 'NeoDM';
    const x = document.createElement('button'); x.className = 'x'; x.type = 'button'; x.textContent = '\u00d7'; x.title = t('ribbon_dismiss'); x.setAttribute('aria-label', t('ribbon_dismiss'));
    x.addEventListener('click', () => { dismissedFor = items.map(i => i.id).join('\n'); render(); });
    head.append(title, x);
    const rows = document.createElement('div'); rows.className = 'rows'; rows.setAttribute('aria-live', 'polite');
    bar.append(head, rows); root.append(style, bar);
    document.documentElement.append(host);
  }
  function render() {
    const visible = enabled && items.length && dismissedFor !== items.map(i => i.id).join('\n');
    if (!visible) { host?.remove(); host = null; root = null; return; }
    if (!host) build(); else if (!host.isConnected) document.documentElement.append(host);
    const rows = root.querySelector('.rows'); rows.textContent = '';
    const ordered = [...items].sort((a, b) => (b.complete - a.complete));
    for (const i of ordered) {
      const row = document.createElement('div'); row.className = 'row' + (i.complete ? '' : ' sub'); row.dataset.id = i.id;
      const info = document.createElement('div'); info.className = 'info';
      const name = document.createElement('div'); name.className = 't'; name.textContent = label(i); name.title = i.url;
      const st = states.get(i.id), s = document.createElement('div'); s.className = 's' + (st ? (st.ok ? ' ok' : ' err') : ''); s.textContent = st ? st.text : (i.complete ? t('ribbon_complete') : t('ribbon_partial'));
      info.append(name, s);
      const b = document.createElement('button'); b.className = 'dl'; b.type = 'button';
      b.textContent = sending.has(i.id) ? t('ribbon_waiting') : st?.ok ? t('ribbon_sent') : t('ribbon_download');
      b.disabled = sending.has(i.id) || !!st?.ok;
      b.addEventListener('click', () => download(i));
      row.append(info, b); rows.append(row);
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
  function setItems(next) { items = Array.isArray(next) ? next : []; if (!items.length) dismissedFor = ''; render(); }

  // Direct <video>/<audio> sources (blob: and encrypted media are ignored; the network sniffer covers streamed media).
  function scanDom() {
    timer = null;
    const sources = [];
    for (const m of Array.from(document.querySelectorAll('video,audio')).slice(0, MAX_DOM)) {
      if (m.mediaKeys) continue;
      const urls = [m.currentSrc, m.getAttribute('src') && m.src, ...Array.from(m.querySelectorAll('source[src]')).map(s => s.src)];
      const url = urls.find(u => u && /^https?:/i.test(u));
      if (url) sources.push({ url, tag: m.tagName.toLowerCase() });
      else if (/^blob:/i.test(m.currentSrc || '')) sources.push({ url: 'blob:', tag: m.tagName.toLowerCase() }); // tells the worker this page streams through MediaSource
    }
    const sig = JSON.stringify(sources);
    if (sig === lastDom) return; lastDom = sig;
    if (sources.length) chrome.runtime.sendMessage({ action: 'media-dom', sources }).catch(() => {});
  }
  const schedule = () => { if (timer === null) timer = setTimeout(scanDom, SCAN_MS); };
  const obs = new MutationObserver(schedule);
  obs.observe(document.documentElement, { subtree: true, childList: true, attributes: true, attributeFilter: ['src'] });
  const events = ['loadedmetadata', 'loadstart', 'play'];
  for (const e of events) document.addEventListener(e, schedule, { capture: true, passive: true });

  chrome.runtime.onMessage.addListener(m => { if (m && m.type === 'neodm-media') setItems(m.items); });
  chrome.storage.onChanged.addListener((c, area) => { if (area === 'local' && c.ribbonEnabled) { enabled = c.ribbonEnabled.newValue !== false; render(); } });
  chrome.storage.local.get('ribbonEnabled').then(s => { enabled = s.ribbonEnabled !== false; return chrome.runtime.sendMessage({ action: 'media-list' }); }).then(r => setItems(r?.items)).catch(() => {});
  scanDom();
  globalThis.__neodmRibbon = { refresh: scanDom, items: () => items.slice(), dispose() { obs.disconnect(); clearTimeout(timer); host?.remove(); delete globalThis.__neodmRibbon; } };
})();
