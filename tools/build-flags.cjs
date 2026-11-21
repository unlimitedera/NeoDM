// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
// Packs the two-letter country flags of the MIT-licensed "flag-icons" set (https://github.com/lipis/flag-icons) into one resource file of small PNG pictures.
// usage: node tools/build-flags.cjs <flag-icons/flags/4x3 folder> src/NDM.App/Assets/flags.pak   (needs: npm i @resvg/resvg-js, run from a folder that has it)
// Layout (little endian): "NDMF", u8 version=1, u16 count, count x (2 letters, u32 offset from file start, u32 length), then the PNG files.
const fs = require('fs'), path = require('path');
const { Resvg } = require('@resvg/resvg-js');
const [,, dir, output] = process.argv;
if (!dir || !output) { console.error('usage: node build-flags.cjs <4x3 folder> <flags.pak>'); process.exit(2); }
const files = fs.readdirSync(dir).filter(f => /^[a-z]{2}\.svg$/.test(f)).sort();
const pngs = files.map(f => new Resvg(fs.readFileSync(path.join(dir, f)), { fitTo: { mode: 'width', value: 48 } }).render().asPng());
const head = 7 + files.length * 10; let offset = head; const out = [Buffer.from('NDMF'), Buffer.from([1])];
const count = Buffer.alloc(2); count.writeUInt16LE(files.length); out.push(count);
files.forEach((f, i) => { const e = Buffer.alloc(10); e.write(f.slice(0, 2).toUpperCase(), 0, 'latin1'); e.writeUInt32LE(offset, 2); e.writeUInt32LE(pngs[i].length, 6); offset += pngs[i].length; out.push(e); });
out.push(...pngs); fs.mkdirSync(path.dirname(output), { recursive: true }); fs.writeFileSync(output, Buffer.concat(out));
console.log(`${files.length} flags, ${offset} bytes`);
