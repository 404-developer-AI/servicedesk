import { request } from "@/lib/http";

// ---- Adsolut catalogue products (VK Werkuren article flags) — v0.0.84 ---

export type AdsolutCatalogueProduct = {
  id: string;
  code: string | null;
  name: string | null;
  serviceProduct: boolean;
  isActive: boolean;
  blocked: boolean;
  endOfSeries: boolean;
  // Admin flag: does this product's line total count toward the receipt's VK
  // Werkuren (and thus the timesheet matching)?
  countsAsWorkHours: boolean;
  workHoursUpdatedUtc: string | null;
  workHoursUpdatedByEmail: string | null;
  adsolutLastModified: string | null;
  syncedUtc: string;
};
export type AdsolutCatalogueProductListResponse = {
  items: AdsolutCatalogueProduct[];
  total: number;
  page: number;
  pageSize: number;
};
export type AdsolutCatalogueProductsState = {
  enabled: boolean;
  intervalMinutes: number;
  totalMirrored: number;
  workHoursCount: number;
  lastFullSyncUtc: string | null;
  lastDeltaSyncUtc: string | null;
  lastError: string | null;
  lastErrorUtc: string | null;
  productsSeen: number;
  productsUpserted: number;
  updatedUtc: string | null;
  nextSyncUtc: string | null;
};
/// Admin-facing manager for which Adsolut catalogue products count as billable
/// work hours (drives the Timesheet → Adsolut "VK Werkuren" matching).
export const adsolutCatalogueProductsApi = {
  list: (
    search: string,
    page: number,
    pageSize = 50,
    sort = "code",
    dir: "asc" | "desc" = "asc",
    activeOnly = false,
    workHours: "all" | "yes" | "no" = "all",
  ) => {
    const qs = new URLSearchParams({
      page: String(page),
      pageSize: String(pageSize),
      sort,
      dir,
    });
    if (search.trim()) qs.set("search", search.trim());
    if (activeOnly) qs.set("activeOnly", "true");
    if (workHours !== "all") qs.set("workHours", workHours);
    return request<AdsolutCatalogueProductListResponse>(
      "GET",
      `/api/timesheet/adsolut/catalogue-products?${qs.toString()}`,
    );
  },
  state: () =>
    request<AdsolutCatalogueProductsState>("GET", "/api/timesheet/adsolut/catalogue-products/state"),
  setWorkHours: (id: string, countsAsWorkHours: boolean) =>
    request<{ id: string; countsAsWorkHours: boolean }>(
      "POST",
      `/api/timesheet/adsolut/catalogue-products/${id}/work-hours`,
      { countsAsWorkHours },
    ),
  sync: () =>
    request<void>("POST", "/api/timesheet/adsolut/catalogue-products/sync"),
};
// ---- Adsolut Orders (bestellingen) — v0.0.59 ------------------------

