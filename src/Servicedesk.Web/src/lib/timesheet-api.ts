import { request } from "@/lib/http";
import { serverWallClockParts } from "@/lib/dateFormat";
import { serverNowMs } from "@/hooks/useServerTime";
export { formatDuration } from "@/lib/format";

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

/// Current server-zone time as minutes since midnight (0..1439). Used as the
/// default for the End field of a fresh draft row — and re-read on a
/// ~30s tick so the field stays current while the agent fills in the
/// other columns, until they manually overwrite it.
export function currentLocalMinutes(): number {
  const p = serverWallClockParts(serverNowMs());
  return p ? p.hh * 60 + p.mi : 0;
}

// ---- Shared entry-draft rules (Tab 1 + ticket "Log time") -------------
// v0.1.19 — one copy of the client-side rules so every place that creates
// an entry asks for the same fields. The server re-validates all of it.

/// Fallback first start of the day when preferences haven't loaded.
export const DEFAULT_DAY_START_MINUTES = 8 * 60 + 30; // 08:30

/// Task a fresh entry starts on: the agent's personal default when it's
/// set and still active, otherwise the first active task (sort order).
/// Null only when there is no active task at all.
export function seedTaskId(
  tasks: TimesheetTask[],
  defaultTaskId: string | null | undefined,
): string | null {
  if (defaultTaskId) {
    const preferred = tasks.find((t) => t.id === defaultTaskId && !t.archived);
    if (preferred) return preferred.id;
  }
  const firstActive = tasks.find((t) => !t.archived);
  return firstActive ? firstActive.id : null;
}

/// Start of the next entry on a day: the latest end among the agent's
/// own entries that day, else their day-start preference.
export function nextStartMinutes(entries: TimesheetEntry[], dayStartMinutes: number): number {
  return entries.length ? Math.max(...entries.map((e) => e.endMinutes)) : dayStartMinutes;
}

