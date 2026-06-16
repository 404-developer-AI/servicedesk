import { useMemo, useState } from "react";
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
  Send,
} from "lucide-react";
import { contractM365Api, type M365Mailbox } from "@/lib/api";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import {
  Tooltip,
  TooltipContent,
  TooltipProvider,
  TooltipTrigger,
} from "@/components/ui/tooltip";
import {
  M365StatusBadge,
  openConsentWindow,
  relativeFromUtc,
} from "@/components/contracts/m365Connection";
import { cn } from "@/lib/utils";
import { contractReportsApi } from "@/lib/contractReports-api";
import { SendReportModal } from "./reports/SendReportModal";

/// One company's synced Microsoft 365 mailboxes, reached from the matching list.
/// Shows the connection state + the same mailbox classification the probe
/// script produced (type, name, UPN, sign-in status, licenses), with connect /
/// sync / disconnect actions. Gated by contracts_enabled (route + endpoint).
export function ContractM365CompanyPage({ companyId }: { companyId: string }) {
  const qc = useQueryClient();
  const queryKey = ["contracts", "m365", "mailboxes", companyId] as const;
  const [sendOpen, setSendOpen] = useState(false);

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
  const veeamAvailable = data?.veeamAvailable ?? false;
  const byType = useMemo(() => summariseByType(mailboxes), [mailboxes]);

  // Protection summary (matches the emailed report's header chips). Denominator
  // counts only mailboxes where the axis has a verdict — when the integration is
  // available that is every mailbox, so it reads e.g. "9 / 15".
  const protection = useMemo(() => {
    const tally = (pick: (m: (typeof mailboxes)[number]) => boolean | null | undefined) => {
      let total = 0;
      let ok = 0;
      for (const m of mailboxes) {
        const v = pick(m);
        if (v === null || v === undefined) continue;
        total++;
        if (v) ok++;
      }
      return { ok, total };
    };
    // Unique mailboxes with a backup licence in use = at least one of
    // OneDrive / Exchange backed up (a mailbox with both still counts once).
    const backupLicenseUsed = mailboxes.filter(
      (m) => m.onedriveProtected === true || m.exchangeProtected === true,
    ).length;
    return {
      spam: tally((m) => m.spamFilterProtected),
      onedrive: tally((m) => m.onedriveProtected),
      exchange: tally((m) => m.exchangeProtected),
      backupLicenseUsed,
    };
  }, [mailboxes]);

  const lastSentQ = useQuery({
    queryKey: ["contract-report-last-sent"],
    queryFn: () => contractReportsApi.lastSent(),
    staleTime: 60_000,
  });
  const lastSentEntry = lastSentQ.data?.items.find((s) => s.companyId === companyId) ?? null;

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
          <TooltipProvider>
            <Tooltip>
              <TooltipTrigger asChild>
                <span tabIndex={mailboxes.length === 0 ? 0 : undefined}>
                  <Button
                    size="sm"
                    variant="outline"
                    className="gap-1.5"
                    onClick={() => setSendOpen(true)}
                    disabled={mailboxes.length === 0}
                  >
                    <Send className="h-3.5 w-3.5" />
                    Send report
                  </Button>
                </span>
              </TooltipTrigger>
              {mailboxes.length === 0 && (
                <TooltipContent className="border border-glass-strong bg-popover text-popover-foreground">
                  No mailboxes synced yet
                </TooltipContent>
              )}
            </Tooltip>
          </TooltipProvider>
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

      {lastSentEntry && (
        <p className="text-xs text-muted-foreground/70">
          Last report sent {relativeFromUtc(lastSentEntry.sentUtc)}
          {lastSentEntry.sentByName && <> by {lastSentEntry.sentByName}</>}
          {lastSentEntry.subject && (
            <span className="text-muted-foreground/50"> · {lastSentEntry.subject}</span>
          )}
        </p>
      )}

      <SendReportModal
        companyId={companyId}
        companyName={title}
        open={sendOpen}
        onOpenChange={setSendOpen}
        onSent={() => {
          lastSentQ.refetch();
        }}
      />

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

      {mailboxes.length > 0 && (spamFilterAvailable || veeamAvailable || byType.length > 0) && (
        <div className="flex flex-wrap items-center gap-2">
          {spamFilterAvailable && (
            <ProtectionChip tone="blue" label="Spam filter" ok={protection.spam.ok} total={protection.spam.total} />
          )}
          {veeamAvailable && (
            <ProtectionChip
              tone="green"
              label="Backup license used"
              ok={protection.backupLicenseUsed}
              total={mailboxes.length}
            />
          )}
          {veeamAvailable && (
            <ProtectionChip tone="grey" label="OneDrive backup" ok={protection.onedrive.ok} total={protection.onedrive.total} />
          )}
          {veeamAvailable && (
            <ProtectionChip tone="grey" label="Exchange backup" ok={protection.exchange.ok} total={protection.exchange.total} />
          )}
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
          <TooltipProvider delayDuration={100}>
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
                    {veeamAvailable && (
                      <>
                        <th className="px-3 py-2.5 font-medium">OneDrive</th>
                        <th className="px-3 py-2.5 font-medium">Exchange</th>
                      </>
                    )}
                    <th className="px-3 py-2.5 font-medium">Licenses</th>
                  </tr>
                </thead>
                <tbody>
                  {mailboxes.map((m) => (
                    <MailboxRow
                      key={m.objectId}
                      mailbox={m}
                      showSpamFilter={spamFilterAvailable}
                      showVeeam={veeamAvailable}
                    />
                  ))}
                </tbody>
              </table>
            </div>
          </TooltipProvider>
        )}
      </div>
    </div>
  );
}

