import { useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { settingsApi } from "@/lib/settings-api";
import { taxonomyApi } from "@/lib/taxonomy-api";
import { Switch } from "@/components/ui/switch";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { cn } from "@/lib/utils";
import { ChecklistsSettingsTab } from "./checklists/ChecklistsSettingsTab";
import { QueuesTab } from "@/pages/settings/tickets/QueuesTab";
import { PrioritiesTab } from "@/pages/settings/tickets/PrioritiesTab";
import { StatusesTab } from "@/pages/settings/tickets/StatusesTab";
import { CategoriesTab } from "@/pages/settings/tickets/CategoriesTab";
import { TicketTypesTab } from "@/pages/settings/tickets/TicketTypesTab";

type TabKey = "queues" | "priorities" | "statuses" | "categories" | "types" | "iso27001" | "warnings" | "grouping" | "checklists" | "general";

const TABS: { key: TabKey; label: string; description: string }[] = [
  {
    key: "queues",
    label: "Queues",
    description:
      "Inboxes that group tickets by team. Every ticket lives in exactly one queue.",
  },
  {
    key: "priorities",
    label: "Priorities",
    description:
      "Severity levels used to sort and route tickets. Level drives ordering.",
  },
  {
    key: "statuses",
    label: "Statuses",
    description:
      "Workflow states. Each one is pinned to a semantic category so SLA and reporting keep working when you rename them.",
  },
  {
    key: "categories",
    label: "Categories",
    description:
      "Topic taxonomy used to classify the kind of work a ticket represents. Optional on tickets.",
  },
  {
    key: "types",
    label: "Types",
    description:
      "First-class ticket type — drives the badge on each ticket and the buttons in the 'Create linked ticket' picker. Bind a manual trigger to a type under Settings → Triggers to expose a one-click linked-ticket preset.",
  },
  {
    key: "iso27001",
    label: "ISO 27001",
    description:
      "Bind the ISO 27001 workflow to a dedicated queue. MGM-flagged agents classify each ticket as event or incident; DPO-flagged agents register incidents in the ISMS. Per-user roles are managed under Settings → Users.",
  },
  {
    key: "warnings",
    label: "Warnings",
    description:
      "Visual alerts that surface in the ticket side panel when something needs attention.",
  },
  {
    key: "grouping",
    label: "Grouping",
    description:
      "How the ticket list orders rows inside each group when it is grouped by Status. Pending groups and Open/New groups each get their own sort; Resolved/Closed keep the view's global sort.",
  },
  {
    key: "checklists",
    label: "Checklists",
    description:
      "Checklist templates agents attach to tickets (queue-scoped, snapshot on attach) and the close-block rule: a blocking checklist with required open items stops the ticket from being resolved or closed.",
  },
  {
    key: "general",
    label: "General",
    description:
      "Ticket-wide options: how a ticket reference reads when copied, shown in mail subjects, and pasted into search.",
  },
];

export function TicketsSettingsPage() {
  // Deep link support (global search → checklist template hit):
  // /settings/tickets?tab=checklists&template=<id>. Read once on mount.
  const initial = useMemo(() => {
    const params = new URLSearchParams(window.location.search);
    const t = params.get("tab");
    return {
      tab: (TABS.some((x) => x.key === t) ? (t as TabKey) : "queues") as TabKey,
      templateId: params.get("template"),
    };
  }, []);
  const [tab, setTab] = useState<TabKey>(initial.tab);
  const active = TABS.find((t) => t.key === tab)!;

  return (
    <div className="flex min-h-[calc(100vh-8rem)] w-full flex-col gap-6">
      <header className="flex items-start justify-between gap-4">
        <div className="space-y-1">
          <h1 className="text-display-md font-semibold text-foreground">Tickets</h1>
          <p className="text-sm text-muted-foreground">
            Customize the taxonomies every ticket hangs off: queues, priorities, statuses
            and categories. Every row is editable — the seeded defaults are just a starting
            point.
          </p>
        </div>
        <Badge className="border border-glass bg-glass text-xs font-normal text-muted-foreground">
          Admin only
        </Badge>
      </header>

      <nav className="glass-card flex flex-wrap gap-1 p-1">
        {TABS.map((t) => (
          <button
            key={t.key}
            type="button"
            onClick={() => setTab(t.key)}
            className={cn(
              "flex-1 rounded-md px-4 py-2 text-sm font-medium transition-colors",
              tab === t.key
                ? "bg-glass-strong text-foreground shadow-[inset_0_0_0_1px_rgba(255,255,255,0.08)]"
                : "text-muted-foreground hover:bg-glass-hover hover:text-foreground",
            )}
          >
            {t.label}
          </button>
        ))}
      </nav>

      <p className="text-xs text-muted-foreground">{active.description}</p>

      <div className="min-h-0 flex-1">
        {tab === "queues" && <QueuesTab />}
        {tab === "priorities" && <PrioritiesTab />}
        {tab === "statuses" && <StatusesTab />}
        {tab === "categories" && <CategoriesTab />}
        {tab === "types" && <TicketTypesTab />}
        {tab === "iso27001" && <Iso27001Tab />}
        {tab === "warnings" && <WarningsTab />}
        {tab === "grouping" && <GroupingTab />}
        {tab === "checklists" && <ChecklistsSettingsTab initialTemplateId={initial.templateId} />}
        {tab === "general" && <GeneralTab />}
      </div>
    </div>
  );
}

function WarningsTab() {
  const qc = useQueryClient();
  const { data: entries, isLoading } = useQuery({
    queryKey: ["settings", "tickets-warnings"],
    queryFn: () => settingsApi.list("Tickets"),
    staleTime: 60_000,
  });

  const showContactNotLinked = useMemo(
    () =>
      entries?.find((e) => e.key === "Tickets.ShowContactNotLinkedWarning")?.value === "true",
    [entries],
  );

  const update = useMutation({
    mutationFn: ({ key, value }: { key: string; value: string }) =>
      settingsApi.update(key, value),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["settings", "tickets-warnings"] });
      qc.invalidateQueries({ queryKey: ["settings", "ticket-warnings"] });
      toast.success("Setting updated");
    },
    onError: () => toast.error("Could not update setting"),
  });

  return (
    <div className="space-y-6">
      <section className="glass-card p-5">
        <div className="space-y-1">
          <h2 className="text-sm font-medium uppercase tracking-wide text-muted-foreground">
            Side-panel warnings
          </h2>
          <p className="text-xs text-muted-foreground/70">
            Each toggle turns a single visual cue on or off in the ticket side panel.
          </p>
        </div>
        <div className="mt-4 space-y-3">
          <ToggleRow
            label="Contact not linked to a company"
            description="Pulses an amber pill at the top of the Status tab when the requester has no current company links."
            checked={!!showContactNotLinked}
            disabled={isLoading || update.isPending}
            onCheckedChange={(v) =>
              update.mutate({
                key: "Tickets.ShowContactNotLinkedWarning",
                value: v ? "true" : "false",
              })
            }
          />
        </div>
      </section>
    </div>
  );
}

