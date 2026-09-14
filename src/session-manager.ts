import { createHash, randomBytes, timingSafeEqual } from "node:crypto";
import type { WebSocket } from "ws";

export type Role = "sender" | "viewer";

export interface Session {
  id: string;
  senderTokenHash: Buffer;
  viewerTokenHash: Buffer;
  createdAt: number;
  expiresAt: number;
  sender?: WebSocket;
  viewer?: WebSocket;
}

export interface NewSession {
  roomId: string;
  senderToken: string;
  viewerToken: string;
  expiresAt: string;
}

const hashToken = (token: string): Buffer =>
  createHash("sha256").update(token).digest();

const tokenMatches = (token: string, expected: Buffer): boolean => {
  const actual = hashToken(token);
  return actual.length === expected.length && timingSafeEqual(actual, expected);
};

export class SessionManager {
  private readonly sessions = new Map<string, Session>();

  constructor(private readonly ttlMs: number) {}

  create(now = Date.now()): NewSession {
    const roomId = randomBytes(12).toString("base64url");
    const senderToken = randomBytes(32).toString("base64url");
    const viewerToken = randomBytes(32).toString("base64url");
    const expiresAt = now + this.ttlMs;

    this.sessions.set(roomId, {
      id: roomId,
      senderTokenHash: hashToken(senderToken),
      viewerTokenHash: hashToken(viewerToken),
      createdAt: now,
      expiresAt
    });

    return {
      roomId,
      senderToken,
      viewerToken,
      expiresAt: new Date(expiresAt).toISOString()
    };
  }

  authenticate(
    roomId: string,
    role: Role,
    token: string,
    now = Date.now()
  ): Session | undefined {
    const session = this.sessions.get(roomId);
    if (!session || session.expiresAt <= now) {
      if (session) this.delete(roomId);
      return undefined;
    }

    const expected =
      role === "sender" ? session.senderTokenHash : session.viewerTokenHash;
    return tokenMatches(token, expected) ? session : undefined;
  }

  get(roomId: string, now = Date.now()): Session | undefined {
    const session = this.sessions.get(roomId);
    if (session && session.expiresAt <= now) {
      this.delete(roomId);
      return undefined;
    }
    return session;
  }

  delete(roomId: string): void {
    const session = this.sessions.get(roomId);
    session?.sender?.close(4001, "Session closed");
    session?.viewer?.close(4001, "Session closed");
    this.sessions.delete(roomId);
  }

  removeExpired(now = Date.now()): number {
    let removed = 0;
    for (const [roomId, session] of this.sessions) {
      if (session.expiresAt <= now) {
        this.delete(roomId);
        removed += 1;
      }
    }
    return removed;
  }

  get size(): number {
    return this.sessions.size;
  }
}
