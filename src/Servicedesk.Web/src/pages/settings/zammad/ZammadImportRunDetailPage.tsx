import { useMemo, useState, type ReactNode } from "react";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { Link, useParams } from "@tanstack/react-router";
import { toast } from "sonner";
import {
  AlertTriangle,
  ArrowLeft,
  CheckCircle2,
  ChevronDown,
  ChevronRight,
  Circle,
  Loader2,
  PauseCircle,
  UserPlus,
  XCircle,
} from "lucide-react";
import { CreateContactFromZammadDialog } from "./CreateContactFromZammadDialog";
import {
  apiErrorMessage,
  zammadDryRunApi,
  type ZammadImportRecordItem,
  type ZammadImportRecordResult,
  type ZammadImportRunStatus,
} from "@/lib/api";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { cn } from "@/lib/utils";

const RESULT_LABEL: Record<ZammadImportRecordResult, string> = {
  mapped: "Mapped",
  skipped_no_contact: "No contact",
  skipped_no_group_mapping: "Group unmapped",
  skipped_no_state_mapping: "State unmapped",
  skipped_no_priority_mapping: "Priority unmapped",
  failed: "Failed",
};

const RESULT_TONE: Record<ZammadImportRecordResult, { dot: string; text: string }> = {
  mapped: { dot: "bg-emerald-400", text: "text-emerald-300" },
  skipped_no_contact: { dot: "bg-amber-400", text: "text-amber-300" },
  skipped_no_group_mapping: { dot: "bg-amber-400", text: "text-amber-300" },
  skipped_no_state_mapping: { dot: "bg-amber-400", text: "text-amber-300" },
  skipped_no_priority_mapping: { dot: "bg-amber-400", text: "text-amber-300" },
  failed: { dot: "bg-rose-400", text: "text-rose-300" },
};

const STATUS_ICON: Record<ZammadImportRunStatus, ReactNode> = {
  Pending: <Circle className="h-3.5 w-3.5 text-amber-300" />,
  Running: <Loader2 className="h-3.5 w-3.5 animate-spin text-sky-300" />,
  Completed: <CheckCircle2 className="h-3.5 w-3.5 text-emerald-300" />,
  Failed: <XCircle className="h-3.5 w-3.5 text-rose-300" />,
  Cancelled: <PauseCircle className="h-3.5 w-3.5 text-muted-foreground" />,
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
      second: "2-digit",
    }).format(new Date(iso));
  } catch {
    return iso;
  }
}

