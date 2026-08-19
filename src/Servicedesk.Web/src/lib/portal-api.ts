// v0.1.0 — Customer-portal API client. Own module (like ticket-api.ts)
// with the same request conventions as lib/api.ts: cookie session, CSRF
// header on writes, ApiError on non-2xx. Three surfaces:
//   portalPublicApi — anonymous config
//   portalAuthApi   — the separate customer auth flow
//   portalTicketApi — scoped ticket list/detail/reply/create
//   portalAdminApi  — agent/admin side (approvals, invitations, accounts)

import { ApiError } from "@/lib/api";
import { csrfHeader } from "@/lib/csrf";

async function request<T>(method: string, url: string, body?: unknown, init?: RequestInit): Promise<T> {
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
    let parsed: unknown = null;
    try {
      const text = await res.text();
      if (text.length > 0) parsed = JSON.parse(text);
    } catch {
      // body wasn't JSON
    }
    throw new ApiError(res.status, url, `${url} → ${res.status} ${res.statusText}`, parsed);
  }
  if (res.status === 204) return undefined as T;
  const text = await res.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

// ---- public ---------------------------------------------------------------

export type PortalPublicConfig =
  | { enabled: false }
  | {
      enabled: true;
      registrationEnabled: boolean;
      newTicketEnabled: boolean;
      organisationName: string;
      registrationIntroHtml: string;
      passwordMinimumLength: number;
      turnstile: { enabled: boolean; siteKey: string; action: string };
    };

export const portalPublicApi = {
  config: () => request<PortalPublicConfig>("GET", "/api/portal/config"),
};

// ---- auth -----------------------------------------------------------------

export type PortalLoginResponse = {
  email: string;
  displayName: string;
  twoFactorRequired: boolean;
  enrollmentRequired: boolean;
};

export type PortalMeUser = {
  id: string;
  email: string;
  displayName: string;
  amr: string;
  twoFactorEnrolled: boolean;
  companyName: string | null;
  companyRole: "Member" | "TicketManager";
  canSeeCompanyTickets: boolean;
};

export type PortalMeResponse = { enabled: boolean; user: PortalMeUser | null; serverTimeUtc: string };

export type InvitationInfo = { email: string; displayName: string; companyName: string | null };

export const portalAuthApi = {
  register: (email: string, password: string, displayName: string, turnstileToken: string | null) =>
    request<{ status: string }>("POST", "/api/portal/auth/public/register", {
      email,
      password,
      displayName,
      turnstileToken,
    }),
  verifyEmail: (token: string) =>
    request<{ status: "verified" | "already_verified" }>("POST", "/api/portal/auth/public/verify-email", { token }),
  login: (email: string, password: string) =>
    request<PortalLoginResponse>("POST", "/api/portal/auth/public/login", { email, password }),
  forgotPassword: (email: string) =>
    request<{ status: string }>("POST", "/api/portal/auth/public/forgot-password", { email }),
  resetPassword: (token: string, password: string) =>
    request<{ status: string }>("POST", "/api/portal/auth/public/reset-password", { token, password }),
  describeInvitation: (token: string) =>
    request<InvitationInfo>("GET", `/api/portal/auth/public/invitation?token=${encodeURIComponent(token)}`),
  acceptInvitation: (token: string, password: string) =>
    request<{ status: string; email: string }>("POST", "/api/portal/auth/public/invitation/accept", { token, password }),
  verifyTwoFactor: (code: string) =>
    request<{ ok: boolean; usedRecoveryCode: boolean }>("POST", "/api/portal/auth/2fa/verify", { code }),
  beginEnroll: () => request<{ secret: string; otpauthUri: string }>("POST", "/api/portal/auth/2fa/enroll/begin"),
  confirmEnroll: (code: string) =>
    request<{ recoveryCodes: string[] }>("POST", "/api/portal/auth/2fa/enroll/confirm", { code }),
  logout: () => request<void>("POST", "/api/portal/auth/logout"),
  me: () => request<PortalMeResponse>("GET", "/api/portal/auth/me"),
};

// ---- tickets --------------------------------------------------------------

export type PortalStatus = { name: string; color: string; category: string };
export type PortalPriority = { name: string; color: string; level?: number };
export type PortalRequester = { name: string; email: string; isYou: boolean };

