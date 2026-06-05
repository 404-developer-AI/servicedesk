// API client for the Email Signatures feature (v0.0.58). Admin-managed,
// mailbox-scoped HTML signatures rendered from a block-tree design. The server
// is authoritative for the send-time render (inline cid images); this client
// drives the builder + a client-side live preview (asset URLs, not cid).

// ---- block-tree design (mirrors Servicedesk.Domain.Signatures) ----

export type SignatureBlockType =
  | "text"
  | "image"
  | "divider"
  | "spacer"
  | "social"
  | "disclaimer"
  | "contactline";

export interface SignatureSocialItem {
  network: string;
  url: string;
  assetId?: string | null;
}

export interface SignatureBlock {
  type: SignatureBlockType;
  // text / disclaimer
  html?: string | null;
  // image
  assetId?: string | null;
  /** Dynamic source, e.g. "agent.photo" — resolved per sender at send time. */
  variable?: string | null;
  widthPx?: number | null;
  /** image: render height; spacer: gap height. */
  heightPx?: number | null;
  href?: string | null;
  alt?: string | null;
  radiusPx?: number | null;
  // divider
  color?: string | null;
  thicknessPx?: number | null;
  marginPx?: number | null;
  // social
  social?: SignatureSocialItem[] | null;
}

export interface SignatureColumn {
  widthPct?: number | null;
  valign?: "top" | "middle" | "bottom" | null;
  blocks: SignatureBlock[];
}

export interface SignatureRow {
  columns: SignatureColumn[];
}

export interface SignatureDesign {
  version: number;
  background?: string | null;
  fontFamily?: string | null;
  maxWidthPx?: number | null;
  rows: SignatureRow[];
}

export const EMPTY_DESIGN: SignatureDesign = {
  version: 1,
  rows: [],
};

// ---- entities ----

export interface SignatureAsset {
  id: string;
  url: string;
  mimeType: string;
  filename: string;
  size: number;
}

export interface SignatureSummary {
  id: string;
  name: string;
  isSystem: boolean;
  enabled: boolean;
  sortOrder: number;
  queueIds: string[];
  updatedUtc: string;
}

export interface SignatureDetail {
  id: string;
  name: string;
  design: SignatureDesign;
  isSystem: boolean;
  enabled: boolean;
  sortOrder: number;
  queueIds: string[];
  assets: SignatureAsset[];
  createdUtc: string;
  updatedUtc: string;
}

export interface SignatureUpsert {
  name: string;
  design: SignatureDesign;
  isSystem: boolean;
  enabled: boolean;
  sortOrder?: number;
  queueIds: string[];
}

export interface SignatureTokenInfo {
  token: string;
  label: string;
}

/** The signed-in agent's local override fields (feed the {{agent.*}} tokens). */
export interface MySignatureProfile {
  displayName: string | null;
  jobTitle: string | null;
  workPhone: string | null;
  mobilePhone: string | null;
  hasPhoto: boolean;
  photoUrl: string | null;
  entraSyncedUtc: string | null;
}

export interface MySignatureProfileUpdate {
  displayName: string | null;
  jobTitle: string | null;
  workPhone: string | null;
  mobilePhone: string | null;
}

/** Resolved values for the current user — drives the live preview. */
export interface MySignatureVariables {
  fullName: string;
  firstName: string;
  lastName: string;
  jobTitle: string;
  email: string;
  phone: string;
  mobile: string;
  photoUrl: string | null;
}

function csrf(): Record<string, string> {
  const token = document.cookie
    .split("; ")
    .find((c) => c.startsWith("XSRF-TOKEN="))
    ?.split("=")[1];
  return token ? { "X-XSRF-TOKEN": decodeURIComponent(token) } : {};
}

