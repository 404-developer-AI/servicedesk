import { expect, test, type Page } from "@playwright/test";
import { selectUnder } from "./support/ui";

/**
 * Flows 2–3 — an agent creates a ticket through the new-ticket drawer, then
 * adds an internal note and changes the status on the ticket page; both
 * survive a reload (they reached the server, not just the screen).
 */
test.describe.configure({ mode: "serial" });

const subject = `E2E printer offline ${Date.now().toString(36)}`;
let ticketUrl = "";

/** Opens the side panel when the viewport starts it collapsed. */
async function sidePanel(page: Page) {
  const expand = page.getByRole("button", { name: "Expand side panel" });
  if (await expand.isVisible()) await expand.click();
  await expect(page.getByRole("button", { name: "Collapse side panel" })).toBeVisible();
}


test("create a ticket from the new-ticket drawer", async ({ page }) => {
  await page.goto("/tickets");
  await page.getByRole("button", { name: "New ticket" }).click();

  await page.getByPlaceholder("Brief summary of the issue").fill(subject);
  await page.getByText("Select a contact…").click();
  await page.keyboard.type("Pat");
  await page.getByRole("button", { name: /Pat Requester/ }).click();
  // Picking a requester always asks which company the ticket belongs to.
  const companyDialog = page.getByRole("dialog", { name: "Select a company for this ticket" });
  await companyDialog.getByText("Example Corp").click();
  await companyDialog.getByRole("button", { name: "Select" }).click();
  await expect(companyDialog).toBeHidden();

  await page.getByRole("combobox").filter({ hasText: "Select queue…" }).click();
  await page.getByRole("option", { name: "Support" }).click();

  await page.getByRole("button", { name: "Create ticket" }).click();

  await expect(page).toHaveURL(/\/tickets\/[0-9a-f-]{36}/);
  await expect(page.getByRole("heading", { level: 1, name: subject })).toBeVisible();
  ticketUrl = page.url();
});

test("add an internal note and change the status", async ({ page }) => {
  await page.goto(ticketUrl);
  const note = `E2E checked the printer queue ${Date.now().toString(36)}`;

  await page.getByRole("button", { name: /^Note/ }).click();
  await page.locator(".ProseMirror").last().click();
  await page.keyboard.type(note);
  await page.getByRole("button", { name: "Add note" }).click();
  await expect(page.getByText(note)).toBeVisible();

  await sidePanel(page);
  await selectUnder(page, "Status").click();
  // Options read "<name> (<category>)".
  await page.getByRole("option", { name: /^Open \(/ }).click();
  await expect(selectUnder(page, "Status")).toHaveText(/Open/);

  await page.reload();
  await expect(page.getByText(note)).toBeVisible();
  await sidePanel(page);
  await expect(selectUnder(page, "Status")).toHaveText(/Open/);
});
