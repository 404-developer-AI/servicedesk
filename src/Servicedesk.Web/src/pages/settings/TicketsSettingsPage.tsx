import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import {
  ApiError,
  taxonomyApi,
  STATE_CATEGORIES,
  type Category,
  type CategoryInput,
  type Priority,
  type PriorityInput,
  type Queue,
  type QueueInput,
  type Status,
  type StatusInput,
  type StatusStateCategory,
} from "@/lib/api";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
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

type TabKey = "queues" | "priorities" | "statuses" | "categories";

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
        <Badge className="border border-white/10 bg-white/[0.05] text-xs font-normal text-muted-foreground">
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
                ? "bg-white/[0.08] text-foreground shadow-[inset_0_0_0_1px_rgba(255,255,255,0.08)]"
                : "text-muted-foreground hover:bg-white/[0.04] hover:text-foreground",
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
      </div>
    </div>
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
      className="inline-block h-4 w-4 rounded-sm border border-white/10"
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
          <thead className="text-xs uppercase tracking-wide text-muted-foreground [&_th]:border-b [&_th]:border-white/10">
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
              <tr key={q.id} className="border-b border-white/5 hover:bg-white/[0.03]">
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
                    <Badge className="border border-white/10 bg-white/[0.05] text-[10px] font-normal text-muted-foreground">
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
  }));

  const save = useMutation({
    mutationFn: async () => {
      if (queue) {
        return taxonomyApi.queues.update(queue.id, form);
      }
      return taxonomyApi.queues.create(form);
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
                  className="h-9 w-12 cursor-pointer rounded-md border border-white/10 bg-transparent"
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
          <thead className="text-xs uppercase tracking-wide text-muted-foreground [&_th]:border-b [&_th]:border-white/10">
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
              <tr key={p.id} className="border-b border-white/5 hover:bg-white/[0.03]">
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
                    <Badge className="border border-white/10 bg-white/[0.05] text-[10px] font-normal text-muted-foreground">
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
                className="h-9 w-full cursor-pointer rounded-md border border-white/10 bg-transparent"
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
          <thead className="text-xs uppercase tracking-wide text-muted-foreground [&_th]:border-b [&_th]:border-white/10">
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
              <tr key={s.id} className="border-b border-white/5 hover:bg-white/[0.03]">
                <td className="px-4 py-3 text-foreground">
                  <div className="flex items-center gap-2">
                    {s.name}
                    {s.isSystem && (
                      <Badge className="border border-white/10 bg-white/[0.05] text-[10px] font-normal text-muted-foreground">
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
              className="h-9 w-full rounded-md border border-white/10 bg-white/[0.04] px-2 text-sm text-foreground outline-none focus:border-primary/60"
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
                className="h-9 w-full cursor-pointer rounded-md border border-white/10 bg-transparent"
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
          <thead className="text-xs uppercase tracking-wide text-muted-foreground [&_th]:border-b [&_th]:border-white/10">
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
                <tr key={c.id} className="border-b border-white/5 hover:bg-white/[0.03]">
                  <td className="px-4 py-3 text-foreground">
                    <div className="flex items-center gap-2">
                      {c.name}
                      {c.isSystem && (
                        <Badge className="border border-white/10 bg-white/[0.05] text-[10px] font-normal text-muted-foreground">
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
                      <Badge className="border border-white/10 bg-white/[0.05] text-[10px] font-normal text-muted-foreground">
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
              className="h-9 w-full rounded-md border border-white/10 bg-white/[0.04] px-2 text-sm text-foreground outline-none focus:border-primary/60"
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
