import { useMemo, useRef, useState } from "react";
import { Check, ChevronDown, Save, Tag } from "lucide-react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Switch } from "@/components/ui/switch";
import { RichTextEditor } from "@/components/RichTextEditor";
import {
  composeTemplatesApi,
  type ComposeTemplate,
} from "@/lib/composeTemplates-api";
import { surveysAgentApi } from "@/lib/surveys-api";
import { taxonomyApi } from "@/lib/api";
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

type EditorHandle = {
  chain: () => {
    focus: () => {
      insertContent: (content: string) => { run: () => void };
    };
  };
};

const LIST_QUERY_KEY = ["compose-templates", "list"] as const;

export function TemplateEditor({
  existing,
  onDone,
}: {
  existing: ComposeTemplate | null;
  onDone: () => void;
}) {
  const qc = useQueryClient();
  const [name, setName] = useState(existing?.name ?? "");
  const [description, setDescription] = useState(existing?.description ?? "");
  const [bodyHtml, setBodyHtml] = useState(existing?.bodyHtml ?? "");
  const [isActive, setIsActive] = useState(existing?.isActive ?? true);
  const [queueIds, setQueueIds] = useState<string[]>(existing?.queueIds ?? []);
  const [statusIds, setStatusIds] = useState<string[]>(
    existing?.statusIds ?? [],
  );
  const [autoInsertOnNote, setAutoInsertOnNote] = useState(
    existing?.autoInsertOnNote ?? false,
  );
  const [linkedSurveyId, setLinkedSurveyId] = useState<string | null>(
    existing?.linkedSurveyId ?? null,
  );

  const usableSurveysQ = useQuery({
    queryKey: ["surveys", "usable"],
    queryFn: () => surveysAgentApi.usable(),
    staleTime: 60_000,
  });

  const queuesQ = useQuery({
    queryKey: ["taxonomy", "queues"],
    queryFn: () => taxonomyApi.queues.list(),
  });
  const queues = queuesQ.data ?? [];

  const statusesQ = useQuery({
    queryKey: ["taxonomy", "statuses"],
    queryFn: () => taxonomyApi.statuses.list(),
  });
  const statuses = (statusesQ.data ?? []).filter((s) => s.isActive);

  const tokensQ = useQuery({
    queryKey: ["compose-templates", "tokens"],
    queryFn: () => composeTemplatesApi.listTokens(),
    staleTime: Infinity,
  });
  const tokens = tokensQ.data?.tokens ?? [];

  // Stash the live editor so the token-picker can drop {{...}} at the
  // current cursor position. We don't need to re-render when it mounts —
  // a ref keeps the picker dropdown leightweight.
  const editorRef = useRef<EditorHandle | null>(null);

  const queueSet = useMemo(() => new Set(queueIds), [queueIds]);
  const statusSet = useMemo(() => new Set(statusIds), [statusIds]);

  const save = useMutation({
    mutationFn: async () => {
      const payload = {
        name: name.trim(),
        description: description.trim() ? description.trim() : null,
        bodyHtml,
        isActive,
        queueIds,
        statusIds,
        autoInsertOnNote,
        linkedSurveyId,
      };
      return existing
        ? composeTemplatesApi.update(existing.id, payload)
        : composeTemplatesApi.create(payload);
    },
    onSuccess: () => {
      toast.success(existing ? "Template updated." : "Template created.");
      qc.invalidateQueries({ queryKey: LIST_QUERY_KEY });
      qc.invalidateQueries({ queryKey: ["compose-templates"] });
      onDone();
    },
    onError: (err: Error & { payload?: { error?: string } }) => {
      const msg = err.payload?.error ?? "Save failed.";
      toast.error(msg);
    },
  });

  const canSave = name.trim().length > 0 && !save.isPending;

  const toggleQueue = (queueId: string) => {
    setQueueIds((prev) =>
      prev.includes(queueId)
        ? prev.filter((id) => id !== queueId)
        : [...prev, queueId],
    );
  };

  const toggleStatus = (statusId: string) => {
    setStatusIds((prev) =>
      prev.includes(statusId)
        ? prev.filter((id) => id !== statusId)
        : [...prev, statusId],
    );
  };

  return (
    <div className="flex flex-col gap-5">
      <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
        <div className="space-y-1.5">
          <label className="text-xs font-medium text-muted-foreground">
            Name
          </label>
          <Input
            value={name}
            onChange={(e) => setName(e.target.value)}
            placeholder="e.g. Standard greeting"
            maxLength={200}
          />
        </div>

        <div className="space-y-1.5">
          <label className="text-xs font-medium text-muted-foreground">
            Description
          </label>
          <Input
            value={description ?? ""}
            onChange={(e) => setDescription(e.target.value)}
            placeholder="Short hint shown in the picker"
            maxLength={2000}
          />
        </div>
      </div>

      <div className="space-y-1.5">
        <div className="flex items-center justify-between">
          <label className="text-xs font-medium text-muted-foreground">
            Body
          </label>
          <DropdownMenu>
            <DropdownMenuTrigger asChild>
              <button
                type="button"
                className={cn(
                  "inline-flex items-center gap-1.5 rounded-md border px-2.5 py-1 text-xs font-medium transition-colors",
                  "border-violet-400/30 bg-violet-400/10 text-violet-200 hover:bg-violet-400/15",
                )}
                title="Insert a placeholder that resolves to the ticket's contact / company at insert time"
              >
                <Tag className="h-3.5 w-3.5" />
                Insert variable
                <ChevronDown className="h-3.5 w-3.5" />
              </button>
            </DropdownMenuTrigger>
            <DropdownMenuContent
              align="end"
              className="max-h-80 w-72 overflow-auto"
            >
              <DropdownMenuLabel className="text-[11px] uppercase tracking-wider text-muted-foreground/70">
                Available variables
              </DropdownMenuLabel>
              <DropdownMenuSeparator />
              {tokens.length === 0 ? (
                <div className="px-2 py-2 text-xs text-muted-foreground">
                  Loading…
                </div>
              ) : (
                tokens.map((t) => (
                  <DropdownMenuItem
                    key={t.token}
                    onSelect={(e) => {
                      // onSelect closes the menu; we re-focus the editor
                      // and insert the literal placeholder at the caret.
                      e.preventDefault();
                      const editor = editorRef.current;
                      if (!editor) return;
                      editor.chain().focus().insertContent(t.token).run();
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
        </div>
        <RichTextEditor
          content={bodyHtml}
          onChange={setBodyHtml}
          placeholder="Type the template body. Use 'Insert variable' to add a placeholder like {{contact.firstName}}…"
          minHeight="200px"
          maxHeight="420px"
          onEditorReady={(editor) => {
            editorRef.current = editor as unknown as EditorHandle | null;
          }}
        />
        <p className="text-[11px] text-muted-foreground/70">
          Placeholders such as <code className="font-mono">{"{{contact.firstName}}"}</code> are filled in when the template lands in a ticket. Empty values stay as the raw placeholder so the agent notices missing data.
        </p>
      </div>

      <div className="space-y-2">
        <div className="flex items-center justify-between">
          <label className="text-xs font-medium text-muted-foreground">
            Available in queues
          </label>
          {queueIds.length > 0 && (
            <button
              type="button"
              onClick={() => setQueueIds([])}
              className="text-[11px] text-muted-foreground hover:text-foreground"
            >
              Clear scope (all queues)
            </button>
          )}
        </div>
        <p className="text-xs text-muted-foreground">
          Leave empty to make the template available in every queue. Otherwise
          the <code className="font-mono">::</code> picker only surfaces it when
          the ticket lives in one of the selected queues.
        </p>
        {queuesQ.isLoading ? (
          <p className="text-xs text-muted-foreground">Loading queues…</p>
        ) : queues.length === 0 ? (
          <p className="text-xs text-muted-foreground">No queues configured.</p>
        ) : (
          <div className="flex flex-wrap gap-2">
            {queues.map((q) => {
              const selected = queueSet.has(q.id);
              return (
                <button
                  key={q.id}
                  type="button"
                  onClick={() => toggleQueue(q.id)}
                  className={cn(
                    "inline-flex items-center gap-1.5 rounded-full border px-3 py-1 text-xs transition-colors",
                    selected
                      ? "border-primary/40 bg-primary/15 text-primary-foreground"
                      : "border-glass-strong bg-glass text-muted-foreground hover:bg-glass-hover",
                  )}
                  aria-pressed={selected}
                >
                  {selected && <Check className="h-3.5 w-3.5" />}
                  {q.name}
                </button>
              );
            })}
          </div>
        )}
      </div>

      <div className="space-y-2">
        <div className="flex items-center justify-between">
          <label className="text-xs font-medium text-muted-foreground">
            Available in statuses
          </label>
          {statusIds.length > 0 && (
            <button
              type="button"
              onClick={() => setStatusIds([])}
              className="text-[11px] text-muted-foreground hover:text-foreground"
            >
              Clear scope (any status)
            </button>
          )}
        </div>
        <p className="text-xs text-muted-foreground">
          Leave empty to make the template available regardless of the ticket's
          status. Otherwise both the <code className="font-mono">::</code>{" "}
          picker and the auto-insert behaviour only fire while the ticket sits
          in one of the selected statuses.
        </p>
        {statusesQ.isLoading ? (
          <p className="text-xs text-muted-foreground">Loading statuses…</p>
        ) : statuses.length === 0 ? (
          <p className="text-xs text-muted-foreground">No active statuses.</p>
        ) : (
          <div className="flex flex-wrap gap-2">
            {statuses.map((s) => {
              const selected = statusSet.has(s.id);
              return (
                <button
                  key={s.id}
                  type="button"
                  onClick={() => toggleStatus(s.id)}
                  className={cn(
                    "inline-flex items-center gap-1.5 rounded-full border px-3 py-1 text-xs transition-colors",
                    selected
                      ? "border-primary/40 bg-primary/15 text-primary-foreground"
                      : "border-glass-strong bg-glass text-muted-foreground hover:bg-glass-hover",
                  )}
                  aria-pressed={selected}
                >
                  {selected && <Check className="h-3.5 w-3.5" />}
                  {s.name}
                </button>
              );
            })}
          </div>
        )}
      </div>

      <div className="flex items-start justify-between gap-4 rounded-lg border border-glass-strong bg-glass px-4 py-3">
        <div className="space-y-0.5">
          <p className="text-sm font-medium text-foreground">
            Auto-insert into the internal-note composer
          </p>
          <p className="text-xs text-muted-foreground">
            When enabled, this template is dropped into the{" "}
            <span className="font-medium text-foreground/80">
              Write an internal note
            </span>{" "}
            composer the first time an agent opens it on a matching ticket —
            but only when the composer is empty (no saved draft). The agent
            can still edit or clear the prefilled body. Scope it tight with
            the queue and status pickers above; if multiple templates match,
            the most-recently-updated one wins.
          </p>
        </div>
        <Switch
          checked={autoInsertOnNote}
          onCheckedChange={setAutoInsertOnNote}
        />
      </div>

      <div className="space-y-2 rounded-lg border border-glass-strong bg-glass px-4 py-3">
        <div className="space-y-0.5">
          <p className="text-sm font-medium text-foreground">
            Linked survey (optional)
          </p>
          <p className="text-xs text-muted-foreground">
            When set, sending a reply or note built from this template
            automatically dispatches the chosen CSAT survey to the ticket
            requester. Idempotent — repeats are skipped while an active
            invitation exists for the (survey, ticket) pair.
          </p>
        </div>
        <Select
          value={linkedSurveyId ?? "__none"}
          onValueChange={(v) => setLinkedSurveyId(v === "__none" ? null : v)}
        >
          <SelectTrigger className="h-9 bg-glass">
            <SelectValue placeholder="No survey" />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="__none">No survey</SelectItem>
            {(usableSurveysQ.data ?? []).map((s) => (
              <SelectItem key={s.id} value={s.id}>
                {s.name}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
      </div>

      <div className="flex items-center justify-between rounded-lg border border-glass-strong bg-glass px-4 py-3">
        <div className="space-y-0.5">
          <p className="text-sm font-medium text-foreground">Active</p>
          <p className="text-xs text-muted-foreground">
            Inactive templates are hidden from the agent <code className="font-mono">::</code> picker but kept for reactivation.
          </p>
        </div>
        <Switch checked={isActive} onCheckedChange={setIsActive} />
      </div>

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
