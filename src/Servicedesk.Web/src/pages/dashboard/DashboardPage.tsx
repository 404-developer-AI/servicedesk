import * as React from "react";
import { HealthPill } from "@/components/health/HealthPill";
import { useCurrentRole } from "@/hooks/useCurrentRole";
import { authStore, useAuth, type DashboardTileSize } from "@/auth/authStore";
import { LayoutDashboard, Pencil, Check, Maximize2 } from "lucide-react";
import {
  DASHBOARD_TILES,
  TILE_SIZE_COLSPAN,
  nextAllowedSize,
  roleSatisfies,
  type DashboardTile,
} from "@/lib/dashboardTiles";
import { cn } from "@/lib/utils";
import { userDashboardLayoutApi, type DashboardLayoutEntry } from "@/lib/api";
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
import { toast } from "sonner";

type LayoutEntry = { tileId: string; size: DashboardTileSize };

export function DashboardPage() {
  const role = useCurrentRole();
  const { user } = useAuth();
  const [editing, setEditing] = React.useState(false);

  // Resolve the user's saved layout, filtered to tiles they can
  // actually render (role + per-tile gates). Recomputed on every
  // auth-store change so a flag-toggle by an admin lands immediately.
  const layout = React.useMemo<LayoutEntry[]>(() => {
    const saved = (user?.dashboardTiles ?? []) as LayoutEntry[];
    return saved.filter((entry) => {
      const tile = DASHBOARD_TILES.find((t) => t.id === entry.tileId);
      if (!tile) return false;
      if (!roleSatisfies(role, tile.minRole)) return false;
      if (tile.id === "activity_feed" && !user?.activityFeedEnabled) return false;
      return true;
    });
  }, [user?.dashboardTiles, user?.activityFeedEnabled, role]);

  // Optimistic local copy used while the user drags / cycles sizes in
  // edit mode. Synced back to the auth-store on every save.
  const [draftLayout, setDraftLayout] = React.useState<LayoutEntry[]>(layout);
  React.useEffect(() => { setDraftLayout(layout); }, [layout]);

  const saveTimer = React.useRef<number | null>(null);
  const scheduleSave = React.useCallback((next: LayoutEntry[]) => {
    if (saveTimer.current !== null) {
      window.clearTimeout(saveTimer.current);
    }
    saveTimer.current = window.setTimeout(async () => {
      saveTimer.current = null;
      try {
        const result = await userDashboardLayoutApi.save(next as DashboardLayoutEntry[]);
        // Reconcile auth-store so a hard refresh starts from the
        // server-canonical layout. SetForUserAsync may drop ids that
        // the user is no longer allowed to render.
        authStore.patch({
          user: authStore.get().user
            ? { ...authStore.get().user!, dashboardTiles: result.tiles }
            : null,
        });
      } catch (e) {
        toast.error("Could not save dashboard layout. Try again.");
      }
    }, 400);
  }, []);

  React.useEffect(() => () => {
    if (saveTimer.current !== null) window.clearTimeout(saveTimer.current);
  }, []);

  function handleDragEnd(event: DragEndEvent) {
    const { active, over } = event;
    if (!over || active.id === over.id) return;
    setDraftLayout((prev) => {
      const from = prev.findIndex((p) => p.tileId === active.id);
      const to = prev.findIndex((p) => p.tileId === over.id);
      if (from < 0 || to < 0) return prev;
      const next = arrayMove(prev, from, to);
      scheduleSave(next);
      return next;
    });
  }

  function handleCycleSize(tileId: string) {
    setDraftLayout((prev) => {
      const idx = prev.findIndex((p) => p.tileId === tileId);
      if (idx < 0) return prev;
      const tile = DASHBOARD_TILES.find((t) => t.id === tileId);
      if (!tile) return prev;
      const newSize = nextAllowedSize(tile, prev[idx].size);
      if (newSize === prev[idx].size) return prev;
      const next = prev.slice();
      next[idx] = { ...next[idx], size: newSize };
      scheduleSave(next);
      return next;
    });
  }

  const sensors = useSensors(
    useSensor(PointerSensor, { activationConstraint: { distance: 6 } }),
    useSensor(KeyboardSensor, { coordinateGetter: sortableKeyboardCoordinates }),
  );

  return (
    <div className="flex flex-1 flex-col gap-4">
      <div className="flex items-center justify-between gap-3">
        <h1 className="text-display-md font-semibold text-foreground">Dashboard</h1>
        <div className="flex items-center gap-2">
          <button
            type="button"
            onClick={() => setEditing((e) => !e)}
            title={editing ? "Done editing" : "Edit layout"}
            className={cn(
              "inline-flex h-8 items-center gap-1.5 rounded-md border px-3 text-xs font-medium transition-colors",
              editing
                ? "border-primary/40 bg-primary/15 text-foreground"
                : "border-white/10 bg-white/[0.04] text-muted-foreground hover:bg-white/[0.08]",
            )}
          >
            {editing ? <Check className="h-3.5 w-3.5" /> : <Pencil className="h-3.5 w-3.5" />}
            {editing ? "Done" : "Edit"}
          </button>
          <HealthPill />
        </div>
      </div>

      {draftLayout.length === 0 ? (
        <EmptyDashboard />
      ) : (
        <DndContext sensors={sensors} collisionDetection={closestCenter} onDragEnd={handleDragEnd}>
          <SortableContext items={draftLayout.map((p) => p.tileId)} strategy={rectSortingStrategy}>
            <div className="grid grid-cols-1 gap-4 md:grid-cols-2 lg:grid-cols-4">
              {draftLayout.map((entry) => {
                const tile = DASHBOARD_TILES.find((t) => t.id === entry.tileId);
                if (!tile) return null;
                return (
                  <SortableTile
                    key={tile.id}
                    tile={tile}
                    size={entry.size}
                    editing={editing}
                    onCycleSize={() => handleCycleSize(tile.id)}
                  />
                );
              })}
            </div>
          </SortableContext>
        </DndContext>
      )}
    </div>
  );
}

