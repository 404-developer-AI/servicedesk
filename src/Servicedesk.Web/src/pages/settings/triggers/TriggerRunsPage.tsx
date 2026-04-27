import * as React from "react";
import { useInfiniteQuery, useQuery } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import {
  ArrowLeft, Activity, CheckCircle2, MinusCircle, AlertTriangle,
  Ban, Loader2, ChevronDown,
} from "lucide-react";
import { triggerApi, type TriggerRun } from "@/lib/api";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { cn } from "@/lib/utils";

type Props = { triggerId: string };

const PAGE_LIMIT = 50;

/// v0.0.24 Blok 8 — full run-history table for one trigger. The list-row
/// summary on the Triggers settings page only shows last-24h counters;
/// admins land here when they want to see *what* a trigger actually did
/// over its lifetime — applied changes (one row per run, with a JSON
/// diff per row), the matching ticket, the moment of firing, and any
/// failure reason. Cursor-paginated (oldest fired_utc of the current
/// page becomes the next-page cursor) so the rolling history stays cheap
/// even at 1M tickets.
export function TriggerRunsPage({ triggerId }: Props) {
  const triggerQ = useQuery({
    queryKey: ["admin", "trigger", triggerId],
    queryFn: () => triggerApi.get(triggerId),
  });

  const runsQ = useInfiniteQuery({
    queryKey: ["admin", "trigger-runs", triggerId],
    queryFn: ({ pageParam }) =>
      triggerApi.runs(triggerId, { limit: PAGE_LIMIT, cursorUtc: pageParam ?? undefined }),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (last) => last.nextCursor ?? undefined,
  });

  const items: TriggerRun[] = React.useMemo(
    () => runsQ.data?.pages.flatMap((p) => p.items) ?? [],
    [runsQ.data],
  );

  return (
    <div className="flex flex-col gap-6">
      <header className="flex flex-col gap-2">
        <Link
          to="/settings/triggers"
          className="inline-flex items-center gap-1.5 text-xs text-muted-foreground hover:text-foreground"
        >
          <ArrowLeft className="h-3.5 w-3.5" />
          Back to triggers
        </Link>
        <div className="flex items-center gap-3">
          <div className="flex h-9 w-9 items-center justify-center rounded-lg bg-primary/20 border border-primary/30">
            <Activity className="h-4 w-4 text-primary" />
          </div>
          <div className="min-w-0 flex-1">
            <h1 className="truncate text-display-md font-semibold leading-tight text-foreground">
              {triggerQ.data?.name ?? "…"} — run history
            </h1>
            <p className="text-xs text-muted-foreground">
              Each row is one evaluation of this trigger against a ticket. Applied runs include the per-action diff that was committed.
            </p>
          </div>
          <Button variant="ghost" size="sm" asChild>
            <Link to="/settings/triggers/$triggerId" params={{ triggerId }}>
              Edit trigger
            </Link>
          </Button>
        </div>
      </header>

      {runsQ.isLoading ? (
        <div className="space-y-2">
          {Array.from({ length: 5 }).map((_, i) => (
            <Skeleton key={i} className="h-16 rounded-lg" />
          ))}
        </div>
      ) : items.length === 0 ? (
        <EmptyState />
      ) : (
        <div className="space-y-2">
          {items.map((r) => (
            <RunRow key={r.id} run={r} />
          ))}
          {runsQ.hasNextPage && (
            <div className="flex justify-center pt-2">
              <Button
                variant="ghost"
                size="sm"
                disabled={runsQ.isFetchingNextPage}
                onClick={() => runsQ.fetchNextPage()}
              >
                {runsQ.isFetchingNextPage ? (
                  <>
                    <Loader2 className="h-3.5 w-3.5 animate-spin" />
                    Loading…
                  </>
                ) : (
                  <>
                    Load older runs
                    <ChevronDown className="h-3.5 w-3.5" />
                  </>
                )}
              </Button>
            </div>
          )}
        </div>
      )}
    </div>
  );
}

