import { ApiError } from "@/lib/api";

export type ReportTemplate = {
  id: string;
  name: string;
  description: string | null;
  subject: string;
  bodyHtml: string;
  queueId: string | null;
  columns: string[];
  scope: "all" | "unprotected";
  attachPdf: boolean;
  isActive: boolean;
  createdUtc: string;
  updatedUtc: string;
};

export type TemplateUpsert = {
  name: string;
  description?: string | null;
  subject?: string;
  bodyHtml?: string;
  queueId?: string | null;
  columns: string[];
  scope: "all" | "unprotected";
  attachPdf: boolean;
  isActive?: boolean;
};

export type ReportColumn = { key: string; label: string };

export type ReportToken = { token: string; label: string };

export type ReportingContact = {
  contactId: string;
  firstName: string;
  lastName: string;
  email: string;
  role: string;
  isReportingContact: boolean;
  isActive: boolean;
};

export type ReportRecipient = { address: string; name: string };

export type SendBody = {
  templateId: string;
  columns?: string[];
  scope?: "all" | "unprotected";
  recipients?: ReportRecipient[];
};

export type ReportPreview = {
  subject: string;
  bodyHtml: string;
  recipients: ReportRecipient[];
  fromAddress: string | null;
  attachPdf: boolean;
  scope: string;
  columns: string[];
  mailboxCount: number;
  spamProtected: number | null;
  spamTotal: number | null;
  exchangeProtected: number | null;
  exchangeTotal: number | null;
  onedriveProtected: number | null;
  onedriveTotal: number | null;
  warnings: string[];
};

export type ReportLastSent = {
  companyId: string;
  sentUtc: string;
  status: string;
  sentByName: string | null;
  subject: string | null;
  mailboxCount: number;
};

async function request<T>(method: string, url: string, body?: unknown): Promise<T> {
  const isSafe = method === "GET" || method === "HEAD";
  const csrf = isSafe
    ? undefined
    : document.cookie
        .split("; ")
        .find((c) => c.startsWith("XSRF-TOKEN="))
        ?.split("=")[1];

  const res = await fetch(url, {
    method,
    credentials: "include",
    headers: {
      Accept: "application/json",
      ...(body !== undefined ? { "Content-Type": "application/json" } : {}),
      ...(csrf ? { "X-XSRF-TOKEN": decodeURIComponent(csrf) } : {}),
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  });

  if (!res.ok) {
    let parsed: unknown = null;
    try {
      const text = await res.text();
      if (text.length > 0) parsed = JSON.parse(text);
    } catch {
      // ignore
    }
    throw new ApiError(res.status, url, `${url} → ${res.status} ${res.statusText}`, parsed);
  }

  if (res.status === 204) return undefined as T;
  const text = await res.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

const BASE = "/api/contracts/reports";

export const contractReportsApi = {
  templates: {
    list: (includeInactive = true) =>
      request<ReportTemplate[]>("GET", `${BASE}/templates?includeInactive=${includeInactive}`),
    get: (id: string) =>
      request<ReportTemplate>("GET", `${BASE}/templates/${id}`),
    create: (body: TemplateUpsert) =>
      request<ReportTemplate>("POST", `${BASE}/templates`, body),
    update: (id: string, body: TemplateUpsert) =>
      request<ReportTemplate>("PUT", `${BASE}/templates/${id}`, body),
    remove: (id: string) =>
      request<void>("DELETE", `${BASE}/templates/${id}`),
  },

  columns: () =>
    request<{ columns: ReportColumn[] }>("GET", `${BASE}/columns`),

  tokens: () =>
    request<{ tokens: ReportToken[] }>("GET", `${BASE}/tokens`),

  defaults: () =>
    request<{ columns: string[] }>("GET", `${BASE}/defaults`),

  reportingContacts: (companyId: string) =>
    request<{ items: ReportingContact[] }>("GET", `${BASE}/companies/${companyId}/reporting-contacts`),

  setReportingContact: (companyId: string, contactId: string, isReporting: boolean) =>
    request<{ ok: true }>(
      "PUT",
      `${BASE}/companies/${companyId}/reporting-contacts/${contactId}`,
      { isReporting },
    ),

  preview: (companyId: string, body: SendBody) =>
    request<ReportPreview>("POST", `${BASE}/companies/${companyId}/preview`, body),

  send: (companyId: string, body: SendBody) =>
    request<{ ok: true; sendId: string; internetMessageId: string }>(
      "POST",
      `${BASE}/companies/${companyId}/send`,
      body,
    ),

  lastSent: () =>
    request<{ items: ReportLastSent[] }>("GET", `${BASE}/last-sent`),
};
