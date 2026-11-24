import * as React from "react";
import { useQuery } from "@tanstack/react-query";
import { toast } from "sonner";
import { FileText, Loader2, Paperclip, Star, UserCircle, UserPlus, X } from "lucide-react";
import { userApi } from "@/lib/users-api";
import { contactApi } from "@/lib/companies-api";
import { ticketApi } from "@/lib/ticket-api";
import type { AgentAnswer, AttachmentAnswer, InviteeAnswer } from "@/lib/forms-api";
import { isEmail } from "@/lib/formDraft";
import { formatBytes } from "@/lib/format";
import { errorMessage } from "@/lib/http";
import { cn } from "@/lib/utils";
import { SearchPicker, formInputCn } from "./FormFieldInput";

/// v0.1.61 — inputs for the intervention-planning field types: several
/// agents (with an organizer for a Teams meeting), people to invite and
/// files. One chip vocabulary for all three: a compact row per item with
/// its actions on the right, theme tokens only.

const chipRow = "flex min-w-0 items-center gap-2 rounded-md border border-glass bg-glass px-2.5 py-1.5 text-sm";

/// Several agents. `organizerMode` (a Teams meeting with more than one
/// technician) shows the organizer star; the first agent is the organizer
/// until another one is starred.
export function AgentsControl({ value, onChange, disabled, invalid, organizerMode }: {
  value: AgentAnswer[]; onChange: (v: AgentAnswer[]) => void;
  disabled?: boolean; invalid?: boolean; organizerMode?: boolean;
}) {
  const agents = useQuery({ queryKey: ["agents"], queryFn: userApi.listAgents, staleTime: 5 * 60_000 });
  const showStar = !!organizerMode && value.length > 1;
  const organizerId = value.find((a) => a.organizer)?.id ?? value[0]?.id ?? null;
  const setOrganizer = (id: string) => onChange(value.map((a) => ({ id: a.id, name: a.name, ...(a.id === id ? { organizer: true } : {}) })));

  return (
    <div className="flex flex-col gap-1.5">
      {value.map((a) => (
        <div key={a.id} className={chipRow}>
          <UserCircle className="h-3.5 w-3.5 shrink-0 text-muted-foreground" />
          <span className="min-w-0 flex-1 truncate">{a.name}</span>
          {showStar && (
            <button type="button" disabled={disabled} onClick={() => setOrganizer(a.id)}
              aria-pressed={organizerId === a.id}
              title={organizerId === a.id ? "Organizer of the Teams meeting" : "Make organizer"}
              className={cn(
                "inline-flex items-center gap-1 rounded px-1.5 py-0.5 text-[11px] font-medium transition-colors",
                organizerId === a.id ? "bg-primary/10 text-primary" : "text-muted-foreground hover:bg-glass-hover hover:text-foreground",
              )}>
              <Star className={cn("h-3 w-3", organizerId === a.id && "fill-current")} />
              {organizerId === a.id ? "Organizer" : "Make organizer"}
            </button>
          )}
          <button type="button" disabled={disabled} aria-label={`Remove ${a.name}`}
            onClick={() => onChange(value.filter((x) => x.id !== a.id))}
            className="rounded p-0.5 text-muted-foreground hover:bg-glass-hover hover:text-foreground">
            <X className="h-3.5 w-3.5" />
          </button>
        </div>
      ))}
      <SearchPicker
        icon={UserPlus}
        label={null}
        placeholder={value.length === 0 ? "Choose agents" : "Add an agent"}
        disabled={disabled || value.length >= 20}
        invalid={invalid}
        queryKey={["forms", "agents-options", agents.dataUpdatedAt, value.length]}
        load={async (term) => {
          const list = agents.data ?? (await userApi.listAgents());
          const needle = term.toLowerCase();
          return list.filter((a) => !value.some((v) => v.id === a.id) && (!needle || a.email.toLowerCase().includes(needle))).slice(0, 50);
        }}
        renderItem={(a) => <span className="truncate">{a.email}</span>}
        onPick={(a) => onChange([...value, { id: a.id, name: a.email }])}
      />
    </div>
  );
}

