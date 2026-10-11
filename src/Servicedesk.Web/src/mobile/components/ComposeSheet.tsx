import * as React from "react";
import { Drawer } from "vaul";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { AtSign, Loader2, Lock, Mail, Phone, StickyNote, X } from "lucide-react";
import { mentionApi, ticketApi } from "@/lib/ticket-api";
import { cn } from "@/lib/utils";
import {
  activeMentionQuery,
  clearDraft,
  composeHtml,
  loadDraft,
  mentionFromPicker,
  saveDraft,
  splitMentions,
  type ComposeKind,
  type ComposeMention,
} from "../compose";

/// One-thumb composer for an internal note or a logged call. Both go through
/// the same endpoint and rules as the desktop composer (POST
/// /api/tickets/{id}/events → TicketMutationService): Note = internal note,
/// Call = internal call (clears Call-back server-side when
/// Tickets.CallbackClearOnCall is on). No customer-facing reply, no mail.
export function ComposeSheet({
  ticketId,
  open,
  kind,
  onKindChange,
  onOpenChange,
  callbackHint,
}: {
  ticketId: string;
  open: boolean;
  kind: ComposeKind;
  onKindChange: (k: ComposeKind) => void;
  onOpenChange: (open: boolean) => void;
  /// Shown on the Call tab when logging the call will clear the flag.
  callbackHint: boolean;
}) {
  const queryClient = useQueryClient();
  const textareaRef = React.useRef<HTMLTextAreaElement>(null);
  const [text, setText] = React.useState("");
  const [mentions, setMentions] = React.useState<ComposeMention[]>([]);
  const [caret, setCaret] = React.useState(0);
  const [pickerManual, setPickerManual] = React.useState<string | null>(null);

  // Each kind has its own draft slot: switching tabs swaps drafts.
  React.useEffect(() => {
    if (!open) return;
    const draft = loadDraft(kind, ticketId);
    setText(draft?.text ?? "");
    setMentions(draft?.mentions ?? []);
    setPickerManual(null);
  }, [open, kind, ticketId]);

  React.useEffect(() => {
    if (open) saveDraft(kind, ticketId, { text, mentions });
  }, [open, kind, ticketId, text, mentions]);

  const typed = activeMentionQuery(text, caret);
  const pickerQuery = pickerManual ?? typed?.query ?? null;
  const pickerOpen = pickerQuery !== null;

  // The kind (and text) are captured per submit: switching Note/Call while
  // the request is in flight must not clear the other slot's draft.
  const submit = useMutation({
    mutationFn: (v: { kind: ComposeKind; text: string; mentions: ComposeMention[] }) => {
      const { userIds, mailboxIds } = splitMentions(v.mentions);
      return ticketApi.addEvent(ticketId, {
        eventType: v.kind === "call" ? "Call" : "Note",
        bodyHtml: composeHtml(v.text, v.mentions),
        bodyText: v.text.trim(),
        isInternal: true,
        mentionedUserIds: userIds.length > 0 ? userIds : undefined,
        mentionedMailboxIds: mailboxIds.length > 0 ? mailboxIds : undefined,
      });
    },
    onSuccess: (_res, v) => {
      toast.success(v.kind === "call" ? "Call logged" : "Note added");
      clearDraft(v.kind, ticketId);
      if (v.kind === kind) {
        setText("");
        setMentions([]);
      }
      queryClient.invalidateQueries({ queryKey: ["ticket", ticketId] });
      // A logged call can clear Call-back server-side; refresh lists so the
      // float moves.
      if (v.kind === "call") queryClient.invalidateQueries({ queryKey: ["tickets"] });
      onOpenChange(false);
    },
    onError: () => toast.error("Could not save — your text is kept as a draft. Try again."),
  });

  const canSubmit = (text.trim().length > 0 || mentions.length > 0) && !submit.isPending;

  const pick = (m: ComposeMention) => {
    setMentions((prev) => (prev.some((x) => x.id === m.id) ? prev : [...prev, m]));
    if (typed && pickerManual === null) {
      // Replace the "@@query" being typed with "@label ".
      const next = `${text.slice(0, typed.start)}@${m.label} ${text.slice(caret)}`;
      const pos = typed.start + m.label.length + 2;
      setText(next);
      requestAnimationFrame(() => {
        textareaRef.current?.focus();
        textareaRef.current?.setSelectionRange(pos, pos);
        setCaret(pos);
      });
    }
    setPickerManual(null);
  };

  return (
    <Drawer.Root open={open} onOpenChange={onOpenChange} repositionInputs={false}>
      <Drawer.Portal>
        <Drawer.Overlay className="fixed inset-0 z-50 bg-overlay" />
        <Drawer.Content
          className="sd-m-sheet fixed inset-x-0 bottom-0 z-50 mx-auto flex max-h-[92dvh] max-w-xl flex-col rounded-t-[20px] border border-glass bg-background pb-[env(safe-area-inset-bottom)] outline-hidden"
          aria-describedby={undefined}
        >
          <div className="mx-auto mt-2.5 h-1.5 w-10 shrink-0 rounded-full bg-muted-foreground/30" aria-hidden />
          <div className="flex items-center gap-2 px-4 pt-3">
            <Drawer.Title className="sr-only">{kind === "call" ? "Log a call" : "Add a note"}</Drawer.Title>
            <div className="grid flex-1 grid-cols-2 rounded-xl border border-glass bg-glass p-1" role="tablist">
              {(["note", "call"] as const).map((k) => (
                <button
                  key={k}
                  type="button"
                  role="tab"
                  aria-selected={kind === k}
                  onClick={() => onKindChange(k)}
                  className={cn(
                    "flex h-10 items-center justify-center gap-2 rounded-lg text-[14px] font-semibold transition-colors",
                    kind === k ? "bg-primary text-primary-foreground shadow-sm" : "text-muted-foreground",
                  )}
                >
                  {k === "note" ? <StickyNote className="h-4 w-4" /> : <Phone className="h-4 w-4" />}
                  {k === "note" ? "Note" : "Call"}
                </button>
              ))}
            </div>
            <Drawer.Close
              className="inline-flex h-11 w-11 shrink-0 items-center justify-center rounded-full text-muted-foreground active:bg-glass-hover"
              aria-label="Close"
            >
              <X className="h-5 w-5" />
            </Drawer.Close>
          </div>

          <div className="flex items-center gap-1.5 px-4 pt-2 text-[12px] text-muted-foreground">
            <Lock className="h-3.5 w-3.5" />
            Internal — never visible to the customer.
            {kind === "call" && callbackHint && <span className="text-foreground/80"> Clears Call-back.</span>}
          </div>

          <div className="flex min-h-0 flex-1 flex-col gap-3 overflow-y-auto px-4 pt-3">
            {mentions.length > 0 && (
              <div className="flex flex-wrap gap-2" aria-label="Tagged">
                {mentions.map((m) => (
                  <span key={m.id} className="sd-mention inline-flex h-9 items-center gap-1 rounded-full pl-3 pr-1 text-[13px]">
                    {m.kind === "mailbox" ? <Mail className="h-3.5 w-3.5" /> : null}@{m.label}
                    <button
                      type="button"
                      onClick={() => setMentions((prev) => prev.filter((x) => x.id !== m.id))}
                      className="inline-flex h-7 w-7 items-center justify-center rounded-full"
                      aria-label={`Remove ${m.label}`}
                    >
                      <X className="h-3.5 w-3.5" />
                    </button>
                  </span>
                ))}
              </div>
            )}

            {pickerOpen && (
              <MentionPicker
                query={pickerQuery ?? ""}
                manual={pickerManual !== null}
                onQueryChange={setPickerManual}
                exclude={mentions.map((m) => m.id)}
                onPick={pick}
                onClose={() => setPickerManual(null)}
              />
            )}

            <textarea
              ref={textareaRef}
              value={text}
              onChange={(e) => {
                setText(e.target.value);
                setCaret(e.target.selectionStart ?? e.target.value.length);
              }}
              onSelect={(e) => setCaret(e.currentTarget.selectionStart ?? 0)}
              rows={6}
              autoFocus
              enterKeyHint="enter"
              autoCapitalize="sentences"
              placeholder={kind === "call" ? "Who called, what was agreed…  Type @@ to tag someone." : "Write an internal note…  Type @@ to tag someone."}
              aria-label={kind === "call" ? "Call summary" : "Note"}
              className="sd-m-input min-h-[9rem] w-full resize-none rounded-xl border border-glass bg-glass px-3.5 py-3 text-base leading-relaxed outline-hidden placeholder:text-muted-foreground"
            />
          </div>

          <div className="flex items-center gap-2 px-4 py-3">
            <button
              type="button"
              onClick={() => setPickerManual((q) => (q === null ? "" : null))}
              className={cn(
                "inline-flex h-12 items-center gap-2 rounded-xl border px-4 text-[14px] font-medium",
                pickerManual !== null ? "border-primary text-primary" : "border-glass bg-glass text-foreground",
              )}
            >
              <AtSign className="h-4 w-4" />
              Tag
            </button>
            <button
              type="button"
              disabled={!canSubmit}
              onClick={() => submit.mutate({ kind, text, mentions })}
              className="inline-flex h-12 flex-1 items-center justify-center gap-2 rounded-xl bg-primary text-[15px] font-semibold text-primary-foreground disabled:opacity-50"
            >
              {submit.isPending && <Loader2 className="h-4 w-4 animate-spin" />}
              {kind === "call" ? "Log call" : "Add note"}
            </button>
          </div>
        </Drawer.Content>
      </Drawer.Portal>
    </Drawer.Root>
  );
}

