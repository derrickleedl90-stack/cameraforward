import { createReadStream, existsSync, statSync } from "node:fs";
import { createServer, type IncomingMessage, type ServerResponse } from "node:http";
import { extname, resolve, sep } from "node:path";
import { loadEnvFile } from "node:process";
import { fileURLToPath } from "node:url";
import { WebSocket, WebSocketServer } from "ws";
import { SessionManager, type Role } from "./session-manager.js";

try {
  loadEnvFile();
} catch (error) {
  if ((error as NodeJS.ErrnoException).code !== "ENOENT") throw error;
}

const devPort = process.argv.find((argument) => argument.startsWith("--dev-port="))?.split("=", 2)[1];
const port = Number.parseInt(devPort ?? process.env.PORT ?? "3000", 10);
const host = process.env.HOST ?? "0.0.0.0";
const ttlMinutes = Number.parseInt(process.env.SESSION_TTL_MINUTES ?? "120", 10);
const sessions = new SessionManager(ttlMinutes * 60_000);
const webRoot = fileURLToPath(new URL("../web", import.meta.url));

type IceServerConfig = {
  urls: string[];
  username?: string;
  credential?: string;
};

const allowedSignalTypes = new Set(["description", "candidate", "hangup"]);
const mimeTypes: Record<string, string> = {
  ".css": "text/css; charset=utf-8",
  ".html": "text/html; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".json": "application/json; charset=utf-8",
  ".svg": "image/svg+xml"
};

type AuthMessage = {
  type: "auth";
  roomId: string;
  role: Role;
  token: string;
};

const json = (response: ServerResponse, status: number, body: unknown): void => {
  response.writeHead(status, {
    "content-type": "application/json; charset=utf-8",
    "cache-control": "no-store",
    "x-content-type-options": "nosniff"
  });
  response.end(JSON.stringify(body));
};

const iceServers = (): IceServerConfig[] => {
  const servers: IceServerConfig[] = [];
  const stunUrls = process.env.STUN_URLS?.split(",").map((url) => url.trim()).filter(Boolean);
  const turnUrls = process.env.TURN_URLS?.split(",").map((url) => url.trim()).filter(Boolean);

  if (stunUrls?.length) servers.push({ urls: stunUrls });
  if (turnUrls?.length && process.env.TURN_USERNAME && process.env.TURN_CREDENTIAL) {
    servers.push({
      urls: turnUrls,
      username: process.env.TURN_USERNAME,
      credential: process.env.TURN_CREDENTIAL
    });
  }
  return servers;
};

const serveStatic = (request: IncomingMessage, response: ServerResponse): void => {
  const pathname = new URL(request.url ?? "/", "http://localhost").pathname;
  const requested = pathname === "/" ? "/index.html" : pathname;
  const filePath = resolve(webRoot, `.${requested}`);

  if (!filePath.startsWith(`${webRoot}${sep}`) || !existsSync(filePath) || !statSync(filePath).isFile()) {
    json(response, 404, { error: "Not found" });
    return;
  }

  response.writeHead(200, {
    "content-type": mimeTypes[extname(filePath)] ?? "application/octet-stream",
    "cache-control": filePath.endsWith(".html") ? "no-store" : "public, max-age=31536000, immutable",
    "x-content-type-options": "nosniff",
    "referrer-policy": "no-referrer",
    "permissions-policy": "camera=(self), microphone=()",
    "content-security-policy": "default-src 'self'; connect-src 'self' ws: wss:; style-src 'self'; script-src 'self'; img-src 'self' data:; media-src 'self' blob:"
  });
  if (request.method === "HEAD") response.end();
  else createReadStream(filePath).pipe(response);
};

const server = createServer((request, response) => {
  if (request.method === "POST" && request.url === "/api/sessions") {
    const session = sessions.create();
    json(response, 201, {
      roomId: session.roomId,
      senderToken: session.senderToken,
      viewerFragment: `/receiver.html#room=${encodeURIComponent(session.roomId)}&token=${encodeURIComponent(session.viewerToken)}`,
      expiresAt: session.expiresAt
    });
    return;
  }

  if (request.method === "GET" && request.url === "/api/config") {
    json(response, 200, { iceServers: iceServers() });
    return;
  }

  if (request.method !== "GET" && request.method !== "HEAD") {
    json(response, 405, { error: "Method not allowed" });
    return;
  }

  serveStatic(request, response);
});

const wss = new WebSocketServer({ noServer: true, maxPayload: 64 * 1024 });

server.on("upgrade", (request, socket, head) => {
  if (new URL(request.url ?? "/", "http://localhost").pathname !== "/ws") {
    socket.destroy();
    return;
  }
  wss.handleUpgrade(request, socket, head, (ws) => wss.emit("connection", ws, request));
});

const send = (ws: WebSocket | undefined, message: unknown): void => {
  if (ws?.readyState === WebSocket.OPEN) ws.send(JSON.stringify(message));
};

wss.on("connection", (ws) => {
  let authenticated: { roomId: string; role: Role } | undefined;
  const authTimer = setTimeout(() => ws.close(4003, "Authentication timeout"), 5_000);

  ws.on("message", (raw) => {
    let message: Record<string, unknown>;
    try {
      message = JSON.parse(raw.toString()) as Record<string, unknown>;
    } catch {
      ws.close(4002, "Invalid JSON");
      return;
    }

    if (!authenticated) {
      const auth = message as AuthMessage;
      if (
        auth.type !== "auth" ||
        typeof auth.roomId !== "string" ||
        typeof auth.token !== "string" ||
        (auth.role !== "sender" && auth.role !== "viewer")
      ) {
        ws.close(4003, "Authentication required");
        return;
      }

      const session = sessions.authenticate(auth.roomId, auth.role, auth.token);
      if (!session) {
        ws.close(4003, "Invalid or expired session");
        return;
      }

      const previous = session[auth.role];
      if (previous && previous !== ws) previous.close(4004, "Replaced by a new connection");
      session[auth.role] = ws;
      authenticated = { roomId: auth.roomId, role: auth.role };
      clearTimeout(authTimer);
      send(ws, { type: "authenticated", role: auth.role });

      if (session.sender?.readyState === WebSocket.OPEN && session.viewer?.readyState === WebSocket.OPEN) {
        send(session.sender, { type: "peer-ready" });
        send(session.viewer, { type: "peer-ready" });
      }
      return;
    }

    if (typeof message.type !== "string" || !allowedSignalTypes.has(message.type)) {
      ws.close(4002, "Unsupported message");
      return;
    }

    const room = sessions.get(authenticated.roomId);

    if (!room) {
      ws.close(4001, "Session expired");
      return;
    }

    const peer = authenticated.role === "sender" ? room.viewer : room.sender;
    send(peer, message);
  });

  ws.on("close", () => {
    clearTimeout(authTimer);
    if (!authenticated) return;
    const session = sessions.get(authenticated.roomId);
    if (!session) return;
    if (session[authenticated.role] !== ws) return;
    session[authenticated.role] = undefined;
    const peer = authenticated.role === "sender" ? session.viewer : session.sender;
    send(peer, { type: "peer-left" });
  });
});

setInterval(() => sessions.removeExpired(), 60_000).unref();

server.listen(port, host, () => {
  console.log(`Camera Forward listening on http://localhost:${port}`);
});
