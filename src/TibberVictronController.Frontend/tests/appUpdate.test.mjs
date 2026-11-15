import { test } from 'node:test';
import assert from 'node:assert/strict';
import { buildIdOf, decideUpdate } from '../src/appUpdate.ts';

const running = 'aaa@2026-11-15T10:00:00.000Z';
const newer = 'bbb@2026-11-15T19:00:00.000Z';
const check = (overrides = {}) => ({
  currentBuildId: running, serverBuildId: newer, attemptedBuildId: null, busy: false, backendHealthy: true, ...overrides
});

test('a newer version on the server reloads the page', () => {
  assert.equal(decideUpdate(check()), 'reload');
});

test('the same version, an unreadable version file and the development server never reload', () => {
  assert.equal(decideUpdate(check({ serverBuildId: running })), 'none');
  assert.equal(decideUpdate(check({ serverBuildId: null })), 'none');
  assert.equal(decideUpdate(check({ currentBuildId: null })), 'none');
  assert.equal(buildIdOf({ revision: 'aaa', builtAtUtc: null }), null);
});

test('input in progress and a restarting backend postpone the reload instead of cancelling it', () => {
  assert.equal(decideUpdate(check({ busy: true })), 'wait');
  assert.equal(decideUpdate(check({ backendHealthy: false })), 'wait');
});

test('a version is reloaded for only once, so a stale cache cannot cause a reload loop', () => {
  assert.equal(decideUpdate(check({ attemptedBuildId: newer })), 'none');
  assert.equal(decideUpdate(check({ attemptedBuildId: 'older@x' })), 'reload');
});

test('the build id matches what the build writes into version.json', () => {
  assert.equal(buildIdOf({ revision: 'aaa', builtAtUtc: '2026-11-15T10:00:00.000Z' }), running);
  assert.equal(buildIdOf({ revision: null, builtAtUtc: '2026-11-15T10:00:00.000Z' }), 'unknown@2026-11-15T10:00:00.000Z');
});
