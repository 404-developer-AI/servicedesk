import { defineConfig, devices } from "@playwright/test";

/**
 * End-to-end smoke flows (e2e/*.e2e.ts) against a running app on a FRESH
 * install: the seed step creates the first admin through the setup wizard
 * and refuses an install that already has users. CI points this at the
 * Docker image booted on an empty database (ci.yml → docker job); locally,
 * see ARCHITECTURE.md → Testing → End-to-end.
 *
 * Projects run in order: seed (API) → login (UI, saves the session) → flows.
 * One worker: the flows share one install and one TOTP-protected admin.
 */
export default defineConfig({
  testDir: "./e2e",
  outputDir: "./e2e/.results",
  fullyParallel: false,
  workers: 1,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  timeout: 60_000,
  expect: { timeout: 10_000 },
  reporter: process.env.CI ? [["list"], ["html", { open: "never", outputFolder: "e2e/.report" }]] : "list",
  use: {
    baseURL: process.env.E2E_BASE_URL ?? "http://localhost:8080",
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
  },
  projects: [
    { name: "seed", testMatch: /seed\.setup\.ts/ },
    { name: "login", testMatch: /login\.setup\.ts/, dependencies: ["seed"], use: { ...devices["Desktop Chrome"] } },
    {
      name: "flows",
      testMatch: /.*\.e2e\.ts/,
      dependencies: ["login"],
      use: { ...devices["Desktop Chrome"], storageState: "e2e/.state/admin.storage.json" },
    },
  ],
});
