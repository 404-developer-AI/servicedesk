import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Plus, Pencil, Trash2, ArrowLeft, Loader2, Search } from "lucide-react";
import {
  Sheet,
  SheetContent,
  SheetHeader,
  SheetTitle,
  SheetDescription,
} from "@/components/ui/sheet";
import { Input } from "@/components/ui/input";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/utils";
import { toast } from "sonner";
import {
  statisticsApi,
  userApi,
  type StatisticMetricDescriptor,
  type StatisticTileInput,
  type AgentUser,
} from "@/lib/ticket-api";

const PERIODS = [
  { value: "day", label: "Day" },
  { value: "week", label: "Week" },
  { value: "month", label: "Month" },
  { value: "year", label: "Year" },
];
const CHART_LABELS: Record<string, string> = { kpi: "KPI number", bar: "Bar chart" };
const GROUPING_LABELS: Record<string, string> = {
  none: "Total (no grouping)",
  task: "By task",
  time: "Over time",
};
const SCOPES = [
  { value: "viewer_self", label: "Each viewer's own figures" },
  { value: "user", label: "A specific technician" },
  { value: "team", label: "Whole team" },
];

type FormState = {
  title: string;
  metricKey: string;
  chartType: string;
  period: string;
  grouping: string;
  scope: string;
  scopeUserId: string | null;
  assignedUserIds: string[];
};

function blankForm(metrics: StatisticMetricDescriptor[]): FormState {
  const m = metrics[0];
  return {
    title: "",
    metricKey: m?.key ?? "",
    chartType: m?.chartTypes[0] ?? "kpi",
    period: "month",
    grouping: m?.groupings[0] ?? "none",
    scope: "viewer_self",
    scopeUserId: null,
    assignedUserIds: [],
  };
}

export function StatisticsManageSheet({
  open,
  onOpenChange,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
}) {
  const qc = useQueryClient();
  const [view, setView] = React.useState<"list" | "form">("list");
  const [editingId, setEditingId] = React.useState<string | null>(null);

  const catalogue = useQuery({
    queryKey: ["statistics", "catalogue"],
    queryFn: () => statisticsApi.catalogue(),
    enabled: open,
  });
  const list = useQuery({
    queryKey: ["statistics", "manage-list"],
    queryFn: () => statisticsApi.manageList(),
    enabled: open,
  });
  const agents = useQuery({
    queryKey: ["statistics", "agents"],
    queryFn: () => userApi.searchAgents("", 200),
    enabled: open,
  });

  const metrics = catalogue.data ?? [];
  const [form, setForm] = React.useState<FormState>(blankForm([]));

  React.useEffect(() => {
    // Reset to the list whenever the sheet is re-opened.
    if (open) {
      setView("list");
      setEditingId(null);
    }
  }, [open]);

  function patch(p: Partial<FormState>) {
    setForm((f) => ({ ...f, ...p }));
  }

  function onMetricChange(key: string) {
    const m = metrics.find((x) => x.key === key);
    patch({
      metricKey: key,
      chartType: m?.chartTypes[0] ?? "kpi",
      grouping: m?.groupings.includes(form.grouping) ? form.grouping : m?.groupings[0] ?? "none",
    });
  }

  function startCreate() {
    setForm(blankForm(metrics));
    setEditingId(null);
    setView("form");
  }

  async function startEdit(id: string) {
    try {
      const detail = await statisticsApi.manageGet(id);
      setForm({
        title: detail.tile.title,
        metricKey: detail.tile.metricKey,
        chartType: detail.tile.chartType,
        period: detail.tile.period,
        grouping: detail.tile.grouping,
        scope: detail.tile.scope,
        scopeUserId: detail.tile.scopeUserId,
        assignedUserIds: detail.assignedUserIds,
      });
      setEditingId(id);
      setView("form");
    } catch {
      toast.error("Could not load that tile.");
    }
  }

  const saveMutation = useMutation({
    mutationFn: async () => {
      const input: StatisticTileInput = {
        title: form.title.trim(),
        metricKey: form.metricKey,
        chartType: form.chartType,
        period: form.period,
        grouping: form.grouping,
        scope: form.scope,
        scopeUserId: form.scope === "user" ? form.scopeUserId : null,
      };
      const tile = editingId
        ? await statisticsApi.update(editingId, input)
        : await statisticsApi.create(input);
      await statisticsApi.setAssignments(tile.id, form.assignedUserIds);
      return tile;
    },
    onSuccess: () => {
      toast.success(editingId ? "Tile updated" : "Tile created");
      qc.invalidateQueries({ queryKey: ["statistics", "manage-list"] });
      qc.invalidateQueries({ queryKey: ["statistics", "assigned"] });
      qc.invalidateQueries({ queryKey: ["statistics", "tile-data"] });
      setView("list");
    },
    onError: (e: unknown) => {
      const msg =
        e && typeof e === "object" && "body" in e
          ? ((e as { body?: { errors?: string[] } }).body?.errors?.join(" ") ?? null)
          : null;
      toast.error(msg ?? "Could not save the tile.");
    },
  });

  const deleteMutation = useMutation({
    mutationFn: (id: string) => statisticsApi.remove(id),
    onSuccess: () => {
      toast.success("Tile deleted");
      qc.invalidateQueries({ queryKey: ["statistics", "manage-list"] });
      qc.invalidateQueries({ queryKey: ["statistics", "assigned"] });
    },
    onError: () => toast.error("Could not delete the tile."),
  });

  const activeMetric = metrics.find((m) => m.key === form.metricKey);
  const titleValid = form.title.trim().length > 0;
  const scopeUserValid = form.scope !== "user" || !!form.scopeUserId;
  const canSave = titleValid && !!form.metricKey && scopeUserValid && !saveMutation.isPending;

  return (
    <Sheet open={open} onOpenChange={onOpenChange}>
      <SheetContent className="flex w-full flex-col gap-0 sm:max-w-lg">
        <SheetHeader>
          <SheetTitle className="flex items-center gap-2">
            {view === "form" && (
              <button
                type="button"
                onClick={() => setView("list")}
                className="rounded p-1 text-muted-foreground hover:bg-glass-hover"
                aria-label="Back to list"
              >
                <ArrowLeft className="h-4 w-4" />
              </button>
            )}
            {view === "list" ? "Manage statistics tiles" : editingId ? "Edit tile" : "New tile"}
          </SheetTitle>
          <SheetDescription>
            {view === "list"
              ? "Build tiles and assign them to agents with statistics access."
              : "Pick what the tile shows, then who can see it."}
          </SheetDescription>
        </SheetHeader>

        <div className="mt-4 flex-1 overflow-y-auto pr-1">
          {view === "list" ? (
            <TileList
              loading={list.isLoading}
              tiles={list.data ?? []}
              onCreate={startCreate}
              onEdit={startEdit}
              onDelete={(id) => deleteMutation.mutate(id)}
            />
          ) : (
            <TileForm
              form={form}
              metrics={metrics}
              activeMetric={activeMetric}
              agents={agents.data ?? []}
              patch={patch}
              onMetricChange={onMetricChange}
            />
          )}
        </div>

        {view === "form" && (
          <div className="mt-3 flex justify-end gap-2 border-t border-glass pt-3">
            <Button variant="ghost" onClick={() => setView("list")}>
              Cancel
            </Button>
            <Button disabled={!canSave} onClick={() => saveMutation.mutate()}>
              {saveMutation.isPending && <Loader2 className="mr-1 h-4 w-4 animate-spin" />}
              {editingId ? "Save changes" : "Create tile"}
            </Button>
          </div>
        )}
      </SheetContent>
    </Sheet>
  );
}