export type AdsolutOrderHeader = {
  id: string;
  docNr: number | null;
  bookCode: string | null;
  kluwerRef: string | null;
  customerAdsolutId: string | null;
  customerName: string | null;
  customerCode: string | null;
  stateCode: string | null;
  stateDescription: string | null;
  orderDate: string | null;
  requestedDeliveryDate: string | null;
  confirmedDeliveryDate: string | null;
  remark: string | null;
  internalMemo: string | null;
  representativeName: string | null;
  totalExclVat: number;
  totalInclVat: number;
  currencyIso: string | null;
  ticketNumber: number | null;
  adsolutCreatedUtc: string | null;
  adsolutLastModified: string | null;
  syncedUtc: string;
};
export type AdsolutOrderLine = {
  id: string;
  lineNr: number | null;
  productCode: string | null;
  description: string | null;
  quantity: number | null;
  unitCode: string | null;
  unitDescription: string | null;
  delivered: number | null;
  grossUnitPrice: number | null;
  unitPrice: number | null;
  discount1: number | null;
  discount2: number | null;
  priceExclVat: number | null;
  priceInclVat: number | null;
  vatCode: string | null;
  vatDescription: string | null;
  /// v0.0.102 — cost price ("KP. Art") from the linked supplier order (BL)
  /// lines of the same order + article; null when no priced BL line exists.
  costUnitPrice: number | null;
  costTotal: number | null;
  /// Line total (excl. VAT) − total cost.
  marginAmount: number | null;
  /// Margin on cost, Adsolut's definition: margin / total cost × 100.
  marginPercent: number | null;
  /// "BL 5500101 · Telavox/AlloCloud" — where the cost came from.
  costSource: string | null;
};
/// A supplier-order ("bestelling") line linked to this order — carries the REAL
/// procurement status (statusCode), supplier (leverancier), date and delivered.
export type AdsolutSupplierOrderLine = {
  id: string;
  blDocNr: number | null;
  blBookCode: string | null;
  supplierName: string | null;
  supplierCode: string | null;
  supplierOrderDate: string | null;
  lineNr: number | null;
  productCode: string | null;
  name: string | null;
  description: string | null;
  quantity: number | null;
  delivered: number | null;
  unitCode: string | null;
  grossUnitPrice: number | null;
  unitPrice: number | null;
  discount1: number | null;
  statusCode: string | null;
  // Resolved warehouse (Stock) + location (Location) names for this line.
  warehouseName: string | null;
  warehouseLocationName: string | null;
};
export type AdsolutOrderListResponse = {
  items: AdsolutOrderHeader[];
  total: number;
  page: number;
  pageSize: number;
  statusFilter: string[];
};
export type AdsolutOrderDetail = {
  header: AdsolutOrderHeader;
  lines: AdsolutOrderLine[];
  // Linked supplier orders (bestellingen) + the status→hex colour map.
  supplierLines: AdsolutSupplierOrderLine[];
  statusColors: Record<string, string>;
};
/// Agent-facing Adsolut Orders data (overview + detail + picker + ticket links).
export const ordersApi = {
  list: (
    search: string,
    page: number,
    pageSize = 50,
    sort = "date",
    dir: "asc" | "desc" = "desc",
  ) => {
    const qs = new URLSearchParams({
      page: String(page),
      pageSize: String(pageSize),
      sort,
      dir,
    });
    if (search.trim()) qs.set("search", search.trim());
    return request<AdsolutOrderListResponse>("GET", `/api/orders?${qs.toString()}`);
  },
  detail: (id: string) => request<AdsolutOrderDetail>("GET", `/api/orders/${id}`),
  resync: (id: string) => request<AdsolutOrderDetail>("POST", `/api/orders/${id}/resync`),
  sync: () => request<void>("POST", "/api/orders/sync"),
  /// "::" picker in the ticket editor — lightweight order rows honoring the
  /// admin display status filter.
  pick: (q: string, limit = 10) => {
    const qs = new URLSearchParams({ limit: String(limit) });
    if (q.trim()) qs.set("q", q.trim());
    return request<{ items: AdsolutOrderHeader[] }>("GET", `/api/orders/picker?${qs.toString()}`);
  },
  // ticket ↔ order links
  listForTicket: (ticketId: string) =>
    request<{ items: AdsolutOrderHeader[] }>("GET", `/api/tickets/${ticketId}/orders`),
  linkToTicket: (ticketId: string, orderId: string) =>
    request<{ ok: boolean; created: boolean; order?: AdsolutOrderHeader }>(
      "POST",
      `/api/tickets/${ticketId}/orders/${orderId}`,
    ),
  unlinkFromTicket: (ticketId: string, orderId: string) =>
    request<{ ok: boolean }>("DELETE", `/api/tickets/${ticketId}/orders/${orderId}`),
};
// ---- Adsolut Articles (artikels) — v0.0.76 --------------------------
// Article catalogue mirror behind the Contracts hub ("Contract Articles").

export type AdsolutArticle = {
  id: string;
  code: string | null;
  name: string | null;
  description: string | null;
  vatCode: string | null;
  vatRate: string | null;
  active: boolean;
  adsolutCreatedUtc: string | null;
  adsolutLastModified: string | null;
  syncedUtc: string;
};
export type AdsolutArticleListResponse = {
  items: AdsolutArticle[];
  total: number;
  page: number;
  pageSize: number;
};
/// Agent-facing Adsolut article-catalogue data (Contract Articles list).
/// Gated by the per-user contracts_enabled flag (RequireAgent + in-handler
/// flag check on the backend).
export const articlesApi = {
  list: (
    search: string,
    page: number,
    pageSize = 50,
    sort = "code",
    dir: "asc" | "desc" = "asc",
    activeOnly = false,
  ) => {
    const qs = new URLSearchParams({
      page: String(page),
      pageSize: String(pageSize),
      sort,
      dir,
    });
    if (search.trim()) qs.set("search", search.trim());
    if (activeOnly) qs.set("activeOnly", "true");
    return request<AdsolutArticleListResponse>("GET", `/api/contracts/articles?${qs.toString()}`);
  },
  resync: (id: string) => request<AdsolutArticle>("POST", `/api/contracts/articles/${id}/resync`),
  sync: () => request<void>("POST", "/api/contracts/articles/sync"),
};
