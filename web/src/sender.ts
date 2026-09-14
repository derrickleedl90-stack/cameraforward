import { fetchConfig, preferH264, websocketUrl } from "./shared";

const preview = document.querySelector<HTMLVideoElement>("#preview")!;
const previewPlaceholder = document.querySelector<HTMLElement>("#previewPlaceholder")!;
const liveBadge = document.querySelector<HTMLElement>("#liveBadge")!;
const cameraSelect = document.querySelector<HTMLSelectElement>("#cameraSelect")!;
const qualitySelect = document.querySelector<HTMLSelectElement>("#qualitySelect")!;
const startButton = document.querySelector<HTMLButtonElement>("#startButton")!;
const stopButton = document.querySelector<HTMLButtonElement>("#stopButton")!;
const sharePanel = document.querySelector<HTMLElement>("#sharePanel")!;
const viewerUrl = document.querySelector<HTMLInputElement>("#viewerUrl")!;
const copyButton = document.querySelector<HTMLButtonElement>("#copyButton")!;
const status = document.querySelector<HTMLElement>("#status")!;
const viewerStatus = document.querySelector<HTMLElement>("#viewerStatus")!;
const bitrate = document.querySelector<HTMLElement>("#bitrate")!;
const route = document.querySelector<HTMLElement>("#route")!;

let stream: MediaStream | undefined;
let connection: RTCPeerConnection | undefined;
let socket: WebSocket | undefined;
let statsTimer: number | undefined;
let roomId = "";
let senderToken = "";
let lastBytes = 0;
let lastStatsTime = 0;
let signalQueue = Promise.resolve();

const quality = () => qualitySelect.value === "720p30"
  ? { width: 1280, height: 720, frameRate: 30, maxBitrate: 6_000_000 }
  : { width: 1920, height: 1080, frameRate: 30, maxBitrate: 12_000_000 };

const send = (message: unknown): void => {
  if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify(message));
};

const setStatus = (message: string): void => {
  status.textContent = message;
};

const enumerateCameras = async (): Promise<void> => {
  if (!navigator.mediaDevices?.enumerateDevices) {
    cameraSelect.replaceChildren(new Option("Camera access is unavailable", ""));
    cameraSelect.disabled = true;
    return;
  }

  const devices = await navigator.mediaDevices.enumerateDevices();
  const cameras = devices.filter(({ kind }) => kind === "videoinput");
  const selected = stream?.getVideoTracks()[0]?.getSettings().deviceId;

  if (!cameras.length) {
    cameraSelect.replaceChildren(new Option("No cameras found", ""));
    cameraSelect.disabled = true;
    return;
  }

  cameraSelect.replaceChildren(...cameras.map((camera, index) => {
    const option = document.createElement("option");
    option.value = camera.deviceId;
    option.textContent = camera.label || `Camera ${index + 1}`;
    option.selected = camera.deviceId === selected;
    return option;
  }));
  cameraSelect.disabled = cameras.length < 2;
};

const cameraErrorMessage = (error: unknown): string => {
  if (!(error instanceof DOMException)) return error instanceof Error ? error.message : "Could not start camera";
  if (error.name === "NotAllowedError" || error.name === "SecurityError") {
    return "Camera permission was denied. Allow camera access in your browser settings and try again.";
  }
  if (error.name === "NotFoundError" || error.name === "DevicesNotFoundError") {
    return "No camera was found. Connect or enable a camera, then try again.";
  }
  if (error.name === "NotReadableError" || error.name === "TrackStartError") {
    return "The camera is busy or unavailable. Close other apps using it and try again.";
  }
  if (error.name === "OverconstrainedError" || error.name === "ConstraintNotSatisfiedError") {
    return "The selected camera does not support the requested settings.";
  }
  return error.message || "Could not start camera";
};

