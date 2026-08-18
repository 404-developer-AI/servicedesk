import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import {
  DndContext,
  PointerSensor,
  KeyboardSensor,
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
import { ChevronDown, ClipboardPaste, GripVertical, Lock, Plus, Save, Trash2, X } from "lucide-react";
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Switch } from "@/components/ui/switch";
import { Skeleton } from "@/components/ui/skeleton";
import { taxonomyApi } from "@/lib/api";
import {
  checklistTemplateApi,
  checklistErrorMessage,
  type ChecklistTemplateDetail,
  type ChecklistTemplateInput,
} from "@/lib/checklist-api";
import { cn } from "@/lib/utils";

type ItemDraft = {
  key: string;
  title: string;
  description: string;
  teamLabel: string;
  timingLabel: string;
  linkUrl: string;
  linkLabel: string;
  isRequired: boolean;
  expanded: boolean;
};

type SectionDraft = { key: string; title: string; items: ItemDraft[] };

let keySeq = 0;
const nextKey = () => `k${Date.now().toString(36)}${(keySeq++).toString(36)}`;

const emptyItem = (title = ""): ItemDraft => ({
  key: nextKey(),
  title,
  description: "",
  teamLabel: "",
  timingLabel: "",
  linkUrl: "",
  linkLabel: "",
  isRequired: true,
  expanded: false,
});

/// Admin editor for one checklist template: name/description/flags, queue
/// scope, and the sections + items (drag to reorder, inline field editing,
/// and a paste box that turns one line per item — `# Title` lines start a
/// section, `title | team | timing | link` splits columns — into rows).
export function ChecklistTemplateEditorSheet({
  templateId,
  maxItems,
  onClose,
}: {
  templateId: string | "new" | null;
  maxItems: number;
  onClose: () => void;
}) {
  const open = templateId !== null;
  return (
    <Sheet open={open} onOpenChange={(o) => { if (!o) onClose(); }}>
      <SheetContent
        side="right"
        className="!w-[min(760px,95vw)] !max-w-none overflow-y-auto bg-popover/95 backdrop-blur-xl border-l border-glass sm:!max-w-none"
      >
        <SheetHeader className="space-y-1">
          <SheetTitle>{templateId === "new" ? "New checklist template" : "Edit checklist template"}</SheetTitle>
          <SheetDescription className="text-xs">
            Sections group the steps; every step can carry a team and timing label, a link to a manual, and
            can be optional. Attached checklists are snapshots — saving here never changes a checklist that is
            already on a ticket.
          </SheetDescription>
        </SheetHeader>
        {templateId === null ? null : <EditorBody key={templateId} templateId={templateId} maxItems={maxItems} onClose={onClose} />}
      </SheetContent>
    </Sheet>
  );
}

