import { api, type ApiClient, type RequestOptions } from "./client.ts";

/** Response of `GET /api/version` (see docs/architecture/contracts.md). */
export interface VersionInfo {
  name: string;
  version: string;
  commit: string;
  environment: string;
  serverTimeUtc: string;
}

export function decodeVersionInfo(value: unknown): VersionInfo | undefined {
  if (typeof value !== "object" || value === null) return undefined;
  const v = value as Record<string, unknown>;
  const { name, version, commit, environment, serverTimeUtc } = v;
  if (
    typeof name !== "string" ||
    typeof version !== "string" ||
    typeof commit !== "string" ||
    typeof environment !== "string" ||
    typeof serverTimeUtc !== "string"
  ) {
    return undefined;
  }
  return { name, version, commit, environment, serverTimeUtc };
}

export function getVersion(options?: RequestOptions, client: ApiClient = api): Promise<VersionInfo> {
  return client.get("/api/version", decodeVersionInfo, options);
}
