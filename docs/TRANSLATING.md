# Translating NeoDM

NeoDM ships with English, Arabic, Spanish, French, German, Brazilian Portuguese, Russian, Turkish, Simplified Chinese and Japanese (all except English were written with AI help and are not native-reviewed). Other languages are added by dropping one JSON file into the repository. No C# or XAML is touched, and a missing or incomplete file can never break the app.

## How it works

- All text of the app lives in `src/NDM.App/Locales/<tag>.json`, embedded into the program at build time. `en.json` is the reference.
- A file is a flat JSON object: `"main.menu.tasks": "_Tasks"`. Keys are dotted and grouped by window (`main.*`, `options.*`, `progress.*`, `tray.*`, `error.*`, ...).
- XAML uses `{loc:T key}`, code uses `Loc.T("key", args)` and `Loc.Plural("key", n)`. Every text in the app updates at once when the language changes. There is no restart.
- A language is listed in Options > General > Language only when its file exists. Adding the file is all it takes to make it appear.
- A key missing from a language shows the English text for that key. A key that exists nowhere shows its own name (a bug that the tests catch).
- Tags: `en, ar, es, fr, de, pt-BR, ru, zh-Hans, ja, tr, hi, it, ko, id, nl, pl, fa`. Other tags need an entry in `LanguageCatalog.cs` (tag, native name, English name, right-to-left flag, font chain, culture, plural rule).
- The pseudo-locale is for layout checks: start the app or a test with `NEODM_PSEUDO=1` and every text becomes accented, 30 percent longer and wrapped in brackets. `NEODM_LANG=ar` forces a language for one run (the tests use `NEODM_LANG=en`).

## Values

- Placeholders use composite format: `{0}`, `{1}`, `{2:N0}`. Keep the same set as English. The order may change.
- `\n` is a line break, `\"` is a quote (normal JSON escapes). Keep the same number of line breaks.
- Keep `NeoDM`, file names, URLs, versions and technical tokens (HTTP, SOCKS5, DHT, KiB/s) as they are.
- Access keys: English marks the key letter with `_` (`_Cancel`). In a language without a natural access letter (Arabic, Persian, Chinese, Japanese, Korean, Hindi) leave the underscore out.
- Numbers always use Western digits 0-9. The app formats sizes, speeds, dates and counts itself (`Loc.Size`, `Loc.Number`, `Loc.DateTime`) with the language's separators. Units stay Latin (B, KB, MB, GB).
- Right-to-left languages (Arabic, Persian, Hebrew): do not add direction marks around placeholders. The app wraps every inserted value (file name, path, number, size) in a left-to-right embedding (U+202A ... U+202C) with a left-to-right mark (U+200E) inside each end; WPF reverses `0 B` and mirrors the brackets of `(40%)` with either one alone. Inside a fixed sentence, put a left-to-right mark on both sides of a Latin token that carries punctuation (`HTTP(S)`, `KiB/s`, `#7C6CF2`). Do not use the isolate characters U+2066 to U+2069: WPF's text engine ignores them and leaves a gap, which is why the checker rejects them in locale files.
- Follow `docs/translation-glossary.md` for the standard terms.

## Plurals

A text that depends on a count has one key per plural form, suffixed with the category:

```json
"main.count_downloads_one": "1 download",
"main.count_downloads_other": "{0} downloads"
```

The categories a language needs (CLDR, whole numbers):

| Language | Categories |
| --- | --- |
| en, es, de, it, nl, tr, fr, pt-BR, hi, fa | one, other |
| ru, pl | one, few, many, other |
| ar | zero, one, two, few, many, other |
| zh-Hans, ja, ko, id | other |

For Arabic: zero is 0, one is 1, two is 2, few is 3-10 (and numbers ending in 03-10), many is 11-99 (and numbers ending in 11-99), other is 100-102 and so on. `tools/check-locales.cjs` fails when a category your language needs is missing, or when you add one it does not use. `{0}` is the count. The zero, one and two forms may leave `{0}` out ("one file", "two files").

## Adding a language

1. Copy `src/NDM.App/Locales/en.json` to `src/NDM.App/Locales/<tag>.json` (for example `fr.json`).
2. Translate the values. Do not change keys. Remove plural keys your language does not use, and add the ones it needs (see the table above).
3. Check it:
   ```
   node tools/check-locales.cjs src/NDM.App/Locales/fr.json
   ```
   It reports missing keys, extra keys, empty values, placeholder mismatches, wrong plural categories, a lost `NeoDM`, and stray whitespace.
4. Copy `extension/_locales/en/messages.json` to `extension/_locales/<tag>/messages.json` (Chrome uses `pt_BR` and `zh_CN` style folder names) and translate the `message` values. Keep the `placeholders` blocks unchanged. Run the same checker on it.
5. Run the localization tests (they build the app, check every locale, and open every window in the new language only when it is Arabic or the pseudo-locale):
   ```
   dotnet run --project src/NDM.App/Tests/Localization/NDM.App.LocalizationTests.csproj -c Release
   ```
   and `node --test tests/extension-i18n-tests.cjs`.
6. Start the app, pick the language in Options > General > Language and look at the main window, Options, the progress window and the capture dialog. A text that is cut off needs a shorter wording.
7. Flag the file honestly. In `LanguageCatalog.cs` the entry has `AiTranslated`. It is `true` for every language that no native speaker has reviewed. The Options page then shows a note with a link to this page. Set it to `false` only when a native speaker has read the whole file.
8. Open a pull request that contains the two JSON files (and the catalog line if you changed the flag). Say who translated and who reviewed.

## What stays English

- NeoDM core library messages that reach the app are mapped to translated text when they have a stable category: HTTP status codes, disk full, access denied, timeout, canceled, name resolution failure, certificate error, proxy sign-in failure, and the validation messages of NeoDM itself. Any other message from the system or from the download engine is shown in English as a second, muted line under the translated text.
- Windows system dialogs (file pickers, UAC) follow the Windows display language, not the NeoDM setting.
- The installer (MSI) and its shortcuts are English in this release.
- The browser extension follows the browser's UI language (`chrome.i18n`), not the NeoDM setting.
- Release notes (CHANGELOG) and the GitHub release text are English.

## Tests that guard this

- `Localization` suite: catalog and fallback, plural tables, formatting, completeness and placeholder parity of every locale file, a scanner that fails on a hard-coded string in XAML or in a C# call that shows text (`hardcoded-allowlist.txt` lists the exceptions with a reason), live switching, right-to-left flags, pseudo-locale and Arabic layout of every window at 100, 125 and 150 percent.
- `tools/check-locales.cjs` runs in `build.ps1` for every app and extension locale.
- `tests/extension-i18n-tests.cjs` covers the extension: same keys and placeholders in every language, no hard-coded English in the extension scripts, right-to-left in the popup for Arabic.
