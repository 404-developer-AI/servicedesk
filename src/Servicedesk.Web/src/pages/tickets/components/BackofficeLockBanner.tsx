import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { useNavigate } from "@tanstack/react-router";
import { CalendarClock, CheckCircle2, ChevronDown, ClipboardList, Lock } from "lucide-react";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { formInputCn } from "@/components/forms/FormFieldInput";
import { BACKOFFICE_FORMS_KEY, backofficeFormsApi } from "@/lib/backoffice-forms-api";
import { errorMessage } from "@/lib/http";
import { cn } from "@/lib/utils";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { useAuth } from "@/auth/authStore";
import { FILL_FORM_EVENT, ticketFormsApi, type FillFormDetail, type TicketFormSummary } from "@/lib/forms-api";
import { finishOverlayTicket, useTicketOverlayStore } from "@/stores/useTicketOverlayStore";
import { usePlanBackofficeForm } from "@/components/ticket-overlay/usePlanBackofficeForm";

/// The soft-lock banner on a ticket in a Locked category (UI guidance only).
/// Status, queue, priority, owner and category are greyed until "Edit
/// anyway"; notes always work. Actions:
///   - Edit anyway — lifts the greying for this visit;
///   - Insert form — the same usable forms as `::`, opened through the page's
///     form host (`sd:fill-form`); when the ticket's open back-office request
///     has a plan form (back office only), "Plan" takes its place — in the
///     overlay and on the full page (forms stay reachable through `::`);
///   - in the ticket overlay (back office): Mark as handled — one note,
///     applied to the open back-office form(s) of this ticket;
///   - on the full page (back office): open the ticket in Back office forms.
export function BackofficeLockBanner({
  ticketId,
  ticketNumber,
  subject,
  categoryName,
  forms,
  embedded,
  onEditAnyway,
}: {
  ticketId: string;
  ticketNumber: number;
  subject: string;
  categoryName: string | null;
  forms: TicketFormSummary[];
  embedded: boolean;
  onEditAnyway: () => void;
}) {
  const { user } = useAuth();
  const isBackoffice = !!user?.backofficeFormsEnabled;
  const openOverlay = useTicketOverlayStore((s) => s.open);
  const navigate = useNavigate();
  const qc = useQueryClient();
  const openForms = forms.filter((f) => f.boStatus === "Open");
  const [handleOpen, setHandleOpen] = React.useState(false);
  const [formsMenuOpen, setFormsMenuOpen] = React.useState(false);
  const { planForm, plan } = usePlanBackofficeForm(ticketId, embedded);
  const showPlan = !!planForm;

  const usable = useQuery({
    queryKey: ["ticket", ticketId, "forms", "usable"],
    queryFn: () => ticketFormsApi.usable(ticketId),
    enabled: formsMenuOpen,
    staleTime: 30_000,
  });

  // Only forms offered through `::` — a header-button-only form keeps its own button.
  const pickerForms = usable.data?.filter((t) => t.inPicker !== false);

  const insertForm = (templateId: string) => {
    window.dispatchEvent(new CustomEvent<FillFormDetail>(FILL_FORM_EVENT, { detail: { ticketId, templateId } }));
  };

  return (
    <div className="sd-bo-lock-banner mb-3 flex shrink-0 flex-wrap items-center gap-x-3 gap-y-2 rounded-lg border border-glass border-l-4 border-l-muted-foreground/50 bg-muted/60 px-4 py-3">
      <Lock className="h-4 w-4 shrink-0 text-muted-foreground" />
      <div className="min-w-[16rem] flex-1">
        <p className="text-sm font-semibold text-foreground">Handled via Back office forms</p>
        <p className="mt-0.5 text-xs text-muted-foreground">
          {categoryName ? <>Category <span className="font-medium text-foreground">{categoryName}</span>. </> : null}
          {openForms.length > 0
            ? `${openForms.length === 1 ? "An open back-office form" : `${openForms.length} open back-office forms`} on this ticket. `
            : ""}
          Status, queue, priority, category and owner are locked. Adding a note is always possible.
        </p>
      </div>

      <div className="flex shrink-0 items-center gap-2">
        <Button variant="ghost" size="sm" onClick={onEditAnyway}>Edit anyway</Button>

        {showPlan && (
          <Button size="sm" onClick={plan}>
            <CalendarClock className="mr-1.5 h-4 w-4" /> Plan
          </Button>
        )}

        {!showPlan && <DropdownMenu open={formsMenuOpen} onOpenChange={setFormsMenuOpen}>
          <DropdownMenuTrigger asChild>
            <Button variant="outline" size="sm">
              <ClipboardList className="mr-1.5 h-4 w-4" /> Insert form
              <ChevronDown className="ml-1 h-3.5 w-3.5 opacity-60" />
            </Button>
          </DropdownMenuTrigger>
          <DropdownMenuContent align="end" className="w-72">
            <DropdownMenuLabel className="text-xs text-muted-foreground">Forms for this ticket</DropdownMenuLabel>
            {usable.isLoading && <div className="px-2 py-1.5 text-sm text-muted-foreground">Loading…</div>}
            {usable.isError && <div className="px-2 py-1.5 text-sm text-destructive">Could not load the forms.</div>}
            {pickerForms?.length === 0 && (
              <div className="px-2 py-1.5 text-sm text-muted-foreground">No forms are available in this ticket's queue.</div>
            )}
            {pickerForms?.map((t) => (
              <DropdownMenuItem key={t.id} onSelect={() => insertForm(t.id)}>
                <ClipboardList className="mr-2 h-4 w-4 shrink-0" />
                <span className="truncate">{t.name}</span>
              </DropdownMenuItem>
            ))}
          </DropdownMenuContent>
        </DropdownMenu>}

        {embedded && isBackoffice && openForms.length > 0 && (
          <Button variant="outline" size="sm" onClick={() => setHandleOpen(true)}
            className="border-primary/30 bg-primary/10 text-primary hover:bg-primary/15 hover:text-primary">
            <CheckCircle2 className="mr-1.5 h-4 w-4" /> Mark as handled
          </Button>
        )}

        {!embedded && isBackoffice && (
          <Button size="sm" variant={showPlan ? "outline" : "default"}
            onClick={() => {
              // Go to the list first: the overlay must never sit on top of the
              // same ticket's own page (two form hosts would both react).
              void navigate({ to: "/backoffice-forms" }).then(() => openOverlay({
                ticketId, ticketNumber, subject,
                source: { path: "/backoffice-forms", search: "", tab: null },
              }));
            }}>
            Open in Back office forms
          </Button>
        )}
      </div>

      {embedded && isBackoffice && (
        <HandleTicketFormsDialog
          open={handleOpen}
          onOpenChange={setHandleOpen}
          ticketId={ticketId}
          onDone={() => void qc.invalidateQueries({ queryKey: ["ticket", ticketId] })}
        />
      )}
    </div>
  );
}

