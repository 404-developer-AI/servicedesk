import { request } from "@/lib/http";

// ---- Reporting API admin (v0.0.96) ----

export type ReportingApiStatus = {
  enabled: boolean;
  keyConfigured: boolean;
};
export const reportingAdminApi = {
  status: () =>
    request<ReportingApiStatus>("GET", "/api/admin/reporting/status"),
  setEnabled: (enabled: boolean) =>
    request<{ enabled: boolean }>("PUT", "/api/admin/reporting/enabled", {
      enabled,
    }),
  setKey: (value: string) =>
    request<void>("PUT", "/api/admin/reporting/key", { value }),
  deleteKey: () => request<void>("DELETE", "/api/admin/reporting/key"),
};
// ---- Timesheet migration import (v0.0.54) ----

export type TimesheetImportStatus = {
  enabled: boolean;
  secretConfigured: boolean;
};
export const timesheetImportAdminApi = {
  status: () =>
    request<TimesheetImportStatus>("GET", "/api/admin/timesheet/import/status"),
  setEnabled: (enabled: boolean) =>
    request<{ enabled: boolean }>(
      "PUT",
      "/api/admin/timesheet/import/enabled",
      { enabled },
    ),
  setSecret: (value: string) =>
    request<void>("PUT", "/api/admin/timesheet/import/secret", { value }),
  deleteSecret: () =>
    request<void>("DELETE", "/api/admin/timesheet/import/secret"),
};
