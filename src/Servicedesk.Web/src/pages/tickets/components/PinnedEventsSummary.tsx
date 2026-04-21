import * as React from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Pin, X, Pencil, Check } from "lucide-react";
import { cn } from "@/lib/utils";
import {
  ticketApi,
  type TicketEvent,
  type TicketEventPin,
} from "@/lib/ticket-api";
import {
  Popover,
  PopoverContent,
  PopoverTrigger,
} from "@/components/ui/popover";
import { EVENT_CONFIG } from "./TicketTimeline";

type PinnedEventsSummaryProps = {
  ticketId: string;
  pinnedEvents: TicketEventPin[];
  events: TicketEvent[];
};

function PinnedItem({
  pin,
  event,
  ticketId,
  onJump,
}: {
  pin: TicketEventPin;
  event: TicketEvent | undefined;
  ticketId: string;
  onJump: () => void;
}) {
  const queryClient = useQueryClient();
  const [editingRemark, setEditingRemark] = React.useState(false);
  const [remarkDraft, setRemarkDraft] = React.useState(pin.remark);

  const config = event
    ? EVENT_CONFIG[event.eventType] ?? { icon: Pin, dotColor: "bg-white/30", label: event.eventType }
    : { icon: Pin, dotColor: "bg-white/30", label: "Event" };
  const Icon = config.icon;

  const unpinMutation = useMutation({
    mutationFn: () => ticketApi.unpinEvent(ticketId, pin.eventId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["ticket", ticketId] });
      toast.success("Event unpinned");
    },
    onError: () => toast.error("Failed to unpin"),
  });

  const remarkMutation = useMutation({
    mutationFn: (remark: string) =>
      ticketApi.updatePinRemark(ticketId, pin.eventId, remark),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["ticket", ticketId] });
      setEditingRemark(false);
      toast.success("Remark updated");
    },
    onError: () => toast.error("Failed to update remark"),
  });

  const bodyPreview = React.useMemo(() => {
    if (!event) return "Unknown event";
    if (event.bodyText?.trim()) return event.bodyText.slice(0, 80);
    if (event.bodyHtml) {
      // DOMParser builds a synthetic document that does NOT fetch external
      // resources (img/iframe/link). Using `div.innerHTML = bodyHtml` would
      // attach a live DOM node and the browser would immediately fire GETs
      // for every inline `<img src="/api/.../attachments/…">` — once per
      // render of this preview. Combined with re-renders triggered by
      // popover open/close, query invalidation, or a parent prop change,
      // that produces "100 reqs for the same 2 attachments / second"
      // without anything actually being viewed.
      const doc = new DOMParser().parseFromString(event.bodyHtml, "text/html");
      const text = doc.body.textContent?.trim();
      if (text) return text.slice(0, 80);
    }
    return config.label;
  }, [event, config.label]);

  return (
    <div
      className="flex items-start gap-3 px-3 py-2.5 hover:bg-white/[0.04] transition-colors group/item cursor-pointer"
      onClick={onJump}
      role="button"
      tabIndex={0}
      onKeyDown={(e) => { if (e.key === "Enter") onJump(); }}
    >
      <span
        className={cn(
          "mt-0.5 w-5 h-5 rounded-full flex items-center justify-center shrink-0",
          config.dotColor
        )}
      >
        <Icon className="w-3 h-3 text-white" />
      </span>

      <div className="flex-1 min-w-0 space-y-1">
        <div className="flex items-center gap-2">
          <span className="text-xs font-medium text-foreground/80 truncate">
            {config.label}
          </span>
          {event?.authorName && (
            <span className="text-[11px] text-muted-foreground/50 truncate">
              by {event.authorName}
            </span>
          )}
        </div>

        <p className="text-xs text-muted-foreground/70 truncate">
          {bodyPreview}
        </p>

        {editingRemark ? (
          <div className="flex items-center gap-1.5 mt-1" onClick={(e) => e.stopPropagation()}>
            <input
              type="text"
              value={remarkDraft}
              onChange={(e) => setRemarkDraft(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === "Enter") remarkMutation.mutate(remarkDraft);
                if (e.key === "Escape") {
                  setRemarkDraft(pin.remark);
                  setEditingRemark(false);
                }
              }}
              className="flex-1 min-w-0 rounded border border-white/10 bg-white/[0.04] px-2 py-1 text-xs text-foreground outline-none focus:border-primary/50"
              autoFocus
            />
            <button
              type="button"
              onClick={() => remarkMutation.mutate(remarkDraft)}
              className="p-0.5 rounded text-green-400 hover:bg-green-400/10"
            >
              <Check className="h-3 w-3" />
            </button>
            <button
              type="button"
              onClick={() => {
                setRemarkDraft(pin.remark);
                setEditingRemark(false);
              }}
              className="p-0.5 rounded text-muted-foreground hover:bg-white/[0.06]"
            >
              <X className="h-3 w-3" />
            </button>
          </div>
        ) : pin.remark ? (
          <div className="flex items-center gap-1.5 mt-0.5">
            <p className="text-[11px] italic text-primary/60 truncate flex-1">
              {pin.remark}
            </p>
            <button
              type="button"
              onClick={(e) => { e.stopPropagation(); setEditingRemark(true); }}
              className="p-0.5 rounded text-muted-foreground/30 opacity-0 group-hover/item:opacity-100 hover:text-foreground transition-all"
            >
              <Pencil className="h-2.5 w-2.5" />
            </button>
          </div>
        ) : (
          <button
            type="button"
            onClick={(e) => { e.stopPropagation(); setEditingRemark(true); }}
            className="text-[11px] text-muted-foreground/30 hover:text-muted-foreground/60 transition-colors opacity-0 group-hover/item:opacity-100"
          >
            Add remark...
          </button>
        )}
      </div>

      <div className="flex items-center gap-1 shrink-0 mt-0.5" onClick={(e) => e.stopPropagation()}>
        <button
          type="button"
          onClick={() => unpinMutation.mutate()}
          className="p-1 rounded text-muted-foreground/40 hover:text-red-400 hover:bg-red-400/10 transition-all"
          title="Unpin"
        >
          <X className="h-3 w-3" />
        </button>
      </div>
    </div>
  );
}

