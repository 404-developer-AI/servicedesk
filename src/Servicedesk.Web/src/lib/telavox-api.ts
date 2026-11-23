import { request } from "@/lib/http";
import type { IntegrationAuditPage } from "@/lib/adsolut-api";

// ---- Telavox call-popup integration (v0.0.34) ----

export type TelavoxConnectionState =
  | "Disabled"
  | "NotConfigured"
  | "NoCustomerSelected"
  | "Ready"
  | "Active";
export type TelavoxStatus = {
  state: TelavoxConnectionState;
  enabled: boolean;
  partnerTokenConfigured: boolean;
  partnerCustomerId: string | null;
  linkedAgentCount: number;
};
export type TelavoxCustomer = { id: string; name: string };
export type TelavoxExtension = {
  id: string;
  number: string;
  name: string;
  userEmail: string | null;
};
export type TelavoxExtensionsResponse = {
  customerId: string;
  items: TelavoxExtension[];
};
export type TelavoxAgentLink = {
  userId: string;
  userEmail: string;
  userRole: string;
  telavoxExtension: string;
  telavoxUserId: string;
  /// PAPI api-user "name" the provisioning service minted for this agent.
  /// Pre-D this field was called `capiUserEmail`; the PAPI swagger takes a
  /// `?name=...` query param, not an email body, so the conceptual rename
  /// followed in v0.0.34 post-D. The DB column is still
  /// `telavox_agent_links.capi_user_email` for backwards compat.
  capiUserName: string;
  provisionedUtc: string;
  lastPollUtc: string | null;
  lastPollError: string | null;
  consecutiveErrors: number;
};
export type TelavoxSecretStatus = { configured: boolean };
/// Telavox contact lookup (v0.1.35): Telavox looks up a ringing caller here.
export type TelavoxContactLookupStatus = {
  enabled: boolean;
  tokenConfigured: boolean;
  path: string;
  found: number;
  notFound: number;
  denied: number;
  lastLookupUtc: string | null;
};
/// Telavox call-event probe (v0.1.36): what Telavox webhooks sent, in memory.
export type TelavoxProbeEvent = {
  receivedUtc: string;
  method: string;
  contentType: string | null;
  fields: { source: string; name: string; value: string }[];
  agentEmail: string | null;
  agentMatchesUser: boolean | null;
};
export type TelavoxEventProbeStatus = {
  enabled: boolean;
  tokenConfigured: boolean;
  path: string;
  received: number;
  events: TelavoxProbeEvent[];
};
export const telavoxAdminApi = {
  status: () =>
    request<TelavoxStatus>("GET", "/api/admin/integrations/telavox/status"),
  secretStatus: () =>
    request<TelavoxSecretStatus>("GET", "/api/admin/integrations/telavox/secret"),
  setSecret: (value: string) =>
    request<void>("PUT", "/api/admin/integrations/telavox/secret", { value }),
  deleteSecret: () =>
    request<void>("DELETE", "/api/admin/integrations/telavox/secret"),
  setCustomerId: (customerId: string) =>
    request<{ customerId: string }>(
      "PUT",
      "/api/admin/integrations/telavox/customer",
      { customerId },
    ),
  clearCustomerId: () =>
    request<void>("DELETE", "/api/admin/integrations/telavox/customer"),
  testConnection: () =>
    request<{ customers: TelavoxCustomer[] }>(
      "POST",
      "/api/admin/integrations/telavox/test-connection",
    ),
  listExtensions: () =>
    request<TelavoxExtensionsResponse>(
      "GET",
      "/api/admin/integrations/telavox/extensions",
    ),
  listAgentLinks: () =>
    request<{ items: TelavoxAgentLink[] }>(
      "GET",
      "/api/admin/integrations/telavox/agents",
    ),
  provisionAgent: (userId: string, extension: string) =>
    request<TelavoxAgentLink>(
      "POST",
      `/api/admin/integrations/telavox/agents/${userId}/provision`,
      { extension },
    ),
  /// Manual-link fallback for installs where PAPI api-user creation is
  /// unavailable. Admin pastes a CAPI bearer-token they minted via the
  /// Telavox webapp; SD stores it verbatim and uses it for per-agent
  /// polling. No upstream call to mint or revoke.
  provisionAgentManual: (userId: string, extension: string, capiToken: string) =>
    request<TelavoxAgentLink>(
      "POST",
      `/api/admin/integrations/telavox/agents/${userId}/provision-manual`,
      { extension, capiToken },
    ),
  revokeAgent: (userId: string) =>
    request<void>(
      "DELETE",
      `/api/admin/integrations/telavox/agents/${userId}/provision`,
    ),
  contactLookupStatus: () =>
    request<TelavoxContactLookupStatus>(
      "GET",
      "/api/admin/integrations/telavox/contact-lookup/status",
    ),
  /// Generates (or rotates) the lookup token; the value is returned once.
  generateContactLookupToken: () =>
    request<{ token: string }>(
      "POST",
      "/api/admin/integrations/telavox/contact-lookup/token",
    ),
  deleteContactLookupToken: () =>
    request<void>(
      "DELETE",
      "/api/admin/integrations/telavox/contact-lookup/token",
    ),
  eventProbe: () =>
    request<TelavoxEventProbeStatus>("GET", "/api/admin/integrations/telavox/event-probe/"),
  clearEventProbe: () =>
    request<void>("DELETE", "/api/admin/integrations/telavox/event-probe/events"),
  /// Generates (or rotates) the probe token; the value is returned once.
  generateEventProbeToken: () =>
    request<{ token: string }>("POST", "/api/admin/integrations/telavox/event-probe/token"),
  deleteEventProbeToken: () =>
    request<void>("DELETE", "/api/admin/integrations/telavox/event-probe/token"),
  auditLog: (cursor: number | null, limit = 50) => {
    const qs = new URLSearchParams();
    if (cursor !== null) qs.set("cursor", String(cursor));
    qs.set("limit", String(limit));
    return request<IntegrationAuditPage>(
      "GET",
      `/api/admin/integrations/telavox/audit?${qs.toString()}`,
    );
  },
};