const GROUP_SORT_FIELDS: { value: string; label: string }[] = [
  { value: "pendingTillUtc", label: "Pending till" },
  { value: "updatedUtc", label: "Last updated" },
  { value: "createdUtc", label: "Created" },
  { value: "dueUtc", label: "Due" },
  { value: "priorityLevel", label: "Priority level" },
  { value: "number", label: "Ticket number" },
];

const GROUP_SORT_DIRECTIONS: { value: string; label: string }[] = [
  { value: "asc", label: "Ascending (oldest / soonest / lowest first)" },
  { value: "desc", label: "Descending (newest / latest / highest first)" },
];

function GroupSortRow({
  label,
  description,
  field,
  direction,
  disabled,
  onChange,
}: {
  label: string;
  description: string;
  field: string;
  direction: string;
  disabled?: boolean;
  onChange: (patch: { field?: string; direction?: string }) => void;
}) {
  return (
    <div className="space-y-2 rounded-md border border-glass bg-glass px-3 py-3">
      <div className="space-y-0.5">
        <div className="text-sm font-medium text-foreground">{label}</div>
        <div className="text-xs text-muted-foreground">{description}</div>
      </div>
      <div className="grid grid-cols-2 gap-3">
        <label className="space-y-1.5">
          <span className="text-xs text-muted-foreground">Field</span>
          <Select value={field} disabled={disabled} onValueChange={(v) => onChange({ field: v })}>
            <SelectTrigger className="h-9 border-glass bg-glass">
              <SelectValue />
            </SelectTrigger>
            <SelectContent className="border-glass bg-popover/80 backdrop-blur-xl">
              {GROUP_SORT_FIELDS.map((o) => (
                <SelectItem key={o.value} value={o.value}>
                  {o.label}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </label>
        <label className="space-y-1.5">
          <span className="text-xs text-muted-foreground">Direction</span>
          <Select value={direction} disabled={disabled} onValueChange={(v) => onChange({ direction: v })}>
            <SelectTrigger className="h-9 border-glass bg-glass">
              <SelectValue />
            </SelectTrigger>
            <SelectContent className="border-glass bg-popover/80 backdrop-blur-xl">
              {GROUP_SORT_DIRECTIONS.map((o) => (
                <SelectItem key={o.value} value={o.value}>
                  {o.label}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </label>
      </div>
    </div>
  );
}

function GroupingTab() {
  const qc = useQueryClient();
  const { data: entries, isLoading } = useQuery({
    queryKey: ["settings", "tickets-grouping"],
    queryFn: () => settingsApi.list("Tickets"),
    staleTime: 60_000,
  });

  const get = (key: string, fallback: string) =>
    entries?.find((e) => e.key === key)?.value ?? fallback;

  const enabled = get("Tickets.GroupSortEnabled", "true") === "true";
  const pendingField = get("Tickets.GroupSortPendingField", "pendingTillUtc");
  const pendingDir = get("Tickets.GroupSortPendingDirection", "asc");
  const openNewField = get("Tickets.GroupSortOpenNewField", "updatedUtc");
  const openNewDir = get("Tickets.GroupSortOpenNewDirection", "asc");

  const update = useMutation({
    mutationFn: ({ key, value }: { key: string; value: string }) =>
      settingsApi.update(key, value),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["settings", "tickets-grouping"] });
      // The grouped ticket list reads the agent-safe projection — refresh it so
      // a change takes effect without a reload.
      qc.invalidateQueries({ queryKey: ["settings", "ticket-grouping"] });
      toast.success("Setting updated");
    },
    onError: () => toast.error("Could not update setting"),
  });

  const busy = isLoading || update.isPending;
  const rowsDisabled = busy || !enabled;

  return (
    <div className="space-y-6">
      <section className="glass-card p-5">
        <div className="space-y-1">
          <h2 className="text-sm font-medium uppercase tracking-wide text-muted-foreground">
            Per-state group sorting
          </h2>
          <p className="text-xs text-muted-foreground/70">
            Only applies when the ticket list is grouped by <strong>Status</strong>.
            Each status group is then sorted by its semantic state category instead
            of the view's global sort. Resolved and Closed groups always keep the
            global sort.
          </p>
        </div>
        <div className="mt-4 space-y-3">
          <ToggleRow
            label="Enable per-state group sorting"
            description="Off = every group uses the view's global sort."
            checked={enabled}
            disabled={busy}
            onCheckedChange={(v) =>
              update.mutate({
                key: "Tickets.GroupSortEnabled",
                value: v ? "true" : "false",
              })
            }
          />
          <GroupSortRow
            label="Pending groups"
            description="Tickets in a Pending-category status. Default: soonest pending-till first."
            field={pendingField}
            direction={pendingDir}
            disabled={rowsDisabled}
            onChange={(p) => {
              if (p.field !== undefined)
                update.mutate({ key: "Tickets.GroupSortPendingField", value: p.field });
              if (p.direction !== undefined)
                update.mutate({ key: "Tickets.GroupSortPendingDirection", value: p.direction });
            }}
          />
          <GroupSortRow
            label="Open & New groups"
            description="Tickets in an Open- or New-category status. Default: least recently updated first."
            field={openNewField}
            direction={openNewDir}
            disabled={rowsDisabled}
            onChange={(p) => {
              if (p.field !== undefined)
                update.mutate({ key: "Tickets.GroupSortOpenNewField", value: p.field });
              if (p.direction !== undefined)
                update.mutate({ key: "Tickets.GroupSortOpenNewDirection", value: p.direction });
            }}
          />
        </div>
      </section>
    </div>
  );
}

function GeneralTab() {
  const qc = useQueryClient();
  const { data: entries, isLoading } = useQuery({
    queryKey: ["settings", "tickets-general"],
    queryFn: () => settingsApi.list("Tickets"),
    staleTime: 60_000,
  });

  const savedPrefix = useMemo(
    () => entries?.find((e) => e.key === "Tickets.ReferencePrefix")?.value ?? "Ticket#",
    [entries],
  );
  const [prefix, setPrefix] = useState<string | null>(null);
  // Null = not yet edited; fall back to the saved value for display.
  const value = prefix ?? savedPrefix;
  const dirty = prefix !== null && prefix !== savedPrefix;

  const update = useMutation({
    mutationFn: (next: string) => settingsApi.update("Tickets.ReferencePrefix", next),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["settings", "tickets-general"] });
      // The copy button reads this via its own cached query — refresh it too.
      qc.invalidateQueries({ queryKey: ["system", "ticket-reference-prefix"] });
      setPrefix(null);
      toast.success("Setting updated");
    },
    onError: () => toast.error("Could not update setting"),
  });

  // Maximum tickets loaded at once in the list + saved views (single-load
  // model — no lazy loading). Existing installs keep their stored value across
  // default changes, so this control is how an admin raises it.
  const savedMaxRows = useMemo(
    () => entries?.find((e) => e.key === "Tickets.ListPageSize")?.value ?? "1000",
    [entries],
  );
  const [maxRows, setMaxRows] = useState<string | null>(null);
  const maxRowsValue = maxRows ?? savedMaxRows;
  const maxRowsNum = Number.parseInt(maxRowsValue, 10);
  const maxRowsValid = Number.isFinite(maxRowsNum) && maxRowsNum >= 1 && maxRowsNum <= 5000;
  const maxRowsDirty = maxRows !== null && maxRows !== savedMaxRows;

  const updateMaxRows = useMutation({
    mutationFn: (next: string) => settingsApi.update("Tickets.ListPageSize", next),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["settings", "tickets-general"] });
      // The ticket list reads this server-side — refetch so the new cap applies.
      qc.invalidateQueries({ queryKey: ["tickets"] });
      setMaxRows(null);
      toast.success("Setting updated");
    },
    onError: () => toast.error("Could not update setting"),
  });

  // v0.1.33 — ticket timeline paging: events in the first page of a ticket
  // (older ones load on demand). 0 = load the whole timeline at once.
  const savedTimelinePage = useMemo(
    () => entries?.find((e) => e.key === "Tickets.TimelinePageSize")?.value ?? "50",
    [entries],
  );
  const [timelinePage, setTimelinePage] = useState<string | null>(null);
  const timelinePageValue = timelinePage ?? savedTimelinePage;
  const timelinePageNum = Number.parseInt(timelinePageValue, 10);
  const timelinePageValid =
    Number.isFinite(timelinePageNum) && (timelinePageNum === 0 || (timelinePageNum >= 10 && timelinePageNum <= 1000));
  const timelinePageDirty = timelinePage !== null && timelinePage !== savedTimelinePage;

  const updateTimelinePage = useMutation({
    mutationFn: (next: string) => settingsApi.update("Tickets.TimelinePageSize", next),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["settings", "tickets-general"] });
      setTimelinePage(null);
      toast.success("Setting updated");
    },
    onError: () => toast.error("Could not update setting"),
  });

  // v0.0.102 — bulk actions on the ticket list. The enable flag hides the
  // selection UI entirely; the cap bounds one request's worth of work and is
  // re-enforced server-side (hard ceiling 500).
  const bulkEnabled = useMemo(
    () => (entries?.find((e) => e.key === "Tickets.BulkActionsEnabled")?.value ?? "true") === "true",
    [entries],
  );
  const savedBulkMax = useMemo(
    () => entries?.find((e) => e.key === "Tickets.BulkActionsMaxSelection")?.value ?? "100",
    [entries],
  );
  const [bulkMax, setBulkMax] = useState<string | null>(null);
  const bulkMaxValue = bulkMax ?? savedBulkMax;
  const bulkMaxNum = Number.parseInt(bulkMaxValue, 10);
  const bulkMaxValid = Number.isFinite(bulkMaxNum) && bulkMaxNum >= 1 && bulkMaxNum <= 500;
  const bulkMaxDirty = bulkMax !== null && bulkMax !== savedBulkMax;

  const updateBulk = useMutation({
    mutationFn: ({ key, value }: { key: string; value: string }) => settingsApi.update(key, value),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["settings", "tickets-general"] });
      // The ticket list reads the agent-safe projection — refresh it too.
      qc.invalidateQueries({ queryKey: ["settings", "bulk-actions"] });
      setBulkMax(null);
      toast.success("Setting updated");
    },
    onError: () => toast.error("Could not update setting"),
  });

  // v0.1.18 — per-view search box knobs (agent-readable via
  // /api/settings/view-search; validated + clamped server-side).
  const updateViewSearch = useMutation({
    mutationFn: ({ key, value }: { key: string; value: string }) => settingsApi.update(key, value),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["settings", "tickets-general"] });
      qc.invalidateQueries({ queryKey: ["settings", "view-search"] });
      toast.success("Setting updated");
    },
    onError: () => toast.error("Could not update setting"),
  });

  // v0.0.105 — project tickets. Both flags are re-enforced server-side on
  // every project endpoint; the agent-safe projection feeds the ticket UI.
  const projectsEnabled = useMemo(
    () => (entries?.find((e) => e.key === "Projects.Enabled")?.value ?? "true") === "true",
    [entries],
  );
  const projectPromptEnabled = useMemo(
    () => (entries?.find((e) => e.key === "Projects.LinkPromptEnabled")?.value ?? "true") === "true",
    [entries],
  );
  const projectQueueId = useMemo(
    () => entries?.find((e) => e.key === "Projects.QueueId")?.value ?? "",
    [entries],
  );
  const { data: projectQueues } = useQuery({
    queryKey: ["taxonomy", "queues"],
    queryFn: () => taxonomyApi.queues.list(),
    staleTime: 60_000,
  });
  const updateProjects = useMutation({
    mutationFn: ({ key, value }: { key: string; value: string }) => settingsApi.update(key, value),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["settings", "tickets-general"] });
      qc.invalidateQueries({ queryKey: ["settings", "projects"] });
      toast.success("Setting updated");
    },
    onError: () => toast.error("Could not update setting"),
  });

  // v0.1.17 — Call-back / Research flags: accent colours (served to agents
  // via the agent-safe /api/settings/ticket-flags projection) and the
  // call-back prompt on open. Colours are validated to #rrggbb server-side.
  const savedCallbackColor = useMemo(
    () => entries?.find((e) => e.key === "Tickets.CallbackColor")?.value ?? "#22c55e",
    [entries],
  );
  const savedResearchColor = useMemo(
    () => entries?.find((e) => e.key === "Tickets.ResearchColor")?.value ?? "#3b82f6",
    [entries],
  );
  const callbackPromptEnabled = useMemo(
    () => (entries?.find((e) => e.key === "Tickets.CallbackOpenPromptEnabled")?.value ?? "false") === "true",
    [entries],
  );
  const callbackClearOnCall = useMemo(
    () => (entries?.find((e) => e.key === "Tickets.CallbackClearOnCall")?.value ?? "true") === "true",
    [entries],
  );
  const updateFlags = useMutation({
    mutationFn: ({ key, value }: { key: string; value: string }) => settingsApi.update(key, value),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["settings", "tickets-general"] });
      qc.invalidateQueries({ queryKey: ["settings", "ticket-flags"] });
      toast.success("Setting updated");
    },
    onError: () => toast.error("Could not update setting"),
  });

  return (
    <div className="space-y-6">
      <section className="glass-card p-5">
        <div className="space-y-1">
          <h2 className="text-sm font-medium uppercase tracking-wide text-muted-foreground">
            Ticket reference
          </h2>
          <p className="text-xs text-muted-foreground/70">
            Prefix shown in front of a ticket number wherever a human-readable
            reference appears — the copy-to-clipboard button, outbound mail
            subjects ([{value}1234]), survey invites, and the{" "}
            <code className="rounded bg-glass px-1">#{"{ticket.reference}"}</code>{" "}
            template variable. Pasting a reference in this form into global
            search, the ticket picker, or a timesheet link resolves it straight
            back to the ticket; a bare number or a leading “#” are always
            accepted too.
          </p>
        </div>
        <div className="mt-4 flex items-end gap-3">
          <label className="min-w-0 flex-1 space-y-1.5">
            <span className="text-sm font-medium text-foreground">Prefix</span>
            <Input
              value={value}
              disabled={isLoading || update.isPending}
              onChange={(e) => setPrefix(e.target.value)}
              placeholder="Ticket#"
              className="max-w-xs"
            />
          </label>
          <Button
            type="button"
            disabled={!dirty || update.isPending}
            onClick={() => update.mutate(value)}
          >
            Save
          </Button>
        </div>
        <p className="mt-3 text-xs text-muted-foreground/70">
          Preview: <span className="font-mono text-foreground">{value}1234</span>
        </p>
      </section>

      <section className="glass-card p-5">
        <div className="space-y-1">
          <h2 className="text-sm font-medium uppercase tracking-wide text-muted-foreground">
            List size
          </h2>
          <p className="text-xs text-muted-foreground/70">
            Maximum tickets loaded at once in the ticket list and saved views.
            The list loads in a single request (no lazy loading); a view with
            more matches than this shows the first N with a “refine your
            filters” note. Keep it high enough to cover your busiest view, but
            mind the browser on very large result sets. Range 1–5000.
          </p>
        </div>
        <div className="mt-4 flex items-end gap-3">
          <label className="min-w-0 flex-1 space-y-1.5">
            <span className="text-sm font-medium text-foreground">Maximum tickets</span>
            <Input
              type="number"
              min={1}
              max={5000}
              value={maxRowsValue}
              disabled={isLoading || updateMaxRows.isPending}
              onChange={(e) => setMaxRows(e.target.value)}
              placeholder="1000"
              className="max-w-xs"
            />
          </label>
          <Button
            type="button"
            disabled={!maxRowsDirty || !maxRowsValid || updateMaxRows.isPending}
            onClick={() => updateMaxRows.mutate(String(maxRowsNum))}
          >
            Save
          </Button>
        </div>
        {!maxRowsValid && (
          <p className="mt-2 text-xs text-destructive">
            Enter a whole number between 1 and 5000.
          </p>
        )}
      </section>

      <section className="glass-card p-5">
        <div className="space-y-1">
          <h2 className="text-sm font-medium uppercase tracking-wide text-muted-foreground">
            Ticket timeline
          </h2>
          <p className="text-xs text-muted-foreground/70">
            How many timeline entries (mails, notes, status changes, …) a ticket
            opens with. Older entries load with “Load older” at the top of the
            timeline, or on their own when a link points at one. Fewer means a
            faster ticket open on long threads; in-ticket search only covers
            what is loaded. 0 loads the whole timeline at once. Range 10–1000,
            or 0.
          </p>
        </div>
        <div className="mt-4 flex items-end gap-3">
          <label className="min-w-0 flex-1 space-y-1.5">
            <span className="text-sm font-medium text-foreground">Entries per page</span>
            <Input
              type="number"
              min={0}
              max={1000}
              value={timelinePageValue}
              disabled={isLoading || updateTimelinePage.isPending}
              onChange={(e) => setTimelinePage(e.target.value)}
              placeholder="50"
              className="max-w-xs"
            />
          </label>
          <Button
            type="button"
            disabled={!timelinePageDirty || !timelinePageValid || updateTimelinePage.isPending}
            onClick={() => updateTimelinePage.mutate(String(timelinePageNum))}
          >
            Save
          </Button>
        </div>
        {!timelinePageValid && (
          <p className="mt-2 text-xs text-destructive">
            Enter 0, or a whole number between 10 and 1000.
          </p>
        )}
      </section>

      <section className="glass-card p-5">
        <div className="space-y-1">
          <h2 className="text-sm font-medium uppercase tracking-wide text-muted-foreground">
            Bulk actions
          </h2>
          <p className="text-xs text-muted-foreground/70">
            Lets agents select several tickets in the list and, in one go, post
            an internal note or public comment and/or change status, queue,
            assignee or priority. Every selected ticket still runs its normal
            rules — queue access, allowed statuses, status gates, triggers, SLA,
            audit — and is skipped (with a reason) rather than forced when a
            rule doesn't allow the change. Bulk actions never send customer
            mail directly; trigger mail behaves exactly as for a manual change.
          </p>
        </div>
        <div className="mt-4 space-y-3">
          <ToggleRow
            label="Enable bulk actions"
            description="Shows row checkboxes and the bulk-edit bar on the ticket list for agents and admins."
            checked={bulkEnabled}
            disabled={isLoading || updateBulk.isPending}
            onCheckedChange={(v) =>
              updateBulk.mutate({ key: "Tickets.BulkActionsEnabled", value: v ? "true" : "false" })
            }
          />
          <div className="flex items-end gap-3">
            <label className="min-w-0 flex-1 space-y-1.5">
              <span className="text-sm font-medium text-foreground">Maximum tickets per bulk action</span>
              <Input
                type="number"
                min={1}
                max={500}
                value={bulkMaxValue}
                disabled={isLoading || updateBulk.isPending}
                onChange={(e) => setBulkMax(e.target.value)}
                placeholder="100"
                className="max-w-xs"
              />
            </label>
            <Button
              type="button"
              disabled={!bulkMaxDirty || !bulkMaxValid || updateBulk.isPending}
              onClick={() =>
                updateBulk.mutate({ key: "Tickets.BulkActionsMaxSelection", value: String(bulkMaxNum) })
              }
            >
              Save
            </Button>
          </div>
          {!bulkMaxValid && (
            <p className="text-xs text-destructive">Enter a whole number between 1 and 500.</p>
          )}
          <p className="text-xs text-muted-foreground/70">
            Bulk actions run synchronously, ticket by ticket, inside one request —
            keep this at a size that comfortably finishes (100 is a good default;
            500 is the hard ceiling).
          </p>
        </div>
      </section>

      <section className="glass-card p-5">
        <div className="space-y-1">
          <h2 className="text-sm font-medium uppercase tracking-wide text-muted-foreground">
            View search
          </h2>
          <p className="text-xs text-muted-foreground/70">
            Views with the search box switched on (Settings → Views → edit a
            view) show one search field with two modes. Columns filters the
            loaded tickets instantly on the visible columns; Full also searches
            descriptions, mails and notes on the server. Both only ever search
            the tickets in that view.
          </p>
        </div>
        <div className="mt-4 space-y-3">
          <NumberSettingRow
            label="Minimum characters"
            description="Shorter search terms show the whole view."
            saved={entries?.find((e) => e.key === "Tickets.ViewSearchMinChars")?.value ?? "2"}
            min={1}
            max={10}
            disabled={isLoading || updateViewSearch.isPending}
            onSave={(v) => updateViewSearch.mutate({ key: "Tickets.ViewSearchMinChars", value: v })}
          />
          <NumberSettingRow
            label="Typing pause before a server search (ms)"
            description="Used by Full mode, and by Columns mode when the view has more tickets than the list loads. Filtering loaded tickets is always instant."
            saved={entries?.find((e) => e.key === "Tickets.ViewSearchDebounceMs")?.value ?? "300"}
            min={0}
            max={2000}
            disabled={isLoading || updateViewSearch.isPending}
            onSave={(v) => updateViewSearch.mutate({ key: "Tickets.ViewSearchDebounceMs", value: v })}
          />
        </div>
      </section>

      <section className="glass-card p-5">
        <div className="space-y-1">
          <h2 className="text-sm font-medium uppercase tracking-wide text-muted-foreground">
            Call-back &amp; Research flags
          </h2>
          <p className="text-xs text-muted-foreground/70">
            Agents mark a ticket as Call-back (someone has to call the customer
            back) or Research (under investigation) from the ticket's Status
            tab; Call-back can also be set when creating a ticket. Views can
            float these tickets to the top with the Call-back and Research
            float switches — always below the Priority float. The colour paints
            the float group, the accent bar and glow on the ticket row and the
            badge in the ticket header. Both flags are internal — customers
            never see them.
          </p>
        </div>
        <div className="mt-4 space-y-3">
          <FlagColorRow
            label="Call-back colour"
            saved={savedCallbackColor}
            disabled={isLoading || updateFlags.isPending}
            onSave={(v) => updateFlags.mutate({ key: "Tickets.CallbackColor", value: v })}
          />
          <FlagColorRow
            label="Research colour"
            saved={savedResearchColor}
            disabled={isLoading || updateFlags.isPending}
            onSave={(v) => updateFlags.mutate({ key: "Tickets.ResearchColor", value: v })}
          />
          <ToggleRow
            label="Ask to turn off call-back on open"
            description="When an agent opens a call-back ticket, ask whether the call-back flag can be turned off. Asked on every open while the flag is on."
            checked={callbackPromptEnabled}
            disabled={isLoading || updateFlags.isPending}
            onCheckedChange={(v) =>
              updateFlags.mutate({ key: "Tickets.CallbackOpenPromptEnabled", value: v ? "true" : "false" })
            }
          />
          <ToggleRow
            label="Turn off call-back when a call is logged"
            description="Logging a phone call with the Call button on a call-back ticket turns the call-back flag off automatically. The change is recorded on the ticket timeline."
            checked={callbackClearOnCall}
            disabled={isLoading || updateFlags.isPending}
            onCheckedChange={(v) =>
              updateFlags.mutate({ key: "Tickets.CallbackClearOnCall", value: v ? "true" : "false" })
            }
          />
        </div>
      </section>

      <section className="glass-card p-5">
        <div className="space-y-1">
          <h2 className="text-sm font-medium uppercase tracking-wide text-muted-foreground">
            Project tickets
          </h2>
          <p className="text-xs text-muted-foreground/70">
            A ticket can be created as (or converted to) a project ticket.
            Other tickets link to it via “Link to project” while staying in
            their own queue; the project ticket shows a panel with every
            linked ticket (drag to prioritize) and a time rollup over the
            whole project for after-calculation. Projects are internal —
            customers never see them.
          </p>
        </div>
        <div className="mt-4 space-y-3">
          <ToggleRow
            label="Enable project tickets"
            description="Shows the project toggle on new tickets, the convert/link actions and the project panel. Turning this off hides every project surface; existing flags and links are kept."
            checked={projectsEnabled}
            disabled={isLoading || updateProjects.isPending}
            onCheckedChange={(v) =>
              updateProjects.mutate({ key: "Projects.Enabled", value: v ? "true" : "false" })
            }
          />
          <ToggleRow
            label="Ask to link new tickets to an open project"
            description="When a ticket is opened for the first time and its company has an open project, the agent is asked whether to link it. A 'no' is remembered per ticket."
            checked={projectPromptEnabled}
            disabled={isLoading || updateProjects.isPending}
            onCheckedChange={(v) =>
              updateProjects.mutate({ key: "Projects.LinkPromptEnabled", value: v ? "true" : "false" })
            }
          />
          <div className="space-y-1.5">
            <span className="text-sm font-medium text-foreground">Project queue</span>
            <select
              value={projectQueueId}
              disabled={isLoading || updateProjects.isPending}
              onChange={(e) =>
                updateProjects.mutate({ key: "Projects.QueueId", value: e.target.value })
              }
              className="h-9 w-full max-w-xs rounded-md border border-glass bg-glass px-2 text-sm text-foreground outline-hidden focus-visible:ring-2 focus-visible:ring-ring"
            >
              <option value="">Not pinned — projects can sit in any queue</option>
              {(projectQueues ?? []).map((q) => (
                <option key={q.id} value={q.id}>
                  {q.name}
                </option>
              ))}
            </select>
            <p className="text-xs text-muted-foreground/70">
              When set, new and converted project tickets are placed in this
              queue automatically, the queue field disappears on project
              tickets, and moving them to another queue is refused everywhere
              (manual, bulk and triggers). This queue's access list decides
              which agents see projects.
            </p>
          </div>
        </div>
      </section>
    </div>
  );
}

