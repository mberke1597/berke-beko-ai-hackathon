import { describe, expect, it, vi } from "vitest";
import { createApiClient, type FetchFn } from "../api/client.ts";
import { ApiError } from "../api/errors.ts";
import { tr } from "../i18n/tr.ts";
import { commandErrorMessage, createHttpHomeApi, telemetryUrl } from "./api.ts";
import { createDemoHome } from "./mock/fixtures.ts";
import { createMockHome } from "./mock/simulator.ts";
import { HomeSession, isMockMode, type HomeBackend } from "./session.ts";
import { HomeStore } from "./store.ts";
import { decodeAppliance, decodeApplianceEvent, decodeHomeView, decodeStateChanged, decodeTelemetry } from "./types.ts";

function json(body: unknown, status = 200, type = "application/json"): Response {
  return new Response(JSON.stringify(body), { status, headers: { "content-type": type } });
}

/** The exact Appliance example from contracts.md. */
const contractAppliance = {
  id: "ac-salon",
  homeId: "home-1",
  roomId: "salon",
  type: "air_conditioner",
  displayName: "Salon Kliması",
  model: "Beko BK-12 (demo)",
  capabilities: {
    commands: ["power", "setpoint", "mode", "fan"],
    modes: ["cool", "heat", "dry", "fan", "auto", "eco"],
    fanLevels: ["auto", "low", "medium", "high"],
    setpoint: { min: 16, max: 30, step: 0.5 },
    programs: [],
  },
  state: {
    power: true,
    status: "running",
    powerW: 820,
    updatedAtUtc: "2026-11-03T10:00:00Z",
    ac: { setpoint: 25, mode: "cool", fan: "auto", roomTempC: 24.8, humidityPct: 55 },
    program: null,
    oven: null,
    fridge: null,
  },
};

describe("decoders", () => {
  it("decodes the contract appliance example", () => {
    expect(decodeAppliance(contractAppliance)).toEqual(contractAppliance);
  });

  it("decodes every fixture appliance and the home view", () => {
    const home = createDemoHome();
    expect(decodeHomeView(JSON.parse(JSON.stringify(home)))).toEqual(home);
  });

  it("rejects unknown types and statuses", () => {
    expect(decodeAppliance({ ...contractAppliance, type: "toaster" })).toBeUndefined();
    expect(decodeAppliance({ ...contractAppliance, state: { ...contractAppliance.state, status: "sleeping" } })).toBeUndefined();
    expect(decodeAppliance({ ...contractAppliance, capabilities: { ...contractAppliance.capabilities, setpoint: { min: 30, max: 16, step: 1 } } })).toBeUndefined();
  });

  it("accepts program objects with ids", () => {
    const decoded = decodeAppliance({ ...contractAppliance, capabilities: { ...contractAppliance.capabilities, programs: [{ id: "eco50" }, "quick"] } });
    expect(decoded?.capabilities.programs).toEqual(["eco50", "quick"]);
  });

  it("decodes hub payloads and telemetry", () => {
    expect(decodeStateChanged({ appliance: contractAppliance })?.id).toBe("ac-salon");
    expect(decodeStateChanged(contractAppliance)).toBeUndefined();
    expect(decodeApplianceEvent({ applianceId: "washer-1", event: "finished", atUtc: "t" })).toEqual({
      applianceId: "washer-1",
      event: "finished",
      atUtc: "t",
    });
    expect(decodeApplianceEvent({ applianceId: "washer-1", event: "exploded" })).toBeUndefined();
    expect(decodeTelemetry([{ tsUtc: "t", powerW: 5 }])).toEqual([{ tsUtc: "t", roomTempC: null, humidityPct: null, powerW: 5 }]);
  });
});

