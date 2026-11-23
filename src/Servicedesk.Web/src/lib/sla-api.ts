import { request } from "@/lib/http";

// ---- SLA ----

export type BusinessHoursSlot = {
  id: number;
  schemaId: string;
  dayOfWeek: number;
  startMinute: number;
  endMinute: number;
};
export type Holiday = {
  id: number;
  schemaId: string;
  date: string;
  name: string;
  source: "manual" | "nager";
  countryCode: string;
};
export type BusinessHoursSchema = {
  id: string;
  name: string;
  timezone: string;
  countryCode: string;
  isDefault: boolean;
  slots: BusinessHoursSlot[];
  holidays: Holiday[];
};
export type SlaPolicy = {
  id: string;
  queueId: string | null;
  priorityId: string;
  businessHoursSchemaId: string;
  firstResponseMinutes: number | null;
  resolutionMinutes: number | null;
  pauseOnPending: boolean;
};
export type TicketSlaState = {
  ticketId: string;
  policyId: string | null;
  firstResponseDeadlineUtc: string | null;
  resolutionDeadlineUtc: string | null;
  firstResponseMetUtc: string | null;
  resolutionMetUtc: string | null;
  firstResponseBusinessMinutes: number | null;
  resolutionBusinessMinutes: number | null;
  isPaused: boolean;
  pausedSinceUtc: string | null;
  pausedAccumMinutes: number;
  lastRecalcUtc: string;
  updatedUtc: string;
};
export type SlaLogItem = {
  ticketId: string;
  number: number;
  subject: string;
  queueId: string;
  queueName: string;
  priorityId: string;
  priorityName: string;
  statusId: string;
  statusName: string;
  createdUtc: string;
  firstResponseDeadlineUtc: string | null;
  firstResponseMetUtc: string | null;
  resolutionDeadlineUtc: string | null;
  resolutionMetUtc: string | null;
  firstResponseTargetMinutes: number | null;
  resolutionTargetMinutes: number | null;
  firstResponseBusinessMinutes: number | null;
  resolutionBusinessMinutes: number | null;
  isPaused: boolean;
  firstResponseBreached: boolean;
  resolutionBreached: boolean;
};
export type SlaLogPage = { items: SlaLogItem[]; nextCursor: number | null };
export type QueueAvgPickup = {
  queueId: string;
  queueName: string;
  ticketCount: number;
  avgBusinessMinutes: number | null;
};
export type NagerCountry = { countryCode: string; name: string };
export const slaApi = {
  listSchemas: () => request<BusinessHoursSchema[]>("GET", "/api/sla/business-hours"),
  createSchema: (body: unknown) =>
    request<BusinessHoursSchema>("POST", "/api/sla/business-hours", body),
  updateSchema: (id: string, body: unknown) =>
    request<BusinessHoursSchema>("PUT", `/api/sla/business-hours/${id}`, body),
  deleteSchema: (id: string) => request<void>("DELETE", `/api/sla/business-hours/${id}`),

  listHolidays: (schemaId: string, year?: number) => {
    const qs = year ? `?year=${year}` : "";
    return request<Holiday[]>("GET", `/api/sla/business-hours/${schemaId}/holidays${qs}`);
  },
  addHoliday: (schemaId: string, body: { date: string; name?: string; countryCode?: string }) =>
    request<void>("POST", `/api/sla/business-hours/${schemaId}/holidays`, body),
  deleteHoliday: (id: number) => request<void>("DELETE", `/api/sla/holidays/${id}`),
  syncHolidays: (schemaId: string, countryCode: string, year?: number) =>
    request<void>("POST", `/api/sla/business-hours/${schemaId}/holidays/sync`, { countryCode, year }),
  listCountries: () => request<NagerCountry[]>("GET", "/api/sla/holidays/countries"),

  listPolicies: () => request<SlaPolicy[]>("GET", "/api/sla/policies"),
  upsertPolicy: (body: unknown) => request<SlaPolicy>("PUT", "/api/sla/policies", body),
  deletePolicy: (id: string) => request<void>("DELETE", `/api/sla/policies/${id}`),

  log: (params: {
    queueId?: string;
    priorityId?: string;
    statusId?: string;
    breachedOnly?: boolean;
    fromUtc?: string;
    toUtc?: string;
    search?: string;
    cursorNumber?: number;
    limit?: number;
  }) => {
    const qs = new URLSearchParams();
    if (params.queueId) qs.set("queueId", params.queueId);
    if (params.priorityId) qs.set("priorityId", params.priorityId);
    if (params.statusId) qs.set("statusId", params.statusId);
    if (params.breachedOnly) qs.set("breachedOnly", "true");
    if (params.fromUtc) qs.set("fromUtc", params.fromUtc);
    if (params.toUtc) qs.set("toUtc", params.toUtc);
    if (params.search) qs.set("search", params.search);
    if (params.cursorNumber) qs.set("cursorNumber", String(params.cursorNumber));
    qs.set("limit", String(params.limit ?? 50));
    return request<SlaLogPage>("GET", `/api/sla/log?${qs.toString()}`);
  },

  avgPickup: (days = 7) =>
    request<{ days: number; items: QueueAvgPickup[] }>(
      "GET",
      `/api/sla/dashboard/avg-pickup?days=${days}`,
    ),

  // "No SLA" arrives as a 200 with a JSON null body (not a 404). Normalize a
  // missing body to null too — React Query rejects undefined cache values.
  ticketState: (ticketId: string) =>
    request<TicketSlaState | null>("GET", `/api/sla/tickets/${ticketId}`).then(
      (state) => state ?? null,
    ),
};