export function ZammadImportRunDetailPage() {
  const params = useParams({
    from: "/settings/integrations/zammad/runs/$runId",
  });
  const runId = params.runId;
  const qc = useQueryClient();
  const [resultFilter, setResultFilter] = useState<string | undefined>(undefined);
  const [createContactRecord, setCreateContactRecord] = useState<{
    recordId: string;
    email: string;
    zammadCustomerId: number | null;
  } | null>(null);

  const detail = useQuery({
    queryKey: ["integrations", "zammad", "import", "run", runId],
    queryFn: () => zammadDryRunApi.getRun(runId),
    // Poll every 2s while the run is active so totals tick live; the
    // refetch-interval function returns false (= no polling) once the
    // status is terminal.
    refetchInterval: (data) => {
      const s = data.state.data?.summary.status;
      return s === "Pending" || s === "Running" ? 2_000 : false;
    },
  });

  const records = useQuery({
    queryKey: [
      "integrations",
      "zammad",
      "import",
      "run-records",
      runId,
      resultFilter,
    ],
    queryFn: () =>
      zammadDryRunApi.getRecords(runId, {
        limit: 200,
        result: resultFilter,
      }),
    staleTime: 5_000,
    // Same dynamic interval as the detail query so records stream in
    // while the worker is still walking the upstream.
    refetchInterval: () => {
      const s = detail.data?.summary.status;
      return s === "Pending" || s === "Running" ? 3_000 : false;
    },
  });

  const cancelMutation = useMutation({
    mutationFn: () => zammadDryRunApi.cancel(runId),
    onSuccess: () => {
      toast.success("Run cancelled.");
      void qc.invalidateQueries({
        queryKey: ["integrations", "zammad", "import", "run", runId],
      });
    },
    onError: (err) => toast.error(apiErrorMessage(err)),
  });

  // Bulk-recheck: re-evaluate every record currently visible. Useful
  // after the admin updates a mapping (no contact-create needed) and
  // wants every previously-skipped row to flip without rerunning the
  // whole run.
  const visibleIds = useMemo(
    () => (records.data?.items ?? []).map((r) => r.id),
    [records.data?.items],
  );
  const recheckVisibleMutation = useMutation({
    mutationFn: () => zammadDryRunApi.recheck(runId, visibleIds),
    onSuccess: ({ rechecked }) => {
      toast.success(`${rechecked} record(s) re-evaluated.`);
      void qc.invalidateQueries({
        queryKey: ["integrations", "zammad", "import", "run-records", runId],
      });
      void qc.invalidateQueries({
        queryKey: ["integrations", "zammad", "import", "run", runId],
      });
    },
    onError: (err) => toast.error(apiErrorMessage(err)),
  });

  if (detail.isLoading) {
    return <Skeleton className="h-64 w-full bg-white/[0.04]" />;
  }
  if (detail.isError) {
    return (
      <div className="rounded-md border border-rose-400/30 bg-rose-500/[0.08] p-3 text-xs text-rose-200">
        Could not load run — {detail.error.message}
      </div>
    );
  }

  const summary = detail.data!.summary;
  const filter = detail.data!.sourceFilter;
  const totals = summary.totals;
  const skippedTotal =
    totals.skippedNoContact +
    totals.skippedNoGroupMapping +
    totals.skippedNoStateMapping +
    totals.skippedNoPriorityMapping;
  const inFlight = summary.status === "Pending" || summary.status === "Running";

  return (
    <div className="space-y-6">
      <div>
        <Link
          to="/settings/integrations/zammad/runs"
          className="inline-flex items-center gap-1.5 text-xs text-muted-foreground hover:text-foreground"
        >
          <ArrowLeft className="h-3 w-3" /> Back to runs
        </Link>
        <div className="mt-2 flex items-center justify-between gap-3">
          <div className="flex items-center gap-2 text-lg font-semibold">
            {STATUS_ICON[summary.status]}
            Dry-run · {summary.status}
          </div>
          <div className="flex items-center gap-2">
            {!inFlight && visibleIds.length > 0 ? (
              <Button
                size="sm"
                variant="outline"
                className="h-8"
                onClick={() => recheckVisibleMutation.mutate()}
                disabled={recheckVisibleMutation.isPending}
                title="Re-fetch each visible record from Zammad and recompute its verdict against the current mapping + contacts."
              >
                {recheckVisibleMutation.isPending ? (
                  <Loader2 className="mr-1.5 h-3 w-3 animate-spin" />
                ) : null}
                Recheck visible ({visibleIds.length})
              </Button>
            ) : null}
            {inFlight ? (
              <Button
                size="sm"
                variant="outline"
                className="h-8"
                onClick={() => cancelMutation.mutate()}
                disabled={cancelMutation.isPending}
              >
                {cancelMutation.isPending ? (
                  <Loader2 className="mr-1.5 h-3 w-3 animate-spin" />
                ) : null}
                Cancel
              </Button>
            ) : null}
          </div>
        </div>
        <div className="mt-1 text-xs text-muted-foreground/70">
          Started {formatDateTime(summary.startedUtc)} by{" "}
          {summary.startedByDisplayName ?? "—"}.
          {summary.finishedUtc
            ? ` Finished ${formatDateTime(summary.finishedUtc)}.`
            : ""}
        </div>
      </div>

      {summary.errorMessage ? (
        <div className="flex gap-2 rounded-md border border-rose-400/30 bg-rose-500/[0.08] p-3 text-xs text-rose-200">
          <AlertTriangle className="mt-0.5 h-3.5 w-3.5 shrink-0" />
          <div>
            <div className="font-medium">Run aborted</div>
            <div className="mt-0.5 text-rose-200/80">{summary.errorMessage}</div>
          </div>
        </div>
      ) : null}

      {/* ---- Summary cards -------------------------------------------- */}
      <div className="grid grid-cols-2 gap-3 sm:grid-cols-5">
        <SummaryCard
          label="Processed"
          value={totals.processed}
          denominator={totals.plannedTotal ?? undefined}
        />
        <SummaryCard label="Mapped" value={totals.mapped} tone="emerald" />
        <SummaryCard label="Skipped" value={skippedTotal} tone="amber" />
        <SummaryCard label="Failed" value={totals.failed} tone="rose" />
        <SummaryCard
          label="Unresolved contacts"
          value={totals.skippedNoContact}
          tone="amber"
        />
      </div>

      {/* ---- Filter buttons ------------------------------------------- */}
      <div className="flex flex-wrap items-center gap-1.5 text-xs">
        <span className="text-muted-foreground/60">Filter:</span>
        <FilterChip
          active={resultFilter === undefined}
          label="All"
          onClick={() => setResultFilter(undefined)}
        />
        <FilterChip
          active={resultFilter === "mapped"}
          label="Mapped"
          onClick={() => setResultFilter("mapped")}
        />
        <FilterChip
          active={resultFilter === "skipped_no_contact"}
          label="No contact"
          onClick={() => setResultFilter("skipped_no_contact")}
        />
        <FilterChip
          active={resultFilter === "skipped_no_group_mapping"}
          label="Group unmapped"
          onClick={() => setResultFilter("skipped_no_group_mapping")}
        />
        <FilterChip
          active={resultFilter === "skipped_no_state_mapping"}
          label="State unmapped"
          onClick={() => setResultFilter("skipped_no_state_mapping")}
        />
        <FilterChip
          active={resultFilter === "skipped_no_priority_mapping"}
          label="Priority unmapped"
          onClick={() => setResultFilter("skipped_no_priority_mapping")}
        />
        <FilterChip
          active={resultFilter === "failed"}
          label="Failed"
          onClick={() => setResultFilter("failed")}
        />
      </div>

      {/* ---- Records table ------------------------------------------- */}
      <RecordsTable
        items={records.data?.items ?? []}
        loading={records.isLoading}
        emptyReason={
          resultFilter
            ? "No records with that result yet."
            : inFlight
              ? "Worker is processing — records appear as they complete."
              : "No records on this run."
        }
        onCreateContact={(recordId, email, zammadCustomerId) =>
          setCreateContactRecord({ recordId, email, zammadCustomerId })
        }
      />

      {filter ? (
        <details className="rounded-xl border border-white/[0.06] bg-white/[0.02] p-3 text-xs">
          <summary className="cursor-pointer text-muted-foreground hover:text-foreground">
            Source filter snapshot
          </summary>
          <pre className="mt-2 overflow-x-auto whitespace-pre-wrap break-all font-mono text-[10px] text-muted-foreground/80">
            {JSON.stringify(filter, null, 2)}
          </pre>
        </details>
      ) : null}

      {createContactRecord ? (
        <CreateContactFromZammadDialog
          open={true}
          runId={runId}
          recordId={createContactRecord.recordId}
          email={createContactRecord.email}
          zammadCustomerId={createContactRecord.zammadCustomerId}
          onClose={() => setCreateContactRecord(null)}
        />
      ) : null}
    </div>
  );
}

