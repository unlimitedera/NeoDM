/* Original NDM bridge; no third-party browser integration code. */
(function (scope) {
  const http = value => { try { const u = new URL(value); return ['http:', 'https:'].includes(u.protocol) && !u.username && !u.password; } catch { return false; } };
  const ack = reply => reply?.ok === true && typeof reply.id === 'string' && reply.id.trim().length > 0;
  const quiet = async fn => { try { return await fn(); } catch { return undefined; } };
  /** Last path component of what the browser chose (Windows or POSIX path), safe for the host's strict file-name check; '' when unusable. */
  function baseName(value) {
    let n = String(value || '').split(/[\\/]/).pop().replace(/[\u0000-\u001f\u007f<>:"|?*]/g, '_').replace(/\.(crdownload|part|download)$/i, '').trim();
    if (n === '.' || n === '..') return '';
    return Array.from(n).slice(0, 255).join('');
  }
  /** Content type of the download if it is a plain type/subtype of at most 100 characters; '' otherwise. */
  function mimeOf(value) {
    const t = String(value || '').split(';')[0].trim();
    return t.length <= 100 && /^[A-Za-z0-9][A-Za-z0-9!#$&^_.+-]*\/[A-Za-z0-9][A-Za-z0-9!#$&^_.+-]*$/.test(t) ? t : '';
  }
  /** Size in bytes the browser itself knows for the download (totalBytes, else fileSize), as an integer between 1 and 2^50; 0 when unknown. */
  function sizeOf(...records) {
    for (const key of ['totalBytes', 'fileSize'])
      for (const r of records) { const n = Number(r?.[key]); if (Number.isInteger(n) && n > 0 && n <= 2 ** 50) return n; }
    return 0;
  }
  /** Stops the browser's own download and leaves nothing behind: no partial file, no finished file, no history entry. */
  async function discard(item, api) {
    await quiet(() => api.cancel(item.id));
    const current = (await quiet(() => api.search({ id: item.id })))?.[0];
    if (current && current.state === 'complete' && current.exists !== false) await quiet(() => api.removeFile(item.id));
    await quiet(() => api.erase({ id: item.id }));
    return current;
  }
  /**
   * Hands a browser download over to NeoDM. The browser download is cancelled and erased FIRST (before NeoDM shows its dialog),
   * so it can never run next to the NeoDM transfer. Outcomes:
   * The request carries the final URL (after redirects), the page that started the download as referer, and noProbe when the chain began with a form POST
   * (the final address may be single use, so the app must not request it once more just to read the name).
   *   ack            -> { ok: true, id }                     NeoDM owns the download
   *   user cancelled -> { ok: false, cancelled: true }       discarded, by the user's choice
   *   hard failure   -> { ok: false, restarted: true, ... }  app unreachable / error: the download is restarted in the browser
   *   handed back    -> { ok: false, restarted: true, handedBack: true, ... }  the user left this download to the browser: restarted in the browser exactly once, neutral text
   * The request also carries cookie (the Cookie header the browser sent to that address, when the caller passes one) so the site accepts the app's request like the browser's.
   * The request also carries size (what the browser reported) when known, so the app can recognise a web page sent in place of the file.
   */
  async function transfer(item, api, send, knownGet, hint, opts = {}) {
    if (!knownGet || !http(item.url) || !http(item.finalUrl || item.url) || item.state !== 'in_progress' || item.paused || item.byExtensionId || item.incognito) return { skipped: true };
    const url = item.finalUrl || item.url;
    const current = await discard(item, api);
    try { opts?.afterDiscard?.(); } catch {}
    // (a) The name the browser itself decided (item, onDeterminingFilename hint, or the record seen while cancelling) and its content type; the app resolves the rest.
    const fileName = baseName(item.filename || hint?.filename || current?.filename), mime = mimeOf(item.mime || hint?.mime || current?.mime), size = sizeOf(item, current);
    let failure;
    try {
      const reply = await send({ url, fileName, referer: http(item.referrer) ? item.referrer : http(opts?.referer) ? opts.referer : '', method: 'GET', ...(mime ? { mime } : {}), ...(size ? { size } : {}), ...(opts?.noProbe ? { noProbe: true } : {}), ...(typeof opts?.cookie === 'string' && opts.cookie ? { cookie: opts.cookie } : {}) });
      if (ack(reply)) return reply;
      failure = new Error(NeoI18n.t('err_no_durable_ack'));
    } catch (e) {
      if (e?.cancelled === true) return { ok: false, cancelled: true, error: NeoI18n.t('err_discarded') };
      failure = e;
    }
    const handedBack = failure?.handBack === true;
    try { await api.download({ url, conflictAction: 'uniquify' }); }
    catch { return { ok: false, error: NeoI18n.t('err_cannot_restart', url) }; }
    if (handedBack) return { ok: false, restarted: true, handedBack: true, error: NeoI18n.t('status_handed_back') };
    return { ok: false, restarted: true, error: NeoI18n.t('err_restarted', failure?.message || NeoI18n.t('err_unknown')) };
  }
  scope.NDMBridge = { http, ack, transfer, discard, baseName, mimeOf, sizeOf };
  if (typeof module !== 'undefined') module.exports = scope.NDMBridge;
})(globalThis);
