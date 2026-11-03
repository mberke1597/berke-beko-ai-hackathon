// One app-wide session for the Çeliknaz home app: data source (real API or mock), store and live connection.

import type { RequestOptions } from "../api/client.ts";
import { createHttpHomeApi, type HomeApi } from "./api.ts";
import { createHttpChatApi, type ChatApi } from "./chat/api.ts";
import { ChatController } from "./chat/controller.ts";
import type { ConnectRunLive } from "./chat/run-live.ts";
import { safeStore, type KeyValueStore } from "./chat/storage.ts";
import { DEFAULT_MEMBER_ID } from "./commands.ts";
import type { ConnectLive } from "./live.ts";
import { HomeStore } from "./store.ts";

export interface HomeBackend {
  api: HomeApi;
  connectLive: ConnectLive;
  chat: ChatApi;
  connectRunLive: ConnectRunLive;
  isMock: boolean;
}

export function isMockMode(env: { VITE_USE_MOCK?: string } = import.meta.env): boolean {
  return env.VITE_USE_MOCK === "true";
}

async function createBackend(): Promise<HomeBackend> {
  if (isMockMode()) {
    const [{ createMockHome }, { createMockAgent }] = await Promise.all([import("./mock/simulator.ts"), import("./mock/agent.ts")]);
    const mock = createMockHome();
    const agent = createMockAgent(mock);
    return { api: mock.api, connectLive: mock.connectLive, chat: agent.chat, connectRunLive: agent.connectRunLive, isMock: true };
  }
  const [{ connectSignalRLive }, { connectSignalRRuns }] = await Promise.all([import("./signalr-live.ts"), import("./chat/signalr-runs.ts")]);
  return {
    api: createHttpHomeApi(),
    connectLive: connectSignalRLive,
    chat: createHttpChatApi(),
    connectRunLive: connectSignalRRuns,
    isMock: false,
  };
}

export class HomeSession {
  readonly store = new HomeStore();
  private backendPromise: Promise<HomeBackend> | undefined;
  private disconnect: (() => void) | undefined;
  private loading: Promise<void> | undefined;
  private chatPromise: Promise<ChatController> | undefined;

  constructor(
    private readonly factory: () => Promise<HomeBackend> = createBackend,
    private readonly storage: KeyValueStore = safeStore(),
  ) {}

  /** The app-wide Çeliknaz chat (shared by the Asistan tab and the voice overlay). */
  chat(): Promise<ChatController> {
    this.chatPromise ??= this.backend().then(
      (backend) =>
        new ChatController({
          chat: backend.chat,
          connectRunLive: backend.connectRunLive,
          store: this.storage,
          memberId: () => this.store.home?.members.find((m) => m.isDefault)?.id ?? DEFAULT_MEMBER_ID,
          onActions: (actions) => {
            this.store.markAiChanged(actions.map((a) => a.applianceId));
          },
        }),
    );
    return this.chatPromise;
  }

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
