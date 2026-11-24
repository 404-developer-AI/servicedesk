import { request } from "@/lib/http";

export type View = {
  id: string;
  userId: string;
  name: string;
  filtersJson: string;
  columns: string | null;
  sortOrder: number;
  isShared: boolean;
  displayConfigJson: string;
  createdUtc: string;
  updatedUtc: string;
  /// v0.1.18 — false = column layout locked to the view for every agent.
  allowUserColumns: boolean;
  /// v0.1.31 — Insights Rewind snapshots this view every interval.
  rewindTracked?: boolean;
};

export type ViewInput = {
  name: string;
  filtersJson?: string;
  columns?: string | null;
  sortOrder?: number;
  isShared?: boolean;
  displayConfigJson?: string;
  allowUserColumns?: boolean;
  rewindTracked?: boolean;
};

export const viewApi = {
  list: () => request<View[]>("GET", "/api/views"),
  // Admin-only: every view in the system, used by management surfaces.
  // Personal `list()` is filtered for admins too so the sidebar stays clean.
  listAll: () => request<View[]>("GET", "/api/views/all"),
  get: (id: string) => request<View>("GET", `/api/views/${id}`),
  create: (input: ViewInput) => request<View>("POST", "/api/views", input),
  update: (id: string, input: ViewInput) =>
    request<View>("PUT", `/api/views/${id}`, input),
  remove: (id: string) => request<void>("DELETE", `/api/views/${id}`),
};
