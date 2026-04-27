import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { AlertTriangle, ArrowRight, GitMerge, Search } from "lucide-react";
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
import { ApiError, contactApi, companyApi, ticketApi } from "@/lib/ticket-api";
import type { Ticket, TicketPickerItem } from "@/lib/ticket-api";
import { cn } from "@/lib/utils";

type Props = {
  open: boolean;
  source: Ticket;
  onClose: () => void;
  onMerged?: (targetId: string) => void;
};

/// v0.0.23: merges the current ticket into a chosen target. The picker excludes
/// the source itself and any ticket that is itself already merged. Cross-customer
/// or cross-company picks surface an explicit warning + acknowledge checkbox so
/// the agent must confirm before the request is sent. Merge is final — the
/// dialog warns about that and the action label says so.
export function MergeTicketDialog({
  open,
  source,
  onClose,
  onMerged,
}: Props) {
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const [query, setQuery] = React.useState("");
  const [selected, setSelected] = React.useState<TicketPickerItem | null>(null);
  const [results, setResults] = React.useState<TicketPickerItem[]>([]);
  const [searching, setSearching] = React.useState(false);
  const [searchError, setSearchError] = React.useState<string | null>(null);
  const [acknowledged, setAcknowledged] = React.useState(false);

  const { data: sourceContact } = useQuery({
    queryKey: ["contact", source.requesterContactId],
    queryFn: () => contactApi.get(source.requesterContactId),
    enabled: open,
    staleTime: 60_000,
  });

  const { data: sourceCompany } = useQuery({
    queryKey: ["company", source.companyId],
    queryFn: () => companyApi.get(source.companyId!),
    enabled: open && !!source.companyId,
    staleTime: 60_000,
  });

  const sourceCompanyName = sourceCompany?.company.name ?? null;
  const sourceRequesterEmail = sourceContact?.email ?? null;

  React.useEffect(() => {
    if (!open) {
      setQuery("");
      setSelected(null);
      setResults([]);
      setAcknowledged(false);
      setSearchError(null);
    }
  }, [open]);

  // Debounced search against /api/tickets/picker. We don't use react-query
  // here because the input is high-velocity and the picker is throwaway state
  // — useQuery would cache stale fragments and complicate the cancellation.
  React.useEffect(() => {
    if (!open) return;
    const handle = window.setTimeout(async () => {
      setSearching(true);
      setSearchError(null);
      try {
        const response = await ticketApi.picker(query.trim() || undefined, source.id, 20);
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

  const isCrossCustomer = selected
    ? selected.requesterContactId !== source.requesterContactId
        || (selected.companyId ?? null) !== (source.companyId ?? null)
    : false;

  const mergeMutation = useMutation({
    mutationFn: () =>
      ticketApi.merge(source.id, {
        targetTicketId: selected!.id,
        acknowledgedCrossCustomer: isCrossCustomer ? acknowledged : false,
      }),
    onSuccess: (response) => {
      queryClient.invalidateQueries({ queryKey: ["ticket", source.id] });
      queryClient.invalidateQueries({ queryKey: ["ticket", response.targetTicketId] });
      queryClient.invalidateQueries({ queryKey: ["tickets"] });
      toast.success(`Merged #${response.sourceNumber} into #${response.targetNumber}`);
      onMerged?.(response.targetTicketId);
      onClose();
      navigate({ to: "/tickets/$ticketId", params: { ticketId: response.targetTicketId } });
    },
    onError: (err) => {
      // The API maps validation failures to specific error codes; surface the
      // server's message verbatim so the agent sees actionable text rather
      // than a generic "merge failed".
      if (err instanceof ApiError) {
        toast.error(`Merge failed: ${err.message}`);
      } else if (err instanceof Error) {
        toast.error(err.message);
      } else {
        toast.error("Merge failed");
      }
    },
  });

  const sourceLabel = `#${source.number} — ${source.subject}`;

  return (
    <Dialog open={open} onOpenChange={(o) => !o && !mergeMutation.isPending && onClose()}>
      <DialogContent className="max-w-2xl">
        <DialogHeader>
          <DialogTitle className="flex items-center gap-2">
            <GitMerge className="h-4 w-4 text-primary" />
            Merge ticket
          </DialogTitle>
          <DialogDescription>
            Move all activity from this ticket into another. The current ticket
            will be closed with status <span className="font-medium">Merged</span>.
            This action is permanent.
          </DialogDescription>
        </DialogHeader>

        <div className="space-y-4">
          <div className="rounded-md border border-white/10 bg-white/[0.02] px-3 py-2.5">
            <div className="text-[10px] uppercase tracking-wider text-muted-foreground/70 mb-1">
              From
            </div>
            <div className="text-sm font-medium text-foreground/90 truncate">
              {sourceLabel}
            </div>
            <div className="text-xs text-muted-foreground/70 truncate">
              {sourceRequesterEmail ?? "No requester email"}
              {sourceCompanyName ? ` · ${sourceCompanyName}` : ""}
            </div>
          </div>

          <div className="flex items-center justify-center">
            <ArrowRight className="h-4 w-4 text-muted-foreground/50" />
          </div>

          <div>
            <div className="text-[10px] uppercase tracking-wider text-muted-foreground/70 mb-1">
              Into
            </div>
            <div className="relative">
              <Search className="absolute left-2.5 top-2.5 h-3.5 w-3.5 text-muted-foreground/60" />
              <Input
                autoFocus
                placeholder="Search by ticket number or subject..."
                value={query}
                onChange={(e) => setQuery(e.target.value)}
                className="pl-8"
              />
            </div>
            <div className="mt-2 max-h-56 overflow-y-auto rounded-md border border-white/10 bg-white/[0.02]">
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
                  No tickets found.
                </div>
              )}
              {!searching && !searchError && results.length > 0 && (
                <ul className="divide-y divide-white/5">
                  {results.map((hit) => {
                    const isSelected = selected?.id === hit.id;
                    const requesterName = [hit.requesterFirstName, hit.requesterLastName]
                      .filter(Boolean)
                      .join(" ");
                    return (
                      <li key={hit.id}>
                        <button
                          type="button"
                          onClick={() => {
                            setSelected(hit);
                            setAcknowledged(false);
                          }}
                          className={cn(
                            "w-full text-left px-3 py-2 hover:bg-white/[0.04] transition-colors",
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

          {selected && isCrossCustomer && (
            <div className="rounded-md border border-amber-400/30 bg-amber-400/5 px-3 py-2.5">
              <div className="flex items-start gap-2 text-sm text-amber-200/90">
                <AlertTriangle className="h-4 w-4 shrink-0 mt-0.5" />
                <div>
                  <div className="font-medium">
                    Different requester or company.
                  </div>
                  <div className="text-xs text-amber-200/70 mt-0.5">
                    These tickets belong to different customers. Merging will
                    expose the source ticket's history to the target's company.
                    Confirm only if this is intentional.
                  </div>
                </div>
              </div>
              <label className="mt-2 flex items-center gap-2 text-xs text-amber-200/90 cursor-pointer">
                <input
                  type="checkbox"
                  className="h-3.5 w-3.5 accent-amber-300"
                  checked={acknowledged}
                  onChange={(e) => setAcknowledged(e.target.checked)}
                />
                I confirm these tickets belong to different customers/companies.
              </label>
            </div>
          )}
        </div>

        <DialogFooter>
          <Button
            variant="ghost"
            onClick={onClose}
            disabled={mergeMutation.isPending}
          >
            Cancel
          </Button>
          <Button
            variant="destructive"
            onClick={() => mergeMutation.mutate()}
            disabled={
              !selected
                || mergeMutation.isPending
                || (isCrossCustomer && !acknowledged)
            }
          >
            {mergeMutation.isPending ? "Merging..." : "Merge — this is permanent"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
