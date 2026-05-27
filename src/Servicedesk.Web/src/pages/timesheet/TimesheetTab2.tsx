import * as React from "react";
import { createPortal } from "react-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import {
  CalendarRange,
  Pencil,
  Search,
  Trash2,
  X,
  Check,
  Filter,
  History,
} from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
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
  timesheetManagerApi,
  timesheetTaskApi,
  parseHHMM,
  formatHHMM,
  formatDuration,
  autoFormatTimeInput,
  type TimesheetEntry,
  type TimesheetTask,
  type TimesheetUser,
  type ManagerEntryFilter,
  type TimesheetFieldError,
} from "@/lib/timesheet-api";

/// Position record for a portal-anchored dropdown. The dropdown is
/// `position: fixed` and anchored by either `top` (drop down) or
/// `bottom` (flip up) — exactly one is set. Using `bottom` for flip-up
/// keeps the popup glued to the input as its content shrinks during
/// typing; anchoring by `top + maxHeight` would freeze the top edge
/// and leave a floating gap above the input. `maxHeight` is the
/// available space so the list is never clipped by a viewport edge.
/// See `computeDropdownAnchor` for the placement rules.
type DropdownAnchor = {
  top?: number;
  bottom?: number;
  left: number;
  width: number;
  maxHeight: number;
};

/// Decide whether to drop the picker below or flip it above the trigger,
/// and cap its height to whatever space is available. Without this, a row
/// near the bottom of the viewport pushes the 288-pixel list off-screen
/// and the user can't scroll to see the matches.
function computeDropdownAnchor(rect: DOMRect): DropdownAnchor {
  const vh = window.innerHeight;
  const margin = 8;
  const gap = 4;
  const preferredMax = 288; // matches the old max-h-72
  const minBelow = 160;
  const availBelow = vh - rect.bottom - gap - margin;
  const availAbove = rect.top - gap - margin;
  const flipAbove = availBelow < minBelow && availAbove > availBelow;
  const maxHeight = Math.max(
    120,
    Math.min(preferredMax, flipAbove ? availAbove : availBelow),
  );
  if (flipAbove) {
    return {
      bottom: vh - rect.top + gap,
      left: rect.left,
      width: rect.width,
      maxHeight,
    };
  }
  return {
    top: rect.bottom + gap,
    left: rect.left,
    width: rect.width,
    maxHeight,
  };
}

