// Types and decoders for "Çeliknaz slice 1" (docs/architecture/contracts.md).

export const APPLIANCE_TYPES = ["air_conditioner", "washing_machine", "dishwasher", "oven", "refrigerator"] as const;
export type ApplianceType = (typeof APPLIANCE_TYPES)[number];

export const APPLIANCE_STATUSES = ["off", "idle", "running", "paused", "finished", "error"] as const;
export type ApplianceStatus = (typeof APPLIANCE_STATUSES)[number];

export const COMMAND_SOURCES = ["app", "remote", "voice", "ai", "schedule"] as const;
export type CommandSource = (typeof COMMAND_SOURCES)[number];

export interface SetpointRange {
  min: number;
  max: number;
  step: number;
}

export interface Capabilities {
  commands: string[];
  modes: string[];
  fanLevels: string[];
  setpoint: SetpointRange | null;
  programs: string[];
}

export interface AcState {
  setpoint: number;
  mode: string;
  fan: string;
  roomTempC: number;
  humidityPct: number;
}

export interface ProgramState {
  program: string;
  temperatureC: number | null;
  remainingMin: number;
  progressPct: number;
  startAtUtc: string | null;
}

export interface OvenState {
  mode: string;
  targetTempC: number;
  currentTempC: number;
  timerMin: number | null;
}

export interface FridgeState {
  fridgeTempC: number;
  freezerTempC: number;
  doorOpen: boolean;
}

export interface ApplianceState {
  power: boolean;
  status: ApplianceStatus;
  powerW: number;
  updatedAtUtc: string;
  ac: AcState | null;
  program: ProgramState | null;
  oven: OvenState | null;
  fridge: FridgeState | null;
}

export interface Appliance {
  id: string;
  homeId: string;
  roomId: string;
  type: ApplianceType;
  displayName: string;
  model: string;
  capabilities: Capabilities;
  state: ApplianceState;
}

export interface Home {
  id: string;
  name: string;
  city: string;
}

export interface Room {
  id: string;
  name: string;
}

export interface Member {
  id: string;
  displayName: string;
  role: string;
  isDefault: boolean;
}

/** Response of `GET /api/homes/me`. */
export interface HomeView {
  home: Home;
  rooms: Room[];
  members: Member[];
  appliances: Appliance[];
}

export interface TelemetryPoint {
  tsUtc: string;
  roomTempC: number | null;
  humidityPct: number | null;
  powerW: number | null;
}

export interface ProgramStart {
  program: string;
  temperatureC: number | null;
  startAtUtc: string | null;
}

export interface OvenSettings {
  mode: string;
  targetTempC: number;
  timerMin: number | null;
}

/** Command body sent to `POST /api/devices/{id}/commands` (without source/memberId). */
export type DeviceCommand =
  | { type: "power"; value: boolean }
  | { type: "setpoint"; value: number }
  | { type: "mode"; value: string }
  | { type: "fan"; value: string }
  | { type: "start_program"; value: ProgramStart }
  | { type: "pause" | "resume" | "stop"; value: null }
  | { type: "set_oven"; value: OvenSettings };

export type CommandType = DeviceCommand["type"];

export interface CommandRequest {
  type: CommandType;
  value: DeviceCommand["value"];
  source: CommandSource;
  memberId: string;
}

export interface CommandResult {
  commandId: string;
  accepted: boolean;
  appliance: Appliance;
}

export type ApplianceEventKind = "finished" | "preheated" | "timer_done" | "error";

/** SignalR `DeviceEvent` payload. */
export interface ApplianceEvent {
  applianceId: string;
  event: ApplianceEventKind;
  atUtc: string;
}

// ---------- decoders ----------

type Rec = Record<string, unknown>;

