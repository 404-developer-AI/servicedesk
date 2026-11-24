import type { Locator, Page } from "@playwright/test";
import { api } from "./api";
import { readState } from "./state";
import type { APIRequestContext } from "@playwright/test";

/**
 * The select (role=combobox) that follows a field label — the side panel,
 * the bulk dialog and the new-ticket drawer all render "label, then select".
 */
export function selectUnder(scope: Page | Locator, label: string): Locator {
  return scope
    .getByText(label, { exact: true })
    .locator("xpath=following-sibling::*[1]/descendant-or-self::*[@role='combobox']")
    .first();
}

/** Seeds a ticket through the API (New, Normal, in the seeded queue). */
export async function seedTicket(request: APIRequestContext, subject: string): Promise<{ id: string; number: number }> {
  const { ids } = readState();
  const res = await api<{ ticket: { id: string; number: number } }>(request, "POST", "/api/tickets", {
    subject,
    bodyText: "Seeded by the e2e suite.",
    requesterContactId: ids.contact,
    queueId: ids.queue,
    statusId: ids.newStatus,
    priorityId: ids.normalPriority,
  });
  return res.ticket;
}
