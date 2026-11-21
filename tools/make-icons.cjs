// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
'use strict';
// Draws the NeoDM icon (rounded navy->teal tile + white down-arrow into a tray) at 16/48/128 px with 4x supersampling. Pure node.
const fs = require('node:fs'), path = require('node:path'), zlib = require('node:zlib');
const { crc32 } = require('./crx3.cjs');
function sdfBox(px, py, x0, y0, x1, y1, r) {
  const cx = (x0 + x1) / 2, cy = (y0 + y1) / 2, hx = (x1 - x0) / 2 - r, hy = (y1 - y0) / 2 - r;
  const dx = Math.abs(px - cx) - hx, dy = Math.abs(py - cy) - hy;
  return Math.hypot(Math.max(dx, 0), Math.max(dy, 0)) + Math.min(Math.max(dx, dy), 0) - r;
}
function shade(u, v) { // -> [r,g,b,a]
  if (sdfBox(u, v, 0.03, 0.03, 0.97, 0.97, 0.22) > 0) return [0, 0, 0, 0];
  const navy = [23, 33, 46], teal = [45, 165, 125], t = Math.min(1, Math.max(0, v)), bg = navy.map((n, i) => n * (1 - t) + teal[i] * t);
  const shaft = u > 0.42 && u < 0.58 && v > 0.16 && v < 0.5;
  const head = v >= 0.44 && v < 0.7 && Math.abs(u - 0.5) < (0.7 - v) * 1.0;
  const tray = sdfBox(u, v, 0.2, 0.74, 0.8, 0.83, 0.04) < 0;
  return shaft || head || tray ? [255, 255, 255, 255] : [...bg, 255];
}
function png(size) {
  const S = 4, rows = [];
  for (let y = 0; y < size; y++) {
    const row = Buffer.alloc(1 + size * 4);
    for (let x = 0; x < size; x++) {
      let r = 0, g = 0, b = 0, a = 0;
      for (let sy = 0; sy < S; sy++) for (let sx = 0; sx < S; sx++) { const p = shade((x + (sx + .5) / S) / size, (y + (sy + .5) / S) / size); r += p[0] * p[3]; g += p[1] * p[3]; b += p[2] * p[3]; a += p[3]; }
      const o = 1 + x * 4; if (a) { row[o] = Math.round(r / a); row[o + 1] = Math.round(g / a); row[o + 2] = Math.round(b / a); } row[o + 3] = Math.round(a / (S * S));
    }
    rows.push(row);
  }
  const chunk = (type, data) => { const len = Buffer.alloc(4); len.writeUInt32BE(data.length); const td = Buffer.concat([Buffer.from(type, 'latin1'), data]); const c = Buffer.alloc(4); c.writeUInt32BE(crc32(td)); return Buffer.concat([len, td, c]); };
  const ihdr = Buffer.alloc(13); ihdr.writeUInt32BE(size, 0); ihdr.writeUInt32BE(size, 4); ihdr[8] = 8; ihdr[9] = 6;
  return Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk('IHDR', ihdr), chunk('IDAT', zlib.deflateSync(Buffer.concat(rows), { level: 9 })), chunk('IEND', Buffer.alloc(0))]);
}
const dir = path.join(__dirname, '..', 'extension', 'icons'); fs.mkdirSync(dir, { recursive: true });
for (const s of [16, 48, 128]) fs.writeFileSync(path.join(dir, s + '.png'), png(s));
console.log('icons written');
