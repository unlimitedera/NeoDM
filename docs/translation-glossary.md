# Translation glossary

Terms that must be translated the same way everywhere in a language. Add a column for your language when you start a translation and keep to it. The Arabic column is the reference for right-to-left languages.

## Rules for every language

- **NeoDM** is never translated or transliterated. File names, URLs, versions and technical tokens (HTTP, HTTPS, SOCKS5, DHT, PeX, LSD, UPnP, DPAPI, SHA-256, KiB/s, .torrent, magnet:) stay in Latin letters.
- Placeholders such as `{0}` stay exactly as they are and are never translated. The order of placeholders may change when the grammar needs it.
- Keep the same sentence type: a button stays an imperative or a short noun, a status line stays a status line. Do not translate word by word; write what a native speaker would expect to read.
- The English texts use plain wording on purpose. Do not add politeness formulas or marketing tone.
- Numbers use Western digits 0-9 in every language, including Arabic and Persian.
- Access keys: English uses `_` before the key letter (`_Tasks`). Languages without a natural access letter (Arabic, Persian, Chinese, Japanese, Korean, Hindi) leave the underscore out. A language with Latin letters may keep it, as long as the letters are unique within one menu or window.
- A file name, folder path or URL inside a sentence is passed in as a placeholder. The app puts left-to-right marks around it in right-to-left languages, so do not add direction marks around placeholders yourself.
- Punctuation follows the language: Arabic comma `،`, Arabic question mark `؟`, Arabic quotation marks `«» `. Keep the ellipsis `…` where English has one.

## Terms

| English | Arabic | Notes |
| --- | --- | --- |
| download (noun and verb) | تنزيل | not "تحميل" |
| downloads (list) | التنزيلات | |
| queue | قائمة الانتظار | |
| pause / paused | إيقاف مؤقت / موقوف مؤقتًا | "Stop" on its own is إيقاف |
| stop | إيقاف | |
| resume | استئناف | |
| cancel / canceled | إلغاء / ملغى | |
| remove | إزالة | takes it off the list, the file stays |
| delete | حذف | removes the file from disk |
| retry | إعادة المحاولة | |
| speed | السرعة | |
| speed limit | حد السرعة | |
| schedule / scheduled | جدولة / مجدول | the Scheduler window is المجدول |
| completed | مكتمل | |
| failed | فشل | |
| queued | في الانتظار | |
| connecting | جارٍ الاتصال | |
| seeding | مشاركة (بذر) | status text; "seed" count label is البذور |
| peers | الأقران | |
| torrent | تورنت | |
| magnet link | رابط Magnet | Magnet stays Latin |
| metadata | البيانات الوصفية | |
| proxy | وكيل | proxy server is خادم الوكيل |
| options | خيارات | the window and the menu item |
| theme | مظهر | |
| accent colour | لون التمييز | |
| language | اللغة | |
| tray (notification area) | منطقة الإشعارات | |
| browser extension | إضافة المتصفح | |
| native host / helper | مساعد المتصفح | |
| segment | جزء | |
| connection | اتصال | |
| category | فئة | |
| folder | مجلد | |
| path | مسار | |
| properties | خصائص | |
| about | حول | |
| help | مساعدة | |
| update | تحديث | |
| release notes | ملاحظات الإصدار | |
| remaining (time) | المتبقي | |
| browse | استعراض | |
| apply / save / close | تطبيق / حفظ / إغلاق | |
| shut down / restart / sleep / hibernate / lock / log off | إيقاف التشغيل / إعادة التشغيل / سكون / سبات / قفل / تسجيل الخروج | |

## Sentence forms (Arabic)

- Errors say what happened first and what to do second, in one or two short sentences, in the passive: تعذّر حفظ التغيير. تحقق من قائمة الانتظار وحاول مرة أخرى.
- Counts use the plural forms of the language (see TRANSLATING.md): zero, one, two, few, many and other all have their own text in Arabic.
- Mixed text: when an Arabic sentence contains a Latin token that carries punctuation (`HTTP(S)`, `KiB/s`, `#7C6CF2`), the translation file puts a left-to-right mark (U+200E) on both sides of the token so the punctuation stays attached to it. A bracketed pair that is entirely left to right, such as `{0} ({1})`, also ends with a mark after the closing bracket.
