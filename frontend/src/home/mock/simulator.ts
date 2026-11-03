// In-browser stand-in for the backend: demo fixtures, command handling and a tiny device simulation.
// Used only when VITE_USE_MOCK=true (and in tests). Follows the contract shapes exactly.

import { ApiError } from "../../api/errors.ts";
import type { HomeApi } from "../api.ts";
import { buildCommand, defaultProgramTemp } from "../commands.ts";
import type { ConnectLive, LiveHandlers } from "../live.ts";
import type {
  Appliance,
  ApplianceEvent,
  ApplianceEventKind,
  CommandRequest,
  CommandResult,
  DeviceCommand,
  HomeView,
  TelemetryPoint,
} from "../types.ts";
import { createDemoHome, findProgram, KITCHEN_TEMP_C } from "./fixtures.ts";

export interface MockHomeOptions {
  /** Real milliseconds between simulator ticks; 0 disables the timer (tests call `tick()`). */
  tickMs?: number;
  /** Simulated minutes per tick. */
  minutesPerTick?: number;
  /** Fake network latency for API calls. */
  latencyMs?: number;
  now?: () => Date;
}

export interface MockHome {
  api: HomeApi;
  connectLive: ConnectLive;
  /** Advances the simulation by one tick. */
  tick(): void;
  /** Current state (copy), for tests. */
  snapshot(): HomeView;
  dispose(): void;
}

const round1 = (value: number): number => Math.round(value * 10) / 10;
const OUTDOOR_DRIFT_TARGET = 28;
const AC_STEP_PER_MIN = 0.1;
const OVEN_HEAT_PER_MIN = 15;

function clone<T>(value: T): T {
  return structuredClone(value);
}

type RejectionCode = "command_not_supported" | "invalid_value" | "value_out_of_range" | "invalid_state";

function problem(status: number, title: string, code?: RejectionCode): ApiError {
  return new ApiError("http", status, code === undefined ? { status, title } : { status, title, code });
}

const round10 = (value: number): number => Math.round(value / 10) * 10;

