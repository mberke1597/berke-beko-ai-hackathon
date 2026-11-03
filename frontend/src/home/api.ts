import { api, type ApiClient, type RequestOptions } from "../api/client.ts";
import { ApiError } from "../api/errors.ts";
import { tr } from "../i18n/tr.ts";
import {
  decodeAppliance,
  decodeCommandResult,
  decodeHomeView,
  decodeTelemetry,
  type Appliance,
  type CommandRequest,
  type CommandResult,
  type HomeView,
  type TelemetryPoint,
} from "./types.ts";

/** Data access for the Çeliknaz home app. Implemented by the real API and by the local mock. */
export interface HomeApi {
  getHome(options?: RequestOptions): Promise<HomeView>;
  getDevice(id: string, options?: RequestOptions): Promise<Appliance>;
  sendCommand(id: string, command: CommandRequest, options?: RequestOptions): Promise<CommandResult>;
  /** Last 24 simulated hours. Simulator timestamps run faster than the browser clock; never compare them. */
  getTelemetry(id: string, options?: RequestOptions): Promise<TelemetryPoint[]>;
}

const devicePath = (id: string): string => `/api/devices/${encodeURIComponent(id)}`;

export function telemetryUrl(id: string): string {
  return `${devicePath(id)}/telemetry`;
}

/** HomeApi backed by the backend REST endpoints (contracts.md "Çeliknaz slice 1"). */
export function createHttpHomeApi(client: ApiClient = api): HomeApi {
  return {
    getHome: (options) => client.get("/api/homes/me", decodeHomeView, options),
    getDevice: (id, options) => client.get(devicePath(id), decodeAppliance, options),
    sendCommand: (id, command, options) => client.post(`${devicePath(id)}/commands`, command, decodeCommandResult, options),
    getTelemetry: (id, options) => client.get(telemetryUrl(id), decodeTelemetry, options),
  };
}

/** Stable 422 reasons from the backend (contracts.md "Commands"). */
export const COMMAND_REJECTION_CODES = ["command_not_supported", "invalid_value", "value_out_of_range", "invalid_state"] as const;
export type CommandRejectionCode = (typeof COMMAND_REJECTION_CODES)[number];

const codeMessages: Record<CommandRejectionCode, string> = {
  command_not_supported: tr.home.errors.unsupported,
  invalid_value: tr.home.errors.invalidValue,
  value_out_of_range: tr.home.errors.invalid,
  invalid_state: tr.home.errors.busy,
};

/** Turkish message for a failed device command; 422 is mapped on the problem `code`. */
export function commandErrorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    const code = error.problem?.code;
    if (code !== undefined && Object.hasOwn(codeMessages, code)) return codeMessages[code as CommandRejectionCode];
    if (error.status === 422) return tr.home.errors.unsupported;
    if (error.status === 404) return tr.home.errors.deviceNotFound;
    if (error.status === 409) return tr.home.errors.busy;
    return error.message;
  }
  return tr.errors.unknown;
}
