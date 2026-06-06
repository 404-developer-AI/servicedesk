import { useMemo, useRef, useState } from "react";
import { ChevronDown, Save, Tag } from "lucide-react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Switch } from "@/components/ui/switch";
import { RichTextEditor } from "@/components/RichTextEditor";
import { AgentPicker } from "@/components/AgentPicker";
import {
  ticketTemplatesApi,
  type TicketTemplate,
} from "@/lib/ticketTemplates-api";
import { composeTemplatesApi } from "@/lib/composeTemplates-api";
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

const LIST_QUERY_KEY = ["ticket-templates", "list"] as const;
const NONE = "__none";

/// Shared "Insert variable" dropdown. `onInsert` receives the literal
/// `{{token}}` placeholder so each caller can drop it into its own field.
function TokenMenu({ onInsert }: { onInsert: (token: string) => void }) {
  const tokensQ = useQuery({
    queryKey: ["compose-templates", "tokens"],
    queryFn: () => composeTemplatesApi.listTokens(),
    staleTime: Infinity,
  });
  const tokens = tokensQ.data?.tokens ?? [];

  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <button
          type="button"
          className={cn(
            "inline-flex items-center gap-1.5 rounded-md border px-2.5 py-1 text-xs font-medium transition-colors",
            "border-violet-400/30 bg-violet-400/10 text-violet-200 hover:bg-violet-400/15",
          )}
          title="Insert a placeholder that resolves to the requester / company when the template is applied"
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
                onInsert(t.token);
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

export function TicketTemplateEditor({
  existing,
  onDone,
}: {
  existing: TicketTemplate | null;
  onDone: () => void;
}) {
  const qc = useQueryClient();
  const [name, setName] = useState(existing?.name ?? "");
  const [description, setDescription] = useState(existing?.description ?? "");
  const [isActive, setIsActive] = useState(existing?.isActive ?? true);
  const [subject, setSubject] = useState(existing?.subject ?? "");
  const [bodyHtml, setBodyHtml] = useState(existing?.bodyHtml ?? "");
  const [initialNoteHtml, setInitialNoteHtml] = useState(
    existing?.initialNoteHtml ?? "",
  );
  const [initialNoteInternal, setInitialNoteInternal] = useState(
    existing?.initialNoteInternal ?? true,
  );
  const [queueId, setQueueId] = useState<string | null>(existing?.queueId ?? null);
  const [priorityId, setPriorityId] = useState<string | null>(
    existing?.priorityId ?? null,
  );
  const [statusId, setStatusId] = useState<string | null>(existing?.statusId ?? null);
  const [categoryId, setCategoryId] = useState<string | null>(
    existing?.categoryId ?? null,
  );
  const [ticketTypeId, setTicketTypeId] = useState<string | null>(
    existing?.ticketTypeId ?? null,
  );
  const [assigneeUserId, setAssigneeUserId] = useState<string | null>(
    existing?.assigneeUserId ?? null,
  );

  const queuesQ = useQuery({
    queryKey: ["taxonomy", "queues"],
    queryFn: () => taxonomyApi.queues.list(),
  });
  const prioritiesQ = useQuery({
    queryKey: ["taxonomy", "priorities"],
    queryFn: () => taxonomyApi.priorities.list(),
  });
  const statusesQ = useQuery({
    queryKey: ["taxonomy", "statuses"],
    queryFn: () => taxonomyApi.statuses.list(),
  });
  const categoriesQ = useQuery({
    queryKey: ["taxonomy", "categories"],
    queryFn: () => taxonomyApi.categories.list(),
  });
  const typesQ = useQuery({
    queryKey: ["taxonomy", "ticket-types"],
    queryFn: () => taxonomyApi.ticketTypes.list(),
  });

  const queues = useMemo(
    () => (queuesQ.data ?? []).filter((q) => q.isActive),
    [queuesQ.data],
  );
  const priorities = useMemo(
    () =>
      (prioritiesQ.data ?? [])
        .filter((p) => p.isActive)
        .sort((a, b) => a.sortOrder - b.sortOrder),
    [prioritiesQ.data],
  );
  const statuses = useMemo(
    () => (statusesQ.data ?? []).filter((s) => s.isActive),
    [statusesQ.data],
  );
  const categories = useMemo(
    () => (categoriesQ.data ?? []).filter((c) => c.isActive),
    [categoriesQ.data],
  );
  const types = useMemo(
    () =>
      (typesQ.data ?? [])
        .filter((t) => t.isActive)
        .sort((a, b) => a.sortOrder - b.sortOrder),
    [typesQ.data],
  );

  const subjectRef = useRef<HTMLInputElement | null>(null);
  const bodyRef = useRef<EditorHandle | null>(null);
  const noteRef = useRef<EditorHandle | null>(null);

  // Insert a token into the subject <input> at the caret, preserving any
  // text the admin typed around it.
  const insertIntoSubject = (token: string) => {
    const el = subjectRef.current;
    if (!el) {
      setSubject((s) => s + token);
      return;
    }
    const start = el.selectionStart ?? subject.length;
    const end = el.selectionEnd ?? subject.length;
    const next = subject.slice(0, start) + token + subject.slice(end);
    setSubject(next);
    // Restore focus + caret after the inserted token on the next tick.
    requestAnimationFrame(() => {
      el.focus();
      const caret = start + token.length;
      el.setSelectionRange(caret, caret);
    });
  };

  const save = useMutation({
    mutationFn: async () => {
      const payload = {
        name: name.trim(),
        description: description.trim() ? description.trim() : null,
        isActive,
        subject: subject.trim(),
        bodyHtml,
        initialNoteHtml,
        initialNoteInternal,
        queueId,
        priorityId,
        statusId,
        categoryId,
        ticketTypeId,
        assigneeUserId,
      };
      return existing
        ? ticketTemplatesApi.update(existing.id, payload)
        : ticketTemplatesApi.create(payload);
    },
    onSuccess: () => {
      toast.success(existing ? "Template updated." : "Template created.");
      qc.invalidateQueries({ queryKey: LIST_QUERY_KEY });
      qc.invalidateQueries({ queryKey: ["ticket-templates"] });
      onDone();
    },
    onError: (err: Error & { payload?: { error?: string } }) => {
      toast.error(err.payload?.error ?? "Save failed.");
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
            placeholder="e.g. New employee onboarding"
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

      {/* Subject */}
      <div className="space-y-1.5">
        <div className="flex items-center justify-between">
          <label className="text-xs font-medium text-muted-foreground">
            Subject
          </label>
          <TokenMenu onInsert={insertIntoSubject} />
        </div>
        <Input
          ref={subjectRef}
          value={subject}
          onChange={(e) => setSubject(e.target.value)}
          placeholder="e.g. Onboarding for {{contact.fullName}}"
          maxLength={1000}
        />
      </div>

      {/* Body */}
      <div className="space-y-1.5">
        <div className="flex items-center justify-between">
          <label className="text-xs font-medium text-muted-foreground">
            Description / body
          </label>
          <TokenMenu
            onInsert={(token) =>
              bodyRef.current?.chain().focus().insertContent(token).run()
            }
          />
        </div>
        <RichTextEditor
          content={bodyHtml}
          onChange={setBodyHtml}
          placeholder="Default ticket description. Use 'Insert variable' to add a placeholder…"
          minHeight="160px"
          maxHeight="380px"
          onEditorReady={(editor) => {
            bodyRef.current = editor as unknown as EditorHandle | null;
          }}
        />
        <p className="text-[11px] text-muted-foreground/70">
          Placeholders such as{" "}
          <code className="font-mono">{"{{contact.firstName}}"}</code> are
          filled from the chosen requester + company when the template is
          applied. Empty values stay as the raw placeholder so the agent
          notices missing data. Ticket-only tokens (number, subject) stay empty
          — the ticket doesn't exist yet.
        </p>
      </div>

      {/* Field pre-fills */}
      <div className="grid grid-cols-1 gap-4 rounded-lg border border-glass-strong bg-glass p-4 md:grid-cols-2">
        <div className="md:col-span-2">
          <p className="text-xs font-medium text-muted-foreground">
            Field pre-fills
          </p>
          <p className="text-[11px] text-muted-foreground/70">
            Leave any field on “Don’t set” to keep the agent’s current choice
            when the template is applied.
          </p>
        </div>

        <FieldSelect
          label="Queue"
          value={queueId}
          onChange={setQueueId}
          options={queues.map((q) => ({ id: q.id, label: q.name }))}
        />
        <FieldSelect
          label="Priority"
          value={priorityId}
          onChange={setPriorityId}
          options={priorities.map((p) => ({ id: p.id, label: p.name }))}
        />
        <FieldSelect
          label="Status"
          value={statusId}
          onChange={setStatusId}
          options={statuses.map((s) => ({ id: s.id, label: s.name }))}
        />
        <FieldSelect
          label="Type"
          value={ticketTypeId}
          onChange={setTicketTypeId}
          options={types.map((t) => ({ id: t.id, label: t.label }))}
        />
        <FieldSelect
          label="Category"
          value={categoryId}
          onChange={setCategoryId}
          options={categories.map((c) => ({ id: c.id, label: c.name }))}
        />
        <div className="space-y-1.5">
          <label className="text-xs font-medium text-muted-foreground">
            Assignee
          </label>
          <AgentPicker
            value={assigneeUserId}
            onChange={setAssigneeUserId}
            placeholder="Don’t set"
          />
        </div>
      </div>

      {/* Initial note */}
      <div className="space-y-2 rounded-lg border border-glass-strong bg-glass p-4">
        <div className="flex items-center justify-between">
          <div className="space-y-0.5">
            <p className="text-sm font-medium text-foreground">
              Initial note (optional)
            </p>
            <p className="text-[11px] text-muted-foreground/70">
              When set, the note block opens pre-filled on the new ticket. The
              agent can edit or remove it before creating.
            </p>
          </div>
          <div className="flex items-center gap-2">
            <TokenMenu
              onInsert={(token) =>
                noteRef.current?.chain().focus().insertContent(token).run()
              }
            />
            <div className="flex items-center gap-1 rounded-md border border-glass-strong bg-glass p-0.5 text-[11px]">
              <button
                type="button"
                onClick={() => setInitialNoteInternal(true)}
                className={cn(
                  "rounded px-2 py-0.5 transition",
                  initialNoteInternal
                    ? "bg-amber-500/20 text-amber-200"
                    : "text-muted-foreground hover:text-foreground",
                )}
              >
                Internal
              </button>
              <button
                type="button"
                onClick={() => setInitialNoteInternal(false)}
                className={cn(
                  "rounded px-2 py-0.5 transition",
                  !initialNoteInternal
                    ? "bg-emerald-500/20 text-emerald-200"
                    : "text-muted-foreground hover:text-foreground",
                )}
              >
                Public
              </button>
            </div>
          </div>
        </div>
        <RichTextEditor
          content={initialNoteHtml}
          onChange={setInitialNoteHtml}
          placeholder="Leave empty for no initial note…"
          minHeight="120px"
          maxHeight="320px"
          onEditorReady={(editor) => {
            noteRef.current = editor as unknown as EditorHandle | null;
          }}
        />
      </div>

      {/* Active */}
      <div className="flex items-center justify-between rounded-lg border border-glass-strong bg-glass px-4 py-3">
        <div className="space-y-0.5">
          <p className="text-sm font-medium text-foreground">Active</p>
          <p className="text-xs text-muted-foreground">
            Inactive templates are hidden from the New-Ticket picker but kept
            for reactivation.
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

function FieldSelect({
  label,
  value,
  onChange,
  options,
}: {
  label: string;
  value: string | null;
  onChange: (v: string | null) => void;
  options: { id: string; label: string }[];
}) {
  return (
    <div className="space-y-1.5">
      <label className="text-xs font-medium text-muted-foreground">{label}</label>
      <Select
        value={value ?? NONE}
        onValueChange={(v) => onChange(v === NONE ? null : v)}
      >
        <SelectTrigger className="h-9 bg-glass">
          <SelectValue placeholder="Don’t set" />
        </SelectTrigger>
        <SelectContent>
          <SelectItem value={NONE}>Don’t set</SelectItem>
          {options.map((o) => (
            <SelectItem key={o.id} value={o.id}>
              {o.label}
            </SelectItem>
          ))}
        </SelectContent>
      </Select>
    </div>
  );
}
