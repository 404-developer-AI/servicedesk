import { expect, test as setup } from "@playwright/test";
import { freshTotp } from "./support/totp";
import { AUTH_FILE, readState, writeState } from "./support/state";

/**
 * Flow 1 — staff sign-in through the real login page: password, then the
 * TOTP challenge. The signed-in cookies are saved for the other specs, so
 * the suite signs in once (a TOTP step is accepted only once).
 */
setup("sign in with password and TOTP", async ({ page }) => {
  const state = readState();

  await page.goto("/login");
  await page.locator('input[autocomplete="username"]').fill(state.email);
  await page.locator('input[autocomplete="current-password"]').fill(state.password);
  await page.locator('button[type="submit"]').click();

  const codeInput = page.locator('input[autocomplete="one-time-code"]');
  await expect(codeInput).toBeVisible();
  const { code, step } = await freshTotp(state.totpSecret, state.lastUsedStep);
  await codeInput.fill(code);
  await page.locator('button[type="submit"]').click();

  await expect(page).not.toHaveURL(/\/login/);
  // Global requirement: version and server time are visible in the UI.
  await expect(page.getByTestId("sidebar-version")).toContainText(/v\d+\.\d+\.\d+/);
  await expect(page.getByTestId("sidebar-server-time")).toContainText(/\d{2}:\d{2}/);

  writeState({ ...state, lastUsedStep: step });
  await page.context().storageState({ path: AUTH_FILE });
});
