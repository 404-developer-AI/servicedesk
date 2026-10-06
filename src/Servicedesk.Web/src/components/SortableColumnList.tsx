import {
  DndContext,
  KeyboardSensor,
  PointerSensor,
  closestCenter,
  useSensor,
  useSensors,
  type DragEndEvent,
} from "@dnd-kit/core";
import {
  SortableContext,
  arrayMove,
  sortableKeyboardCoordinates,
  useSortable,
  verticalListSortingStrategy,
} from "@dnd-kit/sortable";
import { CSS } from "@dnd-kit/utilities";
import { GripVertical, Plus } from "lucide-react";
import { TICKET_COLUMNS, columnLabel, normalizeLayout } from "@/lib/ticketColumns";
import { cn } from "@/lib/utils";

/// v0.1.18 — ordered ticket-column picker shared by the agent column
/// selector and the view editor. Shown columns sit on top in layout order
/// and drag (or keyboard: focus the grip, Space, arrows, Space) to reorder;
/// hidden columns are listed below and join at the end when ticked.
export function SortableColumnList({
  value,
  onChange,
  className,
}: {
  value: readonly string[];
  onChange: (next: string[]) => void;
  className?: string;
}) {
  const shown = normalizeLayout(value);
  const hidden = TICKET_COLUMNS.map((c) => c.id).filter((id) => !shown.includes(id));

  const sensors = useSensors(
    useSensor(PointerSensor, { activationConstraint: { distance: 4 } }),
    useSensor(KeyboardSensor, { coordinateGetter: sortableKeyboardCoordinates }),
  );

  const onDragEnd = (e: DragEndEvent) => {
    const { active, over } = e;
    if (!over || active.id === over.id) return;
    const from = shown.indexOf(String(active.id));
    const to = shown.indexOf(String(over.id));
    if (from < 0 || to < 0) return;
    onChange(arrayMove(shown, from, to));
  };

  return (
    <div className={cn("space-y-2", className)}>
      <DndContext
        sensors={sensors}
        collisionDetection={closestCenter}
        onDragEnd={onDragEnd}
      >
        <SortableContext items={shown} strategy={verticalListSortingStrategy}>
          <ul className="space-y-0.5">
            {shown.map((id, i) => (
              <ShownItem
                key={id}
                id={id}
                position={i + 1}
                onHide={() => onChange(shown.filter((c) => c !== id))}
              />
            ))}
          </ul>
        </SortableContext>
      </DndContext>
      {shown.length === 0 && (
        <p className="px-2 py-1 text-xs text-muted-foreground">No columns picked.</p>
      )}

      {hidden.length > 0 && (
        <div className="border-t border-glass pt-2">
          <div className="px-2 pb-1 text-[10px] font-medium uppercase tracking-wider text-muted-foreground/70">
            Hidden
          </div>
          <ul className="space-y-0.5">
            {hidden.map((id) => (
              <li key={id}>
                <button
                  type="button"
                  onClick={() => onChange([...shown, id])}
                  className="group flex w-full items-center gap-2 rounded-md px-2 py-1 text-left text-sm text-muted-foreground transition-colors hover:bg-glass-hover hover:text-foreground"
                >
                  <Plus className="h-3.5 w-3.5 opacity-60 group-hover:opacity-100" />
                  {columnLabel(id)}
                </button>
              </li>
            ))}
          </ul>
        </div>
      )}
    </div>
  );
}

function ShownItem({ id, position, onHide }: { id: string; position: number; onHide: () => void }) {
  const { attributes, listeners, setNodeRef, transform, transition, isDragging } = useSortable({ id });
  const style = { transform: CSS.Transform.toString(transform), transition };

  return (
    <li
      ref={setNodeRef}
      style={style}
      className={cn(
        "flex items-center gap-2 rounded-md px-1.5 py-1 text-sm",
        isDragging ? "relative z-10 bg-glass-strong shadow-xs" : "hover:bg-glass-hover",
      )}
    >
      <button
        type="button"
        className="cursor-grab touch-none rounded text-muted-foreground/50 hover:text-muted-foreground focus-visible:outline-hidden focus-visible:ring-2 focus-visible:ring-primary/50 active:cursor-grabbing"
        aria-label={`Move ${columnLabel(id)} (position ${position})`}
        {...attributes}
        {...listeners}
      >
        <GripVertical className="h-3.5 w-3.5" />
      </button>
      <label className="flex flex-1 cursor-pointer items-center gap-2">
        <input
          type="checkbox"
          checked
          onChange={onHide}
          className="h-3.5 w-3.5 rounded border border-glass-strong bg-glass accent-primary"
        />
        <span className="text-foreground">{columnLabel(id)}</span>
      </label>
      <span className="w-4 text-right text-[10px] tabular-nums text-muted-foreground/50">{position}</span>
    </li>
  );
}