const HEX_COLOR = /^#[0-9a-fA-F]{6}$/;

/// v0.1.17 — colour picker + hex input for one flag colour, saved on demand.
function FlagColorRow({
  label,
  saved,
  disabled,
  onSave,
}: {
  label: string;
  saved: string;
  disabled: boolean;
  onSave: (value: string) => void;
}) {
  const [draft, setDraft] = useState<string | null>(null);
  const value = draft ?? saved;
  const valid = HEX_COLOR.test(value);
  const dirty = draft !== null && draft.toLowerCase() !== saved.toLowerCase();
  return (
    <div className="space-y-1.5">
      <span className="text-sm font-medium text-foreground">{label}</span>
      <div className="flex items-center gap-2">
        <input
          type="color"
          value={valid ? value : saved}
          disabled={disabled}
          onChange={(e) => setDraft(e.target.value)}
          className="h-9 w-12 cursor-pointer rounded-md border border-glass bg-transparent"
          aria-label={label}
        />
        <Input
          value={value}
          disabled={disabled}
          onChange={(e) => setDraft(e.target.value.trim())}
          className="max-w-36 font-mono text-xs"
        />
        <Button
          type="button"
          disabled={disabled || !dirty || !valid}
          onClick={() => {
            onSave(value);
            setDraft(null);
          }}
        >
          Save
        </Button>
      </div>
      {!valid && <p className="text-xs text-destructive">Enter a hex colour like #22c55e.</p>}
    </div>
  );
}

function ToggleRow({
  label,
  description,
  checked,
  disabled,
  onCheckedChange,
}: {
  label: string;
  description: string;
  checked: boolean;
  disabled?: boolean;
  onCheckedChange: (v: boolean) => void;
}) {
  return (
    <label className="flex items-start justify-between gap-4 rounded-md border border-glass bg-glass px-3 py-2.5">
      <div className="min-w-0 space-y-0.5">
        <div className="text-sm font-medium text-foreground">{label}</div>
        <div className="text-xs text-muted-foreground">{description}</div>
      </div>
      <Switch
        checked={checked}
        disabled={disabled}
        onCheckedChange={onCheckedChange}
      />
    </label>
  );
}

// ---------- ISO 27001 tab (v0.0.40) ----------

function Iso27001Tab() {
  const qc = useQueryClient();
  const { data: entries, isLoading: settingsLoading } = useQuery({
    queryKey: ["settings", "iso27001"],
    queryFn: () => settingsApi.list("Iso27001"),
    staleTime: 60_000,
  });
  const { data: queues, isLoading: queuesLoading } = useQuery({
    queryKey: ["taxonomy", "queues"],
    queryFn: () => taxonomyApi.queues.list(),
  });

  const queueIdValue = useMemo(
    () => entries?.find((e) => e.key === "Iso27001.QueueId")?.value ?? "",
    [entries],
  );

  const update = useMutation({
    mutationFn: ({ value }: { value: string }) =>
      settingsApi.update("Iso27001.QueueId", value),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["settings", "iso27001"] });
      qc.invalidateQueries({ queryKey: ["iso", "config"] });
      toast.success("ISO 27001 queue updated");
    },
    onError: () => toast.error("Could not update ISO 27001 queue"),
  });

  const loading = settingsLoading || queuesLoading;
  const isConfigured = !!queueIdValue && queueIdValue.trim().length > 0;
  const boundQueueName = queues?.find((q) => q.id === queueIdValue)?.name ?? null;

  return (
    <div className="space-y-6">
      <section className="glass-card p-5">
        <div className="space-y-1">
          <h2 className="text-sm font-medium uppercase tracking-wide text-muted-foreground">
            Workflow queue
          </h2>
          <p className="text-xs text-muted-foreground/70">
            Pick the queue where ISO 27001 tickets land. Leave empty to disable
            the classification buttons entirely. Tickets in any other queue are
            unaffected.
          </p>
        </div>
        <div className="mt-4 space-y-3">
          <label className="block">
            <span className="mb-1.5 block text-xs font-medium text-muted-foreground">
              Bound queue
            </span>
            <Select
              value={queueIdValue || "__none__"}
              disabled={loading || update.isPending}
              onValueChange={(v) =>
                update.mutate({ value: v === "__none__" ? "" : v })
              }
            >
              <SelectTrigger className="h-9 w-full border-glass bg-glass">
                <SelectValue placeholder="— disabled —" />
              </SelectTrigger>
              <SelectContent className="border-glass bg-popover/80 backdrop-blur-xl">
                <SelectItem value="__none__">— disabled —</SelectItem>
                {(queues ?? [])
                  .filter((q) => q.isActive)
                  .map((q) => (
                    <SelectItem key={q.id} value={q.id}>
                      {q.name}
                    </SelectItem>
                  ))}
              </SelectContent>
            </Select>
          </label>
          <div className="rounded-md border border-glass-strong bg-glass px-3 py-2 text-xs text-muted-foreground">
            {isConfigured ? (
              <>
                <span className="text-emerald-300">Active.</span>{" "}
                Workflow runs in <span className="font-medium text-foreground">{boundQueueName ?? "(unknown queue)"}</span>.
                Per-user MGM / DPO roles are toggled in Settings → Users.
              </>
            ) : (
              <>
                <span className="text-muted-foreground">Disabled.</span>{" "}
                The classification buttons never appear until a queue is bound here.
              </>
            )}
          </div>
        </div>
      </section>
    </div>
  );
}

