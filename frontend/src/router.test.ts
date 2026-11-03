import { describe, expect, it, vi } from "vitest";
import { hrefFor, matchPath, normalizePath, Router, type HashLocation } from "./router.ts";

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

describe("matchPath", () => {
  it("extracts params", () => {
    expect(matchPath("/ev/cihaz/:id", "/ev/cihaz/ac-salon")).toEqual({ id: "ac-salon" });
  });
  it("decodes params", () => {
    expect(matchPath("/ev/cihaz/:id", "/ev/cihaz/a%20b")).toEqual({ id: "a b" });
  });
  it.each([
    ["/ev/cihaz/:id", "/ev/cihaz"],
    ["/ev/cihaz/:id", "/ev/cihaz/x/y"],
    ["/ev/cihaz/:id", "/ev/oda/x"],
    ["/ev/cihaz/:id", "/ev/cihaz/%E0%A4%A"],
  ])("%s does not match %s", (pattern, path) => {
    expect(matchPath(pattern, path)).toBeUndefined();
  });
});

describe("Router with home routes", () => {
  it("routes the Çeliknaz tabs and appliance detail", () => {
    const evim = vi.fn();
    const tasks = vi.fn();
    const assistant = vi.fn();
    const detail = vi.fn();
    const location = fakeLocation("#/ev");
    const router = new Router({
      routes: {
        "/ev": evim,
        "/ev/gorevler": tasks,
        "/ev/asistan": assistant,
        "/ev/cihaz/:id": detail,
      },
      notFound: vi.fn(),
      location,
    });
    router.start();
    expect(evim).toHaveBeenCalledWith({});
    router.navigate("/ev/gorevler");
    expect(tasks).toHaveBeenCalledTimes(1);
    router.navigate("/ev/asistan/");
    expect(assistant).toHaveBeenCalledTimes(1);
    router.navigate("/ev/cihaz/washer-1");
    expect(detail).toHaveBeenCalledWith({ id: "washer-1" });
  });

  it("prefers static routes over patterns", () => {
    const fixed = vi.fn();
    const pattern = vi.fn();
    new Router({
      routes: { "/ev/:tab": pattern, "/ev/asistan": fixed },
      notFound: vi.fn(),
      location: fakeLocation("#/ev/asistan"),
    }).start();
    expect(fixed).toHaveBeenCalledTimes(1);
    expect(pattern).not.toHaveBeenCalled();
  });
});