function TileList({
  loading,
  tiles,
  onCreate,
  onEdit,
  onDelete,
}: {
  loading: boolean;
  tiles: import("@/lib/ticket-api").StatisticTileSummary[];
  onCreate: () => void;
  onEdit: (id: string) => void;
  onDelete: (id: string) => void;
}) {
  return (
    <div className="flex flex-col gap-2">
      <Button onClick={onCreate} className="self-start">
        <Plus className="mr-1 h-4 w-4" /> New tile
      </Button>
      {loading ? (
        <div className="py-8 text-center text-sm text-muted-foreground">Loading…</div>
      ) : tiles.length === 0 ? (
        <div className="py-8 text-center text-sm text-muted-foreground">
          No tiles yet. Create your first one.
        </div>
      ) : (
        tiles.map((t) => (
          <div
            key={t.id}
            className="flex items-center justify-between gap-2 rounded-lg border border-glass bg-glass px-3 py-2"
          >
            <div className="min-w-0">
              <div className="truncate text-sm font-medium text-foreground">{t.title}</div>
              <div className="truncate text-xs text-muted-foreground">
                {GROUPING_LABELS[t.grouping] ?? t.grouping} · {t.period} ·{" "}
                {t.scope === "user"
                  ? (t.scopeUserEmail ?? "technician")
                  : t.scope === "team"
                    ? "team"
                    : "each viewer"}{" "}
                · {t.assignedCount} assigned
              </div>
            </div>
            <div className="flex shrink-0 items-center gap-1">
              <button
                type="button"
                onClick={() => onEdit(t.id)}
                className="rounded p-1.5 text-muted-foreground hover:bg-glass-hover hover:text-foreground"
                aria-label="Edit"
              >
                <Pencil className="h-3.5 w-3.5" />
              </button>
              <button
                type="button"
                onClick={() => {
                  if (window.confirm(`Delete "${t.title}"? This cannot be undone.`)) onDelete(t.id);
                }}
                className="rounded p-1.5 text-muted-foreground hover:bg-destructive/10 hover:text-destructive"
                aria-label="Delete"
              >
                <Trash2 className="h-3.5 w-3.5" />
              </button>
            </div>
          </div>
        ))
      )}
    </div>
  );
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <label className="flex flex-col gap-1.5">
      <span className="text-xs font-medium text-muted-foreground">{label}</span>
      {children}
    </label>
  );
}

