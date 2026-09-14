import assert from "node:assert/strict";
import WebSocket from "ws";

const baseUrl = process.env.SMOKE_BASE_URL ?? "http://localhost:3000";
const wsUrl = baseUrl.replace(/^http/, "ws") + "/ws";

const waitForType = (socket: WebSocket, type: string): Promise<Record<string, unknown>> =>
  new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error(`Timed out waiting for ${type}`)), 5_000);
    const onMessage = (raw: WebSocket.RawData) => {
      const message = JSON.parse(raw.toString()) as Record<string, unknown>;
      if (message.type === type) {
        clearTimeout(timer);
        socket.off("message", onMessage);
        resolve(message);
      }
    };
    socket.on("message", onMessage);
  });

const open = (): Promise<WebSocket> =>
  new Promise((resolve, reject) => {
    const socket = new WebSocket(wsUrl);
    socket.once("open", () => resolve(socket));
    socket.once("error", reject);
  });

const senderPage = await fetch(baseUrl);
assert.equal(senderPage.status, 200);
assert.match(await senderPage.text(), /Camera Forward/);

const receiverPage = await fetch(`${baseUrl}/receiver.html`);
assert.equal(receiverPage.status, 200);

const response = await fetch(`${baseUrl}/api/sessions`, { method: "POST" });
assert.equal(response.status, 201);
const session = await response.json() as {
  roomId: string;
  senderToken: string;
  viewerFragment: string;
};
const fragment = new URL(session.viewerFragment, baseUrl).hash.slice(1);
const viewerToken = new URLSearchParams(fragment).get("token");
assert.ok(viewerToken);

const sender = await open();
sender.send(JSON.stringify({
  type: "auth",
  roomId: session.roomId,
  role: "sender",
  token: session.senderToken
}));
await waitForType(sender, "authenticated");

const viewer = await open();
viewer.send(JSON.stringify({
  type: "auth",
  roomId: session.roomId,
  role: "viewer",
  token: viewerToken
}));
await Promise.all([waitForType(viewer, "authenticated"), waitForType(sender, "peer-ready")]);

const relayed = waitForType(viewer, "description");
sender.send(JSON.stringify({ type: "description", description: { type: "offer", sdp: "smoke-test" } }));
assert.deepEqual((await relayed).description, { type: "offer", sdp: "smoke-test" });

sender.close();
viewer.close();
console.log("Smoke test passed: pages, session API, authentication, and signaling relay are working.");