/// Pulls the email out of the `contact_not_found:<email>` reason string
/// the resolver writes for skipped_no_contact rows. Returns null when
/// the row was skipped because the upstream had no email at all
/// (`ticket_has_no_customer_email`) — in that case the admin needs
/// data outside our reach to create the contact.
function extractContactEmail(reasons: readonly string[]): string | null {
  for (const r of reasons) {
    const prefix = "contact_not_found:";
    if (r.startsWith(prefix)) return r.slice(prefix.length).trim();
  }
  return null;
}

/// Reads the `zammadCustomerId` field out of the record's mapping
/// snapshot JSON. Returns null when the field is missing or the JSON
/// can't be parsed — the dialog still works without it, just without
/// the name pre-fill.
function extractZammadCustomerId(mappingJson: string): number | null {
  try {
    const parsed = JSON.parse(mappingJson) as Record<string, unknown>;
    const v = parsed?.zammadCustomerId;
    return typeof v === "number" ? v : null;
  } catch {
    return null;
  }
}

function SummaryCard({
  label,
  value,
  denominator,
  tone,
}: {
  label: string;
  value: number;
  denominator?: number;
  tone?: "emerald" | "amber" | "rose";
}) {
  const toneClass = {
    emerald: "text-emerald-300",
    amber: "text-amber-300",
    rose: "text-rose-300",
  };
  return (
    <div className="rounded-xl border border-white/[0.06] bg-white/[0.02] p-3">
      <div className="text-[10px] uppercase tracking-widest text-muted-foreground/60">
        {label}
      </div>
      <div
        className={cn(
          "mt-1 text-xl font-semibold tabular-nums",
          tone ? toneClass[tone] : "text-foreground",
        )}
      >
        {value.toLocaleString()}
        {denominator !== undefined ? (
          <span className="text-base text-muted-foreground/60">
            {" / "}
            {denominator.toLocaleString()}
          </span>
        ) : null}
      </div>
    </div>
  );
}