export function createMockHome(options: MockHomeOptions = {}): MockHome {
  const { tickMs = 2000, minutesPerTick = 1, latencyMs = 120, now = () => new Date() } = options;
  const home = createDemoHome();
  const listeners = new Set<LiveHandlers>();
  const preheated = new Set<string>();

  const find = (id: string): Appliance | undefined => home.appliances.find((a) => a.id === id);

  const delay = <T>(produce: () => T, signal?: AbortSignal): Promise<T> =>
    new Promise<T>((resolve, reject) => {
      const run = (): void => {
        if (signal?.aborted) {
          reject(new DOMException("Aborted", "AbortError"));
          return;
        }
        try {
          resolve(produce());
        } catch (error) {
          reject(error instanceof Error ? error : new Error(String(error)));
        }
      };
      if (latencyMs <= 0) run();
      else setTimeout(run, latencyMs);
    });

  const broadcast = (appliance: Appliance): void => {
    for (const l of listeners) l.stateChanged(clone(appliance));
  };

  const emit = (applianceId: string, event: ApplianceEventKind): void => {
    const payload: ApplianceEvent = { applianceId, event, atUtc: now().toISOString() };
    for (const l of listeners) l.event(payload);
  };

  const touch = (appliance: Appliance): void => {
    appliance.state.updatedAtUtc = now().toISOString();
  };

  function acPower(appliance: Appliance): number {
    const ac = appliance.state.ac;
    if (!appliance.state.power || !ac) return 0;
    if (ac.mode === "fan") return 60;
    return Math.abs(ac.roomTempC - ac.setpoint) > 0.3 ? (ac.mode === "eco" ? 600 : 820) : 350;
  }

  /** Like the backend: a stopped washer keeps its program, ready again at full duration. */
  function resetProgram(appliance: Appliance): void {
    const s = appliance.state;
    if (!s.program) return;
    const duration = findProgram(appliance.type, s.program.program)?.durationMin ?? s.program.remainingMin;
    s.program = { ...s.program, remainingMin: duration, progressPct: 0, startAtUtc: null };
  }

  function apply(appliance: Appliance, command: DeviceCommand): void {
    const s = appliance.state;
    switch (command.type) {
      case "power":
        if (appliance.type === "air_conditioner") {
          s.power = command.value;
          s.status = command.value ? "running" : "off";
        } else if (!command.value) {
          s.power = false;
          s.status = "off";
          resetProgram(appliance);
          if (s.oven) s.oven = { ...s.oven, timerMin: 0 };
          preheated.delete(appliance.id);
        } else if (!s.power) {
          s.power = true;
          s.status = "idle";
        }
        break;
      case "setpoint":
        if (s.ac) s.ac.setpoint = command.value;
        break;
      case "mode":
        if (s.ac) s.ac.mode = command.value;
        if (s.oven) s.oven.mode = command.value;
        break;
      case "fan":
        if (s.ac) s.ac.fan = command.value;
        break;
      case "start_program": {
        if (s.status === "running" || s.status === "paused") throw problem(422, "Program already running", "invalid_state");
        const profile = findProgram(appliance.type, command.value.program);
        s.power = true;
        s.status = "running";
        s.program = {
          program: command.value.program,
          temperatureC: command.value.temperatureC ?? profile?.temperatureC ?? defaultProgramTemp(command.value.program),
          remainingMin: profile?.durationMin ?? 60,
          progressPct: 0,
          startAtUtc: command.value.startAtUtc,
        };
        break;
      }
      case "pause":
        if (s.status !== "running") throw problem(422, "Not running", "invalid_state");
        s.status = "paused";
        break;
      case "resume":
        if (s.status !== "paused") throw problem(422, "Not paused", "invalid_state");
        s.status = "running";
        break;
      case "stop":
        s.status = s.power ? "idle" : "off";
        resetProgram(appliance);
        if (s.oven) s.oven = { ...s.oven, timerMin: 0 };
        preheated.delete(appliance.id);
        break;
      case "set_oven":
        if (s.oven) s.oven = { ...s.oven, ...command.value };
        s.power = true;
        s.status = "running";
        preheated.delete(appliance.id);
        break;
    }
    s.powerW = powerFor(appliance);
    touch(appliance);
  }

  /** Visible power draw, rounded to 10 W like the backend. */
  function powerFor(appliance: Appliance): number {
    return round10(rawPower(appliance));
  }

  function rawPower(appliance: Appliance): number {
    const s = appliance.state;
    if (!s.power || s.status === "off") return 0;
    switch (appliance.type) {
      case "air_conditioner":
        return acPower(appliance);
      case "washing_machine":
      case "dishwasher":
        return s.status === "running" && s.program ? (findProgram(appliance.type, s.program.program)?.powerW ?? 400) : 0;
      case "oven":
        if (s.status !== "running" || !s.oven) return 0;
        return s.oven.currentTempC < s.oven.targetTempC ? 2400 : 900;
      case "refrigerator":
        return 90;
    }
  }

  function step(appliance: Appliance): ApplianceEventKind | undefined {
    const s = appliance.state;
    let event: ApplianceEventKind | undefined;

    if (s.ac) {
      const ac = s.ac;
      if (s.power) {
        const diff = ac.setpoint - ac.roomTempC;
        const towards = Math.sign(diff) * Math.min(Math.abs(diff), AC_STEP_PER_MIN * minutesPerTick);
        const allowed = (ac.mode === "cool" && diff < 0) || (ac.mode === "heat" && diff > 0) || ["auto", "eco"].includes(ac.mode);
        if (allowed) ac.roomTempC = round1(ac.roomTempC + towards);
        if (ac.mode === "dry" || ac.mode === "cool") ac.humidityPct = Math.max(40, Math.round(ac.humidityPct - 0.3 * minutesPerTick));
      } else {
        const drift = OUTDOOR_DRIFT_TARGET - ac.roomTempC;
        ac.roomTempC = round1(ac.roomTempC + Math.sign(drift) * Math.min(Math.abs(drift), 0.05 * minutesPerTick));
        ac.humidityPct = Math.min(60, Math.round(ac.humidityPct + 0.2 * minutesPerTick));
      }
    }

    if (s.program && s.status === "running") {
      const p = s.program;
      const remaining = Math.max(0, p.remainingMin - minutesPerTick);
      const progress = p.remainingMin > 0 ? p.progressPct + ((100 - p.progressPct) * Math.min(minutesPerTick, p.remainingMin)) / p.remainingMin : 100;
      s.program = { ...p, remainingMin: remaining, progressPct: Math.round(progress) };
      if (remaining === 0) {
        s.status = "finished";
        s.program.progressPct = 100;
        event = "finished";
      }
    }

    if (s.oven && s.power && s.status === "running") {
      const o = s.oven;
      const before = o.currentTempC;
      o.currentTempC = Math.min(o.targetTempC, Math.round(before + OVEN_HEAT_PER_MIN * minutesPerTick));
      if (o.currentTempC >= o.targetTempC && !preheated.has(appliance.id)) {
        preheated.add(appliance.id);
        event = "preheated";
      }
      if (o.timerMin !== null && o.timerMin > 0) {
        o.timerMin = Math.max(0, o.timerMin - minutesPerTick);
        if (o.timerMin === 0) {
          s.status = "finished";
          event = "timer_done";
        }
      }
    } else if (s.oven && s.status !== "running" && s.oven.currentTempC > KITCHEN_TEMP_C) {
      s.oven.currentTempC = Math.max(KITCHEN_TEMP_C, Math.round(s.oven.currentTempC - 5 * minutesPerTick));
    }

    s.powerW = powerFor(appliance);
    return event;
  }

  function tick(): void {
    for (const appliance of home.appliances) {
      const before = JSON.stringify(appliance.state);
      const event = step(appliance);
      if (JSON.stringify(appliance.state) !== before) {
        touch(appliance);
        broadcast(appliance);
      }
      if (event) emit(appliance.id, event);
    }
  }

  const timer = tickMs > 0 ? setInterval(tick, tickMs) : undefined;

  function sendCommand(id: string, request: CommandRequest): CommandResult {
    const appliance = find(id);
    if (!appliance) throw problem(404, "Appliance not found");
    const command = { type: request.type, value: request.value } as DeviceCommand;
    let check: ReturnType<typeof buildCommand>;
    try {
      check = buildCommand(appliance, command, request.source, request.memberId);
    } catch {
      throw problem(422, "Geçersiz komut değeri", "invalid_value");
    }
    if (!check.ok) {
      throw check.reason === "unsupported"
        ? problem(422, "Komut desteklenmiyor", "command_not_supported")
        : problem(422, "Değer izin verilen aralığın dışında", "value_out_of_range");
    }
    apply(appliance, command);
    broadcast(appliance);
    return { commandId: `mock-${String(Date.now())}`, accepted: true, appliance: clone(appliance) };
  }

  /** Last 24 hours of (fake) history ending now, like the backend without a range. */
  function telemetry(id: string): TelemetryPoint[] {
    const toUtc = now();
    const fromUtc = new Date(toUtc.getTime() - 24 * 60 * 60 * 1000);
    const appliance = find(id);
    if (!appliance) throw problem(404, "Appliance not found");
    const points: TelemetryPoint[] = [];
    const stepMs = 30 * 60 * 1000;
    const end = toUtc.getTime();
    const count = Math.max(1, Math.floor((end - fromUtc.getTime()) / stepMs));
    for (let i = count; i >= 0; i--) {
      const ts = new Date(end - i * stepMs);
      const hour = ts.getHours() + ts.getMinutes() / 60;
      // Daily wave that ends exactly at the current value.
      const wave = (h: number): number => Math.sin(((h - 15) / 24) * 2 * Math.PI);
      const nowHour = new Date(end).getHours() + new Date(end).getMinutes() / 60;
      const ac = appliance.state.ac;
      points.push({
        tsUtc: ts.toISOString(),
        roomTempC: ac ? round1(ac.roomTempC + 1.6 * (wave(hour) - wave(nowHour))) : null,
        humidityPct: ac ? Math.round(ac.humidityPct - 6 * (wave(hour) - wave(nowHour))) : null,
        powerW: appliance.state.power ? Math.round(appliance.state.powerW * (0.7 + 0.3 * Math.abs(wave(hour)))) : 0,
      });
    }
    return points;
  }

  const api: HomeApi = {
    getHome: (opts) => delay(() => clone(home), opts?.signal),
    getDevice: (id, opts) =>
      delay(() => {
        const appliance = find(id);
        if (!appliance) throw problem(404, "Appliance not found");
        return clone(appliance);
      }, opts?.signal),
    sendCommand: (id, request, opts) => delay(() => sendCommand(id, request), opts?.signal),
    getTelemetry: (id, opts) => delay(() => telemetry(id), opts?.signal),
  };

  const connectLive: ConnectLive = (homeId, handlers) => {
    if (homeId !== home.home.id) {
      handlers.status("disconnected");
      return () => undefined;
    }
    listeners.add(handlers);
    handlers.status("connected");
    return () => {
      listeners.delete(handlers);
    };
  };

  return {
    api,
    connectLive,
    tick,
    snapshot: () => clone(home),
    dispose: () => {
      if (timer !== undefined) clearInterval(timer);
      listeners.clear();
    },
  };
}
