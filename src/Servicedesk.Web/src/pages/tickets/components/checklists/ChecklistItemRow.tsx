import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import {
  AlignLeft,
  Check,
  ChevronDown,
  CircleSlash,
  ExternalLink,
  History,
  MessageSquare,
  Minus,
  Pencil,
  RotateCcw,
  Send,
  Trash2,
  UserPlus,
} from "lucide-react";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { useServerTime, toServerLocal } from "@/hooks/useServerTime";
import {
  ticketChecklistApi,
  checklistErrorMessage,
  type ChecklistItemEvent,
  type ChecklistItemState,
  type TicketChecklistItem,
} from "@/lib/checklist-api";
import { ticketChecklistsKey } from "./useTicketChecklists";

/// One checklist step. The checkbox flips open ↔ done straight away; the
/// expanded body carries description, labels, not-applicable (with the
/// mandatory reason), comments, ad-hoc edit/remove (author or admin, open
/// items only) and the per-item log.
export function ChecklistItemRow({
  ticketId,
  item,
  isNext,
  canEditAdHoc,
  onStateChanged,
}: {
  ticketId: string;
  item: TicketChecklistItem;
  isNext: boolean;
  canEditAdHoc: boolean;
  onStateChanged?: (item: TicketChecklistItem) => void;
}) {
  const queryClient = useQueryClient();
  const [expanded, setExpanded] = React.useState(false);
  const [naMode, setNaMode] = React.useState(false);
  const [naReason, setNaReason] = React.useState("");
  const [editMode, setEditMode] = React.useState(false);
  const [comment, setComment] = React.useState("");
  const [confirmRemove, setConfirmRemove] = React.useState(false);
  const { time: serverTime } = useServerTime();
  const offset = serverTime?.offsetMinutes ?? 0;
  const fmt = (iso: string) => toServerLocal(iso, offset);

  const invalidate = () => {
    queryClient.invalidateQueries({ queryKey: ticketChecklistsKey(ticketId) });
    queryClient.invalidateQueries({ queryKey: ["ticket", ticketId, "checklist-item-events", item.id] });
  };

  const setState = useMutation({
    mutationFn: ({ state, reason }: { state: ChecklistItemState; reason?: string }) =>
      ticketChecklistApi.setItemState(ticketId, item.id, state, reason),
    onMutate: async ({ state }) => {
      // Optimistic flip so the tick feels instant; the realtime push and
      // the invalidate below reconcile counters from the server.
      const key = ticketChecklistsKey(ticketId);
      await queryClient.cancelQueries({ queryKey: key });
      const prev = queryClient.getQueryData<{ items: Array<{ id: string; items: TicketChecklistItem[]; requiredDone: number; doneItems: number }> }>(key);
      if (prev) {
        queryClient.setQueryData(key, {
          ...prev,
          items: prev.items.map((c) =>
            c.id !== item.checklistId
              ? c
              : {
                  ...c,
                  items: c.items.map((i) => (i.id === item.id ? { ...i, state } : i)),
                }),
        });
      }
      return { prev };
    },
    onError: (err, _vars, ctx) => {
      if (ctx?.prev) queryClient.setQueryData(ticketChecklistsKey(ticketId), ctx.prev);
      toast.error(checklistErrorMessage(err, "Could not update the item"));
    },
    onSuccess: (updated) => {
      setNaMode(false);
      setNaReason("");
      onStateChanged?.(updated);
    },
    onSettled: invalidate,
  });

  const addComment = useMutation({
    mutationFn: (text: string) => ticketChecklistApi.addComment(ticketId, item.id, text),
    onSuccess: () => {
      setComment("");
      invalidate();
    },
    onError: (err) => toast.error(checklistErrorMessage(err, "Could not add the comment")),
  });

  const removeItem = useMutation({
    mutationFn: () => ticketChecklistApi.removeItem(ticketId, item.id),
    onSuccess: () => {
      toast.success("Item removed");
      invalidate();
    },
    onError: (err) => toast.error(checklistErrorMessage(err, "Could not remove the item")),
  });

  const isDone = item.state === "done";
  const isNa = item.state === "na";
  const isOpen = item.state === "open";

  const toggle = () => {
    if (setState.isPending) return;
    if (isNa) {
      setState.mutate({ state: "open" });
      return;
    }
    setState.mutate({ state: isDone ? "open" : "done" });
  };

  const stateHint = !isOpen && item.stateChangedByName && item.stateChangedUtc
    ? `${isDone ? "Done" : "Not applicable"} — ${item.stateChangedByName}, ${fmt(item.stateChangedUtc)}`
    : isOpen
      ? "Mark as done"
      : undefined;

  return (
    <li
      id={`cl-item-${item.id}`}
      className={cn(
        "group/item rounded-md border transition-colors",
        isNext
          ? "border-amber-400/40 bg-amber-400/[0.06]"
          : "border-transparent hover:border-glass hover:bg-glass",
        expanded && "border-glass bg-glass",
      )}
    >
      <div className="flex items-start gap-2.5 px-2 py-1.5">
        <button
          type="button"
          onClick={toggle}
          disabled={setState.isPending}
          aria-pressed={isDone}
          aria-label={isDone ? "Mark as open" : isNa ? "Reopen (currently not applicable)" : "Mark as done"}
          title={stateHint}
          className={cn(
            "mt-0.5 inline-flex h-[18px] w-[18px] shrink-0 items-center justify-center rounded-[5px] border transition-all duration-150",
            isDone && "border-emerald-400/70 bg-emerald-400/90 text-emerald-950 shadow-[0_0_0_3px_rgba(52,211,153,0.15)]",
            isNa && "border-glass bg-glass text-muted-foreground/70",
            isOpen && "border-foreground/30 bg-transparent hover:border-emerald-400/70 hover:bg-emerald-400/10",
          )}
        >
          {isDone && <Check className="h-3 w-3" strokeWidth={3} />}
          {isNa && <Minus className="h-3 w-3" strokeWidth={3} />}
        </button>

        <button
          type="button"
          onClick={() => setExpanded((v) => !v)}
          className="min-w-0 flex-1 text-left focus-visible:outline-none"
          aria-expanded={expanded}
        >
          <span
            className={cn(
              "block text-sm leading-snug",
              isDone && "text-muted-foreground/70 line-through decoration-foreground/30",
              isNa && "text-muted-foreground/50 line-through decoration-foreground/20",
              isOpen && "text-foreground",
            )}
          >
            {item.title}
          </span>
          {(item.teamLabel || item.timingLabel || !item.isRequired || item.isAdHoc || item.description || item.linkUrl || item.commentCount > 0 || isNa) && (
            <span className="mt-0.5 flex flex-wrap items-center gap-1.5">
              {item.timingLabel && <Chip tone="sky">{item.timingLabel}</Chip>}
              {item.teamLabel && <Chip tone="violet">{item.teamLabel}</Chip>}
              {!item.isRequired && <Chip tone="muted">optional</Chip>}
              {item.isAdHoc && (
                <Chip tone="muted" title={item.addedByName ? `Added on the ticket by ${item.addedByName}` : "Added on the ticket"}>
                  <UserPlus className="h-2.5 w-2.5" /> {item.addedByName ?? "added"}
                </Chip>
              )}
              {isNa && item.naReason && (
                <span className="text-[11px] text-muted-foreground/60 truncate max-w-[220px]" title={item.naReason}>
                  n/a: {item.naReason}
                </span>
              )}
              {item.description && !expanded && (
                <AlignLeft className="h-3 w-3 text-muted-foreground/50" aria-label="Has a description" />
              )}
              {item.commentCount > 0 && (
                <span className="inline-flex items-center gap-0.5 text-[11px] text-muted-foreground/70">
                  <MessageSquare className="h-3 w-3" /> {item.commentCount}
                </span>
              )}
            </span>
          )}
        </button>

        <span className="flex shrink-0 items-center gap-0.5">
          {item.linkUrl && (
            <a
              href={item.linkUrl}
              target="_blank"
              rel="noreferrer noopener"
              title={item.linkLabel || item.linkUrl}
              className="rounded p-1 text-muted-foreground/60 hover:text-foreground hover:bg-glass-hover"
              onClick={(e) => e.stopPropagation()}
            >
              <ExternalLink className="h-3.5 w-3.5" />
            </a>
          )}
          <button
            type="button"
            onClick={() => setExpanded((v) => !v)}
            className="rounded p-1 text-muted-foreground/50 hover:text-foreground hover:bg-glass-hover"
            aria-label={expanded ? "Collapse" : "Expand"}
          >
            <ChevronDown className={cn("h-3.5 w-3.5 transition-transform", expanded && "rotate-180")} />
          </button>
        </span>
      </div>

      {expanded && (
        <div className="border-t border-glass px-3 py-2.5 space-y-3">
          {editMode ? (
            <EditItemForm
              ticketId={ticketId}
              item={item}
              onDone={() => {
                setEditMode(false);
                invalidate();
              }}
              onCancel={() => setEditMode(false)}
            />
          ) : (
            <>
              {item.description && (
                <p className="whitespace-pre-wrap text-sm text-foreground/85 leading-relaxed">{item.description}</p>
              )}
              {item.linkUrl && (
                <a
                  href={item.linkUrl}
                  target="_blank"
                  rel="noreferrer noopener"
                  className="inline-flex items-center gap-1.5 text-xs text-primary hover:underline underline-offset-2"
                >
                  <ExternalLink className="h-3.5 w-3.5" />
                  {item.linkLabel || item.linkUrl}
                </a>
              )}
              {isNa && item.naReason && (
                <p className="text-xs text-muted-foreground">
                  <span className="text-muted-foreground/60">Not applicable —</span> {item.naReason}
                </p>
              )}
              {!isOpen && item.stateChangedByName && item.stateChangedUtc && (
                <p className="text-[11px] text-muted-foreground/60">
                  {isDone ? "Done" : "Marked not applicable"} by {item.stateChangedByName} · {fmt(item.stateChangedUtc)}
                </p>
              )}
            </>
          )}

          {naMode ? (
            <div className="space-y-2 rounded-md border border-glass bg-glass p-2">
              <label className="block text-xs text-muted-foreground">
                Why doesn't this item apply? <span className="text-amber-300/80">(required)</span>
              </label>
              <textarea
                autoFocus
                value={naReason}
                onChange={(e) => setNaReason(e.target.value)}
                rows={2}
                maxLength={2000}
                placeholder="e.g. customer has no on-prem server"
                className="w-full resize-y rounded-md border border-glass bg-background/40 px-2 py-1.5 text-sm outline-none focus:ring-1 focus:ring-ring"
              />
              <div className="flex justify-end gap-2">
                <Button type="button" size="sm" variant="ghost" onClick={() => { setNaMode(false); setNaReason(""); }}>
                  Cancel
                </Button>
                <Button
                  type="button"
                  size="sm"
                  disabled={!naReason.trim() || setState.isPending}
                  onClick={() => setState.mutate({ state: "na", reason: naReason.trim() })}
                >
                  <CircleSlash className="h-3.5 w-3.5" />
                  Mark not applicable
                </Button>
              </div>
            </div>
          ) : (
            !editMode && (
              <div className="flex flex-wrap items-center gap-1.5">
                {isOpen && (
                  <ActionChip onClick={() => setNaMode(true)} title="Skip this item with a reason — counts as done for the close block">
                    <CircleSlash className="h-3.5 w-3.5" /> Not applicable
                  </ActionChip>
                )}
                {!isOpen && (
                  <ActionChip onClick={() => setState.mutate({ state: "open" })} disabled={setState.isPending}>
                    <RotateCcw className="h-3.5 w-3.5" /> Reopen
                  </ActionChip>
                )}
                {item.isAdHoc && canEditAdHoc && isOpen && (
                  <>
                    <ActionChip onClick={() => setEditMode(true)}>
                      <Pencil className="h-3.5 w-3.5" /> Edit
                    </ActionChip>
                    {confirmRemove ? (
                      <>
                        <span className="text-xs text-muted-foreground">Remove this item?</span>
                        <ActionChip onClick={() => removeItem.mutate()} disabled={removeItem.isPending} tone="danger">
                          <Trash2 className="h-3.5 w-3.5" /> Yes, remove
                        </ActionChip>
                        <ActionChip onClick={() => setConfirmRemove(false)}>Keep</ActionChip>
                      </>
                    ) : (
                      <ActionChip onClick={() => setConfirmRemove(true)} tone="danger">
                        <Trash2 className="h-3.5 w-3.5" /> Remove
                      </ActionChip>
                    )}
                  </>
                )}
              </div>
            )
          )}

          <form
            className="flex items-center gap-2"
            onSubmit={(e) => {
              e.preventDefault();
              const text = comment.trim();
              if (text) addComment.mutate(text);
            }}
          >
            <Input
              value={comment}
              onChange={(e) => setComment(e.target.value)}
              placeholder="Add a comment…"
              maxLength={4000}
              className="h-8 text-sm bg-background/40"
            />
            <Button type="submit" size="sm" variant="secondary" disabled={!comment.trim() || addComment.isPending} className="h-8">
              <Send className="h-3.5 w-3.5" />
            </Button>
          </form>

          <ItemHistory ticketId={ticketId} itemId={item.id} fmt={fmt} />
        </div>
      )}
    </li>
  );
}