/// Inline picker (not a floating popover — on a phone it sits above the
/// keyboard inside the sheet). Agents and tagging mailboxes, same search as
/// the desktop @@-picker; empty query lists the first ones alphabetically.
function MentionPicker({
  query,
  manual,
  onQueryChange,
  exclude,
  onPick,
  onClose,
}: {
  query: string;
  manual: boolean;
  onQueryChange: (q: string) => void;
  exclude: string[];
  onPick: (m: ComposeMention) => void;
  onClose: () => void;
}) {
  const [debounced, setDebounced] = React.useState(query);
  React.useEffect(() => {
    const id = window.setTimeout(() => setDebounced(query), 200);
    return () => window.clearTimeout(id);
  }, [query]);
  const results = useQuery({
    queryKey: ["mention-search", debounced],
    queryFn: () => mentionApi.search(debounced),
    staleTime: 60_000,
  });
  const items = (results.data ?? []).filter((i) => !exclude.includes(i.id)).slice(0, 8);

  return (
    <div className="glass-card overflow-hidden">
      {manual && (
        <div className="border-b border-glass p-2">
          <input
            type="search"
            autoFocus
            value={query}
            onChange={(e) => onQueryChange(e.target.value)}
            placeholder="Search a colleague or mailbox"
            aria-label="Search a colleague or mailbox"
            className="sd-m-input h-11 w-full rounded-lg border border-glass bg-glass px-3 text-base outline-hidden placeholder:text-muted-foreground"
          />
        </div>
      )}
      {results.isLoading ? (
        <div className="flex justify-center py-4 text-muted-foreground">
          <Loader2 className="h-4 w-4 animate-spin" />
        </div>
      ) : items.length === 0 ? (
        <div className="flex items-center justify-between px-4 py-3 text-[13px] text-muted-foreground">
          No one found
          <button type="button" onClick={onClose} className="h-9 px-2 font-medium text-primary">Close</button>
        </div>
      ) : (
        <ul className="max-h-60 divide-y divide-glass overflow-y-auto">
          {items.map((item) => {
            const m = mentionFromPicker(item);
            return (
              <li key={item.id}>
                <button
                  type="button"
                  onClick={() => onPick(m)}
                  className="flex min-h-12 w-full items-center gap-3 px-4 py-2 text-left active:bg-glass-hover"
                >
                  <span className="flex h-8 w-8 shrink-0 items-center justify-center rounded-full bg-accent text-primary">
                    {item.kind === "mailbox" ? <Mail className="h-4 w-4" /> : <AtSign className="h-4 w-4" />}
                  </span>
                  <span className="min-w-0 flex-1">
                    <span className="block truncate text-[15px] font-medium">{item.kind === "mailbox" ? m.label : `@${m.label}`}</span>
                    <span className="block truncate text-[12px] text-muted-foreground">{item.email}</span>
                  </span>
                </button>
              </li>
            );
          })}
        </ul>
      )}
    </div>
  );
}
