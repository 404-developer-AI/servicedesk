import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { toast } from "sonner";
import {
  Zap, Plus, Pencil, Trash2, Activity,
  Clock, AlertTriangle, CheckCircle2, MinusCircle, History,
  GripVertical, ChevronRight, FolderPlus, Timer,
} from "lucide-react";
import {
  triggerApi, triggerGroupApi,
  type TriggerListItem, type TriggerGroup,
  type TriggerPlacement, type TriggerGroupPlacement,
} from "@/lib/api";
import { Button } from "@/components/ui/button";
import { Switch } from "@/components/ui/switch";
import { Skeleton } from "@/components/ui/skeleton";
import { Input } from "@/components/ui/input";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import {
  DndContext, DragOverlay, PointerSensor, KeyboardSensor,
  useSensor, useSensors, closestCenter,
  type DragStartEvent, type DragOverEvent, type DragEndEvent,
  type DraggableAttributes,
} from "@dnd-kit/core";
import type { SyntheticListenerMap } from "@dnd-kit/core/dist/hooks/utilities";
import {
  SortableContext, useSortable, arrayMove,
  sortableKeyboardCoordinates, verticalListSortingStrategy,
} from "@dnd-kit/sortable";
import { CSS } from "@dnd-kit/utilities";
import { TriggerEditorSheet } from "./triggers/TriggerEditorSheet";
import { CollapsibleSettingsCard } from "@/components/settings/CollapsibleSettingsCard";
import { cn } from "@/lib/utils";

// v0.0.100 — engine knobs (Triggers.* category) surfaced on this page; the
// scheduler-side ones existed since v0.0.24 but had no UI until now.
const ENGINE_SETTINGS: ReadonlyArray<{ key: string; label: string }> = [
  { key: "Triggers.SchedulerIntervalSeconds", label: "Scheduler interval (seconds)" },
  { key: "Triggers.EscalationWarningMinutes", label: "Escalation warning lead time (minutes)" },
  { key: "Triggers.SkippedRunRetentionDays", label: "Keep skipped run rows (days)" },
  { key: "Triggers.MaxChainPerMutation", label: "Max trigger chain per mutation" },
  { key: "Triggers.MailDedupWindowMinutes", label: "Mail-action dedup window (minutes)" },
];

const UNGROUPED_ID = "__ungrouped__";
const COLLAPSE_STORAGE_KEY = "settings.triggers.collapsed-groups";

