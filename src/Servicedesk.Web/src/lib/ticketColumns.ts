import type { TicketListItem } from "@/lib/ticket-api";

/// v0.1.18 — the single registry of ticket-list column ids + labels, shared
/// by the table, the agent column picker and the view editor (it used to be
/// copied three times). Order here is only the order in which *hidden*
/// columns are offered; visible columns render in the saved layout order.
export const TICKET_COLUMNS: ReadonlyArray<{ id: string; label: string }> = [
  { id: "number", label: "Number" },
  { id: "subject", label: "Subject" },
  { id: "requester", label: "Requester" },
  { id: "companyName", label: "Company" },
  { id: "queueName", label: "Queue" },
  { id: "statusName", label: "Status" },
  { id: "priorityName", label: "Priority" },
  { id: "categoryName", label: "Category" },
  { id: "assigneeEmail", label: "Assignee" },
  { id: "createdUtc", label: "Created" },
  { id: "updatedUtc", label: "Updated" },
  { id: "dueUtc", label: "Due" },
  { id: "pendingTillUtc", label: "Pending till" },
  { id: "timeLogged", label: "Time logged" },
];

const KNOWN = new Set(TICKET_COLUMNS.map((c) => c.id));

export function columnLabel(id: string): string {
  return TICKET_COLUMNS.find((c) => c.id === id)?.label ?? id;
}

/// Keeps known ids only, first occurrence wins, saved order preserved.
export function normalizeLayout(ids: readonly string[]): string[] {
  const seen = new Set<string>();
  const out: string[] = [];
  for (const raw of ids) {
    const id = raw.trim();
    if (!KNOWN.has(id) || seen.has(id)) continue;
    seen.add(id);
    out.push(id);
  }
  return out;
}

/// Picker order: the visible columns in layout order, then every hidden
/// column in registry order (so a column added in a later release simply
/// shows up at the end, unchecked).
export function pickerOrder(visible: readonly string[]): string[] {
  const shown = normalizeLayout(visible);
  const rest = TICKET_COLUMNS.map((c) => c.id).filter((id) => !shown.includes(id));
  return [...shown, ...rest];
}

// ---- Per-view "Columns" search ----

/// Text a column contributes to the Columns search — exactly what the cell
/// shows. Only text columns are searchable (dates/durations are not). Keep in
/// lockstep with the server whitelist (TicketColumnSearch.FieldMap), which
/// handles the fallback when the list is truncated.
export const COLUMN_SEARCH_TEXT: Readonly<Record<string, (t: TicketListItem) => string>> = {
  number: (t) => `#${t.number}`,
  subject: (t) => t.subject,
  requester: (t) =>
    [t.requesterFirstName, t.requesterLastName, t.requesterEmail].filter(Boolean).join(" "),
  companyName: (t) => t.companyName ?? "",
  queueName: (t) => t.queueName,
  statusName: (t) => t.statusName,
  priorityName: (t) => t.priorityName,
  categoryName: (t) => t.categoryName ?? "",
  assigneeEmail: (t) => t.assigneeEmail ?? "",
};

export function searchableColumns(visible: readonly string[]): string[] {
  return visible.filter((id) => id in COLUMN_SEARCH_TEXT);
}

export const MAX_SEARCH_TOKENS = 8;

/// Case- and accent-insensitive form ("Café" → "cafe"). Keeps string length
/// for the characters that matter so highlight offsets stay usable.
export function foldText(s: string): string {
  return s.normalize("NFD").replace(/\p{M}/gu, "").toLowerCase();
}

export function searchTokens(term: string): string[] {
  return foldText(term).split(/\s+/).filter(Boolean).slice(0, MAX_SEARCH_TOKENS);
}

/// Every token must appear in at least one of the given columns.
export function rowMatchesColumns(
  row: TicketListItem,
  tokens: readonly string[],
  columns: readonly string[],
): boolean {
  if (tokens.length === 0) return true;
  const haystacks = columns
    .map((id) => COLUMN_SEARCH_TEXT[id])
    .filter(Boolean)
    .map((fn) => foldText(fn(row)));
  return tokens.every((tok) => haystacks.some((h) => h.includes(tok)));
}

/// Character ranges in `text` to highlight for the given (folded) tokens.
/// Folding happens per character, so offsets map back onto the original.
export function highlightRanges(text: string, tokens: readonly string[]): Array<[number, number]> {
  if (!text || tokens.length === 0) return [];
  // Fold char by char so index i in `folded` is index i in `text` (combining
  // marks are dropped, never added, for the single code units we see here).
  const chars = Array.from(text);
  const folded = chars.map((c) => foldText(c) || c).map((c) => (c.length === 1 ? c : c[0]));
  const hay = folded.join("");
  const ranges: Array<[number, number]> = [];
  for (const tok of tokens) {
    let from = 0;
    for (;;) {
      const at = hay.indexOf(tok, from);
      if (at < 0) break;
      ranges.push([at, at + tok.length]);
      from = at + tok.length;
    }
  }
  if (ranges.length === 0) return [];
  ranges.sort((a, b) => a[0] - b[0]);
  const merged: Array<[number, number]> = [ranges[0]];
  for (const r of ranges.slice(1)) {
    const last = merged[merged.length - 1];
    if (r[0] <= last[1]) last[1] = Math.max(last[1], r[1]);
    else merged.push(r);
  }
  // Ranges are in code-point positions; convert to string offsets.
  const offsets: number[] = [0];
  for (const c of chars) offsets.push(offsets[offsets.length - 1] + c.length);
  return merged.map(([s, e]) => [offsets[s], offsets[e]]);
}