const captureCamera = async (): Promise<MediaStream> => {
  if (!window.isSecureContext || !navigator.mediaDevices?.getUserMedia) {
    throw new Error("Camera access requires HTTPS or http://localhost.");
  }

  const selectedQuality = quality();
  const selectedDeviceId = cameraSelect.value;
  const video: MediaTrackConstraints = {
    width: { ideal: selectedQuality.width },
    height: { ideal: selectedQuality.height },
    frameRate: { ideal: selectedQuality.frameRate, max: selectedQuality.frameRate }
  };
  if (selectedDeviceId) video.deviceId = { exact: selectedDeviceId };

  try {
    return await navigator.mediaDevices.getUserMedia({ audio: false, video });
  } catch (error) {
    // A camera can disappear between enumeration and capture. Retry with the
    // browser's default device instead of leaving the Start button inert.
    if (selectedDeviceId && error instanceof DOMException && ["NotFoundError", "OverconstrainedError"].includes(error.name)) {
      delete video.deviceId;
      return navigator.mediaDevices.getUserMedia({ audio: false, video });
    }
    throw error;
  }
};

const createPeerConnection = async (): Promise<RTCPeerConnection> => {
  const config = await fetchConfig();
  const pc = new RTCPeerConnection({
    iceServers: config.iceServers,
    bundlePolicy: "max-bundle"
  });

  const videoTrack = stream!.getVideoTracks()[0];
  videoTrack.contentHint = "detail";
  const transceiver = pc.addTransceiver(videoTrack, {
    direction: "sendonly",
    streams: [stream!],
    sendEncodings: [{ maxBitrate: quality().maxBitrate, maxFramerate: quality().frameRate }]
  });
  preferH264(pc);

  const parameters = transceiver.sender.getParameters();
  parameters.encodings ??= [{}];
  parameters.encodings[0].maxBitrate = quality().maxBitrate;
  parameters.encodings[0].maxFramerate = quality().frameRate;
  (parameters as RTCRtpSendParameters & { degradationPreference?: string }).degradationPreference = "maintain-resolution";
  await transceiver.sender.setParameters(parameters);

  pc.onicecandidate = ({ candidate }) => {
    if (candidate) send({ type: "candidate", candidate });
  };
  pc.onconnectionstatechange = () => {
    route.textContent = pc.connectionState;
    if (pc.connectionState === "connected") {
      setStatus("Streaming");
      viewerStatus.textContent = "Connected";
    } else if (["failed", "disconnected"].includes(pc.connectionState)) {
      setStatus("Connection interrupted");
    }
  };
  return pc;
};

const makeOffer = async (): Promise<void> => {
  if (!connection || connection.signalingState !== "stable") return;
  const offer = await connection.createOffer();
  await connection.setLocalDescription(offer);
  send({ type: "description", description: connection.localDescription });
  setStatus("Negotiating video…");
};

const handleSignal = async (message: Record<string, unknown>): Promise<void> => {
  if (message.type === "peer-ready") {
    viewerStatus.textContent = "Joining…";
    await makeOffer();
  } else if (message.type === "description" && connection) {
    await connection.setRemoteDescription(message.description as RTCSessionDescriptionInit);
  } else if (message.type === "candidate" && connection) {
    await connection.addIceCandidate(message.candidate as RTCIceCandidateInit);
  } else if (message.type === "peer-left") {
    viewerStatus.textContent = "Not connected";
    setStatus("Waiting for viewer");
    connection?.close();
    connection = await createPeerConnection();
  }
};

const connectSignaling = async (): Promise<void> => {
  socket = new WebSocket(websocketUrl());
  socket.onopen = () => {
    send({ type: "auth", roomId, role: "sender", token: senderToken });
    setStatus("Waiting for viewer");
  };
  socket.onmessage = ({ data }) => {
    const message = JSON.parse(String(data)) as Record<string, unknown>;
    signalQueue = signalQueue
      .then(() => handleSignal(message))
      .catch((error: unknown) => {
        setStatus(error instanceof Error ? error.message : "Signaling failed");
      });
  };
  socket.onclose = ({ code, reason }) => {
    if (code !== 1000) setStatus(reason || "Signaling disconnected");
  };
};

