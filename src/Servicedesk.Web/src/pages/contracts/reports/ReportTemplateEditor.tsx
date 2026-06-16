import { useRef, useState } from "react";
import { ChevronDown, Save, Tag } from "lucide-react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Switch } from "@/components/ui/switch";
import { RichTextEditor } from "@/components/RichTextEditor";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { taxonomyApi } from "@/lib/api";
import { contractReportsApi, type ReportTemplate, type ReportColumn } from "@/lib/contractReports-api";

type EditorHandle = {
  chain: () => {
    focus: () => {
      insertContent: (content: string) => { run: () => void };
    };
  };
};

const LIST_QUERY_KEY = ["contract-report-templates", "list"] as const;

export function ReportTemplateEditor({
  existing,
  onDone,
}: {
  existing: ReportTemplate | null;
  onDone: () => void;
}) {
  const qc = useQueryClient();

  const [name, setName] = useState(existing?.name ?? "");
  const [description, setDescription] = useState(existing?.description ?? "");
  const [subject, setSubject] = useState(existing?.subject ?? "");
  const [bodyHtml, setBodyHtml] = useState(existing?.bodyHtml ?? "");
  const [queueId, setQueueId] = useState<string | null>(existing?.queueId ?? null);
  const [columns, setColumns] = useState<string[]>(existing?.columns ?? []);
  const [scope, setScope] = useState<"all" | "unprotected">(existing?.scope ?? "all");
  const [attachPdf, setAttachPdf] = useState(existing?.attachPdf ?? true);
  const [isActive, setIsActive] = useState(existing?.isActive ?? true);

  const subjectRef = useRef<HTMLInputElement | null>(null);
  const bodyEditorRef = useRef<EditorHandle | null>(null);

  const tokensQ = useQuery({
    queryKey: ["contract-report-tokens"],
    queryFn: () => contractReportsApi.tokens(),
    staleTime: Infinity,
  });
  const tokens = tokensQ.data?.tokens ?? [];

  const columnsQ = useQuery({
    queryKey: ["contract-report-columns"],
    queryFn: () => contractReportsApi.columns(),
    staleTime: Infinity,
  });
  const availableColumns: ReportColumn[] = columnsQ.data?.columns ?? [];

  const defaultsQ = useQuery({
    queryKey: ["contract-report-defaults"],
    queryFn: () => contractReportsApi.defaults(),
    staleTime: Infinity,
    enabled: existing === null,
  });

  // On first render for a new template, apply defaults
  const defaultsApplied = useRef(false);
  if (!defaultsApplied.current && existing === null && defaultsQ.data) {
    defaultsApplied.current = true;
    setColumns(defaultsQ.data.columns);
  }

  const queuesQ = useQuery({
    queryKey: ["taxonomy", "queues"],
    queryFn: () => taxonomyApi.queues.list(),
  });
  const queues = queuesQ.data ?? [];

  const insertSubjectToken = (token: string) => {
    const el = subjectRef.current;
    if (!el) {
      setSubject((s) => s + token);
      return;
    }
    const start = el.selectionStart ?? subject.length;
    const end = el.selectionEnd ?? subject.length;
    const next = subject.slice(0, start) + token + subject.slice(end);
    setSubject(next);
    requestAnimationFrame(() => {
      el.focus();
      el.setSelectionRange(start + token.length, start + token.length);
    });
  };

  const insertBodyToken = (token: string) => {
    const editor = bodyEditorRef.current;
    if (!editor) return;
    editor.chain().focus().insertContent(token).run();
  };

  const toggleColumn = (key: string) => {
    setColumns((prev) =>
      prev.includes(key) ? prev.filter((c) => c !== key) : [...prev, key],
    );
  };

  const save = useMutation({
    mutationFn: async () => {
      const payload = {
        name: name.trim(),
        description: description.trim() ? description.trim() : null,
        subject: subject.trim(),
        bodyHtml,
        queueId: queueId || null,
        columns,
        scope,
        attachPdf,
        isActive,
      };
      return existing
        ? contractReportsApi.templates.update(existing.id, payload)
        : contractReportsApi.templates.create(payload);
    },
    onSuccess: () => {
      toast.success(existing ? "Template updated." : "Template created.");
      qc.invalidateQueries({ queryKey: LIST_QUERY_KEY });
      onDone();
    },
    onError: (err: unknown) => {
      const e = err as { status?: number; body?: { error?: string } };
      if (e.status === 409) {
        toast.error(e.body?.error ?? "A template with this name already exists.");
      } else {
        toast.error(e.body?.error ?? "Save failed.");
      }
    },
  });

  const canSave = name.trim().length > 0 && !save.isPending;

  return (
    <div className="flex flex-col gap-5">
      <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
        <div className="space-y-1.5">
          <label className="text-xs font-medium text-muted-foreground">Name</label>
          <Input
            value={name}
            onChange={(e) => setName(e.target.value)}
            placeholder="e.g. Monthly M365 report"
            maxLength={200}
          />
        </div>
        <div className="space-y-1.5">
          <label className="text-xs font-medium text-muted-foreground">Description</label>
          <Input
            value={description ?? ""}
            onChange={(e) => setDescription(e.target.value)}
            placeholder="Short description (optional)"
            maxLength={2000}
          />
        </div>
      </div>

      <div className="space-y-1.5">
        <div className="flex items-center justify-between">
          <label className="text-xs font-medium text-muted-foreground">Email subject</label>
          <TokenDropdown
            tokens={tokens}
            onSelect={insertSubjectToken}
            title="Insert variable into subject"
          />
        </div>
        <Input
          ref={subjectRef}
          value={subject}
          onChange={(e) => setSubject(e.target.value)}
          placeholder="e.g. Microsoft 365 backup report — {{company.name}}"
          maxLength={500}
        />
      </div>

      <div className="space-y-1.5">
        <div className="flex items-center justify-between">
          <label className="text-xs font-medium text-muted-foreground">Email body</label>
          <TokenDropdown
            tokens={tokens}
            onSelect={insertBodyToken}
            title="Insert variable into body — use {{report.table}} for the overview table"
          />
        </div>
        <RichTextEditor
          content={bodyHtml}
          onChange={setBodyHtml}
          placeholder="Email body. Insert {{report.table}} where the overview table should appear."
          minHeight="200px"
          maxHeight="420px"
          onEditorReady={(editor) => {
            bodyEditorRef.current = editor as unknown as EditorHandle | null;
          }}
        />
        <p className="text-[11px] text-muted-foreground/70">
          Use <code className="font-mono">{"{{report.table}}"}</code> to insert the per-mailbox overview table. Summary tokens like <code className="font-mono">{"{{report.mailboxCount}}"}</code> resolve server-side.
        </p>
      </div>

      <div className="space-y-1.5">
        <label className="text-xs font-medium text-muted-foreground">Send from (queue mailbox)</label>
        <p className="text-xs text-muted-foreground">
          The report is sent from the selected queue's outbound mailbox. A queue with a mailbox is required to send a report.
        </p>
        <Select
          value={queueId ?? "__none"}
          onValueChange={(v) => setQueueId(v === "__none" ? null : v)}
        >
          <SelectTrigger className="h-9 bg-glass">
            <SelectValue placeholder="No mailbox (cannot send)" />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="__none">No mailbox (cannot send)</SelectItem>
            {queues.map((q) => (
              <SelectItem key={q.id} value={q.id}>
                {q.name}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
      </div>

      <div className="space-y-2">
        <label className="text-xs font-medium text-muted-foreground">Columns</label>
        <p className="text-xs text-muted-foreground">
          Select the columns that appear in the <code className="font-mono">{"{{report.table}}"}</code> overview.
        </p>
        {columnsQ.isLoading ? (
          <p className="text-xs text-muted-foreground">Loading columns…</p>
        ) : availableColumns.length === 0 ? (
          <p className="text-xs text-muted-foreground">No columns available.</p>
        ) : (
          <div className="flex flex-wrap gap-2">
            {availableColumns.map((col) => {
              const selected = columns.includes(col.key);
              return (
                <button
                  key={col.key}
                  type="button"
                  onClick={() => toggleColumn(col.key)}
                  className={cn(
                    "inline-flex items-center gap-1.5 rounded-full border px-3 py-1 text-xs transition-colors",
                    selected
                      ? "border-primary/40 bg-primary/15 text-foreground"
                      : "border-glass-strong bg-glass text-muted-foreground hover:bg-glass-hover",
                  )}
                  aria-pressed={selected}
                >
                  {col.label}
                </button>
              );
            })}
          </div>
        )}
      </div>

      <div className="space-y-2">
        <label className="text-xs font-medium text-muted-foreground">Scope</label>
        <div className="flex w-fit items-center gap-1 rounded-lg border border-glass-strong bg-glass p-1">
          <ScopeButton active={scope === "all"} onClick={() => setScope("all")}>
            All mailboxes
          </ScopeButton>
          <ScopeButton active={scope === "unprotected"} onClick={() => setScope("unprotected")}>
            Unprotected only
          </ScopeButton>
        </div>
        <p className="text-xs text-muted-foreground">
          "Unprotected only" limits the report table to mailboxes that are not fully covered by all enabled protection services.
        </p>
      </div>

      <div className="flex items-center justify-between rounded-lg border border-glass-strong bg-glass px-4 py-3">
        <div className="space-y-0.5">
          <p className="text-sm font-medium text-foreground">Attach PDF</p>
          <p className="text-xs text-muted-foreground">
            Attach a PDF version of the report table alongside the email body.
          </p>
        </div>
        <Switch checked={attachPdf} onCheckedChange={setAttachPdf} />
      </div>

      {existing && (
        <div className="flex items-center justify-between rounded-lg border border-glass-strong bg-glass px-4 py-3">
          <div className="space-y-0.5">
            <p className="text-sm font-medium text-foreground">Active</p>
            <p className="text-xs text-muted-foreground">
              Inactive templates are hidden from the send dialog but kept for reactivation.
            </p>
          </div>
          <Switch checked={isActive} onCheckedChange={setIsActive} />
        </div>
      )}

      <div className="flex items-center justify-end gap-2 pt-2">
        <Button variant="ghost" onClick={onDone}>
          Cancel
        </Button>
        <Button disabled={!canSave} onClick={() => save.mutate()}>
          <Save className="h-4 w-4" />
          {existing ? "Save changes" : "Create template"}
        </Button>
      </div>
    </div>
  );
}

function ScopeButton({
  active,
  onClick,
  children,
}: {
  active: boolean;
  onClick: () => void;
  children: React.ReactNode;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={cn(
        "rounded-md px-3 py-1.5 text-sm font-medium transition-colors",
        active
          ? "bg-primary/15 text-foreground"
          : "text-muted-foreground hover:text-foreground",
      )}
    >
      {children}
    </button>
  );
}

function TokenDropdown({
  tokens,
  onSelect,
  title,
}: {
  tokens: { token: string; label: string }[];
  onSelect: (token: string) => void;
  title?: string;
}) {
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <button
          type="button"
          title={title}
          className={cn(
            "inline-flex items-center gap-1.5 rounded-md border px-2.5 py-1 text-xs font-medium transition-colors",
            "border-violet-400/30 bg-violet-400/10 text-violet-200 hover:bg-violet-400/15",
          )}
        >
          <Tag className="h-3.5 w-3.5" />
          Insert variable
          <ChevronDown className="h-3.5 w-3.5" />
        </button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="max-h-80 w-72 overflow-auto">
        <DropdownMenuLabel className="text-[11px] uppercase tracking-wider text-muted-foreground/70">
          Available variables
        </DropdownMenuLabel>
        <DropdownMenuSeparator />
        {tokens.length === 0 ? (
          <div className="px-2 py-2 text-xs text-muted-foreground">Loading…</div>
        ) : (
          tokens.map((t) => (
            <DropdownMenuItem
              key={t.token}
              onSelect={(e) => {
                e.preventDefault();
                onSelect(t.token);
              }}
              className="flex items-center justify-between gap-3"
            >
              <span className="truncate text-sm">{t.label}</span>
              <code className="shrink-0 rounded bg-glass-strong px-1.5 py-0.5 font-mono text-[10px] text-muted-foreground">
                {t.token}
              </code>
            </DropdownMenuItem>
          ))
        )}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}
