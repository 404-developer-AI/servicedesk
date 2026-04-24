// Display metadata shared by the dropdown and the full-search page. New
// source kinds (companies, kennisbank, …) land here together with their
// label, icon, and click-through target.

import type { SearchHit } from "@/lib/api";

export type SearchKind =
  | "tickets"
  | "contacts"
  | "companies"
  | "settings"
  | "intake-templates"
  | "intake-submissions"
  | (string & {});

export const KIND_LABELS: Record<string, string> = {
  tickets: "Tickets",
  contacts: "Contacten",
  companies: "Bedrijven",
  settings: "Settings",
  "intake-templates": "Intake Templates",
  "intake-submissions": "Intake Submissions",
};

export const KIND_ORDER: string[] = [
  "tickets",
  "contacts",
  "companies",
  "settings",
  "intake-templates",
  "intake-submissions",
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
    default:
      return "#";
  }
}