function isRecord(value: unknown): value is Rec {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

const isString = (v: unknown): v is string => typeof v === "string";
const isNumber = (v: unknown): v is number => typeof v === "number" && Number.isFinite(v);
const isNullableNumber = (v: unknown): v is number | null => v === null || isNumber(v);
const isNullableString = (v: unknown): v is string | null => v === null || isString(v);

function stringArray(value: unknown): string[] | undefined {
  if (value === undefined || value === null) return [];
  if (!Array.isArray(value)) return undefined;
  const result: string[] = [];
  for (const item of value) {
    // Programs may be plain ids or objects with an "id"; keep the id.
    if (isString(item)) result.push(item);
    else if (isRecord(item) && isString(item.id)) result.push(item.id);
    else return undefined;
  }
  return result;
}

function oneOf<T extends string>(values: readonly T[], value: unknown): value is T {
  return isString(value) && (values as readonly string[]).includes(value);
}

function decodeSetpoint(value: unknown): SetpointRange | null | undefined {
  if (value === undefined || value === null) return null;
  if (!isRecord(value)) return undefined;
  const { min, max, step } = value;
  if (!isNumber(min) || !isNumber(max) || !isNumber(step) || step <= 0 || min > max) return undefined;
  return { min, max, step };
}

function decodeCapabilities(value: unknown): Capabilities | undefined {
  if (!isRecord(value)) return undefined;
  const commands = stringArray(value.commands);
  const modes = stringArray(value.modes);
  const fanLevels = stringArray(value.fanLevels);
  const programs = stringArray(value.programs);
  const setpoint = decodeSetpoint(value.setpoint);
  if (!commands || !modes || !fanLevels || !programs || setpoint === undefined) return undefined;
  return { commands, modes, fanLevels, programs, setpoint };
}

function decodeAc(value: unknown): AcState | null | undefined {
  if (value === undefined || value === null) return null;
  if (!isRecord(value)) return undefined;
  const { setpoint, mode, fan, roomTempC, humidityPct } = value;
  if (!isNumber(setpoint) || !isString(mode) || !isString(fan) || !isNumber(roomTempC) || !isNumber(humidityPct)) {
    return undefined;
  }
  return { setpoint, mode, fan, roomTempC, humidityPct };
}

function decodeProgram(value: unknown): ProgramState | null | undefined {
  if (value === undefined || value === null) return null;
  if (!isRecord(value)) return undefined;
  const { program, temperatureC, remainingMin, progressPct, startAtUtc } = value;
  if (
    !isString(program) ||
    !isNullableNumber(temperatureC ?? null) ||
    !isNumber(remainingMin) ||
    !isNumber(progressPct) ||
    !isNullableString(startAtUtc ?? null)
  ) {
    return undefined;
  }
  return {
    program,
    temperatureC: (temperatureC as number | null | undefined) ?? null,
    remainingMin,
    progressPct,
    startAtUtc: (startAtUtc as string | null | undefined) ?? null,
  };
}

function decodeOven(value: unknown): OvenState | null | undefined {
  if (value === undefined || value === null) return null;
  if (!isRecord(value)) return undefined;
  const { mode, targetTempC, currentTempC, timerMin } = value;
  if (!isString(mode) || !isNumber(targetTempC) || !isNumber(currentTempC) || !isNullableNumber(timerMin ?? null)) {
    return undefined;
  }
  return { mode, targetTempC, currentTempC, timerMin: (timerMin as number | null | undefined) ?? null };
}

function decodeFridge(value: unknown): FridgeState | null | undefined {
  if (value === undefined || value === null) return null;
  if (!isRecord(value)) return undefined;
  const { fridgeTempC, freezerTempC, doorOpen } = value;
  if (!isNumber(fridgeTempC) || !isNumber(freezerTempC) || typeof doorOpen !== "boolean") return undefined;
  return { fridgeTempC, freezerTempC, doorOpen };
}

function decodeState(value: unknown): ApplianceState | undefined {
  if (!isRecord(value)) return undefined;
  const { power, status, powerW, updatedAtUtc } = value;
  if (typeof power !== "boolean" || !oneOf(APPLIANCE_STATUSES, status) || !isNumber(powerW) || !isString(updatedAtUtc)) {
    return undefined;
  }
  const ac = decodeAc(value.ac);
  const program = decodeProgram(value.program);
  const oven = decodeOven(value.oven);
  const fridge = decodeFridge(value.fridge);
  if (ac === undefined || program === undefined || oven === undefined || fridge === undefined) return undefined;
  return { power, status, powerW, updatedAtUtc, ac, program, oven, fridge };
}

export function decodeAppliance(value: unknown): Appliance | undefined {
  if (!isRecord(value)) return undefined;
  const { id, homeId, roomId, type, displayName, model } = value;
  if (!isString(id) || !isString(homeId) || !isString(roomId) || !oneOf(APPLIANCE_TYPES, type) || !isString(displayName)) {
    return undefined;
  }
  const capabilities = decodeCapabilities(value.capabilities);
  const state = decodeState(value.state);
  if (!capabilities || !state) return undefined;
  return { id, homeId, roomId, type, displayName, model: isString(model) ? model : "", capabilities, state };
}

function decodeList<T>(value: unknown, decode: (item: unknown) => T | undefined): T[] | undefined {
  if (!Array.isArray(value)) return undefined;
  const result: T[] = [];
  for (const item of value) {
    const decoded = decode(item);
    if (decoded === undefined) return undefined;
    result.push(decoded);
  }
  return result;
}

function decodeRoom(value: unknown): Room | undefined {
  if (!isRecord(value) || !isString(value.id) || !isString(value.name)) return undefined;
  return { id: value.id, name: value.name };
}

function decodeMember(value: unknown): Member | undefined {
  if (!isRecord(value) || !isString(value.id) || !isString(value.displayName)) return undefined;
  return {
    id: value.id,
    displayName: value.displayName,
    role: isString(value.role) ? value.role : "member",
    isDefault: value.isDefault === true,
  };
}

export function decodeHomeView(value: unknown): HomeView | undefined {
  if (!isRecord(value) || !isRecord(value.home)) return undefined;
  const { id, name, city } = value.home;
  if (!isString(id) || !isString(name)) return undefined;
  const rooms = decodeList(value.rooms, decodeRoom);
  const members = decodeList(value.members, decodeMember);
  const appliances = decodeList(value.appliances, decodeAppliance);
  if (!rooms || !members || !appliances) return undefined;
  return { home: { id, name, city: isString(city) ? city : "" }, rooms, members, appliances };
}

function decodeTelemetryPoint(value: unknown): TelemetryPoint | undefined {
  if (!isRecord(value) || !isString(value.tsUtc)) return undefined;
  const num = (v: unknown): number | null => (isNumber(v) ? v : null);
  return { tsUtc: value.tsUtc, roomTempC: num(value.roomTempC), humidityPct: num(value.humidityPct), powerW: num(value.powerW) };
}

export function decodeTelemetry(value: unknown): TelemetryPoint[] | undefined {
  return decodeList(value, decodeTelemetryPoint);
}

export function decodeCommandResult(value: unknown): CommandResult | undefined {
  if (!isRecord(value)) return undefined;
  const appliance = decodeAppliance(value.appliance);
  if (!appliance || typeof value.accepted !== "boolean") return undefined;
  return { commandId: isString(value.commandId) ? value.commandId : "", accepted: value.accepted, appliance };
}

/** Decodes the SignalR `DeviceStateChanged` payload `{ appliance }`. */
export function decodeStateChanged(value: unknown): Appliance | undefined {
  return isRecord(value) ? decodeAppliance(value.appliance) : undefined;
}

export function decodeApplianceEvent(value: unknown): ApplianceEvent | undefined {
  if (!isRecord(value)) return undefined;
  const { applianceId, event, atUtc } = value;
  if (!isString(applianceId) || !oneOf(["finished", "preheated", "timer_done", "error"] as const, event)) return undefined;
  return { applianceId, event, atUtc: isString(atUtc) ? atUtc : new Date().toISOString() };
}
