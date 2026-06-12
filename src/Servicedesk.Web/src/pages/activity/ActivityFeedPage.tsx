import { useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { Activity, Phone, PhoneIncoming, PhoneOutgoing, ExternalLink } from "lucide-react";
import { activityApi, type ActivityEntry, type ActivityListQuery } from "@/lib/api";
import { Input } from "@/components/ui/input";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { useServerTime, toServerLocal } from "@/hooks/useServerTime";
import { useActivityFeedSignalR } from "@/hooks/useActivityFeedSignalR";

/// Whitelist of event-type filter options. Kept in this file so it stays
/// curated — every new event type added to the backend should be listed
/// here so admins can filter by it. Empty value = "all".
const EVENT_TYPES: readonly { value: string; label: string }[] = [
  { value: "", label: "All events" },
  { value: "ticket_created", label: "Ticket created" },
  { value: "ticket_status_changed", label: "Ticket status changed" },
  { value: "ticket_assigned", label: "Ticket assigned" },
  { value: "ticket_priority_changed", label: "Ticket priority changed" },
  { value: "ticket_queue_changed", label: "Ticket queue changed" },
  { value: "ticket_note_internal", label: "Internal note" },
  { value: "ticket_note_public", label: "Public note" },
  { value: "ticket_comment", label: "Customer reply" },
  { value: "ticket_mail_sent", label: "Mail sent" },
  { value: "ticket_mail_received", label: "Mail received" },
  { value: "ticket_system_note", label: "System note" },
  { value: "kb.article.created", label: "KB article created" },
  { value: "kb.article.updated", label: "KB article edited" },
  { value: "kb.article.status.changed", label: "KB article status" },
  { value: "kb.article.deleted", label: "KB article deleted" },
  { value: "telavox_call_completed", label: "Phone call (Telavox)" },
  { value: "login_success", label: "Sign in" },
  { value: "logout", label: "Sign out" },
  { value: "auth.microsoft.login.success", label: "Sign in (M365)" },
  { value: "2fa_enrolled", label: "2FA enabled" },
  { value: "2fa_disabled", label: "2FA disabled" },
  { value: "password_changed", label: "Password changed" },
  { value: "setting_changed", label: "Setting changed" },
  { value: "user.feature_flags.changed", label: "User feature flags changed" },
];

function localPartOf(email: string): string {
  const at = email.indexOf("@");
  return at <= 0 ? email : email.substring(0, at);
}

function parseMetadata(json: string): Record<string, unknown> {
  try { return JSON.parse(json) as Record<string, unknown>; }
  catch { return {}; }
}

export function ActivityFeedPage() {
  const navigate = useNavigate();
  useActivityFeedSignalR(true);

  const [eventType, setEventType] = useState<string>("");
  const [search, setSearch] = useState<string>("");
  const [fromDate, setFromDate] = useState<string>("");
  const [toDate, setToDate] = useState<string>("");
  const [cursor, setCursor] = useState<number | undefined>(undefined);

  const query: ActivityListQuery = useMemo(
    () => ({
      eventType: eventType || undefined,
      search: search.trim() || undefined,
      fromUtc: fromDate ? new Date(fromDate).toISOString() : undefined,
      toUtc: toDate ? new Date(toDate + "T23:59:59").toISOString() : undefined,
      cursor,
      limit: 50,
    }),
    [eventType, search, fromDate, toDate, cursor],
  );

  const { data, isLoading, isError, refetch } = useQuery({
    queryKey: ["activity", "list", query],
    queryFn: () => activityApi.list(query),
  });

  const { time: serverTime } = useServerTime();
  const offsetMinutes = serverTime?.offsetMinutes ?? 0;

  function openTicket(ticketId: string) {
    navigate({ to: "/tickets/$ticketId" as never, params: { ticketId } as never });
  }

  return (
    <div className="flex h-[calc(100vh-8rem)] w-full flex-col gap-6">
      <header className="flex items-start justify-between gap-4">
        <div className="space-y-1">
          <h1 className="text-display-md font-semibold text-foreground">
            <span className="inline-flex items-center gap-2">
              <Activity className="h-5 w-5 text-primary" />
              Activity feed
            </span>
          </h1>
          <p className="text-sm text-muted-foreground">
            Every action taken by agents and admins across the app. Visible only
            to users with the activity-feed flag enabled.
          </p>
        </div>
        <Badge className="border border-glass bg-glass text-xs font-normal text-muted-foreground">
          Activity feed
        </Badge>
      </header>

      <section className="glass-card p-4">
        <div className="flex flex-wrap items-end gap-3">
          <label className="flex flex-col gap-1 text-xs text-muted-foreground">
            Event type
            <select
              value={eventType}
              onChange={(e) => { setEventType(e.target.value); setCursor(undefined); }}
              className="h-9 rounded-md border border-glass bg-glass px-2 text-sm text-foreground outline-none focus:border-primary/60"
            >
              {EVENT_TYPES.map((t) => (
                <option key={t.value} value={t.value} className="bg-background">
                  {t.label}
                </option>
              ))}
            </select>
          </label>

          <label className="flex flex-col gap-1 text-xs text-muted-foreground">
            From
            <input
              type="date"
              value={fromDate}
              onChange={(e) => { setFromDate(e.target.value); setCursor(undefined); }}
              className="h-9 rounded-md border border-glass bg-glass px-2 text-sm text-foreground outline-none focus:border-primary/60"
            />
          </label>

          <label className="flex flex-col gap-1 text-xs text-muted-foreground">
            To
            <input
              type="date"
              value={toDate}
              onChange={(e) => { setToDate(e.target.value); setCursor(undefined); }}
              className="h-9 rounded-md border border-glass bg-glass px-2 text-sm text-foreground outline-none focus:border-primary/60"
            />
          </label>

          <label className="flex flex-col gap-1 text-xs text-muted-foreground">
            Search
            <Input
              value={search}
              onChange={(e) => { setSearch(e.target.value); setCursor(undefined); }}
              placeholder="agent, ticket subject, summary…"
              className="h-9 w-64"
            />
          </label>

          <div className="ml-auto flex gap-2">
            <Button
              variant="secondary"
              onClick={() => { setCursor(undefined); refetch(); }}
            >
              Refresh
            </Button>
          </div>
        </div>
      </section>

      <section className="glass-card min-h-0 flex-1 overflow-y-auto">
        {isLoading ? (
          <div className="space-y-2 p-4">
            {Array.from({ length: 8 }).map((_, i) => (
              <Skeleton key={i} className="h-12 w-full" />
            ))}
          </div>
        ) : isError ? (
          <div className="p-8 text-center text-sm text-destructive">
            Failed to load the activity feed. Is your account enabled for this feature?
          </div>
        ) : data && data.items.length > 0 ? (
          <ul className="divide-y divide-glass">
            {data.items.map((entry) => (
              <ActivityRow
                key={entry.id}
                entry={entry}
                offsetMinutes={offsetMinutes}
                onTicketClick={openTicket}
              />
            ))}
          </ul>
        ) : (
          <div className="p-8 text-center text-sm text-muted-foreground">
            No activity matches the current filters.
          </div>
        )}
      </section>

      <footer className="flex items-center justify-between text-xs text-muted-foreground">
        <span>Showing {data?.items.length ?? 0} entries</span>
        <div className="flex gap-2">
          <Button
            variant="ghost"
            disabled={cursor === undefined}
            onClick={() => setCursor(undefined)}
          >
            First page
          </Button>
          <Button
            variant="secondary"
            disabled={!data?.nextCursor}
            onClick={() => data?.nextCursor && setCursor(data.nextCursor)}
          >
            Next →
          </Button>
        </div>
      </footer>
    </div>
  );
}

function ActivityRow({
  entry,
  offsetMinutes,
  onTicketClick,
}: {
  entry: ActivityEntry;
  offsetMinutes: number;
  onTicketClick: (ticketId: string) => void;
}) {
  const isTicket = entry.entityType === "ticket" && entry.entityId !== null;
  const isTelavox = entry.eventType.startsWith("telavox_");
  const meta = useMemo(() => parseMetadata(entry.metadataJson), [entry.metadataJson]);

  return (
    <li className="flex items-start gap-4 p-4 transition-colors hover:bg-glass-hover">
      <div className="mt-1 flex h-8 w-8 shrink-0 items-center justify-center rounded-full bg-primary/20 text-[11px] font-medium uppercase text-primary">
        {localPartOf(entry.agentEmail).substring(0, 2)}
      </div>
      <div className="min-w-0 flex-1">
        <div className="flex flex-wrap items-baseline gap-x-2 text-sm">
          <span className="font-medium text-foreground">{entry.agentEmail}</span>
          <span className="text-muted-foreground">{entry.summary}</span>
          {isTicket && entry.ticketNumber !== null && (
            <button
              type="button"
              onClick={() => onTicketClick(entry.entityId!)}
              className="inline-flex items-center gap-1 font-medium text-primary hover:underline"
              title={entry.ticketSubject ?? ""}
            >
              #{entry.ticketNumber}
              {entry.ticketSubject ? ` — ${entry.ticketSubject}` : ""}
              <ExternalLink className="h-3 w-3" />
            </button>
          )}
          {isTelavox && (() => {
            const direction = meta["direction"] as string | undefined;
            const durationSeconds =
              typeof meta["durationSeconds"] === "number"
                ? (meta["durationSeconds"] as number)
                : undefined;
            const DirIcon =
              direction === "outgoing"
                ? PhoneOutgoing
                : direction === "incoming"
                  ? PhoneIncoming
                  : Phone;
            const durationLabel =
              durationSeconds !== undefined
                ? `${Math.floor(durationSeconds / 60)}:${String(durationSeconds % 60).padStart(2, "0")}`
                : undefined;
            return (
              <span className="inline-flex items-center gap-1 text-emerald-400">
                <DirIcon className="h-3 w-3" />
                <span className="text-xs text-muted-foreground">
                  {durationLabel ??
                    (meta["extension"] as string | undefined) ??
                    "phone call"}
                </span>
              </span>
            );
          })()}
        </div>
        <div className="mt-0.5 flex items-center gap-2 text-[11px] text-muted-foreground/70">
          <Badge className="border border-glass bg-glass font-mono text-[10px] font-normal text-foreground">
            {entry.eventType}
          </Badge>
          <span>{toServerLocal(entry.occurredUtc, offsetMinutes, true)}</span>
        </div>
      </div>
    </li>
  );
}
