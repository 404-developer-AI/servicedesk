// The server sets XSRF-TOKEN as a non-httpOnly cookie on login. For unsafe
// verbs the frontend mirrors it into the X-XSRF-TOKEN header so the
// double-submit middleware can match them. GETs and setup/login are exempt.
//
// v0.1.1 — the customer portal rides its own cookie pair, so the token for
// /api/portal/auth/* and /api/portal/tickets/* lives in XSRF-TOKEN-PORTAL.
// Everything else — including the agent-side /api/portal/admin/* endpoints —
// uses the staff cookie. Keep these prefixes in sync with
// DoubleSubmitCsrfMiddleware.PortalRealmPrefixes.

const COOKIE_NAME = "XSRF-TOKEN";
const PORTAL_COOKIE_NAME = "XSRF-TOKEN-PORTAL";
const HEADER_NAME = "X-XSRF-TOKEN";

const PORTAL_REALM_PREFIXES = ["/api/portal/auth/", "/api/portal/tickets/"];

/// Which cookie guards this request path (defaults to the staff realm).
export function csrfCookieName(url?: string): string {
  if (!url) return COOKIE_NAME;
  const path = url.startsWith("http") ? new URL(url).pathname : url;
  return PORTAL_REALM_PREFIXES.some((p) => path.startsWith(p)) ? PORTAL_COOKIE_NAME : COOKIE_NAME;
}

export function readCsrfToken(url?: string): string | null {
  if (typeof document === "undefined") return null;
  const name = csrfCookieName(url);
  const match = document.cookie
    .split("; ")
    .find((row) => row.startsWith(`${name}=`));
  if (!match) return null;
  return decodeURIComponent(match.slice(name.length + 1));
}

export function csrfHeader(url?: string): Record<string, string> {
  const token = readCsrfToken(url);
  return token ? { [HEADER_NAME]: token } : {};
}
