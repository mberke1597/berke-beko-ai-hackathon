// One app-wide session for the Çeliknaz home app: data source (real API or mock), store and live connection.

import type { RequestOptions } from "../api/client.ts";
import { createHttpHomeApi, type HomeApi } from "./api.ts";
import type { ConnectLive } from "./live.ts";
import { HomeStore } from "./store.ts";

export interface HomeBackend {
  api: HomeApi;
  connectLive: ConnectLive;
  isMock: boolean;
}

export function isMockMode(env: { VITE_USE_MOCK?: string } = import.meta.env): boolean {
  return env.VITE_USE_MOCK === "true";
}

async function createBackend(): Promise<HomeBackend> {
  if (isMockMode()) {
    const { createMockHome } = await import("./mock/simulator.ts");
    const mock = createMockHome();
    return { api: mock.api, connectLive: mock.connectLive, isMock: true };
  }
  const { connectSignalRLive } = await import("./signalr-live.ts");
  return { api: createHttpHomeApi(), connectLive: connectSignalRLive, isMock: false };
}

export class HomeSession {
  readonly store = new HomeStore();
  private backendPromise: Promise<HomeBackend> | undefined;
  private disconnect: (() => void) | undefined;
  private loading: Promise<void> | undefined;

  constructor(private readonly factory: () => Promise<HomeBackend> = createBackend) {}

  backend(): Promise<HomeBackend> {
    this.backendPromise ??= this.factory();
    return this.backendPromise;
  }

  async api(): Promise<HomeApi> {
    return (await this.backend()).api;
  }

  /** Loads the home once (or again after a failure) and connects to the device hub. */
  ensureLoaded(options?: RequestOptions): Promise<void> {
    if (this.store.home) return Promise.resolve();
    this.loading ??= this.load(options).finally(() => {
      this.loading = undefined;
    });
    return this.loading;
  }

  private async load(options?: RequestOptions): Promise<void> {
    const backend = await this.backend();
    const view = await backend.api.getHome(options);
    this.store.setHome(view);
    this.disconnect?.();
    this.disconnect = backend.connectLive(view.home.id, {
      stateChanged: (appliance) => {
        this.store.upsert(appliance);
      },
      event: (event) => {
        this.store.pushEvent(event);
      },
      status: (status) => {
        this.store.setLive(status);
      },
    });
  }

  dispose(): void {
    this.disconnect?.();
    this.disconnect = undefined;
  }
}

let shared: HomeSession | undefined;

export function homeSession(): HomeSession {
  shared ??= new HomeSession();
  return shared;
}