describe("http home api", () => {
  it("calls the contract endpoints", async () => {
    const calls: { url: string; init: RequestInit | undefined }[] = [];
    const fetchFn: FetchFn = (url, init) => {
      calls.push({ url, init });
      if (url.endsWith("/commands")) return Promise.resolve(json({ commandId: "c1", accepted: true, appliance: contractAppliance }, 202));
      if (url.includes("/telemetry")) return Promise.resolve(json([]));
      if (url === "/api/homes/me") return Promise.resolve(json(JSON.parse(JSON.stringify(createDemoHome()))));
      return Promise.resolve(json(contractAppliance));
    };
    const api = createHttpHomeApi(createApiClient(fetchFn));

    expect((await api.getHome()).home.id).toBe("home-1");
    expect((await api.getDevice("ac-salon")).id).toBe("ac-salon");
    const result = await api.sendCommand("ac-salon", { type: "setpoint", value: 24, source: "remote", memberId: "ayse" });
    expect(result.accepted).toBe(true);
    await api.getTelemetry("ac-salon");

    expect(calls.map((c) => c.url)).toEqual([
      "/api/homes/me",
      "/api/devices/ac-salon",
      "/api/devices/ac-salon/commands",
      "/api/devices/ac-salon/telemetry",
    ]);
    // Simulator time runs faster than the browser clock: telemetry is called without a range.
    expect(telemetryUrl("ac-salon")).toBe("/api/devices/ac-salon/telemetry");
    const post = calls[2]?.init;
    expect(post?.method).toBe("POST");
    expect(JSON.parse(post?.body as string)).toEqual({ type: "setpoint", value: 24, source: "remote", memberId: "ayse" });
    expect((post?.headers as Record<string, string>)["Content-Type"]).toBe("application/json");
  });

  it("maps 422 problem+json to a Turkish 'not supported' message", async () => {
    const fetchFn: FetchFn = () =>
      Promise.resolve(json({ title: "Value out of range", status: 422, traceId: "t" }, 422, "application/problem+json"));
    const api = createHttpHomeApi(createApiClient(fetchFn));
    const error = await api.sendCommand("ac-salon", { type: "setpoint", value: 99, source: "app", memberId: "ayse" }).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(ApiError);
    expect(commandErrorMessage(error)).toBe(tr.home.errors.unsupported);
  });

  it.each([
    ["command_not_supported", tr.home.errors.unsupported],
    ["invalid_value", tr.home.errors.invalidValue],
    ["value_out_of_range", tr.home.errors.invalid],
    ["invalid_state", tr.home.errors.busy],
  ])("maps 422 code %s", async (code, message) => {
    const fetchFn: FetchFn = () =>
      Promise.resolve(json({ title: "Türkçe başlık", status: 422, code }, 422, "application/problem+json"));
    const api = createHttpHomeApi(createApiClient(fetchFn));
    const error = (await api.sendCommand("washer-1", { type: "pause", value: null, source: "app", memberId: "ayse" }).catch((e: unknown) => e)) as ApiError;
    expect(error.problem?.code).toBe(code);
    expect(commandErrorMessage(error)).toBe(message);
  });

  it("maps other statuses", () => {
    expect(commandErrorMessage(new ApiError("http", 404))).toBe(tr.home.errors.deviceNotFound);
    expect(commandErrorMessage(new ApiError("network", 0))).toBe(tr.errors.network);
    expect(commandErrorMessage("boom")).toBe(tr.errors.unknown);
  });
});

