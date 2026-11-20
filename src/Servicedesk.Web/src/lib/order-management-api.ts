import { request } from "@/lib/http";
import type { IntegrationAuditPage } from "@/lib/api";

/// v0.1.59 — Order Management: TD SYNNEX (EU Resellers API) orders mirrored
/// by the server. Every agent route is gated server-side by the per-user
/// `order_management_enabled` flag (403 when off, admins included); ticket
/// routes also check queue access on the ticket (404 outside your queues).

export type TdOrderStatus = "OPEN" | "INVESTIGATION" | "SHIPPING" | "REJECTED" | "COMPLETED" | (string & {});
export type TdOrderBucket = "active" | "late" | "due" | "open" | "investigation" | "shipping" | "completed" | "rejected" | "all";

export interface TdOrderHeader {
  id: string;
  orderNumber: string;
  customerOrderNumber: string | null;
  endUserOrderNumber: string | null;
  orderType: string | null;
  status: TdOrderStatus | null;
  statusDetail: string | null;
  createdBy: string | null;
  createdUtc: string | null;
  modifiedUtc: string | null;
  shipToName: string | null;
  shipToCity: string | null;
  shipToCountry: string | null;
  totalNet: number | null;
  currency: string | null;
  lineCount: number;
  quantityTotal: number;
  /** Earliest open ship date (yyyy-MM-dd) — late when before today. */
  firstShipDate: string | null;
  /** Latest open ship date (yyyy-MM-dd) — when everything should have shipped. */
  etaDate: string | null;
  isLate: boolean;
  lastSyncedUtc: string;
  lastChangedUtc: string | null;
  linkedTicketCount: number;
}

export interface TdOrderSplit {
  quantity: number | null;
  shipDate: string | null;
  shipDateRaw: string | null;
  shipDateDetails: string | null;
  status: string | null;
  statusDetail: string | null;
  isDone: boolean;
  deliveryNotes: string[];
  invoices: string[];
}

export interface TdOrderLine {
  lineId: string | null;
  tdPartNumber: string | null;
  manufacturerPartNumber: string | null;
  manufacturer: string | null;
  description: string | null;
  isEol: boolean;
  unitNetPrice: number | null;
  quantity: number | null;
  totalNetPrice: number | null;
  splits: TdOrderSplit[];
}

export interface TdOrderChange {
  id: number;
  detectedUtc: string;
  summary: string;
  items: { kind: string; text: string; before: string | null; after: string | null }[];
}

export interface TdOrderDetail {
  header: TdOrderHeader;
  shipTo: {
    companyName: string | null;
    contactName: string | null;
    contactEmail: string | null;
    phone: string | null;
    line1: string | null;
    line2: string | null;
    line3: string | null;
    zip: string | null;
    city: string | null;
    country: string | null;
  } | null;
  payment: {
    subtotal: number | null;
    tax: number | null;
    freight: number | null;
    otherFees: number | null;
    total: number | null;
    currency: string | null;
    paymentTerms: string | null;
  } | null;
  lines: TdOrderLine[];
  changes: TdOrderChange[];
}

export interface TdOrderLinkedTicket {
  id: string;
  number: number;
  subject: string;
  statusName: string;
  statusColor: string;
}

export interface TdOrderSummary {
  today: string;
  stats: {
    open: number;
    investigation: number;
    shipping: number;
    late: number;
    dueThisWeek: number;
    completedLast30Days: number;
    openValue: number;
    currency: string | null;
  };
  sync: { enabled: boolean; lastSuccessUtc: string | null; healthy: boolean; orderCount: number };
}

export interface TdOrderListResponse {
  items: TdOrderHeader[];
  total: number;
  page: number;
  pageSize: number;
  bucket: TdOrderBucket;
}

