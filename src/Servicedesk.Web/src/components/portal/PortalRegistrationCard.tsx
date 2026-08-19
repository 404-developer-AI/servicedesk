import * as React from "react";
import { useQuery } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { UserRound } from "lucide-react";
import { Button } from "@/components/ui/button";
import { portalAdminApi } from "@/lib/portal-api";
import {
  PORTAL_ADMIN_QK,
  PortalApproveDialog,
  PortalRejectDialog,
  PortalStatusChip,
} from "@/components/portal/PortalAccountDialogs";

/// Shown on the system ticket that a portal registration created: who
/// registered, the suggested company, approve / reject. Only rendered when
/// the ticket detail payload carries `portalRegistration` (one indexed
/// lookup server-side), so every other ticket pays nothing.
export function PortalRegistrationCard({ ticketId }: { ticketId: string }) {
  const q = useQuery({
    queryKey: [...PORTAL_ADMIN_QK, "by-ticket", ticketId],
    queryFn: () => portalAdminApi.byTicket(ticketId),
  });
  const [approveOpen, setApproveOpen] = React.useState(false);
  const [rejectOpen, setRejectOpen] = React.useState(false);
  const account = q.data?.account ?? null;
  if (!account) return null;
  const pending = account.status === "PendingApproval";

  return (
    <div className="shrink-0 pb-3" data-testid="portal-registration-card">
      <div className="glass-card flex flex-wrap items-center gap-3 border-amber-500/30 px-4 py-3">
        <span className="inline-flex h-8 w-8 items-center justify-center rounded-full bg-amber-500/15 text-amber-600 dark:text-amber-300">
          <UserRound className="h-4 w-4" />
        </span>
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-2 text-sm">
            <span className="font-medium">Portal registration</span>
            <PortalStatusChip status={account.status} />
          </div>
          <p className="mt-0.5 text-xs text-muted-foreground">
            {account.displayName || account.email} ({account.email})
            {q.data?.suggestedCompany ? ` · suggested company: ${q.data.suggestedCompany.name}` : " · unknown email domain"}
            {account.contactId ? (
              <>
                {" · "}
                <Link to="/contacts/$contactId" params={{ contactId: account.contactId }} className="text-primary hover:underline">
                  contact
                </Link>
              </>
            ) : null}
            {account.status === "Active" && account.approvedByEmail ? ` · approved by ${account.approvedByEmail}` : null}
            {account.status === "Rejected" && account.rejectionReason ? ` · reason: ${account.rejectionReason}` : null}
          </p>
        </div>
        {pending ? (
          <div className="flex items-center gap-1.5">
            <Button size="sm" variant="outline" onClick={() => setRejectOpen(true)}>
              Reject
            </Button>
            <Button size="sm" onClick={() => setApproveOpen(true)}>
              Approve…
            </Button>
          </div>
        ) : null}
      </div>
      <PortalApproveDialog
        open={approveOpen}
        onClose={() => setApproveOpen(false)}
        account={account}
        suggestedCompany={q.data?.suggestedCompany ?? null}
      />
      <PortalRejectDialog open={rejectOpen} onClose={() => setRejectOpen(false)} account={account} />
    </div>
  );
}
