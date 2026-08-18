import * as React from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import {
  ArrowDownToLine,
  CheckCircle2,
  ChevronDown,
  ExternalLink,
  EyeOff,
  ListChecks,
  Lock,
  MoreHorizontal,
  Plus,
  Trash2,
  X,
} from "lucide-react";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { useAuth } from "@/auth/authStore";
import {
  ticketChecklistApi,
  checklistErrorMessage,
  nextOpenItem,
  type ChecklistSettings,
  type TicketChecklist,
  type TicketChecklistItem,
  type TicketChecklistSection,
} from "@/lib/checklist-api";
import { AttachChecklistMenu } from "./AttachChecklistMenu";
import { ChecklistItemRow } from "./ChecklistItemRow";
import { ProgressRing } from "./ProgressRing";
import { openChecklistWindow } from "./checklistWindow";
import { ticketChecklistsKey } from "./useTicketChecklists";

/// The working surface: docked in the ticket's right column (in place of
/// the details side panel) or rendered full-height in the pop-out window.
/// One checklist is active at a time (pills when the ticket has several);
/// sections collapse, done items can be hidden, "next open" jumps to where
/// the agent left off, items can be added per section.
export function TicketChecklistPanel({
  ticketId,
  checklists,
  settings,
  activeChecklistId,
  onActiveChange,
  onClose,
  mode,
  className,
}: {
  ticketId: string;
  checklists: TicketChecklist[];
  settings: ChecklistSettings;
  activeChecklistId: string | null;
  onActiveChange: (id: string) => void;
  /// Docked mode: "back to details" close button. Absent in the pop-out.
  onClose?: () => void;
  mode: "docked" | "popout";
  className?: string;
}) {
  const queryClient = useQueryClient();
  const { user } = useAuth();
  const isAdmin = user?.role === "Admin";
  const [hideDone, setHideDone] = React.useState(false);
  const [collapsed, setCollapsed] = React.useState<Set<string>>(() => new Set());

  const active = React.useMemo(
    () => checklists.find((c) => c.id === activeChecklistId) ?? checklists[0] ?? null,
    [checklists, activeChecklistId],
  );
  // Keep the parent's notion of "active" valid when a checklist disappears
  // (detached elsewhere) or the first one is auto-selected.
  React.useEffect(() => {
    if (active && active.id !== activeChecklistId) onActiveChange(active.id);
  }, [active, activeChecklistId, onActiveChange]);

  const detach = useMutation({
    mutationFn: (checklistId: string) => ticketChecklistApi.detach(ticketId, checklistId),
    onSuccess: () => {
      toast.success("Checklist removed from the ticket");
      queryClient.invalidateQueries({ queryKey: ticketChecklistsKey(ticketId) });
      queryClient.invalidateQueries({ queryKey: ["ticket", ticketId] });
    },
    onError: (err) => toast.error(checklistErrorMessage(err, "Could not remove the checklist")),
  });

  const next = active ? nextOpenItem(active) : null;
  const jumpToNext = () => {
    if (!next) return;
    const el = document.getElementById(`cl-item-${next.id}`);
    el?.scrollIntoView({ behavior: "smooth", block: "center" });
    el?.classList.add("ring-2", "ring-amber-400/60");
    window.setTimeout(() => el?.classList.remove("ring-2", "ring-amber-400/60"), 1400);
  };

  const groups = React.useMemo(() => (active ? buildGroups(active) : []), [active]);

  const canDetach = active ? (isAdmin || !active.touched) : false;
  const detachHint = active
    ? active.touched && !isAdmin
      ? "This checklist already has progress — only an admin can remove it"
      : "Remove this checklist from the ticket"
    : "";

  return (
    <div className={cn("glass-panel flex h-full min-h-0 flex-col overflow-hidden", className)}>
      {/* Panel header */}
      <div className="flex items-center gap-2 border-b border-glass px-3 py-2">
        <ListChecks className="h-4 w-4 text-violet-300/90" />
        <span className="text-xs uppercase tracking-wider text-muted-foreground">Checklist</span>
        <span className="ml-auto flex items-center gap-0.5">
          <AttachChecklistMenu
            ticketId={ticketId}
            attachedCount={checklists.length}
            maxPerTicket={settings.maxPerTicket}
            onAttached={(c) => onActiveChange(c.id)}
          >
            <button
              type="button"
              className="rounded-md p-1.5 text-muted-foreground/70 hover:text-foreground hover:bg-glass-hover"
              title="Add another checklist"
              aria-label="Add another checklist"
            >
              <Plus className="h-4 w-4" />
            </button>
          </AttachChecklistMenu>
          {mode === "docked" && (
            <button
              type="button"
              onClick={() => openChecklistWindow(ticketId, active?.id)}
              className="rounded-md p-1.5 text-muted-foreground/70 hover:text-foreground hover:bg-glass-hover"
              title="Open in a separate window (second screen)"
              aria-label="Open in a separate window"
            >
              <ExternalLink className="h-4 w-4" />
            </button>
          )}
          {onClose && (
            <button
              type="button"
              onClick={onClose}
              className="rounded-md p-1.5 text-muted-foreground/70 hover:text-foreground hover:bg-glass-hover"
              title="Back to ticket details"
              aria-label="Back to ticket details"
            >
              <X className="h-4 w-4" />
            </button>
          )}
        </span>
      </div>

      {checklists.length === 0 || !active ? (
        <div className="flex flex-1 flex-col items-center justify-center gap-3 px-6 py-10 text-center">
          <span className="inline-flex h-12 w-12 items-center justify-center rounded-full border border-glass bg-glass">
            <ListChecks className="h-5 w-5 text-muted-foreground/60" />
          </span>
          <p className="text-sm text-muted-foreground">No checklist on this ticket yet.</p>
          <AttachChecklistMenu
            ticketId={ticketId}
            attachedCount={0}
            maxPerTicket={settings.maxPerTicket}
            onAttached={(c) => onActiveChange(c.id)}
            align="center"
          >
            <Button type="button" size="sm" variant="secondary">
              <Plus className="h-4 w-4" /> Add checklist
            </Button>
          </AttachChecklistMenu>
        </div>
      ) : (
        <>
          {/* Checklist switcher */}
          {checklists.length > 1 && (
            <div className="flex gap-1.5 overflow-x-auto border-b border-glass px-3 py-2 [scrollbar-width:thin]">
              {checklists.map((c) => {
                const complete = c.completedUtc !== null;
                return (
                  <button
                    key={c.id}
                    type="button"
                    onClick={() => onActiveChange(c.id)}
                    className={cn(
                      "inline-flex shrink-0 items-center gap-1.5 rounded-full border px-2.5 py-1 text-xs transition-colors",
                      c.id === active.id
                        ? "border-primary/40 bg-primary/15 text-foreground"
                        : "border-glass bg-glass text-muted-foreground hover:text-foreground",
                    )}
                  >
                    <ProgressRing done={c.requiredDone} total={c.requiredTotal} size={14} stroke={2.5} />
                    <span className="max-w-[140px] truncate">{c.name}</span>
                    <span className={cn("tabular-nums", complete ? "text-emerald-300" : "text-amber-200")}>
                      {c.requiredDone}/{c.requiredTotal}
                    </span>
                  </button>
                );
              })}
            </div>
          )}

          {/* Active checklist summary */}
          <div className="border-b border-glass px-3 py-3">
            <div className="flex items-start gap-3">
              <ProgressRing done={active.requiredDone} total={active.requiredTotal} size={44} stroke={4}>
                {active.completedUtc ? (
                  <CheckCircle2 className="h-5 w-5 text-emerald-400" />
                ) : (
                  <span className="text-[11px] font-semibold tabular-nums text-foreground/90">
                    {active.requiredTotal > 0 ? Math.round((active.requiredDone / active.requiredTotal) * 100) : 0}%
                  </span>
                )}
              </ProgressRing>
              <div className="min-w-0 flex-1">
                <div className="flex items-start gap-2">
                  <h3 className="min-w-0 flex-1 truncate text-sm font-semibold text-foreground" title={active.name}>
                    {active.name}
                  </h3>
                  <DropdownMenu>
                    <DropdownMenuTrigger asChild>
                      <button
                        type="button"
                        className="rounded-md p-1 text-muted-foreground/70 hover:text-foreground hover:bg-glass-hover"
                        aria-label="Checklist actions"
                      >
                        <MoreHorizontal className="h-4 w-4" />
                      </button>
                    </DropdownMenuTrigger>
                    <DropdownMenuContent align="end" className="w-56">
                      <DropdownMenuItem onSelect={() => setHideDone((v) => !v)}>
                        <EyeOff className="h-4 w-4" />
                        {hideDone ? "Show completed items" : "Hide completed items"}
                      </DropdownMenuItem>
                      {mode === "docked" && (
                        <DropdownMenuItem onSelect={() => openChecklistWindow(ticketId, active.id)}>
                          <ExternalLink className="h-4 w-4" />
                          Open in a separate window
                        </DropdownMenuItem>
                      )}
                      <DropdownMenuSeparator />
                      <DropdownMenuItem
                        disabled={!canDetach || detach.isPending}
                        onSelect={() => detach.mutate(active.id)}
                        className="text-red-300 focus:text-red-200"
                        title={detachHint}
                      >
                        <Trash2 className="h-4 w-4" />
                        Remove checklist from ticket
                      </DropdownMenuItem>
                    </DropdownMenuContent>
                  </DropdownMenu>
                </div>
                <p className="text-xs text-muted-foreground">
                  <span className="tabular-nums text-foreground/80">{active.requiredDone}</span> of{" "}
                  <span className="tabular-nums text-foreground/80">{active.requiredTotal}</span> required done
                  {active.totalItems > active.requiredTotal && (
                    <span className="text-muted-foreground/60"> · {active.totalItems - active.requiredTotal} optional</span>
                  )}
                </p>
                <div className="mt-1.5 flex flex-wrap items-center gap-1.5">
                  {active.blockClose && !active.completedUtc && (
                    <span
                      className="inline-flex items-center gap-1 rounded-md border border-amber-400/30 bg-amber-400/10 px-1.5 py-0.5 text-[10px] font-medium text-amber-200"
                      title="Resolving/closing the ticket is blocked until every required item is done or not applicable"
                    >
                      <Lock className="h-3 w-3" /> blocks close
                    </span>
                  )}
                  {active.completedUtc && (
                    <span className="inline-flex items-center gap-1 rounded-md border border-emerald-400/30 bg-emerald-400/10 px-1.5 py-0.5 text-[10px] font-medium text-emerald-200">
                      <CheckCircle2 className="h-3 w-3" /> complete
                    </span>
                  )}
                  {next && (
                    <button
                      type="button"
                      onClick={jumpToNext}
                      className="inline-flex items-center gap-1 rounded-md border border-glass bg-glass px-1.5 py-0.5 text-[10px] font-medium text-muted-foreground hover:text-foreground hover:bg-glass-hover"
                      title={`Jump to the next open item: ${next.title}`}
                    >
                      <ArrowDownToLine className="h-3 w-3" /> next open
                    </button>
                  )}
                  {hideDone && (
                    <button
                      type="button"
                      onClick={() => setHideDone(false)}
                      className="inline-flex items-center gap-1 rounded-md border border-glass bg-glass px-1.5 py-0.5 text-[10px] font-medium text-muted-foreground hover:text-foreground"
                    >
                      <EyeOff className="h-3 w-3" /> hiding done
                    </button>
                  )}
                </div>
              </div>
            </div>
            {active.description && (
              <p className="mt-2 line-clamp-3 whitespace-pre-wrap text-xs text-muted-foreground/80" title={active.description}>
                {active.description}
              </p>
            )}
          </div>

          {/* Items */}
          <div className="min-h-0 flex-1 overflow-y-auto px-2 py-2 [scrollbar-width:thin]">
            {groups.map((g) => {
              const key = g.section?.id ?? `__${g.kind}`;
              const isCollapsed = collapsed.has(key);
              const visible = hideDone ? g.items.filter((i) => i.state === "open") : g.items;
              const req = g.items.filter((i) => i.isRequired);
              const reqDone = req.filter((i) => i.state !== "open").length;
              return (
                <section key={key} className="mb-2">
                  {g.section && (
                    <button
                      type="button"
                      onClick={() =>
                        setCollapsed((prev) => {
                          const n = new Set(prev);
                          if (n.has(key)) n.delete(key);
                          else n.add(key);
                          return n;
                        })
                      }
                      className="sticky top-0 z-[1] mb-1 flex w-full items-center gap-2 rounded-md bg-background/60 px-2 py-1.5 text-left backdrop-blur-sm glass-hover"
                      aria-expanded={!isCollapsed}
                    >
                      <ChevronDown className={cn("h-3.5 w-3.5 text-muted-foreground/60 transition-transform", isCollapsed && "-rotate-90")} />
                      <span className="min-w-0 flex-1 truncate text-xs font-semibold uppercase tracking-wide text-muted-foreground">
                        {g.section.title}
                      </span>
                      <span
                        className={cn(
                          "text-[11px] tabular-nums",
                          req.length > 0 && reqDone === req.length ? "text-emerald-300/90" : "text-muted-foreground/70",
                        )}
                      >
                        {reqDone}/{req.length}
                      </span>
                    </button>
                  )}
                  {!g.section && g.kind === "tail" && groups.length > 1 && (
                    <div className="mb-1 px-2 py-1 text-xs font-semibold uppercase tracking-wide text-muted-foreground/70">
                      Other items
                    </div>
                  )}
                  {!isCollapsed && (
                    <ul className="space-y-0.5">
                      {visible.map((item) => (
                        <ChecklistItemRow
                          key={item.id}
                          ticketId={ticketId}
                          item={item}
                          isNext={next?.id === item.id}
                          canEditAdHoc={isAdmin || item.addedByUserId === user?.id}
                        />
                      ))}
                      {visible.length === 0 && g.items.length > 0 && (
                        <li className="px-2 py-1 text-[11px] text-muted-foreground/60">All items in this section are done.</li>
                      )}
                      <li>
                        <AddItemInline
                          ticketId={ticketId}
                          checklistId={active.id}
                          sectionId={g.section?.id ?? null}
                          disabled={active.totalItems >= settings.maxItemsPerChecklist}
                        />
                      </li>
                    </ul>
                  )}
                </section>
              );
            })}
          </div>
        </>
      )}
    </div>
  );
}

