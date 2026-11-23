import { Columns3, GripVertical } from "lucide-react";
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
import { Button } from "@/components/ui/button";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { cn } from "@/lib/utils";

export type ColumnConfig<Id extends string> = { id: Id; visible: boolean };

/// Merge a saved column config with the defaults: keep known ids in their saved
/// order, drop unknown ids, and append any columns added since the config was
/// saved (with their default visibility) so a new column never silently breaks.
export function mergeColumnConfig<Id extends string>(
  raw: string | undefined,
  defaults: ColumnConfig<Id>[],
): ColumnConfig<Id>[] {
  if (!raw) return defaults;
  let saved: ColumnConfig<Id>[];
  try {
    const parsed = JSON.parse(raw) as ColumnConfig<Id>[];
    if (!Array.isArray(parsed)) return defaults;
    saved = parsed.filter((c) => defaults.some((d) => d.id === c.id));
  } catch {
    return defaults;
  }
  const seen = new Set(saved.map((c) => c.id));
  const merged = saved.map((c) => ({ id: c.id, visible: c.visible !== false }));
  for (const d of defaults) {
    if (!seen.has(d.id)) merged.push({ id: d.id, visible: d.visible });
  }
  return merged.length > 0 ? merged : defaults;
}

/// Column visibility toggle + drag-reorder popover shared by the Adsolut-style tables.
export function TableColumnPicker<Id extends string>({
  config,
  labels,
  onChange,
}: {
  config: ColumnConfig<Id>[];
  labels: Record<Id, string>;
  onChange: (next: ColumnConfig<Id>[]) => void;
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

  const toggle = (id: Id) => {
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
                <TableColumnPickerItem
                  key={c.id}
                  id={c.id}
                  label={labels[c.id]}
                  visible={c.visible}
                  onToggle={() => toggle(c.id)}
                />
              ))}
            </ul>
          </SortableContext>
        </DndContext>
      </PopoverContent>
    </Popover>
  );
}

function TableColumnPickerItem({
  id,
  label,
  visible,
  onToggle,
}: {
  id: string;
  label: string;
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
        <span className={cn(visible ? "text-foreground" : "text-muted-foreground")}>{label}</span>
      </label>
    </li>
  );
}