export function PinnedEventsSummary({
  ticketId,
  pinnedEvents,
  events,
}: PinnedEventsSummaryProps) {
  const [open, setOpen] = React.useState(false);

  const eventsById = React.useMemo(() => {
    const map = new Map<number, TicketEvent>();
    for (const e of events) map.set(e.id, e);
    return map;
  }, [events]);

  const scrollToEvent = (eventId: number) => {
    setOpen(false);
    // Small delay so popover closes before scroll
    requestAnimationFrame(() => {
      const el = document.getElementById(`event-${eventId}`);
      if (el) {
        el.scrollIntoView({ behavior: "smooth", block: "center" });
        el.classList.add("ring-2", "ring-primary/50", "rounded-lg");
        setTimeout(
          () => el.classList.remove("ring-2", "ring-primary/50", "rounded-lg"),
          2000
        );
      }
    });
  };

  const count = pinnedEvents.length;
  const label = count === 1 ? "1 pinned comment" : `${count} pinned comments`;

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <button
          type="button"
          className="w-full flex items-center gap-2 rounded-[var(--radius)] border border-amber-500/20 bg-amber-500/[0.04] px-3 py-2 text-sm text-amber-300/80 hover:bg-amber-500/[0.08] hover:border-amber-500/30 transition-colors"
        >
          <Pin className="h-3.5 w-3.5 shrink-0" />
          <span className="font-medium">{label}</span>
        </button>
      </PopoverTrigger>
      <PopoverContent
        align="start"
        className="w-[400px] p-0 border-white/10 bg-background/95 backdrop-blur-xl"
      >
        <div className="flex items-center justify-between px-3 py-2 border-b border-white/10">
          <span className="text-xs font-medium text-foreground/80">
            Pinned events
          </span>
          <span className="text-[11px] text-muted-foreground/50">{count}</span>
        </div>
        <div className="max-h-[480px] overflow-y-auto">
          <div className="divide-y divide-white/[0.06]">
            {pinnedEvents.map((pin) => (
              <PinnedItem
                key={pin.id}
                pin={pin}
                event={eventsById.get(pin.eventId)}
                ticketId={ticketId}
                onJump={() => scrollToEvent(pin.eventId)}
              />
            ))}
          </div>
        </div>
      </PopoverContent>
    </Popover>
  );
}