function Chip({
  tone,
  title,
  children,
}: {
  tone: "sky" | "violet" | "muted";
  title?: string;
  children: React.ReactNode;
}) {
  return (
    <span
      title={title}
      className={cn(
        "inline-flex items-center gap-1 rounded px-1.5 py-[1px] text-[10px] font-medium leading-4",
        tone === "sky" && "border border-sky-400/25 bg-sky-400/10 text-sky-200",
        tone === "violet" && "border border-violet-400/25 bg-violet-400/10 text-violet-200",
        tone === "muted" && "border border-glass bg-glass text-muted-foreground/70",
      )}
    >
      {children}
    </span>
  );
}

function ActionChip({
  onClick,
  disabled,
  title,
  tone,
  children,
}: {
  onClick: () => void;
  disabled?: boolean;
  title?: string;
  tone?: "danger";
  children: React.ReactNode;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={disabled}
      title={title}
      className={cn(
        "inline-flex items-center gap-1 rounded-md border border-glass bg-glass px-2 py-1 text-xs text-muted-foreground transition-colors hover:text-foreground hover:bg-glass-hover disabled:opacity-50",
        tone === "danger" && "hover:border-red-400/40 hover:text-red-200",
      )}
    >
      {children}
    </button>
  );
}

function EditItemForm({
  ticketId,
  item,
  onDone,
  onCancel,
}: {
  ticketId: string;
  item: TicketChecklistItem;
  onDone: () => void;
  onCancel: () => void;
}) {
  const [title, setTitle] = React.useState(item.title);
  const [description, setDescription] = React.useState(item.description);
  const [linkUrl, setLinkUrl] = React.useState(item.linkUrl);
  const save = useMutation({
    mutationFn: () =>
      ticketChecklistApi.updateItem(ticketId, item.id, {
        title: title.trim(),
        description: description.trim(),
        linkUrl: linkUrl.trim(),
        linkLabel: item.linkLabel,
        teamLabel: item.teamLabel,
        timingLabel: item.timingLabel,
      }),
    onSuccess: onDone,
    onError: (err) => toast.error(checklistErrorMessage(err, "Could not save the item")),
  });
  return (
    <form
      className="space-y-2"
      onSubmit={(e) => {
        e.preventDefault();
        if (title.trim()) save.mutate();
      }}
    >
      <Input autoFocus value={title} onChange={(e) => setTitle(e.target.value)} maxLength={300} placeholder="Item title" className="h-8 text-sm" />
      <textarea
        value={description}
        onChange={(e) => setDescription(e.target.value)}
        rows={2}
        maxLength={4000}
        placeholder="Description (optional)"
        className="w-full resize-y rounded-md border border-glass bg-background/40 px-2 py-1.5 text-sm outline-none focus:ring-1 focus:ring-ring"
      />
      <Input value={linkUrl} onChange={(e) => setLinkUrl(e.target.value)} maxLength={2000} placeholder="https://… (optional link)" className="h-8 text-sm" />
      <div className="flex justify-end gap-2">
        <Button type="button" size="sm" variant="ghost" onClick={onCancel}>Cancel</Button>
        <Button type="submit" size="sm" disabled={!title.trim() || save.isPending}>Save</Button>
      </div>
    </form>
  );
}

