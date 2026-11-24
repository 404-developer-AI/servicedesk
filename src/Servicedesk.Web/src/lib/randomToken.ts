const CHARS = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

/// 30-character alphanumeric secret for the "Generate" buttons on API-key
/// settings. Uses the browser CSPRNG; the slight modulo bias is irrelevant
/// for a 62^30 keyspace.
export function generateRandomToken(): string {
  const bytes = new Uint8Array(30);
  crypto.getRandomValues(bytes);
  return Array.from(bytes, (b) => CHARS[b % CHARS.length]).join("");
}