export function TriggersSettingsPage({ initialEditId }: { initialEditId?: string } = {}) {
  const qc = useQueryClient();
  const [editing, setEditing] = React.useState<string | "new" | null>(initialEditId ?? null);
  const [deleting, setDeleting] = React.useState<TriggerListItem | null>(null);
  const [groupDialog, setGroupDialog] = React.useState<TriggerGroup | "new" | null>(null);
  const [deletingGroup, setDeletingGroup] = React.useState<TriggerGroup | null>(null);

  React.useEffect(() => {
    if (initialEditId) setEditing(initialEditId);
  }, [initialEditId]);

  const listQ = useQuery({
    queryKey: ["admin", "triggers"],
    queryFn: () => triggerApi.list(),
  });
  const metaQ = useQuery({
    queryKey: ["admin", "trigger-metadata"],
    queryFn: () => triggerApi.metadata(),
  });
  const groupsQ = useQuery({
    queryKey: ["admin", "trigger-groups"],
    queryFn: () => triggerGroupApi.list(),
  });

  const toggle = useMutation({
    mutationFn: ({ id, isActive }: { id: string; isActive: boolean }) =>
      triggerApi.setActive(id, isActive),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["admin", "triggers"] }),
    onError: () => toast.error("Failed to toggle trigger."),
  });

  const reorderTriggers = useMutation({
    mutationFn: (items: TriggerPlacement[]) => triggerApi.reorder(items),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["admin", "triggers"] }),
    onError: () => toast.error("Failed to save new order."),
  });
  const reorderGroups = useMutation({
    mutationFn: (items: TriggerGroupPlacement[]) => triggerGroupApi.reorder(items),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["admin", "trigger-groups"] }),
    onError: () => toast.error("Failed to save group order."),
  });

  const items = listQ.data?.items ?? [];
  const windowH = listQ.data?.runSummaryWindowHours ?? 24;
  const groups = groupsQ.data?.items ?? [];

  // Local optimistic view: drag-over updates here, then the drop commits
  // the canonical order to the backend. Re-syncs whenever the server
  // result changes.
  const [draftItems, setDraftItems] = React.useState<TriggerListItem[] | null>(null);
  const [draftGroups, setDraftGroups] = React.useState<TriggerGroup[] | null>(null);
  React.useEffect(() => setDraftItems(null), [items]);
  React.useEffect(() => setDraftGroups(null), [groups]);

  const effectiveItems = draftItems ?? items;
  const effectiveGroups = draftGroups ?? groups;

  // Build the per-group sorted slices once per render.
  const byGroup = React.useMemo(() => {
    const map = new Map<string, TriggerListItem[]>();
    map.set(UNGROUPED_ID, []);
    for (const g of effectiveGroups) map.set(g.id, []);
    for (const t of effectiveItems) {
      const key = t.groupId ?? UNGROUPED_ID;
      const arr = map.get(key);
      // A trigger whose group_id points at a deleted group still shows
      // up — surface it in Ungrouped rather than dropping it silently.
      if (arr) arr.push(t);
      else map.get(UNGROUPED_ID)!.push(t);
    }
    for (const arr of map.values()) arr.sort((a, b) => a.sortOrder - b.sortOrder);
    return map;
  }, [effectiveItems, effectiveGroups]);

  const [collapsed, setCollapsed] = React.useState<Set<string>>(() => loadCollapsed());
  const toggleCollapsed = (id: string) => {
    setCollapsed((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id); else next.add(id);
      saveCollapsed(next);
      return next;
    });
  };

  const sensors = useSensors(
    useSensor(PointerSensor, { activationConstraint: { distance: 4 } }),
    useSensor(KeyboardSensor, { coordinateGetter: sortableKeyboardCoordinates }),
  );

  const [activeDrag, setActiveDrag] = React.useState<
    | { kind: "trigger"; item: TriggerListItem }
    | { kind: "group"; group: TriggerGroup }
    | null
  >(null);

  const findContainer = React.useCallback(
    (id: string): string | null => {
      // The container is either UNGROUPED_ID, a group's id, or — when
      // the user drags onto an empty group — the literal "drop:<id>"
      // sentinel attached to the empty-state placeholder.
      if (id === UNGROUPED_ID) return UNGROUPED_ID;
      if (id.startsWith("drop:")) return id.slice("drop:".length);
      if (effectiveGroups.some((g) => g.id === id)) return id;
      const t = effectiveItems.find((x) => x.id === id);
      return t ? t.groupId ?? UNGROUPED_ID : null;
    },
    [effectiveGroups, effectiveItems],
  );

  function handleDragStart(e: DragStartEvent) {
    const id = String(e.active.id);
    if (id.startsWith("group:")) {
      const g = effectiveGroups.find((x) => x.id === id.slice("group:".length));
      if (g) setActiveDrag({ kind: "group", group: g });
      return;
    }
    const t = effectiveItems.find((x) => x.id === id);
    if (t) setActiveDrag({ kind: "trigger", item: t });
  }

  function handleDragOver(e: DragOverEvent) {
    const activeId = String(e.active.id);
    const overId = e.over ? String(e.over.id) : null;
    if (!overId || activeId.startsWith("group:")) return;

    const activeContainer = findContainer(activeId);
    const overContainer = findContainer(overId);
    if (!activeContainer || !overContainer || activeContainer === overContainer) return;

    // Cross-container move: insert the trigger at the over-position in
    // the new container. dnd-kit's items are by id so we rebuild the
    // draft slice.
    setDraftItems((current) => {
      const base = current ?? items;
      const idx = base.findIndex((x) => x.id === activeId);
      if (idx < 0) return base;
      const next = base.slice();
      const [moved] = next.splice(idx, 1);
      const targetGroup = overContainer === UNGROUPED_ID ? null : overContainer;
      moved.groupId = targetGroup;
      // Insert before the hovered item, or at the end of the container
      // when the hover landed on the empty drop sentinel / group header.
      const overIsContainer = overId === overContainer || overId === `drop:${overContainer}`;
      if (overIsContainer) {
        next.push(moved);
      } else {
        const overIdx = next.findIndex((x) => x.id === overId);
        next.splice(overIdx >= 0 ? overIdx : next.length, 0, moved);
      }
      return reindex(next);
    });
  }

  function handleDragEnd(e: DragEndEvent) {
    const activeId = String(e.active.id);
    const overId = e.over ? String(e.over.id) : null;
    setActiveDrag(null);
    if (!overId) { setDraftItems(null); setDraftGroups(null); return; }

    // Group reorder branch — pure outer sortable.
    if (activeId.startsWith("group:") && overId.startsWith("group:")) {
      const a = activeId.slice("group:".length);
      const b = overId.slice("group:".length);
      const oldIdx = effectiveGroups.findIndex((g) => g.id === a);
      const newIdx = effectiveGroups.findIndex((g) => g.id === b);
      if (oldIdx < 0 || newIdx < 0 || oldIdx === newIdx) {
        setDraftGroups(null); return;
      }
      const reordered = arrayMove(effectiveGroups, oldIdx, newIdx).map((g, i) => ({ ...g, sortOrder: i }));
      setDraftGroups(reordered);
      reorderGroups.mutate(reordered.map((g) => ({ id: g.id, sortOrder: g.sortOrder })));
      return;
    }

    if (activeId.startsWith("group:")) return;

    // Trigger reorder branch.
    const activeContainer = findContainer(activeId);
    const overContainer = findContainer(overId);
    if (!activeContainer || !overContainer) { setDraftItems(null); return; }

    // Within-container reorder: arrayMove on the slice.
    if (activeContainer === overContainer && activeId !== overId
        && !overId.startsWith("drop:") && !effectiveGroups.some((g) => g.id === overId)) {
      const slice = (draftItems ?? items).filter((t) => (t.groupId ?? UNGROUPED_ID) === activeContainer);
      const oldIdx = slice.findIndex((t) => t.id === activeId);
      const newIdx = slice.findIndex((t) => t.id === overId);
      if (oldIdx < 0 || newIdx < 0 || oldIdx === newIdx) { setDraftItems(null); return; }
      const reorderedSlice = arrayMove(slice, oldIdx, newIdx).map((t, i) => ({ ...t, sortOrder: i }));
      const rest = (draftItems ?? items).filter((t) => (t.groupId ?? UNGROUPED_ID) !== activeContainer);
      const next = [...rest, ...reorderedSlice];
      setDraftItems(next);
      reorderTriggers.mutate(reorderedSlice.map((t) => ({
        id: t.id, groupId: t.groupId, sortOrder: t.sortOrder,
      })));
      return;
    }

    // Cross-container drop: handleDragOver already moved the trigger
    // and reindexed. Commit the slices for source + dest.
    const commitFrom = draftItems ?? items;
    const affected = commitFrom.filter((t) =>
      (t.groupId ?? UNGROUPED_ID) === activeContainer
      || (t.groupId ?? UNGROUPED_ID) === overContainer
    );
    if (affected.length === 0) { setDraftItems(null); return; }
    reorderTriggers.mutate(affected.map((t) => ({
      id: t.id, groupId: t.groupId, sortOrder: t.sortOrder,
    })));
  }

  return (
    <div className="flex flex-col gap-6">
      <header className="flex items-center justify-between gap-4">
        <div className="flex items-center gap-3">
          <div className="flex h-9 w-9 items-center justify-center rounded-lg bg-primary/20 border border-primary/30">
            <Zap className="h-4 w-4 text-primary" />
          </div>
          <div>
            <h1 className="text-display-md font-semibold text-foreground leading-tight">
              Triggers
            </h1>
            <p className="text-xs text-muted-foreground">
              Auto-route, auto-reply and auto-escalate. Drag rows to reorder
              or move them between groups.
            </p>
          </div>
        </div>
        <div className="flex items-center gap-2">
          <Button
            variant="outline"
            onClick={() => setGroupDialog("new")}
            className="border-glass bg-glass hover:bg-glass-hover"
          >
            <FolderPlus className="h-4 w-4" />
            New group
          </Button>
          <Button
            onClick={() => setEditing("new")}
            className="bg-gradient-to-r from-violet-600 to-indigo-600 hover:from-violet-500 hover:to-indigo-500 text-white shadow-[0_0_20px_rgba(124,58,237,0.3)]"
          >
            <Plus className="h-4 w-4" />
            New trigger
          </Button>
        </div>
      </header>

      {listQ.isLoading ? (
        <div className="space-y-2">
          {Array.from({ length: 3 }).map((_, i) => (
            <Skeleton key={i} className="h-14 rounded-lg" />
          ))}
        </div>
      ) : items.length === 0 && groups.length === 0 ? (
        <EmptyState onNew={() => setEditing("new")} />
      ) : (
        <DndContext
          sensors={sensors}
          collisionDetection={closestCenter}
          onDragStart={handleDragStart}
          onDragOver={handleDragOver}
          onDragEnd={handleDragEnd}
          onDragCancel={() => { setActiveDrag(null); setDraftItems(null); setDraftGroups(null); }}
        >
          <div className="space-y-4">
            {/* Ungrouped is pinned at the top and never draggable. */}
            <UngroupedSection
              items={byGroup.get(UNGROUPED_ID) ?? []}
              windowH={windowH}
              onEdit={setEditing}
              onDelete={setDeleting}
              onToggle={(id, v) => toggle.mutate({ id, isActive: v })}
              toggleLoading={toggle.isPending}
              collapsed={collapsed.has(UNGROUPED_ID)}
              onToggleCollapsed={() => toggleCollapsed(UNGROUPED_ID)}
            />

            <SortableContext
              items={effectiveGroups.map((g) => `group:${g.id}`)}
              strategy={verticalListSortingStrategy}
            >
              {effectiveGroups.map((g) => (
                <SortableGroupSection
                  key={g.id}
                  group={g}
                  items={byGroup.get(g.id) ?? []}
                  windowH={windowH}
                  onEdit={setEditing}
                  onDelete={setDeleting}
                  onToggle={(id, v) => toggle.mutate({ id, isActive: v })}
                  toggleLoading={toggle.isPending}
                  collapsed={collapsed.has(g.id)}
                  onToggleCollapsed={() => toggleCollapsed(g.id)}
                  onEditGroup={() => setGroupDialog(g)}
                  onDeleteGroup={() => setDeletingGroup(g)}
                />
              ))}
            </SortableContext>
          </div>

          <DragOverlay>
            {activeDrag?.kind === "trigger" ? (
              <TriggerRow
                item={activeDrag.item}
                windowH={windowH}
                draggable
                isOverlay
                onEdit={() => {}}
                onDelete={() => {}}
                onToggle={() => {}}
                toggleLoading={false}
              />
            ) : activeDrag?.kind === "group" ? (
              <div className="rounded-lg border border-glass bg-glass-strong px-4 py-3 text-sm font-medium shadow-lg">
                {activeDrag.group.name}
              </div>
            ) : null}
          </DragOverlay>
        </DndContext>
      )}

      <CollapsibleSettingsCard
        icon={<Timer className="h-5 w-5" />}
        category="Triggers"
        title="Scheduler & run history"
        description="How often the time-based scheduler ticks (reminders, SLA escalations, escalation warnings), the escalation-warning lead time, chain/mail-loop caps, and how long skipped run rows are kept before being swept. Applied and failed runs are never swept."
        keys={ENGINE_SETTINGS}
      />

      {metaQ.data && (
        <TriggerEditorSheet
          triggerId={editing}
          metadata={metaQ.data}
          onClose={() => setEditing(null)}
        />
      )}

      {deleting && (
        <DeleteDialog
          item={deleting}
          onClose={() => setDeleting(null)}
          onDeleted={() => {
            qc.invalidateQueries({ queryKey: ["admin", "triggers"] });
            setDeleting(null);
          }}
        />
      )}

      {groupDialog && (
        <GroupDialog
          group={groupDialog === "new" ? null : groupDialog}
          onClose={() => setGroupDialog(null)}
          onSaved={() => {
            qc.invalidateQueries({ queryKey: ["admin", "trigger-groups"] });
            setGroupDialog(null);
          }}
        />
      )}

      {deletingGroup && (
        <DeleteGroupDialog
          group={deletingGroup}
          onClose={() => setDeletingGroup(null)}
          onDeleted={() => {
            qc.invalidateQueries({ queryKey: ["admin", "trigger-groups"] });
            qc.invalidateQueries({ queryKey: ["admin", "triggers"] });
            setDeletingGroup(null);
          }}
        />
      )}
    </div>
  );
}