type Group = {
  kind: "head" | "section" | "tail";
  section: TicketChecklistSection | null;
  items: TicketChecklistItem[];
};

/// Sections in order, each with its items; ungrouped items go before the
/// first section when the template started ungrouped, otherwise after the
/// last (ad-hoc items added "at the end").
function buildGroups(c: TicketChecklist): Group[] {
  const items = [...c.items].sort((a, b) => a.sortOrder - b.sortOrder || a.createdUtc.localeCompare(b.createdUtc));
  const sections = [...c.sections].sort((a, b) => a.sortOrder - b.sortOrder);
  const sectionIds = new Set(sections.map((s) => s.id));
  const grouped = items.filter((i) => i.sectionId && sectionIds.has(i.sectionId));
  const ungrouped = items.filter((i) => !i.sectionId || !sectionIds.has(i.sectionId));
  const firstGroupedOrder = grouped.length > 0 ? grouped[0].sortOrder : Number.POSITIVE_INFINITY;
  const head = ungrouped.filter((i) => i.sortOrder < firstGroupedOrder);
  const tail = ungrouped.filter((i) => i.sortOrder >= firstGroupedOrder);
  const groups: Group[] = [];
  if (sections.length === 0) {
    groups.push({ kind: "head", section: null, items });
    return groups;
  }
  if (head.length > 0) groups.push({ kind: "head", section: null, items: head });
  for (const s of sections) groups.push({ kind: "section", section: s, items: grouped.filter((i) => i.sectionId === s.id) });
  if (tail.length > 0) groups.push({ kind: "tail", section: null, items: tail });
  return groups;
}