const updateStats = async (): Promise<void> => {
  if (!connection) return;
  const reports = await connection.getStats();
  for (const report of reports.values()) {
    if (report.type === "outbound-rtp" && report.kind === "video") {
      if (lastStatsTime) {
        const bitsPerSecond = ((report.bytesSent - lastBytes) * 8 * 1000) / (report.timestamp - lastStatsTime);
        bitrate.textContent = `${(bitsPerSecond / 1_000_000).toFixed(1)} Mbps · ${report.frameWidth ?? "?"}×${report.frameHeight ?? "?"}`;
      }
      lastBytes = report.bytesSent;
      lastStatsTime = report.timestamp;
    }
    if (report.type === "candidate-pair" && report.state === "succeeded" && report.nominated) {
      const local = reports.get(report.localCandidateId);
      route.textContent = local?.candidateType === "relay" ? "TURN relay" : "Direct WebRTC";
    }
  }
};

const stop = (): void => {
  if (statsTimer) window.clearInterval(statsTimer);
  send({ type: "hangup" });
  socket?.close(1000, "Sender stopped");
  connection?.close();
  stream?.getTracks().forEach((track) => track.stop());
  stream = undefined;
  connection = undefined;
  socket = undefined;
  preview.srcObject = null;
  previewPlaceholder.hidden = false;
  liveBadge.hidden = true;
  sharePanel.hidden = true;
  startButton.disabled = false;
  stopButton.disabled = true;
  qualitySelect.disabled = false;
  viewerStatus.textContent = "Not connected";
  bitrate.textContent = "—";
  route.textContent = "—";
  lastBytes = 0;
  lastStatsTime = 0;
  signalQueue = Promise.resolve();
  setStatus("Stopped");
};

const start = async (): Promise<void> => {
  startButton.disabled = true;
  setStatus("Requesting camera…");
  try {
    stream = await captureCamera();
    preview.srcObject = stream;
    previewPlaceholder.hidden = true;
    liveBadge.hidden = false;
    await enumerateCameras();

    const response = await fetch("/api/sessions", { method: "POST" });
    if (!response.ok) throw new Error("Could not create a session");
    const session = await response.json() as {
      roomId: string;
      senderToken: string;
      viewerFragment: string;
    };
    roomId = session.roomId;
    senderToken = session.senderToken;
    viewerUrl.value = new URL(session.viewerFragment, location.origin).href;
    sharePanel.hidden = false;
    stopButton.disabled = false;
    qualitySelect.disabled = true;

    connection = await createPeerConnection();
    await connectSignaling();
    statsTimer = window.setInterval(() => void updateStats(), 1_000);
  } catch (error) {
    stop();
    setStatus(cameraErrorMessage(error));
  }
};

startButton.addEventListener("click", () => void start());
stopButton.addEventListener("click", stop);
copyButton.addEventListener("click", async () => {
  await navigator.clipboard.writeText(viewerUrl.value);
  copyButton.textContent = "Copied";
  window.setTimeout(() => { copyButton.textContent = "Copy"; }, 1_500);
});
cameraSelect.addEventListener("change", () => {
  if (stream) {
    stop();
    void start();
  }
});
window.addEventListener("beforeunload", stop);

if (!window.isSecureContext || !navigator.mediaDevices?.getUserMedia) {
  startButton.disabled = true;
  cameraSelect.replaceChildren(new Option("Camera access is unavailable", ""));
  setStatus("Camera access requires HTTPS or http://localhost.");
} else {
  const refreshCameraList = (): void => void enumerateCameras().catch(() => {
    cameraSelect.replaceChildren(new Option("Default camera", ""));
    cameraSelect.disabled = true;
  });
  refreshCameraList();
  navigator.mediaDevices.addEventListener("devicechange", refreshCameraList);
}