function reindex(arr: TriggerListItem[]): TriggerListItem[] {
  const counters = new Map<string, number>();
  return arr.map((t) => {
    const key = t.groupId ?? UNGROUPED_ID;
    const next = (counters.get(key) ?? 0);
    counters.set(key, next + 1);
    return { ...t, sortOrder: next };
  });
}

function loadCollapsed(): Set<string> {
  try {
    const raw = localStorage.getItem(COLLAPSE_STORAGE_KEY);
    if (!raw) return new Set();
    const parsed: unknown = JSON.parse(raw);
    if (Array.isArray(parsed)) return new Set(parsed.filter((v): v is string => typeof v === "string"));
  } catch { /* ignore */ }
  return new Set();
}
function saveCollapsed(set: Set<string>) {
  try { localStorage.setItem(COLLAPSE_STORAGE_KEY, JSON.stringify([...set])); }
  catch { /* ignore */ }
}

// ---------- Ungrouped (pinned) ----------

function UngroupedSection({
  items, windowH, onEdit, onDelete, onToggle, toggleLoading,
  collapsed, onToggleCollapsed,
}: {
  items: TriggerListItem[];
  windowH: number;
  onEdit: (id: string) => void;
  onDelete: (item: TriggerListItem) => void;
  onToggle: (id: string, v: boolean) => void;
  toggleLoading: boolean;
  collapsed: boolean;
  onToggleCollapsed: () => void;
}) {
  if (items.length === 0) return null;
  return (
    <section className="rounded-xl border border-glass bg-glass">
      <button
        type="button"
        onClick={onToggleCollapsed}
        className="flex w-full items-center gap-2 px-3 py-2 text-left text-xs uppercase tracking-wide text-muted-foreground hover:text-foreground"
      >
        <ChevronRight className={cn("h-3.5 w-3.5 transition-transform", !collapsed && "rotate-90")} />
        <span>Ungrouped</span>
        <span className="ml-1 text-muted-foreground/60">({items.length})</span>
      </button>
      {!collapsed && (
        <SortableContext items={items.map((t) => t.id)} strategy={verticalListSortingStrategy}>
          <DropZone id={UNGROUPED_ID}>
            <div className="space-y-2 px-2 pb-2">
              {items.map((t) => (
                <SortableTriggerRow
                  key={t.id}
                  item={t}
                  windowH={windowH}
                  onEdit={() => onEdit(t.id)}
                  onDelete={() => onDelete(t)}
                  onToggle={(v) => onToggle(t.id, v)}
                  toggleLoading={toggleLoading}
                />
              ))}
            </div>
          </DropZone>
        </SortableContext>
      )}
    </section>
  );
}

