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
};
