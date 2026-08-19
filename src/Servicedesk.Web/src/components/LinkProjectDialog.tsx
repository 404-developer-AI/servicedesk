import * as React from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { ArrowRight, FolderKanban, Search } from "lucide-react";
import { toast } from "sonner";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { ApiError, ticketApi } from "@/lib/ticket-api";
import type { Ticket, TicketPickerItem } from "@/lib/ticket-api";
import { cn } from "@/lib/utils";

type Props = {
  open: boolean;
  source: Ticket;
  onClose: () => void;
  onLinked?: (projectTicketId: string) => void;
};

/// v0.0.104 — picker dialog for linking the current ticket to a project
/// ticket. Only open project tickets are offered (the picker filters
/// server-side); the ticket stays fully in its own queue — the link is
/// just a pointer the project panel aggregates on. Server-side validates
/// project rules + queue access.
export function LinkProjectDialog({ open, source, onClose, onLinked }: Props) {
  const queryClient = useQueryClient();
  const [query, setQuery] = React.useState("");
  const [selected, setSelected] = React.useState<TicketPickerItem | null>(null);
  const [results, setResults] = React.useState<TicketPickerItem[]>([]);
  const [searching, setSearching] = React.useState(false);
  const [searchError, setSearchError] = React.useState<string | null>(null);

  React.useEffect(() => {
    if (!open) {
      setQuery("");
      setSelected(null);
      setResults([]);
      setSearchError(null);
    }
  }, [open]);

  // Same debounced /api/tickets/picker call as the link-parent dialog,
  // restricted to open project tickets. No recent-first: the project list
  // is short, most-recently-updated order is the useful default.
  React.useEffect(() => {
    if (!open) return;
    const handle = window.setTimeout(async () => {
      setSearching(true);
      setSearchError(null);
      try {
        const response = await ticketApi.picker(query.trim() || undefined, source.id, 20, false, true);
        setResults(response.items);
      } catch (err) {
        setSearchError(err instanceof Error ? err.message : "Search failed");
        setResults([]);
      } finally {
        setSearching(false);
      }
    }, 200);
    return () => window.clearTimeout(handle);
  }, [open, query, source.id]);

  const linkMutation = useMutation({
    mutationFn: () => ticketApi.linkProject(source.id, selected!.id),
    onSuccess: (response) => {
      queryClient.invalidateQueries({ queryKey: ["ticket", source.id] });
      queryClient.invalidateQueries({ queryKey: ["ticket", response.projectTicketId] });
      toast.success(`Linked to project #${response.projectNumber}`);
      onLinked?.(response.projectTicketId);
      onClose();
    },
    onError: (err) => {
      if (err instanceof ApiError) {
        toast.error(`Link failed: ${err.message}`);
      } else if (err instanceof Error) {
        toast.error(err.message);
      } else {
        toast.error("Link failed");
      }
    },
  });

  const sourceLabel = `#${source.number} — ${source.subject}`;

  return (
    <Dialog open={open} onOpenChange={(o) => !o && !linkMutation.isPending && onClose()}>
      <DialogContent className="flex max-h-[90vh] w-[calc(100vw-2rem)] max-w-3xl flex-col">
        <DialogHeader>
          <DialogTitle className="flex items-center gap-2">
            <FolderKanban className="h-4 w-4 text-primary" />
            Link to project
          </DialogTitle>
          <DialogDescription>
            Add this ticket to a project. It stays in its own queue; the
            project ticket lists it in its project panel.
          </DialogDescription>
        </DialogHeader>

        <div className="flex min-h-0 flex-1 flex-col space-y-4 overflow-y-auto">
          <div className="rounded-md border border-glass bg-glass px-3 py-2.5">
            <div className="text-[10px] uppercase tracking-wider text-muted-foreground/70 mb-1">
              Ticket
            </div>
            <div className="text-sm font-medium text-foreground/90 break-words">
              {sourceLabel}
            </div>
          </div>

          <div className="flex items-center justify-center">
            <ArrowRight className="h-4 w-4 text-muted-foreground/50" />
          </div>

          <div>
            <div className="text-[10px] uppercase tracking-wider text-muted-foreground/70 mb-1">
              Project ticket
            </div>
            <div className="relative">
              <Search className="absolute left-2.5 top-2.5 h-3.5 w-3.5 text-muted-foreground/60" />
              <Input
                autoFocus
                placeholder="Search open projects by ticket number or subject..."
                value={query}
                onChange={(e) => setQuery(e.target.value)}
                className="pl-8"
              />
            </div>
            <div className="mt-1 max-h-56 overflow-y-auto rounded-md border border-glass bg-glass">
              {searching && (
                <div className="px-3 py-2 text-xs text-muted-foreground/70">
                  Searching...
                </div>
              )}
              {!searching && searchError && (
                <div className="px-3 py-2 text-xs text-rose-300/90">
                  {searchError}
                </div>
              )}
              {!searching && !searchError && results.length === 0 && (
                <div className="px-3 py-2 text-xs text-muted-foreground/70">
                  No open project tickets found.
                </div>
              )}
              {!searching && !searchError && results.length > 0 && (
                <ul className="divide-y divide-glass">
                  {results.map((hit) => {
                    const isSelected = selected?.id === hit.id;
                    const requesterName = [hit.requesterFirstName, hit.requesterLastName]
                      .filter(Boolean)
                      .join(" ");
                    return (
                      <li key={hit.id}>
                        <button
                          type="button"
                          onClick={() => setSelected(hit)}
                          className={cn(
                            "w-full text-left px-3 py-2 hover:bg-glass-hover transition-colors",
                            isSelected && "bg-primary/10",
                          )}
                        >
                          <div className="flex items-center gap-2 text-sm">
                            <span
                              className="inline-flex h-1.5 w-1.5 rounded-full"
                              style={{ backgroundColor: hit.statusColor }}
                              aria-hidden
                            />
                            <span className="font-medium text-foreground/90">
                              #{hit.number}
                            </span>
                            <span className="truncate text-foreground/80">
                              {hit.subject}
                            </span>
                          </div>
                          <div className="ml-3.5 text-xs text-muted-foreground/70 truncate">
                            {hit.statusName}
                            {requesterName ? ` · ${requesterName}` : hit.requesterEmail ? ` · ${hit.requesterEmail}` : ""}
                            {hit.companyName ? ` · ${hit.companyName}` : ""}
                          </div>
                        </button>
                      </li>
                    );
                  })}
                </ul>
              )}
            </div>
          </div>
        </div>

        <DialogFooter>
          <Button
            variant="ghost"
            onClick={onClose}
            disabled={linkMutation.isPending}
          >
            Cancel
          </Button>
          <Button
            onClick={() => linkMutation.mutate()}
            disabled={!selected || linkMutation.isPending}
          >
            {linkMutation.isPending ? "Linking..." : "Link"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
