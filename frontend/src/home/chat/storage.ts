// sessionStorage access that never throws (private mode, blocked storage, thumbnails).

export interface KeyValueStore {
  get(key: string): string | undefined;
  set(key: string, value: string): void;
  remove(key: string): void;
}

export function safeStore(getStorage: () => Storage | undefined = () => window.sessionStorage): KeyValueStore {
  const storage = (): Storage | undefined => {
    try {
      return getStorage();
    } catch {
      return undefined;
    }
  };
  return {
    get: (key) => {
      try {
        return storage()?.getItem(key) ?? undefined;
      } catch {
        return undefined;
      }
    },
    set: (key, value) => {
      try {
        storage()?.setItem(key, value);
      } catch {
        // Storage full or blocked: the value just is not remembered.
      }
    },
    remove: (key) => {
      try {
        storage()?.removeItem(key);
      } catch {
        // ignore
      }
    },
  };
}

/** In-memory store, for tests and when no browser storage exists. */
export function memoryStore(initial: Record<string, string> = {}): KeyValueStore {
  const data = new Map(Object.entries(initial));
  return {
    get: (key) => data.get(key),
    set: (key, value) => {
      data.set(key, value);
    },
    remove: (key) => {
      data.delete(key);
    },
  };
}

export const STORAGE_KEYS = {
  conversationId: "celiknaz.conversationId",
  voiceMuted: "celiknaz.voiceMuted",
} as const;
