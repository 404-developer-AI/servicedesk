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
import { useServerTime } from "@/hooks/useServerTime";

function formatUtc(iso: string): string {
  // "2026-04-11T12:40:18.123Z" → "2026-04-11 12:40:18"
  return new Date(iso).toISOString().replace("T", " ").slice(0, 19);
}

function formatServerLocal(iso: string, offsetMinutes: number): string {
  const localMs = new Date(iso).getTime() + offsetMinutes * 60_000;
  // Use toISOString on a UTC-shifted date so we sidestep the browser's
  // own tz — the server's offset is the only authority here.
  return new Date(localMs).toISOString().replace("T", " ").slice(0, 19);
}

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

  return (
    <div className="app-background min-h-[calc(100vh-8rem)] p-8">
      <div className="mx-auto w-full max-w-6xl space-y-6">
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

        <section className="glass-card overflow-hidden">
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
              <thead className="border-b border-white/10 text-xs uppercase tracking-wide text-muted-foreground">
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
                        {formatServerLocal(entry.utc, offsetMinutes)}
                      </div>
                      <div className="text-[10px] text-muted-foreground/60">
                        {formatUtc(entry.utc)} UTC
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
      </div>

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
              <Row label="Time" value={formatServerLocal(selected.utc, offsetMinutes)} />
              <Row label="Time (UTC)" value={formatUtc(selected.utc)} />
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