function FilterChip({
  active,
  label,
  onClick,
}: {
  active: boolean;
  label: string;
  onClick: () => void;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={cn(
        "rounded-full border px-2.5 py-1 text-[11px] transition",
        active
          ? "border-violet-400/40 bg-violet-500/[0.12] text-violet-100"
          : "border-white/10 bg-white/[0.02] text-muted-foreground hover:border-white/20 hover:text-foreground",
      )}
    >
      {label}
    </button>
  );
}

function RecordsTable({
  items,
  loading,
  emptyReason,
  onCreateContact,
}: {
  items: ZammadImportRecordItem[];
  loading: boolean;
  emptyReason: string;
  onCreateContact: (
    recordId: string,
    email: string,
    zammadCustomerId: number | null,
  ) => void;
}) {
  if (loading) return <Skeleton className="h-32 w-full bg-white/[0.04]" />;
  if (items.length === 0) {
    return (
      <div className="rounded-md border border-white/[0.06] bg-white/[0.02] p-4 text-xs text-muted-foreground">
        {emptyReason}
      </div>
    );
  }
  return (
    <div className="overflow-hidden rounded-xl border border-white/[0.06] bg-white/[0.02]">
      <table className="w-full text-xs">
        <thead className="text-[10px] uppercase tracking-widest text-muted-foreground/60">
          <tr className="border-b border-white/[0.04]">
            <th className="w-8 px-2 py-2"></th>
            <th className="px-2 py-2 text-left">Ticket</th>
            <th className="px-2 py-2 text-left">Title</th>
            <th className="px-2 py-2 text-left">Result</th>
            <th className="px-2 py-2 text-left">Unresolved</th>
            <th className="w-8 px-2 py-2"></th>
          </tr>
        </thead>
        <tbody>
          {items.map((row) => (
            <RecordRow key={row.id} row={row} onCreateContact={onCreateContact} />
          ))}
        </tbody>
      </table>
    </div>
  );
}

