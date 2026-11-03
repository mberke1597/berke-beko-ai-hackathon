import { describe, expect, it } from "vitest";
import {
  availableProgramActions,
  buildCommand,
  canStep,
  defaultProgramTemp,
  isValidInRange,
  ovenRange,
  snapToRange,
  stepValue,
  DEFAULT_OVEN_RANGE,
} from "./commands.ts";
import { createDemoHome } from "./mock/fixtures.ts";
import type { Appliance } from "./types.ts";

const range = { min: 16, max: 30, step: 0.5 };

function appliance(id: string): Appliance {
  const found = createDemoHome().appliances.find((a) => a.id === id);
  if (!found) throw new Error(id);
  return found;
}

describe("setpoint bounds and step", () => {
  it.each([
    [24.8, 25],
    [24.7, 24.5],
    [15, 16],
    [31, 30],
    [16.24, 16],
    [29.76, 30],
  ])("snaps %d to %d", (value, expected) => {
    expect(snapToRange(value, range)).toBe(expected);
  });

  it("steps up and down without floating point drift", () => {
    expect(stepValue(25, 1, range)).toBe(25.5);
    expect(stepValue(25, -1, range)).toBe(24.5);
    expect(stepValue(0.1 + 0.2, 1, { min: 0, max: 1, step: 0.1 })).toBe(0.4);
  });

  it("stays inside min and max", () => {
    expect(stepValue(30, 1, range)).toBe(30);
    expect(stepValue(16, -1, range)).toBe(16);
    expect(canStep(30, 1, range)).toBe(false);
    expect(canStep(30, -1, range)).toBe(true);
    expect(canStep(16, -1, range)).toBe(false);
  });

  it("validates values on the step grid only", () => {
    expect(isValidInRange(25.5, range)).toBe(true);
    expect(isValidInRange(25.3, range)).toBe(false);
    expect(isValidInRange(30.5, range)).toBe(false);
    expect(isValidInRange(Number.NaN, range)).toBe(false);
  });
});

describe("buildCommand", () => {
  const ac = appliance("ac-salon");

  it("wraps a valid command with source and member", () => {
    expect(buildCommand(ac, { type: "setpoint", value: 24.5 })).toEqual({
      ok: true,
      request: { type: "setpoint", value: 24.5, source: "app", memberId: "ayse" },
    });
    const remote = buildCommand(ac, { type: "power", value: false }, "remote", "mehmet");
    expect(remote.ok && remote.request).toEqual({ type: "power", value: false, source: "remote", memberId: "mehmet" });
  });

  it("rejects out-of-range and off-step setpoints", () => {
    expect(buildCommand(ac, { type: "setpoint", value: 31 })).toEqual({ ok: false, reason: "invalid" });
    expect(buildCommand(ac, { type: "setpoint", value: 24.3 })).toEqual({ ok: false, reason: "invalid" });
  });

  it("rejects modes and fan levels outside capabilities", () => {
    expect(buildCommand(ac, { type: "mode", value: "turbo" }).ok).toBe(false);
    expect(buildCommand(ac, { type: "fan", value: "high" }).ok).toBe(true);
    expect(buildCommand(ac, { type: "fan", value: "max" }).ok).toBe(false);
  });

  it("rejects commands the appliance does not support", () => {
    expect(buildCommand(ac, { type: "start_program", value: { program: "eco50", temperatureC: 50, startAtUtc: null } })).toEqual({
      ok: false,
      reason: "unsupported",
    });
    expect(buildCommand(appliance("fridge-1"), { type: "power", value: false })).toEqual({ ok: false, reason: "unsupported" });
  });

  it("checks programs and oven settings", () => {
    const washer = appliance("washer-1");
    expect(buildCommand(washer, { type: "start_program", value: { program: "cotton60", temperatureC: 60, startAtUtc: null } }).ok).toBe(true);
    expect(buildCommand(washer, { type: "start_program", value: { program: "eco50", temperatureC: 50, startAtUtc: null } }).ok).toBe(false);

    const oven = appliance("oven-1");
    expect(ovenRange(oven)).toEqual(DEFAULT_OVEN_RANGE);
    expect(buildCommand(oven, { type: "set_oven", value: { mode: "fan", targetTempC: 180, timerMin: 30 } }).ok).toBe(true);
    expect(buildCommand(oven, { type: "set_oven", value: { mode: "fan", targetTempC: 182, timerMin: null } }).ok).toBe(false);
    expect(buildCommand(oven, { type: "set_oven", value: { mode: "steam", targetTempC: 180, timerMin: null } }).ok).toBe(false);
    expect(buildCommand(oven, { type: "set_oven", value: { mode: "fan", targetTempC: 180, timerMin: -5 } }).ok).toBe(false);
  });
});

describe("availableProgramActions", () => {
  it("depends on the status", () => {
    const washer = appliance("washer-1");
    expect(availableProgramActions(washer)).toEqual(["pause", "stop"]);
    washer.state.status = "paused";
    expect(availableProgramActions(washer)).toEqual(["resume", "stop"]);
    washer.state.status = "finished";
    expect(availableProgramActions(washer)).toEqual(["start"]);
    washer.state.status = "error";
    expect(availableProgramActions(washer)).toEqual([]);
  });
});

describe("defaultProgramTemp", () => {
  it("reads the trailing number", () => {
    expect(defaultProgramTemp("cotton40")).toBe(40);
    expect(defaultProgramTemp("quick")).toBeNull();
  });
});