// ---------- Sortable group section ----------

function SortableGroupSection(props: {
  group: TriggerGroup;
  items: TriggerListItem[];
  windowH: number;
  onEdit: (id: string) => void;
  onDelete: (item: TriggerListItem) => void;
  onToggle: (id: string, v: boolean) => void;
  toggleLoading: boolean;
  collapsed: boolean;
  onToggleCollapsed: () => void;
  onEditGroup: () => void;
  onDeleteGroup: () => void;
}) {
  const { group, items, windowH, collapsed } = props;
  const sortable = useSortable({ id: `group:${group.id}` });
  const style = {
    transform: CSS.Translate.toString(sortable.transform),
    transition: sortable.transition,
  };

  return (
    <section
      ref={sortable.setNodeRef}
      style={style}
      className={cn(
        "rounded-xl border bg-glass",
        sortable.isDragging ? "border-violet-400/40 opacity-50" : "border-glass-strong",
      )}
    >
      <div className="flex items-center gap-2 px-3 py-2.5">
        <button
          type="button"
          {...sortable.attributes}
          {...sortable.listeners}
          className="cursor-grab touch-none rounded p-1 text-muted-foreground/50 hover:bg-glass-hover hover:text-muted-foreground"
          aria-label="Drag group"
        >
          <GripVertical className="h-4 w-4" />
        </button>
        <button
          type="button"
          onClick={props.onToggleCollapsed}
          className="flex flex-1 items-center gap-2 text-left"
        >
          <ChevronRight className={cn("h-3.5 w-3.5 transition-transform text-muted-foreground", !collapsed && "rotate-90")} />
          <span
            className="inline-block h-2.5 w-2.5 rounded-full"
            style={{ backgroundColor: group.color ?? "#7c7cff" }}
            aria-hidden
          />
          <span className="text-sm font-medium text-foreground">{group.name}</span>
          <span className="text-xs text-muted-foreground/60">({items.length})</span>
        </button>
        <Button variant="ghost" size="sm" onClick={props.onEditGroup} className="h-8">
          <Pencil className="h-3.5 w-3.5" />
        </Button>
        <Button
          variant="ghost"
          size="sm"
          onClick={props.onDeleteGroup}
          className="h-8 text-destructive hover:text-destructive"
        >
          <Trash2 className="h-3.5 w-3.5" />
        </Button>
      </div>

      {!collapsed && (
        <SortableContext items={items.map((t) => t.id)} strategy={verticalListSortingStrategy}>
          <DropZone id={group.id}>
            <div className="space-y-2 px-2 pb-2">
              {items.length === 0 ? (
                <div className="rounded-md border border-dashed border-glass px-3 py-6 text-center text-xs text-muted-foreground/60">
                  Drop triggers here
                </div>
              ) : (
                items.map((t) => (
                  <SortableTriggerRow
                    key={t.id}
                    item={t}
                    windowH={windowH}
                    onEdit={() => props.onEdit(t.id)}
                    onDelete={() => props.onDelete(t)}
                    onToggle={(v) => props.onToggle(t.id, v)}
                    toggleLoading={props.toggleLoading}
                  />
                ))
              )}
            </div>
          </DropZone>
        </SortableContext>
      )}
    </section>
  );
}

