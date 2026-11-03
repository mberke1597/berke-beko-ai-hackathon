import type { LiveStatus } from "../live.ts";
import type { RunEvent } from "./types.ts";

export interface RunLiveHandlers {
  event(event: RunEvent): void;
  status(status: LiveStatus): void;
}

export interface RunLiveConnection {
  /** Resolves once the conversation group is joined (rejects if the first connect fails). */
  ready: Promise<void>;
  stop(): void;
}

/** Connects to RunHub for one conversation. */
export type ConnectRunLive = (conversationId: string, handlers: RunLiveHandlers) => RunLiveConnection;
