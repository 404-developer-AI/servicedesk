import * as React from "react";
import * as DialogPrimitive from "@radix-ui/react-dialog";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { CalendarClock, CheckCircle2, ClipboardList, ExternalLink, Eye, Minus, MoreHorizontal, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { useAuth } from "@/auth/authStore";
import { TicketDetailPage } from "@/pages/tickets/TicketDetailPage";
import { BackofficeFormDialog } from "@/pages/backoffice-forms/BackofficeFormDialog";
import { BackofficeStatusChip } from "@/pages/backoffice-forms/BackofficeStatusChip";
import { presenceName } from "@/pages/backoffice-forms/backofficeLabels";
import {
  backofficeFormKeys,
  backofficeFormsApi,
  type BackofficeFormItem,
} from "@/lib/backoffice-forms-api";
import { finishOverlayTicket, useTicketOverlayStore } from "@/stores/useTicketOverlayStore";
import { usePlanBackofficeForm } from "./usePlanBackofficeForm";

/// v0.1.61 — the ticket overlay, mounted once in the shell. Shows the full
/// ticket (same page component, embedded) under one fixed back-office bar:
/// identity on the left, actions in "More" (Plan and Mark as handled live in
/// the lock banner; More keeps them reachable), minimise + close as icons at
/// the far right.
/// Esc and a click outside minimise (never discard). Opening the ticket here
/// is what marks the form "In progress" for other back-office users (live
/// presence through the ticket page's own viewing hook).
export function TicketOverlayHost() {
  const current = useTicketOverlayStore((s) => s.current);
  const minimize = useTicketOverlayStore((s) => s.minimize);
  const close = useTicketOverlayStore((s) => s.close);
  const open = current !== null;

  return (
    <DialogPrimitive.Root open={open} onOpenChange={(o) => { if (!o) minimize(); }}>
      <DialogPrimitive.Portal>
        <DialogPrimitive.Overlay
          className="sd-ticket-overlay-scrim fixed inset-0 z-50 bg-black/40 backdrop-blur-[2px] data-[state=open]:animate-in data-[state=closed]:animate-out data-[state=closed]:fade-out-0 data-[state=open]:fade-in-0" />
        <DialogPrimitive.Content
          aria-describedby={undefined}
          onInteractOutside={(e) => {
            // The minimised-forms tray sits above the overlay: using it must
            // not minimise the ticket underneath.
            const target = e.target as HTMLElement | null;
            if (target?.closest?.(".sd-form-tray")) e.preventDefault();
          }}
          onEscapeKeyDown={(e) => {
            // Esc inside a nested dialog/popover closes that first (Radix
            // handles it); a plain Esc here minimises.
            e.preventDefault();
            minimize();
          }}
          className="sd-ticket-overlay glass-card fixed inset-x-[3vw] bottom-[2.5vh] top-[2.5vh] z-50 flex flex-col overflow-hidden rounded-xl border border-glass-strong bg-background shadow-2xl outline-hidden data-[state=open]:animate-in data-[state=closed]:animate-out data-[state=closed]:fade-out-0 data-[state=open]:fade-in-0 data-[state=closed]:zoom-out-[0.98] data-[state=open]:zoom-in-[0.98]">
          {current && (
            <>
              <DialogPrimitive.Title className="sr-only">
                {current.ticketNumber ? `Ticket #${current.ticketNumber}` : "Ticket"}
              </DialogPrimitive.Title>
              <OverlayBar ticketId={current.ticketId} onMinimize={minimize} onClose={close} />
              <div className="min-h-0 flex-1 overflow-hidden px-5">
                <TicketDetailPage key={current.ticketId} ticketId={current.ticketId} embedded />
              </div>
            </>
          )}
        </DialogPrimitive.Content>
      </DialogPrimitive.Portal>
    </DialogPrimitive.Root>
  );
}

function OverlayBar({ ticketId, onMinimize, onClose }: { ticketId: string; onMinimize: () => void; onClose: () => void }) {
  const { user } = useAuth();
  const isBackoffice = !!user?.backofficeFormsEnabled;
  const navigate = useNavigate();
  const qc = useQueryClient();
  const [dialogFormId, setDialogFormId] = React.useState<string | null>(null);
  const [handleMode, setHandleMode] = React.useState(false);
  const { planForm, plan } = usePlanBackofficeForm(ticketId);

  const formsQ = useQuery({
    queryKey: backofficeFormKeys.onTicket(ticketId),
    queryFn: () => backofficeFormsApi.onTicket(ticketId),
    enabled: isBackoffice,
    staleTime: 10_000,
  });
  const forms: BackofficeFormItem[] = formsQ.data ?? [];
  const ticket = qc.getQueryData<{ ticket?: { number?: number; subject?: string } }>(["ticket", ticketId])?.ticket;
  const first = forms[0] ?? null;

  const openFullPage = () => {
    onClose();
    void navigate({ to: "/tickets/$ticketId", params: { ticketId } });
  };

  return (
    <div className="sd-ticket-overlay-bar flex shrink-0 items-center gap-3 border-b border-glass px-5 py-3">
      <span className="flex h-9 w-9 shrink-0 items-center justify-center rounded-lg border border-primary/25 bg-primary/10 text-primary">
        <ClipboardList className="h-4 w-4" />
      </span>
      <div className="min-w-0 flex-1">
        {isBackoffice && forms.length > 0 ? (
          <>
            <div className="flex min-w-0 items-center gap-2">
              <span className="truncate text-sm font-semibold text-foreground">{first!.name}</span>
              <BackofficeStatusChip item={{ status: first!.status, handledByName: first!.handledByName }} />
              {forms.length > 1 && (
                <span className="shrink-0 text-xs text-muted-foreground">+{forms.length - 1} more open</span>
              )}
            </div>
            <div className="truncate text-xs text-muted-foreground">
              <span className="tabular-nums">#{first!.ticketNumber}</span> · {first!.ticketSubject}
              {first!.companyName ? ` · ${first!.companyName}` : ""}
              {first!.submittedByName ? ` · submitted by ${presenceName(first!.submittedByName)}` : ""}
            </div>
          </>
        ) : (
          <>
            <div className="truncate text-sm font-semibold text-foreground">
              {ticket?.number ? <span className="tabular-nums">#{ticket.number} </span> : null}
              {ticket?.subject ?? "Ticket"}
            </div>
            <div className="text-xs text-muted-foreground">
              {isBackoffice && !formsQ.isLoading ? "No open back-office form on this ticket." : "Opened from the list."}
            </div>
          </>
        )}
      </div>

      <DropdownMenu>
        <DropdownMenuTrigger asChild>
          <Button variant="ghost" size="sm" aria-label="More actions">
            <MoreHorizontal className="h-4 w-4" />
          </Button>
        </DropdownMenuTrigger>
        <DropdownMenuContent align="end" className="w-60">
          {planForm && (
            <DropdownMenuItem onSelect={plan}>
              <CalendarClock className="mr-2 h-4 w-4" /> Plan
            </DropdownMenuItem>
          )}
          {isBackoffice && forms.length > 0 && (
            <>
              <DropdownMenuLabel className="text-xs text-muted-foreground">Back-office forms</DropdownMenuLabel>
              {forms.map((f) => (
                <DropdownMenuItem key={f.id} onSelect={() => { setHandleMode(false); setDialogFormId(f.id); }}>
                  <Eye className="mr-2 h-4 w-4" />
                  <span className="truncate">{f.name}</span>
                </DropdownMenuItem>
              ))}
              {/* Mark as handled lives in the lock banner on a locked ticket;
                  here it stays reachable for any ticket. */}
              {forms.map((f) => (
                <DropdownMenuItem key={`h-${f.id}`} onSelect={() => { setHandleMode(true); setDialogFormId(f.id); }}>
                  <CheckCircle2 className="mr-2 h-4 w-4" />
                  <span className="truncate">Mark “{f.name}” as handled</span>
                </DropdownMenuItem>
              ))}
              <DropdownMenuSeparator />
            </>
          )}
          <DropdownMenuItem onSelect={openFullPage}>
            <ExternalLink className="mr-2 h-4 w-4" /> Open full ticket page
          </DropdownMenuItem>
        </DropdownMenuContent>
      </DropdownMenu>

      <div className="ml-1 flex items-center gap-0.5 border-l border-glass pl-2">
        <Button variant="ghost" size="sm" onClick={onMinimize} aria-label="Minimise" title="Minimise (Esc)">
          <Minus className="h-4 w-4" />
        </Button>
        <Button variant="ghost" size="sm" onClick={onClose} aria-label="Close" title="Close">
          <X className="h-4 w-4" />
        </Button>
      </div>

      <BackofficeFormDialog
        formId={dialogFormId}
        startInHandle={handleMode}
        hideOpenTicket
        onHandled={() => finishOverlayTicket(ticketId)}
        onOpenChange={(o) => { if (!o) { setDialogFormId(null); setHandleMode(false); } }}
      />
    </div>
  );
}
