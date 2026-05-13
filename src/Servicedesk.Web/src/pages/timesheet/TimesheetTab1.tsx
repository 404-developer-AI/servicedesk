import * as React from "react";
import { createPortal } from "react-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import {
  CalendarDays,
  ChevronLeft,
  ChevronRight,
  Pencil,
  Trash2,
  X,
  Check,
} from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Badge } from "@/components/ui/badge";
import { Skeleton } from "@/components/ui/skeleton";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { cn } from "@/lib/utils";
import { ApiError } from "@/lib/ticket-api";
import { ticketApi, type TicketPickerItem } from "@/lib/ticket-api";
import {
  timesheetEntryApi,
  timesheetTaskApi,
  timesheetPreferencesApi,
  todayLocalIso,
  parseHHMM,
  formatHHMM,
  formatDuration,
  currentLocalMinutes,
  autoFormatTimeInput,
  type TimesheetEntry,
  type TimesheetTask,
  type TimesheetFieldError,
} from "@/lib/timesheet-api";

/// Tab 1 — eigen registratie. Renders a day-bound grid of entries with
/// inline add/edit. Each row mutation hits the API per-row (no batch
/// "save day" button) so the audit log captures one event per change and
/// concurrent edits never overwrite each other implicitly.
export function TimesheetTab1() {
  const [date, setDate] = React.useState<string>(() => todayLocalIso());
  const [editingId, setEditingId] = React.useState<string | null>(null);
  const [draftRow, setDraftRow] = React.useState<DraftRow | null>(null);
  const qc = useQueryClient();

  const tasksQuery = useQuery({
    queryKey: ["timesheet", "tasks"],
    queryFn: () => timesheetTaskApi.list(false),
    staleTime: 60_000,
  });
  const tasks = tasksQuery.data ?? [];

  // v0.0.35-E — effective day-start for the calling agent. Falls back to
  // 08:30 (the project-wide default) until the request lands, so the
  // auto-spawn doesn't seed a 00:00 row in the brief loading window.
  const prefsQuery = useQuery({
    queryKey: ["timesheet", "me", "preferences"],
    queryFn: () => timesheetPreferencesApi.me(),
    staleTime: 60_000,
  });
  const dayStartMinutes = prefsQuery.data?.dayStartMinutes ?? DEFAULT_DAY_START_FALLBACK;

  const entriesQuery = useQuery({
    queryKey: ["timesheet", "entries", date],
    queryFn: () => timesheetEntryApi.listByDate(date),
  });
  const entries = entriesQuery.data?.items ?? [];

  const totalMinutes = entries.reduce((sum, e) => sum + e.minutes, 0);
  const lastEnd = entries.length > 0
    ? Math.max(...entries.map((e) => e.endMinutes))
    : null;

  // v0.0.36 — flag rows whose start time doesn't connect to the previous
  // row's end. Only mismatches that fall inside the office-hours window
  // count; outside that window rows may legitimately be sparse. Map keyed
  // by entry id so DisplayRow can render the cell red without re-doing
  // the work per row.
  const mismatchByEntryId = React.useMemo(() => {
    const map = new Map<string, "gap" | "overlap">();
    if (entries.length < 2) return map;
    const officeStart = prefsQuery.data?.officeStartMinutes ?? 510;
    const officeEnd = prefsQuery.data?.officeEndMinutes ?? 1020;
    const sorted = [...entries].sort((a, b) => a.startMinutes - b.startMinutes);
    for (let i = 1; i < sorted.length; i++) {
      const prev = sorted[i - 1];
      const cur = sorted[i];
      if (cur.startMinutes === prev.endMinutes) continue;
      // Mismatch zone — either a gap or an overlap.
      const zoneLo = Math.min(prev.endMinutes, cur.startMinutes);
      const zoneHi = Math.max(prev.endMinutes, cur.startMinutes);
      // Strict intersection: a zone that just touches the office edge
      // (e.g. ends exactly at officeStart) is treated as outside.
      const intersects = zoneLo < officeEnd && zoneHi > officeStart;
      if (!intersects) continue;
      map.set(cur.id, cur.startMinutes < prev.endMinutes ? "overlap" : "gap");
    }
    return map;
  }, [entries, prefsQuery.data?.officeStartMinutes, prefsQuery.data?.officeEndMinutes]);

  const invalidate = () => {
    qc.invalidateQueries({ queryKey: ["timesheet", "entries", date] });
  };

  const goRelative = (days: number) => {
    const d = new Date(date + "T00:00:00");
    d.setDate(d.getDate() + days);
    const y = d.getFullYear();
    const m = (d.getMonth() + 1).toString().padStart(2, "0");
    const day = d.getDate().toString().padStart(2, "0");
    setDate(`${y}-${m}-${day}`);
    setEditingId(null);
    setDraftRow(null);
  };

  // Auto-spawn a ready-to-fill draft row whenever the bottom of the grid
  // is free (no entry is being edited, no draft active). Keeps the user
  // one click away from saving — they never have to click "+ Add" first.
  // Re-runs when the date or last-end change so the pre-fill stays
  // accurate after a save (start = max(end) of prior rows). End-time
  // pre-fills to the current local minute; EditableRow ticks it live
  // until the user manually overrides it.
  React.useEffect(() => {
    if (editingId !== null) return;
    if (draftRow !== null) return;
    if (tasks.length === 0) return;
    // Wait until both queries settle. Without this guard the effect fires
    // on first render with `entries = []` (data still in-flight), pinning
    // Start to the day-start fallback even when the loaded rows have a
    // later last-end. Same trap on date-switch when the queryKey changes.
    if (entriesQuery.isLoading || prefsQuery.isLoading) return;
    const firstActiveTask = tasks.find((t) => !t.archived);
    if (!firstActiveTask) return;
    setDraftRow({
      mode: "new",
      entryDate: date,
      startMinutes: lastEnd ?? dayStartMinutes,
      endMinutes: currentLocalMinutes(),
      taskId: firstActiveTask.id,
      ticket: null,
      description: "",
    });
  }, [
    editingId,
    draftRow,
    tasks,
    date,
    lastEnd,
    dayStartMinutes,
    entriesQuery.isLoading,
    prefsQuery.isLoading,
  ]);

  return (
    <div className="flex min-h-0 flex-1 flex-col gap-4">
      <div className="glass-panel flex flex-wrap items-center justify-between gap-3 p-3">
        <div className="flex items-center gap-2">
          <Button
            size="sm"
            variant="ghost"
            onClick={() => goRelative(-1)}
            aria-label="Previous day"
            className="h-8 w-8 p-0"
          >
            <ChevronLeft className="h-4 w-4" />
          </Button>
          <div className="flex items-center gap-2">
            <CalendarDays className="h-4 w-4 text-muted-foreground" />
            <input
              type="date"
              value={date}
              onChange={(e) => {
                setDate(e.target.value);
                setEditingId(null);
                setDraftRow(null);
              }}
              className="rounded-md border border-white/10 bg-white/[0.03] px-2 py-1 text-sm text-foreground focus:outline-none focus:ring-2 focus:ring-ring"
            />
            <Button
              size="sm"
              variant="ghost"
              onClick={() => {
                setDate(todayLocalIso());
                setEditingId(null);
                setDraftRow(null);
              }}
              className="h-8 px-2 text-xs"
            >
              Today
            </Button>
          </div>
          <Button
            size="sm"
            variant="ghost"
            onClick={() => goRelative(1)}
            aria-label="Next day"
            className="h-8 w-8 p-0"
          >
            <ChevronRight className="h-4 w-4" />
          </Button>
        </div>
        <div className="flex items-center gap-3">
          <Badge className="border border-white/10 bg-white/[0.05] text-xs font-normal text-muted-foreground">
            {entries.length} {entries.length === 1 ? "entry" : "entries"}
          </Badge>
          <span className="text-sm text-muted-foreground">
            Day total:{" "}
            <span className="font-mono font-medium text-foreground">
              {formatDuration(totalMinutes)}
            </span>
          </span>
        </div>
      </div>

      <section className="glass-card flex min-h-0 flex-1 flex-col overflow-hidden">
        <div className="flex-1 overflow-y-auto">
        <table className="w-full table-fixed text-left text-sm">
          <thead className="sticky top-0 z-10 bg-[hsl(var(--background))]/85 text-xs uppercase tracking-wide text-muted-foreground backdrop-blur-md [&_th]:border-b [&_th]:border-white/10">
            <tr>
              <th className="w-24 px-3 py-2 font-medium">Start</th>
              <th className="w-24 px-3 py-2 font-medium">End</th>
              <th className="w-28 2xl:w-[22%] px-3 py-2 font-medium">Ticket</th>
              <th className="w-[14%] px-3 py-2 font-medium">Company</th>
              <th className="w-36 px-3 py-2 font-medium">Task</th>
              <th className="px-3 py-2 font-medium">Description</th>
              <th className="w-20 px-3 py-2 font-medium">Total</th>
              <th className="w-20 px-3 py-2" />
            </tr>
          </thead>
          <tbody>
            {entriesQuery.isLoading && (
              <tr>
                <td colSpan={8} className="px-3 py-3">
                  <Skeleton className="h-6 w-full" />
                </td>
              </tr>
            )}
            {!entriesQuery.isLoading &&
              entries.map((entry) =>
                editingId === entry.id ? (
                  <EditableRow
                    key={entry.id}
                    tasks={tasks}
                    draft={toDraft(entry)}
                    onCancel={() => setEditingId(null)}
                    onSaved={() => {
                      // Edit-existing: just close the editor and refresh.
                      // No draft-spawn here, the bottom-of-grid draft is
                      // already there.
                      setEditingId(null);
                      invalidate();
                    }}
                  />
                ) : (
                  <DisplayRow
                    key={entry.id}
                    entry={entry}
                    mismatch={mismatchByEntryId.get(entry.id) ?? null}
                    onEdit={() => {
                      setEditingId(entry.id);
                      setDraftRow(null);
                    }}
                    onDelete={() => invalidate()}
                  />
                ),
              )}
            {draftRow && (
              <EditableRow
                // Key ensures the EditableRow remounts after each save —
                // its internal `useState(draft)` reseeds from the new
                // draft's pre-fill instead of carrying stale typing into
                // the next entry.
                key={draftKey(draftRow)}
                tasks={tasks}
                draft={draftRow}
                // Bumps whenever the entries list grows — re-triggers
                // scrollIntoView so the draft stays visible after a save
                // pushes a fresh row in above it.
                scrollSignal={entries.length}
                onCancel={() => setDraftRow(null)}
                onSaved={(saved) => {
                  invalidate();
                  // Immediately spawn the next draft with start = the
                  // saved row's end. Doing it here (instead of waiting
                  // for the useEffect after a refetch) avoids the stale
                  // `lastEnd` race that would otherwise pre-fill the
                  // new row's start with the value from before this
                  // save landed.
                  const firstActiveTask = tasks.find((t) => !t.archived);
                  if (!firstActiveTask) {
                    setDraftRow(null);
                    return;
                  }
                  setDraftRow({
                    mode: "new",
                    entryDate: date,
                    startMinutes: saved.endMinutes,
                    endMinutes: currentLocalMinutes(),
                    taskId: firstActiveTask.id,
                    ticket: null,
                    description: "",
                  });
                }}
              />
            )}
            {!entriesQuery.isLoading && tasks.length === 0 && (
              <tr>
                <td colSpan={8} className="px-3 py-8 text-center text-sm text-muted-foreground">
                  No timesheet tasks configured — ask an admin to seed Settings → Timesheet tasks.
                </td>
              </tr>
            )}
          </tbody>
        </table>
        </div>
      </section>
    </div>
  );
}