/// People to invite: servicedesk contacts (search) or a typed address.
export function InviteesControl({ value, onChange, disabled, invalid }: {
  value: InviteeAnswer[]; onChange: (v: InviteeAnswer[]) => void; disabled?: boolean; invalid?: boolean;
}) {
  const [typed, setTyped] = React.useState("");
  const typedOk = isEmail(typed);
  const addTyped = () => {
    const email = typed.trim();
    if (!isEmail(email)) return;
    if (!value.some((p) => p.email.toLowerCase() === email.toLowerCase())) onChange([...value, { email, name: "" }]);
    setTyped("");
  };

  return (
    <div className="flex flex-col gap-1.5">
      {value.map((p) => (
        <div key={p.contactId ?? p.email} className={chipRow}>
          <UserCircle className="h-3.5 w-3.5 shrink-0 text-muted-foreground" />
          <span className="min-w-0 flex-1 truncate">
            {p.name ? <>{p.name} <span className="text-muted-foreground">· {p.email}</span></> : p.email}
          </span>
          {!p.contactId && <span className="shrink-0 text-[11px] text-muted-foreground">external address</span>}
          <button type="button" disabled={disabled} aria-label={`Remove ${p.email}`}
            onClick={() => onChange(value.filter((x) => x !== p))}
            className="rounded p-0.5 text-muted-foreground hover:bg-glass-hover hover:text-foreground">
            <X className="h-3.5 w-3.5" />
          </button>
        </div>
      ))}
      <div className="grid gap-1.5 sm:grid-cols-2">
        <SearchPicker
          icon={UserPlus}
          label={null}
          placeholder="Add a contact"
          disabled={disabled}
          invalid={invalid}
          hint="No contacts match. Type an address next to this instead."
          queryKey={["forms", "invitee-contacts", value.length]}
          load={async (term) => {
            if (term.length < 2) return [];
            const list = await contactApi.list(term);
            return list.filter((c) => c.email && !value.some((p) => p.contactId === c.id)).slice(0, 30);
          }}
          renderItem={(c) => (
            <span className="block min-w-0">
              <span className="block truncate font-medium">{`${c.firstName} ${c.lastName}`.trim() || c.email}</span>
              <span className="block truncate text-xs text-muted-foreground">{c.email}</span>
            </span>
          )}
          onPick={(c) => onChange([...value, { contactId: c.id, email: c.email, name: `${c.firstName} ${c.lastName}`.trim() }])}
        />
        <div className="flex gap-1.5">
          <input type="email" value={typed} disabled={disabled} placeholder="or type an e-mail address"
            onChange={(e) => setTyped(e.target.value)}
            onKeyDown={(e) => { if (e.key === "Enter") { e.preventDefault(); addTyped(); } }}
            className={cn(formInputCn, "h-9 min-w-0 flex-1", typed && !typedOk && "border-amber-500/60")} />
          <button type="button" disabled={disabled || !typedOk} onClick={addTyped}
            className="h-9 shrink-0 rounded-md border border-glass-strong bg-glass px-3 text-sm text-foreground transition-colors hover:bg-glass-hover disabled:opacity-40">
            Add
          </button>
        </div>
      </div>
    </div>
  );
}

/// Files for the form: uploaded to the ticket (same endpoint, checks and
/// size cap as composer attachments) and bound to the form's note on save.
export function AttachmentsControl({ ticketId, value, onChange, disabled, invalid, maxTotalBytes }: {
  ticketId: string | null; value: AttachmentAnswer[]; onChange: (v: AttachmentAnswer[]) => void;
  disabled?: boolean; invalid?: boolean; maxTotalBytes?: number | null;
}) {
  const inputRef = React.useRef<HTMLInputElement>(null);
  const [busy, setBusy] = React.useState(0);
  const total = value.reduce((s, f) => s + f.size, 0);
  const over = !!maxTotalBytes && total > maxTotalBytes;

  const upload = async (files: FileList | null) => {
    if (!files || !ticketId) return;
    const picked = Array.from(files);
    setBusy((n) => n + picked.length);
    const added: AttachmentAnswer[] = [];
    for (const file of picked) {
      try {
        const meta = await ticketApi.uploadAttachment(ticketId, file);
        added.push({ id: meta.id, name: meta.filename, size: meta.size, contentType: meta.mimeType });
      } catch (err) {
        toast.error(errorMessage(err, `"${file.name}" could not be uploaded.`));
      } finally {
        setBusy((n) => n - 1);
      }
    }
    if (added.length > 0) onChange([...value, ...added]);
    if (inputRef.current) inputRef.current.value = "";
  };

  return (
    <div className="flex flex-col gap-1.5">
      {value.map((f) => (
        <div key={f.id} className={chipRow}>
          <FileText className="h-3.5 w-3.5 shrink-0 text-muted-foreground" />
          <span className="min-w-0 flex-1 truncate">{f.name}</span>
          <span className="shrink-0 text-[11px] tabular-nums text-muted-foreground">{formatBytes(f.size)}</span>
          <button type="button" disabled={disabled} aria-label={`Remove ${f.name}`}
            onClick={() => onChange(value.filter((x) => x.id !== f.id))}
            className="rounded p-0.5 text-muted-foreground hover:bg-glass-hover hover:text-foreground">
            <X className="h-3.5 w-3.5" />
          </button>
        </div>
      ))}
      <div className="flex items-center gap-3">
        <button type="button" disabled={disabled || !ticketId || busy > 0 || value.length >= 20}
          onClick={() => inputRef.current?.click()}
          className={cn(formInputCn, "inline-flex h-9 w-auto items-center gap-2 px-3", invalid && "border-rose-500/60")}>
          {busy > 0 ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Paperclip className="h-3.5 w-3.5 opacity-60" />}
          {busy > 0 ? "Uploading…" : "Add files"}
        </button>
        {value.length > 0 && (
          <span className={cn("text-xs tabular-nums", over ? "font-medium text-destructive" : "text-muted-foreground")}>
            {formatBytes(total)}{maxTotalBytes ? ` of ${formatBytes(maxTotalBytes)}` : ""}
            {over && " — too large together; remove a file"}
          </span>
        )}
        {!ticketId && <span className="text-xs text-muted-foreground">Files are added on the ticket.</span>}
        <input ref={inputRef} type="file" multiple hidden onChange={(e) => void upload(e.target.files)} />
      </div>
    </div>
  );
}
