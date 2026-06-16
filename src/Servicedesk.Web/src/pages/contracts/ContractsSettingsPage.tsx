import { useState } from "react";
import { ArrowLeft, Mail, Pencil, Plus, Trash2 } from "lucide-react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { contractReportsApi, type ReportTemplate } from "@/lib/contractReports-api";
import { ReportTemplateEditor } from "./reports/ReportTemplateEditor";

const LIST_QUERY_KEY = ["contract-report-templates", "list"] as const;

type Mode =
  | { kind: "list" }
  | { kind: "new" }
  | { kind: "edit"; templateId: string };

export function ContractsSettingsPage() {
  const [mode, setMode] = useState<Mode>({ kind: "list" });

  return (
    <div className="flex flex-1 flex-col gap-6 p-4 sm:p-6">
      <div className="space-y-1">
        <div className="mb-2 text-primary">
          <Mail className="h-6 w-6" />
        </div>
        <h1 className="text-display-md font-semibold text-foreground">Contract settings</h1>
        <p className="max-w-xl text-sm text-muted-foreground">
          Manage report email templates and reporting-contact configuration.
        </p>
      </div>

      {mode.kind === "list" && (
        <TemplateListPanel
          onNew={() => setMode({ kind: "new" })}
          onEdit={(id) => setMode({ kind: "edit", templateId: id })}
        />
      )}
      {mode.kind === "new" && (
        <TemplateEditorPanel templateId={null} onDone={() => setMode({ kind: "list" })} />
      )}
      {mode.kind === "edit" && (
        <TemplateEditorPanel
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
  const listQ = useQuery({
    queryKey: LIST_QUERY_KEY,
    queryFn: () => contractReportsApi.templates.list(true),
  });

  const remove = useMutation({
    mutationFn: (id: string) => contractReportsApi.templates.remove(id),
    onSuccess: () => {
      toast.success("Template deleted.");
      qc.invalidateQueries({ queryKey: LIST_QUERY_KEY });
    },
    onError: () => toast.error("Could not delete template."),
  });

  const rows = listQ.data ?? [];

  return (
    <section className="rounded-lg border border-glass-strong bg-glass p-5">
      <header className="mb-4 flex items-center justify-between gap-4">
        <div className="space-y-1">
          <h2 className="text-xs font-medium uppercase tracking-widest text-muted-foreground/60">
            Email templates
          </h2>
          <p className="text-xs text-muted-foreground">
            Inactive templates are kept for reactivation — only active templates appear in the send dialog.
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
          No report templates yet. Create one to start sending periodic contract reports to customers.
        </p>
      )}

      <ul className="flex flex-col gap-2">
        {rows.map((t) => (
          <TemplateRow
            key={t.id}
            template={t}
            onEdit={() => onEdit(t.id)}
            onDelete={() => {
              if (confirm(`Delete '${t.name}'? This cannot be undone.`)) {
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
  onEdit,
  onDelete,
}: {
  template: ReportTemplate;
  onEdit: () => void;
  onDelete: () => void;
}) {
  return (
    <li
      className={cn(
        "flex items-center justify-between gap-4 rounded-lg border px-4 py-3",
        template.isActive
          ? "border-glass-strong bg-glass"
          : "border-glass bg-glass opacity-70",
      )}
    >
      <div className="min-w-0 flex-1">
        <div className="flex items-center gap-2">
          <span className="truncate text-sm font-medium text-foreground">{template.name}</span>
          <ScopeBadge scope={template.scope} />
          {!template.isActive && (
            <span className="rounded-full bg-glass-strong px-2 py-0.5 text-[10px] uppercase tracking-wider text-muted-foreground">
              Inactive
            </span>
          )}
        </div>
        {template.description && (
          <p className="truncate text-xs text-muted-foreground">{template.description}</p>
        )}
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

function ScopeBadge({ scope }: { scope: "all" | "unprotected" }) {
  if (scope === "unprotected") {
    return (
      <span className="rounded-full border border-amber-400/30 bg-amber-400/10 px-2 py-0.5 text-[10px] uppercase tracking-wider text-amber-200">
        Unprotected only
      </span>
    );
  }
  return (
    <span className="rounded-full border border-glass-strong bg-glass px-2 py-0.5 text-[10px] uppercase tracking-wider text-muted-foreground">
      All mailboxes
    </span>
  );
}

function TemplateEditorPanel({
  templateId,
  onDone,
}: {
  templateId: string | null;
  onDone: () => void;
}) {
  const existingQ = useQuery({
    queryKey: ["contract-report-templates", "detail", templateId],
    queryFn: () => contractReportsApi.templates.get(templateId!),
    enabled: templateId !== null,
  });

  if (templateId && existingQ.isLoading) {
    return <p className="text-sm text-muted-foreground">Loading template…</p>;
  }

  return (
    <section className="rounded-lg border border-glass-strong bg-glass p-5">
      <header className="mb-4 flex items-center justify-between gap-4">
        <div className="space-y-1">
          <h2 className="text-xs font-medium uppercase tracking-widest text-muted-foreground/60">
            {templateId ? "Edit report template" : "New report template"}
          </h2>
          <p className="text-xs text-muted-foreground">
            Templates define the subject, body and column set for a report email. Use{" "}
            <code className="font-mono">{"{{report.table}}"}</code> in the body to insert the overview table.
          </p>
        </div>
        <Button variant="ghost" size="sm" onClick={onDone}>
          <ArrowLeft className="h-4 w-4" />
          Back to list
        </Button>
      </header>
      <ReportTemplateEditor existing={existingQ.data ?? null} onDone={onDone} />
    </section>
  );
}
