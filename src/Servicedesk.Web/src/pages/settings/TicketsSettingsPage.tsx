import { useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import {
  ApiError,
  graphAdminApi,
  settingsApi,
  taxonomyApi,
  STATE_CATEGORIES,
  type Category,
  type CategoryInput,
  type GraphMailFolder,
  type Priority,
  type PriorityInput,
  type Queue,
  type QueueInput,
  type Status,
  type StatusInput,
  type StatusStateCategory,
  type TicketType,
  type TicketTypeInput,
} from "@/lib/api";
import { Plus, Trash2, Mail } from "lucide-react";
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
import { Skeleton } from "@/components/ui/skeleton";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { cn } from "@/lib/utils";
import { ChecklistsSettingsTab } from "./checklists/ChecklistsSettingsTab";

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

  // v0.0.104 — project tickets. Both flags are re-enforced server-side on
  // every project endpoint; the agent-safe projection feeds the ticket UI.
  const projectsEnabled = useMemo(
    () => (entries?.find((e) => e.key === "Projects.Enabled")?.value ?? "true") === "true",
    [entries],
  );
  const projectPromptEnabled = useMemo(
    () => (entries?.find((e) => e.key === "Projects.LinkPromptEnabled")?.value ?? "true") === "true",
    [entries],
  );
  const updateProjects = useMutation({
    mutationFn: ({ key, value }: { key: string; value: string }) => settingsApi.update(key, value),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["settings", "tickets-general"] });
      qc.invalidateQueries({ queryKey: ["settings", "projects"] });
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
        </div>
      </section>
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

// ---------- Shared primitives ----------

function TableShell({ children }: { children: React.ReactNode }) {
  return (
    <section className="glass-card overflow-hidden">
      <table className="w-full text-left text-sm">{children}</table>
    </section>
  );
}

function ColorSwatch({ color }: { color: string }) {
  return (
    <span
      className="inline-block h-4 w-4 rounded-sm border border-glass"
      style={{ backgroundColor: color }}
      aria-hidden
    />
  );
}

function useDeleteHandler(
  remove: (id: string) => Promise<void>,
  invalidateKey: unknown[],
  label: string,
) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: remove,
    onSuccess: (_result, deletedId) => {
      qc.setQueryData<Array<{ id: string }>>(invalidateKey, (old) =>
        old?.filter((item) => item.id !== deletedId),
      );
      qc.invalidateQueries({ queryKey: invalidateKey });
      toast.success(`${label} deleted`);
    },
    onError: (err) => {
      if (err instanceof ApiError && err.status === 409) {
        toast.error(
          "Still in use or system-protected. Reassign tickets or rename instead.",
        );
      } else {
        toast.error(`Failed to delete ${label.toLowerCase()}`);
      }
    },
  });
}

// ---------- Queues tab ----------

function QueuesTab() {
  const qc = useQueryClient();
  const [editing, setEditing] = useState<Queue | null | "new">(null);

  const { data, isLoading } = useQuery({
    queryKey: ["taxonomy", "queues"],
    queryFn: () => taxonomyApi.queues.list(),
  });

  const del = useDeleteHandler(
    (id) => taxonomyApi.queues.remove(id),
    ["taxonomy", "queues"],
    "Queue",
  );

  return (
    <div className="space-y-4">
      <div className="flex justify-end">
        <Button onClick={() => setEditing("new")}>+ New queue</Button>
      </div>
      {isLoading ? (
        <LoadingSkeleton />
      ) : (
        <TableShell>
          <thead className="text-xs uppercase tracking-wide text-muted-foreground [&_th]:border-b [&_th]:border-glass">
            <tr>
              <th className="px-4 py-3 font-medium">Name</th>
              <th className="px-4 py-3 font-medium">Color</th>
              <th className="px-4 py-3 font-medium">Order</th>
              <th className="px-4 py-3 font-medium">Status</th>
              <th className="px-4 py-3" />
            </tr>
          </thead>
          <tbody>
            {data?.map((q) => (
              <tr key={q.id} className="border-b border-glass hover:bg-glass-hover">
                <td className="px-4 py-3 text-foreground">
                  <div className="flex items-center gap-2">
                    {q.name}
                  </div>
                  {q.description && (
                    <div className="text-xs text-muted-foreground">{q.description}</div>
                  )}
                </td>
                <td className="px-4 py-3">
                  <div className="flex items-center gap-2">
                    <ColorSwatch color={q.color} />
                    <span className="font-mono text-[11px] text-muted-foreground">{q.color}</span>
                  </div>
                </td>
                <td className="px-4 py-3 text-muted-foreground">{q.sortOrder}</td>
                <td className="px-4 py-3">
                  {q.isActive ? (
                    <Badge className="border border-emerald-400/20 bg-emerald-400/10 text-[10px] font-normal text-emerald-200">
                      active
                    </Badge>
                  ) : (
                    <Badge className="border border-glass bg-glass text-[10px] font-normal text-muted-foreground">
                      inactive
                    </Badge>
                  )}
                </td>
                <td className="px-4 py-3 text-right">
                  <Button variant="ghost" size="sm" onClick={() => setEditing(q)}>
                    Edit
                  </Button>
                  <Button
                    variant="ghost"
                    size="sm"
                    disabled={del.isPending}
                    onClick={() => {
                      if (confirm(`Delete queue "${q.name}"?`)) del.mutate(q.id);
                    }}
                  >
                    Delete
                  </Button>
                </td>
              </tr>
            ))}
            {data && data.length === 0 && (
              <tr>
                <td colSpan={6} className="p-8 text-center text-sm text-muted-foreground">
                  No queues yet.
                </td>
              </tr>
            )}
          </tbody>
        </TableShell>
      )}

      {editing && (
        <QueueDialog
          queue={editing === "new" ? null : editing}
          onClose={() => setEditing(null)}
          onSaved={() => {
            qc.invalidateQueries({ queryKey: ["taxonomy", "queues"] });
            setEditing(null);
          }}
        />
      )}
    </div>
  );
}

/// One editable inbound-mailbox source in the queue dialog. `key` is a stable
/// client-side id so per-row folder lists / loading state survive re-renders;
/// `id` is the server source id (absent for newly-added rows).
type InboundRow = {
  key: string;
  id?: string;
  mailbox: string;
  folderId: string;
  folderName: string;
  pollingEnabled: boolean;
};

