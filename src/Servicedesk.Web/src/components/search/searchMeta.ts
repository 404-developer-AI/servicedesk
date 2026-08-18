// Display metadata shared by the dropdown and the full-search page. New
// source kinds (companies, kennisbank, …) land here together with their
// label, icon, and click-through target.

import type { SearchHit } from "@/lib/api";

export type SearchKind =
  | "tickets"
  | "kb-articles"
  | "contacts"
  | "companies"
  | "assets"
  | "surveys"
  | "timesheet"
  | "settings"
  | "intake-templates"
  | "intake-submissions"
  | "triggers"
  | "adsolut-sales-receipts"
  | "adsolut-orders"
  | "adsolut-articles"
  | "adsolut-contracts"
  | "m365-mailboxes"
  | "sophos-tenants"
  | "employee-feedback"
  | "checklist-items"
  | "checklist-templates"
  | (string & {});

export const KIND_LABELS: Record<string, string> = {
  tickets: "Tickets",
  "kb-articles": "Knowledge Base",
  contacts: "Contacts",
  companies: "Companies",
  assets: "Assets",
  surveys: "Surveys",
  timesheet: "Timesheet",
  settings: "Settings",
  "intake-templates": "Intake Templates",
  "intake-submissions": "Intake Submissions",
  triggers: "Triggers",
  "adsolut-sales-receipts": "Verkoopbonnen",
  "adsolut-orders": "Orders",
  "adsolut-articles": "Contract Articles",
  "adsolut-contracts": "Contracts",
  "m365-mailboxes": "Microsoft 365 mailboxes",
  "sophos-tenants": "Sophos spam filter",
  "employee-feedback": "Employee Feedback",
  "checklist-items": "Checklist items",
  "checklist-templates": "Checklist templates",
};

export const KIND_ORDER: string[] = [
  "tickets",
  "kb-articles",
  "contacts",
  "companies",
  "assets",
  "surveys",
  "timesheet",
  "settings",
  "intake-templates",
  "intake-submissions",
  "triggers",
  "adsolut-sales-receipts",
  "adsolut-orders",
  "adsolut-articles",
  "adsolut-contracts",
  "m365-mailboxes",
  "sophos-tenants",
  "employee-feedback",
  "checklist-items",
  "checklist-templates",
];

export function labelForKind(kind: string): string {
  return KIND_LABELS[kind] ?? kind;
}

/** Navigation target for a hit. Unknown kinds fall back to a no-op "#". */
export function hitHref(hit: SearchHit): string {
  switch (hit.kind) {
    case "tickets":
      return `/tickets/${hit.entityId}`;
    case "contacts":
      return `/contacts/${hit.entityId}`;
    case "companies":
      return `/companies/${hit.entityId}`;
    case "settings":
      return hit.meta?.path ?? "/settings";
    case "intake-templates":
      return `/settings/intake-forms?template=${hit.entityId}`;
    case "intake-submissions": {
      // Submissions sit inside a ticket's timeline; route to that ticket
      // and deep-link to the submission event so the scroll lands on it.
      const ticketId = hit.meta?.ticketId;
      const eventId = hit.meta?.eventId;
      if (!ticketId) return "#";
      const hash = eventId ? `#event-${eventId}` : "";
      return `/tickets/${ticketId}${hash}`;
    }
    case "triggers":
      return `/settings/triggers/${hit.entityId}`;
    case "kb-articles":
      return `/kb/articles/${hit.entityId}`;
    case "adsolut-sales-receipts":
      // The receipts live in the Timesheet → Adsolut tab (no standalone
      // detail page); land the user on the timesheet area.
      return "/timesheet";
    case "adsolut-orders":
      // Orders open in the Orders overview; deep-link the order id so the
      // overview can expand/scroll to it.
      return `/orders?orderId=${hit.entityId}`;
    case "adsolut-articles":
      // Articles live in the Contract Articles list (no standalone detail
      // page); land the user on the list and seed the search with the code.
      return `/contracts/articles?q=${encodeURIComponent(hit.meta?.code ?? "")}`;
    case "adsolut-contracts":
      // Contracts live in the Contracts overview list; no standalone detail
      // page — land the user on the overview.
      return "/contracts/overview";
    case "m365-mailboxes":
      // A mailbox hit links to its owning company's M365 detail page.
      return `/contracts/m365-matching/${hit.entityId}`;
    case "sophos-tenants":
      // A Sophos tenant hit links to its matched company's M365 detail page,
      // where the spam-filter (Protected/Unprotected) column lives.
      return `/contracts/m365-matching/${hit.entityId}`;
    case "employee-feedback":
      return "/feedback";
    case "checklist-items":
      // The item lives on a ticket; open the ticket with the checklist
      // panel focused on that checklist.
      return `/tickets/${hit.entityId}?checklist=${hit.meta?.checklistId ?? ""}`;
    case "checklist-templates":
      return `/settings/tickets?tab=checklists&template=${hit.entityId}`;
    default:
      return "#";
  }
}