// Invisible droppable sentinel — gives the empty placeholder an id the
// drag-over handler can hit-test against so a drop onto a fresh group
// is recognised even though there are no rows to hover.
function DropZone({ id, children }: { id: string; children: React.ReactNode }) {
  const sortable = useSortable({ id: `drop:${id}`, disabled: true });
  return (
    <div ref={sortable.setNodeRef}>
      {children}
    </div>
  );
}

// ---------- Sortable trigger row ----------

function SortableTriggerRow({
  item, windowH, onEdit, onDelete, onToggle, toggleLoading,
}: {
  item: TriggerListItem;
  windowH: number;
  onEdit: () => void;
  onDelete: () => void;
  onToggle: (v: boolean) => void;
  toggleLoading: boolean;
}) {
  const sortable = useSortable({ id: item.id });
  const style = {
    transform: CSS.Translate.toString(sortable.transform),
    transition: sortable.transition,
  };
  return (
    <div ref={sortable.setNodeRef} style={style} className={sortable.isDragging ? "opacity-40" : undefined}>
      <TriggerRow
        item={item}
        windowH={windowH}
        draggable
        dragAttributes={sortable.attributes}
        dragListeners={sortable.listeners}
        onEdit={onEdit}
        onDelete={onDelete}
        onToggle={onToggle}
        toggleLoading={toggleLoading}
      />
    </div>
  );
}

