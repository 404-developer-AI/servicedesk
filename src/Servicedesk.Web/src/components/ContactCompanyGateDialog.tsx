import * as React from "react";
import { Building2, User } from "lucide-react";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import { CompanyPicker } from "@/components/CompanyPicker";
import { cn } from "@/lib/utils";
import type {
  ContactCompanyRole,
  StatusGateContactCompanyLink,
} from "@/lib/ticket-api";

type Props = {
  /// Null hides the dialog. The parent passes the next gate in the
  /// sequence (or null when done) so a single component instance can
  /// walk through multiple matching gates without remount churn.
  gate: StatusGateContactCompanyLink | null;
  /// Fires when the agent picks a company + role and clicks the bottom
  /// confirm button. The parent posts both to PATCH inside the
  /// per-trigger confirmation array; the server creates the link before
  /// the status flip.
  onConfirm: (payload: { companyId: string; role: ContactCompanyRole }) => void;
  /// Fires when the agent clicks Cancel, the overlay, or Esc. The
  /// parent rolls back the dropdown state and surfaces a "status not
  /// changed" toast.
  onCancel: () => void;
};

const ROLE_OPTIONS: { value: ContactCompanyRole; label: string; description: string }[] = [
  {
    value: "primary",
    label: "Primary",
    description: "The contact's main company. Demotes any existing primary link.",
  },
  {
    value: "secondary",
    label: "Secondary",
    description: "Additional company alongside the primary link.",
  },
  {
    value: "supplier",
    label: "Supplier",
    description: "Supplier or vendor relationship.",
  },
];

/// v0.0.42 — hard-block dialog for status changes that require the
/// ticket's requester to be linked to a company first. Renders a
/// glass-card with title + optional message + the requester's display
/// name + an inline company picker + a role selector + bottom
/// confirm/cancel buttons. Confirm is disabled until both a company
/// and a role are picked. The dialog is controlled by `gate` so the
/// parent can swap in the next gate without an unmount round-trip.
export function ContactCompanyGateDialog({ gate, onConfirm, onCancel }: Props) {
  const [companyId, setCompanyId] = React.useState<string | null>(null);
  const [role, setRole] = React.useState<ContactCompanyRole>("primary");

  // Reset the picker each time a new gate slot is opened so an earlier
  // gate's selection does not pre-fill the next one.
  React.useEffect(() => {
    if (gate) {
      setCompanyId(null);
      setRole("primary");
    }
  }, [gate?.triggerId]);

  if (!gate) return null;

  const canConfirm = !!companyId;

  function handleConfirm() {
    if (!companyId) return;
    onConfirm({ companyId, role });
  }

  return (
    <Dialog
      open={!!gate}
      onOpenChange={(o) => {
        if (!o) onCancel();
      }}
    >
      <DialogContent className="sm:max-w-lg border border-glass bg-popover/95 backdrop-blur-xl">
        <DialogHeader>
          <DialogTitle>{gate.title}</DialogTitle>
          {gate.message ? (
            <DialogDescription className="whitespace-pre-wrap text-sm text-muted-foreground">
              {gate.message}
            </DialogDescription>
          ) : null}
        </DialogHeader>

        <div className="space-y-3.5">
          <div className="flex items-center gap-2 rounded-md border border-glass-strong bg-glass px-3 py-2 text-xs text-muted-foreground">
            <User className="h-3.5 w-3.5 shrink-0 opacity-60" />
            <span className="truncate">
              Requester: <span className="text-foreground/90">{gate.requesterDisplayName}</span>
            </span>
          </div>

          <div className="space-y-1.5">
            <label className="text-xs font-medium text-muted-foreground">
              Company<span className="ml-1 text-rose-400">*</span>
            </label>
            <CompanyPicker
              value={companyId}
              onChange={setCompanyId}
              placeholder="Pick a company to link…"
            />
          </div>

          <div className="space-y-1.5">
            <label className="text-xs font-medium text-muted-foreground">
              Role<span className="ml-1 text-rose-400">*</span>
            </label>
            <div className="grid grid-cols-1 gap-1.5 sm:grid-cols-3">
              {ROLE_OPTIONS.map((opt) => (
                <button
                  key={opt.value}
                  type="button"
                  onClick={() => setRole(opt.value)}
                  title={opt.description}
                  className={cn(
                    "rounded-md border px-3 py-2 text-left text-sm transition-colors",
                    role === opt.value
                      ? "border-violet-400/60 bg-violet-500/15 text-violet-100 shadow-[0_0_12px_rgba(124,58,237,0.25)]"
                      : "border-glass bg-glass text-foreground/80 hover:border-violet-400/30 hover:bg-violet-500/10",
                  )}
                >
                  <div className="flex items-center gap-1.5 font-medium">
                    <Building2 className="h-3.5 w-3.5 opacity-70" />
                    {opt.label}
                  </div>
                  <p className="mt-0.5 text-[11px] text-muted-foreground/80 leading-tight">
                    {opt.description}
                  </p>
                </button>
              ))}
            </div>
          </div>
        </div>

        <DialogFooter className="gap-2 sm:gap-2">
          <Button variant="ghost" onClick={onCancel}>
            {gate.cancelLabel}
          </Button>
          <Button
            onClick={handleConfirm}
            disabled={!canConfirm}
            className="bg-gradient-to-r from-violet-600 to-indigo-600 hover:from-violet-500 hover:to-indigo-500 text-white shadow-[0_0_20px_rgba(124,58,237,0.3)]"
          >
            {gate.confirmLabel}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
