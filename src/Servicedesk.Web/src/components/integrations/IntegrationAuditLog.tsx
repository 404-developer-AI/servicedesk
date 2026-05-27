import { useState } from "react";
import { useInfiniteQuery } from "@tanstack/react-query";
import { ChevronDown, ChevronRight, RefreshCw } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import {
  adsolutApi,
  telavoxAdminApi,
  zammadAdminApi,
  type IntegrationAuditEntry,
  type IntegrationAuditOutcome,
  type IntegrationAuditPage,
} from "@/lib/api";
import { cn } from "@/lib/utils";

const OUTCOME_TONE: Record<IntegrationAuditOutcome, { dot: string; text: string; label: string }> = {
  ok: { dot: "bg-emerald-400", text: "text-emerald-300", label: "OK" },
  warn: { dot: "bg-amber-400", text: "text-amber-300", label: "Warn" },
  error: { dot: "bg-rose-400", text: "text-rose-300", label: "Error" },
};

function formatDateTime(iso: string) {
  try {
    return new Intl.DateTimeFormat(undefined, {
      year: "numeric",
      month: "short",
      day: "2-digit",
      hour: "2-digit",
      minute: "2-digit",
      second: "2-digit",
    }).format(new Date(iso));
  } catch {
    return iso;
  }
}

function tryPretty(json: string): string {
  if (!json) return "";
  try {
    return JSON.stringify(JSON.parse(json), null, 2);
  } catch {
    return json;
  }
}

type Props = {
  /** Which integration to query. Each backend exposes its own /audit
   * endpoint scoped to its `integration_audit.integration` column, so
   * the dispatch lives here. */
  integration: "adsolut" | "telavox" | "zammad";
};

export function IntegrationAuditLog({ integration }: Props) {
  // useInfiniteQuery handles cursor walking. The tag includes integration so
  // the cache is partitioned per integration once Zammad / TRMM go live.
  // First page caps at 20 rows to keep the panel focused on the latest
  // activity; the existing "Load older entries" button below the table
  // walks the cursor forward in 20-row chunks.
  const PAGE_SIZE = 20;
  const query = useInfiniteQuery<IntegrationAuditPage, Error>({
    queryKey: ["integrations", integration, "audit"] as const,
    initialPageParam: null as number | null,
    queryFn: ({ pageParam }) => {
      const cursor = pageParam as number | null;
      switch (integration) {
        case "adsolut":
          return adsolutApi.auditLog(cursor, PAGE_SIZE);
        case "telavox":
          return telavoxAdminApi.auditLog(cursor, PAGE_SIZE);
        case "zammad":
          return zammadAdminApi.auditLog(cursor, PAGE_SIZE);
      }
    },
    getNextPageParam: (last) => last.nextCursor ?? null,
    // Short stale time + visibility-paced polling. 10s is fast enough
    // that an admin watching a test-connection roundtrip sees the new
    // row appear without having to F5, but not so fast that we burn DB
    // calls when the page is parked open. `refetchIntervalInBackground`
    // stays default-off so a hidden tab is silent.
    staleTime: 5_000,
    refetchInterval: 10_000,
    // Force-refetch when the admin re-focuses the window — covers the
    // common case where they ran an action in another tab and come back.
    refetchOnWindowFocus: true,
  });

  if (query.isLoading) {
    return <Skeleton className="h-48 w-full" />;
  }

  if (query.isError) {
    return (
      <div className="rounded-md border border-rose-400/30 bg-rose-500/[0.08] p-3 text-xs text-rose-200">
        Could not load audit log — {query.error.message}
      </div>
    );
  }

  const items = query.data?.pages.flatMap((p) => p.items) ?? [];
  if (items.length === 0) {
    return (
      <div className="rounded-md border border-glass-strong bg-glass p-4 text-xs text-muted-foreground">
        No integration calls logged yet. Connect or run a test refresh to populate this view.
      </div>
    );
  }

  return (
    <div className="flex flex-col gap-2">
      <div className="flex items-center justify-end">
        <Button
          size="sm"
          variant="ghost"
          className="h-7 text-xs text-muted-foreground hover:text-foreground"
          onClick={() => query.refetch()}
          disabled={query.isFetching}
          title="Auto-refreshes every 10s; click to refresh now."
        >
          <RefreshCw
            className={cn(
              "mr-1.5 h-3 w-3",
              query.isFetching && "animate-spin",
            )}
          />
          {query.isFetching ? "Refreshing…" : "Refresh"}
        </Button>
      </div>
      <div className="overflow-hidden rounded-md border border-glass-strong bg-glass">
        <table className="w-full text-xs">
          <thead className="text-[10px] uppercase tracking-widest text-muted-foreground/60">
            <tr className="border-b border-glass">
              <th className="w-8 px-2 py-2"></th>
              <th className="px-2 py-2 text-left">When</th>
              <th className="px-2 py-2 text-left">Event</th>
              <th className="px-2 py-2 text-left">Outcome</th>
              <th className="px-2 py-2 text-right">HTTP</th>
              <th className="px-2 py-2 text-right">Latency</th>
              <th className="px-2 py-2 text-left">Error</th>
              <th className="px-2 py-2 text-left">Actor</th>
            </tr>
          </thead>
          <tbody>
            {items.map((row) => (
              <Row key={row.id} row={row} />
            ))}
          </tbody>
        </table>
      </div>
      {query.hasNextPage && (
        <div className="flex justify-center">
          <Button
            size="sm"
            variant="ghost"
            onClick={() => query.fetchNextPage()}
            disabled={query.isFetchingNextPage}
          >
            {query.isFetchingNextPage ? "Loading…" : "Load older entries"}
          </Button>
        </div>
      )}
    </div>
  );
}