/// "Mark as handled" from the lock banner: one note, applied to every open
/// back-office form of this ticket (usually just one). Each form goes through
/// the normal guarded handle call with its own version.
function HandleTicketFormsDialog({
  open,
  onOpenChange,
  ticketId,
  onDone,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  ticketId: string;
  onDone: () => void;
}) {
  const qc = useQueryClient();
  const [note, setNote] = React.useState("");
  React.useEffect(() => { if (open) setNote(""); }, [open]);

  const formsQ = useQuery({
    queryKey: [...BACKOFFICE_FORMS_KEY, "ticket", ticketId],
    queryFn: () => backofficeFormsApi.onTicket(ticketId),
    enabled: open,
  });
  const openItems = (formsQ.data ?? []).filter((f) => f.status === "Open");

  const handle = useMutation({
    mutationFn: async () => {
      for (const f of openItems) await backofficeFormsApi.handle(f.id, note.trim(), f.version);
    },
    onSuccess: () => {
      toast.success(openItems.length === 1 ? "Form marked as handled" : `${openItems.length} forms marked as handled`);
      onOpenChange(false);
      // Only shown in the overlay: the work is done, close the ticket.
      finishOverlayTicket(ticketId);
    },
    onError: (err) => toast.error(errorMessage(err, "The form could not be marked as handled.")),
    onSettled: () => {
      void qc.invalidateQueries({ queryKey: BACKOFFICE_FORMS_KEY });
      onDone();
    },
  });

  return (
    <Dialog open={open} onOpenChange={(o) => { if (!handle.isPending) onOpenChange(o); }}>
      <DialogContent className="sm:max-w-lg">
        <DialogHeader>
          <DialogTitle>Mark as handled</DialogTitle>
          <DialogDescription>
            {openItems.length > 1
              ? `The ${openItems.length} open back-office forms on this ticket are marked as handled with this note.`
              : openItems.length === 1
                ? `"${openItems[0].name}" on this ticket is marked as handled with this note.`
                : formsQ.isLoading ? "Loading…" : "This ticket has no open back-office form."}
          </DialogDescription>
        </DialogHeader>
        <textarea autoFocus rows={4} maxLength={2000} value={note} onChange={(e) => setNote(e.target.value)}
          placeholder="What was done — e.g. contract updated, licences activated"
          className={cn(formInputCn, "min-h-24 resize-y py-2")} />
        <DialogFooter>
          <Button variant="ghost" size="sm" onClick={() => onOpenChange(false)} disabled={handle.isPending}>Cancel</Button>
          <Button size="sm" onClick={() => handle.mutate()} disabled={handle.isPending || openItems.length === 0}>
            <CheckCircle2 className="mr-1.5 h-4 w-4" /> Mark as handled
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
