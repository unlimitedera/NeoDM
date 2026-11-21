// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
'use strict';
// Minimal Marionette client (Firefox remote control over TCP) for the Firefox smoke tests: no packages needed.
const net = require('node:net');
class Marionette {
  constructor(port) { this.port = port; this.id = 0; this.pending = new Map(); this.buf = ''; }
  async connect(tries = 150) {
    for (let i = 0; i < tries; i++) {
      try { await new Promise((resolve, reject) => { const s = net.connect(this.port, '127.0.0.1'); s.once('error', reject); s.once('connect', () => { this.sock = s; s.removeListener('error', reject); resolve(); }); }); break; }
      catch (e) { if (i === tries - 1) throw e; await new Promise(r => setTimeout(r, 200)); }
    }
    this.sock.setEncoding('utf8'); this.sock.on('data', d => this.onData(d)); this.sock.on('error', () => { });
    await new Promise(r => { this.hello = r; });
  }
  onData(d) {
    this.buf += d;
    for (;;) {
      const colon = this.buf.indexOf(':'); if (colon < 0) return;
      const len = parseInt(this.buf.slice(0, colon), 10), body = this.buf.slice(colon + 1);
      if (Buffer.byteLength(body) < len) return;
      const text = Buffer.from(body).subarray(0, len).toString('utf8'); this.buf = Buffer.from(body).subarray(len).toString('utf8');
      const m = JSON.parse(text);
      if (!Array.isArray(m)) { if (this.hello) this.hello(m); continue; }
      const p = this.pending.get(m[1]); if (!p) continue; this.pending.delete(m[1]);
      m[2] ? p.reject(new Error((m[2].error || 'error') + ': ' + (m[2].message || ''))) : p.resolve(m[3]);
    }
  }
  send(name, params = {}, timeout = 30000) {
    return new Promise((resolve, reject) => {
      const id = ++this.id, text = JSON.stringify([0, id, name, params]);
      const timer = setTimeout(() => { this.pending.delete(id); reject(new Error('Marionette timeout: ' + name)); }, timeout);
      this.pending.set(id, { resolve: v => { clearTimeout(timer); resolve(v); }, reject: e => { clearTimeout(timer); reject(e); } });
      this.sock.write(Buffer.byteLength(text) + ':' + text);
    });
  }
  async exec(script, args = [], sandbox = 'default') { return (await this.send('WebDriver:ExecuteScript', { script, args, sandbox })).value; }
  async execAsync(script, args = [], sandbox = 'default') { return (await this.send('WebDriver:ExecuteAsyncScript', { script, args, sandbox }, 40000)).value; }
  close() { try { this.sock.destroy(); } catch { } }
}
module.exports = { Marionette };
