import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
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
import {
  AlignLeft,
  ArrowLeft,
  Calendar,
  CalendarClock,
  Check,
  CheckSquare,
  ChevronDown,
  ChevronsUpDown,
  Clock,
  Copy,
  GripVertical,
  Hash,
  Heading,
  ListChecks,
  ListFilter,
  Package,
  Pencil,
  Plus,
  ToggleLeft,
  Trash2,
  Type,
  UserCircle,
  Users,
  X,
} from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { Switch } from "@/components/ui/switch";
import { FormPreview } from "@/components/forms/FormDialogs";
import { formInputCn } from "@/components/forms/FormFieldInput";
import { settingsApi, taxonomyApi } from "@/lib/api";
import {
  FORM_FIELD_TYPES,
  FORM_FIELD_TYPE_LABEL,
  formApiError,
  formTemplatesApi,
  hasOptions,
  type FormField,
  type FormFieldType,
  type FormTemplateSummary,
} from "@/lib/forms-api";
import { cn } from "@/lib/utils";

/// v0.1.52 — Settings → Templates → Forms: the Forms.* settings plus the
/// catalogue of interactive form templates agents fill in via `::` in the
/// Note/Call composer. Filled forms are snapshots: editing or deleting a
/// template here never changes a form already on a ticket.

const LIST_KEY = ["form-templates", "list"] as const;

const TYPE_ICON: Record<FormFieldType, React.ComponentType<{ className?: string }>> = {
  SectionHeader: Heading,
  ShortText: Type,
  LongText: AlignLeft,
  Number: Hash,
  Dropdown: ChevronsUpDown,
  MultiSelect: ListFilter,
  Checkbox: ToggleLeft,
  CheckboxGroup: CheckSquare,
  Date: Calendar,
  DateTime: CalendarClock,
  Duration: Clock,
  Agent: UserCircle,
  Contact: Users,
  Article: Package,
};

const TYPE_HINT: Record<FormFieldType, string> = {
  SectionHeader: "Groups the fields below under a heading.",
  ShortText: "One line of free text.",
  LongText: "Several lines of free text.",
  Number: "A number with optional bounds and decimals.",
  Dropdown: "Pick one option.",
  MultiSelect: "Pick any number of options (chips).",
  Checkbox: "A single yes/no tick.",
  CheckboxGroup: "A list of options to tick off.",
  Date: "A calendar date.",
  DateTime: "Date and time, in server time.",
  Duration: "Hours and minutes (h:mm).",
  Agent: "One of the agents.",
  Contact: "The requester or a contact of the ticket's company.",
  Article: "Adsolut articles with a quantity — contract articles first.",
};

const newId = () =>
  typeof crypto !== "undefined" && "randomUUID" in crypto
    ? crypto.randomUUID()
    : "xxxxxxxx-xxxx-4xxx-8xxx-xxxxxxxxxxxx".replace(/x/g, () => Math.floor(Math.random() * 16).toString(16));
const newOptionId = () => Math.random().toString(36).slice(2, 10);

function newField(type: FormFieldType): FormField {
  return {
    id: newId(),
    type,
    label: type === "SectionHeader" ? "New section" : FORM_FIELD_TYPE_LABEL[type],
    help: "",
    required: false,
    width: "full",
    options: hasOptions(type)
      ? [{ id: newOptionId(), label: "Option 1" }, { id: newOptionId(), label: "Option 2" }]
      : null,
    min: type === "Article" ? 1 : null,
    max: null,
    decimals: type === "Number" ? 0 : null,
    minRows: null,
    maxRows: null,
    defaultValue: "",
  };
}

export function FormTemplatesPanel() {
  const [editing, setEditing] = React.useState<string | "new" | null>(null);
  if (editing !== null) {
    return <FormTemplateEditor templateId={editing === "new" ? null : editing} onDone={() => setEditing(null)} />;
  }
  return (
    <div className="flex flex-col gap-6">
      <FormSettingsCard />
      <FormTemplateList onNew={() => setEditing("new")} onEdit={setEditing} />
    </div>
  );
}

/* ─── settings ─── */

