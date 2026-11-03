import { HubConnectionBuilder, LogLevel } from "@microsoft/signalr";
import type { ConnectLive } from "./live.ts";
import { decodeApplianceEvent, decodeStateChanged } from "./types.ts";

export const DEVICE_HUB_URL = "/hubs/devices";

/** Real-time device updates from the backend `DeviceHub` (contracts.md "SignalR DeviceHub"). */
export const connectSignalRLive: ConnectLive = (homeId, handlers) => {
  const connection = new HubConnectionBuilder()
    .withUrl(DEVICE_HUB_URL)
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Warning)
    .build();

  const lifecycle = { stopped: false };
  // A function call so the check is not narrowed away across "await".
  const isStopped = (): boolean => lifecycle.stopped;

  connection.on("DeviceStateChanged", (payload: unknown) => {
    const appliance = decodeStateChanged(payload);
    if (appliance) handlers.stateChanged(appliance);
  });
  connection.on("DeviceEvent", (payload: unknown) => {
    const event = decodeApplianceEvent(payload);
    if (event) handlers.event(event);
  });
  connection.onreconnecting(() => {
    handlers.status("reconnecting");
  });
  connection.onreconnected(() => {
    // Groups are per connection; join again after a reconnect.
    void connection.invoke("JoinHome", homeId).catch(() => undefined);
    handlers.status("connected");
  });
  connection.onclose(() => {
    if (!lifecycle.stopped) handlers.status("disconnected");
  });

  const start = async (attempt: number): Promise<void> => {
    if (lifecycle.stopped) return;
    try {
      handlers.status(attempt === 0 ? "connecting" : "reconnecting");
      await connection.start();
      await connection.invoke("JoinHome", homeId);
      handlers.status("connected");
    } catch {
      if (isStopped()) return;
      handlers.status("disconnected");
      // Automatic reconnect only covers drops after a successful start; retry the first start with backoff.
      const delay = Math.min(30_000, 2_000 * 2 ** attempt);
      window.setTimeout(() => void start(attempt + 1), delay);
    }
  };
  void start(0);

  return () => {
    lifecycle.stopped = true;
    void connection.stop();
  };
};
