import { useMemo } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { toast } from "sonner";
import {
  AlertTriangle,
  ChevronLeft,
  Inbox,
  Plug,
  Power,
  RefreshCw,
} from "lucide-react";
import { contractM365Api, type M365Mailbox } from "@/lib/api";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import {
  M365StatusBadge,
  openConsentWindow,
  relativeFromUtc,
} from "@/components/contracts/m365Connection";
import { cn } from "@/lib/utils";

/// One company's synced Microsoft 365 mailboxes, reached from the matching list.
/// Shows the connection state + the same mailbox classification the probe
/// script produced (type, name, UPN, sign-in status, licenses), with connect /
/// sync / disconnect actions. Gated by contracts_enabled (route + endpoint).
export function ContractM365CompanyPage({ companyId }: { companyId: string }) {
  const qc = useQueryClient();
  const queryKey = ["contracts", "m365", "mailboxes", companyId] as const;

  const detail = useQuery({
    queryKey,
    queryFn: () => contractM365Api.mailboxes(companyId),
    refetchOnWindowFocus: true,
  });

  const data = detail.data;
  const status = data?.status ?? null;
  const connected = status === "connected";
  const canConnect = status === null || status === "disconnected" || status === "needs_reconsent";

  const connect = useMutation({
    mutationFn: () => openConsentWindow(() => contractM365Api.connect(companyId)),
    onError: () =>
      toast.error(
        "Could not start the Microsoft 365 connection. Check the integration under Settings → Integrations.",
      ),
  });

  const sync = useMutation({
    mutationFn: () => contractM365Api.sync(companyId),
    onSuccess: (r) => {
      if (r.success) toast.success(r.changed ? `Synced — ${r.mailboxCount} mailboxes updated.` : "Synced — no changes.");
      else toast.error(r.error ?? "Sync failed.");
      qc.invalidateQueries({ queryKey });
    },
    onError: () => toast.error("Sync failed."),
  });

  const disconnect = useMutation({
    mutationFn: () => contractM365Api.disconnect(companyId),
    onSuccess: () => {
      toast.success("Disconnected.");
      qc.invalidateQueries({ queryKey });
      qc.invalidateQueries({ queryKey: ["contracts", "m365", "connections"] });
    },
    onError: () => toast.error("Could not disconnect."),
  });

  const mailboxes = data?.mailboxes ?? [];
  const spamFilterAvailable = data?.spamFilterAvailable ?? false;
  const byType = useMemo(() => summariseByType(mailboxes), [mailboxes]);

  const title = data?.companyName || data?.companyCode || "Company";
  const lastChecked = relativeFromUtc(data?.lastCheckedUtc);
  const lastChanged = relativeFromUtc(data?.lastChangedUtc);

  return (
    <div className="flex flex-1 flex-col gap-4 p-4 sm:p-6">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="flex items-center gap-3">
          <Link
            to="/contracts/m365-matching"
            className="flex h-10 w-10 items-center justify-center rounded-full border border-glass bg-glass text-muted-foreground transition-colors hover:text-foreground"
            aria-label="Back to Microsoft 365 matching"
          >
            <ChevronLeft className="h-4.5 w-4.5" />
          </Link>
          <div>
            <div className="flex items-center gap-2.5">
              <h1 className="text-display-md font-semibold text-foreground">{title}</h1>
              <M365StatusBadge status={status} />
            </div>
            <p className="text-xs text-muted-foreground">
              {data?.companyCode && <span className="font-mono">{data.companyCode}</span>}
              {connected && lastChecked && (
                <span className="text-muted-foreground/70">
                  {data?.companyCode ? " · " : ""}
                  {mailboxes.length} mailboxes · checked {lastChecked}
                  {lastChanged && lastChanged !== lastChecked ? ` · changed ${lastChanged}` : ""}
                </span>
              )}
            </p>
          </div>
        </div>
        <div className="flex items-center gap-2">
          {connected && (
            <Button
              size="sm"
              variant="outline"
              className="gap-1.5"
              onClick={() => sync.mutate()}
              disabled={sync.isPending}
            >
              <RefreshCw className={cn("h-3.5 w-3.5", sync.isPending && "animate-spin")} />
              Sync now
            </Button>
          )}
          {canConnect && (
            <Button
              size="sm"
              variant="outline"
              className="gap-1.5"
              onClick={() => connect.mutate()}
              disabled={connect.isPending}
            >
              <Plug className="h-3.5 w-3.5" />
              {status === "needs_reconsent" ? "Reconnect" : "Connect with M365"}
            </Button>
          )}
          {status !== null && status !== "disconnected" && (
            <Button
              size="sm"
              variant="ghost"
              className="gap-1.5 text-muted-foreground hover:text-rose-300"
              onClick={() => disconnect.mutate()}
              disabled={disconnect.isPending}
            >
              <Power className="h-3.5 w-3.5" />
              Disconnect
            </Button>
          )}
        </div>
      </div>

      {data?.lastError && (status === "needs_reconsent" || status === "error") && (
        <div className="flex items-start gap-2.5 rounded-lg border border-amber-400/20 bg-amber-500/[0.06] p-3 text-xs text-amber-200">
          <AlertTriangle className="mt-0.5 h-4 w-4 flex-none" />
          <div>
            <p className="font-medium">
              {status === "needs_reconsent" ? "Re-consent required" : "Last sync failed"}
            </p>
            <p className="text-amber-200/80">{data.lastError}</p>
          </div>
        </div>
      )}

      {byType.length > 0 && (
        <div className="flex flex-wrap gap-2">
          {byType.map((t) => (
            <span
              key={t.type}
              className="inline-flex items-center gap-1.5 rounded-full border border-glass bg-glass px-2.5 py-1 text-xs text-muted-foreground"
            >
              <span className="capitalize text-foreground">{t.type}</span>
              <span className="tabular-nums">{t.count}</span>
            </span>
          ))}
        </div>
      )}

      <div className="glass-panel flex-1 overflow-hidden">
        {detail.isLoading ? (
          <div className="space-y-2 p-4">
            <Skeleton className="h-10 w-full" />
            <Skeleton className="h-10 w-full" />
            <Skeleton className="h-10 w-full" />
          </div>
        ) : detail.isError ? (
          <div className="p-6 text-sm text-rose-300">Could not load mailboxes. Refresh to retry.</div>
        ) : !connected && mailboxes.length === 0 ? (
          <EmptyConnect status={status} onConnect={() => connect.mutate()} pending={connect.isPending} />
        ) : mailboxes.length === 0 ? (
          <div className="flex flex-col items-center justify-center gap-3 p-12 text-center">
            <Inbox className="h-6 w-6 text-muted-foreground" />
            <p className="max-w-md text-sm text-muted-foreground">
              No mailboxes found in this tenant yet. If the connection is fresh, the first sync may still be running —
              try “Sync now”.
            </p>
          </div>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b border-glass text-left text-[11px] uppercase tracking-wider text-muted-foreground/70">
                  <th className="px-3 py-2.5 font-medium">Type</th>
                  <th className="px-3 py-2.5 font-medium">Name</th>
                  <th className="px-3 py-2.5 font-medium">UPN</th>
                  <th className="px-3 py-2.5 font-medium">Enabled</th>
                  {spamFilterAvailable && (
                    <th className="px-3 py-2.5 font-medium">Spam filter</th>
                  )}
                  <th className="px-3 py-2.5 font-medium">Licenses</th>
                </tr>
              </thead>
              <tbody>
                {mailboxes.map((m) => (
                  <MailboxRow key={m.objectId} mailbox={m} showSpamFilter={spamFilterAvailable} />
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </div>
  );
}

function MailboxRow({
  mailbox,
  showSpamFilter,
}: {
  mailbox: M365Mailbox;
  showSpamFilter: boolean;
}) {
  return (
    <tr className="border-b border-glass align-top transition-colors hover:bg-glass-hover">
      <td className="px-3 py-2.5">
        <span className="inline-flex rounded-full border border-glass bg-glass px-2 py-0.5 text-[11px] capitalize text-muted-foreground">
          {mailbox.mailboxType ?? "—"}
        </span>
      </td>
      <td className="px-3 py-2.5 text-foreground">{mailbox.displayName || "—"}</td>
      <td className="px-3 py-2.5 font-mono text-xs text-muted-foreground">{mailbox.upn || mailbox.mail || "—"}</td>
      <td className="px-3 py-2.5">
        {mailbox.enabled === null ? (
          <span className="text-muted-foreground">—</span>
        ) : mailbox.enabled ? (
          <span className="text-emerald-300">Yes</span>
        ) : (
          <span className="text-rose-300">No</span>
        )}
      </td>
      {showSpamFilter && (
        <td className="px-3 py-2.5">
          <SpamFilterBadge protected_={mailbox.spamFilterProtected} />
        </td>
      )}
      <td className="px-3 py-2.5 text-xs text-muted-foreground">{mailbox.licenses || "—"}</td>
    </tr>
  );
}

/// Sophos spam-filter verdict for one mailbox. Protected = its address is in
/// the matched Sophos tenant's mailbox set; Unprotected = it is not.
function SpamFilterBadge({ protected_ }: { protected_: boolean | null }) {
  if (protected_ === null) return <span className="text-muted-foreground">—</span>;
  return protected_ ? (
    <span className="inline-flex items-center gap-1.5 rounded-full border border-emerald-400/30 bg-emerald-500/10 px-2 py-0.5 text-[11px] text-emerald-300">
      <span className="h-1.5 w-1.5 rounded-full bg-emerald-400" /> Protected
    </span>
  ) : (
    <span className="inline-flex items-center gap-1.5 rounded-full border border-glass-strong bg-glass-strong px-2 py-0.5 text-[11px] text-muted-foreground">
      <span className="h-1.5 w-1.5 rounded-full bg-muted-foreground/50" /> Unprotected
    </span>
  );
}

function EmptyConnect({
  status,
  onConnect,
  pending,
}: {
  status: string | null;
  onConnect: () => void;
  pending: boolean;
}) {
  return (
    <div className="flex flex-col items-center justify-center gap-3 p-12 text-center">
      <Plug className="h-6 w-6 text-muted-foreground" />
      <p className="max-w-md text-sm text-muted-foreground">
        {status === "needs_reconsent"
          ? "This customer's consent is no longer valid. Reconnect to read their Microsoft 365 directory again."
          : "Not connected yet. Connect to sign in with the customer's global admin and grant consent, then their mailboxes and licenses appear here."}
      </p>
      <Button size="sm" variant="outline" className="gap-1.5" onClick={onConnect} disabled={pending}>
        <Plug className="h-3.5 w-3.5" />
        {status === "needs_reconsent" ? "Reconnect" : "Connect with M365"}
      </Button>
    </div>
  );
}

function summariseByType(mailboxes: M365Mailbox[]): { type: string; count: number }[] {
  const counts = new Map<string, number>();
  for (const m of mailboxes) {
    const t = m.mailboxType || "other";
    counts.set(t, (counts.get(t) ?? 0) + 1);
  }
  return Array.from(counts.entries())
    .map(([type, count]) => ({ type, count }))
    .sort((a, b) => a.type.localeCompare(b.type));
}
