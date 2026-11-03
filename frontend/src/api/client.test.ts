import { describe, expect, it } from "vitest";
import { tr } from "../i18n/tr.ts";
import { createApiClient, type FetchFn } from "./client.ts";
import { ApiError, messageForStatus, parseProblemDetails } from "./errors.ts";
import { decodeVersionInfo, getVersion } from "./version.ts";

function jsonResponse(body: unknown, status = 200, type = "application/json"): Response {
  return new Response(JSON.stringify(body), { status, headers: { "content-type": type } });
}

const version = {
  name: "Beko Agent Platform",
  version: "0.1.0",
  commit: "abc1234",
  environment: "Production",
  serverTimeUtc: "2026-11-03T10:00:00Z",
};

describe("messageForStatus", () => {
  it.each([
    [400, tr.errors.badRequest],
    [401, tr.errors.unauthorized],
    [403, tr.errors.forbidden],
    [404, tr.errors.notFound],
    [409, tr.errors.conflict],
    [422, tr.errors.validation],
    [429, tr.errors.tooManyRequests],
    [500, tr.errors.server],
    [503, tr.errors.unavailable],
    [418, tr.errors.unknown],
  ])("maps %i", (status, message) => {
    expect(messageForStatus(status)).toBe(message);
  });
});

describe("parseProblemDetails", () => {
  it("keeps known fields only", () => {
    expect(parseProblemDetails({ title: "x", status: 404, traceId: "t1", extra: 1 })).toEqual({
      title: "x",
      status: 404,
      traceId: "t1",
    });
  });
  it("returns undefined for non-objects", () => {
    expect(parseProblemDetails("oops")).toBeUndefined();
  });
});

describe("api client", () => {
  it("returns decoded data on success", async () => {
    const fetchFn: FetchFn = () => Promise.resolve(jsonResponse(version));
    await expect(getVersion({}, createApiClient(fetchFn))).resolves.toEqual(version);
  });

  it("throws ApiError with Turkish message and problem details on HTTP error", async () => {
    const fetchFn: FetchFn = () =>
      Promise.resolve(jsonResponse({ title: "Not Found", status: 404, traceId: "abc" }, 404, "application/problem+json"));
    const error = await getVersion({}, createApiClient(fetchFn)).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(ApiError);
    const apiError = error as ApiError;
    expect(apiError.kind).toBe("http");
    expect(apiError.status).toBe(404);
    expect(apiError.message).toBe(tr.errors.notFound);
    expect(apiError.traceId).toBe("abc");
  });

  it("handles non-JSON error bodies", async () => {
    const fetchFn: FetchFn = () => Promise.resolve(new Response("boom", { status: 502 }));
    const error = (await getVersion({}, createApiClient(fetchFn)).catch((e: unknown) => e)) as ApiError;
    expect(error.message).toBe(tr.errors.unavailable);
    expect(error.problem).toBeUndefined();
  });

  it("maps fetch failures to a network error", async () => {
    const fetchFn: FetchFn = () => Promise.reject(new TypeError("Failed to fetch"));
    const error = (await getVersion({}, createApiClient(fetchFn)).catch((e: unknown) => e)) as ApiError;
    expect(error.kind).toBe("network");
    expect(error.status).toBe(0);
    expect(error.message).toBe(tr.errors.network);
  });

  it("rejects payloads with the wrong shape", async () => {
    const fetchFn: FetchFn = () => Promise.resolve(jsonResponse({ name: "x" }));
    const error = (await getVersion({}, createApiClient(fetchFn)).catch((e: unknown) => e)) as ApiError;
    expect(error.kind).toBe("invalid-response");
    expect(error.message).toBe(tr.errors.invalidResponse);
    expect(decodeVersionInfo(null)).toBeUndefined();
  });
});
