import { request } from "@/lib/http";

export type AdsolutErpSalesReceiptStatusOption = {
  code: string;
  description: string | null;
  count: number;
};
export type AdsolutErpSalesReceiptsState = {
  enabled: boolean;
  intervalMinutes: number;
  statusFilter: string[];
  totalMirrored: number;
  lastFullSyncUtc: string | null;
  lastDeltaSyncUtc: string | null;
  lastError: string | null;
  lastErrorUtc: string | null;
  receiptsSeen: number;
  receiptsUpserted: number;
  updatedUtc: string | null;
  // Server-computed next tick — never trust the client clock.
  nextSyncUtc: string | null;
  statusOptions: AdsolutErpSalesReceiptStatusOption[];
};
/// ERP Orders (bestellingen) mirror state — integration-page panel (v0.0.59).
/// The status filter here is DISPLAY-ONLY (mirror always holds every status).
export type AdsolutErpOrdersState = {
  enabled: boolean;
  intervalMinutes: number;
  statusFilter: string[];
  totalMirrored: number;
  lastFullSyncUtc: string | null;
  lastDeltaSyncUtc: string | null;
  lastError: string | null;
  lastErrorUtc: string | null;
  ordersSeen: number;
  ordersUpserted: number;
  supplierOrdersSeen: number;
  supplierOrdersUpserted: number;
  updatedUtc: string | null;
  nextSyncUtc: string | null;
  statusOptions: AdsolutErpSalesReceiptStatusOption[];
  // Supplier-order ("bestelling") statuses discovered in the mirror + the
  // saved status→hex colour map (incl. the special NO_STATUS key).
  supplierStatusOptions: AdsolutErpSalesReceiptStatusOption[];
  statusColors: Record<string, string>;
};
/// ERP Articles (artikels) mirror state — integration-page panel (v0.0.76).
/// Simple reference-list slice: enable toggle + interval + sync counters.
export type AdsolutErpArticlesState = {
  enabled: boolean;
  intervalMinutes: number;
  totalMirrored: number;
  lastFullSyncUtc: string | null;
  lastDeltaSyncUtc: string | null;
  lastError: string | null;
  lastErrorUtc: string | null;
  articlesSeen: number;
  articlesUpserted: number;
  updatedUtc: string | null;
  nextSyncUtc: string | null;
};
export type AdsolutSalesReceiptHeader = {
  id: string;
  docNr: number | null;
  bookCode: string | null;
  customerName: string | null;
  customerCode: string | null;
  stateCode: string | null;
  stateDescription: string | null;
  salesReceiptDate: string | null;
  description: string | null;
  employeeName: string | null;
  employeeCode: string | null;
  representativeName: string | null;
  totalExclVat: number;
  currencyIso: string | null;
  adsolutCreatedUtc: string | null;
  adsolutLastModified: string | null;
  syncedUtc: string;
  // Ticket number parsed from the description ("Ticket#<n>"), or null.
  ticketNumber: number | null;
  // Id of the matching ticket (null when no Ticket# ref or no such ticket in
  // this install). Drives the "open the ticket" link in the Adsolut tab.
  ticketId: string | null;
  // Total registered timesheet minutes on the matched ticket, computed live.
  // null = no Ticket# ref, no matching ticket, no registered hours, or this is
  // a non-primary receipt of a ticket billed across multiple verkoopbonnen
  // (its hours live on the primary receipt to avoid double-counting).
  totalMinutes: number | null;
  // Gross price = hourly rate × registered hours. null when no hours or no
  // rate configured (Settings → Timesheet → Hourly rate).
  brutoPrice: number | null;
  // Receipt grouping for tickets billed across multiple verkoopbonnen. The
  // registered hours live once on the primary receipt (lowest doc-nr); siblings
  // carry no hours and point back to the primary. count/ordinal are 1 for a
  // solo receipt. combinedTotalExclVat sums the excl-VAT total of every receipt
  // on the same ticket — what the registered hours are compared against.
  isPrimary: boolean;
  ticketReceiptCount: number;
  ticketReceiptOrdinal: number;
  combinedTotalExclVat: number;
  // "VK Werkuren" — excl-VAT total of only the product lines whose product is
  // flagged as counting toward work hours (Settings → Timesheet → work-hours
  // articles). werkurenExclVat is this receipt's own figure; combinedWerkuren
  // ExclVat sums it across every receipt on the ticket — what the registered
  // hours (Difference) are matched against, replacing the full excl-VAT total.
  // 0 until the product catalogue is synced and articles are flagged.
  werkurenExclVat: number;
  combinedWerkurenExclVat: number;
  // "Back Office checked" marker for this receipt (context 'adsolut').
  boChecked: boolean;
  checkedUtc: string | null;
  checkedByEmail: string | null;
  // v0.0.84 — Timesheet → Comments. The shared per-ticket thread id (null when
  // none yet), whether any comment exists, and whether this user has an unread
  // message linked to them on this ticket.
  commentThreadId: string | null;
  hasComments: boolean;
  commentsUnread: boolean;
};
export type AdsolutReceiptHours = {
  totalMinutes: number;
  totalBrutoPrice: number | null;
  byTask: {
    taskName: string;
    isAbsence: boolean;
    minutes: number;
    brutoPrice: number | null;
  }[];
};
export type AdsolutSalesReceiptLine = {
  id: string;
  lineNr: number | null;
  productCode: string | null;
  name: string | null;
  description: string | null;
  quantity: number | null;
  unitCode: string | null;
  unitPrice: number | null;
  totalExclVat: number | null;
  totalInclVat: number | null;
  vatCode: string | null;
};
export type AdsolutSalesReceiptPerformance = {
  id: string;
  employeeCode: string | null;
  performanceDate: string | null;
  fromTime: string | null;
  untilTime: string | null;
  durationMinutes: number | null;
  invoiceDurationMinutes: number | null;
  invoiceUnitPrice: number | null;
  invoiceTotal: number | null;
  performanceCode: string | null;
  description: string | null;
};
export type AdsolutSalesReceiptListResponse = {
  items: AdsolutSalesReceiptHeader[];
  total: number;
  page: number;
  pageSize: number;
};
export type AdsolutSalesReceiptDetail = {
  header: AdsolutSalesReceiptHeader;
  lines: AdsolutSalesReceiptLine[];
  performances: AdsolutSalesReceiptPerformance[];
};
/// Agent-facing Timesheet → Adsolut tab data (mirrored sales receipts).
export const adsolutTimesheetApi = {
  listReceipts: (
    search: string,
    page: number,
    pageSize = 50,
    sort = "date",
    dir: "asc" | "desc" = "desc",
    boFilter: "all" | "checked" | "unchecked" = "all",
    year?: number,
    month?: number,
    withComments = false,
  ) => {
    const qs = new URLSearchParams({
      page: String(page),
      pageSize: String(pageSize),
      sort,
      dir,
    });
    if (search.trim()) qs.set("search", search.trim());
    if (boFilter !== "all") qs.set("boFilter", boFilter);
    if (year !== undefined && month !== undefined) {
      qs.set("year", String(year));
      qs.set("month", String(month));
    }
    if (withComments) qs.set("withComments", "true");
    return request<AdsolutSalesReceiptListResponse>(
      "GET",
      `/api/timesheet/adsolut/receipts?${qs.toString()}`,
    );
  },
  receiptDetail: (id: string) =>
    request<AdsolutSalesReceiptDetail>("GET", `/api/timesheet/adsolut/receipts/${id}`),
  receiptHours: (id: string) =>
    request<AdsolutReceiptHours>("GET", `/api/timesheet/adsolut/receipts/${id}/hours`),
  resyncReceipt: (id: string) =>
    request<AdsolutSalesReceiptDetail>(
      "POST",
      `/api/timesheet/adsolut/receipts/${id}/resync`,
    ),
};
// ---- Adsolut Contracts (contracten) — v0.0.77 -----------------------
// Contract header + line mirror behind the Contracts hub ("Contracts overview").