function SortableTile({
  tile,
  size,
  editing,
  onCycleSize,
}: {
  tile: DashboardTile;
  size: DashboardTileSize;
  editing: boolean;
  onCycleSize: () => void;
}) {
  const { attributes, listeners, setNodeRef, transform, transition, isDragging } = useSortable({
    id: tile.id,
    disabled: !editing,
  });

  const Tile = tile.Component;
  const colSpan = TILE_SIZE_COLSPAN[size];

  const colSpanClass =
    colSpan === 1 ? "lg:col-span-1"
    : colSpan === 2 ? "lg:col-span-2"
    : colSpan === 3 ? "lg:col-span-3"
    : "lg:col-span-4";

  return (
    <div
      ref={setNodeRef}
      style={{
        transform: CSS.Transform.toString(transform),
        transition,
      }}
      className={cn(
        "relative h-full",
        // Span both md columns when the lg-span is 3 or 4, so the
        // breakpoint between tablet and desktop still renders sensibly.
        (colSpan >= 3) && "md:col-span-2",
        colSpanClass,
        isDragging && "z-10 opacity-60",
      )}
    >
      {editing && (
        <div className="absolute inset-0 z-20 rounded-2xl border-2 border-dashed border-primary/40 bg-primary/[0.03] pointer-events-none" />
      )}
      {editing && (
        <div className="absolute right-3 top-3 z-30 flex items-center gap-1">
          <button
            type="button"
            onClick={onCycleSize}
            title={`Resize (currently ${size})`}
            className="inline-flex h-7 items-center gap-1 rounded-md border border-white/10 bg-background/80 px-2 text-[11px] font-medium text-foreground backdrop-blur hover:bg-white/[0.1]"
          >
            <Maximize2 className="h-3 w-3" />
            {size}
          </button>
          <button
            type="button"
            {...attributes}
            {...listeners}
            title="Drag to reorder"
            aria-label="Drag handle"
            className="inline-flex h-7 cursor-grab items-center rounded-md border border-white/10 bg-background/80 px-2 text-[11px] font-medium text-foreground backdrop-blur hover:bg-white/[0.1] active:cursor-grabbing"
          >
            ⠿
          </button>
        </div>
      )}
      <div className={cn("h-full", editing && "pointer-events-none select-none")}>
        <Tile />
      </div>
    </div>
  );
}

function EmptyDashboard() {
  return (
    <div className="glass-panel mx-auto mt-8 flex max-w-xl flex-col items-center gap-3 px-8 py-12 text-center">
      <div className="flex h-12 w-12 items-center justify-center rounded-2xl border border-white/10 bg-white/[0.04]">
        <LayoutDashboard className="h-5 w-5 text-muted-foreground" />
      </div>
      <h2 className="text-base font-semibold text-foreground">
        Your dashboard is empty
      </h2>
      <p className="max-w-sm text-sm text-muted-foreground">
        No dashboard tiles have been enabled for your account yet. Ask an
        administrator to enable tiles via Settings → Users → Features.
      </p>
    </div>
  );
}