function TriggerRow({
  item, windowH, onEdit, onDelete, onToggle, toggleLoading,
  draggable, dragAttributes, dragListeners, isOverlay,
}: {
  item: TriggerListItem;
  windowH: number;
  onEdit: () => void;
  onDelete: () => void;
  onToggle: (v: boolean) => void;
  toggleLoading: boolean;
  draggable?: boolean;
  dragAttributes?: DraggableAttributes;
  dragListeners?: SyntheticListenerMap;
  isOverlay?: boolean;
}) {
  const totalRuns =
    item.runs.applied + item.runs.skippedNoMatch + item.runs.skippedLoop + item.runs.failed;
  return (
    <div
      className={cn(
        "rounded-lg border bg-glass px-3 py-3 transition-colors",
        item.isActive
          ? "border-glass-strong hover:bg-glass-hover"
          : "border-glass opacity-70",
        isOverlay && "border-violet-400/40 bg-popover/95 shadow-2xl",
      )}
    >
      <div className="flex items-center gap-2">
        {draggable && (
          <button
            type="button"
            {...(dragAttributes ?? {})}
            {...(dragListeners ?? {})}
            className="cursor-grab touch-none rounded p-1 text-muted-foreground/40 hover:bg-glass-hover hover:text-muted-foreground"
            aria-label="Drag trigger"
          >
            <GripVertical className="h-4 w-4" />
          </button>
        )}
        <Switch
          checked={item.isActive}
          disabled={toggleLoading}
          onCheckedChange={onToggle}
        />
        <div className="min-w-0 flex-1">
          <div className="flex items-center gap-2">
            <button
              type="button"
              onClick={onEdit}
              className="truncate text-left text-sm font-medium text-foreground hover:text-primary"
            >
              {item.name}
            </button>
            <ActivatorBadge kind={item.activatorKind} mode={item.activatorMode} />
            {item.locale && (
              <span className="rounded-full border border-glass bg-glass px-2 py-0.5 text-[10px] text-muted-foreground">
                {item.locale}
              </span>
            )}
          </div>
          {item.description && (
            <p className="mt-0.5 truncate text-xs text-muted-foreground/70">
              {item.description}
            </p>
          )}
          <div className="mt-1.5 flex flex-wrap items-center gap-2 text-[11px] text-muted-foreground/80">
            <span title={`Runs in the last ${windowH}h`} className="flex items-center gap-1">
              <Activity className="h-3 w-3" />
              {totalRuns} run{totalRuns === 1 ? "" : "s"} / {windowH}h
            </span>
            {item.runs.applied > 0 && (
              <span className="flex items-center gap-1 text-emerald-300">
                <CheckCircle2 className="h-3 w-3" />
                {item.runs.applied} applied
              </span>
            )}
            {item.runs.skippedNoMatch > 0 && (
              <span className="flex items-center gap-1 text-muted-foreground">
                <MinusCircle className="h-3 w-3" />
                {item.runs.skippedNoMatch} skipped
              </span>
            )}
            {item.runs.failed > 0 && (
              <span className="flex items-center gap-1 text-red-300">
                <AlertTriangle className="h-3 w-3" />
                {item.runs.failed} failed
              </span>
            )}
            {item.runs.lastFiredUtc && (
              <span className="flex items-center gap-1">
                <Clock className="h-3 w-3" />
                Last: {formatRelative(item.runs.lastFiredUtc)}
              </span>
            )}
          </div>
        </div>
        <div className="flex items-center gap-1">
          <Button variant="ghost" size="sm" asChild className="h-8 gap-1.5">
            <Link to="/settings/triggers/$triggerId/runs" params={{ triggerId: item.id }}>
              <History className="h-3.5 w-3.5" />
              Runs
            </Link>
          </Button>
          <Button variant="ghost" size="sm" onClick={onEdit} className="h-8 gap-1.5">
            <Pencil className="h-3.5 w-3.5" />
            Edit
          </Button>
          <Button
            variant="ghost"
            size="sm"
            onClick={onDelete}
            className="h-8 gap-1.5 text-destructive hover:text-destructive"
          >
            <Trash2 className="h-3.5 w-3.5" />
            Delete
          </Button>
        </div>
      </div>
    </div>
  );
}

