import { useMemo, useState } from "react";
import { ClipboardList, Plus, Pencil, Trash2, ArrowLeft } from "lucide-react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import {
  intakeFormsApi,
  type IntakeTemplate,
} from "@/lib/intakeForms-api";
import { TemplateEditor } from "./intake-forms/TemplateEditor";

export function IntakeFormsSettingsPage() {
  const [mode, setMode] = useState<
    | { kind: "list" }
    | { kind: "new" }
    | { kind: "edit"; templateId: string }
  >({ kind: "list" });

  return (
    <div className="flex flex-col gap-6">
      <header className="space-y-2">
        <div className="mb-2 text-primary">
          <ClipboardList className="h-6 w-6" />
        </div>
        <h1 className="text-display-md font-semibold text-foreground">Intake Forms</h1>
        <p className="max-w-xl text-sm text-muted-foreground">
          Reusable customer-facing questionnaires. Agents attach a template to an
          outgoing mail; the customer fills it in without logging in and the
          answers land back on the ticket timeline.
        </p>
      </header>

      {mode.kind === "list" && (
        <TemplateListPanel onNew={() => setMode({ kind: "new" })} onEdit={(id) => setMode({ kind: "edit", templateId: id })} />
      )}
      {mode.kind === "new" && (
        <EditorPanel
          templateId={null}
          onDone={() => setMode({ kind: "list" })}
        />
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

function TemplateListPanel({
  onNew,
  onEdit,
}: {
  onNew: () => void;
  onEdit: (id: string) => void;
}) {
  const qc = useQueryClient();
  const templatesQ = useQuery({
    queryKey: ["intake-forms", "templates", { includeInactive: true }],
    queryFn: () => intakeFormsApi.listTemplates(true),
  });

  const deactivate = useMutation({
    mutationFn: (id: string) => intakeFormsApi.deactivateTemplate(id),
    onSuccess: () => {
      toast.success("Template deactivated.");
      qc.invalidateQueries({ queryKey: ["intake-forms", "templates"] });
    },
    onError: () => toast.error("Could not deactivate template."),
  });

  const rows = useMemo(() => templatesQ.data ?? [], [templatesQ.data]);

  return (
    <section className="rounded-lg border border-white/[0.06] bg-white/[0.02] p-5">
      <header className="mb-4 flex items-center justify-between gap-4">
        <div className="space-y-1">
          <h2 className="text-xs font-medium uppercase tracking-widest text-muted-foreground/60">
            Templates
          </h2>
          <p className="text-xs text-muted-foreground">
            Create, edit and deactivate templates. Inactive templates stay
            referenced by past submissions but can't be sent to new customers.
          </p>
        </div>
        <Button onClick={onNew} size="sm">
          <Plus className="h-4 w-4" />
          New template
        </Button>
      </header>

      {templatesQ.isLoading && (
        <p className="text-sm text-muted-foreground">Loading…</p>
      )}
      {templatesQ.isError && (
        <p className="text-sm text-red-300">Could not load templates.</p>
      )}

      {rows.length === 0 && !templatesQ.isLoading && (
        <p className="text-sm text-muted-foreground">
          No templates yet. Create one to let agents send intake forms from a
          ticket.
        </p>
      )}

      <ul className="flex flex-col gap-2">
        {rows.map((t) => (
          <li
            key={t.id}
            className={cn(
              "flex items-center justify-between gap-4 rounded-lg border px-4 py-3",
              t.isActive
                ? "border-white/[0.06] bg-white/[0.02]"
                : "border-white/[0.04] bg-white/[0.01] opacity-70",
            )}
          >
            <div className="min-w-0 flex-1">
              <div className="flex items-center gap-2">
                <span className="truncate text-sm font-medium text-foreground">
                  {t.name}
                </span>
                {!t.isActive && (
                  <span className="rounded-full bg-white/10 px-2 py-0.5 text-[10px] uppercase tracking-wider text-muted-foreground">
                    Inactive
                  </span>
                )}
              </div>
              {t.description && (
                <p className="truncate text-xs text-muted-foreground">
                  {t.description}
                </p>
              )}
              <p className="mt-1 text-[11px] text-muted-foreground/60">
                {t.questions.length} question{t.questions.length === 1 ? "" : "s"}
              </p>
            </div>
            <div className="flex items-center gap-2">
              <Button
                variant="ghost"
                size="sm"
                onClick={() => onEdit(t.id)}
                aria-label={`Edit ${t.name}`}
              >
                <Pencil className="h-4 w-4" />
                Edit
              </Button>
              {t.isActive && (
                <Button
                  variant="ghost"
                  size="sm"
                  onClick={() => {
                    if (
                      confirm(
                        `Deactivate '${t.name}'? Past submissions stay visible; new mails can no longer use this template.`,
                      )
                    ) {
                      deactivate.mutate(t.id);
                    }
                  }}
                  aria-label={`Deactivate ${t.name}`}
                >
                  <Trash2 className="h-4 w-4" />
                  Deactivate
                </Button>
              )}
            </div>
          </li>
        ))}
      </ul>
    </section>
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
    queryKey: ["intake-forms", "template", templateId],
    queryFn: () => intakeFormsApi.getTemplate(templateId!),
    enabled: templateId !== null,
  });

  const existing: IntakeTemplate | null = templateId
    ? existingQ.data ?? null
    : null;

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
            Questions are shown to the customer in the order listed. Section
            headers don't collect input.
          </p>
        </div>
        <Button variant="ghost" size="sm" onClick={onDone}>
          <ArrowLeft className="h-4 w-4" />
          Back to list
        </Button>
      </header>
      <TemplateEditor existing={existing} onDone={onDone} />
    </section>
  );
}
