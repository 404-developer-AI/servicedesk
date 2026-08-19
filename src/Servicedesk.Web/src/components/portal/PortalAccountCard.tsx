import * as React from "react";
import { useMutation } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { Send, UserRound } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { apiErrorMessage } from "@/lib/api";
import { portalAdminApi } from "@/lib/portal-api";
import type { Contact } from "@/lib/ticket-api";
import { useAuth } from "@/auth/authStore";
import { useServerTime, toServerLocal } from "@/hooks/useServerTime";
import {
  PortalAccountActions,
  PortalApproveDialog,
  PortalInviteDialog,
  PortalRejectDialog,
  PortalStatusChip,
  usePortalAccountForContact,
  usePortalAdminInvalidate,
} from "@/components/portal/PortalAccountDialogs";

/// "Portal account" block on the contact detail page: the linked account's
/// state + lifecycle actions, open invitations, or an invite button.
export function PortalAccountCard({ contact }: { contact: Contact }) {
  const { user } = useAuth();
  const isAdmin = user?.role === "Admin";
  const q = usePortalAccountForContact(contact.id);
  const invalidate = usePortalAdminInvalidate();
  const { time } = useServerTime();
  const fmt = (iso: string | null) => (!iso ? "—" : time ? toServerLocal(iso, time.offsetMinutes) : new Date(iso).toLocaleString());
  const [inviteOpen, setInviteOpen] = React.useState(false);
  const [approveOpen, setApproveOpen] = React.useState(false);
  const [rejectOpen, setRejectOpen] = React.useState(false);

  const resend = useMutation({
    mutationFn: (id: string) => portalAdminApi.resendInvitation(id),
    onSuccess: () => {
      toast.success("Invitation re-sent");
      invalidate();
    },
    onError: (err) => toast.error(apiErrorMessage(err) ?? "Could not resend"),
  });
  const revoke = useMutation({
    mutationFn: (id: string) => portalAdminApi.revokeInvitation(id),
    onSuccess: () => {
      toast.success("Invitation revoked");
      invalidate();
    },
    onError: (err) => toast.error(apiErrorMessage(err) ?? "Could not revoke"),
  });

  const account = q.data?.account ?? null;
  const invitations = (q.data?.invitations ?? []).filter((i) => !i.expired);

  return (
    <section className="glass-card p-5" data-testid="contact-portal-account">
      <div className="mb-3 flex items-center justify-between gap-2">
        <h2 className="flex items-center gap-2 text-sm font-semibold">
          <UserRound className="h-4 w-4 text-primary" /> Portal account
        </h2>
        {account ? <PortalStatusChip status={account.status} /> : null}
      </div>

      {q.isLoading ? (
        <Skeleton className="h-16 w-full" />
      ) : q.isError ? (
        <p className="text-xs text-muted-foreground">Portal status unavailable (is the portal enabled?).</p>
      ) : account ? (
        <div className="space-y-3 text-sm">
          <dl className="grid grid-cols-[110px_1fr] gap-y-1 text-xs">
            <dt className="text-muted-foreground">Sign-in email</dt>
            <dd className="truncate">{account.email}</dd>
            <dt className="text-muted-foreground">Portal role</dt>
            <dd>{contact.companyRole === "TicketManager" ? "Ticket manager — sees every company ticket" : "Member — sees own tickets"}</dd>
            <dt className="text-muted-foreground">Origin</dt>
            <dd>
              {account.origin === "Invitation" ? `Invited${account.invitedByEmail ? ` by ${account.invitedByEmail}` : ""}` : "Self-registered"}
              {account.approvalTicketId ? (
                <>
                  {" · "}
                  <Link to="/tickets/$ticketId" params={{ ticketId: account.approvalTicketId }} className="text-primary hover:underline">
                    ticket #{account.approvalTicketNumber}
                  </Link>
                </>
              ) : null}
            </dd>
            <dt className="text-muted-foreground">Two-factor</dt>
            <dd>{account.twoFactorEnrolled ? "Authenticator set up" : "Not set up yet (forced at first sign-in)"}</dd>
            <dt className="text-muted-foreground">Last sign-in</dt>
            <dd>{fmt(account.lastLoginUtc)}</dd>
          </dl>
          <PortalAccountActions account={account} isAdmin={isAdmin} compact onApprove={() => setApproveOpen(true)} onReject={() => setRejectOpen(true)} />
          <p className="text-[11px] text-muted-foreground">
            The portal role (Member / Ticket manager) is the contact&apos;s company role — change it in the contact details.
          </p>
        </div>
      ) : invitations.length > 0 ? (
        <div className="space-y-2 text-sm">
          {invitations.map((inv) => (
            <div key={inv.id} className="flex flex-wrap items-center gap-2 rounded-md border border-glass bg-glass px-3 py-2 text-xs">
              <Send className="h-3.5 w-3.5 text-muted-foreground" />
              <span>
                Invitation sent {fmt(inv.createdUtc)} · expires {fmt(inv.expiresUtc)}
                {inv.createdByEmail ? ` · by ${inv.createdByEmail}` : ""}
              </span>
              <div className="ml-auto flex gap-1.5">
                <Button size="sm" variant="outline" onClick={() => resend.mutate(inv.id)} disabled={resend.isPending}>
                  Resend
                </Button>
                <Button size="sm" variant="ghost" onClick={() => revoke.mutate(inv.id)} disabled={revoke.isPending}>
                  Revoke
                </Button>
              </div>
            </div>
          ))}
        </div>
      ) : (
        <div className="space-y-3">
          <p className="text-xs text-muted-foreground">
            This contact has no portal account. Invite them to follow and open tickets themselves — they set a password via the mail
            link and an authenticator app at their first sign-in.
          </p>
          <Button size="sm" className="gap-1.5" onClick={() => setInviteOpen(true)} disabled={!contact.email}>
            <Send className="h-3.5 w-3.5" /> Invite to the portal
          </Button>
        </div>
      )}

      <PortalInviteDialog
        open={inviteOpen}
        onClose={() => setInviteOpen(false)}
        contact={{
          id: contact.id,
          email: contact.email,
          name: `${contact.firstName} ${contact.lastName}`.trim(),
          primaryCompanyId: contact.primaryCompanyId,
          companyRole: contact.companyRole,
        }}
      />
      <PortalApproveDialog open={approveOpen} onClose={() => setApproveOpen(false)} account={account} suggestedCompany={null} />
      <PortalRejectDialog open={rejectOpen} onClose={() => setRejectOpen(false)} account={account} />
    </section>
  );
}
