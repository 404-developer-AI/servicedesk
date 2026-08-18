import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { ListChecks, Lock, Plus } from "lucide-react";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { cn } from "@/lib/utils";
import { useAuth } from "@/auth/authStore";
import { ticketChecklistApi, checklistErrorMessage, type TicketChecklist } from "@/lib/checklist-api";
import { ticketChecklistsKey } from "./useTicketChecklists";

/// "Add checklist" popover: lists the active templates whose queue scope
/// covers the ticket's current queue (server-filtered) and attaches one.
/// The trigger is supplied by the caller so the same menu backs the header
/// button (empty state) and the panel's "+" action.
export function AttachChecklistMenu({
  ticketId,
  attachedCount,
  maxPerTicket,
  onAttached,
  children,
  align = "end",
}: {
  ticketId: string;
  attachedCount: number;
  maxPerTicket: number;
  onAttached?: (checklist: TicketChecklist) => void;
  children: React.ReactNode;
  align?: "start" | "end" | "center";
}) {
  const [open, setOpen] = React.useState(false);
  const queryClient = useQueryClient();
  const { user } = useAuth();
  const isAdmin = user?.role === "Admin";

  const templatesQ = useQuery({
    queryKey: ["ticket", ticketId, "checklist-templates-available"],
    queryFn: () => ticketChecklistApi.availableTemplates(ticketId),
    enabled: open,
    staleTime: 30_000,
  });

  const attach = useMutation({
    mutationFn: (templateId: string) => ticketChecklistApi.attach(ticketId, templateId),
    onSuccess: (created) => {
      queryClient.invalidateQueries({ queryKey: ticketChecklistsKey(ticketId) });
      queryClient.invalidateQueries({ queryKey: ["ticket", ticketId] });
      toast.success(`Checklist “${created.name}” added`);
      setOpen(false);
      onAttached?.(created);
    },
    onError: (err) => toast.error(checklistErrorMessage(err, "Could not add the checklist")),
  });

  const atCap = attachedCount >= maxPerTicket;
  const templates = templatesQ.data?.items ?? [];

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>{children}</PopoverTrigger>
      <PopoverContent align={align} className="w-[340px] p-0 overflow-hidden">
        <div className="px-3 py-2.5 border-b border-glass flex items-center gap-2">
          <ListChecks className="h-4 w-4 text-violet-300/90" />
          <span className="text-sm font-medium">Add a checklist</span>
          <span className="ml-auto text-[11px] text-muted-foreground/70">
            {attachedCount}/{maxPerTicket}
          </span>
        </div>
        <div className="max-h-[360px] overflow-y-auto py-1">
          {templatesQ.isLoading && (
            <div className="px-3 py-3 text-xs text-muted-foreground">Loading templates…</div>
          )}
          {templatesQ.isError && (
            <div className="px-3 py-3 text-xs text-amber-300/80">Could not load the templates.</div>
          )}
          {!templatesQ.isLoading && !templatesQ.isError && templates.length === 0 && (
            <div className="px-3 py-4 text-xs text-muted-foreground/80 space-y-1">
              <p>No checklist templates are available for this ticket's queue.</p>
              {isAdmin ? (
                <p className="text-muted-foreground/60">
                  Create one under Settings → Tickets → Checklists and put this queue in its scope.
                </p>
              ) : (
                <p className="text-muted-foreground/60">Ask an admin to create one for this queue.</p>
              )}
            </div>
          )}
          {templates.map((t) => (
            <button
              key={t.id}
              type="button"
              disabled={attach.isPending || atCap}
              onClick={() => attach.mutate(t.id)}
              className={cn(
                "w-full text-left px-3 py-2 flex items-start gap-2.5 glass-hover transition-colors disabled:opacity-60",
              )}
              title={atCap ? `This ticket already has the maximum of ${maxPerTicket} checklists` : undefined}
            >
              <Plus className="h-4 w-4 mt-0.5 shrink-0 text-muted-foreground/70" />
              <span className="min-w-0 flex-1">
                <span className="block text-sm text-foreground truncate">{t.name}</span>
                {t.description && (
                  <span className="block text-xs text-muted-foreground/70 line-clamp-2">{t.description}</span>
                )}
                <span className="mt-0.5 flex items-center gap-2 text-[11px] text-muted-foreground/60">
                  <span>{t.itemCount} item{t.itemCount === 1 ? "" : "s"}</span>
                  {t.blockClose && (
                    <span className="inline-flex items-center gap-1 text-amber-300/80">
                      <Lock className="h-3 w-3" /> blocks closing
                    </span>
                  )}
                </span>
              </span>
            </button>
          ))}
        </div>
        {atCap && templates.length > 0 && (
          <div className="px-3 py-2 border-t border-glass text-[11px] text-amber-300/80">
            Maximum of {maxPerTicket} checklists per ticket reached.
          </div>
        )}
      </PopoverContent>
    </Popover>
  );
}
