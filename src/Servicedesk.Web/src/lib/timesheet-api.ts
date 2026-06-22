import { csrfHeader } from "@/lib/csrf";
import { ApiError } from "@/lib/ticket-api";

/// Local request helper mirroring the one in ticket-api.ts. Surfaces the
/// API error body via `error.message` (and `ApiError.status`) so the
/// page can show field-level 422 validation problems inline.
async function request<T>(
  method: string,
  url: string,
  body?: unknown,
): Promise<T> {
  const isSafe = method === "GET" || method === "HEAD";
  const res = await fetch(url, {
    method,
    credentials: "include",
    headers: {
      Accept: "application/json",
      ...(body !== undefined ? { "Content-Type": "application/json" } : {}),
      ...(isSafe ? {} : csrfHeader()),
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!res.ok) {
    const text = await res.text().catch(() => "");
    throw new ApiError(
      res.status,
      url,
      text || `${url} → ${res.status} ${res.statusText}`,
    );
  }
  if (res.status === 204) return undefined as T;
  const text = await res.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

/// v0.0.35 — Timesheet feature API surface.
///
/// Two distinct endpoint groups:
///   - `/api/timesheet/tasks` (read) + `/api/admin/timesheet/tasks` (CUD)
///     for the catalogue (Settings → Timesheet tasks);
///   - `/api/timesheet/entries` for the agent's own daily registration.
///
/// Time-of-day is exchanged as **minutes since midnight** (0..1440). The
/// server stores `start_minutes` / `end_minutes` directly so we avoid a
/// timezone round-trip on a concept the agent thinks of locally.

// ---- Tasks (catalogue) -------------------------------------------------

export type TimesheetTask = {
  id: string;
  name: string;
  requiresTicket: boolean;
  isAbsence: boolean;
  archived: boolean;
  sortOrder: number;
  createdUtc: string;
  updatedUtc: string;
};

export type TimesheetTaskUpsert = {
  name: string;
  requiresTicket: boolean;
  isAbsence: boolean;
  archived?: boolean;
  sortOrder?: number;
};

export const timesheetTaskApi = {
  list: (includeArchived = false) =>
    request<TimesheetTask[]>(
      "GET",
      `/api/timesheet/tasks?includeArchived=${includeArchived}`,
    ),

  create: (body: TimesheetTaskUpsert) =>
    request<TimesheetTask>("POST", "/api/admin/timesheet/tasks", body),

  update: (id: string, body: TimesheetTaskUpsert) =>
    request<TimesheetTask>("PUT", `/api/admin/timesheet/tasks/${id}`, body),
};

// ---- Entries (per-user daily registration) ----------------------------

export type TimesheetEntry = {
  id: string;
  userId: string;
  /// Only populated by manager-scoped reads — own-rows reads leave it
  /// empty because the calling agent already knows it's their own data.
  userEmail?: string;
  entryDate: string;
  startMinutes: number;
  endMinutes: number;
  minutes: number;
  taskId: string;
  taskName: string;
  taskRequiresTicket: boolean;
  taskIsAbsence: boolean;
  ticketId: string | null;
  ticketNumber: number | null;
  ticketSubject: string | null;
  companyId: string | null;
  companyName: string | null;
  description: string;
  /// v0.0.36 — billed flag. Display-only in the current iteration; the
  /// toggle UI lands in a later commit so this stays read-only on the
  /// wire and falls back to `false` for rows pre-dating the column.
  invoiced: boolean;
  createdUtc: string;
  updatedUtc: string;
};

export type TimesheetEntryInput = {
  entryDate: string;
  startMinutes: number;
  endMinutes: number;
  taskId: string;
  ticketId: string | null;
  description: string;
};

export type TimesheetFieldError = { field: string; message: string };

export type TimesheetEntryListResponse = {
  date: string;
  items: TimesheetEntry[];
};

export const timesheetEntryApi = {
  listByDate: (date: string) =>
    request<TimesheetEntryListResponse>(
      "GET",
      `/api/timesheet/entries?date=${encodeURIComponent(date)}`,
    ),

  create: (body: TimesheetEntryInput) =>
    request<TimesheetEntry>("POST", "/api/timesheet/entries", body),

  update: (id: string, body: TimesheetEntryInput) =>
    request<TimesheetEntry>("PUT", `/api/timesheet/entries/${id}`, body),

  remove: (id: string) =>
    request<void>("DELETE", `/api/timesheet/entries/${id}`),
};

// ---- Helpers ----------------------------------------------------------

/// Auto-format helper for time inputs. The user can type "0850" and we
/// rewrite it to "08:50" on the third keystroke — the colon is inserted
/// **after** the third digit lands rather than after the second so that
/// backspacing back to "08" doesn't immediately re-add the colon (which
/// would block the user from ever deleting the colon character).
///
/// Inputs that already contain non-digit characters (e.g. the user
/// already typed the colon themselves, or pasted "8:50") are returned
/// unchanged.
export function autoFormatTimeInput(raw: string): string {
  if (!/^\d+$/.test(raw)) return raw;
  if (raw.length <= 2) return raw;
  return raw.slice(0, 2) + ":" + raw.slice(2);
}

/// "HH:MM" → minutes since midnight (or null when unparseable).
export function parseHHMM(value: string): number | null {
  const m = /^(\d{1,2}):(\d{2})$/.exec(value.trim());
  if (!m) return null;
  const hh = Number(m[1]);
  const mm = Number(m[2]);
  if (hh < 0 || hh > 24) return null;
  if (mm < 0 || mm > 59) return null;
  const total = hh * 60 + mm;
  if (total > 1440) return null;
  return total;
}

/// minutes since midnight → "HH:MM" (zero-padded).
export function formatHHMM(minutes: number): string {
  const safe = Math.max(0, Math.min(1440, Math.round(minutes)));
  const hh = Math.floor(safe / 60);
  const mm = safe % 60;
  return `${hh.toString().padStart(2, "0")}:${mm.toString().padStart(2, "0")}`;
}

/// 75 minutes → "1h 15m" — used in the row's total column and day totals.
export function formatDuration(minutes: number): string {
  if (minutes <= 0) return "0m";
  const hh = Math.floor(minutes / 60);
  const mm = minutes % 60;
  if (hh === 0) return `${mm}m`;
  if (mm === 0) return `${hh}h`;
  return `${hh}h ${mm}m`;
}

/// Today as YYYY-MM-DD in the user's local timezone — the date picker
/// opens on this value. Server timezone is irrelevant here; the agent
/// registers in their own day.
export function todayLocalIso(): string {
  const d = new Date();
  const y = d.getFullYear();
  const m = (d.getMonth() + 1).toString().padStart(2, "0");
  const day = d.getDate().toString().padStart(2, "0");
  return `${y}-${m}-${day}`;
}

/// Current local time as minutes since midnight (0..1439). Used as the
/// default for the End field of a fresh draft row — and re-read on a
/// ~30s tick so the field stays current while the agent fills in the
/// other columns, until they manually overwrite it.
export function currentLocalMinutes(): number {
  const d = new Date();
  return d.getHours() * 60 + d.getMinutes();
}

// ---- Manager (Tab 2 + Tab 3) -----------------------------------------
//
// Manager endpoints are gated server-side by the `timesheet_manager`
// per-user flag (independent of role); the UI hides the tabs for
// non-managers but a stray call still 403s.

export type TimesheetUser = {
  id: string;
  email: string;
  enabled: boolean;
  manager: boolean;
};

export type ManagerEntryFilter = {
  /// Single day (YYYY-MM-DD). Empty = every day (paged). Sent to the
  /// server as from=to=day so one date narrows to that one day.
  day?: string;
  userId?: string;
  ticketId?: string;
  taskId?: string;
  search?: string;
  page?: number;
  pageSize?: number;
};

export type ManagerEntriesResponse = {
  items: TimesheetEntry[];
  total: number;
  totalMinutes: number;
  page: number;
  pageSize: number;
};

export type MonthDayBreakdown = {
  taskId: string;
  taskName: string;
  isAbsence: boolean;
  minutes: number;
};

export type MonthDayRollup = {
  date: string;
  workMinutes: number;
  absenceMinutes: number;
  entryCount: number;
  breakdown: MonthDayBreakdown[];
};

export type MonthRollup = {
  userId: string;
  userEmail: string;
  year: number;
  month: number;
  days: MonthDayRollup[];
};

// ---- Preferences (v0.0.35-E) ------------------------------------------

export type TimesheetPreferences = {
  dayStartMinutes: number;
  targetMinutesPerDay: number;
  targetMinutesPerWeek: number;
  /// ISO weekday numbers (1=Mon..7=Sun), already sorted ascending.
  workDays: number[];
  /// v0.0.36 — daily ceiling on absence-task minutes before the ISO-week
  /// is flagged "target not met" in Tab 3. 0 = no ceiling.
  maxAbsenceMinutesPerDay: number;
  /// v0.0.36 — office-hour window. Tab 1 only flags a row-to-row gap or
  /// overlap when the mismatch zone falls inside [start, end].
  officeStartMinutes: number;
  officeEndMinutes: number;
  /// v0.0.74 — the agent's personal default task for new Tab-1 rows.
  /// `null` = no preference; Tab 1 then seeds new rows with the first
  /// active task (sort order).
  defaultTaskId: string | null;
};

export const timesheetPreferencesApi = {
  /// Effective preferences for the caller. Used by Tab 1 to seed the
  /// new-day start time.
  me: () => request<TimesheetPreferences>("GET", "/api/timesheet/me/preferences"),

  /// Effective preferences for any user (manager-only). Used by Tab 3
  /// so the colour grid respects per-user overrides.
  forUser: (userId: string) =>
    request<TimesheetPreferences>(
      "GET",
      `/api/timesheet/manager/preferences/${userId}`,
    ),

  /// v0.0.74 — self-service: set (or clear with `null`) the caller's own
  /// default Tab-1 task. Returns the persisted value.
  setDefaultTask: (taskId: string | null) =>
    request<{ defaultTaskId: string | null }>(
      "PUT",
      "/api/timesheet/me/preferences/default-task",
      { taskId },
    ),
};

// ---- Ticket-scoped (v0.0.35-F) ----------------------------------------
//
// Read-only surface used by the expandable timesheet panel on the ticket
// detail page and by the "Import registered time" button on the reply
// editor. Both endpoints require an Agent/Admin session; the server gates
// the routes.

export type TicketTimesheetResponse = {
  items: TimesheetEntry[];
  totalMinutes: number;
};

// v0.0.87 — per-ticket hour-limit alert snapshot. All fields are
// server-derived; the client only displays them and never recomputes the
// limit or the `exceeded` decision.
export type TicketTimeAlertStatus = {
  enabled: boolean;
  thresholdMinutes: number;
  extraMinutes: number;
  limitMinutes: number;
  totalMinutes: number;
  remainingMinutes: number;
  exceeded: boolean;
  defaultExtraMinutes: number;
  confirmationText: string;
};

export const timesheetTicketApi = {
  list: (ticketId: string) =>
    request<TicketTimesheetResponse>("GET", `/api/timesheet/ticket/${ticketId}`),

  /// Returns pre-rendered HTML built from the admin-editable template in
  /// Settings → Timesheet. The reply editor pastes this verbatim.
  replyHtml: (ticketId: string) =>
    request<{ html: string }>("GET", `/api/timesheet/ticket/${ticketId}/reply-html`),

  /// v0.0.87 — hour-limit status for the ticket-open warning + remaining
  /// time display.
  timeAlert: (ticketId: string) =>
    request<TicketTimeAlertStatus>(
      "GET",
      `/api/timesheet/ticket/${ticketId}/time-alert`,
    ),

  /// Agent dismissed the warning (logged, limit unchanged).
  dismissTimeAlert: (ticketId: string) =>
    request<void>(
      "POST",
      `/api/timesheet/ticket/${ticketId}/time-alert/dismiss`,
    ),

  /// Agent raised the ticket's limit. `customerConfirmed` is the mandatory
  /// written-confirmation tick; the server re-checks it.
  extendTimeAlert: (
    ticketId: string,
    body: { addMinutes: number; customerConfirmed: boolean },
  ) =>
    request<void>(
      "POST",
      `/api/timesheet/ticket/${ticketId}/time-alert/extend`,
      body,
    ),
};

export const timesheetManagerApi = {
  listUsers: () =>
    request<TimesheetUser[]>("GET", "/api/timesheet/manager/users"),

  listEntries: (filter: ManagerEntryFilter) => {
    const p = new URLSearchParams();
    // One day narrows to that day; an empty day sends no bound = all days.
    if (filter.day) {
      p.set("from", filter.day);
      p.set("to", filter.day);
    }
    if (filter.userId) p.set("userId", filter.userId);
    if (filter.ticketId) p.set("ticketId", filter.ticketId);
    if (filter.taskId) p.set("taskId", filter.taskId);
    if (filter.search && filter.search.trim()) p.set("search", filter.search.trim());
    if (filter.page) p.set("page", String(filter.page));
    if (filter.pageSize) p.set("pageSize", String(filter.pageSize));
    return request<ManagerEntriesResponse>(
      "GET",
      `/api/timesheet/manager/entries?${p.toString()}`,
    );
  },

  updateEntry: (id: string, body: TimesheetEntryInput) =>
    request<TimesheetEntry>("PUT", `/api/timesheet/manager/entries/${id}`, body),

  deleteEntry: (id: string) =>
    request<void>("DELETE", `/api/timesheet/manager/entries/${id}`),

  getMonth: (userId: string, year: number, month: number) =>
    request<MonthRollup>(
      "GET",
      `/api/timesheet/manager/month?userId=${userId}&year=${year}&month=${month}`,
    ),

  /// Returns the export URL. We don't `fetch` it here because the
  /// browser handles a normal anchor click cleanly (Content-Disposition
  /// kicks the download); fetching would require manual blob plumbing.
  exportCsvUrl: (userId: string, year: number, month: number) =>
    `/api/timesheet/manager/month/export.csv?userId=${userId}&year=${year}&month=${month}`,
};