export function validateEntryDraft(draft: {
  startMinutes: number | null;
  endMinutes: number | null;
  taskId: string | null | undefined;
  hasTicket: boolean;
  requiresTicket: boolean;
  description: string;
}): Record<string, string> {
  const errs: Record<string, string> = {};
  if (draft.startMinutes === null) errs.startMinutes = "Start time is required (HH:MM).";
  if (draft.endMinutes === null) errs.endMinutes = "End time is required (HH:MM).";
  if (draft.startMinutes !== null && draft.endMinutes !== null && draft.endMinutes <= draft.startMinutes) {
    errs.endMinutes = "End must be after start.";
  }
  if (!draft.taskId) errs.taskId = "Pick a task.";
  if (draft.requiresTicket && !draft.hasTicket) errs.ticketId = "This task needs a ticket.";
  if (draft.description.trim().length === 0) errs.description = "Describe what you did.";
  return errs;
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
  // v0.0.94 presence columns — minutes since midnight in the app timezone,
  // null when the day has no login / no entries. firstLoginKind tells the
  // tooltip whether the first login was a standard or an M365 one.
  firstLoginMinutes: number | null;
  firstLoginKind: "password" | "microsoft" | null;
  firstClockMinutes: number | null;
  lastClockMinutes: number | null;
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
  // v0.0.88 — tracking turned off for this ticket; alert never fires.
  trackingDisabled: boolean;
  // v0.0.88 — prompt above the mandatory reason field on the disable form.
  disableReasonPrompt: string;
  // v0.1.12 — false: no Cancel/Esc/outside-click; only the admin Shift-held
  // silent dismiss remains (server refuses logged dismissals).
  allowCancel: boolean;
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

  /// Agent dismissed the warning (logged, limit unchanged). Admins may pass
  /// `silent` to dismiss without writing a timeline event; the server only
  /// honours it for the Admin role and downgrades it to a logged dismissal
  /// otherwise.
  dismissTimeAlert: (ticketId: string, silent = false) =>
    request<void>(
      "POST",
      `/api/timesheet/ticket/${ticketId}/time-alert/dismiss${silent ? "?silent=true" : ""}`,
    ),

  /// Agent raised the ticket's limit. `customerConfirmed` is the mandatory
  /// written-confirmation tick; the server re-checks it. `note` (v0.0.88) is
  /// optional and, when present, posted as an internal note on the ticket.
  extendTimeAlert: (
    ticketId: string,
    body: { addMinutes: number; customerConfirmed: boolean; note?: string },
  ) =>
    request<void>(
      "POST",
      `/api/timesheet/ticket/${ticketId}/time-alert/extend`,
      body,
    ),

  /// v0.0.88 — agent disabled hour tracking for this ticket. `reason` is
  /// mandatory (re-checked server-side) and posted as an internal note.
  disableTimeAlert: (ticketId: string, body: { reason: string }) =>
    request<void>(
      "POST",
      `/api/timesheet/ticket/${ticketId}/time-alert/disable`,
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

// ---- Back-office Timesheet tabs (Resolved / CWI) — v0.0.56 -----------

export type BackofficeContext = "resolved" | "cwi" | "adsolut";
export type BackofficeTicket = {
  ticketId: string;
  ticketNumber: number;
  subject: string;
  companyName: string | null;
  /// Total registered timesheet minutes on the ticket (all agents).
  totalMinutes: number;
  boChecked: boolean;
  checkedUtc: string | null;
  checkedByEmail: string | null;
  /// When the ticket entered its current status (server-resolved).
  enteredUtc: string;
  // v0.0.84 — Timesheet → Comments (shared per-ticket thread).
  commentThreadId: string | null;
  hasComments: boolean;
  commentsUnread: boolean;
};
export type BackofficeListResponse = {
  year: number;
  month: number;
  /// False when no statuses are configured for this tab in Settings —
  /// the tab then renders a "configure statuses" hint instead of an
  /// empty list that looks like a bug.
  statusesConfigured: boolean;
  items: BackofficeTicket[];
};
export type BackofficeTicketHours = {
  totalMinutes: number;
  byTask: { taskName: string; isAbsence: boolean; minutes: number }[];
};
/// Agent-facing data for the back-office Resolved + CWI tabs. The two
/// list calls take a calendar month (1-12); the server resolves the month
/// boundaries in its own timezone.
export const backofficeTimesheetApi = {
  listResolved: (year: number, month: number) =>
    request<BackofficeListResponse>(
      "GET",
      `/api/timesheet/backoffice/resolved?year=${year}&month=${month}`,
    ),
  listCwi: (year: number, month: number) =>
    request<BackofficeListResponse>(
      "GET",
      `/api/timesheet/backoffice/cwi?year=${year}&month=${month}`,
    ),
  ticketHours: (ticketId: string) =>
    request<BackofficeTicketHours>(
      "GET",
      `/api/timesheet/backoffice/tickets/${ticketId}/hours`,
    ),
  /// Tick/untick the BO marker for a set of entities on one tab. `ids` are
  /// ticket ids for resolved/cwi and sales-receipt ids for adsolut.
  setChecks: (context: BackofficeContext, ids: string[], checked: boolean) =>
    request<{ ok: boolean; count: number; checked: boolean }>(
      "POST",
      "/api/timesheet/backoffice/checks",
      { context, ids, checked },
    ),
};
// ---- Timesheet → Comments (per-ticket review chat) — v0.0.84 ----------

/// One row in the current user's Comments inbox: a thread they are linked into
/// that is still active (its origin row is not yet BO-checked).
export type CommentInboxItem = {
  threadId: string;
  ticketNumber: number;
  ticketId: string | null;
  originContext: BackofficeContext;
  /// The exact origin row id (sales-receipt for adsolut, ticket for
  /// resolved/cwi). Used to BO-check the row straight from the Comments tab —
  /// the same shared check the origin tab toggles.
  originEntityId: string;
  lastMessageUtc: string;
  lastMessagePreview: string | null;
  lastAuthorEmail: string | null;
  unreadCount: number;
};
export type CommentRecipient = { userId: string; email: string };
export type CommentMessage = {
  id: number;
  authorId: string | null;
  authorEmail: string | null;
  body: string;
  createdUtc: string;
  recipients: CommentRecipient[];
};
export type CommentThread = {
  id: string;
  ticketNumber: number;
  ticketId: string | null;
  originContext: BackofficeContext;
  createdBy: string | null;
  createdByEmail: string | null;
  messages: CommentMessage[];
};
export type CommentUserOption = { id: string; email: string };
/// Body for posting a message. `threadId` continues an existing thread (a
/// reply); omit it to find-or-create the shared thread for `ticketNumber`, in
/// which case `context` + `entityId` (the origin row) are required. Linking
/// `recipientIds` (≥1) is mandatory both ways.
export type PostCommentBody = {
  threadId?: string;
  ticketNumber: number;
  ticketId?: string | null;
  context?: BackofficeContext;
  entityId?: string;
  body: string;
  recipientIds: string[];
};
export const timesheetCommentsApi = {
  inbox: () => request<CommentInboxItem[]>("GET", "/api/timesheet/comments/inbox"),
  unreadCount: () =>
    request<{ count: number }>("GET", "/api/timesheet/comments/unread-count"),
  recipients: () =>
    request<CommentUserOption[]>("GET", "/api/timesheet/comments/recipients"),
  thread: (threadId: string) =>
    request<CommentThread>("GET", `/api/timesheet/comments/threads/${threadId}`),
  markRead: (threadId: string) =>
    request<{ ok: boolean }>(
      "POST",
      `/api/timesheet/comments/threads/${threadId}/read`,
      {},
    ),
  post: (body: PostCommentBody) =>
    request<{ ok: boolean; threadId: string; messageId: number }>(
      "POST",
      "/api/timesheet/comments/messages",
      body,
    ),
};
