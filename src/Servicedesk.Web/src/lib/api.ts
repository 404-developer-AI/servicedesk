import { devRoleStore } from "@/stores/useDevRoleStore";

export type SystemVersion = {
  version: string;
  commit: string;
  buildTime: string;
};

export type SystemTime = {
  utc: string;
  timezone: string;
  offsetMinutes: number;
};

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

async function getJson<T>(url: string, init?: RequestInit): Promise<T> {
  const res = await fetch(url, {
    ...init,
    headers: {
      Accept: "application/json",
      ...(init?.headers ?? {}),
    },
  });
  if (!res.ok) {
    throw new Error(`${url} → ${res.status} ${res.statusText}`);
  }
  return (await res.json()) as T;
}

// The dev role header is read fresh on every call so toggling the role in
// the header switcher propagates without a page reload. In v0.0.4 this is
// replaced by the authenticated session header / cookie.
function devRoleHeaders(): Record<string, string> {
  return { "X-Dev-Role": devRoleStore.get() };
}

export const systemApi = {
  version: () => getJson<SystemVersion>("/api/system/version"),
  time: () => getJson<SystemTime>("/api/system/time"),
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
    const qs = params.toString();
    return getJson<AuditPage>(`/api/audit?${qs}`, { headers: devRoleHeaders() });
  },
  get: (id: number) =>
    getJson<AuditEntry>(`/api/audit/${id}`, { headers: devRoleHeaders() }),
};