function ActivatorBadge({ kind, mode }: { kind: string; mode: string }) {
  const label =
    kind === "action"
      ? mode === "selective" ? "On update (selective)" : "On update (always)"
      : mode === "reminder" ? "Pending-till"
      : mode === "escalation" ? "SLA breach"
      : mode === "escalation_warning" ? "SLA warning"
      : `${kind}:${mode}`;
  const tone = kind === "action" ? "violet" : "amber";
  return (
    <span
      className={cn(
        "rounded-full border px-2 py-0.5 text-[10px] font-medium whitespace-nowrap",
        tone === "violet"
          ? "border-violet-400/30 bg-violet-400/10 text-violet-200"
          : "border-amber-400/30 bg-amber-400/10 text-amber-200",
      )}
    >
      {label}
    </span>
  );
}

function EmptyState({ onNew }: { onNew: () => void }) {
  return (
    <div className="rounded-lg border border-glass-strong bg-glass px-6 py-12 flex flex-col items-center justify-center gap-4 text-center">
      <div className="flex h-10 w-10 items-center justify-center rounded-xl bg-primary/10 border border-primary/20">
        <Zap className="h-5 w-5 text-primary/60" />
      </div>
      <div>
        <p className="text-sm font-medium text-foreground">No triggers yet</p>
        <p className="text-xs text-muted-foreground mt-1">
          Create one to auto-route, auto-reply, or escalate on SLA breach.
        </p>
      </div>
      <Button onClick={onNew} variant="secondary">
        <Plus className="h-4 w-4" />
        Create your first trigger
      </Button>
    </div>
  );
}