export type PortalTicketListItem = {
  id: string;
  number: number;
  subject: string;
  status: PortalStatus;
  priority: PortalPriority;
  requester: PortalRequester;
  createdUtc: string;
  updatedUtc: string;
  closedUtc: string | null;
};

export type PortalTicketList = {
  items: PortalTicketListItem[];
  total: number;
  page: number;
  pageSize: number;
  scope: "company" | "own";
};

export type PortalAttachment = {
  id: string | null;
  name: string | null;
  mimeType: string | null;
  size: number;
  url: string | null;
};

export type PortalMessage = {
  id: number;
  type: string;
  kind: "customer" | "agent" | "system";
  authorName: string;
  isYou: boolean;
  bodyHtml: string | null;
  bodyText: string | null;
  statusChange: { from: string | null; to: string | null } | null;
  attachments: PortalAttachment[];
  createdUtc: string;
};

export type PortalTicketDetail = {
  ticket: {
    id: string;
    number: number;
    subject: string;
    status: PortalStatus;
    priority: PortalPriority;
    requester: PortalRequester;
    companyName: string | null;
    source: string;
    createdUtc: string;
    updatedUtc: string;
    resolvedUtc: string | null;
    closedUtc: string | null;
    descriptionHtml: string | null;
    descriptionText: string;
  };
  messages: PortalMessage[];
  canReply: boolean;
  replyBlockedReason: "closed" | "resolved" | null;
};

export type PortalUploadResult = { id: string; url: string; mimeType: string; size: number; filename: string };

export const portalTicketApi = {
  list: (filter: "open" | "closed" | "all", search: string, page: number) => {
    const qs = new URLSearchParams({ filter, page: String(page) });
    if (search.trim()) qs.set("search", search.trim());
    return request<PortalTicketList>("GET", `/api/portal/tickets/?${qs.toString()}`);
  },
  get: (id: string) => request<PortalTicketDetail>("GET", `/api/portal/tickets/${id}`),
  create: (subject: string, bodyHtml: string) =>
    request<{ id: string; number: number; messageEventId: number | null }>("POST", "/api/portal/tickets/", {
      subject,
      bodyHtml,
    }),
  reply: (id: string, bodyHtml: string) =>
    request<{ eventId: number }>("POST", `/api/portal/tickets/${id}/messages`, { bodyHtml }),
  upload: async (ticketId: string, eventId: number, file: File): Promise<PortalUploadResult> => {
    const form = new FormData();
    form.append("file", file, file.name);
    const url = `/api/portal/tickets/${ticketId}/attachments?eventId=${eventId}`;
    const res = await fetch(url, {
      method: "POST",
      credentials: "include",
      headers: { Accept: "application/json", ...csrfHeader() },
      body: form,
    });
    if (!res.ok) {
      let parsed: unknown = null;
      try {
        parsed = JSON.parse(await res.text());
      } catch {
        // ignore
      }
      throw new ApiError(res.status, url, `${url} → ${res.status}`, parsed);
    }
    return (await res.json()) as PortalUploadResult;
  },
};

// ---- admin ----------------------------------------------------------------

export type PortalAccountStatus = "PendingVerification" | "PendingApproval" | "Active" | "Rejected" | "Deactivated";

export type PortalAccount = {
  userId: string;
  email: string;
  status: PortalAccountStatus;
  displayName: string;
  origin: "Registration" | "Invitation";
  isActive: boolean;
  contactId: string | null;
  contactName: string | null;
  companyRole: "Member" | "TicketManager" | null;
  companyId: string | null;
  companyName: string | null;
  registrationIp: string | null;
  emailVerifiedUtc: string | null;
  approvalTicketId: string | null;
  approvalTicketNumber: number | null;
  approvedByEmail: string | null;
  approvedUtc: string | null;
  rejectedUtc: string | null;
  rejectionReason: string | null;
  invitedByEmail: string | null;
  twoFactorEnrolled: boolean;
  lastLoginUtc: string | null;
  createdUtc: string;
  updatedUtc: string;
};

export type PortalInvitation = {
  id: string;
  email: string;
  contactId: string | null;
  companyId: string | null;
  companyName: string | null;
  companyRole: string | null;
  displayName: string;
  createdByEmail: string | null;
  createdUtc: string;
  expiresUtc: string;
  expired: boolean;
};

