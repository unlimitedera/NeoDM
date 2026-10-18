#!/usr/bin/env node
'use strict';
// Validates locale files against the English reference.
//   node tools/check-locales.cjs                 every file in src/NDM.App/Locales and extension/_locales
//   node tools/check-locales.cjs path/to/fr.json  one or more files (app locale or extension messages.json)
// Exit code 0 = all valid, 1 = problems (listed), 2 = usage or unreadable reference.
const fs = require('node:fs'), path = require('node:path');
const root = path.resolve(__dirname, '..');
const appDir = path.join(root, 'src', 'NDM.App', 'Locales'), extDir = path.join(root, 'extension', '_locales');

// Plural categories each language uses (CLDR, whole numbers). Must match PluralRules.cs.
const PLURALS = { ar: ['zero', 'one', 'two', 'few', 'many', 'other'], ru: ['one', 'few', 'many', 'other'], pl: ['one', 'few', 'many', 'other'], he: ['one', 'two', 'other'], zh: ['other'], ja: ['other'], ko: ['other'], id: ['other'] };
const categories = tag => PLURALS[tag.split(/[-_]/)[0].toLowerCase()] || ['one', 'other'];
const SUFFIX = /^(.+)_(zero|one|two|few|many|other)$/;
const group = key => { const m = SUFFIX.exec(key); return m ? m[1] : key; };
const placeholders = text => [...new Set([...text.matchAll(/\{(\d+)(?::[^}]*)?\}/g)].map(m => m[1]))].sort();
const same = (a, b) => a.length === b.length && a.every((x, i) => x === b[i]);
const readJson = file => { try { return JSON.parse(fs.readFileSync(file, 'utf8').replace(/^\uFEFF/, '')); } catch (e) { throw new Error(`${file}: ${e.message}`); } };

function checkApp(file, en) {
  const tag = path.basename(file, '.json'), data = readJson(file), problems = [];
  if (typeof data !== 'object' || Array.isArray(data)) return [`${file}: must be a flat JSON object`];
  const enGroups = new Map();
  for (const key of Object.keys(en)) { const g = group(key); if (!enGroups.has(g)) enGroups.set(g, []); enGroups.get(g).push(key); }
  const used = categories(tag);
  for (const [key, value] of Object.entries(data)) {
    if (typeof value !== 'string') { problems.push(`value of ${key} is not a string`); continue; }
    const known = key in en || (SUFFIX.test(key) && enGroups.has(group(key)) && enGroups.get(group(key)).some(k => SUFFIX.test(k)));
    if (!known) { problems.push(`extra key ${key}`); continue; }
    if (SUFFIX.test(key) && enGroups.has(group(key)) && !used.includes(SUFFIX.exec(key)[2])) problems.push(`plural category of ${key} is not used by ${tag} (${used.join(', ')})`);
    if (/[\u2066-\u2069]/.test(value)) problems.push(`isolate character in ${key} (use U+200E: WPF ignores isolates)`);
    if (value.trim() === '') problems.push(`empty value ${key}`);
    else if (key in en && value !== value.trim() && en[key] === en[key].trim()) problems.push(`leading or trailing whitespace in ${key}`);
  }
  for (const [g, keys] of enGroups) {
    const isPlural = keys.some(k => SUFFIX.test(k));
    if (!isPlural) {
      const key = keys[0];
      if (!(key in data)) { problems.push(`missing key ${key}`); continue; }
      const v = data[key];
      if (typeof v !== 'string') continue;
      if (!same(placeholders(v), placeholders(en[key]))) problems.push(`placeholders differ in ${key}: [${placeholders(v)}] vs en [${placeholders(en[key])}]`);
      if (en[key].includes('NeoDM') && !v.includes('NeoDM')) problems.push(`brand name NeoDM lost in ${key}`);
      if ((en[key].match(/\r?\n/g) || []).length !== (v.match(/\r?\n/g) || []).length) problems.push(`line break count differs in ${key}`);
    } else {
      const allowed = [...new Set(keys.flatMap(k => placeholders(en[k])))].sort();
      for (const cat of used) {
        const key = `${g}_${cat}`;
        if (!(key in data)) { problems.push(`missing plural form ${key}`); continue; }
        const p = placeholders(String(data[key]));
        if (!p.every(x => allowed.includes(x))) problems.push(`unknown placeholder in ${key}`);
        if ((cat === 'other' || cat === 'many') && !same(p, allowed)) problems.push(`placeholders differ in ${key}`);
      }
    }
  }
  return problems.map(p => `${path.relative(root, file)}: ${p}`);
}

function checkExtension(file, en) {
  const data = readJson(file), problems = [];
  for (const key of Object.keys(en)) if (!(key in data)) problems.push(`missing message ${key}`);
  for (const [key, entry] of Object.entries(data)) {
    if (!(key in en)) { problems.push(`extra message ${key}`); continue; }
    if (!entry || typeof entry.message !== 'string' || entry.message.trim() === '') { problems.push(`empty message ${key}`); continue; }
    const names = o => Object.keys(o.placeholders || {}).sort(), contents = o => Object.values(o.placeholders || {}).map(p => p.content).sort();
    if (!same(names(entry), names(en[key])) || !same(contents(entry), contents(en[key]))) problems.push(`placeholders differ in ${key}`);
    const used = s => [...new Set([...s.matchAll(/\$([A-Za-z0-9_@]+)\$/g)].map(m => m[1].toLowerCase()))].sort();
    if (!same(used(entry.message), used(en[key].message))) problems.push(`placeholder tokens differ in ${key}`);
    if (en[key].message.includes('NeoDM') && !entry.message.includes('NeoDM')) problems.push(`brand name NeoDM lost in ${key}`);
    if (entry.message !== entry.message.trim() && en[key].message === en[key].message.trim()) problems.push(`leading or trailing whitespace in ${key}`);
  }
  return problems.map(p => `${path.relative(root, file)}: ${p}`);
}

function main(argv) {
  const files = argv.length ? argv.map(f => path.resolve(f)) : [
    ...fs.readdirSync(appDir).filter(f => f.endsWith('.json')).map(f => path.join(appDir, f)),
    ...fs.readdirSync(extDir).map(d => path.join(extDir, d, 'messages.json')).filter(f => fs.existsSync(f)),
  ];
  let enApp, enExt;
  try { enApp = readJson(path.join(appDir, 'en.json')); enExt = readJson(path.join(extDir, 'en', 'messages.json')); } catch (e) { console.error('Cannot read the English reference: ' + e.message); return 2; }
  const all = [];
  let checked = 0;
  for (const file of files) {
    const isExt = path.basename(file) === 'messages.json';
    if (!isExt && path.basename(file) === 'en.json' || isExt && path.basename(path.dirname(file)) === 'en') { checked++; continue; }
    try { all.push(...(isExt ? checkExtension(file, enExt) : checkApp(file, enApp))); } catch (e) { all.push(e.message); }
    checked++;
  }
  if (all.length) { console.error(all.join('\n')); console.error(`\n${all.length} problem(s) in ${checked} file(s).`); return 1; }
  console.log(`check-locales: ${checked} file(s) valid (${Object.keys(enApp).length} app keys, ${Object.keys(enExt).length} extension messages in English).`);
  return 0;
}
if (require.main === module) process.exit(main(process.argv.slice(2)));
module.exports = { checkApp, checkExtension, categories };
