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
   */
  async function transfer(item, api, send, knownGet, hint, opts = {}) {
    if (!knownGet || !http(item.url) || !http(item.finalUrl || item.url) || item.state !== 'in_progress' || item.paused || item.byExtensionId || item.incognito) return { skipped: true };
    const url = item.finalUrl || item.url;
    const current = await discard(item, api);
    // (a) The name the browser itself decided (item, onDeterminingFilename hint, or the record seen while cancelling) and its content type; the app resolves the rest.
    const fileName = baseName(item.filename || hint?.filename || current?.filename), mime = mimeOf(item.mime || hint?.mime || current?.mime);
    let failure;
    try {
      const reply = await send({ url, fileName, referer: http(item.referrer) ? item.referrer : http(opts?.referer) ? opts.referer : '', method: 'GET', ...(mime ? { mime } : {}), ...(opts?.noProbe ? { noProbe: true } : {}) });
      if (ack(reply)) return reply;
      failure = new Error('No durable acceptance acknowledgement');
    } catch (e) {
      if (e?.cancelled === true) return { ok: false, cancelled: true, error: 'Discarded: you cancelled the download in NeoDM.' };
      failure = e;
    }
    try { await api.download({ url, conflictAction: 'uniquify' }); }
    catch { return { ok: false, error: 'NeoDM could not take the download and the browser could not restart it: ' + url }; }
    return { ok: false, restarted: true, error: 'NeoDM was unavailable (' + (failure?.message || 'error') + '); the download was restarted in the browser.' };
  }
  scope.NDMBridge = { http, ack, transfer, discard, baseName, mimeOf };
  if (typeof module !== 'undefined') module.exports = scope.NDMBridge;
})(globalThis);
