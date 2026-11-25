import { History } from "lucide-react";
import { useServerTimeZone } from "@/hooks/useServerTime";
import { formatDateTime } from "@/lib/dateFormat";
import type { CampaignRound } from "@/lib/campaigns-api";

/// v0.1.67 — every round of a campaign: who sent it, when, the frozen
/// subject and how many mails went out, were skipped or failed.
export function CampaignRoundHistory({ rounds }: { rounds: CampaignRound[] }) {
  const zone = useServerTimeZone();
  if (rounds.length === 0) {
    return (
      <div className="glass-panel flex flex-col items-center gap-2 p-10 text-center">
        <History className="h-6 w-6 text-muted-foreground" />
        <p className="text-sm text-muted-foreground">Nothing sent yet.</p>
      </div>
    );
  }
  return (
    <div className="glass-panel overflow-auto">
      <table className="w-full text-sm">
        <thead className="bg-popover">
          <tr className="border-b border-glass text-left text-[11px] uppercase tracking-wider text-muted-foreground/70">
            <th className="px-4 py-2.5 font-medium">Round</th>
            <th className="px-3 py-2.5 font-medium">Subject</th>
            <th className="px-3 py-2.5 font-medium">Sent by</th>
            <th className="px-3 py-2.5 font-medium">Result</th>
            <th className="px-4 py-2.5 font-medium">Pending till</th>
          </tr>
        </thead>
        <tbody className="divide-y divide-glass">
          {rounds.map((r) => (
            <tr key={r.id} className="align-top">
              <td className="px-4 py-2.5">
                <div className="font-medium">#{r.roundNo} {r.kind === "FollowUp" ? "Follow-up" : "Initial"}</div>
                <div className="text-[11px] text-muted-foreground">
                  {r.scheduledUtc ? `Scheduled for ${formatDateTime(r.scheduledUtc, zone)}` : formatDateTime(r.startedUtc, zone)}
                </div>
              </td>
              <td className="max-w-80 truncate px-3 py-2.5" title={r.subject}>{r.subject}</td>
              <td className="px-3 py-2.5 text-muted-foreground">{r.sentByEmail ?? "—"}</td>
              <td className="px-3 py-2.5 text-xs">
                <span className="font-medium tabular-nums">{r.sent}/{r.total} sent</span>
                {r.queued > 0 && <span className="ml-2 text-primary">{r.queued} queued</span>}
                {r.skipped > 0 && <span className="ml-2 text-muted-foreground">{r.skipped} skipped</span>}
                {r.failed > 0 && <span className="ml-2 text-destructive">{r.failed} failed</span>}
                {r.cancelled > 0 && <span className="ml-2 text-muted-foreground">{r.cancelled} cancelled</span>}
                {!r.completedUtc && r.queued > 0 && (
                  <div className="mt-0.5 text-[11px] text-muted-foreground">{r.scheduledUtc ? "Waiting for its moment" : "Sending…"}</div>
                )}
              </td>
              <td className="px-4 py-2.5 text-muted-foreground">{formatDateTime(r.pendingTillUtc, zone)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