function RunRow({ run }: { run: TriggerRun }) {
  const [open, setOpen] = React.useState(false);
  const tone = outcomeTone(run.outcome);
  const Icon = tone.icon;
  const hasDiff = !!run.appliedChangesJson && run.appliedChangesJson !== "[]" && run.appliedChangesJson !== "{}";

  return (
    <div className="rounded-lg border border-white/[0.06] bg-white/[0.02] transition-colors hover:bg-white/[0.04]">
      <button
        type="button"
        onClick={() => setOpen((o) => !o)}
        className="flex w-full items-center gap-3 px-4 py-3 text-left"
      >
        <span
          className={cn(
            "inline-flex h-7 w-7 shrink-0 items-center justify-center rounded-full border",
            tone.bg,
          )}
          title={tone.label}
        >
          <Icon className={cn("h-3.5 w-3.5", tone.fg)} />
        </span>
        <div className="min-w-0 flex-1">
          <div className="flex items-center gap-2 text-sm">
            <span className={cn("font-medium", tone.fg)}>{tone.label}</span>
            {run.ticketNumber !== null ? (
              <Link
                to="/tickets/$ticketId"
                params={{ ticketId: run.ticketId }}
                onClick={(e) => e.stopPropagation()}
                className="font-mono text-xs text-primary hover:underline"
              >
                #{run.ticketNumber}
              </Link>
            ) : (
              <span className="font-mono text-xs text-muted-foreground/60">ticket gone</span>
            )}
            <span className="text-xs text-muted-foreground/60">{formatAbsolute(run.firedUtc)}</span>
          </div>
          {run.errorMessage && (
            <p className="mt-0.5 truncate text-xs text-red-300/80">{run.errorMessage}</p>
          )}
        </div>
        {hasDiff && (
          <ChevronDown
            className={cn(
              "h-4 w-4 shrink-0 text-muted-foreground/50 transition-transform",
              open && "rotate-180",
            )}
          />
        )}
      </button>
      {open && hasDiff && (
        <div className="border-t border-white/[0.06] bg-black/20 px-4 py-3">
          <pre className="overflow-x-auto whitespace-pre-wrap break-all text-[11px] leading-relaxed text-muted-foreground">
            {prettyJson(run.appliedChangesJson)}
          </pre>
        </div>
      )}
    </div>
  );
}

function EmptyState() {
  return (
    <div className="flex flex-col items-center justify-center gap-3 rounded-lg border border-white/[0.06] bg-white/[0.02] px-6 py-12 text-center">
      <Activity className="h-5 w-5 text-muted-foreground/40" />
      <div>
        <p className="text-sm font-medium text-foreground">No runs yet</p>
        <p className="mt-1 text-xs text-muted-foreground">
          This trigger hasn&rsquo;t evaluated against a ticket. Activate it
          and let the activator fire, or use the Test runner in the editor.
        </p>
      </div>
    </div>
  );
}

type Tone = {
  label: string;
  bg: string;
  fg: string;
  icon: React.ComponentType<{ className?: string }>;
};

function outcomeTone(outcome: string): Tone {
  switch (outcome) {
    case "applied":
      return {
        label: "Applied",
        bg: "border-emerald-400/30 bg-emerald-400/10",
        fg: "text-emerald-300",
        icon: CheckCircle2,
      };
    case "skipped_no_match":
      return {
        label: "No match",
        bg: "border-white/10 bg-white/[0.02]",
        fg: "text-muted-foreground",
        icon: MinusCircle,
      };
    case "skipped_loop":
      return {
        label: "Loop guard",
        bg: "border-amber-400/30 bg-amber-400/10",
        fg: "text-amber-300",
        icon: Ban,
      };
    case "failed":
      return {
        label: "Failed",
        bg: "border-red-400/30 bg-red-400/10",
        fg: "text-red-300",
        icon: AlertTriangle,
      };
    default:
      return {
        label: outcome,
        bg: "border-white/10 bg-white/[0.02]",
        fg: "text-muted-foreground",
        icon: Activity,
      };
  }
}

function formatAbsolute(iso: string): string {
  const t = Date.parse(iso);
  if (!Number.isFinite(t)) return iso;
  return new Date(t).toLocaleString(undefined, {
    year: "numeric",
    month: "short",
    day: "numeric",
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
  });
}

function prettyJson(raw: string | null): string {
  if (!raw) return "";
  try {
    return JSON.stringify(JSON.parse(raw), null, 2);
  } catch {
    return raw;
  }
}
