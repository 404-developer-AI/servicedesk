// Display metadata shared by the dropdown and the full-search page. New
// source kinds (companies, kennisbank, …) land here together with their
// label, icon, and click-through target.

import type { SearchHit } from "@/lib/api";

export type SearchKind =
  | "tickets"
  | "contacts"
  | "companies"
  | "settings"
  | (string & {});

export const KIND_LABELS: Record<string, string> = {
  tickets: "Tickets",
  contacts: "Contacten",
  companies: "Bedrijven",
  settings: "Settings",
};

export const KIND_ORDER: string[] = ["tickets", "contacts", "companies", "settings"];

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
    default:
      return "#";
  }
}