export type AdsolutContractHeader = {
  id: string;
  docNr: number | null;
  customerAdsolutId: string | null;
  customerName: string | null;
  companyId: string | null;
  companyName: string | null;
  relationCode: string | null;
  stateCode: string | null;
  stateDescription: string | null;
  docDate: string | null;
  startDate: string | null;
  stopDate: string | null;
  endDate: string | null;
  description: string | null;
  memo: string | null;
  periodicityCode: string | null;
  periodicityLabel: string | null;
  invoicingPeriodicityCode: string | null;
  invoicingPeriodicityLabel: string | null;
  numberOfTerms: number | null;
  totalExclVat: number;
  totalVat: number;
  totalInclVat: number;
  adsolutCreatedUtc: string | null;
  adsolutLastModified: string | null;
  syncedUtc: string;
};
export type AdsolutContractLine = {
  id: string;
  lineNr: number | null;
  name: string | null;
  description: string | null;
  quantity: number;
  grossUnitPrice: number | null;
  discount1: number | null;
  discount2: number | null;
  unitPrice: number | null;
  unitPriceIncl: number | null;
  startDate: string | null;
  endDate: string | null;
};
export type AdsolutContractListResponse = {
  items: AdsolutContractHeader[];
  total: number;
  page: number;
  pageSize: number;
  statusFilter: string[];
  /// Admin-configured status code → hex colour map for the overview pill.
  statusColors: Record<string, string>;
};
export type AdsolutContractDetail = {
  header: AdsolutContractHeader;
  lines: AdsolutContractLine[];
};
/// Admin-facing Adsolut ERP Contracts state — integration-page panel.
export type AdsolutErpContractsState = {
  enabled: boolean;
  intervalMinutes: number;
  statusFilter: string[];
  totalMirrored: number;
  lastFullSyncUtc: string | null;
  lastDeltaSyncUtc: string | null;
  lastError: string | null;
  lastErrorUtc: string | null;
  contractsSeen: number;
  contractsUpserted: number;
  updatedUtc: string | null;
  nextSyncUtc: string | null;
  statusOptions: { code: string; description: string | null; count: number }[];
  /// Status code → hex colour map (drives the overview pill colour).
  statusColors: Record<string, string>;
};
/// Agent-facing Adsolut Contracts data (overview + detail).
/// Gated by the per-user contracts_enabled flag (RequireAgent + in-handler
/// flag check on the backend).
export const contractsApi = {
  list: (
    search: string,
    page: number,
    pageSize = 50,
    sort = "start",
    dir: "asc" | "desc" = "desc",
  ) => {
    const qs = new URLSearchParams({
      page: String(page),
      pageSize: String(pageSize),
      sort,
      dir,
    });
    if (search.trim()) qs.set("search", search.trim());
    return request<AdsolutContractListResponse>("GET", `/api/contracts/overview?${qs.toString()}`);
  },
  detail: (id: string) => request<AdsolutContractDetail>("GET", `/api/contracts/overview/${id}`),
  resync: (id: string) => request<AdsolutContractDetail>("POST", `/api/contracts/overview/${id}/resync`),
  sync: () => request<void>("POST", "/api/contracts/overview/sync"),
};
