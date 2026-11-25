import * as React from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useRouterState } from "@tanstack/react-router";
import { CalendarClock, Check, Clock3, Eye, Trash2, X } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { PendingTillField } from "@/components/PendingTillField";
import { useServerTimeZone } from "@/hooks/useServerTime";
import { formatDateMedium, formatDateTime } from "@/lib/dateFormat";
import { minuteToTime } from "@/lib/timeOfDay";
import { errorMessage } from "@/lib/http";
import { cn } from "@/lib/utils";
import { useTicketOverlayStore } from "@/stores/useTicketOverlayStore";
import {
  campaignKeys,
  campaignsApi,
  skipReasonLabel,
  type CampaignDecision,
  type CampaignRecipient,
  type CampaignRecipientState,
} from "@/lib/campaigns-api";
import { CampaignDecisionChip, CampaignStateChip } from "./CampaignStateChip";
import { RECIPIENT_FILTERS as FILTERS, filterRecipients, type RecipientFilter } from "./campaignRecipientFilters";

const DECISIONS: { id: CampaignDecision; label: string; icon: typeof Check }[] = [
  { id: "Approved", label: "Approved", icon: Check },
  { id: "OtherTime", label: "Other time", icon: Clock3 },
  { id: "Declined", label: "Declined", icon: X },
];

