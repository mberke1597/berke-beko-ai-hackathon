import type { Appliance, ApplianceEvent } from "./types.ts";

export type LiveStatus = "connecting" | "connected" | "reconnecting" | "disconnected";

export interface LiveHandlers {
  stateChanged(appliance: Appliance): void;
  event(event: ApplianceEvent): void;
  status(status: LiveStatus): void;
}

/** Connects to the device hub for one home; returns a function that disconnects. */
export type ConnectLive = (homeId: string, handlers: LiveHandlers) => () => void;