/// Stable identity for an auto-spawned draft row so React remounts the
/// EditableRow after each save (without it, the internal useState keeps
/// the previously-typed values around).
function draftKey(draft: DraftRow): string {
  return `draft:${draft.entryDate}:${draft.startMinutes ?? "x"}:${draft.taskId}`;
}

// ---- Row rendering ----------------------------------------------------

function DisplayRow({
  entry,
  mismatch,
  onEdit,
  onDelete,
}: {
  entry: TimesheetEntry;
  mismatch: "gap" | "overlap" | null;
  onEdit: () => void;
  onDelete: () => void;
}) {
  const deleteMutation = useMutation({
    mutationFn: () => timesheetEntryApi.remove(entry.id),
    onSuccess: () => {
      toast.success("Entry removed");
      onDelete();
    },
    onError: (err) => toast.error(err instanceof Error ? err.message : "Delete failed"),
  });

  const startTitle =
    mismatch === "gap"
      ? "Gap with the previous row inside office hours"
      : mismatch === "overlap"
        ? "Overlap with the previous row inside office hours"
        : undefined;

  return (
    <tr
      className={cn(
        "border-b border-white/5 last:border-b-0 hover:bg-white/[0.03]",
        mismatch && "bg-red-500/[0.06] hover:bg-red-500/[0.09]",
      )}
    >
      <td
        className={cn(
          "px-3 py-2 font-mono text-xs",
          mismatch ? "text-red-200 font-medium" : "text-foreground/90",
        )}
        title={startTitle}
      >
        {formatHHMM(entry.startMinutes)}
      </td>
      <td className="px-3 py-2 font-mono text-xs text-foreground/90">
        {formatHHMM(entry.endMinutes)}
      </td>
      <td className="px-3 py-2 text-foreground/90">
        {entry.ticketId ? (
          <div className="flex items-baseline gap-2 truncate" title={`#${entry.ticketNumber} ${entry.ticketSubject ?? ""}`}>
            <span className="font-mono text-xs shrink-0">#{entry.ticketNumber}</span>
            <span className="hidden truncate font-sans text-xs text-foreground/70 2xl:inline">
              {entry.ticketSubject}
            </span>
          </div>
        ) : (
          <span className="text-xs text-muted-foreground/50">—</span>
        )}
      </td>
      <td className="px-3 py-2 text-xs text-muted-foreground">
        <span className="block truncate" title={entry.ticketId ? (entry.companyName ?? "— (no company)") : "— (no company)"}>
          {entry.ticketId
            ? (entry.companyName ?? "— (no company)")
            : "— (no company)"}
        </span>
      </td>
      <td className="px-3 py-2">
        <TaskPill name={entry.taskName} isAbsence={entry.taskIsAbsence} />
      </td>
      <td className="px-3 py-2 text-foreground/90 break-words whitespace-pre-wrap">{entry.description}</td>
      <td className="px-3 py-2 font-mono text-xs text-foreground/90">
        {formatDuration(entry.minutes)}
      </td>
      <td className="px-3 py-2">
        <div className="flex items-center justify-end gap-1">
          <Button
            size="sm"
            variant="ghost"
            className="h-7 w-7 p-0"
            onClick={onEdit}
            aria-label="Edit entry"
          >
            <Pencil className="h-3.5 w-3.5" />
          </Button>
          <Button
            size="sm"
            variant="ghost"
            className="h-7 w-7 p-0 text-red-300/80 hover:text-red-300"
            disabled={deleteMutation.isPending}
            onClick={() => {
              if (window.confirm("Remove this timesheet entry?")) deleteMutation.mutate();
            }}
            aria-label="Delete entry"
          >
            <Trash2 className="h-3.5 w-3.5" />
          </Button>
        </div>
      </td>
    </tr>
  );
}

