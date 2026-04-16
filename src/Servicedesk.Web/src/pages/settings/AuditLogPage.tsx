import { useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { auditApi, type AuditEntry, type AuditListQuery } from "@/lib/api";
import { Input } from "@/components/ui/input";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { useServerTime, toServerLocal, formatUtcSuffix } from "@/hooks/useServerTime";

const EVENT_TYPES = ["", "rate_limited", "csp_violation", "setting_changed"] as const;

export function AuditLogPage() {
  const [eventType, setEventType] = useState<string>("");
  const [actor, setActor] = useState<string>("");
  const [cursor, setCursor] = useState<number | undefined>(undefined);
  const [selected, setSelected] = useState<AuditEntry | null>(null);

  const query: AuditListQuery = useMemo(
    () => ({
      eventType: eventType || undefined,
      actor: actor || undefined,
      cursor,
      limit: 50,
    }),
    [eventType, actor, cursor],
  );

  const { data, isLoading, isError, refetch } = useQuery({
    queryKey: ["audit", query],
    queryFn: () => auditApi.list(query),
  });

  const { time: serverTime } = useServerTime();
  const offsetMinutes = serverTime?.offsetMinutes ?? 0;

  // Layout: page header + filter bar + pagination footer stay pinned; only
  // the table body scrolls. We bound the outer flex-col to the visible
  // content area (viewport - Header - main pb-6 - SettingsLayout py-4 ≈ 8rem)
  // so the middle section can take flex-1 and scroll its overflow.
  return (
    <div className="flex h-[calc(100vh-8rem)] w-full flex-col gap-6">
        <header className="flex items-start justify-between gap-4">
          <div className="space-y-1">
            <h1 className="text-display-md font-semibold text-foreground">Audit log</h1>
            <p className="text-sm text-muted-foreground">
              Append-only record of security events. Every row is HMAC-chained so
              tampering with any earlier row invalidates every subsequent hash.
            </p>
          </div>
          <Badge className="border border-white/10 bg-white/[0.05] text-xs font-normal text-muted-foreground">
            Admin only
          </Badge>
        </header>

        <section className="glass-card p-4">
          <div className="flex flex-wrap items-end gap-3">
            <label className="flex flex-col gap-1 text-xs text-muted-foreground">
              Event type
              <select
                value={eventType}
                onChange={(e) => {
                  setEventType(e.target.value);
                  setCursor(undefined);
                }}
                className="h-9 rounded-md border border-white/10 bg-white/[0.04] px-2 text-sm text-foreground outline-none focus:border-primary/60"
              >
                {EVENT_TYPES.map((t) => (
                  <option key={t} value={t} className="bg-background">
                    {t === "" ? "All" : t}
                  </option>
                ))}
              </select>
            </label>
            <label className="flex flex-col gap-1 text-xs text-muted-foreground">
              Actor
              <Input
                value={actor}
                onChange={(e) => {
                  setActor(e.target.value);
                  setCursor(undefined);
                }}
                placeholder="username or ip"
                className="h-9 w-56"
              />
            </label>
            <div className="ml-auto flex gap-2">
              <Button
                variant="secondary"
                onClick={() => {
                  setCursor(undefined);
                  refetch();
                }}
              >
                Refresh
              </Button>
            </div>
          </div>
        </section>

        <section className="glass-card min-h-0 flex-1 overflow-y-auto">
          {isLoading ? (
            <div className="space-y-2 p-4">
              {Array.from({ length: 6 }).map((_, i) => (
                <Skeleton key={i} className="h-9 w-full" />
              ))}
            </div>
          ) : isError ? (
            <div className="p-8 text-center text-sm text-destructive">
              Failed to load audit entries. Are you signed in as Admin?
            </div>
          ) : data && data.items.length > 0 ? (
            <table data-testid="audit-table" className="w-full text-left text-sm">
              <thead className="sticky top-0 z-10 bg-[hsl(245_14%_12%)] text-xs uppercase tracking-wide text-muted-foreground [&_th]:border-b [&_th]:border-white/10">
                <tr>
                  <th className="px-4 py-3 font-medium">Time</th>
                  <th className="px-4 py-3 font-medium">Event</th>
                  <th className="px-4 py-3 font-medium">Actor</th>
                  <th className="px-4 py-3 font-medium">Role</th>
                  <th className="px-4 py-3 font-medium">Target</th>
                  <th className="px-4 py-3 font-medium">IP</th>
                </tr>
              </thead>
              <tbody>
                {data.items.map((entry) => (
                  <tr
                    key={entry.id}
                    className="cursor-pointer border-b border-white/5 transition-colors hover:bg-white/[0.04]"
                    onClick={() => setSelected(entry)}
                  >
                    <td className="px-4 py-3 font-mono text-xs">
                      <div className="text-foreground/90">
                        {toServerLocal(entry.utc, offsetMinutes, true)}
                      </div>
                      <div className="text-[10px] text-muted-foreground/60">
                        {formatUtcSuffix(entry.utc)}
                      </div>
                    </td>
                    <td className="px-4 py-3">
                      <Badge className="border border-white/10 bg-white/[0.05] font-mono text-[10px] font-normal text-foreground">
                        {entry.eventType}
                      </Badge>
                    </td>
                    <td className="px-4 py-3 text-foreground">{entry.actor}</td>
                    <td className="px-4 py-3 text-muted-foreground">{entry.actorRole}</td>
                    <td className="px-4 py-3 text-muted-foreground">{entry.target ?? "—"}</td>
                    <td className="px-4 py-3 font-mono text-xs text-muted-foreground">
                      {entry.clientIp ?? "—"}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          ) : (
            <div className="p-8 text-center text-sm text-muted-foreground">
              No audit entries match the current filters.
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

      <Dialog open={selected !== null} onOpenChange={(o) => !o && setSelected(null)}>
        <DialogContent className="max-w-2xl">
          <DialogHeader>
            <DialogTitle>Audit entry #{selected?.id}</DialogTitle>
            <DialogDescription>
              {selected?.eventType} — {selected?.actor} ({selected?.actorRole})
            </DialogDescription>
          </DialogHeader>
          {selected && (
            <div className="space-y-3 text-xs">
              <Row label="Time" value={`${toServerLocal(selected.utc, offsetMinutes, true)}  ${formatUtcSuffix(selected.utc)}`} />
              <Row label="Target" value={selected.target ?? "—"} />
              <Row label="Client IP" value={selected.clientIp ?? "—"} />
              <Row label="User agent" value={selected.userAgent ?? "—"} />
              <Row label="Entry hash" value={selected.entryHash} mono />
              <Row label="Prev hash" value={selected.prevHash} mono />
              <div className="space-y-1">
                <div className="text-muted-foreground">Payload</div>
                <pre className="glass-panel max-h-64 overflow-auto p-3 font-mono text-[11px] text-foreground">
                  {JSON.stringify(selected.payload, null, 2)}
                </pre>
              </div>
            </div>
          )}
        </DialogContent>
      </Dialog>
    </div>
  );
}

function Row({ label, value, mono }: { label: string; value: string; mono?: boolean }) {
  return (
    <div className="flex gap-3">
      <div className="w-28 shrink-0 text-muted-foreground">{label}</div>
      <div className={mono ? "break-all font-mono text-foreground" : "text-foreground"}>{value}</div>
    </div>
  );
}
