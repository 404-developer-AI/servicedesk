import { createHmac } from "node:crypto";

/** RFC 6238 TOTP (SHA-1, 6 digits) — what an authenticator app shows. */
export const TOTP_STEP_SECONDS = 30;

const BASE32 = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

function base32Decode(input: string): Buffer {
  const clean = input.replace(/=+$/, "").replace(/\s+/g, "").toUpperCase();
  let bits = 0;
  let value = 0;
  const out: number[] = [];
  for (const ch of clean) {
    const idx = BASE32.indexOf(ch);
    if (idx < 0) throw new Error(`Invalid base32 character: ${ch}`);
    value = (value << 5) | idx;
    bits += 5;
    if (bits >= 8) {
      out.push((value >>> (bits - 8)) & 0xff);
      bits -= 8;
    }
  }
  return Buffer.from(out);
}

/** The timestep number of a moment (seconds since the epoch / 30). */
export function stepAt(ms: number): number {
  return Math.floor(ms / 1000 / TOTP_STEP_SECONDS);
}

/** The six-digit code for timestep `step`. */
export function totpForStep(secretBase32: string, step: number): string {
  const counter = Buffer.alloc(8);
  counter.writeBigUInt64BE(BigInt(step));
  const hmac = createHmac("sha1", base32Decode(secretBase32)).update(counter).digest();
  const offset = hmac[hmac.length - 1] & 0x0f;
  const binary = (hmac.readUInt32BE(offset) & 0x7fffffff) % 1_000_000;
  return binary.toString().padStart(6, "0");
}

/**
 * A code for a timestep the server has not accepted yet. The server rejects
 * a code whose step is at or before the last accepted one (replay guard), so
 * after `lastUsedStep` this waits for the next step boundary when needed.
 */
export async function freshTotp(secretBase32: string, lastUsedStep: number | null): Promise<{ code: string; step: number }> {
  let step = stepAt(Date.now());
  if (lastUsedStep !== null && step <= lastUsedStep) {
    const nextStepMs = (lastUsedStep + 1) * TOTP_STEP_SECONDS * 1000;
    await new Promise((r) => setTimeout(r, nextStepMs - Date.now() + 250));
    step = stepAt(Date.now());
  }
  return { code: totpForStep(secretBase32, step), step };
}