function TaskPill({ name, isAbsence }: { name: string; isAbsence: boolean }) {
  return (
    <span
      className={cn(
        "inline-flex max-w-full items-center truncate rounded-full border px-2 py-0.5 text-[11px] font-medium",
        isAbsence
          ? "border-amber-400/30 bg-amber-400/10 text-amber-200"
          : "border-white/10 bg-white/[0.04] text-foreground/80",
      )}
      title={name}
    >
      {name}
    </span>
  );
}

// ---- Editable row -----------------------------------------------------

type DraftRow = {
  mode: "new" | { type: "edit"; id: string };
  entryDate: string;
  startMinutes: number | null;
  endMinutes: number | null;
  taskId: string;
  ticket: TicketPickerItem | null;
  description: string;
};

/// Last-resort fallback for the start-time pre-fill, used only while
/// the `/api/timesheet/me/preferences` query is in flight. The real
/// value (global default + per-user override) comes from the server in
/// v0.0.35-E.
const DEFAULT_DAY_START_FALLBACK = 8 * 60 + 30; // 08:30

function toDraft(entry: TimesheetEntry): DraftRow {
  return {
    mode: { type: "edit", id: entry.id },
    entryDate: entry.entryDate.slice(0, 10),
    startMinutes: entry.startMinutes,
    endMinutes: entry.endMinutes,
    taskId: entry.taskId,
    ticket: entry.ticketId
      ? ({
          id: entry.ticketId,
          number: entry.ticketNumber ?? 0,
          subject: entry.ticketSubject ?? "",
          statusId: "",
          statusName: "",
          statusColor: "",
          statusStateCategory: "",
          companyId: entry.companyId,
          companyName: entry.companyName,
          requesterContactId: "",
          requesterEmail: null,
          requesterFirstName: null,
          requesterLastName: null,
        } satisfies TicketPickerItem)
      : null,
    description: entry.description,
  };
}