const NUMBER_SETTINGS = [
  { key: "Forms.MaxFieldsPerTemplate", label: "Maximum fields per form", fallback: "60", min: 1, max: 200, hint: "Including section headers. 1–200." },
  { key: "Forms.MaxPerTicket", label: "Maximum forms per ticket", fallback: "50", min: 1, max: 200, hint: "1–200." },
  { key: "Forms.MaxArticleRows", label: "Maximum articles per article field", fallback: "50", min: 1, max: 500, hint: "1–500." },
  { key: "Forms.MaxArticleQuantity", label: "Largest quantity per article", fallback: "100000", min: 1, max: 10000000, hint: "1–10,000,000." },
  { key: "Forms.LongTextMaxLength", label: "Long-text field length", fallback: "4000", min: 100, max: 20000, hint: "Characters. 100–20,000." },
] as const;

function FormSettingsCard() {
  const qc = useQueryClient();
  const settingsQ = useQuery({
    queryKey: ["settings", "tickets-forms"],
    queryFn: () => settingsApi.list("Tickets"),
    staleTime: 60_000,
  });
  const val = (key: string, fallback: string) => settingsQ.data?.find((e) => e.key === key)?.value ?? fallback;
  const [drafts, setDrafts] = React.useState<Record<string, string>>({});

  const update = useMutation({
    mutationFn: ({ key, value }: { key: string; value: string }) => settingsApi.update(key, value),
    onSuccess: (_d, v) => {
      void qc.invalidateQueries({ queryKey: ["settings", "tickets-forms"] });
      setDrafts((d) => {
        const next = { ...d };
        delete next[v.key];
        return next;
      });
      toast.success("Setting updated");
    },
    onError: () => toast.error("Could not update setting"),
  });

  const enabled = val("Forms.Enabled", "true") === "true";
  const busy = settingsQ.isLoading || update.isPending;

  return (
    <section className="glass-card space-y-4 p-5">
      <div className="space-y-1">
        <h2 className="text-sm font-medium uppercase tracking-wide text-muted-foreground">Forms</h2>
        <p className="text-xs text-muted-foreground/70">
          Interactive forms with typed fields. Agents pick one with <code className="font-mono">::</code> in the
          Note or Call composer, fill it in, and save it: the answers land on the ticket as an internal note and
          the form is listed in the ticket's side panel, where it can be reopened and edited (every edit is a new
          version with its own note).
        </p>
      </div>
      <label className="flex items-start justify-between gap-4 rounded-md border border-glass bg-glass px-3 py-2.5">
        <div className="min-w-0 space-y-0.5">
          <div className="text-sm font-medium text-foreground">Forms enabled</div>
          <div className="text-xs text-muted-foreground">
            Off hides the forms from the <code className="font-mono">::</code> picker and blocks new saves. Filled
            forms stay readable.
          </div>
        </div>
        <Switch checked={enabled} disabled={busy}
          onCheckedChange={(v) => update.mutate({ key: "Forms.Enabled", value: v ? "true" : "false" })} />
      </label>
      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
        {NUMBER_SETTINGS.map((s) => {
          const saved = val(s.key, s.fallback);
          const value = drafts[s.key] ?? saved;
          const n = Number(value);
          const valid = Number.isInteger(n) && n >= s.min && n <= s.max;
          return (
            <div key={s.key} className="rounded-md border border-glass bg-glass px-3 py-2.5">
              <label className="block space-y-1.5">
                <span className="text-sm font-medium text-foreground">{s.label}</span>
                <span className="flex items-center gap-2">
                  <Input type="number" min={s.min} max={s.max} value={value} disabled={busy}
                    onChange={(e) => setDrafts((d) => ({ ...d, [s.key]: e.target.value }))} className="max-w-[140px]" />
                  <Button type="button" size="sm" disabled={value === saved || !valid || busy}
                    onClick={() => update.mutate({ key: s.key, value: String(n) })}>
                    Save
                  </Button>
                </span>
              </label>
              <p className={cn("mt-1 text-xs", valid ? "text-muted-foreground/70" : "text-destructive")}>
                {valid ? s.hint : `Enter a whole number between ${s.min} and ${s.max}.`}
              </p>
            </div>
          );
        })}
      </div>
    </section>
  );
}

/* ─── list ─── */

