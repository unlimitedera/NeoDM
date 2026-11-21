// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
// Converts the DB-IP "IP to Country Lite" CSV (CC BY 4.0, https://db-ip.com) into the compact range table NeoDM bundles.
// usage: node tools/build-ip-country.cjs dbip-country-lite-YYYY-MM.csv src/NDM.Core/Data/ip-country.bin
// Layout: "NDMC", u8 version=2, u8 v6shift, u8 country count, count x 2 letters, u32 n4, u32 n6 (little endian), then n4 varint deltas of the IPv4 starts,
// n4 country indexes (u8, 0 = unknown), n6 varint deltas of (first 64 bits of the IPv6 start >> v6shift), n6 country indexes.
// A range lasts until the next start.
const fs = require('fs');
const [,, input, output] = process.argv;
if (!input || !output) { console.error('usage: node build-ip-country.cjs <csv> <out.bin>'); process.exit(2); }
const code = cc => cc.length === 2 && /^[A-Z]{2}$/.test(cc) && cc !== 'ZZ' ? (cc.charCodeAt(0) << 8) | cc.charCodeAt(1) : 0;
const v4 = [], v6 = [];
const ip4 = s => s.split('.').reduce((a, b) => a * 256 + Number(b), 0);
function ip6hi(s) {
  let [h, t] = s.split('::'); const head = h ? h.split(':') : [], tail = t ? t.split(':') : [];
  const parts = t === undefined ? head : [...head, ...Array(8 - head.length - tail.length).fill('0'), ...tail];
  let v = 0n; for (let i = 0; i < 4; i++) v = (v << 16n) | BigInt(parseInt(parts[i] || '0', 16)); return v;
}
function push(list, start, end, c, step, max) {
  const last = list[list.length - 1];
  if (last && last.end + step < start && last.c !== 0) list.push({ start: last.end + step, end: start - step, c: 0 });
  const l2 = list[list.length - 1];
  if (l2 && l2.c === c && l2.end + step === start) l2.end = end; else list.push({ start, end, c });
}
for (const line of fs.readFileSync(input, 'utf8').split(/\r?\n/)) {
  if (!line) continue; const [a, b, cc] = line.split(',');
  if (a.includes(':')) { const s = ip6hi(a), e = ip6hi(b); push(v6, s, e, code(cc), 1n, 0); }
  else push(v4, ip4(a), ip4(b), code(cc), 1, 0);
}
const countries = [...new Set([...v4, ...v6].map(r => r.c).filter(c => c))].sort((a, b) => a - b);
if (countries.length > 254) throw new Error('too many countries');
const index = new Map(countries.map((c, i) => [c, i + 1]));
const shift = v6.every(r => (r.start & 0xFFFFn) === 0n) ? 16n : 0n;
const bytes = [];
const varint = v => { v = BigInt(v); while (v >= 0x80n) { bytes.push(Number(v & 0x7Fn) | 0x80); v >>= 7n; } bytes.push(Number(v)); };
const u32 = v => { bytes.push(v & 255, (v >>> 8) & 255, (v >>> 16) & 255, (v >>> 24) & 255); };
bytes.push(...Buffer.from('NDMC'), 2, Number(shift), countries.length);
for (const c of countries) bytes.push(c >> 8, c & 255);
u32(v4.length); u32(v6.length);
let prev = 0; for (const r of v4) { varint(r.start - prev); prev = r.start; }
for (const r of v4) bytes.push(index.get(r.c) || 0);
let prev6 = 0n; for (const r of v6) { const v = r.start >> shift; varint(v - prev6); prev6 = v; }
for (const r of v6) bytes.push(index.get(r.c) || 0);
fs.mkdirSync(require('path').dirname(output), { recursive: true }); fs.writeFileSync(output, Buffer.from(bytes).map(b => b ^ 0x5A)); // stored XOR 0x5A; IpCountry.LoadBundled unmasks it
console.log(`v4 ranges ${v4.length}, v6 ranges ${v6.length}, countries ${countries.length}, shift ${shift}, ${bytes.length} bytes`);
