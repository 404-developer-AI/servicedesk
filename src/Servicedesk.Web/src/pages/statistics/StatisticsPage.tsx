import * as React from "react";
import { useQuery } from "@tanstack/react-query";
import { BarChart3, Pencil, Check, Maximize2, Eye, EyeOff, Settings2 } from "lucide-react";
import { useAuth } from "@/auth/authStore";
import { statisticsApi, type StatisticTileDto, type StatisticLayoutEntry } from "@/lib/ticket-api";
import { cn } from "@/lib/utils";
import { toast } from "sonner";
import {
  DndContext,
  PointerSensor,
  KeyboardSensor,
  useSensor,
  useSensors,
  closestCenter,
  type DragEndEvent,
} from "@dnd-kit/core";
import {
  SortableContext,
  arrayMove,
  sortableKeyboardCoordinates,
  rectSortingStrategy,
  useSortable,
} from "@dnd-kit/sortable";
import { CSS } from "@dnd-kit/utilities";
import { StatisticTileCard } from "./StatisticTileCard";
import { StatisticsManageSheet } from "./StatisticsManageSheet";

const SIZE_ORDER = ["small", "medium", "wide", "full"] as const;
const SIZE_COLSPAN: Record<string, number> = { small: 1, medium: 2, wide: 3, full: 4 };

function nextSize(size: string): string {
  const i = SIZE_ORDER.indexOf(size as (typeof SIZE_ORDER)[number]);
  return SIZE_ORDER[(i + 1) % SIZE_ORDER.length];
}

export function StatisticsPage() {
  const { user } = useAuth();
  const canWrite = !!user?.statisticsWrite;
  const [editing, setEditing] = React.useState(false);
  const [manageOpen, setManageOpen] = React.useState(false);

  const tilesQuery = useQuery({
    queryKey: ["statistics", "assigned"],
    queryFn: () => statisticsApi.listAssigned(),
  });

  // Local draft used while dragging / cycling size / toggling hidden in
  // edit mode. Re-seeded from the server whenever the assigned list loads.
  const [draft, setDraft] = React.useState<StatisticTileDto[]>([]);
  React.useEffect(() => {
    if (tilesQuery.data) setDraft(tilesQuery.data);
  }, [tilesQuery.data]);

  const saveTimer = React.useRef<number | null>(null);
  const scheduleSave = React.useCallback((next: StatisticTileDto[]) => {
    if (saveTimer.current !== null) window.clearTimeout(saveTimer.current);
    saveTimer.current = window.setTimeout(async () => {
      saveTimer.current = null;
      const layout: StatisticLayoutEntry[] = next.map((t) => ({
        tileId: t.id,
        size: t.size,
        hidden: t.hidden,
      }));
      try {
        await statisticsApi.saveLayout(layout);
      } catch {
        toast.error("Could not save your statistics layout.");
      }
    }, 400);
  }, []);
  React.useEffect(
    () => () => {
      if (saveTimer.current !== null) window.clearTimeout(saveTimer.current);
    },
    [],
  );

  function handleDragEnd(event: DragEndEvent) {
    const { active, over } = event;
    if (!over || active.id === over.id) return;
    setDraft((prev) => {
      const from = prev.findIndex((p) => p.id === active.id);
      const to = prev.findIndex((p) => p.id === over.id);
      if (from < 0 || to < 0) return prev;
      const next = arrayMove(prev, from, to).map((t, i) => ({ ...t, position: i }));
      scheduleSave(next);
      return next;
    });
  }

  function handleCycleSize(id: string) {
    setDraft((prev) => {
      const next = prev.map((t) => (t.id === id ? { ...t, size: nextSize(t.size) } : t));
      scheduleSave(next);
      return next;
    });
  }

  function handleToggleHidden(id: string) {
    setDraft((prev) => {
      const next = prev.map((t) => (t.id === id ? { ...t, hidden: !t.hidden } : t));
      scheduleSave(next);
      return next;
    });
  }

  const sensors = useSensors(
    useSensor(PointerSensor, { activationConstraint: { distance: 6 } }),
    useSensor(KeyboardSensor, { coordinateGetter: sortableKeyboardCoordinates }),
  );

  // Normal mode hides hidden tiles; edit mode shows them all so they can be
  // un-hidden.
  const visible = editing ? draft : draft.filter((t) => !t.hidden);

  return (
    <div className="flex flex-1 flex-col gap-4">
      <div className="flex items-center justify-between gap-3">
        <h1 className="text-display-md font-semibold text-foreground">Statistics</h1>
        <div className="flex items-center gap-2">
          {canWrite && (
            <button
              type="button"
              onClick={() => setManageOpen(true)}
              className="inline-flex h-8 items-center gap-1.5 rounded-md border border-glass bg-glass px-3 text-xs font-medium text-muted-foreground transition-colors hover:bg-glass-hover"
            >
              <Settings2 className="h-3.5 w-3.5" />
              Manage tiles
            </button>
          )}
          {draft.length > 0 && (
            <button
              type="button"
              onClick={() => setEditing((e) => !e)}
              className={cn(
                "inline-flex h-8 items-center gap-1.5 rounded-md border px-3 text-xs font-medium transition-colors",
                editing
                  ? "border-primary/40 bg-primary/15 text-foreground"
                  : "border-glass bg-glass text-muted-foreground hover:bg-glass-hover",
              )}
            >
              {editing ? <Check className="h-3.5 w-3.5" /> : <Pencil className="h-3.5 w-3.5" />}
              {editing ? "Done" : "Edit"}
            </button>
          )}
        </div>
      </div>

      {tilesQuery.isLoading ? (
        <div className="grid grid-cols-1 gap-4 md:grid-cols-2 lg:grid-cols-4">
          {[0, 1, 2].map((i) => (
            <div key={i} className="glass-card h-40 animate-pulse" />
          ))}
        </div>
      ) : visible.length === 0 ? (
        <EmptyStatistics canWrite={canWrite} onManage={() => setManageOpen(true)} />
      ) : (
        <DndContext sensors={sensors} collisionDetection={closestCenter} onDragEnd={handleDragEnd}>
          <SortableContext items={visible.map((t) => t.id)} strategy={rectSortingStrategy}>
            <div className="grid grid-cols-1 gap-4 md:grid-cols-2 lg:grid-cols-4">
              {visible.map((tile) => (
                <SortableStatTile
                  key={tile.id}
                  tile={tile}
                  editing={editing}
                  onCycleSize={() => handleCycleSize(tile.id)}
                  onToggleHidden={() => handleToggleHidden(tile.id)}
                />
              ))}
            </div>
          </SortableContext>
        </DndContext>
      )}

      {canWrite && (
        <StatisticsManageSheet open={manageOpen} onOpenChange={setManageOpen} />
      )}
    </div>
  );
}

