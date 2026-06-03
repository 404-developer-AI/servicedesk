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

type TabKey = "queues" | "priorities" | "statuses" | "categories" | "types" | "iso27001" | "warnings" | "general";

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
    key: "general",
    label: "General",
    description:
      "Ticket-wide options: how a ticket reference reads when copied, shown in mail subjects, and pasted into search.",
  },
];

export function TicketsSettingsPage() {
  const [tab, setTab] = useState<TabKey>("queues");
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
    inboundMailboxAddress: queue?.inboundMailboxAddress ?? "",
    outboundMailboxAddress: queue?.outboundMailboxAddress ?? "",
    inboundFolderId: queue?.inboundFolderId ?? "",
    inboundFolderName: queue?.inboundFolderName ?? "",
    allowedStatusIds: queue?.allowedStatusIds ?? [],
    defaultStatusId: queue?.defaultStatusId ?? null,
  }));

  // v0.0.40 polish — load every status so the "Status scope" block
  // can offer the multi-select. Cached + reused with the existing key.
  const { data: statuses } = useQuery({
    queryKey: ["taxonomy", "statuses"],
    queryFn: () => taxonomyApi.statuses.list(),
    staleTime: 60_000,
  });

  const [folders, setFolders] = useState<GraphMailFolder[]>([]);
  const [foldersLoading, setFoldersLoading] = useState(false);
  const [foldersError, setFoldersError] = useState<string | null>(null);

  const loadFolders = async () => {
    const mailbox = form.inboundMailboxAddress?.trim();
    if (!mailbox) return;
    setFoldersLoading(true);
    setFoldersError(null);
    try {
      const result = await graphAdminApi.listFolders(mailbox);
      if (Array.isArray(result)) {
        setFolders(result);
      } else {
        setFoldersError((result as { error?: string }).error ?? "Unknown error");
      }
    } catch (err) {
      setFoldersError(err instanceof ApiError ? `Failed (${err.status})` : "Failed to load folders");
    } finally {
      setFoldersLoading(false);
    }
  };

  const hasMailbox = !!form.inboundMailboxAddress?.trim();
  const hasFolder = !!form.inboundFolderId?.trim();

  const save = useMutation({
    mutationFn: async () => {
      const payload: QueueInput = {
        ...form,
        inboundMailboxAddress: form.inboundMailboxAddress?.trim() || null,
        outboundMailboxAddress: form.outboundMailboxAddress?.trim() || null,
        inboundFolderId: form.inboundFolderId?.trim() || null,
        inboundFolderName: form.inboundFolderName?.trim() || null,
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
      toast.error(err instanceof ApiError ? `Save failed (${err.status})` : "Save failed");
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
        <div className="space-y-3 text-sm">
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
            <div className="text-xs font-medium uppercase tracking-wider text-muted-foreground/70">
              Microsoft Graph mailbox
            </div>
            <Field label="Inbound mailbox address">
              <Input
                type="email"
                placeholder="support@company.com"
                value={form.inboundMailboxAddress ?? ""}
                onChange={(e) =>
                  setForm((f) => ({ ...f, inboundMailboxAddress: e.target.value }))
                }
              />
            </Field>
            <Field label="Outbound mailbox address (not used yet)">
              <Input
                type="email"
                placeholder="support@company.com"
                value={form.outboundMailboxAddress ?? ""}
                onChange={(e) =>
                  setForm((f) => ({ ...f, outboundMailboxAddress: e.target.value }))
                }
              />
            </Field>

            <Field label="Inbound folder">
              <div className="flex items-center gap-2">
                <Select
                  value={form.inboundFolderId || undefined}
                  disabled={!hasMailbox}
                  onValueChange={(v) => {
                    const selected = folders.find((f) => f.id === v);
                    setForm((f) => ({
                      ...f,
                      inboundFolderId: v,
                      inboundFolderName: selected?.displayName ?? "",
                    }));
                  }}
                >
                  <SelectTrigger className="h-9 flex-1 border-glass bg-glass focus:border-glass-strong focus:bg-glass-strong transition-colors">
                    <SelectValue
                      placeholder="Select a folder…"
                    >
                      {form.inboundFolderName || form.inboundFolderId || "Select a folder…"}
                    </SelectValue>
                  </SelectTrigger>
                  <SelectContent className="border-glass bg-popover/80 backdrop-blur-xl">
                    {folders.map((f) => (
                      <SelectItem key={f.id} value={f.id}>
                        <span className="flex items-center gap-2">
                          {f.displayName}
                          <span className="text-xs text-muted-foreground">
                            ({f.totalItemCount})
                          </span>
                        </span>
                      </SelectItem>
                    ))}
                    {folders.length === 0 && form.inboundFolderId && (
                      <SelectItem value={form.inboundFolderId}>
                        {form.inboundFolderName || form.inboundFolderId}
                      </SelectItem>
                    )}
                    {folders.length === 0 && !form.inboundFolderId && (
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
                  disabled={!hasMailbox || foldersLoading}
                  onClick={loadFolders}
                >
                  {foldersLoading ? "Loading…" : "Load folders"}
                </Button>
              </div>
              {foldersError && (
                <p className="mt-1 text-xs text-red-400">{foldersError}</p>
              )}
              {hasMailbox && !hasFolder && (
                <p className="mt-1 text-xs text-amber-400">
                  A folder must be selected when an inbound mailbox is configured.
                </p>
              )}
            </Field>

            <p className="text-xs text-muted-foreground">
              Mail sent to the inbound address is polled from the selected folder by Microsoft Graph and routed to this queue.
              Outbound is reserved for per-queue send-as replies in a later release.
            </p>
          </div>
        </div>
        <DialogFooter>
          <Button variant="ghost" onClick={onClose}>
            Cancel
          </Button>
          <Button
            onClick={() => save.mutate()}
            disabled={save.isPending || !form.name || (hasMailbox && !hasFolder)}
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
