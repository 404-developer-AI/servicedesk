import { request } from "@/lib/http";

// ---- Statistics (v0.0.69) -------------------------------------------
// Light tile builder. Read surface: list assigned tiles + per-tile data +
// save layout. Write surface: catalogue + tile CRUD + assignments.

export type StatisticMetricDescriptor = {
  key: string;
  label: string;
  unit: string;
  chartTypes: string[];
  groupings: string[];
  supportsScope: boolean;
};

export type StatisticTileDto = {
  id: string;
  title: string;
  metricKey: string;
  chartType: string;
  period: string;
  grouping: string;
  scope: string;
  scopeUserId: string | null;
  scopeUserEmail?: string | null;
  position: number;
  size: string;
  hidden: boolean;
};

export type StatisticTileSummary = {
  id: string;
  title: string;
  metricKey: string;
  chartType: string;
  period: string;
  grouping: string;
  scope: string;
  scopeUserId: string | null;
  scopeUserEmail: string | null;
  assignedCount: number;
};

export type StatisticBareTile = {
  id: string;
  title: string;
  metricKey: string;
  chartType: string;
  period: string;
  grouping: string;
  scope: string;
  scopeUserId: string | null;
  scopeUserIds: string[];
};

export type StatisticDataPoint = {
  label: string;
  value: number;
  value2?: number | null;
  segments?: number[] | null;
};

export type StatisticTileData = {
  tileId: string;
  metricKey: string;
  chartType: string;
  unit: string;
  periodLabel: string;
  total: number;
  points: StatisticDataPoint[];
  seriesLabels?: string[] | null;
  generatedUtc: string;
};

export type StatisticTileInput = {
  title: string;
  metricKey: string;
  chartType: string;
  period: string;
  grouping: string;
  scope: string;
  scopeUserId: string | null;
  scopeUserIds?: string[];
};

export type StatisticLayoutEntry = { tileId: string; size: string; hidden: boolean };

export const statisticsApi = {
  // read surface
  listAssigned: () => request<StatisticTileDto[]>("GET", "/api/statistics/tiles"),
  tileData: (id: string, offset = 0) =>
    request<StatisticTileData>("GET", `/api/statistics/tiles/${id}/data?offset=${offset}`),
  saveLayout: (tiles: StatisticLayoutEntry[]) =>
    request<void>("PUT", "/api/statistics/layout", { tiles }),
  // write surface (builder)
  catalogue: () => request<StatisticMetricDescriptor[]>("GET", "/api/statistics/catalogue"),
  manageList: () => request<StatisticTileSummary[]>("GET", "/api/statistics/manage/tiles"),
  manageGet: (id: string) =>
    request<{ tile: StatisticBareTile; assignedUserIds: string[] }>(
      "GET",
      `/api/statistics/manage/tiles/${id}`,
    ),
  create: (input: StatisticTileInput) =>
    request<StatisticBareTile>("POST", "/api/statistics/manage/tiles", input),
  update: (id: string, input: StatisticTileInput) =>
    request<StatisticBareTile>("PUT", `/api/statistics/manage/tiles/${id}`, input),
  remove: (id: string) =>
    request<void>("DELETE", `/api/statistics/manage/tiles/${id}`),
  setAssignments: (id: string, userIds: string[]) =>
    request<{ assignedUserIds: string[] }>(
      "PUT",
      `/api/statistics/manage/tiles/${id}/assignments`,
      { userIds },
    ),
};
