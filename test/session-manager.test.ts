import assert from "node:assert/strict";
import { test } from "node:test";
import { SessionManager } from "../src/session-manager.js";

test("creates unique sessions and authenticates each role", () => {
  const manager = new SessionManager(60_000);
  const first = manager.create(1_000);
  const second = manager.create(1_000);

  assert.notEqual(first.roomId, second.roomId);
  assert.ok(manager.authenticate(first.roomId, "sender", first.senderToken, 2_000));
  assert.ok(manager.authenticate(first.roomId, "viewer", first.viewerToken, 2_000));
  assert.equal(manager.authenticate(first.roomId, "sender", first.viewerToken, 2_000), undefined);
  assert.equal(manager.authenticate(first.roomId, "viewer", "wrong", 2_000), undefined);
});

test("expires and removes sessions", () => {
  const manager = new SessionManager(1_000);
  const session = manager.create(10_000);

  assert.ok(manager.authenticate(session.roomId, "viewer", session.viewerToken, 10_999));
  assert.equal(manager.authenticate(session.roomId, "viewer", session.viewerToken, 11_000), undefined);
  assert.equal(manager.size, 0);
});

test("bulk expiration removes only expired sessions", () => {
  const manager = new SessionManager(1_000);
  manager.create(1_000);
  manager.create(1_500);

  assert.equal(manager.removeExpired(2_100), 1);
  assert.equal(manager.size, 1);
});
