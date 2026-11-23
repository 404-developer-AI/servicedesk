import { request } from "@/lib/http";

// ---- Global Search ----

export type SearchHit = {
  kind: string;
  entityId: string;
  title: string;
  snippet: string | null;
  rank: number;
  meta: Record<string, string | null> | null;
};
export type SearchGroup = {
  kind: string;
  hits: SearchHit[];
  totalInGroup: number;
  hasMore: boolean;
  /** Tickets quick-mode only: total matches per partition ("open"/"closed"). */
  partitionTotals: Record<string, number> | null;
  /** v0.1.26 — tickets: message texts were searched in recent messages only
   *  and older ones exist; show "N+" and offer the deep search. */
  partial?: boolean;
};
export type SearchDropdownResponse = {
  groups: SearchGroup[];
  totalHits: number;
  availableKinds: string[];
  minQueryLength: number;
  debounceMs: number;
};
export type SearchSort = "relevance" | "newest" | "oldest" | "status";
export type SearchFullResponse = {
  group: SearchGroup;
  availableKinds: string[];
  minQueryLength: number;
};
export const searchApi = {
  quick: (q: string, limit = 8, deep = false) =>
    request<SearchDropdownResponse>(
      "GET",
      `/api/search?q=${encodeURIComponent(q)}&limit=${limit}${deep ? "&deep=true" : ""}`,
    ),
  full: (q: string, type: string, limit = 25, offset = 0, sort: SearchSort = "relevance", deep = false) =>
    request<SearchFullResponse>(
      "GET",
      `/api/search/full?q=${encodeURIComponent(q)}&type=${encodeURIComponent(type)}&limit=${limit}&offset=${offset}&sort=${sort}${deep ? "&deep=true" : ""}`,
    ),
};