function MailboxRow({
  mailbox,
  showSpamFilter,
  showVeeam,
}: {
  mailbox: M365Mailbox;
  showSpamFilter: boolean;
  showVeeam: boolean;
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
      {showVeeam && (
        <>
          <td className="px-3 py-2.5">
            <BackupBadge
              protected_={mailbox.onedriveProtected}
              restorePoints={mailbox.onedriveRestorePoints}
              lastBackupUtc={mailbox.onedriveLastBackupUtc}
            />
          </td>
          <td className="px-3 py-2.5">
            <BackupBadge
              protected_={mailbox.exchangeProtected}
              restorePoints={mailbox.exchangeRestorePoints}
              lastBackupUtc={mailbox.exchangeLastBackupUtc}
            />
          </td>
        </>
      )}
      <td className="px-3 py-2.5 text-xs text-muted-foreground">{mailbox.licenses || "—"}</td>
    </tr>
  );
}

/// Veeam backup verdict for one mailbox+service. Protected = VSPC has a backup
/// object for this display name in that repository; hovering the pill reveals
/// the last-backup time and how many restore points we hold. Unprotected = the
/// company is in VSPC but this mailbox has no backup object for the service.
function BackupBadge({
  protected_,
  restorePoints,
  lastBackupUtc,
}: {
  protected_: boolean | null;
  restorePoints: number | null;
  lastBackupUtc: string | null;
}) {
  if (protected_ === null) return <span className="text-muted-foreground">—</span>;
  if (!protected_) {
    return (
      <span className="inline-flex items-center gap-1.5 rounded-full border border-glass-strong bg-glass-strong px-2 py-0.5 text-[11px] text-muted-foreground">
        <span className="h-1.5 w-1.5 rounded-full bg-muted-foreground/50" /> Unprotected
      </span>
    );
  }
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <span className="inline-flex cursor-default items-center gap-1.5 rounded-full border border-emerald-400/30 bg-emerald-500/10 px-2 py-0.5 text-[11px] text-emerald-300">
          <span className="h-1.5 w-1.5 rounded-full bg-emerald-400" /> Protected
        </span>
      </TooltipTrigger>
      <TooltipContent className="border border-glass-strong bg-popover text-popover-foreground">
        <div className="space-y-0.5">
          <div className="text-[11px] text-muted-foreground">
            Last backup:{" "}
            <span className="text-foreground">{formatBackupStamp(lastBackupUtc)}</span>
          </div>
          <div className="text-[11px] text-muted-foreground">
            Restore points:{" "}
            <span className="tabular-nums text-foreground">{restorePoints ?? 0}</span>
          </div>
        </div>
      </TooltipContent>
    </Tooltip>
  );
}

/// Absolute, server-sourced backup timestamp for the Protected pill hover.
function formatBackupStamp(iso: string | null): string {
  if (!iso) return "unknown";
  try {
    return new Intl.DateTimeFormat(undefined, {
      year: "numeric",
      month: "short",
      day: "2-digit",
      hour: "2-digit",
      minute: "2-digit",
    }).format(new Date(iso));
  } catch {
    return iso;
  }
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

/// Header summary chip: a labelled count (e.g. "Spam filter 9/15"). The tone is
/// brand-coded rather than coverage-coded — Sophos blue for the spam filter,
/// Veeam green for backup licences, neutral grey for everything else.
type ChipTone = "blue" | "green" | "grey";

const CHIP_TONES: Record<ChipTone, string> = {
  blue: "border-sky-400/30 bg-sky-500/10 text-sky-300",
  green: "border-emerald-400/30 bg-emerald-500/10 text-emerald-300",
  grey: "border-glass bg-glass text-muted-foreground",
};

function ProtectionChip({
  label,
  ok,
  total,
  tone,
}: {
  label: string;
  ok: number;
  total: number;
  tone: ChipTone;
}) {
  return (
    <span
      className={cn(
        "inline-flex items-center gap-1.5 rounded-full border px-2.5 py-1 text-xs",
        CHIP_TONES[tone],
      )}
    >
      <span className="text-foreground/90">{label}</span>
      <span className="tabular-nums font-medium">{ok}</span>
      <span className="tabular-nums text-muted-foreground">/ {total}</span>
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
