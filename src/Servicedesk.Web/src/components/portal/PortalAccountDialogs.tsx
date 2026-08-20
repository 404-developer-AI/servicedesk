import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
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
import { CompanyPicker } from "@/components/CompanyPicker";
import { apiErrorMessage } from "@/lib/api";
import { portalAdminApi, type PortalAccount, type PortalAccountStatus, type PortalCompanyLinkInput } from "@/lib/portal-api";
import { contactApi } from "@/lib/ticket-api";
import { Eye, Plus, X } from "lucide-react";
import { cn } from "@/lib/utils";

// Shared agent-side building blocks for the customer portal: the approve
// dialog (used on the registration ticket card AND on Settings → Portal),
// the invite dialog (Settings → Portal + contact page) and the status chip.
// Every mutation invalidates the ["portal-admin"] query family so all
// surfaces refresh together.

export const PORTAL_ADMIN_QK = ["portal-admin"] as const;

export function usePortalAdminInvalidate() {
  const qc = useQueryClient();
  return React.useCallback(() => {
    qc.invalidateQueries({ queryKey: PORTAL_ADMIN_QK });
    qc.invalidateQueries({ queryKey: ["ticket"] });
    qc.invalidateQueries({ queryKey: ["contact"] });
  }, [qc]);
}

export const STATUS_LABEL: Record<PortalAccountStatus, string> = {
  PendingVerification: "Awaiting email confirmation",
  PendingApproval: "Awaiting approval",
  Active: "Active",
  Rejected: "Rejected",
  Deactivated: "Deactivated",
};

export function PortalStatusChip({ status, className }: { status: PortalAccountStatus; className?: string }) {
  const tone =
    status === "Active"
      ? "border-emerald-500/30 bg-emerald-500/[0.08] text-emerald-700 dark:text-emerald-300"
      : status === "PendingApproval"
        ? "border-amber-500/30 bg-amber-500/[0.10] text-amber-700 dark:text-amber-300"
        : status === "PendingVerification"
          ? "border-sky-500/30 bg-sky-500/[0.08] text-sky-700 dark:text-sky-300"
          : "border-glass bg-glass text-muted-foreground";
  return (
    <span className={cn("inline-flex items-center rounded-full border px-2 py-0.5 text-[11px] font-medium", tone, className)}>
      {STATUS_LABEL[status]}
    </span>
  );
}

type RoleOption = "Member" | "TicketManager";

/// One editable (company, portal role) row. `companyId` null = not chosen yet.
export type CompanyLinkRow = { key: string; companyId: string | null; role: RoleOption };

function newRow(companyId: string | null = null, role: RoleOption = "Member"): CompanyLinkRow {
  const key = typeof crypto !== "undefined" && "randomUUID" in crypto ? crypto.randomUUID() : `${Date.now()}-${Math.random()}`;
  return { key, companyId, role };
}

function rowsToLinks(rows: CompanyLinkRow[]): PortalCompanyLinkInput[] {
  const seen = new Set<string>();
  const out: PortalCompanyLinkInput[] = [];
  for (const r of rows) {
    if (!r.companyId || seen.has(r.companyId)) continue;
    seen.add(r.companyId);
    out.push({ companyId: r.companyId, role: r.role });
  }
  return out;
}

function RoleToggle({ value, onChange, disabled }: { value: RoleOption; onChange: (v: RoleOption) => void; disabled?: boolean }) {
  return (
    <div className="inline-flex shrink-0 rounded-md border border-glass bg-glass p-0.5" role="radiogroup" aria-label="Portal role">
      {(
        [
          { v: "Member", label: "Member", title: "Sees only the tickets they opened at this company" },
          { v: "TicketManager", label: "Ticket manager", title: "Sees every ticket of this company" },
        ] as const
      ).map((opt) => (
        <button
          key={opt.v}
          type="button"
          role="radio"
          aria-checked={value === opt.v}
          title={opt.title}
          disabled={disabled}
          onClick={() => onChange(opt.v)}
          className={cn(
            "rounded px-2 py-1 text-[11px] font-medium transition-colors",
            value === opt.v ? "bg-primary text-primary-foreground shadow-sm" : "text-muted-foreground hover:text-foreground",
          )}
        >
          {opt.label}
        </button>
      ))}
    </div>
  );
}