export type PortalAdminStatus = {
  enabled: boolean;
  registrationEnabled: boolean;
  registrationTicketEnabled: boolean;
  registrationQueueConfigured: boolean;
  newTicketQueueConfigured: boolean;
  fromMailbox: string | null;
  publicBaseUrlConfigured: boolean;
  turnstile: { enabled: boolean; siteKeyConfigured: boolean; secretConfigured: boolean; misconfigured: boolean };
  counts: { pendingVerification: number; pendingApproval: number; active: number; deactivated: number; rejected: number };
};

export type SuggestedCompany = { id: string; name: string } | null;

export const portalAdminApi = {
  status: () => request<PortalAdminStatus>("GET", "/api/portal/admin/status"),
  listAccounts: (statuses: PortalAccountStatus[] | null, search: string) => {
    const qs = new URLSearchParams();
    if (statuses && statuses.length) qs.set("status", statuses.join(","));
    if (search.trim()) qs.set("search", search.trim());
    const q = qs.toString();
    return request<PortalAccount[]>("GET", `/api/portal/admin/accounts${q ? `?${q}` : ""}`);
  },
  getAccount: (userId: string) =>
    request<{ account: PortalAccount; suggestedCompany: SuggestedCompany }>("GET", `/api/portal/admin/accounts/${userId}`),
  byContact: (contactId: string) =>
    request<{ account: PortalAccount | null; invitations: PortalInvitation[] }>(
      "GET",
      `/api/portal/admin/accounts/by-contact/${contactId}`,
    ),
  byTicket: (ticketId: string) =>
    request<{ account: PortalAccount | null; suggestedCompany: SuggestedCompany }>(
      "GET",
      `/api/portal/admin/accounts/by-ticket/${ticketId}`,
    ),
  approve: (userId: string, companyId: string | null, companyRole: "Member" | "TicketManager") =>
    request<{ account: PortalAccount }>("POST", `/api/portal/admin/accounts/${userId}/approve`, { companyId, companyRole }),
  reject: (userId: string, reason: string) =>
    request<{ account: PortalAccount }>("POST", `/api/portal/admin/accounts/${userId}/reject`, { reason }),
  deactivate: (userId: string) => request<{ account: PortalAccount }>("POST", `/api/portal/admin/accounts/${userId}/deactivate`),
  reactivate: (userId: string) => request<{ account: PortalAccount }>("POST", `/api/portal/admin/accounts/${userId}/reactivate`),
  resetTotp: (userId: string) => request<{ account: PortalAccount }>("POST", `/api/portal/admin/accounts/${userId}/reset-totp`),
  revokeSessions: (userId: string) =>
    request<{ account: PortalAccount }>("POST", `/api/portal/admin/accounts/${userId}/revoke-sessions`),
  resendVerification: (userId: string) =>
    request<{ account: PortalAccount }>("POST", `/api/portal/admin/accounts/${userId}/resend-verification`),
  remove: (userId: string) => request<void>("DELETE", `/api/portal/admin/accounts/${userId}`),
  listInvitations: (contactId?: string, includeExpired = false) => {
    const qs = new URLSearchParams();
    if (contactId) qs.set("contactId", contactId);
    if (includeExpired) qs.set("includeExpired", "true");
    const q = qs.toString();
    return request<PortalInvitation[]>("GET", `/api/portal/admin/invitations${q ? `?${q}` : ""}`);
  },
  invite: (input: {
    email?: string;
    displayName?: string;
    contactId?: string | null;
    companyId?: string | null;
    companyRole: "Member" | "TicketManager";
  }) => request<{ invitationId: string }>("POST", "/api/portal/admin/invitations", input),
  resendInvitation: (id: string) => request<{ ok: boolean }>("POST", `/api/portal/admin/invitations/${id}/resend`),
  revokeInvitation: (id: string) => request<void>("DELETE", `/api/portal/admin/invitations/${id}`),
  turnstileSecretStatus: () => request<{ configured: boolean }>("GET", "/api/portal/admin/turnstile/secret"),
  setTurnstileSecret: (value: string) => request<void>("PUT", "/api/portal/admin/turnstile/secret", { value }),
  deleteTurnstileSecret: () => request<void>("DELETE", "/api/portal/admin/turnstile/secret"),
};