/// v0.1.67 — the campaign dashboard: every recipient in the caller's queues
/// with its derived state. A customer answer (mail reply or portal message)
/// or an expired pending timer lands under "Needs attention"; the agent
/// records the outcome in one click. Rows open the ticket in the overlay.
export function CampaignRecipientTable({
  campaignId,
  recipients,
  hiddenCount,
  readOnly,
  onPreview,
}: {
  campaignId: string;
  recipients: CampaignRecipient[];
  hiddenCount: number;
  readOnly: boolean;
  onPreview: (ticketId: string) => void;
}) {
  const zone = useServerTimeZone();
  const qc = useQueryClient();
  const location = useRouterState({ select: (s) => s.location });
  const openOverlay = useTicketOverlayStore((s) => s.open);
  const [filter, setFilter] = React.useState<RecipientFilter>(() =>
    recipients.some((r) => r.state !== "NotSent") ? "attention" : "all");
  const [selected, setSelected] = React.useState<Set<string>>(new Set());
  const [bulkDate, setBulkDate] = React.useState<string | null>(null);

  const rows = filterRecipients(recipients, filter);
  React.useEffect(() => {
    const visible = new Set(rows.map((r) => r.ticketId));
    setSelected((prev) => new Set([...prev].filter((id) => visible.has(id))));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [filter, recipients]);

  const refresh = () => void qc.invalidateQueries({ queryKey: campaignKeys.detail(campaignId) });
  const decide = useMutation({
    mutationFn: ({ ticketId, decision }: { ticketId: string; decision: CampaignDecision | null }) =>
      campaignsApi.setDecision(campaignId, ticketId, decision),
    onSuccess: refresh,
    onError: (err) => toast.error(errorMessage(err, "Could not save the decision")),
  });
  const setProposed = useMutation({
    mutationFn: (proposedUtc: string | null) => campaignsApi.setProposed(campaignId, [...selected], proposedUtc),
    onSuccess: (r) => { toast.success(`Proposed date set on ${r.updated} ticket${r.updated === 1 ? "" : "s"}`); setBulkDate(null); refresh(); },
    onError: (err) => toast.error(errorMessage(err, "Could not set the date")),
  });
  const remove = useMutation({
    mutationFn: () => campaignsApi.removeTickets(campaignId, [...selected]),
    onSuccess: (r) => {
      toast.success(r.kept > 0
        ? `${r.removed} removed · ${r.kept} kept (already mailed — their mail stays on the ticket)`
        : `${r.removed} removed`);
      setSelected(new Set());
      refresh();
    },
    onError: (err) => toast.error(errorMessage(err, "Could not remove the tickets")),
  });

  const allChecked = rows.length > 0 && rows.every((r) => selected.has(r.ticketId));
  const toggleAll = () => setSelected(allChecked ? new Set() : new Set(rows.map((r) => r.ticketId)));
  const toggle = (id: string) => setSelected((prev) => {
    const next = new Set(prev);
    if (next.has(id)) next.delete(id); else next.add(id);
    return next;
  });

  const open = (r: CampaignRecipient) => openOverlay({
    ticketId: r.ticketId,
    ticketNumber: r.number,
    subject: r.subject,
    source: { path: location.pathname, search: location.searchStr, tab: null },
  });

  return (
    <div className="sd-campaign-recipients flex flex-col gap-3">
      <div className="flex flex-wrap items-center gap-1.5" role="tablist" aria-label="Filter recipients">
        {FILTERS.map((f) => {
          const count = recipients.filter(f.match).length;
          return (
            <button
              key={f.id}
              type="button"
              role="tab"
              aria-selected={filter === f.id}
              onClick={() => setFilter(f.id)}
              className={cn(
                "inline-flex items-center gap-1.5 rounded-md border px-2.5 py-1 text-xs font-medium transition-colors",
                filter === f.id ? "border-primary/40 bg-primary/10 text-primary" : "border-glass text-muted-foreground hover:bg-glass/60",
              )}
            >
              {f.label}
              <span className="tabular-nums opacity-80">{count}</span>
            </button>
          );
        })}
      </div>

      {selected.size > 0 && !readOnly && (
        <div className="glass-card flex flex-wrap items-center gap-3 px-3 py-2 text-sm">
          <span className="font-medium">{selected.size} selected</span>
          <div className="flex items-center gap-2">
            <CalendarClock className="h-4 w-4 text-muted-foreground" />
            <div className="w-56">
              <PendingTillField value={bulkDate} onCommit={setBulkDate} commitOnChange pastMessage="The proposed date must be in the future." />
            </div>
            <Button size="sm" variant="outline" disabled={!bulkDate || setProposed.isPending} onClick={() => setProposed.mutate(bulkDate)}>
              Set proposed date
            </Button>
            <Button size="sm" variant="ghost" disabled={setProposed.isPending} onClick={() => setProposed.mutate(null)}>
              Use default
            </Button>
          </div>
          <Button size="sm" variant="ghost" className="ml-auto text-destructive" disabled={remove.isPending} onClick={() => remove.mutate()}>
            <Trash2 className="mr-1.5 h-3.5 w-3.5" /> Remove
          </Button>
        </div>
      )}

      <div className="glass-panel overflow-auto">
        {rows.length === 0 ? (
          <p className="p-8 text-center text-sm text-muted-foreground">
            {recipients.length === 0 ? "No tickets in this campaign yet." : "No tickets in this list."}
          </p>
        ) : (
          <table className="w-full text-sm">
            <thead className="sticky top-0 z-10 bg-popover">
              <tr className="border-b border-glass text-left text-[11px] uppercase tracking-wider text-muted-foreground/70">
                <th className="w-8 px-3 py-2.5">
                  {!readOnly && <input type="checkbox" aria-label="Select all" checked={allChecked} onChange={toggleAll} />}
                </th>
                <th className="px-3 py-2.5 font-medium">Ticket</th>
                <th className="px-3 py-2.5 font-medium">Contact</th>
                <th className="px-3 py-2.5 font-medium">Proposed</th>
                <th className="px-3 py-2.5 font-medium">State</th>
                <th className="px-3 py-2.5 font-medium">Outcome</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-glass">
              {rows.map((r) => (
                <Row
                  key={r.ticketId}
                  r={r}
                  zone={zone}
                  readOnly={readOnly}
                  checked={selected.has(r.ticketId)}
                  onToggle={() => toggle(r.ticketId)}
                  onOpen={() => open(r)}
                  onPreview={() => onPreview(r.ticketId)}
                  onDecide={(decision) => decide.mutate({ ticketId: r.ticketId, decision })}
                  busy={decide.isPending}
                />
              ))}
            </tbody>
          </table>
        )}
      </div>
      {hiddenCount > 0 && (
        <p className="text-xs text-muted-foreground">
          {hiddenCount} ticket{hiddenCount === 1 ? "" : "s"} in queues you cannot access {hiddenCount === 1 ? "is" : "are"} not shown.
        </p>
      )}
    </div>
  );
}

function Row({
  r, zone, readOnly, checked, onToggle, onOpen, onPreview, onDecide, busy,
}: {
  r: CampaignRecipient;
  zone: string | null;
  readOnly: boolean;
  checked: boolean;
  onToggle: () => void;
  onOpen: () => void;
  onPreview: () => void;
  onDecide: (d: CampaignDecision | null) => void;
  busy: boolean;
}) {
  const mailed = !!r.firstSentUtc;
  const attention = !r.decision && (r.state === "ReplyReceived" || r.state === "TimerExpired");
  return (
    <tr className={cn("sd-campaign-recipient align-top hover:bg-glass/60", attention && "bg-amber-100/30 dark:bg-amber-400/5")}>
      <td className="px-3 py-2.5">
        {!readOnly && <input type="checkbox" aria-label={`Select ticket ${r.number}`} checked={checked} onChange={onToggle} />}
      </td>
      <td className="max-w-80 px-3 py-2.5">
        <button type="button" onClick={onOpen} className="text-left hover:text-primary">
          <span className="font-medium tabular-nums">#{r.number}</span>{" "}
          <span className="text-foreground">{r.subject}</span>
        </button>
        <div className="mt-0.5 text-[11px] text-muted-foreground">{r.statusName}{r.companyName ? ` · ${r.companyName}` : ""}</div>
      </td>
      <td className="px-3 py-2.5">
        <div className="truncate">{r.requesterName ?? "—"}</div>
        <div className="truncate text-[11px] text-muted-foreground">{r.requesterEmail ?? "No e-mail address"}</div>
      </td>
      <td className="whitespace-nowrap px-3 py-2.5 text-muted-foreground">
        {r.proposedUtc ? formatDateTime(r.proposedUtc, zone) : <span className="text-[11px]">Default</span>}
      </td>
      <td className="px-3 py-2.5">
        <CampaignStateChip state={r.state} />
        <StateDetail r={r} zone={zone} />
      </td>
      <td className="px-3 py-2.5">
        <div className="flex flex-wrap items-center gap-1.5">
          {r.decision ? (
            <>
              <CampaignDecisionChip decision={r.decision} />
              {r.decisionSource === "Customer" && (
                <span className="text-[11px] text-muted-foreground" title="Answered through the answer link">by customer</span>
              )}
              {!readOnly && (
                <button type="button" className="text-[11px] text-muted-foreground hover:text-foreground" disabled={busy} onClick={() => onDecide(null)}>
                  Clear
                </button>
              )}
            </>
          ) : mailed && !readOnly ? (
            DECISIONS.map((d) => (
              <Button key={d.id} size="sm" variant={attention ? "outline" : "ghost"} className="h-7 px-2 text-xs" disabled={busy} onClick={() => onDecide(d.id)}>
                <d.icon className="mr-1 h-3.5 w-3.5" /> {d.label}
              </Button>
            ))
          ) : (
            <Button size="sm" variant="ghost" className="h-7 px-2 text-xs" onClick={onPreview}>
              <Eye className="mr-1 h-3.5 w-3.5" /> Preview
            </Button>
          )}
        </div>
        {r.decisionPreferredDate && (
          <p className="mt-1 text-[11px] text-foreground/80">
            Preferred: {formatDateMedium(r.decisionPreferredDate)}
            {r.decisionPreferredMinute !== null ? ` · ${minuteToTime(r.decisionPreferredMinute)}` : ""}
          </p>
        )}
        {r.decisionNote && (
          <p className="mt-1 max-w-64 whitespace-pre-line text-[11px] text-muted-foreground" title={r.decisionNote}>
            “{r.decisionNote.length > 160 ? `${r.decisionNote.slice(0, 160)}…` : r.decisionNote}”
          </p>
        )}
      </td>
    </tr>
  );
}

const DETAIL: Partial<Record<CampaignRecipientState, (r: CampaignRecipient, zone: string | null) => string | null>> = {
  ReplyReceived: (r, z) =>
    `${r.replyType === "PortalMessage" ? "Portal message" : r.replyType === "CampaignAnswer" ? "Answer link" : "Mail"} · ${formatDateTime(r.replyUtc, z)}`,
  Queued: (r, z) => (r.notBeforeUtc ? `Scheduled for ${formatDateTime(r.notBeforeUtc, z)}` : null),
  TimerExpired: (r, z) => `No answer · due ${formatDateTime(r.roundPendingTillUtc, z)}`,
  Waiting: (r, z) => `Sent ${formatDateTime(r.lastSentUtc, z)}${r.roundNo && r.roundNo > 1 ? ` · round ${r.roundNo}` : ""}`,
  Failed: (r) => r.lastError,
  Skipped: (r) => skipReasonLabel(r.skipReason),
};

function StateDetail({ r, zone }: { r: CampaignRecipient; zone: string | null }) {
  const text = DETAIL[r.state]?.(r, zone);
  const pending = r.pendingResult && r.pendingResult !== "applied" ? `Pending not set: ${skipReasonLabel(r.pendingResult)}` : null;
  if (!text && !pending) return null;
  return (
    <div className="mt-1 max-w-64 space-y-0.5 text-[11px] text-muted-foreground">
      {text && <div className="truncate" title={text}>{text}</div>}
      {pending && <div className="truncate" title={pending}>{pending}</div>}
    </div>
  );
}
