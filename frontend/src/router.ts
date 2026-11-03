// Tiny hash router: "#/giris" -> "/giris". No framework, no dynamic segments yet.

/** Renders a route. May return a cleanup function, called before the next route renders. */
export type RouteHandler = () => (() => void) | undefined;

/** Abstraction over `window.location.hash` so the router can be tested without a DOM. */
export interface HashLocation {
  getHash(): string;
  setHash(hash: string): void;
  /** Subscribes to hash changes; returns an unsubscribe function. */
  subscribe(listener: () => void): () => void;
}

export interface RouterOptions {
  routes: Readonly<Record<string, RouteHandler>>;
  notFound: RouteHandler;
  location?: HashLocation;
}

export const browserHashLocation: HashLocation = {
  getHash: () => window.location.hash,
  setHash: (hash) => {
    window.location.hash = hash;
  },
  subscribe: (listener) => {
    window.addEventListener("hashchange", listener);
    return () => {
      window.removeEventListener("hashchange", listener);
    };
  },
};

/** Converts a location hash into a normalized route path ("" | "#" | "#/" -> "/", query and trailing slash removed). */
export function normalizePath(hash: string): string {
  let path = hash.startsWith("#") ? hash.slice(1) : hash;
  const queryIndex = path.indexOf("?");
  if (queryIndex >= 0) path = path.slice(0, queryIndex);
  if (!path.startsWith("/")) path = `/${path}`;
  if (path.length > 1 && path.endsWith("/")) path = path.replace(/\/+$/, "");
  return path === "" ? "/" : path;
}

/** Builds an href for a route path, e.g. "/giris" -> "#/giris". */
export function hrefFor(path: string): string {
  return `#${normalizePath(path)}`;
}

export class Router {
  private readonly routes: Readonly<Record<string, RouteHandler>>;
  private readonly notFound: RouteHandler;
  private readonly location: HashLocation;
  private cleanup: (() => void) | undefined;
  private unsubscribe: (() => void) | undefined;
  private path: string | undefined;

  constructor(options: RouterOptions) {
    this.routes = options.routes;
    this.notFound = options.notFound;
    this.location = options.location ?? browserHashLocation;
  }

  get currentPath(): string | undefined {
    return this.path;
  }

  start(): void {
    if (this.unsubscribe) return;
    this.unsubscribe = this.location.subscribe(() => {
      this.resolve();
    });
    this.resolve();
  }

  stop(): void {
    this.unsubscribe?.();
    this.unsubscribe = undefined;
    this.runCleanup();
  }

  navigate(path: string): void {
    const target = hrefFor(path);
    if (normalizePath(this.location.getHash()) === normalizePath(target)) {
      // Same hash does not fire "hashchange"; re-render explicitly.
      this.resolve();
      return;
    }
    this.location.setHash(target);
  }

  private resolve(): void {
    const path = normalizePath(this.location.getHash());
    this.path = path;
    this.runCleanup();
    const handler = Object.hasOwn(this.routes, path) ? this.routes[path] : undefined;
    const result = (handler ?? this.notFound)();
    this.cleanup = typeof result === "function" ? result : undefined;
  }

  private runCleanup(): void {
    const cleanup = this.cleanup;
    this.cleanup = undefined;
    cleanup?.();
  }
}
