import { Link } from "@tanstack/react-router";
import { X } from "lucide-react";
import { useRecentTicketsStore } from "@/stores/useRecentTicketsStore";
import {
  Tooltip,
  TooltipContent,
  TooltipTrigger,
  TooltipProvider,
} from "@/components/ui/tooltip";

export function RecentTickets() {
  const tickets = useRecentTicketsStore((s) => s.recentTickets);
  const removeTicket = useRecentTicketsStore((s) => s.removeTicket);

  if (tickets.length === 0) return null;

  const visible = tickets.slice(0, 7);
  const overflow = tickets.length - visible.length;

  return (
    <TooltipProvider delayDuration={200}>
      <div className="flex items-center gap-1.5">
        {visible.map((t) => (
          <Tooltip key={t.id}>
            <TooltipTrigger asChild>
              <div className="group flex items-center gap-1 rounded-md border border-white/10 bg-white/[0.04] px-2 py-1 text-xs text-muted-foreground transition-colors hover:bg-white/[0.07] hover:text-foreground">
                <Link
                  to="/tickets/$ticketId"
                  params={{ ticketId: t.id }}
                  className="font-mono"
                >
                  #{t.number}
                </Link>
                <button
                  type="button"
                  onClick={(e) => {
                    e.stopPropagation();
                    removeTicket(t.id);
                  }}
                  className="ml-0.5 hidden rounded p-0.5 hover:bg-white/10 group-hover:inline-flex"
                  aria-label={`Remove #${t.number} from recent`}
                >
                  <X className="h-3 w-3" />
                </button>
              </div>
            </TooltipTrigger>
            <TooltipContent side="bottom" className="max-w-[200px] text-xs">
              {t.subject}
            </TooltipContent>
          </Tooltip>
        ))}
        {overflow > 0 && (
          <span className="text-xs text-muted-foreground">+{overflow}</span>
        )}
      </div>
    </TooltipProvider>
  );
}
