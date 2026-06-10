// v0.0.31 Knowledge Base API client. Mirrors the shape of intakeFormsApi:
// one object of named functions wrapping a small `request` helper with
// CSRF + cookie credentials.

export type KbConfig = {
  id: string;
  isActive: boolean;
  defaultLocaleCode: string;
  createdUtc: string;
  updatedUtc: string;
};

export type KbLocale = {
  code: string;
  displayName: string;
  isActive: boolean;
  sortOrder: number;
};

export type KbSection = {
  id: string;
  parentSectionId: string | null;
  slug: string;
  iconName: string | null;
  position: number;
  createdUtc: string;
  updatedUtc: string;
  createdByUserId: string | null;
  updatedByUserId: string | null;
};

export type KbSectionTranslation = {
  id: string;
  sectionId: string;
  localeCode: string;
  title: string;
  description: string | null;
  createdUtc: string;
  updatedUtc: string;
};

/// Output shape of GET /api/kb/sections — the recursive section-tree with
/// the default-locale title + description pre-resolved per node so the
/// frontend never has to merge translations itself.
export type KbSectionNode = {
  id: string;
  parentSectionId: string | null;
  slug: string;
  iconName: string | null;
  position: number;
  title: string;
  description: string | null;
  children: KbSectionNode[];
};

export type KbSectionTree = {
  config: KbConfig;
  tree: KbSectionNode[];
};

export type KbArticleStatus = "Draft" | "Internal" | "Published" | "Archived";

export type KbArticle = {
  id: string;
  sectionId: string;
  slug: string;
  status: KbArticleStatus;
  isFeatured: boolean;
  editorNotes: string | null;
  position: number;
  publishAt: string | null;
  archiveAt: string | null;
  lastStatusChangedUtc: string;
  lastStatusChangedByUserId: string | null;
  createdUtc: string;
  updatedUtc: string;
  createdByUserId: string | null;
  updatedByUserId: string | null;
};

export type KbArticleTranslation = {
  id: string;
  articleId: string;
  localeCode: string;
  title: string;
  bodyHtml: string;
  bodyText: string;
  createdUtc: string;
  updatedUtc: string;
};

export type KbArticleListItem = {
  id: string;
  sectionId: string;
  slug: string;
  status: KbArticleStatus;
  isFeatured: boolean;
  position: number;
  createdUtc: string;
  updatedUtc: string;
  lastStatusChangedUtc: string;
  lastStatusChangedByUserId: string | null;
  title: string;
};

export type KbArticleListPage = {
  items: KbArticleListItem[];
  totalHits: number;
  page: number;
  pageSize: number;
};

export type KbFeaturedArticle = {
  id: string;
  sectionId: string;
  slug: string;
  title: string;
  updatedUtc: string;
};

export type KbArticleWithTranslation = {
  article: KbArticle;
  translation: KbArticleTranslation;
};

export type KbSectionRequest = {
  parentSectionId?: string | null;
  slug?: string;
  iconName?: string | null;
  position?: number;
  title?: string;
  description?: string | null;
  localeCode?: string;
};

export type KbArticleCreateRequest = {
  sectionId: string;
  slug?: string;
  title?: string;
  bodyHtml?: string;
  editorNotes?: string | null;
  position?: number;
  localeCode?: string;
};

export type KbArticleUpdateRequest = {
  sectionId?: string;
  slug?: string;
  title?: string;
  bodyHtml?: string;
  editorNotes?: string | null;
  position?: number;
  localeCode?: string;
};