function AddItemInline({
  ticketId,
  checklistId,
  sectionId,
  disabled,
}: {
  ticketId: string;
  checklistId: string;
  sectionId: string | null;
  disabled: boolean;
}) {
  const queryClient = useQueryClient();
  const [open, setOpen] = React.useState(false);
  const [title, setTitle] = React.useState("");
  const [description, setDescription] = React.useState("");
  const [linkUrl, setLinkUrl] = React.useState("");
  const [more, setMore] = React.useState(false);

  const add = useMutation({
    mutationFn: () =>
      ticketChecklistApi.addItem(ticketId, checklistId, {
        sectionId,
        title: title.trim(),
        description: description.trim(),
        linkUrl: linkUrl.trim(),
      }),
    onSuccess: () => {
      setTitle("");
      setDescription("");
      setLinkUrl("");
      queryClient.invalidateQueries({ queryKey: ticketChecklistsKey(ticketId) });
    },
    onError: (err) => toast.error(checklistErrorMessage(err, "Could not add the item")),
  });

  if (!open) {
    return (
      <button
        type="button"
        disabled={disabled}
        onClick={() => setOpen(true)}
        title={disabled ? "This checklist has reached the maximum number of items" : "Add an item to this section"}
        className="mt-0.5 inline-flex items-center gap-1 rounded-md px-2 py-1 text-xs text-muted-foreground/60 hover:text-foreground hover:bg-glass-hover disabled:opacity-40"
      >
        <Plus className="h-3.5 w-3.5" /> Add item
      </button>
    );
  }
  return (
    <form
      className="mt-1 space-y-1.5 rounded-md border border-glass bg-glass p-2"
      onSubmit={(e) => {
        e.preventDefault();
        if (title.trim() && !add.isPending) add.mutate();
      }}
    >
      <Input
        autoFocus
        value={title}
        onChange={(e) => setTitle(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === "Escape") setOpen(false);
        }}
        maxLength={300}
        placeholder="New item — press Enter to add"
        className="h-8 text-sm bg-background/40"
      />
      {more && (
        <>
          <textarea
            value={description}
            onChange={(e) => setDescription(e.target.value)}
            rows={2}
            maxLength={4000}
            placeholder="Description (optional)"
            className="w-full resize-y rounded-md border border-glass bg-background/40 px-2 py-1.5 text-sm outline-none focus:ring-1 focus:ring-ring"
          />
          <Input value={linkUrl} onChange={(e) => setLinkUrl(e.target.value)} maxLength={2000} placeholder="https://… (optional link)" className="h-8 text-sm bg-background/40" />
        </>
      )}
      <div className="flex items-center gap-2">
        <button type="button" onClick={() => setMore((v) => !v)} className="text-[11px] text-muted-foreground/70 hover:text-foreground">
          {more ? "Less" : "Description / link"}
        </button>
        <span className="ml-auto flex gap-1.5">
          <Button type="button" size="sm" variant="ghost" className="h-7 text-xs" onClick={() => setOpen(false)}>
            Done
          </Button>
          <Button type="submit" size="sm" className="h-7 text-xs" disabled={!title.trim() || add.isPending}>
            <Plus className="h-3.5 w-3.5" /> Add
          </Button>
        </span>
      </div>
    </form>
  );
}
