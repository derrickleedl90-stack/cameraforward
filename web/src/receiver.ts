import { fetchConfig, parseFragment, preferH264, websocketUrl } from "./shared";

const video = document.querySelector<HTMLVideoElement>("#remoteVideo")!;
const status = document.querySelector<HTMLElement>("#receiverStatus")!;
const credentials = parseFragment();
const roomId = credentials.get("room");
const token = credentials.get("token");

let connection: RTCPeerConnection | undefined;
let socket: WebSocket | undefined;
let signalQueue = Promise.resolve();

const setStatus = (message: string, error = false): void => {
  status.textContent = message;
  status.hidden = false;
  status.classList.toggle("error", error);
};

const send = (message: unknown): void => {
  if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify(message));
};

const createConnection = async (): Promise<RTCPeerConnection> => {
  const config = await fetchConfig();
  const pc = new RTCPeerConnection({ iceServers: config.iceServers, bundlePolicy: "max-bundle" });
  pc.addTransceiver("video", { direction: "recvonly" });
  preferH264(pc);
  pc.onicecandidate = ({ candidate }) => {
    if (candidate) send({ type: "candidate", candidate });
  };
  pc.ontrack = ({ streams }) => {
    video.srcObject = streams[0] ?? new MediaStream();
    if (!streams[0]) video.srcObject.addTrack(pc.getReceivers()[0].track);
    void video.play().then(() => { status.hidden = true; });
  };
  pc.onconnectionstatechange = () => {
    if (pc.connectionState === "connected") status.hidden = true;
    if (pc.connectionState === "disconnected") setStatus("Camera connection interrupted…");
    if (pc.connectionState === "failed") setStatus("Camera connection failed", true);
  };
  return pc;
};

const handleSignal = async (message: Record<string, unknown>): Promise<void> => {
  if (message.type === "description" && connection) {
    const description = message.description as RTCSessionDescriptionInit;
    await connection.setRemoteDescription(description);
    if (description.type === "offer") {
      const answer = await connection.createAnswer();
      await connection.setLocalDescription(answer);
      send({ type: "description", description: connection.localDescription });
    }
  } else if (message.type === "candidate" && connection) {
    await connection.addIceCandidate(message.candidate as RTCIceCandidateInit);
  } else if (message.type === "peer-left" || message.type === "hangup") {
    video.srcObject = null;
    setStatus("Waiting for camera sender…");
    connection?.close();
    connection = await createConnection();
  }
};

const run = async (): Promise<void> => {
  if (!roomId || !token) {
    setStatus("Invalid receiver URL", true);
    return;
  }

  connection = await createConnection();
  socket = new WebSocket(websocketUrl());
  socket.onopen = () => {
    send({ type: "auth", roomId, role: "viewer", token });
    setStatus("Waiting for camera sender…");
  };
  socket.onmessage = ({ data }) => {
    const message = JSON.parse(String(data)) as Record<string, unknown>;
    signalQueue = signalQueue
      .then(() => handleSignal(message))
      .catch((error: unknown) => {
        setStatus(error instanceof Error ? error.message : "Connection failed", true);
      });
  };
  socket.onclose = ({ code, reason }) => {
    if (code !== 1000) setStatus(reason || "Receiver disconnected", true);
  };
};

void run().catch((error: unknown) => {
  setStatus(error instanceof Error ? error.message : "Receiver could not start", true);
});
