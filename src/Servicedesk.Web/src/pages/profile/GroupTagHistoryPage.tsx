import * as React from "react";
import { useQuery } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { ArrowLeft, ExternalLink } from "lucide-react";
import { useServerTimeZone } from "@/hooks/useServerTime";
import { formatDateTime } from "@/lib/dateFormat";
import { cn } from "@/lib/utils";
import { GROUP_TAGS_KEY, groupTagApi, type GroupTag, type GroupTagCursor } from "@/lib/group-tags-api";

/// v0.1.54 — history of the group tags of every active group the user
/// belongs to (open and acknowledged, with who acknowledged it; personal
/// "Everyone confirms" tags show the caller's own read + "read by X of Y"). Cursor
/// paged like My tags; /api/group-tags/history only returns the caller's
/// own groups.
export function GroupTagHistoryPage() {
  const zone = useServerTimeZone();
  const [rows, setRows] = React.useState<GroupTag[]>([]);
  const [cursor, setCursor] = React.useState<GroupTagCursor | null>(null);
  const [nextCursor, setNextCursor] = React.useState<GroupTagCursor | null>(null);

  const query = useQuery({
    queryKey: [...GROUP_TAGS_KEY, "history", cursor?.id ?? "first"],
    queryFn: () => groupTagApi.history(cursor, 50),
  });

  React.useEffect(() => {
    if (!query.data) return;
    setRows((prev) => (cursor ? [...prev, ...query.data.items] : query.data.items));
    setNextCursor(query.data.nextCursor);
  }, [query.data, cursor]);

  return (
    <div className="mx-auto max-w-4xl space-y-6 py-4">
      <header className="space-y-1">
        <Link to="/profile"
          className="inline-flex items-center gap-1 text-[11px] uppercase tracking-[0.22em] text-muted-foreground hover:text-foreground">
          <ArrowLeft className="h-3 w-3" />
          Profile
        </Link>
        <h1 className="font-display text-display-sm font-semibold">My group tags</h1>
        <p className="text-xs text-muted-foreground">
          Every tag for the groups you belong to — open, or acknowledged and by whom. One acknowledgement clears a tag
          for the whole group, except for groups where each member confirms personally.
        </p>
      </header>

      <section className="glass-card overflow-hidden">
        {rows.length === 0 && query.isFetching ? (
          <div className="px-4 py-8 text-center text-xs text-muted-foreground/70">Loading…</div>
        ) : rows.length === 0 ? (
          <div className="px-4 py-8 text-center text-xs text-muted-foreground/70">
            No group tags yet. When someone tags one of your groups, it shows up here.
          </div>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full text-left text-sm">
              <thead className="border-b border-glass text-[11px] uppercase tracking-[0.14em] text-muted-foreground/70">
                <tr>
                  <th className="px-3 py-2 font-medium">Time</th>
                  <th className="px-3 py-2 font-medium">Group</th>
                  <th className="px-3 py-2 font-medium">From</th>
                  <th className="px-3 py-2 font-medium">Ticket</th>
                  <th className="px-3 py-2 font-medium">Status</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-glass">
                {rows.map((t) => (
                  <tr key={t.id} className="align-top">
                    <td className="whitespace-nowrap px-3 py-2 text-xs text-muted-foreground">{formatDateTime(t.createdUtc, zone)}</td>
                    <td className="px-3 py-2 text-xs font-medium text-foreground">{t.groupName}</td>
                    <td className="px-3 py-2 text-xs text-muted-foreground">@{t.sourceUserEmail?.split("@")[0] ?? "agent"}</td>
                    <td className="max-w-80 px-3 py-2">
                      <Link to="/tickets/$ticketId" params={{ ticketId: t.ticketId }} hash={`event-${t.eventId}`}
                        className="group inline-flex max-w-full items-center gap-1 text-xs text-foreground hover:underline">
                        <span className="font-mono text-muted-foreground">#{t.ticketNumber}</span>
                        <span className="truncate">{t.ticketSubject}</span>
                        <ExternalLink className="h-3 w-3 shrink-0 opacity-0 group-hover:opacity-100" />
                      </Link>
                      {t.previewText && <div className="mt-0.5 truncate text-[11px] text-muted-foreground/80">{t.previewText}</div>}
                    </td>
                    <td className="whitespace-nowrap px-3 py-2">
                      {t.personalAck ? <PersonalStatus tag={t} zone={zone} /> : (
                        <>
                          <StatusChip done={!!t.ackedUtc}>
                            {t.ackedUtc ? `Acknowledged · ${t.ackedByEmail?.split("@")[0] ?? "someone"}` : "Open"}
                          </StatusChip>
                          {t.ackedUtc && <div className="mt-0.5 text-[10px] text-muted-foreground/70">{formatDateTime(t.ackedUtc, zone)}</div>}
                        </>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        {nextCursor ? (
          <div className="flex justify-center border-t border-glass px-3 py-3">
            <button type="button" onClick={() => setCursor(nextCursor)} disabled={query.isFetching}
              className="rounded-md px-3 py-1 text-xs text-muted-foreground transition-colors hover:bg-glass-hover hover:text-foreground disabled:opacity-50">
              {query.isFetching ? "Loading…" : "Load more"}
            </button>
          </div>
        ) : null}
      </section>
    </div>
  );
}

function StatusChip({ done, children }: { done: boolean; children: React.ReactNode }) {
  return (
    <span className={cn("rounded-md border px-1.5 py-0.5 text-[10px] font-medium",
      done
        ? "border-glass-strong bg-glass text-muted-foreground"
        : "border-red-400/40 bg-red-500/10 text-red-600 dark:text-red-300")}>
      {children}
    </span>
  );
}

/// "Everyone confirms" tag: the caller's own read, plus how many of the
/// members who were in the group at tag time have read it (no names).
function PersonalStatus({ tag, zone }: { tag: GroupTag; zone: string | null }) {
  return (
    <>
      <StatusChip done={!!tag.readByMeUtc}>{tag.readByMeUtc ? "Read by you" : "Unread"}</StatusChip>
      <div className="mt-0.5 text-[10px] text-muted-foreground/70">
        {tag.readByMeUtc ? `${formatDateTime(tag.readByMeUtc, zone)} · ` : ""}
        Read by {tag.readCount} of {tag.recipientCount}
      </div>
    </>
  );
}