function TileForm({
  form,
  metrics,
  activeMetric,
  agents,
  patch,
  onMetricChange,
}: {
  form: FormState;
  metrics: StatisticMetricDescriptor[];
  activeMetric: StatisticMetricDescriptor | undefined;
  agents: AgentUser[];
  patch: (p: Partial<FormState>) => void;
  onMetricChange: (key: string) => void;
}) {
  const [agentSearch, setAgentSearch] = React.useState("");
  const filteredAgents = agents.filter((a) =>
    a.email.toLowerCase().includes(agentSearch.toLowerCase()),
  );

  function toggleAssignee(id: string) {
    patch({
      assignedUserIds: form.assignedUserIds.includes(id)
        ? form.assignedUserIds.filter((x) => x !== id)
        : [...form.assignedUserIds, id],
    });
  }

  return (
    <div className="flex flex-col gap-4">
      <Field label="Title">
        <Input
          value={form.title}
          onChange={(e) => patch({ title: e.target.value })}
          placeholder="e.g. My hours this month"
          maxLength={120}
        />
      </Field>

      <Field label="Metric">
        <Select value={form.metricKey} onValueChange={onMetricChange}>
          <SelectTrigger>
            <SelectValue placeholder="Pick a metric" />
          </SelectTrigger>
          <SelectContent>
            {metrics.map((m) => (
              <SelectItem key={m.key} value={m.key}>
                {m.label}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
      </Field>

      <div className="grid grid-cols-2 gap-3">
        <Field label="Chart">
          <Select value={form.chartType} onValueChange={(v) => patch({ chartType: v })}>
            <SelectTrigger>
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {(activeMetric?.chartTypes ?? []).map((c) => (
                <SelectItem key={c} value={c}>
                  {CHART_LABELS[c] ?? c}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </Field>

        <Field label="Period">
          <Select value={form.period} onValueChange={(v) => patch({ period: v })}>
            <SelectTrigger>
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {PERIODS.map((p) => (
                <SelectItem key={p.value} value={p.value}>
                  {p.label}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </Field>
      </div>

      <Field label="Grouping">
        <Select value={form.grouping} onValueChange={(v) => patch({ grouping: v })}>
          <SelectTrigger>
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            {(activeMetric?.groupings ?? []).map((g) => (
              <SelectItem key={g} value={g}>
                {GROUPING_LABELS[g] ?? g}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
      </Field>

      <Field label="Scope (whose data)">
        <Select value={form.scope} onValueChange={(v) => patch({ scope: v, scopeUserId: null })}>
          <SelectTrigger>
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            {SCOPES.map((s) => (
              <SelectItem key={s.value} value={s.value}>
                {s.label}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
      </Field>

      {form.scope === "user" && (
        <Field label="Technician">
          <Select
            value={form.scopeUserId ?? ""}
            onValueChange={(v) => patch({ scopeUserId: v })}
          >
            <SelectTrigger>
              <SelectValue placeholder="Pick a technician" />
            </SelectTrigger>
            <SelectContent>
              {agents.map((a) => (
                <SelectItem key={a.id} value={a.id}>
                  {a.email}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </Field>
      )}

      <div className="flex flex-col gap-2">
        <span className="text-xs font-medium text-muted-foreground">
          Assign to ({form.assignedUserIds.length})
        </span>
        <div className="relative">
          <Search className="absolute left-2.5 top-2.5 h-3.5 w-3.5 text-muted-foreground" />
          <Input
            value={agentSearch}
            onChange={(e) => setAgentSearch(e.target.value)}
            placeholder="Search agents…"
            className="pl-8"
          />
        </div>
        <div className="max-h-52 overflow-y-auto rounded-lg border border-glass">
          {filteredAgents.length === 0 ? (
            <div className="py-4 text-center text-xs text-muted-foreground">No agents found.</div>
          ) : (
            filteredAgents.map((a) => {
              const checked = form.assignedUserIds.includes(a.id);
              return (
                <label
                  key={a.id}
                  className={cn(
                    "flex cursor-pointer items-center gap-2.5 px-3 py-1.5 text-sm transition-colors hover:bg-glass-hover",
                    checked ? "text-foreground" : "text-muted-foreground",
                  )}
                >
                  <input
                    type="checkbox"
                    checked={checked}
                    onChange={() => toggleAssignee(a.id)}
                    className="rounded border-glass-strong bg-glass accent-primary"
                  />
                  <span className="truncate">{a.email}</span>
                </label>
              );
            })
          )}
        </div>
      </div>
    </div>
  );
}
