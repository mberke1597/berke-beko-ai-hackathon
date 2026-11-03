import { describe, expect, it, vi } from "vitest";
import { hrefFor, normalizePath, Router, type HashLocation } from "./router.ts";

function fakeLocation(initial = ""): HashLocation & { fire(): void } {
  let hash = initial;
  const listeners = new Set<() => void>();
  return {
    getHash: () => hash,
    setHash: (next) => {
      hash = next;
      for (const l of listeners) l();
    },
    subscribe: (listener) => {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    fire: () => {
      for (const l of listeners) l();
    },
  };
}

describe("normalizePath", () => {
  it.each([
    ["", "/"],
    ["#", "/"],
    ["#/", "/"],
    ["#/giris", "/giris"],
    ["#/giris/", "/giris"],
    ["#giris", "/giris"],
    ["#/giris?x=1", "/giris"],
  ])("%s -> %s", (hash, path) => {
    expect(normalizePath(hash)).toBe(path);
  });

  it("builds hrefs", () => {
    expect(hrefFor("/giris")).toBe("#/giris");
  });
});

describe("Router", () => {
  it("renders the matching route on start and on hash change", () => {
    const location = fakeLocation("#/giris");
    const home = vi.fn();
    const login = vi.fn();
    const router = new Router({ routes: { "/": home, "/giris": login }, notFound: vi.fn(), location });
    router.start();
    expect(login).toHaveBeenCalledTimes(1);
    router.navigate("/");
    expect(home).toHaveBeenCalledTimes(1);
    expect(router.currentPath).toBe("/");
  });

  it("falls back to notFound and ignores prototype keys", () => {
    const notFound = vi.fn();
    const location = fakeLocation("#/constructor");
    new Router({ routes: { "/": vi.fn() }, notFound, location }).start();
    expect(notFound).toHaveBeenCalledTimes(1);
  });

  it("runs cleanup before rendering the next route and on stop", () => {
    const cleanup = vi.fn();
    const location = fakeLocation("#/");
    const router = new Router({ routes: { "/": () => cleanup, "/giris": vi.fn() }, notFound: vi.fn(), location });
    router.start();
    router.navigate("/giris");
    expect(cleanup).toHaveBeenCalledTimes(1);
    router.navigate("/");
    router.stop();
    expect(cleanup).toHaveBeenCalledTimes(2);
  });

  it("re-renders when navigating to the current path", () => {
    const home = vi.fn();
    const router = new Router({ routes: { "/": home }, notFound: vi.fn(), location: fakeLocation("#/") });
    router.start();
    router.navigate("/");
    expect(home).toHaveBeenCalledTimes(2);
  });
});
