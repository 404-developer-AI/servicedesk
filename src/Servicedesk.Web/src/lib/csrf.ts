// The server sets XSRF-TOKEN as a non-httpOnly cookie on login. For unsafe
// verbs the frontend mirrors it into the X-XSRF-TOKEN header so the
// double-submit middleware can match them. GETs and setup/login are exempt.

const COOKIE_NAME = "XSRF-TOKEN";
const HEADER_NAME = "X-XSRF-TOKEN";

export function readCsrfToken(): string | null {
  if (typeof document === "undefined") return null;
  const match = document.cookie
    .split("; ")
    .find((row) => row.startsWith(`${COOKIE_NAME}=`));
  if (!match) return null;
  return decodeURIComponent(match.slice(COOKIE_NAME.length + 1));
}

export function csrfHeader(): Record<string, string> {
  const token = readCsrfToken();
  return token ? { [HEADER_NAME]: token } : {};
}
