import { csrfHeader } from "@/lib/csrf";

/// The one HTTP layer for every `/api` call in the SPA. Feature modules
/// (`lib/*-api.ts`) build their endpoint maps on `request()` /
/// `requestResponse()` instead of rolling their own fetch wrapper, so error
/// shape, CSRF and body handling are decided once, here.
///
/// - Errors are always an `ApiError` (status, url, parsed JSON body). Show
///   them with `apiErrorMessage()`; read per-field validation errors with
///   `apiFieldErrors()`.
/// - Unsafe verbs always carry the CSRF header for the request's realm
///   (staff vs portal cookie, see `lib/csrf.ts`). Caller headers can add to
///   the defaults but never remove the CSRF header.
/// - `X-Client-Version` is stamped by the global fetch wrapper
///   (`lib/clientVersion.ts`), not here.

export class ApiError extends Error {
  readonly status: number;
  readonly url: string;
  /// Parsed JSON body of the error response (`{ error, message, errors… }`
  /// on most endpoints), or `null` when the body was empty / not JSON.
  readonly body: unknown;
  constructor(status: number, url: string, message: string, body: unknown = null) {
    super(message);
    this.name = "ApiError";
    this.status = status;
    this.url = url;
    this.body = body;
  }
}

/// Human-readable server message of an `ApiError`: `body.message`, else
/// `body.error` (the API's `{ error, message }` shape), else a ProblemDetails
/// `detail` / `title`. `null` when there is none — callers then show their
/// own fallback text.
export function apiErrorMessage(err: unknown): string | null {
  if (!(err instanceof ApiError) || err.body === null || typeof err.body !== "object") {
    return null;
  }
  const b = err.body as Record<string, unknown>;
  if (typeof b.message === "string" && b.message.length > 0) return b.message;
  if (typeof b.error === "string" && b.error.length > 0) return b.error;
  if (typeof b.detail === "string" && b.detail.length > 0) return b.detail;
  if (typeof b.title === "string" && b.title.length > 0) return b.title;
  return null;
}

/// Per-field validation errors (`{ errors: [{ field, message }] }`) as a
/// field → message map; `null` when the error carries none.
export function apiFieldErrors(err: unknown): Record<string, string> | null {
  if (!(err instanceof ApiError) || err.body === null || typeof err.body !== "object") {
    return null;
  }
  const list = (err.body as { errors?: unknown }).errors;
  if (!Array.isArray(list)) return null;
  const map: Record<string, string> = {};
  for (const e of list) {
    if (e && typeof e === "object" && typeof (e as { field?: unknown }).field === "string") {
      map[(e as { field: string }).field] = String((e as { message?: unknown }).message ?? "");
    }
  }
  return Object.keys(map).length > 0 ? map : null;
}

export type RequestOptions = {
  /// Extra headers; merged over the defaults (Accept / Content-Type).
  headers?: Record<string, string>;
  /// Accept header; default `application/json`.
  accept?: string;
  signal?: AbortSignal;
  /// Let the request outlive the page (unload-time saves).
  keepalive?: boolean;
  /// `"omit"` for the anonymous public endpoints (survey / intake / KB
  /// token links): no cookies are sent, so no CSRF header either.
  credentials?: RequestCredentials;
};

const SAFE_METHODS = new Set(["GET", "HEAD", "OPTIONS"]);

function buildInit(method: string, url: string, body: unknown, opts: RequestOptions | undefined): RequestInit {
  const verb = method.toUpperCase();
  const isForm = typeof FormData !== "undefined" && body instanceof FormData;
  const headers: Record<string, string> = {
    Accept: opts?.accept ?? "application/json",
    // FormData sets its own multipart Content-Type (with boundary).
    ...(body !== undefined && !isForm ? { "Content-Type": "application/json" } : {}),
    ...(opts?.headers ?? {}),
    // Last, so a caller header can never drop or replace it.
    ...(SAFE_METHODS.has(verb) || opts?.credentials === "omit" ? {} : csrfHeader(url)),
  };
  return {
    method: verb,
    credentials: opts?.credentials ?? "include",
    headers,
    body: body === undefined ? undefined : isForm ? (body as FormData) : JSON.stringify(body),
    signal: opts?.signal,
    keepalive: opts?.keepalive,
  };
}

async function toApiError(res: Response, url: string): Promise<ApiError> {
  let parsed: unknown = null;
  try {
    const text = await res.text();
    if (text.length > 0) parsed = JSON.parse(text);
  } catch {
    // Empty or non-JSON body: status + url still drive the fallback text.
  }
  return new ApiError(res.status, url, `${url} → ${res.status} ${res.statusText}`.trimEnd(), parsed);
}

/// Sends the request and returns the raw `Response` (for downloads, blobs,
/// headers). Throws `ApiError` on a non-2xx status.
export async function requestResponse(
  method: string,
  url: string,
  body?: unknown,
  opts?: RequestOptions,
): Promise<Response> {
  const res = await fetch(url, buildInit(method, url, body, opts));
  if (!res.ok) throw await toApiError(res, url);
  return res;
}

/// JSON request: parsed response body, or `undefined` for 204 / an empty
/// body. `body` may be a plain value (sent as JSON) or `FormData`.
export async function request<T>(
  method: string,
  url: string,
  body?: unknown,
  opts?: RequestOptions,
): Promise<T> {
  const res = await requestResponse(method, url, body, opts);
  if (res.status === 204) return undefined as T;
  const text = await res.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

/// Text to show for any failed action: the server's message for an
/// `ApiError`, the thrown text for a client-side `Error` (e.g. a form's own
/// validation), else the caller's fallback.
export function errorMessage(err: unknown, fallback: string): string {
  if (err instanceof ApiError) return apiErrorMessage(err) ?? fallback;
  return err instanceof Error && err.message ? err.message : fallback;
}
