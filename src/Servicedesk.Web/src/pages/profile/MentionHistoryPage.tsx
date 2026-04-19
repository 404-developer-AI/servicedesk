import * as React from "react";
import { useQuery } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { AtSign, ArrowLeft, ExternalLink } from "lucide-react";
import {
  notificationApi,
  type UserNotification,
  type NotificationHistoryCursor,
} from "@/lib/notification-api";
import { useServerTime, toServerLocal } from "@/hooks/useServerTime";
import { cn } from "@/lib/utils";

/// v0.0.12 stap 4 — full history of tags received by the current user.
/// Cursor-paginated with "Meer laden". Status badges distinguish between
/// "Open" (still unacked), "Gedismiss" (acked without viewing) and
/// "Bekeken" (clicked through at least once). Same ownership guards as
/// the navbar widget: every row comes from /api/notifications/history
/// which filters on the calling user's id.
export function MentionHistoryPage() {
  const [pages, setPages] = React.useState<UserNotification[]>([]);
  const [cursor, setCursor] = React.useState<NotificationHistoryCursor | null>(null);
  const [nextCursor, setNextCursor] = React.useState<NotificationHistoryCursor | null>(null);
  const serverTime = useServerTime();
  const offsetMinutes = serverTime.time?.offsetMinutes ?? 0;

  const query = useQuery({
    queryKey: ["notifications", "history", cursor?.id ?? "first"],
    queryFn: () => notificationApi.listHistory(cursor ?? undefined, 50),
  });

  React.useEffect(() => {
    if (!query.data) return;
    setPages((prev) => cursor ? [...prev, ...query.data.items] : query.data.items);
    setNextCursor(query.data.nextCursor);
  }, [query.data, cursor]);

  const loadMore = () => {
    if (nextCursor) setCursor(nextCursor);
  };

  return (
    <div className="mx-auto max-w-4xl space-y-6 py-4">
      <header className="space-y-1">
        <Link
          to="/profile"
          className="inline-flex items-center gap-1 text-[11px] uppercase tracking-[0.22em] text-muted-foreground hover:text-foreground"
        >
          <ArrowLeft className="h-3 w-3" />
          Profile
        </Link>
        <h1 className="font-display text-display-sm font-semibold">Mijn tags</h1>
        <p className="text-xs text-muted-foreground">
          Elke keer dat iemand jou @@-tagt in een ticket, wordt dat hier bewaard —
          inclusief of je het al bekeken of acked hebt.
        </p>
      </header>

      <section className="glass-card overflow-hidden">
        {pages.length === 0 && query.isFetching ? (
          <div className="px-4 py-8 text-center text-xs text-muted-foreground/70">Loading…</div>
        ) : pages.length === 0 ? (
          <div className="px-4 py-8 text-center text-xs text-muted-foreground/70">
            Geen tags ontvangen. Zodra iemand jou tagt, verschijnt het hier.
          </div>
        ) : (
          <table className="w-full text-left text-sm">
            <thead className="border-b border-white/5 text-[11px] uppercase tracking-[0.14em] text-muted-foreground/70">
              <tr>
                <th className="px-3 py-2 font-medium">Tijd</th>
                <th className="px-3 py-2 font-medium">Van</th>
                <th className="px-3 py-2 font-medium">Ticket</th>
                <th className="px-3 py-2 font-medium">Preview</th>
                <th className="px-3 py-2 font-medium">Status</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-white/5">
              {pages.map((n) => (
                <Row
                  key={n.id}
                  n={n}
                  offsetMinutes={offsetMinutes ?? 0}
                />
              ))}
            </tbody>
          </table>
        )}
        {nextCursor ? (
          <div className="flex justify-center border-t border-white/5 px-3 py-3">
            <button
              type="button"
              onClick={loadMore}
              disabled={query.isFetching}
              className="rounded-md border border-white/10 bg-white/[0.04] px-3 py-1.5 text-xs text-foreground hover:bg-white/[0.07] disabled:opacity-50"
            >
              {query.isFetching ? "Laden…" : "Meer laden"}
            </button>
          </div>
        ) : null}
      </section>
    </div>
  );
}

function Row({ n, offsetMinutes }: { n: UserNotification; offsetMinutes: number }) {
  const when = toServerLocal(n.createdUtc, offsetMinutes);
  const localPart = n.sourceUserEmail?.split("@")[0] ?? "agent";

  const status: { label: string; tone: string } =
    n.viewedUtc !== null
      ? { label: "Bekeken", tone: "bg-sky-500/15 text-sky-300 border-sky-500/30" }
      : n.ackedUtc !== null
        ? { label: "Gedismiss", tone: "bg-white/[0.05] text-muted-foreground border-white/10" }
        : { label: "Open", tone: "bg-amber-500/15 text-amber-300 border-amber-500/30" };

  return (
    <tr className="hover:bg-white/[0.02]">
      <td className="px-3 py-2 text-xs text-muted-foreground">{when}</td>
      <td className="px-3 py-2 text-xs">
        <span className="inline-flex items-center gap-1">
          <AtSign className="h-3 w-3 text-purple-300" />
          <span className="font-medium">@{localPart}</span>
        </span>
      </td>
      <td className="px-3 py-2 text-xs">
        <Link
          to="/tickets/$ticketId"
          params={{ ticketId: n.ticketId }}
          hash={`event-${n.eventId}`}
          className="inline-flex items-center gap-1 font-mono text-[11px] text-primary hover:underline"
        >
          #{n.ticketNumber}
          <ExternalLink className="h-3 w-3" />
        </Link>
        <div className="truncate text-[11px] text-muted-foreground/70">
          {n.ticketSubject}
        </div>
      </td>
      <td className="max-w-[24rem] px-3 py-2 text-xs text-muted-foreground/90">
        <span className="line-clamp-2">{n.previewText}</span>
      </td>
      <td className="px-3 py-2">
        <span className={cn(
          "inline-flex rounded-full border px-2 py-0.5 text-[10px] font-medium uppercase tracking-wide",
          status.tone,
        )}>
          {status.label}
        </span>
      </td>
    </tr>
  );
}