/// Tab 2 — manager overview across all users. Renders a filterable grid
/// with inline edit + delete. Server-side every mutation here lands in
/// the audit log as `timesheet.entry.edited_by_manager` /
/// `…deleted_by_manager` with before/after payloads.
export function TimesheetTab2() {
  const qc = useQueryClient();
  const [filter, setFilter] = React.useState<ManagerEntryFilter>(() => defaultFilter());
  const [draft, setDraft] = React.useState<ManagerEntryFilter>(filter);
  const [editingId, setEditingId] = React.useState<string | null>(null);

  const tasksQuery = useQuery({
    queryKey: ["timesheet", "tasks"],
    queryFn: () => timesheetTaskApi.list(false),
    staleTime: 60_000,
  });
  const tasks = tasksQuery.data ?? [];

  const usersQuery = useQuery({
    queryKey: ["timesheet", "manager", "users"],
    queryFn: () => timesheetManagerApi.listUsers(),
    staleTime: 60_000,
  });
  const users = usersQuery.data ?? [];

  const entriesQuery = useQuery({
    queryKey: ["timesheet", "manager", "entries", filter],
    queryFn: () => timesheetManagerApi.listEntries(filter),
  });
  const entries = entriesQuery.data?.items ?? [];

  const invalidate = () => {
    qc.invalidateQueries({ queryKey: ["timesheet", "manager", "entries"] });
  };

  const totalMinutes = entries.reduce((sum, e) => sum + e.minutes, 0);

  const applyFilter = () => {
    setFilter({ ...draft });
    setEditingId(null);
  };
  const resetFilter = () => {
    const d = defaultFilter();
    setDraft(d);
    setFilter(d);
    setEditingId(null);
  };

  return (
    <div className="flex flex-col gap-4">
      <FilterBar
        draft={draft}
        setDraft={setDraft}
        users={users}
        tasks={tasks}
        onApply={applyFilter}
        onReset={resetFilter}
        loading={entriesQuery.isFetching}
        totalCount={entries.length}
        totalMinutes={totalMinutes}
      />

      <section className="glass-card overflow-hidden">
        <div className="overflow-x-auto">
        <table className="w-full min-w-[1410px] text-left text-sm">
          <thead className="text-xs uppercase tracking-wide text-muted-foreground [&_th]:border-b [&_th]:border-glass">
            <tr>
              <th className="w-28 px-3 py-2 font-medium">Date</th>
              <th className="w-44 px-3 py-2 font-medium">User</th>
              <th className="w-20 px-3 py-2 font-medium">Start</th>
              <th className="w-20 px-3 py-2 font-medium">End</th>
              <th className="w-32 px-3 py-2 font-medium">Logged</th>
              <th className="w-56 px-3 py-2 font-medium">Ticket</th>
              <th className="w-36 px-3 py-2 font-medium">Company</th>
              <th className="w-36 px-3 py-2 font-medium">Task</th>
              <th className="px-3 py-2 font-medium">Description</th>
              <th className="w-24 px-3 py-2 font-medium">Billed</th>
              <th className="w-20 px-3 py-2 font-medium">Total</th>
              <th className="w-20 px-3 py-2" />
            </tr>
          </thead>
          <tbody>
            {entriesQuery.isLoading && (
              <tr>
                <td colSpan={12} className="px-3 py-3">
                  <Skeleton className="h-6 w-full" />
                </td>
              </tr>
            )}
            {!entriesQuery.isLoading && entries.length === 0 && (
              <tr>
                <td
                  colSpan={12}
                  className="px-3 py-8 text-center text-sm text-muted-foreground"
                >
                  No entries match these filters.
                </td>
              </tr>
            )}
            {!entriesQuery.isLoading &&
              entries.map((entry) =>
                editingId === entry.id ? (
                  <ManagerEditableRow
                    key={entry.id}
                    entry={entry}
                    tasks={tasks}
                    onCancel={() => setEditingId(null)}
                    onSaved={() => {
                      setEditingId(null);
                      invalidate();
                    }}
                  />
                ) : (
                  <ManagerDisplayRow
                    key={entry.id}
                    entry={entry}
                    onEdit={() => setEditingId(entry.id)}
                    onDeleted={() => invalidate()}
                  />
                ),
              )}
          </tbody>
        </table>
        </div>
      </section>
    </div>
  );
}

// Radix Select treats the empty string as "no value", so the "All
// users" / "All tasks" rows need a sentinel string. These never appear
// in API payloads — the onValueChange handler strips them back to
// `undefined` before reading filter state.
const ALL_USERS = "__all_users__";
const ALL_TASKS = "__all_tasks__";

function defaultFilter(): ManagerEntryFilter {
  // Default to "last 7 days" — same window the server falls back to when
  // the client sends no dates. Stating it explicitly here keeps the UI
  // honest about the current window.
  const today = new Date();
  const from = new Date(today);
  from.setDate(from.getDate() - 6);
  return { from: localIso(from), to: localIso(today) };
}

function localIso(d: Date): string {
  const y = d.getFullYear();
  const m = (d.getMonth() + 1).toString().padStart(2, "0");
  const day = d.getDate().toString().padStart(2, "0");
  return `${y}-${m}-${day}`;
}

// ---- Filter bar -------------------------------------------------------

