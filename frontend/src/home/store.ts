// Holds the loaded home and applies live updates. UI components subscribe to it.

import type { LiveStatus } from "./live.ts";
import type { Appliance, ApplianceEvent, HomeView } from "./types.ts";

export type StoreChange =
  | { kind: "home" }
  | { kind: "appliance"; appliance: Appliance }
  | { kind: "event"; event: ApplianceEvent }
  | { kind: "live"; status: LiveStatus };

export type StoreListener = (change: StoreChange) => void;

function isNewer(next: Appliance, current: Appliance): boolean {
  // Ignore out-of-order updates (e.g. a slow command response after a newer hub message).
  const a = Date.parse(next.state.updatedAtUtc);
  const b = Date.parse(current.state.updatedAtUtc);
  return Number.isNaN(a) || Number.isNaN(b) || a >= b;
}

export class HomeStore {
  private view: HomeView | undefined;
  private liveStatus: LiveStatus = "disconnected";
  private readonly listeners = new Set<StoreListener>();

  get home(): HomeView | undefined {
    return this.view;
  }

  get live(): LiveStatus {
    return this.liveStatus;
  }

  subscribe(listener: StoreListener): () => void {
    this.listeners.add(listener);
    return () => {
      this.listeners.delete(listener);
    };
  }

  setHome(view: HomeView): void {
    this.view = view;
    this.notify({ kind: "home" });
  }

  getAppliance(id: string): Appliance | undefined {
    return this.view?.appliances.find((a) => a.id === id);
  }

  /** Replaces an appliance with a newer version. Returns false when ignored. */
  upsert(appliance: Appliance): boolean {
    if (!this.view) return false;
    const list = this.view.appliances;
    const index = list.findIndex((a) => a.id === appliance.id);
    if (index >= 0) {
      const current = list[index];
      if (current && !isNewer(appliance, current)) return false;
      list[index] = appliance;
    } else {
      list.push(appliance);
    }
    this.notify({ kind: "appliance", appliance });
    return true;
  }

  pushEvent(event: ApplianceEvent): void {
    this.notify({ kind: "event", event });
  }

  setLive(status: LiveStatus): void {
    if (status === this.liveStatus) return;
    this.liveStatus = status;
    this.notify({ kind: "live", status });
  }

  private notify(change: StoreChange): void {
    for (const listener of [...this.listeners]) listener(change);
  }
}
