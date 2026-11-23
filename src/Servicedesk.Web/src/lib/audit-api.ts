import { request } from "@/lib/http";

export type AuditEntry = {
  id: number;
  utc: string;
  actor: string;
  actorRole: string;
  eventType: string;
  target: string | null;
  clientIp: string | null;
  userAgent: string | null;
  payload: unknown;
  entryHash: string;
  prevHash: string;
};
export type AuditPage = {
  items: AuditEntry[];
  nextCursor: number | null;
};
export type AuditListQuery = {
  eventType?: string;
  actor?: string;
  fromUtc?: string;
  toUtc?: string;
  cursor?: number;
  limit?: number;
};
export const auditApi = {
  list: (query: AuditListQuery = {}) => {
    const params = new URLSearchParams();
    if (query.eventType) params.set("eventType", query.eventType);
    if (query.actor) params.set("actor", query.actor);
    if (query.fromUtc) params.set("fromUtc", query.fromUtc);
    if (query.toUtc) params.set("toUtc", query.toUtc);
    if (query.cursor !== undefined) params.set("cursor", String(query.cursor));
    params.set("limit", String(query.limit ?? 50));
    return request<AuditPage>("GET", `/api/audit?${params.toString()}`);
  },
  get: (id: number) => request<AuditEntry>("GET", `/api/audit/${id}`),
};