/// Multi-company editor used by the approve + invite dialogs: every row is
/// a company with its own portal role; the first row becomes the contact's
/// primary link (the rest secondary).
export function CompanyLinksEditor({
  rows,
  onChange,
  disabled,
  suggestion,
}: {
  rows: CompanyLinkRow[];
  onChange: (rows: CompanyLinkRow[]) => void;
  disabled?: boolean;
  suggestion?: { id: string; name: string } | null;
}) {
  const update = (key: string, patch: Partial<CompanyLinkRow>) => onChange(rows.map((r) => (r.key === key ? { ...r, ...patch } : r)));
  const remove = (key: string) => onChange(rows.filter((r) => r.key !== key));
  const suggestionUsed = suggestion ? rows.some((r) => r.companyId === suggestion.id) : true;
  return (
    <div className="space-y-2">
      {rows.map((r, idx) => (
        <div key={r.key} className="flex flex-wrap items-center gap-2 rounded-lg border border-glass bg-glass p-2">
          <span className="w-14 shrink-0 text-[10px] uppercase tracking-[0.12em] text-muted-foreground">{idx === 0 ? "Primary" : "Also"}</span>
          <div className="min-w-[200px] flex-1">
            <CompanyPicker value={r.companyId} onChange={(id) => update(r.key, { companyId: id })} placeholder="Select a company…" />
          </div>
          <RoleToggle value={r.role} onChange={(role) => update(r.key, { role })} disabled={disabled} />
          <button
            type="button"
            className="text-muted-foreground hover:text-foreground disabled:opacity-40"
            aria-label="Remove company"
            disabled={disabled || rows.length === 1}
            onClick={() => remove(r.key)}
          >
            <X className="h-4 w-4" />
          </button>
        </div>
      ))}
      <div className="flex flex-wrap items-center gap-3">
        <Button type="button" variant="ghost" size="sm" className="gap-1.5" disabled={disabled} onClick={() => onChange([...rows, newRow()])}>
          <Plus className="h-3.5 w-3.5" /> Add another company
        </Button>
        {suggestion && !suggestionUsed ? (
          <p className="text-[11px] text-muted-foreground">
            Suggested by email domain:{" "}
            <button
              type="button"
              className="font-medium text-primary hover:underline"
              onClick={() => {
                const empty = rows.find((x) => !x.companyId);
                if (empty) update(empty.key, { companyId: suggestion.id });
                else onChange([...rows, newRow(suggestion.id)]);
              }}
            >
              {suggestion.name}
            </button>
          </p>
        ) : null}
      </div>
      <p className="text-[11px] text-muted-foreground">
        Member = sees the tickets they opened at that company · Ticket manager = sees every ticket of that company. The customer
        switches company in the portal; tickets stay separated per company. Leave the company empty for own tickets only.
      </p>
    </div>
  );
}

// ---- approve ----------------------------------------------------------------

type ApproveProps = {
  open: boolean;
  onClose: () => void;
  account: PortalAccount | null;
  suggestedCompany: { id: string; name: string } | null;
  onDone?: (account: PortalAccount) => void;
};