describe("mock home", () => {
  const options = { tickMs: 0, latencyMs: 0, now: () => new Date("2026-11-03T10:00:00Z") };

  it("serves the seeded demo home", async () => {
    const mock = createMockHome(options);
    const view = await mock.api.getHome();
    expect(view.home).toEqual({ id: "home-1", name: "Evim", city: "İstanbul" });
    expect(view.rooms.map((r) => r.id)).toEqual(["salon", "yatak-odasi", "cocuk-odasi", "mutfak", "camasir-odasi"]);
    expect(view.members.map((m) => m.id)).toEqual(["ayse", "mehmet", "zeynep"]);
    expect(view.appliances.map((a) => a.id).sort()).toEqual(
      ["ac-cocuk", "ac-salon", "ac-yatak", "dishwasher-1", "fridge-1", "oven-1", "washer-1"].sort(),
    );
    const salon = view.appliances.find((a) => a.id === "ac-salon");
    expect(salon?.state.ac).toMatchObject({ setpoint: 25, mode: "cool", roomTempC: 24.8, humidityPct: 55 });
    for (const a of view.appliances) {
      const subs = [a.state.ac, a.state.program, a.state.oven, a.state.fridge].filter((s) => s !== null);
      expect(subs).toHaveLength(1);
    }
    mock.dispose();
  });

  it("applies commands, broadcasts state and rejects invalid ones with 422", async () => {
    const mock = createMockHome(options);
    const stateChanged = vi.fn();
    const stop = mock.connectLive("home-1", { stateChanged, event: vi.fn(), status: vi.fn() });

    const result = await mock.api.sendCommand("ac-salon", { type: "setpoint", value: 23.5, source: "app", memberId: "ayse" });
    expect(result.appliance.state.ac?.setpoint).toBe(23.5);
    expect(stateChanged).toHaveBeenCalledTimes(1);

    const error = (await mock.api
      .sendCommand("ac-salon", { type: "setpoint", value: 40, source: "app", memberId: "ayse" })
      .catch((e: unknown) => e)) as ApiError;
    expect(error.status).toBe(422);
    const unsupported = (await mock.api
      .sendCommand("fridge-1", { type: "power", value: false, source: "app", memberId: "ayse" })
      .catch((e: unknown) => e)) as ApiError;
    expect(unsupported.status).toBe(422);
    expect(error.problem?.code).toBe("value_out_of_range");
    expect(unsupported.problem?.code).toBe("command_not_supported");
    const notRunning = (await mock.api
      .sendCommand("dishwasher-1", { type: "pause", value: null, source: "app", memberId: "ayse" })
      .catch((e: unknown) => e)) as ApiError;
    expect(notRunning.problem?.code).toBe("invalid_state");
    const missing = (await mock.api.getDevice("nope").catch((e: unknown) => e)) as ApiError;
    expect(missing.status).toBe(404);
    stop();
    mock.dispose();
  });

  it("simulates a program finishing and emits a DeviceEvent", () => {
    const mock = createMockHome(options);
    const event = vi.fn();
    mock.connectLive("home-1", { stateChanged: vi.fn(), event, status: vi.fn() });
    for (let i = 0; i < 42; i++) mock.tick();
    const washer = mock.snapshot().appliances.find((a) => a.id === "washer-1");
    expect(washer?.state.status).toBe("finished");
    expect(washer?.state.program?.progressPct).toBe(100);
    expect(event).toHaveBeenCalledWith({ applianceId: "washer-1", event: "finished", atUtc: "2026-11-03T10:00:00.000Z" });
    mock.dispose();
  });

  it("keeps the program at full duration after stop and power off", async () => {
    const mock = createMockHome(options);
    const stopped = await mock.api.sendCommand("washer-1", { type: "stop", value: null, source: "app", memberId: "ayse" });
    expect(stopped.appliance.state.status).toBe("idle");
    expect(stopped.appliance.state.program).toEqual({ program: "cotton40", temperatureC: 40, remainingMin: 120, progressPct: 0, startAtUtc: null });
    const started = await mock.api.sendCommand("washer-1", {
      type: "start_program",
      value: { program: "quick30", temperatureC: null, startAtUtc: null },
      source: "app",
      memberId: "ayse",
    });
    expect(started.appliance.state.program).toMatchObject({ program: "quick30", temperatureC: 30, remainingMin: 30 });
    expect(started.appliance.state.powerW % 10).toBe(0);
    const off = await mock.api.sendCommand("washer-1", { type: "power", value: false, source: "app", memberId: "ayse" });
    expect(off.appliance.state.status).toBe("off");
    expect(off.appliance.state.program?.remainingMin).toBe(30);
    mock.dispose();
  });

  it("moves the room temperature towards the setpoint", async () => {
    const mock = createMockHome(options);
    await mock.api.sendCommand("ac-salon", { type: "setpoint", value: 22, source: "remote", memberId: "ayse" });
    mock.tick();
    mock.tick();
    expect(mock.snapshot().appliances.find((a) => a.id === "ac-salon")?.state.ac?.roomTempC).toBe(24.6);
    mock.dispose();
  });

  it("generates 24 h telemetry ending at the current room temperature", async () => {
    const mock = createMockHome(options);
    const points = await mock.api.getTelemetry("ac-salon");
    expect(points).toHaveLength(49);
    expect(points.at(-1)?.roomTempC).toBe(24.8);
    expect(points.at(-1)?.humidityPct).toBe(55);
    mock.dispose();
  });
});

describe("store and session", () => {
  it("ignores out-of-order appliance updates", () => {
    const store = new HomeStore();
    store.setHome(createDemoHome());
    const listener = vi.fn();
    store.subscribe(listener);
    const ac = structuredClone(store.getAppliance("ac-salon"));
    if (!ac) throw new Error("missing");
    ac.state.updatedAtUtc = "2026-11-03T09:00:00Z";
    expect(store.upsert(ac)).toBe(false);
    ac.state.updatedAtUtc = "2026-11-03T11:00:00Z";
    ac.state.power = false;
    expect(store.upsert(ac)).toBe(true);
    expect(store.getAppliance("ac-salon")?.state.power).toBe(false);
    expect(listener).toHaveBeenCalledWith({ kind: "appliance", appliance: ac });
  });

  it("loads the home once and wires live updates into the store", async () => {
    const mock = createMockHome({ tickMs: 0, latencyMs: 0 });
    const factory = vi.fn(() => Promise.resolve<HomeBackend>({ api: mock.api, connectLive: mock.connectLive, isMock: true }));
    const session = new HomeSession(factory);
    await Promise.all([session.ensureLoaded(), session.ensureLoaded()]);
    await session.ensureLoaded();
    expect(factory).toHaveBeenCalledTimes(1);
    expect(session.store.live).toBe("connected");

    const events = vi.fn();
    session.store.subscribe((c) => {
      if (c.kind === "event") events(c.event);
    });
    for (let i = 0; i < 42; i++) mock.tick();
    expect(session.store.getAppliance("washer-1")?.state.status).toBe("finished");
    expect(events).toHaveBeenCalledTimes(1);
    session.dispose();
    mock.dispose();
  });

  it("reads mock mode from the environment", () => {
    expect(isMockMode({ VITE_USE_MOCK: "true" })).toBe(true);
    expect(isMockMode({ VITE_USE_MOCK: "false" })).toBe(false);
    expect(isMockMode({})).toBe(false);
  });
});