export const orderManagementApi = {
  summary: () => request<TdOrderSummary>("GET", "/api/order-management/summary"),
  list: (p: { search?: string; bucket?: TdOrderBucket; sort?: string; dir?: "asc" | "desc"; page?: number; pageSize?: number }) => {
    const qs = new URLSearchParams();
    if (p.search) qs.set("search", p.search);
    if (p.bucket) qs.set("bucket", p.bucket);
    if (p.sort) qs.set("sort", p.sort);
    if (p.dir) qs.set("dir", p.dir);
    if (p.page) qs.set("page", String(p.page));
    if (p.pageSize) qs.set("pageSize", String(p.pageSize));
    return request<TdOrderListResponse>("GET", `/api/order-management/orders?${qs.toString()}`);
  },
  picker: (q: string, limit = 10) =>
    request<{ items: TdOrderHeader[] }>(
      "GET",
      `/api/order-management/orders/picker?q=${encodeURIComponent(q)}&limit=${limit}`,
    ),
  detail: (id: string) =>
    request<{ order: TdOrderDetail; linkedTickets: TdOrderLinkedTicket[]; hiddenLinkedTicketCount: number }>(
      "GET",
      `/api/order-management/orders/${id}`,
    ),
  sync: () =>
    request<{ success: boolean; status: string; seen: number; changed: number }>("POST", "/api/order-management/sync"),

  forTicket: (ticketId: string) =>
    request<{ items: TdOrderDetail[] }>("GET", `/api/tickets/${ticketId}/tdsynnex-orders`),
  link: (ticketId: string, orderId: string) =>
    request<{ ok: boolean; created: boolean }>("POST", `/api/tickets/${ticketId}/tdsynnex-orders/${orderId}`),
  unlink: (ticketId: string, orderId: string) =>
    request<{ ok: boolean }>("DELETE", `/api/tickets/${ticketId}/tdsynnex-orders/${orderId}`),
};

/// Under the ticket's query tree so the TicketUpdated push refreshes the chip.
export const ticketOrdersKey = (ticketId: string) => ["ticket", ticketId, "tdsynnex-orders"] as const;

// ---- admin (Settings → Integrations → TD SYNNEX) ----

export type TdSynnexState = "disabled" | "not-configured" | "ready";

export interface TdSynnexStatus {
  state: TdSynnexState;
  enabled: boolean;
  baseUrl: string;
  tokenUrl: string;
  clientIdConfigured: boolean;
  clientSecretConfigured: boolean;
  syncIntervalMinutes: number;
  initialSyncDays: number;
  activeRefreshMinutes: number;
  lastCheckedUtc: string | null;
  lastSuccessUtc: string | null;
  lastActiveRefreshUtc: string | null;
  lastStatus: string | null;
  lastError: string | null;
  lastSeen: number;
  lastChanged: number;
  orderCount: number;
}

export const tdSynnexAdminApi = {
  status: () => request<TdSynnexStatus>("GET", "/api/admin/integrations/tdsynnex/status"),
  setCredentials: (clientId: string | null, clientSecret: string | null) =>
    request<void>("PUT", "/api/admin/integrations/tdsynnex/credentials", { clientId, clientSecret }),
  deleteCredentials: () => request<void>("DELETE", "/api/admin/integrations/tdsynnex/credentials"),
  setEnabled: (enabled: boolean) =>
    request<{ enabled: boolean }>("PUT", "/api/admin/integrations/tdsynnex/enabled", { enabled }),
  setConfig: (cfg: {
    baseUrl?: string;
    tokenUrl?: string;
    syncIntervalMinutes?: number;
    initialSyncDays?: number;
    activeRefreshMinutes?: number;
  }) => request<void>("PUT", "/api/admin/integrations/tdsynnex/config", cfg),
  test: () =>
    request<{ success: boolean; totalResults: number; listKey: string | null }>(
      "POST",
      "/api/admin/integrations/tdsynnex/test-connection",
    ),
  sync: () =>
    request<{ success: boolean; status: string; error: string | null; seen: number; upserted: number; changed: number }>(
      "POST",
      "/api/admin/integrations/tdsynnex/sync",
    ),
  auditLog: (cursor: number | null, limit = 50) => {
    const qs = new URLSearchParams();
    if (cursor !== null) qs.set("cursor", String(cursor));
    qs.set("limit", String(limit));
    return request<IntegrationAuditPage>("GET", `/api/admin/integrations/tdsynnex/audit?${qs.toString()}`);
  },
};