function RecordRow({
  row,
  onCreateContact,
}: {
  row: ZammadImportRecordItem;
  onCreateContact: (
    recordId: string,
    email: string,
    zammadCustomerId: number | null,
  ) => void;
}) {
  const [expanded, setExpanded] = useState(false);
  const tone = RESULT_TONE[row.result];
  // Only rows skipped because the contact was missing get the create
  // button. ticket_has_no_customer_email cases stay button-less because
  // we have no email to seed the dialog with.
  const contactEmail =
    row.result === "skipped_no_contact"
      ? extractContactEmail(row.unresolvedReasons)
      : null;
  const zammadCustomerId = useMemo(
    () => (contactEmail ? extractZammadCustomerId(row.mappingJson) : null),
    [contactEmail, row.mappingJson],
  );
  return (
    <>
      <tr
        className="cursor-pointer border-b border-white/[0.03] last:border-b-0 hover:bg-white/[0.02]"
        onClick={() => setExpanded((v) => !v)}
      >
        <td className="px-2 py-1.5 align-top text-muted-foreground/40">
          {expanded ? (
            <ChevronDown className="h-3 w-3" />
          ) : (
            <ChevronRight className="h-3 w-3" />
          )}
        </td>
        <td className="px-2 py-1.5 align-top">
          <div className="font-mono text-[11px] text-muted-foreground">
            {row.zammadTicketNumber ? `#${row.zammadTicketNumber}` : "—"}
          </div>
          <div className="text-[10px] text-muted-foreground/50">
            zammad_id={row.zammadTicketId}
          </div>
        </td>
        <td className="px-2 py-1.5 align-top text-foreground">
          {row.zammadTicketTitle ?? "—"}
        </td>
        <td className={cn("px-2 py-1.5 align-top", tone.text)}>
          <span className="inline-flex items-center gap-1.5">
            <span className={cn("h-1.5 w-1.5 rounded-full", tone.dot)} />
            {RESULT_LABEL[row.result]}
          </span>
        </td>
        <td className="px-2 py-1.5 align-top text-muted-foreground">
          {row.unresolvedReasons.length === 0 ? (
            <span className="text-muted-foreground/40">—</span>
          ) : (
            <ul className="space-y-0.5">
              {row.unresolvedReasons.slice(0, 3).map((r, i) => (
                <li key={i} className="font-mono text-[10px]">
                  {r}
                </li>
              ))}
              {row.unresolvedReasons.length > 3 ? (
                <li className="text-[10px] text-muted-foreground/40">
                  +{row.unresolvedReasons.length - 3} more
                </li>
              ) : null}
            </ul>
          )}
        </td>
        <td className="px-2 py-1.5 text-right align-top">
          {contactEmail ? (
            <button
              type="button"
              onClick={(e) => {
                e.stopPropagation();
                onCreateContact(row.id, contactEmail, zammadCustomerId);
              }}
              className="inline-flex items-center gap-1 rounded border border-violet-400/30 bg-violet-500/[0.10] px-1.5 py-0.5 text-[10px] text-violet-200 hover:border-violet-400/50 hover:bg-violet-500/[0.18]"
              title={`Create contact for ${contactEmail}`}
            >
              <UserPlus className="h-3 w-3" />
              Create
            </button>
          ) : null}
        </td>
      </tr>
      {expanded ? (
        <tr className="border-b border-white/[0.03] bg-black/20">
          <td colSpan={6} className="px-4 py-3">
            {row.unresolvedReasons.length > 0 ? (
              <div className="mb-2 text-[11px]">
                <div className="text-[10px] uppercase tracking-wider text-muted-foreground/60">
                  All unresolved
                </div>
                <ul className="mt-1 space-y-0.5 font-mono text-[10px] text-muted-foreground">
                  {row.unresolvedReasons.map((r, i) => (
                    <li key={i}>{r}</li>
                  ))}
                </ul>
              </div>
            ) : null}
            <div className="text-[10px] uppercase tracking-wider text-muted-foreground/60">
              Mapping snapshot
            </div>
            <pre className="mt-1 overflow-x-auto whitespace-pre-wrap break-all font-mono text-[10px] text-muted-foreground/80">
              {tryPretty(row.mappingJson)}
            </pre>
          </td>
        </tr>
      ) : null}
    </>
  );
}

function tryPretty(json: string): string {
  try {
    return JSON.stringify(JSON.parse(json), null, 2);
  } catch {
    return json;
  }
}
