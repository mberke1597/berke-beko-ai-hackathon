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
  getTelemetry(id: string, fromUtc: Date, toUtc: Date, options?: RequestOptions): Promise<TelemetryPoint[]>;
}

const devicePath = (id: string): string => `/api/devices/${encodeURIComponent(id)}`;

export function telemetryUrl(id: string, fromUtc: Date, toUtc: Date): string {
  const query = new URLSearchParams({ fromUtc: fromUtc.toISOString(), toUtc: toUtc.toISOString() });
  return `${devicePath(id)}/telemetry?${query.toString()}`;
}

/** HomeApi backed by the backend REST endpoints (contracts.md "Çeliknaz slice 1"). */
export function createHttpHomeApi(client: ApiClient = api): HomeApi {
  return {
    getHome: (options) => client.get("/api/homes/me", decodeHomeView, options),
    getDevice: (id, options) => client.get(devicePath(id), decodeAppliance, options),
    sendCommand: (id, command, options) => client.post(`${devicePath(id)}/commands`, command, decodeCommandResult, options),
    getTelemetry: (id, fromUtc, toUtc, options) => client.get(telemetryUrl(id, fromUtc, toUtc), decodeTelemetry, options),
  };
}

/** Turkish message for a failed device command (422 means "not supported / out of range"). */
export function commandErrorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    if (error.status === 422) return tr.home.errors.unsupported;
    if (error.status === 404) return tr.home.errors.deviceNotFound;
    if (error.status === 409) return tr.home.errors.busy;
    return error.message;
  }
  return tr.errors.unknown;
}
