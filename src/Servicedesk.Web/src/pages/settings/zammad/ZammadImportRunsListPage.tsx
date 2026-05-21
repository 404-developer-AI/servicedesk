import { useQuery } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { ArrowLeft, ListTree } from "lucide-react";
import {
  zammadDryRunApi,
  type ZammadImportRunStatus,
  type ZammadImportRunSummary,
} from "@/lib/api";
import { Skeleton } from "@/components/ui/skeleton";
import { cn } from "@/lib/utils";

const RUNS_QK = ["integrations", "zammad", "import", "runs"] as const;

const STATUS_TONE: Record<ZammadImportRunStatus, { dot: string; text: string }> = {
  Pending: { dot: "bg-amber-400", text: "text-amber-300" },
  Running: { dot: "bg-sky-400", text: "text-sky-300" },
  Completed: { dot: "bg-emerald-400", text: "text-emerald-300" },
  Failed: { dot: "bg-rose-400", text: "text-rose-300" },
  Cancelled: { dot: "bg-white/40", text: "text-muted-foreground" },
};

function formatDateTime(iso: string | null) {
  if (!iso) return "—";
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

export function ZammadImportRunsListPage() {
  const query = useQuery({
    queryKey: RUNS_QK,
    queryFn: () => zammadDryRunApi.listRuns(),
    // Background refresh so a pending → running → completed run lights
    // up without F5. Slow enough that an idle list page doesn't burn
    // requests; fast enough to feel live.
    staleTime: 5_000,
    refetchInterval: 10_000,
    refetchOnWindowFocus: true,
  });

  return (
    <div className="space-y-6">
      <div>
        <Link
          to="/settings/integrations/zammad"
          className="inline-flex items-center gap-1.5 text-xs text-muted-foreground hover:text-foreground"
        >
          <ArrowLeft className="h-3 w-3" /> Back to Zammad integration
        </Link>
        <div className="mt-2 flex items-center gap-2 text-lg font-semibold">
          <ListTree className="h-4 w-4 text-muted-foreground" />
          Zammad import runs
        </div>
        <p className="mt-1 text-xs text-muted-foreground/70">
          Every dry-run and (later) real import shows up here. Click a row
          to see per-ticket mapping verdicts and unresolved contacts.
        </p>
      </div>

      {query.isLoading ? (
        <Skeleton className="h-48 w-full bg-white/[0.04]" />
      ) : query.isError ? (
        <div className="rounded-md border border-rose-400/30 bg-rose-500/[0.08] p-3 text-xs text-rose-200">
          Could not load runs — {query.error.message}
        </div>
      ) : (query.data?.items.length ?? 0) === 0 ? (
        <div className="rounded-md border border-white/[0.06] bg-white/[0.02] p-4 text-xs text-muted-foreground">
          No runs yet. Start a dry-run from the Ticket picker on the
          integration page.
        </div>
      ) : (
        <RunsTable rows={query.data!.items} />
      )}
    </div>
  );
}

function RunsTable({ rows }: { rows: ZammadImportRunSummary[] }) {
  return (
    <div className="overflow-hidden rounded-xl border border-white/[0.06] bg-white/[0.02]">
      <table className="w-full text-xs">
        <thead className="text-[10px] uppercase tracking-widest text-muted-foreground/60">
          <tr className="border-b border-white/[0.04]">
            <th className="px-3 py-2 text-left">Started</th>
            <th className="px-3 py-2 text-left">By</th>
            <th className="px-3 py-2 text-left">Kind</th>
            <th className="px-3 py-2 text-left">Status</th>
            <th className="px-3 py-2 text-right">Progress</th>
            <th className="px-3 py-2 text-right">Mapped</th>
            <th className="px-3 py-2 text-right">Skipped</th>
            <th className="px-3 py-2 text-right">Failed</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((row) => {
            const skipped =
              row.totals.skippedNoContact +
              row.totals.skippedNoGroupMapping +
              row.totals.skippedNoStateMapping +
              row.totals.skippedNoPriorityMapping;
            const tone = STATUS_TONE[row.status];
            return (
              <tr
                key={row.id}
                className="cursor-pointer border-b border-white/[0.03] last:border-b-0 hover:bg-white/[0.02]"
              >
                <td className="px-3 py-2 align-middle">
                  <Link
                    to="/settings/integrations/zammad/runs/$runId"
                    params={{ runId: row.id }}
                    className="text-foreground hover:underline"
                  >
                    {formatDateTime(row.startedUtc)}
                  </Link>
                </td>
                <td className="px-3 py-2 align-middle text-muted-foreground">
                  {row.startedByDisplayName ?? "—"}
                </td>
                <td className="px-3 py-2 align-middle text-muted-foreground">
                  {row.kind === "DryRun" ? "Dry-run" : "Import"}
                </td>
                <td className={cn("px-3 py-2 align-middle", tone.text)}>
                  <span className="inline-flex items-center gap-1.5">
                    <span className={cn("h-1.5 w-1.5 rounded-full", tone.dot)} />
                    {row.status}
                  </span>
                </td>
                <td className="px-3 py-2 text-right tabular-nums text-muted-foreground">
                  {row.totals.processed}
                  {row.totals.plannedTotal !== null
                    ? ` / ${row.totals.plannedTotal}`
                    : ""}
                </td>
                <td className="px-3 py-2 text-right tabular-nums text-emerald-300">
                  {row.totals.mapped}
                </td>
                <td className="px-3 py-2 text-right tabular-nums text-amber-300">
                  {skipped}
                </td>
                <td className="px-3 py-2 text-right tabular-nums text-rose-300">
                  {row.totals.failed}
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}
