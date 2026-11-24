import { expect, test } from "@playwright/test";
import { seedTicket, selectUnder } from "./support/ui";

/**
 * Flow 4 — bulk edit from the ticket list: select two tickets, set the
 * priority for both through the shared single-ticket rules (none skipped),
 * and see the list reflect it.
 */
test("bulk-edit the priority of two selected tickets", async ({ page, request }) => {
  const tag = Date.now().toString(36);
  const subjects = [`E2E bulk A ${tag}`, `E2E bulk B ${tag}`];
  for (const subject of subjects) await seedTicket(request, subject);

  await page.goto("/tickets");
  for (const subject of subjects) {
    await page.getByRole("row").filter({ hasText: subject }).getByRole("checkbox").check();
  }
  await page.getByRole("button", { name: /Bulk edit/ }).click();

  const dialog = page.getByRole("dialog", { name: "Bulk edit" });
  await selectUnder(dialog, "Priority").click();
  await page.getByRole("option", { name: /^High/ }).click();
  await dialog.getByRole("button", { name: "Apply to 2 tickets" }).click();

  const result = page.getByRole("dialog", { name: /Bulk edit — result/ });
  await expect(result).toContainText("2 of 2 tickets updated");
  await result.getByRole("button", { name: "Done" }).click();

  for (const subject of subjects) {
    await expect(page.getByRole("row").filter({ hasText: subject })).toContainText("High");
  }
});