function QueueDialog({
  queue,
  onClose,
  onSaved,
}: {
  queue: Queue | null;
  onClose: () => void;
  onSaved: () => void;
}) {
  const [form, setForm] = useState<QueueInput>(() => ({
    name: queue?.name ?? "",
    description: queue?.description ?? "",
    color: queue?.color ?? "#7c7cff",
    icon: queue?.icon ?? "inbox",
    sortOrder: queue?.sortOrder ?? 0,
    isActive: queue?.isActive ?? true,
    outboundMailboxAddress: queue?.outboundMailboxAddress ?? "",
    allowedStatusIds: queue?.allowedStatusIds ?? [],
    defaultStatusId: queue?.defaultStatusId ?? null,
    aiAssistEnabled: queue?.aiAssistEnabled ?? true,
    timeAlertMode: queue?.timeAlertMode ?? "inherit",
    timeAlertThresholdMinutes: queue?.timeAlertThresholdMinutes ?? null,
  }));

  // v0.0.66 — a queue can have many inbound mailbox sources. Each editable row
  // carries a stable client-side `key` so its folder list / loading state is
  // tracked independently of server ids (new rows have no id yet).
  const [rows, setRows] = useState<InboundRow[]>(() =>
    (queue?.inboundMailboxes ?? []).map((m) => ({
      key: crypto.randomUUID(),
      id: m.id,
      mailbox: m.mailbox ?? "",
      folderId: m.folderId ?? "",
      folderName: m.folderName ?? "",
      pollingEnabled: m.pollingEnabled,
    })),
  );

  // v0.0.40 polish — load every status so the "Status scope" block
  // can offer the multi-select. Cached + reused with the existing key.
  const { data: statuses } = useQuery({
    queryKey: ["taxonomy", "statuses"],
    queryFn: () => taxonomyApi.statuses.list(),
    staleTime: 60_000,
  });

  const [folders, setFolders] = useState<Record<string, GraphMailFolder[]>>({});
  const [foldersLoading, setFoldersLoading] = useState<Record<string, boolean>>({});
  const [foldersError, setFoldersError] = useState<Record<string, string | null>>({});

  const updateRow = (key: string, patch: Partial<InboundRow>) =>
    setRows((rs) => rs.map((r) => (r.key === key ? { ...r, ...patch } : r)));

  const addRow = () =>
    setRows((rs) => [
      ...rs,
      { key: crypto.randomUUID(), mailbox: "", folderId: "", folderName: "", pollingEnabled: true },
    ]);

  const removeRow = (key: string) => setRows((rs) => rs.filter((r) => r.key !== key));

  const loadFolders = async (row: InboundRow) => {
    const mailbox = row.mailbox.trim();
    if (!mailbox) return;
    setFoldersLoading((s) => ({ ...s, [row.key]: true }));
    setFoldersError((s) => ({ ...s, [row.key]: null }));
    try {
      const result = await graphAdminApi.listFolders(mailbox);
      if (Array.isArray(result)) {
        setFolders((s) => ({ ...s, [row.key]: result }));
      } else {
        setFoldersError((s) => ({
          ...s,
          [row.key]: (result as { error?: string }).error ?? "Unknown error",
        }));
      }
    } catch (err) {
      setFoldersError((s) => ({
        ...s,
        [row.key]: err instanceof ApiError ? `Failed (${err.status})` : "Failed to load folders",
      }));
    } finally {
      setFoldersLoading((s) => ({ ...s, [row.key]: false }));
    }
  };

  // Every row must have both a mailbox and a selected folder before saving.
  const rowsComplete = rows.every((r) => r.mailbox.trim() && r.folderId.trim());

  const save = useMutation({
    mutationFn: async () => {
      const payload: QueueInput = {
        ...form,
        outboundMailboxAddress: form.outboundMailboxAddress?.trim() || null,
        inboundMailboxes: rows.map((r) => ({
          id: r.id ?? null,
          mailbox: r.mailbox.trim(),
          folderId: r.folderId.trim() || null,
          folderName: r.folderName.trim() || null,
          pollingEnabled: r.pollingEnabled,
        })),
        allowedStatusIds: form.allowedStatusIds ?? [],
        defaultStatusId: form.defaultStatusId ?? null,
      };
      if (queue) {
        return taxonomyApi.queues.update(queue.id, payload);
      }
      return taxonomyApi.queues.create(payload);
    },
    onSuccess: () => {
      toast.success(queue ? "Queue updated" : "Queue created");
      onSaved();
    },
    onError: (err) => {
      toast.error(
        err instanceof ApiError
          ? err.status === 409
            ? "A mailbox/folder is already assigned to another queue"
            : `Save failed (${err.status})`
          : "Save failed",
      );
    },
  });

  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="max-w-lg">
        <DialogHeader>
          <DialogTitle>{queue ? "Edit queue" : "New queue"}</DialogTitle>
          <DialogDescription>
            Queues are the inboxes tickets land in. Name it after a team or workstream.
          </DialogDescription>
        </DialogHeader>
        <div className="max-h-[70vh] space-y-3 overflow-y-auto pr-1 text-sm">
          <Field label="Name">
            <Input
              value={form.name}
              onChange={(e) => setForm((f) => ({ ...f, name: e.target.value }))}
            />
          </Field>
          <Field label="Description">
            <Input
              value={form.description ?? ""}
              onChange={(e) => setForm((f) => ({ ...f, description: e.target.value }))}
            />
          </Field>
          <div className="grid grid-cols-2 gap-3">
            <Field label="Color">
              <div className="flex items-center gap-2">
                <input
                  type="color"
                  value={form.color ?? "#7c7cff"}
                  onChange={(e) => setForm((f) => ({ ...f, color: e.target.value }))}
                  className="h-9 w-12 cursor-pointer rounded-md border border-glass bg-transparent"
                />
                <Input
                  value={form.color ?? ""}
                  onChange={(e) => setForm((f) => ({ ...f, color: e.target.value }))}
                  className="font-mono text-xs"
                />
              </div>
            </Field>
            <Field label="Sort order">
              <Input
                type="number"
                value={form.sortOrder}
                onChange={(e) => setForm((f) => ({ ...f, sortOrder: Number(e.target.value) }))}
              />
            </Field>
          </div>
          <label className="flex items-center gap-2 text-xs text-muted-foreground">
            <input
              type="checkbox"
              checked={form.isActive}
              onChange={(e) => setForm((f) => ({ ...f, isActive: e.target.checked }))}
            />
            Active (visible to agents when creating tickets)
          </label>
          <label className="flex items-center gap-2 text-xs text-muted-foreground">
            <input
              type="checkbox"
              checked={form.aiAssistEnabled ?? true}
              onChange={(e) => setForm((f) => ({ ...f, aiAssistEnabled: e.target.checked }))}
            />
            AI assist (show "Analyze &amp; propose a solution by AI" on tickets in this queue)
          </label>

          {/* v0.0.87 — per-queue override for the ticket hour-limit alert.
              "Inherit" uses the global Settings → Timesheet switch + limit;
              On/Off force it for this queue; the limit override replaces the
              global limit for tickets in this queue only. */}
          <div className="mt-2 space-y-3 rounded-md border border-glass-strong bg-glass p-3">
            <div className="text-xs font-medium uppercase tracking-wider text-muted-foreground/70">
              Hour-limit alert
            </div>
            <Field label="Mode">
              <select
                value={form.timeAlertMode ?? "inherit"}
                onChange={(e) =>
                  setForm((f) => ({
                    ...f,
                    timeAlertMode: e.target.value as "inherit" | "on" | "off",
                  }))
                }
                className="h-9 w-full rounded-md border border-glass bg-glass px-2 text-sm text-foreground outline-none focus-visible:ring-2 focus-visible:ring-ring"
              >
                <option value="inherit">Inherit global setting</option>
                <option value="on">Always on for this queue</option>
                <option value="off">Off for this queue</option>
              </select>
            </Field>
            {form.timeAlertMode !== "off" && (
              <Field label="Limit override (minutes, empty = global)">
                <Input
                  type="number"
                  min={1}
                  value={form.timeAlertThresholdMinutes ?? ""}
                  onChange={(e) => {
                    const v = e.target.value.trim();
                    const n = v === "" ? null : Number.parseInt(v, 10);
                    setForm((f) => ({
                      ...f,
                      timeAlertThresholdMinutes:
                        n !== null && Number.isFinite(n) && n > 0 ? n : null,
                    }));
                  }}
                  placeholder="Use global limit"
                  className="font-mono"
                />
              </Field>
            )}
            <p className="text-[11px] text-muted-foreground/70">
              Controls the per-ticket hour-limit warning for tickets in this
              queue. The queue active on a ticket when it is opened (or its queue
              is changed) decides which rule applies.
            </p>
          </div>

          {/* v0.0.40 polish — status scope. Leave empty to keep the
              current behaviour (all statuses available); pick a subset
              to filter the status dropdown and the trigger set_status
              picker. Default status is the auto-flip target when a
              ticket moves into this queue on a status not in the list. */}
          <div className="mt-2 space-y-3 rounded-md border border-glass-strong bg-glass p-3">
            <div className="text-xs font-medium uppercase tracking-wider text-muted-foreground/70">
              Status scope
            </div>
            <Field label="Allowed statuses (leave empty = all)">
              <div className="flex flex-wrap gap-1.5">
                {(statuses ?? []).filter((s) => s.isActive).map((s) => {
                  const selected = form.allowedStatusIds?.includes(s.id) ?? false;
                  return (
                    <button
                      key={s.id}
                      type="button"
                      onClick={() =>
                        setForm((f) => {
                          const current = f.allowedStatusIds ?? [];
                          const next = selected
                            ? current.filter((id) => id !== s.id)
                            : [...current, s.id];
                          return {
                            ...f,
                            allowedStatusIds: next,
                            // If the current defaultStatusId is no
                            // longer in the (non-empty) list, clear it.
                            defaultStatusId:
                              next.length > 0 && f.defaultStatusId
                                ? next.includes(f.defaultStatusId) ? f.defaultStatusId : null
                                : f.defaultStatusId,
                          };
                        })
                      }
                      className={cn(
                        "rounded-md border px-2 py-0.5 text-[11px] transition",
                        selected
                          ? "border-violet-400/40 bg-violet-400/15 text-violet-200"
                          : "border-glass bg-glass text-muted-foreground hover:bg-glass-hover",
                      )}
                      style={selected ? { color: s.color } : undefined}
                    >
                      {s.name}
                    </button>
                  );
                })}
              </div>
            </Field>
            <Field label="Default status on queue change">
              <Select
                value={form.defaultStatusId ?? "__none__"}
                onValueChange={(v) =>
                  setForm((f) => ({
                    ...f,
                    defaultStatusId: v === "__none__" ? null : v,
                  }))
                }
              >
                <SelectTrigger className="h-9 border-glass bg-glass">
                  <SelectValue placeholder="— no auto-flip —" />
                </SelectTrigger>
                <SelectContent className="border-glass bg-popover/80 backdrop-blur-xl">
                  <SelectItem value="__none__">— no auto-flip —</SelectItem>
                  {(statuses ?? [])
                    .filter((s) => s.isActive)
                    .filter((s) => (form.allowedStatusIds?.length ?? 0) === 0 || form.allowedStatusIds!.includes(s.id))
                    .map((s) => (
                      <SelectItem key={s.id} value={s.id}>
                        {s.name}
                      </SelectItem>
                    ))}
                </SelectContent>
              </Select>
            </Field>
            <p className="text-[11px] text-muted-foreground/70">
              When a ticket moves into this queue (manually or via a trigger) and its current status is not allowed here, the status auto-flips to the default. Leave both empty to disable.
            </p>
          </div>

          <div className="mt-2 space-y-3 rounded-md border border-glass-strong bg-glass p-3">
            <div className="flex items-center justify-between">
              <div className="text-xs font-medium uppercase tracking-wider text-muted-foreground/70">
                Microsoft Graph mailboxes
              </div>
              <Button
                type="button"
                size="sm"
                variant="outline"
                className="h-7 shrink-0 gap-1 border-glass bg-glass px-2 text-xs hover:bg-glass-hover"
                onClick={addRow}
              >
                <Plus className="h-3.5 w-3.5" /> Add mailbox
              </Button>
            </div>

            {rows.length === 0 && (
              <div className="rounded-md border border-dashed border-glass-strong bg-glass px-4 py-5 text-center text-xs text-muted-foreground">
                No inbound mailboxes. Add one to poll mail into this queue.
              </div>
            )}

            {rows.map((row) => {
              const rowFolders = folders[row.key] ?? [];
              const rowLoading = !!foldersLoading[row.key];
              const rowError = foldersError[row.key];
              const hasMailbox = !!row.mailbox.trim();
              const hasFolder = !!row.folderId.trim();
              return (
                <div
                  key={row.key}
                  className="space-y-3 rounded-md border border-glass bg-glass-hover/30 p-3"
                >
                  <div className="flex items-center gap-2">
                    <Mail className="h-4 w-4 shrink-0 text-muted-foreground" />
                    <Input
                      type="email"
                      placeholder="support@company.com"
                      value={row.mailbox}
                      onChange={(e) =>
                        updateRow(row.key, { mailbox: e.target.value })
                      }
                      className="flex-1"
                    />
                    <label className="flex shrink-0 items-center gap-1.5 text-[11px] text-muted-foreground">
                      <Switch
                        checked={row.pollingEnabled}
                        onCheckedChange={(checked) =>
                          updateRow(row.key, { pollingEnabled: checked })
                        }
                        aria-label="Toggle polling for this mailbox"
                      />
                      Polling
                    </label>
                    <Button
                      type="button"
                      size="icon"
                      variant="ghost"
                      className="h-8 w-8 shrink-0 text-muted-foreground hover:text-destructive"
                      onClick={() => removeRow(row.key)}
                      aria-label="Remove mailbox"
                    >
                      <Trash2 className="h-4 w-4" />
                    </Button>
                  </div>

                  <div className="flex items-center gap-2">
                    <Select
                      value={row.folderId || undefined}
                      disabled={!hasMailbox}
                      onValueChange={(v) => {
                        const selected = rowFolders.find((f) => f.id === v);
                        updateRow(row.key, {
                          folderId: v,
                          folderName: selected?.displayName ?? row.folderName,
                        });
                      }}
                    >
                      <SelectTrigger className="h-9 flex-1 border-glass bg-glass transition-colors focus:border-glass-strong focus:bg-glass-strong">
                        <SelectValue placeholder="Select a folder…">
                          {row.folderName || row.folderId || "Select a folder…"}
                        </SelectValue>
                      </SelectTrigger>
                      <SelectContent className="border-glass bg-popover/80 backdrop-blur-xl">
                        {rowFolders.map((f) => (
                          <SelectItem key={f.id} value={f.id}>
                            <span className="flex items-center gap-2">
                              {f.displayName}
                              <span className="text-xs text-muted-foreground">
                                ({f.totalItemCount})
                              </span>
                            </span>
                          </SelectItem>
                        ))}
                        {rowFolders.length === 0 && row.folderId && (
                          <SelectItem value={row.folderId}>
                            {row.folderName || row.folderId}
                          </SelectItem>
                        )}
                        {rowFolders.length === 0 && !row.folderId && (
                          <div className="px-2 py-1.5 text-xs text-muted-foreground">
                            Click "Load folders" to fetch available folders.
                          </div>
                        )}
                      </SelectContent>
                    </Select>
                    <Button
                      type="button"
                      size="sm"
                      variant="outline"
                      className="shrink-0 border-glass bg-glass hover:bg-glass-hover"
                      disabled={!hasMailbox || rowLoading}
                      onClick={() => loadFolders(row)}
                    >
                      {rowLoading ? "Loading…" : "Load folders"}
                    </Button>
                  </div>
                  {rowError && <p className="text-xs text-red-400">{rowError}</p>}
                  {hasMailbox && !hasFolder && (
                    <p className="text-xs text-amber-400">
                      Select a folder for this mailbox.
                    </p>
                  )}
                </div>
              );
            })}

            <Field label="Outbound mailbox address (send-as for replies)">
              <Input
                type="email"
                placeholder="support@company.com"
                value={form.outboundMailboxAddress ?? ""}
                onChange={(e) =>
                  setForm((f) => ({ ...f, outboundMailboxAddress: e.target.value }))
                }
              />
            </Field>

            <p className="text-xs text-muted-foreground">
              Each inbound mailbox is polled from its selected folder by Microsoft
              Graph and routed to this queue. A mailbox/folder can only feed one
              queue. Replies are always sent from the outbound address above.
            </p>
          </div>
        </div>
        <DialogFooter>
          <Button variant="ghost" onClick={onClose}>
            Cancel
          </Button>
          <Button
            onClick={() => save.mutate()}
            disabled={save.isPending || !form.name || !rowsComplete}
          >
            {save.isPending ? "Saving..." : "Save"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

// ---------- Priorities tab ----------

function PrioritiesTab() {
  const qc = useQueryClient();
  const [editing, setEditing] = useState<Priority | null | "new">(null);

  const { data, isLoading } = useQuery({
    queryKey: ["taxonomy", "priorities"],
    queryFn: () => taxonomyApi.priorities.list(),
  });

  const del = useDeleteHandler(
    (id) => taxonomyApi.priorities.remove(id),
    ["taxonomy", "priorities"],
    "Priority",
  );

  return (
    <div className="space-y-4">
      <div className="flex justify-end">
        <Button onClick={() => setEditing("new")}>+ New priority</Button>
      </div>
      {isLoading ? (
        <LoadingSkeleton />
      ) : (
        <TableShell>
          <thead className="text-xs uppercase tracking-wide text-muted-foreground [&_th]:border-b [&_th]:border-glass">
            <tr>
              <th className="px-4 py-3 font-medium">Name</th>
              <th className="px-4 py-3 font-medium">Level</th>
              <th className="px-4 py-3 font-medium">Color</th>
              <th className="px-4 py-3 font-medium">Default</th>
              <th className="px-4 py-3 font-medium">Status</th>
              <th className="px-4 py-3" />
            </tr>
          </thead>
          <tbody>
            {data?.map((p) => (
              <tr key={p.id} className="border-b border-glass hover:bg-glass-hover">
                <td className="px-4 py-3 text-foreground">{p.name}</td>
                <td className="px-4 py-3 text-muted-foreground">{p.level}</td>
                <td className="px-4 py-3">
                  <div className="flex items-center gap-2">
                    <ColorSwatch color={p.color} />
                    <span className="font-mono text-[11px] text-muted-foreground">{p.color}</span>
                  </div>
                </td>
                <td className="px-4 py-3 text-xs text-muted-foreground">
                  {p.isDefault ? "yes" : "—"}
                </td>
                <td className="px-4 py-3">
                  {p.isActive ? (
                    <Badge className="border border-emerald-400/20 bg-emerald-400/10 text-[10px] font-normal text-emerald-200">
                      active
                    </Badge>
                  ) : (
                    <Badge className="border border-glass bg-glass text-[10px] font-normal text-muted-foreground">
                      inactive
                    </Badge>
                  )}
                </td>
                <td className="px-4 py-3 text-right">
                  <Button variant="ghost" size="sm" onClick={() => setEditing(p)}>
                    Edit
                  </Button>
                  <Button
                    variant="ghost"
                    size="sm"
                    disabled={p.isDefault || del.isPending}
                    onClick={() => {
                      if (confirm(`Delete priority "${p.name}"?`)) del.mutate(p.id);
                    }}
                  >
                    Delete
                  </Button>
                </td>
              </tr>
            ))}
            {data && data.length === 0 && (
              <tr>
                <td colSpan={7} className="p-8 text-center text-sm text-muted-foreground">
                  No priorities yet.
                </td>
              </tr>
            )}
          </tbody>
        </TableShell>
      )}

      {editing && (
        <PriorityDialog
          priority={editing === "new" ? null : editing}
          onClose={() => setEditing(null)}
          onSaved={() => {
            qc.invalidateQueries({ queryKey: ["taxonomy", "priorities"] });
            setEditing(null);
          }}
        />
      )}
    </div>
  );
}

function PriorityDialog({
  priority,
  onClose,
  onSaved,
}: {
  priority: Priority | null;
  onClose: () => void;
  onSaved: () => void;
}) {
  const [form, setForm] = useState<PriorityInput>(() => ({
    name: priority?.name ?? "",
    level: priority?.level ?? 20,
    color: priority?.color ?? "#7c7cff",
    icon: priority?.icon ?? "flag",
    sortOrder: priority?.sortOrder ?? 0,
    isActive: priority?.isActive ?? true,
    isDefault: priority?.isDefault ?? false,
  }));

  const save = useMutation({
    mutationFn: async () =>
      priority
        ? taxonomyApi.priorities.update(priority.id, form)
        : taxonomyApi.priorities.create(form),
    onSuccess: () => {
      toast.success(priority ? "Priority updated" : "Priority created");
      onSaved();
    },
    onError: () => toast.error("Save failed"),
  });

  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="max-w-lg">
        <DialogHeader>
          <DialogTitle>{priority ? "Edit priority" : "New priority"}</DialogTitle>
          <DialogDescription>
            Higher <em>level</em> means more severe. Use it for sorting and escalation rules.
          </DialogDescription>
        </DialogHeader>
        <div className="space-y-3 text-sm">
          <Field label="Name">
            <Input
              value={form.name}
              onChange={(e) => setForm((f) => ({ ...f, name: e.target.value }))}
            />
          </Field>
          <div className="grid grid-cols-3 gap-3">
            <Field label="Level">
              <Input
                type="number"
                value={form.level}
                onChange={(e) => setForm((f) => ({ ...f, level: Number(e.target.value) }))}
              />
            </Field>
            <Field label="Sort order">
              <Input
                type="number"
                value={form.sortOrder}
                onChange={(e) => setForm((f) => ({ ...f, sortOrder: Number(e.target.value) }))}
              />
            </Field>
            <Field label="Color">
              <input
                type="color"
                value={form.color ?? "#7c7cff"}
                onChange={(e) => setForm((f) => ({ ...f, color: e.target.value }))}
                className="h-9 w-full cursor-pointer rounded-md border border-glass bg-transparent"
              />
            </Field>
          </div>
          <div className="flex items-center gap-6">
            <label className="flex items-center gap-2 text-xs text-muted-foreground">
              <input
                type="checkbox"
                checked={form.isActive}
                onChange={(e) => setForm((f) => ({ ...f, isActive: e.target.checked }))}
              />
              Active
            </label>
            <label className="flex items-center gap-2 text-xs text-muted-foreground">
              <input
                type="checkbox"
                checked={form.isDefault}
                onChange={(e) => setForm((f) => ({ ...f, isDefault: e.target.checked }))}
              />
              Default priority (pre-selected for new tickets, no row highlight)
            </label>
          </div>
        </div>
        <DialogFooter>
          <Button variant="ghost" onClick={onClose}>
            Cancel
          </Button>
          <Button
            onClick={() => save.mutate()}
            disabled={save.isPending || !form.name}
          >
            {save.isPending ? "Saving..." : "Save"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

// ---------- Statuses tab ----------

const STATE_CATEGORY_COLOR: Record<StatusStateCategory, string> = {
  New: "text-sky-200 border-sky-400/20 bg-sky-400/10",
  Open: "text-indigo-200 border-indigo-400/20 bg-indigo-400/10",
  Pending: "text-amber-200 border-amber-400/20 bg-amber-400/10",
  Resolved: "text-emerald-200 border-emerald-400/20 bg-emerald-400/10",
  Closed: "text-slate-300 border-slate-400/20 bg-slate-400/10",
};

function StatusesTab() {
  const qc = useQueryClient();
  const [editing, setEditing] = useState<Status | null | "new">(null);

  const { data, isLoading } = useQuery({
    queryKey: ["taxonomy", "statuses"],
    queryFn: () => taxonomyApi.statuses.list(),
  });

  const del = useDeleteHandler(
    (id) => taxonomyApi.statuses.remove(id),
    ["taxonomy", "statuses"],
    "Status",
  );

  return (
    <div className="space-y-4">
      <div className="flex justify-end">
        <Button onClick={() => setEditing("new")}>+ New status</Button>
      </div>
      {isLoading ? (
        <LoadingSkeleton />
      ) : (
        <TableShell>
          <thead className="text-xs uppercase tracking-wide text-muted-foreground [&_th]:border-b [&_th]:border-glass">
            <tr>
              <th className="px-4 py-3 font-medium">Name</th>
              <th className="px-4 py-3 font-medium">Semantic</th>
              <th className="px-4 py-3 font-medium">Color</th>
              <th className="px-4 py-3 font-medium">Default</th>
              <th className="px-4 py-3" />
            </tr>
          </thead>
          <tbody>
            {data?.map((s) => (
              <tr key={s.id} className="border-b border-glass hover:bg-glass-hover">
                <td className="px-4 py-3 text-foreground">
                  <div className="flex items-center gap-2">
                    {s.name}
                    {s.isSystem && (
                      <Badge className="border border-glass bg-glass text-[10px] font-normal text-muted-foreground">
                        system
                      </Badge>
                    )}
                  </div>
                </td>
                <td className="px-4 py-3">
                  <Badge
                    className={cn(
                      "border text-[10px] font-normal",
                      STATE_CATEGORY_COLOR[s.stateCategory],
                    )}
                  >
                    {s.stateCategory}
                  </Badge>
                </td>
                <td className="px-4 py-3">
                  <div className="flex items-center gap-2">
                    <ColorSwatch color={s.color} />
                    <span className="font-mono text-[11px] text-muted-foreground">{s.color}</span>
                  </div>
                </td>
                <td className="px-4 py-3 text-xs text-muted-foreground">
                  {s.isDefault ? "yes" : "—"}
                </td>
                <td className="px-4 py-3 text-right">
                  <Button variant="ghost" size="sm" onClick={() => setEditing(s)}>
                    Edit
                  </Button>
                  <Button
                    variant="ghost"
                    size="sm"
                    disabled={s.isSystem || del.isPending}
                    onClick={() => {
                      if (confirm(`Delete status "${s.name}"?`)) del.mutate(s.id);
                    }}
                  >
                    Delete
                  </Button>
                </td>
              </tr>
            ))}
            {data && data.length === 0 && (
              <tr>
                <td colSpan={6} className="p-8 text-center text-sm text-muted-foreground">
                  No statuses yet.
                </td>
              </tr>
            )}
          </tbody>
        </TableShell>
      )}

      {editing && (
        <StatusDialog
          status={editing === "new" ? null : editing}
          onClose={() => setEditing(null)}
          onSaved={() => {
            qc.invalidateQueries({ queryKey: ["taxonomy", "statuses"] });
            setEditing(null);
          }}
        />
      )}
    </div>
  );
}

function StatusDialog({
  status,
  onClose,
  onSaved,
}: {
  status: Status | null;
  onClose: () => void;
  onSaved: () => void;
}) {
  const [form, setForm] = useState<StatusInput>(() => ({
    name: status?.name ?? "",
    stateCategory: status?.stateCategory ?? "Open",
    color: status?.color ?? "#7c7cff",
    icon: status?.icon ?? "circle",
    sortOrder: status?.sortOrder ?? 0,
    isActive: status?.isActive ?? true,
    isDefault: status?.isDefault ?? false,
  }));

  const save = useMutation({
    mutationFn: async () =>
      status
        ? taxonomyApi.statuses.update(status.id, form)
        : taxonomyApi.statuses.create(form),
    onSuccess: () => {
      toast.success(status ? "Status updated" : "Status created");
      onSaved();
    },
    onError: () => toast.error("Save failed"),
  });

  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="max-w-lg">
        <DialogHeader>
          <DialogTitle>{status ? "Edit status" : "New status"}</DialogTitle>
          <DialogDescription>
            Pin each status to a semantic category so SLA and reporting stay correct even
            when you rename the display label.
          </DialogDescription>
        </DialogHeader>
        <div className="space-y-3 text-sm">
          <Field label="Name">
            <Input
              value={form.name}
              onChange={(e) => setForm((f) => ({ ...f, name: e.target.value }))}
            />
          </Field>
          <Field label="Semantic category">
            <select
              value={form.stateCategory}
              onChange={(e) =>
                setForm((f) => ({ ...f, stateCategory: e.target.value as StatusStateCategory }))
              }
              className="h-9 w-full rounded-md border border-glass bg-glass px-2 text-sm text-foreground outline-none focus:border-primary/60"
            >
              {STATE_CATEGORIES.map((c) => (
                <option key={c} value={c} className="bg-background">
                  {c}
                </option>
              ))}
            </select>
          </Field>
          <div className="grid grid-cols-2 gap-3">
            <Field label="Sort order">
              <Input
                type="number"
                value={form.sortOrder}
                onChange={(e) => setForm((f) => ({ ...f, sortOrder: Number(e.target.value) }))}
              />
            </Field>
            <Field label="Color">
              <input
                type="color"
                value={form.color ?? "#7c7cff"}
                onChange={(e) => setForm((f) => ({ ...f, color: e.target.value }))}
                className="h-9 w-full cursor-pointer rounded-md border border-glass bg-transparent"
              />
            </Field>
          </div>
          <div className="flex flex-col gap-2 text-xs text-muted-foreground">
            <label className="flex items-center gap-2">
              <input
                type="checkbox"
                checked={form.isActive}
                onChange={(e) => setForm((f) => ({ ...f, isActive: e.target.checked }))}
              />
              Active
            </label>
            <label className="flex items-center gap-2">
              <input
                type="checkbox"
                checked={form.isDefault}
                onChange={(e) => setForm((f) => ({ ...f, isDefault: e.target.checked }))}
              />
              Default for new tickets (only one status can be the default)
            </label>
          </div>
        </div>
        <DialogFooter>
          <Button variant="ghost" onClick={onClose}>
            Cancel
          </Button>
          <Button
            onClick={() => save.mutate()}
            disabled={save.isPending || !form.name}
          >
            {save.isPending ? "Saving..." : "Save"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

// ---------- Categories tab ----------

function CategoriesTab() {
  const qc = useQueryClient();
  const [editing, setEditing] = useState<Category | null | "new">(null);

  const { data, isLoading } = useQuery({
    queryKey: ["taxonomy", "categories"],
    queryFn: () => taxonomyApi.categories.list(),
  });

  const del = useDeleteHandler(
    (id) => taxonomyApi.categories.remove(id),
    ["taxonomy", "categories"],
    "Category",
  );

  return (
    <div className="space-y-4">
      <div className="flex justify-end">
        <Button onClick={() => setEditing("new")}>+ New category</Button>
      </div>
      {isLoading ? (
        <LoadingSkeleton />
      ) : (
        <TableShell>
          <thead className="text-xs uppercase tracking-wide text-muted-foreground [&_th]:border-b [&_th]:border-glass">
            <tr>
              <th className="px-4 py-3 font-medium">Name</th>
              <th className="px-4 py-3 font-medium">Parent</th>
              <th className="px-4 py-3 font-medium">Order</th>
              <th className="px-4 py-3 font-medium">Status</th>
              <th className="px-4 py-3" />
            </tr>
          </thead>
          <tbody>
            {data?.map((c) => {
              const parent = data.find((x) => x.id === c.parentId);
              return (
                <tr key={c.id} className="border-b border-glass hover:bg-glass-hover">
                  <td className="px-4 py-3 text-foreground">
                    <div className="flex items-center gap-2">
                      {c.name}
                      {c.isSystem && (
                        <Badge className="border border-glass bg-glass text-[10px] font-normal text-muted-foreground">
                          system
                        </Badge>
                      )}
                    </div>
                    {c.description && (
                      <div className="text-xs text-muted-foreground">{c.description}</div>
                    )}
                  </td>
                  <td className="px-4 py-3 text-xs text-muted-foreground">
                    {parent?.name ?? "—"}
                  </td>
                  <td className="px-4 py-3 text-muted-foreground">{c.sortOrder}</td>
                  <td className="px-4 py-3">
                    {c.isActive ? (
                      <Badge className="border border-emerald-400/20 bg-emerald-400/10 text-[10px] font-normal text-emerald-200">
                        active
                      </Badge>
                    ) : (
                      <Badge className="border border-glass bg-glass text-[10px] font-normal text-muted-foreground">
                        inactive
                      </Badge>
                    )}
                  </td>
                  <td className="px-4 py-3 text-right">
                    <Button variant="ghost" size="sm" onClick={() => setEditing(c)}>
                      Edit
                    </Button>
                    <Button
                      variant="ghost"
                      size="sm"
                      disabled={c.isSystem || del.isPending}
                      onClick={() => {
                        if (confirm(`Delete category "${c.name}"?`)) del.mutate(c.id);
                      }}
                    >
                      Delete
                    </Button>
                  </td>
                </tr>
              );
            })}
            {data && data.length === 0 && (
              <tr>
                <td colSpan={6} className="p-8 text-center text-sm text-muted-foreground">
                  No categories yet. Create a top-level category to get started.
                </td>
              </tr>
            )}
          </tbody>
        </TableShell>
      )}

      {editing && (
        <CategoryDialog
          category={editing === "new" ? null : editing}
          allCategories={data ?? []}
          onClose={() => setEditing(null)}
          onSaved={() => {
            qc.invalidateQueries({ queryKey: ["taxonomy", "categories"] });
            setEditing(null);
          }}
        />
      )}
    </div>
  );
}

function CategoryDialog({
  category,
  allCategories,
  onClose,
  onSaved,
}: {
  category: Category | null;
  allCategories: Category[];
  onClose: () => void;
  onSaved: () => void;
}) {
  const [form, setForm] = useState<CategoryInput>(() => ({
    name: category?.name ?? "",
    parentId: category?.parentId ?? null,
    description: category?.description ?? "",
    sortOrder: category?.sortOrder ?? 0,
    isActive: category?.isActive ?? true,
  }));

  const save = useMutation({
    mutationFn: async () =>
      category
        ? taxonomyApi.categories.update(category.id, form)
        : taxonomyApi.categories.create(form),
    onSuccess: () => {
      toast.success(category ? "Category updated" : "Category created");
      onSaved();
    },
    onError: () => toast.error("Save failed"),
  });

  // Exclude self (so a category can't be its own parent). Cycle prevention
  // for deeper chains is enforced server-side.
  const parentOptions = allCategories.filter((c) => c.id !== category?.id);

  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="max-w-lg">
        <DialogHeader>
          <DialogTitle>{category ? "Edit category" : "New category"}</DialogTitle>
          <DialogDescription>
            Categories are the topic tree you classify tickets with. They're optional on
            tickets — admins can keep it flat or build a hierarchy.
          </DialogDescription>
        </DialogHeader>
        <div className="space-y-3 text-sm">
          <Field label="Name">
            <Input
              value={form.name}
              onChange={(e) => setForm((f) => ({ ...f, name: e.target.value }))}
            />
          </Field>
          <Field label="Parent category">
            <select
              value={form.parentId ?? ""}
              onChange={(e) =>
                setForm((f) => ({ ...f, parentId: e.target.value || null }))
              }
              className="h-9 w-full rounded-md border border-glass bg-glass px-2 text-sm text-foreground outline-none focus:border-primary/60"
            >
              <option value="" className="bg-background">
                — top level —
              </option>
              {parentOptions.map((c) => (
                <option key={c.id} value={c.id} className="bg-background">
                  {c.name}
                </option>
              ))}
            </select>
          </Field>
          <Field label="Description">
            <Input
              value={form.description ?? ""}
              onChange={(e) => setForm((f) => ({ ...f, description: e.target.value }))}
            />
          </Field>
          <Field label="Sort order">
            <Input
              type="number"
              value={form.sortOrder}
              onChange={(e) => setForm((f) => ({ ...f, sortOrder: Number(e.target.value) }))}
            />
          </Field>
          <label className="flex items-center gap-2 text-xs text-muted-foreground">
            <input
              type="checkbox"
              checked={form.isActive}
              onChange={(e) => setForm((f) => ({ ...f, isActive: e.target.checked }))}
            />
            Active
          </label>
        </div>
        <DialogFooter>
          <Button variant="ghost" onClick={onClose}>
            Cancel
          </Button>
          <Button
            onClick={() => save.mutate()}
            disabled={save.isPending || !form.name}
          >
            {save.isPending ? "Saving..." : "Save"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

// ---------- Tiny shared bits ----------

function Field({
  label,
  hint,
  children,
}: {
  label: string;
  hint?: string;
  children: React.ReactNode;
}) {
  return (
    <label className="flex flex-col gap-1">
      <span className="text-xs text-muted-foreground">{label}</span>
      {children}
      {hint && <span className="text-[11px] text-muted-foreground/60">{hint}</span>}
    </label>
  );
}

function LoadingSkeleton() {
  return (
    <div className="glass-card space-y-2 p-4">
      {Array.from({ length: 5 }).map((_, i) => (
        <Skeleton key={i} className="h-10 w-full" />
      ))}
    </div>
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

// ---------- Ticket Types tab (v0.0.39) ----------

function TicketTypesTab() {
  const qc = useQueryClient();
  const [editing, setEditing] = useState<TicketType | null | "new">(null);

  const { data, isLoading } = useQuery({
    queryKey: ["taxonomy", "ticket-types"],
    queryFn: () => taxonomyApi.ticketTypes.list(),
  });

  const del = useDeleteHandler(
    (id) => taxonomyApi.ticketTypes.remove(id),
    ["taxonomy", "ticket-types"],
    "Ticket type",
  );

  return (
    <div className="space-y-4">
      <div className="flex justify-end">
        <Button onClick={() => setEditing("new")}>+ New ticket type</Button>
      </div>
      {isLoading ? (
        <LoadingSkeleton />
      ) : (
        <TableShell>
          <thead className="text-xs uppercase tracking-wide text-muted-foreground [&_th]:border-b [&_th]:border-glass">
            <tr>
              <th className="px-4 py-3 font-medium">Label</th>
              <th className="px-4 py-3 font-medium">Code</th>
              <th className="px-4 py-3 font-medium">Icon</th>
              <th className="px-4 py-3 font-medium">Color</th>
              <th className="px-4 py-3 font-medium">Order</th>
              <th className="px-4 py-3 font-medium">Status</th>
              <th className="px-4 py-3" />
            </tr>
          </thead>
          <tbody>
            {data?.map((t) => (
              <tr key={t.id} className="border-b border-glass hover:bg-glass-hover">
                <td className="px-4 py-3 text-foreground">
                  <div className="flex items-center gap-2">
                    {t.label}
                    {t.isSystem && (
                      <Badge className="border border-glass bg-glass text-[10px] font-normal text-muted-foreground">
                        system
                      </Badge>
                    )}
                  </div>
                  {t.description && (
                    <div className="text-xs text-muted-foreground">{t.description}</div>
                  )}
                </td>
                <td className="px-4 py-3">
                  <span className="font-mono text-[11px] text-muted-foreground">{t.code}</span>
                </td>
                <td className="px-4 py-3">
                  <span className="font-mono text-[11px] text-muted-foreground">{t.icon}</span>
                </td>
                <td className="px-4 py-3">
                  <div className="flex items-center gap-2">
                    <ColorSwatch color={t.color} />
                    <span className="font-mono text-[11px] text-muted-foreground">{t.color}</span>
                  </div>
                </td>
                <td className="px-4 py-3 text-muted-foreground">{t.sortOrder}</td>
                <td className="px-4 py-3">
                  {t.isActive ? (
                    <Badge className="border border-emerald-400/20 bg-emerald-400/10 text-[10px] font-normal text-emerald-200">
                      active
                    </Badge>
                  ) : (
                    <Badge className="border border-glass bg-glass text-[10px] font-normal text-muted-foreground">
                      inactive
                    </Badge>
                  )}
                </td>
                <td className="px-4 py-3 text-right">
                  <Button variant="ghost" size="sm" onClick={() => setEditing(t)}>
                    Edit
                  </Button>
                  <Button
                    variant="ghost"
                    size="sm"
                    disabled={del.isPending || t.isSystem}
                    onClick={() => {
                      if (confirm(`Delete ticket type "${t.label}"?`)) del.mutate(t.id);
                    }}
                  >
                    Delete
                  </Button>
                </td>
              </tr>
            ))}
            {data && data.length === 0 && (
              <tr>
                <td colSpan={7} className="p-8 text-center text-sm text-muted-foreground">
                  No ticket types yet.
                </td>
              </tr>
            )}
          </tbody>
        </TableShell>
      )}

      {editing && (
        <TicketTypeDialog
          ticketType={editing === "new" ? null : editing}
          onClose={() => setEditing(null)}
          onSaved={() => {
            qc.invalidateQueries({ queryKey: ["taxonomy", "ticket-types"] });
            setEditing(null);
          }}
        />
      )}
    </div>
  );
}

function TicketTypeDialog({
  ticketType,
  onClose,
  onSaved,
}: {
  ticketType: TicketType | null;
  onClose: () => void;
  onSaved: () => void;
}) {
  const [form, setForm] = useState<TicketTypeInput>(() => ({
    code: ticketType?.code ?? "",
    label: ticketType?.label ?? "",
    description: ticketType?.description ?? "",
    icon: ticketType?.icon ?? "ticket",
    color: ticketType?.color ?? "#7c7cff",
    sortOrder: ticketType?.sortOrder ?? 0,
    isActive: ticketType?.isActive ?? true,
  }));

  const save = useMutation({
    mutationFn: async () => {
      if (ticketType) return taxonomyApi.ticketTypes.update(ticketType.id, form);
      return taxonomyApi.ticketTypes.create(form);
    },
    onSuccess: () => {
      toast.success(ticketType ? "Ticket type updated" : "Ticket type created");
      onSaved();
    },
    onError: (err) => {
      toast.error(err instanceof ApiError ? `Save failed (${err.status})` : "Save failed");
    },
  });

  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="max-w-lg">
        <DialogHeader>
          <DialogTitle>{ticketType ? "Edit ticket type" : "New ticket type"}</DialogTitle>
          <DialogDescription>
            First-class ticket type. Codes are lowercase slugs (e.g. <span className="font-mono">order</span>). Icons are{" "}
            <a
              href="https://lucide.dev/icons/"
              target="_blank"
              rel="noopener noreferrer"
              className="text-primary hover:underline"
            >
              lucide icon names
            </a>{" "}
            (e.g. <span className="font-mono">shopping-cart</span>). The chosen color appears as a badge accent on every ticket of this type.
          </DialogDescription>
        </DialogHeader>
        <div className="space-y-3 text-sm">
          <div className="grid grid-cols-2 gap-3">
            <Field label="Code">
              <Input
                value={form.code}
                placeholder="order"
                disabled={ticketType?.isSystem}
                onChange={(e) => setForm((f) => ({ ...f, code: e.target.value }))}
              />
            </Field>
            <Field label="Label">
              <Input
                value={form.label}
                placeholder="Order"
                onChange={(e) => setForm((f) => ({ ...f, label: e.target.value }))}
              />
            </Field>
          </div>
          <Field label="Description">
            <Input
              value={form.description ?? ""}
              placeholder="Procurement or hardware order request"
              onChange={(e) => setForm((f) => ({ ...f, description: e.target.value }))}
            />
          </Field>
          <div className="grid grid-cols-3 gap-3">
            <Field label="Icon">
              <Input
                value={form.icon ?? ""}
                placeholder="shopping-cart"
                onChange={(e) => setForm((f) => ({ ...f, icon: e.target.value }))}
              />
            </Field>
            <Field label="Color">
              <div className="flex items-center gap-2">
                <input
                  type="color"
                  value={form.color ?? "#7c7cff"}
                  onChange={(e) => setForm((f) => ({ ...f, color: e.target.value }))}
                  className="h-9 w-12 cursor-pointer rounded-md border border-glass bg-transparent"
                />
                <Input
                  value={form.color ?? ""}
                  onChange={(e) => setForm((f) => ({ ...f, color: e.target.value }))}
                  className="font-mono text-xs"
                />
              </div>
            </Field>
            <Field label="Sort order">
              <Input
                type="number"
                value={form.sortOrder}
                onChange={(e) => setForm((f) => ({ ...f, sortOrder: Number(e.target.value) }))}
              />
            </Field>
          </div>
          <label className="flex items-center gap-2 text-xs text-muted-foreground">
            <input
              type="checkbox"
              checked={form.isActive}
              onChange={(e) => setForm((f) => ({ ...f, isActive: e.target.checked }))}
            />
            Active (visible in the "Create linked ticket" picker when bound to a manual trigger)
          </label>
        </div>
        <DialogFooter>
          <Button variant="ghost" onClick={onClose}>
            Cancel
          </Button>
          <Button onClick={() => save.mutate()} disabled={save.isPending}>
            {save.isPending ? "Saving…" : ticketType ? "Save" : "Create"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
