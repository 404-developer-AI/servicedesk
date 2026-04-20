import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ArrowRight, Building2, UserRound } from "lucide-react";
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
import { ContactPicker } from "@/components/ContactPicker";
import { contactApi, companyApi, ticketApi } from "@/lib/ticket-api";
import { cn } from "@/lib/utils";

type Props = {
  open: boolean;
  ticketId: string;
  currentContactId: string;
  currentCompanyId: string | null;
  onClose: () => void;
  onSwitched?: () => void;
};

/// v0.0.12: switches a ticket's requester to a different contact. The ticket's
/// frozen company is re-resolved server-side using the same decision tree as
/// ticket intake — the agent only picks the new contact, the backend figures
/// out the company. Same-contact selects are blocked client-side so the no-op
/// never hits the wire.
export function SwitchRequesterDialog({
  open,
  ticketId,
  currentContactId,
  currentCompanyId,
  onClose,
  onSwitched,
}: Props) {
  const queryClient = useQueryClient();
  const [newContactId, setNewContactId] = React.useState<string | null>(null);

  React.useEffect(() => {
    if (!open) setNewContactId(null);
  }, [open]);

  const { data: currentContact } = useQuery({
    queryKey: ["contact", currentContactId],
    queryFn: () => contactApi.get(currentContactId),
    enabled: open,
    staleTime: 60_000,
  });

  const { data: currentCompany } = useQuery({
    queryKey: ["company", currentCompanyId],
    queryFn: () => companyApi.get(currentCompanyId!),
    enabled: open && !!currentCompanyId,
    staleTime: 60_000,
  });

  const { data: newContact } = useQuery({
    queryKey: ["contact", newContactId],
    queryFn: () => contactApi.get(newContactId!),
    enabled: open && !!newContactId,
    staleTime: 60_000,
  });

  const { data: newContactCompanies } = useQuery({
    queryKey: ["contact-companies", newContactId],
    queryFn: () => contactApi.listCompanies(newContactId!),
    enabled: open && !!newContactId,
    staleTime: 60_000,
  });

  const newPrimary = newContactCompanies?.find((l) => l.role === "primary") ?? null;
  const newSecondaryCount = newContactCompanies?.filter((l) => l.role === "secondary").length ?? 0;
  const hasOnlySuppliers =
    (newContactCompanies?.length ?? 0) > 0
    && (newContactCompanies?.every((l) => l.role === "supplier") ?? false);

  const predictedCompanyLabel: { text: string; tone: "primary" | "warn" | "muted" } = newContact
    ? newPrimary
      ? { text: newPrimary.companyName, tone: "primary" }
      : newSecondaryCount === 1
        ? { text: newContactCompanies![0].companyName, tone: "primary" }
        : newSecondaryCount > 1
          ? { text: "Multiple secondaries — agent will choose after switch", tone: "warn" }
          : hasOnlySuppliers
            ? { text: "Supplier-only — agent will choose after switch", tone: "warn" }
            : { text: "No company link", tone: "muted" }
    : { text: "No contact selected yet", tone: "muted" };

  const isSameContact = !!newContactId && newContactId === currentContactId;

  const switchMutation = useMutation({
    mutationFn: () => ticketApi.changeRequester(ticketId, newContactId!),
    onSuccess: (detail) => {
      queryClient.setQueryData(["ticket", ticketId], detail);
      queryClient.invalidateQueries({ queryKey: ["ticket", ticketId] });
      queryClient.invalidateQueries({ queryKey: ["tickets"] });
      queryClient.invalidateQueries({ queryKey: ["contact", currentContactId] });
      if (newContactId) {
        queryClient.invalidateQueries({ queryKey: ["contact", newContactId] });
      }
      toast.success("Requester switched");
      onSwitched?.();
      onClose();
    },
    onError: (e) => {
      toast.error(e instanceof Error ? e.message : "Switch failed");
    },
  });

  const currentName =
    currentContact
      ? [currentContact.firstName, currentContact.lastName].filter(Boolean).join(" ")
        || currentContact.email
      : "Current requester";

  return (
    <Dialog open={open} onOpenChange={(o) => !o && !switchMutation.isPending && onClose()}>
      <DialogContent className="max-w-lg">
        <DialogHeader>
          <DialogTitle className="flex items-center gap-2">
            <UserRound className="h-4 w-4 text-primary" />
            Switch requester
          </DialogTitle>
          <DialogDescription>
            Pick a different contact as the requester. The ticket's company
            follows automatically from the new contact's links.
          </DialogDescription>
        </DialogHeader>

        <div className="space-y-3">
          <div className="rounded-md border border-white/10 bg-white/[0.02] px-3 py-2.5">
            <div className="text-[10px] uppercase tracking-wider text-muted-foreground/70 mb-1">
              From
            </div>
            <div className="flex items-center gap-2 text-sm">
              <UserRound className="h-3.5 w-3.5 text-muted-foreground/60 shrink-0" />
              <span className="truncate font-medium text-foreground/90">{currentName}</span>
            </div>
            {currentContact?.email && currentContact.email !== currentName && (
              <div className="text-xs text-muted-foreground/70 ml-5 truncate">
                {currentContact.email}
              </div>
            )}
            <div className="flex items-center gap-2 text-xs text-muted-foreground/80 mt-1">
              <Building2 className="h-3 w-3 shrink-0" />
              <span className="truncate">
                {currentCompany?.company.name ?? "No company"}
              </span>
            </div>
          </div>

          <div className="flex items-center justify-center">
            <ArrowRight className="h-4 w-4 text-muted-foreground/50" />
          </div>

          <div>
            <div className="text-[10px] uppercase tracking-wider text-muted-foreground/70 mb-1">
              To
            </div>
            <ContactPicker
              value={newContactId}
              onChange={setNewContactId}
              placeholder="Search a contact..."
            />
            {newContact && (
              <div className="mt-2 flex items-center gap-2 text-xs text-muted-foreground/90">
                <Building2 className="h-3 w-3 shrink-0" />
                <span
                  className={cn(
                    "truncate",
                    predictedCompanyLabel.tone === "primary" && "text-foreground/90",
                    predictedCompanyLabel.tone === "warn" && "text-amber-300/90",
                    predictedCompanyLabel.tone === "muted" && "text-muted-foreground/70",
                  )}
                >
                  New company: {predictedCompanyLabel.text}
                </span>
              </div>
            )}
            {isSameContact && (
              <div className="mt-2 text-xs text-amber-300/90">
                This is already the current requester.
              </div>
            )}
          </div>
        </div>

        <DialogFooter>
          <Button
            variant="ghost"
            onClick={onClose}
            disabled={switchMutation.isPending}
          >
            Cancel
          </Button>
          <Button
            onClick={() => switchMutation.mutate()}
            disabled={
              !newContactId || isSameContact || switchMutation.isPending
            }
          >
            {switchMutation.isPending ? "Switching..." : "Switch requester"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