function FilterBar({
  draft,
  setDraft,
  users,
  tasks,
  onApply,
  onReset,
  loading,
  totalCount,
  totalMinutes,
}: {
  draft: ManagerEntryFilter;
  setDraft: (next: ManagerEntryFilter) => void;
  users: TimesheetUser[];
  tasks: TimesheetTask[];
  onApply: () => void;
  onReset: () => void;
  loading: boolean;
  totalCount: number;
  totalMinutes: number;
}) {
  return (
    <div className="glass-panel flex flex-col gap-3 p-3">
      <div className="flex flex-wrap items-end gap-3">
        <FieldGroup label="From">
          <input
            type="date"
            value={draft.from ?? ""}
            onChange={(e) => setDraft({ ...draft, from: e.target.value || undefined })}
            className="h-8 rounded-md border border-glass bg-glass px-2 text-sm text-foreground focus:outline-none focus:ring-2 focus:ring-ring"
          />
        </FieldGroup>
        <FieldGroup label="To">
          <input
            type="date"
            value={draft.to ?? ""}
            onChange={(e) => setDraft({ ...draft, to: e.target.value || undefined })}
            className="h-8 rounded-md border border-glass bg-glass px-2 text-sm text-foreground focus:outline-none focus:ring-2 focus:ring-ring"
          />
        </FieldGroup>
        <FieldGroup label="User">
          <Select
            value={draft.userId ?? ALL_USERS}
            onValueChange={(v) =>
              setDraft({ ...draft, userId: v === ALL_USERS ? undefined : v })
            }
          >
            <SelectTrigger className="h-8 min-w-[12rem] text-sm">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value={ALL_USERS}>All users</SelectItem>
              {users.map((u) => (
                <SelectItem key={u.id} value={u.id}>
                  {u.email}
                  {u.manager ? " · mgr" : ""}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </FieldGroup>
        <FieldGroup label="Task">
          <Select
            value={draft.taskId ?? ALL_TASKS}
            onValueChange={(v) =>
              setDraft({ ...draft, taskId: v === ALL_TASKS ? undefined : v })
            }
          >
            <SelectTrigger className="h-8 min-w-[10rem] text-sm">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value={ALL_TASKS}>All tasks</SelectItem>
              {tasks.map((t) => (
                <SelectItem key={t.id} value={t.id}>
                  {t.name}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </FieldGroup>
        <FieldGroup label="Ticket">
          <TicketFilterPicker
            value={draft.ticketId}
            onChange={(id) => setDraft({ ...draft, ticketId: id })}
          />
        </FieldGroup>
        <FieldGroup label="Search" className="min-w-[14rem] flex-1">
          <div className="relative">
            <Search className="pointer-events-none absolute left-2 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-muted-foreground" />
            <Input
              value={draft.search ?? ""}
              onChange={(e) => setDraft({ ...draft, search: e.target.value })}
              onKeyDown={(e) => {
                if (e.key === "Enter") onApply();
              }}
              placeholder="Company / contact / description…"
              className="h-8 pl-7 text-sm"
            />
          </div>
        </FieldGroup>
        <div className="flex items-center gap-2">
          <Button size="sm" onClick={onApply} disabled={loading} className="h-8">
            <Filter className="mr-1.5 h-3.5 w-3.5" />
            Apply
          </Button>
          <Button
            size="sm"
            variant="ghost"
            onClick={onReset}
            disabled={loading}
            className="h-8"
          >
            Reset
          </Button>
        </div>
      </div>
      <div className="flex items-center justify-between border-t border-glass pt-2 text-xs text-muted-foreground">
        <div className="flex items-center gap-2">
          <CalendarRange className="h-3.5 w-3.5" />
          <span>Showing {totalCount} {totalCount === 1 ? "entry" : "entries"}</span>
        </div>
        <span>
          Total:{" "}
          <span className="font-mono font-medium text-foreground">
            {formatDuration(totalMinutes)}
          </span>
        </span>
      </div>
    </div>
  );
}

function FieldGroup({
  label,
  children,
  className,
}: {
  label: string;
  children: React.ReactNode;
  className?: string;
}) {
  return (
    <div className={cn("flex flex-col gap-1", className)}>
      <label className="text-[10px] uppercase tracking-wider text-muted-foreground">
        {label}
      </label>
      {children}
    </div>
  );
}

// ---- Display row ------------------------------------------------------

function ManagerDisplayRow({
  entry,
  onEdit,
  onDeleted,
}: {
  entry: TimesheetEntry;
  onEdit: () => void;
  onDeleted: () => void;
}) {
  const deleteMutation = useMutation({
    mutationFn: () => timesheetManagerApi.deleteEntry(entry.id),
    onSuccess: () => {
      toast.success("Entry removed");
      onDeleted();
    },
    onError: (err) => toast.error(err instanceof Error ? err.message : "Delete failed"),
  });

  return (
    <tr className="border-b border-glass last:border-b-0 hover:bg-glass-hover">
      <td className="px-3 py-2 font-mono text-xs text-foreground/90">
        {entry.entryDate.slice(0, 10)}
      </td>
      <td className="px-3 py-2 text-xs text-foreground/80">
        {entry.userEmail || "—"}
      </td>
      <td className="px-3 py-2 font-mono text-xs text-foreground/90">
        {formatHHMM(entry.startMinutes)}
      </td>
      <td className="px-3 py-2 font-mono text-xs text-foreground/90">
        {formatHHMM(entry.endMinutes)}
      </td>
      <td className="px-3 py-2">
        <LogTimeCell
          createdUtc={entry.createdUtc}
          entryDate={entry.entryDate}
          endMinutes={entry.endMinutes}
        />
      </td>
      <td className="px-3 py-2 text-foreground/90">
        {entry.ticketId ? (
          <span className="font-mono text-xs">
            #{entry.ticketNumber}
            <span className="ml-2 font-sans text-foreground/70">{entry.ticketSubject}</span>
          </span>
        ) : (
          <span className="text-xs text-muted-foreground/50">—</span>
        )}
      </td>
      <td className="px-3 py-2 text-xs text-muted-foreground">
        {entry.ticketId ? (entry.companyName ?? "— (no company)") : "— (no company)"}
      </td>
      <td className="px-3 py-2">
        <TaskPill name={entry.taskName} isAbsence={entry.taskIsAbsence} />
      </td>
      <td className="px-3 py-2 text-foreground/90 whitespace-pre-wrap">
        {entry.description}
      </td>
      <td className="px-3 py-2">
        <BilledPill invoiced={entry.invoiced} />
      </td>
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
              if (
                window.confirm(
                  `Remove this entry of ${entry.userEmail || "this user"}? This is logged in the audit trail.`,
                )
              ) {
                deleteMutation.mutate();
              }
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
        "inline-flex items-center rounded-full border px-2 py-0.5 text-[11px] font-medium",
        isAbsence
          ? "border-amber-400/30 bg-amber-400/10 text-amber-200"
          : "border-glass bg-glass text-foreground/80",
      )}
    >
      {name}
    </span>
  );
}

// ---- Editable row -----------------------------------------------------

type EditDraft = {
  entryDate: string;
  startMinutes: number | null;
  endMinutes: number | null;
  taskId: string;
  ticket: TicketPickerItem | null;
  description: string;
};

function ManagerEditableRow({
  entry,
  tasks,
  onCancel,
  onSaved,
}: {
  entry: TimesheetEntry;
  tasks: TimesheetTask[];
  onCancel: () => void;
  onSaved: () => void;
}) {
  const [row, setRow] = React.useState<EditDraft>(() => ({
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
  }));
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const [startText, setStartText] = React.useState(formatHHMM(entry.startMinutes));
  const [endText, setEndText] = React.useState(formatHHMM(entry.endMinutes));

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
    // Auto-insert ":" so "0850" becomes "08:50" on the third keystroke.
    const formatted = autoFormatTimeInput(text);
    setText(formatted);
    if (formatted.trim() === "") {
      commit(null);
      return;
    }
    const parsed = parseHHMM(formatted);
    if (parsed !== null) commit(parsed);
  };

  const onTimeBlur = (
    text: string,
    setText: (s: string) => void,
    committed: number | null,
  ) => {
    const trimmed = text.trim();
    if (trimmed === "") return;
    const parsed = parseHHMM(trimmed);
    if (parsed !== null) setText(formatHHMM(parsed));
    else if (committed !== null) setText(formatHHMM(committed));
  };

  const saveMutation = useMutation({
    mutationFn: async () => {
      const errs = validateLocal(row, requiresTicket);
      if (Object.keys(errs).length > 0) {
        setErrors(errs);
        throw new Error("Some fields are invalid.");
      }
      setErrors({});
      return await timesheetManagerApi.updateEntry(entry.id, {
        entryDate: row.entryDate,
        startMinutes: row.startMinutes!,
        endMinutes: row.endMinutes!,
        taskId: row.taskId,
        ticketId: requiresTicket ? (row.ticket?.id ?? null) : null,
        description: row.description.trim(),
      });
    },
    onSuccess: () => {
      toast.success("Entry updated (logged for audit)");
      onSaved();
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
    <tr className="border-b border-glass bg-primary/[0.04]" onKeyDown={onRowKeyDown}>
      <td className="px-3 py-2 align-top">
        <Input
          type="date"
          value={row.entryDate}
          onChange={(e) => setRow({ ...row, entryDate: e.target.value })}
          className="h-8 font-mono text-xs"
        />
      </td>
      <td className="px-3 py-2 align-top text-xs text-foreground/70">
        {entry.userEmail || "—"}
      </td>
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
        />
        {errors.endMinutes && (
          <p className="mt-1 text-[10px] text-red-300">{errors.endMinutes}</p>
        )}
      </td>
      <td className="px-3 py-2 align-top">
        <LogTimeCell
          createdUtc={entry.createdUtc}
          entryDate={entry.entryDate}
          endMinutes={entry.endMinutes}
        />
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
        {requiresTicket
          ? (row.ticket?.companyName ?? "— (no company)")
          : "— (no company)"}
      </td>
      <td className="px-3 py-2 align-top">
        <Select
          value={row.taskId || undefined}
          onValueChange={(nextTaskId) => {
            const nextTask = tasks.find((t) => t.id === nextTaskId);
            setRow({
              ...row,
              taskId: nextTaskId,
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
          placeholder="What was done?"
          className={cn("h-8 text-xs", errors.description && "border-red-400/60")}
        />
        {errors.description && (
          <p className="mt-1 text-[10px] text-red-300">{errors.description}</p>
        )}
      </td>
      <td className="px-3 py-2 align-top">
        {/* Read-only — the billed toggle UI lands in a later iteration. */}
        <BilledPill invoiced={entry.invoiced} />
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
            aria-label="Save changes"
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

// ---- Billed pill (shared between display + editable rows) -------------

function BilledPill({ invoiced }: { invoiced: boolean }) {
  if (invoiced) {
    return (
      <span
        className="inline-flex items-center gap-1 rounded-md border border-emerald-400/30 bg-emerald-400/10 px-1.5 py-0.5 text-[10px] font-medium text-emerald-200"
        title="Billed"
      >
        <Check className="h-3 w-3" />
        Billed
      </span>
    );
  }
  return (
    <span
      className="inline-flex items-center rounded-md border border-glass-strong bg-glass px-1.5 py-0.5 text-[10px] font-medium text-muted-foreground/70"
      title="Not billed yet"
    >
      —
    </span>
  );
}

// ---- Log-time cell (creation-vs-end mismatch indicator) ---------------

/// Threshold for "logged late" — when the entry's `created_utc` differs
/// from the local "entry_date + end_minutes" moment by more than this
/// many minutes, the cell turns red so a manager can immediately spot
/// back-dated registrations. 5 minutes is a generous default that
/// swallows ordinary network/save delay; promote to a Timesheet setting
/// if installs want a different policy.
const LOG_TIME_MISMATCH_THRESHOLD_MINUTES = 5;

function LogTimeCell({
  createdUtc,
  entryDate,
  endMinutes,
}: {
  createdUtc: string;
  entryDate: string;
  endMinutes: number;
}) {
  const created = new Date(createdUtc);
  // Build the entry's end as a local-time Date — `entry_date + end_minutes`
  // is a local-day concept; the server stores `end_minutes` as minutes
  // since midnight without a timezone. We construct the same local moment
  // in the browser so the comparison happens against the manager's clock.
  const endLocal = new Date(entryDate.slice(0, 10) + "T00:00:00");
  endLocal.setMinutes(endLocal.getMinutes() + endMinutes);

  const diffMinutes = Math.abs(created.getTime() - endLocal.getTime()) / 60000;
  const mismatch = diffMinutes > LOG_TIME_MISMATCH_THRESHOLD_MINUTES;

  const label = formatLogTime(created);
  const title = mismatch
    ? `Logged ${formatLogDelta(diffMinutes)} ${created > endLocal ? "after" : "before"} the entry's end time.`
    : "Logged within tolerance of the entry's end time.";

  return (
    <span
      title={title}
      className={cn(
        "inline-flex items-center gap-1 font-mono text-xs",
        mismatch ? "text-red-300" : "text-muted-foreground",
      )}
    >
      <History className="h-3 w-3" />
      {label}
    </span>
  );
}

function formatLogTime(d: Date): string {
  const m = (d.getMonth() + 1).toString().padStart(2, "0");
  const day = d.getDate().toString().padStart(2, "0");
  const hh = d.getHours().toString().padStart(2, "0");
  const mm = d.getMinutes().toString().padStart(2, "0");
  return `${m}-${day} ${hh}:${mm}`;
}

function formatLogDelta(minutes: number): string {
  if (minutes < 60) return `${Math.round(minutes)}m`;
  if (minutes < 1440) {
    const hh = Math.floor(minutes / 60);
    const mm = Math.round(minutes - hh * 60);
    return mm === 0 ? `${hh}h` : `${hh}h ${mm}m`;
  }
  const days = Math.floor(minutes / 1440);
  const rem = Math.round((minutes - days * 1440) / 60);
  return rem === 0 ? `${days}d` : `${days}d ${rem}h`;
}

// ---- Ticket autocomplete (shared shape with Tab 1) --------------------

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
  const inputRef = React.useRef<HTMLInputElement | null>(null);
  const [anchor, setAnchor] = React.useState<DropdownAnchor | null>(null);

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

  React.useLayoutEffect(() => {
    if (!open || value) {
      setAnchor(null);
      return;
    }
    const place = () => {
      const el = inputRef.current;
      if (!el) return;
      setAnchor(computeDropdownAnchor(el.getBoundingClientRect()));
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
      <div className="h-8 rounded-md border border-dashed border-glass bg-glass px-2 py-1 text-xs text-muted-foreground/60">
        — (task has no ticket)
      </div>
    );
  }

  return (
    <div className="relative">
      {value ? (
        <div
          className={cn(
            "flex h-8 items-center justify-between gap-2 rounded-md border bg-glass px-2 text-xs",
            error ? "border-red-400/60" : "border-glass",
          )}
        >
          <span className="truncate font-mono">
            #{value.number}
            <span className="ml-2 font-sans text-foreground/70">{value.subject}</span>
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
        />
      )}
      {!value && open && anchor &&
        createPortal(
          <div
            className="fixed z-50 overflow-y-auto rounded-md border border-glass bg-[hsl(var(--background))] p-1 shadow-2xl backdrop-blur-xl"
            style={{
              ...(anchor.top !== undefined ? { top: anchor.top } : {}),
              ...(anchor.bottom !== undefined ? { bottom: anchor.bottom } : {}),
              left: anchor.left,
              width: Math.max(anchor.width, 384),
              maxHeight: anchor.maxHeight,
            }}
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
                className="flex w-full flex-col items-start gap-0 rounded px-2 py-1.5 text-left text-xs hover:bg-glass-hover"
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

// ---- Filter-bar ticket picker (compact, no clear-X-then-edit) ---------

function TicketFilterPicker({
  value,
  onChange,
}: {
  value: string | undefined;
  onChange: (id: string | undefined) => void;
}) {
  const [open, setOpen] = React.useState(false);
  const [query, setQuery] = React.useState("");
  const [results, setResults] = React.useState<TicketPickerItem[]>([]);
  const [selected, setSelected] = React.useState<TicketPickerItem | null>(null);
  const inputRef = React.useRef<HTMLInputElement | null>(null);
  const [anchor, setAnchor] = React.useState<DropdownAnchor | null>(null);

  // Drop the local "selected" preview if the parent cleared the filter
  // (Reset button).
  React.useEffect(() => {
    if (!value) setSelected(null);
  }, [value]);

  React.useEffect(() => {
    if (!open || selected) return;
    let cancelled = false;
    const t = window.setTimeout(async () => {
      try {
        const res = await ticketApi.picker(query.trim() || undefined, undefined, 15);
        if (!cancelled) setResults(res.items);
      } catch {
        if (!cancelled) setResults([]);
      }
    }, 180);
    return () => {
      cancelled = true;
      window.clearTimeout(t);
    };
  }, [query, open, selected]);

  React.useLayoutEffect(() => {
    if (!open || selected) {
      setAnchor(null);
      return;
    }
    const place = () => {
      const el = inputRef.current;
      if (!el) return;
      setAnchor(computeDropdownAnchor(el.getBoundingClientRect()));
    };
    place();
    window.addEventListener("scroll", place, true);
    window.addEventListener("resize", place);
    return () => {
      window.removeEventListener("scroll", place, true);
      window.removeEventListener("resize", place);
    };
  }, [open, selected]);

  if (selected) {
    return (
      <div className="flex h-8 min-w-[12rem] items-center justify-between gap-2 rounded-md border border-glass bg-glass px-2 text-xs">
        <span className="truncate font-mono">#{selected.number}</span>
        <button
          type="button"
          className="text-muted-foreground hover:text-foreground"
          onClick={() => {
            setSelected(null);
            onChange(undefined);
            setQuery("");
          }}
          aria-label="Clear ticket filter"
        >
          <X className="h-3.5 w-3.5" />
        </button>
      </div>
    );
  }

  return (
    <div className="relative min-w-[12rem]">
      <Input
        ref={inputRef}
        value={query}
        onChange={(e) => {
          setQuery(e.target.value);
          setOpen(true);
        }}
        onFocus={() => setOpen(true)}
        onBlur={() => window.setTimeout(() => setOpen(false), 150)}
        placeholder="Any ticket…"
        className="h-8 text-sm"
      />
      {open && !selected && anchor &&
        createPortal(
          <div
            className="fixed z-50 overflow-y-auto rounded-md border border-glass bg-[hsl(var(--background))] p-1 shadow-2xl backdrop-blur-xl"
            style={{
              ...(anchor.top !== undefined ? { top: anchor.top } : {}),
              ...(anchor.bottom !== undefined ? { bottom: anchor.bottom } : {}),
              left: anchor.left,
              width: Math.max(anchor.width, 320),
              maxHeight: anchor.maxHeight,
            }}
            onMouseDown={(e) => e.preventDefault()}
          >
            {results.length === 0 && (
              <div className="px-2 py-1.5 text-xs text-muted-foreground">No matches</div>
            )}
            {results.map((r) => (
              <button
                key={r.id}
                type="button"
                onMouseDown={(e) => {
                  e.preventDefault();
                  setSelected(r);
                  onChange(r.id);
                  setOpen(false);
                }}
                className="flex w-full flex-col items-start rounded px-2 py-1.5 text-left text-xs hover:bg-glass-hover"
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
    </div>
  );
}

// ---- Validation helpers ----------------------------------------------

function validateLocal(row: EditDraft, requiresTicket: boolean): Record<string, string> {
  const errs: Record<string, string> = {};
  if (row.startMinutes === null) errs.startMinutes = "Start required.";
  if (row.endMinutes === null) errs.endMinutes = "End required.";
  if (
    row.startMinutes !== null &&
    row.endMinutes !== null &&
    row.endMinutes <= row.startMinutes
  ) {
    errs.endMinutes = "End must be after start.";
  }
  if (!row.taskId) errs.taskId = "Pick a task.";
  if (requiresTicket && !row.ticket) errs.ticketId = "This task needs a ticket.";
  if (row.description.trim().length === 0) errs.description = "Describe what was done.";
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
