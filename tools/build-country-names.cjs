// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
// Builds the country-name table (10 app languages) from the MIT-licensed "i18n-iso-countries" package (https://github.com/michaelwittig/node-i18n-iso-countries).
// usage: node tools/build-country-names.cjs src/NDM.App/Assets/country-names.json   (run from a folder that has the package: npm i i18n-iso-countries)
const fs = require('fs'), c = require('i18n-iso-countries');
const map = { en: 'en', ar: 'ar', es: 'es', fr: 'fr', de: 'de', 'pt-BR': 'pt', ru: 'ru', tr: 'tr', 'zh-Hans': 'zh', ja: 'ja' };
const out = {};
for (const [app, lib] of Object.entries(map)) {
  c.registerLocale(require('i18n-iso-countries/langs/' + lib + '.json'));
  out[app] = Object.fromEntries(Object.entries(c.getNames(lib)).map(([k, v]) => [k, Array.isArray(v) ? v[0] : v]));
}
fs.writeFileSync(process.argv[2], JSON.stringify(out));
console.log(Object.keys(out).length + ' languages');
