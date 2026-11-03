import type { AppNotification, ComfortCheck, HouseholdTask } from "./slice2/types.ts";
import type { Appliance, ApplianceEvent } from "./types.ts";

export type LiveStatus = "connecting" | "connected" | "reconnecting" | "disconnected";

/** Slice 2 events that share the device hub. */
export type LiveExtra =
  | { kind: "taskAssigned"; task: HouseholdTask }
  | { kind: "notificationCreated"; notification: AppNotification }
  | { kind: "comfortCheckRequested"; check: ComfortCheck };

export interface LiveHandlers {
  stateChanged(appliance: Appliance): void;
  event(event: ApplianceEvent): void;
  status(status: LiveStatus): void;
  taskAssigned?(task: HouseholdTask): void;
  notificationCreated?(notification: AppNotification): void;
  comfortCheckRequested?(check: ComfortCheck): void;
}

/** Routes a slice 2 event to the matching optional handler. */
export function dispatchExtra(handlers: LiveHandlers, extra: LiveExtra): void {
  switch (extra.kind) {
    case "taskAssigned":
      handlers.taskAssigned?.(extra.task);
      break;
    case "notificationCreated":
      handlers.notificationCreated?.(extra.notification);
      break;
    case "comfortCheckRequested":
      handlers.comfortCheckRequested?.(extra.check);
      break;
  }
}

/** Connects to the device hub for one home; returns a function that disconnects. */
export type ConnectLive = (homeId: string, handlers: LiveHandlers) => () => void;
