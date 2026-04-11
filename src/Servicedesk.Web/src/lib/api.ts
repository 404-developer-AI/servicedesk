import { csrfHeader } from "@/lib/csrf";
import type { Role } from "@/lib/roles";

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

export type AuthUserPayload = {
  id: string;
  email: string;
  role: Role;
  amr: string;
  twoFactorEnabled: boolean;
};

export type MeResponse = {
  user: AuthUserPayload | null;
  serverTimeUtc: string;
};

export type SetupStatus = { available: boolean };

export type LoginResponse = {
  email: string;
  role: Role;
  twoFactorRequired: boolean;
};

export type TotpEnrollment = {
  secret: string;
  otpauthUri: string;
};

export type RecoveryCodesResponse = { recoveryCodes: string[] };

export class ApiError extends Error {
  readonly status: number;
  readonly url: string;
  constructor(status: number, url: string, message: string) {
    super(message);
    this.status = status;
    this.url = url;
  }
}

async function request<T>(
  method: string,
  url: string,
  body?: unknown,
  init?: RequestInit,
): Promise<T> {
  const isSafe = method === "GET" || method === "HEAD";
  const res = await fetch(url, {
    method,
    credentials: "include",
    headers: {
      Accept: "application/json",
      ...(body !== undefined ? { "Content-Type": "application/json" } : {}),
      ...(isSafe ? {} : csrfHeader()),
      ...(init?.headers ?? {}),
    },
    body: body === undefined ? undefined : JSON.stringify(body),
    ...init,
  });
  if (!res.ok) {
    throw new ApiError(res.status, url, `${url} → ${res.status} ${res.statusText}`);
  }
  if (res.status === 204) {
    return undefined as T;
  }
  const text = await res.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

export const systemApi = {
  version: () => request<SystemVersion>("GET", "/api/system/version"),
  time: () => request<SystemTime>("GET", "/api/system/time"),
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

export const authApi = {
  setupStatus: () => request<SetupStatus>("GET", "/api/auth/setup/status"),
  createAdmin: (email: string, password: string) =>
    request<{ email: string; role: Role }>("POST", "/api/auth/setup/create-admin", { email, password }),
  login: (email: string, password: string) =>
    request<LoginResponse>("POST", "/api/auth/login", { email, password }),
  verifyTwoFactor: (code: string) =>
    request<void>("POST", "/api/auth/2fa/verify", { code }),
  logout: () => request<void>("POST", "/api/auth/logout"),
  me: () => request<MeResponse>("GET", "/api/auth/me"),
  beginTotpEnroll: () => request<TotpEnrollment>("POST", "/api/auth/2fa/enroll/begin"),
  confirmTotpEnroll: (code: string) =>
    request<RecoveryCodesResponse>("POST", "/api/auth/2fa/enroll/confirm", { code }),
  disableTotp: () => request<void>("POST", "/api/auth/2fa/disable"),
};
