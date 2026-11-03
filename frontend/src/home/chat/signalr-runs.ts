import { HubConnectionBuilder, LogLevel } from "@microsoft/signalr";
import type { ConnectRunLive } from "./run-live.ts";
import { decodeRunEvent, RUN_EVENT_KINDS } from "./types.ts";

export const RUN_HUB_URL = "/hubs/runs";

/** Streams run events of one conversation from the backend `RunHub` (contracts.md Phase 2). */
export const connectSignalRRuns: ConnectRunLive = (conversationId, handlers) => {
  const connection = new HubConnectionBuilder()
    .withUrl(RUN_HUB_URL)
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Warning)
    .build();
  const lifecycle = { stopped: false };

  for (const kind of RUN_EVENT_KINDS) {
    connection.on(kind, (payload: unknown) => {
      const event = decodeRunEvent(kind, payload);
      if (event) handlers.event(event);
    });
  }
  connection.onreconnecting(() => {
    handlers.status("reconnecting");
  });
  connection.onreconnected(() => {
    void connection.invoke("JoinConversation", conversationId).catch(() => undefined);
    handlers.status("connected");
  });
  connection.onclose(() => {
    if (!lifecycle.stopped) handlers.status("disconnected");
  });

  handlers.status("connecting");
  const ready = connection
    .start()
    .then(() => connection.invoke("JoinConversation", conversationId))
    .then(() => {
      handlers.status("connected");
    })
    .catch((error: unknown) => {
      handlers.status("disconnected");
      throw error instanceof Error ? error : new Error(String(error));
    });

  return {
    ready,
    stop: () => {
      lifecycle.stopped = true;
      void connection.stop();
    },
  };
};
