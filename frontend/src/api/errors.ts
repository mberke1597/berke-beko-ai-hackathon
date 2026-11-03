import { tr } from "../i18n/tr.ts";

/** RFC 9457 problem details as returned by the backend (`application/problem+json`). */
export interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  traceId?: string;
}

export type ApiErrorKind = "http" | "network" | "invalid-response";

/** Error thrown by the API client. `message` is always a Turkish, user-facing text. */
export class ApiError extends Error {
  readonly kind: ApiErrorKind;
  /** HTTP status, or 0 when no response was received. */
  readonly status: number;
  readonly problem: ProblemDetails | undefined;

  constructor(kind: ApiErrorKind, status: number, problem?: ProblemDetails, options?: ErrorOptions) {
    super(messageFor(kind, status), options);
    this.name = "ApiError";
    this.kind = kind;
    this.status = status;
    this.problem = problem;
  }

  get traceId(): string | undefined {
    return this.problem?.traceId;
  }
}

function messageFor(kind: ApiErrorKind, status: number): string {
  if (kind === "network") return tr.errors.network;
  if (kind === "invalid-response") return tr.errors.invalidResponse;
  return messageForStatus(status);
}

/** Maps an HTTP status code to a Turkish user-facing message. */
export function messageForStatus(status: number): string {
  switch (status) {
    case 400:
      return tr.errors.badRequest;
    case 401:
      return tr.errors.unauthorized;
    case 403:
      return tr.errors.forbidden;
    case 404:
      return tr.errors.notFound;
    case 409:
      return tr.errors.conflict;
    case 422:
      return tr.errors.validation;
    case 429:
      return tr.errors.tooManyRequests;
    case 502:
    case 503:
    case 504:
      return tr.errors.unavailable;
    default:
      return status >= 500 && status <= 599 ? tr.errors.server : tr.errors.unknown;
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

/** Extracts known RFC 9457 fields from an unknown JSON value; returns undefined if it is not an object. */
export function parseProblemDetails(value: unknown): ProblemDetails | undefined {
  if (!isRecord(value)) return undefined;
  const problem: ProblemDetails = {};
  if (typeof value.type === "string") problem.type = value.type;
  if (typeof value.title === "string") problem.title = value.title;
  if (typeof value.status === "number") problem.status = value.status;
  if (typeof value.detail === "string") problem.detail = value.detail;
  if (typeof value.traceId === "string") problem.traceId = value.traceId;
  return problem;
}
