// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
'use strict';
// Deterministic CRX3 builder. No browser involved. Original code, uses only node:crypto and node:zlib.
const crypto = require('node:crypto');
const zlib = require('node:zlib');

const CRC_TABLE = (() => { const t = new Uint32Array(256); for (let n = 0; n < 256; n++) { let c = n; for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1; t[n] = c >>> 0; } return t; })();
function crc32(buf) { let c = 0xffffffff; for (let i = 0; i < buf.length; i++) c = CRC_TABLE[(c ^ buf[i]) & 255] ^ (c >>> 8); return (c ^ 0xffffffff) >>> 0; }

/** files: [{name, data:Buffer}] -> deterministic ZIP (sorted, fixed 1980-01-01 timestamps, deflate). */
function buildZip(files) {
  const sorted = [...files].sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0));
  const locals = [], centrals = []; let offset = 0;
  for (const f of sorted) {
    const name = Buffer.from(f.name, 'utf8'), raw = f.data, comp = zlib.deflateRawSync(raw, { level: 9 }), crc = crc32(raw);
    const lh = Buffer.alloc(30);
    lh.writeUInt32LE(0x04034b50, 0); lh.writeUInt16LE(20, 4); lh.writeUInt16LE(0x0800, 6); lh.writeUInt16LE(8, 8);
    lh.writeUInt16LE(0, 10); lh.writeUInt16LE(0x21, 12); lh.writeUInt32LE(crc, 14); lh.writeUInt32LE(comp.length, 18);
    lh.writeUInt32LE(raw.length, 22); lh.writeUInt16LE(name.length, 26); lh.writeUInt16LE(0, 28);
    const ch = Buffer.alloc(46);
    ch.writeUInt32LE(0x02014b50, 0); ch.writeUInt16LE(20, 4); ch.writeUInt16LE(20, 6); ch.writeUInt16LE(0x0800, 8); ch.writeUInt16LE(8, 10);
    ch.writeUInt16LE(0, 12); ch.writeUInt16LE(0x21, 14); ch.writeUInt32LE(crc, 16); ch.writeUInt32LE(comp.length, 20);
    ch.writeUInt32LE(raw.length, 24); ch.writeUInt16LE(name.length, 28); ch.writeUInt32LE((0o100644 << 16) >>> 0, 38); ch.writeUInt32LE(offset, 42);
    locals.push(lh, name, comp); centrals.push(ch, name); offset += 30 + name.length + comp.length;
  }
  const cd = Buffer.concat(centrals), eocd = Buffer.alloc(22);
  eocd.writeUInt32LE(0x06054b50, 0); eocd.writeUInt16LE(sorted.length, 8); eocd.writeUInt16LE(sorted.length, 10);
  eocd.writeUInt32LE(cd.length, 12); eocd.writeUInt32LE(offset, 16);
  return Buffer.concat([...locals, cd, eocd]);
}

const varint = n => { const out = []; while (n > 127) { out.push((n & 127) | 128); n = Math.floor(n / 128); } out.push(n); return Buffer.from(out); };
const field = (num, data) => Buffer.concat([varint(num * 8 + 2), varint(data.length), data]);

/** Extension ID: first 16 bytes of SHA-256(SPKI DER), each nibble mapped to a..p. */
function idFromPublicDer(der) {
  return [...crypto.createHash('sha256').update(der).digest().subarray(0, 16)].map(b => String.fromCharCode(97 + (b >> 4), 97 + (b & 15))).join('');
}
function publicDerFromPem(pem) {
  return crypto.createPublicKey(crypto.createPrivateKey(pem)).export({ type: 'spki', format: 'der' });
}

function buildCrx(zip, privatePem) {
  const priv = crypto.createPrivateKey(privatePem), der = publicDerFromPem(privatePem);
  const crxId = crypto.createHash('sha256').update(der).digest().subarray(0, 16);
  const signedData = field(1, crxId); // SignedData { bytes crx_id = 1 }
  const lenBuf = Buffer.alloc(4); lenBuf.writeUInt32LE(signedData.length);
  const signature = crypto.sign('sha256', Buffer.concat([Buffer.from('CRX3 SignedData\0', 'latin1'), lenBuf, signedData, zip]), priv);
  const proof = Buffer.concat([field(1, der), field(2, signature)]); // AsymmetricKeyProof { public_key=1, signature=2 }
  const header = Buffer.concat([field(2, proof), field(10000, signedData)]); // sha256_with_rsa=2, signed_header_data=10000
  const pre = Buffer.alloc(12); pre.write('Cr24', 0, 'latin1'); pre.writeUInt32LE(3, 4); pre.writeUInt32LE(header.length, 8);
  return Buffer.concat([pre, header, zip]);
}
module.exports = { crc32, buildZip, buildCrx, idFromPublicDer, publicDerFromPem };
