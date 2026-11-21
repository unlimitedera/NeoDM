// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
'use strict';
/* 1.3.3: the wait for the app's capture dialog is unbounded while the app confirms it is alive (pending frames); only a silent app restarts the download. */
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { makeBackground, response, tick } = require('./extension-harness.cjs');

const URL_ = 'https://files.example/clip.mp4';
const start = async b => { b.hooks.headers(response(URL_, 'video/mp4', 5e6)); await tick(); return b.message({ action: 'media-download', id: URL_ }, b.pageSender()); };
const reply = req => (req.action === 'ping' ? { ok: true, host: 'com.ndm.bridge' } : null);

test('download request asks for keep-alive; ping does not', async () => {
  const b = makeBackground({ nativeReply: req => reply(req) || { ok: true, id: 'a' } });
  await b.message({ action: 'test' }); assert.equal('keepAlive' in b.nativeRequests.at(-1), false);
  const r = await start(b); assert.equal(r.ok, true);
  assert.equal(b.nativeRequests.at(-1).keepAlive, true);
});

test('pending frames keep the wait alive and the real reply still wins (timer is re-armed to the short idle limit, not 330 s)', async () => {
  const b = makeBackground({ nativeReply: req => reply(req) || [{ pending: true }, { pending: true }, { pending: true }, { ok: true, id: 'late' }] });
  b.timers.length = 0;
  const r = await start(b);
  assert.equal(r.ok, true); assert.equal(r.id, 'late');
  assert.deepEqual(b.timers.map(t => t.ms), [330000, 60000, 60000, 60000]);
});

test('an app that stops answering after pending frames ends the wait with an error within the idle limit (a real failure)', async () => {
  const b = makeBackground({ nativeReply: req => reply(req) || [{ pending: true }] });
  b.timers.length = 0;
  const pending = start(b); await tick(20);
  assert.deepEqual(b.timers.map(t => t.ms), [330000, 60000]);
  b.timers.at(-1).fn();
  const r = await pending;
  assert.equal(r.ok, false); assert.match(r.error, /stopped answering/);
});

test('older host (no pending frames, single reply) behaves as before: 330 s limit, plain ack', async () => {
  const b = makeBackground({ nativeReply: req => reply(req) || { ok: true, id: 'old' } });
  b.timers.length = 0;
  const r = await start(b);
  assert.equal(r.ok, true); assert.equal(r.id, 'old'); assert.deepEqual(b.timers.map(t => t.ms), [330000]);
});

test('user cancel after a long wait is still a deliberate discard, not a restart', async () => {
  const b = makeBackground({ nativeReply: req => reply(req) || [{ pending: true }, { ok: false, error: 'Download cancelled in NeoDM.', cancelled: true }] });
  const r = await start(b);
  assert.equal(r.ok, false); assert.match(r.error || '', /cancel/i);
});

test('app unreachable (host disconnects) is a hard failure', async () => {
  const b = makeBackground({ nativeReply: req => reply(req) || new Error('gone') });
  const r = await start(b);
  assert.equal(r.ok, false);
});
