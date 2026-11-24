import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import path from "node:path";

/**
 * What the seed step hands to the specs. Local to the run (gitignored): the
 * credentials belong to a throw-away admin on a fresh e2e install, never to
 * a real account.
 */
export type E2eState = {
  email: string;
  password: string;
  totpSecret: string;
  /** The last timestep the server accepted (enrolment or a login). */
  lastUsedStep: number;
  requesterEmail: string;
  /** Ids of the seeded data, for specs that create their own tickets. */
  ids: { contact: string; company: string; queue: string; newStatus: string; normalPriority: string; highPriority: string };
};

const DIR = path.join(import.meta.dirname, "..", ".state");
export const STATE_FILE = path.join(DIR, "seed.json");
/** Signed-in browser storage (cookies) written by the login step. */
export const AUTH_FILE = path.join(DIR, "admin.storage.json");

export function writeState(state: E2eState): void {
  mkdirSync(DIR, { recursive: true });
  writeFileSync(STATE_FILE, JSON.stringify(state, null, 2));
}

export function readState(): E2eState {
  return JSON.parse(readFileSync(STATE_FILE, "utf8")) as E2eState;
}
