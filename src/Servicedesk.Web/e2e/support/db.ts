import { execFileSync } from "node:child_process";

/**
 * Direct SQL against the e2e database, for the one step a browser cannot
 * do: receiving mail. `E2E_PG_URL` is a libpq URI of the fresh e2e database
 * (CI: the job's postgres service; locally: the sd_e2e database);
 * `E2E_PSQL` overrides the psql binary (e.g. on Windows). Never point this
 * at a real database — the suite only runs on a fresh install.
 *
 * Values go in as psql variables (`:'name'` in the statement — quoted by
 * psql), never by string concatenation; the statement is read from stdin
 * because `psql -c` does not interpolate variables.
 */
export function pgUrl(): string | undefined {
  return process.env.E2E_PG_URL;
}

export function sql(statement: string, vars: Record<string, string> = {}): string {
  const url = pgUrl();
  if (!url) throw new Error("E2E_PG_URL is not set");
  const args = [url, "-X", "-q", "-v", "ON_ERROR_STOP=1", "-tA"];
  for (const [k, v] of Object.entries(vars)) args.push("-v", `${k}=${v}`);
  return execFileSync(process.env.E2E_PSQL ?? "psql", args, { encoding: "utf8", input: statement }).trim();
}