/// v0.1.18 — a whole-number setting with its own Save button and range
/// check (the server validates the same range).
function NumberSettingRow({
  label,
  description,
  saved,
  min,
  max,
  disabled,
  onSave,
}: {
  label: string;
  description: string;
  saved: string;
  min: number;
  max: number;
  disabled: boolean;
  onSave: (value: string) => void;
}) {
  const [draft, setDraft] = useState<string | null>(null);
  const value = draft ?? saved;
  const num = Number.parseInt(value, 10);
  const valid = /^\d+$/.test(value.trim()) && num >= min && num <= max;
  const dirty = draft !== null && draft !== saved;
  return (
    <div className="space-y-1.5">
      <div className="flex items-end gap-3">
        <label className="min-w-0 flex-1 space-y-1.5">
          <span className="text-sm font-medium text-foreground">{label}</span>
          <Input
            type="number"
            min={min}
            max={max}
            value={value}
            disabled={disabled}
            onChange={(e) => setDraft(e.target.value)}
            className="max-w-xs"
          />
        </label>
        <Button
          type="button"
          disabled={!dirty || !valid || disabled}
          onClick={() => {
            onSave(String(num));
            setDraft(null);
          }}
        >
          Save
        </Button>
      </div>
      {valid ? (
        <p className="text-xs text-muted-foreground/70">{description}</p>
      ) : (
        <p className="text-xs text-destructive">Enter a whole number between {min} and {max}.</p>
      )}
    </div>
  );
}
