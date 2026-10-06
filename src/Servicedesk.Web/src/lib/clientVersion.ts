/**
 * Client build identity + the global fetch wrapper that stamps it onto
 * every API call (and, since v0.1.22, refuses /api paths with dot-segments).
 *
 * The wrapper exists so the X-Client-Version header can never be forgotten:
 * the codebase has (deliberately) grown several request helpers next to the
 * central one in api.ts, plus raw fetches for uploads — patching
 * window.fetch once at boot covers all of them, including any helper added
 * in the future. The server's ClientVersionGateMiddleware rejects writes
 * from a stale bundle with 426; the wrapper turns that into a window event
 * the update module reacts to.
 *
 * Version resolution: the Docker build bakes APP_VERSION into the bundle
 * (same value the backend gets, so the strings match exactly). Outside
 * Docker the baked value is "dev" — then no header is sent (the gate stays
 * inert) and mismatch detection anchors on the first server version this
 * session observed instead.
 */

export const CLIENT_VERSION_HEADER = "X-Client-Version";
export const CLIENT_VERSION_OUTDATED_EVENT = "app:client-version-outdated";
/// v0.1.5 — fired when an authenticated /api call comes back 401, i.e. the
/// session expired, idled out (Security.Session.IdleTimeoutMinutes) or was
/// revoked. `detail.path` is the request path so the handler can tell a staff
/// session (→ /login) from a portal one (→ /portal/login). Auth-flow calls
/// are excluded (a 401 there is a normal login/probe outcome, not an expiry).
export const SESSION_EXPIRED_EVENT = "app:session-expired";

/// A 401 on these prefixes is part of the sign-in flow itself (wrong password,
/// the logged-out /me + /setup probes at boot), never a mid-session expiry, so
/// it must not bounce the user to the login page.
function isAuthFlowPath(path: string): boolean {
  return path.startsWith("/api/auth/") || path.startsWith("/api/portal/auth/");
}

function sameOriginApiPath(input: RequestInfo | URL): string | null {
  try {
    const raw =
      typeof input === "string" ? input : input instanceof URL ? input.href : input.url;
    const url = new URL(raw, window.location.origin);
    if (url.origin !== window.location.origin || !url.pathname.startsWith("/api/")) return null;
    return url.pathname;
  } catch {
    return null;
  }
}

const BAKED_VERSION: string =
  typeof __APP_VERSION__ !== "undefined" && __APP_VERSION__ ? __APP_VERSION__ : "dev";

let referenceVersion: string | null = BAKED_VERSION !== "dev" ? BAKED_VERSION : null;

/** The version this session compares server versions against, if known yet. */
export function getReferenceVersion(): string | null {
  return referenceVersion;
}

/** Dev fallback: adopt the first observed server version as our own. */
export function anchorReferenceVersion(serverVersion: string): void {
  if (!referenceVersion && serverVersion) {
    referenceVersion = serverVersion;
  }
}

function isSameOriginApiCall(input: RequestInfo | URL): boolean {
  try {
    const raw =
      typeof input === "string" ? input : input instanceof URL ? input.href : input.url;
    const url = new URL(raw, window.location.origin);
    return url.origin === window.location.origin && url.pathname.startsWith("/api/");
  } catch {
    return false;
  }
}

/// A "." or ".." path segment in the raw request string — also percent-encoded
/// ("%2e") or behind a backslash, which URL parsing treats as "/" for http(s).
/// API paths never contain one legitimately; one can only come from an
/// unencoded id/value interpolated into the URL, which the browser would then
/// resolve to a *different* endpoint (path traversal, e.g. an id of
/// "../../settings/x" turning a checklist call into a settings call).
const DOT_SEGMENT = /(^|[/\\])(\.|%2e){1,2}([/\\]|$)/i;

/** True when the raw path part (before ? / #) of an /api request string contains a dot-segment. */
export function hasDotSegment(raw: string): boolean {
  const path = raw.split(/[?#]/, 1)[0];
  return DOT_SEGMENT.test(path);
}

/** Install once in main.tsx, before anything issues a fetch. */
export function installClientVersionFetch(): void {
  const originalFetch = window.fetch.bind(window);

  window.fetch = async (input: RequestInfo | URL, init?: RequestInit) => {
    // Defence in depth for every request helper at once: refuse an /api
    // request whose raw string would be re-routed by dot-segment resolution.
    // Only string inputs carry the raw form; URL/Request are already resolved.
    if (typeof input === "string" && (input.startsWith("/api/") || isSameOriginApiCall(input)) && hasDotSegment(input)) {
      throw new TypeError("Blocked an API request with a dot-segment in its path.");
    }

    let options = init;
    if (BAKED_VERSION !== "dev" && isSameOriginApiCall(input)) {
      const headers = new Headers(
        init?.headers ?? (input instanceof Request ? input.headers : undefined),
      );
      if (!headers.has(CLIENT_VERSION_HEADER)) {
        headers.set(CLIENT_VERSION_HEADER, BAKED_VERSION);
      }
      options = { ...init, headers };
    }

    const response = await originalFetch(input, options);
    if (response.status === 426) {
      window.dispatchEvent(new Event(CLIENT_VERSION_OUTDATED_EVENT));
    }
    if (response.status === 401) {
      const path = sameOriginApiPath(input);
      if (path && !isAuthFlowPath(path)) {
        window.dispatchEvent(new CustomEvent(SESSION_EXPIRED_EVENT, { detail: { path } }));
      }
    }
    return response;
  };
}