async function request<T>(method: string, url: string, body?: unknown): Promise<T> {
  const csrf = document.cookie
    .split("; ")
    .find((c) => c.startsWith("XSRF-TOKEN="))
    ?.split("=")[1];
  const res = await fetch(url, {
    method,
    credentials: "include",
    headers: {
      "Content-Type": "application/json",
      ...(csrf ? { "X-XSRF-TOKEN": decodeURIComponent(csrf) } : {}),
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!res.ok) {
    let payload: unknown = null;
    try { payload = await res.json(); } catch { /* ignore */ }
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

export const kbApi = {
  // ─── Config + Locales (Admin only) ───
  getConfig: () => request<KbConfig>("GET", "/api/kb/config"),
  updateConfig: (body: { isActive?: boolean; defaultLocaleCode: string }) =>
    request<KbConfig>("PUT", "/api/kb/config", body),
  listLocales: (includeInactive = true) =>
    request<KbLocale[]>("GET", `/api/kb/locales${includeInactive ? "?includeInactive=true" : ""}`),
  upsertLocale: (body: KbLocale) =>
    request<KbLocale>("POST", "/api/kb/locales", body),
  removeLocale: (code: string) =>
    request<void>("DELETE", `/api/kb/locales/${encodeURIComponent(code)}`),

  // ─── Sections (Agent+Admin reads, Admin writes) ───
  listSections: () => request<KbSectionTree>("GET", "/api/kb/sections"),
  getSection: (id: string) =>
    request<{ section: KbSection; translations: KbSectionTranslation[] }>(
      "GET",
      `/api/kb/sections/${id}`,
    ),
  createSection: (body: KbSectionRequest) =>
    request<{ section: KbSection; translation: KbSectionTranslation }>(
      "POST",
      "/api/kb/sections",
      body,
    ),
  updateSection: (id: string, body: KbSectionRequest) =>
    request<KbSection>("PUT", `/api/kb/sections/${id}`, body),
  deleteSection: (id: string) =>
    request<void>("DELETE", `/api/kb/sections/${id}`),
  moveSection: (id: string, body: { parentSectionId: string | null; position?: number }) =>
    request<KbSection>("POST", `/api/kb/sections/${id}/move`, body),

  // ─── Articles (Agent+Admin reads, mixed writes) ───
  listArticles: (params: {
    sectionId?: string;
    status?: KbArticleStatus;
    search?: string;
    page?: number;
    pageSize?: number;
  } = {}) => {
    const usp = new URLSearchParams();
    if (params.sectionId) usp.set("sectionId", params.sectionId);
    if (params.status) usp.set("status", params.status);
    if (params.search) usp.set("search", params.search);
    if (params.page) usp.set("page", String(params.page));
    if (params.pageSize) usp.set("pageSize", String(params.pageSize));
    const qs = usp.toString();
    return request<KbArticleListPage>(
      "GET",
      `/api/kb/articles${qs ? `?${qs}` : ""}`,
    );
  },
  getArticle: (id: string, includeBody = false) =>
    request<{ article: KbArticle; translation: KbArticleTranslation | null }>(
      "GET",
      `/api/kb/articles/${id}${includeBody ? "?include=body" : ""}`,
    ),
  getArticleBySlug: (sectionSlug: string, articleSlug: string) =>
    request<KbArticleWithTranslation>(
      "GET",
      `/api/kb/articles/by-slug/${encodeURIComponent(sectionSlug)}/${encodeURIComponent(articleSlug)}`,
    ),
  listFeatured: (limit = 6) =>
    request<KbFeaturedArticle[]>("GET", `/api/kb/featured?limit=${limit}`),
  // v0.0.75 — tells the `::` picker whether public article links are
  // enabled and which base URL to build them with (App.PublicBaseUrl;
  // empty when unset — callers fall back to window.location.origin).
  publicLinkConfig: () =>
    request<{ enabled: boolean; publicBaseUrl: string }>(
      "GET",
      "/api/kb/public-link-config",
    ),
  createArticle: (body: KbArticleCreateRequest) =>
    request<{ article: KbArticle; translation: KbArticleTranslation }>(
      "POST",
      "/api/kb/articles",
      body,
    ),
  updateArticle: (id: string, body: KbArticleUpdateRequest) =>
    request<KbArticle>("PUT", `/api/kb/articles/${id}`, body),
  changeStatus: (id: string, status: KbArticleStatus) =>
    request<KbArticle>("POST", `/api/kb/articles/${id}/status`, { status }),
  setFeatured: (id: string, isFeatured: boolean) =>
    request<KbArticle>("POST", `/api/kb/articles/${id}/featured`, { isFeatured }),
  moveArticle: (id: string, body: { sectionId: string; position?: number }) =>
    request<KbArticle>("POST", `/api/kb/articles/${id}/move`, body),
  deleteArticle: (id: string, hard = false) =>
    request<void>("DELETE", `/api/kb/articles/${id}${hard ? "?hard=true" : ""}`),

  /// Upload an inline image (or any non-HTML attachment) for an article.
  /// Returned shape matches TicketAttachmentMeta so the shared
  /// RichTextEditor can route paste/drag/drop/paperclip flows through one
  /// callback regardless of context. Multipart upload — uses raw fetch so
  /// the server-side MIME-sniffer sees the actual bytes.
  uploadAttachment: async (
    articleId: string,
    file: File,
  ): Promise<{ id: string; url: string; mimeType: string; size: number; filename: string }> => {
    const csrf = document.cookie
      .split("; ")
      .find((c) => c.startsWith("XSRF-TOKEN="))
      ?.split("=")[1];
    const fd = new FormData();
    fd.append("file", file, file.name);
    const res = await fetch(`/api/kb/articles/${articleId}/attachments`, {
      method: "POST",
      credentials: "include",
      headers: csrf ? { "X-XSRF-TOKEN": decodeURIComponent(csrf) } : undefined,
      body: fd,
    });
    if (!res.ok) {
      let payload: unknown = null;
      try { payload = await res.json(); } catch { /* ignore */ }
      const err = new Error(`Upload failed: ${res.status}`) as Error & {
        status?: number; payload?: unknown;
      };
      err.status = res.status;
      err.payload = payload;
      throw err;
    }
    return res.json();
  },
};

// ─── Public (no-login) article reader — v0.0.75 ───

export type KbPublicArticle = {
  id: string;
  slug: string;
  title: string;
  bodyHtml: string;
  updatedUtc: string;
};

/// Anonymous fetch for the /kb/public/:id reader page. `credentials:
/// "omit"` like the public intake/survey helpers — the customer has no
/// session and the endpoint must never depend on one. The server only
/// serves Published articles and 404s everything else.
export const publicKbApi = {
  get: async (id: string): Promise<KbPublicArticle> => {
    const res = await fetch(`/api/public/kb/articles/${encodeURIComponent(id)}`, {
      credentials: "omit",
      headers: { Accept: "application/json" },
    });
    if (!res.ok) {
      const err = new Error(`Public article fetch failed: ${res.status}`) as Error & {
        status?: number;
      };
      err.status = res.status;
      throw err;
    }
    return res.json();
  },
};