export function PortalApproveDialog({ open, onClose, account, suggestedCompany, onDone }: ApproveProps) {
  const invalidate = usePortalAdminInvalidate();
  const [rows, setRows] = React.useState<CompanyLinkRow[]>([newRow()]);

  React.useEffect(() => {
    if (!open) return;
    const initial = account?.companyId ?? suggestedCompany?.id ?? null;
    setRows([newRow(initial, account?.companyRole === "TicketManager" ? "TicketManager" : "Member")]);
  }, [open, account, suggestedCompany]);

  const links = rowsToLinks(rows);
  const approve = useMutation({
    mutationFn: () => portalAdminApi.approve(account!.userId, links),
    onSuccess: (res) => {
      toast.success(`Portal account for ${account?.email} approved`);
      invalidate();
      onDone?.(res.account);
      onClose();
    },
    onError: (err) => toast.error(apiErrorMessage(err) ?? "Approval failed"),
  });

  return (
    <Dialog open={open} onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="sm:max-w-[600px]">
        <DialogHeader>
          <DialogTitle>Approve portal registration</DialogTitle>
          <DialogDescription>
            {account ? (
              <>
                <span className="font-medium text-foreground">{account.displayName || account.email}</span> ({account.email}) registered
                {account.emailVerifiedUtc ? " and confirmed their email address" : ""}. Choose the companies and the role per company; the
                contact is created or linked automatically.
              </>
            ) : null}
          </DialogDescription>
        </DialogHeader>
        <div className="space-y-1.5">
          <label className="text-xs uppercase tracking-[0.14em] text-muted-foreground">Companies & portal roles</label>
          <CompanyLinksEditor rows={rows} onChange={setRows} suggestion={suggestedCompany} disabled={approve.isPending} />
          {!suggestedCompany ? <p className="text-[11px] text-muted-foreground">Unknown email domain — pick the company manually.</p> : null}
        </div>
        <DialogFooter>
          <Button variant="ghost" onClick={onClose} disabled={approve.isPending}>
            Cancel
          </Button>
          <Button onClick={() => approve.mutate()} disabled={!account || approve.isPending}>
            {approve.isPending ? "Approving…" : links.length === 0 ? "Approve without company" : "Approve and activate"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

// ---- reject -----------------------------------------------------------------

export function PortalRejectDialog({
  open,
  onClose,
  account,
  onDone,
}: {
  open: boolean;
  onClose: () => void;
  account: PortalAccount | null;
  onDone?: () => void;
}) {
  const invalidate = usePortalAdminInvalidate();
  const [reason, setReason] = React.useState("");
  React.useEffect(() => {
    if (open) setReason("");
  }, [open]);
  const reject = useMutation({
    mutationFn: () => portalAdminApi.reject(account!.userId, reason.trim()),
    onSuccess: () => {
      toast.success("Registration rejected");
      invalidate();
      onDone?.();
      onClose();
    },
    onError: (err) => toast.error(apiErrorMessage(err) ?? "Rejection failed"),
  });
  return (
    <Dialog open={open} onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="sm:max-w-[460px]">
        <DialogHeader>
          <DialogTitle>Reject registration</DialogTitle>
          <DialogDescription>
            {account?.email} will not be able to sign in. The reason is kept internally (not mailed to the registrant).
          </DialogDescription>
        </DialogHeader>
        <textarea
          value={reason}
          onChange={(e) => setReason(e.target.value)}
          rows={3}
          maxLength={1000}
          placeholder="Reason (optional, internal)"
          className="w-full rounded-md border border-glass bg-glass px-3 py-2 text-sm outline-none focus-visible:ring-2 focus-visible:ring-ring"
        />
        <DialogFooter>
          <Button variant="ghost" onClick={onClose} disabled={reject.isPending}>
            Cancel
          </Button>
          <Button variant="destructive" onClick={() => reject.mutate()} disabled={!account || reject.isPending}>
            {reject.isPending ? "Rejecting…" : "Reject"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

// ---- invite -----------------------------------------------------------------

type InviteProps = {
  open: boolean;
  onClose: () => void;
  /// Pre-bound contact (contact page). The email comes from the contact then
  /// and the rows are prefilled from the contact's existing company links.
  contact?: { id: string; email: string; name: string } | null;
};

export function PortalInviteDialog({ open, onClose, contact }: InviteProps) {
  const invalidate = usePortalAdminInvalidate();
  const [email, setEmail] = React.useState("");
  const [name, setName] = React.useState("");
  const [rows, setRows] = React.useState<CompanyLinkRow[]>([newRow()]);
  const contactLinks = useQuery({
    queryKey: ["contact-companies", contact?.id],
    queryFn: () => contactApi.listCompanies(contact!.id),
    enabled: open && !!contact,
  });

  React.useEffect(() => {
    if (!open) return;
    setEmail(contact?.email ?? "");
    setName(contact?.name ?? "");
    setRows([newRow()]);
  }, [open, contact]);
  React.useEffect(() => {
    if (!open || !contact || !contactLinks.data) return;
    const linked = contactLinks.data
      .filter((c) => c.role === "primary" || c.role === "secondary")
      .sort((a, b) => (a.role === "primary" ? -1 : b.role === "primary" ? 1 : 0))
      .map((c) => newRow(c.companyId, c.portalRole === "TicketManager" ? "TicketManager" : "Member"));
    setRows(linked.length > 0 ? linked : [newRow()]);
  }, [open, contact, contactLinks.data]);

  const links = rowsToLinks(rows);
  const invite = useMutation({
    mutationFn: () =>
      portalAdminApi.invite({
        email: contact ? undefined : email.trim(),
        displayName: name.trim(),
        contactId: contact?.id ?? null,
        companies: links,
      }),
    onSuccess: () => {
      toast.success(`Invitation sent to ${contact?.email ?? email.trim()}`);
      invalidate();
      onClose();
    },
    onError: (err) => toast.error(apiErrorMessage(err) ?? "Invitation failed"),
  });

  const emailOk = contact ? true : /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email.trim());

  return (
    <Dialog open={open} onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="sm:max-w-[600px]">
        <DialogHeader>
          <DialogTitle>Invite to the customer portal</DialogTitle>
          <DialogDescription>
            The customer receives a mail with an activation link, sets a password and then sets up two-factor authentication at the
            first sign-in. Invited accounts skip the approval step.
          </DialogDescription>
        </DialogHeader>
        <div className="space-y-4">
          <div className="grid gap-3 sm:grid-cols-2">
            <div className="space-y-1.5">
              <label className="text-xs uppercase tracking-[0.14em] text-muted-foreground">Email</label>
              <Input type="email" value={email} onChange={(e) => setEmail(e.target.value)} readOnly={!!contact} className={contact ? "bg-glass" : ""} placeholder="customer@company.com" />
            </div>
            <div className="space-y-1.5">
              <label className="text-xs uppercase tracking-[0.14em] text-muted-foreground">Name</label>
              <Input value={name} onChange={(e) => setName(e.target.value)} placeholder="Shown in the mail" />
            </div>
          </div>
          <div className="space-y-1.5">
            <label className="text-xs uppercase tracking-[0.14em] text-muted-foreground">Companies & portal roles</label>
            <CompanyLinksEditor rows={rows} onChange={setRows} disabled={invite.isPending} />
          </div>
        </div>
        <DialogFooter>
          <Button variant="ghost" onClick={onClose} disabled={invite.isPending}>
            Cancel
          </Button>
          <Button onClick={() => invite.mutate()} disabled={!emailOk || invite.isPending}>
            {invite.isPending ? "Sending…" : "Send invitation"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

// ---- account actions -----------------------------------------------------------

/// Row of lifecycle actions for one account, rendered on the contact page
/// card and in the Settings → Portal accounts table.
export function PortalAccountActions({
  account,
  isAdmin,
  compact,
  onApprove,
  onReject,
}: {
  account: PortalAccount;
  isAdmin: boolean;
  compact?: boolean;
  onApprove?: () => void;
  onReject?: () => void;
}) {
  const invalidate = usePortalAdminInvalidate();
  const run = useMutation({
    mutationFn: async (action: "deactivate" | "reactivate" | "reset-totp" | "revoke-sessions" | "resend-verification" | "delete") => {
      switch (action) {
        case "deactivate":
          return portalAdminApi.deactivate(account.userId);
        case "reactivate":
          return portalAdminApi.reactivate(account.userId);
        case "reset-totp":
          return portalAdminApi.resetTotp(account.userId);
        case "revoke-sessions":
          return portalAdminApi.revokeSessions(account.userId);
        case "resend-verification":
          return portalAdminApi.resendVerification(account.userId);
        case "delete":
          return portalAdminApi.remove(account.userId);
      }
    },
    onSuccess: (_r, action) => {
      const msg: Record<string, string> = {
        deactivate: "Account deactivated — sessions signed out",
        reactivate: "Account reactivated",
        "reset-totp": "Authenticator reset — the customer sets it up again at the next sign-in",
        "revoke-sessions": "All sessions signed out",
        "resend-verification": "Verification mail sent",
        delete: "Account deleted",
      };
      toast.success(msg[action] ?? "Done");
      invalidate();
    },
    onError: (err) => toast.error(apiErrorMessage(err) ?? "Action failed"),
  });
  const size = compact ? "sm" : "sm";
  const confirmThen = (q: string, action: Parameters<typeof run.mutate>[0]) => {
    if (window.confirm(q)) run.mutate(action);
  };
  // v0.1.1 — shadow login: mints a read-only portal session for this
  // customer and opens /portal in a new tab. Admin-only; the tab's banner
  // has the Exit that ends the session.
  const view = useMutation({
    mutationFn: () => portalAdminApi.impersonate(account.userId),
    onSuccess: () => {
      window.open("/portal", "_blank");
      toast.success("Read-only portal view opened — use Exit view in the portal tab to end it.");
    },
    onError: (err) => toast.error(apiErrorMessage(err) ?? "Could not open the portal view"),
  });
  return (
    <div className="flex flex-wrap items-center gap-1.5">
      {account.status === "PendingApproval" && onApprove ? (
        <Button size={size} onClick={onApprove} disabled={run.isPending}>
          Approve
        </Button>
      ) : null}
      {(account.status === "PendingApproval" || account.status === "PendingVerification") && onReject ? (
        <Button size={size} variant="outline" onClick={onReject} disabled={run.isPending}>
          Reject
        </Button>
      ) : null}
      {account.status === "PendingVerification" ? (
        <Button size={size} variant="outline" onClick={() => run.mutate("resend-verification")} disabled={run.isPending}>
          Resend verification
        </Button>
      ) : null}
      {account.status === "Active" ? (
        <>
          {isAdmin ? (
            <Button size={size} variant="outline" className="gap-1.5" onClick={() => view.mutate()} disabled={view.isPending}>
              <Eye className="h-3.5 w-3.5" /> View portal
            </Button>
          ) : null}
          <Button size={size} variant="outline" onClick={() => confirmThen("Deactivate this portal account? All sessions are signed out.", "deactivate")} disabled={run.isPending}>
            Deactivate
          </Button>
          {account.twoFactorEnrolled ? (
            <Button size={size} variant="outline" onClick={() => confirmThen("Reset the authenticator? The customer must set it up again at the next sign-in; all sessions are signed out.", "reset-totp")} disabled={run.isPending}>
              Reset 2FA
            </Button>
          ) : null}
          <Button size={size} variant="ghost" onClick={() => run.mutate("revoke-sessions")} disabled={run.isPending}>
            Sign out everywhere
          </Button>
        </>
      ) : null}
      {account.status === "Deactivated" ? (
        <Button size={size} variant="outline" onClick={() => run.mutate("reactivate")} disabled={run.isPending}>
          Reactivate
        </Button>
      ) : null}
      {isAdmin ? (
        <Button
          size={size}
          variant="ghost"
          className="text-destructive hover:text-destructive"
          onClick={() => confirmThen(`Delete the portal account of ${account.email}? This cannot be undone (the contact and tickets stay).`, "delete")}
          disabled={run.isPending}
        >
          Delete
        </Button>
      ) : null}
    </div>
  );
}

/// Resolves the live account row for a contact (card on the contact page).
export function usePortalAccountForContact(contactId: string | undefined) {
  return useQuery({
    queryKey: [...PORTAL_ADMIN_QK, "by-contact", contactId],
    queryFn: () => portalAdminApi.byContact(contactId!),
    enabled: !!contactId,
  });
}
