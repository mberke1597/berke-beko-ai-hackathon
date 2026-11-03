// Building and validating device commands on the client, mirroring the backend rules (422 otherwise).

import type { Appliance, CommandRequest, CommandSource, CommandType, DeviceCommand, SetpointRange } from "./types.ts";

/** Oven temperature range used when the appliance does not report `capabilities.setpoint`. */
export const DEFAULT_OVEN_RANGE: SetpointRange = { min: 50, max: 250, step: 5 };

/** Default member until auth exists (contracts: Ayşe is the default member). */
export const DEFAULT_MEMBER_ID = "ayse";

function decimals(step: number): number {
  const text = String(step);
  const dot = text.indexOf(".");
  return dot < 0 ? 0 : text.length - dot - 1;
}

/** Rounds `value` to the nearest step (counted from `min`) and clamps it into the range. */
export function snapToRange(value: number, range: SetpointRange): number {
  const steps = Math.round((value - range.min) / range.step);
  const snapped = range.min + steps * range.step;
  const clamped = Math.min(range.max, Math.max(range.min, snapped));
  return Number(clamped.toFixed(decimals(range.step)));
}

/** Moves `current` one step up (+1) or down (-1), staying inside the range. */
export function stepValue(current: number, direction: 1 | -1, range: SetpointRange): number {
  return snapToRange(snapToRange(current, range) + direction * range.step, range);
}

export function canStep(current: number, direction: 1 | -1, range: SetpointRange): boolean {
  return stepValue(current, direction, range) !== snapToRange(current, range);
}

/** True when `value` is inside the range and on a step boundary. */
export function isValidInRange(value: number, range: SetpointRange): boolean {
  if (!Number.isFinite(value) || value < range.min || value > range.max) return false;
  return Math.abs(snapToRange(value, range) - value) < 1e-9;
}

export function supportsCommand(appliance: Appliance, type: CommandType): boolean {
  return appliance.capabilities.commands.includes(type);
}

export function ovenRange(appliance: Appliance): SetpointRange {
  return appliance.type === "oven" && appliance.capabilities.setpoint ? appliance.capabilities.setpoint : DEFAULT_OVEN_RANGE;
}

export type CommandCheck = { ok: true; request: CommandRequest } | { ok: false; reason: "unsupported" | "invalid" };

function isValid(appliance: Appliance, command: DeviceCommand): boolean {
  const caps = appliance.capabilities;
  switch (command.type) {
    case "power":
      return typeof command.value === "boolean";
    case "setpoint":
      return caps.setpoint !== null && isValidInRange(command.value, caps.setpoint);
    case "mode":
      return caps.modes.includes(command.value);
    case "fan":
      return caps.fanLevels.includes(command.value);
    case "start_program":
      return caps.programs.includes(command.value.program);
    case "pause":
    case "resume":
    case "stop":
      return true;
    case "set_oven": {
      const { mode, targetTempC, timerMin } = command.value;
      const modeOk = caps.modes.length === 0 || caps.modes.includes(mode);
      const timerOk = timerMin === null || (Number.isInteger(timerMin) && timerMin >= 0);
      return modeOk && timerOk && isValidInRange(targetTempC, ovenRange(appliance));
    }
  }
}

/** Validates a command against the appliance capabilities and wraps it into a request body. */
export function buildCommand(
  appliance: Appliance,
  command: DeviceCommand,
  source: CommandSource = "app",
  memberId: string = DEFAULT_MEMBER_ID,
): CommandCheck {
  if (!supportsCommand(appliance, command.type)) return { ok: false, reason: "unsupported" };
  if (!isValid(appliance, command)) return { ok: false, reason: "invalid" };
  return { ok: true, request: { type: command.type, value: command.value, source, memberId } };
}

export type ProgramAction = "start" | "pause" | "resume" | "stop";

/** Which program controls make sense in the current status (washer, dishwasher, oven). */
export function availableProgramActions(appliance: Appliance): ProgramAction[] {
  const actions: ProgramAction[] = [];
  const status = appliance.state.status;
  const has = (type: CommandType): boolean => supportsCommand(appliance, type);
  if (status === "running") {
    if (has("pause")) actions.push("pause");
    if (has("stop")) actions.push("stop");
  } else if (status === "paused") {
    if (has("resume")) actions.push("resume");
    if (has("stop")) actions.push("stop");
  } else if (status !== "error" && has("start_program")) {
    actions.push("start");
  }
  return actions;
}

/** Default wash temperature for a program id ("cotton40" -> 40, "quick" -> null). */
export function defaultProgramTemp(program: string): number | null {
  const match = /(\d+)$/.exec(program);
  return match?.[1] === undefined ? null : Number(match[1]);
}
