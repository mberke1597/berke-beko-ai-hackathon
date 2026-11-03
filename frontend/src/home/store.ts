// Holds the loaded home and applies live updates. UI components subscribe to it.

import type { LiveStatus } from "./live.ts";
import type { Appliance, ApplianceEvent, HomeView } from "./types.ts";

export type StoreChange =
  | { kind: "home" }
  | { kind: "appliance"; appliance: Appliance }
  | { kind: "event"; event: ApplianceEvent }
  | { kind: "live"; status: LiveStatus }
  | { kind: "aiTag"; applianceId: string };

export type StoreListener = (change: StoreChange) => void;

function isNewer(next: Appliance, current: Appliance): boolean {
  // Ignore out-of-order updates (e.g. a slow command response after a newer hub message).
  const a = Date.parse(next.state.updatedAtUtc);
  const b = Date.parse(current.state.updatedAtUtc);
  return Number.isNaN(a) || Number.isNaN(b) || a >= b;
}

/** How long the "Çeliknaz ayarladı" tag stays on a card (browser clock, not simulator time). */
export const AI_TAG_MS = 20_000;

export class HomeStore {
  private view: HomeView | undefined;
  private readonly aiTags = new Map<string, number>();
  private readonly aiTimers = new Map<string, ReturnType<typeof setTimeout>>();
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

  /** Marks appliances changed by a Çeliknaz action; the tag disappears after `AI_TAG_MS`. */
  markAiChanged(ids: readonly string[], now: number = Date.now()): void {
    for (const id of new Set(ids)) {
      this.aiTags.set(id, now + AI_TAG_MS);
      const old = this.aiTimers.get(id);
      if (old !== undefined) clearTimeout(old);
      this.aiTimers.set(
        id,
        setTimeout(() => {
          this.aiTags.delete(id);
          this.aiTimers.delete(id);
          this.notify({ kind: "aiTag", applianceId: id });
        }, AI_TAG_MS),
      );
      this.notify({ kind: "aiTag", applianceId: id });
    }
  }

  isAiChanged(id: string, now: number = Date.now()): boolean {
    const until = this.aiTags.get(id);
    return until !== undefined && until > now;
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
