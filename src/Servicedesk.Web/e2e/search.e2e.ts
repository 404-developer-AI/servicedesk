import { expect, test } from "@playwright/test";
import { seedTicket } from "./support/ui";

/**
 * Flow 5 — global search: a word from a ticket's subject finds it in the
 * suggestions, and picking the suggestion opens the ticket.
 */
test("find a ticket with global search and open it", async ({ page, request }) => {
  const token = `zebra${Date.now().toString(36)}`;
  const ticket = await seedTicket(request, `E2E search target ${token}`);

  await page.goto("/");
  await page.getByTestId("global-search").click();
  await page.keyboard.type(token);

  const suggestion = page.getByRole("listbox", { name: "Suggestions" }).getByRole("option", { name: new RegExp(`#${ticket.number}\\b`) });
  await expect(suggestion).toContainText(token);
  await suggestion.click();

  await expect(page).toHaveURL(new RegExp(`/tickets/${ticket.id}`));
  await expect(page.getByRole("heading", { level: 1, name: `E2E search target ${token}` })).toBeVisible();
});