async function request<T>(method: string, url: string, body?: unknown): Promise<T> {
  const res = await fetch(url, {
    method,
    credentials: "include",
    headers: { "Content-Type": "application/json", ...csrf() },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!res.ok) {
    let payload: unknown = null;
    try {
      payload = await res.json();
    } catch {
      /* ignore */
    }
    const err = new Error(`Request failed: ${res.status}`) as Error & {
      status?: number;
      payload?: unknown;
    };
    err.status = res.status;
    err.payload = payload;
    throw err;
  }
  if (res.status === 204) return undefined as T;
  const text = await res.text();
  return text ? (JSON.parse(text) as T) : (undefined as T);
}

async function upload<T>(url: string, file: File): Promise<T> {
  const form = new FormData();
  form.append("file", file);
  const res = await fetch(url, {
    method: "POST",
    credentials: "include",
    headers: { ...csrf() },
    body: form,
  });
  if (!res.ok) {
    let payload: unknown = null;
    try {
      payload = await res.json();
    } catch {
      /* ignore */
    }
    const err = new Error(`Upload failed: ${res.status}`) as Error & {
      status?: number;
      payload?: unknown;
    };
    err.status = res.status;
    err.payload = payload;
    throw err;
  }
  const text = await res.text();
  return text ? (JSON.parse(text) as T) : (undefined as T);
}

export const signaturesApi = {
  list: () => request<SignatureSummary[]>("GET", "/api/settings/signatures"),
  get: (id: string) => request<SignatureDetail>("GET", `/api/settings/signatures/${id}`),
  create: (body: SignatureUpsert) =>
    request<SignatureDetail>("POST", "/api/settings/signatures", body),
  update: (id: string, body: SignatureUpsert) =>
    request<SignatureDetail>("PUT", `/api/settings/signatures/${id}`, body),
  remove: (id: string) => request<void>("DELETE", `/api/settings/signatures/${id}`),

  uploadAsset: (id: string, file: File) =>
    upload<SignatureAsset>(`/api/settings/signatures/${id}/assets`, file),
  deleteAsset: (id: string, assetId: string) =>
    request<void>("DELETE", `/api/settings/signatures/${id}/assets/${assetId}`),

  listTokens: () =>
    request<{ tokens: SignatureTokenInfo[]; photoVariable: string }>(
      "GET",
      "/api/settings/signatures/tokens",
    ),

  // v0.0.61 — portable single-file bundle (design + static image assets as
  // base64). Export downloads it; import re-creates the signature (and its
  // images) on another install, disabled, ready to assign to a mailbox.
  exportBundle: (id: string) =>
    request<SignatureBundle>("GET", `/api/settings/signatures/${id}/export`),
  importBundle: (bundle: SignatureBundle) =>
    request<SignatureDetail>("POST", "/api/settings/signatures/import", bundle),
};

export interface SignatureBundleAsset {
  id: string;
  mimeType: string;
  filename: string;
  dataBase64: string;
}

export interface SignatureBundle {
  kind: string;
  version: number;
  name: string;
  design: SignatureDesign;
  assets: SignatureBundleAsset[];
}

export const signatureProfileApi = {
  get: () => request<MySignatureProfile>("GET", "/api/me/signature-profile"),
  update: (body: MySignatureProfileUpdate) =>
    request<void>("PUT", "/api/me/signature-profile", body),
  uploadPhoto: (file: File) =>
    upload<{ photoUrl: string; mimeType: string }>("/api/me/signature-photo", file),
  deletePhoto: () => request<void>("DELETE", "/api/me/signature-photo"),
  variables: () => request<MySignatureVariables>("GET", "/api/me/signature-variables"),
};

/** Admin-managed profile row — one per agent/admin user. */
export interface TeamProfile {
  userId: string;
  email: string;
  role: string;
  displayName: string | null;
  jobTitle: string | null;
  workPhone: string | null;
  mobilePhone: string | null;
  hasPhoto: boolean;
  photoUrl: string | null;
  entraSyncedUtc: string | null;
}

export interface TeamProfileUpdate {
  displayName: string | null;
  jobTitle: string | null;
  workPhone: string | null;
  mobilePhone: string | null;
}

export const teamProfilesApi = {
  list: () => request<TeamProfile[]>("GET", "/api/admin/signature-profiles"),
  update: (userId: string, body: TeamProfileUpdate) =>
    request<void>("PUT", `/api/admin/signature-profiles/${userId}`, body),
  uploadPhoto: (userId: string, file: File) =>
    upload<{ photoUrl: string; mimeType: string }>(
      `/api/admin/signature-profiles/${userId}/photo`,
      file,
    ),
  deletePhoto: (userId: string) =>
    request<void>("DELETE", `/api/admin/signature-profiles/${userId}/photo`),
};

export const FRAME_URL = "/api/admin/signature-profiles/frame";

export const photoFrameApi = {
  upload: (file: File) =>
    upload<{ frameUrl: string; mimeType: string }>(FRAME_URL, file),
  remove: () => request<void>("DELETE", FRAME_URL),
};