function DeleteDialog({
  item, onClose, onDeleted,
}: {
  item: TriggerListItem;
  onClose: () => void;
  onDeleted: () => void;
}) {
  const del = useMutation({
    mutationFn: () => triggerApi.remove(item.id),
    onSuccess: () => {
      toast.success("Trigger deleted");
      onDeleted();
    },
    onError: () => toast.error("Failed to delete trigger."),
  });

  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="max-w-sm">
        <DialogHeader>
          <DialogTitle>Delete trigger</DialogTitle>
          <DialogDescription>
            Delete &ldquo;{item.name}&rdquo;? Run history is removed too. This
            cannot be undone.
          </DialogDescription>
        </DialogHeader>
        <DialogFooter>
          <Button variant="ghost" onClick={onClose}>Cancel</Button>
          <Button
            variant="destructive"
            disabled={del.isPending}
            onClick={() => del.mutate()}
          >
            {del.isPending ? "Deleting…" : "Delete"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

function GroupDialog({
  group, onClose, onSaved,
}: {
  group: TriggerGroup | null;
  onClose: () => void;
  onSaved: () => void;
}) {
  const [name, setName] = React.useState(group?.name ?? "");
  const [color, setColor] = React.useState(group?.color ?? "#7c7cff");

  const save = useMutation({
    mutationFn: () =>
      group
        ? triggerGroupApi.update(group.id, { name: name.trim(), color })
        : triggerGroupApi.create({ name: name.trim(), color }),
    onSuccess: () => {
      toast.success(group ? "Group updated" : "Group created");
      onSaved();
    },
    onError: () => toast.error("Save failed."),
  });

  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="max-w-sm">
        <DialogHeader>
          <DialogTitle>{group ? "Rename group" : "New group"}</DialogTitle>
          <DialogDescription>
            Groups are a UX-only construct — they don&rsquo;t affect when a
            trigger fires.
          </DialogDescription>
        </DialogHeader>
        <div className="space-y-3 text-sm">
          <label className="flex flex-col gap-1">
            <span className="text-xs text-muted-foreground">Name</span>
            <Input value={name} onChange={(e) => setName(e.target.value)} autoFocus />
          </label>
          <label className="flex flex-col gap-1">
            <span className="text-xs text-muted-foreground">Color</span>
            <div className="flex items-center gap-2">
              <input
                type="color"
                value={color}
                onChange={(e) => setColor(e.target.value)}
                className="h-9 w-12 cursor-pointer rounded-md border border-glass bg-transparent"
              />
              <Input value={color} onChange={(e) => setColor(e.target.value)} className="font-mono text-xs" />
            </div>
          </label>
        </div>
        <DialogFooter>
          <Button variant="ghost" onClick={onClose}>Cancel</Button>
          <Button onClick={() => save.mutate()} disabled={save.isPending || !name.trim()}>
            {save.isPending ? "Saving…" : "Save"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

function DeleteGroupDialog({
  group, onClose, onDeleted,
}: {
  group: TriggerGroup;
  onClose: () => void;
  onDeleted: () => void;
}) {
  const del = useMutation({
    mutationFn: () => triggerGroupApi.remove(group.id),
    onSuccess: () => {
      toast.success("Group deleted");
      onDeleted();
    },
    onError: () => toast.error("Failed to delete group."),
  });

  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="max-w-sm">
        <DialogHeader>
          <DialogTitle>Delete group</DialogTitle>
          <DialogDescription>
            Delete &ldquo;{group.name}&rdquo;? Any triggers in this group move
            back to <em>Ungrouped</em>; they are not removed.
          </DialogDescription>
        </DialogHeader>
        <DialogFooter>
          <Button variant="ghost" onClick={onClose}>Cancel</Button>
          <Button
            variant="destructive"
            disabled={del.isPending}
            onClick={() => del.mutate()}
          >
            {del.isPending ? "Deleting…" : "Delete"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

function formatRelative(iso: string): string {
  const t = Date.parse(iso);
  if (!Number.isFinite(t)) return iso;
  const diffMs = Date.now() - t;
  const diffMin = Math.round(diffMs / 60_000);
  if (diffMin < 1) return "just now";
  if (diffMin < 60) return `${diffMin} min ago`;
  const diffH = Math.round(diffMin / 60);
  if (diffH < 24) return `${diffH} h ago`;
  const diffD = Math.round(diffH / 24);
  return `${diffD} d ago`;
}
