import { useEffect, useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import {
  ArrowDown,
  ArrowUp,
  ChevronLeft,
  Columns3,
  GripVertical,
  Package,
  RefreshCw,
  Search,
} from "lucide-react";
import { Link } from "@tanstack/react-router";
import {
  DndContext,
  PointerSensor,
  closestCenter,
  useSensor,
  useSensors,
  type DragEndEvent,
} from "@dnd-kit/core";
import {
  SortableContext,
  arrayMove,
  useSortable,
  verticalListSortingStrategy,
} from "@dnd-kit/sortable";
import { CSS } from "@dnd-kit/utilities";
import { articlesApi, preferencesApi, type AdsolutArticle } from "@/lib/api";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { cn } from "@/lib/utils";

const PAGE_SIZE = 50;
const ARTICLES_KEY = ["contract-articles", "list"] as const;
// Per-agent column config (visibility + order) — persisted server-side via the
// generic workspace-preferences store, so it follows the agent across devices.
const COLUMNS_PREF_KEY = "workspace:contract-articles-columns";

type SortDir = "asc" | "desc";

// ---- column model -----------------------------------------------------

type ColId = "code" | "name" | "active";

type ColDef = {
  id: ColId;
  label: string;
  align: "left" | "right";
  defaultVisible: boolean;
  /** Server sort key — present = the header is clickable to sort. */
  sortKey?: string;
  render: (a: AdsolutArticle) => React.ReactNode;
};

const ALL_COLUMNS: ColDef[] = [
  {
    id: "code",
    label: "Code",
    align: "left",
    defaultVisible: true,
    sortKey: "code",
    render: (a) => <span className="font-mono text-xs text-foreground">{a.code ?? "—"}</span>,
  },
  {
    id: "name",
    label: "Name",
    align: "left",
    defaultVisible: true,
    sortKey: "name",
    render: (a) => <span className="text-foreground">{a.name || a.code || "—"}</span>,
  },
  {
    id: "active",
    label: "Active",
    align: "left",
    defaultVisible: true,
    sortKey: "active",
    render: (a) =>
      a.active ? (
        <span className="inline-flex items-center rounded-full border border-emerald-400/30 bg-emerald-400/10 px-2 py-0.5 text-[11px] text-emerald-300">
          Active
        </span>
      ) : (
        <span className="inline-flex items-center rounded-full border border-glass-strong bg-glass px-2 py-0.5 text-[11px] text-muted-foreground">
          Inactive
        </span>
      ),
  },
];

const COL_LABELS: Record<ColId, string> = Object.fromEntries(
  ALL_COLUMNS.map((c) => [c.id, c.label]),
) as Record<ColId, string>;

type ColConfig = { id: ColId; visible: boolean };

const DEFAULT_CONFIG: ColConfig[] = ALL_COLUMNS.map((c) => ({ id: c.id, visible: c.defaultVisible }));

/// Merge a saved config with ALL_COLUMNS: keep known ids in their saved order,
/// drop unknown ids, and append any columns added since the config was saved
/// (using each column's defaultVisible) so a new column never silently breaks.
function mergeConfig(raw: string | undefined): ColConfig[] {
  if (!raw) return DEFAULT_CONFIG;
  let saved: ColConfig[];
  try {
    const parsed = JSON.parse(raw) as ColConfig[];
    if (!Array.isArray(parsed)) return DEFAULT_CONFIG;
    saved = parsed.filter((c) => ALL_COLUMNS.some((d) => d.id === c.id));
  } catch {
    return DEFAULT_CONFIG;
  }
  const seen = new Set(saved.map((c) => c.id));
  const merged = saved.map((c) => ({ id: c.id, visible: c.visible !== false }));
  for (const d of ALL_COLUMNS) {
    if (!seen.has(d.id)) merged.push({ id: d.id, visible: d.defaultVisible });
  }
  return merged.length > 0 ? merged : DEFAULT_CONFIG;
}

// ---- main page --------------------------------------------------------

/// Contract Articles list (Contracts → Contract Articles). Mounted only when the
/// user has the `contracts_enabled` feature flag (route gate + backend
/// RequireAgent + in-handler flag check). Lists the mirrored Adsolut article
/// catalogue with per-agent column visibility/order, server-side sorting, an
/// active-only filter, a global "Sync articles" trigger and a per-row resync.
export function ContractArticlesPage() {
  const [search, setSearch] = useState("");
  const [page, setPage] = useState(1);
  const [activeOnly, setActiveOnly] = useState(false);
  const [sortKey, setSortKey] = useState("code");
  const [sortDir, setSortDir] = useState<SortDir>("asc");

  // Per-agent column config — load once from the workspace prefs, then own it
  // locally and persist on every change.
  const wsQuery = useQuery({
    queryKey: ["preferences", "workspace"],
    queryFn: () => preferencesApi.getWorkspace(),
    staleTime: 60_000,
  });
  const [config, setConfig] = useState<ColConfig[] | null>(null);
  useEffect(() => {
    if (config !== null || wsQuery.data === undefined) return;
    setConfig(mergeConfig(wsQuery.data[COLUMNS_PREF_KEY]));
  }, [wsQuery.data, config]);
  const effectiveConfig = config ?? DEFAULT_CONFIG;

  const persistConfig = (next: ColConfig[]) => {
    setConfig(next);
    preferencesApi.saveWorkspace([{ key: COLUMNS_PREF_KEY, value: JSON.stringify(next) }]).catch(() => {
      /* best-effort */
    });
  };

  const visibleColumns = useMemo(
    () =>
      effectiveConfig
        .filter((c) => c.visible)
        .map((c) => ALL_COLUMNS.find((d) => d.id === c.id))
        .filter((d): d is ColDef => d !== undefined),
    [effectiveConfig],
  );

  const list = useQuery({
    queryKey: [...ARTICLES_KEY, search, page, sortKey, sortDir, activeOnly],
    queryFn: () => articlesApi.list(search, page, PAGE_SIZE, sortKey, sortDir, activeOnly),
  });

  const sync = useMutation({
    mutationFn: () => articlesApi.sync(),
    onSuccess: () => toast.success("Articles sync started — refresh in a moment for new data."),
    onError: () => toast.error("Could not start the articles sync. Is the Adsolut Articles pull enabled?"),
  });

  const total = list.data?.total ?? 0;
  const totalPages = Math.max(1, Math.ceil(total / PAGE_SIZE));
  const pageItems = list.data?.items ?? [];

  const onSort = (key: string) => {
    if (sortKey === key) {
      setSortDir((d) => (d === "asc" ? "desc" : "asc"));
    } else {
      setSortKey(key);
      setSortDir("asc");
    }
    setPage(1);
  };

  return (
    <div className="flex flex-1 flex-col gap-4 p-4 sm:p-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div className="flex items-center gap-3">
          <Link
            to="/contracts"
            className="flex h-10 w-10 items-center justify-center rounded-full border border-glass bg-glass text-muted-foreground transition-colors hover:text-foreground"
            aria-label="Back to Contracts"
          >
            <ChevronLeft className="h-4.5 w-4.5" />
          </Link>
          <div>
            <h1 className="text-display-md font-semibold text-foreground">Contract Articles</h1>
            <p className="text-xs text-muted-foreground">
              {total.toLocaleString()} articles mirrored from Adsolut
            </p>
          </div>
        </div>
        <div className="flex items-center gap-2">
          <div className="relative w-full max-w-xs">
            <Search className="absolute left-3 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-muted-foreground" />
            <Input
              value={search}
              onChange={(e) => {
                setSearch(e.target.value);
                setPage(1);
              }}
              placeholder="Search code, name…"
              className="pl-9"
            />
          </div>
          <Button
            size="sm"
            variant="ghost"
            className={cn("gap-1.5", activeOnly && "text-foreground")}
            onClick={() => {
              setActiveOnly((v) => !v);
              setPage(1);
            }}
          >
            <input
              type="checkbox"
              checked={activeOnly}
              readOnly
              className="pointer-events-none h-3.5 w-3.5 rounded border border-glass-strong bg-glass accent-purple-400"
            />
            Active only
          </Button>
          <ColumnPicker config={effectiveConfig} onChange={persistConfig} />
          <Button
            size="sm"
            variant="ghost"
            className="gap-1.5"
            onClick={() => sync.mutate()}
            disabled={sync.isPending}
          >
            <RefreshCw className={cn("h-4 w-4", sync.isPending && "animate-spin")} />
            Sync articles
          </Button>
        </div>
      </div>

      <div className="glass-panel flex-1 overflow-hidden">
        {list.isLoading ? (
          <div className="space-y-2 p-4">
            <Skeleton className="h-10 w-full" />
            <Skeleton className="h-10 w-full" />
            <Skeleton className="h-10 w-full" />
          </div>
        ) : list.isError ? (
          <div className="p-6 text-sm text-rose-300">
            Could not load articles. Refresh or check the integration status.
          </div>
        ) : pageItems.length === 0 ? (
          <div className="flex flex-col items-center justify-center gap-2 p-12 text-center">
            <Package className="h-6 w-6 text-muted-foreground" />
            <p className="text-sm text-muted-foreground">
              {search.trim()
                ? "No articles match your search."
                : "No articles mirrored yet. An admin enables the pull under Settings → Integrations → Adsolut."}
            </p>
          </div>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b border-glass text-left text-[11px] uppercase tracking-wider text-muted-foreground/70">
                  {visibleColumns.map((col) => {
                    const active = col.sortKey && sortKey === col.sortKey;
                    return (
                      <th
                        key={col.id}
                        className={cn(
                          "px-3 py-2.5 font-medium",
                          col.align === "right" && "text-right",
                          col.sortKey && "cursor-pointer select-none hover:text-foreground",
                        )}
                        onClick={col.sortKey ? () => onSort(col.sortKey!) : undefined}
                      >
                        <span className={cn("inline-flex items-center gap-1", col.align === "right" && "flex-row-reverse")}>
                          {col.label}
                          {active &&
                            (sortDir === "asc" ? <ArrowUp className="h-3 w-3" /> : <ArrowDown className="h-3 w-3" />)}
                        </span>
                      </th>
                    );
                  })}
                  <th className="w-10 px-3 py-2.5" />
                </tr>
              </thead>
              <tbody>
                {pageItems.map((a) => (
                  <ArticleRow key={a.id} article={a} columns={visibleColumns} />
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>

      {total > PAGE_SIZE && (
        <div className="flex items-center justify-between text-xs text-muted-foreground">
          <span>
            Page {page} of {totalPages} · {total.toLocaleString()} total
          </span>
          <div className="flex items-center gap-2">
            <Button
              size="sm"
              variant="ghost"
              onClick={() => setPage((p) => Math.max(1, p - 1))}
              disabled={page <= 1 || list.isFetching}
            >
              Previous
            </Button>
            <Button
              size="sm"
              variant="ghost"
              onClick={() => setPage((p) => Math.min(totalPages, p + 1))}
              disabled={page >= totalPages || list.isFetching}
            >
              Next
            </Button>
          </div>
        </div>
      )}
    </div>
  );
}

// ---- row --------------------------------------------------------------

function ArticleRow({ article, columns }: { article: AdsolutArticle; columns: ColDef[] }) {
  const qc = useQueryClient();
  const resync = useMutation({
    mutationFn: () => articlesApi.resync(article.id),
    onSuccess: () => {
      toast.success(`Article ${article.code ?? ""} resynced`);
      qc.invalidateQueries({ queryKey: ARTICLES_KEY });
    },
    onError: () => toast.error("Resync failed"),
  });

  return (
    <tr className="border-b border-glass transition-colors hover:bg-glass-hover">
      {columns.map((col) => (
        <td key={col.id} className={cn("px-3 py-2.5", col.align === "right" && "text-right")}>
          {col.render(article)}
        </td>
      ))}
      <td className="px-3 py-2.5 text-right">
        <Button
          size="sm"
          variant="ghost"
          className="h-7 w-7 p-0"
          aria-label="Resync this article"
          onClick={() => resync.mutate()}
          disabled={resync.isPending}
        >
          <RefreshCw className={cn("h-3.5 w-3.5", resync.isPending && "animate-spin")} />
        </Button>
      </td>
    </tr>
  );
}

// ---- column picker (toggle + drag-reorder) ----------------------------

function ColumnPicker({
  config,
  onChange,
}: {
  config: ColConfig[];
  onChange: (next: ColConfig[]) => void;
}) {
  const sensors = useSensors(useSensor(PointerSensor, { activationConstraint: { distance: 4 } }));

  const onDragEnd = (e: DragEndEvent) => {
    const { active, over } = e;
    if (!over || active.id === over.id) return;
    const from = config.findIndex((c) => c.id === active.id);
    const to = config.findIndex((c) => c.id === over.id);
    if (from < 0 || to < 0) return;
    onChange(arrayMove(config, from, to));
  };

  const toggle = (id: ColId) => {
    const visibleCount = config.filter((c) => c.visible).length;
    const target = config.find((c) => c.id === id);
    // Keep at least one column visible.
    if (target?.visible && visibleCount <= 1) return;
    onChange(config.map((c) => (c.id === id ? { ...c, visible: !c.visible } : c)));
  };

  return (
    <Popover>
      <PopoverTrigger asChild>
        <Button size="sm" variant="ghost" className="gap-1.5">
          <Columns3 className="h-4 w-4" />
          Columns
        </Button>
      </PopoverTrigger>
      <PopoverContent align="end" className="w-64 p-2">
        <div className="px-1 pb-2 text-[11px] uppercase tracking-wider text-muted-foreground/70">
          Columns — drag to reorder
        </div>
        <DndContext sensors={sensors} collisionDetection={closestCenter} onDragEnd={onDragEnd}>
          <SortableContext items={config.map((c) => c.id)} strategy={verticalListSortingStrategy}>
            <ul className="space-y-0.5">
              {config.map((c) => (
                <ColumnPickerItem key={c.id} id={c.id} visible={c.visible} onToggle={() => toggle(c.id)} />
              ))}
            </ul>
          </SortableContext>
        </DndContext>
      </PopoverContent>
    </Popover>
  );
}

function ColumnPickerItem({
  id,
  visible,
  onToggle,
}: {
  id: ColId;
  visible: boolean;
  onToggle: () => void;
}) {
  const { attributes, listeners, setNodeRef, transform, transition, isDragging } = useSortable({ id });
  const style = { transform: CSS.Transform.toString(transform), transition };

  return (
    <li
      ref={setNodeRef}
      style={style}
      className={cn(
        "flex items-center gap-2 rounded-md px-1.5 py-1 text-sm",
        isDragging ? "bg-glass-strong" : "hover:bg-glass-hover",
      )}
    >
      <button
        type="button"
        className="cursor-grab touch-none text-muted-foreground/50 hover:text-muted-foreground active:cursor-grabbing"
        aria-label="Drag to reorder"
        {...attributes}
        {...listeners}
      >
        <GripVertical className="h-3.5 w-3.5" />
      </button>
      <label className="flex flex-1 cursor-pointer items-center gap-2">
        <input
          type="checkbox"
          checked={visible}
          onChange={onToggle}
          className="h-3.5 w-3.5 rounded border border-glass-strong bg-glass accent-purple-400"
        />
        <span className={cn(visible ? "text-foreground" : "text-muted-foreground")}>{COL_LABELS[id]}</span>
      </label>
    </li>
  );
}