function EditorBody({ templateId, maxItems, onClose }: { templateId: string | "new"; maxItems: number; onClose: () => void }) {
  const qc = useQueryClient();
  const isNew = templateId === "new";
  const detailQ = useQuery({
    queryKey: ["checklist-templates", templateId],
    queryFn: () => checklistTemplateApi.get(templateId),
    enabled: !isNew,
  });
  const queuesQ = useQuery({ queryKey: ["taxonomy", "queues"], queryFn: () => taxonomyApi.queues.list() });

  const [name, setName] = React.useState("");
  const [description, setDescription] = React.useState("");
  const [isActive, setIsActive] = React.useState(true);
  const [blockClose, setBlockClose] = React.useState(true);
  const [queueIds, setQueueIds] = React.useState<string[]>([]);
  const [sections, setSections] = React.useState<SectionDraft[]>([{ key: nextKey(), title: "", items: [emptyItem()] }]);
  const [hydrated, setHydrated] = React.useState(isNew);
  const [pasteOpen, setPasteOpen] = React.useState(isNew);
  const [pasteText, setPasteText] = React.useState("");

  React.useEffect(() => {
    if (isNew || !detailQ.data || hydrated) return;
    const d: ChecklistTemplateDetail = detailQ.data;
    setName(d.name);
    setDescription(d.description);
    setIsActive(d.isActive);
    setBlockClose(d.blockClose);
    setQueueIds(d.queueIds);
    setSections(
      d.sections.length > 0
        ? d.sections.map((s) => ({
            key: nextKey(),
            title: s.title,
            items: s.items.map((i) => ({ ...emptyItem(i.title), ...i, key: nextKey(), expanded: false })),
          }))
        : [{ key: nextKey(), title: "", items: [emptyItem()] }],
    );
    setHydrated(true);
  }, [detailQ.data, hydrated, isNew]);

  const itemCount = sections.reduce((n, s) => n + s.items.filter((i) => i.title.trim()).length, 0);

  const save = useMutation({
    mutationFn: () => {
      const input: ChecklistTemplateInput = {
        name: name.trim(),
        description: description.trim(),
        isActive,
        blockClose,
        queueIds,
        sections: sections.map((s) => ({
          title: s.title.trim(),
          items: s.items
            .filter((i) => i.title.trim())
            .map((i) => ({
              title: i.title.trim(),
              description: i.description.trim(),
              teamLabel: i.teamLabel.trim(),
              timingLabel: i.timingLabel.trim(),
              linkUrl: i.linkUrl.trim(),
              linkLabel: i.linkLabel.trim(),
              isRequired: i.isRequired,
            })),
        })).filter((s) => s.title || s.items.length > 0),
      };
      return isNew ? checklistTemplateApi.create(input) : checklistTemplateApi.update(templateId, input);
    },
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["checklist-templates"] });
      toast.success(isNew ? "Template created" : "Template saved");
      onClose();
    },
    onError: (err) => toast.error(checklistErrorMessage(err, "Could not save the template")),
  });

  const sensors = useSensors(
    useSensor(PointerSensor, { activationConstraint: { distance: 4 } }),
    useSensor(KeyboardSensor, { coordinateGetter: sortableKeyboardCoordinates }),
  );

  const onSectionDragEnd = (e: DragEndEvent) => {
    const { active, over } = e;
    if (!over || active.id === over.id) return;
    setSections((prev) => {
      const from = prev.findIndex((s) => s.key === active.id);
      const to = prev.findIndex((s) => s.key === over.id);
      return from < 0 || to < 0 ? prev : arrayMove(prev, from, to);
    });
  };

  const updateSection = (key: string, patch: Partial<SectionDraft> | ((s: SectionDraft) => SectionDraft)) =>
    setSections((prev) => prev.map((s) => (s.key === key ? (typeof patch === "function" ? patch(s) : { ...s, ...patch }) : s)));

  const applyPaste = () => {
    const lines = pasteText.split(/\r?\n/);
    let added = 0;
    setSections((prev) => {
      const next = prev.map((s) => ({ ...s, items: [...s.items] }));
      // Drop the single empty placeholder row of a fresh template.
      if (next.length === 1 && next[0].title === "" && next[0].items.length === 1 && !next[0].items[0].title.trim()) next[0].items = [];
      let target = next[next.length - 1];
      for (const raw of lines) {
        const line = raw.trim();
        if (!line) continue;
        if (line.startsWith("#")) {
          target = { key: nextKey(), title: line.replace(/^#+\s*/, "").trim(), items: [] };
          next.push(target);
          continue;
        }
        const cols = line.split(/\s*\|\s*|\t/).map((c) => c.trim());
        const item = emptyItem(cols[0]);
        item.teamLabel = cols[1] ?? "";
        item.timingLabel = cols[2] ?? "";
        item.linkUrl = cols[3] ?? "";
        if (!target) {
          target = { key: nextKey(), title: "", items: [] };
          next.push(target);
        }
        target.items.push(item);
        added++;
      }
      return next;
    });
    setPasteText("");
    setPasteOpen(false);
    if (added > 0) toast.success(`${added} item${added === 1 ? "" : "s"} added — review and save`);
  };

  const canSave = name.trim().length > 0 && itemCount > 0 && itemCount <= maxItems && !save.isPending;

  if (!isNew && (detailQ.isLoading || !hydrated)) {
    return (
      <div className="mt-6 space-y-3">
        <Skeleton className="h-9 w-full" />
        <Skeleton className="h-24 w-full" />
        <Skeleton className="h-40 w-full" />
      </div>
    );
  }
  if (!isNew && detailQ.isError) {
    return <div className="mt-6 text-sm text-destructive">Could not load the template.</div>;
  }

  return (
    <div className="mt-5 space-y-5 pb-24">
      {/* Header fields */}
      <div className="grid gap-3">
        <label className="space-y-1">
          <span className="text-xs font-medium text-muted-foreground">Name</span>
          <Input value={name} onChange={(e) => setName(e.target.value)} maxLength={200} placeholder="e.g. Onboarding — new customer" />
        </label>
        <label className="space-y-1">
          <span className="text-xs font-medium text-muted-foreground">Description (optional)</span>
          <textarea
            value={description}
            onChange={(e) => setDescription(e.target.value)}
            rows={2}
            maxLength={4000}
            placeholder="Shown at the top of the checklist on the ticket."
            className="w-full resize-y rounded-md border border-input bg-background/40 px-3 py-2 text-sm outline-none focus:ring-1 focus:ring-ring"
          />
        </label>
        <div className="grid gap-2 sm:grid-cols-2">
          <label className="flex items-start justify-between gap-3 rounded-md border border-glass bg-glass px-3 py-2.5">
            <span className="space-y-0.5">
              <span className="block text-sm font-medium">Active</span>
              <span className="block text-xs text-muted-foreground">Inactive templates are not offered to agents.</span>
            </span>
            <Switch checked={isActive} onCheckedChange={setIsActive} />
          </label>
          <label className="flex items-start justify-between gap-3 rounded-md border border-glass bg-glass px-3 py-2.5">
            <span className="space-y-0.5">
              <span className="flex items-center gap-1.5 text-sm font-medium">
                <Lock className="h-3.5 w-3.5 text-amber-300/90" /> Blocks closing
              </span>
              <span className="block text-xs text-muted-foreground">
                The ticket can't be resolved/closed while required items are open.
              </span>
            </span>
            <Switch checked={blockClose} onCheckedChange={setBlockClose} />
          </label>
        </div>
        <div className="rounded-md border border-glass bg-glass px-3 py-2.5">
          <div className="flex items-center justify-between gap-3">
            <div>
              <div className="text-sm font-medium">Queue scope</div>
              <div className="text-xs text-muted-foreground">
                Agents can attach this template only to tickets in these queues. Nothing selected = every queue.
              </div>
            </div>
            {queueIds.length > 0 && (
              <button type="button" onClick={() => setQueueIds([])} className="text-xs text-muted-foreground hover:text-foreground">
                Clear (all queues)
              </button>
            )}
          </div>
          <div className="mt-2 flex flex-wrap gap-1.5">
            {(queuesQ.data ?? []).map((q) => {
              const on = queueIds.includes(q.id);
              return (
                <button
                  key={q.id}
                  type="button"
                  onClick={() => setQueueIds((prev) => (on ? prev.filter((x) => x !== q.id) : [...prev, q.id]))}
                  className={cn(
                    "inline-flex items-center gap-1.5 rounded-md border px-2.5 py-1 text-xs transition-colors",
                    on ? "border-primary/40 bg-primary/15 text-foreground" : "border-glass bg-glass text-muted-foreground hover:text-foreground",
                    !q.isActive && "opacity-60",
                  )}
                >
                  <span className="h-2 w-2 rounded-full" style={{ backgroundColor: q.color }} />
                  {q.name}
                </button>
              );
            })}
          </div>
        </div>
      </div>

      {/* Sections + items */}
      <div className="space-y-2">
        <div className="flex items-center justify-between">
          <h3 className="text-xs font-medium uppercase tracking-wide text-muted-foreground">
            Steps <span className="ml-1 tabular-nums text-muted-foreground/70">({itemCount}/{maxItems})</span>
          </h3>
          <div className="flex items-center gap-1.5">
            <Button type="button" size="sm" variant="ghost" className="h-7 text-xs" onClick={() => setPasteOpen((v) => !v)}>
              <ClipboardPaste className="h-3.5 w-3.5" /> Paste lines
            </Button>
            <Button
              type="button"
              size="sm"
              variant="ghost"
              className="h-7 text-xs"
              onClick={() => setSections((prev) => [...prev, { key: nextKey(), title: "New section", items: [emptyItem()] }])}
            >
              <Plus className="h-3.5 w-3.5" /> Section
            </Button>
          </div>
        </div>

        {pasteOpen && (
          <div className="space-y-2 rounded-md border border-dashed border-glass bg-glass p-3">
            <p className="text-xs text-muted-foreground">
              One item per line. Start a line with <code className="font-mono">#</code> to open a new section. Optional
              columns separated by <code className="font-mono">|</code>: <code className="font-mono">title | team | timing | link</code>.
              Paste straight from a spreadsheet — tabs work too.
            </p>
            <textarea
              value={pasteText}
              onChange={(e) => setPasteText(e.target.value)}
              rows={8}
              placeholder={"# Week 1 — Onboarding\nCreate prospect in Adsolut | Back Office | Week 1\nCreate customer folder in Keeper | Back Office | Week 1\n# Week 2\nAnalysis on site | Field Services | Week 2 | https://…"}
              className="w-full resize-y rounded-md border border-input bg-background/40 px-3 py-2 font-mono text-xs outline-none focus:ring-1 focus:ring-ring"
            />
            <div className="flex justify-end gap-2">
              <Button type="button" size="sm" variant="ghost" onClick={() => setPasteOpen(false)}>Cancel</Button>
              <Button type="button" size="sm" disabled={!pasteText.trim()} onClick={applyPaste}>
                <Plus className="h-3.5 w-3.5" /> Add lines
              </Button>
            </div>
          </div>
        )}

        <DndContext sensors={sensors} collisionDetection={closestCenter} onDragEnd={onSectionDragEnd}>
          <SortableContext items={sections.map((s) => s.key)} strategy={verticalListSortingStrategy}>
            <div className="space-y-2">
              {sections.map((s, idx) => (
                <SectionEditor
                  key={s.key}
                  section={s}
                  index={idx}
                  onlySection={sections.length === 1}
                  onChange={(patch) => updateSection(s.key, patch)}
                  onRemove={() => setSections((prev) => prev.filter((x) => x.key !== s.key))}
                />
              ))}
            </div>
          </SortableContext>
        </DndContext>
      </div>

      {/* Footer */}
      <div className="sticky bottom-0 -mx-6 mt-4 flex items-center gap-2 border-t border-glass bg-popover/95 px-6 py-3 backdrop-blur-xl">
        <span className="text-xs text-muted-foreground">
          {itemCount === 0 ? "Add at least one item." : itemCount > maxItems ? `Too many items (max ${maxItems}).` : `${itemCount} item${itemCount === 1 ? "" : "s"}`}
        </span>
        <span className="ml-auto flex gap-2">
          <Button type="button" variant="ghost" onClick={onClose}>
            <X className="h-4 w-4" /> Cancel
          </Button>
          <Button type="button" disabled={!canSave} onClick={() => save.mutate()}>
            <Save className="h-4 w-4" /> {isNew ? "Create" : "Save"}
          </Button>
        </span>
      </div>
    </div>
  );
}

function SectionEditor({
  section,
  index,
  onlySection,
  onChange,
  onRemove,
}: {
  section: SectionDraft;
  index: number;
  onlySection: boolean;
  onChange: (patch: Partial<SectionDraft> | ((s: SectionDraft) => SectionDraft)) => void;
  onRemove: () => void;
}) {
  const { attributes, listeners, setNodeRef, transform, transition, isDragging } = useSortable({ id: section.key });
  const style = { transform: CSS.Transform.toString(transform), transition };
  const [collapsed, setCollapsed] = React.useState(false);

  const sensors = useSensors(
    useSensor(PointerSensor, { activationConstraint: { distance: 4 } }),
    useSensor(KeyboardSensor, { coordinateGetter: sortableKeyboardCoordinates }),
  );
  const onItemDragEnd = (e: DragEndEvent) => {
    const { active, over } = e;
    if (!over || active.id === over.id) return;
    onChange((s) => {
      const from = s.items.findIndex((i) => i.key === active.id);
      const to = s.items.findIndex((i) => i.key === over.id);
      return from < 0 || to < 0 ? s : { ...s, items: arrayMove(s.items, from, to) };
    });
  };
  const updateItem = (key: string, patch: Partial<ItemDraft>) =>
    onChange((s) => ({ ...s, items: s.items.map((i) => (i.key === key ? { ...i, ...patch } : i)) }));
  const removeItem = (key: string) => onChange((s) => ({ ...s, items: s.items.filter((i) => i.key !== key) }));
  const addItem = () => onChange((s) => ({ ...s, items: [...s.items, emptyItem()] }));

  return (
    <div ref={setNodeRef} style={style} className={cn("rounded-md border border-glass bg-glass", isDragging && "opacity-70 shadow-lg")}>
      <div className="flex items-center gap-2 px-2 py-1.5">
        <button
          type="button"
          className="cursor-grab rounded p-1 text-muted-foreground/50 hover:text-foreground active:cursor-grabbing"
          title="Drag to reorder sections"
          {...attributes}
          {...listeners}
        >
          <GripVertical className="h-4 w-4" />
        </button>
        <button type="button" onClick={() => setCollapsed((v) => !v)} className="rounded p-1 text-muted-foreground/60 hover:text-foreground" aria-label={collapsed ? "Expand section" : "Collapse section"}>
          <ChevronDown className={cn("h-4 w-4 transition-transform", collapsed && "-rotate-90")} />
        </button>
        <Input
          value={section.title}
          onChange={(e) => onChange({ title: e.target.value })}
          maxLength={200}
          placeholder={index === 0 ? "Section title (optional — leave empty for ungrouped steps)" : "Section title"}
          className="h-8 flex-1 bg-background/40 text-sm font-medium"
        />
        <span className="text-[11px] tabular-nums text-muted-foreground/70">{section.items.filter((i) => i.title.trim()).length}</span>
        <button
          type="button"
          onClick={onRemove}
          disabled={onlySection}
          title={onlySection ? "A template needs at least one section" : "Remove section and its items"}
          className="rounded p-1 text-muted-foreground/60 hover:text-red-300 disabled:opacity-40"
        >
          <Trash2 className="h-4 w-4" />
        </button>
      </div>
      {!collapsed && (
        <div className="border-t border-glass px-2 py-1.5">
          <DndContext sensors={sensors} collisionDetection={closestCenter} onDragEnd={onItemDragEnd}>
            <SortableContext items={section.items.map((i) => i.key)} strategy={verticalListSortingStrategy}>
              <ul className="space-y-1">
                {section.items.map((item) => (
                  <ItemEditor key={item.key} item={item} onChange={(p) => updateItem(item.key, p)} onRemove={() => removeItem(item.key)} />
                ))}
              </ul>
            </SortableContext>
          </DndContext>
          <button type="button" onClick={addItem} className="mt-1 inline-flex items-center gap-1 rounded-md px-2 py-1 text-xs text-muted-foreground/70 hover:bg-glass-hover hover:text-foreground">
            <Plus className="h-3.5 w-3.5" /> Add item
          </button>
        </div>
      )}
    </div>
  );
}

function ItemEditor({ item, onChange, onRemove }: { item: ItemDraft; onChange: (patch: Partial<ItemDraft>) => void; onRemove: () => void }) {
  const { attributes, listeners, setNodeRef, transform, transition, isDragging } = useSortable({ id: item.key });
  const style = { transform: CSS.Transform.toString(transform), transition };
  const hasExtras = item.description || item.teamLabel || item.timingLabel || item.linkUrl || !item.isRequired;
  return (
    <li ref={setNodeRef} style={style} className={cn("rounded-md border border-transparent bg-background/30", isDragging && "opacity-70 shadow-lg", item.expanded && "border-glass")}>
      <div className="flex items-center gap-1.5 px-1 py-1">
        <button type="button" className="cursor-grab rounded p-1 text-muted-foreground/40 hover:text-foreground active:cursor-grabbing" title="Drag to reorder" {...attributes} {...listeners}>
          <GripVertical className="h-3.5 w-3.5" />
        </button>
        <Input
          value={item.title}
          onChange={(e) => onChange({ title: e.target.value })}
          maxLength={300}
          placeholder="Step title"
          className="h-8 flex-1 bg-transparent text-sm"
        />
        {!item.expanded && hasExtras && (
          <span className="hidden sm:flex items-center gap-1 text-[10px] text-muted-foreground/60">
            {item.timingLabel && <span className="rounded border border-sky-400/25 bg-sky-400/10 px-1 text-sky-200">{item.timingLabel}</span>}
            {item.teamLabel && <span className="rounded border border-violet-400/25 bg-violet-400/10 px-1 text-violet-200">{item.teamLabel}</span>}
            {!item.isRequired && <span className="rounded border border-glass px-1">optional</span>}
          </span>
        )}
        <button
          type="button"
          onClick={() => onChange({ expanded: !item.expanded })}
          className={cn("rounded px-1.5 py-1 text-[11px] text-muted-foreground/70 hover:bg-glass-hover hover:text-foreground", item.expanded && "text-foreground")}
          title="Description, labels, link, required"
        >
          {item.expanded ? "Less" : "Details"}
        </button>
        <button type="button" onClick={onRemove} className="rounded p-1 text-muted-foreground/50 hover:text-red-300" title="Remove step">
          <X className="h-3.5 w-3.5" />
        </button>
      </div>
      {item.expanded && (
        <div className="grid gap-2 border-t border-glass px-2 py-2 sm:grid-cols-2">
          <label className="space-y-1 sm:col-span-2">
            <span className="text-[11px] text-muted-foreground">Description</span>
            <textarea
              value={item.description}
              onChange={(e) => onChange({ description: e.target.value })}
              rows={2}
              maxLength={4000}
              className="w-full resize-y rounded-md border border-input bg-background/40 px-2 py-1.5 text-sm outline-none focus:ring-1 focus:ring-ring"
            />
          </label>
          <label className="space-y-1">
            <span className="text-[11px] text-muted-foreground">Team label</span>
            <Input value={item.teamLabel} onChange={(e) => onChange({ teamLabel: e.target.value })} maxLength={60} placeholder="e.g. Back Office" className="h-8 text-sm" />
          </label>
          <label className="space-y-1">
            <span className="text-[11px] text-muted-foreground">Timing label</span>
            <Input value={item.timingLabel} onChange={(e) => onChange({ timingLabel: e.target.value })} maxLength={60} placeholder="e.g. Week 2" className="h-8 text-sm" />
          </label>
          <label className="space-y-1">
            <span className="text-[11px] text-muted-foreground">Link (https://…)</span>
            <Input value={item.linkUrl} onChange={(e) => onChange({ linkUrl: e.target.value })} maxLength={2000} placeholder="Manual, template, KB article…" className="h-8 text-sm" />
          </label>
          <label className="space-y-1">
            <span className="text-[11px] text-muted-foreground">Link label</span>
            <Input value={item.linkLabel} onChange={(e) => onChange({ linkLabel: e.target.value })} maxLength={120} placeholder="e.g. Manual" className="h-8 text-sm" />
          </label>
          <label className="flex items-center justify-between gap-3 rounded-md border border-glass bg-glass px-3 py-2 sm:col-span-2">
            <span className="text-xs">
              <span className="block font-medium">Required</span>
              <span className="block text-muted-foreground">Required items count towards completion and the close block; optional ones are informational.</span>
            </span>
            <Switch checked={item.isRequired} onCheckedChange={(v) => onChange({ isRequired: v })} />
          </label>
        </div>
      )}
    </li>
  );
}
