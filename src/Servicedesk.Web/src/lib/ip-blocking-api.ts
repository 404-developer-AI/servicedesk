import { request } from "@/lib/http";

// ---- IP blocking (v0.1.30, admin only) ----

export type IpProposalStatus = "open" | "blocked" | "whitelisted" | "dismissed";
export type IpProposal = {
  id: number;
  ip: string;
  status: IpProposalStatus;
  autoBlocked: boolean;
  /** scanner_paths | rate_limited | csrf_rejected | failed_logins */
  reasons: string[];
  evidence: { windowMinutes?: number; counts?: Record<string, number>; samplePaths?: string[] };
  knownLogin: boolean;
  firstSeenUtc: string;
  lastSeenUtc: string;
  createdUtc: string;
  decidedUtc: string | null;
  decidedBy: string | null;
};
export type IpRule = {
  ip: string;
  kind: "block" | "whitelist";
  source: "auto" | "admin";
  reason: string | null;
  proposalId: number | null;
  createdUtc: string;
  createdBy: string;
  expiresUtc: string | null;
  hitCount: number;
  lastHitUtc: string | null;
};
export type IpBlockingOverview = {
  enabled: boolean;
  yourIp: string | null;
  openProposals: IpProposal[];
  recentProposals: IpProposal[];
  rules: IpRule[];
};
const IP_BASE = "/api/admin/security/ip-blocking";
export const ipBlockingApi = {
  overview: () => request<IpBlockingOverview>("GET", `${IP_BASE}/overview`),
  block: (id: number, body: { reason?: string; confirmKnownLogins?: boolean } = {}) =>
    request<void>("POST", `${IP_BASE}/proposals/${id}/block`, body),
  whitelist: (id: number, body: { reason?: string } = {}) =>
    request<void>("POST", `${IP_BASE}/proposals/${id}/whitelist`, body),
  dismiss: (id: number) => request<void>("POST", `${IP_BASE}/proposals/${id}/dismiss`),
  addRule: (body: { ip: string; kind: "block" | "whitelist"; reason?: string; confirmKnownLogins?: boolean }) =>
    request<void>("POST", `${IP_BASE}/rules`, body),
  removeRule: (ip: string) => request<void>("DELETE", `${IP_BASE}/rules/${encodeURIComponent(ip)}`),
};
