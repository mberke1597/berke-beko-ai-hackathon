import { ApiError, parseProblemDetails } from "./errors.ts";

export type FetchFn = (input: string, init?: RequestInit) => Promise<Response>;

/** Validates an unknown JSON payload and returns it typed, or undefined if the shape is wrong. */
export type Decoder<T> = (value: unknown) => T | undefined;

export interface RequestOptions {
  signal?: AbortSignal;
}

export interface ApiClient {
  get<T>(path: string, decode: Decoder<T>, options?: RequestOptions): Promise<T>;
  post<T>(path: string, body: unknown, decode: Decoder<T>, options?: RequestOptions): Promise<T>;
  put<T>(path: string, body: unknown, decode: Decoder<T>, options?: RequestOptions): Promise<T>;
  delete<T>(path: string, decode: Decoder<T>, options?: RequestOptions): Promise<T>;
}

/** Decoder for `204 No Content` responses (and any body that is ignored). */
export const decodeNoContent: Decoder<null> = () => null;

function isJson(response: Response): boolean {
  const type = response.headers.get("content-type") ?? "";
  return type.includes("application/json") || type.includes("+json");
}

async function readJson(response: Response): Promise<unknown> {
  if (!isJson(response)) return undefined;
  try {
    return (await response.json()) as unknown;
  } catch {
    return undefined;
  }
}

/** Creates a small typed fetch client. `fetchFn` is injectable for tests. */
export function createApiClient(fetchFn: FetchFn = (input, init) => fetch(input, init)): ApiClient {
  async function request<T>(
    method: string,
    path: string,
    decode: Decoder<T>,
    options: RequestOptions = {},
    requestBody?: unknown,
  ): Promise<T> {
    let response: Response;
    try {
      const headers: Record<string, string> = { Accept: "application/json" };
      const init: RequestInit = { method, headers };
      if (requestBody !== undefined) {
        headers["Content-Type"] = "application/json";
        init.body = JSON.stringify(requestBody);
      }
      if (options.signal) init.signal = options.signal;
      response = await fetchFn(path, init);
    } catch (error) {
      if (error instanceof DOMException && error.name === "AbortError") throw error;
      throw new ApiError("network", 0, undefined, { cause: error });
    }

    const body = await readJson(response);
    if (!response.ok) {
      throw new ApiError("http", response.status, parseProblemDetails(body));
    }

    const decoded = decode(body);
    if (decoded === undefined) {
      throw new ApiError("invalid-response", response.status);
    }
    return decoded;
  }

  return {
    get: (path, decode, options) => request("GET", path, decode, options),
    post: (path, body, decode, options) => request("POST", path, decode, options, body),
    put: (path, body, decode, options) => request("PUT", path, decode, options, body),
    delete: (path, decode, options) => request("DELETE", path, decode, options),
  };
}

export const api = createApiClient();