function SortableStatTile({
  tile,
  editing,
  onCycleSize,
  onToggleHidden,
}: {
  tile: StatisticTileDto;
  editing: boolean;
  onCycleSize: () => void;
  onToggleHidden: () => void;
}) {
  const { attributes, listeners, setNodeRef, transform, transition, isDragging } = useSortable({
    id: tile.id,
    disabled: !editing,
  });
  const colSpan = SIZE_COLSPAN[tile.size] ?? 2;
  const colSpanClass =
    colSpan === 1 ? "lg:col-span-1"
    : colSpan === 2 ? "lg:col-span-2"
    : colSpan === 3 ? "lg:col-span-3"
    : "lg:col-span-4";

  return (
    <div
      ref={setNodeRef}
      style={{ transform: CSS.Transform.toString(transform), transition }}
      className={cn(
        "relative h-full",
        colSpan >= 3 && "md:col-span-2",
        colSpanClass,
        isDragging && "z-10 opacity-60",
        editing && tile.hidden && "opacity-50",
      )}
    >
      {editing && (
        <div className="pointer-events-none absolute inset-0 z-20 rounded-2xl border-2 border-dashed border-primary/40 bg-primary/[0.03]" />
      )}
      {editing && (
        <div className="absolute right-3 top-3 z-30 flex items-center gap-1">
          <button
            type="button"
            onClick={onToggleHidden}
            title={tile.hidden ? "Show tile" : "Hide tile"}
            className="inline-flex h-7 items-center gap-1 rounded-md border border-glass bg-background/80 px-2 text-[11px] font-medium text-foreground backdrop-blur hover:bg-glass-hover"
          >
            {tile.hidden ? <EyeOff className="h-3 w-3" /> : <Eye className="h-3 w-3" />}
          </button>
          <button
            type="button"
            onClick={onCycleSize}
            title={`Resize (currently ${tile.size})`}
            className="inline-flex h-7 items-center gap-1 rounded-md border border-glass bg-background/80 px-2 text-[11px] font-medium text-foreground backdrop-blur hover:bg-glass-hover"
          >
            <Maximize2 className="h-3 w-3" />
            {tile.size}
          </button>
          <button
            type="button"
            {...attributes}
            {...listeners}
            title="Drag to reorder"
            aria-label="Drag handle"
            className="inline-flex h-7 cursor-grab items-center rounded-md border border-glass bg-background/80 px-2 text-[11px] font-medium text-foreground backdrop-blur hover:bg-glass-hover active:cursor-grabbing"
          >
            ⠿
          </button>
        </div>
      )}
      <div className={cn("h-full", editing && "pointer-events-none select-none")}>
        <StatisticTileCard tile={tile} />
      </div>
    </div>
  );
}

function EmptyStatistics({ canWrite, onManage }: { canWrite: boolean; onManage: () => void }) {
  return (
    <div className="glass-panel mx-auto mt-8 flex max-w-xl flex-col items-center gap-3 px-8 py-12 text-center">
      <div className="flex h-12 w-12 items-center justify-center rounded-2xl border border-glass bg-glass">
        <BarChart3 className="h-5 w-5 text-muted-foreground" />
      </div>
      <h2 className="text-base font-semibold text-foreground">No statistics tiles yet</h2>
      <p className="max-w-sm text-sm text-muted-foreground">
        {canWrite
          ? "Build a tile and assign it to yourself or a colleague to get started."
          : "No statistics tiles have been assigned to your account yet. Ask a statistics builder to assign you one."}
      </p>
      {canWrite && (
        <button
          type="button"
          onClick={onManage}
          className="mt-1 inline-flex h-8 items-center gap-1.5 rounded-md border border-primary/40 bg-primary/15 px-3 text-xs font-medium text-foreground transition-colors hover:bg-primary/25"
        >
          <Settings2 className="h-3.5 w-3.5" />
          Manage tiles
        </button>
      )}
    </div>
  );
}