function ItemHistory({ ticketId, itemId, fmt }: { ticketId: string; itemId: string; fmt: (iso: string) => string }) {
  const [open, setOpen] = React.useState(false);
  const q = useQuery({
    queryKey: ["ticket", ticketId, "checklist-item-events", itemId],
    queryFn: () => ticketChecklistApi.itemEvents(ticketId, itemId),
    enabled: open,
    staleTime: 10_000,
  });
  const events: ChecklistItemEvent[] = q.data?.items ?? [];
  return (
    <div>
      <button
        type="button"
        onClick={() => setOpen((v) => !v)}
        className="inline-flex items-center gap-1 text-[11px] text-muted-foreground/70 hover:text-foreground"
      >
        <History className="h-3 w-3" />
        {open ? "Hide log" : "Show log"}
        <ChevronDown className={cn("h-3 w-3 transition-transform", open && "rotate-180")} />
      </button>
      {open && (
        <ul className="mt-1.5 space-y-1 border-l border-glass pl-2.5">
          {q.isLoading && <li className="text-[11px] text-muted-foreground/60">Loading…</li>}
          {!q.isLoading && events.length === 0 && (
            <li className="text-[11px] text-muted-foreground/60">No activity yet.</li>
          )}
          {events.map((e) => (
            <li key={e.id} className="text-[11px] leading-snug">
              <span className="text-foreground/80">{e.userName ?? "System"}</span>{" "}
              <span className="text-muted-foreground/80">{describeEvent(e)}</span>
              {e.comment && e.kind !== "item_added" && e.kind !== "item_edited" && (
                <span className="block text-muted-foreground/90 whitespace-pre-wrap">“{e.comment}”</span>
              )}
              <span className="block text-muted-foreground/50">{fmt(e.createdUtc)}</span>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

function describeEvent(e: ChecklistItemEvent): string {
  switch (e.kind) {
    case "state_change": {
      if (e.toState === "done") return "ticked it off";
      if (e.toState === "na") return "marked it not applicable";
      if (e.toState === "open") return e.fromState === "done" ? "reopened it" : "set it back to open";
      return `changed it (${e.fromState ?? "?"} → ${e.toState ?? "?"})`;
    }
    case "comment":
      return "commented";
    case "item_added":
      return "added this item";
    case "item_edited":
      return "edited this item";
    case "item_removed":
      return "removed this item";
    default:
      return e.kind;
  }
}