function FormTemplateList({ onNew, onEdit }: { onNew: () => void; onEdit: (id: string) => void }) {
  const qc = useQueryClient();
  const listQ = useQuery({ queryKey: LIST_KEY, queryFn: formTemplatesApi.list });
  const queuesQ = useQuery({ queryKey: ["taxonomy", "queues"], queryFn: () => taxonomyApi.queues.list() });
  const queueName = React.useMemo(() => new Map((queuesQ.data ?? []).map((q) => [q.id, q.name])), [queuesQ.data]);
  const [confirmDelete, setConfirmDelete] = React.useState<string | null>(null);

  const duplicate = useMutation({
    mutationFn: (id: string) => formTemplatesApi.duplicate(id),
    onSuccess: (t) => {
      void qc.invalidateQueries({ queryKey: LIST_KEY });
      toast.success(`Duplicated as “${t.name}” (inactive)`);
    },
    onError: () => toast.error("Could not duplicate the form."),
  });
  const remove = useMutation({
    mutationFn: (id: string) => formTemplatesApi.remove(id),
    onSuccess: () => {
      setConfirmDelete(null);
      void qc.invalidateQueries({ queryKey: LIST_KEY });
      toast.success("Form deleted");
    },
    onError: () => toast.error("Could not delete the form."),
  });

  const rows = listQ.data ?? [];
  return (
    <section className="glass-card p-5">
      <div className="mb-4 flex items-start justify-between gap-4">
        <div className="space-y-1">
          <h2 className="text-sm font-medium uppercase tracking-wide text-muted-foreground">Form templates</h2>
          <p className="text-xs text-muted-foreground/70">
            Queue scope decides where a form is offered (empty = every queue). Inactive forms stay here but are
            hidden from agents.
          </p>
        </div>
        <Button type="button" size="sm" onClick={onNew}>
          <Plus className="h-4 w-4" /> New form
        </Button>
      </div>

      {listQ.isLoading ? (
        <Skeleton className="h-14 w-full" />
      ) : rows.length === 0 ? (
        <div className="rounded-lg border border-dashed border-glass-strong px-6 py-10 text-center">
          <ListChecks className="mx-auto h-6 w-6 text-muted-foreground/50" />
          <p className="mt-2 text-sm font-medium">No forms yet</p>
          <p className="mx-auto mt-1 max-w-sm text-xs text-muted-foreground">
            Build a form for recurring intakes — a laptop delivery, an on-site visit, a hardware order — and agents
            fill it in straight from the composer.
          </p>
        </div>
      ) : (
        <ul className="flex flex-col gap-2">
          {rows.map((t: FormTemplateSummary) => (
            <li key={t.id} className={cn(
              "flex items-center gap-4 rounded-lg border px-4 py-3 transition-colors",
              t.isActive ? "border-glass-strong bg-glass" : "border-glass bg-glass opacity-70",
            )}>
              <span className="flex h-8 w-8 shrink-0 items-center justify-center rounded-md border border-primary/20 bg-primary/8 text-primary">
                <ListChecks className="h-4 w-4" />
              </span>
              <button type="button" className="min-w-0 flex-1 text-left" onClick={() => onEdit(t.id)}>
                <div className="flex items-center gap-2">
                  <span className="truncate text-sm font-medium text-foreground">{t.name}</span>
                  {!t.isActive && (
                    <span className="rounded-full bg-glass-strong px-2 py-0.5 text-[10px] uppercase tracking-wider text-muted-foreground">Inactive</span>
                  )}
                </div>
                <p className="mt-0.5 truncate text-[11px] text-muted-foreground/70">
                  {t.fieldCount} field{t.fieldCount === 1 ? "" : "s"}
                  <span className="mx-1.5 opacity-40">·</span>
                  {t.queueIds.length === 0 ? "All queues" : t.queueIds.map((id) => queueName.get(id) ?? "Unknown").join(", ")}
                  {t.description && <><span className="mx-1.5 opacity-40">·</span>{t.description}</>}
                </p>
              </button>
              {confirmDelete === t.id ? (
                <div className="flex items-center gap-2">
                  <span className="text-xs text-muted-foreground">Delete? Filled forms are kept.</span>
                  <Button size="sm" variant="ghost" onClick={() => setConfirmDelete(null)}>Cancel</Button>
                  <Button size="sm" variant="destructive" disabled={remove.isPending} onClick={() => remove.mutate(t.id)}>Delete</Button>
                </div>
              ) : (
                <div className="flex items-center gap-1">
                  <Button variant="ghost" size="sm" onClick={() => onEdit(t.id)}><Pencil className="h-4 w-4" /> Edit</Button>
                  <Button variant="ghost" size="sm" aria-label={`Duplicate ${t.name}`} disabled={duplicate.isPending} onClick={() => duplicate.mutate(t.id)}>
                    <Copy className="h-4 w-4" />
                  </Button>
                  <Button variant="ghost" size="sm" aria-label={`Delete ${t.name}`} onClick={() => setConfirmDelete(t.id)}>
                    <Trash2 className="h-4 w-4" />
                  </Button>
                </div>
              )}
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

/* ─── editor ─── */

function FormTemplateEditor({ templateId, onDone }: { templateId: string | null; onDone: () => void }) {
  const qc = useQueryClient();
  const existingQ = useQuery({
    queryKey: ["form-templates", "detail", templateId],
    queryFn: () => formTemplatesApi.get(templateId!),
    enabled: templateId !== null,
  });
  const queuesQ = useQuery({ queryKey: ["taxonomy", "queues"], queryFn: () => taxonomyApi.queues.list() });

  const [name, setName] = React.useState("");
  const [description, setDescription] = React.useState("");
  const [isActive, setIsActive] = React.useState(true);
  const [queueIds, setQueueIds] = React.useState<string[]>([]);
  const [fields, setFields] = React.useState<FormField[]>([]);
  const [openFieldId, setOpenFieldId] = React.useState<string | null>(null);
  const [paletteOpen, setPaletteOpen] = React.useState(false);
  const [loaded, setLoaded] = React.useState(templateId === null);

  React.useEffect(() => {
    const t = existingQ.data;
    if (!t || loaded) return;
    setName(t.name);
    setDescription(t.description);
    setIsActive(t.isActive);
    setQueueIds(t.queueIds);
    setFields(t.fields.map((f) => ({ ...f, options: f.options ?? null })));
    setLoaded(true);
  }, [existingQ.data, loaded]);

  const save = useMutation({
    mutationFn: () => {
      const body = { name: name.trim(), description: description.trim(), isActive, queueIds, fields };
      return templateId ? formTemplatesApi.update(templateId, body) : formTemplatesApi.create(body);
    },
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: LIST_KEY });
      void qc.invalidateQueries({ queryKey: ["form-templates", "detail", templateId] });
      toast.success(templateId ? "Form saved" : "Form created");
      onDone();
    },
    onError: (err) => toast.error(formApiError(err)?.error ?? "Could not save the form."),
  });

  const sensors = useSensors(
    useSensor(PointerSensor, { activationConstraint: { distance: 4 } }),
    useSensor(KeyboardSensor, { coordinateGetter: sortableKeyboardCoordinates }),
  );
  const onDragEnd = (e: DragEndEvent) => {
    const { active, over } = e;
    if (!over || active.id === over.id) return;
    setFields((fs) => {
      const from = fs.findIndex((f) => f.id === active.id);
      const to = fs.findIndex((f) => f.id === over.id);
      return from < 0 || to < 0 ? fs : arrayMove(fs, from, to);
    });
  };

  const patch = (id: string, p: Partial<FormField>) => setFields((fs) => fs.map((f) => (f.id === id ? { ...f, ...p } : f)));
  const addField = (type: FormFieldType) => {
    const f = newField(type);
    setFields((fs) => [...fs, f]);
    setOpenFieldId(f.id);
    setPaletteOpen(false);
  };

  const inputCount = fields.filter((f) => f.type !== "SectionHeader").length;
  const labelsOk = fields.every((f) => f.label.trim().length > 0)
    && fields.every((f) => !hasOptions(f.type) || (f.options ?? []).some((o) => o.label.trim()));
  const canSave = name.trim().length > 0 && inputCount > 0 && labelsOk && !save.isPending;

  if (templateId && !loaded) {
    return <Skeleton className="h-64 w-full" />;
  }

  return (
    <div className="flex flex-col gap-5">
      <div className="flex items-center justify-between gap-4">
        <Button variant="ghost" size="sm" onClick={onDone}>
          <ArrowLeft className="h-4 w-4" /> Back to forms
        </Button>
        <div className="flex items-center gap-2">
          {!canSave && name.trim() && (
            <span className="text-xs text-muted-foreground">
              {inputCount === 0 ? "Add at least one input field." : !labelsOk ? "Every field needs a label (and options)." : ""}
            </span>
          )}
          <Button variant="ghost" size="sm" onClick={onDone}>Cancel</Button>
          <Button size="sm" disabled={!canSave} onClick={() => save.mutate()}>
            {templateId ? "Save form" : "Create form"}
          </Button>
        </div>
      </div>

      <div className="grid items-start gap-5 xl:grid-cols-[minmax(0,1fr)_minmax(0,26rem)]">
        <div className="flex min-w-0 flex-col gap-5">
          <section className="glass-card space-y-4 p-5">
            <div className="grid gap-4 sm:grid-cols-[minmax(0,1fr)_auto]">
              <label className="space-y-1.5">
                <span className="text-xs font-medium text-muted-foreground">Name</span>
                <Input value={name} maxLength={200} onChange={(e) => setName(e.target.value)} placeholder="e.g. Laptop delivery" />
              </label>
              <label className="flex items-end gap-3 pb-1.5">
                <Switch checked={isActive} onCheckedChange={setIsActive} />
                <span className="text-sm">{isActive ? "Active" : "Inactive"}</span>
              </label>
            </div>
            <label className="block space-y-1.5">
              <span className="text-xs font-medium text-muted-foreground">Description</span>
              <Input value={description} maxLength={2000} onChange={(e) => setDescription(e.target.value)}
                placeholder="Shown to agents in the :: picker and at the top of the form" />
            </label>
            <div className="space-y-2">
              <div className="flex items-center justify-between">
                <span className="text-xs font-medium text-muted-foreground">Available in queues</span>
                {queueIds.length > 0 && (
                  <button type="button" onClick={() => setQueueIds([])} className="text-[11px] text-muted-foreground hover:text-foreground">
                    Clear scope (all queues)
                  </button>
                )}
              </div>
              <div className="flex flex-wrap gap-2">
                {(queuesQ.data ?? []).map((q) => {
                  const on = queueIds.includes(q.id);
                  return (
                    <button key={q.id} type="button" aria-pressed={on}
                      onClick={() => setQueueIds((ids) => (on ? ids.filter((x) => x !== q.id) : [...ids, q.id]))}
                      className={cn("inline-flex items-center gap-1.5 rounded-full border px-3 py-1 text-xs transition-colors",
                        on ? "border-primary/40 bg-primary/12 text-foreground" : "border-glass-strong bg-glass text-muted-foreground hover:bg-glass-hover")}>
                      {on && <Check className="h-3.5 w-3.5" />}
                      {q.name}
                    </button>
                  );
                })}
              </div>
              <p className="text-[11px] text-muted-foreground/70">Leave empty to offer the form in every queue.</p>
            </div>
          </section>

          <section className="glass-card p-5">
            <div className="mb-3 flex items-center justify-between">
              <h2 className="text-sm font-medium uppercase tracking-wide text-muted-foreground">
                Fields <span className="ml-1 tabular-nums text-muted-foreground/60">{fields.length}</span>
              </h2>
              <span className="text-[11px] text-muted-foreground/60">Drag to reorder</span>
            </div>

            {fields.length === 0 && (
              <p className="mb-3 rounded-md border border-dashed border-glass-strong px-4 py-6 text-center text-xs text-muted-foreground">
                No fields yet — add the first one below.
              </p>
            )}

            <DndContext sensors={sensors} collisionDetection={closestCenter} onDragEnd={onDragEnd}>
              <SortableContext items={fields.map((f) => f.id)} strategy={verticalListSortingStrategy}>
                <ul className="space-y-2">
                  {fields.map((f) => (
                    <FieldRow
                      key={f.id}
                      field={f}
                      open={openFieldId === f.id}
                      onToggle={() => setOpenFieldId((id) => (id === f.id ? null : f.id))}
                      onChange={(p) => patch(f.id, p)}
                      onRemove={() => setFields((fs) => fs.filter((x) => x.id !== f.id))}
                      onDuplicate={() => setFields((fs) => {
                        const i = fs.findIndex((x) => x.id === f.id);
                        const copy: FormField = {
                          ...f,
                          id: newId(),
                          label: `${f.label} (copy)`,
                          options: f.options?.map((o) => ({ ...o, id: newOptionId() })) ?? null,
                        };
                        return [...fs.slice(0, i + 1), copy, ...fs.slice(i + 1)];
                      })}
                    />
                  ))}
                </ul>
              </SortableContext>
            </DndContext>

            <div className="mt-3">
              {paletteOpen ? (
                <div className="rounded-lg border border-glass-strong bg-glass p-2">
                  <div className="mb-1.5 flex items-center justify-between px-1">
                    <span className="text-[10px] font-semibold uppercase tracking-wider text-muted-foreground/70">Add a field</span>
                    <button type="button" aria-label="Close" onClick={() => setPaletteOpen(false)} className="rounded p-1 text-muted-foreground hover:bg-glass-hover">
                      <X className="h-3.5 w-3.5" />
                    </button>
                  </div>
                  <div className="grid gap-1 sm:grid-cols-2">
                    {FORM_FIELD_TYPES.map((t) => {
                      const Icon = TYPE_ICON[t];
                      return (
                        <button key={t} type="button" onClick={() => addField(t)}
                          className="flex items-start gap-2.5 rounded-md px-2.5 py-2 text-left transition-colors hover:bg-glass-hover">
                          <Icon className="mt-0.5 h-4 w-4 shrink-0 text-primary/80" />
                          <span className="min-w-0">
                            <span className="block text-sm font-medium">{FORM_FIELD_TYPE_LABEL[t]}</span>
                            <span className="block text-[11px] leading-snug text-muted-foreground/80">{TYPE_HINT[t]}</span>
                          </span>
                        </button>
                      );
                    })}
                  </div>
                </div>
              ) : (
                <button type="button" onClick={() => setPaletteOpen(true)}
                  className="flex w-full items-center justify-center gap-2 rounded-lg border border-dashed border-glass-strong py-2.5 text-sm text-muted-foreground transition-colors hover:border-primary/40 hover:bg-glass-hover hover:text-foreground">
                  <Plus className="h-4 w-4" /> Add field
                </button>
              )}
            </div>
          </section>
        </div>

        <aside className="glass-card sticky top-4 min-w-0 p-5">
          <div className="mb-4 flex items-center justify-between">
            <h2 className="text-sm font-medium uppercase tracking-wide text-muted-foreground">Preview</h2>
            <span className="text-[11px] text-muted-foreground/60">What agents see</span>
          </div>
          <div className="mb-4 border-b border-glass pb-3">
            <div className="text-base font-semibold">{name.trim() || "Untitled form"}</div>
            {description.trim() && <p className="mt-0.5 text-xs text-muted-foreground">{description}</p>}
          </div>
          {fields.length === 0 ? (
            <p className="py-8 text-center text-xs text-muted-foreground/60">Fields you add appear here.</p>
          ) : (
            <FormPreview fields={fields} />
          )}
        </aside>
      </div>
    </div>
  );
}

function FieldRow({
  field: f,
  open,
  onToggle,
  onChange,
  onRemove,
  onDuplicate,
}: {
  field: FormField;
  open: boolean;
  onToggle: () => void;
  onChange: (p: Partial<FormField>) => void;
  onRemove: () => void;
  onDuplicate: () => void;
}) {
  const { attributes, listeners, setNodeRef, transform, transition, isDragging } = useSortable({ id: f.id });
  const Icon = TYPE_ICON[f.type];
  const isHeader = f.type === "SectionHeader";

  return (
    <li ref={setNodeRef} style={{ transform: CSS.Transform.toString(transform), transition }}
      className={cn("sd-form-editor-row rounded-lg border bg-glass transition-shadow",
        open ? "border-primary/30" : "border-glass-strong",
        isDragging && "relative z-10 shadow-lg",
        isHeader && !open && "bg-glass-strong")}>
      <div className="flex items-center gap-2 px-2 py-2">
        <button type="button" aria-label={`Move ${f.label}`} {...attributes} {...listeners}
          className="cursor-grab touch-none rounded p-1 text-muted-foreground/50 hover:text-muted-foreground active:cursor-grabbing">
          <GripVertical className="h-4 w-4" />
        </button>
        <button type="button" onClick={onToggle} className="flex min-w-0 flex-1 items-center gap-2.5 text-left">
          <Icon className="h-4 w-4 shrink-0 text-primary/80" />
          <span className={cn("min-w-0 flex-1 truncate text-sm", isHeader ? "font-semibold uppercase tracking-wide text-[12px]" : "font-medium")}>
            {f.label || <span className="text-muted-foreground">Untitled</span>}
          </span>
          <span className="hidden shrink-0 text-[11px] text-muted-foreground/70 sm:block">{FORM_FIELD_TYPE_LABEL[f.type]}</span>
          {f.required && <span className="shrink-0 rounded-full border border-rose-400/40 px-1.5 py-0.5 text-[9px] font-semibold uppercase tracking-wider text-rose-600 dark:text-rose-300">Required</span>}
          {f.width === "half" && <span className="shrink-0 rounded-full border border-glass-strong px-1.5 py-0.5 text-[9px] uppercase tracking-wider text-muted-foreground">½</span>}
          <ChevronDown className={cn("h-4 w-4 shrink-0 text-muted-foreground/60 transition-transform", open && "rotate-180")} />
        </button>
      </div>

      {open && (
        <div className="space-y-3 border-t border-glass px-4 pb-4 pt-3">
          <div className="grid gap-3 sm:grid-cols-2">
            <label className="space-y-1 sm:col-span-2">
              <span className="text-[11px] font-medium text-muted-foreground">{isHeader ? "Heading" : "Label"}</span>
              <input value={f.label} maxLength={300} onChange={(e) => onChange({ label: e.target.value })} className={cn(formInputCn, "h-9")} />
            </label>
            <label className="space-y-1 sm:col-span-2">
              <span className="text-[11px] font-medium text-muted-foreground">Help text</span>
              <input value={f.help} maxLength={1000} onChange={(e) => onChange({ help: e.target.value })}
                placeholder="Optional hint shown under the field" className={cn(formInputCn, "h-9")} />
            </label>
          </div>

          {hasOptions(f.type) && <OptionsEditor field={f} onChange={onChange} />}

          {f.type === "Number" && (
            <div className="grid grid-cols-3 gap-3">
              <NumProp label="Minimum" value={f.min} onChange={(v) => onChange({ min: v })} />
              <NumProp label="Maximum" value={f.max} onChange={(v) => onChange({ max: v })} />
              <NumProp label="Decimals" value={f.decimals ?? 0} onChange={(v) => onChange({ decimals: Math.max(0, Math.min(6, v ?? 0)) })} />
            </div>
          )}
          {f.type === "Article" && (
            <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
              <NumProp label="Min. quantity" value={f.min} onChange={(v) => onChange({ min: v })} />
              <NumProp label="Max. quantity" value={f.max} onChange={(v) => onChange({ max: v })} />
              <NumProp label="Min. articles" value={f.minRows} onChange={(v) => onChange({ minRows: v })} />
              <NumProp label="Max. articles" value={f.maxRows} onChange={(v) => onChange({ maxRows: v })} />
            </div>
          )}
          {(f.type === "ShortText" || f.type === "LongText" || f.type === "Number") && (
            <label className="block space-y-1">
              <span className="text-[11px] font-medium text-muted-foreground">Default value</span>
              <input value={f.defaultValue} maxLength={500} onChange={(e) => onChange({ defaultValue: e.target.value })} className={cn(formInputCn, "h-9")} />
            </label>
          )}
          {f.type === "Dropdown" && (
            <label className="block space-y-1">
              <span className="text-[11px] font-medium text-muted-foreground">Default option</span>
              <select value={f.defaultValue} onChange={(e) => onChange({ defaultValue: e.target.value })} className={cn(formInputCn, "h-9")}>
                <option value="">None</option>
                {(f.options ?? []).map((o) => <option key={o.id} value={o.id}>{o.label}</option>)}
              </select>
            </label>
          )}

          <div className="flex flex-wrap items-center gap-x-5 gap-y-2 pt-1">
            {!isHeader && (
              <>
                <label className="flex items-center gap-2 text-sm">
                  <Switch checked={f.required} onCheckedChange={(v) => onChange({ required: v })} />
                  Required
                </label>
                <label className="flex items-center gap-2 text-sm">
                  <Switch checked={f.width === "half"} onCheckedChange={(v) => onChange({ width: v ? "half" : "full" })} />
                  Half width
                </label>
                {f.type === "Checkbox" && (
                  <label className="flex items-center gap-2 text-sm">
                    <Switch checked={f.defaultValue === "true"} onCheckedChange={(v) => onChange({ defaultValue: v ? "true" : "" })} />
                    Ticked by default
                  </label>
                )}
              </>
            )}
            <div className="ml-auto flex gap-1">
              <Button variant="ghost" size="sm" onClick={onDuplicate}><Copy className="h-3.5 w-3.5" /> Duplicate</Button>
              <Button variant="ghost" size="sm" className="text-rose-600 hover:text-rose-600 dark:text-rose-400" onClick={onRemove}>
                <Trash2 className="h-3.5 w-3.5" /> Remove
              </Button>
            </div>
          </div>
        </div>
      )}
    </li>
  );
}

function NumProp({ label, value, onChange }: { label: string; value: number | null | undefined; onChange: (v: number | null) => void }) {
  return (
    <label className="block space-y-1">
      <span className="text-[11px] font-medium text-muted-foreground">{label}</span>
      <input type="number" value={value ?? ""} onChange={(e) => onChange(e.target.value === "" ? null : Number(e.target.value))}
        className={cn(formInputCn, "h-9 tabular-nums")} />
    </label>
  );
}

function OptionsEditor({ field: f, onChange }: { field: FormField; onChange: (p: Partial<FormField>) => void }) {
  const options = f.options ?? [];
  const set = (next: typeof options) => onChange({ options: next });
  return (
    <div className="space-y-1.5">
      <div className="flex items-center justify-between">
        <span className="text-[11px] font-medium text-muted-foreground">Options</span>
        <span className="text-[10px] text-muted-foreground/60">Paste a list to add several at once</span>
      </div>
      <ul className="space-y-1">
        {options.map((o, i) => (
          <li key={o.id} className="flex items-center gap-1.5">
            <span className="w-5 text-right text-[10px] tabular-nums text-muted-foreground/50">{i + 1}</span>
            <input value={o.label} maxLength={200}
              onChange={(e) => set(options.map((x) => (x.id === o.id ? { ...x, label: e.target.value } : x)))}
              onPaste={(e) => {
                const lines = e.clipboardData.getData("text").split(/\r?\n/).map((l) => l.trim()).filter(Boolean);
                if (lines.length < 2) return;
                e.preventDefault();
                const added = lines.slice(1).map((label) => ({ id: newOptionId(), label }));
                set([...options.slice(0, i), { ...o, label: lines[0] }, ...added, ...options.slice(i + 1)]);
              }}
              className={cn(formInputCn, "h-8 flex-1")} />
            <button type="button" aria-label="Move up" disabled={i === 0}
              onClick={() => set(arrayMove(options, i, i - 1))}
              className="rounded p-1 text-muted-foreground/60 hover:bg-glass-hover disabled:opacity-30">
              <ChevronDown className="h-3.5 w-3.5 rotate-180" />
            </button>
            <button type="button" aria-label="Remove option" disabled={options.length <= 1}
              onClick={() => set(options.filter((x) => x.id !== o.id))}
              className="rounded p-1 text-muted-foreground/60 hover:bg-rose-500/10 hover:text-rose-500 disabled:opacity-30">
              <X className="h-3.5 w-3.5" />
            </button>
          </li>
        ))}
      </ul>
      <button type="button" onClick={() => set([...options, { id: newOptionId(), label: `Option ${options.length + 1}` }])}
        className="ml-6 inline-flex items-center gap-1 text-xs text-primary hover:underline">
        <Plus className="h-3 w-3" /> Add option
      </button>
    </div>
  );
}
