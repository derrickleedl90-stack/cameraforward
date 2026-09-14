export interface PublicConfig {
  iceServers: RTCIceServer[];
}

export const fetchConfig = async (): Promise<PublicConfig> => {
  const response = await fetch("/api/config", { cache: "no-store" });
  if (!response.ok) throw new Error("Could not load connection configuration");
  return response.json() as Promise<PublicConfig>;
};

export const websocketUrl = (): string => {
  const protocol = location.protocol === "https:" ? "wss:" : "ws:";
  return `${protocol}//${location.host}/ws`;
};

export const preferH264 = (connection: RTCPeerConnection): void => {
  const transceiver = connection.getTransceivers().find(({ receiver }) => receiver.track.kind === "video");
  const codecs = RTCRtpReceiver.getCapabilities?.("video")?.codecs;
  if (!transceiver?.setCodecPreferences || !codecs) return;
  const ordered = [...codecs].sort((a, b) => {
    const aPreferred = a.mimeType.toLowerCase() === "video/h264" ? 1 : 0;
    const bPreferred = b.mimeType.toLowerCase() === "video/h264" ? 1 : 0;
    return bPreferred - aPreferred;
  });
  transceiver.setCodecPreferences(ordered);
};

export const parseFragment = (): URLSearchParams =>
  new URLSearchParams(location.hash.startsWith("#") ? location.hash.slice(1) : location.hash);
