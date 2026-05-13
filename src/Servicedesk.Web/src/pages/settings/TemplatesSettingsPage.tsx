import { useMemo, useState } from "react";
import { ArrowLeft, FileText, Pencil, Plus, Trash2 } from "lucide-react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { TemplateEditor } from "./templates/ComposeTemplateEditor";
import {
  composeTemplatesApi,
  type ComposeTemplate,
} from "@/lib/composeTemplates-api";
import { taxonomyApi } from "@/lib/api";

const LIST_QUERY_KEY = ["compose-templates", "list"] as const;

type Mode =
  | { kind: "list" }
  | { kind: "new" }
  | { kind: "edit"; templateId: string };

export function TemplatesSettingsPage() {
  const [mode, setMode] = useState<Mode>({ kind: "list" });

  return (
    <div className="flex flex-col gap-6">
      <header className="space-y-2">
        <div className="mb-2 text-primary">
          <FileText className="h-6 w-6" />
        </div>
        <h1 className="text-display-md font-semibold text-foreground">Templates</h1>
        <p className="max-w-xl text-sm text-muted-foreground">
          Pre-canned text blocks agents drop into an internal note, public
          reply, or outgoing mail by typing <code className="rounded bg-white/[0.06] px-1.5 py-0.5 font-mono text-[0.8em]">::</code>.
          Scope a template to one or more queues to keep the picker tight.
        </p>
      </header>

      {mode.kind === "list" && (
        <ListPanel
          onNew={() => setMode({ kind: "new" })}
          onEdit={(id) => setMode({ kind: "edit", templateId: id })}
        />
      )}
      {mode.kind === "new" && (
        <EditorPanel templateId={null} onDone={() => setMode({ kind: "list" })} />
      )}
      {mode.kind === "edit" && (
        <EditorPanel
          templateId={mode.templateId}
          onDone={() => setMode({ kind: "list" })}
        />
      )}
    </div>
  );
}

function ListPanel({
  onNew,
  onEdit,
}: {
  onNew: () => void;
  onEdit: (id: string) => void;
}) {
  const qc = useQueryClient();
  const listQ = useQuery({
    queryKey: LIST_QUERY_KEY,
    queryFn: () => composeTemplatesApi.list(true),
  });
  const queuesQ = useQuery({
    queryKey: ["taxonomy", "queues"],
    queryFn: () => taxonomyApi.queues.list(),
  });

  const queueNameById = useMemo(() => {
    const map = new Map<string, string>();
    for (const q of queuesQ.data ?? []) map.set(q.id, q.name);
    return map;
  }, [queuesQ.data]);

  const remove = useMutation({
    mutationFn: (id: string) => composeTemplatesApi.remove(id),
    onSuccess: () => {
      toast.success("Template deleted.");
      qc.invalidateQueries({ queryKey: LIST_QUERY_KEY });
    },
    onError: () => toast.error("Could not delete template."),
  });

  const rows = listQ.data ?? [];

  return (
    <section className="rounded-lg border border-white/[0.06] bg-white/[0.02] p-5">
      <header className="mb-4 flex items-center justify-between gap-4">
        <div className="space-y-1">
          <h2 className="text-xs font-medium uppercase tracking-widest text-muted-foreground/60">
            Templates
          </h2>
          <p className="text-xs text-muted-foreground">
            Inactive templates stay in the list so an admin can reactivate
            them; agents only see active templates in the <code className="font-mono">::</code> picker.
          </p>
        </div>
        <Button onClick={onNew} size="sm">
          <Plus className="h-4 w-4" />
          New template
        </Button>
      </header>

      {listQ.isLoading && <Skeleton className="h-12 w-full" />}
      {listQ.isError && (
        <p className="text-sm text-red-300">Could not load templates.</p>
      )}

      {!listQ.isLoading && rows.length === 0 && (
        <p className="text-sm text-muted-foreground">
          No templates yet. Create one to give agents quick text blocks for
          notes, replies and outgoing mail.
        </p>
      )}

      <ul className="flex flex-col gap-2">
        {rows.map((t) => (
          <TemplateRow
            key={t.id}
            template={t}
            queueNameById={queueNameById}
            onEdit={() => onEdit(t.id)}
            onDelete={() => {
              if (
                confirm(
                  `Delete '${t.name}'? Agents will no longer see it in the :: picker.`,
                )
              ) {
                remove.mutate(t.id);
              }
            }}
          />
        ))}
      </ul>
    </section>
  );
}

function TemplateRow({
  template,
  queueNameById,
  onEdit,
  onDelete,
}: {
  template: ComposeTemplate;
  queueNameById: Map<string, string>;
  onEdit: () => void;
  onDelete: () => void;
}) {
  const scopeLabel = template.queueIds.length === 0
    ? "All queues"
    : template.queueIds
        .map((id) => queueNameById.get(id) ?? "Unknown")
        .join(", ");

  return (
    <li
      className={cn(
        "flex items-center justify-between gap-4 rounded-lg border px-4 py-3",
        template.isActive
          ? "border-white/[0.06] bg-white/[0.02]"
          : "border-white/[0.04] bg-white/[0.01] opacity-70",
      )}
    >
      <div className="min-w-0 flex-1">
        <div className="flex items-center gap-2">
          <span className="truncate text-sm font-medium text-foreground">
            {template.name}
          </span>
          {!template.isActive && (
            <span className="rounded-full bg-white/10 px-2 py-0.5 text-[10px] uppercase tracking-wider text-muted-foreground">
              Inactive
            </span>
          )}
        </div>
        {template.description && (
          <p className="truncate text-xs text-muted-foreground">
            {template.description}
          </p>
        )}
        <p className="mt-1 truncate text-[11px] text-muted-foreground/60">
          {scopeLabel}
        </p>
      </div>
      <div className="flex items-center gap-2">
        <Button
          variant="ghost"
          size="sm"
          onClick={onEdit}
          aria-label={`Edit ${template.name}`}
        >
          <Pencil className="h-4 w-4" />
          Edit
        </Button>
        <Button
          variant="ghost"
          size="sm"
          onClick={onDelete}
          aria-label={`Delete ${template.name}`}
        >
          <Trash2 className="h-4 w-4" />
          Delete
        </Button>
      </div>
    </li>
  );
}

function EditorPanel({
  templateId,
  onDone,
}: {
  templateId: string | null;
  onDone: () => void;
}) {
  const existingQ = useQuery({
    queryKey: ["compose-templates", "detail", templateId],
    queryFn: () => composeTemplatesApi.get(templateId!),
    enabled: templateId !== null,
  });

  if (templateId && existingQ.isLoading) {
    return <p className="text-sm text-muted-foreground">Loading template…</p>;
  }

  return (
    <section className="rounded-lg border border-white/[0.06] bg-white/[0.02] p-5">
      <header className="mb-4 flex items-center justify-between gap-4">
        <div className="space-y-1">
          <h2 className="text-xs font-medium uppercase tracking-widest text-muted-foreground/60">
            {templateId ? "Edit template" : "New template"}
          </h2>
          <p className="text-xs text-muted-foreground">
            The body is rich text — agents will paste it in exactly as written.
            Limit queue scope to keep each agent's picker tight.
          </p>
        </div>
        <Button variant="ghost" size="sm" onClick={onDone}>
          <ArrowLeft className="h-4 w-4" />
          Back to list
        </Button>
      </header>
      <TemplateEditor existing={existingQ.data ?? null} onDone={onDone} />
    </section>
  );
}
