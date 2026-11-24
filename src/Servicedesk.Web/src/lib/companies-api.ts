import { request } from "@/lib/http";
import {
  type Company,
  type CompanyDomain,
  type ContactAuditEntry,
  type ContactCompanyRole,
  type ContactListItem,
  type PortalRole,
} from "@/lib/ticket-api";

// ---- Contacts ----

export type Contact = {
  id: string;
  /// The id of the company the contact is linked to with role='primary'.
  /// Read-only — server computes this from the contact_companies join table.
  /// A contact may also have secondary/supplier links, but those live on
  /// the contact-detail view, not here.
  primaryCompanyId: string | null;
  companyRole: string;
  firstName: string;
  lastName: string;
  email: string;
  phone: string;
  /// v0.0.28 — mirrors the Adsolut `mobilePhone` field. Empty string when
  /// not set; renders as a hidden row in the contact-detail UI.
  mobilePhone: string;
  jobTitle: string;
  isActive: boolean;
  createdUtc: string;
  updatedUtc: string;
};

export type ContactOverviewPage = {
  items: ContactListItem[];
  total: number;
  page: number;
  pageSize: number;
};

export type ContactAuditPage = {
  items: ContactAuditEntry[];
  nextCursor: number | null;
};

export type ContactBrowseQuery = {
  search?: string;
  companyId?: string;
  role?: ContactCompanyRole | "none";
  includeInactive?: boolean;
  sort?: "name_asc" | "email_asc" | "last_activity_desc";
  page?: number;
  pageSize?: number;
};

export type ContactInput = {
  email: string;
  /// Shorthand on create/update: when set the server also inserts a
  /// link to this company in the same transaction. On POST /api/contacts
  /// the role below decides whether it's primary/secondary/supplier; on
  /// PUT the link is always upserted as 'primary' (legacy behaviour).
  companyId?: string | null;
  /// Only honoured on POST when companyId is set. Defaults to 'primary'.
  role?: ContactCompanyRole;
  companyRole?: string;
  firstName?: string;
  lastName?: string;
  phone?: string;
  mobilePhone?: string;
  jobTitle?: string;
  isActive?: boolean;
};

export type CompanyInput = {
  name: string;
  code: string;
  shortName?: string;
  vatNumber?: string;
  email?: string;
  description?: string;
  website?: string;
  phone?: string;
  addressLine1?: string;
  addressLine2?: string;
  city?: string;
  postalCode?: string;
  country?: string;
  isActive?: boolean;
  alertText?: string;
  alertOnCreate?: boolean;
  alertOnOpen?: boolean;
  alertOnOpenMode?: "session" | "every";
};

export type CompanyDetail = {
  company: Company;
  domains: CompanyDomain[];
};

/// One role-tagged link between a contact and a company. Returned by
/// /api/companies/{id}/links for the Contacts-tab so each contact row can
/// render with its role badge relative to *this* company.
export type ContactCompanyLink = {
  id: string;
  contactId: string;
  companyId: string;
  role: ContactCompanyRole;
  createdUtc: string;
  updatedUtc: string;
  /// v0.1.0 — customer-portal role for this link (null = Member on
  /// primary/secondary links, no portal access on supplier links).
  portalRole: PortalRole | null;
};

/// One entry in the contact's role-annotated company list — used by the
/// ticket company-assignment dialog and future contact-detail view.
export type ContactCompanyOption = {
  linkId: string;
  companyId: string;
  companyName: string;
  companyCode: string;
  companyShortName: string;
  companyIsActive: boolean;
  role: ContactCompanyRole;
  portalRole: PortalRole | null;
};

/// Minimal company row returned by the agent-readable picker endpoint.
export type CompanyPickerItem = {
  id: string;
  name: string;
  code: string;
  shortName: string;
  isActive: boolean;
};

/// Row shape returned by the call-popup's phone-keyed lookup. Adds the
/// contact's "best" company link (primary → secondary → supplier priority)
/// on top of the flat Contact fields so the popup can render the linked
/// company without a second round-trip. `linkedCompany*` are all null when
/// the contact has no links at all.
export type ContactPhoneLookupItem = Contact & {
  linkedCompanyId: string | null;
  linkedCompanyName: string | null;
  linkedCompanyRole: ContactCompanyRole | null;
};