function Row({ row }: { row: IntegrationAuditEntry }) {
  const [expanded, setExpanded] = useState(false);
  const tone = OUTCOME_TONE[row.outcome];
  const hasPayload = row.payload && row.payload !== "{}" && row.payload.length > 2;

  return (
    <>
      <tr
        className={cn(
          "border-b border-glass last:border-b-0",
          hasPayload ? "cursor-pointer hover:bg-glass-hover" : "",
        )}
        onClick={hasPayload ? () => setExpanded((v) => !v) : undefined}
      >
        <td className="px-2 py-1.5 align-top text-muted-foreground/40">
          {hasPayload ? (
            expanded ? (
              <ChevronDown className="h-3 w-3" />
            ) : (
              <ChevronRight className="h-3 w-3" />
            )
          ) : null}
        </td>
        <td className="px-2 py-1.5 align-top whitespace-nowrap text-muted-foreground">
          {formatDateTime(row.utc)}
        </td>
        <td className="px-2 py-1.5 align-top font-mono text-[11px]">{row.eventType}</td>
        <td className={cn("px-2 py-1.5 align-top", tone.text)}>
          <span className="inline-flex items-center gap-1.5">
            <span className={cn("h-1.5 w-1.5 rounded-full", tone.dot)} />
            {tone.label}
          </span>
        </td>
        <td className="px-2 py-1.5 align-top text-right tabular-nums text-muted-foreground">
          {row.httpStatus ?? "—"}
        </td>
        <td className="px-2 py-1.5 align-top text-right tabular-nums text-muted-foreground">
          {row.latencyMs !== null ? `${row.latencyMs} ms` : "—"}
        </td>
        <td className="px-2 py-1.5 align-top">
          {row.errorCode ? (
            <code className="rounded bg-rose-500/10 px-1.5 py-0.5 font-mono text-[10px] text-rose-200">
              {row.errorCode}
            </code>
          ) : (
            <span className="text-muted-foreground/40">—</span>
          )}
        </td>
        <td className="px-2 py-1.5 align-top text-muted-foreground">
          {row.actorRole ? `${row.actorRole}${row.actorId ? ` (${row.actorId.slice(0, 8)}…)` : ""}` : "system"}
        </td>
      </tr>
      {expanded && hasPayload && (
        <tr className="border-b border-glass bg-black/20">
          <td colSpan={8} className="px-4 py-3">
            <pre className="overflow-x-auto whitespace-pre-wrap break-all font-mono text-[11px] text-muted-foreground">
              {tryPretty(row.payload)}
            </pre>
            {row.endpoint && (
              <div className="mt-2 text-[10px] text-muted-foreground/50">
                Endpoint: <span className="font-mono">{row.endpoint}</span>
              </div>
            )}
          </td>
        </tr>
      )}
    </>
  );
}
