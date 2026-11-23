import { request } from "@/lib/http";

// ---- Assets (v0.0.52) ----

export type AssetType = "server" | "workstation";
export type AssetEolStatus = "active" | "soon" | "expired" | "unknown";
export type AssetListItem = {
  id: string;
  trmmAgentId: string;
  hostname: string;
  agentType: AssetType;
  osName: string | null;
  osFamily: string | null;
  osBuild: string | null;
  lastSeenUtc: string | null;
  online: boolean;
  publicIp: string | null;
  trmmClientId: number;
  clientName: string;
  clientCode: string | null;
  companyId: string | null;
  companyName: string | null;
  trmmSiteId: number;
  siteName: string;
  eolUtc: string | null;
  eolStatus: AssetEolStatus;
};
export type AssetDetail = AssetListItem & {
  createdUtc: string;
  updatedUtc: string;
  lastSyncUtc: string;
};
export type AssetListResponse = {
  items: AssetListItem[];
  total: number;
  page: number;
  pageSize: number;
  warnThresholdDays: number;
};
export type AssetSort =
  | "hostname_asc"
  | "hostname_desc"
  | "build_asc"
  | "build_desc"
  | "last_seen_asc"
  | "last_seen_desc"
  | "client_asc"
  | "client_desc";
export type AssetListParams = {
  search?: string;
  type?: AssetType | "";
  builds?: string[];
  companyIds?: string[];
  online?: boolean;
  eolStatus?: AssetEolStatus | "";
  sort?: AssetSort;
  page?: number;
  pageSize?: number;
};
export type AssetSyncState = {
  enabled: boolean;
  lastSyncUtc: string | null;
  lastStatus: "ok" | "failed" | null;
  lastError: string | null;
  syncIntervalMinutes: number;
};
export const assetsApi = {
  list: (params: AssetListParams) => {
    const qs = new URLSearchParams();
    if (params.search?.trim()) qs.set("search", params.search.trim());
    if (params.type) qs.set("type", params.type);
    if (params.builds && params.builds.length > 0)
      qs.set("builds", params.builds.join(","));
    if (params.companyIds && params.companyIds.length > 0)
      qs.set("companyIds", params.companyIds.join(","));
    if (params.online !== undefined) qs.set("online", String(params.online));
    if (params.eolStatus) qs.set("eolStatus", params.eolStatus);
    if (params.sort) qs.set("sort", params.sort);
    if (params.page) qs.set("page", String(params.page));
    if (params.pageSize) qs.set("pageSize", String(params.pageSize));
    const suffix = qs.toString();
    return request<AssetListResponse>(
      "GET",
      suffix.length > 0 ? `/api/assets/?${suffix}` : "/api/assets/",
    );
  },
  listBuilds: () =>
    request<{ items: string[] }>("GET", "/api/assets/builds"),
  syncState: () =>
    request<AssetSyncState>("GET", "/api/assets/sync-state"),
  get: (id: string) => request<AssetDetail>("GET", `/api/assets/${id}`),
};
// ---- Assets → Remote Desktop (v0.1.10) ----

export type RdsStatus =
  | "rds"
  | "not_rds"
  | "failed"
  | "no_check"
  | "pending"
  | "error";
export type RemoteDesktopServer = {
  id: string;
  trmmAgentId: string;
  hostname: string;
  agentType: "server" | "workstation";
  osName: string | null;
  osFamily: string | null;
  osBuild: string | null;
  online: boolean;
  lastSeenUtc: string | null;
  siteName: string;
  trmmClientId: number;
  clientName: string;
  clientDisplayName: string;
  clientCode: string | null;
  companyId: string | null;
  companyName: string | null;
  status: RdsStatus | null;
  checkStatus: string | null;
  retcode: number | null;
  stdout: string | null;
  stderr: string | null;
  /** Unzoned "yyyy-MM-ddTHH:mm:ss" — the server's own clock as printed by the script. */
  lastLoginLocal: string | null;
  lastLoginKind: "rdp" | "local" | string | null;
  lastLoginUser: string | null;
  lastRunUtc: string | null;
  fetchedUtc: string | null;
  fetchError: string | null;
};
export type RemoteDesktopClient = {
  trmmClientId: number;
  name: string;
  displayName: string;
  code: string | null;
  companyId: string | null;
  companyName: string | null;
  noteCount: number;
  lastNoteUtc: string | null;
  servers: RemoteDesktopServer[];
};
export type RemoteDesktopOverview = {
  enabled: boolean;
  trmmEnabled: boolean;
  rdsCheckEnabled: boolean;
  syncIntervalMinutes: number;
  lastRdsSyncUtc: string | null;
  lastRdsStatus: "ok" | "failed" | null;
  lastRdsError: string | null;
  summary: {
    clients: number;
    rdsServers: number;
    attention: number;
    hiddenNotRds: number;
    unchecked: number;
    scopedAgents: number;
  };
  clients: RemoteDesktopClient[];
  attention: RemoteDesktopServer[];
};
export type RemoteDesktopClientNote = {
  id: string;
  trmmClientId: number;
  body: string;
  authorId: string | null;
  authorEmail: string | null;
  createdUtc: string;
  updatedUtc: string;
  edited: boolean;
};
export const remoteDesktopApi = {
  overview: (search: string) => {
    const qs = new URLSearchParams();
    if (search.trim()) qs.set("search", search.trim());
    const suffix = qs.toString();
    return request<RemoteDesktopOverview>(
      "GET",
      suffix.length > 0
        ? `/api/assets/remote-desktop/?${suffix}`
        : "/api/assets/remote-desktop/",
    );
  },
  exportCsvUrl: (search: string) => {
    const qs = new URLSearchParams();
    if (search.trim()) qs.set("search", search.trim());
    const suffix = qs.toString();
    return suffix.length > 0
      ? `/api/assets/remote-desktop/export.csv?${suffix}`
      : "/api/assets/remote-desktop/export.csv";
  },
  listNotes: (trmmClientId: number) =>
    request<{ items: RemoteDesktopClientNote[] }>(
      "GET",
      `/api/assets/remote-desktop/clients/${trmmClientId}/notes`,
    ),
  addNote: (trmmClientId: number, body: string) =>
    request<RemoteDesktopClientNote>(
      "POST",
      `/api/assets/remote-desktop/clients/${trmmClientId}/notes`,
      { body },
    ),
  updateNote: (noteId: string, body: string) =>
    request<RemoteDesktopClientNote>(
      "PUT",
      `/api/assets/remote-desktop/notes/${noteId}`,
      { body },
    ),
  deleteNote: (noteId: string) =>
    request<void>("DELETE", `/api/assets/remote-desktop/notes/${noteId}`),
};