export type ContactPhoneLookupResponse = {
  /// Server-normalised E.164 form of the supplied phone. `null` when the
  /// input could not be parsed against the install's default country —
  /// the popup uses that as the "unknown caller" signal.
  phoneE164: string | null;
  items: ContactPhoneLookupItem[];
};

export const contactApi = {
  list: (search?: string, companyId?: string) => {
    const params = new URLSearchParams();
    if (search) params.set("search", search);
    if (companyId) params.set("companyId", companyId);
    const qs = params.toString();
    return request<Contact[]>("GET", `/api/contacts${qs ? `?${qs}` : ""}`);
  },
  /// v0.0.34 — backs the Telavox call-popup. Server normalises the raw
  /// CAPI `From` number to E.164 using the install-wide DefaultCountryCode
  /// and matches against `phone_e164` / `mobile_phone_e164`.
  lookupByPhone: (phone: string, limit = 10) => {
    const params = new URLSearchParams({ phone, limit: String(limit) });
    return request<ContactPhoneLookupResponse>(
      "GET",
      `/api/contacts/lookup-by-phone?${params.toString()}`,
    );
  },
  browse: (query: ContactBrowseQuery = {}) => {
    const params = new URLSearchParams();
    if (query.search) params.set("search", query.search);
    if (query.companyId) params.set("companyId", query.companyId);
    if (query.role) params.set("role", query.role);
    if (query.includeInactive) params.set("includeInactive", "true");
    if (query.sort) params.set("sort", query.sort);
    if (query.page) params.set("page", String(query.page));
    if (query.pageSize) params.set("pageSize", String(query.pageSize));
    const qs = params.toString();
    return request<ContactOverviewPage>("GET", `/api/contacts/browse${qs ? `?${qs}` : ""}`);
  },
  audit: (id: string, cursor?: number, limit?: number) => {
    const params = new URLSearchParams();
    if (cursor) params.set("cursor", String(cursor));
    if (limit) params.set("limit", String(limit));
    const qs = params.toString();
    return request<ContactAuditPage>("GET", `/api/contacts/${id}/audit${qs ? `?${qs}` : ""}`);
  },
  get: (id: string) => request<Contact>("GET", `/api/contacts/${id}`),
  listCompanies: (id: string) =>
    request<ContactCompanyOption[]>("GET", `/api/contacts/${id}/companies`),
  create: (input: ContactInput) =>
    request<Contact>("POST", "/api/contacts", input),
  update: (id: string, input: ContactInput) =>
    request<Contact>("PUT", `/api/contacts/${id}`, input),
};

export const companyApi = {
  list: (search?: string, includeInactive = false) => {
    const params = new URLSearchParams();
    if (search) params.set("search", search);
    if (includeInactive) params.set("includeInactive", "true");
    const qs = params.toString();
    return request<Company[]>("GET", `/api/companies${qs ? `?${qs}` : ""}`);
  },
  get: (id: string) => request<CompanyDetail>("GET", `/api/companies/${id}`),
  picker: (search?: string) => {
    const params = new URLSearchParams();
    if (search) params.set("search", search);
    const qs = params.toString();
    return request<CompanyPickerItem[]>("GET", `/api/companies/picker${qs ? `?${qs}` : ""}`);
  },
  create: (input: CompanyInput) => request<Company>("POST", "/api/companies", input),
  update: (id: string, input: CompanyInput) =>
    request<Company>("PUT", `/api/companies/${id}`, input),
  remove: (id: string) => request<void>("DELETE", `/api/companies/${id}`),
  listContacts: (id: string, search?: string) => {
    const params = new URLSearchParams();
    if (search) params.set("search", search);
    const qs = params.toString();
    return request<Contact[]>("GET", `/api/companies/${id}/contacts${qs ? `?${qs}` : ""}`);
  },
  links: (id: string) =>
    request<ContactCompanyLink[]>("GET", `/api/companies/${id}/links`),
  linkContact: (companyId: string, contactId: string, role?: ContactCompanyRole) =>
    request<void>(
      "POST",
      `/api/companies/${companyId}/contacts/${contactId}`,
      role ? { role } : undefined,
    ),
  unlinkContact: (companyId: string, contactId: string) =>
    request<void>("DELETE", `/api/companies/${companyId}/contacts/${contactId}`),
  addDomain: (id: string, domain: string) =>
    request<CompanyDomain>("POST", `/api/companies/${id}/domains`, { domain }),
  removeDomain: (id: string, domainId: string) =>
    request<void>("DELETE", `/api/companies/${id}/domains/${domainId}`),
};
