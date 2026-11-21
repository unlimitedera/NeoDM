// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
// Firefox build only. Loaded first by the background page (Firefox runs the listed background scripts in order instead of a service worker).
// The shared background.js starts with importScripts(...): in Firefox those files are already loaded, so the call must do nothing.
if (typeof importScripts !== 'function') globalThis.importScripts = function () { };
// Firefox also offers the promise based browser.* namespace; the shared code uses chrome.*, which Firefox provides too. Keep both names pointing at one API.
if (typeof chrome === 'undefined' && typeof browser !== 'undefined') globalThis.chrome = browser;
if (typeof browser === 'undefined' && typeof chrome !== 'undefined') globalThis.browser = chrome;