function EditableRow({
  tasks,
  draft,
  scrollSignal,
  onCancel,
  onSaved,
}: {
  tasks: TimesheetTask[];
  draft: DraftRow;
  scrollSignal?: number;
  onCancel: () => void;
  // Receives the saved entry from the API response so the parent can
  // seed the next draft row's start = this row's end without waiting
  // for the entries query to refetch (which is async and creates a
  // race with the useEffect that auto-spawns the draft).
  onSaved: (saved: TimesheetEntry) => void;
}) {
  const [row, setRow] = React.useState<DraftRow>(draft);
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  // Keep the auto-spawned draft visible on long days. Fires on mount
  // (initial spawn) and whenever `scrollSignal` (= entries.length) ticks
  // up — that second pass catches the post-save refetch which inserts a
  // new row above the draft and otherwise pushes it under the viewport.
  // `block: "nearest"` is a no-op when the row is already on-screen, so
  // the effect stays quiet for short days and mid-typing tick re-renders.
  const trRef = React.useRef<HTMLTableRowElement | null>(null);
  React.useEffect(() => {
    if (row.mode !== "new") return;
    trRef.current?.scrollIntoView({ block: "nearest", behavior: "smooth" });
  }, [row.mode, scrollSignal]);

  // Time inputs keep their raw text locally so partial input ("1", "12:",
  // "12:0") survives without parseHHMM nuking it to null mid-typing.
  // parseHHMM runs on every keystroke and, when it succeeds, commits to
  // row.startMinutes / row.endMinutes — that keeps the live "Total"
  // column accurate. On blur we re-format the text from the committed
  // minutes (or leave it raw if not parseable, so the user sees what
  // they typed and the inline validation can highlight it).
  const [startText, setStartText] = React.useState<string>(
    row.startMinutes === null ? "" : formatHHMM(row.startMinutes),
  );
  const [endText, setEndText] = React.useState<string>(
    row.endMinutes === null ? "" : formatHHMM(row.endMinutes),
  );

  // Live-tick the End column so it tracks "now" as the user fills in
  // the other fields — but only while it's untouched. We compare endText
  // against the last value the tick wrote; once they differ, the user
  // has manually overridden the field and we leave it alone. Refs let
  // the interval callback read the latest values without re-creating
  // itself on every keystroke.
  const endTextRef = React.useRef(endText);
  endTextRef.current = endText;
  const lastAutoEndRef = React.useRef<number | null>(
    row.mode === "new" ? row.endMinutes : null,
  );
  React.useEffect(() => {
    if (row.mode !== "new") return;
    const id = window.setInterval(() => {
      const auto = lastAutoEndRef.current;
      if (auto === null) return;
      // User changed the field manually → stop the auto-update so we
      // don't trample their input on the next minute roll-over.
      if (endTextRef.current !== formatHHMM(auto)) return;
      const now = currentLocalMinutes();
      if (now === auto) return;
      lastAutoEndRef.current = now;
      setEndText(formatHHMM(now));
      setRow((r) => ({ ...r, endMinutes: now }));
    }, 15_000);
    return () => window.clearInterval(id);
  }, [row.mode]);

  const task = tasks.find((t) => t.id === row.taskId);
  const requiresTicket = task?.requiresTicket ?? false;

  const totalMinutes =
    row.startMinutes !== null && row.endMinutes !== null
      ? row.endMinutes - row.startMinutes
      : null;

  const onTimeChange = (
    text: string,
    setText: (s: string) => void,
    commit: (v: number | null) => void,
  ) => {
    // Auto-insert ":" so the user can type "0850" and see "08:50". The
    // helper is a no-op once a non-digit is in the string, so user-typed
    // colons + backspaces stay predictable.
    const formatted = autoFormatTimeInput(text);
    setText(formatted);
    if (formatted.trim() === "") {
      commit(null);
      return;
    }
    const parsed = parseHHMM(formatted);
    if (parsed !== null) commit(parsed);
    // else: don't touch committed value — user is mid-typing.
  };

  const onTimeBlur = (
    text: string,
    setText: (s: string) => void,
    committed: number | null,
  ) => {
    // Re-format to the canonical HH:MM once focus leaves, so a stray
    // "8:5" becomes "08:05" when parseable.
    const trimmed = text.trim();
    if (trimmed === "") return;
    const parsed = parseHHMM(trimmed);
    if (parsed !== null) {
      setText(formatHHMM(parsed));
    } else if (committed !== null) {
      // Unparseable text but we have a previously-committed value — snap
      // back to it so the field is never visually out of sync with the
      // value used for save.
      setText(formatHHMM(committed));
    }
    // Else: leave the raw text so validation can flag it on save.
  };

  const saveMutation = useMutation({
    mutationFn: async () => {
      const fieldErrors = validateLocal(row, requiresTicket);
      if (Object.keys(fieldErrors).length > 0) {
        setErrors(fieldErrors);
        throw new Error("Some fields are invalid.");
      }
      setErrors({});

      const body = {
        entryDate: row.entryDate,
        startMinutes: row.startMinutes!,
        endMinutes: row.endMinutes!,
        taskId: row.taskId,
        ticketId: requiresTicket ? (row.ticket?.id ?? null) : null,
        description: row.description.trim(),
      };
      if (row.mode === "new") {
        return await timesheetEntryApi.create(body);
      }
      return await timesheetEntryApi.update(row.mode.id, body);
    },
    onSuccess: (saved) => {
      toast.success(row.mode === "new" ? "Entry added" : "Entry updated");
      onSaved(saved);
    },
    onError: (err) => {
      if (err instanceof ApiError && err.status === 422) {
        const parsed = parseApiErrors(err.message);
        if (parsed) {
          setErrors(parsed);
          return;
        }
      }
      if (err instanceof Error && err.message !== "Some fields are invalid.") {
        toast.error(err.message);
      }
    },
  });

  // Speed matters for end-of-day batch entry: Enter saves the current
  // row, Escape cancels. We attach the handler to the <tr> so any inner
  // input/select participates. The Select popover swallows its own keys
  // while open (Radix), so Enter inside an open menu still picks an item
  // rather than firing this — that's the right priority for the user.
  const onRowKeyDown = (e: React.KeyboardEvent<HTMLTableRowElement>) => {
    if (e.defaultPrevented) return;
    if (e.key === "Enter" && !e.shiftKey && !e.ctrlKey && !e.metaKey) {
      e.preventDefault();
      if (!saveMutation.isPending) saveMutation.mutate();
      return;
    }
    if (e.key === "Escape") {
      e.preventDefault();
      if (!saveMutation.isPending) onCancel();
    }
  };

  return (
    <tr ref={trRef} className="border-b border-white/10 bg-primary/[0.04]" onKeyDown={onRowKeyDown}>
      <td className="px-3 py-2 align-top">
        <Input
          value={startText}
          onChange={(e) =>
            onTimeChange(e.target.value, setStartText, (v) =>
              setRow((r) => ({ ...r, startMinutes: v })),
            )
          }
          onBlur={() => onTimeBlur(startText, setStartText, row.startMinutes)}
          placeholder="HH:MM"
          className={cn("h-8 font-mono text-xs", errors.startMinutes && "border-red-400/60")}
          aria-invalid={!!errors.startMinutes}
        />
        {errors.startMinutes && (
          <p className="mt-1 text-[10px] text-red-300">{errors.startMinutes}</p>
        )}
      </td>
      <td className="px-3 py-2 align-top">
        <Input
          value={endText}
          onChange={(e) =>
            onTimeChange(e.target.value, setEndText, (v) =>
              setRow((r) => ({ ...r, endMinutes: v })),
            )
          }
          onBlur={() => onTimeBlur(endText, setEndText, row.endMinutes)}
          placeholder="HH:MM"
          className={cn("h-8 font-mono text-xs", errors.endMinutes && "border-red-400/60")}
          aria-invalid={!!errors.endMinutes}
        />
        {errors.endMinutes && (
          <p className="mt-1 text-[10px] text-red-300">{errors.endMinutes}</p>
        )}
      </td>
      <td className="px-3 py-2 align-top">
        <TicketAutocomplete
          value={row.ticket}
          disabled={!requiresTicket}
          onChange={(t) => setRow({ ...row, ticket: t })}
          error={errors.ticketId}
        />
      </td>
      <td className="px-3 py-2 align-top text-xs text-muted-foreground">
        <span
          className="block truncate"
          title={requiresTicket ? (row.ticket?.companyName ?? "— (no company)") : "— (no company)"}
        >
          {requiresTicket
            ? (row.ticket?.companyName ?? "— (no company)")
            : "— (no company)"}
        </span>
      </td>
      <td className="px-3 py-2 align-top">
        <Select
          value={row.taskId || undefined}
          onValueChange={(nextTaskId) => {
            const nextTask = tasks.find((t) => t.id === nextTaskId);
            setRow({
              ...row,
              taskId: nextTaskId,
              // If the new task forbids tickets, drop any existing pick.
              ticket: nextTask?.requiresTicket ? row.ticket : null,
            });
          }}
        >
          <SelectTrigger
            className={cn(
              "h-8 text-xs",
              errors.taskId && "border-red-400/60",
            )}
          >
            <SelectValue placeholder="Select task…" />
          </SelectTrigger>
          <SelectContent>
            {tasks.map((t) => (
              <SelectItem key={t.id} value={t.id} className="text-xs">
                {t.name}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
        {errors.taskId && (
          <p className="mt-1 text-[10px] text-red-300">{errors.taskId}</p>
        )}
      </td>
      <td className="px-3 py-2 align-top">
        <Input
          value={row.description}
          onChange={(e) => setRow({ ...row, description: e.target.value })}
          placeholder="What did you do?"
          className={cn("h-8 text-xs", errors.description && "border-red-400/60")}
          aria-invalid={!!errors.description}
        />
        {errors.description && (
          <p className="mt-1 text-[10px] text-red-300">{errors.description}</p>
        )}
      </td>
      <td className="px-3 py-2 align-top font-mono text-xs text-foreground/90">
        {totalMinutes !== null && totalMinutes > 0 ? formatDuration(totalMinutes) : "—"}
      </td>
      <td className="px-3 py-2 align-top">
        <div className="flex items-center justify-end gap-1">
          <Button
            size="sm"
            variant="ghost"
            className="h-7 w-7 p-0 text-emerald-300/80 hover:text-emerald-200"
            onClick={() => saveMutation.mutate()}
            disabled={saveMutation.isPending}
            aria-label="Save row"
          >
            <Check className="h-3.5 w-3.5" />
          </Button>
          <Button
            size="sm"
            variant="ghost"
            className="h-7 w-7 p-0"
            onClick={onCancel}
            disabled={saveMutation.isPending}
            aria-label="Cancel"
          >
            <X className="h-3.5 w-3.5" />
          </Button>
        </div>
      </td>
    </tr>
  );
}

// ---- Ticket autocomplete (reuses /api/tickets/picker) -----------------

function TicketAutocomplete({
  value,
  disabled,
  onChange,
  error,
}: {
  value: TicketPickerItem | null;
  disabled: boolean;
  onChange: (t: TicketPickerItem | null) => void;
  error?: string;
}) {
  const [query, setQuery] = React.useState("");
  const [open, setOpen] = React.useState(false);
  const [results, setResults] = React.useState<TicketPickerItem[]>([]);
  const [searching, setSearching] = React.useState(false);
  // The dropdown lives in a portal on document.body (see comment below)
  // so its position is computed against the trigger's bounding rect and
  // refreshed on scroll/resize while open.
  const inputRef = React.useRef<HTMLInputElement | null>(null);
  const [anchor, setAnchor] = React.useState<{
    /// Either `top` (anchor top edge, dropdown grows downward) OR
    /// `bottom` (anchor bottom edge, dropdown grows upward). Exactly
    /// one is set. Using `bottom` for flip-above keeps the dropdown
    /// glued to the input as the content height shrinks while the
    /// user types — using `top` would freeze the top edge and leave
    /// a floating gap above the input.
    top?: number;
    bottom?: number;
    left: number;
    width: number;
    maxHeight: number;
  } | null>(null);

  React.useEffect(() => {
    if (disabled || !open) return;
    let cancelled = false;
    const handle = window.setTimeout(async () => {
      setSearching(true);
      try {
        const res = await ticketApi.picker(query.trim() || undefined, undefined, 15);
        if (!cancelled) setResults(res.items);
      } catch {
        if (!cancelled) setResults([]);
      } finally {
        if (!cancelled) setSearching(false);
      }
    }, 180);
    return () => {
      cancelled = true;
      window.clearTimeout(handle);
    };
  }, [query, open, disabled]);

  // Track the trigger's screen-rect while the dropdown is open so the
  // portal stays glued to it on page scroll / window resize. We render
  // the dropdown via createPortal because the parent `<section
  // class="glass-card overflow-hidden">` clips any descendant that
  // extends past its bounds — a regular absolute-positioned popup ends
  // up cropped at the bottom edge of the card. Portaling to body
  // sidesteps both the overflow-hidden and any stacking-context oddities
  // from the surrounding table / sticky sidebar.
  React.useLayoutEffect(() => {
    if (!open || value) {
      setAnchor(null);
      return;
    }
    const place = () => {
      const el = inputRef.current;
      if (!el) return;
      const r = el.getBoundingClientRect();
      // Flip above when the row sits low in the viewport and the
      // 288px dropdown would otherwise spill off-screen — happens on
      // long days where the auto-spawned draft is the last visible
      // row. Pick the side with more room when below is cramped.
      const vh = window.innerHeight;
      const margin = 8;
      const gap = 4;
      const preferredMax = 288; // matches the old max-h-72
      const minBelow = 160;
      const availBelow = vh - r.bottom - gap - margin;
      const availAbove = r.top - gap - margin;
      const flipAbove = availBelow < minBelow && availAbove > availBelow;
      const maxHeight = Math.max(
        120,
        Math.min(preferredMax, flipAbove ? availAbove : availBelow),
      );
      // When flipping above, anchor via `bottom` so the dropdown's
      // bottom edge stays glued to the input top as the result list
      // shrinks during typing — using `top + maxHeight` would freeze
      // the top, leaving a floating gap when actual content < maxHeight.
      if (flipAbove) {
        setAnchor({
          bottom: vh - r.top + gap,
          left: r.left,
          width: r.width,
          maxHeight,
        });
      } else {
        setAnchor({
          top: r.bottom + gap,
          left: r.left,
          width: r.width,
          maxHeight,
        });
      }
    };
    place();
    window.addEventListener("scroll", place, true);
    window.addEventListener("resize", place);
    return () => {
      window.removeEventListener("scroll", place, true);
      window.removeEventListener("resize", place);
    };
  }, [open, value]);

  if (disabled) {
    return (
      <div className="h-8 rounded-md border border-dashed border-white/10 bg-white/[0.02] px-2 py-1 text-xs text-muted-foreground/60">
        — (task has no ticket)
      </div>
    );
  }

  return (
    <div className="relative">
      {value ? (
        <div
          className={cn(
            "flex h-8 items-center justify-between gap-2 rounded-md border bg-white/[0.03] px-2 text-xs",
            error ? "border-red-400/60" : "border-white/10",
          )}
          title={`#${value.number} ${value.subject ?? ""}`}
        >
          <span className="truncate font-mono">
            #{value.number}
            <span className="ml-2 hidden font-sans text-foreground/70 2xl:inline">{value.subject}</span>
          </span>
          <button
            type="button"
            className="text-muted-foreground hover:text-foreground"
            onClick={() => {
              onChange(null);
              setQuery("");
            }}
            aria-label="Clear ticket"
          >
            <X className="h-3.5 w-3.5" />
          </button>
        </div>
      ) : (
        <Input
          ref={inputRef}
          value={query}
          onChange={(e) => {
            setQuery(e.target.value);
            setOpen(true);
          }}
          onFocus={() => setOpen(true)}
          onBlur={() => window.setTimeout(() => setOpen(false), 150)}
          placeholder="Search ticket # or subject…"
          className={cn("h-8 text-xs", error && "border-red-400/60")}
          aria-invalid={!!error}
        />
      )}
      {!value && open && anchor &&
        createPortal(
          <div
            className="fixed z-50 overflow-y-auto rounded-md border border-white/10 bg-[hsl(var(--background))] p-1 shadow-2xl backdrop-blur-xl"
            style={{
              ...(anchor.top !== undefined ? { top: anchor.top } : {}),
              ...(anchor.bottom !== undefined ? { bottom: anchor.bottom } : {}),
              left: anchor.left,
              // Picker is denser than the trigger column; clamp to a
              // useful min so a tiny column doesn't squash the dropdown.
              width: Math.max(anchor.width, 384),
              maxHeight: anchor.maxHeight,
            }}
            // Keep the input focused — clicking inside should not blur
            // the trigger and close the dropdown before the click lands.
            onMouseDown={(e) => e.preventDefault()}
          >
            {searching && (
              <div className="px-2 py-1.5 text-xs text-muted-foreground">Searching…</div>
            )}
            {!searching && results.length === 0 && (
              <div className="px-2 py-1.5 text-xs text-muted-foreground">No matches</div>
            )}
            {results.map((r) => (
              <button
                key={r.id}
                type="button"
                onMouseDown={(e) => {
                  e.preventDefault();
                  onChange(r);
                  setQuery("");
                  setOpen(false);
                }}
                className="flex w-full flex-col items-start gap-0 rounded px-2 py-1.5 text-left text-xs hover:bg-white/[0.06]"
              >
                <span className="font-mono">
                  #{r.number}
                  <span className="ml-2 font-sans text-foreground">{r.subject}</span>
                </span>
                {r.companyName && (
                  <span className="text-[10px] text-muted-foreground">{r.companyName}</span>
                )}
              </button>
            ))}
          </div>,
          document.body,
        )}
      {error && <p className="mt-1 text-[10px] text-red-300">{error}</p>}
    </div>
  );
}

// ---- Validation helpers ----------------------------------------------

function validateLocal(row: DraftRow, requiresTicket: boolean): Record<string, string> {
  const errs: Record<string, string> = {};
  if (row.startMinutes === null) errs.startMinutes = "Start time is required (HH:MM).";
  if (row.endMinutes === null) errs.endMinutes = "End time is required (HH:MM).";
  if (row.startMinutes !== null && row.endMinutes !== null && row.endMinutes <= row.startMinutes) {
    errs.endMinutes = "End must be after start.";
  }
  if (!row.taskId) errs.taskId = "Pick a task.";
  if (requiresTicket && !row.ticket) errs.ticketId = "This task needs a ticket.";
  if (row.description.trim().length === 0) errs.description = "Describe what you did.";
  return errs;
}

function parseApiErrors(raw: string): Record<string, string> | null {
  try {
    const parsed = JSON.parse(raw) as { errors?: TimesheetFieldError[] };
    if (!parsed.errors || !Array.isArray(parsed.errors)) return null;
    const map: Record<string, string> = {};
    for (const e of parsed.errors) map[e.field] = e.message;
    return map;
  } catch {
    return null;
  }
}
